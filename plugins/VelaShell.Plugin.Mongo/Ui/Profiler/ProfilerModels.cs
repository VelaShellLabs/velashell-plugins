using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>执行计划的大类(慢查询表「计划」列的标签颜色)。</summary>
internal enum SlowPlanKind
{
    /// <summary>其他(EOF、单纯的命令)。</summary>
    Other,

    /// <summary>走了索引。</summary>
    IndexScan,

    /// <summary>走了索引但在内存里排序(橙)。</summary>
    InMemorySort,

    /// <summary>全表扫描(红)。</summary>
    CollectionScan
}

/// <summary>一条 <c>system.profile</c> 记录的要点。</summary>
internal sealed record ProfileEntry
{
    /// <summary>发生时刻(UTC)。</summary>
    public DateTime Time { get; init; }

    /// <summary>操作类型(<c>query</c> / <c>update</c> / <c>command</c> / <c>getmore</c>…)。</summary>
    public string Op { get; init; } = "";

    /// <summary>命名空间。</summary>
    public string Namespace { get; init; } = "";

    /// <summary>查询形状。</summary>
    public required QueryShape Shape { get; init; }

    /// <summary>耗时(毫秒)。</summary>
    public double Millis { get; init; }

    /// <summary>扫描的文档数。</summary>
    public long DocsExamined { get; init; }

    /// <summary>扫描的索引键数。</summary>
    public long KeysExamined { get; init; }

    /// <summary>返回的文档数(写操作取匹配 / 删除数)。</summary>
    public long Returned { get; init; }

    /// <summary>计划摘要(<c>COLLSCAN</c>、<c>IXSCAN { status: 1 }</c>)。</summary>
    public string PlanSummary { get; init; } = "";

    /// <summary>有没有内存排序。</summary>
    public bool HasSortStage { get; init; }

    /// <summary>内存排序的数据量(字节;执行统计里没有为 0)。</summary>
    public long SortBytes { get; init; }

    /// <summary>排序溢写到了磁盘。</summary>
    public bool UsedDisk { get; init; }

    /// <summary>客户端地址。</summary>
    public string Client { get; init; } = "";

    /// <summary>应用名(<c>appName</c>)。</summary>
    public string AppName { get; init; } = "";

    /// <summary>用户。</summary>
    public string User { get; init; } = "";

    /// <summary>是不是 getMore(并进原查询的形状,但不算一次执行)。</summary>
    public bool IsGetMore => Op == "getmore";

    /// <summary>原文(还原语句、取条件与排序用)。</summary>
    public BsonDocument Raw { get; init; } = [];

    /// <summary>计划大类。</summary>
    public SlowPlanKind Plan => ClassifyPlan(PlanSummary, HasSortStage);

    /// <summary>
    /// 元数据 / 诊断类命令(listIndexes、$collStats、dbStats…)。level 2 下它们会被如实记下来 ——
    /// 包括本工具自己的对象树在加载统计 —— 却不是"查询",混进慢查询表只会把真问题挤到下面。
    /// </summary>
    public bool IsDiagnostic
    {
        get
        {
            if (Op != "command" || Raw.GetValue("command", BsonNull.Value) is not BsonDocument { ElementCount: > 0 } command)
            {
                return false;
            }
            string verb = command.GetElement(0).Name;
            if (DiagnosticVerbs.Contains(verb))
            {
                return true;
            }
            if (verb == "count" && PlanSummary.Contains("RECORD_STORE_FAST_COUNT", StringComparison.Ordinal))
            {
                // 不带条件的 count 读的是集合元数据(estimatedDocumentCount),不扫描任何文档。
                return true;
            }
            return verb == "aggregate"
                   && command.GetValue("pipeline", BsonNull.Value) is BsonArray { Count: > 0 } pipeline
                   && pipeline[0] is BsonDocument { ElementCount: > 0 } first
                   && first.GetElement(0).Name is "$collStats" or "$indexStats" or "$currentOp" or "$listSessions" or "$planCacheStats";
        }
    }

    private static readonly HashSet<string> DiagnosticVerbs =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "listIndexes", "listCollections", "listDatabases", "collStats", "dbStats", "profile", "ping", "hello", "isMaster",
        "buildInfo", "serverStatus", "getParameter", "connectionStatus", "killCursors", "endSessions", "explain",
        "replSetGetStatus", "replSetGetConfig", "getCmdLineOpts", "hostInfo", "usersInfo", "rolesInfo", "validate"
    ];

    /// <summary>解析一条记录。</summary>
    public static ProfileEntry Parse(BsonDocument doc)
    {
        long sortBytes = 0;
        bool usedDisk = false;
        if (doc.TryGetValue("execStats", out BsonValue stats) && stats is BsonDocument s)
        {
            FindSort(s, ref sortBytes, ref usedDisk);
        }
        usedDisk |= doc.GetValue("usedDisk", false).ToBoolean();
        long returned = Number(doc, "nreturned");
        if (!doc.Contains("nreturned"))
        {
            returned = Number(doc, "nMatched") + Number(doc, "ndeleted");
        }
        return new()
        {
            Time = doc.TryGetValue("ts", out BsonValue ts) && ts.IsValidDateTime ? ts.ToUniversalTime() : DateTime.MinValue,
            Op = Str(doc, "op"),
            Namespace = Str(doc, "ns"),
            Shape = QueryShape.FromProfile(doc),
            Millis = doc.TryGetValue("millis", out BsonValue ms) && ms.IsNumeric ? ms.ToDouble() : 0,
            DocsExamined = Number(doc, "docsExamined"),
            KeysExamined = Number(doc, "keysExamined"),
            Returned = returned,
            PlanSummary = Str(doc, "planSummary"),
            HasSortStage = doc.GetValue("hasSortStage", false).ToBoolean(),
            SortBytes = sortBytes,
            UsedDisk = usedDisk,
            Client = Str(doc, "client"),
            AppName = Str(doc, "appName"),
            User = Str(doc, "user"),
            Raw = doc
        };
    }

    /// <summary>
    /// 计划大类。COLLSCAN 最严重(红),其次是内存排序(橙)—— 走了索引却还要在内存里排序,
    /// 数据一多就会撞上 100 MB 的排序上限;IDHACK / COUNT_SCAN / DISTINCT_SCAN 都算走索引。
    /// </summary>
    public static SlowPlanKind ClassifyPlan(string summary, bool hasSortStage)
    {
        if (summary.Contains("COLLSCAN", StringComparison.Ordinal))
        {
            return SlowPlanKind.CollectionScan;
        }
        if (hasSortStage)
        {
            return SlowPlanKind.InMemorySort;
        }
        return summary.Contains("IXSCAN", StringComparison.Ordinal)
               || summary.Contains("IDHACK", StringComparison.Ordinal)
               || summary.Contains("COUNT_SCAN", StringComparison.Ordinal)
               || summary.Contains("DISTINCT_SCAN", StringComparison.Ordinal)
               || summary.Contains("EXPRESS", StringComparison.Ordinal)
            ? SlowPlanKind.IndexScan
            : SlowPlanKind.Other;
    }

    /// <summary>
    /// 条件与排序(建议索引的输入)。find 取 filter / sort;聚合取第一个 <c>$match</c> 与紧跟其后的 <c>$sort</c>;
    /// 写操作取 <c>q</c>;count / distinct / findAndModify 取 query。
    /// </summary>
    public (BsonDocument Filter, BsonDocument? Sort) FilterAndSort()
    {
        BsonDocument command = Raw.GetValue("command", new BsonDocument()) as BsonDocument ?? [];
        if (IsGetMore && Raw.GetValue("originatingCommand", BsonNull.Value) is BsonDocument origin)
        {
            command = origin;
        }
        if (command.Contains("q"))
        {
            return (Doc(command, "q") ?? [], null);
        }
        if (command.Contains("filter") || command.Contains("find"))
        {
            return (Doc(command, "filter") ?? [], Doc(command, "sort"));
        }
        if (command.GetValue("pipeline", BsonNull.Value) is BsonArray pipeline)
        {
            // 只有打头的 $match / $sort 能用上索引;排在 $group、$unwind 之后的就与集合上的索引无关了。
            string Head(int i) => i < pipeline.Count && pipeline[i] is BsonDocument { ElementCount: > 0 } s ? s.GetElement(0).Name : "";
            if (Head(0) == "$match" && pipeline[0][0] is BsonDocument match)
            {
                return (match, Head(1) == "$sort" ? pipeline[1][0] as BsonDocument : null);
            }
            BsonDocument? leadingSort = Head(0) == "$sort" ? pipeline[0][0] as BsonDocument : null;
            return (new BsonDocument(), leadingSort);
        }
        return (Doc(command, "query") ?? [], Doc(command, "sort"));
    }

    private static void FindSort(BsonDocument stage, ref long bytes, ref bool disk)
    {
        if (stage.GetValue("stage", "").ToString() is "SORT" or "sort")
        {
            foreach (string key in new[] { "totalDataSizeSorted", "totalDataSizeSortedBytesEstimate" })
            {
                if (stage.TryGetValue(key, out BsonValue v) && v.IsNumeric)
                {
                    bytes = Math.Max(bytes, v.ToInt64());
                }
            }
            disk |= stage.GetValue("usedDisk", false).ToBoolean();
        }
        foreach (string child in new[] { "inputStage", "outerStage", "innerStage" })
        {
            if (stage.TryGetValue(child, out BsonValue c) && c is BsonDocument cd)
            {
                FindSort(cd, ref bytes, ref disk);
            }
        }
        if (stage.TryGetValue("inputStages", out BsonValue list) && list is BsonArray stages)
        {
            foreach (BsonDocument cd in stages.OfType<BsonDocument>())
            {
                FindSort(cd, ref bytes, ref disk);
            }
        }
    }

    private static string Str(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsString ? v.AsString : "";

    private static long Number(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsNumeric ? v.ToInt64() : 0;

    private static BsonDocument? Doc(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsBsonDocument ? v.AsBsonDocument : null;
}

/// <summary>慢查询表的格式化(毫秒 / 秒、比例、计划标签)。</summary>
internal static class ProfilerFormat
{
    /// <summary><c>820 ms</c> / <c>41.2 s</c>。</summary>
    public static string Millis(double ms) => ms < 1000
        ? Math.Round(ms).ToString("0", CultureInfo.InvariantCulture) + " ms"
        : (ms / 1000).ToString(ms < 100_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    /// <summary>
    /// 扫描 : 返回(<c>42 : 1</c>)。比值约到整数;一条都没返回时写成 <c>扫描数 : 0</c> ——
    /// "扫了一百万条、返回零条"本身就是最该看的那种。
    /// </summary>
    public static string Ratio(long examined, long returned) => returned <= 0
        ? $"{BsonText.Grouped(examined)} : 0"
        : $"{Math.Max(0, Math.Round((double)examined / returned)).ToString("0", CultureInfo.InvariantCulture)} : 1";

    /// <summary>扫描与返回之比是否偏高(≥ 10 : 1,或扫了不少却一条没返回)。</summary>
    public static bool RatioHigh(long examined, long returned) =>
        returned <= 0 ? examined >= 100 : (double)examined / returned >= 10;

    /// <summary>计划标签文字。</summary>
    public static string PlanText(SlowPlanKind kind, string summary) => kind switch
    {
        SlowPlanKind.CollectionScan => "COLLSCAN",
        SlowPlanKind.InMemorySort => "SORT",
        SlowPlanKind.IndexScan => summary.Split(' ', 2)[0] is { Length: > 0 } head && head != "IXSCAN" ? head : "IXSCAN",
        _ => summary.Length > 0 ? summary.Split(' ', 2)[0] : "—"
    };

    /// <summary>计划标签的样式类(<c>err</c> / <c>warn</c> / <c>ok</c> / <c>muted</c>)。</summary>
    public static string PlanClass(SlowPlanKind kind) => kind switch
    {
        SlowPlanKind.CollectionScan => "err",
        SlowPlanKind.InMemorySort => "warn",
        SlowPlanKind.IndexScan => "ok",
        _ => "muted"
    };
}

/// <summary>按形状聚合后的一行(设计稿 15 慢查询表)。</summary>
internal sealed class ProfilerShapeRow
{
    /// <summary>由同一形状的记录构造。</summary>
    /// <param name="entries">记录(任意顺序)。</param>
    /// <param name="overallMillis">全部慢操作的总耗时(算占比)。</param>
    public ProfilerShapeRow(IReadOnlyList<ProfileEntry> entries, double overallMillis)
    {
        Entries = entries;
        Shape = entries[0].Shape;
        int runs = entries.Count(static e => !e.IsGetMore);
        // getMore 是同一次查询的后续批次:耗时与扫描量算进来,次数不算 —— 否则平均耗时会被一串几毫秒的批次摊薄。
        Count = runs > 0 ? runs : entries.Count;
        TotalMillis = entries.Sum(static e => e.Millis);
        MaxMillis = entries.Max(static e => e.Millis);
        Examined = entries.Sum(static e => e.DocsExamined);
        Returned = entries.Sum(static e => e.Returned);
        Plan = entries.Max(static e => e.Plan);
        Latest = entries.Where(static e => !e.IsGetMore).MaxBy(static e => e.Time) ?? entries.MaxBy(static e => e.Time)!;
        Share = overallMillis <= 0 ? 0 : TotalMillis / overallMillis;
    }

    /// <summary>形状。</summary>
    public QueryShape Shape { get; }

    /// <summary>记录。</summary>
    public IReadOnlyList<ProfileEntry> Entries { get; }

    /// <summary>执行次数。</summary>
    public int Count { get; }

    /// <summary>总耗时。</summary>
    public double TotalMillis { get; }

    /// <summary>平均耗时。</summary>
    public double AvgMillis => TotalMillis / Math.Max(1, Count);

    /// <summary>最大耗时。</summary>
    public double MaxMillis { get; }

    /// <summary>扫描文档总数。</summary>
    public long Examined { get; }

    /// <summary>返回文档总数。</summary>
    public long Returned { get; }

    /// <summary>最差的计划。</summary>
    public SlowPlanKind Plan { get; }

    /// <summary>最近一次样本。</summary>
    public ProfileEntry Latest { get; }

    /// <summary>占慢查询总耗时的比例。</summary>
    public double Share { get; }

    /// <summary>首行(<c>orders.find</c>)。</summary>
    public string Title => Shape.Title;

    /// <summary>次行(归一化条件)。</summary>
    public string Summary => Shape.Summary;

    /// <summary>次数。</summary>
    public string CountText => BsonText.Grouped(Count);

    /// <summary>平均。</summary>
    public string AvgText => ProfilerFormat.Millis(AvgMillis);

    /// <summary>最大。</summary>
    public string MaxText => ProfilerFormat.Millis(MaxMillis);

    /// <summary>扫描 : 返回。</summary>
    public string RatioText => ProfilerFormat.Ratio(Examined, Returned);

    /// <summary>扫描比偏高(橙字)。</summary>
    public bool RatioWarn => ProfilerFormat.RatioHigh(Examined, Returned);

    /// <summary>有问题的计划(平均耗时转橙)。</summary>
    public bool AvgWarn => Plan is SlowPlanKind.CollectionScan or SlowPlanKind.InMemorySort;

    /// <summary>计划标签。</summary>
    public string PlanText => ProfilerFormat.PlanText(Plan, Latest.PlanSummary);

    /// <summary>计划标签:红。</summary>
    public bool PlanErr => Plan == SlowPlanKind.CollectionScan;

    /// <summary>计划标签:橙。</summary>
    public bool PlanWarn => Plan == SlowPlanKind.InMemorySort;

    /// <summary>计划标签:绿。</summary>
    public bool PlanOk => Plan == SlowPlanKind.IndexScan;

    /// <summary>计划标签:灰(EOF、命令之类)。</summary>
    public bool PlanOther => Plan == SlowPlanKind.Other;
}

/// <summary>逐条模式的一行。</summary>
/// <param name="Entry">记录。</param>
/// <param name="Group">所属形状。</param>
internal sealed record ProfilerEntryRow(ProfileEntry Entry, ProfilerShapeRow Group)
{
    /// <summary>首行(<c>orders.find</c>)。</summary>
    public string Title => Entry.Shape.Title + (Entry.IsGetMore ? " · getMore" : "");

    /// <summary>次行:这一条的真实条件(不打码)。</summary>
    public string Summary
    {
        get
        {
            (BsonDocument filter, BsonDocument? sort) = Entry.FilterAndSort();
            string text = QueryShape.Plain(filter);
            if (filter.ElementCount == 0)
            {
                text = "{}";
            }
            return sort is { ElementCount: > 0 } ? $"{text}  sort {QueryShape.Plain(sort)}" : text;
        }
    }

    /// <summary>时间(本地 <c>HH:mm:ss</c>)。</summary>
    public string TimeText => Entry.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>耗时。</summary>
    public string MillisText => ProfilerFormat.Millis(Entry.Millis);

    /// <summary>扫描 : 返回。</summary>
    public string RatioText => ProfilerFormat.Ratio(Entry.DocsExamined, Entry.Returned);

    /// <summary>扫描比偏高。</summary>
    public bool RatioWarn => ProfilerFormat.RatioHigh(Entry.DocsExamined, Entry.Returned);

    /// <summary>计划标签。</summary>
    public string PlanText => ProfilerFormat.PlanText(Entry.Plan, Entry.PlanSummary);

    /// <summary>计划标签:红。</summary>
    public bool PlanErr => Entry.Plan == SlowPlanKind.CollectionScan;

    /// <summary>计划标签:橙。</summary>
    public bool PlanWarn => Entry.Plan == SlowPlanKind.InMemorySort;

    /// <summary>计划标签:绿。</summary>
    public bool PlanOk => Entry.Plan == SlowPlanKind.IndexScan;

    /// <summary>计划标签:灰。</summary>
    public bool PlanOther => Entry.Plan == SlowPlanKind.Other;

    /// <summary>有问题的计划(耗时转橙)。</summary>
    public bool MillisWarn => Entry.Plan is SlowPlanKind.CollectionScan or SlowPlanKind.InMemorySort;
}

/// <summary>当前操作表的一行。</summary>
/// <param name="OpId">opid 原值(killOp 要原样传回去:单机是整数,mongos 是 <c>shard:opid</c> 字符串)。</param>
/// <param name="OpIdText">opid 文字。</param>
/// <param name="Type">类型(<c>query</c> / <c>getmore</c> / <c>update</c> / <c>command</c>)。</param>
/// <param name="Namespace">命名空间。</param>
/// <param name="Summary">命令摘要。</param>
/// <param name="Client">客户端。</param>
/// <param name="Seconds">已运行秒数。</param>
/// <param name="Ratio">进度条比例(相对当前最长的那条)。</param>
internal sealed record ProfilerOpRow(
    BsonValue OpId,
    string OpIdText,
    string Type,
    string Namespace,
    string Summary,
    string Client,
    double Seconds,
    double Ratio)
{
    /// <summary>运行超过 10 秒(整行淡红 + 红色进度条与按钮)。</summary>
    public bool IsLong => Seconds > 10;

    /// <summary>已运行(<c>41.2 s</c>)。</summary>
    public string ElapsedText => Seconds.ToString(Seconds < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";

    /// <summary>进度条颜色令牌。</summary>
    public string BarToken => IsLong ? "VelaError" : "MongoChart1";
}
