using System.Globalization;
using Avalonia.Threading;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 文档头、扫描进度条、状态条,以及键详情头上的几个动作。
/// <para>
/// 这一段界面回答的全是"我现在在哪、刚才发生了什么":连的是哪台、哪个库、扫到什么程度、
/// 上一条命令是什么、有没有错。它们看着琐碎,但同时开着生产与开发两个标签页时,
/// 这一行就是唯一能一眼分清的东西。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    private DispatcherTimer? _autoRefreshTimer;

    // ── 自动刷新 ──────────────────────────────────────────────────

    /// <summary>自动刷新的间隔(秒)。</summary>
    public int AutoRefreshSeconds { get; } = 5;

    /// <summary>自动刷新是否开着。</summary>
    public bool IsAutoRefreshOn
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(AutoRefreshLabel));
            RaisePropertyChanged(nameof(IsAutoRefreshPaused));
            RaisePropertyChanged(nameof(AutoRefreshPausedNotice));
        }
    }

    /// <summary>自动刷新按钮的文案。</summary>
    public string AutoRefreshLabel =>
        Loc.Format("Redis_AutoRefreshEvery", AutoRefreshSeconds.ToString(CultureInfo.CurrentCulture));

    /// <summary>自动刷新的口径说明(悬停)。</summary>
    public string AutoRefreshHint => Loc["Redis_AutoRefreshHint"];

    /// <summary>开关自动刷新。</summary>
    public AsyncCommand ToggleAutoRefreshCommand { get; private set; } = null!;

    /// <summary>自动刷新此刻正让着未保存的编辑(界面据此显示那条说明)。</summary>
    public bool IsAutoRefreshPaused => IsAutoRefreshOn && HasUnsavedEdits;

    /// <summary>停走的原因;没停时为空串。</summary>
    public string AutoRefreshPausedNotice => IsAutoRefreshPaused ? Loc["Redis_AutoRefreshHeldNotice"] : string.Empty;

    /// <summary>
    /// 自动刷新一拍:刷当前抽屉页 + 重读选中的键。
    /// <para>
    /// <b>刻意不重扫键空间</b> —— 每 5 秒对生产库发一轮 <c>SCAN</c> 是在替用户制造负载,
    /// 而且列表会在他眼皮底下不停跳动。要重扫有「重扫」按钮。
    /// </para>
    /// <para>
    /// <b>也刻意不碰有未保存编辑的详情页</b>:重读会把编辑区重置回服务端的现值,
    /// 于是一次后台刷新就能把用户改了一半、还没按保存的内容悄悄盖掉 —— 那是**丢数据**,
    /// 而且丢得毫无痕迹(他甚至不会看见闪一下,只会以为自己没输进去)。抽屉与延迟照旧刷:
    /// 停的是会覆盖输入的那一步,不是整个刷新。
    /// </para>
    /// </summary>
    internal async Task AutoRefreshTickAsync()
    {
        if (_disposed || Confirmation.IsOpen)
        {
            // 确认框开着时不动界面:一个在你读弹窗时自己变化的背景,会让人怀疑自己看错了。
            return;
        }
        await RefreshActiveTabAsync().ConfigureAwait(true);
        if (!HasUnsavedEdits)
        {
            await ReloadSelectedAsync().ConfigureAwait(true);
        }
        await MeasureLatencyAsync().ConfigureAwait(true);
    }

    private void SetAutoRefresh(bool on)
    {
        IsAutoRefreshOn = on;
        if (!on)
        {
            _autoRefreshTimer?.Stop();
            return;
        }
        _autoRefreshTimer ??= CreateAutoRefreshTimer();
        _autoRefreshTimer.Start();
    }

    private DispatcherTimer CreateAutoRefreshTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoRefreshSeconds) };
        timer.Tick += (_, _) => _ = AutoRefreshTickAsync();
        return timer;
    }

    // ── 文档头 ────────────────────────────────────────────────────

    /// <summary>数据库选择器右边那一格:当前库的键数。</summary>
    public string DatabaseSummary
    {
        get
        {
            long known = _totalKeys >= 0
                ? _totalKeys
                : _connection.Info.KeyCountByDatabase.GetValueOrDefault(CurrentDatabase, -1);
            return known < 0
                ? string.Empty
                : Loc.Format("Redis_DbKeyCount", known.ToString("N0", CultureInfo.CurrentCulture));
        }
    }

    /// <summary>键空间工具条右侧:整库键数(<c>DBSIZE</c>)。</summary>
    public string DbSizeText => _totalKeys >= 0
        ? Loc.Format("Redis_DbSize", _totalKeys.ToString("N0", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>当前库的短标签(<c>db0</c>)。</summary>
    public string DatabaseLabel => $"db{CurrentDatabase.ToString(CultureInfo.InvariantCulture)}";

    // ── 扫描进度 ──────────────────────────────────────────────────

    /// <summary>
    /// 进度条的填充比例(0–1)。
    /// <para>
    /// 分母是 <c>DBSIZE</c>,而分子是**已遍历的槽位数**,所以这是一条粗略的进度 ——
    /// 界面上那句文字会说清"计数是已扫到的"。拿不到 <c>DBSIZE</c> 时给 0:
    /// 一条画到一半的进度条,比一条空的更容易被当成确定的进度。
    /// </para>
    /// </summary>
    public double ScanProgress => _totalKeys > 0
        ? Math.Clamp((double)_visited / _totalKeys, 0, 1)
        : IsScanComplete ? 1 : 0;

    /// <summary>进度条下面那一行:已扫多少、游标在哪、扫完没有。</summary>
    public string ScanProgressText
    {
        get
        {
            var parts = new List<string>(3)
            {
                Loc.Format("Redis_ScannedCountShort", MatchedCount.ToString("N0", CultureInfo.CurrentCulture))
            };
            if (_cursor is not "0")
            {
                parts.Add(Loc.Format("Redis_ScanCursor", _cursor));
            }
            parts.Add(IsScanComplete ? Loc["Redis_ScanCursorZero"] : Loc["Redis_ScanNotDone"]);
            return string.Join(" · ", parts);
        }
    }

    // ── 状态条 ────────────────────────────────────────────────────

    /// <summary>上一条命令的回显(状态条左侧)。写操作与控制台都往这儿汇。</summary>
    public string LastCommandText
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>状态条中间那格:有错就显示错,没错就明说"没有"。</summary>
    public string ErrorSummary => StatusMessage.Length > 0 ? StatusMessage : Loc["Redis_NoError"];

    /// <summary>有错(界面据此染红)。</summary>
    public bool HasError => StatusMessage.Length > 0;

    /// <summary>记一条命令回显。</summary>
    /// <param name="command">命令文本。</param>
    /// <param name="outcome">结果摘要。</param>
    private void NoteCommand(string command, string outcome) =>
        LastCommandText = Loc.Format("Redis_LastCommand", command, outcome);

    // ── 键详情头 ──────────────────────────────────────────────────

    /// <summary>
    /// 选中键的键名(详情头最左那一格)。
    /// <para>
    /// 详情头整块由 <see cref="HasSelection" /> 控制显隐,但 Avalonia 的绑定**不因父级不可见而停止求值**:
    /// 没选中键时 <c>{Binding Selected.Key.Display}</c> 会在 <c>Selected</c> 这一环上断掉,
    /// 于是每次清空选中都往日志里灌一条 "Value is null"。摊平成一个视图模型属性即可 ——
    /// 与旁边的 <see cref="SelectedTtlText" /> / <see cref="SelectedSizeText" /> 同一条路子:
    /// <b>详情头上的每一格都由视图模型算好,视图只管显示</b>。
    /// </para>
    /// </summary>
    public string SelectedKeyText => Selected?.Key.Display ?? string.Empty;

    /// <summary>选中键的类型(详情头那枚徽章)。没选中时留空,徽章自然也就是空的。</summary>
    public string SelectedTypeText => Selected?.Type ?? string.Empty;

    /// <summary>选中键的规模(详情头右上那一格)。</summary>
    public string SelectedSizeText => Selected is { MemoryBytes: >= 0 } info
        ? FormatBytes(info.MemoryBytes)
        : Selected is { Length: >= 0 } fallback
            ? FormatSize(fallback.Type, fallback.Length)
            : string.Empty;

    /// <summary>复制选中键的键名。</summary>
    public AsyncCommand CopyKeyNameCommand { get; private set; } = null!;

    /// <summary>重读选中的键。</summary>
    public AsyncCommand ReloadKeyCommand { get; private set; } = null!;

    /// <summary>
    /// 复制当前编辑框里的那段文本。
    /// <para>复制的是**你看到的那一形态**(解压后的 JSON / 转义串 / 十六进制转储),
    /// 而不是服务端的原始字节 —— 前者才是用户按下"复制"时脑子里想的东西。</para>
    /// </summary>
    public AsyncCommand CopyValueCommand { get; private set; } = null!;

    private void InitializeHeader()
    {
        ToggleAutoRefreshCommand = new(() =>
        {
            SetAutoRefresh(!IsAutoRefreshOn);
            return Task.CompletedTask;
        });
        CopyKeyNameCommand = new(() =>
        {
            if (Selected?.Key is { } key)
            {
                CopyRequested?.Invoke(this, key.Display);
                StatusMessage = Loc.Format("Redis_BatchCopied", "1");
            }
            return Task.CompletedTask;
        }, () => HasSelection);
        ReloadKeyCommand = new(ReloadSelectedAsync, () => HasSelection);
        CopyValueCommand = new(() =>
        {
            CopyRequested?.Invoke(this, StringDraft);
            return Task.CompletedTask;
        }, () => IsStringSelected);
    }

    /// <summary>进度、库信息与状态条上所有派生文案的统一刷新入口。</summary>
    private void RaiseHeaderState()
    {
        RaisePropertyChanged(nameof(ScanProgress));
        RaisePropertyChanged(nameof(ScanProgressText));
        RaisePropertyChanged(nameof(DatabaseSummary));
        RaisePropertyChanged(nameof(DbSizeText));
        RaisePropertyChanged(nameof(DatabaseLabel));
        RaisePropertyChanged(nameof(ErrorSummary));
        RaisePropertyChanged(nameof(HasError));
    }
}
