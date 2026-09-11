using Avalonia.Threading;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    /// <summary>事件密集时的合并窗口。<c>compose up</c> 一次能推来几十条事件,逐条刷新等于把远端敲成筛子。</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(350);

    /// <summary>事件流断掉时的兜底轮询间隔。</summary>
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _eventCts;
    private Task? _eventTask;
    private DispatcherTimer? _coalesceTimer;
    private DispatcherTimer? _fallbackTimer;
    private DateTimeOffset _eventsSince;

    /// <summary>
    /// 事件流接上了没有。顶栏那枚「实时」徽章绑的就是它 ——
    /// <b>徽章亮着,才代表事件流真的接上了</b>;灭了就说明现在看到的东西最多滞后 30 秒。
    /// </summary>
    public bool EventsConnected
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(EventsText), nameof(EventsDegraded), nameof(EventsDegradedText));
            }
        }
    }

    /// <summary>状态栏右侧那句关于事件流的话。</summary>
    public string EventsText => EventsConnected
        ? "事件流 已连接"
        : Settings.AutoRefreshFallback ? "事件流 断开 · 已退化为 30s 轮询" : "事件流 断开";

    /// <summary>
    /// 已经连上 daemon,但事件流断了。
    /// <para>
    /// 这一档要在**页面里**说,不能只让顶栏那枚徽章灭掉:此刻列表看起来一切正常,
    /// 而它显示的可能是 30 秒前的世界 —— 在这种状态下按「删除」是要出事的。
    /// </para>
    /// </summary>
    public bool EventsDegraded => IsReady && !EventsConnected;

    /// <summary>退化横幅上的那句话。</summary>
    public string EventsDegradedText => Settings.AutoRefreshFallback
        ? "事件流已断开,已退化为 30 秒定时刷新 —— 看到的状态最多可能滞后 30 秒。"
        : "事件流已断开,且自动刷新是关的 —— 现在看到的状态不会自己更新。";

    /// <summary>起事件流。</summary>
    private void StartEventStream()
    {
        _eventCts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _eventsSince = DateTimeOffset.UtcNow;
        var token = _eventCts.Token;
        _eventTask = Task.Run(() => EventLoopAsync(token), token);
    }

    /// <summary>停事件流并等它收尾。</summary>
    private async Task StopEventStreamAsync()
    {
        StopTimers();
        EventsConnected = false;
        if (_eventCts is null)
        {
            return;
        }
        await _eventCts.CancelAsync().ConfigureAwait(false);
        if (_eventTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 收尾时的异常一律吞掉:这条流本来就是被我们掐断的。
            }
        }
        _eventCts.Dispose();
        _eventCts = null;
        _eventTask = null;
    }

    private async Task EventLoopAsync(CancellationToken token)
    {
        // 退避从 1 秒起、封顶 15 秒:daemon 重启时不该被我们每 100ms 敲一次,
        // 但用户也不该为了看到界面复活而等上一分钟。
        var backoff = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            var client = Client;
            if (client is null)
            {
                return;
            }
            try
            {
                Ui.Post(() =>
                {
                    EventsConnected = true;
                    StopFallbackTimer();
                });
                await client.StreamEventsAsync(_eventsSince, OnDockerEvent, token).ConfigureAwait(false);
                // 流正常结束(daemon 关了连接)也走重连,而不是安静地不再更新。
                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Context.Log.Debug($"docker events stream ended: {ex.Message}");
                backoff = backoff < TimeSpan.FromSeconds(15) ? backoff * 2 : TimeSpan.FromSeconds(15);
            }
            Ui.Post(() =>
            {
                EventsConnected = false;
                StartFallbackTimer();
            });
            try
            {
                await Task.Delay(backoff, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            // 重连时从上次收到的那一刻续上,把断线期间发生的事补回来 ——
            // 否则界面会漏掉整段"你没看见但确实发生了"的变化。
            _eventsSince = DateTimeOffset.UtcNow.AddSeconds(-5);
        }
    }

    /// <summary>
    /// 一条事件到了。在**读流的线程**上被调用,所以这里只做判断与投递,不碰界面。
    /// </summary>
    private void OnDockerEvent(DockerEvent dockerEvent)
    {
        Ui.Post(() =>
        {
            Overview.AcceptEvent(dockerEvent);
            var wanted = ActivePage?.WantsRefresh(dockerEvent) ?? false;
            // 总览页永远关心计数,即使它不在前台 —— 用户切回去时不该看到一份旧数字。
            if (!wanted && !Overview.WantsRefresh(dockerEvent))
            {
                return;
            }
            RequestCoalescedRefresh();
        });
    }

    /// <summary>把这一刻起 350ms 内的所有刷新请求并成一次。</summary>
    private void RequestCoalescedRefresh()
    {
        _coalesceTimer ??= CreateTimer(CoalesceWindow, () =>
        {
            StopCoalesceTimer();
            _ = RefreshActiveAsync();
        });
        // 重新计时:事件还在涌进来的时候不急着刷,等它停下来那一刻刷一次就够了。
        _coalesceTimer.Stop();
        _coalesceTimer.Start();
    }

    private void StartFallbackTimer()
    {
        if (!Settings.AutoRefreshFallback || _fallbackTimer is not null)
        {
            return;
        }
        _fallbackTimer = CreateTimer(FallbackInterval, () => _ = RefreshActiveAsync());
        _fallbackTimer.Start();
    }

    private void StopFallbackTimer()
    {
        _fallbackTimer?.Stop();
        _fallbackTimer = null;
    }

    private void StopCoalesceTimer()
    {
        _coalesceTimer?.Stop();
        _coalesceTimer = null;
    }

    private void StopTimers()
    {
        Ui.Post(() =>
        {
            StopCoalesceTimer();
            StopFallbackTimer();
        });
    }

    private static DispatcherTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
