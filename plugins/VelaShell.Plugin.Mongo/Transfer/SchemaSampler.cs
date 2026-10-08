using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// 抽样 schema:导出字段表(设计稿 19「来自 Schema 抽样」)与导入的默认目标类型都从这里来。
/// <para>
/// 嵌套文档按需**展开**成 <c>customer.name</c> / <c>customer.level</c> 这样的叶子路径(CSV 一列一个叶子);
/// 数组**不展开** —— 数组里的元素个数每份文档都不同,展开成 <c>items.0.sku</c> 列只会得到一张参差不齐的表,
/// 所以数组整体是一个字段,导出时写成 JSON 字符串。
/// </para>
/// </summary>
internal static class SchemaSampler
{
    /// <summary>分析一批文档。</summary>
    /// <param name="documents">文档。</param>
    /// <param name="flatten">嵌套文档展开成叶子路径。</param>
    /// <param name="maxDepth">最多展开几层。</param>
    public static IReadOnlyList<XferField> Analyze(IEnumerable<BsonDocument> documents, bool flatten, int maxDepth = 4)
    {
        var order = new List<string>();
        var kinds = new Dictionary<string, Dictionary<BsonKind, int>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (BsonDocument document in documents)
        {
            total++;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Visit(document, null, 0);

            void Visit(BsonDocument doc, string? prefix, int depth)
            {
                foreach (BsonElement element in doc)
                {
                    string path = BsonPath.Join(prefix, element.Name);
                    if (flatten && depth < maxDepth && TransferText.IsNestedDocument(element.Value))
                    {
                        Visit(element.Value.AsBsonDocument, path, depth + 1);
                        continue;
                    }
                    if (!kinds.TryGetValue(path, out Dictionary<BsonKind, int>? perKind))
                    {
                        perKind = [];
                        kinds[path] = perKind;
                        order.Add(path);
                        counts[path] = 0;
                    }
                    BsonKind kind = BsonKinds.Of(element.Value);
                    perKind[kind] = perKind.GetValueOrDefault(kind) + 1;
                    if (seen.Add(path))
                    {
                        counts[path]++;
                    }
                }
            }
        }
        // _id 永远在第一列(与 mongoexport、网格一致),其余按第一次出现的顺序。
        IEnumerable<string> ordered = order.Where(static p => p == "_id").Concat(order.Where(static p => p != "_id"));
        return
        [
            .. ordered.Select(path => new XferField(
                path,
                [
                    .. kinds[path].OrderBy(static kv => kv.Key == BsonKind.Null ? 1 : 0)
                        .ThenByDescending(static kv => kv.Value)
                        .Select(static kv => kv.Key)
                ],
                counts[path],
                total))
        ];
    }

    /// <summary>
    /// 从集合里抽样(<c>$match</c> + <c>$sample</c>)。视图同样支持 <c>$sample</c>。
    /// 抽样失败(权限、超时)时退回 <c>find().limit()</c>:字段表宁可不随机,也不能是空的。
    /// </summary>
    public static async Task<IReadOnlyList<BsonDocument>> SampleAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument? filter,
        int size,
        CancellationToken cancellationToken)
    {
        var stages = new List<BsonDocument>();
        if (filter is { ElementCount: > 0 })
        {
            stages.Add(new BsonDocument("$match", filter));
        }
        stages.Add(new BsonDocument("$sample", new BsonDocument("size", size)));
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await collection.AggregateAsync<BsonDocument>(
                stages.ToArray(),
                new AggregateOptions { MaxTime = TimeSpan.FromSeconds(15), AllowDiskUse = true },
                cancellationToken).ConfigureAwait(false);
            return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException)
        {
            return await collection.Find(filter ?? []).Limit(size).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>从 $jsonSchema 里取一个字段声明的类型(导入的默认目标类型优先听它的)。</summary>
    public static BsonKind? KindFromSchema(BsonDocument? schemaProperty)
    {
        if (schemaProperty is null)
        {
            return null;
        }
        BsonValue? type = schemaProperty.TryGetValue("bsonType", out BsonValue bsonType)
            ? bsonType
            : schemaProperty.TryGetValue("type", out BsonValue jsonType) ? jsonType : null;
        string? name = type switch
        {
            BsonString s => s.Value,
            BsonArray a => a.FirstOrDefault(static v => v.IsString && v.AsString != "null")?.AsString,
            _ => null
        };
        return name switch
        {
            "string" => BsonKind.String,
            "int" => BsonKind.Int32,
            "long" => BsonKind.Int64,
            "double" or "number" => BsonKind.Double,
            "decimal" => BsonKind.Decimal128,
            "bool" or "boolean" => BsonKind.Boolean,
            "date" => BsonKind.Date,
            "objectId" => BsonKind.ObjectId,
            "object" => BsonKind.Object,
            "array" => BsonKind.Array,
            _ => null
        };
    }
}
