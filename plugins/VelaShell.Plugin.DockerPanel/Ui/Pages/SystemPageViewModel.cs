using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>
/// 系统页顶上那一排统计卡(设计稿的 C/StatCard)。
/// <para>
/// 第三行的 <paramref name="Sub" /> 不是装饰:一个孤零零的「镜像 34」说明不了任何事,
/// 「其中 14 个没人用」才是让人决定要不要清理的那一句。
/// </para>
/// </summary>
/// <param name="Icon">图标资源键。</param>
/// <param name="Label">名目。</param>
/// <param name="Value">主数值。</param>
/// <param name="Sub">底下那行小字。</param>
/// <param name="Tone">数值的语气。</param>
public readonly record struct SystemStat(string Icon, string Label, string Value, string Sub, RowTone Tone = RowTone.Idle);

/// <summary>磁盘占用表里的一行。</summary>
/// <param name="Kind">类型。</param>
/// <param name="Total">总数。</param>
/// <param name="Active">活跃数。</param>
/// <param name="Size">占用。</param>
/// <param name="Reclaimable">可回收。</param>
/// <param name="Tone">可回收那一列的语气。</param>
public readonly record struct DiskRow(string Kind, string Total, string Active, string Size, string Reclaimable, RowTone Tone);

/// <summary>堆叠占用条里的一段。</summary>
/// <param name="Label">图例文字。</param>
/// <param name="Bytes">字节数。</param>
/// <param name="Weight">占比(用于 star 宽度)。</param>
/// <param name="Brush">颜色资源键。</param>
public readonly record struct DiskSegment(string Label, long Bytes, double Weight, string Brush);

/// <summary>一张回收卡片。</summary>
public sealed class PruneCard(string icon, string title, string description, string tag, RowTone tone, RelayCommand command) : ObservableObject
{

    /// <summary>图标。</summary>
    public string Icon { get; } = icon;

    /// <summary>标题。</summary>
    public string Title { get; } = title;

    /// <summary>说明。</summary>
    public string Description { get; } = description;

    /// <summary>右上角的标签(安全 / 注意 / 危险 / 会丢数据)。</summary>
    public string Tag { get; } = tag;

    /// <summary>语气。</summary>
    public RowTone Tone { get; } = tone;

    /// <summary>可回收大小。</summary>
    public string SizeText
    {
        get;
        set => SetField(ref field, value);
    } = "统计中…";

    /// <summary>执行。</summary>
    public RelayCommand Command { get; } = command;
}

/// <summary>系统页:磁盘占用与空间回收。</summary>
public sealed class SystemPageViewModel : PageViewModel
{
    private DiskUsage? _usage;
    private SystemInfo? _info;
    private SystemVersion? _version;
    private long _buildCacheBytes;

    /// <summary>建系统页。</summary>
    public SystemPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
        PruneCards =
        [
            new("Icon.trash-2", "已停止的容器", "删除所有 exited 状态的容器。运行中的不受影响。", "安全", RowTone.Ok,
                new RelayCommand(_ => PruneContainersAsync())),
            new("Docker.circle-dashed", "悬空镜像", "没有标签、也没有容器引用的中间层。", "安全", RowTone.Ok,
                new RelayCommand(_ => Shell.Images.PruneDanglingCommand.Execute(null))),
            new("Icon.layers", "全部未使用镜像", "含有标签但当前无容器在用的镜像;重新拉取需要时间与带宽。", "注意", RowTone.Warn,
                new RelayCommand(_ => Shell.Images.PruneAllCommand.Execute(null))),
            new("Docker.database", "构建缓存", "下次构建会明显变慢。", "安全", RowTone.Ok,
                new RelayCommand(_ => PruneBuildCacheAsync())),
            new("Docker.broom", "以上全部", "等价于 system prune -a,不含卷。", "危险", RowTone.Danger,
                new RelayCommand(_ => PruneAllAsync(withVolumes: false))),
            new("Docker.shield-alert", "以上全部 + 卷", "会删除未被任何容器使用的卷 —— 数据不可恢复,需手打 delete。", "会丢数据", RowTone.Danger,
                new RelayCommand(_ => PruneAllAsync(withVolumes: true)))
        ];
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.System;

    /// <inheritdoc />
    public override string Title => "系统";

    /// <summary>顶部四张统计卡。</summary>
    public ObservableCollection<SystemStat> Stats { get; } = [];

    private int _networkCount;

    /// <summary>磁盘占用明细。</summary>
    public ObservableCollection<DiskRow> DiskRows { get; } = [];

    /// <summary>堆叠占用条。</summary>
    public ObservableCollection<DiskSegment> Segments { get; } = [];

    /// <summary>回收卡片。</summary>
    public ObservableCollection<PruneCard> PruneCards { get; }

    /// <summary>引擎信息(左列)。</summary>
    public ObservableCollection<DetailField> EngineLeft { get; } = [];

    /// <summary>引擎信息(右列)。</summary>
    public ObservableCollection<DetailField> EngineRight { get; } = [];

    /// <summary>总可回收。</summary>
    public string ReclaimableText { get; private set; } = "—";

    /// <summary>磁盘占用小字。</summary>
    public string DiskSubtitle { get; private set; } = "";

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

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
            // 这三条一起发:df 在镜像多的机器上要几秒,而它是这一页的主角,
            // 不该等 info 与 version 串行跑完才开始。
            var usageTask = client.DiskUsageAsync(cancellationToken);
            var infoTask = client.InfoAsync(cancellationToken);
            var versionTask = client.VersionAsync(cancellationToken);
            await Task.WhenAll(usageTask, infoTask, versionTask).ConfigureAwait(true);
            _usage = usageTask.Result;
            _info = infoTask.Result;
            _version = versionTask.Result;
            // 网络数量 df 不给。它便宜,顺手问一次;失败就当 0,不该拖垮这一页。
            try
            {
                _networkCount = (await client.ListNetworksAsync(cancellationToken).ConfigureAwait(true)).Length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _networkCount = 0;
            }
            BuildStats();
            BuildDisk();
            BuildEngine();
            LoadedOnce = true;
            // 宿主磁盘总量 Docker API 不给,只能另走一次 df。放在最后、失败不影响这一页。
            await LoadHostDiskAsync(client, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _usage = null;
        _info = null;
        _version = null;
        Stats.Clear();
        DiskRows.Clear();
        Segments.Clear();
        EngineLeft.Clear();
        EngineRight.Clear();
        LoadedOnce = false;
    }

    /// <summary>
    /// 系统页刻意<b>不</b>跟事件走。<c>/system/df</c> 要 daemon 把每个卷 du 一遍,
    /// 在镜像多的机器上是好几秒 —— 挂在事件上等于每起一个容器就让远端算一遍磁盘。
    /// </summary>
    public override bool WantsRefresh(DockerEvent dockerEvent) => false;

    private void BuildStats()
    {
        Stats.Clear();
        if (_info is not { } info)
        {
            return;
        }
        // 每张卡的第三行都是"这个数字的分母或去向" —— 光看「镜像 34」说明不了任何事,
        // 「其中 14 个没人用」才是让人动手的那一句。
        var volumes = _usage?.Volumes?.Length ?? 0;
        var volumesIdle = _usage?.Volumes?.Count(v => v.UsageData is not { RefCount: > 0 }) ?? 0;
        var imagesDangling = _usage?.Images?.Count(i => i.IsDangling) ?? 0;
        var cacheRecords = _usage?.BuildCache?.Length ?? 0;
        Stats.Add(new("Docker.box", "容器", info.Containers.ToString(),
            $"运行中 {info.ContainersRunning}",
            info.ContainersRunning > 0 ? RowTone.Ok : RowTone.Idle));
        Stats.Add(new("Icon.layers", "镜像", info.Images.ToString(),
            imagesDangling > 0 ? $"悬空 {imagesDangling}" : "没有悬空镜像",
            imagesDangling > 0 ? RowTone.Warn : RowTone.Idle));
        Stats.Add(new("Icon.hard-drive", "卷", volumes.ToString(),
            volumesIdle > 0 ? $"没人用 {volumesIdle}" : "都在使用中",
            volumesIdle > 0 ? RowTone.Warn : RowTone.Idle));
        Stats.Add(new("Icon.network", "网络", _networkCount.ToString(), "含内置的三个"));
        Stats.Add(new("Docker.database", "构建缓存", Humanize.Bytes(_buildCacheBytes),
            $"{cacheRecords} 条记录", _buildCacheBytes > 0 ? RowTone.Warn : RowTone.Idle));
    }

    /// <summary>
    /// 问一次宿主磁盘的总量与可用量。
    /// <para>
    /// Engine API 没有这个 —— <c>system df</c> 只讲 Docker 自己占了多少,
    /// 不讲那块盘还剩多少。但"还剩 3 GB"才是决定要不要清理的那个数,所以另走一次
    /// <c>df</c>,查的是 <c>Docker Root Dir</c> 所在的那块盘(不是 <c>/</c>,它们常常不是同一块)。
    /// </para>
    /// <para>失败就不显示,不报错:它是锦上添花,不该让这一页看起来出了问题。</para>
    /// </summary>
    private async Task LoadHostDiskAsync(DockerClient client, CancellationToken cancellationToken)
    {
        var root = _info?.DockerRootDir is { Length: > 0 } dir ? dir : "/var/lib/docker";
        try
        {
            var result = await client
                .ExecCaptureAsync(FirstRunningContainerId ?? "", ["/bin/sh", "-c", $"df -PB1 {Sh.Quote(root)}"],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            if (result.ExitCode != 0 || ParseDf(result.StandardOutput) is not var (total, used))
            {
                return;
            }
            HostDiskText = $"宿主盘 {Humanize.Bytes(total)} 中已用 {Humanize.Bytes(used)}";
            OnPropertyChanged(nameof(HostDiskText));
        }
        catch (Exception)
        {
            // 没有可借的容器、镜像里没有 df、路径不存在 —— 三种都很常见,都不值得报。
        }
    }

    /// <summary>借一个正在跑的容器来执行 df —— 面板没有"在宿主上执行命令"的通道。</summary>
    private string? FirstRunningContainerId =>
        Shell.Containers.View.FirstOrDefault(r => r.IsRunning)?.Id;

    /// <summary>宿主磁盘那一句;拿不到时为空。</summary>
    public string HostDiskText { get; private set; } = "";

    private static readonly string[] request = ["POST /volumes/prune"];

    /// <summary>解一行 <c>df -PB1</c>。返回 (总量, 已用)。</summary>
    internal static (long Total, long Used)? ParseDf(string output)
    {
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Filesystem 1-blocks Used Available Capacity Mounted
            if (parts.Length >= 4 && long.TryParse(parts[1], out var total) && long.TryParse(parts[2], out var used))
            {
                return (total, used);
            }
        }
        return null;
    }

    private void BuildDisk()
    {
        DiskRows.Clear();
        Segments.Clear();
        if (_usage is not { } usage)
        {
            return;
        }
        var reclaim = DiskMath.Reclaimable(usage);
        var imageTotal = usage.Images?.Sum(i => i.Size) ?? 0;
        var imageReclaim = reclaim.Images;
        // 可写层大小只有 /system/df 会给(/containers/json 要带 size=1 才算,那对每个容器做一次
        // diff,贵得多)—— 这一页正好是 df,所以这里能拿到真值。
        var containerTotal = usage.Containers?.Sum(c => c.SizeRw) ?? 0;
        // 「已停止的容器」能回收的是**已停止那些**的可写层,不是全部容器的。
        var containerReclaim = usage.Containers?.Where(c => c.State != "running").Sum(c => c.SizeRw) ?? 0;
        var volumeTotal = usage.Volumes?.Sum(v => v.UsageData is { Size: > 0 } u ? u.Size : 0) ?? 0;
        var volumeReclaim = reclaim.Volumes;
        _buildCacheBytes = reclaim.BuildCache;

        DiskRows.Add(new("镜像", (usage.Images?.Length ?? 0).ToString(),
            (usage.Images?.Count(i => i.Containers > 0) ?? 0).ToString(),
            Humanize.Bytes(usage.LayersSize > 0 ? usage.LayersSize : imageTotal),
            Ratio(imageReclaim, imageTotal), imageReclaim > 0 ? RowTone.Warn : RowTone.Idle));
        DiskRows.Add(new("容器", (usage.Containers?.Length ?? 0).ToString(),
            (usage.Containers?.Count(c => c.State == "running") ?? 0).ToString(),
            Humanize.Bytes(containerTotal), Ratio(containerReclaim, containerTotal),
            containerReclaim > 0 ? RowTone.Warn : RowTone.Idle));
        DiskRows.Add(new("本地卷", (usage.Volumes?.Length ?? 0).ToString(),
            (usage.Volumes?.Count(v => v.UsageData is { RefCount: > 0 }) ?? 0).ToString(),
            Humanize.Bytes(volumeTotal), Ratio(volumeReclaim, volumeTotal),
            volumeReclaim > 0 ? RowTone.Warn : RowTone.Idle));
        DiskRows.Add(new("构建缓存", (usage.BuildCache?.Length ?? 0).ToString(), "0",
            Humanize.Bytes(usage.BuildCache?.Sum(c => c.Size) ?? 0),
            Ratio(_buildCacheBytes, usage.BuildCache?.Sum(c => c.Size) ?? 0),
            _buildCacheBytes > 0 ? RowTone.Danger : RowTone.Idle));

        var total = imageTotal + containerTotal + volumeTotal + _buildCacheBytes;
        if (total > 0)
        {
            // 权重归一化成 0–1:界面用星形列宽画堆叠条,那条路要的是比例而不是字节。
            Segments.Add(new($"镜像 {Humanize.Bytes(imageTotal)}", imageTotal, (double)imageTotal / total, "VelaAccent"));
            Segments.Add(new($"容器可写层 {Humanize.Bytes(containerTotal)}", containerTotal,
                (double)containerTotal / total, "VelaInfo"));
            Segments.Add(new($"卷 {Humanize.Bytes(volumeTotal)}", volumeTotal, (double)volumeTotal / total,
                "VelaStatusConnected"));
            Segments.Add(new($"构建缓存 {Humanize.Bytes(_buildCacheBytes)}", _buildCacheBytes,
                (double)_buildCacheBytes / total, "VelaWarning"));
        }
        ReclaimableText = Humanize.Bytes(reclaim.Total);
        DiskSubtitle = $"docker system df -v · 共 {Humanize.Bytes(total)} · 可回收 {ReclaimableText}";
        // df 是这一页跑一次就有的东西,顺手喂给总览那张卡 ——
        // 让用户为了看一眼"能清多少"而必须先来系统页,是没道理的。
        Shell.Overview.AcceptReclaim(reclaim);
        foreach (var card in PruneCards)
        {
            card.SizeText = card.Title switch
            {
                "已停止的容器" => Humanize.Bytes(containerReclaim),
                "悬空镜像" => Humanize.Bytes(usage.Images?.Where(i => i.IsDangling).Sum(i => i.Size) ?? 0),
                "全部未使用镜像" => Humanize.Bytes(imageReclaim),
                "构建缓存" => Humanize.Bytes(_buildCacheBytes),
                "以上全部" => Humanize.Bytes(imageReclaim + containerReclaim + _buildCacheBytes),
                _ => $"{Humanize.Bytes(imageReclaim + containerReclaim + _buildCacheBytes)} + {Humanize.Bytes(volumeReclaim)}"
            };
        }
        OnPropertiesChanged(nameof(ReclaimableText), nameof(DiskSubtitle));
        BuildStats();
    }

    private static string Ratio(long part, long whole) =>
        whole <= 0 ? "—" : $"{Humanize.Bytes(part)} ({part * 100 / whole}%)";

    private void BuildEngine()
    {
        EngineLeft.Clear();
        EngineRight.Clear();
        if (_version is { } version)
        {
            EngineLeft.Add(new("Engine 版本", $"{version.Version} ({version.GitCommit})"));
            EngineLeft.Add(new("API 版本", $"{version.ApiVersion}(最低 {version.MinAPIVersion})"));
        }
        if (_info is { } info)
        {
            EngineLeft.Add(new("操作系统", $"{info.OperatingSystem} · {info.KernelVersion}"));
            EngineLeft.Add(new("架构 / CPU", $"{info.Architecture} · {info.NCPU} 核 · {Humanize.Bytes(info.MemTotal)}"));
            EngineRight.Add(new("存储驱动", info.Driver ?? "—"));
            EngineRight.Add(new("Cgroup", $"{info.CgroupVersion} · {info.CgroupDriver}"));
            EngineRight.Add(new("日志驱动", info.LoggingDriver ?? "—"));
            EngineRight.Add(new("Swarm", info.Swarm?.LocalNodeState ?? "inactive"));
            EngineRight.Add(new("Docker 根目录", info.DockerRootDir ?? "—"));
        }
    }

    private async Task PruneContainersAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = "清理已停止的容器?",
            Icon = "Icon.trash-2",
            HostName = "",
            ConfirmLabel = "开始清理",
            ConfirmIcon = "Docker.broom",
            Commands = ["POST /containers/prune"],
            CommandNote = "等价于  docker container prune",
            Consequences =
            [
                new(1, "只删 exited 状态的容器,运行中的不受影响。"),
                new(2, "写在容器可写层里(不在卷里)的数据会随之丢失。"),
                new(0, "compose 项目的容器删掉后,下次 up -d 会重建。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        await RunPruneAsync("清理已停止容器", () => client.PruneContainersAsync(Shell.Lifetime)).ConfigureAwait(true);
    }

    private async Task PruneBuildCacheAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = "清理构建缓存?",
            Icon = "Docker.database",
            HostName = "",
            ConfirmLabel = "开始清理",
            ConfirmIcon = "Docker.broom",
            Commands = ["POST /build/prune"],
            CommandNote = "等价于  docker builder prune",
            Consequences =
            [
                new(1, "不丢数据 —— 缓存只是加速用的。"),
                new(2, "下次构建会明显变慢,每一层都要重新算。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        await RunPruneAsync("清理构建缓存", () => client.PruneBuildCacheAsync(false, Shell.Lifetime)).ConfigureAwait(true);
    }

    private async Task PruneAllAsync(bool withVolumes)
    {
        if (Client is not { } client)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = withVolumes ? "清理以上全部,并删除未使用的卷?" : "清理以上全部?",
            Icon = withVolumes ? "Docker.shield-alert" : "Docker.broom",
            Tier = withVolumes ? ConfirmTier.DataLoss : ConfirmTier.Destructive,
            ConfirmWord = "delete",
            ConfirmLabel = withVolumes ? "全部清理(含卷)" : "全部清理",
            ConfirmIcon = "Docker.broom",
            HostName = "",
            Commands =
            [
                "POST /containers/prune",
                "POST /images/prune?filters={\"dangling\":[\"false\"]}",
                "POST /networks/prune",
                "POST /build/prune",
                .. withVolumes ? request : []
            ],
            CommandNote = $"等价于  docker system prune -a{(withVolumes ? " --volumes" : "")}",
            Consequences = withVolumes
                ? []
                :
                [
                    new(2, "会删掉全部未被容器使用的镜像 —— 重新拉要花时间与带宽。"),
                    new(1, "不含卷:数据卷不受影响。"),
                    new(0, $"预计回收 {ReclaimableText}。")
                ],
            DataLossHeadline = withVolumes ? "未被任何容器使用的卷会被删除,里面的数据永久丢失" : null,
            DataLossPoints = withVolumes
                ?
                [
                    "\"未使用\"只是说没有容器「现在」挂着它 —— 一个刚 down 掉的项目,它的数据卷就在名单里。",
                    "Docker 不做回收站,也没有快照。",
                    "只想回收镜像与缓存的话,用不带卷的那一档。"
                ]
                : []
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        await RunPruneAsync(withVolumes ? "全部清理(含卷)" : "全部清理", async () =>
        {
            var containers = await client.PruneContainersAsync(Shell.Lifetime).ConfigureAwait(false);
            var images = await client.PruneImagesAsync(false, Shell.Lifetime).ConfigureAwait(false);
            var networks = await client.PruneNetworksAsync(Shell.Lifetime).ConfigureAwait(false);
            var cache = await client.PruneBuildCacheAsync(true, Shell.Lifetime).ConfigureAwait(false);
            var volumes = withVolumes
                ? await client.PruneVolumesAsync(Shell.Lifetime).ConfigureAwait(false)
                : new PruneReport();
            return new PruneReport
            {
                SpaceReclaimed = containers.SpaceReclaimed + images.SpaceReclaimed + networks.SpaceReclaimed +
                                 cache.SpaceReclaimed + volumes.SpaceReclaimed,
                ContainersDeleted = containers.ContainersDeleted,
                ImagesDeleted = images.ImagesDeleted,
                NetworksDeleted = networks.NetworksDeleted,
                CachesDeleted = cache.CachesDeleted,
                VolumesDeleted = volumes.VolumesDeleted
            };
        }).ConfigureAwait(true);
    }

    private async Task RunPruneAsync(string title, Func<Task<PruneReport>> action)
    {
        var task = Shell.Tasks.Start("Docker.broom", title, indeterminate: true);
        try
        {
            var report = await action().ConfigureAwait(true);
            task.Finish(PanelTaskState.Succeeded, "完成",
                $"删除 {report.DeletedCount} 项 · 回收 {Humanize.Bytes(report.SpaceReclaimed)}");
            Shell.Feedback.Notify(FeedbackKind.Success, $"{title} 完成",
                $"删除 {report.DeletedCount} 项 · 回收 {Humanize.Bytes(report.SpaceReclaimed)}");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            task.Finish(PanelTaskState.Failed, "失败", ex.Message);
            Shell.Feedback.ReportError(title, ex);
        }
    }
}
