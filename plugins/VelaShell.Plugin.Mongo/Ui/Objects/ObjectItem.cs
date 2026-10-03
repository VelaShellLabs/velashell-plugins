using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>对象列表里一行的种类(比 <see cref="CollectionKind" /> 多一个"GridFS 桶",少一个系统集合)。</summary>
internal enum ObjectKind
{
    /// <summary>普通集合。</summary>
    Collection,

    /// <summary>时序集合。</summary>
    TimeSeries,

    /// <summary>固定集合。</summary>
    Capped,

    /// <summary>聚簇集合。</summary>
    Clustered,

    /// <summary>视图。</summary>
    View,

    /// <summary>GridFS 桶(<c>X.files</c> + <c>X.chunks</c> 合成一行)。</summary>
    Bucket
}

/// <summary>对象列表的三种视图(工具行右侧那组分段)。</summary>
internal enum ObjectViewMode
{
    /// <summary>大图标网格。</summary>
    Grid,

    /// <summary>列表。</summary>
    List,

    /// <summary>详情表(默认,设计稿 12)。</summary>
    Details
}

/// <summary>
/// 对象列表的一行。列表先出名字与种类,统计数字由视图模型并发取回后陆续补上 ——
/// 所以数字列都是可空的:<see langword="null" /> = 还没取到(显示空白),视图 = 永远没有(显示「—」)。
/// </summary>
internal sealed class ObjectItem : ObservableObject
{
    private readonly Loc _loc;
    private bool _isTtl;

    /// <summary>一个集合或视图。</summary>
    public ObjectItem(CollectionInfo info, Loc loc)
    {
        _loc = loc;
        Info = info;
        Name = info.Name;
        Database = info.Database;
        Kind = info.Kind switch
        {
            CollectionKind.TimeSeries => ObjectKind.TimeSeries,
            CollectionKind.Capped => ObjectKind.Capped,
            CollectionKind.Clustered => ObjectKind.Clustered,
            CollectionKind.View => ObjectKind.View,
            _ => ObjectKind.Collection
        };
    }

    /// <summary>一个 GridFS 桶。</summary>
    public ObjectItem(GridFsBucketInfo bucket, Loc loc)
    {
        _loc = loc;
        Bucket = bucket;
        Name = bucket.Name;
        Database = bucket.Database;
        Kind = ObjectKind.Bucket;
    }

    /// <summary>所在库。</summary>
    public string Database { get; }

    /// <summary>集合名 / 视图名 / 桶名(操作用的那个名字)。</summary>
    public string Name { get; }

    /// <summary>
    /// 显示名:桶写成 <c>fs.files / chunks</c>(设计稿把两个集合合成一行)。名称列只有 170px,
    /// 写全放不下时只写桶名(<c>avatars</c>)—— 类型列的 GridFS 徽章已经说明了它是一对集合。
    /// </summary>
    public string DisplayName => Bucket is { } b
        ? b.Name.Length <= 6 ? $"{b.FilesCollection} / chunks" : b.Name
        : Name;

    /// <summary>种类。</summary>
    public ObjectKind Kind { get; }

    /// <summary>集合信息;桶为 <see langword="null" />。</summary>
    public CollectionInfo? Info { get; }

    /// <summary>桶信息;集合与视图为 <see langword="null" />。</summary>
    public GridFsBucketInfo? Bucket { get; }

    /// <summary>是不是视图(数字列一律「—」)。</summary>
    public bool IsView => Kind == ObjectKind.View;

    /// <summary>是不是能装文档、能导入 / 清空的那种集合(不是视图、不是桶)。</summary>
    public bool IsCollection => Kind is ObjectKind.Collection or ObjectKind.TimeSeries or ObjectKind.Capped or ObjectKind.Clustered;

    /// <summary>`db.x` 的命名空间。</summary>
    public string Namespace => $"{Database}.{Name}";

    // ── 外观 ─────────────────────────────────────────────────────────────

    /// <summary>图标(按种类,与对象树一致)。</summary>
    public string IconKey => Kind switch
    {
        ObjectKind.TimeSeries => "Mongo.chart-no-axes-column",
        ObjectKind.View => "Mongo.eye",
        ObjectKind.Bucket => "Mongo.hard-drive",
        ObjectKind.Clustered => "Mongo.layers",
        _ => "Mongo.table-2"
    };

    /// <summary>图标本色(未选中时)。</summary>
    public string IconToken => Kind == ObjectKind.Bucket ? "VelaWarning" : "VelaInfo";

    /// <summary>实际画的图标色:选中行改用强调色(设计稿 12 的 events 行)。</summary>
    public string ShownIconToken => IsSelected ? "VelaAccent" : IconToken;

    /// <summary>是否为当前选中行(由视图模型维护,用于强调色的名字与图标)。</summary>
    public bool IsSelected
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(ShownIconToken));
            }
        }
    }

    /// <summary>带 TTL 索引(普通集合的类型列改成橙色 TTL 徽章)。</summary>
    public bool IsTtl
    {
        get => _isTtl;
        set
        {
            if (SetProperty(ref _isTtl, value))
            {
                RaisePropertiesChanged(nameof(TypeText), nameof(TagClass), nameof(HasTag));
            }
        }
    }

    /// <summary>类型列的文字(<c>集合</c> / <c>时序</c> / <c>固定 1 GB</c> / <c>GridFS</c> / <c>TTL</c> / <c>视图</c>)。</summary>
    public string TypeText => Kind switch
    {
        ObjectKind.TimeSeries => _loc["Tag_TimeSeries"],
        ObjectKind.Capped => Info?.CappedSize is { } size ? _loc.Format("Obj_TagCappedSize", ShortBytes(size)) : _loc["Tag_Capped"],
        ObjectKind.Clustered => _loc["Tag_Clustered"],
        ObjectKind.View => _loc["Obj_TagView"],
        ObjectKind.Bucket => "GridFS",
        _ when _isTtl => _loc["Tag_Ttl"],
        _ => _loc["Obj_KindCollectionShort"]
    };

    /// <summary>类型徽章的配色类;空 = 不画徽章,只写一行灰字(普通集合)。</summary>
    public string TagClass => Kind switch
    {
        ObjectKind.TimeSeries => "info",
        ObjectKind.Bucket => "warn",
        ObjectKind.Capped or ObjectKind.Clustered or ObjectKind.View => "muted",
        _ when _isTtl => "warn",
        _ => ""
    };

    /// <summary>类型列画不画徽章。</summary>
    public bool HasTag => TagClass.Length > 0;

    /// <summary>完整的种类名(<c>时序集合</c>),详情面板副标题与状态栏用。</summary>
    public string KindName => _loc[Kind switch
    {
        ObjectKind.TimeSeries => "Obj_KindTimeSeries",
        ObjectKind.Capped => "Obj_KindCapped",
        ObjectKind.Clustered => "Obj_KindClustered",
        ObjectKind.View => "Obj_KindView",
        ObjectKind.Bucket => "Obj_KindBucket",
        _ => "Obj_KindCollection"
    }];

    // ── 验证列 ───────────────────────────────────────────────────────────

    /// <summary>有验证规则。</summary>
    public bool HasValidator => Info?.Validator is not null;

    /// <summary>验证级别(<c>strict</c> / <c>moderate</c>)。</summary>
    public string ValidationLevel => Info?.ValidationLevel ?? "";

    /// <summary>盾牌颜色:strict 绿、moderate 蓝、off 灰(与设计稿 12 一致)。</summary>
    public string ValidationToken => ValidationLevel switch
    {
        "strict" => "VelaStatusConnected",
        "moderate" => "VelaInfo",
        _ => "VelaTextMuted"
    };

    // ── 统计 ─────────────────────────────────────────────────────────────

    /// <summary>索引(<c>listIndexes</c> 原文;取统计时顺带取回,DDL 与 TTL 徽章用)。</summary>
    public IReadOnlyList<BsonDocument>? Indexes { get; set; }

    /// <summary>统计原文(集合;桶是 files 那一份)。</summary>
    public CollectionStats? Stats { get; private set; }

    /// <summary>桶的 chunks 统计。</summary>
    public CollectionStats? ChunkStats { get; private set; }

    /// <summary>统计取回来了没有。</summary>
    public bool HasStats => Stats is not null;

    /// <summary>文档数(桶 = 文件数)。</summary>
    public long? Count { get; private set; }

    /// <summary>平均文档大小(桶 = 平均文件大小)。</summary>
    public long? AvgSize { get; private set; }

    /// <summary>数据大小(未压缩)。</summary>
    public long? DataSize { get; private set; }

    /// <summary>存储大小(压缩后)。</summary>
    public long? StorageSize { get; private set; }

    /// <summary>索引个数。</summary>
    public int? IndexCount { get; private set; }

    /// <summary>索引大小。</summary>
    public long? IndexSize { get; private set; }

    /// <summary>时序集合的 bucket 数(<c>storageStats.timeseries.bucketCount</c>)。</summary>
    public long? BucketCount =>
        Stats?.Raw.TryGetValue("timeseries", out BsonValue ts) == true && ts.IsBsonDocument
        && ts.AsBsonDocument.TryGetValue("bucketCount", out BsonValue n) && n.IsNumeric
            ? n.ToInt64()
            : null;

    /// <summary>文档数列:一千万以下写全(<c>1,284,902</c>),再大就缩写(<c>42.7M</c>)—— 与设计稿同一口径。</summary>
    public string CountText => IsView ? "—" : Count is { } n ? (n >= 10_000_000 ? BsonText.Count(n) : BsonText.Grouped(n)) : "";

    /// <summary>平均大小列。</summary>
    public string AvgSizeText => Size(AvgSize);

    /// <summary>数据大小列。</summary>
    public string DataSizeText => Size(DataSize);

    /// <summary>存储大小列。</summary>
    public string StorageSizeText => Size(StorageSize);

    /// <summary>索引个数列。</summary>
    public string IndexCountText => IsView ? "—" : IndexCount?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>索引大小列。</summary>
    public string IndexSizeText => Size(IndexSize);

    /// <summary>
    /// 填上一个集合的统计。时序集合的 <c>storageStats.count</c> 是 0(文档在桶里),
    /// 文档数改用 <paramref name="estimatedCount" />。
    /// </summary>
    public void SetStats(CollectionStats stats, long? estimatedCount = null)
    {
        Stats = stats;
        Count = stats.Count > 0 ? stats.Count : estimatedCount ?? stats.Count;
        DataSize = stats.Size;
        AvgSize = stats.AvgObjSize > 0 ? stats.AvgObjSize : Count > 0 ? stats.Size / Count : 0;
        StorageSize = stats.StorageSize;
        IndexCount = stats.IndexCount;
        IndexSize = stats.TotalIndexSize;
        RaiseStats();
    }

    /// <summary>
    /// 填上一个桶的统计:文件数取 files,数据量 = files + chunks,平均大小按 chunks / 文件数
    /// (一份"文件"的大小落在 chunks 里,files 只是元数据)。
    /// </summary>
    public void SetBucketStats(CollectionStats files, CollectionStats chunks)
    {
        Stats = files;
        ChunkStats = chunks;
        Count = files.Count;
        DataSize = files.Size + chunks.Size;
        AvgSize = files.Count > 0 ? chunks.Size / files.Count : 0;
        StorageSize = files.StorageSize + chunks.StorageSize;
        IndexCount = files.IndexCount + chunks.IndexCount;
        IndexSize = files.TotalIndexSize + chunks.TotalIndexSize;
        RaiseStats();
    }

    /// <summary>
    /// 重拉列表时先沿用上一轮的数字(同名同种类的行),新统计到了再覆盖 ——
    /// 否则每次切回标签,整列数字都会先清空再一格格冒出来。
    /// </summary>
    public void CarryOver(ObjectItem previous)
    {
        if (previous.Kind != Kind || previous.Stats is null)
        {
            return;
        }
        Stats = previous.Stats;
        ChunkStats = previous.ChunkStats;
        Count = previous.Count;
        AvgSize = previous.AvgSize;
        DataSize = previous.DataSize;
        StorageSize = previous.StorageSize;
        IndexCount = previous.IndexCount;
        IndexSize = previous.IndexSize;
        _isTtl = previous._isTtl;
        RaiseStats();
    }

    private void RaiseStats() => RaisePropertiesChanged(
        nameof(Stats), nameof(HasStats), nameof(Count), nameof(AvgSize), nameof(DataSize), nameof(StorageSize),
        nameof(IndexCount), nameof(IndexSize), nameof(CountText), nameof(AvgSizeText), nameof(DataSizeText),
        nameof(StorageSizeText), nameof(IndexCountText), nameof(IndexSizeText), nameof(BucketCount));

    private string Size(long? bytes) => IsView ? "—" : bytes is { } b ? BsonText.Bytes(b) : "";

    /// <summary><c>1.0 GB</c> → <c>1 GB</c>:徽章里那半格宽度很贵。</summary>
    internal static string ShortBytes(long bytes)
    {
        string text = BsonText.Bytes(bytes);
        return text.Replace(".0 ", " ", StringComparison.Ordinal);
    }

    /// <summary>排序键(列名见 <see cref="ObjectsTabViewModel.SortColumn" />)。</summary>
    internal IComparable SortKey(string column) => column switch
    {
        "Type" => TypeText,
        "Count" => Count ?? -1,
        "AvgSize" => AvgSize ?? -1,
        "DataSize" => DataSize ?? -1,
        "StorageSize" => StorageSize ?? -1,
        "Indexes" => (long)(IndexCount ?? -1),
        "IndexSize" => IndexSize ?? -1,
        "Validation" => HasValidator ? ValidationLevel : "",
        _ => DisplayName
    };
}

/// <summary>详情面板的一个小节(<c>时序选项</c> / <c>统计</c> / <c>最近活动</c>)。</summary>
/// <param name="Title">小节标题。</param>
/// <param name="Facts">键值行。</param>
internal sealed record ObjectFactSection(string Title, IReadOnlyList<ObjectFact> Facts);

/// <summary>
/// 详情面板的一行键值。值可以后补(写入速率要两次采样、最近文档要再查一次),所以是可观察的。
/// </summary>
internal sealed class ObjectFact : ObservableObject
{
    private string _value;
    private string _token;

    /// <summary>构造。</summary>
    /// <param name="label">标签。</param>
    /// <param name="value">值。</param>
    /// <param name="token">值的颜色令牌。</param>
    public ObjectFact(string label, string value, string token = "VelaTextSecondary")
    {
        Label = label;
        _value = value;
        _token = token;
    }

    /// <summary>标签。</summary>
    public string Label { get; }

    /// <summary>值。</summary>
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    /// <summary>值的颜色令牌。</summary>
    public string Token
    {
        get => _token;
        set => SetProperty(ref _token, value);
    }
}

/// <summary>权限页的一个动作(<c>find</c>、<c>insert</c>…);写类动作单独上色。</summary>
/// <param name="Name">动作名。</param>
/// <param name="IsWrite">是不是写类动作。</param>
internal sealed record ObjectPrivilege(string Name, bool IsWrite);
