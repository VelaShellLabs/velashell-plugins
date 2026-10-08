using System.Globalization;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>阶段流里的一张阶段卡片(设计稿 14 的 IXSCAN / FETCH / LIMIT / PROJECTION)。</summary>
internal sealed class ExplainStageCard
{
    /// <summary>阶段。</summary>
    public required ExplainStage Stage { get; init; }

    /// <summary>阶段名。</summary>
    public string Name => Stage.Name;

    /// <summary>图标。</summary>
    public required string IconKey { get; init; }

    /// <summary>图标颜色令牌。</summary>
    public required string IconToken { get; init; }

    /// <summary>描边颜色令牌(获胜路径绿、需要注意橙、全表扫描红)。</summary>
    public required string StrokeToken { get; init; }

    /// <summary>本阶段耗时(<c>6 ms</c>)。</summary>
    public required string Time { get; init; }

    /// <summary>说明(索引名 + 边界、filter、limitAmount…)。</summary>
    public required string Detail { get; init; }

    /// <summary>指标行。</summary>
    public required IReadOnlyList<ExplainMetric> Metrics { get; init; }

    /// <summary>耗时占比(0–1),画在卡片底部那根细条上。</summary>
    public required double TimeRatio { get; init; }

    /// <summary>细条颜色令牌。</summary>
    public required string BarToken { get; init; }

    /// <summary>底部提示(<c>312 份文档读出后被丢弃</c>);没有为 <see langword="null" />。</summary>
    public string? Note { get; init; }

    /// <summary>提示颜色令牌。</summary>
    public string NoteToken { get; init; } = "VelaStatusConnected";

    /// <summary>提示图标。</summary>
    public string NoteIcon { get; init; } = "Mongo.circle-check";

    /// <summary>有没有说明。</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>有没有提示。</summary>
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

/// <summary>阶段之间的箭头(标出流过去的文档数)。</summary>
/// <param name="Count">数量文字。</param>
internal sealed record ExplainArrow(string Count);

/// <summary>候选计划列表的一行。</summary>
/// <param name="Winner">获胜。</param>
/// <param name="Tag">标签文字(获胜 / 拒绝)。</param>
/// <param name="Chain">阶段链。</param>
/// <param name="Stats">试运行数据(<c>101 works · 返回 100</c>)。</param>
/// <param name="Score">打分文字。</param>
internal sealed record ExplainCandidateRow(bool Winner, string Tag, string Chain, string Stats, string Score)
{
    /// <summary>有没有打分。</summary>
    public bool HasScore => Score.Length > 0;
}

/// <summary>树视图的一行。</summary>
/// <param name="Depth">深度。</param>
/// <param name="Name">阶段名。</param>
/// <param name="Detail">索引名 / filter。</param>
/// <param name="Metrics">指标摘要。</param>
internal sealed record ExplainTreeRow(int Depth, string Name, string Detail, string Metrics)
{
    /// <summary>左缩进。</summary>
    public double Indent => 12 + Depth * 18;
}

/// <summary>摘要里的一格统计(总耗时、返回、扫描键…)。</summary>
/// <param name="Label">标签。</param>
/// <param name="Value">值。</param>
/// <param name="Warn">值用警告色。</param>
internal sealed record ExplainStat(string Label, string Value, bool Warn = false);

/// <summary>
/// 「执行计划」页签(设计稿 14):可视化阶段流 / 树 / 原始 JSON 三种看法、verbosity 下拉、候选计划、
/// 右侧摘要与 ESR 优化建议。真正跑 explain 的是查询标签页 —— 这里只负责把一份 <see cref="ExplainPlan" /> 摆出来。
/// </summary>
internal sealed class ExplainPane : QueryPane
{
    private readonly QueryTabViewModel _owner;

    /// <summary>构造。</summary>
    public ExplainPane(QueryTabViewModel owner)
        : base(owner.Loc)
    {
        _owner = owner;
        CreateIndexCommand = new AsyncCommand(() => Plan?.Advice is { } advice ? owner.CreateSuggestedIndexAsync(advice.Keys) : Task.CompletedTask,
            () => Plan?.Advice is not null);
        HintCompareCommand = new AsyncCommand(owner.CompareWithHintAsync, () => Plan is not null && Command is not null);
        RefreshCommand = new AsyncCommand(() => Command is null ? Task.CompletedTask : owner.ExplainAsync(Command, Database ?? owner.Database));
    }

    /// <inheritdoc />
    public override QueryPaneKind PaneKind => QueryPaneKind.Explain;

    /// <inheritdoc />
    public override string Header => Loc["Query_TabExplain"];

    /// <inheritdoc />
    public override string IconKey => "Mongo.gauge";

    /// <summary>可选的 verbosity。</summary>
    public IReadOnlyList<string> Verbosities { get; } = ["queryPlanner", "executionStats", "allPlansExecution"];

    /// <summary>当前 verbosity(改了就重跑)。</summary>
    public string Verbosity
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && Command is not null)
            {
                RefreshCommand.Execute(null);
            }
        }
    } = "executionStats";

    /// <summary>看法(<c>visual</c> / <c>tree</c> / <c>raw</c>)。</summary>
    public string ViewMode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(IsVisual));
                RaisePropertyChanged(nameof(IsTree));
                RaisePropertyChanged(nameof(IsRaw));
                RaisePropertyChanged(nameof(ShowVisual));
                RaisePropertyChanged(nameof(ShowTree));
                RaisePropertyChanged(nameof(ShowRaw));
                RaisePropertyChanged(nameof(RawJson));
            }
        }
    } = "visual";

    /// <summary>可视化。</summary>
    public bool IsVisual => ViewMode == "visual";

    /// <summary>树。</summary>
    public bool IsTree => ViewMode == "tree";

    /// <summary>原始 JSON。</summary>
    public bool IsRaw => ViewMode == "raw";

    /// <summary>显示阶段流(有计划且是可视化看法)。</summary>
    public bool ShowVisual => HasPlan && IsVisual;

    /// <summary>显示树。</summary>
    public bool ShowTree => HasPlan && IsTree;

    /// <summary>显示原始 JSON。</summary>
    public bool ShowRaw => HasPlan && IsRaw;

    /// <summary>被解释的语句。</summary>
    public ShellCommand? Command { get; private set; }

    /// <summary>语句作用的库。</summary>
    public string? Database { get; private set; }

    /// <summary>解读结果。</summary>
    public ExplainPlan? Plan { get; private set; }

    /// <summary>有计划可看。</summary>
    public bool HasPlan => Plan is not null && !IsBusy;

    /// <summary>空态(还没跑过)。</summary>
    public bool IsEmpty => Plan is null && !IsBusy && Error is null;

    /// <summary>跑着。</summary>
    public bool IsBusy
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>失败原因。</summary>
    public string? Error
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>有没有失败。</summary>
    public bool HasError => Error is not null && !IsBusy;

    /// <summary>标题旁的语句摘要(<c>db.orders.find(…).sort({ createdAt: -1 }).limit(100)</c>)。</summary>
    public string CommandText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>阶段流(卡片与箭头交替)。</summary>
    public IReadOnlyList<object> FlowItems { get; private set; } = [];

    /// <summary>候选计划。</summary>
    public IReadOnlyList<ExplainCandidateRow> Candidates { get; private set; } = [];

    /// <summary>候选计划标题旁的说明。</summary>
    public string CandidatesNote { get; private set; } = "";

    /// <summary>有没有候选计划。</summary>
    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>摘要的六格统计。</summary>
    public IReadOnlyList<ExplainStat> Stats { get; private set; } = [];

    /// <summary>有优化建议。</summary>
    public bool HasAdvice => Plan?.Advice is not null;

    /// <summary>没有建议(计划健康)。</summary>
    public bool NoAdvice => Plan is not null && Plan.Advice is null;

    /// <summary>建议的说明。</summary>
    public string AdviceText { get; private set; } = "";

    /// <summary>建议的索引键(<c>{ status: 1, createdAt: -1, total: 1 }</c>)。</summary>
    public string AdviceKeys { get; private set; } = "";

    /// <summary>建议的预期效果。</summary>
    public string AdvicePrediction { get; private set; } = "";

    /// <summary>有没有预期效果。</summary>
    public bool HasPrediction => AdvicePrediction.Length > 0;

    /// <summary>树视图的行。</summary>
    public IReadOnlyList<ExplainTreeRow> TreeRows { get; private set; } = [];

    /// <summary>原始 JSON(只在切到原始视图时才拼)。</summary>
    public string RawJson => IsRaw && Plan is not null ? BsonText.Pretty(Plan.Raw, EjsonMode.Relaxed) : "";

    /// <summary>整份原始 JSON(复制 / 导出)。</summary>
    public string? RawText => Plan is null ? null : BsonText.Pretty(Plan.Raw, EjsonMode.Relaxed);

    /// <summary>创建建议的索引。</summary>
    public AsyncCommand CreateIndexCommand { get; }

    /// <summary>用 hint 对比。</summary>
    public AsyncCommand HintCompareCommand { get; }

    /// <summary>重跑。</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>开始一次 explain(清掉旧的,转圈)。</summary>
    internal void Begin(ShellCommand command, string database)
    {
        Command = command;
        Database = database;
        CommandText = Summarize(command);
        Error = null;
        IsBusy = true;
    }

    /// <summary>失败。</summary>
    internal void Fail(string message)
    {
        IsBusy = false;
        Error = message;
    }

    /// <summary>摆出一份执行计划。</summary>
    internal void Show(ExplainPlan plan)
    {
        Plan = plan;
        BuildFlow(plan);
        BuildCandidates(plan);
        BuildStats(plan);
        BuildAdvice(plan);
        TreeRows =
        [
            .. plan.Tree.Select(s => new ExplainTreeRow(s.Depth, s.ShortName, s.IndexName is null ? s.Detail.Split('\n')[0] : "", Metrics(s)))
        ];
        Error = null;
        IsBusy = false;
        foreach (string name in (string[])[nameof(FlowItems), nameof(Candidates), nameof(CandidatesNote), nameof(HasCandidates), nameof(Stats),
                     nameof(AdviceText), nameof(AdviceKeys), nameof(AdvicePrediction), nameof(HasPrediction), nameof(HasAdvice), nameof(NoAdvice),
                     nameof(TreeRows), nameof(RawJson), nameof(Plan)])
        {
            RaisePropertyChanged(name);
        }
        CreateIndexCommand.RaiseCanExecuteChanged();
        HintCompareCommand.RaiseCanExecuteChanged();
        RaiseState();
    }

    private void RaiseState()
    {
        RaisePropertyChanged(nameof(HasPlan));
        RaisePropertyChanged(nameof(ShowVisual));
        RaisePropertyChanged(nameof(ShowTree));
        RaisePropertyChanged(nameof(ShowRaw));
        RaisePropertyChanged(nameof(IsEmpty));
        RaisePropertyChanged(nameof(HasError));
    }

    /// <summary>语句的一行摘要:实参里的长文档折成 <c>…</c>,链上的短实参原样保留。</summary>
    internal static string Summarize(ShellCommand command)
    {
        var b = new System.Text.StringBuilder("db");
        if (command.Collection is { } c)
        {
            _ = b.Append('.').Append(c);
        }
        if (command.Method is { } method)
        {
            _ = b.Append('.').Append(method.Name).Append('(').Append(method.Arguments.Count == 0 ? "" : "…").Append(')');
        }
        foreach (ShellCall call in command.Chain)
        {
            string args = string.Join(", ", call.Arguments.Select(static a => BsonText.Literal(a.Value)));
            _ = b.Append('.').Append(call.Name).Append('(').Append(args.Length > 40 ? "…" : args).Append(')');
        }
        return b.ToString();
    }

    private void BuildFlow(ExplainPlan plan)
    {
        var items = new List<object>();
        double total = Math.Max(1, Math.Max(plan.TotalMs ?? 0, plan.Stages.Sum(static s => s.OwnMs ?? 0)));
        for (int i = 0; i < plan.Stages.Count; i++)
        {
            ExplainStage stage = plan.Stages[i];
            if (i > 0)
            {
                long? flowing = plan.Stages[i - 1].NReturned;
                items.Add(new ExplainArrow(flowing is { } n ? BsonText.Grouped(n) : ""));
            }
            items.Add(Card(plan, stage, i, total));
        }
        FlowItems = items;
    }

    private ExplainStageCard Card(ExplainPlan plan, ExplainStage stage, int index, double total)
    {
        var metrics = new List<ExplainMetric>();
        string? note = null;
        string noteToken = "VelaStatusConnected";
        string noteIcon = "Mongo.circle-check";
        bool warnFetch = stage.Name == "FETCH" && stage.Discarded > 0 && stage.Selectivity < 0.8;
        switch (stage.Name)
        {
            case "IXSCAN" or "EXPRESS_IXSCAN" or "COUNT_SCAN" or "DISTINCT_SCAN":
                Add(metrics, "keysExamined", stage.KeysExamined);
                Add(metrics, "nReturned", stage.NReturned);
                if (stage.Direction is { } direction)
                {
                    metrics.Add(new ExplainMetric(Loc["Query_ExplainDirection"], direction));
                }
                if (plan.SortFromIndex)
                {
                    note = Loc["Query_ExplainSortFromIndex"];
                }
                break;
            case "COLLSCAN":
                Add(metrics, "docsExamined", stage.DocsExamined);
                Add(metrics, "nReturned", stage.NReturned);
                if (stage.Direction is { } scanDirection)
                {
                    metrics.Add(new ExplainMetric(Loc["Query_ExplainDirection"], scanDirection));
                }
                note = Loc["Query_ExplainCollScan"];
                noteToken = "VelaError";
                noteIcon = "Mongo.circle-x";
                break;
            case "FETCH":
                Add(metrics, "docsExamined", stage.DocsExamined);
                Add(metrics, "nReturned", stage.NReturned);
                if (stage.Selectivity is { } selectivity && stage.Filter is not null)
                {
                    metrics.Add(new ExplainMetric(Loc["Query_ExplainSelectivity"],
                        (selectivity * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%", selectivity < 0.5));
                }
                if (warnFetch)
                {
                    note = Loc.Format("Query_ExplainDiscarded", BsonText.Grouped(stage.Discarded));
                    noteToken = "VelaWarning";
                    noteIcon = "Mongo.triangle-alert";
                }
                break;
            case "LIMIT":
                Add(metrics, "nReturned", stage.NReturned);
                bool early = index > 0 && plan.Stages[index - 1].IsEof == false;
                metrics.Add(new ExplainMetric(Loc["Query_ExplainEarlyExit"], early ? Loc["Common_Yes"] : Loc["Common_No"]));
                break;
            case "SORT":
                Add(metrics, "nReturned", stage.NReturned);
                if (stage.Raw.GetValue("totalDataSizeSorted", null) is { IsNumeric: true } size)
                {
                    metrics.Add(new ExplainMetric(Loc["Query_ExplainSortedBytes"], BsonText.Bytes(size.ToInt64())));
                }
                metrics.Add(new ExplainMetric("usedDisk", stage.Raw.GetValue("usedDisk", false).ToBoolean() ? Loc["Common_Yes"] : Loc["Common_No"]));
                note = Loc["Query_ExplainMemorySort"];
                noteToken = "VelaWarning";
                noteIcon = "Mongo.triangle-alert";
                break;
            case var name when name.StartsWith("PROJECTION", StringComparison.Ordinal):
                Add(metrics, "nReturned", stage.NReturned);
                metrics.Add(new ExplainMetric(Loc["Query_ExplainCovered"], plan.IsCovered ? Loc["Common_Yes"] : Loc["Common_No"]));
                break;
            default:
                Add(metrics, "nReturned", stage.NReturned);
                if (stage.Raw.GetValue("usedDisk", null) is { } usedDisk)
                {
                    metrics.Add(new ExplainMetric("usedDisk", usedDisk.ToBoolean() ? Loc["Common_Yes"] : Loc["Common_No"]));
                }
                else
                {
                    Add(metrics, "works", stage.Works);
                }
                break;
        }
        (string icon, string iconToken) = stage.Name switch
        {
            "IXSCAN" or "EXPRESS_IXSCAN" or "COUNT_SCAN" or "DISTINCT_SCAN" or "IDHACK" => ("Mongo.key-round", "VelaStatusConnected"),
            "COLLSCAN" => ("Mongo.table-2", "VelaError"),
            "FETCH" => ("Mongo.file-search", warnFetch ? "VelaWarning" : "VelaInfo"),
            "LIMIT" or "SKIP" => ("Mongo.list-end", "VelaInfo"),
            "SORT" or "$sort" => ("Mongo.arrow-down-up", stage.Name == "SORT" ? "VelaWarning" : "VelaInfo"),
            "$group" or "$bucket" or "$bucketAuto" => ("Mongo.layers", "VelaAccent"),
            var n when n.StartsWith("PROJECTION", StringComparison.Ordinal) || n is "$project" or "$addFields" or "$set" => ("Mongo.columns-3", "VelaInfo"),
            _ => ("Mongo.list", "VelaInfo")
        };
        string stroke = stage.Tone switch
        {
            ExplainTone.Good => "VelaStatusConnected",
            ExplainTone.Warning => "VelaWarning",
            ExplainTone.Bad => "VelaError",
            _ => "VelaBorderPrimary"
        };
        long own = stage.OwnMs ?? 0;
        return new ExplainStageCard
        {
            Stage = stage,
            IconKey = icon,
            IconToken = iconToken,
            StrokeToken = stroke,
            Time = stage.OwnMs is null ? "" : Loc.Format("Common_Ms", own),
            Detail = stage.Detail,
            Metrics = metrics,
            TimeRatio = Math.Clamp(own / total, 0.03, 1),
            BarToken = warnFetch || stage.Tone is ExplainTone.Warning or ExplainTone.Bad ? (stage.Tone == ExplainTone.Bad ? "VelaError" : "VelaWarning") : "MongoChart1",
            Note = note,
            NoteToken = noteToken,
            NoteIcon = noteIcon
        };
    }

    private static void Add(List<ExplainMetric> metrics, string label, long? value)
    {
        if (value is { } v)
        {
            metrics.Add(new ExplainMetric(label, BsonText.Grouped(v)));
        }
    }

    private void BuildCandidates(ExplainPlan plan)
    {
        Candidates =
        [
            .. plan.Candidates.Select(c => new ExplainCandidateRow(
                c.Winner,
                c.Winner ? Loc["Query_ExplainWinner"] : Loc["Query_ExplainRejected"],
                c.Chain,
                c.HasStats ? Loc.Format("Query_ExplainTrial", BsonText.Grouped(c.Works ?? 0), BsonText.Grouped(c.NReturned ?? 0)) : "",
                c.Score is { } score ? "score " + score.ToString("0.0000", CultureInfo.InvariantCulture) : ""))
        ];
        ExplainCandidate? winner = plan.Candidates.FirstOrDefault(static c => c.Winner);
        CandidatesNote = plan.Candidates.Count <= 1
            ? Loc["Query_ExplainSingleCandidate"]
            : winner?.Works is { } works
                ? Loc.Format("Query_ExplainTrialNote", BsonText.Grouped(works))
                : Loc["Query_ExplainNeedAllPlans"];
    }

    private void BuildStats(ExplainPlan plan)
    {
        static string Dash(long? value) => value is { } v ? BsonText.Grouped(v) : "—";
        double? ratio = plan.ExaminedRatio;
        Stats =
        [
            new ExplainStat(Loc["Query_ExplainTotal"], plan.TotalMs is { } ms ? Loc.Format("Common_Ms", ms) : "—"),
            new ExplainStat(Loc["Query_ExplainReturned"], Dash(plan.NReturned)),
            new ExplainStat(Loc["Query_ExplainKeys"], Dash(plan.TotalKeysExamined)),
            new ExplainStat(Loc["Query_ExplainDocs"], Dash(plan.TotalDocsExamined)),
            new ExplainStat(Loc["Query_ExplainRatio"], ratio is { } r ? r.ToString(r >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " ×" : "—", ratio > 1.05),
            new ExplainStat(Loc["Query_ExplainInMemorySort"], plan.HasInMemorySort ? Loc["Common_Yes"] : Loc["Common_No"], plan.QueryLayerSort)
        ];
    }

    private void BuildAdvice(ExplainPlan plan)
    {
        if (plan.Advice is not { } advice)
        {
            AdviceText = Loc["Query_AdviceNone"];
            AdviceKeys = "";
            AdvicePrediction = "";
            return;
        }
        AdviceText = Loc.Format(advice.ReasonKey, advice.ReasonArgument);
        AdviceKeys = BsonText.Literal(advice.Keys);
        AdvicePrediction = advice.ExpectedKeys is { } expected ? Loc.Format("Query_AdvicePredict", BsonText.Grouped(expected)) : "";
    }

    private string Metrics(ExplainStage stage)
    {
        var parts = new List<string>();
        if (stage.NReturned is { } n)
        {
            parts.Add("nReturned " + BsonText.Grouped(n));
        }
        if (stage.KeysExamined is { } k)
        {
            parts.Add("keys " + BsonText.Grouped(k));
        }
        if (stage.DocsExamined is { } d)
        {
            parts.Add("docs " + BsonText.Grouped(d));
        }
        if (stage.OwnMs is { } ms)
        {
            parts.Add(Loc.Format("Common_Ms", ms));
        }
        return string.Join(" · ", parts);
    }
}
