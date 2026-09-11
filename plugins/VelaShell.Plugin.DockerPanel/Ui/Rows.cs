using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>行的状态色。界面按它取左侧色条、圆点与状态文字的颜色。</summary>
public enum RowTone
{
    /// <summary>运行中 / 健康 / 成功。</summary>
    Ok,

    /// <summary>暂停 / 需要注意。</summary>
    Warn,

    /// <summary>不健康 / 异常退出。</summary>
    Danger,

    /// <summary>已停止 / 未使用。</summary>
    Idle,

    /// <summary>过渡态(重启中、创建中)。</summary>
    Busy
}

/// <summary>
/// 列表行的公共部分:身份、选中态、忙碌态。
/// <para>
/// 行是**就地合并**的(见 <see cref="KeyedCollection{T}" />):刷新只更新内容,不换实例。
/// 用户选中三行准备批量停止,一条事件刷新一次就全丢了 —— 那是这个面板能犯的最烦人的错误。
/// </para>
/// </summary>
public abstract class RowBase(string id) : ObservableObject
{

    /// <summary>稳定身份。</summary>
    public string Id { get; } = id;

    /// <summary>短 id。</summary>
    public string ShortId => Humanize.ShortId(Id);

    /// <summary>是否被勾选(批量操作的目标)。</summary>
    public bool Selected
    {
        get; set
        {
            if (SetField(ref field, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>
    /// 这一行就是右侧抽屉里正开着的那一个。
    /// <para>
    /// 与 <see cref="Selected" /> 是两件事,底色也不同:勾选是"批量的目标"(强调色底),
    /// 当前是"我正在看的那一个"(中性选中底)。混成一种,勾了三行再点开第四行时
    /// 就分不出哪一行会被下一步的「删除」打到。
    /// </para>
    /// </summary>
    public bool Current { get; set => SetField(ref field, value); }

    /// <summary>
    /// 这一行正在被操作。界面据此在行顶画一条 2px 进度条、把动作按钮换成"取消",
    /// 其余按钮禁用**而不是消失** —— 位置不跳。
    /// </summary>
    public bool Busy { get; set => SetField(ref field, value); }

    /// <summary>行内进度 0–1;为 0 表示不确定型。</summary>
    public double BusyProgress { get; set => SetField(ref field, value); }

    /// <summary>勾选状态变了。</summary>
    public event Action? SelectionChanged;
}

/// <summary>容器列表的一行。</summary>
public sealed class ContainerRow(ContainerSummary summary) : RowBase(summary.Id)
{

    /// <summary>最近若干次 CPU 采样(行内 sparkline 用)。</summary>
    public ObservableCollection<double> CpuSamples { get; } = [];

    /// <summary>
    /// 拥有这一行的页面。
    /// <para>
    /// 存在的唯一理由是右键菜单:<c>ContextMenu</c> 弹在一棵**独立的 popup 树**里,
    /// <c>$parent[ItemsControl]</c> 在那里解析不到页面的视图模型。给行一个回引,
    /// 菜单项就能写成 <c>{Binding Owner.XxxCommand}</c>。
    /// </para>
    /// </summary>
    public Pages.ContainersPageViewModel? Owner { get; set; }

    /// <summary>底层数据。</summary>
    public ContainerSummary Summary { get; private set; } = summary;

    /// <summary>容器名。</summary>
    public string Name => Summary.Name;

    /// <summary>镜像引用。</summary>
    public string Image => Summary.Image ?? "";

    /// <summary>端口摘要。</summary>
    public string Ports => Humanize.Ports(Summary.Ports);

    /// <summary>compose 项目名;不属于任何项目时为空。</summary>
    public string Project => Summary.ComposeProject ?? "";

    /// <summary>有没有 compose 项目。</summary>
    public bool HasProject => Project.Length > 0;

    /// <summary>daemon 的状态串。</summary>
    public string Status => Summary.Status ?? "";

    /// <summary>是否在跑。</summary>
    public bool IsRunning => Summary.State == "running";

    /// <summary>是否暂停。</summary>
    public bool IsPaused => Summary.State == "paused";

    /// <summary>是否不健康。</summary>
    public bool IsUnhealthy => Status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否异常退出。</summary>
    public bool IsFailed => Summary.State == "exited" && !Status.Contains("(0)", StringComparison.Ordinal);

    /// <summary>行的状态色。</summary>
    public RowTone Tone =>
        Summary.State is "restarting" or "created" ? RowTone.Busy
        : IsUnhealthy || IsFailed ? RowTone.Danger
        : IsPaused ? RowTone.Warn
        : IsRunning ? RowTone.Ok
        : RowTone.Idle;

    /// <summary>
    /// 运行时长 / 停止多久。直接用 daemon 那句人话的后半段,
    /// 而不是自己算 —— 它已经把 "Up 3 days (healthy)" 里的括号处理好了。
    /// </summary>
    public string Uptime
    {
        get
        {
            var status = Status;
            if (status.Length == 0)
            {
                return "—";
            }
            // 已退出的容器:括号里的退出码是这一行最要紧的信息,不能跟着括号一起被裁掉。
            if (status.StartsWith("Exited ", StringComparison.Ordinal))
            {
                return "退出 " + status[7..];
            }
            // 运行中的容器:括号里是健康状态,它已经由左侧色条与圆点表达了,这一列只要时长。
            var paren = status.IndexOf(" (", StringComparison.Ordinal);
            var text = paren > 0 ? status[..paren] : status;
            return text.StartsWith("Up ", StringComparison.Ordinal) ? text[3..] : text;
        }
    }

    /// <summary>CPU 占用文本。</summary>
    public string CpuText { get; private set => SetField(ref field, value); } = "—";

    /// <summary>内存占用文本。</summary>
    public string MemText { get; private set => SetField(ref field, value); } = "—";

    /// <summary>CPU 百分比(决定 sparkline 是否转黄)。</summary>
    public double CpuPercent
    {
        get; private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(CpuHot));
            }
        }
    }

    /// <summary>CPU 高到该提醒的程度。</summary>
    public bool CpuHot => CpuPercent >= 30;

    /// <summary>最近一帧的内存占用字节(总览页按它汇总,没采到时为 0)。</summary>
    public long MemoryBytes { get; private set => SetField(ref field, value); }

    /// <summary>用新快照更新这一行。</summary>
    public void Update(ContainerRow incoming)
    {
        Summary = incoming.Summary;
        if (!IsRunning)
        {
            CpuText = "—";
            MemText = "—";
            CpuPercent = 0;
            MemoryBytes = 0;
            if (CpuSamples.Count > 0)
            {
                CpuSamples.Clear();
            }
        }
        OnPropertiesChanged(nameof(Summary), nameof(Name), nameof(Image), nameof(Ports), nameof(Project),
            nameof(HasProject), nameof(Status), nameof(IsRunning), nameof(IsPaused), nameof(IsUnhealthy),
            nameof(IsFailed), nameof(Tone), nameof(Uptime));
    }

    /// <summary>吃进一帧统计。</summary>
    public void ApplyStats(ContainerStats stats)
    {
        CpuPercent = stats.CpuPercent;
        CpuText = Humanize.Percent(stats.CpuPercent);
        MemoryBytes = (long)stats.MemoryUsed;
        // 列表这一格很窄,只放绝对值;"占上限多少"留给详情抽屉,那里有横向空间。
        MemText = Humanize.Bytes(stats.MemoryUsed);
        CpuSamples.Add(stats.CpuPercent);
        // 只留最近 14 个点:行内那条 sparkline 就这么宽,多留的点画不出来也没人看。
        while (CpuSamples.Count > 14)
        {
            CpuSamples.RemoveAt(0);
        }
    }
}

/// <summary>镜像列表的一行。</summary>
public sealed class ImageRow(ImageSummary summary) : RowBase(summary.Id)
{

    /// <summary>拥有这一行的页面(右键菜单用,理由同 <see cref="ContainerRow.Owner" />)。</summary>
    public Pages.ImagesPageViewModel? Owner { get; set; }

    /// <summary>底层数据。</summary>
    public ImageSummary Summary { get; private set; } = summary;

    /// <summary>仓库名(第一个标签的前半段)。</summary>
    public string Repository => SplitTag().Repository;

    /// <summary>标签(第一个标签的后半段)。</summary>
    public string Tag => SplitTag().Tag;

    /// <summary>是否悬空。</summary>
    public bool IsDangling => Summary.IsDangling;

    /// <summary>大小。</summary>
    public string SizeText => Humanize.Bytes(Summary.Size);

    /// <summary>创建时间。</summary>
    public string CreatedText => Humanize.AgoFromUnix(Summary.Created);

    /// <summary>被多少容器使用。</summary>
    public string UsageText => Summary.Containers switch
    {
        < 0 => IsDangling ? "悬空" : "—",
        0 => IsDangling ? "悬空" : "未使用",
        1 => "1 个容器",
        _ => $"{Summary.Containers} 个容器"
    };

    /// <summary>使用情况的语气。</summary>
    public RowTone Tone => IsDangling ? RowTone.Warn : Summary.Containers > 0 ? RowTone.Ok : RowTone.Idle;

    /// <summary>全部标签(详情用)。</summary>
    public string AllTags => Summary.RepoTags is { Length: > 0 } tags ? string.Join(" · ", tags) : "<none>";

    /// <summary>用新快照更新。</summary>
    public void Update(ImageRow incoming)
    {
        Summary = incoming.Summary;
        OnPropertiesChanged(nameof(Summary), nameof(Repository), nameof(Tag), nameof(IsDangling),
            nameof(SizeText), nameof(CreatedText), nameof(UsageText), nameof(Tone), nameof(AllTags));
    }

    private (string Repository, string Tag) SplitTag()
    {
        var first = Summary.RepoTags is { Length: > 0 } tags ? tags[0] : "<none>:<none>";
        var colon = first.LastIndexOf(':');
        // 冒号可能属于端口(registry:5000/foo),所以只有它出现在最后一个斜杠之后才是标签分隔。
        var slash = first.LastIndexOf('/');
        return colon > slash && colon > 0 ? (first[..colon], first[(colon + 1)..]) : (first, "latest");
    }
}

/// <summary>卷列表的一行。</summary>
public sealed class VolumeRow(VolumeSummary summary, int refCount) : RowBase(summary.Name)
{

    /// <summary>拥有这一行的页面(列宽绑在页面上,理由同 <see cref="ContainerRow.Owner" />)。</summary>
    public Pages.VolumesPageViewModel? Owner { get; set; }

    /// <summary>底层数据。</summary>
    public VolumeSummary Summary { get; private set; } = summary;

    /// <summary>卷名。</summary>
    public string Name => Summary.Name;

    /// <summary>驱动。</summary>
    public string Driver => Summary.Driver ?? "local";

    /// <summary>挂载点。</summary>
    public string Mountpoint => Summary.Mountpoint ?? "";

    /// <summary>创建时间。</summary>
    public string CreatedText => Humanize.LocalTime(Summary.CreatedAt);

    /// <summary>被多少容器引用。</summary>
    public int RefCount { get; private set; } = refCount;

    /// <summary>使用情况文本。</summary>
    public string UsageText => RefCount switch
    {
        <= 0 => "未使用",
        1 => "1 个容器",
        _ => $"{RefCount} 个容器"
    };

    /// <summary>大小;<c>/system/df</c> 没算过时给破折号。</summary>
    public string SizeText => Summary.UsageData is { Size: >= 0 } usage ? Humanize.Bytes(usage.Size) : "—";

    /// <summary>语气。</summary>
    public RowTone Tone => RefCount > 0 ? RowTone.Ok : RowTone.Warn;

    /// <summary>compose 项目名。</summary>
    public string Project => Summary.Labels?.GetValueOrDefault("com.docker.compose.project") ?? "";

    /// <summary>用新快照更新。</summary>
    public void Update(VolumeRow incoming)
    {
        Summary = incoming.Summary;
        RefCount = incoming.RefCount;
        OnPropertiesChanged(nameof(Summary), nameof(Name), nameof(Driver), nameof(Mountpoint),
            nameof(CreatedText), nameof(RefCount), nameof(UsageText), nameof(SizeText), nameof(Tone), nameof(Project));
    }
}

/// <summary>网络列表的一行。</summary>
public sealed class NetworkRow(NetworkSummary summary) : RowBase(summary.Id)
{

    /// <summary>拥有这一行的页面(列宽绑在页面上,理由同 <see cref="ContainerRow.Owner" />)。</summary>
    public Pages.NetworksPageViewModel? Owner { get; set; }

    /// <summary>底层数据。</summary>
    public NetworkSummary Summary { get; private set; } = summary;

    /// <summary>网络名。</summary>
    public string Name => Summary.Name;

    /// <summary>驱动。</summary>
    public string Driver => Summary.Driver ?? "";

    /// <summary>作用域。</summary>
    public string Scope => Summary.Scope ?? "";

    /// <summary>第一段子网。</summary>
    public string Subnet => Summary.FirstSubnet ?? "—";

    /// <summary>是否内置(bridge/host/none —— 删不掉)。</summary>
    public bool IsPredefined => Summary.IsPredefined;

    /// <summary>接入的容器数。</summary>
    public int AttachedCount => Summary.Containers?.Count ?? 0;

    /// <summary>接入情况文本。</summary>
    public string AttachedText => AttachedCount switch
    {
        0 => "未接入",
        1 => "1 个容器",
        _ => $"{AttachedCount} 个容器"
    };

    /// <summary>语气。</summary>
    public RowTone Tone => IsPredefined ? RowTone.Idle : AttachedCount > 0 ? RowTone.Ok : RowTone.Warn;

    /// <summary>用新快照更新。</summary>
    public void Update(NetworkRow incoming)
    {
        Summary = incoming.Summary;
        OnPropertiesChanged(nameof(Summary), nameof(Name), nameof(Driver), nameof(Scope), nameof(Subnet),
            nameof(IsPredefined), nameof(AttachedCount), nameof(AttachedText), nameof(Tone));
    }
}
