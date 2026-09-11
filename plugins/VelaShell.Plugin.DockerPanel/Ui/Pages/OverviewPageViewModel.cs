using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>事件时间线里的一条。</summary>
public sealed class EventItem(DockerEvent source)
{
    /// <summary>时刻。</summary>
    public string Time { get; } = source.At.ToString("HH:mm:ss");

    /// <summary>事件类型(<c>start</c> / <c>die</c> / <c>pull</c>…)。</summary>
    public string Action { get; } = source.Action ?? "";

    /// <summary>对象名。</summary>
    public string Target { get; } = source.DisplayName;

    /// <summary>一句人话。</summary>
    public string Message { get; } = Describe(source);

    /// <summary>语气。</summary>
    public RowTone Tone { get; } = Classify(source);

    private static RowTone Classify(DockerEvent source) => source.Action switch
    {
        "start" or "create" or "health_status: healthy" or "connect" => RowTone.Ok,
        "die" or "oom" or "health_status: unhealthy" => RowTone.Danger,
        "pause" or "restart" => RowTone.Warn,
        "stop" or "kill" or "destroy" or "disconnect" => RowTone.Idle,
        _ => RowTone.Busy
    };

    private static string Describe(DockerEvent source)
    {
        var exitCode = source.Actor?.Attributes?.GetValueOrDefault("exitCode");
        // 卷与网络的 create 与容器的 create 是同一个词,先按对象类型分流。
        if (source is { Type: "volume", Action: "create" })
        {
            return "本地卷已创建";
        }
        if (source is { Type: "network", Action: "create" })
        {
            return "网络已创建";
        }
        return source.Action switch
        {
            "start" => "容器已启动",
            "stop" => "收到 SIGTERM",
            "kill" => "收到 SIGKILL",
            "die" => exitCode is { Length: > 0 } code ? $"退出码 {code}" : "容器已退出",
            "create" => "容器已创建",
            "destroy" => "容器已移除",
            "pause" => "容器已暂停",
            "unpause" => "容器已恢复",
            "restart" => "容器已重启",
            "oom" => "内存超限,被 OOM 杀掉",
            "pull" => "镜像拉取完成",
            "tag" => "镜像已打标签",
            "untag" => "镜像标签已移除",
            "delete" => "镜像已删除",
            "connect" => "容器已接入网络",
            "disconnect" => "容器已从网络摘除",
            "exec_start" => "exec 会话开始",
            { } action when action.StartsWith("health_status", StringComparison.Ordinal) =>
                action["health_status: ".Length..],
            _ => source.Action ?? ""
        };
    }
}

/// <summary>“需要关注”里的一条。</summary>
/// <param name="Icon">图标。</param>
/// <param name="Tone">语气。</param>
/// <param name="Title">标题。</param>
/// <param name="Detail">小字。</param>
/// <param name="ActionLabel">右侧动作文字。</param>
/// <param name="Action">动作。</param>
public sealed record AttentionItem(string Icon, RowTone Tone, string Title, string Detail, string ActionLabel,
    Action Action);

/// <summary>Top N 排行里的一条。</summary>
public sealed class TopItem(string name, double value, double max, string valueText, bool hot) : ObservableObject
{
    /// <summary>名字。</summary>
    public string Name { get; } = name;

    /// <summary>数值。</summary>
    public double Value { get; } = value;

    /// <summary>相对最大值的比例 0–1(条按这个画,而不是按绝对百分比)。</summary>
    public double Ratio { get; } = max > 0 ? Math.Clamp(value / max, 0, 1) : 0;

    /// <summary>右侧文字。</summary>
    public string ValueText { get; } = valueText;

    /// <summary>要不要转成警示色。</summary>
    public bool Hot { get; } = hot;
}

/// <summary>
/// CPU 趋势图里的一根柱子。
/// <para>
/// 高度在视图模型里就算好,不在 XAML 里换算 —— 纵轴要按**窗口峰值**缩放,
/// 而那个分母只有视图模型知道。
/// </para>
/// </summary>
/// <param name="Height">柱高(像素,最高 96)。</param>
/// <param name="HasSample">这一格采到数没有。没采到的格子留空,而不是画成 0 —— 那是两件事。</param>
public readonly record struct TrendBar(double Height, bool HasSample);

/// <summary>总览页。</summary>
public sealed class OverviewPageViewModel : PageViewModel
{
    private const int MaxEvents = 60;

    /// <summary>趋势图的格子数。48 × 5s = 4 分钟,与图下那条时间轴一一对上。</summary>
    private const int TrendSlots = 48;

    /// <summary>趋势图的柱高上限(像素)。</summary>
    private const double TrendHeight = 96;

    /// <summary>
    /// 纵轴的下限。
    /// <para>
    /// 一台 32 核机器的容器总占用常年在个位数,纵轴钉死 0–100% 会把任何真实负载
    /// 都压成一条贴着底边的直线 —— 那张图就什么也读不出来了。
    /// 所以纵轴跟着窗口峰值走,但不低于这个数,免得 0.1% 的抖动被放大成山峰。
    /// </para>
    /// </summary>
    private const double TrendFloorPercent = 5;

    /// <summary>算"持续高 CPU"的门槛,与行内 sparkline 转黄的阈值同一个数。</summary>
    private const double HotCpuThreshold = 30;

    /// <summary>要连续在高位这么久才报 —— CPU 尖峰在容器世界里再正常不过。</summary>
    private static readonly TimeSpan HotCpuHold = TimeSpan.FromMinutes(2);

    private readonly List<double> _cpuTrend = [];
    private readonly Dictionary<string, DateTimeOffset> _hotSince = [];
    private readonly Dictionary<string, string> _hotNames = [];
    private readonly Dictionary<string, double> _hotPercent = [];
    private double _cpuPeak;
    private long _hostMemory;
    private int _hostCpus;
    private bool _reclaimRequested;

    /// <summary>建总览页。</summary>
    public OverviewPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
        RunContainerCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.Images));
        PullImageCommand = new RelayCommand(_ => Shell.ShowPullDialogAsync(null));
        CleanupCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.System));
        ComposeCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.Compose));
        RefreshReclaimCommand = new RelayCommand(_ => RefreshReclaimAsync(Shell.Lifetime));
        NewComposeCommand = new RelayCommand(_ => Shell.GoToAsync(PanelPage.Compose));
        OpenTerminalCommand = new RelayCommand(_ => OpenTerminalAsync());
        ExportDiagnosticsCommand = new RelayCommand(_ => ExportDiagnosticsAsync());
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Overview;

    /// <inheritdoc />
    public override string Title => "总览";

    /// <summary>运行中容器。</summary>
    public string RunningText
    {
        get;
        private set => SetField(ref field, value);
    } = "—";

    /// <summary>运行中容器的小字。</summary>
    public string RunningDetail
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>CPU 总占用。</summary>
    public string CpuText
    {
        get;
        private set => SetField(ref field, value);
    } = "—";

    /// <summary>CPU 小字。</summary>
    public string CpuDetail
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>内存。</summary>
    public string MemText
    {
        get;
        private set => SetField(ref field, value);
    } = "—";

    /// <summary>内存小字。</summary>
    public string MemDetail
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>可回收空间。</summary>
    public string ReclaimText
    {
        get;
        private set => SetField(ref field, value);
    } = "—";

    /// <summary>可回收小字。</summary>
    public string ReclaimDetail
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>CPU 趋势采样。</summary>
    /// <summary>
    /// CPU 趋势图的 48 根柱子。**永远是 48 根**:还没采到的那些留空,
    /// 否则刚打开面板时几根柱子挤在左边,而下面那条时间轴却横跨整张卡,对不上。
    /// </summary>
    public ObservableCollection<TrendBar> CpuTrend { get; } = [];

    /// <summary>趋势图标题旁边那行小字 —— 得写清楚纵轴到哪儿,否则读不出量级。</summary>
    public string TrendScaleText
    {
        get;
        private set => SetField(ref field, value);
    } = "每 5 秒一个采样点";

    /// <summary>按当前窗口重画趋势图。</summary>
    private void RebuildTrend()
    {
        var peak = _cpuTrend.Count > 0 ? _cpuTrend.Max() : 0;
        var scale = Math.Max(peak, TrendFloorPercent);
        TrendScaleText = $"每 5 秒一个采样点 · 纵轴 0–{Humanize.Percent(scale)}";
        CpuTrend.Clear();
        for (var i = _cpuTrend.Count; i < TrendSlots; i++)
        {
            CpuTrend.Add(new(0, false));
        }
        foreach (var sample in _cpuTrend)
        {
            CpuTrend.Add(new(Math.Max(2, Math.Clamp(sample / scale, 0, 1) * TrendHeight), true));
        }
    }

    /// <summary>把趋势图清成 48 个空格子(而不是清成空的)。</summary>
    private void ClearTrend()
    {
        _cpuTrend.Clear();
        RebuildTrend();
    }

    /// <summary>CPU 占用 Top 5。</summary>
    public ObservableCollection<TopItem> TopCpu { get; } = [];

    /// <summary>实时事件。</summary>
    public ObservableCollection<EventItem> Events { get; } = [];

    /// <summary>需要关注。</summary>
    public ObservableCollection<AttentionItem> Attention { get; } = [];

    /// <summary>有没有要关注的。</summary>
    public bool HasAttention => Attention.Count > 0;

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>运行容器。</summary>
    public RelayCommand RunContainerCommand { get; }

    /// <summary>拉取镜像。</summary>
    public RelayCommand PullImageCommand { get; }

    /// <summary>清理空间。</summary>
    public RelayCommand CleanupCommand { get; }

    /// <summary>去 Compose。</summary>
    public RelayCommand ComposeCommand { get; }

    /// <summary>重算可回收空间。</summary>
    public RelayCommand RefreshReclaimCommand { get; }

    /// <summary>去 Compose 页新建项目。</summary>
    public RelayCommand NewComposeCommand { get; }

    /// <summary>打开一个容器的终端(挑第一个在跑的)。</summary>
    public RelayCommand OpenTerminalCommand { get; }

    /// <summary>导出一份诊断信息。</summary>
    public RelayCommand ExportDiagnosticsCommand { get; }

    /// <summary>能不能弹本地文件对话框。</summary>
    public bool CanPickFiles => FilePicker.IsAvailable;

    /// <summary>
    /// 打开终端。挑第一个在跑的容器 —— 没有在跑的就说清楚,而不是打开一个空终端。
    /// </summary>
    private async Task OpenTerminalAsync()
    {
        await Shell.GoToAsync(PanelPage.Containers).ConfigureAwait(true);
        if (Shell.Containers.View.FirstOrDefault(r => r.IsRunning) is not { } row)
        {
            Shell.Feedback.Status(FeedbackKind.Warning, "没有正在运行的容器 —— 终端要有个容器才能进。");
            return;
        }
        Shell.Containers.RowTerminalCommand.Execute(row);
    }

    /// <summary>
    /// 导出诊断:把面板此刻能拿到的那几份事实写成一个文本文件。
    /// <para>
    /// 用途是发给别人看。所以**不含**任何凭据、环境变量与日志正文 ——
    /// 那些地方最容易夹带口令,而一份要发出去的文件不该由用户去逐行检查。
    /// </para>
    /// </summary>
    private async Task ExportDiagnosticsAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var target = await FilePicker
            .PickSaveAsync("导出诊断信息", $"docker-diagnostics-{Shell.SelectedEndpoint?.DisplayName ?? "host"}.txt", "txt")
            .ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        try
        {
            var info = await client.InfoAsync(Shell.Lifetime).ConfigureAwait(true);
            var version = await client.VersionAsync(Shell.Lifetime).ConfigureAwait(true);
            var containers = await client.ListContainersAsync(true, Shell.Lifetime).ConfigureAwait(true);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# VelaShell Docker 面板 · 诊断信息");
            sb.AppendLine("# 不含凭据、环境变量与日志正文 —— 可以直接发给别人。");
            sb.AppendLine($"主机        {Shell.SelectedEndpoint?.DisplayName}");
            sb.AppendLine($"socket      {Shell.SelectedEndpoint?.Endpoint.SocketPath}");
            sb.AppendLine($"Engine      {version.Version} (API {version.ApiVersion})");
            sb.AppendLine($"操作系统    {info.OperatingSystem} · {info.KernelVersion}");
            sb.AppendLine($"架构 / CPU  {info.Architecture} · {info.NCPU} 核 · {Humanize.Bytes(info.MemTotal)}");
            sb.AppendLine($"存储驱动    {info.Driver}");
            sb.AppendLine($"容器 / 镜像 {info.Containers} / {info.Images}");
            sb.AppendLine();
            sb.AppendLine("## 容器");
            foreach (var container in containers)
            {
                sb.AppendLine($"{container.Name,-28} {container.State,-10} {container.Image,-42} {container.Status}");
            }
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(output);
            await writer.WriteAsync(sb.ToString()).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "诊断信息已导出",
                $"{target.Name} —— 不含凭据与日志正文。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("导出诊断信息", ex);
        }
    }

    /// <inheritdoc />
    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        Busy = true;
        try
        {
            var containers = await client.ListContainersAsync(true, cancellationToken).ConfigureAwait(true);
            var info = await client.InfoAsync(cancellationToken).ConfigureAwait(true);
            var running = containers.Count(c => c.State == "running");
            var unhealthy = containers.Count(c => (c.Status ?? "").Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase));
            var failed = containers.Count(c => c.State == "exited" && !(c.Status ?? "").Contains("(0)", StringComparison.Ordinal));
            RunningText = $"{running} / {containers.Length}";
            RunningDetail = $"{unhealthy} 个不健康 · {failed} 个异常退出";
            _hostMemory = info.MemTotal;
            _hostCpus = info.NCPU;
            if (MemText == "—")
            {
                MemDetail = $"共 {Humanize.Bytes(info.MemTotal)} · {info.NCPU} 核";
            }
            BuildAttention(containers);
            Shell.SetContainerCount(containers.Length);
            Shell.SetImageCount(info.Images);
            LoadedOnce = true;
            OnPropertyChanged(nameof(HasAttention));
        }
        finally
        {
            Busy = false;
        }
        // 第一次进来时在后台算一次可回收空间 —— 它太慢,不能挂在刷新的主路径上。
        if (!_reclaimRequested)
        {
            _ = RefreshReclaimAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        Events.Clear();
        Attention.Clear();
        TopCpu.Clear();
        ClearTrend();
        LoadedOnce = false;
        RunningText = "—";
        CpuText = "—";
        MemText = "—";
        MemDetail = "";
        ReclaimText = "—";
        ReclaimDetail = "";
        _hostMemory = 0;
        _hostCpus = 0;
        _reclaimRequested = false;
        OnPropertyChanged(nameof(HasAttention));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type is "container" or "image";

    /// <summary>收下一条事件,进时间线。</summary>
    public void AcceptEvent(DockerEvent dockerEvent)
    {
        Events.Insert(0, new(dockerEvent));
        while (Events.Count > MaxEvents)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    /// <summary>
    /// 用容器页那份已经采到的统计更新 Top 5 与趋势 ——
    /// 不再单独发一轮请求。
    /// </summary>
    public void AcceptStatsSnapshot(IReadOnlyList<ContainerRow> rows)
    {
        List<ContainerRow> running = [.. rows.Where(r => r.IsRunning && r.CpuPercent > 0)];
        var total = running.Sum(r => r.CpuPercent);
        CpuText = Humanize.Percent(total);
        _cpuPeak = Math.Max(_cpuPeak, total);
        CpuDetail = running.Count > 0
            ? $"{(_hostCpus > 0 ? $"{_hostCpus} 核 · " : "")}峰值 {Humanize.Percent(_cpuPeak)}"
            : "没有容器在用 CPU";
        TrackHotContainers(running);
        // 内存卡走的是同一批采样:容器占用之和 / 宿主总量,
        // 单独再问一次 daemon 只会得到同样的数字。
        var usedMemory = rows.Where(r => r.IsRunning).Sum(r => r.MemoryBytes);
        MemText = usedMemory > 0 ? Humanize.Bytes(usedMemory) : "0 B";
        MemDetail = _hostMemory > 0
            ? $"容器占用 · 宿主共 {Humanize.Bytes(_hostMemory)}{(_hostCpus > 0 ? $" · {_hostCpus} 核" : "")}"
            : "容器占用";
        _cpuTrend.Add(total);
        while (_cpuTrend.Count > TrendSlots)
        {
            _cpuTrend.RemoveAt(0);
        }
        RebuildTrend();
        var max = running.Count > 0 ? running.Max(r => r.CpuPercent) : 0;
        TopCpu.Clear();
        foreach (var row in running.OrderByDescending(r => r.CpuPercent).Take(5))
        {
            TopCpu.Add(new(row.Name, row.CpuPercent, max, Humanize.Percent(row.CpuPercent), row.CpuHot));
        }
    }

    /// <summary>
    /// 用户在设置里关掉了实时统计。这两张卡不该继续显示一个停在过去某一刻的数字 ——
    /// 说清"是被关掉了"比留一个看不出新鲜度的旧值诚实。
    /// </summary>
    public void SetStatsDisabled()
    {
        CpuText = "—";
        CpuDetail = "实时统计已在设置里关闭";
        MemText = "—";
        MemDetail = _hostMemory > 0 ? $"宿主共 {Humanize.Bytes(_hostMemory)} · {_hostCpus} 核" : "";
        TopCpu.Clear();
        ClearTrend();
    }

    /// <summary>更新"可回收空间"那张卡(系统页算完之后顺手喂进来)。</summary>
    public void AcceptReclaim(ReclaimBreakdown reclaim)
    {
        _reclaimRequested = true;
        ReclaimText = Humanize.Bytes(reclaim.Total);
        ReclaimDetail = reclaim.Describe();
    }

    /// <summary>
    /// 自己去算一次可回收空间。
    /// <para>
    /// <c>system df</c> 会让 daemon 把每个卷 <c>du</c> 一遍,在卷多的机器上要好几秒 ——
    /// 所以它<b>不</b>跟着总览的每次刷新跑,只在第一次进来时算一遍,
    /// 之后靠系统页的刷新或卡片上的重算按钮更新。
    /// </para>
    /// </summary>
    public async Task RefreshReclaimAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        _reclaimRequested = true;
        ReclaimText = "…";
        ReclaimDetail = "正在统计";
        try
        {
            var usage = await client.DiskUsageAsync(cancellationToken).ConfigureAwait(true);
            AcceptReclaim(DiskMath.Reclaimable(usage));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReclaimText = "—";
            ReclaimDetail = "统计失败";
            Shell.Context.Log.Warn($"overview: system df failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 记住哪些容器一直在高位。
    /// <para>
    /// 关注的是**持续**而不是某一帧:CPU 尖峰在容器世界里再正常不过,
    /// 把一次 40% 报成告警只会训练用户忽略这块面板。所以要连续几轮都在高位才算。
    /// </para>
    /// </summary>
    private void TrackHotContainers(IReadOnlyList<ContainerRow> running)
    {
        HashSet<string> stillHot = [];
        foreach (var row in running.Where(r => r.CpuPercent >= HotCpuThreshold))
        {
            stillHot.Add(row.Id);
            if (!_hotSince.ContainsKey(row.Id))
            {
                _hotSince[row.Id] = DateTimeOffset.UtcNow;
            }
            _hotNames[row.Id] = row.Name;
            _hotPercent[row.Id] = row.CpuPercent;
        }
        // 掉下去就清零 —— 「已持续 12 分钟」得是真的连续,不能把两段拼起来。
        foreach (var id in _hotSince.Keys.Where(id => !stillHot.Contains(id)).ToList())
        {
            _hotSince.Remove(id);
            _hotNames.Remove(id);
            _hotPercent.Remove(id);
        }
        RebuildHotAttention();
    }

    /// <summary>把"持续高 CPU"那几条插进关注列表,并把过期的那些拿掉。</summary>
    private void RebuildHotAttention()
    {
        foreach (var stale in Attention.Where(a => a.Icon == "Icon.cpu").ToList())
        {
            Attention.Remove(stale);
        }
        foreach ((var id, var since) in _hotSince)
        {
            var held = DateTimeOffset.UtcNow - since;
            if (held < HotCpuHold)
            {
                continue;
            }
            var name = _hotNames.GetValueOrDefault(id, Humanize.ShortId(id));
            Attention.Insert(0, new("Icon.cpu", RowTone.Warn,
                $"{name} CPU 持续高于 {HotCpuThreshold:F0}%",
                $"已持续 {Humanize.Duration(held)} · 当前 {Humanize.Percent(_hotPercent.GetValueOrDefault(id))}",
                "查看统计",
                () => OpenContainer(id)));
        }
        OnPropertyChanged(nameof(HasAttention));
    }

    private void BuildAttention(IReadOnlyList<ContainerSummary> containers)
    {
        Attention.Clear();
        foreach (var container in containers)
        {
            var status = container.Status ?? "";
            if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase))
            {
                Attention.Add(new("Icon.triangle-alert", RowTone.Danger,
                    $"{container.Name} 健康检查失败",
                    $"{container.Image} · {status}",
                    "查看日志",
                    () => OpenContainer(container.Id)));
            }
            else if (container.State == "exited" && !status.Contains("(0)", StringComparison.Ordinal))
            {
                Attention.Add(new("Docker.circle-x", RowTone.Danger,
                    $"{container.Name} 异常退出",
                    $"{container.Image} · {status}",
                    "查看日志",
                    () => OpenContainer(container.Id)));
            }
            else if (container.State == "paused")
            {
                Attention.Add(new("Icon.pause", RowTone.Warn,
                    $"{container.Name} 处于暂停态",
                    "进程还在,但不会处理任何请求。",
                    "去恢复",
                    () => OpenContainer(container.Id)));
            }
        }
        if (Shell.Volumes.UnusedCount > 0)
        {
            Attention.Add(new("Docker.database", RowTone.Warn,
                $"{Shell.Volumes.UnusedCount} 个卷没有容器在用",
                "它们可能是刚 down 掉的项目留下的 —— 也可能就是垃圾。",
                "去看看",
                () => _ = Shell.GoToAsync(PanelPage.Volumes)));
        }
    }

    private void OpenContainer(string id)
    {
        _ = Shell.GoToAsync(PanelPage.Containers).ContinueWith(_ => Ui.Post(() =>
        {
            if (Shell.Containers.View.FirstOrDefault(r => r.Id == id) is { } row)
            {
                Shell.Containers.OpenDetailCommand.Execute(row);
            }
        }), TaskScheduler.Default);
    }
}
