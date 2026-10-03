namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>对象标签页的种类(决定 Navicat 工具栏哪个大按钮亮着)。</summary>
public enum TabKind
{
    /// <summary>连接中 / 连接失败的占位标签。</summary>
    Connection,

    /// <summary>对象列表。</summary>
    Objects,

    /// <summary>集合工作台。</summary>
    Collection,

    /// <summary>查询编辑器。</summary>
    Query,

    /// <summary>聚合管道。</summary>
    Pipeline,

    /// <summary>GridFS。</summary>
    GridFs,

    /// <summary>集合设计。</summary>
    Design,

    /// <summary>服务器监控。</summary>
    Monitor,

    /// <summary>慢查询。</summary>
    Profiler,

    /// <summary>用户与角色。</summary>
    Users
}

/// <summary>
/// 一个对象标签页(工作台主区里那一排:<c>对象</c> / <c>orders @shop</c> / <c>查询 1</c> / …)。
/// <para>
/// 外壳只认这个基类:标题、图标、"有未提交修改"圆点、关闭前确认、给状态栏的那一行。
/// 内容由派生类自己画(视图经 <see cref="ViewLocator" /> 按视图模型类型找到)。
/// </para>
/// </summary>
internal abstract class WorkspaceTab : ObservableObject, IDisposable
{
    private string _title = "";
    private string _scope = "";
    private bool _isModified;
    private bool _isActive;
    private string _statusText = "";
    private readonly IMongoWorkspace? _workspace;

    /// <summary>构造一条连接里的标签页。</summary>
    /// <param name="workspace">这条连接的服务。</param>
    protected WorkspaceTab(IMongoWorkspace workspace)
        : this((IWorkbench)workspace)
    {
        _workspace = workspace;
    }

    /// <summary>
    /// 构造外壳自己的标签页(连接中 / 连接失败的占位卡):不属于任何一条连接,<see cref="Workspace" /> 不可用。
    /// </summary>
    /// <param name="workbench">外壳服务。</param>
    protected WorkspaceTab(IWorkbench workbench)
    {
        Workbench = workbench;
        CloseCommand = new AsyncCommand(() => Closer?.Invoke(this) ?? Task.CompletedTask);
    }

    /// <summary>这条连接的服务。外壳自己的标签页没有连接,读它是编程错误。</summary>
    public IMongoWorkspace Workspace =>
        _workspace ?? throw new InvalidOperationException(GetType().Name + " does not belong to a connection.");

    /// <summary>外壳服务。</summary>
    public IWorkbench Workbench { get; }

    /// <summary>它属于哪条连接;外壳自己的标签页为 <see langword="null" />。</summary>
    public IMongoWorkspace? Owner => _workspace;

    /// <summary>关掉自己(外壳在挂上标签时设好)。</summary>
    internal Func<WorkspaceTab, Task>? Closer { get; set; }

    /// <summary>文案表(视图里 <c>{Binding Loc[...]}</c> 用)。</summary>
    public Loc Loc => Workbench.Loc;

    /// <summary>种类。</summary>
    public abstract TabKind Kind { get; }

    /// <summary>同一对象只开一个标签:相同键的标签被复用而不是再开一个。</summary>
    public abstract string Key { get; }

    /// <summary>标题(<c>orders</c>、<c>查询 1</c>、<c>设计 · orders</c>)。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                RaisePropertyChanged(nameof(Header));
            }
        }
    }

    /// <summary>作用域后缀(<c>@shop</c>);没有为空。</summary>
    public string Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value))
            {
                RaisePropertyChanged(nameof(Header));
            }
        }
    }

    /// <summary>标签上显示的完整文字。</summary>
    public string Header => string.IsNullOrEmpty(Scope) ? Title : $"{Title} {Scope}";

    /// <summary>标签图标(<c>Mongo.table-2</c>)。</summary>
    public abstract string IconKey { get; }

    /// <summary>标签图标颜色令牌。</summary>
    public abstract string IconToken { get; }

    /// <summary>有未提交的修改(标签上的橙点,关闭前要确认)。</summary>
    public bool IsModified
    {
        get => _isModified;
        protected set => SetProperty(ref _isModified, value);
    }

    /// <summary>当前是不是活动标签。</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value))
            {
                return;
            }
            if (value)
            {
                OnActivated();
            }
            else
            {
                OnDeactivated();
            }
        }
    }

    /// <summary>
    /// 给宿主状态栏的那一行(<c>shop.orders · 50 行 · 12 ms · IXSCAN status_1_createdAt_-1</c>)。
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        protected set
        {
            if (SetProperty(ref _statusText, value) && IsActive)
            {
                Workbench.NotifyStatusChanged();
            }
        }
    }

    /// <summary>能不能关(对象列表那个固定标签不能关)。</summary>
    public virtual bool CanClose => true;

    /// <summary>关闭命令。</summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>第一次显示时加载(外壳在标签加进来之后调一次)。</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>变成活动标签时(刷新过期数据之类)。</summary>
    protected virtual void OnActivated()
    {
    }

    /// <summary>不再是活动标签时(停采样、停轮询之类)。</summary>
    protected virtual void OnDeactivated()
    {
    }

    /// <summary>
    /// 关闭前的确认。有未提交修改时问一句;返回 <see langword="false" /> 即不关。
    /// </summary>
    public virtual async Task<bool> ConfirmCloseAsync()
    {
        if (!IsModified)
        {
            return true;
        }
        return await Workbench.ConfirmAsync(new()
        {
            Title = Loc["Tab_CloseModifiedTitle"],
            Message = Loc.Format("Tab_CloseModifiedBody", Header),
            ConfirmLabel = Loc["Tab_CloseDiscard"],
            IconKey = "Mongo.triangle-alert",
            Danger = true
        }).ConfigureAwait(true);
    }

    /// <summary>F5 / Ctrl+R 之类的"刷新当前页"。</summary>
    public virtual Task RefreshAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public virtual void Dispose()
    {
    }
}
