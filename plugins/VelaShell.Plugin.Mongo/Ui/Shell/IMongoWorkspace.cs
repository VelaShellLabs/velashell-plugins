using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合设计页的子页(设计稿 07 / 08 / 16 顶部那一排)。</summary>
public enum DesignPage
{
    /// <summary>索引。</summary>
    Indexes,

    /// <summary>Schema 分析。</summary>
    Schema,

    /// <summary>验证规则。</summary>
    Validation,

    /// <summary>选项(TTL、排序规则)。</summary>
    Options,

    /// <summary>统计。</summary>
    Stats
}

/// <summary>对象列表的过滤芯片(设计稿 12)。</summary>
public enum ObjectFilter
{
    /// <summary>全部。</summary>
    All,

    /// <summary>集合。</summary>
    Collections,

    /// <summary>视图。</summary>
    Views,

    /// <summary>时序集合。</summary>
    TimeSeries,

    /// <summary>GridFS 桶。</summary>
    GridFs
}

/// <summary>文件选择框的一类文件。</summary>
/// <param name="Name">显示名(<c>JSON</c>)。</param>
/// <param name="Patterns">通配(<c>*.json</c>)。</param>
public sealed record FileKind(string Name, params string[] Patterns)
{
    /// <summary>JSON / JSONL。</summary>
    public static FileKind Json { get; } = new("JSON", "*.json", "*.jsonl", "*.ndjson");

    /// <summary>CSV。</summary>
    public static FileKind Csv { get; } = new("CSV", "*.csv", "*.tsv");

    /// <summary>Excel。</summary>
    public static FileKind Excel { get; } = new("Excel", "*.xlsx");

    /// <summary>BSON。</summary>
    public static FileKind Bson { get; } = new("BSON", "*.bson");

    /// <summary>脚本。</summary>
    public static FileKind Script { get; } = new("JavaScript", "*.js");

    /// <summary>任意文件。</summary>
    public static FileKind Any { get; } = new("*", "*");
}

/// <summary>
/// 工作台外壳本身的服务:覆盖层、确认框、提示、剪贴板与文件选择。不属于任何一条连接 ——
/// 新建连接的对话框、连接中 / 连接失败的占位标签只认得它。
/// </summary>
internal interface IWorkbench
{
    /// <summary>文案表。</summary>
    Loc Loc { get; }

    /// <summary>持久化(历史、收藏、保存的查询与配置)。</summary>
    MongoStore Store { get; }

    /// <summary>日志。</summary>
    IPluginLogger Log { get; }

    /// <summary>挂一个对话框到覆盖层。</summary>
    void ShowDialog(DialogViewModel dialog);

    /// <summary>关掉一个对话框。</summary>
    void CloseDialog(DialogViewModel dialog);

    /// <summary>确认框(危险操作一律红色描边按钮 + 明确后果;生产连接要手打名称)。</summary>
    Task<bool> ConfirmAsync(ConfirmRequest request);

    /// <summary>右下角的提示(可带一个动作,如「撤销」)。</summary>
    void Toast(ToastRequest toast);

    /// <summary>复制到剪贴板。</summary>
    Task CopyAsync(string text);

    /// <summary>选一个保存路径。</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds);

    /// <summary>选一个或多个要打开的文件。</summary>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple = false);

    /// <summary>选一个文件夹。</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>当前标签页的状态行变了(由标签页调用,外壳转给状态显示)。</summary>
    void NotifyStatusChanged();
}

/// <summary>
/// 一条连接交给各对象标签页与对话框的服务(工作台外壳的服务也经它转达)。
/// <para>
/// 对象树的根上可以挂好几条连接,每条连着的连接各有一个(<see cref="MongoSession" />);
/// 标签页与对话框拿到的就是自己那条连接的这个对象,不会拿错库。
/// </para>
/// <para>
/// 标签页之间**不互相引用**:网格要"在新查询中打开",不是去找查询编辑器的实例,
/// 而是调 <see cref="OpenQuery" /> —— 外壳决定是开新标签还是复用。对话框同理,
/// 一律经 <see cref="IWorkbench.ShowDialog" /> 挂到外壳的覆盖层上。
/// </para>
/// </summary>
internal interface IMongoWorkspace : IWorkbench
{
    /// <summary>连接。</summary>
    MongoConnection Connection { get; }

    /// <summary>持久化键里用的连接标识(已保存连接的 id,不含凭据)。</summary>
    string ConnectionKey { get; }

    /// <summary>连接名(用户给这条连接起的名字,<c>mongo-inner-01</c>)。</summary>
    string ConnectionName { get; }

    /// <summary>写护栏(只读开关、写前确认、权限)。</summary>
    WriteGuard Guard { get; }

    /// <summary>
    /// 写之前的统一检查:只读模式或没有写权限时弹提示并返回 <see langword="false" />。
    /// 每一条写路径都必须先过它 —— 只读是客户端护栏,漏一处就是一处"只读模式下照样写进去了"。
    /// </summary>
    bool EnsureWritable(string database);

    /// <summary>当前选中的库 / 集合(工具栏大按钮作用的对象)。</summary>
    (string? Database, string? Collection) Scope { get; }

    /// <summary>对象树里已知的库名。</summary>
    IReadOnlyList<string> Databases { get; }

    /// <summary>对象树里已知的某个库的集合(没展开过的库为空)。</summary>
    IReadOnlyList<CollectionInfo> CollectionsOf(string database);

    /// <summary>打开集合工作台(网格 / 树 / JSON)。</summary>
    void OpenCollection(string database, string collection, string? filter = null);

    /// <summary>打开一个新查询标签。</summary>
    void OpenQuery(string database, string? text = null, bool run = false);

    /// <summary>打开聚合管道构建器。</summary>
    void OpenPipeline(string database, string collection, BsonArray? pipeline = null);

    /// <summary>打开集合设计。</summary>
    void OpenDesign(string database, string collection, DesignPage page = DesignPage.Indexes);

    /// <summary>打开 GridFS 桶。</summary>
    void OpenGridFs(string database, string bucket);

    /// <summary>打开对象列表。</summary>
    void OpenObjects(string database, ObjectFilter filter = ObjectFilter.All);

    /// <summary>打开服务器监控。</summary>
    void OpenMonitor();

    /// <summary>打开慢查询与当前操作。</summary>
    void OpenProfiler(string database);

    /// <summary>打开用户与角色。</summary>
    void OpenUsers(string? database, bool roles = false);

    /// <summary>重载对象树(建删集合、改名之后)。</summary>
    Task RefreshTreeAsync(string? database = null);
}
