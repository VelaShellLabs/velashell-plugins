using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 列表行的 CPU / 内存采样器。
/// <para>
/// <b>为什么不给每一行开一条 <c>stats</c> 流。</b> 那是最实时的做法,但一条流占一个 SSH
/// 通道,而隧道配额是每插件 16 条 —— 二十个容器的机器上,光列表就能把事件流、日志、
/// 终端要用的通道全吃光。所以列表走定期快照(<c>stream=0</c>),真正的实时流只留给
/// 详情抽屉里那一个容器。
/// </para>
/// <para>
/// 并发也压到 3:HTTP 连接池只有 6,采样把它占满的话,用户点一下"停止"要排队等采样跑完。
/// </para>
/// </summary>
public sealed class StatsSampler(Func<DockerClient?> clientAccessor) : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private const int MaxConcurrency = 3;

    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Func<IReadOnlyList<ContainerRow>>? _targets;
    private Action<IReadOnlyList<ContainerRow>>? _onRound;

    /// <summary>开始采样。</summary>
    /// <param name="targets">每一轮要采哪些行(通常是"当前可见且在跑的")。</param>
    /// <param name="onRound">
    /// 每采完一轮回调一次。总览页的 CPU 趋势与 Top 5 靠它喂 ——
    /// 让总览自己再发一轮请求等于把同一批数据要两遍。
    /// </param>
    /// <param name="lifetime">面板生命周期。</param>
    public void Start(Func<IReadOnlyList<ContainerRow>> targets, Action<IReadOnlyList<ContainerRow>>? onRound,
        CancellationToken lifetime)
    {
        Stop();
        _targets = targets;
        _onRound = onRound;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), token);
    }

    /// <summary>停止采样。</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = clientAccessor();
            var rows = _targets?.Invoke() ?? [];
            if (client is not null && rows.Count > 0)
            {
                await Task.WhenAll(rows.Select(row => SampleAsync(client, row, token))).ConfigureAwait(false);
                if (!token.IsCancellationRequested && _onRound is { } onRound)
                {
                    Ui.Post(() => onRound(rows));
                }
            }
            try
            {
                await Task.Delay(Interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SampleAsync(DockerClient client, ContainerRow row, CancellationToken token)
    {
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            var stats = await client.StatsSnapshotAsync(row.Id, token).ConfigureAwait(false);
            if (stats is not null)
            {
                Ui.Post(() => row.ApplyStats(stats));
            }
        }
        catch (Exception)
        {
            // 采样失败不该冒泡:容器可能刚好在这一刻被删了,而那不是一个要报给用户的错误。
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Stop();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
