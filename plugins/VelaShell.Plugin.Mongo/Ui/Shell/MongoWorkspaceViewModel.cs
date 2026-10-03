using System.Collections.ObjectModel;
using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>剪贴板与文件选择框只有 TopLevel 才拿得到,由视图注入。</summary>
internal interface IViewServices
{
    /// <summary>复制到剪贴板。</summary>
    Task CopyAsync(string text);

    /// <summary>选保存路径。</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds);

    /// <summary>选要打开的文件。</summary>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple);

    /// <summary>选文件夹。</summary>
    Task<string?> PickFolderAsync(string title);
}

/// <summary>
/// MongoDB 工作台的外壳(从命令面板打开的那一个面板,与 Docker 面板同一个路数)。
/// <para>
/// 连接由插件自己管:对象树的根上是全部已保存的连接(Navicat 的习惯),双击连上,连着的那条下面挂库;
/// 标签页属于各自的连接(<see cref="MongoSession" />),工具栏与状态条跟着当前标签 / 选中行所在的那条连接走。
/// 新建 / 编辑连接是设计稿 10 的对话框,连接中 / 连接失败是设计稿 22 的占位标签。
/// </para>
/// </summary>
internal sealed partial class MongoWorkspaceViewModel : ObservableObject, IWorkbench, IDisposable
{
    private readonly IPluginContext _context;
    private WorkspaceTab? _activeTab;
    private DialogViewModel? _dialog;
    private MongoSession? _currentSession;

    /// <summary>构造。</summary>
    /// <param name="loc">文案表。</param>
    /// <param name="context">插件上下文。</param>
    /// <param name="store">插件私有的持久化。</param>
    public MongoWorkspaceViewModel(Loc loc, IPluginContext context, MongoStore store)
    {
        Loc = loc;
        _context = context;
        Store = store;
        Log = context.Log;
        Profiles = new MongoProfileStore(context);
        Connector = new MongoConnector(context, loc);
        InitializeCommands();
    }

    /// <inheritdoc />
    public Loc Loc { get; }

    /// <inheritdoc />
    public MongoStore Store { get; }

    /// <inheritdoc />
    public IPluginLogger Log { get; }

    /// <summary>已保存连接的读写。</summary>
    internal MongoProfileStore Profiles { get; }

    /// <summary>连接与测试。</summary>
    internal MongoConnector Connector { get; }

    /// <summary>插件上下文(连接对话框要列宿主里已保存的 SSH 连接)。</summary>
    internal IPluginContext Context => _context;

    /// <summary>视图注入的剪贴板与文件选择。</summary>
    internal IViewServices? ViewServices { get; set; }

    /// <summary>对象树根上的连接。</summary>
    public ObservableCollection<ConnectionEntry> Connections { get; } = [];

    /// <summary>一条已保存的连接都没有(内容区给「新建连接」的空状态)。</summary>
    public bool HasNoConnections => Connections.Count == 0;

    /// <summary>
    /// 当前连接:活动标签所属的那条;没有标签时是选中行所在的那条。
    /// 工具栏的大按钮、只读开关、服务器徽章与状态条都作用于它。
    /// </summary>
    public MongoSession? CurrentSession
    {
        get => _currentSession;
        private set
        {
            if (!SetProperty(ref _currentSession, value))
            {
                return;
            }
            RaiseSessionProperties();
        }
    }

    /// <summary>有当前连接(工具栏右侧的只读开关与服务器徽章只在这时出现)。</summary>
    public bool HasSession => _currentSession is not null;

    private void UpdateCurrentSession() =>
        CurrentSession = _activeTab?.Owner as MongoSession
                         ?? SelectedNode?.Session
                         ?? (_activeTab is null ? Connections.Select(static c => c.Session).OfType<MongoSession>().FirstOrDefault() : null);

    /// <summary>某条连接的护栏、可用性或延迟变了;是当前连接就刷新工具栏与状态条。</summary>
    internal void OnSessionChanged(MongoSession session)
    {
        if (ReferenceEquals(session, _currentSession))
        {
            RaiseSessionProperties();
        }
    }

    private void RaiseSessionProperties() => RaisePropertiesChanged(
        nameof(HasSession), nameof(IsReadOnly), nameof(ReadOnlyLabel), nameof(ServerBadge), nameof(ServerDetail),
        nameof(ServerDotClass), nameof(BannerMessage), nameof(HasBanner), nameof(CanOfferDropDatabase),
        nameof(ShowSystemDatabases), nameof(SystemDatabasesTip), nameof(StatusConnection), nameof(StatusScope), nameof(StatusDetail), nameof(StatusDotClass));

    // ── 工具栏右侧:只读开关 + 服务器徽章 ──────────────────────────────────

    /// <summary>当前连接是否只读(开关双向绑定;解锁生产连接要确认,走 <see cref="ToggleReadOnlyCommand" />)。</summary>
    public bool IsReadOnly
    {
        get => _currentSession?.Guard.IsReadOnly ?? false;
        set
        {
            if (_currentSession is { } session)
            {
                session.Guard.IsReadOnly = value;
            }
        }
    }

    /// <summary>开关上的字:「读写」/「只读」。</summary>
    public string ReadOnlyLabel => IsReadOnly ? Loc["Toolbar_ReadOnlyOn"] : Loc["Toolbar_ReadWrite"];

    /// <summary>服务器徽章(<c>rs0 · PRIMARY</c>)。</summary>
    public string ServerBadge => _currentSession?.Connection.Server.Badge ?? "";

    /// <summary>徽章下面那行小字(版本 · 地址 · 延迟)。</summary>
    public string ServerDetail
    {
        get
        {
            if (_currentSession is not { } session)
            {
                return "";
            }
            ServerInfo server = session.Connection.Server;
            string address = server.Me is { Length: > 0 } me ? me : session.Connection.Endpoint;
            return $"MongoDB {server.Version} · {address}" + (session.LatencyMs is { } ms ? $" · {ms} ms" : "");
        }
    }

    /// <summary>徽章前的色点:连着且可写绿、只读成员橙、断了红。</summary>
    public string ServerDotClass => _currentSession is not { } session
        ? "off"
        : !session.IsAvailable ? "err" : session.Connection.Server.IsWritable ? "ok" : "warn";

    /// <summary>当前连接断了时内容区顶上的横幅。</summary>
    public string BannerMessage => _currentSession is { IsAvailable: false } ? Loc["Workspace_Disconnected"] : "";

    /// <summary>有横幅。</summary>
    public bool HasBanner => BannerMessage.Length > 0;

    /// <summary>对象树的右键菜单要不要给「删除数据库」(连接里禁用了就不给)。</summary>
    public bool CanOfferDropDatabase => _currentSession is { } session && !session.Guard.DisableDropDatabase;

    // ── 状态条(设计稿底部那一行:连接 · 范围 · 当前标签的状态)──────────────

    /// <summary>状态条左边的连接名。</summary>
    public string StatusConnection => _currentSession?.ConnectionName ?? "";

    /// <summary>状态条上连接名前的色点。</summary>
    public string StatusDotClass => ServerDotClass;

    /// <summary>状态条上的范围(<c>shop.orders</c>)。</summary>
    public string StatusScope
    {
        get
        {
            if (_currentSession is not { } session)
            {
                return "";
            }
            (string? db, string? coll) = session.Scope;
            return db is null ? "" : coll is null ? db : $"{db}.{coll}";
        }
    }

    /// <summary>当前标签自己的状态(<c>shop.orders · 50 行 · 12 ms · IXSCAN</c>)。</summary>
    public string StatusText => _activeTab?.StatusText is { Length: > 0 } text ? text : "";

    /// <summary>状态条连接名后面那一段:标签有自己的状态就用它(那里已经带着范围),没有就只写范围。</summary>
    public string StatusDetail => StatusText.Length > 0 ? StatusText : StatusScope;

    /// <inheritdoc />
    public void NotifyStatusChanged() => RaisePropertiesChanged(nameof(StatusText), nameof(StatusScope), nameof(StatusDetail));

    // ── 标签页 ─────────────────────────────────────────────────────────────

    /// <summary>对象标签。</summary>
    public ObservableCollection<WorkspaceTab> Tabs { get; } = [];

    /// <summary>活动标签。</summary>
    public WorkspaceTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            WorkspaceTab? previous = _activeTab;
            if (!SetProperty(ref _activeTab, value))
            {
                return;
            }
            if (previous is not null)
            {
                previous.IsActive = false;
            }
            if (value is not null)
            {
                value.IsActive = true;
            }
            RaisePropertiesChanged(nameof(ActiveTool), nameof(HasTabs));
            UpdateCurrentSession();
            NotifyStatusChanged();
            RevealInTree(value);
        }
    }

    /// <summary>有标签(没有时内容区给空状态)。</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>工具栏上哪个大按钮亮着。</summary>
    public string ActiveTool => _activeTab switch
    {
        null => "",
        ObjectsTabViewModel { Filter: ObjectFilter.Views } => "views",
        ObjectsTabViewModel => "collections",
        CollectionTabViewModel => "collections",
        QueryTabViewModel => "query",
        PipelineTabViewModel => "aggregate",
        GridFsTabViewModel => "gridfs",
        DesignTabViewModel { Page: DesignPage.Indexes } => "indexes",
        DesignTabViewModel => "collections",
        MonitorTabViewModel => "monitor",
        ProfilerTabViewModel => "profiler",
        UsersTabViewModel { ShowRoles: true } => "roles",
        UsersTabViewModel => "users",
        _ => ""
    };

    internal void OnTabToolChanged() => RaisePropertyChanged(nameof(ActiveTool));

    /// <summary>
    /// 挂上一个标签:同一条连接里同键的已经开着就切过去(新建的那个扔掉)。新标签开在当前标签右边(Navicat 的习惯)。
    /// </summary>
    internal T Activate<T>(T tab) where T : WorkspaceTab
    {
        if (Tabs.FirstOrDefault(t => t.Key == tab.Key && ReferenceEquals(t.Owner, tab.Owner)) is T existing)
        {
            tab.Dispose();
            ActiveTab = existing;
            return existing;
        }
        tab.Closer = CloseTabAsync;
        int index = _activeTab is null ? Tabs.Count : Tabs.IndexOf(_activeTab) + 1;
        Tabs.Insert(Math.Clamp(index, 0, Tabs.Count), tab);
        ActiveTab = tab;
        RaisePropertyChanged(nameof(HasTabs));
        _ = LoadTabAsync(tab);
        return tab;
    }

    private async Task LoadTabAsync(WorkspaceTab tab)
    {
        try
        {
            await tab.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error($"Loading tab '{tab.Header}' failed.", ex);
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    /// <summary>关一个标签(有未提交的修改先问)。</summary>
    internal async Task CloseTabAsync(WorkspaceTab tab)
    {
        if (!tab.CanClose || !await tab.ConfirmCloseAsync().ConfigureAwait(true))
        {
            return;
        }
        RemoveTab(tab);
    }

    private void RemoveTab(WorkspaceTab tab)
    {
        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        }
        tab.Dispose();
        RaisePropertyChanged(nameof(HasTabs));
        UpdateCurrentSession();
    }

    /// <summary>不问就关掉一批标签(它们指向的东西已经不在了)。</summary>
    internal void CloseTabsWhere(Func<WorkspaceTab, bool> predicate)
    {
        foreach (WorkspaceTab tab in Tabs.Where(predicate).ToList())
        {
            RemoveTab(tab);
        }
    }

    // ── 覆盖层 ─────────────────────────────────────────────────────────────

    /// <summary>当前对话框。</summary>
    public DialogViewModel? Dialog
    {
        get => _dialog;
        private set
        {
            if (SetProperty(ref _dialog, value))
            {
                RaisePropertyChanged(nameof(HasDialog));
            }
        }
    }

    /// <summary>有对话框(覆盖层与遮罩)。</summary>
    public bool HasDialog => _dialog is not null;

    /// <summary>右下角的提示。</summary>
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <inheritdoc />
    public void ShowDialog(DialogViewModel dialog)
    {
        if (Dialog is { } open && !ReferenceEquals(open, dialog))
        {
            CloseDialog(open);
        }
        Dialog = dialog;
    }

    /// <inheritdoc />
    public void CloseDialog(DialogViewModel dialog)
    {
        if (!ReferenceEquals(Dialog, dialog))
        {
            return;
        }
        Dialog = null;
        dialog.OnClosed();
        (dialog as IDisposable)?.Dispose();
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        var dialog = new ConfirmDialogViewModel(this, request);
        // 确认框可能是从另一个对话框里弹出来的(向导执行前、连接对话框里删连接):先记住它,确认完再放回去。
        DialogViewModel? underneath = Dialog;
        Dialog = dialog;
        return AwaitAndRestore();

        async Task<bool> AwaitAndRestore()
        {
            bool result = await dialog.Result.ConfigureAwait(true);
            if (underneath is not null && Dialog is null)
            {
                Dialog = underneath;
            }
            return result;
        }
    }

    /// <inheritdoc />
    public void Toast(ToastRequest toast) => Dispatcher.UIThread.Post(() =>
    {
        // 最多叠三条:更多的提示只会把内容区挡住,而早的那几条已经过时了。
        while (Toasts.Count >= 3)
        {
            Toasts.RemoveAt(0);
        }
        Toasts.Add(new(toast, t => Toasts.Remove(t)));
    });

    /// <inheritdoc />
    public Task CopyAsync(string text) => ViewServices?.CopyAsync(text) ?? Task.CompletedTask;

    /// <inheritdoc />
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds) =>
        ViewServices?.PickSaveFileAsync(title, suggestedName, kinds) ?? Task.FromResult<string?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple = false) =>
        ViewServices?.PickOpenFilesAsync(title, kinds, multiple) ?? Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public Task<string?> PickFolderAsync(string title) =>
        ViewServices?.PickFolderAsync(title) ?? Task.FromResult<string?>(null);

    /// <summary>集合名在 mongosh 里的写法:合法标识符直接写,否则 <c>getCollection("…")</c>。</summary>
    internal static string ShellCollectionRef(string collection) =>
        System.Text.RegularExpressions.Regex.IsMatch(collection, "^[A-Za-z_$][A-Za-z0-9_$]*$")
            ? collection
            : $"getCollection({BsonText.Quote(collection)})";

    internal static string Count(long n) => BsonText.Grouped(n);

    /// <summary>关掉全部标签与连接(面板关了)。</summary>
    public void Dispose()
    {
        foreach (WorkspaceTab tab in Tabs)
        {
            tab.Dispose();
        }
        Tabs.Clear();
        DisposeTree();
        foreach (ConnectionEntry entry in Connections)
        {
            entry.Connecting?.Cancel();
            if (entry.Session is { } session)
            {
                entry.Session = null;
                session.Dispose();
                _ = session.Link.DisposeAsync().AsTask();
            }
        }
    }
}
