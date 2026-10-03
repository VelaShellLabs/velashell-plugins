using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>
/// 一个字段路径在抽样里的画像(设计稿 08 的一行)。
/// <para>
/// 计数口径是**按文档**:<see cref="Present" /> 是"有这个路径的文档数",<see cref="Kinds" /> 是"这个路径
/// 取到某种类型的文档数"(同一份文档里数组展开出多个值时,同一种类型只记一次)。
/// 值分布(<see cref="Values" />、<see cref="Numbers" />…)则是**按值**计 —— <c>items.sku</c> 一份文档里有三个 sku,
/// 三个都要进分布,否则直方图会系统性地偏向数组头一个元素。
/// </para>
/// </summary>
internal sealed class AnalyzedField
{
    /// <summary>出现次数超过这个数的不同取值就不再逐个跟踪(只记"很多")。</summary>
    internal const int DistinctCap = 10_000;

    private readonly Dictionary<BsonKind, int> _kinds = [];
    private readonly Dictionary<BsonValue, int> _values = [];

    /// <summary>构造。</summary>
    /// <param name="path">点号路径(<c>customer.level</c>)。</param>
    /// <param name="parent">父路径;顶层为 <see langword="null" />。</param>
    /// <param name="inArray">这个路径是不是经由文档数组展开出来的(<c>items.sku</c>)。</param>
    public AnalyzedField(string path, string? parent, bool inArray)
    {
        Path = path;
        Parent = parent;
        InArray = inArray;
        Name = path[(path.LastIndexOf('.') + 1)..];
        Depth = path.Count(static c => c == '.');
    }

    /// <summary>点号路径。</summary>
    public string Path { get; }

    /// <summary>最后一段(<c>level</c>)。</summary>
    public string Name { get; }

    /// <summary>父路径;顶层为 <see langword="null" />。</summary>
    public string? Parent { get; }

    /// <summary>嵌套深度(顶层 0)。</summary>
    public int Depth { get; }

    /// <summary>是不是经由文档数组展开出来的路径。</summary>
    public bool InArray { get; }

    /// <summary>抽样文档总数。</summary>
    public int Total { get; internal set; }

    /// <summary>有这个路径的文档数(含值为 null 的)。</summary>
    public int Present { get; internal set; }

    /// <summary>缺失这个路径的文档数。</summary>
    public int Missing => Math.Max(0, Total - Present);

    /// <summary>各类型的文档数。</summary>
    public IReadOnlyDictionary<BsonKind, int> Kinds => _kinds;

    /// <summary>不同取值 → 出现次数(只跟踪标量;超过 <see cref="DistinctCap" /> 后不再新增)。</summary>
    public IReadOnlyDictionary<BsonValue, int> Values => _values;

    /// <summary>不同取值多到停止跟踪了。</summary>
    public bool DistinctOverflow { get; private set; }

    /// <summary>标量值的总个数(按值计)。</summary>
    public int ScalarCount { get; private set; }

    /// <summary>数值(Decimal128 / Int / Double 一律折成 double)。</summary>
    public List<double> Numbers { get; } = [];

    /// <summary>日期(UTC)。</summary>
    public List<DateTime> Dates { get; } = [];

    /// <summary>ObjectId 里带的时间戳(UTC)。</summary>
    public List<DateTime> IdTimes { get; } = [];

    /// <summary>字符串长度。</summary>
    public List<int> StringLengths { get; } = [];

    /// <summary>数组长度 → 次数。</summary>
    public Dictionary<int, int> ArrayLengths { get; } = [];

    /// <summary>数组元素类型 → 个数。</summary>
    public Dictionary<BsonKind, int> ElementKinds { get; } = [];

    /// <summary>数组里的文档元素总数。</summary>
    public int ElementDocuments { get; internal set; }

    /// <summary>数组里"子字段齐全"(含全部子字段的并集)的文档元素个数。</summary>
    public int CompleteElementDocuments { get; internal set; }

    /// <summary>直接子字段名(按首次出现的顺序)。</summary>
    public List<string> Children { get; } = [];

    /// <summary>值的 BSON 体积之和(估索引键长用)。</summary>
    public long ValueBytes { get; private set; }

    /// <summary>出现率(0–1,按文档)。</summary>
    public double Presence => Total == 0 ? 0 : (double)Present / Total;

    /// <summary>不同取值个数(溢出时为下限)。</summary>
    public int DistinctCount => _values.Count;

    /// <summary>出现过的类型(不含缺失),按文档数从多到少。</summary>
    public IReadOnlyList<BsonKind> PresentKinds =>
        [.. _kinds.Where(static k => k.Value > 0).OrderByDescending(static k => k.Value).Select(static k => k.Key)];

    /// <summary>占比最高的那种类型;没有值时为 <see cref="BsonKind.Missing" />。</summary>
    public BsonKind DominantKind => PresentKinds.FirstOrDefault(BsonKind.Missing);

    /// <summary>占比最高的非 null 类型(混合类型建议"统一为 X"时用)。</summary>
    public BsonKind DominantValueKind => PresentKinds.FirstOrDefault(static k => k != BsonKind.Null, DominantKind);

    /// <summary>出现了两种及以上的类型(null 也算一种:设计稿 08 把 String + Null 标成了混合)。</summary>
    public bool IsMixed => PresentKinds.Count > 1;

    /// <summary>全部取值互不相同(且抽到的不止一两份)。</summary>
    public bool IsUnique => !DistinctOverflow && ScalarCount >= 2 && DistinctCount == ScalarCount;

    /// <summary>平均一个值的 BSON 字节数。</summary>
    public double AverageValueBytes => ScalarCount == 0 ? 0 : (double)ValueBytes / ScalarCount;

    /// <summary>记一份文档在这个路径上取到的全部值。</summary>
    internal void Observe(IReadOnlyList<BsonValue> values)
    {
        Present++;
        var seen = new HashSet<BsonKind>();
        foreach (BsonValue value in values)
        {
            BsonKind kind = BsonKinds.Of(value);
            if (seen.Add(kind))
            {
                _kinds[kind] = _kinds.GetValueOrDefault(kind) + 1;
            }
            ObserveValue(value, kind);
        }
    }

    private void ObserveValue(BsonValue value, BsonKind kind)
    {
        switch (kind)
        {
            case BsonKind.Object:
                return;
            case BsonKind.Array:
            {
                BsonArray array = value.AsBsonArray;
                ArrayLengths[array.Count] = ArrayLengths.GetValueOrDefault(array.Count) + 1;
                foreach (BsonValue item in array)
                {
                    BsonKind itemKind = BsonKinds.Of(item);
                    ElementKinds[itemKind] = ElementKinds.GetValueOrDefault(itemKind) + 1;
                }
                return;
            }
        }
        ScalarCount++;
        ValueBytes += EstimateBytes(value);
        if (!_values.TryGetValue(value, out int count))
        {
            if (_values.Count >= DistinctCap)
            {
                DistinctOverflow = true;
            }
            else
            {
                _values[value] = 1;
            }
        }
        else
        {
            _values[value] = count + 1;
        }
        switch (kind)
        {
            case BsonKind.Int32:
            case BsonKind.Int64:
            case BsonKind.Double:
                Numbers.Add(value.ToDouble());
                break;
            case BsonKind.Decimal128:
                Numbers.Add(Decimal128.ToDouble(value.AsDecimal128));
                break;
            case BsonKind.Date:
                Dates.Add(value.AsBsonDateTime.ToUniversalTime());
                break;
            case BsonKind.ObjectId:
                IdTimes.Add(value.AsObjectId.CreationTime);
                break;
            case BsonKind.String:
                StringLengths.Add(value.AsString.Length);
                break;
        }
    }

    /// <summary>一个值在索引键里大约占多少字节(只为预估,不求精确)。</summary>
    private static int EstimateBytes(BsonValue value) => value.BsonType switch
    {
        BsonType.String => value.AsString.Length + 5,
        BsonType.ObjectId => 12,
        BsonType.Int32 => 4,
        BsonType.Int64 or BsonType.Double or BsonType.DateTime or BsonType.Timestamp => 8,
        BsonType.Decimal128 => 16,
        BsonType.Boolean => 1,
        BsonType.Binary => value.AsBsonBinaryData.Bytes.Length + 5,
        _ => 8
    };
}

/// <summary>一次 Schema 抽样分析的结果。</summary>
/// <param name="Sampled">抽到的文档数。</param>
/// <param name="Fields">全部字段路径(按首次出现的顺序,父在子前)。</param>
internal sealed record AnalyzedSchema(int Sampled, IReadOnlyList<AnalyzedField> Fields)
{
    /// <summary>按路径取画像。</summary>
    public AnalyzedField? this[string path] => Fields.FirstOrDefault(f => f.Path == path);

    /// <summary>某个路径的直接子字段。</summary>
    public IEnumerable<AnalyzedField> ChildrenOf(string? parent) => Fields.Where(f => f.Parent == parent);
}

/// <summary>值分布的画法(设计稿 08 第三列)。</summary>
internal enum AnalyzedViz
{
    /// <summary>一行说明文字。</summary>
    Text,

    /// <summary>少量取值:横向比例条。</summary>
    Bars,

    /// <summary>数值:直方图。</summary>
    Histogram,

    /// <summary>日期:按天直方图。</summary>
    Timeline,

    /// <summary>数组:长度分布柱。</summary>
    Lengths
}

/// <summary>一根横向比例条(少量取值 / 长度分布)。</summary>
/// <param name="Label">取值。</param>
/// <param name="Count">次数。</param>
/// <param name="Ratio">占比(0–1)。</param>
internal sealed record AnalyzedBucket(string Label, int Count, double Ratio);

/// <summary>
/// Schema 抽样分析(设计稿 08):把 <c>$sample</c> 抽回来的文档逐路径统计成 <see cref="AnalyzedField" />,
/// 再据此给出每一行的灰字提示(<c>String · 4 个取值 · 适合作等值前缀</c>)与值分布。
/// <para>
/// 纯函数、不碰服务器:抽样在视图模型里做,这里只管算 —— 于是单测可以直接喂文档。
/// 提示措辞刻意**只说抽样里看得到的事实**(取值个数、缺失比例、混合类型),
/// "适合作等值前缀"这类建议也只在证据足够时才给,宁可少说,不说错。
/// </para>
/// </summary>
internal static class SchemaAnalyzer
{
    /// <summary>取值个数不超过它的字符串视为"少量取值"(画比例条、可做 enum / 等值前缀)。</summary>
    public const int LowCardinality = 12;

    /// <summary>比例条最多画几根(其余并进"其它")。</summary>
    public const int MaxBars = 5;

    /// <summary>直方图的柱数。</summary>
    public const int HistogramBins = 20;

    /// <summary>分析一批文档。</summary>
    /// <param name="documents">抽样文档。</param>
    /// <param name="maxDepth">最深展开几层(与 <see cref="BsonPath.Walk" /> 同口径)。</param>
    public static AnalyzedSchema Analyze(IReadOnlyList<BsonDocument> documents, int maxDepth = 6)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var fields = new Dictionary<string, AnalyzedField>(StringComparer.Ordinal);
        var order = new List<AnalyzedField>();
        foreach (BsonDocument doc in documents)
        {
            var perDoc = new Dictionary<string, List<BsonValue>>(StringComparer.Ordinal);
            var arrayPaths = new HashSet<string>(StringComparer.Ordinal);
            Collect(doc, null, maxDepth, inArray: false, perDoc, arrayPaths);
            foreach ((string path, List<BsonValue> values) in perDoc)
            {
                if (!fields.TryGetValue(path, out AnalyzedField? profile))
                {
                    int dot = path.LastIndexOf('.');
                    string? parent = dot < 0 ? null : path[..dot];
                    profile = new AnalyzedField(path, parent, arrayPaths.Contains(path));
                    fields[path] = profile;
                    order.Add(profile);
                    if (parent is not null && fields.TryGetValue(parent, out AnalyzedField? parentProfile)
                                           && !parentProfile.Children.Contains(profile.Name))
                    {
                        parentProfile.Children.Add(profile.Name);
                    }
                }
                profile.Observe(values);
            }
            ObserveElementCompleteness(doc, null, maxDepth, fields);
        }
        foreach (AnalyzedField profile in order)
        {
            profile.Total = documents.Count;
        }
        return new(documents.Count, order);
    }

    /// <summary>一份文档里每个路径取到的全部值(数组里的文档逐个展开,与 <see cref="BsonPath.Walk" /> 同口径)。</summary>
    private static void Collect(BsonDocument doc, string? prefix, int depth, bool inArray,
        Dictionary<string, List<BsonValue>> perDoc, HashSet<string> arrayPaths)
    {
        foreach (BsonElement element in doc)
        {
            string path = BsonPath.Join(prefix, element.Name);
            if (!perDoc.TryGetValue(path, out List<BsonValue>? list))
            {
                list = [];
                perDoc[path] = list;
            }
            list.Add(element.Value);
            if (inArray)
            {
                arrayPaths.Add(path);
            }
            if (depth <= 0)
            {
                continue;
            }
            if (element.Value is BsonDocument child)
            {
                Collect(child, path, depth - 1, inArray, perDoc, arrayPaths);
            }
            else if (element.Value is BsonArray array)
            {
                foreach (BsonDocument item in array.OfType<BsonDocument>())
                {
                    Collect(item, path, depth - 1, inArray: true, perDoc, arrayPaths);
                }
            }
        }
    }

    /// <summary>
    /// 数组里的文档元素"子字段齐不齐":第二遍扫描时并集已知,逐个元素看它缺不缺。
    /// 只看顶层与一层嵌套里的数组 —— 再深的数组在 08 里不单独成行。
    /// </summary>
    private static void ObserveElementCompleteness(BsonDocument doc, string? prefix, int depth, Dictionary<string, AnalyzedField> fields)
    {
        if (depth <= 0)
        {
            return;
        }
        foreach (BsonElement element in doc)
        {
            string path = BsonPath.Join(prefix, element.Name);
            if (element.Value is BsonArray array && fields.TryGetValue(path, out AnalyzedField? profile))
            {
                foreach (BsonDocument item in array.OfType<BsonDocument>())
                {
                    profile.ElementDocuments++;
                    if (profile.Children.All(item.Contains))
                    {
                        profile.CompleteElementDocuments++;
                    }
                }
            }
            else if (element.Value is BsonDocument child)
            {
                ObserveElementCompleteness(child, path, depth - 1, fields);
            }
        }
    }

    /// <summary>该字段画哪种分布图。</summary>
    public static AnalyzedViz VizOf(AnalyzedField field)
    {
        BsonKind kind = field.DominantKind;
        if (field.IsMixed)
        {
            return AnalyzedViz.Text;
        }
        return kind switch
        {
            BsonKind.Array => AnalyzedViz.Lengths,
            BsonKind.Date when field.Dates.Count >= 2 => AnalyzedViz.Timeline,
            _ when BsonKinds.IsNumeric(kind) && field.Numbers.Count >= 2 && DistinctNumbers(field) > MaxBars => AnalyzedViz.Histogram,
            BsonKind.String or BsonKind.Boolean or BsonKind.Int32 or BsonKind.Int64 or BsonKind.Double or BsonKind.Decimal128
                when IsLowCardinality(field) && field.DistinctCount <= 8 => AnalyzedViz.Bars,
            _ => AnalyzedViz.Text
        };
    }

    /// <summary>少量取值(可画比例条、可生成 enum)。</summary>
    public static bool IsLowCardinality(AnalyzedField field) =>
        !field.DistinctOverflow && field.DistinctCount > 0 && field.DistinctCount <= LowCardinality
        && field.ScalarCount >= Math.Min(10, field.Total) && field.DistinctCount < field.ScalarCount;

    /// <summary>出现最多的几个取值(按次数从多到少);占比以全部标量值为分母。</summary>
    public static IReadOnlyList<AnalyzedBucket> TopValues(AnalyzedField field, int max = MaxBars)
    {
        if (field.ScalarCount == 0)
        {
            return [];
        }
        return
        [
            .. field.Values
                .OrderByDescending(static v => v.Value)
                .ThenBy(static v => v.Key.ToString(), StringComparer.Ordinal)
                .Take(max)
                .Select(v => new AnalyzedBucket(Label(v.Key), v.Value, (double)v.Value / field.ScalarCount))
        ];
    }

    /// <summary>数值直方图:<see cref="HistogramBins" /> 根等宽柱;极端值(超出 P99 的 1.5 倍)并进最后一根。</summary>
    /// <returns>柱高、下界、上界、上界是不是截断过(轴标签写成 <c>12,000+</c>)。</returns>
    public static (double[] Bins, double Min, double Max, bool Clipped) Histogram(AnalyzedField field)
    {
        if (field.Numbers.Count == 0)
        {
            return ([], 0, 0, false);
        }
        double[] sorted = [.. field.Numbers.Order()];
        double min = sorted[0];
        double max = sorted[^1];
        double p99 = Percentile(sorted, 0.99);
        bool clipped = false;
        if (max > p99 * 1.5 && p99 > min)
        {
            max = p99;
            clipped = true;
        }
        // 全非负且最小值离 0 不远时从 0 起画:与设计稿"0 … 12,000+"一致,也更符合直觉。
        if (min > 0 && min < (max - min) * 0.1)
        {
            min = 0;
        }
        var bins = new double[HistogramBins];
        double width = max > min ? (max - min) / HistogramBins : 1;
        foreach (double v in sorted)
        {
            int index = max > min ? (int)Math.Floor((v - min) / width) : 0;
            bins[Math.Clamp(index, 0, HistogramBins - 1)]++;
        }
        return (bins, min, max, clipped);
    }

    /// <summary>
    /// 日期按天计数(跨度超过 120 天改按周)。返回每一格的计数与起点。
    /// </summary>
    public static (double[] Bins, DateTime Start, DateTime End, int DaysPerBin) Timeline(AnalyzedField field)
    {
        if (field.Dates.Count == 0)
        {
            return ([], default, default, 1);
        }
        DateTime start = field.Dates.Min().Date;
        DateTime end = field.Dates.Max().Date;
        int days = (int)(end - start).TotalDays + 1;
        int perBin = days > 120 ? 7 : 1;
        int count = Math.Max(1, (int)Math.Ceiling(days / (double)perBin));
        var bins = new double[count];
        foreach (DateTime date in field.Dates)
        {
            int index = (int)((date.Date - start).TotalDays / perBin);
            bins[Math.Clamp(index, 0, count - 1)]++;
        }
        return (bins, start, end, perBin);
    }

    /// <summary>数组长度分布:<c>0 / 1 / 2 / 3 / 4 / 5+</c>(没出现过空数组就不画 0 那根)。</summary>
    public static IReadOnlyList<AnalyzedBucket> Lengths(AnalyzedField field)
    {
        int total = field.ArrayLengths.Values.Sum();
        if (total == 0)
        {
            return [];
        }
        var list = new List<AnalyzedBucket>();
        int from = field.ArrayLengths.ContainsKey(0) ? 0 : 1;
        for (int n = from; n <= 4; n++)
        {
            int c = field.ArrayLengths.GetValueOrDefault(n);
            list.Add(new(n.ToString(CultureInfo.InvariantCulture), c, (double)c / total));
        }
        int more = field.ArrayLengths.Where(static p => p.Key >= 5).Sum(static p => p.Value);
        list.Add(new("5+", more, (double)more / total));
        return list;
    }

    /// <summary>
    /// 字段名下面那行灰字。返回文字与"是不是警告"(混合类型是橙色)。
    /// </summary>
    public static (string Text, bool Warning) Describe(AnalyzedField field, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(loc);
        BsonKind kind = field.DominantKind;
        string name = KindLabel(field);
        if (field.IsMixed)
        {
            return (loc.Format("Design_HintMixed", BsonKinds.Name(field.DominantValueKind)), true);
        }
        // "唯一"只对标识类字段有意义:金额、时间在抽样里恰好各不相同,说它"唯一"是误导。
        if (field.Path == "_id" || (field.IsUnique && field.Missing == 0 && field.ScalarCount >= 20 && !field.InArray
                                    && kind is BsonKind.String or BsonKind.ObjectId or BsonKind.Uuid or BsonKind.Binary))
        {
            return (loc.Format("Design_HintUnique", name), false);
        }
        if (field.Missing > 0 && kind is not BsonKind.Missing)
        {
            int pct = Pct(field.Missing, field.Total);
            return (loc.Format(field.Missing * 5 <= field.Total ? "Design_HintMissingDefault" : "Design_HintOptional", name, Math.Max(1, pct)), false);
        }
        switch (kind)
        {
            case BsonKind.String when IsLowCardinality(field):
                return (loc.Format("Design_HintEqualityPrefix", name, field.DistinctCount), false);
            case BsonKind.String:
                return field.DistinctOverflow || field.DistinctCount * 10 >= field.ScalarCount * 9
                    ? (loc.Format("Design_HintNearlyUnique", name), false)
                    : (loc.Format("Design_HintDistinct", name, BsonText.Grouped(field.DistinctCount)), false);
            case BsonKind.Int32 or BsonKind.Int64 or BsonKind.Double or BsonKind.Decimal128 when field.Numbers.Count > 0:
            {
                double[] sorted = [.. field.Numbers.Order()];
                return (loc.Format("Design_HintNumeric", name, Number(Percentile(sorted, 0.5)), Number(Percentile(sorted, 0.95))), false);
            }
            case BsonKind.Date when field.Dates.Count > 0:
                return (WeekendDip(field)
                    ? loc.Format("Design_HintWeekendDip", name)
                    : loc.Format("Design_HintDateSpan", name, (int)(field.Dates.Max() - field.Dates.Min()).TotalDays + 1), false);
            case BsonKind.Array:
            {
                if (field.ElementDocuments > 0)
                {
                    return (loc.Format("Design_HintArrayObjects", name, Pct(field.CompleteElementDocuments, field.ElementDocuments)), false);
                }
                int arrays = field.ArrayLengths.Values.Sum();
                double avg = arrays == 0 ? 0 : field.ArrayLengths.Sum(static p => (double)p.Key * p.Value) / arrays;
                return (loc.Format("Design_HintArrayAvg", name, avg.ToString("0.#", CultureInfo.InvariantCulture)), false);
            }
            case BsonKind.Object:
                return (loc.Format("Design_HintObject", name, field.Children.Count), false);
            case BsonKind.Boolean:
            {
                int trues = field.Values.TryGetValue(BsonBoolean.True, out int t) ? t : 0;
                return (loc.Format("Design_HintBoolean", name, Pct(trues, field.ScalarCount)), false);
            }
            case BsonKind.ObjectId:
                return (loc.Format("Design_HintReference", name, BsonText.Grouped(field.DistinctCount)), false);
            default:
                return (name, false);
        }
    }

    /// <summary>
    /// 值分布那一列的说明文字(<see cref="AnalyzedViz.Text" /> 时整列就是它)。
    /// </summary>
    public static string VizText(AnalyzedField field, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(loc);
        BsonKind kind = field.DominantKind;
        if (field.IsMixed)
        {
            IReadOnlyList<AnalyzedBucket> top = TopValues(NonNull(field), 2);
            if (top.Count == 0)
            {
                return loc.Format("Design_VizDistinct", BsonText.Grouped(field.DistinctCount));
            }
            var parts = top.Select(b => $"{b.Label} {Pct(b.Ratio)}%").ToList();
            double rest = 1 - top.Sum(static b => b.Ratio);
            if (rest > 0.005)
            {
                parts.Add(loc.Format("Design_VizOther", Pct(rest)));
            }
            return string.Join(" · ", parts);
        }
        switch (kind)
        {
            case BsonKind.ObjectId when field.IdTimes.Count > 0:
            {
                string head = field.IsUnique
                    ? loc.Format("Design_VizUniqueValues", BsonText.Grouped(field.DistinctCount))
                    : loc.Format("Design_VizDistinct", BsonText.Grouped(field.DistinctCount));
                DateTime from = field.IdTimes.Min().ToLocalTime();
                DateTime to = field.IdTimes.Max().ToLocalTime();
                string range = from.Year == to.Year
                    ? $"{from:yyyy-MM-dd} → {to:MM-dd}"
                    : $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}";
                return $"{head} · {loc.Format("Design_VizIdTime", range)}";
            }
            case BsonKind.String:
            {
                string head = field.IsUnique
                    ? loc.Format("Design_VizUniqueValues", BsonText.Grouped(field.DistinctCount))
                    : loc.Format("Design_VizDistinct", BsonText.Grouped(field.DistinctCount) + (field.DistinctOverflow ? "+" : ""));
                string example = field.Values.Keys.FirstOrDefault() is { } first ? Label(first) : "";
                string lengths = field.StringLengths.Count == 0
                    ? ""
                    : " · " + loc.Format("Design_VizLength", field.StringLengths.Min(), field.StringLengths.Max());
                return example.Length == 0 ? head + lengths : $"{head} · {loc.Format("Design_VizExample", example)}{lengths}";
            }
            case BsonKind.Object:
                return loc.Format("Design_VizChildren", string.Join(" · ", field.Children.Take(6)) + (field.Children.Count > 6 ? " …" : ""));
            case BsonKind.Null:
                return loc["Design_VizAllNull"];
            default:
                return loc.Format("Design_VizDistinct", BsonText.Grouped(field.DistinctCount));
        }
    }

    /// <summary>类型标签(<c>String</c>、<c>Array&lt;Object&gt;</c>)。</summary>
    public static string KindLabel(AnalyzedField field)
    {
        BsonKind kind = field.DominantKind;
        if (kind == BsonKind.Array && field.ElementKinds.Count > 0)
        {
            BsonKind element = field.ElementKinds.OrderByDescending(static p => p.Value).First().Key;
            return $"Array<{BsonKinds.Name(element)}>";
        }
        return BsonKinds.Name(kind);
    }

    /// <summary>
    /// 周末低谷:按星期几计数,周末日均不到工作日日均的六成,且跨度至少两周 —— 证据不够就不说。
    /// </summary>
    internal static bool WeekendDip(AnalyzedField field)
    {
        if (field.Dates.Count < 50 || (field.Dates.Max() - field.Dates.Min()).TotalDays < 14)
        {
            return false;
        }
        var byDay = new int[7];
        foreach (DateTime date in field.Dates)
        {
            byDay[(int)date.ToLocalTime().DayOfWeek]++;
        }
        double weekend = (byDay[(int)DayOfWeek.Saturday] + byDay[(int)DayOfWeek.Sunday]) / 2.0;
        double weekday = (byDay.Sum() - (weekend * 2)) / 5.0;
        return weekday > 0 && weekend < weekday * 0.6;
    }

    /// <summary>取样本分位数(已排序)。</summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }
        double rank = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    /// <summary>给人看的数字:大数取整加千分位,小数保留两位。</summary>
    internal static string Number(double value) =>
        Math.Abs(value) >= 1000 || Math.Abs(value - Math.Round(value)) < 1e-9
            ? Math.Round(value).ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>取值的显示文字:字符串加引号,其余用单行字面量。</summary>
    internal static string Label(BsonValue value) => value.BsonType switch
    {
        BsonType.String => BsonText.Quote(BsonText.OneLine(value.AsString)),
        BsonType.Decimal128 => BsonText.FormatDecimal(value.AsDecimal128),
        _ => BsonText.Inline(value)
    };

    /// <summary>百分比整数(四舍五入)。</summary>
    internal static int Pct(int part, int total) => total == 0 ? 0 : (int)Math.Round(part * 100.0 / total);

    /// <summary>百分比整数(四舍五入)。</summary>
    internal static int Pct(double ratio) => (int)Math.Round(ratio * 100);

    private static int DistinctNumbers(AnalyzedField field) => field.DistinctOverflow ? int.MaxValue : field.DistinctCount;

    /// <summary>只看非 null 取值的那份画像视角(混合类型的值分布不该被 null 占满)。</summary>
    private static AnalyzedField NonNull(AnalyzedField field)
    {
        var copy = new AnalyzedField(field.Path, field.Parent, field.InArray);
        foreach ((BsonValue value, int count) in field.Values)
        {
            if (value.IsBsonNull)
            {
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                copy.Observe([value]);
            }
        }
        return copy;
    }
}
