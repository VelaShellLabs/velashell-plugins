using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>新建集合的五种形态(设计稿 17 那排卡片)。</summary>
internal enum NewCollectionKind
{
    /// <summary>普通集合。</summary>
    Plain,

    /// <summary>时序集合。</summary>
    TimeSeries,

    /// <summary>固定集合。</summary>
    Capped,

    /// <summary>聚簇集合。</summary>
    Clustered,

    /// <summary>视图。</summary>
    View
}

/// <summary>集合名的校验结论。</summary>
internal enum CollectionNameState
{
    /// <summary>还没填。</summary>
    Empty,

    /// <summary>可用。</summary>
    Available,

    /// <summary>同名集合 / 视图已存在。</summary>
    Exists,

    /// <summary>不合法(含 <c>$</c>、以 <c>system.</c> 开头、命名空间超长…)。</summary>
    Invalid
}

/// <summary>下拉里的一个单位(<c>天</c> = 86400 秒、<c>MB</c> = 1048576 字节)。</summary>
/// <param name="Label">显示名。</param>
/// <param name="Factor">换算到基本单位的倍数。</param>
internal sealed record NewCollUnit(string Label, long Factor)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// 新建集合(设计稿 17)。左边填,右边实时显示**将执行的 mongosh 命令** ——
/// 预览与真正发出去的命令出自同一个 <see cref="BuildCreateCommand" />,两者不可能对不上。
/// </summary>
internal sealed class NewCollectionDialogViewModel : DialogViewModel
{
    private readonly HashSet<string> _existing = new(StringComparer.Ordinal);
    private string _name = "";
    private NewCollectionKind _kind = NewCollectionKind.Plain;
    private string _timeField = "ts";
    private string _metaField = "";
    private string _granularity = "seconds";
    private string _expireValue = "";
    private NewCollUnit _expireUnit;
    private string _cappedSize = "100";
    private NewCollUnit _cappedUnit;
    private string _cappedMax = "";
    private string? _viewSource;
    private string _pipelineText = "[\n  { $match: {} }\n]";
    private bool _useCollation;
    private string _collationLocale = "zh";
    private int _collationStrength = 2;
    private bool _copyIndexes;
    private string? _copyIndexesFrom;
    private IReadOnlyList<BsonDocument> _sourceIndexes = [];
    private bool _editValidationAfter;
    private bool _isCreating;
    private string _error = "";

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">在哪个库里建。</param>
    /// <param name="kind">一打开就选中的卡片(对象树「视图」分组的右键「新建视图」直接落在视图卡上)。</param>
    /// <param name="viewSource">视图的源集合(从某个集合 / 视图上右键新建视图时,预先填成它)。</param>
    public NewCollectionDialogViewModel(IMongoWorkspace workspace, string database,
        NewCollectionKind kind = NewCollectionKind.Plain, string? viewSource = null)
        : base(workspace)
    {
        Database = database;
        Title = workspace.Loc[kind == NewCollectionKind.View ? "Nav_NewView" : "Nav_NewCollection"];
        Subtitle = database;
        _kind = kind;
        _viewSource = viewSource;
        ExpireUnits =
        [
            new(Loc["NewColl_UnitSeconds"], 1),
            new(Loc["NewColl_UnitMinutes"], 60),
            new(Loc["NewColl_UnitHours"], 3_600),
            new(Loc["NewColl_UnitDays"], 86_400)
        ];
        _expireUnit = ExpireUnits[3];
        SizeUnits = [new("KB", 1L << 10), new("MB", 1L << 20), new("GB", 1L << 30)];
        _cappedUnit = SizeUnits[1];
        CreateCommand = new(CreateAsync, () => CanCreate);
        foreach (CollectionInfo info in workspace.CollectionsOf(database))
        {
            _existing.Add(info.Name);
        }
        FillSources(workspace.CollectionsOf(database));
        _ = LoadExistingAsync();
    }

    /// <summary>库名。</summary>
    public string Database { get; }

    /// <inheritdoc />
    public override string IconKey => "Mongo.plus";

    /// <inheritdoc />
    public override string IconToken => "VelaAccent";

    /// <inheritdoc />
    public override double Width => 940;

    /// <inheritdoc />
    public override double Height => 560;

    /// <inheritdoc />
    public override bool CanCloseWithEscape => !_isCreating;

    // ── 名称 ─────────────────────────────────────────────────────────────

    /// <summary>集合名称。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                Changed(nameof(NameState), nameof(NameMessage), nameof(NameOk), nameof(NameBad), nameof(NameIconKey));
            }
        }
    }

    /// <summary>名称校验结论。</summary>
    public CollectionNameState NameState => ValidateName(_name, Database, _existing, out _);

    /// <summary>名称旁那行小字(<c>✓ 可用 · shop.device_metrics</c> / <c>已存在 · shop.orders</c> / 非法原因)。</summary>
    public string NameMessage
    {
        get
        {
            CollectionNameState state = ValidateName(_name, Database, _existing, out string? reason);
            string ns = $"{Database}.{_name.Trim()}";
            return state switch
            {
                CollectionNameState.Available => Loc.Format("NewColl_NameAvailable", ns),
                CollectionNameState.Exists => Loc.Format("NewColl_NameExists", ns),
                CollectionNameState.Invalid => Loc[reason ?? "NewColl_NameInvalid"],
                _ => ""
            };
        }
    }

    /// <summary>名称可用(绿)。</summary>
    public bool NameOk => NameState == CollectionNameState.Available;

    /// <summary>名称不可用(红框)。</summary>
    public bool NameBad => NameState is CollectionNameState.Exists or CollectionNameState.Invalid;

    /// <summary>名称提示的图标。</summary>
    public string NameIconKey => NameOk ? "Mongo.circle-check" : "Mongo.circle-x";

    /// <summary>
    /// 集合名规则(服务器的硬性约束,不是风格建议):不能空、不能含 <c>$</c> 与空字符、
    /// 不能以 <c>system.</c> 开头、<c>库.集合</c> 不超过 255 字节;首尾空白多半是手滑,也拦下。
    /// </summary>
    internal static CollectionNameState ValidateName(string name, string database, IReadOnlySet<string> existing, out string? reasonKey)
    {
        reasonKey = null;
        if (name.Length == 0)
        {
            return CollectionNameState.Empty;
        }
        if (name.Trim().Length != name.Length)
        {
            reasonKey = "NewColl_NameWhitespace";
            return CollectionNameState.Invalid;
        }
        if (name.Contains('$', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            reasonKey = "NewColl_NameDollar";
            return CollectionNameState.Invalid;
        }
        if (name.StartsWith("system.", StringComparison.Ordinal))
        {
            reasonKey = "NewColl_NameSystem";
            return CollectionNameState.Invalid;
        }
        if (name.StartsWith('.') || name.EndsWith('.'))
        {
            reasonKey = "NewColl_NameDot";
            return CollectionNameState.Invalid;
        }
        if (Encoding.UTF8.GetByteCount($"{database}.{name}") > 255)
        {
            reasonKey = "NewColl_NameTooLong";
            return CollectionNameState.Invalid;
        }
        return existing.Contains(name) ? CollectionNameState.Exists : CollectionNameState.Available;
    }

    // ── 类型 ─────────────────────────────────────────────────────────────

    /// <summary>类型。</summary>
    public NewCollectionKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                Changed(nameof(IsPlain), nameof(IsTimeSeries), nameof(IsCapped), nameof(IsClustered), nameof(IsView),
                    nameof(OptionsTitle), nameof(OptionsNote), nameof(TipTitle), nameof(TipBody), nameof(CanEditValidation),
                    nameof(CanCopyIndexes));
            }
        }
    }

    /// <summary>卡片:普通集合。</summary>
    public bool IsPlain { get => _kind == NewCollectionKind.Plain; set { if (value) { Kind = NewCollectionKind.Plain; } } }

    /// <summary>卡片:时序集合。</summary>
    public bool IsTimeSeries { get => _kind == NewCollectionKind.TimeSeries; set { if (value) { Kind = NewCollectionKind.TimeSeries; } } }

    /// <summary>卡片:固定集合。</summary>
    public bool IsCapped { get => _kind == NewCollectionKind.Capped; set { if (value) { Kind = NewCollectionKind.Capped; } } }

    /// <summary>卡片:聚簇集合。</summary>
    public bool IsClustered { get => _kind == NewCollectionKind.Clustered; set { if (value) { Kind = NewCollectionKind.Clustered; } } }

    /// <summary>卡片:视图。</summary>
    public bool IsView { get => _kind == NewCollectionKind.View; set { if (value) { Kind = NewCollectionKind.View; } } }

    /// <summary>选项面板标题。</summary>
    public string OptionsTitle => Loc[_kind switch
    {
        NewCollectionKind.TimeSeries => "NewColl_OptTimeSeries",
        NewCollectionKind.Capped => "NewColl_OptCapped",
        NewCollectionKind.Clustered => "NewColl_OptClustered",
        NewCollectionKind.View => "NewColl_OptView",
        _ => "NewColl_OptPlain"
    }];

    /// <summary>选项面板标题旁的灰字(创建后改不了的那几样)。</summary>
    public string OptionsNote => Loc[_kind switch
    {
        NewCollectionKind.TimeSeries => "NewColl_NoteTimeSeries",
        NewCollectionKind.Capped => "NewColl_NoteCapped",
        NewCollectionKind.Clustered => "NewColl_NoteClustered",
        NewCollectionKind.View => "NewColl_NoteView",
        _ => "NewColl_NotePlain"
    }];

    /// <summary>右侧提示框标题。</summary>
    public string TipTitle => Loc[_kind switch
    {
        NewCollectionKind.TimeSeries => "NewColl_TipTimeSeriesTitle",
        NewCollectionKind.Capped => "NewColl_TipCappedTitle",
        NewCollectionKind.Clustered => "NewColl_TipClusteredTitle",
        NewCollectionKind.View => "NewColl_TipViewTitle",
        _ => "NewColl_TipPlainTitle"
    }];

    /// <summary>右侧提示框正文。</summary>
    public string TipBody => Loc[_kind switch
    {
        NewCollectionKind.TimeSeries => "NewColl_TipTimeSeries",
        NewCollectionKind.Capped => "NewColl_TipCapped",
        NewCollectionKind.Clustered => "NewColl_TipClustered",
        NewCollectionKind.View => "NewColl_TipView",
        _ => "NewColl_TipPlain"
    }];

    // ── 时序选项 ─────────────────────────────────────────────────────────

    /// <summary>timeField(必填)。</summary>
    public string TimeField
    {
        get => _timeField;
        set
        {
            if (SetProperty(ref _timeField, value))
            {
                Changed(nameof(SampleDocument));
            }
        }
    }

    /// <summary>metaField(可选)。</summary>
    public string MetaField
    {
        get => _metaField;
        set
        {
            if (SetProperty(ref _metaField, value))
            {
                Changed(nameof(SampleDocument));
            }
        }
    }

    /// <summary>粒度:<c>seconds</c> / <c>minutes</c> / <c>hours</c>。</summary>
    public string Granularity
    {
        get => _granularity;
        set
        {
            if (SetProperty(ref _granularity, value))
            {
                Changed(nameof(IsSeconds), nameof(IsMinutes), nameof(IsHours));
            }
        }
    }

    /// <summary>分段:seconds。</summary>
    public bool IsSeconds { get => _granularity == "seconds"; set { if (value) { Granularity = "seconds"; } } }

    /// <summary>分段:minutes。</summary>
    public bool IsMinutes { get => _granularity == "minutes"; set { if (value) { Granularity = "minutes"; } } }

    /// <summary>分段:hours。</summary>
    public bool IsHours { get => _granularity == "hours"; set { if (value) { Granularity = "hours"; } } }

    /// <summary>自动过期的数值(空 = 不过期)。</summary>
    public string ExpireValue
    {
        get => _expireValue;
        set
        {
            if (SetProperty(ref _expireValue, value))
            {
                Changed(nameof(ExpireSecondsText));
            }
        }
    }

    /// <summary>自动过期的单位。</summary>
    public NewCollUnit ExpireUnit
    {
        get => _expireUnit;
        set
        {
            if (value is not null && SetProperty(ref _expireUnit, value))
            {
                Changed(nameof(ExpireSecondsText));
            }
        }
    }

    /// <summary>单位下拉的选项。</summary>
    public IReadOnlyList<NewCollUnit> ExpireUnits { get; }

    /// <summary>换算后的秒数(<c>= 7,776,000 s</c>);填的不是正整数时为空。</summary>
    public string ExpireSecondsText => ExpireSeconds is { } s ? $"= {BsonText.Grouped(s)} s" : "";

    /// <summary>换算后的秒数;没填或填错为 <see langword="null" />。</summary>
    public long? ExpireSeconds =>
        long.TryParse(_expireValue.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n)
        && n > 0 && n <= long.MaxValue / _expireUnit.Factor
            ? n * _expireUnit.Factor
            : null;

    /// <summary>示例文档(按填的字段名现拼,让人一眼看出 timeField / metaField 落在文档的哪里)。</summary>
    public string SampleDocument
    {
        get
        {
            string time = _timeField.Trim().Length > 0 ? BsonText.FieldName(_timeField.Trim()) : "ts";
            string meta = _metaField.Trim().Length > 0 ? $", {BsonText.FieldName(_metaField.Trim())}: {{ … }}" : "";
            return $"{{ {time}: ISODate(…){meta}, value: 21.4 }}";
        }
    }

    // ── 固定集合选项 ─────────────────────────────────────────────────────

    /// <summary>容量上限的数值。</summary>
    public string CappedSize
    {
        get => _cappedSize;
        set => SetAndRefresh(ref _cappedSize, value);
    }

    /// <summary>容量单位。</summary>
    public NewCollUnit CappedUnit
    {
        get => _cappedUnit;
        set
        {
            if (value is not null)
            {
                SetAndRefresh(ref _cappedUnit, value);
            }
        }
    }

    /// <summary>容量单位选项。</summary>
    public IReadOnlyList<NewCollUnit> SizeUnits { get; }

    /// <summary>文档数上限(可选)。</summary>
    public string CappedMax
    {
        get => _cappedMax;
        set => SetAndRefresh(ref _cappedMax, value);
    }

    // ── 视图选项 ─────────────────────────────────────────────────────────

    /// <summary>视图的源集合。</summary>
    public string? ViewSource
    {
        get => _viewSource;
        set => SetAndRefresh(ref _viewSource, value);
    }

    /// <summary>视图的管道(mongosh 写法)。</summary>
    public string PipelineText
    {
        get => _pipelineText;
        set => SetAndRefresh(ref _pipelineText, value ?? "");
    }

    /// <summary>可选的源集合(集合与视图)。</summary>
    public ObservableCollection<string> ViewSources { get; } = [];

    // ── 更多 ─────────────────────────────────────────────────────────────

    /// <summary>指定排序规则。</summary>
    public bool UseCollation
    {
        get => _useCollation;
        set => SetAndRefresh(ref _useCollation, value);
    }

    /// <summary>排序规则的 locale。</summary>
    public string CollationLocale
    {
        get => _collationLocale;
        set => SetAndRefresh(ref _collationLocale, value);
    }

    /// <summary>排序规则的 strength(1–5)。</summary>
    public int CollationStrength
    {
        get => _collationStrength;
        set => SetAndRefresh(ref _collationStrength, Math.Clamp(value, 1, 5));
    }

    /// <summary>strength 下拉的选项。</summary>
    public IReadOnlyList<int> Strengths { get; } = [1, 2, 3, 4, 5];

    /// <summary>排序规则未勾选时那行灰字(<c>locale: "zh", strength: 2</c>)。</summary>
    public string CollationSummary => $"locale: {BsonText.Quote(_collationLocale)}, strength: {_collationStrength}";

    /// <summary>复制索引。</summary>
    public bool CopyIndexes
    {
        get => _copyIndexes;
        set
        {
            if (!SetProperty(ref _copyIndexes, value))
            {
                return;
            }
            if (value && _copyIndexesFrom is null && IndexSources.Count > 0)
            {
                CopyIndexesFrom = IndexSources[0];
            }
            Changed();
        }
    }

    /// <summary>从哪个集合复制索引。</summary>
    public string? CopyIndexesFrom
    {
        get => _copyIndexesFrom;
        set
        {
            if (SetProperty(ref _copyIndexesFrom, value))
            {
                _sourceIndexes = [];
                Changed();
                _ = LoadSourceIndexesAsync(value);
            }
        }
    }

    /// <summary>可复制索引的源集合(不含视图)。</summary>
    public ObservableCollection<string> IndexSources { get; } = [];

    /// <summary>复制索引可用(视图没有索引)。</summary>
    public bool CanCopyIndexes => _kind != NewCollectionKind.View;

    /// <summary>创建后立即打开「验证规则」页。</summary>
    public bool EditValidationAfter
    {
        get => _editValidationAfter;
        set => SetProperty(ref _editValidationAfter, value);
    }

    /// <summary>「创建后编辑验证规则」可用(时序集合与视图不支持验证规则)。</summary>
    public bool CanEditValidation => _kind is not (NewCollectionKind.TimeSeries or NewCollectionKind.View);

    // ── 预览与创建 ───────────────────────────────────────────────────────

    /// <summary>右侧「将执行的命令」。</summary>
    public string Preview
    {
        get
        {
            string name = _name.Trim().Length > 0 ? _name.Trim() : Loc["NewColl_NamePlaceholder"];
            BsonDocument command = BuildCreateCommand(lenient: true);
            if (_kind == NewCollectionKind.View)
            {
                BsonDocument? collation = command.TryGetValue("collation", out BsonValue c) ? c.AsBsonDocument : null;
                return ObjectScripts.CreateView(name, _viewSource ?? "", command.GetValue("pipeline", new BsonArray()).AsBsonArray, collation);
            }
            var options = new BsonDocument(command.Where(static e => e.Name != "create"));
            var lines = new List<string> { ObjectScripts.CreateCollection(name, options) };
            if (_copyIndexes && CanCopyIndexes)
            {
                lines.AddRange(_sourceIndexes.Where(ObjectScripts.IsReplayable).Select(i => ObjectScripts.CreateIndex(name, i)));
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>选项里的错误(时间字段没填、管道写错…);空 = 没问题。</summary>
    public string OptionsError
    {
        get
        {
            switch (_kind)
            {
                case NewCollectionKind.TimeSeries:
                {
                    string time = _timeField.Trim();
                    string meta = _metaField.Trim();
                    if (time.Length == 0)
                    {
                        return Loc["NewColl_ErrTimeField"];
                    }
                    if (time.Contains('$', StringComparison.Ordinal) || time.Contains('.', StringComparison.Ordinal) || time == "_id")
                    {
                        return Loc["NewColl_ErrFieldName"];
                    }
                    if (meta.Length > 0 && (meta == time || meta == "_id" || meta.Contains('$', StringComparison.Ordinal) || meta.Contains('.', StringComparison.Ordinal)))
                    {
                        return Loc["NewColl_ErrMetaField"];
                    }
                    if (_expireValue.Trim().Length > 0 && ExpireSeconds is null)
                    {
                        return Loc["NewColl_ErrExpire"];
                    }
                    break;
                }
                case NewCollectionKind.Capped:
                    if (CappedBytes is null)
                    {
                        return Loc["NewColl_ErrSize"];
                    }
                    if (_cappedMax.Trim().Length > 0 && CappedMaxDocs is null)
                    {
                        return Loc["NewColl_ErrMax"];
                    }
                    break;
                case NewCollectionKind.View:
                    if (string.IsNullOrEmpty(_viewSource))
                    {
                        return Loc["NewColl_ErrViewSource"];
                    }
                    if (!TryParsePipeline(out _, out string? error))
                    {
                        return Loc.Format("NewColl_ErrPipeline", error);
                    }
                    break;
            }
            if (_useCollation && _collationLocale.Trim().Length == 0)
            {
                return Loc["NewColl_ErrCollation"];
            }
            if (_copyIndexes && CanCopyIndexes && _copyIndexesFrom is null)
            {
                return Loc["NewColl_ErrCopySource"];
            }
            return "";
        }
    }

    /// <summary>能不能点「创建集合」。</summary>
    public bool CanCreate => !_isCreating && NameState == CollectionNameState.Available && OptionsError.Length == 0;

    /// <summary>正在创建。</summary>
    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                Changed();
            }
        }
    }

    /// <summary>底栏左侧的错误(选项错误或服务器拒绝的原因)。</summary>
    public string Error
    {
        get => _error.Length > 0 ? _error : OptionsError;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>底栏有没有错误。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>创建。</summary>
    public AsyncCommand CreateCommand { get; }

    private long? CappedBytes =>
        double.TryParse(_cappedSize.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0
            ? (long)Math.Ceiling(n * _cappedUnit.Factor)
            : null;

    private long? CappedMaxDocs =>
        long.TryParse(_cappedMax.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n) && n > 0 ? n : null;

    private bool TryParsePipeline(out BsonArray pipeline, out string? error)
    {
        try
        {
            pipeline = ShellJson.ParseArray(_pipelineText);
            if (pipeline.Any(static s => !s.IsBsonDocument || s.AsBsonDocument.ElementCount != 1))
            {
                error = Loc["NewColl_ErrStage"];
                return false;
            }
            error = null;
            return true;
        }
        catch (ShellJsonException ex)
        {
            pipeline = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 要发给服务器的 <c>create</c> 命令。<paramref name="lenient" /> 为真时(预览用)填错的项直接略过,
    /// 让预览一直有东西可看;真正创建时选项错误早已拦在 <see cref="CanCreate" /> 上。
    /// </summary>
    internal BsonDocument BuildCreateCommand(bool lenient = false)
    {
        var command = new BsonDocument("create", _name.Trim());
        switch (_kind)
        {
            case NewCollectionKind.TimeSeries:
            {
                var ts = new BsonDocument("timeField", _timeField.Trim());
                if (_metaField.Trim().Length > 0)
                {
                    ts.Add("metaField", _metaField.Trim());
                }
                ts.Add("granularity", _granularity);
                command.Add("timeseries", ts);
                if (ExpireSeconds is { } seconds)
                {
                    command.Add("expireAfterSeconds", Number(seconds));
                }
                break;
            }
            case NewCollectionKind.Capped:
                command.Add("capped", true);
                command.Add("size", Number(CappedBytes ?? 0));
                if (CappedMaxDocs is { } max)
                {
                    command.Add("max", Number(max));
                }
                break;
            case NewCollectionKind.Clustered:
                command.Add("clusteredIndex", new BsonDocument
                {
                    { "key", new BsonDocument("_id", 1) },
                    { "unique", true }
                });
                break;
            case NewCollectionKind.View:
                command.Add("viewOn", _viewSource ?? "");
                command.Add("pipeline", TryParsePipeline(out BsonArray pipeline, out _) || lenient ? pipeline : []);
                break;
        }
        if (_useCollation && _collationLocale.Trim().Length > 0)
        {
            command.Add("collation", new BsonDocument
            {
                { "locale", _collationLocale.Trim() },
                { "strength", _collationStrength }
            });
        }
        return command;
    }

    private static BsonValue Number(long value) =>
        value is >= int.MinValue and <= int.MaxValue ? new BsonInt32((int)value) : new BsonInt64(value);

    /// <summary>创建(「创建集合」按钮;单测直接等它)。</summary>
    internal async Task CreateAsync()
    {
        if (!CanCreate || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        string name = _name.Trim();
        NewCollectionKind kind = _kind;
        bool editValidation = _editValidationAfter && CanEditValidation;
        IsCreating = true;
        Error = "";
        try
        {
            await Workspace.Connection.RunCommandAsync(Database, BuildCreateCommand()).ConfigureAwait(true);
            (int copied, List<string> failed) = _copyIndexes && CanCopyIndexes && _copyIndexesFrom is { } source
                ? await CopyIndexesAsync(source, name).ConfigureAwait(true)
                : (0, []);
            Workspace.Toast(new()
            {
                Title = Loc.Format("NewColl_Created", $"{Database}.{name}"),
                Detail = failed.Count > 0
                    ? Loc.Format("NewColl_IndexesFailed", copied, string.Join(", ", failed))
                    : copied > 0 ? Loc.Format("NewColl_IndexesCopied", copied) : null,
                Kind = failed.Count > 0 ? ToastKind.Warning : ToastKind.Success
            });
            ObjectsChanged.Raise(Workspace, Database);
            Close();
            await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
            if (kind != NewCollectionKind.View && editValidation)
            {
                Workspace.OpenDesign(Database, name, DesignPage.Validation);
            }
            else
            {
                Workspace.OpenCollection(Database, name);
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Error = Loc.Format("Common_Failed", MongoConnector.Describe(ex));
        }
        finally
        {
            IsCreating = false;
        }
    }

    /// <summary>
    /// 把源集合的二级索引逐个建到新集合上。逐个建而不是一条 <c>createIndexes</c> 全包:
    /// 时序集合不支持某些索引形态(如唯一索引),一条失败不该连累其余。
    /// </summary>
    private async Task<(int Copied, List<string> Failed)> CopyIndexesAsync(string source, string target)
    {
        IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(Database, source).ConfigureAwait(true);
        int copied = 0;
        var failed = new List<string>();
        foreach (BsonDocument index in indexes.Where(ObjectScripts.IsReplayable))
        {
            var spec = new BsonDocument("key", index["key"]);
            spec.AddRange(ObjectScripts.IndexOptions(index));
            try
            {
                await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
                {
                    { "createIndexes", target },
                    { "indexes", new BsonArray { spec } }
                }).ConfigureAwait(true);
                copied++;
            }
            catch (MongoCommandException)
            {
                failed.Add(index.GetValue("name", "?").ToString() ?? "?");
            }
        }
        return (copied, failed);
    }

    // ── 加载 ─────────────────────────────────────────────────────────────

    /// <summary>重新列一次集合:对象树可能还没展开过这个库,"已存在"不能只看树上那份。</summary>
    private async Task LoadExistingAsync()
    {
        try
        {
            IReadOnlyList<CollectionInfo> all = await Workspace.Connection.ListCollectionsAsync(Database).ConfigureAwait(true);
            _existing.Clear();
            foreach (CollectionInfo info in all)
            {
                _existing.Add(info.Name);
            }
            FillSources(all);
            Changed(nameof(NameState), nameof(NameMessage), nameof(NameOk), nameof(NameBad), nameof(NameIconKey));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // 列不出来就用对象树上那份;创建时服务器自己会拒绝重名。
        }
    }

    private void FillSources(IReadOnlyList<CollectionInfo> all)
    {
        string? keepView = _viewSource;
        ViewSources.Clear();
        IndexSources.Clear();
        foreach (CollectionInfo info in all.Where(static c => c.Kind != CollectionKind.System).OrderBy(static c => c.Name, StringComparer.Ordinal))
        {
            ViewSources.Add(info.Name);
            if (info.Kind is not (CollectionKind.View or CollectionKind.TimeSeries))
            {
                IndexSources.Add(info.Name);
            }
        }
        if (keepView is not null && ViewSources.Contains(keepView))
        {
            ViewSource = keepView;
        }
        else if (ViewSources.Count > 0 && _viewSource is null)
        {
            ViewSource = ViewSources.FirstOrDefault(static n => !n.Contains('.', StringComparison.Ordinal)) ?? ViewSources[0];
        }
    }

    private async Task LoadSourceIndexesAsync(string? source)
    {
        if (source is null)
        {
            return;
        }
        try
        {
            IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(Database, source).ConfigureAwait(true);
            if (_copyIndexesFrom == source)
            {
                _sourceIndexes = indexes;
                Changed();
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
        }
    }

    private void SetAndRefresh<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            Changed(name == nameof(UseCollation) || name == nameof(CollationLocale) || name == nameof(CollationStrength)
                ? nameof(CollationSummary)
                : null);
        }
    }

    /// <summary>任何输入变了:预览、校验与按钮可用性一起重算(它们都是派生值,算一次很便宜)。</summary>
    private void Changed(params string?[] extra)
    {
        foreach (string? name in extra)
        {
            if (name is not null)
            {
                RaisePropertyChanged(name);
            }
        }
        RaisePropertiesChanged(nameof(Preview), nameof(OptionsError), nameof(Error), nameof(HasError), nameof(CanCreate));
        CreateCommand.RaiseCanExecuteChanged();
    }
}
