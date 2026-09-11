using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>后台任务的状态。</summary>
public enum PanelTaskState
{
    /// <summary>进行中。</summary>
    Running,

    /// <summary>成功结束。</summary>
    Succeeded,

    /// <summary>部分成功(批量操作里有失败的目标)。</summary>
    PartiallyFailed,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>被取消。</summary>
    Cancelled
}

/// <summary>
/// 任务中心里的一个条目。
/// <para>
/// 进度分两种,界面上长得不一样:知道分母的(镜像层字节数、批量的目标个数)画确定型进度条;
/// 不知道要多久的(<c>compose up</c> 等健康检查、<c>docker stop</c> 等 SIGTERM)画不确定型 ——
/// 给后者编一个假的百分比,只会让用户在 90% 处等上两分钟。
/// </para>
/// </summary>
/// <remarks>建一个任务。</remarks>
public sealed class PanelTask(string icon, string title, bool indeterminate) : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>图标资源键。</summary>
    public string Icon { get; } = icon;

    /// <summary>任务标题。</summary>
    public string Title
    {
        get => title;
        set => SetField(ref title, value);
    }

    /// <summary>标题下面那行小字(层数、速率、当前目标)。</summary>
    public string Detail
    {
        get;
        set => SetField(ref field, value);
    } = "";

    /// <summary>0–1 的进度;仅 <see cref="Indeterminate" /> 为假时有意义。</summary>
    public double Progress
    {
        get;
        set => SetField(ref field, Math.Clamp(value, 0, 1));
    }

    /// <summary>是否不确定型。</summary>
    public bool Indeterminate
    {
        get => indeterminate;
        set => SetField(ref indeterminate, value);
    }

    /// <summary>右侧的一小段文字(百分比 / “完成” / “部分失败”)。</summary>
    public string RightText
    {
        get;
        set => SetField(ref field, value);
    } = "";

    /// <summary>状态。</summary>
    public PanelTaskState State
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsRunning), nameof(IsFinished), nameof(CanCancel));
            }
        }
    } = PanelTaskState.Running;

    /// <summary>还在跑。</summary>
    public bool IsRunning => State == PanelTaskState.Running;

    /// <summary>已经结束(成功、失败或取消)。</summary>
    public bool IsFinished => State != PanelTaskState.Running;

    /// <summary>能不能取消。</summary>
    public bool CanCancel => IsRunning && !_cts.IsCancellationRequested;

    /// <summary>任务自己的取消令牌。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>结束后可以点开看详情的东西(批量结果、执行记录)。</summary>
    public object? Payload { get; set; }

    /// <summary>取消这个任务。</summary>
    public void Cancel()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    /// <summary>标记结束。</summary>
    public void Finish(PanelTaskState state, string rightText, string? detail = null)
    {
        State = state;
        RightText = rightText;
        if (detail is not null)
        {
            Detail = detail;
        }
        Indeterminate = false;
        if (state == PanelTaskState.Succeeded)
        {
            Progress = 1;
        }
    }

    /// <summary>释放取消源。</summary>
    public void Dispose() => _cts.Dispose();
}

/// <summary>
/// 任务中心:面板里所有"要花时间"的动作都在这儿登记。
/// <para>
/// 存在的理由是**关掉对话框不该等于取消任务**。拉一个 2 GB 的镜像时,用户多半想
/// 一边等一边去看别的容器;进度移交给顶栏这枚小环,任务照跑。
/// </para>
/// </summary>
public sealed class TaskCenter : ObservableObject
{
    /// <summary>已完成任务的保留条数。再多就没人看了,只会把弹层撑长。</summary>
    private const int MaxFinished = 20;

    /// <summary>全部任务,进行中的排在前面。</summary>
    public ObservableCollection<PanelTask> Tasks { get; } = [];

    /// <summary>进行中的任务数。</summary>
    public int RunningCount => Tasks.Count(t => t.IsRunning);

    /// <summary>已完成的任务数。</summary>
    public int FinishedCount => Tasks.Count(t => t.IsFinished);

    /// <summary>有没有任务在跑(顶栏那枚指示器据此显隐)。</summary>
    public bool HasRunning => RunningCount > 0;

    /// <summary>
    /// 全部进行中任务的合并进度(0–1)。不确定型的任务不参与平均 ——
    /// 把一个"不知道要多久"按 0 计进去,会让整体进度看起来一直卡住。
    /// </summary>
    public double OverallProgress
    {
        get
        {
            List<PanelTask> determinate = [.. Tasks.Where(t => t is { IsRunning: true, Indeterminate: false })];
            return determinate.Count == 0 ? 0 : determinate.Average(t => t.Progress);
        }
    }

    /// <summary>顶栏那枚指示器上的文字。</summary>
    public string IndicatorText => RunningCount switch
    {
        0 => "",
        1 => "1 个任务",
        _ => $"{RunningCount} 个任务"
    };

    /// <summary>顶栏指示器右侧的百分比;没有确定型任务时为空。</summary>
    public string IndicatorPercent
    {
        get
        {
            var progress = OverallProgress;
            return progress <= 0 ? "" : $"{progress * 100:0}%";
        }
    }

    /// <summary>登记一个新任务并放到最前面。</summary>
    public PanelTask Start(string icon, string title, bool indeterminate, string detail = "")
    {
        var task = new PanelTask(icon, title, indeterminate) { Detail = detail };
        task.PropertyChanged += (_, _) => RaiseSummary();
        Ui.Post(() =>
        {
            Tasks.Insert(0, task);
            Trim();
            RaiseSummary();
        });
        return task;
    }

    /// <summary>清掉已完成的。</summary>
    public void ClearFinished()
    {
        Ui.Post(() =>
        {
            foreach (var task in Tasks.Where(t => t.IsFinished).ToArray())
            {
                Tasks.Remove(task);
                task.Dispose();
            }
            RaiseSummary();
        });
    }

    /// <summary>取消全部进行中的任务(面板关闭时)。</summary>
    public void CancelAll()
    {
        foreach (var task in Tasks.Where(t => t.IsRunning).ToArray())
        {
            task.Cancel();
        }
    }

    private void Trim()
    {
        PanelTask[] finished = [.. Tasks.Where(t => t.IsFinished)];
        for (var i = MaxFinished; i < finished.Length; i++)
        {
            Tasks.Remove(finished[i]);
            finished[i].Dispose();
        }
    }

    private void RaiseSummary() => Ui.Post(() =>
        OnPropertiesChanged(nameof(RunningCount), nameof(FinishedCount), nameof(HasRunning),
            nameof(OverallProgress), nameof(IndicatorText), nameof(IndicatorPercent)));
}

/// <summary>
/// 拉取进度的按层聚合。
/// <para>
/// daemon 推来的是一条平铺的 NDJSON,每层的每一次进度都是独立一帧。界面要的是
/// "9 层,6 层复用,2 层在下载" —— 这个类负责把前者变成后者,并算出总字节进度。
/// </para>
/// </summary>
public sealed class PullAggregator
{
    private readonly Dictionary<string, LayerState> _layers = [];

    /// <summary>层的状态。</summary>
    private sealed record LayerState(string Status, long Current, long Total);

    /// <summary>吃进一帧。</summary>
    public void Accept(PullProgressFrame frame)
    {
        if (string.IsNullOrEmpty(frame.Id))
        {
            // 没有 id 的帧是总览行("Pulling from library/nginx"、最终摘要),不计进层里。
            return;
        }
        var current = frame.ProgressDetail?.Current ?? 0;
        var total = frame.ProgressDetail?.Total ?? 0;
        var status = frame.Status ?? "";
        // "Pull complete" / "Already exists" 之后不再有字节明细,
        // 但它们已经算 100% —— 沿用上一次拿到的 total,免得进度条在最后一刻回退。
        if (_layers.TryGetValue(frame.Id, out var previous))
        {
            if (total == 0)
            {
                total = previous.Total;
            }
            if (IsComplete(status))
            {
                current = total;
            }
        }
        _layers[frame.Id] = new(status, current, total);
    }

    /// <summary>层总数。</summary>
    public int LayerCount => _layers.Count;

    /// <summary>已经完成的层数(含复用)。</summary>
    public int CompletedLayers => _layers.Values.Count(l => IsComplete(l.Status));

    /// <summary>复用(本地已有)的层数。</summary>
    public int ReusedLayers => _layers.Values.Count(l =>
        l.Status.Contains("Already exists", StringComparison.OrdinalIgnoreCase));

    /// <summary>正在下载或解压的层数。</summary>
    public int ActiveLayers => _layers.Values.Count(l =>
        l.Status.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase) ||
        l.Status.StartsWith("Extracting", StringComparison.OrdinalIgnoreCase));

    /// <summary>已下载字节。</summary>
    public long CurrentBytes => _layers.Values.Sum(l => l.Current);

    /// <summary>总字节(只统计报了 total 的层)。</summary>
    public long TotalBytes => _layers.Values.Sum(l => l.Total);

    /// <summary>
    /// 总进度 0–1。
    /// <para>
    /// 优先按字节算;一个字节都还没报(全是 "Already exists")时退回按层数算 ——
    /// 否则一次全命中缓存的拉取会显示成 0%,然后直接跳到完成。
    /// </para>
    /// </summary>
    public double Progress =>
        TotalBytes > 0 ? Math.Clamp((double)CurrentBytes / TotalBytes, 0, 1)
        : LayerCount > 0 ? (double)CompletedLayers / LayerCount
        : 0;

    /// <summary>
    /// 一行摘要,给任务中心与对话框共用。
    /// <para>
    /// 写成"完成 3 / 12 层"而不是"12 层":拉一个大镜像要几分钟,
    /// 用户盯着的是**还剩多少**,而层总数从头到尾都不变,说了等于没说。
    /// (<c>CompletedLayers</c> 早就算好了,只是一直没进这句话。)
    /// </para>
    /// </summary>
    public string Summary =>
        LayerCount == 0
            ? "正在连接仓库…"
            : TotalBytes > 0
                ? $"完成 {CompletedLayers} / {LayerCount} 层 · {Humanize.Bytes(CurrentBytes)} / {Humanize.Bytes(TotalBytes)} · 复用 {ReusedLayers} 层"
                : $"完成 {CompletedLayers} / {LayerCount} 层 · 复用 {ReusedLayers} 层";

    /// <summary>逐层快照,给对话框里的层列表用。</summary>
    public IReadOnlyList<(string Id, string Status, double Progress, string SizeText)> Snapshot() =>
        [.. _layers.Select(kv => (
            Id: kv.Key,
            kv.Value.Status,
            Progress: kv.Value.Total > 0 ? Math.Clamp((double)kv.Value.Current / kv.Value.Total, 0, 1) : IsComplete(kv.Value.Status) ? 1d : 0d,
            SizeText: kv.Value.Total > 0
                ? $"{Humanize.Bytes(kv.Value.Current)} / {Humanize.Bytes(kv.Value.Total)}"
                : IsComplete(kv.Value.Status) ? "已就绪" : "待定"))
            .OrderBy(l => IsComplete(l.Status) ? 1 : 0)
            .ThenBy(l => l.Id, StringComparer.Ordinal)];

    private static bool IsComplete(string status) =>
        status.Contains("Already exists", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("Pull complete", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("Layer already exists", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("Pushed", StringComparison.OrdinalIgnoreCase);
}
