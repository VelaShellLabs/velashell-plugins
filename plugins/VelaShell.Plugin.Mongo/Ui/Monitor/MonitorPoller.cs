namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// "每隔 N 秒做一次"的循环(服务器监控的采样、慢查询页的 $currentOp 刷新)。
/// <para>
/// 不用 <c>DispatcherTimer</c>:定时器到点就触发,上一轮还没回来(网络慢、服务器忙)时会叠出第二轮请求;
/// 这里是"做完一轮 → 等一个间隔 → 再做",天然不会叠加,慢连接上也不会越积越多。
/// 循环在 UI 线程的同步上下文里续跑,改集合不必再手动封送;真正耗时的是驱动的网络往返,不占 UI 线程。
/// </para>
/// </summary>
/// <param name="tick">每一轮要做的事。</param>
/// <param name="interval">间隔。</param>
internal sealed class MonitorPoller(Func<CancellationToken, Task> tick, TimeSpan interval)
{
    private CancellationTokenSource? _cts;

    /// <summary>间隔。改了之后下一轮生效;要立刻生效就 <see cref="Restart" />。</summary>
    public TimeSpan Interval { get; set; } = interval;

    /// <summary>正在跑。</summary>
    public bool IsRunning => _cts is not null;

    /// <summary>开始(已在跑则什么都不做)。</summary>
    /// <param name="delayFirst">先等一个间隔再做第一轮(刚手动采过一次时,紧接着再采一次只会得到一段几毫秒的噪声率值)。</param>
    public void Start(bool delayFirst = false)
    {
        if (_cts is not null)
        {
            return;
        }
        _cts = new CancellationTokenSource();
        _ = RunAsync(delayFirst, _cts.Token);
    }

    /// <summary>
    /// 停下。不 Dispose 取消源:正在路上的那一轮还拿着它的令牌,
    /// 释放之后再往令牌上注册回调会抛 —— 没有计时器的取消源不释放也不漏资源。
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>停下再开(改了间隔、要立刻刷新一轮时)。</summary>
    /// <param name="delayFirst">先等一个间隔再做第一轮。</param>
    public void Restart(bool delayFirst = false)
    {
        Stop();
        Start(delayFirst);
    }

    private async Task RunAsync(bool delayFirst, CancellationToken token)
    {
        if (delayFirst)
        {
            try
            {
                await Task.Delay(Interval, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
        while (!token.IsCancellationRequested)
        {
            try
            {
                await tick(token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // 每一轮自己负责呈现失败(提示 / 空态);这里只保证一次失败不让循环就此停摆。
            }
            try
            {
                await Task.Delay(Interval, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
