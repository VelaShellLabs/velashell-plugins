using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>左导航栏的一个去处。</summary>
public enum PanelPage
{
    /// <summary>总览。</summary>
    Overview,

    /// <summary>容器。</summary>
    Containers,

    /// <summary>镜像。</summary>
    Images,

    /// <summary>卷。</summary>
    Volumes,

    /// <summary>网络。</summary>
    Networks,

    /// <summary>Compose。</summary>
    Compose,

    /// <summary>系统。</summary>
    System
}

/// <summary>
/// 一个页面的视图模型。
/// <para>
/// 页面**不自己拉数据**:什么时候刷新由外壳按事件流决定。这样一条 <c>docker events</c>
/// 就能同时喂饱所有页面,而不是每页各挂一个定时器把远端敲成筛子。
/// </para>
/// </summary>
public abstract class PageViewModel(DockerPanelViewModel shell) : ObservableObject
{

    /// <summary>外壳(拿客户端、闸门、任务中心、反馈)。</summary>
    protected DockerPanelViewModel Shell { get; } = shell;

    /// <summary>当前端点的客户端;还没连上时为 <see langword="null" />。</summary>
    protected DockerClient? Client => Shell.Client;

    /// <summary>页面标识。</summary>
    public abstract PanelPage Page { get; }

    /// <summary>标题(工具条左上角)。</summary>
    public abstract string Title { get; }

    /// <summary>
    /// 右侧详情抽屉的尺寸与形态。没有抽屉的页面(总览 / 系统 / Compose)不用它。
    /// <para>
    /// 放在基类上,是为了让视图那两个通用件(<see cref="DrawerLayout" />、
    /// <see cref="ColumnResizer" />)对四个页面一视同仁 —— 否则同一套拖拽逻辑要抄四遍。
    /// </para>
    /// </summary>
    public DrawerState Drawer { get; } = new();

    /// <summary>这一页列表的列宽;没有可拖列表的页面为 <see langword="null" />。</summary>
    public virtual ListColumns? ColumnLayout => null;

    /// <summary>某一列当前可见行里的文字(双击自适应时用来量宽度)。</summary>
    public virtual IEnumerable<string> ColumnTexts(string key) => [];

    /// <summary>正在加载。</summary>
    public bool Busy
    {
        get;
        protected set => SetField(ref field, value);
    }

    /// <summary>至少成功加载过一次 —— 决定骨架屏什么时候让位给真数据。</summary>
    public bool LoadedOnce
    {
        get;
        protected set => SetField(ref field, value);
    }

    /// <summary>页面被选中时调用。</summary>
    public virtual Task ActivateAsync(CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    /// <summary>重新拉一遍这一页的数据。</summary>
    public abstract Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 换了端点:把这一页清空。
    /// <para>
    /// 不清的话,切到另一台机器的瞬间会先看到上一台的容器列表 ——
    /// 一个足以让人对着错误的机器按下"删除"的过渡态。
    /// </para>
    /// </summary>
    public abstract void Reset();

    /// <summary>
    /// 一条 daemon 事件到了。返回 <see langword="true" /> 表示这一页需要刷新。
    /// <para>
    /// 由页面自己判断,而不是一律刷新:<c>compose up</c> 一次能推来几十条事件,
    /// 逐条刷新等于把远端敲成筛子。
    /// </para>
    /// </summary>
    public abstract bool WantsRefresh(DockerEvent dockerEvent);
}
