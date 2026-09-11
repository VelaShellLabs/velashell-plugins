using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui.Pages;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>面板与 daemon 的连接状态。</summary>
public enum PanelConnectionState
{
    /// <summary>还没选端点。</summary>
    NoEndpoint,

    /// <summary>正在建立通道。</summary>
    Connecting,

    /// <summary>连上了。</summary>
    Ready,

    /// <summary>连不上。</summary>
    Failed
}

/// <summary>主机切换器里的一项。</summary>
public sealed class EndpointItem(DockerEndpoint endpoint, bool available, string stateText, FeedbackKind stateKind)
    : ObservableObject
{
    /// <summary>端点。</summary>
    public DockerEndpoint Endpoint { get; } = endpoint;

    /// <summary>显示名。</summary>
    public string DisplayName => Endpoint.DisplayName;

    /// <summary>小字(user@host / 管道路径)。</summary>
    public string Detail => Endpoint.Detail;

    /// <summary>是不是本机。</summary>
    public bool IsLocal => Endpoint.Kind == DockerEndpointKind.Local;

    /// <summary>能不能选(会话断了、找不到 socket 的项置灰)。</summary>
    public bool Available { get; private set; } = available;

    /// <summary>右侧的状态短语。</summary>
    public string StateText { get; private set; } = stateText;

    /// <summary>状态短语的语气。</summary>
    public FeedbackKind StateKind { get; private set; } = stateKind;

    /// <summary>图标资源键。</summary>
    public string Icon => IsLocal ? "Docker.monitor" : "Docker.server";

    /// <summary>更新状态。</summary>
    public void Update(bool available, string stateText, FeedbackKind stateKind)
    {
        Available = available;
        StateText = stateText;
        StateKind = stateKind;
        OnPropertiesChanged(nameof(Available), nameof(StateText), nameof(StateKind));
    }
}

/// <summary>连不上时给用户的一条出路。</summary>
/// <param name="Label">按钮文字。</param>
/// <param name="Icon">图标资源键。</param>
/// <param name="Primary">是不是主要按钮。</param>
/// <param name="Invoke">回调。</param>
public sealed record RecoveryAction(string Label, string Icon, bool Primary, Action Invoke);

/// <summary>设置里列出的一个常见 socket 位置。</summary>
/// <param name="Path">路径。</param>
/// <param name="Source">哪一种安装方式会摆在这儿。</param>
public sealed record SocketHint(string Path, string Source);

/// <summary>
/// 面板外壳的视图模型:端点、导航、连接生命周期与事件驱动刷新。
/// <para>
/// 页面自己不拉数据也不挂定时器 —— 刷新的时机全部由这里按 <c>docker events</c> 决定,
/// 一条事件流喂饱所有页面。
/// </para>
/// </summary>
public sealed partial class DockerPanelViewModel : ObservableObject, IAsyncDisposable
{
    private int _countContainers;
    private int _countImages;
    private int _countVolumes;
    private readonly CancellationTokenSource _lifetime = new();

    // 落地页是总览:刚连上时,用户第一件想知道的是"这台机器现在怎么样",
    // 而不是"这里有哪些容器"—— 后者是他确认前者之后才要往下走的一步。
    private PageViewModel? _activePage;

    /// <summary>建外壳。</summary>
    public DockerPanelViewModel(IPluginContext context)
    {
        Context = context;
        Settings = new(context.Storage);
        Confirm = new();
        Tasks = new();
        Feedback = new();
        Overview = new OverviewPageViewModel(this);
        Containers = new ContainersPageViewModel(this);
        Images = new ImagesPageViewModel(this);
        Volumes = new VolumesPageViewModel(this);
        Networks = new NetworksPageViewModel(this);
        ComposePage = new ComposePageViewModel(this);
        SystemPage = new SystemPageViewModel(this);
        AllPages = [Overview, Containers, Images, Volumes, Networks, ComposePage, SystemPage];
        _activePage = Overview;

        SelectPageCommand = new RelayCommand(p =>
        {
            if (p is PanelPage page)
            {
                _ = GoToAsync(page);
            }
        });
        RefreshCommand = new RelayCommand(_ => RefreshActiveAsync(), _ => IsReady);
        ReconnectCommand = new RelayCommand(_ => ConnectAsync(SelectedEndpoint));
        SelectEndpointCommand = new RelayCommand(p =>
        {
            EndpointMenuOpen = false;
            return p is EndpointItem item && item.Available ? ConnectAsync(item) : Task.CompletedTask;
        });
        ToggleEndpointMenuCommand = new RelayCommand(_ => EndpointMenuOpen = !EndpointMenuOpen);
        ToggleTaskCenterCommand = new RelayCommand(_ => TaskCenterOpen = !TaskCenterOpen);
        ToggleSettingsCommand = new RelayCommand(_ => SettingsOpen = !SettingsOpen);
        Palette = new(CollectPaletteEntries);
        OpenPaletteCommand = new RelayCommand(_ =>
        {
            Palette.Open(SelectedEndpoint?.DisplayName ?? "(未选择)");
            return Task.CompletedTask;
        });
        SetLogTailCommand = new RelayCommand(p =>
        {
            if (p is string tail)
            {
                Settings.LogTail = tail;
                Settings.NotifyLogTailSegments();
            }
        });
        ApplySocketPathCommand = new RelayCommand(_ => ApplySocketPathAsync());
        // 一条命令把四个常见位置和 docker 自己的说法一次问清,省得用户挨个试。
        // 用 -S 而不是 -e:那个位置上摆着一个同名的普通文件,比不存在更难查。
        //
        // 光回答"有没有"是不够的。最常见的一种失败是:socket 在、daemon 也在跑,
        // 但当前账号不在它的属组里 —— 那要用户对着 `ls -l` 和 `id` 两串输出自己交叉比对。
        // 所以这里直接用 -r/-w 替他试一次,并把属组和他自己所在的组打在同一行上。
        DiscoverSocketCommand = new RelayCommand(_ => SendToHostTerminalAsync(
            "for s in /var/run/docker.sock \"$XDG_RUNTIME_DIR/docker.sock\" " +
            "\"$HOME/.docker/run/docker.sock\" \"$HOME/.colima/default/docker.sock\"; do " +
            "[ -S \"$s\" ] || continue; " +
            "if [ -r \"$s\" ] && [ -w \"$s\" ]; then echo \"可用 $s\"; " +
            "else echo \"有 $s,但当前账号读写不了:它属组 $(stat -c %G \"$s\" 2>/dev/null)," +
            "而你在 $(id -nG)\"; fi; done; " +
            "docker context inspect --format '{{.Endpoints.docker.Host}}' 2>/dev/null; " +
            "echo \"DOCKER_HOST=$DOCKER_HOST\""));
        ResetSocketPathCommand = new RelayCommand(_ =>
        {
            SocketPathInput = SocketPathDefault;
            return ApplySocketPathAsync();
        });
        ClearFinishedTasksCommand = new RelayCommand(_ => Tasks.ClearFinished());

        RelayCommand.UnhandledCommandError += ex => Feedback.ReportError("操作", ex);
        Settings.Changed += () =>
        {
            _ = Settings.SaveAsync(_lifetime.Token);
            // 关掉"实时统计"要立刻停下采样,而不是等下次切页 ——
            // 那个开关的整个卖点就是省远端开销。
            if (IsReady)
            {
                Containers.StartSampling();
            }
        };
    }

    /// <summary>宿主上下文。</summary>
    public IPluginContext Context { get; }

    /// <summary>面板设置。</summary>
    public PanelSettings Settings { get; }

    /// <summary>确认闸门。</summary>
    public ConfirmGate Confirm { get; }

    /// <summary>任务中心。</summary>
    public TaskCenter Tasks { get; }

    /// <summary>结果反馈(状态栏 + toast)。</summary>
    public Feedback Feedback { get; }

    /// <summary>当前端点的客户端;没连上时为 <see langword="null" />。</summary>
    public DockerClient? Client { get; private set; }

    /// <summary>Compose 通道;没连上时为 <see langword="null" />。</summary>
    public ComposeCli? Compose { get; private set; }

    /// <summary>仓库凭据。</summary>
    public RegistryAuthProvider? RegistryAuth { get; private set; }

    /// <summary>面板生命周期令牌。</summary>
    public CancellationToken Lifetime => _lifetime.Token;

    // ── 端点 ──────────────────────────────────────────────────────

    /// <summary>可选的端点。</summary>
    public ObservableCollection<EndpointItem> Endpoints { get; } = [];

    /// <summary>当前端点。</summary>
    public EndpointItem? SelectedEndpoint
    {
        get; private set
        {
            if (SetField(ref field, value))
            {
                SocketPathInput = value?.Endpoint.SocketPath ?? "";
                OnPropertiesChanged(nameof(EndpointName), nameof(EndpointDetail), nameof(HasEndpoint),
                    nameof(ComposeAvailable), nameof(SocketPathDefault), nameof(SocketPathChanged));
            }
        }
    }

    /// <summary>顶栏显示的主机名。</summary>
    public string EndpointName => SelectedEndpoint?.DisplayName ?? "选择目标";

    /// <summary>顶栏主机名后面那行小字。</summary>
    public string EndpointDetail => SelectedEndpoint is { } item
        ? $"{item.Endpoint.SocketPath} · {(item.IsLocal ? "本机" : "SSH 隧道")}"
        : "";

    /// <summary>选了端点没有。</summary>
    public bool HasEndpoint => SelectedEndpoint is not null;

    /// <summary>
    /// 设置抽屉里那个 socket 路径输入框。
    /// <para>
    /// 存在的理由:"这台机器上找不到 docker.sock"的补救动作就是换一条路径
    /// (rootless Docker 在 <c>$XDG_RUNTIME_DIR/docker.sock</c>,Colima、OrbStack 各有各的位置)。
    /// 补救按钮把设置抽屉打开却没有这个框,等于把用户领到一堵墙前面。
    /// </para>
    /// </summary>
    public string SocketPathInput
    {
        get; set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(SocketPathChanged));
            }
        }
    } = "";

    /// <summary>当前端点默认的 socket 路径(用来判断"是不是改过了")。</summary>
    public string SocketPathDefault =>
        SelectedEndpoint?.Endpoint.Kind == DockerEndpointKind.Local && OperatingSystem.IsWindows()
            ? DockerEndpoint.DefaultWindowsPipe
            : DockerEndpoint.DefaultUnixSocket;

    /// <summary>改过 socket 路径没有(决定"恢复默认"要不要亮)。</summary>
    public bool SocketPathChanged =>
        SocketPathInput.Trim().Length > 0 && SocketPathInput.Trim() != SocketPathDefault;

    /// <summary>主机切换器是否展开。</summary>
    public bool EndpointMenuOpen { get; set => SetField(ref field, value); }

    /// <summary>
    /// Compose 页可不可用。
    /// <para>
    /// 只取决于**有没有连上** —— 远端与本机都有各自的执行通道
    /// (见 <see cref="IComposeHost" />)。至于那台机器上到底装没装 <c>docker compose</c>,
    /// 是 Compose 页自己 <c>compose version</c> 探出来的,不该在这里猜。
    /// </para>
    /// </summary>
    public bool ComposeAvailable => Compose is not null;

    // ── 连接状态 ──────────────────────────────────────────────────

    /// <summary>连接状态。</summary>
    public PanelConnectionState State
    {
        get; private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsConnecting), nameof(IsReady), nameof(IsFailed), nameof(NeedsEndpoint),
                    nameof(EventsDegraded));
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    } = PanelConnectionState.NoEndpoint;

    /// <summary>还没选端点。</summary>
    public bool NeedsEndpoint => State == PanelConnectionState.NoEndpoint;

    /// <summary>正在建立通道。</summary>
    public bool IsConnecting => State == PanelConnectionState.Connecting;

    /// <summary>连上了。</summary>
    public bool IsReady => State == PanelConnectionState.Ready;

    /// <summary>连不上。</summary>
    public bool IsFailed => State == PanelConnectionState.Failed;

    /// <summary>连不上时的标题。</summary>
    public string ErrorTitle { get; private set => SetField(ref field, value); } = "";

    /// <summary>连不上时的正文。</summary>
    public string ErrorDetail { get; private set => SetField(ref field, value); } = "";

    /// <summary>连不上时下面那行等宽小字(daemon 的原话)。</summary>
    public string ErrorHint { get; private set => SetField(ref field, value); } = "";

    /// <summary>连不上时的图标。</summary>
    public string ErrorIcon { get; private set => SetField(ref field, value); } = "Icon.circle-alert";

    /// <summary>连不上时给的出路。</summary>
    public ObservableCollection<RecoveryAction> RecoveryActions { get; } = [];

    // ── 导航 ──────────────────────────────────────────────────────

    /// <summary>总览页。</summary>
    public OverviewPageViewModel Overview { get; }

    /// <summary>容器页。</summary>
    public ContainersPageViewModel Containers { get; }

    /// <summary>镜像页。</summary>
    public ImagesPageViewModel Images { get; }

    /// <summary>卷页。</summary>
    public VolumesPageViewModel Volumes { get; }

    /// <summary>网络页。</summary>
    public NetworksPageViewModel Networks { get; }

    /// <summary>Compose 页。</summary>
    public ComposePageViewModel ComposePage { get; }

    /// <summary>系统页。</summary>
    public SystemPageViewModel SystemPage { get; }

    /// <summary>全部页面。</summary>
    public IReadOnlyList<PageViewModel> AllPages { get; }

    /// <summary>当前页标识(左导航栏据此选中)。</summary>
    public PanelPage CurrentPage
    {
        get; private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsOverview), nameof(IsContainers), nameof(IsImages),
                    nameof(IsVolumes), nameof(IsNetworks), nameof(IsCompose), nameof(IsSystem));
            }
        }
    } = PanelPage.Overview;

    /// <summary>当前页的视图模型。</summary>
    public PageViewModel? ActivePage
    {
        get => _activePage;
        private set => SetField(ref _activePage, value);
    }

    /// <summary>当前是总览页。</summary>
    public bool IsOverview => CurrentPage == PanelPage.Overview;

    /// <summary>当前是容器页。</summary>
    public bool IsContainers => CurrentPage == PanelPage.Containers;

    /// <summary>当前是镜像页。</summary>
    public bool IsImages => CurrentPage == PanelPage.Images;

    /// <summary>当前是卷页。</summary>
    public bool IsVolumes => CurrentPage == PanelPage.Volumes;

    /// <summary>当前是网络页。</summary>
    public bool IsNetworks => CurrentPage == PanelPage.Networks;

    /// <summary>当前是 Compose 页。</summary>
    public bool IsCompose => CurrentPage == PanelPage.Compose;

    /// <summary>当前是系统页。</summary>
    public bool IsSystem => CurrentPage == PanelPage.System;

    // ── 状态栏 ────────────────────────────────────────────────────

    /// <summary>daemon 版本。</summary>
    public string EngineVersion { get; private set => SetField(ref field, value); } = "";

    /// <summary>API 版本。</summary>
    public string ApiVersion { get; private set => SetField(ref field, value); } = "";

    /// <summary>“18 容器 · 34 镜像 · 9 卷”。</summary>
    public string CountsText { get; private set => SetField(ref field, value); } = "";

    /// <summary>任务中心弹层是否展开。</summary>
    public bool TaskCenterOpen { get; set => SetField(ref field, value); }

    /// <summary>设置抽屉是否展开。</summary>
    public bool SettingsOpen { get; set => SetField(ref field, value); }

    // ── 命令 ──────────────────────────────────────────────────────

    /// <summary>切页。</summary>
    public RelayCommand SelectPageCommand { get; }

    /// <summary>手动刷新当前页。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>重连。</summary>
    public RelayCommand ReconnectCommand { get; }

    /// <summary>选一个端点。</summary>
    public RelayCommand SelectEndpointCommand { get; }

    /// <summary>展开/收起主机切换器。</summary>
    public RelayCommand ToggleEndpointMenuCommand { get; }

    /// <summary>展开/收起任务中心。</summary>
    public RelayCommand ToggleTaskCenterCommand { get; }

    /// <summary>展开/收起设置。</summary>
    public RelayCommand ToggleSettingsCommand { get; }

    /// <summary>面板内的命令面板(<c>Ctrl+K</c>)。</summary>
    public CommandPalette Palette { get; }

    /// <summary>打开命令面板。</summary>
    public RelayCommand OpenPaletteCommand { get; }

    /// <summary>
    /// 收集当前能做的事。
    /// <para>
    /// 每次打开都重新收集,不缓存 —— 容器列表随时在变,一份缓存下来的命令列表
    /// 会让用户对着一个已经不存在的容器按回车。
    /// </para>
    /// </summary>
    private List<PaletteEntry> CollectPaletteEntries()
    {
        List<PaletteEntry> entries = [];
        if (!IsReady)
        {
            return entries;
        }

        // ── 动作:对具体容器的高频操作。放最前面,因为它们是"要做一件事"而不是"要看一眼"。
        foreach (var row in Containers.View.Where(r => r.IsRunning).Take(20))
        {
            var target = row;
            entries.Add(new("动作", $"重启 {target.Name}", DescribeContainer(target), "Docker.rotate-cw",
                RowTone.Ok, false, () => { Containers.RestartCommand.Execute(target); return Task.CompletedTask; }));
            entries.Add(new("动作", $"停止 {target.Name}", DescribeContainer(target), "Icon.square",
                RowTone.Idle, false, () => { Containers.StopCommand.Execute(target); return Task.CompletedTask; }));
        }

        // ── 容器 / 镜像 / 卷:导航到某个对象。
        foreach (var row in Containers.View.Take(40))
        {
            var target = row;
            entries.Add(new("容器", target.Name, DescribeContainer(target), "Docker.box",
                target.Tone, false, () => Containers.OpenDetailCommand is { } open
                    ? Task.Run(() => Ui.Post(() => open.Execute(target)))
                    : Task.CompletedTask));
        }
        foreach (var row in Images.View.Take(40))
        {
            var target = row;
            entries.Add(new("镜像 / 卷", $"{target.Repository}:{target.Tag}", $"镜像 · {target.SizeText}",
                "Icon.layers", RowTone.Idle, false,
                () => { Images.OpenDetailCommand.Execute(target); return Task.CompletedTask; }));
        }
        foreach (var row in Volumes.View.Take(40))
        {
            var target = row;
            entries.Add(new("镜像 / 卷", target.Name, $"卷 · {target.SizeText}", "Icon.hard-drive",
                RowTone.Idle, false, () => { Volumes.SelectCommand.Execute(target); return Task.CompletedTask; }));
        }

        // ── 面板命令:导航与全局动作。破坏性的带省略号,选中后仍走闸门。
        entries.Add(new("面板命令", "拉取镜像…", "从仓库拉一个镜像", "Docker.arrow-down-to-line",
            RowTone.Idle, false, () => ShowPullDialogAsync(null)));
        entries.Add(new("面板命令", "清理未使用的镜像…", "释放磁盘,重新拉要花时间与带宽", "Docker.broom",
            RowTone.Warn, true, () => { Images.PruneAllCommand.Execute(null); return Task.CompletedTask; }));
        entries.Add(new("面板命令", "清理悬空镜像…", "只删没有标签也没人用的中间层", "Docker.broom",
            RowTone.Idle, true, () => { Images.PruneDanglingCommand.Execute(null); return Task.CompletedTask; }));
        entries.Add(new("面板命令", "打开设置", "连接、显示、行为", "Icon.settings",
            RowTone.Idle, false, () => { SettingsOpen = true; return Task.CompletedTask; }));
        foreach ((var page, var title, var icon) in ((PanelPage, string, string)[])
                 [
                     (PanelPage.Overview, "总览", "Docker.layout-dashboard"),
                     (PanelPage.Containers, "容器", "Docker.box"),
                     (PanelPage.Images, "镜像", "Icon.layers"),
                     (PanelPage.Volumes, "卷", "Icon.hard-drive"),
                     (PanelPage.Networks, "网络", "Icon.network"),
                     (PanelPage.System, "系统", "Icon.gauge")
                 ])
        {
            var target = page;
            entries.Add(new("面板命令", $"转到{title}", "切换页面", icon, RowTone.Idle, false,
                () => GoToAsync(target)));
        }
        return entries;
    }

    private static string DescribeContainer(ContainerRow row) =>
        row.HasProject ? $"{(row.IsRunning ? "运行中" : row.Uptime)} · {row.Project}"
            : $"{(row.IsRunning ? "运行中" : row.Uptime)} · {row.Image}";

    /// <summary>设置默认补多少行历史。</summary>
    public RelayCommand SetLogTailCommand { get; }

    /// <summary>用输入框里的路径重连。</summary>
    public RelayCommand ApplySocketPathCommand { get; }

    /// <summary>恢复默认 socket 路径并重连。</summary>
    public RelayCommand ResetSocketPathCommand { get; }

    /// <summary>
    /// 常见的 socket 位置。摆在设置里而不是写进文档:
    /// 用户是在"连不上"的当口来找它的,那一刻他不会去翻文档。
    /// </summary>
    public IReadOnlyList<SocketHint> SocketHints { get; } =
    [
        new("/var/run/docker.sock", "标准安装"),
        new("$XDG_RUNTIME_DIR/docker.sock", "rootless"),
        new("~/.docker/run/docker.sock", "Docker Desktop"),
        new("~/.colima/default/docker.sock", "Colima")
    ];

    /// <summary>
    /// 把"这台机器的 socket 到底在哪"这条命令送到宿主终端里去问。
    /// <para>
    /// 面板不替用户猜:rootless、Colima、OrbStack、Docker Desktop 各摆各的位置,
    /// 而**远端自己**一句话就能答上来。本机端点没有终端可送,那就把命令原样告诉他。
    /// </para>
    /// </summary>
    public RelayCommand DiscoverSocketCommand { get; }

    /// <summary>清掉已完成的任务。</summary>
    public RelayCommand ClearFinishedTasksCommand { get; }

    // ── 生命周期 ──────────────────────────────────────────────────

    /// <summary>面板打开时调用:读设置、列端点、自动连上唯一那个。</summary>
    public async Task InitializeAsync()
    {
        await Settings.LoadAsync(_lifetime.Token).ConfigureAwait(true);
        Context.Events.SessionConnected += OnSessionChanged;
        Context.Events.SessionDisconnected += OnSessionChanged;
        await ReloadEndpointsAsync().ConfigureAwait(true);
        // 只有一个可用端点时直接连上 —— 让用户为一个没得选的选择再点一次没有意义。
        EndpointItem[] usable = [.. Endpoints.Where(e => e.Available)];
        if (usable.Length == 1)
        {
            await ConnectAsync(usable[0]).ConfigureAwait(true);
        }
    }

    /// <summary>重新列一遍端点(会话连上/断开时也会走这里)。</summary>
    public async Task ReloadEndpointsAsync()
    {
        IReadOnlyList<SessionInfo> sessions;
        try
        {
            sessions = await Context.Sessions.ListAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sessions = [];
        }
        List<EndpointItem> items =
        [
            new(DockerEndpoint.Local("本机 Docker"), true, "可用", FeedbackKind.Info)
        ];
        foreach (var session in sessions.OrderBy(s => s.Host, StringComparer.OrdinalIgnoreCase))
        {
            var connected = session.State == SessionState.Connected;
            items.Add(new(
                DockerEndpoint.Remote(session.SessionId, session.Host, $"{session.Username}@{session.Host}:{session.Port}"),
                connected,
                connected ? "已连接" : "未连接",
                connected ? FeedbackKind.Success : FeedbackKind.Info));
        }
        Ui.Post(() =>
        {
            Endpoints.Clear();
            foreach (var item in items)
            {
                Endpoints.Add(item);
            }
            if (State == PanelConnectionState.NoEndpoint)
            {
                SetNoEndpoint(sessions.Count(s => s.State == SessionState.Connected));
            }
        });
    }

    private void OnSessionChanged(SessionInfo session) => _ = ReloadEndpointsAsync();

    /// <summary>
    /// 连到一个端点:建客户端 → 探一次 <c>/version</c> → 起事件流 → 刷新当前页。
    /// </summary>
    public async Task ConnectAsync(EndpointItem? item)
    {
        if (item is null)
        {
            return;
        }
        await StopEventStreamAsync().ConfigureAwait(true);
        if (Client is not null)
        {
            await Client.DisposeAsync().ConfigureAwait(true);
            Client = null;
        }
        // 通道跟着连接一起作废 —— 留着旧的,导航栏会显示 Compose 可点,
        // 点进去却是上一台机器的项目。
        Compose = null;
        OnPropertyChanged(nameof(ComposeAvailable));
        foreach (var page in AllPages)
        {
            page.Reset();
        }
        SelectedEndpoint = item;
        State = PanelConnectionState.Connecting;
        RecoveryActions.Clear();

        var endpoint = item.Endpoint;
        var remembered = await Settings.GetSocketPathAsync(endpoint.DisplayName, _lifetime.Token).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(remembered) && remembered != endpoint.SocketPath)
        {
            endpoint = endpoint with { SocketPath = remembered };
        }
        // 输入框显示的是**实际用的**那条路径,而不是端点的出厂默认值。
        SocketPathInput = endpoint.SocketPath;
        IDockerTransport transport = endpoint.Kind == DockerEndpointKind.Local
            ? new LocalTransport(endpoint.SocketPath)
            : new TunnelTransport(Context.RemoteTunnel, endpoint.SessionId, endpoint.SocketPath);
        var client = new DockerClient(endpoint, transport);
        try
        {
            var version = await client.PingAsync(_lifetime.Token).ConfigureAwait(true);
            Client = client;
            RegistryAuth = new(Context.RemoteFs, endpoint);
            // compose 只有 CLI,所以要一条"跑命令"的通道:远端是 SSH,本机是本地进程。
            Compose = new ComposeCli(endpoint.Kind == DockerEndpointKind.Remote
                ? new RemoteComposeHost(Context.RemoteExec, Context.RemoteFs, endpoint.SessionId)
                : new LocalComposeHost());
            OnPropertyChanged(nameof(ComposeAvailable));
            EngineVersion = version.Version is { Length: > 0 } v ? $"Engine {v}" : "";
            ApiVersion = version.ApiVersion is { Length: > 0 } a ? $"API v{a}" : "";
            State = PanelConnectionState.Ready;
            item.Update(true, "通道已建立", FeedbackKind.Success);
            Feedback.Status(FeedbackKind.Success, $"已连上 {endpoint.DisplayName} 的 Docker");
            StartEventStream();
            // 容器列表 + 统计采样在后台先跑起来:总览页那几张卡靠它喂,
            // 而用户可能整段时间都不会点开容器页。
            _ = Containers.PrimeAsync(_lifetime.Token);
            await RefreshActiveAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await client.DisposeAsync().ConfigureAwait(true);
            SetFailed(ex, item);
        }
    }

    /// <summary>
    /// 记住这台主机的 socket 路径并重连。
    /// <para>
    /// 路径按<b>主机</b>存,不按会话 id —— 会话 id 每次重连都换,
    /// 而"这台机器的 docker 在哪儿"是这台机器的属性,不该跟着会话一起过期。
    /// </para>
    /// </summary>
    private async Task ApplySocketPathAsync()
    {
        if (SelectedEndpoint is not { } item)
        {
            return;
        }
        var path = SocketPathInput.Trim();
        if (path.Length == 0)
        {
            Feedback.Status(FeedbackKind.Warning, "socket 路径不能为空。");
            return;
        }
        await Settings.SetSocketPathAsync(item.DisplayName, path, _lifetime.Token).ConfigureAwait(true);
        SettingsOpen = false;
        OnPropertyChanged(nameof(SocketPathChanged));
        await ConnectAsync(item).ConfigureAwait(true);
    }

    /// <summary>
    /// 把一条排查命令送进宿主的终端(如同用户键入,需要授权)。
    /// <para>
    /// 送过去而不是面板自己跑:排查这类命令的输出常常要人读、要接着改着再跑一遍,
    /// 那是终端的主场;而且它经宿主的授权闸,面板不越过那道门。
    /// </para>
    /// </summary>
    /// <summary>
    /// 把一条命令送到宿主终端里去(补救动作用:登录 registry、查 socket、看 daemon 日志)。
    /// <para>本机端点没有终端可送 —— 那就把命令原样告诉用户,让他自己敲。</para>
    /// </summary>
    public async Task SendToHostTerminalAsync(string command)
    {
        if (SelectedEndpoint?.Endpoint is not { Kind: DockerEndpointKind.Remote } endpoint)
        {
            Feedback.Notify(FeedbackKind.Info, "本机端点没有终端可送", $"自己执行:{command}");
            return;
        }
        try
        {
            await Context.Terminal.WriteAsync(endpoint.SessionId, command + "\n", _lifetime.Token)
                          .ConfigureAwait(true);
            Feedback.Notify(FeedbackKind.Info, "已送到宿主终端", command);
        }
        catch (PluginPermissionDeniedException)
        {
            Feedback.Notify(FeedbackKind.Warning, "没有向终端回写的授权", $"可以自己执行:{command}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Feedback.ReportError("送往宿主终端", ex);
        }
    }

    private void SetNoEndpoint(int connectedSessions)
    {
        State = PanelConnectionState.NoEndpoint;
        ErrorIcon = "Icon.plug";
        ErrorTitle = "选一条已连接的 SSH 会话开始";
        ErrorDetail = "面板不自己发起连接 —— 凭据永远不出宿主核心。选定后会在这条会话上打开一条到 docker.sock 的通道。";
        ErrorHint = connectedSessions switch
        {
            0 => "当前没有已连接的会话 —— 也可以直接管本机 Docker。",
            1 => "当前宿主有 1 条已连接会话。",
            _ => $"当前宿主有 {connectedSessions} 条已连接会话。"
        };
        RecoveryActions.Clear();
        RecoveryActions.Add(new("选择会话…", "Icon.chevron-down", true, () => EndpointMenuOpen = true));
        RecoveryActions.Add(new("管理本机 Docker", "Docker.monitor", false, () =>
            _ = ConnectAsync(Endpoints.FirstOrDefault(e => e.IsLocal))));
    }

    private void SetFailed(Exception ex, EndpointItem item)
    {
        State = PanelConnectionState.Failed;
        RecoveryActions.Clear();
        if (ex is DockerUnreachableException unreachable)
        {
            ErrorHint = unreachable.InnerException?.Message ?? "";
            switch (unreachable.Reason)
            {
                case DockerUnreachableReason.SocketMissing:
                    ErrorIcon = "Docker.circle-x";
                    ErrorTitle = "打不开到 docker.sock 的通道";
                    ErrorDetail = "路径不存在、daemon 没在跑,或者当前账号根本碰不到这个 socket —— 远端只回了一句笼统的失败,自己分不出是哪一种。下面这条命令一次看清。";
                    // 三种可能一条命令全覆盖:socket 在不在、当前账号读不读得动它、daemon 跑没跑。
                    // 关键是**替他判**,而不是把 `ls -l` 和 `id` 两串输出丢过去让他自己对 ——
                    // 权限这一档最常见,而 "srw-rw---- root docker" 与 "groups=Users"
                    // 要交叉比对才看得出来。
                    RecoveryActions.Add(new("在终端里检查", "Icon.terminal", true,
                        () => _ = SendToHostTerminalAsync(
                            $"s={item.Endpoint.SocketPath}; " +
                            "if [ ! -S \"$s\" ]; then echo \"没有 $s —— 换条路径,或者远端压根没装 Docker\"; " +
                            "elif [ -r \"$s\" ] && [ -w \"$s\" ]; then echo \"$s 可读写 —— 问题不在权限\"; " +
                            "else echo \"$s 在,但当前账号读写不了:它属组 $(stat -c %G \"$s\" 2>/dev/null)," +
                            "而你在 $(id -nG)\"; fi; " +
                            "systemctl status docker --no-pager | head -3")));
                    RecoveryActions.Add(new("换一个 socket 路径", "Icon.settings", false, () => SettingsOpen = true));
                    item.Update(false, "打不开通道", FeedbackKind.Error);
                    break;
                case DockerUnreachableReason.PermissionDenied:
                    ErrorIcon = "Docker.lock";
                    ErrorTitle = "当前账号没有 docker.sock 的读写权限";
                    ErrorDetail = "账号不在 socket 的属组里。把账号加进 docker 组,或者换一个有权限的账号 —— " +
                                  "面板不会替你 sudo,那需要一个它拿不到也不该拿的口令。";
                    // 这一句不是啰嗦:组是**登录时**读进去的,usermod 改完之后当前这条 SSH 会话
                    // 仍然带着旧的组。不说清楚的话,用户会以为那条命令没生效,然后反复再跑一遍。
                    ErrorHint = "改完要重开一条 SSH 会话再连 —— 组是登录时定的,现有会话不会自己更新。" +
                                "另外:docker 组等同于 root(能挂载宿主根目录),给之前先掂量一下。";
                    RecoveryActions.Add(new("加进 docker 组", "Docker.users", true,
                        () => _ = SendToHostTerminalAsync("sudo usermod -aG docker $USER")));
                    // sudo -n 只在**已经配了免密**时才成 —— 面板绝不弹口令框,
                    // 那需要一个它拿不到也不该拿的东西。配不了就如实报出来。
                    RecoveryActions.Add(new("试试 sudo -n", "Docker.shield-alert", false,
                        () => _ = SendToHostTerminalAsync(
                            $"sudo -n test -r {item.Endpoint.SocketPath} && echo OK || echo '需要口令 —— 面板走不了这条路'")));
                    item.Update(true, "没有权限", FeedbackKind.Warning);
                    break;
                case DockerUnreachableReason.TunnelUnsupported:
                    ErrorIcon = "Icon.triangle-alert";
                    ErrorTitle = "这个宿主不支持远程隧道";
                    ErrorDetail = "Docker 面板需要 hostMode = inProcess:它交出去的是一条活的字节流,跨进程代理不了。";
                    break;
                case DockerUnreachableReason.SessionUnavailable:
                    ErrorIcon = "Icon.wifi-off";
                    ErrorTitle = "这条 SSH 会话已经断开";
                    ErrorDetail = "重新连上之后再选一次。";
                    item.Update(false, "已断开", FeedbackKind.Error);
                    break;
                default:
                    ErrorIcon = "Icon.circle-alert";
                    ErrorTitle = "连不上 Docker";
                    ErrorDetail = unreachable.Message;
                    break;
            }
        }
        else
        {
            ErrorIcon = "Icon.circle-alert";
            ErrorTitle = "连不上 Docker";
            ErrorDetail = ex.Message;
            ErrorHint = "";
        }
        RecoveryActions.Add(new("重试", "Icon.refresh-cw", RecoveryActions.Count == 0, () => _ = ConnectAsync(item)));
        Context.Log.Warn($"connect to docker failed: {ex.Message}");
        // 上面那一屏是"还不知道是哪种连不上"时的说法。真去问一句要跑一条远端命令,
        // 不该让用户对着转圈等 —— 先显示笼统的版本,探出结果再替换成具体的那一屏。
        if (ex is DockerUnreachableException
            {
                Reason: DockerUnreachableReason.SocketMissing
                or DockerUnreachableReason.PermissionDenied or DockerUnreachableReason.Unknown
            })
        {
            _ = RefineFailureAsync(item);
        }
    }

    /// <summary>
    /// 连不上之后,替用户把"到底是哪种连不上"问清楚,再用一句人话重写这一屏。
    /// <para>
    /// 为什么值得多跑一次:sshd 打不开通道时只回一句笼统的失败,面板分不出"没这个文件"
    /// 与"你没权限"。而这两件事要做的完全不一样。分不出来,界面就只能说
    /// "自己去终端看看",再把 <c>ls -l</c> 与 <c>id</c> 两串输出丢给用户交叉比对 ——
    /// 那是把诊断工作外包给了最不该做诊断的人。
    /// </para>
    /// <para>
    /// 探测是只读的,而且**不阻塞**上面那一屏:先把笼统的版本显示出来,探出结果再替换。
    /// 探不出来就什么都不动。
    /// </para>
    /// </summary>
    private async Task RefineFailureAsync(EndpointItem item)
    {
        var probe = await SocketProbe
            .RunAsync(Context.RemoteExec, item.Endpoint, _lifetime.Token).ConfigureAwait(true);
        // 探测期间用户可能已经换了端点、或者又连上了 —— 那就别再动这一屏。
        if (State != PanelConnectionState.Failed || !ReferenceEquals(SelectedEndpoint, item))
        {
            return;
        }
        switch (probe.Kind)
        {
            case SocketProbeKind.PermissionDenied:
                ShowPermissionDenied(item, probe);
                break;
            case SocketProbeKind.Missing:
                ShowSocketMissing(item);
                break;
            case SocketProbeKind.Ready:
                // 文件在、也读得动,却还是连不上 —— 那就不是这两件事。别再引导用户去加组。
                ErrorHint = $"{item.Endpoint.SocketPath} 存在而且当前账号读写得动,所以问题不在路径也不在权限。" +
                            "多半是 Docker 服务本身没起来,或者这条 SSH 会话中途断了。";
                break;
        }
    }

    /// <summary>
    /// "没权限"这一屏。
    /// <para>
    /// 句子里的账号名与组名都是**探出来的真名**,不是"当前账号""某个组"。
    /// 用户不需要知道什么叫属组,他只需要看懂"joes 不在 docker 里面,把它加进去"。
    /// </para>
    /// </summary>
    private void ShowPermissionDenied(EndpointItem item, SocketProbeResult probe)
    {
        var account = probe.Account is { Length: > 0 } a ? a : "当前账号";
        var group = probe.Group is { Length: > 0 } g ? g : "docker";
        ErrorIcon = "Docker.lock";
        ErrorTitle = $"账号「{account}」还不被允许使用这台机器上的 Docker";
        ErrorDetail = $"Docker 装着、也在跑,只是它只对 {group} 组开放,而 {account} 不在这个组里" +
                      (probe.Groups is { Length: > 0 } groups ? $"(它现在属于:{groups})" : "") +
                      $"。把 {account} 加进 {group} 组就能用了 —— 这一步要管理员口令,面板不会替你做。";
        // 这一句不是啰嗦:账号属于哪些组是**登录那一刻**定下来的。改完不重连,
        // 用户会以为那条命令没生效,然后反复再跑一遍。
        ErrorHint = "加完之后要重新连一次这条 SSH 会话才算数 —— 账号属于哪些组是登录时定下来的," +
                    "现在这条连接还带着旧的。 · 提醒:进了这个组就等于拿到这台机器的完全控制权,加之前先想清楚。";
        RecoveryActions.Clear();
        RecoveryActions.Add(new($"把 {account} 加进 {group} 组", "Docker.users", true,
            () => _ = SendToHostTerminalAsync($"sudo usermod -aG {group} {account}")));
        RecoveryActions.Add(new("改好了,重新连", "Icon.refresh-cw", false, () => _ = ConnectAsync(item)));
        item.Update(true, "没有权限", FeedbackKind.Warning);
    }

    /// <summary>"那个位置上没有 socket"这一屏。三种原因各给一条出路。</summary>
    private void ShowSocketMissing(EndpointItem item)
    {
        ErrorIcon = "Docker.circle-x";
        ErrorTitle = "这台机器上找不到 Docker 的接口";
        ErrorDetail = $"面板要找的是 {item.Endpoint.SocketPath} 这个文件,但它不在那儿。" +
                      "常见的就三种:这台机器没装 Docker、Docker 服务没启动,或者它把接口放在了别的位置。";
        ErrorHint = "";
        RecoveryActions.Clear();
        RecoveryActions.Add(new("看看 Docker 服务在不在跑", "Icon.terminal", true,
            () => _ = SendToHostTerminalAsync("systemctl status docker --no-pager | head -5")));
        RecoveryActions.Add(new("换一个位置", "Icon.settings", false, () => SettingsOpen = true));
        RecoveryActions.Add(new("重新连", "Icon.refresh-cw", false, () => _ = ConnectAsync(item)));
        item.Update(false, "找不到接口", FeedbackKind.Error);
    }

    /// <summary>切页。</summary>
    public async Task GoToAsync(PanelPage page)
    {
        if (page == PanelPage.Compose && !ComposeAvailable)
        {
            Feedback.Status(FeedbackKind.Info, "还没连上 Docker —— Compose 页要等通道建立之后才能用。");
            return;
        }
        CurrentPage = page;
        PageViewModel target = page switch
        {
            PanelPage.Overview => Overview,
            PanelPage.Images => Images,
            PanelPage.Volumes => Volumes,
            PanelPage.Networks => Networks,
            PanelPage.Compose => ComposePage,
            PanelPage.System => SystemPage,
            _ => Containers
        };
        ActivePage = target;
        if (IsReady)
        {
            try
            {
                await target.ActivateAsync(_lifetime.Token).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Feedback.ReportError(target.Title, ex);
            }
        }
    }

    /// <summary>刷新当前页。</summary>
    public async Task RefreshActiveAsync()
    {
        if (!IsReady || ActivePage is not { } page)
        {
            return;
        }
        try
        {
            await page.RefreshAsync(_lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Feedback.ReportError(page.Title, ex);
        }
    }

    /// <summary>状态栏那串计数:容器数。</summary>
    public void SetContainerCount(int count) => SetCount(ref _countContainers, count);

    /// <summary>状态栏那串计数:镜像数。</summary>
    public void SetImageCount(int count) => SetCount(ref _countImages, count);

    /// <summary>状态栏那串计数:卷数。</summary>
    public void SetVolumeCount(int count) => SetCount(ref _countVolumes, count);

    /// <summary>
    /// 三个数字分开记。
    /// <para>
    /// 早先是一个 <c>SetCounts(a, b, c)</c>,每个调用方只知道自己那一个数,
    /// 另外两个只能去读别的页 —— 而那些页在被打开之前都是 0,
    /// 于是状态栏在用户逛到那一页之前一直显示"0 镜像"。
    /// </para>
    /// </summary>
    private void SetCount(ref int slot, int count)
    {
        if (slot == count)
        {
            return;
        }
        slot = count;
        CountsText = $"{_countContainers} 容器 · {_countImages} 镜像 · {_countVolumes} 卷";
    }

    /// <summary>
    /// 给一条确认请求补上主机信息。
    /// <para>
    /// 调用方不必自己填 —— 漏填一次的代价是一个不写清主机的"确定删除 3 个卷吗",
    /// 而那正是这个面板能犯的最贵的错误。所以这条路是**唯一**打开闸门的路。
    /// </para>
    /// </summary>
    public ConfirmRequest BuildConfirm(ConfirmRequest request) => request with
    {
        HostName = SelectedEndpoint?.DisplayName ?? "(未选择)",
        HostDetail = SelectedEndpoint is { } item
            ? $"· {item.Detail} · {item.Endpoint.SocketPath}"
            : "",
        HostWarning = SelectedEndpoint?.IsLocal == false
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Context.Events.SessionConnected -= OnSessionChanged;
        Context.Events.SessionDisconnected -= OnSessionChanged;
        Confirm.CancelPending();
        Tasks.CancelAll();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await StopEventStreamAsync().ConfigureAwait(false);
        foreach (var page in AllPages)
        {
            if (page is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (Client is not null)
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            Client = null;
        }
        _lifetime.Dispose();
    }
}
