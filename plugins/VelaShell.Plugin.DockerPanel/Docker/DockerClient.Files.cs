using System.Net.Http.Headers;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>容器内的一个文件系统条目。</summary>
/// <param name="Name">名字(不含路径)。</param>
/// <param name="FullPath">绝对路径。</param>
/// <param name="IsDirectory">是否目录。</param>
/// <param name="IsSymlink">是否符号链接。</param>
/// <param name="Size">字节数;目录为 0。</param>
/// <param name="Mode">权限串,如 <c>-rw-r--r--</c>。</param>
/// <param name="Owner">属主与属组,如 <c>root root</c>。</param>
/// <param name="Modified">修改时间的原始文本。</param>
/// <param name="LinkTarget">符号链接的目标。</param>
public readonly record struct ContainerFileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    string Mode,
    string Owner,
    string Modified,
    string? LinkTarget);

public sealed partial class DockerClient
{
    /// <summary>面板允许在线编辑的单文件上限。超过它的文件只给下载,不进编辑器。</summary>
    public const long MaxEditableFileBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 单文件上传上限。
    /// <para>
    /// 比编辑上限宽得多(配置文件之外还有证书、静态资源),但仍然有限:
    /// tar 打包是在内存里做的,而这条隧道跟事件流、日志流共用同一批 SSH 通道 ——
    /// 一个几百兆的上传会把它们一起饿死。真要搬大文件,scp 是对的工具。
    /// </para>
    /// </summary>
    public const long MaxUploadFileBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 列出容器内某个目录。
    /// <para>
    /// Engine API **没有**列目录这个端点 —— <c>/archive</c> 只能整包取走,
    /// 拿一个 <c>/var/log</c> 下来可能是几个 G。所以这里走 exec 跑一条 <c>ls</c>,
    /// 只传目录条目本身,不传文件内容。
    /// </para>
    /// </summary>
    public async Task<ContainerFileEntry[]> ListDirectoryAsync(string containerId, string path,
        CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrWhiteSpace(path) ? "/" : path;
        // -A 排掉 . 与 ..,--time-style=long-iso 把时间列压成固定的两段,
        // 省得为不同 locale 的 ls 输出各写一套解析。
        var result = await ExecCaptureAsync(containerId,
            ["/bin/sh", "-c", $"ls -lAL --time-style=long-iso -- {ShellQuote(target)} 2>/dev/null || ls -lA -- {ShellQuote(target)}"],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess && result.StandardOutput.Length == 0)
        {
            throw new DockerApiException(System.Net.HttpStatusCode.NotFound,
                result.StandardError.Length > 0 ? result.FailureText : $"列不出目录 {target}。");
        }
        List<ContainerFileEntry> entries = [];
        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            if (ParseLsLine(raw, target) is { } entry)
            {
                entries.Add(entry);
            }
        }
        return [.. entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 解一行 <c>ls -lA</c>。解不动就返回 null —— 总量行("total 12")与空行都走这条路。
    /// </summary>
    internal static ContainerFileEntry? ParseLsLine(string line, string directory)
    {
        var trimmed = line.TrimEnd('\r');
        if (trimmed.Length < 10 || trimmed.StartsWith("total ", StringComparison.Ordinal))
        {
            return null;
        }
        var mode = trimmed[..10];
        if (mode[0] is not ('-' or 'd' or 'l' or 'c' or 'b' or 'p' or 's'))
        {
            return null;
        }
        // 权限 链接数 属主 属组 大小 日期 时间 名字…
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8)
        {
            return null;
        }
        var owner = $"{parts[2]} {parts[3]}";
        _ = long.TryParse(parts[4], out var size);
        var modified = $"{parts[5]} {parts[6]}";
        // 名字里可能有空格,所以按"前七段之后的全部"取,而不是取第八段。
        var nameStart = IndexOfNthToken(trimmed, 7);
        var name = nameStart >= 0 ? trimmed[nameStart..] : parts[^1];
        string? linkTarget = null;
        if (mode[0] == 'l')
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0)
            {
                linkTarget = name[(arrow + 4)..];
                name = name[..arrow];
            }
        }
        if (name is "." or "..")
        {
            return null;
        }
        var full = directory.TrimEnd('/') + "/" + name;
        return new(name, full, mode[0] == 'd', mode[0] == 'l', size, mode, owner, modified, linkTarget);
    }

    /// <summary>找出第 n 个空白分隔字段的起始下标(n 从 0 起)。</summary>
    private static int IndexOfNthToken(string text, int n)
    {
        int index = 0, token = 0;
        while (index < text.Length)
        {
            while (index < text.Length && text[index] == ' ')
            {
                index++;
            }
            if (index >= text.Length)
            {
                return -1;
            }
            if (token == n)
            {
                return index;
            }
            while (index < text.Length && text[index] != ' ')
            {
                index++;
            }
            token++;
        }
        return -1;
    }

    /// <summary>POSIX 单引号引用。列目录是这一层唯一一处把用户输入拼进 shell 的地方。</summary>
    internal static string ShellQuote(string value) => Sh.Quote(value);

    /// <summary>
    /// 读容器里的一个文件。走 <c>GET /archive</c> 拿一段 tar 再取出唯一那个文件 ——
    /// 不经过 shell,因此不受登录 shell、引用规则与 locale 的影响。
    /// </summary>
    public async Task<byte[]> ReadFileAsync(string containerId, string path, long maxBytes = MaxEditableFileBytes,
        CancellationToken cancellationToken = default)
    {
        var url = $"/containers/{Uri.EscapeDataString(containerId)}/archive" + Query(("path", path));
        using var response = await OpenStreamAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var entry = await TarUtil.ReadFirstFileAsync(body, maxBytes, cancellationToken)
                                                            .ConfigureAwait(false);
        return entry?.Content
               ?? throw new DockerApiException(System.Net.HttpStatusCode.NotFound, $"{path} 不是一个普通文件。");
    }

    /// <summary>
    /// 把内容写回容器里的一个文件。
    /// <para>
    /// 整个文件以 tar 流 <c>PUT</c> 回容器的可写层。<b>这是会丢数据的操作</b> ——
    /// 原文件被整体覆盖,Docker 不做备份 —— 界面必须走"手打确认串"那一档闸门。
    /// </para>
    /// </summary>
    public async Task WriteFileAsync(string containerId, string path, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var directory = GetDirectory(path);
        var name = path[(path.LastIndexOf('/') + 1)..];
        var tar = TarUtil.CreateSingleFile(name, content.Span);
        var url = $"/containers/{Uri.EscapeDataString(containerId)}/archive" +
                     Query(("path", directory), ("noOverwriteDirNonDir", "1"));
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new ByteArrayContent(tar)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把容器里的一个路径整包取下来(目录也行),交给调用方落盘。</summary>
    public async Task<Stream> DownloadArchiveAsync(string containerId, string path, CancellationToken cancellationToken = default)
    {
        var url = $"/containers/{Uri.EscapeDataString(containerId)}/archive" + Query(("path", path));
        var response = await OpenStreamAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        return new ResponseOwningStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string GetDirectory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unreachable(ex);
        }
    }

    /// <summary>把 <see cref="HttpResponseMessage" /> 的生命周期钉在它的响应体流上。</summary>
    private sealed class ResponseOwningStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
