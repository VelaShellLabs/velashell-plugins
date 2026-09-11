using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>详情抽屉的页签。</summary>
public enum DetailTab
{
    /// <summary>概览。</summary>
    Overview,

    /// <summary>日志。</summary>
    Logs,

    /// <summary>文件。</summary>
    Files,

    /// <summary>终端。</summary>
    Terminal,

    /// <summary>统计。</summary>
    Stats
}

/// <summary>概览页里的一条键值。</summary>
/// <param name="Key">键。</param>
/// <param name="Value">值。</param>
/// <param name="Tone">语气(决定值的颜色)。</param>
public readonly record struct DetailField(string Key, string Value, RowTone Tone = RowTone.Idle);

/// <summary>概览里的一条挂载。</summary>
/// <param name="Icon">图标资源键。</param>
/// <param name="Text">“源 → 目标”。</param>
/// <param name="Mode">只读 / 读写。</param>
/// <param name="ReadOnly">是不是只读。</param>
public readonly record struct MountLine(string Icon, string Text, string Mode, bool ReadOnly);

/// <summary>
/// 容器详情抽屉。
/// <para>
/// 抽屉在右侧而不是底部:详情与列表同屏对照,选中哪一行就看哪一行 ——
/// 底部抽屉会把列表挤成三行,而那正是用户要在里面找东西的地方。
/// </para>
/// </summary>
public sealed class ContainerDetailViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DockerPanelViewModel _shell;
    private CancellationTokenSource? _statsCts;
    private ContainerRow _row;
    private ContainerInspect? _inspect;

    /// <summary>建抽屉。</summary>
    public ContainerDetailViewModel(DockerPanelViewModel shell, ContainersPageViewModel page, ContainerRow row)
    {
        _shell = shell;
        Owner = page;
        _row = row;
        Logs = new(shell, row.Id, () => _inspect?.Config is not null && IsTty);
        Files = new(shell, row.Id, row.Name);
        Terminal = new(shell, row.Id, row.Name);

        SetTabCommand = new RelayCommand(p => p is DetailTab tab ? SetTabAsync(tab) : Task.CompletedTask);
        RefreshProcessesCommand = new RelayCommand(_ => RefreshProcessesAsync(_shell.Lifetime));
        CloseCommand = new RelayCommand(_ => page.CloseDetailCommand.Execute(null));
        CommitCommand = new RelayCommand(_ => CommitAsync());
        ToggleRawCommand = new RelayCommand(_ => ShowRaw = !ShowRaw);
        CopyIdCommand = new RelayCommand(_ => shell.Context.Clipboard.SetTextAsync(row.Id, shell.Lifetime));
        StartCommand = new RelayCommand(_ => page.StartCommand.Execute(_row));
        StopCommand = new RelayCommand(_ => page.StopCommand.Execute(_row));
        RestartCommand = new RelayCommand(_ => page.RestartCommand.Execute(_row));
        PauseCommand = new RelayCommand(_ => (_row.IsPaused ? page.UnpauseCommand : page.PauseCommand).Execute(_row));
        RemoveCommand = new RelayCommand(_ => page.RemoveCommand.Execute(_row));
        RenameCommand = new RelayCommand(_ => RenameAsync());
        RestartPolicyCommand = new RelayCommand(_ => ChangeRestartPolicyAsync());
    }

    /// <summary>容器 id。</summary>
    public string ContainerId => _row.Id;

    /// <summary>容器名。</summary>
    public string Name => _row.Name;

    /// <summary>短 id。</summary>
    public string ShortId => _row.ShortId;

    /// <summary>状态串。</summary>
    public string Status => _row.Status;

    /// <summary>状态色。</summary>
    public RowTone Tone => _row.Tone;

    /// <summary>compose 归属(“项目 / 服务”)。</summary>
    public string ComposePath => _row.Summary is { ComposeProject: { Length: > 0 } project } summary
        ? $"{project} / {summary.ComposeService ?? "—"}"
        : "";

    /// <summary>属不属于某个 compose 项目。</summary>
    public bool HasCompose => ComposePath.Length > 0;

    /// <summary>是否在跑。</summary>
    public bool IsRunning => _row.IsRunning;

    /// <summary>是否暂停。</summary>
    public bool IsPaused => _row.IsPaused;

    /// <summary>暂停按钮的文字(暂停 / 恢复)。</summary>
    public string PauseLabel => IsPaused ? "恢复" : "暂停";

    /// <summary>容器有没有分配 TTY —— 决定日志与 exec 的解帧方式。</summary>
    public bool IsTty { get => _inspect?.Config is not null && _row.Summary.State == "running" && field; private set; }

    /// <summary>当前页签。</summary>
    public DetailTab Tab
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsOverview), nameof(IsLogs), nameof(IsFiles), nameof(IsTerminal), nameof(IsStats));
            }
        }
    } = DetailTab.Overview;

    /// <summary>当前是概览页。</summary>
    public bool IsOverview => Tab == DetailTab.Overview;

    /// <summary>当前是日志页。</summary>
    public bool IsLogs => Tab == DetailTab.Logs;

    /// <summary>当前是文件页。</summary>
    public bool IsFiles => Tab == DetailTab.Files;

    /// <summary>当前是终端页。</summary>
    public bool IsTerminal => Tab == DetailTab.Terminal;

    /// <summary>当前是统计页。</summary>
    public bool IsStats => Tab == DetailTab.Stats;

    /// <summary>基本信息。</summary>
    public ObservableCollection<DetailField> Basics { get; } = [];

    /// <summary>端口映射。</summary>
    public ObservableCollection<DetailField> PortLines { get; } = [];

    /// <summary>挂载。</summary>
    public ObservableCollection<MountLine> Mounts { get; } = [];

    /// <summary>网络。</summary>
    public ObservableCollection<DetailField> Networks { get; } = [];

    /// <summary>环境变量。</summary>
    public ObservableCollection<DetailField> Environment { get; } = [];

    /// <summary>健康检查状态。</summary>
    public string HealthText { get; private set; } = "";

    /// <summary>有没有健康检查。</summary>
    public bool HasHealth => HealthText.Length > 0;

    /// <summary>inspect 的原文。</summary>
    public string RawInspect
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>概览里是不是在看原文。</summary>
    public bool ShowRaw
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>实时 CPU 百分比 0–100。</summary>
    public double CpuPercent
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>CPU 文本。</summary>
    public string CpuText
    {
        get;
        private set => SetField(ref field, value);
    } = "—";

    /// <summary>内存文本。</summary>
    public string MemText
    {
        get;
        private set => SetField(ref field, value);
    } = "—";

    /// <summary>内存上限文本。</summary>
    public string MemLimitText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>内存占比 0–1。</summary>
    public double MemRatio
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>
    /// 卡片右上角那行小字。CPU 给的是这段采样里的峰值 ——
    /// 当前值是 2%、峰值是 90% 的容器,和一直 2% 的容器是两回事,
    /// 而只看当下那一个数字,这两者长得一模一样。
    /// </summary>
    public string CpuPeakText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>内存卡片右上角:占上限的百分比。没有上限时为空。</summary>
    public string MemRatioText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>CPU 采样(抽屉里那条图)。</summary>
    public ObservableCollection<double> CpuHistory { get; } = [];

    /// <summary>内存采样。</summary>
    public ObservableCollection<double> MemHistory { get; } = [];

    /// <summary>容器里正在跑的进程(<c>docker top</c>)。</summary>
    public ObservableCollection<ProcessRow> Processes { get; } = [];

    /// <summary>进程表第三列的表头 —— 远端的 <c>ps</c> 给的是 TIME 还是 %CPU 由它决定。</summary>
    public string ProcessCpuTitle
    {
        get;
        private set => SetField(ref field, value);
    } = "CPU";

    /// <summary>进程表的一句话状态(空 = 有数据)。</summary>
    public string ProcessNote
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasProcessNote));
            }
        }
    } = "";

    /// <summary>有没有要说的(没在跑 / 读失败 / 还没读)。</summary>
    public bool HasProcessNote => ProcessNote.Length > 0;

    /// <summary>刷新进程表。</summary>
    public RelayCommand RefreshProcessesCommand { get; }

    /// <summary>关掉抽屉。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>
    /// 钉住抽屉:列表刷新、切页、点别的行都不关它。
    /// <para>
    /// 用得着的场合很具体 —— 一边看着这个容器的日志,一边在列表里找别的东西。
    /// </para>
    /// </summary>
    public bool Pinned
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>
    /// 抽屉最大化(占满整个页签)。
    /// <para>
    /// 状态存在**页面**上:抽屉的宽度与最大化是外层那张 Grid 的布局,
    /// 而换一个容器就是换一个抽屉视图模型 —— 存在这里会一点行就丢。
    /// </para>
    /// </summary>
    public bool Maximized
    {
        get => Owner.Drawer.Maximized;
        private set => Owner.Drawer.Maximized = value;
    }

    /// <summary>
    /// 抽屉所在的页面。
    /// <para>
    /// 界面要绑页面上的布局状态(最大化与否)。绕道 <see cref="Maximized" /> 不行 ——
    /// 通知是页面发的,而这个视图模型不会替它转发一遍。
    /// </para>
    /// </summary>
    public ContainersPageViewModel Owner { get; }

    /// <summary>切换钉住。</summary>
    public RelayCommand TogglePinCommand => field ??= new(_ =>
    {
        Pinned = !Pinned;
        return Task.CompletedTask;
    });

    /// <summary>切换最大化。</summary>
    public RelayCommand ToggleMaximizeCommand => field ??= new(_ =>
    {
        Maximized = !Maximized;
        return Task.CompletedTask;
    });

    /// <summary>复制等价的 <c>docker run</c> 命令(与右键菜单同一条路)。</summary>
    public RelayCommand CopyRunCommand => field ??= new(_ =>
    {
        Owner.RowCopyRunCommand.Execute(_row);
        return Task.CompletedTask;
    });

    /// <summary>强杀。</summary>
    public RelayCommand KillCommand => field ??= new(_ =>
    {
        Owner.KillCommand.Execute(_row);
        return Task.CompletedTask;
    });

    /// <summary>把当前可写层提交成一个镜像。</summary>
    public RelayCommand CommitCommand { get; }

    /// <summary>
    /// 提交为镜像。
    /// <para>
    /// 不走确认闸门:它<b>不删除也不覆盖任何东西</b>,只是多出一个镜像。
    /// 真正要提醒的是"这个镜像没有 Dockerfile",而那句话在表单的脚注里说更合适 ——
    /// 把闸门用在不会丢东西的操作上,只会让用户学会无视闸门。
    /// </para>
    /// </summary>
    private async Task CommitAsync()
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        var form = new CommitContainerForm(Name, _row.Image);
        if (!await _shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            var result = await client.CommitContainerAsync(ContainerId, form.Repository, form.Tag,
                form.Comment, author: null, form.Pause, _shell.Lifetime).ConfigureAwait(true);
            _shell.Feedback.Notify(FeedbackKind.Success, "已提交为镜像",
                $"{form.Repository}:{form.Tag} · {Humanize.ShortId(result.Id)}");
            await _shell.Images.RefreshAsync(_shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _shell.Feedback.ReportError("提交为镜像", ex);
        }
    }

    /// <summary>日志页。</summary>
    public LogsViewModel Logs { get; }

    /// <summary>文件页。</summary>
    public ContainerFilesViewModel Files { get; }

    /// <summary>终端页。</summary>
    public ContainerTerminalViewModel Terminal { get; }

    /// <summary>切页签。</summary>
    public RelayCommand SetTabCommand { get; }

    /// <summary>切换原文视图。</summary>
    public RelayCommand ToggleRawCommand { get; }

    /// <summary>复制容器 id。</summary>
    public RelayCommand CopyIdCommand { get; }

    /// <summary>启动。</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>停止。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>重启。</summary>
    public RelayCommand RestartCommand { get; }

    /// <summary>暂停 / 恢复。</summary>
    public RelayCommand PauseCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>重命名。</summary>
    public RelayCommand RenameCommand { get; }

    /// <summary>改重启策略。</summary>
    public RelayCommand RestartPolicyCommand { get; }

    /// <summary>加载详情。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        _inspect = await client.InspectContainerAsync(ContainerId, cancellationToken).ConfigureAwait(true);
        RawInspect = await client.InspectContainerRawAsync(ContainerId, cancellationToken).ConfigureAwait(true);
        IsTty = RawInspect.Contains("\"Tty\": true", StringComparison.Ordinal);
        BuildOverview();
        if (IsRunning)
        {
            StartStatsStream();
        }
    }

    /// <summary>切到某个页签(右键菜单与命令面板从外面调)。</summary>
    public Task GoToTabAsync(DetailTab tab) => SetTabAsync(tab);

    /// <summary>打开文件页并直接落到某个目录。</summary>
    public async Task OpenFilesAtAsync(string path)
    {
        Tab = DetailTab.Files;
        await Files.GoToAsync(path).ConfigureAwait(true);
    }

    /// <summary>列表刷新后同步这一行的新状态。</summary>
    public void ApplySummary(ContainerRow row)
    {
        _row = row;
        OnPropertiesChanged(nameof(Name), nameof(Status), nameof(Tone), nameof(IsRunning), nameof(IsPaused),
            nameof(PauseLabel), nameof(ComposePath), nameof(HasCompose));
        if (!IsRunning)
        {
            StopStatsStream();
            CpuText = "—";
            MemText = "—";
            CpuPercent = 0;
            MemRatio = 0;
        }
        else if (_statsCts is null)
        {
            StartStatsStream();
        }
    }

    private void BuildOverview()
    {
        Basics.Clear();
        PortLines.Clear();
        Mounts.Clear();
        Networks.Clear();
        Environment.Clear();
        if (_inspect is not { } inspect)
        {
            return;
        }
        Basics.Add(new("镜像", inspect.Config?.Image ?? _row.Image));
        Basics.Add(new("镜像摘要", Humanize.ShortId(inspect.Image)));
        Basics.Add(new("容器 ID", Humanize.ShortId(inspect.Id)));
        Basics.Add(new("创建于", Humanize.LocalTime(inspect.Created)));
        Basics.Add(new("启动于", Humanize.LocalTime(inspect.State?.StartedAt)));
        if (inspect.State is { Running: false, FinishedAt: { } finished })
        {
            Basics.Add(new("结束于", Humanize.LocalTime(finished),
                inspect.State.ExitCode == 0 ? RowTone.Idle : RowTone.Danger));
            Basics.Add(new("退出码", inspect.State.ExitCode.ToString(),
                inspect.State.ExitCode == 0 ? RowTone.Idle : RowTone.Danger));
        }
        Basics.Add(new("重启策略", inspect.HostConfig?.RestartPolicy?.Name is { Length: > 0 } policy ? policy : "no"));
        Basics.Add(new("重启次数", inspect.RestartCount.ToString()));
        Basics.Add(new("平台", inspect.Platform ?? "—"));
        if (inspect.Config?.Cmd is { Length: > 0 } cmd)
        {
            Basics.Add(new("命令", string.Join(' ', cmd)));
        }
        if (inspect.HostConfig?.Privileged == true)
        {
            Basics.Add(new("特权模式", "是 —— 容器可以做几乎任何宿主能做的事", RowTone.Danger));
        }

        foreach (var port in _row.Summary.Ports ?? [])
        {
            PortLines.Add(port.PublicPort > 0
                ? new($"{port.PrivatePort}/{port.Type ?? "tcp"}",
                    $"{(string.IsNullOrEmpty(port.IP) ? "0.0.0.0" : port.IP)}:{port.PublicPort} → {port.PrivatePort}",
                    RowTone.Ok)
                : new($"{port.PrivatePort}/{port.Type ?? "tcp"}", "仅容器内可见(未发布)"));
        }

        foreach (var mount in inspect.Mounts ?? [])
        {
            var source = mount.Type == "volume" ? mount.Name ?? mount.Source ?? "" : mount.Source ?? "";
            Mounts.Add(new(
                mount.Type == "volume" ? "Docker.database" : "Icon.folder",
                $"{source} → {mount.Destination}",
                mount.RW ? "读写" : "只读",
                !mount.RW));
        }

        foreach ((var name, var endpoint) in inspect.NetworkSettings?.Networks ?? [])
        {
            Networks.Add(new(name, endpoint.IPAddress is { Length: > 0 } ip ? ip : "(未分配)", RowTone.Ok));
        }

        foreach (var entry in inspect.Config?.Env ?? [])
        {
            var equals = entry.IndexOf('=');
            Environment.Add(equals > 0
                ? new(entry[..equals], entry[(equals + 1)..])
                : new(entry, ""));
        }

        HealthText = inspect.State?.Health is { Status: { Length: > 0 } status } health
            ? health.FailingStreak > 0 ? $"{status} · 连续失败 {health.FailingStreak} 次" : status
            : "";
        OnPropertiesChanged(nameof(HealthText), nameof(HasHealth), nameof(IsTty));
    }

    private async Task SetTabAsync(DetailTab tab)
    {
        Tab = tab;
        // 文件页是设计稿里的整屏三栏(文件树 / 编辑器 / 属性),440px 的抽屉装不下。
        // 但"直接铺满整个页签"太狠:列表整个没了,回去的路只剩头上那颗还原键。
        // 撑到够摆开三栏就停,列表还在旁边,手柄也还在,用户随时能往回拖。
        if (tab is DetailTab.Files)
        {
            Owner.Drawer.EnsureAtLeast(820);
        }
        switch (tab)
        {
            case DetailTab.Logs:
                await Logs.EnsureStartedAsync().ConfigureAwait(true);
                break;
            case DetailTab.Files:
                await Files.EnsureLoadedAsync().ConfigureAwait(true);
                break;
            case DetailTab.Terminal:
                await Terminal.EnsureStartedAsync().ConfigureAwait(true);
                break;
            case DetailTab.Stats:
                await RefreshProcessesAsync(_shell.Lifetime).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>
    /// 读一次容器里的进程表。
    /// <para>
    /// 不做轮询:这张表的价值是"这一刻里面到底跑着什么",而不是看它跳动 ——
    /// 后者是 <c>top</c> 的活,该在终端页里干。
    /// </para>
    /// </summary>
    private async Task RefreshProcessesAsync(CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            Processes.Clear();
            ProcessNote = "容器没在跑,没有进程可看。";
            return;
        }
        if (_shell.Client is not { } client)
        {
            return;
        }
        try
        {
            var top = await client.TopAsync(ContainerId, cancellationToken: cancellationToken)
                                                 .ConfigureAwait(true);
            Processes.Clear();
            foreach (var row in ProcessTable.Normalize(top))
            {
                Processes.Add(row);
            }
            ProcessCpuTitle = ProcessTable.CpuColumnTitle(top);
            ProcessNote = Processes.Count == 0 ? "这一刻没有读到进程。" : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Processes.Clear();
            // 用的是精简镜像(distroless、scratch)时容器里没有 ps,daemon 会直接报错 ——
            // 这不是面板的故障,得说清楚。
            ProcessNote = $"读不到进程表:{ex.Message}";
        }
    }

    /// <summary>
    /// 抽屉里那一个容器才配拥有一条真正的 <c>stats</c> 流 ——
    /// 列表走的是低频快照(见 <see cref="StatsSampler" />)。
    /// </summary>
    private void StartStatsStream()
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        StopStatsStream();
        _statsCts = CancellationTokenSource.CreateLinkedTokenSource(_shell.Lifetime);
        var token = _statsCts.Token;
        var id = ContainerId;
        _ = Task.Run(async () =>
        {
            try
            {
                await client.StreamStatsAsync(id, sample => Ui.Post(() => ApplyStats(sample)), token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 容器停了、面板关了都会走到这里,都不是要报给用户的错误。
            }
        }, token);
    }

    private void StopStatsStream()
    {
        _statsCts?.Cancel();
        _statsCts?.Dispose();
        _statsCts = null;
    }

    private void ApplyStats(ContainerStats stats)
    {
        CpuPercent = stats.CpuPercent;
        CpuText = Humanize.Percent(stats.CpuPercent);
        MemText = Humanize.Bytes(stats.MemoryUsed);
        MemLimitText = stats.MemoryLimit > 0 ? $"/ {Humanize.Bytes(stats.MemoryLimit)}" : "";
        MemRatio = stats.MemoryLimit > 0 ? Math.Clamp((double)stats.MemoryUsed / stats.MemoryLimit, 0, 1) : 0;
        MemRatioText = stats.MemoryLimit > 0 ? Humanize.Percent(MemRatio * 100) : "";
        Append(CpuHistory, stats.CpuPercent);
        Append(MemHistory, MemRatio * 100);
        CpuPeakText = CpuHistory.Count > 0 ? $"峰值 {Humanize.Percent(CpuHistory.Max())}" : "";
    }

    private static void Append(ObservableCollection<double> series, double value)
    {
        series.Add(value);
        while (series.Count > 40)
        {
            series.RemoveAt(0);
        }
    }

    private async Task RenameAsync()
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        var form = new RenameContainerForm(Name, _row.Project);
        if (!await _shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await client.RenameContainerAsync(ContainerId, form.NewName.Trim(), _shell.Lifetime).ConfigureAwait(true);
            _shell.Feedback.Status(FeedbackKind.Success, $"已重命名为 {form.NewName.Trim()}");
            await Owner.RefreshAsync(_shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _shell.Feedback.ReportError("重命名", ex);
        }
    }

    private async Task ChangeRestartPolicyAsync()
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        var form = new RestartPolicyForm(_inspect?.HostConfig?.RestartPolicy?.Name ?? "no",
            _inspect?.HostConfig?.RestartPolicy?.MaximumRetryCount ?? 5);
        if (!await _shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await client.UpdateRestartPolicyAsync(ContainerId, form.Policy, form.MaxRetries, _shell.Lifetime)
                        .ConfigureAwait(true);
            _shell.Feedback.Status(FeedbackKind.Success, $"重启策略已改为 {form.Policy}");
            await LoadAsync(_shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _shell.Feedback.ReportError("修改重启策略", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        StopStatsStream();
        await Logs.DisposeAsync().ConfigureAwait(false);
        await Terminal.DisposeAsync().ConfigureAwait(false);
    }
}
