using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合设计 · 选项:TTL、排序规则、固定集合上限、时序参数、视图定义、变更流前后镜像。
/// <para>
/// 能用 <c>collMod</c> 改的都给改(TTL 秒数、固定集合上限 6.0+、时序粒度只能往粗改、视图的源与管道、前后镜像),
/// 改不了的(排序规则、timeField / metaField)只读展示并写明原因 —— 用户最常问的就是"这个能不能改"。
/// </para>
/// </summary>
internal sealed partial class DesignTabViewModel
{
    /// <summary>时序粒度从细到粗(只能往粗改)。</summary>
    public static IReadOnlyList<string> Granularities { get; } = ["seconds", "minutes", "hours"];

    private string _collationText = "";
    private string _cappedSize = "";
    private string _cappedMax = "";
    private string _tsTimeField = "";
    private string _tsMetaField = "";
    private string _tsGranularity = "";
    private string _tsExpire = "";
    private string _viewOn = "";
    private string _viewPipeline = "";
    private bool _prePost;
    private bool _prePostOriginal;
    private string _optionsJson = "";

    /// <summary>TTL 索引。</summary>
    public ObservableCollection<TtlRow> TtlRows { get; } = [];

    /// <summary>有 TTL 索引。</summary>
    public bool HasTtl => TtlRows.Count > 0;

    /// <summary>排序规则(只读);没有为空。</summary>
    public string CollationText
    {
        get => _collationText;
        private set
        {
            if (SetProperty(ref _collationText, value))
            {
                RaisePropertyChanged(nameof(HasCollation));
            }
        }
    }

    /// <summary>设了排序规则。</summary>
    public bool HasCollation => _collationText.Length > 0;

    /// <summary>固定集合字节上限(可改,6.0+)。</summary>
    public string CappedSize
    {
        get => _cappedSize;
        set => SetProperty(ref _cappedSize, value ?? "");
    }

    /// <summary>固定集合文档数上限(可改,6.0+;空 = 不限)。</summary>
    public string CappedMax
    {
        get => _cappedMax;
        set => SetProperty(ref _cappedMax, value ?? "");
    }

    /// <summary>固定集合字节上限换算(<c>= 1.0 GB</c>)。</summary>
    public string CappedSizeHuman => long.TryParse(_cappedSize, out long b) ? "= " + BsonText.Bytes(b) : "";

    /// <summary>时序:时间字段(只读)。</summary>
    public string TsTimeField
    {
        get => _tsTimeField;
        private set => SetProperty(ref _tsTimeField, value);
    }

    /// <summary>时序:元数据字段(只读)。</summary>
    public string TsMetaField
    {
        get => _tsMetaField;
        private set => SetProperty(ref _tsMetaField, value);
    }

    /// <summary>时序:粒度(只能往粗改)。</summary>
    public string TsGranularity
    {
        get => _tsGranularity;
        set => SetProperty(ref _tsGranularity, value ?? "");
    }

    /// <summary>时序:过期秒数(空 = 不过期)。</summary>
    public string TsExpire
    {
        get => _tsExpire;
        set
        {
            if (SetProperty(ref _tsExpire, value ?? ""))
            {
                RaisePropertyChanged(nameof(TsExpireHuman));
            }
        }
    }

    /// <summary>时序过期换算。</summary>
    public string TsExpireHuman => long.TryParse(_tsExpire, out long s) ? "= " + Duration(s) : Loc["Design_NeverExpire"];

    /// <summary>视图:源集合。</summary>
    public string ViewOn
    {
        get => _viewOn;
        set => SetProperty(ref _viewOn, value ?? "");
    }

    /// <summary>视图:管道(mongosh 写法)。</summary>
    public string ViewPipeline
    {
        get => _viewPipeline;
        set => SetProperty(ref _viewPipeline, value ?? "");
    }

    /// <summary>变更流前后镜像(changeStreamPreAndPostImages,6.0+)。</summary>
    public bool PrePostImages
    {
        get => _prePost;
        set
        {
            if (SetProperty(ref _prePost, value))
            {
                RaisePropertyChanged(nameof(PrePostDirty));
            }
        }
    }

    /// <summary>前后镜像开关改过了。</summary>
    public bool PrePostDirty => _prePost != _prePostOriginal;

    /// <summary>listCollections 的 options 原文(只读,页底)。</summary>
    public string OptionsJson
    {
        get => _optionsJson;
        private set => SetProperty(ref _optionsJson, value);
    }

    /// <summary>应用一条 TTL 的新秒数。</summary>
    public AsyncCommand<TtlRow> ApplyTtlCommand { get; private set; } = null!;

    /// <summary>应用固定集合上限。</summary>
    public AsyncCommand ApplyCappedCommand { get; private set; } = null!;

    /// <summary>应用时序参数。</summary>
    public AsyncCommand ApplyTimeSeriesCommand { get; private set; } = null!;

    /// <summary>应用视图定义。</summary>
    public AsyncCommand ApplyViewCommand { get; private set; } = null!;

    /// <summary>应用前后镜像开关。</summary>
    public AsyncCommand ApplyPrePostCommand { get; private set; } = null!;

    /// <summary>选时序粒度。</summary>
    public RelayCommand<string> SetGranularityCommand { get; private set; } = null!;

    private void InitializeOptions()
    {
        TtlRows.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(HasTtl));
        ApplyTtlCommand = new(ApplyTtlAsync);
        ApplyCappedCommand = new(ApplyCappedAsync);
        ApplyTimeSeriesCommand = new(ApplyTimeSeriesAsync);
        ApplyViewCommand = new(ApplyViewAsync);
        ApplyPrePostCommand = new(async () =>
        {
            if (await CollModAsync(new BsonDocument("changeStreamPreAndPostImages", new BsonDocument("enabled", _prePost)), Loc["Design_OptPrePost"])
                    .ConfigureAwait(true))
            {
                await LoadOptionsAsync().ConfigureAwait(true);
            }
        });
        SetGranularityCommand = new(g => TsGranularity = g);
    }

    /// <summary>加载选项页(重探集合信息;TTL 来自索引列表)。</summary>
    private async Task LoadOptionsAsync()
    {
        await RefreshInfoAsync().ConfigureAwait(true);
        // TTL 行来自索引列表:别处(shell、别的客户端)可能刚建过 TTL 索引,每次进选项页都重读一遍。
        await LoadIndexesAsync().ConfigureAwait(true);
        BsonDocument options = _live.Options;
        CollationText = options.GetValue("collation", BsonNull.Value) is BsonDocument collation ? BsonText.Literal(collation) : "";
        CappedSize = options.GetValue("size", BsonNull.Value) is { IsNumeric: true } size ? size.ToInt64().ToString(CultureInfo.InvariantCulture) : "";
        CappedMax = options.GetValue("max", BsonNull.Value) is { IsNumeric: true } max && max.ToInt64() > 0 ? max.ToInt64().ToString(CultureInfo.InvariantCulture) : "";
        RaisePropertyChanged(nameof(CappedSizeHuman));
        BsonDocument ts = options.GetValue("timeseries", BsonNull.Value) as BsonDocument ?? [];
        TsTimeField = ts.GetValue("timeField", "").ToString() ?? "";
        TsMetaField = ts.GetValue("metaField", BsonNull.Value) is BsonString meta ? meta.Value : "—";
        TsGranularity = ts.GetValue("granularity", BsonNull.Value) is BsonString g ? g.Value : "seconds";
        TsExpire = options.GetValue("expireAfterSeconds", BsonNull.Value) is { IsNumeric: true } e ? e.ToInt64().ToString(CultureInfo.InvariantCulture) : "";
        ViewOn = _live.ViewOn ?? "";
        ViewPipeline = _live.Pipeline is { } pipeline ? JsonSchemaGenerator.Format(pipeline, RuleWidth) : "[]";
        _prePostOriginal = options.GetValue("changeStreamPreAndPostImages", BsonNull.Value) is BsonDocument pp && pp.GetValue("enabled", false).ToBoolean();
        PrePostImages = _prePostOriginal;
        RaisePropertyChanged(nameof(PrePostDirty));
        OptionsJson = options.ElementCount == 0 ? "{}" : BsonText.Pretty(options);
        RefreshTtlRows();
        _optionsLoaded = true;
        UpdateStatus();
    }

    /// <summary>TTL 行 ← 索引列表里带 <c>expireAfterSeconds</c> 的。</summary>
    private void RefreshTtlRows()
    {
        TtlRows.Clear();
        foreach (IndexRow row in Indexes)
        {
            if (row.Spec.TryGetValue("expireAfterSeconds", out BsonValue seconds) && seconds.IsNumeric)
            {
                TtlRows.Add(new TtlRow(row.Name, row.Key.Names.FirstOrDefault() ?? "", seconds.ToInt64(), Duration));
            }
        }
    }

    /// <summary>改 TTL 秒数(<c>collMod: { index: { name, expireAfterSeconds } }</c>)。</summary>
    internal async Task ApplyTtlAsync(TtlRow row)
    {
        if (!long.TryParse(row.Seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || seconds < 0)
        {
            Workspace.Toast(new() { Title = Loc["Design_ErrTtlSeconds"], Kind = ToastKind.Warning });
            return;
        }
        var index = new BsonDocument { { "name", row.Index }, { "expireAfterSeconds", seconds } };
        if (await CollModAsync(new BsonDocument("index", index), Loc.Format("Design_OptTtlChange", row.Index, Duration(seconds))).ConfigureAwait(true))
        {
            await LoadIndexesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>改固定集合上限(6.0+ 的 <c>cappedSize / cappedMax</c>)。</summary>
    private async Task ApplyCappedAsync()
    {
        var changes = new BsonDocument();
        if (long.TryParse(_cappedSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out long size) && size > 0)
        {
            changes["cappedSize"] = size;
        }
        if (_cappedMax.Trim().Length == 0)
        {
            changes["cappedMax"] = 0;
        }
        else if (long.TryParse(_cappedMax, NumberStyles.Integer, CultureInfo.InvariantCulture, out long max) && max >= 0)
        {
            changes["cappedMax"] = max;
        }
        if (changes.ElementCount == 0)
        {
            return;
        }
        await CollModAsync(changes, Loc["Design_OptCapped"]).ConfigureAwait(true);
        await LoadOptionsAsync().ConfigureAwait(true);
    }

    /// <summary>改时序参数:粒度(只能往粗)与过期秒数(空 = 关闭过期)。</summary>
    private async Task ApplyTimeSeriesAsync()
    {
        var changes = new BsonDocument();
        BsonDocument ts = _live.Options.GetValue("timeseries", BsonNull.Value) as BsonDocument ?? [];
        string current = ts.GetValue("granularity", BsonNull.Value) is BsonString g ? g.Value : "";
        if (_tsGranularity.Length > 0 && _tsGranularity != current)
        {
            if (Granularities.ToList().IndexOf(_tsGranularity) < Granularities.ToList().IndexOf(current))
            {
                Workspace.Toast(new() { Title = Loc["Design_ErrGranularity"], Kind = ToastKind.Warning });
                return;
            }
            changes["timeseries"] = new BsonDocument("granularity", _tsGranularity);
        }
        if (_tsExpire.Trim().Length == 0)
        {
            if (_live.Options.Contains("expireAfterSeconds"))
            {
                changes["expireAfterSeconds"] = "off";
            }
        }
        else if (long.TryParse(_tsExpire, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) && seconds >= 0)
        {
            changes["expireAfterSeconds"] = seconds;
        }
        if (changes.ElementCount == 0)
        {
            return;
        }
        await CollModAsync(changes, Loc["Design_OptTimeSeries"]).ConfigureAwait(true);
        await LoadOptionsAsync().ConfigureAwait(true);
    }

    /// <summary>改视图定义(<c>collMod: { viewOn, pipeline }</c>)。</summary>
    private async Task ApplyViewAsync()
    {
        BsonArray pipeline;
        try
        {
            pipeline = ShellJson.ParseArray(_viewPipeline);
        }
        catch (ShellJsonException ex)
        {
            Workspace.Toast(new() { Title = Loc.Format("Design_SyntaxError", ex.Message), Kind = ToastKind.Warning });
            return;
        }
        if (_viewOn.Trim().Length == 0)
        {
            return;
        }
        await CollModAsync(new BsonDocument { { "viewOn", _viewOn.Trim() }, { "pipeline", pipeline } }, Loc["Design_OptView"]).ConfigureAwait(true);
        await LoadOptionsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 一次 <c>collMod</c>:写护栏 → 确认(列出要改的东西)→ 执行 → 重探集合信息。
    /// </summary>
    private async Task<bool> CollModAsync(BsonDocument changes, string what)
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return false;
        }
        bool confirmed = await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Design_CollModTitle"],
            Message = Loc.Format("Design_CollModBody", Namespace, what),
            ConfirmLabel = Loc["Common_Apply"],
            IconKey = "Mongo.settings-2",
            Danger = false,
            Facts = [.. changes.Select(static e => new ConfirmFact(e.Name, BsonText.Literal(e.Value)))],
            TypeToConfirm = Workspace.Guard.ConfirmWrites ? CollectionName : null
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return false;
        }
        try
        {
            var command = new BsonDocument("collMod", CollectionName);
            command.Merge(changes);
            await Workspace.Connection.RunCommandAsync(Database, command, Lifetime).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Design_CollModDone", what), Kind = ToastKind.Success });
            await RefreshInfoAsync().ConfigureAwait(true);
            await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
            return false;
        }
    }
}
