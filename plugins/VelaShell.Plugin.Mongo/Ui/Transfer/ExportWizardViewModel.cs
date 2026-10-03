using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>导出格式。</summary>
public enum ExportFormat
{
    /// <summary>JSON(EJSON,行分隔或数组)。</summary>
    Json,

    /// <summary>CSV。</summary>
    Csv,

    /// <summary>Excel 工作簿。</summary>
    Excel,

    /// <summary>BSON 转储(mongodump 兼容)。</summary>
    BsonDump,

    /// <summary>Shell 脚本(insertMany 语句)。</summary>
    Shell
}

/// <summary>
/// 导出向导(设计稿 19):数据来源 → 格式与字段 → 目标与选项 → 执行。
/// <para>
/// 字段表来自抽样 schema,输出预览用的是**真实的前 3 份文档**按当前选项写出来的样子 ——
/// 改分隔符、改编码、改某列的换算,右栏立刻变。导出本身是流式的:游标一批一批拉,
/// 写出器一份一份写,一百万份文档也只占一批的内存。
/// </para>
/// </summary>
internal sealed class ExportWizardViewModel : XferWizardViewModel
{
    private const int PreviewDocuments = 3;

    private readonly FindRequest? _query;
    private readonly bool _isDump;
    private CollectionInfo? _info;
    private IReadOnlyList<BsonDocument> _sample = [];
    private IReadOnlyList<BsonDocument> _head = [];
    private ExportFormat _format;
    private string _filterText = "";
    private bool _useQuery;
    private long _count;
    private bool _countKnown;
    private XferOption _delimiter;
    private XferOption _encoding;
    private XferOption _nested;
    private XferOption _nullMode;
    private EjsonMode _jsonMode = EjsonMode.Relaxed;
    private string _sheetName = "";
    private IReadOnlyList<string> _resultFiles = [];
    private CancellationTokenSource? _run;
    private CancellationTokenSource? _countDebounce;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">库。</param>
    /// <param name="collection">集合;<see langword="null" /> = 整库(多选集合)。</param>
    /// <param name="query">当前筛选(从网格 / 查询结果来);没有为 <see langword="null" />。</param>
    /// <param name="preset">预选的格式(外壳的「转储」按钮给 <see cref="ExportFormat.BsonDump" />)。</param>
    public ExportWizardViewModel(IMongoWorkspace workspace, string database, string? collection, FindRequest? query, ExportFormat? preset)
        : base(workspace, [
            workspace.Loc["Exp_StepSource"], workspace.Loc["Exp_StepFormat"], workspace.Loc["Exp_StepTarget"], workspace.Loc["Exp_StepRun"]
        ])
    {
        Database = database;
        Collection = collection;
        _query = query;
        _isDump = preset == ExportFormat.BsonDump;
        _format = preset ?? (collection is null ? ExportFormat.Json : ExportFormat.Csv);
        Title = _isDump ? Loc["Toolbar_Dump"] : Loc["Toolbar_Export"];
        Subtitle = collection is null ? database : $"{database}.{collection}";
        _useQuery = query is { Filter.ElementCount: > 0 };
        _filterText = query is { Filter.ElementCount: > 0 } q ? BsonText.Literal(q.Filter) : "";

        Delimiters =
        [
            new(',', Loc["Xfer_DelimComma"]), new('\t', Loc["Xfer_DelimTab"]),
            new(';', Loc["Xfer_DelimSemicolon"]), new('|', Loc["Xfer_DelimPipe"])
        ];
        Encodings =
        [
            new(TextEncodingKind.Utf8Bom, Loc["Exp_EncUtf8Bom"]), new(TextEncodingKind.Utf8, "UTF-8"),
            new(TextEncodingKind.Gbk, "GBK")
        ];
        NestedModes = [new(NestedMode.Flatten, Loc["Exp_NestedFlatten"]), new(NestedMode.Json, Loc["Exp_NestedJson"])];
        NullModes = [new(true, Loc["Exp_NullEmpty"]), new(false, Loc["Exp_NullLiteral"])];
        _delimiter = Delimiters[0];
        _encoding = Encodings[0];
        _nested = NestedModes[0];
        _nullMode = NullModes[0];

        Formats =
        [
            new(ExportFormat.Json, "JSON", Loc["Exp_FmtJsonSub"], "Mongo.braces", SelectFormat),
            new(ExportFormat.Csv, "CSV", Loc["Exp_FmtCsvSub"], "Mongo.sheet", SelectFormat),
            new(ExportFormat.Excel, "Excel", Loc["Exp_FmtExcelSub"], "Mongo.file-spreadsheet", SelectFormat),
            new(ExportFormat.BsonDump, Loc["Exp_FmtDump"], Loc["Exp_FmtDumpSub"], "Mongo.archive", SelectFormat),
            new(ExportFormat.Shell, Loc["Exp_FmtShell"], Loc["Exp_FmtShellSub"], "Mongo.file-code", SelectFormat)
        ];
        foreach (ExportFormatCard card in Formats)
        {
            card.Sync(card.Format == _format);
        }

        BrowseCommand = new AsyncCommand(BrowseAsync);
        StopCommand = new RelayCommand(() => _run?.Cancel(), () => IsRunning);
        RevealCommand = new RelayCommand(() =>
        {
            if (_resultFiles.Count > 0)
            {
                Reveal(_resultFiles.Count == 1 ? _resultFiles[0] : TargetPath);
            }
        });
        CopyPathCommand = new AsyncCommand(() => Workspace.CopyAsync(TargetPath));
        SaveProfileCommand = new AsyncCommand(SaveProfileAsync);
        SelectAllCommand = new RelayCommand(() => SetAllSources(true));
        SelectNoneCommand = new RelayCommand(() => SetAllSources(false));
        _sheetName = collection ?? "";
        UpdateSummaries();
        _ = InitializeAsync();
    }

    /// <summary>库。</summary>
    public string Database { get; }

    /// <summary>集合;整库导出为 <see langword="null" />。</summary>
    public string? Collection { get; }

    /// <inheritdoc />
    public override string IconKey => _isDump ? "Mongo.archive" : "Mongo.upload";

    /// <summary>整库(多选集合)。</summary>
    public bool IsMulti => Collection is null;

    /// <summary>单集合。</summary>
    public bool IsSingle => Collection is not null;

    /// <summary>来源卡片的标题(<c>shop.orders</c> / <c>shop</c>)。</summary>
    public string SourceTitle => Subtitle;

    /// <summary>来源卡片的图标。</summary>
    public string SourceIconKey => IsMulti ? "Mongo.database" : "Mongo.table-2";

    /// <summary>来源卡片的图标色。</summary>
    public string SourceIconToken => IsMulti ? "VelaWarning" : "VelaInfo";

    /// <summary>首次加载中。</summary>
    public bool Loading
    {
        get;
        private set => SetProperty(ref field, value);
    } = true;

    // ── 第一步:数据来源 ─────────────────────────────────────────────────

    /// <summary>多集合导出的来源列表。</summary>
    public ObservableCollection<ExportSourceRow> Sources { get; } = [];

    /// <summary>全选。</summary>
    public RelayCommand SelectAllCommand { get; }

    /// <summary>全不选。</summary>
    public RelayCommand SelectNoneCommand { get; }

    /// <summary>从网格带来了筛选条件。</summary>
    public bool HasQuery => _query is { Filter.ElementCount: > 0 };

    /// <summary>只导出当前筛选的结果(否则整个集合)。</summary>
    public bool UseQuery
    {
        get => _useQuery;
        set
        {
            if (SetProperty(ref _useQuery, value))
            {
                RaisePropertiesChanged(nameof(UseAll), nameof(FilterSummary));
                OnFilterChanged();
            }
        }
    }

    /// <summary>导出全部文档。</summary>
    public bool UseAll
    {
        get => !_useQuery;
        set => UseQuery = !value;
    }

    /// <summary>筛选条件原文(mongosh 写法,可改)。</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                if (value.Trim().Length > 0 && !_useQuery)
                {
                    _useQuery = true;
                    RaisePropertiesChanged(nameof(UseQuery), nameof(UseAll));
                }
                RaisePropertyChanged(nameof(FilterSummary));
                OnFilterChanged();
            }
        }
    }

    /// <summary>筛选条件写错了。</summary>
    public bool FilterInvalid
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>来源卡片里的那行灰字(条件原文;全部文档时为空)。</summary>
    public string FilterSummary => _useQuery && _filterText.Trim().Length > 0 ? _filterText.Trim() : Loc["Exp_AllDocuments"];

    /// <summary>当前生效的筛选。</summary>
    private BsonDocument Filter
    {
        get
        {
            if (!_useQuery || IsMulti || _filterText.Trim().Length == 0)
            {
                return [];
            }
            return ShellJson.TryParseDocument(_filterText, out BsonDocument parsed, out _) ? parsed : [];
        }
    }

    /// <summary>排序(从网格带来的)。</summary>
    private BsonDocument? Sort => _useQuery && _query?.Sort is { ElementCount: > 0 } sort ? sort : null;

    /// <summary>右上角「约 312 份文档」。</summary>
    public string CountText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>已保存的导出配置(下拉)。</summary>
    public ObservableCollection<XferOption> Profiles { get; } = [];

    /// <summary>有保存的配置。</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>选中一份配置即套用。</summary>
    public XferOption? Profile
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && value?.Value is SavedItem item && ExportProfile.Parse(item.Content) is { } profile)
            {
                ApplyProfile(profile);
            }
        }
    }

    // ── 第二步:格式与字段 ───────────────────────────────────────────────

    /// <summary>格式卡片。</summary>
    public ObservableCollection<ExportFormatCard> Formats { get; }

    /// <summary>当前格式。</summary>
    public ExportFormat Format
    {
        get => _format;
        set => SelectFormat(value);
    }

    /// <summary>CSV。</summary>
    public bool IsCsv => _format == ExportFormat.Csv;

    /// <summary>JSON。</summary>
    public bool IsJson => _format == ExportFormat.Json;

    /// <summary>Excel。</summary>
    public bool IsExcel => _format == ExportFormat.Excel;

    /// <summary>BSON 转储。</summary>
    public bool IsDump => _format == ExportFormat.BsonDump;

    /// <summary>Shell 脚本。</summary>
    public bool IsShell => _format == ExportFormat.Shell;

    /// <summary>右栏选项区的标题(<c>CSV 选项</c>)。</summary>
    public string OptionsTitle => _format switch
    {
        ExportFormat.Csv => Loc["Exp_OptCsv"],
        ExportFormat.Excel => Loc["Exp_OptExcel"],
        ExportFormat.BsonDump => Loc["Exp_OptDump"],
        ExportFormat.Shell => Loc["Exp_OptShell"],
        _ => Loc["Exp_OptJson"]
    };

    /// <summary>字段表。</summary>
    public ObservableCollection<ExportFieldRow> Fields { get; } = [];

    /// <summary>显示字段表(单集合、非转储)。</summary>
    public bool ShowFieldTable => IsSingle && _format != ExportFormat.BsonDump;

    /// <summary>字段表里显示列名与换算两列(CSV / Excel)。</summary>
    public bool ShowColumnEditing => _format is ExportFormat.Csv or ExportFormat.Excel;

    /// <summary>字段表的说明。</summary>
    public string FieldsHint => ShowColumnEditing ? Loc["Exp_FieldsHint"] : Loc["Exp_FieldsHintProjection"];

    /// <summary>列名列的表头(<c>CSV 列名</c> / <c>Excel 列名</c>)。</summary>
    public string ColumnHeaderText => _format == ExportFormat.Excel ? Loc["Exp_ColExcelName"] : Loc["Exp_ColCsvName"];

    /// <summary>不显示字段表时的说明(转储 / 多集合)。</summary>
    public string FormatNotice => _format == ExportFormat.BsonDump ? Loc["Exp_DumpNotice"]
        : IsMulti ? Loc["Exp_MultiNotice"]
        : "";

    /// <summary>勾了几个字段。</summary>
    public int IncludedCount => Fields.Count(static f => f.Include);

    // ── 右栏:格式选项 ────────────────────────────────────────────────────

    /// <summary>分隔符。</summary>
    public IReadOnlyList<XferOption> Delimiters { get; }

    /// <summary>编码。</summary>
    public IReadOnlyList<XferOption> Encodings { get; }

    /// <summary>嵌套对象。</summary>
    public IReadOnlyList<XferOption> NestedModes { get; }

    /// <summary>空值。</summary>
    public IReadOnlyList<XferOption> NullModes { get; }

    /// <summary>分隔符。</summary>
    public XferOption Delimiter
    {
        get => _delimiter;
        set
        {
            if (value is not null && SetProperty(ref _delimiter, value))
            {
                OnOptionsChanged();
            }
        }
    }

    /// <summary>编码。</summary>
    public XferOption Encoding
    {
        get => _encoding;
        set
        {
            if (value is not null && SetProperty(ref _encoding, value))
            {
                OnOptionsChanged();
            }
        }
    }

    /// <summary>嵌套对象。</summary>
    public XferOption Nested
    {
        get => _nested;
        set
        {
            if (value is not null && SetProperty(ref _nested, value))
            {
                RebuildFields();
                OnOptionsChanged();
            }
        }
    }

    /// <summary>空值。</summary>
    public XferOption NullMode
    {
        get => _nullMode;
        set
        {
            if (value is not null && SetProperty(ref _nullMode, value))
            {
                foreach (ExportFieldRow row in Fields)
                {
                    row.SetNullMode((bool)value.Value);
                }
                OnOptionsChanged();
            }
        }
    }

    /// <summary>首行写入列名。</summary>
    public bool WriteHeader
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = true;

    /// <summary>JSON:行分隔。</summary>
    public bool JsonLines
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(JsonArray));
                OnOptionsChanged();
            }
        }
    } = true;

    /// <summary>JSON:数组。</summary>
    public bool JsonArray
    {
        get => !JsonLines;
        set => JsonLines = !value;
    }

    /// <summary>EJSON:Shell 写法。</summary>
    public bool JsonShell
    {
        get => _jsonMode == EjsonMode.Shell;
        set => SetJsonMode(value, EjsonMode.Shell);
    }

    /// <summary>EJSON:Relaxed。</summary>
    public bool JsonRelaxed
    {
        get => _jsonMode == EjsonMode.Relaxed;
        set => SetJsonMode(value, EjsonMode.Relaxed);
    }

    /// <summary>EJSON:Canonical。</summary>
    public bool JsonCanonical
    {
        get => _jsonMode == EjsonMode.Canonical;
        set => SetJsonMode(value, EjsonMode.Canonical);
    }

    /// <summary>Excel 工作表名。</summary>
    public string SheetName
    {
        get => _sheetName;
        set
        {
            if (SetProperty(ref _sheetName, value))
            {
                OnOptionsChanged();
            }
        }
    }

    /// <summary>Excel 冻结首行。</summary>
    public bool FreezeHeader
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>转储 gzip。</summary>
    public bool Gzip
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    }

    /// <summary>Shell:每条 insertMany 的文档数。</summary>
    public string ShellBatch
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = "1000";

    /// <summary>输出预览。</summary>
    public ObservableCollection<XferPreviewLine> PreviewLines { get; } = [];

    /// <summary>预估:文档数。</summary>
    public string EstimateDocs
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    /// <summary>预估:文件大小。</summary>
    public string EstimateSize
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    /// <summary>预估:耗时。</summary>
    public string EstimateTime
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    // ── 第三步:目标 ─────────────────────────────────────────────────────

    /// <summary>目标路径(文件,或转储 / 多集合时的目录)。</summary>
    public string TargetPath
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(TargetExists), nameof(HasTarget));
                UpdateSummaries();
            }
        }
    } = "";

    /// <summary>选了目标。</summary>
    public bool HasTarget => TargetPath.Trim().Length > 0;

    /// <summary>目标是目录(转储、多集合的 JSON / CSV)。</summary>
    public bool TargetIsFolder => _format == ExportFormat.BsonDump || IsMulti && _format is ExportFormat.Json or ExportFormat.Csv;

    /// <summary>目标那一栏的标签(<c>保存到文件</c> / <c>保存到目录</c>)。</summary>
    public string TargetLabel => TargetIsFolder ? Loc["Exp_TargetFolder"] : Loc["Exp_TargetFile"];

    /// <summary>目标已存在(将覆盖)。</summary>
    public bool TargetExists
    {
        get
        {
            string path = TargetPath.Trim();
            if (path.Length == 0)
            {
                return false;
            }
            if (!TargetIsFolder)
            {
                return File.Exists(path);
            }
            return _format == ExportFormat.BsonDump
                ? Directory.Exists(Path.Combine(path, TransferText.SafeFileName(Database)))
                : SelectedSources().Any(s => File.Exists(Path.Combine(path, TransferText.SafeFileName(s.Name) + ExportJob.Extension(_format, JsonOptionsValue))));
        }
    }

    /// <summary>完成后在资源管理器中显示。</summary>
    public bool RevealWhenDone
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>浏览…</summary>
    public AsyncCommand BrowseCommand { get; }

    /// <summary>目标一步的摘要行(<c>CSV · 7 个字段 · ≈ 312 份 · ≈ 38 KB</c>)。</summary>
    public string PlanSummary => string.Join(" · ", new[]
    {
        Formats.First(f => f.Format == _format).Title,
        ShowColumnEditing && IsSingle ? Loc.Format("Exp_FieldsCount", IncludedCount) : null,
        IsMulti ? Loc.Format("Exp_CollectionsCount", SelectedSources().Count) : null,
        EstimateDocs.Length > 1 ? Loc.Format("Exp_DocsShort", EstimateDocs) : null,
        EstimateSize
    }.Where(static s => !string.IsNullOrEmpty(s) && s != "—"));

    // ── 第四步:执行 ─────────────────────────────────────────────────────

    /// <summary>进度 0–100。</summary>
    public double Progress
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>进度说明(<c>908,330 / 1,812,640 文档 · 已用 50 s · 预计剩余 50 s</c>)。</summary>
    public string ProgressText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>百分比。</summary>
    public string PercentText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "0%";

    /// <summary>速率(<c>18.2k 文档/s</c>)。</summary>
    public string RateText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>已写出字节。</summary>
    public string BytesText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>结果说明。</summary>
    public string ResultText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasResult));
            }
        }
    } = "";

    /// <summary>有结果。</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>结果的语气(<c>ok</c> / <c>warn</c> / <c>err</c>)。</summary>
    public string ResultTone
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ResultOk), nameof(ResultFailed));
            }
        }
    } = "ok";

    /// <summary>成功。</summary>
    public bool ResultOk => ResultTone == "ok";

    /// <summary>失败或取消。</summary>
    public bool ResultFailed => ResultTone != "ok";

    /// <summary>执行日志。</summary>
    public ObservableCollection<XferLogLine> RunLog { get; } = [];

    /// <summary>生成的文件。</summary>
    public ObservableCollection<string> OutputFiles { get; } = [];

    /// <summary>停止导出。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>在资源管理器中显示。</summary>
    public RelayCommand RevealCommand { get; }

    /// <summary>复制路径。</summary>
    public AsyncCommand CopyPathCommand { get; }

    /// <summary>保存为导出配置。</summary>
    public AsyncCommand SaveProfileCommand { get; }

    /// <inheritdoc />
    protected override string StartText => _countKnown
        ? Loc.Format("Exp_Start", BsonText.Grouped(_count))
        : Loc["Exp_StartPlain"];

    // ── 加载 ────────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        try
        {
            IReadOnlyList<CollectionInfo> all = Workspace.CollectionsOf(Database);
            if (all.Count == 0)
            {
                all = await Workspace.Connection.ListCollectionsAsync(Database).ConfigureAwait(true);
            }
            if (IsMulti)
            {
                foreach (CollectionInfo info in all.Where(static c => c.Kind != CollectionKind.System))
                {
                    Sources.Add(new ExportSourceRow(info, isChecked: true, OnSourcesChanged));
                }
                RaisePropertyChanged(nameof(PlanSummary));
            }
            else
            {
                _info = all.FirstOrDefault(c => c.Name == Collection)
                        ?? new CollectionInfo(Database, Collection!, CollectionKind.Collection, []);
            }
            await RefreshCountAsync(CancellationToken.None).ConfigureAwait(true);
            if (IsSingle)
            {
                await LoadSampleAsync().ConfigureAwait(true);
            }
            else
            {
                RefreshPreview();
            }
            IReadOnlyList<SavedItem> saved = await Workspace.Store.LoadSavedAsync(ExportProfile.Kind, Workspace.ConnectionKey).ConfigureAwait(true);
            foreach (SavedItem item in saved)
            {
                Profiles.Add(new XferOption(item, item.Name));
            }
            RaisePropertyChanged(nameof(HasProfiles));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            Loading = false;
        }
    }

    private async Task RefreshCountAsync(CancellationToken cancellationToken)
    {
        MongoConnection connection = Workspace.Connection;
        if (IsMulti)
        {
            long total = 0;
            foreach (ExportSourceRow row in Sources)
            {
                row.Count = await connection.EstimatedCountAsync(Database, row.Name, cancellationToken).ConfigureAwait(true) ?? 0;
                row.CountText = BsonText.Grouped(row.Count);
                if (row.Info.Kind != CollectionKind.View)
                {
                    row.Bytes = (await connection.GetStatsAsync(Database, row.Name, cancellationToken).ConfigureAwait(true)).Size;
                }
                if (row.IsChecked)
                {
                    total += row.Count;
                }
            }
            SetCount(total, known: true);
            return;
        }
        BsonDocument filter = Filter;
        long count;
        try
        {
            count = await connection.Collection(Database, Collection!).CountDocumentsAsync(filter,
                new CountOptions { MaxTime = TimeSpan.FromSeconds(8) }, cancellationToken).ConfigureAwait(true);
        }
        catch (MongoException) when (filter.ElementCount == 0)
        {
            count = await connection.EstimatedCountAsync(Database, Collection!, cancellationToken).ConfigureAwait(true) ?? 0;
        }
        SetCount(count, known: true);
    }

    private void SetCount(long count, bool known)
    {
        _count = count;
        _countKnown = known;
        CountText = known ? Loc.Format("Exp_AboutDocs", BsonText.Grouped(count)) : "";
        RaiseStartTextChanged();
        UpdateEstimate();
        UpdateSummaries();
    }

    private async Task LoadSampleAsync()
    {
        IMongoCollection<BsonDocument> collection = Workspace.Connection.Collection(Database, Collection!);
        int size = Math.Clamp(Workspace.Connection.Settings.SampleSize, 50, 1000);
        BsonDocument filter = Filter;
        _sample = await SchemaSampler.SampleAsync(collection, filter, size, CancellationToken.None).ConfigureAwait(true);
        IFindFluent<BsonDocument, BsonDocument> head = collection.Find(filter).Limit(PreviewDocuments);
        if (Sort is { } sort)
        {
            head = head.Sort(sort);
        }
        _head = await head.ToListAsync().ConfigureAwait(true);
        RebuildFields();
        RefreshPreview();
        UpdateEstimate();
    }

    private void OnFilterChanged()
    {
        if (_filterText.Trim().Length > 0)
        {
            FilterInvalid = !ShellJson.TryParseDocument(_filterText, out _, out _);
        }
        else
        {
            FilterInvalid = false;
        }
        UpdateSummaries();
        if (FilterInvalid || IsMulti)
        {
            return;
        }
        // 改条件时防抖 400 ms 再去数:打字的每一个字符都发一次 countDocuments 毫无意义。
        _countDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _countDebounce = cts;
        _ = DebouncedAsync(cts.Token);

        async Task DebouncedAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(400, token).ConfigureAwait(true);
                await RefreshCountAsync(token).ConfigureAwait(true);
                await LoadSampleAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                CountText = Loc.Format("Common_Failed", MongoConnector.Describe(ex));
            }
        }
    }

    private void OnSourcesChanged()
    {
        SetCount(Sources.Where(static s => s.IsChecked).Sum(static s => s.Count), known: true);
        RaisePropertiesChanged(nameof(PlanSummary), nameof(TargetExists));
    }

    private void SetAllSources(bool value)
    {
        foreach (ExportSourceRow row in Sources)
        {
            row.IsChecked = value;
        }
    }

    private IReadOnlyList<CollectionInfo> SelectedSources() => IsMulti
        ? [.. Sources.Where(static s => s.IsChecked).Select(static s => s.Info)]
        : _info is { } info ? [info] : [];

    // ── 格式、字段、预览 ───────────────────────────────────────────────

    private void SelectFormat(ExportFormat format)
    {
        bool changed = _format != format;
        _format = format;
        foreach (ExportFormatCard card in Formats)
        {
            card.Sync(card.Format == format);
        }
        if (!changed)
        {
            return;
        }
        RaisePropertiesChanged(nameof(Format), nameof(IsCsv), nameof(IsJson), nameof(IsExcel), nameof(IsDump), nameof(IsShell),
            nameof(OptionsTitle), nameof(ShowFieldTable), nameof(ShowColumnEditing), nameof(FieldsHint), nameof(ColumnHeaderText),
            nameof(FormatNotice), nameof(TargetIsFolder), nameof(TargetLabel), nameof(TargetExists));
        RebuildFields();
        // 换了格式,原先选的目标扩展名就不对了:清空,让用户重选(而不是悄悄写出一个 .csv 名字的 xlsx)。
        if (TargetPath.Length > 0)
        {
            TargetPath = "";
        }
        OnOptionsChanged();
    }

    private void SetJsonMode(bool on, EjsonMode mode)
    {
        if (!on || _jsonMode == mode)
        {
            return;
        }
        _jsonMode = mode;
        RaisePropertiesChanged(nameof(JsonShell), nameof(JsonRelaxed), nameof(JsonCanonical));
        OnOptionsChanged();
    }

    /// <summary>按当前格式与嵌套选项重建字段表(保留用户已改的勾选、列名、换算与顺序)。</summary>
    private void RebuildFields()
    {
        if (IsMulti)
        {
            return;
        }
        bool flatten = _format == ExportFormat.Excel || _format == ExportFormat.Csv && (NestedMode)_nested.Value == NestedMode.Flatten;
        IReadOnlyList<XferField> sampled = SchemaSampler.Analyze(_sample, flatten);
        var previous = Fields.ToDictionary(static f => f.Path, StringComparer.Ordinal);
        var order = Fields.Select(static (f, i) => (f.Path, i)).ToDictionary(static p => p.Path, static p => p.i, StringComparer.Ordinal);
        Fields.Clear();
        bool columns = _format is ExportFormat.Csv or ExportFormat.Excel;
        foreach (XferField field in sampled.OrderBy(f => order.TryGetValue(f.Path, out int i) ? i : int.MaxValue))
        {
            ExportFieldRow row;
            if (previous.TryGetValue(field.Path, out ExportFieldRow? old))
            {
                row = new ExportFieldRow(field, old.Include, old.Header, old.Conversion, Loc, OnFieldsChanged);
            }
            else
            {
                // 数组默认不进 CSV / Excel:一列 JSON 字符串在表格里几乎没法用,要的人自己勾上。
                bool include = !columns || field.Dominant is not BsonKind.Array;
                row = new ExportFieldRow(field, include, field.Path, field.DefaultConversion, Loc, OnFieldsChanged);
            }
            row.SetNullMode((bool)_nullMode.Value);
            Fields.Add(row);
        }
        RaisePropertyChanged(nameof(IncludedCount));
    }

    private void OnFieldsChanged()
    {
        RaisePropertyChanged(nameof(IncludedCount));
        OnOptionsChanged();
    }

    /// <summary>拖动调整列顺序。</summary>
    internal void MoveField(int from, int to)
    {
        if (from < 0 || from >= Fields.Count || to < 0 || to >= Fields.Count || from == to)
        {
            return;
        }
        Fields.Move(from, to);
        OnOptionsChanged();
    }

    private void OnOptionsChanged()
    {
        RefreshPreview();
        UpdateEstimate();
        UpdateSummaries();
    }

    private CsvOptions CsvOptionsValue => new()
    {
        Delimiter = (char)_delimiter.Value,
        Encoding = (TextEncodingKind)_encoding.Value,
        Nested = (NestedMode)_nested.Value,
        NullAsEmpty = (bool)_nullMode.Value,
        WriteHeader = WriteHeader
    };

    private JsonOptions JsonOptionsValue => new() { Lines = JsonLines, Mode = _jsonMode };

    private ExcelOptions ExcelOptionsValue => new() { SheetName = _sheetName, FreezeHeader = FreezeHeader, WriteHeader = WriteHeader };

    private ShellOptions ShellOptionsValue => new()
    {
        BatchSize = int.TryParse(ShellBatch, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 1000
    };

    private IReadOnlyList<ExportColumn> Columns => [.. Fields.Where(static f => f.Include).Select(static f => f.ToColumn())];

    /// <summary>JSON / Shell 只导出勾选的字段时的投影;全勾时为 <see langword="null" />(不投影,保留字段原有顺序)。</summary>
    private BsonDocument? Projection
    {
        get
        {
            if (_format is not (ExportFormat.Json or ExportFormat.Shell) || IsMulti || Fields.All(static f => f.Include))
            {
                return null;
            }
            var projection = new BsonDocument();
            foreach (ExportFieldRow row in Fields.Where(static f => f.Include))
            {
                projection[row.Path] = 1;
            }
            if (!Fields.Any(static f => f.Path == "_id" && f.Include))
            {
                projection["_id"] = 0;
            }
            return projection;
        }
    }

    private void RefreshPreview()
    {
        PreviewLines.Clear();
        IReadOnlyList<BsonDocument> docs = _head;
        string collection = Collection ?? SelectedSources().FirstOrDefault()?.Name ?? "";
        switch (_format)
        {
            case ExportFormat.Csv:
                {
                    CsvOptions csv = CsvOptionsValue;
                    IReadOnlyList<ExportColumn> columns = Columns;
                    if (csv.WriteHeader && columns.Count > 0)
                    {
                        PreviewLines.Add(new(CsvWriter.FormatRow([.. columns.Select(static c => c.Header)], csv.Delimiter), true));
                    }
                    foreach (BsonDocument doc in docs)
                    {
                        PreviewLines.Add(new(CsvWriter.FormatRow([.. columns.Select(c => PreviewCell(CellFormatter.Format(BsonPath.Get(doc, c.Path), c.Conversion, csv.NullAsEmpty)))], csv.Delimiter), false));
                    }
                    break;
                }
            case ExportFormat.Excel:
                {
                    IReadOnlyList<ExportColumn> columns = Columns;
                    if (WriteHeader && columns.Count > 0)
                    {
                        PreviewLines.Add(new(string.Join(" │ ", columns.Select(static c => c.Header)), true));
                    }
                    foreach (BsonDocument doc in docs)
                    {
                        PreviewLines.Add(new(string.Join(" │ ", columns.Select(c => PreviewCell(CellFormatter.Format(BsonPath.Get(doc, c.Path), c.Conversion, true)))), false));
                    }
                    break;
                }
            case ExportFormat.Json:
                {
                    if (!JsonLines)
                    {
                        PreviewLines.Add(new("[", true));
                    }
                    BsonDocument? projection = Projection;
                    foreach (BsonDocument doc in docs)
                    {
                        PreviewLines.Add(new(BsonText.Compact(Project(doc, projection), _jsonMode) + (JsonLines ? "" : ","), false));
                    }
                    break;
                }
            case ExportFormat.Shell:
                {
                    PreviewLines.Add(new($"db.getSiblingDB({BsonText.Quote(Database)}).getCollection({BsonText.Quote(collection)}).insertMany([", true));
                    BsonDocument? projection = Projection;
                    foreach (BsonDocument doc in docs)
                    {
                        PreviewLines.Add(new("  " + BsonText.Literal(Project(doc, projection)) + ",", false));
                    }
                    break;
                }
            default:
                {
                    foreach (CollectionInfo info in SelectedSources().Take(3))
                    {
                        string db = TransferText.SafeFileName(Database);
                        string name = TransferText.SafeFileName(info.Name);
                        if (info.Kind != CollectionKind.View)
                        {
                            PreviewLines.Add(new($"{db}/{name}.bson{(Gzip ? ".gz" : "")}", true));
                        }
                        PreviewLines.Add(new($"{db}/{name}.metadata.json{(Gzip ? ".gz" : "")}", false));
                    }
                    break;
                }
        }
    }

    /// <summary>预览里的 ObjectId 缩成 12 位 + 省略号(与设计稿一致;真实文件里是完整的 24 位)。</summary>
    private static string PreviewCell(string text) =>
        text.Length == 24 && text.All(Uri.IsHexDigit) ? text[..12] + "…" : text;

    private static BsonDocument Project(BsonDocument doc, BsonDocument? projection)
    {
        if (projection is null)
        {
            return doc;
        }
        var result = new BsonDocument();
        foreach (BsonElement element in projection)
        {
            if (element.Value.ToInt32() == 1 && BsonPath.Get(doc, element.Name) is { } value)
            {
                _ = BsonPath.Set(result, element.Name, value);
            }
        }
        return result;
    }

    /// <summary>预估:文档数、按抽样平均行长算的文件大小、粗估的耗时。</summary>
    private void UpdateEstimate()
    {
        EstimateDocs = _countKnown ? "≈ " + BsonText.Grouped(_count) : "—";
        double bytesPerDoc = AverageBytes();
        if (bytesPerDoc > 0 && _countKnown)
        {
            long bytes = (long)(bytesPerDoc * _count);
            EstimateSize = "≈ " + BsonText.Bytes(bytes);
            // 本机写文件远快于网络:按 2.5 万份/秒 + 60 MB/s 粗估,只求数量级对。
            double seconds = _count / 25_000d + bytes / (60d * 1024 * 1024);
            EstimateTime = TransferText.Duration(TimeSpan.FromSeconds(seconds));
        }
        else
        {
            EstimateSize = "—";
            EstimateTime = _countKnown ? TransferText.Duration(TimeSpan.FromSeconds(_count / 25_000d)) : "—";
        }
        RaisePropertyChanged(nameof(PlanSummary));
    }

    private double AverageBytes()
    {
        if (IsMulti)
        {
            // 整库导出不抽样:按各集合的 BSON 数据量估,再乘一个格式系数(文本比 BSON 胖,gzip 约三分之一)。
            long docsTotal = Sources.Where(static s => s.IsChecked).Sum(static s => s.Count);
            long bytesTotal = Sources.Where(static s => s.IsChecked).Sum(static s => s.Bytes);
            if (docsTotal == 0 || bytesTotal == 0)
            {
                return 0;
            }
            double factor = _format switch
            {
                ExportFormat.BsonDump => Gzip ? 0.35 : 1.0,
                ExportFormat.Json => 1.3,
                ExportFormat.Shell => 1.4,
                ExportFormat.Excel => 0.5,
                _ => 0.8
            };
            return bytesTotal * factor / docsTotal;
        }
        IReadOnlyList<BsonDocument> docs = _sample.Count > 0 ? [.. _sample.Take(200)] : _head;
        if (docs.Count == 0)
        {
            return 0;
        }
        try
        {
            if (_format == ExportFormat.BsonDump)
            {
                double raw = docs.Average(static d => (double)d.ToBson().Length);
                return Gzip ? raw * 0.35 : raw;
            }
            ExportJob job = BuildJob("estimate");
            using var sink = new CountingStream(Stream.Null);
            using (ExportWriter writer = ExportRunner.CreateWriter(job, sink))
            {
                writer.Begin(Collection ?? "", Columns);
                BsonDocument? projection = Projection;
                foreach (BsonDocument doc in docs)
                {
                    writer.Write(Project(doc, projection));
                }
                writer.End();
                writer.Complete();
            }
            return sink.Written / (double)docs.Count;
        }
        catch (Exception)
        {
            // 预估只是锦上添花:哪种格式在抽样上写不出来,就显示"—",绝不能因此让向导挂掉。
            return 0;
        }
    }

    private void UpdateSummaries()
    {
        Steps[0].Summary = IsMulti
            ? Loc.Format("Exp_SummaryMulti", Database, SelectedSources().Count)
            : Loc.Format("Exp_SummarySingle", Subtitle, _useQuery && _filterText.Trim().Length > 0 ? Loc["Exp_CurrentFilter"] : Loc["Exp_AllDocuments"]);
        string formatName = Formats.First(f => f.Format == _format).Title;
        Steps[1].Summary = ShowColumnEditing && IsSingle ? $"{formatName} · {Loc.Format("Exp_FieldsCount", IncludedCount)}" : formatName;
        Steps[2].Summary = HasTarget ? Path.GetFileName(TargetPath.TrimEnd('\\', '/')) : Loc["Exp_LocalFile"];
        RaisePropertyChanged(nameof(PlanSummary));
    }

    // ── 目标 ────────────────────────────────────────────────────────────

    private async Task BrowseAsync()
    {
        if (TargetIsFolder)
        {
            string? folder = await Workspace.PickFolderAsync(Loc["Exp_PickFolder"]).ConfigureAwait(true);
            if (folder is not null)
            {
                TargetPath = folder;
            }
            return;
        }
        string name = TransferText.SafeFileName(Collection ?? Database) + ExportJob.Extension(_format, JsonOptionsValue);
        FileKind kind = _format switch
        {
            ExportFormat.Csv => FileKind.Csv,
            ExportFormat.Excel => FileKind.Excel,
            ExportFormat.Shell => FileKind.Script,
            _ => FileKind.Json
        };
        string? path = await Workspace.PickSaveFileAsync(Loc["Exp_PickFile"], name, [kind, FileKind.Any]).ConfigureAwait(true);
        if (path is not null)
        {
            TargetPath = path;
        }
    }

    // ── 校验与执行 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override async Task<bool> ValidateAsync(int step)
    {
        switch (step)
        {
            case 0:
                if (FilterInvalid)
                {
                    Workspace.Toast(new() { Title = Loc["Exp_BadFilter"], Kind = ToastKind.Warning });
                    return false;
                }
                if (SelectedSources().Count == 0)
                {
                    Workspace.Toast(new() { Title = Loc["Exp_NoSource"], Kind = ToastKind.Warning });
                    return false;
                }
                return true;
            case 1:
                if (ShowColumnEditing && IsSingle && IncludedCount == 0)
                {
                    Workspace.Toast(new() { Title = Loc["Exp_NoFields"], Kind = ToastKind.Warning });
                    return false;
                }
                return true;
            case 2:
                if (!HasTarget)
                {
                    await BrowseAsync().ConfigureAwait(true);
                }
                return HasTarget;
            default:
                return true;
        }
    }

    /// <inheritdoc />
    protected override async Task StartAsync()
    {
        if (TargetExists && !await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Exp_OverwriteTitle"],
            Message = Loc.Format("Exp_OverwriteBody", TargetPath),
            ConfirmLabel = Loc["Exp_Overwrite"],
            IconKey = "Mongo.triangle-alert"
        }).ConfigureAwait(true))
        {
            return;
        }
        ExportJob job = BuildJob(TargetPath.Trim());
        EnterRunStep();
        RunLog.Clear();
        OutputFiles.Clear();
        ResultText = "";
        Progress = 0;
        PercentText = "0%";
        StopCommand.RaiseCanExecuteChanged();
        Steps[RunStep].Summary = Loc["Xfer_Running"];
        AddLog(Loc.Format("Exp_LogStart", SourceTitle, Formats.First(f => f.Format == _format).Title, job.Target), XferTone.Normal);
        _run = new CancellationTokenSource();
        var progress = new Progress<ExportProgress>(OnProgress);
        CancellationToken token = _run.Token;
        try
        {
            ExportResult result = await Task.Run(() => ExportRunner.RunAsync(Workspace.Connection, job, progress, token), token).ConfigureAwait(true);
            _resultFiles = result.Files;
            foreach (string file in result.Files)
            {
                OutputFiles.Add(file);
            }
            Progress = 100;
            PercentText = "100%";
            ProgressText = Loc.Format("Exp_ProgressDone", BsonText.Grouped(result.Documents), TransferText.Duration(result.Elapsed));
            BytesText = BsonText.Bytes(result.Bytes);
            // 完成后给全程平均速率(很快跑完时最后一次进度上报可能还没来得及算出速率)。
            RateText = Loc.Format("Xfer_Rate", TransferText.Rate(result.Documents / Math.Max(result.Elapsed.TotalSeconds, 0.001)));
            ResultTone = "ok";
            ResultText = Loc.Format("Exp_Done", BsonText.Grouped(result.Documents), BsonText.Bytes(result.Bytes), TransferText.Duration(result.Elapsed));
            Steps[RunStep].Summary = Loc["Xfer_Completed"];
            AddLog(ResultText, XferTone.Ok);
            if (RevealWhenDone && result.Files.Count > 0)
            {
                Reveal(result.Files.Count == 1 ? result.Files[0] : job.Target);
            }
            string revealPath = result.Files.Count == 1 ? result.Files[0] : job.Target;
            FinishRun(new ToastRequest
            {
                Title = Loc.Format("Exp_ToastDone", BsonText.Grouped(result.Documents)),
                Detail = revealPath,
                Kind = ToastKind.Success,
                ActionLabel = Loc["Exp_Reveal"],
                Action = () =>
                {
                    Reveal(revealPath);
                    return Task.CompletedTask;
                },
                Duration = TimeSpan.FromSeconds(8)
            });
        }
        catch (OperationCanceledException)
        {
            ResultTone = "warn";
            ResultText = Loc["Exp_Cancelled"];
            Steps[RunStep].Summary = Loc["Xfer_Cancelled"];
            AddLog(ResultText, XferTone.Warn);
            FinishRun(new ToastRequest { Title = Loc["Exp_Cancelled"], Kind = ToastKind.Info });
        }
        catch (Exception ex)
        {
            // 不只接驱动异常:写文件、编码、换算里任何一处抛出来,执行页都要落到"失败"而不是永远停在"进行中"。
            Workspace.Log.Error("Export failed.", ex);
            string reason = MongoConnector.Describe(ex);
            ResultTone = "err";
            ResultText = Loc.Format("Common_Failed", reason);
            Steps[RunStep].Summary = Loc["Xfer_Failed"];
            AddLog(ResultText, XferTone.Error);
            FinishRun(new ToastRequest { Title = Loc.Format("Common_Failed", reason), Kind = ToastKind.Error });
        }
        finally
        {
            _run.Dispose();
            _run = null;
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private ExportJob BuildJob(string target) => new()
    {
        Database = Database,
        Sources = IsMulti
            ? [.. SelectedSources().Select(static s => new ExportSource(s))]
            : [new ExportSource(_info ?? new CollectionInfo(Database, Collection!, CollectionKind.Collection, []), Columns)],
        Format = _format,
        Target = target,
        TargetIsFolder = TargetIsFolder,
        Filter = Filter,
        Sort = Sort,
        Projection = Projection,
        Csv = CsvOptionsValue,
        Json = JsonOptionsValue,
        Excel = ExcelOptionsValue,
        Dump = new DumpOptions { Gzip = Gzip },
        Shell = ShellOptionsValue,
        EstimatedTotal = _countKnown ? _count : 0
    };

    private void OnProgress(ExportProgress p)
    {
        double fraction = p.Total > 0 ? Math.Min(1, (double)p.Documents / p.Total) : 0;
        Progress = fraction * 100;
        PercentText = (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        double rate = p.Elapsed.TotalSeconds > 0.2 ? p.Documents / p.Elapsed.TotalSeconds : 0;
        string remaining = rate > 0 && p.Total > p.Documents ? TransferText.Duration(TimeSpan.FromSeconds((p.Total - p.Documents) / rate)) : "—";
        ProgressText = Loc.Format("Exp_ProgressLine", BsonText.Grouped(p.Documents), BsonText.Grouped(p.Total), TransferText.Duration(p.Elapsed), remaining);
        RateText = Loc.Format("Xfer_Rate", TransferText.Rate(rate));
        BytesText = BsonText.Bytes(p.Bytes);
        Steps[RunStep].Summary = Loc.Format("Xfer_RunningPercent", PercentText);
    }

    private void AddLog(string text, XferTone tone) => OnUi(() => RunLog.Add(new XferLogLine(Now(), text, tone)));

    // ── 配置 ────────────────────────────────────────────────────────────

    private async Task SaveProfileAsync()
    {
        var profile = new BsonDocument
        {
            { "format", _format.ToString() },
            { "useQuery", _useQuery },
            { "filter", _filterText },
            { "csv", new BsonDocument
                {
                    { "delimiter", ((char)_delimiter.Value).ToString() },
                    { "encoding", ((TextEncodingKind)_encoding.Value).ToString() },
                    { "nested", ((NestedMode)_nested.Value).ToString() },
                    { "nullAsEmpty", (bool)_nullMode.Value },
                    { "header", WriteHeader }
                }
            },
            { "json", new BsonDocument { { "lines", JsonLines }, { "mode", _jsonMode.ToString() } } },
            { "excel", new BsonDocument { { "sheet", _sheetName }, { "freeze", FreezeHeader } } },
            { "dump", new BsonDocument("gzip", Gzip) },
            { "shell", new BsonDocument("batch", ShellBatch) },
            { "fields", new BsonArray(Fields.Select(static f => new BsonDocument
                {
                    { "path", f.Path },
                    { "header", f.Header },
                    { "include", f.Include },
                    { "conversion", f.Conversion.ToString() }
                }))
            }
        };
        string name = $"{SourceTitle} · {Formats.First(f => f.Format == _format).Title}";
        var item = new SavedItem(name, ExportProfile.Serialize(profile), DateTimeOffset.Now);
        await Workspace.Store.SaveItemAsync(ExportProfile.Kind, Workspace.ConnectionKey, item).ConfigureAwait(true);
        XferOption? existing = Profiles.FirstOrDefault(p => p.Label == name);
        if (existing is not null)
        {
            _ = Profiles.Remove(existing);
        }
        Profiles.Insert(0, new XferOption(item, name));
        RaisePropertyChanged(nameof(HasProfiles));
        Workspace.Toast(new() { Title = Loc.Format("Exp_ProfileSaved", name), Kind = ToastKind.Success });
    }

    /// <summary>套用一份保存的配置(字段只套用这次抽样里也有的那些)。</summary>
    internal void ApplyProfile(BsonDocument profile)
    {
        if (Enum.TryParse(profile.GetValue("format", "").AsString, out ExportFormat format))
        {
            SelectFormat(format);
        }
        if (profile.GetValue("csv", new BsonDocument()) is BsonDocument csv)
        {
            string delimiter = csv.GetValue("delimiter", ",").AsString;
            Delimiter = Delimiters.FirstOrDefault(d => ((char)d.Value).ToString() == delimiter) ?? Delimiters[0];
            Encoding = Encodings.FirstOrDefault(e => e.Value.ToString() == csv.GetValue("encoding", "").AsString) ?? _encoding;
            Nested = NestedModes.FirstOrDefault(n => n.Value.ToString() == csv.GetValue("nested", "").AsString) ?? _nested;
            NullMode = NullModes.FirstOrDefault(n => (bool)n.Value == csv.GetValue("nullAsEmpty", true).ToBoolean()) ?? _nullMode;
            WriteHeader = csv.GetValue("header", true).ToBoolean();
        }
        if (profile.GetValue("json", new BsonDocument()) is BsonDocument json)
        {
            JsonLines = json.GetValue("lines", true).ToBoolean();
            if (Enum.TryParse(json.GetValue("mode", "Relaxed").AsString, out EjsonMode mode))
            {
                SetJsonMode(true, mode);
            }
        }
        if (profile.GetValue("excel", new BsonDocument()) is BsonDocument excel)
        {
            FreezeHeader = excel.GetValue("freeze", true).ToBoolean();
        }
        if (profile.GetValue("dump", new BsonDocument()) is BsonDocument dump)
        {
            Gzip = dump.GetValue("gzip", false).ToBoolean();
        }
        if (profile.GetValue("fields", new BsonArray()) is BsonArray fields)
        {
            var byPath = Fields.ToDictionary(static f => f.Path, StringComparer.Ordinal);
            int position = 0;
            foreach (BsonDocument saved in fields.OfType<BsonDocument>())
            {
                if (!byPath.TryGetValue(saved.GetValue("path", "").AsString, out ExportFieldRow? row))
                {
                    continue;
                }
                row.Include = saved.GetValue("include", true).ToBoolean();
                row.Header = saved.GetValue("header", row.Path).AsString;
                if (Enum.TryParse(saved.GetValue("conversion", "").AsString, out CellConversion conversion))
                {
                    row.ConversionOption = row.Choices.FirstOrDefault(c => (CellConversion)c.Value == conversion) ?? row.ConversionOption;
                }
                int index = Fields.IndexOf(row);
                if (index != position && position < Fields.Count)
                {
                    Fields.Move(index, position);
                }
                position++;
            }
        }
        OnOptionsChanged();
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        _countDebounce?.Cancel();
        base.OnClosed();
    }
}
