using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>
/// 一条操作的"查询形状":把条件里的字面值换成 <c>?</c>,只留下字段、运算符与结构
/// (设计稿 15:<c>orders.find</c> + <c>{ customer.level: ?, total: { $gte: ? } }  sort { total: -1 }</c>)。
/// <para>
/// 慢查询要按形状聚合而不是逐条看:同一个接口一天跑上千次,逐条列出来只会把真正的问题淹掉;
/// 按形状一合并,"哪一类查询吃掉了 71% 的慢查询耗时"就一眼可见。
/// </para>
/// <para>
/// 不直接用服务器给的 <c>queryHash</c>:它只覆盖 find / aggregate 这类走查询规划的操作,
/// 更新、删除、count 之类要么没有、要么与 find 混在一个哈希里,而且哈希本身不可读 ——
/// 表格第二行要显示的恰恰是归一化之后的条件。
/// </para>
/// </summary>
/// <param name="Collection">集合名(不含库名)。</param>
/// <param name="Operation">操作名(<c>find</c>、<c>aggregate</c>、<c>updateMany</c>…)。</param>
/// <param name="Filter">归一化后的条件(或管道)。</param>
/// <param name="Sort">排序(原样保留方向);没有为 <see langword="null" />。</param>
/// <param name="Extra">其余影响执行计划的修饰(<c>limit 50</c>);没有为 <see langword="null" />。</param>
internal sealed record QueryShape(string Collection, string Operation, string Filter, string? Sort, string? Extra)
{
    /// <summary>首行:<c>orders.find</c>。</summary>
    public string Title => $"{Collection}.{Operation}";

    /// <summary>次行:条件 + 排序 + 修饰,两个空格隔开。</summary>
    public string Summary
    {
        get
        {
            var builder = new StringBuilder(Filter);
            if (Sort is { Length: > 0 })
            {
                _ = builder.Append("  sort ").Append(Sort);
            }
            if (Extra is { Length: > 0 })
            {
                _ = builder.Append("  ").Append(Extra);
            }
            return builder.ToString();
        }
    }

    /// <summary>分组键(集合 + 操作 + 次行)。</summary>
    public string Key => $"{Collection}\u001f{Operation}\u001f{Summary}";

    /// <summary>
    /// 从一条 <c>system.profile</c> 记录取形状。<c>getmore</c> 取它的 <c>originatingCommand</c> ——
    /// 一次查询的后续批次与首批是同一个形状,应该算进同一组。
    /// </summary>
    /// <param name="entry">profile 记录。</param>
    public static QueryShape FromProfile(BsonDocument entry)
    {
        string op = Str(entry, "op") ?? "command";
        BsonDocument command = Doc(entry, "command") ?? [];
        if (op == "getmore" && Doc(entry, "originatingCommand") is { } origin)
        {
            command = origin;
            op = origin.Contains("find") ? "query" : "command";
        }
        string collection = CollectionOf(entry, command);
        switch (op)
        {
            case "query":
            case "command" when command.Contains("find"):
                return new(collection, "find", Normalize(Doc(command, "filter") ?? []), SortText(Doc(command, "sort")), FindExtra(command));
            case "update":
                return new(collection, command.GetValue("multi", false).ToBoolean() ? "updateMany" : "updateOne",
                    Normalize(Doc(command, "q") ?? []), null, null);
            case "remove":
                return new(collection, command.GetValue("limit", 0).ToDouble() == 1 ? "deleteOne" : "deleteMany",
                    Normalize(Doc(command, "q") ?? []), null, null);
            case "insert":
                return new(collection, "insert", "{}", null, null);
        }
        if (command.Contains("aggregate") && command.GetValue("pipeline", new BsonArray()) is BsonArray pipeline)
        {
            return new(collection, "aggregate", NormalizePipeline(pipeline), null, null);
        }
        if (command.Contains("count"))
        {
            return new(collection, "count", Normalize(Doc(command, "query") ?? []), null, null);
        }
        if (command.Contains("distinct"))
        {
            string key = Str(command, "key") ?? "?";
            return new(collection, $"distinct({key})", Normalize(Doc(command, "query") ?? []), null, null);
        }
        if (command.Contains("findAndModify") || command.Contains("findandmodify"))
        {
            string name = command.GetValue("remove", false).ToBoolean() ? "findOneAndDelete" : "findOneAndUpdate";
            return new(collection, name, Normalize(Doc(command, "query") ?? []), SortText(Doc(command, "sort")), null);
        }
        string verb = command.ElementCount > 0 ? command.GetElement(0).Name : op;
        return new(collection, verb, "{}", null, null);
    }

    /// <summary>
    /// 归一化一份条件:字面值 → <c>?</c>,字段名与运算符原样(键名不加引号,便于扫读)。
    /// <list type="bullet">
    /// <item><c>$and / $or / $nor</c> 的数组逐个展开 —— 它们的结构就是形状本身;</item>
    /// <item><c>$in / $nin / $all</c> 的整个数组记一个 <c>?</c> —— 元素个数不同不该拆成两种形状;</item>
    /// <item><c>$exists</c> 的布尔值保留 —— true 与 false 走的是完全不同的计划;</item>
    /// <item><c>$expr</c> 里按表达式处理:<c>$字段</c> 路径保留,其余字面值打码。</item>
    /// </list>
    /// </summary>
    /// <param name="filter">条件。</param>
    public static string Normalize(BsonValue filter)
    {
        var builder = new StringBuilder();
        WriteQuery(builder, filter);
        return builder.ToString();
    }

    /// <summary>
    /// 归一化一条管道:<c>[$match { day: ? }, $group { _id: $device }]</c>。
    /// <c>$match</c> 按条件处理;其余阶段按表达式处理 —— 字段路径、数字(排序方向、投影开关、
    /// <c>$limit</c>)都是结构,只有字符串常量以外的日期、ObjectId 之类才打码。
    /// </summary>
    /// <param name="pipeline">管道。</param>
    public static string NormalizePipeline(BsonArray pipeline)
    {
        var builder = new StringBuilder("[");
        for (int i = 0; i < pipeline.Count; i++)
        {
            _ = builder.Append(i == 0 ? "" : ", ");
            if (pipeline[i] is not BsonDocument { ElementCount: > 0 } stage)
            {
                _ = builder.Append('?');
                continue;
            }
            BsonElement head = stage.GetElement(0);
            _ = builder.Append(head.Name).Append(' ');
            if (head.Name == "$match")
            {
                WriteQuery(builder, head.Value);
            }
            else
            {
                WriteExpression(builder, head.Value);
            }
        }
        return builder.Append(']').ToString();
    }

    /// <summary>排序文档原样写成一行(<c>{ total: -1 }</c>);方向是形状的一部分,不打码。</summary>
    internal static string? SortText(BsonDocument? sort) =>
        sort is { ElementCount: > 0 } ? Plain(sort) : null;

    /// <summary>键名不加引号、值按 mongosh 字面量的一行文本。</summary>
    internal static string Plain(BsonDocument doc)
    {
        var builder = new StringBuilder("{ ");
        int i = 0;
        foreach (BsonElement element in doc)
        {
            _ = builder.Append(i++ == 0 ? "" : ", ").Append(element.Name).Append(": ").Append(BsonText.Literal(element.Value));
        }
        return builder.Append(" }").ToString();
    }

    private static string? FindExtra(BsonDocument command)
    {
        var parts = new List<string>();
        if (command.TryGetValue("limit", out BsonValue limit) && limit.IsNumeric && limit.ToInt64() != 0)
        {
            // limit 是形状的一部分(设计稿 15 的 products.find 行):limit 50 与不限走的可能是两条计划。
            parts.Add($"limit {Math.Abs(limit.ToInt64())}");
        }
        if (command.TryGetValue("skip", out BsonValue skip) && skip.IsNumeric && skip.ToInt64() != 0)
        {
            parts.Add("skip ?");
        }
        if (command.TryGetValue("hint", out BsonValue hint))
        {
            parts.Add("hint " + (hint is BsonDocument h ? Plain(h) : hint.ToString()));
        }
        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    private static void WriteQuery(StringBuilder b, BsonValue value)
    {
        if (value is not BsonDocument doc)
        {
            _ = b.Append('?');
            return;
        }
        if (doc.ElementCount == 0)
        {
            _ = b.Append("{}");
            return;
        }
        _ = b.Append("{ ");
        int i = 0;
        foreach (BsonElement element in doc)
        {
            _ = b.Append(i++ == 0 ? "" : ", ").Append(element.Name).Append(": ");
            switch (element.Name)
            {
                case "$and" or "$or" or "$nor" when element.Value is BsonArray branches:
                    _ = b.Append('[');
                    for (int j = 0; j < branches.Count; j++)
                    {
                        _ = b.Append(j == 0 ? "" : ", ");
                        WriteQuery(b, branches[j]);
                    }
                    _ = b.Append(']');
                    break;
                case "$exists" when element.Value.IsBoolean:
                    _ = b.Append(element.Value.AsBoolean ? "true" : "false");
                    break;
                case "$expr":
                    WriteExpression(b, element.Value);
                    break;
                default:
                    if (element.Value is BsonDocument nested)
                    {
                        // 运算符文档({ $gte: 5000 })、$elemMatch / $not 的子条件、嵌入文档的整体相等,都继续往里走。
                        WriteQuery(b, nested);
                    }
                    else
                    {
                        _ = b.Append('?');
                    }
                    break;
            }
        }
        _ = b.Append(" }");
    }

    private static void WriteExpression(StringBuilder b, BsonValue value)
    {
        switch (value)
        {
            case BsonDocument doc:
                if (doc.ElementCount == 0)
                {
                    _ = b.Append("{}");
                    return;
                }
                _ = b.Append("{ ");
                int i = 0;
                foreach (BsonElement element in doc)
                {
                    _ = b.Append(i++ == 0 ? "" : ", ").Append(element.Name).Append(": ");
                    if (element.Name == "$match")
                    {
                        // $lookup / $facet 里嵌套的子管道:$match 依旧按条件打码。
                        WriteQuery(b, element.Value);
                    }
                    else
                    {
                        WriteExpression(b, element.Value);
                    }
                }
                _ = b.Append(" }");
                return;
            case BsonArray array:
                _ = b.Append('[');
                for (int j = 0; j < array.Count; j++)
                {
                    _ = b.Append(j == 0 ? "" : ", ");
                    WriteExpression(b, array[j]);
                }
                _ = b.Append(']');
                return;
            case BsonString s:
                // 表达式里的字符串多半是结构($字段路径、$lookup 的 from / as);常量字符串也一并保留,
                // 宁可两个形状分开,也不要把 "$device" 这种关键信息抹成问号。
                _ = b.Append(s.Value.StartsWith('$') ? s.Value : BsonText.Quote(s.Value));
                return;
            case BsonInt32 or BsonInt64 or BsonDouble or BsonDecimal128 or BsonBoolean or BsonNull:
                _ = b.Append(BsonText.Literal(value));
                return;
            default:
                _ = b.Append('?');
                return;
        }
    }

    /// <summary>命令里第一个值是集合名的那些动词。</summary>
    private static readonly string[] CollectionVerbs =
        ["find", "aggregate", "count", "distinct", "findAndModify", "findandmodify", "update", "delete", "insert"];

    /// <summary>命令里与语句无关的会话 / 路由字段(还原 mongosh 语句时去掉)。</summary>
    private static readonly HashSet<string> Plumbing =
    [
        with(StringComparer.Ordinal),
        "lsid", "$clusterTime", "$db", "$readPreference", "txnNumber", "autocommit", "startTransaction",
        "$audit", "$client", "mayBypassWriteBlocking", "cursor", "batchSize", "singleBatch", "comment",
        "maxTimeMS", "readConcern", "writeConcern", "$configTime", "$topologyTime", "shardVersion", "databaseVersion"
    ];

    /// <summary>
    /// 把一条 profile 记录还原成可以粘进查询编辑器的 mongosh 语句,并给出对应的执行计划写法。
    /// <para>
    /// find / aggregate 的执行计划直接在语句末尾接 <c>.explain("executionStats")</c>;
    /// 写操作与 count / distinct 走 <c>db.coll.explain("executionStats").method(...)</c> ——
    /// 在 <c>updateMany(...)</c> 后面接 explain 的写法会先把更新真的执行一遍,那是事故而不是诊断。
    /// </para>
    /// </summary>
    /// <param name="entry">profile 记录。</param>
    /// <returns>语句与执行计划语句。</returns>
    public static (string Statement, string Explain) Rebuild(BsonDocument entry)
    {
        string op = Str(entry, "op") ?? "command";
        BsonDocument command = Doc(entry, "command") ?? [];
        if (op == "getmore" && Doc(entry, "originatingCommand") is { } origin)
        {
            command = origin;
            op = origin.Contains("find") ? "query" : "command";
        }
        string db = $"db.{CollectionRef(CollectionOf(entry, command))}";
        const string verbosity = "\"executionStats\"";
        if (op == "query" || command.Contains("find"))
        {
            var builder = new StringBuilder($"{db}.find({Arg(Doc(command, "filter") ?? [])}");
            if (Doc(command, "projection") is { ElementCount: > 0 } projection)
            {
                _ = builder.Append(", ").Append(BsonText.Literal(projection));
            }
            _ = builder.Append(')');
            if (Doc(command, "sort") is { ElementCount: > 0 } sort)
            {
                _ = builder.Append(".sort(").Append(BsonText.Literal(sort)).Append(')');
            }
            if (command.TryGetValue("hint", out BsonValue hint))
            {
                _ = builder.Append(".hint(").Append(BsonText.Literal(hint)).Append(')');
            }
            if (command.TryGetValue("skip", out BsonValue skip) && skip.IsNumeric && skip.ToInt64() != 0)
            {
                _ = builder.Append(".skip(").Append(skip.ToInt64()).Append(')');
            }
            if (command.TryGetValue("limit", out BsonValue limit) && limit.IsNumeric && limit.ToInt64() != 0)
            {
                _ = builder.Append(".limit(").Append(Math.Abs(limit.ToInt64())).Append(')');
            }
            string find = builder.ToString();
            return (find, $"{find}.explain({verbosity})");
        }
        if (command.TryGetValue("pipeline", out BsonValue pipelineValue) && pipelineValue is BsonArray pipeline && command.Contains("aggregate"))
        {
            string stages = pipeline.Count == 0
                ? "[]"
                : "[\n" + string.Join(",\n", pipeline.Select(static s => "  " + BsonText.Literal(s))) + "\n]";
            string aggregate = $"{db}.aggregate({stages})";
            return (aggregate, $"{aggregate}.explain({verbosity})");
        }
        (string method, string args)? call = op switch
        {
            "update" => (command.GetValue("multi", false).ToBoolean() ? "updateMany" : "updateOne",
                $"{Arg(Doc(command, "q") ?? [])}, {BsonText.Literal(command.GetValue("u", new BsonDocument()))}"),
            "remove" => (command.GetValue("limit", 0).ToDouble() == 1 ? "deleteOne" : "deleteMany", Arg(Doc(command, "q") ?? [])),
            _ when command.Contains("count") => ("countDocuments", Arg(Doc(command, "query") ?? [])),
            _ when command.Contains("distinct") => ("distinct", $"{BsonText.Quote(Str(command, "key") ?? "")}, {Arg(Doc(command, "query") ?? [])}"),
            _ when command.Contains("findAndModify") || command.Contains("findandmodify") => FindAndModify(command),
            _ => null
        };
        if (call is { } c)
        {
            return ($"{db}.{c.method}({c.args})", $"{db}.explain({verbosity}).{c.method}({c.args})");
        }
        var cleaned = new BsonDocument(command.Where(static e => !Plumbing.Contains(e.Name)));
        string run = $"db.runCommand({BsonText.Literal(cleaned)})";
        return (run, $"db.runCommand({{ explain: {BsonText.Literal(cleaned)}, verbosity: {verbosity} }})");
    }

    private static (string, string) FindAndModify(BsonDocument command)
    {
        string query = Arg(Doc(command, "query") ?? []);
        var options = new BsonDocument();
        if (Doc(command, "sort") is { ElementCount: > 0 } sort)
        {
            options["sort"] = sort;
        }
        if (command.GetValue("remove", false).ToBoolean())
        {
            return ("findOneAndDelete", options.ElementCount > 0 ? $"{query}, {BsonText.Literal(options)}" : query);
        }
        if (command.GetValue("upsert", false).ToBoolean())
        {
            options["upsert"] = true;
        }
        string update = BsonText.Literal(command.GetValue("update", new BsonDocument()));
        return ("findOneAndUpdate", options.ElementCount > 0 ? $"{query}, {update}, {BsonText.Literal(options)}" : $"{query}, {update}");
    }

    /// <summary>
    /// 条件参数:单个短条件写一行,多个条件每个顶层键一行(设计稿 15 的样本代码块),
    /// 值仍是一行 —— <c>total: { $gte: 5000 }</c> 拆成三行反而难读。
    /// </summary>
    private static string Arg(BsonDocument filter)
    {
        string flat = BsonText.Literal(filter);
        if (filter.ElementCount <= 1 && flat.Length <= 60)
        {
            return flat;
        }
        return "{\n" + string.Join(",\n", filter.Select(static e => $"  {BsonText.FieldName(e.Name)}: {BsonText.Literal(e.Value)}")) + "\n}";
    }

    /// <summary><c>orders</c> 还是 <c>getCollection("weird-name")</c>。</summary>
    internal static string CollectionRef(string collection) =>
        collection.Length > 0 && (char.IsAsciiLetter(collection[0]) || collection[0] is '_' or '$')
                              && collection.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '$')
            ? collection
            : $"getCollection({BsonText.Quote(collection)})";

    private static string CollectionOf(BsonDocument entry, BsonDocument command)
    {
        foreach (string verb in CollectionVerbs)
        {
            if (command.TryGetValue(verb, out BsonValue v) && v.IsString)
            {
                return v.AsString;
            }
        }
        string ns = Str(entry, "ns") ?? "";
        int dot = ns.IndexOf('.');
        return dot >= 0 ? ns[(dot + 1)..] : ns;
    }

    private static string? Str(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsString ? v.AsString : null;

    private static BsonDocument? Doc(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsBsonDocument ? v.AsBsonDocument : null;
}

/// <summary>按 ESR(等值 → 排序 → 范围)给出的建议索引。</summary>
/// <param name="Keys">建议的索引键。</param>
/// <param name="Equality">等值条件字段。</param>
/// <param name="Sort">只用于排序的字段。</param>
/// <param name="Range">只用于范围的字段。</param>
/// <param name="SortAndRange">同时用于排序与范围的字段。</param>
internal sealed record IndexAdvice(
    BsonDocument Keys,
    IReadOnlyList<string> Equality,
    IReadOnlyList<string> Sort,
    IReadOnlyList<string> Range,
    IReadOnlyList<string> SortAndRange)
{
    /// <summary>索引键的 mongosh 写法(<c>{ "customer.level": 1, total: -1 }</c>)。</summary>
    public string KeysText => BsonText.Literal(Keys);

    /// <summary>
    /// 按 ESR 规则从条件与排序推一个复合索引。
    /// <para>
    /// 等值字段放最前(把扫描范围直接收窄到一段连续的键),排序字段居中(让索引顺序替代内存排序),
    /// 范围字段垫后(范围之后的键无法再用于排序)。同一字段既是排序又是范围时放在排序位上 ——
    /// 它在那个位置同时服务两件事。<c>$or / $expr / $where / $text</c> 推不出单一索引,返回 null。
    /// </para>
    /// </summary>
    /// <param name="filter">条件。</param>
    /// <param name="sort">排序;没有为 null。</param>
    public static IndexAdvice? Suggest(BsonDocument filter, BsonDocument? sort)
    {
        var equality = new List<string>();
        var range = new List<string>();
        if (!Collect(filter, equality, range))
        {
            return null;
        }
        var keys = new BsonDocument();
        foreach (string field in equality)
        {
            keys[field] = 1;
        }
        var sortOnly = new List<string>();
        var both = new List<string>();
        if (sort is not null)
        {
            foreach (BsonElement element in sort)
            {
                if (keys.Contains(element.Name) || element.Value is not (BsonInt32 or BsonInt64 or BsonDouble))
                {
                    // 等值字段在排序里是常量,不占位;{ $meta: "textScore" } 之类不是索引能给的顺序。
                    continue;
                }
                keys[element.Name] = element.Value.ToDouble() < 0 ? -1 : 1;
                (range.Contains(element.Name) ? both : sortOnly).Add(element.Name);
            }
        }
        foreach (string field in range)
        {
            if (!keys.Contains(field))
            {
                keys[field] = 1;
            }
        }
        return keys.ElementCount == 0
            ? null
            : new IndexAdvice(keys, equality, sortOnly, range.Where(f => !both.Contains(f)).ToList(), both);
    }

    /// <summary>这份建议是否已被某个现有索引覆盖(键序与方向一致的前缀,或整体反向)。</summary>
    /// <param name="index">现有索引的 <c>key</c> 文档。</param>
    public bool CoveredBy(BsonDocument index)
    {
        if (index.ElementCount < Keys.ElementCount)
        {
            return false;
        }
        bool same = true;
        bool reversed = true;
        for (int i = 0; i < Keys.ElementCount; i++)
        {
            BsonElement want = Keys.GetElement(i);
            BsonElement have = index.GetElement(i);
            if (want.Name != have.Name || !have.Value.IsNumeric)
            {
                return false;
            }
            double w = want.Value.ToDouble();
            double h = have.Value.ToDouble();
            same &= Math.Sign(w) == Math.Sign(h);
            reversed &= Math.Sign(w) == -Math.Sign(h);
        }
        return same || reversed;
    }

    private static bool Collect(BsonDocument filter, List<string> equality, List<string> range)
    {
        foreach (BsonElement element in filter)
        {
            if (element.Name == "$and" && element.Value is BsonArray parts)
            {
                foreach (BsonValue part in parts)
                {
                    if (part is not BsonDocument doc || !Collect(doc, equality, range))
                    {
                        return false;
                    }
                }
                continue;
            }
            if (element.Name.StartsWith('$'))
            {
                // $or / $nor / $expr / $where / $text:这些推不出一条前缀可用的复合索引。
                return false;
            }
            bool isRange = element.Value is BsonDocument ops
                           && ops.ElementCount > 0
                           && ops.GetElement(0).Name.StartsWith('$')
                           && ops.Names.Any(static n => n is not ("$eq" or "$in"));
            List<string> target = isRange ? range : equality;
            if (!equality.Contains(element.Name) && !range.Contains(element.Name))
            {
                target.Add(element.Name);
            }
        }
        return true;
    }
}
