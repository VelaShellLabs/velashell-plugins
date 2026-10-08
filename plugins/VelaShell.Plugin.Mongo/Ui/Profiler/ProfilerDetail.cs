using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 慢查询右侧的详情(设计稿 15):最近一次样本、还原的 mongosh 语句、扫描 / 返回 / 计划 / 内存排序,
/// 以及按 ESR 推出来的建议索引。
/// </summary>
internal sealed class ProfilerDetail : ObservableObject
{
    private string _adviceText;
    private bool _canCreateIndex;

    /// <summary>构造。</summary>
    /// <param name="group">所属形状。</param>
    /// <param name="sample">展示的那一条样本(按形状模式是最近一次;逐条模式是选中的那条)。</param>
    /// <param name="loc">文案。</param>
    public ProfilerDetail(ProfilerShapeRow group, ProfileEntry sample, Loc loc)
    {
        Group = group;
        Loc = loc;
        Sample = sample;
        string plan = ProfilerFormat.PlanText(group.Plan, sample.PlanSummary);
        Title = $"{group.Title} · {plan}";
        Subtitle = loc.Format("Prof_DetailCount", BsonText.Grouped(group.Count),
            (group.Share * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
        SampleHeader = loc.Format("Prof_Sample", sample.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        (Statement, Explain) = QueryShape.Rebuild(sample.Raw);
        string client = string.IsNullOrEmpty(sample.Client) ? "—" : sample.Client;
        if (sample.User.Length > 0)
        {
            client = $"{sample.User}@{client}";
        }
        Client = sample.AppName.Length > 0 ? $"{client} · {sample.AppName}" : client;
        DocsExamined = BsonText.Grouped(sample.DocsExamined);
        DocsWarn = ProfilerFormat.RatioHigh(sample.DocsExamined, sample.Returned);
        Returned = BsonText.Grouped(sample.Returned);
        PlanSummary = sample.PlanSummary.Length > 0 ? sample.PlanSummary : "—";
        PlanToken = sample.Plan switch
        {
            SlowPlanKind.CollectionScan => "VelaError",
            SlowPlanKind.InMemorySort => "VelaWarning",
            SlowPlanKind.IndexScan => "VelaStatusConnected",
            _ => "VelaTextSecondary"
        };
        InMemorySort = sample.HasSortStage
            ? sample.SortBytes > 0
                ? loc.Format("Prof_SortYes", BsonText.Bytes(sample.SortBytes)) + (sample.UsedDisk ? " · " + loc["Prof_SortDisk"] : "")
                : loc["Common_Yes"]
            : loc["Common_No"];
        SortWarn = sample.HasSortStage;

        (BsonDocument filter, BsonDocument? sort) = sample.FilterAndSort();
        Advice = IndexAdvice.Suggest(filter, sort);
        bool healthy = sample.Plan == SlowPlanKind.IndexScan && !ProfilerFormat.RatioHigh(group.Examined, group.Returned);
        if (Advice is null)
        {
            _adviceText = filter.ElementCount == 0 && sort is null ? loc["Prof_AdviceNoFilter"] : loc["Prof_AdviceNone"];
        }
        else if (healthy)
        {
            _adviceText = loc.Format("Prof_AdviceFine", sample.PlanSummary);
        }
        else
        {
            _adviceText = DescribeAdvice(Advice, loc);
            _canCreateIndex = true;
        }
        AdviceKeys = Advice is not null && !healthy ? Advice.KeysText : "";
        // 代码块的高度随语句行数走(只读展示,不需要滚动条);上限 12 行,再长就让编辑器自己滚。
        int lines = Math.Clamp(Statement.Count(static c => c == '\n') + 1, 1, 12);
        StatementHeight = (lines * 14) + 12;
    }

    /// <summary>所属形状。</summary>
    public ProfilerShapeRow Group { get; }

    /// <summary>文案(详情模板里的标签 <c>{Binding Loc[...]}</c>)。</summary>
    public Loc Loc { get; }

    /// <summary>样本。</summary>
    public ProfileEntry Sample { get; }

    /// <summary>集合名(建索引、拼语句用)。</summary>
    public string Collection => Group.Shape.Collection;

    /// <summary>标题(<c>orders.find · COLLSCAN</c>)。</summary>
    public string Title { get; }

    /// <summary>副标题(<c>1,204 次 · 占慢查询总耗时 71%</c>)。</summary>
    public string Subtitle { get; }

    /// <summary>「最近一次样本 · 21:08:44」。</summary>
    public string SampleHeader { get; }

    /// <summary>还原的 mongosh 语句。</summary>
    public string Statement { get; }

    /// <summary>对应的执行计划语句。</summary>
    public string Explain { get; }

    /// <summary>代码块高度。</summary>
    public double StatementHeight { get; }

    /// <summary>客户端(<c>10.0.8.44 · order-sweeper</c>)。</summary>
    public string Client { get; }

    /// <summary>docsExamined。</summary>
    public string DocsExamined { get; }

    /// <summary>docsExamined 偏高(橙)。</summary>
    public bool DocsWarn { get; }

    /// <summary>nreturned。</summary>
    public string Returned { get; }

    /// <summary>planSummary。</summary>
    public string PlanSummary { get; }

    /// <summary>planSummary 的颜色。</summary>
    public string PlanToken { get; }

    /// <summary>内存排序(<c>是 · 18 MB</c> / <c>否</c>)。</summary>
    public string InMemorySort { get; }

    /// <summary>有内存排序(橙)。</summary>
    public bool SortWarn { get; }

    /// <summary>建议的索引;推不出为 null。</summary>
    public IndexAdvice? Advice { get; }

    /// <summary>建议的说明文字。</summary>
    public string AdviceText
    {
        get => _adviceText;
        set => SetProperty(ref _adviceText, value);
    }

    /// <summary>建议索引键的代码(<c>{ "customer.level": 1, total: -1 }</c>);没有建议为空。</summary>
    public string AdviceKeys { get; }

    /// <summary>有没有索引键代码块。</summary>
    public bool HasAdviceKeys => AdviceKeys.Length > 0;

    /// <summary>「创建建议索引」可不可用(没有建议、已被现有索引覆盖时不可用)。</summary>
    public bool CanCreateIndex
    {
        get => _canCreateIndex;
        set => SetProperty(ref _canCreateIndex, value);
    }

    /// <summary>
    /// 把 ESR 的推理写成一句话(<c>customer.level 为等值条件,total 同时用于排序与范围,按 ESR 建议:</c>)——
    /// 只给一个索引键而不说为什么,用户没法判断该不该照建。
    /// </summary>
    private static string DescribeAdvice(IndexAdvice advice, Loc loc)
    {
        var parts = new List<string>();
        if (advice.Equality.Count > 0)
        {
            parts.Add(loc.Format("Prof_EsrEquality", string.Join(", ", advice.Equality)));
        }
        if (advice.Sort.Count > 0)
        {
            parts.Add(loc.Format("Prof_EsrSort", string.Join(", ", advice.Sort)));
        }
        if (advice.SortAndRange.Count > 0)
        {
            parts.Add(loc.Format("Prof_EsrSortRange", string.Join(", ", advice.SortAndRange)));
        }
        if (advice.Range.Count > 0)
        {
            parts.Add(loc.Format("Prof_EsrRange", string.Join(", ", advice.Range)));
        }
        return string.Join(loc["Prof_EsrJoin"], parts) + loc["Prof_EsrTail"];
    }
}
