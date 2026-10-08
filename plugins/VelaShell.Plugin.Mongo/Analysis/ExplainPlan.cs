using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>阶段卡片的语气(决定描边颜色)。</summary>
internal enum ExplainTone
{
    /// <summary>普通(VelaBorderPrimary)。</summary>
    Normal,

    /// <summary>获胜路径上用到了索引(绿色描边)。</summary>
    Good,

    /// <summary>值得注意(橙色):FETCH 后丢弃大量文档、内存排序。</summary>
    Warning,

    /// <summary>糟糕(红色):全集合扫描。</summary>
    Bad
}

/// <summary>阶段卡片上的一行指标。</summary>
/// <param name="Label">标签(<c>keysExamined</c>、<c>方向</c>)。</param>
/// <param name="Value">值。</param>
/// <param name="Warn">值用警告色。</param>
internal sealed record ExplainMetric(string Label, string Value, bool Warn = false);

/// <summary>
/// 执行计划里的一个阶段(IXSCAN / FETCH / SORT / LIMIT / PROJECTION / $group …)。
/// </summary>
internal sealed class ExplainStage
{
    /// <summary>阶段名(<c>IXSCAN</c>、<c>$group</c>)。</summary>
    public required string Name { get; init; }

    /// <summary>原文(树视图与原始 JSON 用)。</summary>
    public required BsonDocument Raw { get; init; }

    /// <summary>在树里的深度(根为 0)。</summary>
    public int Depth { get; init; }

    /// <summary>返回的文档数。</summary>
    public long? NReturned { get; init; }

    /// <summary>扫描的索引键数。</summary>
    public long? KeysExamined { get; init; }

    /// <summary>读出的文档数。</summary>
    public long? DocsExamined { get; init; }

    /// <summary>工作单元数。</summary>
    public long? Works { get; init; }

    /// <summary>累计耗时(含下游,毫秒估计)。</summary>
    public long? CumulativeMs { get; init; }

    /// <summary>本阶段自己的耗时(累计减去输入阶段的累计)。</summary>
    public long? OwnMs { get; set; }

    /// <summary>索引名。</summary>
    public string? IndexName { get; init; }

    /// <summary>索引键。</summary>
    public BsonDocument? KeyPattern { get; init; }

    /// <summary>扫描方向(<c>forward</c> / <c>backward</c>)。</summary>
    public string? Direction { get; init; }

    /// <summary>扫描边界(<c>{ status: ["paid","paid"] }</c>)。</summary>
    public string? Bounds { get; init; }

    /// <summary>本阶段的过滤条件(FETCH / COLLSCAN 上的 filter)。</summary>
    public BsonDocument? Filter { get; init; }

    /// <summary>是否已到结尾(<c>isEOF</c>);LIMIT 提前结束时为 false。</summary>
    public bool? IsEof { get; init; }

    /// <summary>子阶段(OR / SORT_MERGE 之类有多个输入)。</summary>
    public IReadOnlyList<ExplainStage> Children { get; init; } = [];

    /// <summary>是不是访问路径(读索引或集合的叶子阶段)。</summary>
    public bool IsAccess => Name is "IXSCAN" or "COLLSCAN" or "IDHACK" or "COUNT_SCAN" or "DISTINCT_SCAN" or "TEXT_MATCH" or "GEO_NEAR_2DSPHERE"
        or "EXPRESS_IXSCAN" or "EXPRESS_CLUSTERED_IXSCAN" or "CLUSTERED_IXSCAN" or "EOF";

    /// <summary>读出后被过滤丢弃的文档数(FETCH 带 filter 时)。</summary>
    public long Discarded => DocsExamined is { } examined && NReturned is { } returned && Filter is not null ? Math.Max(0, examined - returned) : 0;

    /// <summary>选择率(返回 / 读入);读入不明为 <see langword="null" />。</summary>
    public double? Selectivity
    {
        get
        {
            long? input = DocsExamined ?? KeysExamined;
            return input is > 0 && NReturned is { } returned ? Math.Min(1, (double)returned / input.Value) : null;
        }
    }

    /// <summary>卡片语气。</summary>
    public ExplainTone Tone => Name switch
    {
        "COLLSCAN" => ExplainTone.Bad,
        "SORT" => ExplainTone.Warning,
        _ when IsAccess && Name != "EOF" => ExplainTone.Good,
        _ when Discarded > 0 && Selectivity < 0.8 => ExplainTone.Warning,
        _ => ExplainTone.Normal
    };

    /// <summary>卡片上的一段说明文字(索引名 + 边界、filter、limitAmount、投影…)。</summary>
    public string Detail
    {
        get
        {
            if (IndexName is { } index)
            {
                return Bounds is { Length: > 0 } bounds ? $"{index}\n{bounds}" : index;
            }
            if (Filter is { } filter)
            {
                return "filter: " + BsonText.Literal(filter);
            }
            foreach (string key in (string[])["limitAmount", "skipAmount"])
            {
                if (Raw.TryGetValue(key, out BsonValue amount))
                {
                    return $"{key}: {amount}";
                }
            }
            foreach (string key in (string[])["sortPattern", "transformBy", "sortKey"])
            {
                if (Raw.TryGetValue(key, out BsonValue pattern) && pattern is BsonDocument d)
                {
                    return key == "transformBy" ? BsonText.Literal(d) : $"{key}: {BsonText.Literal(d)}";
                }
            }
            if (Name.StartsWith('$') && Raw.TryGetValue(Name, out BsonValue body))
            {
                return BsonText.Literal(body);
            }
            return "";
        }
    }

    /// <summary>链上的短名(<c>IXSCAN status_1_createdAt_-1</c>)。</summary>
    public string ShortName => IndexName is { } index ? $"{Name} {index}" : Name;
}

/// <summary>一个候选计划(获胜的与被拒的)。</summary>
/// <param name="Winner">是不是获胜计划。</param>
/// <param name="Chain">阶段链(<c>IXSCAN status_1_createdAt_-1 → FETCH → LIMIT</c>)。</param>
/// <param name="Works">试运行的工作单元数。</param>
/// <param name="NReturned">试运行返回数。</param>
/// <param name="Score">规划器打分。</param>
internal sealed record ExplainCandidate(bool Winner, string Chain, long? Works, long? NReturned, double? Score)
{
    /// <summary>有没有试运行数据(只有 allPlansExecution 才有)。</summary>
    public bool HasStats => Works is not null;
}

/// <summary>一条优化建议(按 ESR:等值 → 排序 → 范围)。</summary>
/// <param name="ReasonKey">原因文案键。</param>
/// <param name="ReasonArgument">原因文案参数(涉及的字段)。</param>
/// <param name="Keys">建议的索引键。</param>
/// <param name="ExpectedKeys">预计扫描键数;估不出为 <see langword="null" />。</param>
internal sealed record ExplainAdvice(string ReasonKey, string ReasonArgument, BsonDocument Keys, long? ExpectedKeys);

/// <summary>
/// 一份 explain 输出的解读(设计稿 14):把嵌套的 inputStage 树拉成一条**线性阶段流**(访问路径在左、根在右),
/// 算出每个阶段自己的耗时、选择率、丢弃数,汇总扫描 / 返回比与内存排序,
/// 再按 ESR 规则对照查询本身给出一条索引建议。
/// <para>
/// 认三种形态:find 的 <c>queryPlanner + executionStats</c>;aggregate 的 <c>stages[0].$cursor</c> 包裹
/// (其后是 <c>$group</c> / <c>$sort</c> 这些管道阶段);分片集群的 <c>shards[]</c>(取第一个分片,尽力而为)。
/// SBE 引擎的 <c>queryPlan</c> 也按经典形状读。认不出的字段一概忽略 —— 解读失败不该让原始 JSON 也看不到。
/// </para>
/// </summary>
internal sealed class ExplainPlan
{
    /// <summary>原文。</summary>
    public required BsonDocument Raw { get; init; }

    /// <summary>命名空间。</summary>
    public string? Namespace { get; init; }

    /// <summary>是不是聚合。</summary>
    public bool IsAggregate { get; init; }

    /// <summary>有没有执行统计(executionStats / allPlansExecution)。</summary>
    public bool HasExecutionStats { get; init; }

    /// <summary>线性阶段流:按执行顺序(访问路径在前)。</summary>
    public IReadOnlyList<ExplainStage> Stages { get; init; } = [];

    /// <summary>树(根在前,深度优先)—— 树视图用。</summary>
    public IReadOnlyList<ExplainStage> Tree { get; init; } = [];

    /// <summary>候选计划(获胜的排第一)。</summary>
    public IReadOnlyList<ExplainCandidate> Candidates { get; init; } = [];

    /// <summary>总耗时(毫秒)。</summary>
    public long? TotalMs { get; init; }

    /// <summary>返回的文档数。</summary>
    public long? NReturned { get; init; }

    /// <summary>扫描的索引键。</summary>
    public long? TotalKeysExamined { get; init; }

    /// <summary>读出的文档。</summary>
    public long? TotalDocsExamined { get; init; }

    /// <summary>有没有内存排序。</summary>
    public bool HasInMemorySort => Stages.Any(static s => s.Name is "SORT" or "$sort");

    /// <summary>查询层(而不是 $group 之后的管道)做了内存排序 —— 只有这种才是索引能救的。</summary>
    public bool QueryLayerSort => Stages.Any(static s => s.Name == "SORT");

    /// <summary>排序由索引提供(查询要排序、计划里却没有内存 SORT)。</summary>
    public bool SortFromIndex { get; init; }

    /// <summary>是不是全集合扫描。</summary>
    public bool IsCollectionScan => Stages.Any(static s => s.Name == "COLLSCAN");

    /// <summary>覆盖查询(只读索引、没有 FETCH)。</summary>
    public bool IsCovered => Stages.Any(static s => s.Name == "IXSCAN") && !Stages.Any(static s => s.Name is "FETCH" or "COLLSCAN");

    /// <summary>查询层($cursor)交给管道的文档数;find 与 <see cref="NReturned" /> 相同。</summary>
    public long? QueryReturned { get; init; }

    /// <summary>
    /// 扫描 / 返回比(扫描取键与文档中较大者;返回 0 时按 1 算)。聚合按查询层的输出算 ——
    /// $group 把一千多份压成二十几份是它的本分,不该算成"扫描得太多"。
    /// </summary>
    public double? ExaminedRatio
    {
        get
        {
            long examined = Math.Max(TotalKeysExamined ?? 0, TotalDocsExamined ?? 0);
            return (QueryReturned ?? NReturned) is { } returned && (TotalKeysExamined is not null || TotalDocsExamined is not null)
                ? (double)examined / Math.Max(1, returned)
                : null;
        }
    }

    /// <summary>获胜计划的摘要(<c>IXSCAN status_1_createdAt_-1</c>)—— 状态条与网格状态行用。</summary>
    public string Summary
    {
        get
        {
            ExplainStage? access = Stages.FirstOrDefault(static s => s.IsAccess);
            return access?.ShortName ?? Stages.FirstOrDefault()?.Name ?? "";
        }
    }

    /// <summary>优化建议;没有为 <see langword="null" />。</summary>
    public ExplainAdvice? Advice { get; private set; }

    /// <summary>被解释的查询的筛选条件(从 explain 的 command 里取)。</summary>
    public BsonDocument? QueryFilter { get; init; }

    /// <summary>被解释的查询的排序。</summary>
    public BsonDocument? QuerySort { get; init; }

    /// <summary>解析一份 explain 输出。</summary>
    public static ExplainPlan Parse(BsonDocument explain)
    {
        var command = explain.GetValue("command", null) as BsonDocument;
        BsonDocument queryPlanner;
        BsonDocument? executionStats;
        var pipelineStages = new List<ExplainStage>();
        bool aggregate = false;

        if (explain.GetValue("stages", null) is BsonArray stages && stages.Count > 0)
        {
            // aggregate:第一个阶段是 $cursor(里面是查询层的 queryPlanner / executionStats),其余是管道阶段。
            aggregate = true;
            BsonDocument first = stages[0].AsBsonDocument;
            BsonDocument cursor = first.GetValue("$cursor", null) as BsonDocument ?? [];
            queryPlanner = cursor.GetValue("queryPlanner", null) as BsonDocument ?? [];
            executionStats = cursor.GetValue("executionStats", null) as BsonDocument;
            foreach (BsonDocument stage in stages.Skip(first.Contains("$cursor") ? 1 : 0).OfType<BsonDocument>())
            {
                string name = stage.Names.FirstOrDefault(static n => n.StartsWith('$')) ?? "?";
                pipelineStages.Add(new ExplainStage
                {
                    Name = name,
                    Raw = stage,
                    NReturned = Long(stage, "nReturned"),
                    CumulativeMs = Long(stage, "executionTimeMillisEstimate")
                });
            }
        }
        else
        {
            aggregate = command?.Contains("aggregate") == true;
            queryPlanner = explain.GetValue("queryPlanner", null) as BsonDocument ?? [];
            executionStats = explain.GetValue("executionStats", null) as BsonDocument;
        }

        BsonDocument? winning = Unwrap(queryPlanner.GetValue("winningPlan", null) as BsonDocument);
        BsonDocument? executionRoot = Unwrap(executionStats?.GetValue("executionStages", null) as BsonDocument);
        BsonDocument? root = executionRoot ?? winning;

        // 树:管道阶段在上(最后一个是根),查询层的阶段树挂在最下面那个管道阶段之下。
        var tree = new List<ExplainStage>();
        for (int p = pipelineStages.Count - 1; p >= 0; p--)
        {
            ExplainStage source = pipelineStages[p];
            pipelineStages[p] = new ExplainStage
            {
                Name = source.Name,
                Raw = source.Raw,
                Depth = pipelineStages.Count - 1 - p,
                NReturned = source.NReturned,
                CumulativeMs = source.CumulativeMs
            };
            tree.Add(pipelineStages[p]);
        }
        ExplainStage? rootStage = root is null ? null : Build(root, pipelineStages.Count, tree);
        var linear = new List<ExplainStage>();
        for (ExplainStage? s = rootStage; s is not null; s = s.Children.FirstOrDefault())
        {
            linear.Add(s);
        }
        linear.Reverse();
        // 本阶段耗时 = 自己的累计 − 输入阶段的累计(explain 给的是含下游的累计估计)。
        for (int i = 0; i < linear.Count; i++)
        {
            long? cumulative = linear[i].CumulativeMs;
            long? input = i > 0 ? linear[i - 1].CumulativeMs : 0;
            linear[i].OwnMs = cumulative is { } c ? Math.Max(0, c - (input ?? 0)) : null;
        }
        long? lastCumulative = linear.LastOrDefault()?.CumulativeMs;
        foreach (ExplainStage stage in pipelineStages)
        {
            stage.OwnMs = stage.CumulativeMs is { } c ? Math.Max(0, c - (lastCumulative ?? 0)) : null;
            lastCumulative = stage.CumulativeMs ?? lastCumulative;
            linear.Add(stage);
        }

        BsonDocument? filter = null;
        BsonDocument? sort = null;
        if (command is not null)
        {
            if (command.GetValue("pipeline", null) is BsonArray pipeline)
            {
                // 只看管道开头那几个能下推到查询层的阶段:$group 之后的 $sort 排的是分组结果,索引帮不上。
                foreach (BsonDocument stage in pipeline.OfType<BsonDocument>())
                {
                    if (stage.GetValue("$match", null) is BsonDocument match && filter is null)
                    {
                        filter = match;
                    }
                    else if (stage.GetValue("$sort", null) is BsonDocument sortStage && sort is null)
                    {
                        sort = sortStage;
                    }
                    else if (!stage.Contains("$limit") && !stage.Contains("$skip"))
                    {
                        break;
                    }
                }
            }
            else
            {
                filter = (command.GetValue("filter", null) ?? command.GetValue("query", null)) as BsonDocument;
                sort = command.GetValue("sort", null) as BsonDocument;
            }
        }
        filter ??= queryPlanner.GetValue("parsedQuery", null) as BsonDocument;

        long? totalMs = executionStats is null ? null : Long(executionStats, "executionTimeMillis");
        if (pipelineStages.LastOrDefault()?.CumulativeMs is { } pipelineMs && (totalMs is null || pipelineMs > totalMs))
        {
            totalMs = pipelineMs;
        }
        long? returned = pipelineStages.Count > 0 && pipelineStages[^1].NReturned is { } last ? last
            : executionStats is null ? null : Long(executionStats, "nReturned");

        bool sortFromIndex = sort is { ElementCount: > 0 } && !linear.Any(static s => s.Name == "SORT")
                             && linear.Any(static s => s.Name is "IXSCAN" or "EXPRESS_IXSCAN")
                             && (!aggregate || !pipelineStages.Any(static s => s.Name == "$sort"));

        var plan = new ExplainPlan
        {
            Raw = explain,
            Namespace = queryPlanner.GetValue("namespace", null)?.ToString(),
            IsAggregate = aggregate,
            HasExecutionStats = executionStats is not null,
            Stages = linear,
            Tree = tree,
            Candidates = BuildCandidates(queryPlanner, executionStats, winning),
            TotalMs = totalMs,
            NReturned = returned,
            QueryReturned = executionStats is null ? null : Long(executionStats, "nReturned"),
            TotalKeysExamined = executionStats is null ? null : Long(executionStats, "totalKeysExamined"),
            TotalDocsExamined = executionStats is null ? null : Long(executionStats, "totalDocsExamined"),
            SortFromIndex = sortFromIndex,
            QueryFilter = filter,
            QuerySort = sort
        };
        plan.Advice = Advise(plan);
        return plan;
    }

    /// <summary>
    /// 分片(<c>SHARD_MERGE</c> / <c>SINGLE_SHARD</c> + <c>shards[]</c>)取第一个分片的计划;
    /// SBE 的 <c>{ queryPlan, slotBasedPlan }</c> 取 <c>queryPlan</c>。
    /// </summary>
    private static BsonDocument? Unwrap(BsonDocument? stage)
    {
        if (stage is null)
        {
            return null;
        }
        if (stage.GetValue("queryPlan", null) is BsonDocument queryPlan)
        {
            return Unwrap(queryPlan);
        }
        if (stage.GetValue("shards", null) is BsonArray shards && shards.Count > 0 && shards[0] is BsonDocument shard)
        {
            BsonDocument? inner = Unwrap((shard.GetValue("executionStages", null) ?? shard.GetValue("winningPlan", null)) as BsonDocument);
            if (inner is null)
            {
                return stage;
            }
            // 合并阶段本身留作根,第一个分片的计划挂在它下面 —— 线性流里看得到"先在分片上扫,再在 mongos 合并"。
            var merged = new BsonDocument(stage.Where(static e => e.Name != "shards"))
            {
                ["inputStage"] = inner
            };
            if (!merged.Contains("stage"))
            {
                merged["stage"] = "SHARD_MERGE";
            }
            return merged;
        }
        return stage;
    }

    private static ExplainStage Build(BsonDocument raw, int depth, List<ExplainStage> tree)
    {
        var children = new List<BsonDocument>();
        if (raw.GetValue("inputStage", null) is BsonDocument input)
        {
            children.Add(input);
        }
        if (raw.GetValue("inputStages", null) is BsonArray inputs)
        {
            children.AddRange(inputs.OfType<BsonDocument>());
        }
        if (raw.GetValue("outerStage", null) is BsonDocument outer)
        {
            children.Add(outer);
        }
        if (raw.GetValue("innerStage", null) is BsonDocument inner)
        {
            children.Add(inner);
        }
        int index = tree.Count;
        tree.Add(null!);
        var built = children.Select(c => Build(c, depth + 1, tree)).ToList();
        var stage = new ExplainStage
        {
            Name = raw.GetValue("stage", "?").ToString() ?? "?",
            Raw = raw,
            Depth = depth,
            NReturned = Long(raw, "nReturned"),
            KeysExamined = Long(raw, "keysExamined"),
            DocsExamined = Long(raw, "docsExamined"),
            Works = Long(raw, "works"),
            CumulativeMs = Long(raw, "executionTimeMillisEstimate"),
            IndexName = raw.GetValue("indexName", null)?.ToString(),
            KeyPattern = raw.GetValue("keyPattern", null) as BsonDocument,
            Direction = raw.GetValue("direction", null)?.ToString(),
            Bounds = raw.GetValue("indexBounds", null) is BsonDocument bounds ? FormatBounds(bounds) : null,
            Filter = raw.GetValue("filter", null) as BsonDocument,
            IsEof = raw.TryGetValue("isEOF", out BsonValue eof) ? eof.ToBoolean() : null,
            Children = built
        };
        tree[index] = stage;
        return stage;
    }

    /// <summary>
    /// 索引边界:<c>{ status: ["paid", "paid"], createdAt: [MaxKey, 2026-08-27] }</c>。
    /// 服务器把日期写成 <c>new Date(毫秒)</c>,这里换回人看得懂的日期;±∞ 写成 MaxKey / MinKey。
    /// </summary>
    internal static string FormatBounds(BsonDocument bounds)
    {
        IEnumerable<string> parts = bounds.Elements.Select(static e =>
        {
            string ranges = e.Value is BsonArray array
                ? string.Join(", ", array.Select(static r => Readable(r.ToString() ?? "")))
                : Readable(e.Value.ToString() ?? "");
            return $"{e.Name}: {ranges}";
        });
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string Readable(string range)
    {
        string text = System.Text.RegularExpressions.Regex.Replace(range, @"new Date\((-?\d+)\)", static m =>
        {
            long ms = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (ms >= 253402300799999L)
            {
                return "MaxKey";
            }
            if (ms <= -62135596800000L)
            {
                return "MinKey";
            }
            DateTime at = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            return at.TimeOfDay == TimeSpan.Zero
                ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        });
        return text.Replace("\", \"", "\",\"", StringComparison.Ordinal)
            .Replace("inf.0", "∞", StringComparison.Ordinal);
    }

    private static List<ExplainCandidate> BuildCandidates(BsonDocument queryPlanner, BsonDocument? executionStats, BsonDocument? winning)
    {
        var candidates = new List<ExplainCandidate>();
        var all = executionStats?.GetValue("allPlansExecution", null) as BsonArray;
        BsonArray rejected = queryPlanner.GetValue("rejectedPlans", null) as BsonArray ?? [];
        if (winning is null)
        {
            return candidates;
        }
        string winnerChain = Chain(winning);
        BsonDocument? winnerStats = all?.OfType<BsonDocument>()
            .FirstOrDefault(p => p.GetValue("executionStages", null) is BsonDocument s && Chain(Unwrap(s)!) == winnerChain);
        candidates.Add(Candidate(true, winnerChain, winnerStats));
        var used = new HashSet<BsonDocument>(ReferenceEqualityComparer.Instance);
        if (winnerStats is not null)
        {
            _ = used.Add(winnerStats);
        }
        foreach (BsonDocument plan in rejected.OfType<BsonDocument>())
        {
            string chain = Chain(Unwrap(plan)!);
            BsonDocument? stats = all?.OfType<BsonDocument>()
                .FirstOrDefault(p => !used.Contains(p) && p.GetValue("executionStages", null) is BsonDocument s && Chain(Unwrap(s)!) == chain);
            if (stats is not null)
            {
                _ = used.Add(stats);
            }
            candidates.Add(Candidate(false, chain, stats));
        }
        return candidates;
    }

    private static ExplainCandidate Candidate(bool winner, string chain, BsonDocument? stats) => new(
        winner,
        chain,
        stats?.GetValue("executionStages", null) is BsonDocument stages ? Long(stages, "works") : null,
        stats is null ? null : Long(stats, "nReturned"),
        stats?.GetValue("score", null) is { IsNumeric: true } score ? score.ToDouble() : null);

    /// <summary>计划的阶段链(访问路径在前):<c>IXSCAN status_1_createdAt_-1 → FETCH → LIMIT</c>。</summary>
    internal static string Chain(BsonDocument stage)
    {
        var names = new List<string>();
        for (BsonDocument? s = stage; s is not null;)
        {
            string name = s.GetValue("stage", "?").ToString() ?? "?";
            names.Add(s.GetValue("indexName", null) is { } index ? $"{name} {index}" : name);
            s = s.GetValue("inputStage", null) as BsonDocument
                ?? (s.GetValue("inputStages", null) as BsonArray)?.OfType<BsonDocument>().FirstOrDefault();
        }
        names.Reverse();
        return string.Join(" → ", names);
    }

    /// <summary>
    /// ESR 建议:等值字段在前、排序字段居中、范围字段在后。只在计划确实有可改进之处时给
    /// (全集合扫描、内存排序、FETCH 后丢弃、扫描 / 返回比明显大于 1),且建议键与现在用的索引不同。
    /// </summary>
    internal static ExplainAdvice? Advise(ExplainPlan plan)
    {
        BsonDocument filter = plan.QueryFilter ?? [];
        BsonDocument sort = plan.QuerySort ?? [];
        if (filter.ElementCount == 0 && sort.ElementCount == 0)
        {
            return null;
        }
        (List<string> equality, List<string> range) = Classify(filter);
        var keys = new BsonDocument();
        foreach (string field in equality)
        {
            keys[field] = 1;
        }
        foreach (BsonElement element in sort)
        {
            if (!keys.Contains(element.Name))
            {
                keys[element.Name] = element.Value.IsNumeric && element.Value.ToInt32() < 0 ? -1 : 1;
            }
        }
        foreach (string field in range)
        {
            if (!keys.Contains(field))
            {
                keys[field] = 1;
            }
        }
        if (keys.ElementCount == 0)
        {
            return null;
        }
        ExplainStage? access = plan.Stages.FirstOrDefault(static s => s.IsAccess);
        if (access?.KeyPattern is { } current && SameKeys(current, keys))
        {
            return null;
        }
        ExplainStage? fetch = plan.Stages.FirstOrDefault(static s => s.Name == "FETCH" && s.Filter is not null);
        long? expected = plan.NReturned;
        if (plan.IsCollectionScan)
        {
            return new ExplainAdvice("Query_AdviceCollScan", "", keys, expected);
        }
        if (fetch is { Discarded: > 0 })
        {
            string fields = string.Join(", ", fetch.Filter!.Names.Where(static n => !n.StartsWith('$')));
            return new ExplainAdvice("Query_AdviceFetchFilter", fields.Length > 0 ? fields : BsonText.Literal(fetch.Filter), keys, expected);
        }
        if (plan.QueryLayerSort)
        {
            return new ExplainAdvice("Query_AdviceSort", string.Join(", ", sort.Names), keys, expected);
        }
        if (plan.ExaminedRatio is > 2)
        {
            return new ExplainAdvice("Query_AdviceRatio", plan.ExaminedRatio.Value.ToString("0.0", CultureInfo.InvariantCulture), keys, expected);
        }
        return null;
    }

    /// <summary>把筛选条件里的字段分成等值与范围两类($and 拍平;$or / $expr 之类不参与建议)。</summary>
    internal static (List<string> Equality, List<string> Range) Classify(BsonDocument filter)
    {
        var equality = new List<string>();
        var range = new List<string>();
        void Walk(BsonDocument doc)
        {
            foreach (BsonElement element in doc)
            {
                if (element.Name == "$and" && element.Value is BsonArray clauses)
                {
                    foreach (BsonDocument clause in clauses.OfType<BsonDocument>())
                    {
                        Walk(clause);
                    }
                    continue;
                }
                if (element.Name.StartsWith('$'))
                {
                    continue;
                }
                bool isRange = element.Value is BsonDocument ops && ops.ElementCount > 0 && ops.Names.First().StartsWith('$')
                               && !ops.Names.All(static n => n is "$eq" or "$in");
                bool isRegex = element.Value is BsonRegularExpression;
                (isRange || isRegex ? range : equality).Add(element.Name);
            }
        }
        Walk(filter);
        return ([.. equality.Distinct()], [.. range.Distinct()]);
    }

    /// <summary>建议键是现有索引的前缀(或相同):现有索引已经够用,不再建议。</summary>
    private static bool SameKeys(BsonDocument current, BsonDocument suggested) =>
        suggested.ElementCount <= current.ElementCount && suggested.Elements.Zip(current.Elements).All(static p =>
            p.First.Name == p.Second.Name && p.First.Value.ToString() == p.Second.Value.ToString());

    private static long? Long(BsonDocument document, string name) =>
        document.TryGetValue(name, out BsonValue value) && value.IsNumeric ? value.ToInt64() : null;
}
