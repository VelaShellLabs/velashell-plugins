using System.Collections.ObjectModel;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>对象树节点的种类。</summary>
internal enum NodeKind
{
    /// <summary>连接分组(对象树根上的一节,不属于任何连接)。</summary>
    Group,

    /// <summary>连接(根)。</summary>
    Connection,

    /// <summary>数据库。</summary>
    Database,

    /// <summary>分组文件夹(集合 / 视图 / GridFS 存储桶 / 用户与角色 / 存储函数)。</summary>
    Folder,

    /// <summary>集合(含时序、固定、聚簇)。</summary>
    Collection,

    /// <summary>视图。</summary>
    View,

    /// <summary>GridFS 桶。</summary>
    Bucket,

    /// <summary>用户。</summary>
    User,

    /// <summary>自定义角色。</summary>
    Role,

    /// <summary>存储函数(<c>system.js</c> 里的一条)。</summary>
    Function,

    /// <summary>"加载中…" / 错误占位。</summary>
    Placeholder
}

/// <summary>分组文件夹的种类。</summary>
internal enum FolderKind
{
    /// <summary>集合。</summary>
    Collections,

    /// <summary>视图。</summary>
    Views,

    /// <summary>GridFS 存储桶。</summary>
    Buckets,

    /// <summary>用户与角色。</summary>
    Users,

    /// <summary>存储函数。</summary>
    Functions
}

/// <summary>
/// 对象树的一个节点。树被**拍平**成一张虚拟化列表来画(见 <see cref="MongoWorkspaceViewModel.VisibleNodes" />):
/// 缩进由 <see cref="Depth" /> 决定,选中底色贯穿整行 —— TreeView 的缩进画在选中底色之外,
/// 与设计稿那种"整行高亮 + 左侧强调条"对不上。
/// </summary>
internal sealed class TreeNode : ObservableObject
{
    private string _name;

    /// <summary>构造。</summary>
    /// <param name="kind">种类。</param>
    /// <param name="name">显示名。</param>
    /// <param name="depth">缩进层级。</param>
    /// <param name="parent">父行。</param>
    /// <param name="owner">所属连接;不给就随父行(只有根行要给)。</param>
    public TreeNode(NodeKind kind, string name, int depth, TreeNode? parent, ConnectionEntry? owner = null)
    {
        Kind = kind;
        _name = name;
        Depth = depth;
        Parent = parent;
        Owner = owner ?? parent?.Owner;
    }

    /// <summary>这一行属于哪条连接(根上可以挂好几条)。</summary>
    public ConnectionEntry? Owner { get; }

    /// <summary>它那条连接此刻的会话;没连着为 <see langword="null" />。</summary>
    public MongoSession? Session => Owner?.Session;

    /// <summary>连接行的色点:<c>ok</c> 连着、<c>connecting</c> 正在连、<c>err</c> 没连上、<c>off</c> 没连。</summary>
    public string DotClass
    {
        get;
        set => SetProperty(ref field, value);
    } = "off";

    /// <summary>种类。</summary>
    public NodeKind Kind { get; }

    /// <summary>显示名。</summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>层级(0 = 连接)。</summary>
    public int Depth { get; }

    /// <summary>父节点。</summary>
    public TreeNode? Parent { get; }

    /// <summary>所属库(连接节点为空)。</summary>
    public string Database { get; init; } = "";

    /// <summary>文件夹种类(仅 <see cref="NodeKind.Folder" />)。</summary>
    public FolderKind Folder { get; init; }

    /// <summary>集合信息(集合 / 视图节点)。</summary>
    public CollectionInfo? Collection { get; init; }

    /// <summary>GridFS 桶(桶节点)。</summary>
    public GridFsBucketInfo? Bucket { get; init; }

    /// <summary>子节点。</summary>
    public ObservableCollection<TreeNode> Children { get; } = [];

    /// <summary>子节点是否已加载(库节点惰性加载)。</summary>
    public bool IsLoaded { get; set; }

    /// <summary>能不能展开(文件夹、库、连接)。</summary>
    public bool IsExpandable => Kind is NodeKind.Group or NodeKind.Connection or NodeKind.Database or NodeKind.Folder;

    /// <summary>展开状态。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ChevronKey), nameof(IconToken), nameof(IsEmphasized));
            }
        }
    }

    /// <summary>右侧小字(大小 / 计数 / 版本)。</summary>
    public string Meta
    {
        get;
        set => SetProperty(ref field, value);
    } = "";

    /// <summary>徽章文字(<c>TTL</c> / <c>时序</c> / <c>固定</c>);没有为 <see langword="null" />。</summary>
    public string? Tag
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasTag));
            }
        }
    }

    /// <summary>徽章配色类(<c>warn</c> / <c>info</c> / <c>muted</c>)。</summary>
    public string TagClass
    {
        get;
        set => SetProperty(ref field, value);
    } = "muted";

    /// <summary>有没有徽章。</summary>
    public bool HasTag => !string.IsNullOrEmpty(Tag);

    /// <summary>左侧缩进(设计稿:连接 8、库 22、文件夹 36、对象 50 —— 每层 14)。</summary>
    public double Indent => 8 + (Depth * 14);

    /// <summary>折叠箭头;叶子节点为空(留位不画)。</summary>
    public string? ChevronKey => IsExpandable ? (IsExpanded ? "Mongo.chevron-down" : "Mongo.chevron-right") : null;

    /// <summary>行高:连接根行 30、其余 26(设计稿 01 的对象树)。</summary>
    public double RowHeight => Kind == NodeKind.Connection ? 30 : 26;

    /// <summary>是不是连接根行(画状态圆点而不是图标)。</summary>
    public bool IsConnection => Kind == NodeKind.Connection;

    /// <summary>名字加粗、用主文字色(连接行、展开的库)。</summary>
    public bool IsEmphasized => Kind == NodeKind.Connection || (Kind == NodeKind.Database && IsExpanded);

    /// <summary>图标。</summary>
    public string IconKey => Kind switch
    {
        NodeKind.Database => "Mongo.database",
        NodeKind.Folder or NodeKind.Group => "Mongo.folder",
        NodeKind.Collection when Collection?.Kind == CollectionKind.TimeSeries => "Mongo.chart-no-axes-column",
        NodeKind.Collection => "Mongo.table-2",
        NodeKind.View => "Mongo.eye",
        NodeKind.Bucket => "Mongo.hard-drive",
        NodeKind.User => "Mongo.user",
        NodeKind.Role => "Mongo.shield",
        NodeKind.Function => "Mongo.square-code",
        NodeKind.Placeholder => "Mongo.loader-circle",
        _ => "Mongo.leaf"
    };

    /// <summary>图标颜色令牌。</summary>
    public string IconToken => Kind switch
    {
        NodeKind.Database => IsExpanded ? "VelaWarning" : "VelaTextMuted",
        NodeKind.Folder or NodeKind.Group => "VelaFileFolderIcon",
        NodeKind.Collection or NodeKind.View or NodeKind.User or NodeKind.Role or NodeKind.Function => "VelaInfo",
        NodeKind.Bucket => "VelaWarning",
        _ => "VelaTextMuted"
    };

    /// <summary>命名空间(集合 / 视图 / 桶)。</summary>
    public string? Namespace => Collection?.Namespace ?? (Bucket is { } b ? $"{b.Database}.{b.Name}" : null);
}
