using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>抽样里的一个字段。</summary>
internal sealed class DocumentEditorFieldStat
{
    /// <summary>构造。</summary>
    /// <param name="path">合并路径(数组不带下标:<c>items.sku</c>)。</param>
    public DocumentEditorFieldStat(string path)
    {
        Path = path;
        int dot = path.LastIndexOf('.');
        Parent = dot < 0 ? "" : path[..dot];
        Name = dot < 0 ? path : path[(dot + 1)..];
    }

    /// <summary>合并路径。</summary>
    public string Path { get; }

    /// <summary>父路径(顶层为空串)。</summary>
    public string Parent { get; }

    /// <summary>字段名。</summary>
    public string Name { get; }

    /// <summary>出现在几份文档里。</summary>
    public int Documents { get; set; }

    /// <summary>各类型出现的次数。</summary>
    public Dictionary<BsonKind, int> Kinds { get; } = [];

    /// <summary>主导类型(出现最多的那种;并列取先出现的)。</summary>
    public BsonKind Dominant => Kinds.Count == 0 ? BsonKind.String : Kinds.MaxBy(static k => k.Value).Key;
}

/// <summary>
/// 集合的字段抽样(<c>$sample</c> 1000 份):给"按 Schema 补全缺失字段"与字段名补全用。
/// <para>
/// 只看字段名、出现率与主导类型,不做 Schema 分析页那种值分布 —— 编辑一份文档时要的只是
/// "这个集合里的文档通常还有哪些字段、它们一般是什么类型"。
/// </para>
/// </summary>
internal sealed class DocumentEditorSchema
{
    private readonly Dictionary<string, DocumentEditorFieldStat> _fields;

    private DocumentEditorSchema(int sampled, Dictionary<string, DocumentEditorFieldStat> fields)
    {
        Sampled = sampled;
        _fields = fields;
    }

    /// <summary>抽了几份。</summary>
    public int Sampled { get; }

    /// <summary>全部字段(按合并路径)。</summary>
    public IReadOnlyCollection<DocumentEditorFieldStat> Fields => _fields.Values;

    /// <summary>某字段的出现率(0–1)。</summary>
    public double Ratio(DocumentEditorFieldStat field) => Sampled == 0 ? 0 : (double)field.Documents / Sampled;

    /// <summary>某父路径下的字段,按出现率从高到低。</summary>
    public IEnumerable<DocumentEditorFieldStat> ChildrenOf(string parent) =>
        _fields.Values.Where(f => f.Parent == parent).OrderByDescending(static f => f.Documents).ThenBy(static f => f.Name, StringComparer.Ordinal);

    /// <summary>从一批文档统计。</summary>
    public static DocumentEditorSchema From(IEnumerable<BsonDocument> documents)
    {
        var fields = new Dictionary<string, DocumentEditorFieldStat>(StringComparer.Ordinal);
        int sampled = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BsonDocument document in documents)
        {
            sampled++;
            seen.Clear();
            foreach ((string path, BsonValue value) in BsonPath.Walk(document))
            {
                if (!fields.TryGetValue(path, out DocumentEditorFieldStat? stat))
                {
                    stat = new(path);
                    fields[path] = stat;
                }
                // 数组里的对象会把同一路径走好几遍:文档数按一份算,类型按每次出现算。
                if (seen.Add(path))
                {
                    stat.Documents++;
                }
                BsonKind kind = BsonKinds.Of(value);
                stat.Kinds[kind] = stat.Kinds.GetValueOrDefault(kind) + 1;
            }
        }
        return new(sampled, fields);
    }

    /// <summary>抽样(<c>$sample</c>,带 maxTimeMS;超时或出错由调用方处理)。</summary>
    public static async Task<DocumentEditorSchema> SampleAsync(IMongoCollection<BsonDocument> collection, int size, CancellationToken ct)
    {
        PipelineDefinition<BsonDocument, BsonDocument> pipeline = new[]
        {
            new BsonDocument("$sample", new BsonDocument("size", size))
        };
        var options = new AggregateOptions { MaxTime = TimeSpan.FromSeconds(10), AllowDiskUse = false };
        using IAsyncCursor<BsonDocument> cursor = await collection.AggregateAsync(pipeline, options, ct).ConfigureAwait(false);
        List<BsonDocument> documents = await cursor.ToListAsync(ct).ConfigureAwait(false);
        return From(documents);
    }
}
