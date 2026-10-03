namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 覆盖层上的对话框(编辑文档、新建集合、导入 / 导出 / 数据传输向导、确认框)。
/// <para>
/// 外壳画统一的外框:28px 标题栏(15px 图标 + 标题 + 等宽副标题 + 右上角 40×27 关闭方块,
/// 与宿主 DESIGN.md §4.2 的对话框标题栏同规格),内容区与底栏由派生类的视图自己画。
/// </para>
/// </summary>
internal abstract class DialogViewModel : ObservableObject
{
    private string _title = "";
    private string _subtitle = "";
    private readonly IMongoWorkspace? _workspace;

    /// <summary>构造一条连接里的对话框。</summary>
    /// <param name="workspace">这条连接的服务。</param>
    protected DialogViewModel(IMongoWorkspace workspace)
        : this((IWorkbench)workspace)
    {
        _workspace = workspace;
    }

    /// <summary>构造外壳自己的对话框(新建 / 编辑连接、确认框):不属于任何一条连接。</summary>
    /// <param name="workbench">外壳服务。</param>
    protected DialogViewModel(IWorkbench workbench)
    {
        Workbench = workbench;
        CloseCommand = new RelayCommand(Close);
        RequestCloseCommand = new AsyncCommand(RequestCloseAsync);
    }

    /// <summary>这条连接的服务。外壳自己的对话框没有连接,读它是编程错误。</summary>
    public IMongoWorkspace Workspace =>
        _workspace ?? throw new InvalidOperationException(GetType().Name + " does not belong to a connection.");

    /// <summary>外壳服务。</summary>
    public IWorkbench Workbench { get; }

    /// <summary>它属于哪条连接;外壳自己的对话框为 <see langword="null" />(断开连接时据此把它一并关掉)。</summary>
    public IMongoWorkspace? Owner => _workspace;

    /// <summary>文案表。</summary>
    public Loc Loc => Workbench.Loc;

    /// <summary>标题。</summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>标题旁的等宽小字(<c>shop.orders</c>)。</summary>
    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    /// <summary>标题栏图标。</summary>
    public virtual string IconKey => "Mongo.leaf";

    /// <summary>标题栏图标颜色令牌。</summary>
    public virtual string IconToken => "VelaAccent";

    /// <summary>对话框宽度。</summary>
    public virtual double Width => 640;

    /// <summary>对话框高度;<see cref="double.NaN" /> = 按内容。</summary>
    public virtual double Height => double.NaN;

    /// <summary>能不能不问就关(有未保存修改、向导执行中时为假 —— × 与 Esc 会先确认)。</summary>
    public virtual bool CanCloseWithEscape => true;

    /// <summary>关闭命令(直接关,不问 —— 保存成功、取消按钮这类"已经决定了"的路径用)。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>
    /// "想关"命令:标题栏的 × 与 Esc 走它。先过 <see cref="ConfirmCloseAsync" />,
    /// 有未保存的修改或正在执行时问一句,而不是一下子把用户填了半天的东西丢掉。
    /// </summary>
    public AsyncCommand RequestCloseCommand { get; }

    /// <summary>按 × / Esc 时调用。</summary>
    public async Task RequestCloseAsync()
    {
        if (await ConfirmCloseAsync().ConfigureAwait(true))
        {
            Close();
        }
    }

    /// <summary>
    /// 关闭前的确认。默认:<see cref="CanCloseWithEscape" /> 为真就直接放行,否则弹一次"放弃并关闭?"。
    /// 派生类可以给出更具体的问法(或直接拒绝)。
    /// </summary>
    protected virtual async Task<bool> ConfirmCloseAsync()
    {
        if (CanCloseWithEscape)
        {
            return true;
        }
        return await Workbench.ConfirmAsync(new()
        {
            Title = Loc["Dialog_DiscardTitle"],
            Message = Loc.Format("Dialog_DiscardBody", Title),
            ConfirmLabel = Loc["Dialog_DiscardConfirm"],
            IconKey = "Mongo.triangle-alert",
            Danger = true
        }).ConfigureAwait(true);
    }

    /// <summary>关掉了(确认框靠它回填结果)。</summary>
    public event Action? Closed;

    /// <summary>关闭。</summary>
    public void Close() => Workbench.CloseDialog(this);

    /// <summary>外壳关掉它之后调用。</summary>
    internal virtual void OnClosed() => Closed?.Invoke();
}
