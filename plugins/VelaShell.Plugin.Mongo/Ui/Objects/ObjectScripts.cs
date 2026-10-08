using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合结构 → mongosh 脚本。对象列表的「复制结构」、详情面板的 DDL 页与新建集合的「将执行的命令」
/// 共用这一份,于是三处写出来的是同一种形状 —— 用户从预览里学到的写法,复制出去也是那个样子。
/// <para>
/// 输出的是**能直接贴进 mongosh 重放**的脚本,而不是 <c>listCollections</c> 原文:
/// 服务器回显里有几样是派生值或内部字段(时序的 <c>bucketMaxSpanSeconds</c>、聚簇索引的 <c>v</c>、
/// 索引的 <c>v</c> / <c>ns</c>),原样回放要么报错、要么和粒度互相冲突,这里统一剥掉。
/// </para>
/// </summary>
internal static class ObjectScripts
{
    /// <summary><c>db.orders</c> 还是 <c>db.getCollection("weird-name")</c>。</summary>
    public static string CollectionRef(string collection) => "db." + MongoWorkspaceViewModel.ShellCollectionRef(collection);

    /// <summary>
    /// <c>db.createCollection("name", { … })</c>,按设计稿 17 的排版:名字与选项各占一段,选项缩进两格。
    /// </summary>
    /// <param name="name">集合名。</param>
    /// <param name="options">建集合的选项;空文档即不带第二个参数。</param>
    public static string CreateCollection(string name, BsonDocument options)
    {
        if (options.ElementCount == 0)
        {
            return $"db.createCollection({BsonText.Quote(name)})";
        }
        var b = new StringBuilder();
        _ = b.Append("db.createCollection(\n  ").Append(BsonText.Quote(name)).Append(",\n");
        _ = b.Append(Indent(BsonText.Pretty(Simplify(options)), "  ")).Append("\n)");
        return b.ToString();
    }

    /// <summary><c>db.createView("name", "source", [ … ])</c>(带排序规则时补第四个参数)。</summary>
    /// <param name="name">视图名。</param>
    /// <param name="viewOn">源集合。</param>
    /// <param name="pipeline">管道。</param>
    /// <param name="collation">排序规则;没有为 <see langword="null" />。</param>
    public static string CreateView(string name, string viewOn, BsonArray pipeline, BsonDocument? collation = null)
    {
        var b = new StringBuilder();
        _ = b.Append("db.createView(\n  ").Append(BsonText.Quote(name)).Append(",\n  ").Append(BsonText.Quote(viewOn)).Append(",\n");
        _ = b.Append(Indent(BsonText.Pretty(Simplify(pipeline)), "  "));
        if (collation is { ElementCount: > 0 })
        {
            _ = b.Append(",\n").Append(Indent(BsonText.Pretty(new BsonDocument("collation", Simplify(collation))), "  "));
        }
        _ = b.Append("\n)");
        return b.ToString();
    }

    /// <summary>一条 <c>createIndex</c>:键模式 + 除 <c>v</c> / <c>key</c> / <c>ns</c> 以外的全部选项(名字保留,重放后索引名不变)。</summary>
    /// <param name="collection">集合名。</param>
    /// <param name="index"><c>listIndexes</c> 回来的一条。</param>
    public static string CreateIndex(string collection, BsonDocument index)
    {
        BsonDocument key = index.GetValue("key", new BsonDocument()).AsBsonDocument;
        BsonDocument options = IndexOptions(index);
        string call = CollectionRef(collection) + ".createIndex(" + BsonText.Literal(Simplify(key));
        return options.ElementCount == 0
            ? call + ")"
            : call + ", " + BsonText.Literal(Simplify(options)) + ")";
    }

    /// <summary>索引的建索引选项(剥掉服务器回显的内部字段)。</summary>
    public static BsonDocument IndexOptions(BsonDocument index)
    {
        var options = new BsonDocument();
        foreach (BsonElement element in index)
        {
            if (element.Name is "v" or "key" or "ns" or "clustered")
            {
                continue;
            }
            _ = options.Add(element);
        }
        return options;
    }

    /// <summary>这条索引要不要出现在脚本里:<c>_id</c> 索引与聚簇索引是建集合时自带的,写出来重放会报"已存在"。</summary>
    public static bool IsReplayable(BsonDocument index) =>
        index.GetValue("name", "").AsString != "_id_" && !index.GetValue("clustered", false).ToBoolean();

    /// <summary>验证规则:<c>db.runCommand({ collMod: …, validator, validationLevel, validationAction })</c>。</summary>
    public static string CollMod(CollectionInfo info)
    {
        var command = new BsonDocument
        {
            { "collMod", info.Name },
            { "validator", info.Validator ?? [] },
            { "validationLevel", info.ValidationLevel },
            { "validationAction", info.ValidationAction }
        };
        return "db.runCommand(" + BsonText.Pretty(Simplify(command)) + ")";
    }

    /// <summary>
    /// <c>listCollections</c> 的 <c>options</c> → 能回放的 <c>createCollection</c> 选项。
    /// 验证规则拆出去单独走 <c>collMod</c>(规则往往很长,和建集合选项搅在一起没法读)。
    /// </summary>
    public static BsonDocument CreationOptions(BsonDocument options)
    {
        var result = new BsonDocument();
        foreach (BsonElement element in options)
        {
            switch (element.Name)
            {
                case "validator" or "validationLevel" or "validationAction" or "viewOn" or "pipeline":
                    continue;
                case "timeseries" when element.Value.IsBsonDocument:
                    {
                        // 给了粒度时 bucketMaxSpanSeconds 是它的派生值;两个都写,6.3 起服务器会拒绝。
                        BsonDocument ts = element.Value.AsBsonDocument.DeepClone().AsBsonDocument;
                        if (ts.Contains("granularity"))
                        {
                            ts.Remove("bucketMaxSpanSeconds");
                            ts.Remove("bucketRoundingSeconds");
                        }
                        _ = result.Add("timeseries", ts);
                        continue;
                    }
                case "clusteredIndex" when element.Value.IsBsonDocument:
                    {
                        BsonDocument clustered = element.Value.AsBsonDocument.DeepClone().AsBsonDocument;
                        clustered.Remove("v");
                        _ = result.Add("clusteredIndex", clustered);
                        continue;
                    }
                default:
                    _ = result.Add(element);
                    continue;
            }
        }
        return result;
    }

    /// <summary>
    /// 一个集合(或视图)的完整结构脚本:建集合 + 每个二级索引 + 验证规则。
    /// </summary>
    /// <param name="info">集合信息。</param>
    /// <param name="indexes">它的索引(视图传空)。</param>
    public static string Ddl(CollectionInfo info, IReadOnlyList<BsonDocument> indexes)
    {
        if (info.Kind == CollectionKind.View)
        {
            BsonDocument? collation = info.Options.TryGetValue("collation", out BsonValue c) && c.IsBsonDocument ? c.AsBsonDocument : null;
            return CreateView(info.Name, info.ViewOn ?? "", info.Pipeline ?? [], collation);
        }
        var lines = new List<string> { CreateCollection(info.Name, CreationOptions(info.Options)) };
        lines.AddRange(indexes.Where(IsReplayable).Select(i => CreateIndex(info.Name, i)));
        if (info.Validator is not null)
        {
            lines.Add(CollMod(info));
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 把能装进 32 位的 <c>Int64</c> 收窄成 <c>Int32</c>:服务器回显的 <c>expireAfterSeconds</c>、
    /// <c>size</c> 是 long,原样写出来是 <c>NumberLong("7776000")</c> —— 能跑,但没人这么写。
    /// </summary>
    public static BsonValue Simplify(BsonValue value) => value switch
    {
        BsonInt64 l when l.Value is >= int.MinValue and <= int.MaxValue => new BsonInt32((int)l.Value),
        BsonDocument doc => new BsonDocument(doc.Select(e => new BsonElement(e.Name, Simplify(e.Value)))),
        BsonArray array => new BsonArray(array.Select(Simplify)),
        _ => value
    };

    /// <inheritdoc cref="Simplify(BsonValue)" />
    public static BsonDocument Simplify(BsonDocument value) => (BsonDocument)Simplify((BsonValue)value);

    /// <inheritdoc cref="Simplify(BsonValue)" />
    public static BsonArray Simplify(BsonArray value) => (BsonArray)Simplify((BsonValue)value);

    private static string Indent(string text, string prefix) =>
        string.Join("\n", text.Split('\n').Select(line => prefix + line));
}
