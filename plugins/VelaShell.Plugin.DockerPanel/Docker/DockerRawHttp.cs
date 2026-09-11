using System.Text;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 直接在一条隧道上说 HTTP/1.1,用于 Docker 那几个**劫持连接**的端点
/// (<c>/exec/{id}/start</c>、<c>/containers/{id}/attach</c>)。
/// <para>
/// 这些端点在响应头之后就把连接变成一条裸的双工管道,而 <see cref="HttpClient" />
/// 的模型是"请求发完才读响应" —— 它没有办法在同一条连接上边写 stdin 边读 stdout。
/// 既然我们本来就握着这条流,不如自己写这十几行,而不是去跟一个不为此设计的抽象较劲。
/// </para>
/// </summary>
internal static class DockerRawHttp
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// 在 <paramref name="stream" /> 上发一条带 JSON 体的 POST,读完响应头后把流原样交回。
    /// </summary>
    /// <param name="stream">已连到 daemon 的双工流(调用后归本方法的调用方所有)。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="jsonBody">请求体;为 null 表示没有体。</param>
    /// <param name="upgrade">是否带 <c>Upgrade: tcp</c>(exec/attach 要)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>状态码与"已经把头读完"的流(头之后多读到的字节已经补回流首)。</returns>
    public static async Task<(int StatusCode, string ReasonPhrase, Stream Body)> PostAsync(
        Stream stream, string path, string? jsonBody, bool upgrade, CancellationToken cancellationToken)
    {
        var body = jsonBody is null ? [] : Encoding.UTF8.GetBytes(jsonBody);
        var request = new StringBuilder()
            .Append("POST ").Append(path).Append(" HTTP/1.1\r\n")
            .Append("Host: docker\r\n")
            .Append("User-Agent: VelaShell-DockerPanel\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (upgrade)
        {
            request.Append("Connection: Upgrade\r\nUpgrade: tcp\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadResponseHeadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 一个字节一个字节地读到 <c>\r\n\r\n</c> 为止。
    /// </summary>
    /// <remarks>
    /// 刻意不做块读:响应头之后紧跟着的就是要交给调用方的裸数据,多读一个字节
    /// 就得自己补回去。头很短(几百字节),这点系统调用换来的是零缓冲复杂度。
    /// </remarks>
    private static async Task<(int, string, Stream)> ReadResponseHeadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var head = new List<byte>(512);
        var one = new byte[1];
        var matched = 0;
        while (matched < HeaderTerminator.Length)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new DockerApiException(System.Net.HttpStatusCode.BadGateway,
                    "daemon 在返回响应头之前就把连接关掉了。");
            }
            head.Add(one[0]);
            matched = one[0] == HeaderTerminator[matched] ? matched + 1 : one[0] == HeaderTerminator[0] ? 1 : 0;
        }
        var text = Encoding.ASCII.GetString([.. head]);
        var statusLine = text[..text.IndexOf('\r')];
        var parts = statusLine.Split(' ', 3);
        var status = parts.Length > 1 && int.TryParse(parts[1], out var code) ? code : 0;
        var reason = parts.Length > 2 ? parts[2] : "";
        return (status, reason, stream);
    }
}
