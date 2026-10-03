using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合设计 · Schema 分析与"由抽样生成验证规则"(设计稿 08)。</summary>
internal sealed partial class DesignTabViewModel
{
    /// <summary>抽样数的三档。</summary>
    public static IReadOnlyList<int> SampleSizes { get; } = [100, 1000, 5000];

    private int _sampleSize;
    private string _schemaFilter = "";
    private bool _filterSeeded;
    private bool _isAnalyzing;
    private string _schemaTiming = "";
    private string _sampledText = "";
    private AnalyzedSchema? _schema;
    private IReadOnlyList<BsonDocument> _sample = [];
    private BsonDocument? _generated;
    private string _generatedText = "";
    private string _genLevel = "moderate";
    private string _genAction = "warn";
    private bool _isGenChecking;
    private long _genFailCount = -1;
    private string _genCheckTitle = "";
    private string _genCheckReasons = "";

    /// <summary>抽样文档数。</summary>
    public int SampleSize
    {
        get => _sampleSize;
        set
        {
            if (SetProperty(ref _sampleSize, value))
            {
                RaisePropertyChanged(nameof(SampleSizeText));
            }
        }
    }

    /// <summary>抽样数文字(<c>1,000</c>)。</summary>
    public string SampleSizeText => BsonText.Grouped(_sampleSize);

    /// <summary>沿用的筛选条件(mongosh 写法;空 = 全集合抽样)。</summary>
    public string SchemaFilter
    {
        get => _schemaFilter;
        set => SetProperty(ref _schemaFilter, value ?? "");
    }

    /// <summary>字段行。</summary>
    public ObservableCollection<SchemaFieldRow> SchemaFields { get; } = [];

    /// <summary>正在抽样分析。</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                RaisePropertyChanged(nameof(ShowSchemaEmpty));
                UpdateStatus();
            }
        }
    }

    /// <summary>抽样结果为空(空集合或筛选不到)。</summary>
    public bool ShowSchemaEmpty => !_isAnalyzing && _schema is { Sampled: 0 };

    /// <summary>工具行右侧:<c>分析用时 0.4 s · 2026-09-26 21:08</c>。</summary>
    public string SchemaTiming
    {
        get => _schemaTiming;
        private set => SetProperty(ref _schemaTiming, value);
    }

    /// <summary>抽到了多少(<c>抽样 1,000 / 1,284,902</c>)。</summary>
    public string SampledText
    {
        get => _sampledText;
        private set => SetProperty(ref _sampledText, value);
    }

    /// <summary>最近一次的分析结果。</summary>
    public AnalyzedSchema? Schema
    {
        get => _schema;
        private set
        {
            if (SetProperty(ref _schema, value))
            {
                RaisePropertyChanged(nameof(ShowSchemaEmpty));
            }
        }
    }

    /// <summary>抽样到的文档(验证规则页的试写文档以其中一份起头)。</summary>
    internal IReadOnlyList<BsonDocument> Sample => _sample;

    /// <summary>由抽样生成的 validator。</summary>
    public BsonDocument? Generated
    {
        get => _generated;
        private set
        {
            if (SetProperty(ref _generated, value))
            {
                RaisePropertyChanged(nameof(HasGenerated));
            }
        }
    }

    /// <summary>有生成结果。</summary>
    public bool HasGenerated => _generated is not null;

    /// <summary>生成规则的预览文本。</summary>
    public string GeneratedText
    {
        get => _generatedText;
        private set => SetProperty(ref _generatedText, value);
    }

    /// <summary>生成规则用的 validationLevel(默认 moderate:只校验新写入与本来就合规的文档的更新)。</summary>
    public string GenLevel
    {
        get => _genLevel;
        set
        {
            if (SetProperty(ref _genLevel, value ?? "moderate"))
            {
                RaisePropertyChanged(nameof(GenCheckReasons));
            }
        }
    }

    /// <summary>生成规则用的 validationAction(默认 warn:先观察,不拦写入)。</summary>
    public string GenAction
    {
        get => _genAction;
        set => SetProperty(ref _genAction, value ?? "warn");
    }

    /// <summary>正在做现有数据预检。</summary>
    public bool IsGenChecking
    {
        get => _isGenChecking;
        private set => SetProperty(ref _isGenChecking, value);
    }

    /// <summary>预检不通过的份数;未检为 -1。</summary>
    public long GenFailCount
    {
        get => _genFailCount;
        private set
        {
            if (SetProperty(ref _genFailCount, value))
            {
                RaisePropertiesChanged(nameof(GenHasFailures), nameof(GenAllPass), nameof(GenFailLink));
            }
        }
    }

    /// <summary>有不通过的文档(橙色预检框)。</summary>
    public bool GenHasFailures => _genFailCount > 0;

    /// <summary>全部通过(绿色预检框)。</summary>
    public bool GenAllPass => _genFailCount == 0;

    /// <summary>预检标题。</summary>
    public string GenCheckTitle
    {
        get => _genCheckTitle;
        private set => SetProperty(ref _genCheckTitle, value);
    }

    /// <summary>主要原因 + 建议。</summary>
    public string GenCheckReasons
    {
        get => _genCheckReasons;
        private set => SetProperty(ref _genCheckReasons, value);
    }

    /// <summary>「在查询编辑器中查看这 37 份文档 →」。</summary>
    public string GenFailLink => Loc.Format("Design_GenViewFailures", BsonText.Grouped(Math.Max(0, _genFailCount)));

    /// <summary>重新分析。</summary>
    public AsyncCommand AnalyzeCommand { get; private set; } = null!;

    /// <summary>换抽样数(并重新分析)。</summary>
    public AsyncCommand<string> SetSampleCommand { get; private set; } = null!;

    /// <summary>复制生成的规则。</summary>
    public AsyncCommand CopyGeneratedCommand { get; private set; } = null!;

    /// <summary>在查询编辑器里查看不符合的文档。</summary>
    public RelayCommand ViewGeneratedFailuresCommand { get; private set; } = null!;

    /// <summary>在验证规则页里调整。</summary>
    public RelayCommand EditGeneratedCommand { get; private set; } = null!;

    /// <summary>直接应用为验证规则。</summary>
    public AsyncCommand ApplyGeneratedCommand { get; private set; } = null!;

    private void InitializeSchema()
    {
        AnalyzeCommand = new(AnalyzeAsync);
        SetSampleCommand = new(async text =>
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size))
            {
                SampleSize = size;
                await AnalyzeAsync().ConfigureAwait(true);
            }
        });
        CopyGeneratedCommand = new(async () =>
        {
            if (_generated is not null)
            {
                await Workspace.CopyAsync(JsonSchemaGenerator.Format(_generated)).ConfigureAwait(true);
                Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
            }
        });
        ViewGeneratedFailuresCommand = new(() =>
        {
            if (_generated is not null)
            {
                Workspace.OpenQuery(Database, FailingQuery(_generated), run: true);
            }
        });
        EditGeneratedCommand = new(() =>
        {
            if (_generated is not null)
            {
                LoadRuleIntoEditor(_generated, _genLevel, _genAction);
                Page = DesignPage.Validation;
            }
        });
        ApplyGeneratedCommand = new(() => _generated is null
            ? Task.CompletedTask
            : ApplyValidatorAsync(_generated, _genLevel, _genAction, _genFailCount));
    }

    /// <summary>不符合某条 validator 的文档的查询(<c>db.orders.find({ $nor: [ … ] })</c>)。</summary>
    internal string FailingQuery(BsonDocument validator) =>
        $"{ShellRef}.find({BsonText.Literal(new BsonDocument("$nor", new BsonArray { validator }))})";

    /// <summary>
    /// 抽样并分析:<c>$match</c>(沿用的筛选)→ <c>$sample</c>。统计在后台线程算,算完一次性换掉整张表。
    /// 第一次分析时把这个集合最近一次用过的筛选条件带进来(「筛选条件沿用」)。
    /// </summary>
    internal async Task AnalyzeAsync()
    {
        if (_isAnalyzing || _disposed)
        {
            return;
        }
        IsAnalyzing = true;
        try
        {
            if (!_filterSeeded)
            {
                _filterSeeded = true;
                IReadOnlyList<string> history = await Workspace.Store.LoadFilterHistoryAsync(Workspace.ConnectionKey, Namespace).ConfigureAwait(true);
                if (_schemaFilter.Length == 0 && history.Count > 0)
                {
                    SchemaFilter = history[0];
                }
            }
            BsonDocument filter = ShellJson.ParseDocument(_schemaFilter);
            var pipeline = new List<BsonDocument>();
            if (filter.ElementCount > 0)
            {
                pipeline.Add(new BsonDocument("$match", filter));
            }
            pipeline.Add(new BsonDocument("$sample", new BsonDocument("size", _sampleSize)));
            var watch = Stopwatch.StartNew();
            var options = new AggregateOptions
            {
                MaxTime = TimeSpan.FromMilliseconds(Math.Max(1000, Workspace.Connection.Settings.MaxTimeMs)),
                Comment = "velashell:design-sample"
            };
            using IAsyncCursor<BsonDocument> cursor = await Workspace.Connection.Collection(Database, CollectionName)
                .AggregateAsync<BsonDocument>(pipeline, options, Lifetime).ConfigureAwait(true);
            List<BsonDocument> docs = await cursor.ToListAsync(Lifetime).ConfigureAwait(true);
            AnalyzedSchema schema = await Task.Run(() => SchemaAnalyzer.Analyze(docs), Lifetime).ConfigureAwait(true);
            List<SchemaFieldRow> rows = await Task.Run(() => schema.Fields.Select(BuildSchemaRow).ToList(), Lifetime).ConfigureAwait(true);
            watch.Stop();
            long? total = await Workspace.Connection.EstimatedCountAsync(Database, CollectionName, Lifetime).ConfigureAwait(true);

            _sample = docs;
            Schema = schema;
            SchemaFields.Clear();
            foreach (SchemaFieldRow row in rows)
            {
                SchemaFields.Add(row);
            }
            SchemaTiming = Loc.Format("Design_SchemaTiming",
                watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            SampledText = Loc.Format("Design_Sampled", BsonText.Grouped(docs.Count), total is { } t ? BsonText.Grouped(t) : "?");
            if (docs.Count > 0)
            {
                Generated = JsonSchemaGenerator.Generate(schema);
                GeneratedText = JsonSchemaGenerator.Format(Generated, width: 48, compactRoot: true);
            }
            else
            {
                Generated = null;
                GeneratedText = "";
            }
            OnSchemaForNewIndex(schema);
            OnSchemaForValidation();
            UpdateStatus();
            if (Generated is not null)
            {
                _ = PrecheckGeneratedAsync(Generated);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    /// <summary>一个字段画像 → 表格一行。</summary>
    private SchemaFieldRow BuildSchemaRow(AnalyzedField field)
    {
        (string hint, bool warn) = SchemaAnalyzer.Describe(field, Loc);
        var pieces = new List<BarPiece>();
        var legend = new List<LegendItem>();
        foreach (BsonKind kind in field.PresentKinds)
        {
            double ratio = field.Total == 0 ? 0 : (double)field.Kinds[kind] / field.Total;
            string token = BsonKinds.ColorToken(kind);
            pieces.Add(new(ratio, token));
            legend.Add(new($"{BsonKinds.Name(kind)} {Percent(ratio)}", token));
        }
        if (field.Missing > 0)
        {
            double ratio = (double)field.Missing / field.Total;
            pieces.Add(new(ratio, "VelaTextMuted"));
            legend.Add(new(Loc.Format("Design_LegendMissing", Percent(ratio)), "VelaTextMuted"));
        }

        AnalyzedViz viz = SchemaAnalyzer.VizOf(field);
        IReadOnlyList<ValueBar> bars = [];
        IReadOnlyList<ChartSeries>? histogram = null;
        IReadOnlyList<string>? labels = null;
        string axisMin = "", axisMax = "";
        IReadOnlyList<LengthBar> lengths = [];
        switch (viz)
        {
            case AnalyzedViz.Bars:
            {
                string token = BsonKinds.ColorToken(field.DominantKind);
                bars =
                [
                    .. field.Values
                        .OrderByDescending(static v => v.Value)
                        .Take(SchemaAnalyzer.MaxBars)
                        .Select(v =>
                        {
                            double ratio = (double)v.Value / field.ScalarCount;
                            string label = v.Key.IsString ? BsonText.OneLine(v.Key.AsString) : SchemaAnalyzer.Label(v.Key);
                            return new ValueBar(label, Percent(ratio), ratio, token);
                        })
                ];
                break;
            }
            case AnalyzedViz.Histogram:
            {
                (double[] bins, double min, double max, bool clipped) = SchemaAnalyzer.Histogram(field);
                histogram = [new ChartSeries(field.Path, "VelaShellGreen", bins)];
                double width = (max - min) / Math.Max(1, bins.Length);
                labels = [.. bins.Select((_, i) => $"{SchemaAnalyzer.Number(min + (width * i))} – {SchemaAnalyzer.Number(min + (width * (i + 1)))}")];
                axisMin = SchemaAnalyzer.Number(min);
                axisMax = SchemaAnalyzer.Number(max) + (clipped ? "+" : "");
                break;
            }
            case AnalyzedViz.Timeline:
            {
                (double[] bins, DateTime start, DateTime end, int perBin) = SchemaAnalyzer.Timeline(field);
                histogram = [new ChartSeries(field.Path, "VelaShellMagenta", bins)];
                labels = [.. bins.Select((_, i) => start.AddDays(i * perBin).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))];
                axisMin = start.ToString("MM-dd", CultureInfo.InvariantCulture);
                axisMax = end.ToString("MM-dd", CultureInfo.InvariantCulture);
                break;
            }
            case AnalyzedViz.Lengths:
            {
                IReadOnlyList<AnalyzedBucket> buckets = SchemaAnalyzer.Lengths(field);
                double peak = buckets.Count == 0 ? 1 : Math.Max(0.0001, buckets.Max(static b => b.Ratio));
                lengths = [.. buckets.Select(b => new LengthBar(b.Label, Math.Round(36 * b.Ratio / peak), b.Count))];
                break;
            }
        }
        return new SchemaFieldRow
        {
            Path = field.Path,
            Hint = hint,
            HintWarn = warn,
            Pieces = pieces,
            Legend = legend,
            Viz = viz,
            VizText = viz == AnalyzedViz.Text ? SchemaAnalyzer.VizText(field, Loc) : "",
            Bars = bars,
            Histogram = histogram,
            HistogramLabels = labels,
            AxisMin = axisMin,
            AxisMax = axisMax,
            Lengths = lengths
        };
    }

    /// <summary>百分比文字:不足 1% 但非零的写成 <c>&lt;1%</c>,别让一段可见的比例条配一个 0%。</summary>
    private static string Percent(double ratio) =>
        ratio is > 0 and < 0.005 ? "<1%" : Math.Round(ratio * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// 现有数据预检:服务器端 <c>countDocuments({ $nor: [ validator ] })</c> 数全集合不符合的份数
    /// (只读,带超时 —— 大集合上可能要扫一阵);再取最多 200 份不符合的文档,用客户端校验器归纳主要原因。
    /// </summary>
    private async Task PrecheckGeneratedAsync(BsonDocument validator)
    {
        IsGenChecking = true;
        try
        {
            (long count, IReadOnlyList<(string Label, int Count)> reasons) = await CountFailuresAsync(validator).ConfigureAwait(true);
            if (!ReferenceEquals(validator, _generated))
            {
                return;
            }
            GenFailCount = count;
            GenCheckTitle = count == 0
                ? Loc["Design_GenAllPass"]
                : Loc.Format("Design_GenFailTitle", BsonText.Grouped(count));
            GenCheckReasons = count == 0
                ? Loc["Design_GenAllPassDetail"]
                : (reasons.Count > 0
                      ? Loc.Format("Design_GenReasons", string.Join(" · ", reasons.Take(3).Select(static r => $"{r.Label} ({r.Count})")))
                      : "")
                  + Loc["Design_GenAdvice"];
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            GenFailCount = -1;
            GenCheckTitle = Loc.Format("Common_Failed", MongoConnector.Describe(ex));
            GenCheckReasons = "";
        }
        finally
        {
            IsGenChecking = false;
        }
    }

    /// <summary>
    /// 全集合不符合份数 + 主要原因。计数超时(大集合)就退回抽样口径。
    /// </summary>
    internal async Task<(long Count, IReadOnlyList<(string Label, int Count)> Reasons)> CountFailuresAsync(BsonDocument validator)
    {
        IMongoCollection<BsonDocument> collection = Workspace.Connection.Collection(Database, CollectionName);
        var nor = new BsonDocument("$nor", new BsonArray { validator });
        long count;
        try
        {
            count = await collection.CountDocumentsAsync(nor, new CountOptions { MaxTime = TimeSpan.FromSeconds(15), Comment = "velashell:design-precheck" }, Lifetime)
                .ConfigureAwait(true);
        }
        catch (MongoExecutionTimeoutException)
        {
            count = _sample.Count(doc => !SampleMatches(validator, doc));
        }
        List<BsonDocument> failing = count == 0
            ? []
            : await collection.Find(nor).Limit(200).ToListAsync(Lifetime).ConfigureAwait(true);
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (BsonDocument doc in failing)
        {
            foreach (string label in JsonSchemaValidator.Validate(validator, doc, Loc).Select(ReasonLabel).Distinct(StringComparer.Ordinal))
            {
                reasons[label] = reasons.GetValueOrDefault(label) + 1;
            }
        }
        return (count, [.. reasons.OrderByDescending(static r => r.Value).Select(static r => (r.Key, r.Value))]);
    }

    /// <summary>客户端粗判(只在服务器计数超时时兜底用)。</summary>
    private bool SampleMatches(BsonDocument validator, BsonDocument doc) => JsonSchemaValidator.Validate(validator, doc, Loc).Count == 0;

    /// <summary>一条违规 → 原因短语(<c>customer.level 缺失</c>、<c>total 须 ≥ 0</c>)。</summary>
    private string ReasonLabel(SchemaViolation violation) =>
        violation.Keyword == "required"
            ? Loc.Format("Design_ReasonMissing", NormalizePath(violation.Path))
            : violation.Message;

    /// <summary>去掉数组下标(<c>items.0.qty</c> → <c>items.qty</c>):同一类原因不该按下标拆成几十条。</summary>
    internal static string NormalizePath(string path) =>
        string.Join(".", path.Split('.').Where(static s => s.Length == 0 || !s.All(char.IsAsciiDigit)));
}
