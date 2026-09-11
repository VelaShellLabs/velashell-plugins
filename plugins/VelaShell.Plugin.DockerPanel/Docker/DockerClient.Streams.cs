using System.Text.Json;

namespace VelaShell.Plugin.DockerPanel.Docker;

public sealed partial class DockerClient
{
    /// <summary>
    /// 接上 <c>GET /events</c>,逐条回调。
    /// <para>
    /// 面板的刷新是**事件驱动**的:别处起了个容器、CI 推了个镜像、某个容器 OOM 死掉,
    /// 界面在一秒内自己就更新了。所以自动刷新默认是关的 —— 它只在这条流断掉时兜底。
    /// </para>
    /// </summary>
    /// <param name="since">从这个时刻起补发历史事件;为 null 只收新的。</param>
    /// <param name="onEvent">逐条回调(在读流的线程上,应快速返回)。</param>
    /// <param name="cancellationToken">取消令牌(**必须**持有并在不再需要时触发)。</param>
    public async Task StreamEventsAsync(DateTimeOffset? since, Action<DockerEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var path = "/events" + Query(("since", since?.ToUnixTimeSeconds().ToString()));
        using var response = await OpenStreamAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            DockerEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<DockerEvent>(line, DockerJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }
            if (evt is not null)
            {
                onEvent(evt);
            }
        }
    }

    /// <summary>
    /// 容器日志。<paramref name="follow" /> 为真时是一条真正的长连接
    /// (等价于 <c>docker logs -f</c>),新行到达即回调。
    /// </summary>
    /// <param name="id">容器 id 或名字。</param>
    /// <param name="tty">容器是否分配了 TTY —— 决定要不要解 8 字节帧头,取自 inspect。</param>
    /// <param name="follow">是否跟随。</param>
    /// <param name="tail">先补多少行历史;<c>all</c> 表示全部。</param>
    /// <param name="timestamps">是否带时间戳。</param>
    /// <param name="since">只要这个时刻之后的日志。</param>
    /// <param name="onLine">逐行回调。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task StreamLogsAsync(string id, bool tty, bool follow, string tail, bool timestamps,
        DateTimeOffset? since, Action<DockerLogLine> onLine, CancellationToken cancellationToken)
    {
        var path = $"/containers/{Uri.EscapeDataString(id)}/logs" + Query(
            ("stdout", "1"),
            ("stderr", "1"),
            ("follow", follow ? "1" : "0"),
            ("tail", string.IsNullOrWhiteSpace(tail) ? "500" : tail),
            ("timestamps", timestamps ? "1" : "0"),
            ("since", since?.ToUnixTimeSeconds().ToString()));
        using var response = await OpenStreamAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var decoder = new DockerFrameDecoder(tty, timestamps);
        await decoder.ReadAsync(body, onLine, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 容器实时统计(<c>stream=1</c>)。daemon 每秒推一帧,
    /// 第一帧的 <c>precpu</c> 是空的,所以 CPU 百分比从第二帧起才有意义。
    /// </summary>
    public async Task StreamStatsAsync(string id, Action<ContainerStats> onSample, CancellationToken cancellationToken)
    {
        var path = $"/containers/{Uri.EscapeDataString(id)}/stats" + Query(("stream", "1"), ("one-shot", "0"));
        using var response = await OpenStreamAsync(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }
            ContainerStats? sample;
            try
            {
                sample = JsonSerializer.Deserialize<ContainerStats>(line, DockerJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }
            if (sample is not null)
            {
                onSample(sample);
            }
        }
    }

    /// <summary>
    /// 取一次性的统计快照(<c>stream=0</c>)。列表页用它给每一行补 CPU/内存,
    /// 不为每个容器长期占一条连接。
    /// </summary>
    public async Task<ContainerStats?> StatsSnapshotAsync(string id, CancellationToken cancellationToken = default)
    {
        var path = $"/containers/{Uri.EscapeDataString(id)}/stats" + Query(("stream", "0"), ("one-shot", "0"));
        var body = await GetStringAsync(path, cancellationToken).ConfigureAwait(false);
        return DockerJson.TryDeserialize<ContainerStats>(body);
    }
}
