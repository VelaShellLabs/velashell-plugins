using Avalonia.Controls;
using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>容器页的筛选档。</summary>
public enum ContainerFilter
{
    /// <summary>全部。</summary>
    All,

    /// <summary>运行中。</summary>
    Running,

    /// <summary>已停止。</summary>
    Stopped,

    /// <summary>异常(不健康 / 非零退出)。</summary>
    Problem
}

/// <summary>
/// 筛选弹层里的一项 compose 项目(设计稿 19 号板)。
/// <para>
/// 名字为空串代表「不属于任何项目」—— 那也是一档真实的筛选,而不是"没选"。
/// </para>
/// </summary>
/// <param name="Name">项目名;空串表示不属于任何项目。</param>
/// <param name="Label">显示文字。</param>
/// <param name="Count">这一档有几个容器。</param>
public readonly record struct ProjectFilterItem(string Name, string Label, int Count);

/// <summary>容器页。</summary>
public sealed class ContainersPageViewModel : PageViewModel, IAsyncDisposable
{
    private readonly List<ContainerRow> _all = [];
    private readonly StatsSampler _sampler;

    /// <summary>建容器页。</summary>
    public ContainersPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        _sampler = new(() => Client);
        SetFilterCommand = new RelayCommand(p =>
        {
            if (p is ContainerFilter filter)
            {
                Filter = filter;
            }
        });
        OpenDetailCommand = new RelayCommand(p => p is ContainerRow row ? OpenDetailAsync(row) : Task.CompletedTask);
        CloseDetailCommand = new RelayCommand(_ => CloseDetail());
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        StartCommand = new RelayCommand(p => BatchAsync("启动", Targets(p),
            (c, id, ct) => c.StartContainerAsync(id, ct)));
        StopCommand = new RelayCommand(p => BatchAsync("停止", Targets(p),
            (c, id, ct) => c.StopContainerAsync(id, 10, ct)));
        RestartCommand = new RelayCommand(p => BatchAsync("重启", Targets(p),
            (c, id, ct) => c.RestartContainerAsync(id, 10, ct)));
        PauseCommand = new RelayCommand(p => BatchAsync("暂停", Targets(p),
            (c, id, ct) => c.PauseContainerAsync(id, ct)));
        UnpauseCommand = new RelayCommand(p => BatchAsync("恢复", Targets(p),
            (c, id, ct) => c.UnpauseContainerAsync(id, ct)));
        KillCommand = new RelayCommand(p => KillAsync(Targets(p)));
        RemoveCommand = new RelayCommand(p => RemoveAsync(Targets(p)));
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
        // 列表让不让位,除了日志模式还取决于抽屉铺没铺满 —— 后者的通知在抽屉那边发。
        Drawer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DrawerState.ListVisible))
            {
                OnPropertyChanged(nameof(ListVisible));
            }
        };
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Containers;

    /// <inheritdoc />
    public override string Title => "容器";

    /// <summary>过滤后的行(界面绑这个)。</summary>
    public KeyedCollection<ContainerRow> View { get; } = new(r => r.Id);

    /// <summary>当前筛选档。</summary>
    public ContainerFilter Filter
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsAll), nameof(IsRunningFilter), nameof(IsStoppedFilter), nameof(IsProblemFilter));
                ApplyView();
            }
        }
    } = ContainerFilter.All;

    /// <summary>筛选:全部。</summary>
    public bool IsAll => Filter == ContainerFilter.All;

    /// <summary>筛选:运行中。</summary>
    public bool IsRunningFilter => Filter == ContainerFilter.Running;

    /// <summary>筛选:已停止。</summary>
    public bool IsStoppedFilter => Filter == ContainerFilter.Stopped;

    /// <summary>筛选:异常。</summary>
    public bool IsProblemFilter => Filter == ContainerFilter.Problem;

    /// <summary>搜索词(名字 / 镜像 / 项目)。</summary>
    public string Search
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyView();
            }
        }
    } = "";

    /// <summary>全部数量。</summary>
    public int TotalCount => _all.Count;

    /// <summary>运行中数量。</summary>
    public int RunningCount => _all.Count(r => r.IsRunning);

    /// <summary>已停止数量。</summary>
    public int StoppedCount => _all.Count(r => !r.IsRunning && !r.IsPaused);

    /// <summary>异常数量。</summary>
    public int ProblemCount => _all.Count(r => r.IsUnhealthy || r.IsFailed);

    /// <summary>
    /// 筛选弹层里的 compose 项目清单(设计稿 19 号板)。
    /// <para>
    /// 状态那一档已经在工具条的分段控件上,所以弹层里只放项目 ——
    /// 同一个筛选出现在两个地方,用户改了一处就再也说不清当前到底筛掉了什么。
    /// </para>
    /// </summary>
    public ObservableCollection<ProjectFilterItem> ProjectFilters { get; } = [];

    /// <summary>当前项目筛选;<see langword="null" /> 表示不按项目筛。</summary>
    public string? ProjectFilter
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(HasProjectFilter), nameof(ProjectFilterLabel));
                ApplyView();
            }
        }
    }

    /// <summary>按项目筛着没有。</summary>
    public bool HasProjectFilter => ProjectFilter is not null;

    /// <summary>筛选按钮上显示的文字。</summary>
    public string ProjectFilterLabel => ProjectFilter switch
    {
        null => "项目",
        "" => "不属于任何项目",
        _ => ProjectFilter
    };

    /// <summary>选一个项目来筛;参数为 <see langword="null" /> 时清除。</summary>
    public RelayCommand SetProjectFilterCommand => field ??= new(p =>
    {
        ProjectFilter = p as string;
        return Task.CompletedTask;
    });

    /// <summary>清除项目筛选。</summary>
    public RelayCommand ClearProjectFilterCommand => field ??= new(_ =>
    {
        ProjectFilter = null;
        return Task.CompletedTask;
    });

    private void BuildProjectFilters()
    {
        ProjectFilters.Clear();
        foreach (var group in _all
                     .GroupBy(r => r.Project)
                     .Where(g => g.Key.Length > 0)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ProjectFilters.Add(new(group.Key, group.Key, group.Count()));
        }
        var loose = _all.Count(r => r.Project.Length == 0);
        if (loose > 0)
        {
            ProjectFilters.Add(new("", "(不属于任何项目)", loose));
        }
        // 筛着的项目没了(最后一个容器被删了)就自动松开,否则列表会空得莫名其妙。
        if (ProjectFilter is { } current && ProjectFilters.All(p => p.Name != current))
        {
            ProjectFilter = null;
        }
    }

    /// <summary>已勾选的数量。</summary>
    public int SelectedCount
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(SelectionText));
            }
        }
    }

    /// <summary>有没有勾选。</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>选中条上的文字。</summary>
    public string SelectionText => $"已选 {SelectedCount} 个容器";

    /// <summary>
    /// 列头那枚全选框。作用范围是**当前可见的**那些行,不是 _all ——
    /// 筛到「已停止 5」时按下全选,用户要的是这 5 个;
    /// 把背后那 13 个运行中的一起勾上,下一步「删除」就成了事故。
    /// 部分勾选时停在第三态,再按一次全勾上。
    /// </summary>
    public bool? AllSelected
    {
        get
        {
            if (View.Count == 0)
            {
                return false;
            }
            var picked = View.Count(r => r.Selected);
            return picked == 0 ? false : picked == View.Count ? true : null;
        }
        set
        {
            var select = value is true;
            foreach (var row in View)
            {
                row.Selected = select;
            }
            RecountSelection();
        }
    }

    /// <summary>列表是不是空的(且已经加载过)。</summary>
    public bool IsEmpty => LoadedOnce && _all.Count == 0;

    /// <summary>筛完之后没有匹配项。</summary>
    public bool NoMatch => LoadedOnce && _all.Count > 0 && View.Count == 0;

    /// <summary>详情抽屉;没打开时为 <see langword="null" />。</summary>
    public ContainerDetailViewModel? Detail
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                Drawer.IsOpen = value is not null;
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    /// <summary>详情抽屉开着没有。</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>
    /// 列表的列宽。列头与几百行数据行共用这一份 —— 拖列头的轨道改的就是它。
    /// <para>
    /// 放在**页面**上而不是行上:列宽是这张表的属性,不是某一行的。
    /// </para>
    /// </summary>
    public ContainerColumns Columns { get; } = new();

    /// <inheritdoc />
    public override ListColumns ColumnLayout => Columns;

    /// <inheritdoc />
    public override IEnumerable<string> ColumnTexts(string key) => key switch
    {
        "name" => View.Select(r => r.Name),
        "image" => View.Select(r => r.Image),
        "ports" => View.Select(r => r.Ports),
        "cpu" => View.Select(r => r.CpuText),
        "mem" => View.Select(r => r.MemText),
        "uptime" => View.Select(r => r.Uptime),
        _ => []
    };

    /// <summary>列表这一块要不要露出来(日志模式取代它,抽屉最大化盖住它)。</summary>
    public bool ListVisible => !IsLogsMode && Drawer.ListVisible;

    /// <summary>切筛选。</summary>
    public RelayCommand SetFilterCommand { get; }

    /// <summary>打开详情。</summary>
    public RelayCommand OpenDetailCommand { get; }

    /// <summary>关掉详情。</summary>
    public RelayCommand CloseDetailCommand { get; }

    /// <summary>取消勾选。</summary>
    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>启动。</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>停止。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>重启。</summary>
    public RelayCommand RestartCommand { get; }

    /// <summary>暂停。</summary>
    public RelayCommand PauseCommand { get; }

    /// <summary>恢复。</summary>
    public RelayCommand UnpauseCommand { get; }

    /// <summary>强杀。</summary>
    public RelayCommand KillCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>
    /// 「运行容器…」:去镜像页挑一个。
    /// 不直接开运行表单 —— 那张表单的镜像字段是只读的,得先有镜像才谈得上运行。
    /// </summary>
    public RelayCommand RunFromImagesCommand => field ??= new(_ => Shell.GoToAsync(PanelPage.Images));

    /// <summary>工具条上的「拉取镜像」:和镜像页那颗是同一个对话框。</summary>
    public RelayCommand PullImageCommand => field ??= new(_ => Shell.ShowPullDialogAsync(null));

    /// <summary>空列表那一屏的「从 compose 起一套」。</summary>
    public RelayCommand GoComposeCommand => field ??= new(_ => Shell.GoToAsync(PanelPage.Compose));

    /// <inheritdoc />
    public override async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        StartSampling();
    }

    /// <summary>
    /// 开始(或重启)统计采样。
    /// <para>
    /// 采样<b>不</b>只在容器页跑:总览页的 CPU / 内存两张卡和 Top 5 都吃这一份数据,
    /// 而用户完全可能整段时间都待在总览页。所以连上之后就起,断开才停。
    /// </para>
    /// <para>
    /// 采的是 <c>_all</c> 而不是 <c>View</c>:后者被搜索框和筛选档裁过,
    /// 用它算出来的"CPU 总占用"会跟着用户输入的关键字变 —— 那不是一个总量。
    /// </para>
    /// </summary>
    public void StartSampling()
    {
        if (!Shell.Settings.InlineSparklines)
        {
            _sampler.Stop();
            Shell.Overview.SetStatsDisabled();
            return;
        }
        _sampler.Start(() => [.. _all.Where(r => r.IsRunning)],
            rows => Shell.Overview.AcceptStatsSnapshot(rows), Shell.Lifetime);
    }

    /// <summary>停止采样(断开时)。</summary>
    public void StopSampling() => _sampler.Stop();

    /// <summary>
    /// 连上之后在后台把容器列表读一遍并起采样 —— 让总览页不必等用户先逛一趟容器页。
    /// </summary>
    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        if (_all.Count == 0)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Shell.Context.Log.Warn($"containers: prime failed: {ex.Message}");
                return;
            }
        }
        StartSampling();
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
            var summaries = await client.ListContainersAsync(true, cancellationToken).ConfigureAwait(true);
            // 在跑的排前面,同组内按名字 —— 运维找的十有八九是正在跑的那几个。
            List<ContainerRow> incoming =
            [
                .. summaries
                    .OrderByDescending(s => s.State == "running")
                    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new ContainerRow(s))
            ];
            var previous = _all.ToDictionary(r => r.Id);
            _all.Clear();
            foreach (var row in incoming)
            {
                if (previous.TryGetValue(row.Id, out var existing))
                {
                    existing.Update(row);
                    _all.Add(existing);
                }
                else
                {
                    row.SelectionChanged += RecountSelection;
                    // 右键菜单要经这个回引找到页面的命令(菜单弹在独立的 popup 树里)。
                    row.Owner = this;
                    _all.Add(row);
                }
            }
            LoadedOnce = true;
            BuildProjectFilters();
            ApplyView();
            OnPropertiesChanged(nameof(TotalCount), nameof(RunningCount), nameof(StoppedCount), nameof(ProblemCount));
            Shell.SetContainerCount(_all.Count);
            if (Detail is { } detail && _all.FirstOrDefault(r => r.Id == detail.ContainerId) is { } updated)
            {
                detail.ApplySummary(updated);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _ = CloseDetail(force: true);
        _ = ExitLogsModeAsync();
        _sampler.Stop();
        _all.Clear();
        View.Clear();
        LoadedOnce = false;
        SelectedCount = 0;
        OnPropertiesChanged(nameof(TotalCount), nameof(RunningCount), nameof(StoppedCount), nameof(ProblemCount),
            nameof(IsEmpty), nameof(NoMatch));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type == "container";

    private void ApplyView()
    {
        var needle = Search.Trim();
        var filtered = _all.Where(row => Filter switch
        {
            ContainerFilter.Running => row.IsRunning,
            ContainerFilter.Stopped => !row.IsRunning && !row.IsPaused,
            ContainerFilter.Problem => row.IsUnhealthy || row.IsFailed,
            _ => Shell.Settings.ShowStopped || row.IsRunning || row.IsPaused
        });
        if (ProjectFilter is { } project)
        {
            filtered = filtered.Where(row => row.Project == project);
        }
        if (needle.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Image.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Project.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Id.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        }
        View.Merge([.. filtered], (_, _) => { });
        OnPropertiesChanged(nameof(IsEmpty), nameof(NoMatch), nameof(AllSelected));
    }

    private void RecountSelection()
    {
        SelectedCount = _all.Count(r => r.Selected);
        OnPropertyChanged(nameof(AllSelected));
    }

    private void ClearSelection()
    {
        foreach (var row in _all.Where(r => r.Selected))
        {
            row.Selected = false;
        }
        SelectedCount = 0;
        OnPropertyChanged(nameof(AllSelected));
    }

    /// <summary>
    /// 命令参数是单行时只作用于那一行,否则作用于全部勾选项。
    /// <para>
    /// 这条规则让"行内按钮"与"选中条按钮"共用一套命令 —— 行内那颗按的是它自己,
    /// 顶上那颗按的是所有勾选的。用户不必猜哪颗按钮管哪些容器。
    /// </para>
    /// </summary>
    private IReadOnlyList<ContainerRow> Targets(object? parameter) =>
        parameter is ContainerRow row ? [row] : [.. _all.Where(r => r.Selected)];

    private async Task BatchAsync(string verb, IReadOnlyList<ContainerRow> targets,
        Func<DockerClient, string, CancellationToken, Task> action)
    {
        if (Client is not { } client || targets.Count == 0)
        {
            return;
        }
        foreach (var row in targets)
        {
            row.Busy = true;
        }
        var task = Shell.Tasks.Start("Docker.box", $"{verb} {targets.Count} 个容器", indeterminate: targets.Count == 1);
        // 选中条原地变进度条:不弹窗、不遮列表,选中也不丢。
        BatchProgress = new(verb, targets.Count, task);
        try
        {
            var result = await BatchRunner.RunAsync(
                [.. targets.Select(r => (Target: r, r.Name))],
                (row, ct) => action(client, row.Id, ct),
                (done, total, current) =>
                {
                    task.Progress = total == 0 ? 0 : (double)done / total;
                    task.Indeterminate = total == 1;
                    task.Detail = current.Length > 0 ? $"{done}/{total} · 当前:{current}" : $"{done}/{total}";
                    Ui.Post(() => BatchProgress?.Advance(done, current, DescribeWait(verb, current)));
                },
                task.Token).ConfigureAwait(true);
            task.Finish(
                result.AllSucceeded ? PanelTaskState.Succeeded : PanelTaskState.PartiallyFailed,
                result.AllSucceeded ? "完成" : $"成功 {result.SucceededCount} · 失败 {result.FailedCount}");
            task.Payload = result;
            Shell.Feedback.ReportBatch(verb, result, inView: Shell.CurrentPage == PanelPage.Containers,
                () => Shell.TaskCenterOpen = true);
            // 有失败的就把结果留在选中条上,带一个「重试失败的 N 个」——
            // 让用户为了重试去翻任务中心,是把最想立刻做的那件事藏起来。
            if (!result.AllSucceeded)
            {
                BatchOutcome[] failures = [.. result.Failures];
                BatchSummary = new(verb, result, [.. targets.Where(t => failures.Any(f => f.Target == t.Name))],
                    action);
            }
            BatchProgress = null;
        }
        finally
        {
            BatchProgress = null;
            foreach (var row in targets)
            {
                row.Busy = false;
            }
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
    }

    /// <summary>「当前在等什么」——- 说清楚比只报一个百分比有用。</summary>
    private static string DescribeWait(string verb, string current) => current.Length == 0 ? "" : verb switch
    {
        "停止" => $"{current} · 等待 SIGTERM(10s 超时)",
        "重启" => $"{current} · 等待重新启动",
        "删除" => $"{current} · 等待可写层被回收",
        _ => current
    };

    /// <summary>批量进行中的原地进度;没有时为 <see langword="null" />。</summary>
    public BatchProgressState? BatchProgress
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasBatchProgress));
            }
        }
    }

    /// <summary>批量正在跑。</summary>
    public bool HasBatchProgress => BatchProgress is not null;

    /// <summary>批量结束后的结果条(只在有失败时出现)。</summary>
    public BatchSummaryState? BatchSummary
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasBatchSummary));
            }
        }
    }

    /// <summary>有失败结果要展示。</summary>
    public bool HasBatchSummary => BatchSummary is not null;

    /// <summary>关掉结果条。</summary>
    public RelayCommand DismissBatchCommand => field ??= new(_ =>
    {
        BatchSummary = null;
        return Task.CompletedTask;
    });

    /// <summary>只对失败的那几个再跑一遍。</summary>
    public RelayCommand RetryFailedCommand => field ??= new(_ =>
    {
        if (BatchSummary is not { } summary)
        {
            return Task.CompletedTask;
        }
        BatchSummary = null;
        return BatchAsync(summary.Verb, summary.FailedRows, summary.Action);
    });

    private async Task KillAsync(IReadOnlyList<ContainerRow> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = targets.Count == 1 ? $"强制杀死 {targets[0].Name}?" : $"强制杀死 {targets.Count} 个容器?",
            Icon = "Icon.zap",
            HostName = "",
            ConfirmLabel = "强制杀死",
            ConfirmIcon = "Icon.zap",
            Commands = [.. targets.Select(t => $"POST /containers/{t.Name}/kill?signal=SIGKILL")],
            CommandNote = $"等价于  docker kill {string.Join(' ', targets.Select(t => t.Name))}",
            Targets = [.. targets.Select(ToConfirmTarget)],
            Consequences =
            [
                new(3, "SIGKILL 不给进程刷缓冲、关连接的机会 —— 写到一半的数据会丢。"),
                new(0, "除非它已经卡死,否则该用「停止」:那会先发 SIGTERM,等 10 秒再动手。")
            ]
        })).ConfigureAwait(true);
        if (confirmed)
        {
            await BatchAsync("强杀", targets, (c, id, ct) => c.KillContainerAsync(id, "SIGKILL", ct)).ConfigureAwait(true);
        }
    }

    private async Task RemoveAsync(IReadOnlyList<ContainerRow> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }
        var anyRunning = targets.Any(t => t.IsRunning);
        string[] projects = [.. targets.Select(t => t.Project).Where(p => p.Length > 0).Distinct()];
        List<ConfirmConsequence> consequences =
        [
            new(3, "容器及其可写层会被删除,写在容器里(不在卷里)的数据丢失。"),
            new(1, "挂载的命名卷不受影响 —— 本次不带 -v。")
        ];
        if (anyRunning)
        {
            consequences.Insert(0, new(2, "其中有正在运行的容器,会先被强制停止。"));
        }
        if (projects.Length > 0)
        {
            consequences.Add(new(0,
                $"它们属于 compose 项目 {string.Join('、', projects)},下次 up -d 会按 compose.yaml 重建。"));
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = targets.Count == 1 ? $"删除容器 {targets[0].Name}?" : $"删除 {targets.Count} 个容器?",
            Icon = "Icon.trash-2",
            HostName = "",
            ConfirmLabel = targets.Count == 1 ? "删除容器" : $"删除 {targets.Count} 个容器",
            Commands = [.. targets.Select(t => $"DELETE /containers/{t.Name}?v=false&force={(anyRunning ? "true" : "false")}")],
            CommandNote = $"等价于  docker rm {(anyRunning ? "-f " : "")}{string.Join(' ', targets.Select(t => t.Name))}",
            Targets = [.. targets.Select(ToConfirmTarget)],
            Consequences = consequences
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        await CloseDetailIfRemoved(targets);
        await BatchAsync("删除", targets, (c, id, ct) => c.RemoveContainerAsync(id, anyRunning, false, ct)).ConfigureAwait(true);
        ClearSelection();
    }

    private async Task CloseDetailIfRemoved(IReadOnlyList<ContainerRow> targets)
    {
        // 钉住也拦不住这一条:容器都删了,留一个指向它的抽屉没有意义。
        if (Detail is { } detail && targets.Any(t => t.Id == detail.ContainerId))
        {
            await CloseDetail(force: true);
        }
    }

    private static ConfirmTarget ToConfirmTarget(ContainerRow row) =>
        new(row.Name, row.Image, row.Status, row.IsRunning);

    // ── 日志模式 ──────────────────────────────────────────────────

    /// <summary>
    /// 合并日志视图;<see langword="null" /> 表示当前是普通列表模式。
    /// <para>
    /// 它取代**列表**而不是叠在上面:合并多条流时左边要放来源选择器,
    /// 而那块地方原本就是列表的。
    /// </para>
    /// </summary>
    public LogsViewModel? MergedLogs
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsLogsMode), nameof(ListVisible));
            }
        }
    }

    /// <summary>在日志模式。</summary>
    public bool IsLogsMode => MergedLogs is not null;

    /// <summary>来源选择器里的条目。</summary>
    public ObservableCollection<LogSourceItem> LogSources { get; } = [];

    /// <summary>来源过滤词。</summary>
    public string LogSourceSearch
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyLogSourceView();
            }
        }
    } = "";

    /// <summary>选中的来源数。</summary>
    public int SelectedSourceCount => LogSources.Count(s => s.Selected);

    /// <summary>合并了几条流。</summary>
    public string MergedText => SelectedSourceCount switch
    {
        0 => "没有选中来源",
        1 => "1 条流",
        _ => $"合并 {SelectedSourceCount} 条流"
    };

    /// <summary>进入日志模式(用当前勾选的容器,没勾就用参数那一个)。</summary>
    public RelayCommand ViewLogsCommand => field ??= new(p => EnterLogsModeAsync(Targets(p)));

    /// <summary>回到列表。</summary>
    public RelayCommand ExitLogsCommand => field ??= new(_ => ExitLogsModeAsync());

    /// <summary>
    /// 从合并流里摘掉一条来源。
    /// 走勾选那条路,左边面板的状态跟着一起变 —— 两处不能各说各话。
    /// </summary>
    private void RemoveLogSource(LogSource source)
    {
        if (LogSources.FirstOrDefault(s => s.Source.ContainerId == source.ContainerId) is { } item)
        {
            item.Selected = false;
        }
    }

    /// <summary>来源全选 / 全不选。</summary>
    public RelayCommand ToggleAllSourcesCommand => field ??= new(_ =>
    {
        var select = LogSources.Any(s => !s.Selected);
        foreach (var item in LogSources)
        {
            item.Selected = select;
        }
        return Task.CompletedTask;
    });

    private async Task EnterLogsModeAsync(IReadOnlyList<ContainerRow> seed)
    {
        BuildLogSources(seed);
        var logs = new LogsViewModel(Shell, SelectedSources())
        {
            // chip 上的 × 走勾选那条路,左边面板跟着一起变。
            SourceRemover = source =>
            {
                RemoveLogSource(source);
                return Task.CompletedTask;
            }
        };
        MergedLogs = logs;
        await logs.EnsureStartedAsync().ConfigureAwait(true);
    }

    private async Task ExitLogsModeAsync()
    {
        if (MergedLogs is { } logs)
        {
            MergedLogs = null;
            await logs.DisposeAsync().ConfigureAwait(true);
        }
        foreach (var item in LogSources)
        {
            item.SelectionChanged -= OnLogSourceToggled;
        }
        LogSources.Clear();
    }

    private void BuildLogSources(IReadOnlyList<ContainerRow> seed)
    {
        foreach (var existing in LogSources)
        {
            existing.SelectionChanged -= OnLogSourceToggled;
        }
        LogSources.Clear();
        HashSet<string> seeded = [.. seed.Select(r => r.Id)];
        // 全部容器都列出来(含已停止的)—— 已停止容器的日志依然读得到,
        // 而"为什么它退出了"恰恰是最常要看的一份日志。
        var index = 0;
        foreach (var row in _all)
        {
            var item = new LogSourceItem(
                new(row.Id, row.Name, row.Summary.State == "running"),
                row.IsRunning ? "运行中" : row.IsPaused ? "已暂停" : row.Uptime,
                row.Tone,
                index++)
            {
                Selected = seeded.Contains(row.Id)
            };
            item.SelectionChanged += OnLogSourceToggled;
            LogSources.Add(item);
        }
        ApplyLogSourceView();
    }

    private void OnLogSourceToggled()
    {
        OnPropertiesChanged(nameof(SelectedSourceCount), nameof(MergedText));
        // 勾选变化直接换流。清屏是有意的 —— 合并流按到达顺序排,
        // 留着旧行再补新来源的 tail,拼出来的时间线是假的。
        _ = MergedLogs?.SetSourcesAsync(SelectedSources());
    }

    private List<LogSource> SelectedSources()
    {
        // 颜色序号按**选中顺序里的位置**重新编号,而不是用列表里的下标:
        // 否则只选两个相邻容器时会拿到两个几乎一样的颜色。
        List<LogSourceItem> selected = [.. LogSources.Where(s => s.Selected)];
        for (var i = 0; i < selected.Count; i++)
        {
            selected[i].Index = i;
        }
        return [.. selected.Select(s => s.Source)];
    }

    private void ApplyLogSourceView()
    {
        var needle = LogSourceSearch.Trim();
        foreach (var item in LogSources)
        {
            item.Visible = needle.Length == 0
                           || item.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 行级动作(右键菜单 / 命令面板都走这几个)────────────────────

    /// <summary>看这一个容器的日志(详情抽屉的日志页签)。</summary>
    public RelayCommand RowLogsCommand => field ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Logs) : Task.CompletedTask);

    /// <summary>进这一个容器的终端。</summary>
    public RelayCommand RowTerminalCommand => field ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Terminal) : Task.CompletedTask);

    /// <summary>浏览这一个容器的文件。</summary>
    public RelayCommand RowFilesCommand => field ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Files) : Task.CompletedTask);

    /// <summary>看这一个容器的实时统计。</summary>
    public RelayCommand RowStatsCommand => field ??= new(p =>
        p is ContainerRow row ? OpenDetailAtTabAsync(row, DetailTab.Stats) : Task.CompletedTask);

    /// <summary>重命名。</summary>
    public RelayCommand RowRenameCommand => field ??= new(p =>
        p is ContainerRow row ? RowDetailActionAsync(row, d => d.RenameCommand) : Task.CompletedTask);

    /// <summary>改重启策略。</summary>
    public RelayCommand RowRestartPolicyCommand => field ??= new(p =>
        p is ContainerRow row ? RowDetailActionAsync(row, d => d.RestartPolicyCommand) : Task.CompletedTask);

    /// <summary>复制容器 id。</summary>
    public RelayCommand RowCopyIdCommand => field ??= new(p =>
        p is ContainerRow row ? CopyAsync(row.Id, "容器 ID") : Task.CompletedTask);

    /// <summary>复制等价的 <c>docker run</c> 命令。</summary>
    public RelayCommand RowCopyRunCommand => field ??= new(p =>
        p is ContainerRow row ? CopyRunCommandAsync(row) : Task.CompletedTask);

    private async Task OpenDetailAtTabAsync(ContainerRow row, DetailTab tab)
    {
        await OpenDetailAsync(row).ConfigureAwait(true);
        if (Detail is { } detail)
        {
            await detail.GoToTabAsync(tab).ConfigureAwait(true);
        }
    }

    /// <summary>菜单里那几项其实住在详情抽屉上 —— 先把抽屉打开,再借它的命令。</summary>
    private async Task RowDetailActionAsync(ContainerRow row, Func<ContainerDetailViewModel, RelayCommand> pick)
    {
        await OpenDetailAsync(row).ConfigureAwait(true);
        if (Detail is { } detail)
        {
            pick(detail).Execute(null);
        }
    }

    private async Task CopyAsync(string text, string what)
    {
        await Shell.Context.Clipboard.SetTextAsync(text, Shell.Lifetime).ConfigureAwait(true);
        Shell.Feedback.Status(FeedbackKind.Success, $"已复制{what}");
    }

    /// <summary>
    /// 反推 <c>docker run</c> 并复制。
    /// <para>
    /// 会顺带读一次镜像的 inspect —— 为的是把镜像自带的环境变量减掉。
    /// 不减的话复制出来的命令会拖着十几条 <c>PATH</c> / <c>LANG</c>,那些不是用户写的。
    /// </para>
    /// </summary>
    private async Task CopyRunCommandAsync(ContainerRow row)
    {
        if (Client is not { } client)
        {
            return;
        }
        try
        {
            var inspect = await client.InspectContainerAsync(row.Id, Shell.Lifetime).ConfigureAwait(true);
            string[]? imageEnv = null;
            if (inspect.Config?.Image is { Length: > 0 } image)
            {
                try
                {
                    var imageInspect = await client.InspectImageAsync(image, Shell.Lifetime).ConfigureAwait(true);
                    imageEnv = imageInspect.Config?.Env;
                }
                catch (Exception)
                {
                    // 镜像已经被删了(容器还在跑)也很常见 —— 那就不减,多几条环境变量而已。
                }
            }
            var command = RunCommandBuilder.Build(inspect, imageEnv);
            await Shell.Context.Clipboard
                .SetTextAsync($"{command}\n{RunCommandBuilder.Caveat}", Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "已复制 docker run 命令",
                "由 inspect 反推,是近似值 —— 执行前请核对(提醒已一并复制)。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("生成 docker run 命令", ex);
        }
    }

    /// <summary>打开某个容器的详情,并把文件页落到指定目录(卷页借道过来时用)。</summary>
    public async Task OpenDetailAtFilesAsync(ContainerRow row, string path)
    {
        await OpenDetailAsync(row).ConfigureAwait(true);
        if (Detail is { } detail)
        {
            await detail.OpenFilesAtAsync(path).ConfigureAwait(true);
        }
    }

    private async Task OpenDetailAsync(ContainerRow row)
    {
        if (Detail is { } existing && existing.ContainerId == row.Id)
        {
            return;
        }
        // 换容器时钉住也要让位 —— 用户明确点了另一行。
        await CloseDetail(force: true);
        var detail = new ContainerDetailViewModel(Shell, this, row);
        Detail = detail;
        MarkCurrent(row.Id);
        await detail.LoadAsync(Shell.Lifetime).ConfigureAwait(true);
    }

    /// <summary>关抽屉。钉住的抽屉只有 <paramref name="force" /> 才关得掉。</summary>
    private async Task CloseDetail(bool force = false)
    {
        if (Detail is { } detail && (force || !detail.Pinned))
        {
            await detail.DisposeAsync();
            Detail = null;
            MarkCurrent(null);
        }
    }

    /// <summary>
    /// 把"抽屉里开着的那一行"标出来。走 _all 而不是 View ——
    /// 抽屉钉住时用户可能已经切走了筛选,那一行不在可见集里也仍然是当前行。
    /// </summary>
    private void MarkCurrent(string? id)
    {
        foreach (var row in _all)
        {
            row.Current = row.Id == id;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CloseDetail(force: true);
        await ExitLogsModeAsync().ConfigureAwait(false);
        await _sampler.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 批量进行中的原地进度(选中条变成的那一条)。
/// <para>
/// 设计稿 17 号板要求的形态:同一条位置不动,内容从"已选 10 个容器 + 五个动作"
/// 变成"正在停止 10 个容器… 6/10 · 当前:worker-2 · 等待 SIGTERM" + 取消剩余。
/// 不弹窗、不遮列表、选中不丢。
/// </para>
/// </summary>
public sealed class BatchProgressState(string verb, int total, PanelTask task) : ObservableObject
{

    /// <summary>动作名。</summary>
    public string Verb { get; } = verb;

    /// <summary>目标总数。</summary>
    public int Total { get; } = total;

    /// <summary>已完成数。</summary>
    public int Done
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(CountText), nameof(Progress));
            }
        }
    }

    /// <summary>"6 / 10"。</summary>
    public string CountText => $"{Done} / {Total}";

    /// <summary>0–1。</summary>
    public double Progress => Total == 0 ? 0 : (double)Done / Total;

    /// <summary>标题。</summary>
    public string Title => $"正在{Verb} {Total} 个容器…";

    /// <summary>当前在等什么。</summary>
    public string WaitText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>取消剩下的。</summary>
    public RelayCommand CancelCommand => field ??= new(_ =>
    {
        task.Cancel();
        return Task.CompletedTask;
    });

    /// <summary>推进一格。</summary>
    public void Advance(int done, string current, string wait)
    {
        Done = done;
        WaitText = wait.Length > 0 ? $"当前:{wait}" : current;
    }
}

/// <summary>
/// 批量结束后的结果条。只在**有失败**时出现 —— 全成功的时候状态栏那一句就够了,
/// 再留一条要用户手动关掉的横幅是在收税。
/// </summary>
public sealed class BatchSummaryState(
    string verb,
    BatchResult result,
    IReadOnlyList<ContainerRow> failedRows,
    Func<DockerClient, string, CancellationToken, Task> action)
{
    /// <summary>动作名。</summary>
    public string Verb { get; } = verb;

    /// <summary>失败的那几行(重试用)。</summary>
    public IReadOnlyList<ContainerRow> FailedRows { get; } = failedRows;

    /// <summary>原来那个动作(重试用)。</summary>
    public Func<DockerClient, string, CancellationToken, Task> Action { get; } = action;

    /// <summary>标题。</summary>
    public string Title => $"已{Verb} {result.SucceededCount} 个,{result.FailedCount} 个失败";

    /// <summary>重试按钮上的文字。</summary>
    public string RetryText => $"重试失败的 {result.FailedCount} 个";

    /// <summary>失败的逐条明细。成功的那些不列 —— 它们不需要用户做任何事。</summary>
    public IReadOnlyList<BatchOutcome> Failures { get; } = [.. result.Failures];

    /// <summary>"+ N 个成功已折叠"。</summary>
    public string CollapsedText => result.SucceededCount > 0 ? $"+ {result.SucceededCount} 个成功已折叠" : "";

    /// <summary>有折叠起来的成功项。</summary>
    public bool HasCollapsed => result.SucceededCount > 0;
}

/// <summary>
/// 容器列表的列宽。
/// <para>
/// 六列全是定宽可拖的,富余的宽度交给「运行时长」与行尾动作之间的填充列。
/// 不留弹性列:弹性列的边界是**算**出来的、拖不动,
/// 而它往往正是最想拖的那一条(名称跟着窗口长到一千多像素,就是这么来的)。
/// </para>
/// <para>默认宽度取自设计稿 <c>C/ContainerRow</c>;镜像列比稿子宽一档,带 registry 前缀的名字本来就长。</para>
/// </summary>
public sealed class ContainerColumns : ListColumns
{

    /// <inheritdoc />
    public override IReadOnlyList<string> Keys { get; } = ["name", "image", "ports", "cpu", "mem", "uptime"];

    /// <summary>名称列。</summary>
    public GridLength Name
    {
        get;
        set => SetField(ref field, Clamp(value, "name"));
    } = new(240);

    /// <summary>镜像列。</summary>
    public GridLength Image
    {
        get;
        set => SetField(ref field, Clamp(value, "image"));
    } = new(260);

    /// <summary>端口列。</summary>
    public GridLength Ports
    {
        get;
        set => SetField(ref field, Clamp(value, "ports"));
    } = new(118);

    /// <summary>CPU 列。</summary>
    public GridLength Cpu
    {
        get;
        set => SetField(ref field, Clamp(value, "cpu"));
    } = new(108);

    /// <summary>内存列。</summary>
    public GridLength Mem
    {
        get;
        set => SetField(ref field, Clamp(value, "mem"));
    } = new(84);

    /// <summary>运行时长列。</summary>
    public GridLength Uptime
    {
        get;
        set => SetField(ref field, Clamp(value, "uptime"));
    } = new(78);

    /// <inheritdoc />
    public override double Get(string key) => key switch
    {
        "name" => Name.Value,
        "image" => Image.Value,
        "ports" => Ports.Value,
        "cpu" => Cpu.Value,
        "mem" => Mem.Value,
        _ => Uptime.Value
    };

    /// <inheritdoc />
    public override void Set(string key, double width)
    {
        GridLength value = new(width);
        switch (key)
        {
            case "name": Name = value; break;
            case "image": Image = value; break;
            case "ports": Ports = value; break;
            case "cpu": Cpu = value; break;
            case "mem": Mem = value; break;
            case "uptime": Uptime = value; break;
        }
    }

    /// <inheritdoc />
    public override double Min(string key) => key switch
    {
        "name" => 140,
        "image" => 120,
        "ports" => 70,
        "cpu" => 78,
        "mem" => 62,
        _ => 62
    };

    /// <inheritdoc />
    public override double MaxAutoFit(string key) => key is "name" or "image" ? 760 : 300;

    /// <inheritdoc />
    // 名称格里还坐着状态点与项目徽标;CPU 格里还坐着 sparkline。
    public override double Padding(string key) => key switch
    {
        "name" => 90,
        "cpu" => 74,
        _ => 18
    };
}
