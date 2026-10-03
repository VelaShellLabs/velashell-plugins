using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>验证规则的一个历史版本(每次应用前自动存下旧规则)。</summary>
/// <param name="At">保存时间。</param>
/// <param name="Level">validationLevel。</param>
/// <param name="Action">validationAction。</param>
/// <param name="Text">规则文本(mongosh 写法)。</param>
internal sealed record RuleVersion(DateTimeOffset At, string Level, string Action, string Text)
{
    /// <summary>菜单里的一行:<c>2026-09-26 21:08 · moderate · warn</c>。</summary>
    public string Label => $"{At.ToLocalTime():yyyy-MM-dd HH:mm} · {Level} · {Action}";
}

/// <summary>集合设计 · 验证规则(设计稿 16)。</summary>
internal sealed partial class DesignTabViewModel
{
    /// <summary>编辑器里规则行的最大宽度(超过才折行)。</summary>
    private const int RuleWidth = 88;

    /// <summary>规则测试的抽样数。</summary>
    public const int PrecheckSampleSize = 1000;

    private static readonly string[] BsonTypeAliases =
    [
        "string", "int", "long", "double", "decimal", "number", "bool", "date", "objectId", "object", "array", "null",
        "binData", "regex", "timestamp"
    ];

    private static readonly string[] SchemaKeywords =
    [
        "bsonType", "required", "properties", "enum", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "items", "minItems", "maxItems", "uniqueItems", "additionalProperties",
        "patternProperties", "minProperties", "maxProperties", "description", "title", "anyOf", "allOf", "oneOf", "not"
    ];

    private bool _editorReady;
    private bool _validationLoading;
    private string _ruleText = "";
    private string _appliedText = "";
    private string _ruleLevel = "strict";
    private string _ruleAction = "error";
    private string _appliedLevel = "strict";
    private string _appliedAction = "error";
    private BsonDocument? _ruleValidator;
    private IReadOnlyList<EditorDiagnostic> _ruleDiagnostics = [];
    private bool _ruleSyntaxOk = true;
    private string _ruleSyntaxText = "";
    private bool _isPrechecking;
    private bool _precheckRan;
    private bool _precheckAgain;
    private bool _precheckResample;
    private List<BsonValue> _precheckIds = [];
    private long _passCount;
    private long _failCount = -1;
    private IReadOnlyList<BarPiece> _passPieces = [];
    private string _tryDocText = "";
    private IReadOnlyList<EditorDiagnostic> _tryDiagnostics = [];
    private IReadOnlyList<LineMark> _tryMarks = [];
    private bool? _tryPassed;
    private string _tryTitle = "";
    private string _tryDetail = "";
    private DispatcherTimer? _tryTimer;
    private DispatcherTimer? _ruleTimer;
    private int _tryVersion;

    /// <summary>编辑器里的规则文本(mongosh 写法)。</summary>
    public string RuleText
    {
        get => _ruleText;
        set
        {
            if (SetProperty(ref _ruleText, value ?? ""))
            {
                ParseRule();
                RecomputeRuleState();
                Restart(ref _ruleTimer, TimeSpan.FromMilliseconds(1200), () => _ = RunPrecheckAsync(resample: false));
                Restart(ref _tryTimer, TimeSpan.FromMilliseconds(350), () => _ = EvaluateTryDocAsync());
            }
        }
    }

    /// <summary>validationLevel(off / moderate / strict)。</summary>
    public string RuleLevel
    {
        get => _ruleLevel;
        set
        {
            if (SetProperty(ref _ruleLevel, value ?? "strict"))
            {
                RecomputeRuleState();
            }
        }
    }

    /// <summary>validationAction(warn / error)。</summary>
    public string RuleAction
    {
        get => _ruleAction;
        set
        {
            if (SetProperty(ref _ruleAction, value ?? "error"))
            {
                RecomputeRuleState();
                Restart(ref _tryTimer, TimeSpan.FromMilliseconds(50), () => _ = EvaluateTryDocAsync());
            }
        }
    }

    /// <summary>编辑器诊断(语法错误)。</summary>
    public IReadOnlyList<EditorDiagnostic> RuleDiagnostics
    {
        get => _ruleDiagnostics;
        private set => SetProperty(ref _ruleDiagnostics, value);
    }

    /// <summary>语法正确。</summary>
    public bool RuleSyntaxOk
    {
        get => _ruleSyntaxOk;
        private set
        {
            if (SetProperty(ref _ruleSyntaxOk, value))
            {
                RaisePropertyChanged(nameof(RuleSyntaxBad));
                ApplyRuleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>语法有误。</summary>
    public bool RuleSyntaxBad => !_ruleSyntaxOk;

    /// <summary>工具行右侧的语法状态文字。</summary>
    public string RuleSyntaxText
    {
        get => _ruleSyntaxText;
        private set => SetProperty(ref _ruleSyntaxText, value);
    }

    /// <summary>底部命令预览。</summary>
    public string RuleCommand =>
        $"db.runCommand({{ collMod: {QuotedCollection}, validator: {{…}}, validationLevel: {BsonText.Quote(_ruleLevel)}, validationAction: {BsonText.Quote(_ruleAction)} }})";

    /// <summary>子页条上验证规则旁的小字。</summary>
    public string ValidationTabHint => _live.Validator switch
    {
        null => Loc["Design_NoRule"],
        { } v when v.Contains("$jsonSchema") => "$jsonSchema",
        _ => Loc["Design_QueryRule"]
    };

    /// <summary>能设验证规则(视图与时序集合不行)。</summary>
    public bool CanValidate => !IsView && !IsTimeSeries;

    /// <summary>规则测试:正在预检。</summary>
    public bool IsPrechecking
    {
        get => _isPrechecking;
        private set => SetProperty(ref _isPrechecking, value);
    }

    /// <summary>预检抽了多少份(<c>抽样 1,000</c>)。</summary>
    public string PrecheckSampleText => Loc.Format("Design_PrecheckSample", BsonText.Grouped(_precheckIds.Count > 0 ? _precheckIds.Count : PrecheckSampleSize));

    /// <summary>通过份数文字。</summary>
    public string PassText => Loc.Format("Design_Pass", BsonText.Grouped(_passCount));

    /// <summary>不通过份数文字。</summary>
    public string FailText => Loc.Format("Design_Fail", BsonText.Grouped(Math.Max(0, _failCount)));

    /// <summary>通过 / 不通过比例条。</summary>
    public IReadOnlyList<BarPiece> PassPieces
    {
        get => _passPieces;
        private set => SetProperty(ref _passPieces, value);
    }

    /// <summary>不通过原因(按 关键字 + 路径 分组)。</summary>
    public ObservableCollection<FailureReasonRow> FailureReasons { get; } = [];

    /// <summary>有不通过原因。</summary>
    public bool HasFailureReasons => FailureReasons.Count > 0;

    /// <summary>试写文档(mongosh 写法)。</summary>
    public string TryDocText
    {
        get => _tryDocText;
        set
        {
            if (SetProperty(ref _tryDocText, value ?? ""))
            {
                Restart(ref _tryTimer, TimeSpan.FromMilliseconds(350), () => _ = EvaluateTryDocAsync());
            }
        }
    }

    /// <summary>试写文档的诊断(违规的值下面画波浪线)。</summary>
    public IReadOnlyList<EditorDiagnostic> TryDiagnostics
    {
        get => _tryDiagnostics;
        private set => SetProperty(ref _tryDiagnostics, value);
    }

    /// <summary>试写文档的整行标记(违规的行红底)。</summary>
    public IReadOnlyList<LineMark> TryMarks
    {
        get => _tryMarks;
        private set => SetProperty(ref _tryMarks, value);
    }

    /// <summary>试写结果:通过 / 不通过 / 未知(语法错误)。</summary>
    public bool? TryPassed
    {
        get => _tryPassed;
        private set
        {
            if (SetProperty(ref _tryPassed, value))
            {
                RaisePropertiesChanged(nameof(TryOk), nameof(TryFailed), nameof(TryUnknown));
            }
        }
    }

    /// <summary>通过(绿框)。</summary>
    public bool TryOk => _tryPassed == true;

    /// <summary>不通过(红框)。</summary>
    public bool TryFailed => _tryPassed == false;

    /// <summary>没法判断(灰框)。</summary>
    public bool TryUnknown => _tryPassed is null && _tryTitle.Length > 0;

    /// <summary>结果框标题。</summary>
    public string TryTitle
    {
        get => _tryTitle;
        private set
        {
            if (SetProperty(ref _tryTitle, value))
            {
                RaisePropertyChanged(nameof(TryUnknown));
            }
        }
    }

    /// <summary>结果框明细(每条违规一行)。</summary>
    public string TryDetail
    {
        get => _tryDetail;
        private set
        {
            if (SetProperty(ref _tryDetail, value))
            {
                RaisePropertyChanged(nameof(HasTryDetail));
            }
        }
    }

    /// <summary>有明细。</summary>
    public bool HasTryDetail => _tryDetail.Length > 0;

    /// <summary>历史版本(新的在前)。</summary>
    public ObservableCollection<RuleVersion> RuleHistory { get; } = [];

    /// <summary>规则编辑器的补全($jsonSchema 关键字、bsonType 别名、抽样字段名)。</summary>
    public Func<CompletionRequest, Task<CompletionSet?>> RuleCompletion => CompleteRuleAsync;

    /// <summary>选 validationLevel。</summary>
    public RelayCommand<string> SetLevelCommand { get; private set; } = null!;

    /// <summary>选 validationAction。</summary>
    public RelayCommand<string> SetActionCommand { get; private set; } = null!;

    /// <summary>从 Schema 分析生成。</summary>
    public AsyncCommand GenerateRuleCommand { get; private set; } = null!;

    /// <summary>格式化。</summary>
    public RelayCommand FormatRuleCommand { get; private set; } = null!;

    /// <summary>恢复一个历史版本(进编辑器,不直接应用)。</summary>
    public RelayCommand<RuleVersion> RestoreVersionCommand { get; private set; } = null!;

    /// <summary>放弃修改。</summary>
    public RelayCommand DiscardRuleCommand { get; private set; } = null!;

    /// <summary>应用规则。</summary>
    public AsyncCommand ApplyRuleCommand { get; private set; } = null!;

    /// <summary>重新抽样预检。</summary>
    public AsyncCommand RefreshPrecheckCommand { get; private set; } = null!;

    /// <summary>在网格中查看某类不通过的文档。</summary>
    public RelayCommand<FailureReasonRow> OpenFailureCommand { get; private set; } = null!;

    private void InitializeValidation()
    {
        SetLevelCommand = new(level => RuleLevel = level);
        SetActionCommand = new(action => RuleAction = action);
        GenerateRuleCommand = new(async () =>
        {
            if (_generated is null)
            {
                await AnalyzeAsync().ConfigureAwait(true);
            }
            if (_generated is not null)
            {
                LoadRuleIntoEditor(_generated, _genLevel, _genAction);
            }
        });
        FormatRuleCommand = new(() =>
        {
            if (_ruleValidator is not null)
            {
                RuleText = JsonSchemaGenerator.Format(_ruleValidator, RuleWidth);
            }
        });
        RestoreVersionCommand = new(version =>
        {
            RuleText = version.Text;
            RuleLevel = version.Level;
            RuleAction = version.Action;
        });
        DiscardRuleCommand = new(() =>
        {
            RuleText = _appliedText;
            RuleLevel = _appliedLevel;
            RuleAction = _appliedAction;
        });
        ApplyRuleCommand = new(() => _ruleValidator is null
            ? Task.CompletedTask
            : ApplyValidatorAsync(_ruleValidator, _ruleLevel, _ruleAction, _failCount), () => _ruleSyntaxOk);
        RefreshPrecheckCommand = new(() => RunPrecheckAsync(resample: true));
        OpenFailureCommand = new(row => Workspace.OpenCollection(Database, CollectionName, row.Filter));
        FailureReasons.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(HasFailureReasons));
    }

    /// <summary>没有验证规则时编辑器里的起手式。</summary>
    /// <remarks>不写 <c>required: []</c>:服务器不接受空的 required 数组,起手式本身就得是一条能用的规则。</remarks>
    private static string RuleTemplate =>
        "{\n  $jsonSchema: {\n    bsonType: \"object\",\n    properties: {}\n  }\n}";

    /// <summary>
    /// 加载验证规则页:编辑器换成当前生效的规则,读历史版本,跑一次抽样预检,备一份试写文档。
    /// </summary>
    private async Task LoadValidationAsync(bool resetEditor)
    {
        if (_validationLoading)
        {
            return;
        }
        _validationLoading = true;
        try
        {
            if (resetEditor)
            {
                InitializeEditorFromLive();
            }
            await LoadHistoryAsync().ConfigureAwait(true);
            await RunPrecheckAsync(resample: !_precheckRan).ConfigureAwait(true);
            if (_tryDocText.Length == 0)
            {
                await SeedTryDocAsync().ConfigureAwait(true);
            }
            _validationLoaded = true;
        }
        finally
        {
            _validationLoading = false;
        }
    }

    /// <summary>编辑器 ← 当前生效的规则(重探过的集合信息)。</summary>
    private void InitializeEditorFromLive()
    {
        BsonDocument? validator = _live.Validator;
        _appliedLevel = _live.ValidationLevel;
        _appliedAction = _live.ValidationAction;
        _appliedText = validator is null ? RuleTemplate : JsonSchemaGenerator.Format(validator, RuleWidth);
        _editorReady = true;
        RuleText = _appliedText;
        RuleLevel = _appliedLevel;
        RuleAction = _appliedAction;
        RecomputeRuleState();
        RaisePropertyChanged(nameof(ValidationTabHint));
        // 载入不算改动:不必为它再排一次预检(调用方会自己跑)。
        _ruleTimer?.Stop();
    }

    /// <summary>把一条规则放进编辑器(从 Schema 分析生成 / 在编辑器中调整 / 恢复历史版本)。不应用。</summary>
    internal void LoadRuleIntoEditor(BsonDocument validator, string level, string action)
    {
        if (!_editorReady)
        {
            InitializeEditorFromLive();
        }
        RuleText = JsonSchemaGenerator.Format(validator, RuleWidth);
        RuleLevel = level;
        RuleAction = action;
    }

    /// <summary>解析编辑器文本 → validator + 诊断。</summary>
    private void ParseRule()
    {
        var diagnostics = new List<EditorDiagnostic>();
        foreach (ShellDiagnostic d in ShellJson.Diagnose(_ruleText))
        {
            diagnostics.Add(new(d.Offset, Math.Max(1, d.Length), Loc.Format(d.MessageKey, d.Argument), DiagnosticSeverity.Error, d.Fix));
        }
        BsonDocument? parsed = null;
        string error = "";
        if (diagnostics.Count == 0)
        {
            try
            {
                parsed = ShellJson.ParseDocument(_ruleText);
            }
            catch (ShellJsonException ex)
            {
                error = ex.Message;
                int offset = ex.Offset >= 0 ? Math.Min(ex.Offset, Math.Max(0, _ruleText.Length - 1)) : 0;
                diagnostics.Add(new(offset, 1, ex.Message));
            }
        }
        else
        {
            error = diagnostics[0].Message;
        }
        _ruleValidator = parsed;
        RuleDiagnostics = diagnostics;
        RuleSyntaxOk = parsed is not null;
        RuleSyntaxText = parsed is not null ? Loc["Design_SyntaxOk"] : Loc.Format("Design_SyntaxError", error);
    }

    /// <summary>改动标记、命令预览、状态栏。</summary>
    private void RecomputeRuleState()
    {
        IsModified = _editorReady
                     && (!string.Equals(_ruleText.Trim(), _appliedText.Trim(), StringComparison.Ordinal)
                         || _ruleLevel != _appliedLevel || _ruleAction != _appliedAction);
        RaisePropertyChanged(nameof(RuleCommand));
        UpdateStatus();
    }

    /// <summary>
    /// 规则测试 · 对现有文档预检。
    /// <para>
    /// 抽样只抽一次 <c>_id</c>(<c>$sample</c> + 只投影 <c>_id</c>),之后每次改规则都用
    /// <c>{ _id: { $in: 抽样 }, $nor: [ 规则 ] }</c> 在服务器上判 —— 同一批文档反复判,
    /// 改一个关键字前后的计数才可比;判定以服务器为准,客户端校验器只负责归纳"为什么"。
    /// </para>
    /// </summary>
    internal async Task RunPrecheckAsync(bool resample)
    {
        if (!_editorReady || _disposed || !CanValidate)
        {
            return;
        }
        if (_isPrechecking)
        {
            // 正在判上一版规则:记一笔,判完马上用最新的规则再判一次(而不是丢掉这次改动)。
            _precheckAgain = true;
            _precheckResample |= resample;
            return;
        }
        BsonDocument? validator = _ruleValidator;
        if (validator is null)
        {
            return;
        }
        IsPrechecking = true;
        try
        {
            IMongoCollection<BsonDocument> collection = Workspace.Connection.Collection(Database, CollectionName);
            if (resample || _precheckIds.Count == 0)
            {
                var pipeline = new[]
                {
                    new BsonDocument("$sample", new BsonDocument("size", PrecheckSampleSize)),
                    new BsonDocument("$project", new BsonDocument("_id", 1))
                };
                using IAsyncCursor<BsonDocument> cursor = await collection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: Lifetime).ConfigureAwait(true);
                _precheckIds = [.. (await cursor.ToListAsync(Lifetime).ConfigureAwait(true)).Select(static d => d["_id"])];
                RaisePropertyChanged(nameof(PrecheckSampleText));
            }
            _precheckRan = true;
            if (_precheckIds.Count == 0)
            {
                SetPrecheck(0, 0, []);
                return;
            }
            List<BsonDocument> failing = [];
            if (validator.ElementCount > 0)
            {
                var filter = new BsonDocument
                {
                    { "_id", new BsonDocument("$in", new BsonArray(_precheckIds)) },
                    { "$nor", new BsonArray { validator } }
                };
                failing = await collection.Find(filter).Limit(PrecheckSampleSize).ToListAsync(Lifetime).ConfigureAwait(true);
            }
            if (!ReferenceEquals(validator, _ruleValidator))
            {
                // 判的途中规则又改了:这份结果作废,按最新规则再判一次。
                _precheckAgain = true;
                return;
            }
            SetPrecheck(_precheckIds.Count - failing.Count, failing.Count, GroupFailures(validator, failing));
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
        finally
        {
            IsPrechecking = false;
            if (_precheckAgain && !_disposed)
            {
                bool again = _precheckResample;
                _precheckAgain = false;
                _precheckResample = false;
                _ = RunPrecheckAsync(again);
            }
        }
    }

    private void SetPrecheck(long pass, long fail, IReadOnlyList<FailureReasonRow> reasons)
    {
        _passCount = pass;
        _failCount = fail;
        long total = pass + fail;
        PassPieces = total == 0
            ? []
            : [new BarPiece((double)pass / total, "VelaStatusConnected"), new BarPiece((double)fail / total, "VelaError")];
        FailureReasons.Clear();
        foreach (FailureReasonRow row in reasons)
        {
            FailureReasons.Add(row);
        }
        RaisePropertiesChanged(nameof(PassText), nameof(FailText));
    }

    /// <summary>
    /// 不通过的文档 → 按(路径, 关键字)分组,每组记份数、一个示例 <c>_id</c>、在网格中查看用的筛选。
    /// 客户端校验器说不出原因的(服务器支持而它不认的关键字)单独归一组。
    /// </summary>
    private IReadOnlyList<FailureReasonRow> GroupFailures(BsonDocument validator, IReadOnlyList<BsonDocument> failing)
    {
        var groups = new Dictionary<(string Path, string Keyword), List<BsonValue>>();
        var order = new List<(string, string)>();
        foreach (BsonDocument doc in failing)
        {
            BsonValue id = doc.GetValue("_id", BsonNull.Value);
            var keys = JsonSchemaValidator.Validate(validator, doc, Loc)
                .Select(static v => (NormalizePath(v.Path), v.Keyword))
                .Distinct()
                .ToList();
            if (keys.Count == 0)
            {
                keys.Add(("", ""));
            }
            foreach ((string, string) key in keys)
            {
                if (!groups.TryGetValue(key, out List<BsonValue>? ids))
                {
                    ids = [];
                    groups[key] = ids;
                    order.Add(key);
                }
                ids.Add(id);
            }
        }
        return
        [
            .. order
                .OrderByDescending(k => groups[k].Count)
                .Select(k =>
                {
                    List<BsonValue> ids = groups[k];
                    BsonValue first = ids[0];
                    string example = first.IsObjectId ? BsonText.Shorten(first.AsObjectId.ToString()) : BsonText.Inline(first);
                    BsonDocument filter = ids.Count <= 200
                        ? new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids)))
                        : new BsonDocument("$nor", new BsonArray { validator });
                    return new FailureReasonRow
                    {
                        Path = k.Item1.Length == 0 ? Loc["Design_ReasonServer"] : k.Item1,
                        Keyword = KeywordSummary(validator, k.Item1, k.Item2),
                        RawKeyword = k.Item2,
                        Count = ids.Count,
                        CountText = Loc.Format("Design_Copies", BsonText.Grouped(ids.Count)),
                        Example = Loc.Format("Design_Example", example),
                        Filter = BsonText.Literal(filter)
                    };
                })
        ];
    }

    /// <summary>
    /// 关键字摘要:<c>required</c>;带取值的关键字把规则里的值也写上(<c>minimum: 0</c>)。
    /// </summary>
    private static string KeywordSummary(BsonDocument validator, string path, string keyword)
    {
        if (keyword.Length == 0)
        {
            return "$jsonSchema";
        }
        if (keyword == "required" || SchemaAt(validator, path) is not { } schema || !schema.TryGetValue(keyword, out BsonValue value))
        {
            return keyword;
        }
        string text = BsonText.Literal(value);
        return text.Length <= 24 ? $"{keyword}: {text}" : keyword;
    }

    /// <summary>沿 <c>properties</c>(数组则经 <c>items</c>)走到某个路径的子 schema。</summary>
    internal static BsonDocument? SchemaAt(BsonDocument validator, string path)
    {
        BsonDocument? schema = validator.GetValue("$jsonSchema", BsonNull.Value) as BsonDocument ?? validator;
        foreach (string segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (schema?.GetValue("items", BsonNull.Value) is BsonDocument items && !schema.Contains("properties"))
            {
                schema = items;
            }
            schema = (schema?.GetValue("properties", BsonNull.Value) as BsonDocument)?.GetValue(segment, BsonNull.Value) as BsonDocument;
            if (schema is null)
            {
                return null;
            }
        }
        return schema;
    }

    /// <summary>试写文档起手:抽样里的一份文档(去掉 <c>_id</c>,试写的是"新插入")。</summary>
    private async Task SeedTryDocAsync()
    {
        BsonDocument? doc = _sample.FirstOrDefault();
        if (doc is null)
        {
            try
            {
                doc = await Workspace.Connection.Collection(Database, CollectionName)
                    .Find(FilterDefinition<BsonDocument>.Empty).Limit(1).FirstOrDefaultAsync(Lifetime).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                doc = null;
            }
        }
        doc = doc?.DeepClone().AsBsonDocument ?? [];
        doc.Remove("_id");
        TryDocText = BsonText.Pretty(doc);
    }

    /// <summary>
    /// 试写文档的实时校验:客户端校验器给出逐条原因(并在编辑器里标出那一行);
    /// 若服务器支持 <c>$documents</c>(5.1+),再让服务器判一次 —— 结论以服务器为准,不写入任何东西。
    /// </summary>
    internal async Task EvaluateTryDocAsync()
    {
        if (_disposed)
        {
            return;
        }
        int version = ++_tryVersion;
        if (_tryDocText.Trim().Length == 0)
        {
            TryPassed = null;
            TryTitle = "";
            TryDetail = "";
            TryDiagnostics = [];
            TryMarks = [];
            return;
        }
        if (!ShellJson.TryParseDocument(_tryDocText, out BsonDocument doc, out string? error))
        {
            TryPassed = null;
            TryTitle = Loc.Format("Design_SyntaxError", error);
            TryDetail = "";
            TryDiagnostics = [.. ShellJson.Diagnose(_tryDocText).Select(d => new EditorDiagnostic(d.Offset, Math.Max(1, d.Length), Loc.Format(d.MessageKey, d.Argument)))];
            TryMarks = [];
            return;
        }
        if (_ruleValidator is not { } validator)
        {
            TryPassed = null;
            TryTitle = Loc["Design_TryRuleInvalid"];
            TryDetail = "";
            return;
        }
        IReadOnlyList<SchemaViolation> violations = JsonSchemaValidator.Validate(validator, doc, Loc);
        bool? serverFailed = await ServerRejectsAsync(validator, doc).ConfigureAwait(true);
        if (version != _tryVersion)
        {
            return;
        }
        bool failed = serverFailed ?? violations.Count > 0;
        TryPassed = !failed;
        TryTitle = failed
            ? Loc.Format("Design_TryFail", _ruleAction, Loc[_ruleAction == "warn" ? "Design_TryFailWarn" : "Design_TryFailError"])
            : Loc["Design_TryPass"];
        TryDetail = !failed
            ? ""
            : violations.Count > 0
                ? string.Join("\n", violations.Take(6).Select(v => $"{ViolationPath(v)}:{v.Message}"))
                : Loc["Design_TryServerOnly"];
        (TryMarks, TryDiagnostics) = failed ? MarkViolations(_tryDocText, violations) : ([], []);
    }

    /// <summary><c>properties.total.minimum</c>(设计稿 16 结果框里的写法)。</summary>
    private static string ViolationPath(SchemaViolation v)
    {
        string path = NormalizePath(v.Path);
        if (v.Keyword == "required")
        {
            int dot = path.LastIndexOf('.');
            string parent = dot < 0 ? "" : string.Concat(path[..dot].Split('.').Select(static s => $"properties.{s}."));
            return $"{parent}required";
        }
        return path.Length == 0
            ? v.Keyword
            : string.Concat(path.Split('.').Select(static s => $"properties.{s}.")) + v.Keyword;
    }

    /// <summary>让服务器判一次(<c>$documents</c> + <c>$match: { $nor: [规则] }</c>);不支持返回 <see langword="null" />。</summary>
    private async Task<bool?> ServerRejectsAsync(BsonDocument validator, BsonDocument doc)
    {
        if (validator.ElementCount == 0)
        {
            return false;
        }
        try
        {
            BsonDocument reply = await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "aggregate", 1 },
                {
                    "pipeline", new BsonArray
                    {
                        new BsonDocument("$documents", new BsonArray { doc }),
                        new BsonDocument("$match", new BsonDocument("$nor", new BsonArray { validator })),
                        new BsonDocument("$count", "n")
                    }
                },
                { "cursor", new BsonDocument() }
            }, Lifetime).ConfigureAwait(true);
            return reply["cursor"]["firstBatch"].AsBsonArray.Count > 0;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// 违规 → 编辑器标记:按路径最后一段找到 <c>key:</c> 所在的行,整行红底,值下面画波浪线。
    /// 缺失字段(required)在文本里找不到对应的行,不标。
    /// </summary>
    internal static (IReadOnlyList<LineMark>, IReadOnlyList<EditorDiagnostic>) MarkViolations(string text, IReadOnlyList<SchemaViolation> violations)
    {
        var marks = new List<LineMark>();
        var diagnostics = new List<EditorDiagnostic>();
        foreach (SchemaViolation v in violations)
        {
            if (v.Keyword == "required")
            {
                continue;
            }
            string[] segments = NormalizePath(v.Path).Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }
            int from = 0;
            Match match = Match.Empty;
            foreach (string segment in segments)
            {
                match = Regex.Match(text[from..], $"(?<![A-Za-z0-9_$])[\"']?{Regex.Escape(segment)}[\"']?\\s*:");
                if (!match.Success)
                {
                    break;
                }
                from += match.Index + match.Length;
            }
            if (!match.Success)
            {
                continue;
            }
            int valueStart = from;
            while (valueStart < text.Length && text[valueStart] == ' ')
            {
                valueStart++;
            }
            int lineEnd = text.IndexOf('\n', valueStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }
            int valueEnd = lineEnd;
            while (valueEnd > valueStart && (char.IsWhiteSpace(text[valueEnd - 1]) || text[valueEnd - 1] == ','))
            {
                valueEnd--;
            }
            int line = text[..valueStart].Count(static c => c == '\n') + 1;
            if (marks.All(m => m.Line != line))
            {
                marks.Add(new LineMark(line, LineMarkKind.Removed));
            }
            diagnostics.Add(new EditorDiagnostic(valueStart, Math.Max(1, valueEnd - valueStart), v.Message));
        }
        return (marks, diagnostics);
    }

    /// <summary>读历史版本。</summary>
    private async Task LoadHistoryAsync()
    {
        SavedItem[] items = await Workspace.Store.GetAsync<SavedItem[]>(HistoryKey).ConfigureAwait(true) ?? [];
        RuleHistory.Clear();
        foreach (SavedItem item in items)
        {
            string[] parts = item.Name.Split('|');
            RuleHistory.Add(new RuleVersion(item.SavedAt, parts.ElementAtOrDefault(0) ?? "strict", parts.ElementAtOrDefault(1) ?? "error", item.Content));
        }
    }

    private string HistoryKey => $"design-validator-history:{Workspace.ConnectionKey}:{Namespace}";

    /// <summary>
    /// 应用一条验证规则(<c>collMod</c>)。写护栏 → 确认(写清 level / action 与已知的不符合份数)→
    /// 先把旧规则存进历史 → collMod → 重探集合信息、重置编辑器、重新预检。
    /// </summary>
    internal async Task ApplyValidatorAsync(BsonDocument validator, string level, string action, long knownFailures)
    {
        if (!CanValidate)
        {
            Workspace.Toast(new() { Title = Loc["Design_RuleUnsupported"], Kind = ToastKind.Warning });
            return;
        }
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        var facts = new List<ConfirmFact>
        {
            new("validationLevel", level),
            new("validationAction", action)
        };
        if (knownFailures > 0)
        {
            facts.Add(new(Loc["Design_PrecheckFailures"], BsonText.Grouped(knownFailures)));
        }
        bool confirmed = await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Design_ApplyTitle"],
            Message = Loc.Format("Design_ApplyBody", Namespace, level, action)
                      + (knownFailures > 0 ? " " + Loc.Format(action == "error" ? "Design_ApplyRejects" : "Design_ApplyWarns", BsonText.Grouped(knownFailures)) : ""),
            ConfirmLabel = Loc["Design_ApplyRule"],
            IconKey = "Mongo.shield-check",
            Danger = action == "error" && level != "off" && knownFailures > 0,
            Facts = facts,
            TypeToConfirm = Workspace.Guard.ConfirmWrites ? CollectionName : null
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await SaveHistoryAsync().ConfigureAwait(true);
            await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "collMod", CollectionName },
                { "validator", validator },
                { "validationLevel", level },
                { "validationAction", action }
            }, Lifetime).ConfigureAwait(true);
            await RefreshInfoAsync().ConfigureAwait(true);
            InitializeEditorFromLive();
            await LoadHistoryAsync().ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc["Design_Applied"], Detail = $"validationLevel {level} · validationAction {action}", Kind = ToastKind.Success });
            await RunPrecheckAsync(resample: false).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
    }

    /// <summary>把当前生效的规则存进历史(最多 20 版)。</summary>
    private async Task SaveHistoryAsync()
    {
        BsonDocument? current = _live.Validator;
        if (current is null)
        {
            return;
        }
        SavedItem[] existing = await Workspace.Store.GetAsync<SavedItem[]>(HistoryKey).ConfigureAwait(true) ?? [];
        var item = new SavedItem($"{_live.ValidationLevel}|{_live.ValidationAction}", JsonSchemaGenerator.Format(current, RuleWidth), DateTimeOffset.Now);
        SavedItem[] next = [item, .. existing.Take(19)];
        await Workspace.Store.SetAsync(HistoryKey, next).ConfigureAwait(true);
    }

    /// <summary>Schema 分析刚完成:试写文档还空着就用抽样起头。</summary>
    private void OnSchemaForValidation()
    {
        if (_validationLoaded && _tryDocText.Length == 0)
        {
            _ = SeedTryDocAsync();
        }
    }

    /// <summary>
    /// 规则编辑器的补全:<c>bsonType</c> 后面给类型别名,<c>$</c> 开头给 <c>$jsonSchema</c> 等,
    /// 其余给 <c>$jsonSchema</c> 关键字与抽样到的字段名。
    /// </summary>
    private Task<CompletionSet?> CompleteRuleAsync(CompletionRequest request)
    {
        string text = request.Text;
        int caret = Math.Clamp(request.CaretOffset, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$' or '.'))
        {
            start--;
        }
        string prefix = text[start..caret];
        if (!request.Explicit && prefix.Length == 0)
        {
            return Task.FromResult<CompletionSet?>(null);
        }
        var items = new List<CompletionItem>();
        string before = text[..start];
        bool quoted = start > 0 && text[start - 1] is '"' or '\'';
        if (Regex.IsMatch(before, "bsonType\\s*:\\s*(\\[[^\\]]*)?[\"']?$"))
        {
            items.AddRange(BsonTypeAliases.Select(alias => new CompletionItem
            {
                Label = alias,
                InsertText = quoted ? alias : $"\"{alias}\"",
                IconKey = "Mongo.type",
                IconToken = "VelaShellCyan",
                Category = "bsonType"
            }));
        }
        else if (prefix.StartsWith('$'))
        {
            items.Add(new CompletionItem
            {
                Label = "$jsonSchema",
                InsertText = "$jsonSchema: {\n  bsonType: \"object\",\n  |\n}",
                IconKey = "Mongo.shield-check",
                IconToken = "VelaShellMagenta",
                Category = Loc["Design_CompletionOperator"],
                Description = Loc["Design_Kw_jsonSchema"],
                DocsUrl = "https://www.mongodb.com/docs/manual/reference/operator/query/jsonSchema/"
            });
            foreach (string op in new[] { "$and", "$or", "$nor", "$expr" })
            {
                items.Add(new CompletionItem { Label = op, InsertText = $"{op}: [ | ]", IconKey = "Mongo.braces", Category = Loc["Design_CompletionOperator"] });
            }
        }
        else
        {
            items.AddRange(SchemaKeywords.Select(keyword => new CompletionItem
            {
                Label = keyword,
                InsertText = keyword switch
                {
                    "properties" or "patternProperties" => $"{keyword}: {{\n  |\n}}",
                    "required" or "enum" or "anyOf" or "allOf" or "oneOf" => $"{keyword}: [ | ]",
                    "items" or "not" => $"{keyword}: {{ | }}",
                    "bsonType" or "description" or "title" or "pattern" => $"{keyword}: \"|\"",
                    _ => $"{keyword}: |"
                },
                IconKey = "Mongo.shield-check",
                IconToken = "VelaShellMagenta",
                Category = Loc["Design_CompletionKeyword"],
                Badge = "$jsonSchema",
                Description = Loc["Design_Kw_" + keyword],
                DocsUrl = "https://www.mongodb.com/docs/manual/reference/operator/query/jsonSchema/#available-keywords"
            }));
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldOption field in _fieldOptions)
            {
                string name = field.Path[(field.Path.LastIndexOf('.') + 1)..];
                if (names.Add(name))
                {
                    items.Add(new CompletionItem
                    {
                        Label = name,
                        InsertText = $"{BsonText.FieldName(name)}: {{ bsonType: \"{JsonSchemaGenerator.BsonTypeName(field.Kind) ?? "string"}\"| }}",
                        IconKey = "Mongo.variable",
                        IconToken = field.Token,
                        Category = Loc.Format("Design_CompletionField", field.KindName)
                    });
                }
            }
        }
        List<CompletionItem> matched = prefix.Length == 0
            ? items
            : [.. items.Where(i => i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Concat(items.Where(i => !i.Label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                         && i.Label.Contains(prefix, StringComparison.OrdinalIgnoreCase)))];
        if (matched.Count == 0)
        {
            return Task.FromResult<CompletionSet?>(null);
        }
        return Task.FromResult<CompletionSet?>(new CompletionSet
        {
            Items = matched,
            ReplaceOffset = start,
            ReplaceLength = prefix.Length
        });
    }

    /// <summary>
    /// 规则编辑器里悬停在一个 <c>$jsonSchema</c> 关键字上时的说明(设计稿 16 的悬停卡):
    /// 关键字分类 + 一句释义;抽样预检里正好有文档违反这个关键字时,再补一句"有几份、在当前
    /// validationLevel / validationAction 下会怎样"。不是关键字返回 <see langword="null" />。
    /// </summary>
    internal (string Title, string Badge, string Body)? KeywordHover(string word)
    {
        if (word != "$jsonSchema" && !SchemaKeywords.Contains(word, StringComparer.Ordinal))
        {
            return null;
        }
        string group = word switch
        {
            "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf" => "numeric",
            "minLength" or "maxLength" or "pattern" => "string",
            "items" or "minItems" or "maxItems" or "uniqueItems" => "array",
            "required" or "properties" or "additionalProperties" or "patternProperties" or "minProperties" or "maxProperties" => "object",
            "bsonType" or "enum" => "type",
            "anyOf" or "allOf" or "oneOf" or "not" => "logic",
            "$jsonSchema" => "operator",
            _ => "meta"
        };
        string body = Loc["Design_Kw_" + word.TrimStart('$')];
        var failing = FailureReasons.Where(r => r.RawKeyword == word).ToList();
        if (failing.Count > 0)
        {
            string consequence = _ruleLevel == "off"
                ? Loc["Design_HoverOff"]
                : _ruleAction == "warn"
                    ? Loc.Format("Design_HoverWarn", _ruleLevel)
                    : Loc[_ruleLevel == "moderate" ? "Design_HoverErrorModerate" : "Design_HoverErrorStrict"];
            body += Loc.Format("Design_HoverFailures", BsonText.Grouped(failing.Sum(static r => r.Count)),
                string.Join(", ", failing.Select(static r => r.Path)), consequence);
        }
        return (word, Loc["Design_KwGroup_" + group], body);
    }

    /// <summary>防抖:重启一个一次性计时器。</summary>
    private static void Restart(ref DispatcherTimer? timer, TimeSpan delay, Action action)
    {
        timer?.Stop();
        var t = new DispatcherTimer { Interval = delay };
        t.Tick += (_, _) =>
        {
            t.Stop();
            action();
        };
        timer = t;
        t.Start();
    }
}
