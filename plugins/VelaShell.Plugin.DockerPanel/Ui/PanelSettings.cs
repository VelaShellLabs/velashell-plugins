using VelaShell.PluginSdk.Storage;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 面板设置。
/// <para>
/// 分两档存:<b>按主机记忆</b>的(socket 路径 —— 换条会话再连上还是同一套)与
/// <b>全局</b>的(密度、日志行数、显示选项)。插件不写任何机密。
/// </para>
/// </summary>
public sealed class PanelSettings(IPluginStorage storage) : ObservableObject
{
    private const string GlobalKey = "panel.settings";
    private const string HostPrefix = "host.";

    private bool _showStopped = true;
    private bool _inlineSparklines = true;
    private bool _compactRows = true;
    private string _logTail = "500";
    private bool _logTimestamps = true;
    private bool _logWrap = true;
    private bool _autoRefreshFallback = true;

    /// <summary>列表里显示已停止的容器。</summary>
    public bool ShowStopped
    {
        get => _showStopped;
        set { if (SetField(ref _showStopped, value)) { Changed?.Invoke(); } }
    }

    /// <summary>日志默认行数是不是 100(设置里那组分段按钮用)。</summary>
    public bool LogTailIs100 => LogTail == "100";

    /// <summary>日志默认行数是不是 500。</summary>
    public bool LogTailIs500 => LogTail == "500";

    /// <summary>日志默认行数是不是 2000。</summary>
    public bool LogTailIs2000 => LogTail == "2000";

    /// <summary>日志默认行数是不是「全部」。</summary>
    public bool LogTailIsAll => LogTail == "all";

    /// <summary>行内画 CPU / 内存 sparkline(关掉可省一点远端开销)。</summary>
    public bool InlineSparklines
    {
        get => _inlineSparklines;
        set { if (SetField(ref _inlineSparklines, value)) { Changed?.Invoke(); } }
    }

    /// <summary>紧凑行高(32px);关掉是 40px。</summary>
    public bool CompactRows
    {
        get => _compactRows;
        set
        {
            if (SetField(ref _compactRows, value))
            {
                OnPropertyChanged(nameof(RowHeight));
                Changed?.Invoke();
            }
        }
    }

    /// <summary>数据行高。</summary>
    public double RowHeight => _compactRows ? 32 : 40;

    /// <summary>日志默认补多少行历史。</summary>
    public string LogTail
    {
        get => _logTail;
        set { if (SetField(ref _logTail, value)) { Changed?.Invoke(); } }
    }

    /// <summary>日志带时间戳。</summary>
    public bool LogTimestamps
    {
        get => _logTimestamps;
        set { if (SetField(ref _logTimestamps, value)) { Changed?.Invoke(); } }
    }

    /// <summary>
    /// 日志自动换行。默认开:面板里的日志既可能占满整屏,也可能挤在 440px 的详情抽屉里,
    /// 而后者关掉换行就等于把长行截在右边界上 —— 看不到的那一半往往正是要看的那一半。
    /// </summary>
    public bool LogWrap
    {
        get => _logWrap;
        set { if (SetField(ref _logWrap, value)) { Changed?.Invoke(); } }
    }

    /// <summary>
    /// 事件流断掉时退化为 30 秒定时刷新。默认开 —— 一块静止的、看起来正常的面板
    /// 比明说"没连上"更糟。
    /// </summary>
    public bool AutoRefreshFallback
    {
        get => _autoRefreshFallback;
        set { if (SetField(ref _autoRefreshFallback, value)) { Changed?.Invoke(); } }
    }

    /// <summary>行数分段按钮的选中态跟着 <see cref="LogTail" /> 走,改完要通知一遍。</summary>
    public void NotifyLogTailSegments() =>
        OnPropertiesChanged(nameof(LogTail), nameof(LogTailIs100), nameof(LogTailIs500),
            nameof(LogTailIs2000), nameof(LogTailIsAll));

    /// <summary>任一项改动。</summary>
    public event Action? Changed;

    /// <summary>读全局设置。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        GlobalState? state = await TryGetAsync<GlobalState>(GlobalKey, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }
        _showStopped = state.ShowStopped;
        _inlineSparklines = state.InlineSparklines;
        _compactRows = state.CompactRows;
        _logTail = state.LogTail ?? "500";
        _logTimestamps = state.LogTimestamps;
        _logWrap = state.LogWrap;
        _autoRefreshFallback = state.AutoRefreshFallback;
        OnPropertiesChanged(nameof(ShowStopped), nameof(InlineSparklines), nameof(CompactRows),
            nameof(RowHeight), nameof(LogTail), nameof(LogTimestamps), nameof(LogWrap), nameof(AutoRefreshFallback));
    }

    /// <summary>写全局设置。</summary>
    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        storage.SetAsync(GlobalKey, new GlobalState
        {
            ShowStopped = ShowStopped,
            InlineSparklines = InlineSparklines,
            CompactRows = CompactRows,
            LogTail = LogTail,
            LogTimestamps = LogTimestamps,
            LogWrap = LogWrap,
            AutoRefreshFallback = AutoRefreshFallback
        }, cancellationToken);

    /// <summary>读某台主机的 socket 路径;没记过返回 <see langword="null" />。</summary>
    public async Task<string?> GetSocketPathAsync(string host, CancellationToken cancellationToken = default) =>
        (await TryGetAsync<HostState>(HostPrefix + host, cancellationToken).ConfigureAwait(false))?.SocketPath;

    /// <summary>记住某台主机的 socket 路径。</summary>
    public Task SetSocketPathAsync(string host, string socketPath, CancellationToken cancellationToken = default) =>
        storage.SetAsync(HostPrefix + host, new HostState { SocketPath = socketPath }, cancellationToken);

    /// <summary>
    /// 读一项设置。读坏了就当没有 —— 绝不因为一份写歪的记录让面板打不开。
    /// </summary>
    private async Task<T?> TryGetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await storage.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private sealed record GlobalState
    {
        public bool ShowStopped { get; init; } = true;
        public bool InlineSparklines { get; init; } = true;
        public bool CompactRows { get; init; } = true;
        public string? LogTail { get; init; }
        public bool LogTimestamps { get; init; } = true;
        public bool LogWrap { get; init; } = true;
        public bool AutoRefreshFallback { get; init; } = true;
    }

    private sealed record HostState
    {
        public string? SocketPath { get; init; }
    }
}
