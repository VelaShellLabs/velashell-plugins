using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>连上的那台服务器是什么(工具栏右侧的服务器徽章、对象树根行)。</summary>
/// <param name="Version">服务器版本(<c>7.0.14</c>)。</param>
/// <param name="SetName">副本集名;单机 / mongos 为 <see langword="null" />。</param>
/// <param name="Role">当前直连的成员身份:<c>PRIMARY</c> / <c>SECONDARY</c> / <c>STANDALONE</c> / <c>MONGOS</c> / <c>ARBITER</c>。</param>
/// <param name="Me">服务器自报的 <c>host:port</c>。</param>
/// <param name="Members">副本集成员(<c>hello.hosts</c>);单机为空。</param>
/// <param name="IsWritable">当前成员可写(主节点 / 单机 / mongos)。</param>
/// <param name="StorageEngine">存储引擎(<c>wiredTiger</c>)。</param>
/// <param name="MaxWireVersion">线协议版本(按它判断服务器支持哪些命令)。</param>
internal sealed record ServerInfo(
    string Version,
    string? SetName,
    string Role,
    string Me,
    IReadOnlyList<string> Members,
    bool IsWritable,
    string StorageEngine,
    int MaxWireVersion)
{
    /// <summary>工具栏徽章第一行:<c>rs0 · PRIMARY</c>,单机就是 <c>STANDALONE</c>。</summary>
    public string Badge => SetName is { Length: > 0 } set ? $"{set} · {Role}" : Role;

    /// <summary>主版本号(按它挑命令形态:<c>collStats</c> 在 6.2 起改走 <c>$collStats</c>)。</summary>
    public int Major => int.TryParse(Version.Split('.')[0], out int major) ? major : 0;
}

/// <summary>当前用户的一条角色授予。</summary>
/// <param name="Role">角色名。</param>
/// <param name="Database">角色所在库。</param>
internal sealed record RoleGrant(string Role, string Database)
{
    /// <inheritdoc />
    public override string ToString() => $"{Role}@{Database}";
}

/// <summary>
/// 当前用户能做什么(<c>connectionStatus</c> 的 <c>showPrivileges</c>)。
/// <para>
/// 只用来**置灰**界面,不用来拦请求 —— 真正的权限判定永远在服务器那边;
/// 这里猜错的代价只是一个按钮该亮没亮,不是越权。
/// </para>
/// </summary>
/// <param name="User">已认证用户(<c>user@db</c>);匿名为空。</param>
/// <param name="Roles">角色授予。</param>
/// <param name="WritableDatabases">有写类动作的库;<c>"*"</c> 表示任意库。</param>
internal sealed record PrivilegeSummary(string User, IReadOnlyList<RoleGrant> Roles, IReadOnlySet<string> WritableDatabases)
{
    /// <summary>没开认证(或探测失败)时的"全放行"。</summary>
    public static PrivilegeSummary Unrestricted { get; } = new("", [], new HashSet<string>(StringComparer.Ordinal) { "*" });

    /// <summary>在这个库上能不能写。</summary>
    public bool CanWrite(string database) =>
        WritableDatabases.Contains("*") || WritableDatabases.Contains(database);

    /// <summary>有没有任何写权限(决定"写入功能将置灰"那条提示)。</summary>
    public bool CanWriteAnything => WritableDatabases.Count > 0;
}

/// <summary>一个数据库。</summary>
/// <param name="Name">库名。</param>
/// <param name="SizeOnDisk">磁盘占用(字节)。</param>
/// <param name="Empty">是否为空库。</param>
internal sealed record DatabaseInfo(string Name, long SizeOnDisk, bool Empty)
{
    /// <summary>系统库(admin / config / local):对象树里默认不列,列出时沉到用户库之前、默认不展开。</summary>
    public bool IsSystem => IsSystemName(Name);

    /// <summary><paramref name="name" /> 是不是系统库的名字。</summary>
    public static bool IsSystemName(string name) => name is "admin" or "config" or "local";
}

/// <summary>集合的形态(决定对象树里的图标与徽章、能不能编辑)。</summary>
internal enum CollectionKind
{
    /// <summary>普通集合。</summary>
    Collection,

    /// <summary>视图:只读,背后是一条管道。</summary>
    View,

    /// <summary>时序集合。</summary>
    TimeSeries,

    /// <summary>固定集合(capped)。</summary>
    Capped,

    /// <summary>聚簇集合(clustered index)。</summary>
    Clustered,

    /// <summary>系统集合(<c>system.*</c>)。</summary>
    System
}

/// <summary>一个集合(或视图)。</summary>
/// <param name="Database">所在库。</param>
/// <param name="Name">集合名。</param>
/// <param name="Kind">形态。</param>
/// <param name="Options"><c>listCollections</c> 回来的 <c>options</c> 原文。</param>
internal sealed record CollectionInfo(string Database, string Name, CollectionKind Kind, BsonDocument Options)
{
    /// <summary>命名空间 <c>db.coll</c>。</summary>
    public string Namespace => $"{Database}.{Name}";

    /// <summary>视图的源集合。</summary>
    public string? ViewOn => Options.TryGetValue("viewOn", out BsonValue v) && v.IsString ? v.AsString : null;

    /// <summary>视图的管道。</summary>
    public BsonArray? Pipeline => Options.TryGetValue("pipeline", out BsonValue v) && v.IsBsonArray ? v.AsBsonArray : null;

    /// <summary>验证规则(<c>validator</c>);没有则为 <see langword="null" />。</summary>
    public BsonDocument? Validator =>
        Options.TryGetValue("validator", out BsonValue v) && v.IsBsonDocument && v.AsBsonDocument.ElementCount > 0
            ? v.AsBsonDocument
            : null;

    /// <summary><c>validationLevel</c>(缺省 <c>strict</c>)。</summary>
    public string ValidationLevel =>
        Options.TryGetValue("validationLevel", out BsonValue v) && v.IsString ? v.AsString : "strict";

    /// <summary><c>validationAction</c>(缺省 <c>error</c>)。</summary>
    public string ValidationAction =>
        Options.TryGetValue("validationAction", out BsonValue v) && v.IsString ? v.AsString : "error";

    /// <summary>固定集合的字节上限。</summary>
    public long? CappedSize =>
        Kind == CollectionKind.Capped && Options.TryGetValue("size", out BsonValue v) && v.IsNumeric ? v.ToInt64() : null;

    /// <summary>视图与时序集合不能按 <c>_id</c> 原地改。</summary>
    public bool IsEditable => Kind is not (CollectionKind.View or CollectionKind.System);
}

/// <summary>一个 GridFS 存储桶(<c>&lt;bucket&gt;.files</c> + <c>&lt;bucket&gt;.chunks</c>)。</summary>
/// <param name="Database">所在库。</param>
/// <param name="Name">桶名(<c>fs</c>)。</param>
internal sealed record GridFsBucketInfo(string Database, string Name)
{
    /// <summary>文件元数据集合名。</summary>
    public string FilesCollection => $"{Name}.files";

    /// <summary>分块集合名。</summary>
    public string ChunksCollection => $"{Name}.chunks";
}

/// <summary>集合统计(对象树底部信息、对象列表、集合设计的「统计」页)。</summary>
internal sealed record CollectionStats
{
    /// <summary>文档数。</summary>
    public long Count { get; init; }

    /// <summary>数据大小(未压缩,字节)。</summary>
    public long Size { get; init; }

    /// <summary>平均文档大小(字节)。</summary>
    public long AvgObjSize { get; init; }

    /// <summary>存储大小(压缩后,字节)。</summary>
    public long StorageSize { get; init; }

    /// <summary>索引总大小(字节)。</summary>
    public long TotalIndexSize { get; init; }

    /// <summary>索引个数。</summary>
    public int IndexCount { get; init; }

    /// <summary>各索引大小。</summary>
    public IReadOnlyDictionary<string, long> IndexSizes { get; init; } = new Dictionary<string, long>();

    /// <summary>存储引擎名(WiredTiger)。</summary>
    public string Engine { get; init; } = "WiredTiger";

    /// <summary>空间可复用的字节(<c>freeStorageSize</c>)。</summary>
    public long FreeStorageSize { get; init; }

    /// <summary>原文(统计页的"原始 JSON")。</summary>
    public BsonDocument Raw { get; init; } = [];
}

/// <summary>一次查询的输入(网格、树、JSON 三视图与导出共用)。</summary>
internal sealed record FindRequest
{
    /// <summary>库名。</summary>
    public required string Database { get; init; }

    /// <summary>集合名。</summary>
    public required string Collection { get; init; }

    /// <summary>筛选条件。</summary>
    public BsonDocument Filter { get; init; } = [];

    /// <summary>投影;空 = 全部字段。</summary>
    public BsonDocument? Projection { get; init; }

    /// <summary>排序;空 = 自然序。</summary>
    public BsonDocument? Sort { get; init; }

    /// <summary>跳过。</summary>
    public int Skip { get; init; }

    /// <summary>限制。</summary>
    public int Limit { get; init; } = 50;

    /// <summary>索引提示(索引名或键模式);空 = 自动。</summary>
    public BsonValue? Hint { get; init; }

    /// <summary>服务器端超时(毫秒);0 = 不限。</summary>
    public int MaxTimeMs { get; init; }

    /// <summary>排序规则。</summary>
    public BsonDocument? Collation { get; init; }

    /// <summary>
    /// 打在这次操作上的 <c>comment</c>。长查询要能"取消(killOp)",就得先在
    /// <c>currentOp</c> 里认出它 —— 认它靠的就是这个标记。
    /// </summary>
    public string? Comment { get; init; }
}

/// <summary>一次查询的结果。</summary>
/// <param name="Documents">本页文档。</param>
/// <param name="Elapsed">往返耗时。</param>
internal sealed record FindResult(IReadOnlyList<BsonDocument> Documents, TimeSpan Elapsed);
