using System.Globalization;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>索引的形态(设计稿 07「类型」列)。</summary>
internal enum IndexForm
{
    /// <summary>单字段。</summary>
    Single,

    /// <summary>复合。</summary>
    Compound,

    /// <summary>多键(数组字段上的索引)。</summary>
    MultiKey,

    /// <summary>文本。</summary>
    Text,

    /// <summary>地理(2dsphere / 2d)。</summary>
    Geo,

    /// <summary>哈希。</summary>
    Hashed,

    /// <summary>通配符。</summary>
    Wildcard
}

/// <summary>
/// 一条查询的形状:哪些字段做等值、哪些排序、哪些做范围(ESR 规则的输入)。
/// </summary>
/// <param name="Equality">等值字段(<c>status: "paid"</c>、<c>$eq</c>、<c>$in</c>)。</param>
/// <param name="Sort">排序字段与方向。</param>
/// <param name="Range">范围字段(<c>$gt</c> / <c>$lt</c> / <c>$ne</c> / <c>$regex</c> …)。</param>
internal sealed record EsrShape(IReadOnlyList<string> Equality, IReadOnlyList<(string Path, int Direction)> Sort, IReadOnlyList<string> Range)
{
    /// <summary>同形状的查询归为一组的键(等值、范围按名字排序;排序保留顺序与方向)。</summary>
    public string Signature =>
        "E:" + string.Join(",", Equality.Order(StringComparer.Ordinal))
        + "|S:" + string.Join(",", Sort.Select(static s => $"{s.Path}{(s.Direction < 0 ? "-" : "+")}"))
        + "|R:" + string.Join(",", Range.Order(StringComparer.Ordinal));

    /// <summary>形状里出现过的全部字段(去重,按 E → S → R 的顺序)。</summary>
    public IReadOnlyList<string> Fields =>
        [.. Equality.Concat(Sort.Select(static s => s.Path)).Concat(Range).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// 按 ESR 规则(Equality → Sort → Range)排出的建议索引键。
    /// 等值字段 1、排序字段沿用排序方向、范围字段 1;同一字段只出现一次(先到先得)。
    /// </summary>
    public BsonDocument EsrKey()
    {
        var key = new BsonDocument();
        foreach (string field in Equality)
        {
            _ = key.Set(field, 1);
        }
        foreach ((string path, int direction) in Sort)
        {
            if (!key.Contains(path))
            {
                key[path] = direction < 0 ? -1 : 1;
            }
        }
        foreach (string field in Range)
        {
            if (!key.Contains(field))
            {
                key[field] = 1;
            }
        }
        return key;
    }

    /// <summary>某个字段在形状里扮演的角色(新建索引面板的「等值 / 排序 / 范围」小标签)。</summary>
    public string? RoleOf(string field) =>
        Equality.Contains(field) ? "E" : Sort.Any(s => s.Path == field) ? "S" : Range.Contains(field) ? "R" : null;

    /// <summary>
    /// 由筛选条件与排序算形状。<c>$or</c> / <c>$expr</c> / <c>$where</c> / <c>$text</c> 这类没法直接走普通 B 树索引的条件跳过,
    /// <c>$and</c> 展开。什么字段都没有时返回 <see langword="null" />。
    /// </summary>
    public static EsrShape? From(BsonDocument? filter, BsonDocument? sort)
    {
        var equality = new List<string>();
        var range = new List<string>();
        if (filter is not null)
        {
            Classify(filter, equality, range);
        }
        var sorts = new List<(string, int)>();
        if (sort is not null)
        {
            foreach (BsonElement element in sort)
            {
                if (element.Value.IsNumeric)
                {
                    sorts.Add((element.Name, element.Value.ToDouble() < 0 ? -1 : 1));
                }
            }
        }
        foreach (string e in equality)
        {
            _ = range.Remove(e);
        }
        return equality.Count + sorts.Count + range.Count == 0 ? null : new EsrShape(equality, sorts, range);
    }

    private static void Classify(BsonDocument filter, List<string> equality, List<string> range)
    {
        foreach (BsonElement element in filter)
        {
            if (element.Name.StartsWith('$'))
            {
                if (element.Name == "$and" && element.Value is BsonArray parts)
                {
                    foreach (BsonDocument part in parts.OfType<BsonDocument>())
                    {
                        Classify(part, equality, range);
                    }
                }
                continue;
            }
            List<string> target = IsEquality(element.Value) ? equality : range;
            if (!equality.Contains(element.Name) && !target.Contains(element.Name))
            {
                target.Add(element.Name);
            }
        }
    }

    /// <summary>字面量、<c>$eq</c>、<c>$in</c>(只要不夹带范围运算符)算等值。</summary>
    private static bool IsEquality(BsonValue value)
    {
        if (value is not BsonDocument doc || doc.ElementCount == 0 || !doc.GetElement(0).Name.StartsWith('$'))
        {
            return value is not BsonRegularExpression;
        }
        return doc.Names.All(static n => n is "$eq" or "$in");
    }
}

/// <summary>一组同形状的慢查询(来自 <c>system.profile</c>)。</summary>
/// <param name="Shape">形状。</param>
/// <param name="Count">次数。</param>
/// <param name="AverageMillis">平均耗时。</param>
/// <param name="AverageExamined">平均扫描文档数。</param>
/// <param name="AverageReturned">平均返回文档数。</param>
/// <param name="Collscan">是不是全表扫描。</param>
/// <param name="InMemorySort">是不是内存排序。</param>
/// <param name="PlanSummary">最近一次的计划摘要(<c>COLLSCAN</c>、<c>IXSCAN { customer.id: 1 }</c>)。</param>
/// <param name="Filter">代表性的筛选条件(最近一次)。</param>
/// <param name="Sort">代表性的排序。</param>
internal sealed record AdvisorQueryGroup(
    EsrShape Shape,
    int Count,
    double AverageMillis,
    double AverageExamined,
    double AverageReturned,
    bool Collscan,
    bool InMemorySort,
    string PlanSummary,
    BsonDocument Filter,
    BsonDocument? Sort);

/// <summary>现有索引的使用画像(<c>listIndexes</c> + <c>$indexStats</c> + <c>indexSizes</c>)。</summary>
/// <param name="Name">索引名。</param>
/// <param name="Key">键模式。</param>
/// <param name="Spec">完整规格(unique / sparse / partialFilterExpression / hidden …)。</param>
/// <param name="Ops">$indexStats 的 ops;拿不到为 <see langword="null" />。</param>
/// <param name="Since">$indexStats 的统计起点。</param>
/// <param name="Size">索引大小(字节)。</param>
internal sealed record AdvisorIndex(string Name, BsonDocument Key, BsonDocument Spec, long? Ops, DateTime? Since, long Size)
{
    /// <summary>隐藏索引(优化器不用,但照常维护)。</summary>
    public bool Hidden => Spec.GetValue("hidden", false).ToBoolean();

    /// <summary>唯一索引(承担约束,不能当成"冗余"删)。</summary>
    public bool Unique => Spec.GetValue("unique", false).ToBoolean();

    /// <summary>部分 / 稀疏索引(只覆盖一部分文档,不能拿来替代全量索引)。</summary>
    public bool Partial => Spec.Contains("partialFilterExpression") || Spec.GetValue("sparse", false).ToBoolean();
}

/// <summary>建议的种类(设计稿 07「索引建议」的三种卡片)。</summary>
internal enum AdvisorKind
{
    /// <summary>慢查询没有合适的索引:按 ESR 建一个。</summary>
    CreateIndex,

    /// <summary>慢查询用着一个单字段索引但还要内存排序 / 多扫:把它扩成复合索引即可替代。</summary>
    ExtendIndex,

    /// <summary>某索引是另一个索引的前缀:多半冗余。</summary>
    RedundantIndex,

    /// <summary>长期未被使用的索引。</summary>
    UnusedIndex
}

/// <summary>一条索引建议。</summary>
/// <param name="Kind">种类。</param>
/// <param name="Key">建议新建的键(<see cref="AdvisorKind.CreateIndex" /> / <see cref="AdvisorKind.ExtendIndex" />)。</param>
/// <param name="Index">涉及的现有索引(扩展 / 冗余 / 未使用)。</param>
/// <param name="Other">冗余时覆盖它的那个索引。</param>
/// <param name="Queries">依据的慢查询组。</param>
/// <param name="UnusedDays">未使用的天数(从统计起点算)。</param>
internal sealed record AdvisorSuggestion(
    AdvisorKind Kind,
    BsonDocument? Key = null,
    AdvisorIndex? Index = null,
    AdvisorIndex? Other = null,
    AdvisorQueryGroup? Queries = null,
    int UnusedDays = 0);

/// <summary>
/// 索引顾问(设计稿 07 下半部分):读 <c>system.profile</c> 里的慢查询与 <c>$indexStats</c>,给出三类建议 ——
/// 缺索引(按 ESR 规则生成键)、前缀重复、长期未使用。
/// <para>
/// 只给**有证据**的建议:没有慢查询记录就不猜"可能缺索引";统计窗口不满一周就不说"未使用"
/// (服务器昨天才重启,所有索引的 ops 都是个位数,那不叫没人用)。纯函数,单测直接喂数据。
/// </para>
/// </summary>
internal static class IndexAdvisor
{
    /// <summary>「未使用」至少要观察这么多天才算数。</summary>
    public const int UnusedWindowDays = 7;

    /// <summary>一次查询扫描/返回比超过这个数就算"低效"(即使走了索引)。</summary>
    public const int ExaminedRatio = 10;

    /// <summary>
    /// 从 profile 记录里认出值得给建议的慢查询,并按形状归组(次数多的在前)。
    /// 只认 <c>find / aggregate / count / distinct / update / delete</c>;getMore、命令、内部操作跳过。
    /// </summary>
    public static IReadOnlyList<AdvisorQueryGroup> GroupSlowQueries(IEnumerable<BsonDocument> profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var groups = new Dictionary<string, List<(EsrShape Shape, BsonDocument Entry, BsonDocument Filter, BsonDocument? Sort)>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (BsonDocument entry in profile)
        {
            if (!TryExtract(entry, out BsonDocument filter, out BsonDocument? sort))
            {
                continue;
            }
            string plan = entry.GetValue("planSummary", "").ToString() ?? "";
            bool collscan = plan.StartsWith("COLLSCAN", StringComparison.Ordinal);
            bool sortStage = entry.GetValue("hasSortStage", false).ToBoolean();
            double examined = Number(entry, "docsExamined");
            double returned = Number(entry, "nreturned");
            bool inefficient = examined >= 1000 && examined > ExaminedRatio * Math.Max(1, returned);
            if (!collscan && !sortStage && !inefficient)
            {
                continue;
            }
            if (EsrShape.From(filter, sort) is not { } shape)
            {
                continue;
            }
            if (!groups.TryGetValue(shape.Signature, out List<(EsrShape Shape, BsonDocument Entry, BsonDocument Filter, BsonDocument? Sort)>? list))
            {
                list = [];
                groups[shape.Signature] = list;
                order.Add(shape.Signature);
            }
            list.Add((shape, entry, filter, sort));
        }
        return
        [
            .. order.Select(sig =>
                {
                    List<(EsrShape Shape, BsonDocument Entry, BsonDocument Filter, BsonDocument? Sort)> list = groups[sig];
                    (EsrShape Shape, BsonDocument Entry, BsonDocument Filter, BsonDocument? Sort) = list[0];
                    return new AdvisorQueryGroup(
                        Shape,
                        list.Count,
                        list.Average(static x => Number(x.Entry, "millis")),
                        list.Average(static x => Number(x.Entry, "docsExamined")),
                        list.Average(static x => Number(x.Entry, "nreturned")),
                        list.Any(static x => x.Entry.GetValue("planSummary", "").ToString()!.StartsWith("COLLSCAN", StringComparison.Ordinal)),
                        list.Any(static x => x.Entry.GetValue("hasSortStage", false).ToBoolean()),
                        Entry.GetValue("planSummary", "").ToString() ?? "",
                        Filter,
                        Sort);
                })
                .OrderByDescending(static g => g.Count * g.AverageMillis)
        ];
    }

    /// <summary>从一条 profile 记录里取出筛选条件与排序。</summary>
    internal static bool TryExtract(BsonDocument entry, out BsonDocument filter, out BsonDocument? sort)
    {
        filter = [];
        sort = null;
        string op = entry.GetValue("op", "").ToString() ?? "";
        if (!entry.TryGetValue("command", out BsonValue raw) || raw is not BsonDocument command)
        {
            return false;
        }
        switch (op)
        {
            case "query" when command.Contains("find"):
                filter = Doc(command, "filter");
                sort = command.GetValue("sort", BsonNull.Value) as BsonDocument;
                return true;
            case "update" or "remove":
                filter = Doc(command, "q");
                return true;
            case "command" when command.Contains("count"):
                filter = Doc(command, "query");
                return true;
            case "command" when command.Contains("distinct"):
                filter = Doc(command, "query");
                return true;
            case "command" when command.Contains("aggregate") && command.GetValue("pipeline", BsonNull.Value) is BsonArray pipeline:
                {
                    // 只看管道开头的 $match / $sort:后面的阶段吃的是上一阶段的输出,与集合索引无关。
                    foreach (BsonDocument stage in pipeline.OfType<BsonDocument>())
                    {
                        if (stage.TryGetValue("$match", out BsonValue match) && match is BsonDocument m && sort is null && filter.ElementCount == 0)
                        {
                            filter = m;
                            continue;
                        }
                        if (stage.TryGetValue("$sort", out BsonValue s) && s is BsonDocument sd && sort is null)
                        {
                            sort = sd;
                            continue;
                        }
                        break;
                    }
                    return filter.ElementCount > 0 || sort is not null;
                }
            default:
                return false;
        }
    }

    /// <summary>
    /// 出建议。顺序:缺索引 → 可扩展 → 前缀重复 → 未使用。
    /// </summary>
    /// <param name="groups">慢查询组(<see cref="GroupSlowQueries" />)。</param>
    /// <param name="indexes">现有索引。</param>
    /// <param name="now">当前时间(UTC;单测注入)。</param>
    public static IReadOnlyList<AdvisorSuggestion> Advise(IReadOnlyList<AdvisorQueryGroup> groups, IReadOnlyList<AdvisorIndex> indexes, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(indexes);
        var advice = new List<AdvisorSuggestion>();
        var suggested = new HashSet<string>(StringComparer.Ordinal);
        foreach (AdvisorQueryGroup group in groups)
        {
            BsonDocument key = group.Shape.EsrKey();
            if (key.ElementCount == 0 || !suggested.Add(key.ToString()))
            {
                continue;
            }
            // 已经有一个(非隐藏、非部分)索引以建议键为前缀:建议已落实(多半是慢查询之后才建的)。
            if (indexes.Any(i => !i.Hidden && !i.Partial && IsPrefix(key, i.Key)))
            {
                continue;
            }
            AdvisorIndex? extendable = indexes
                .Where(i => i.Name != "_id_" && !i.Hidden && !i.Partial && i.Key.ElementCount < key.ElementCount && IsPrefix(i.Key, key))
                .OrderByDescending(static i => i.Key.ElementCount)
                .FirstOrDefault();
            advice.Add(extendable is null
                ? new AdvisorSuggestion(AdvisorKind.CreateIndex, Key: key, Queries: group)
                : new AdvisorSuggestion(AdvisorKind.ExtendIndex, Key: key, Index: extendable, Queries: group));
        }
        foreach (AdvisorIndex index in indexes)
        {
            if (index.Name == "_id_" || index.Unique || index.Partial || index.Hidden
                || ShapeOf(index.Spec) is not (IndexForm.Single or IndexForm.Compound))
            {
                continue;
            }
            AdvisorIndex? cover = indexes.FirstOrDefault(other =>
                other.Name != index.Name && !other.Hidden && !other.Partial
                && other.Key.ElementCount > index.Key.ElementCount && IsPrefix(index.Key, other.Key));
            if (cover is not null)
            {
                advice.Add(new AdvisorSuggestion(AdvisorKind.RedundantIndex, Index: index, Other: cover));
            }
        }
        foreach (AdvisorIndex index in indexes)
        {
            if (index.Name == "_id_" || index.Hidden || index.Ops is not 0 || index.Since is not { } since)
            {
                continue;
            }
            int days = (int)Math.Floor((now - since.ToUniversalTime()).TotalDays);
            if (days >= UnusedWindowDays && advice.All(a => a.Index?.Name != index.Name))
            {
                advice.Add(new AdvisorSuggestion(AdvisorKind.UnusedIndex, Index: index, UnusedDays: days));
            }
        }
        return advice;
    }

    /// <summary><paramref name="prefix" /> 是不是 <paramref name="key" /> 的前缀(字段与方向都一致;等长也算)。</summary>
    public static bool IsPrefix(BsonDocument prefix, BsonDocument key)
    {
        if (prefix.ElementCount == 0 || prefix.ElementCount > key.ElementCount)
        {
            return false;
        }
        for (int i = 0; i < prefix.ElementCount; i++)
        {
            BsonElement a = prefix.GetElement(i);
            BsonElement b = key.GetElement(i);
            if (a.Name != b.Name || !SameDirection(a.Value, b.Value))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SameDirection(BsonValue a, BsonValue b)
    {
        if (a.IsNumeric && b.IsNumeric)
        {
            return Math.Sign(a.ToDouble()) == Math.Sign(b.ToDouble());
        }
        return a.Equals(b);
    }

    /// <summary>
    /// 默认索引名(与服务器规则一致):<c>字段_值</c> 用下划线连起来 —— <c>status_1_createdAt_-1</c>、<c>note_text</c>。
    /// </summary>
    public static string DefaultName(BsonDocument key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Join("_", key.Select(static e => $"{e.Name}_{ValueText(e.Value)}"));
    }

    private static string ValueText(BsonValue value) => value.BsonType switch
    {
        BsonType.Int32 or BsonType.Int64 => value.ToInt64().ToString(CultureInfo.InvariantCulture),
        BsonType.Double => value.AsDouble.ToString(CultureInfo.InvariantCulture),
        BsonType.String => value.AsString,
        _ => value.ToString() ?? ""
    };

    /// <summary>
    /// 索引的形态。多键要靠执行计划里的 <c>isMultiKey</c> 才知道 —— 键模式本身看不出来,由调用方传进来。
    /// </summary>
    public static IndexForm ShapeOf(BsonDocument spec, bool multiKey = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        BsonDocument key = spec.GetValue("key", new BsonDocument()) as BsonDocument ?? [];
        var values = key.Select(static e => e.Value).ToList();
        if (values.Any(static v => v.IsString && v.AsString == "text"))
        {
            return IndexForm.Text;
        }
        if (values.Any(static v => v.IsString && v.AsString is "2dsphere" or "2d" or "geoHaystack"))
        {
            return IndexForm.Geo;
        }
        if (values.Any(static v => v.IsString && v.AsString == "hashed"))
        {
            return IndexForm.Hashed;
        }
        if (key.Names.Any(static n => n == "$**" || n.EndsWith(".$**", StringComparison.Ordinal)))
        {
            return IndexForm.Wildcard;
        }
        if (multiKey)
        {
            return IndexForm.MultiKey;
        }
        return key.ElementCount > 1 ? IndexForm.Compound : IndexForm.Single;
    }

    /// <summary>
    /// 键在界面上的样子:文本索引的键模式是 <c>{ _fts: "text", _ftsx: 1 }</c>,真正的字段在 <c>weights</c> 里,
    /// 这里还原成 <c>note text</c>;其余原样。返回 (字段, 值) 列表。
    /// </summary>
    public static IReadOnlyList<(string Field, BsonValue Value)> DisplayKeys(BsonDocument spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        BsonDocument key = spec.GetValue("key", new BsonDocument()) as BsonDocument ?? [];
        var list = new List<(string, BsonValue)>();
        foreach (BsonElement element in key)
        {
            if (element.Name == "_fts")
            {
                if (spec.GetValue("weights", BsonNull.Value) is BsonDocument weights)
                {
                    list.AddRange(weights.Select(static w => (w.Name, (BsonValue)"text")));
                }
                continue;
            }
            if (element.Name == "_ftsx")
            {
                continue;
            }
            list.Add((element.Name, element.Value));
        }
        return list;
    }

    /// <summary>
    /// 预估索引大小:文档数 × (各键字段平均值长 + 每条索引项的固定开销)。
    /// WiredTiger 的前缀压缩会让真实值更小,所以只用来给"量级"感,界面上写"≈"。
    /// </summary>
    /// <param name="documents">文档数。</param>
    /// <param name="averageKeyBytes">各键字段平均值长之和。</param>
    public static long EstimateSize(long documents, double averageKeyBytes) =>
        (long)(documents * (averageKeyBytes + 18) * 0.85);

    /// <summary>
    /// 预估构建耗时(秒):按集合数据量扫一遍(约 60 MB/s)加上排序插入(约 30 万键/秒)粗估。
    /// </summary>
    public static double EstimateBuildSeconds(long documents, long dataBytes) =>
        (dataBytes / (60.0 * 1024 * 1024)) + (documents / 300_000.0);

    private static BsonDocument Doc(BsonDocument command, string name) =>
        command.GetValue(name, BsonNull.Value) as BsonDocument ?? [];

    private static double Number(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsNumeric ? v.ToDouble() : 0;
}
