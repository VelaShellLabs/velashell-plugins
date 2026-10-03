using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 导入向导(设计稿 20):选择文件 → 字段映射 → 预览与校验 → 导入。
/// <para>
/// 预览与校验是对前 1,000 行的 **dry-run**:用与正式导入完全相同的换算器走一遍,
/// 把类型转换、validator 的 required / enum、upsert 匹配键缺失这些问题在写之前就摆出来;
/// 右栏「预计结果」按 dry-run 的比例外推到整个文件,upsert 命中数用匹配键 <c>$in</c> 实际查一次。
/// </para>
/// </summary>
internal sealed class ImportWizardViewModel : XferWizardViewModel
{
    private const int DryRunRows = 1000;
    private const int SampleRows = 200;

    private const string KeepKind = "keep";

    private readonly Task _ready;
    private ImportSource? _source;
    private CollectionInfo? _target;
    private ImportRules _baseRules = new();
    private HashSet<string> _uniqueKeys = [with(StringComparer.Ordinal)];
    private Dictionary<string, BsonKind> _targetKinds = [with(StringComparer.Ordinal)];
    private List<ConvertedRow> _dryRows = [];
    private long? _rowCount;
    private XferOption _encodingChoice;
    private XferOption _delimiterChoice;
    private bool _hasHeader = true;
    private ImportFilter _filter = ImportFilter.All;
    private int _okCount;
    private int _warnCount;
    private int _errCount;
    private ImportWriteMode _mode = ImportWriteMode.Insert;
    private XferOption? _matchKey;
    private XferOption _onError;
    private string _dateGuess = "";
    private CancellationTokenSource? _countCts;
    private CancellationTokenSource? _run;
    private string _resultTone = "ok";
    private string? _errorReport;
    private long _inserted;
    private long _updated;
    private long _skipped;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">目标库。</param>
    /// <param name="collection">目标集合。</param>
    public ImportWizardViewModel(IMongoWorkspace workspace, string database, string collection)
        : base(workspace, [
            workspace.Loc["Imp_StepFile"], workspace.Loc["Imp_StepMapping"], workspace.Loc["Imp_StepPreview"], workspace.Loc["Imp_StepRun"]
        ])
    {
        Database = database;
        Collection = collection;
        Title = Loc["Toolbar_Import"];
        Subtitle = $"{database}.{collection}";
        EncodingChoices =
        [
            new(KeepKind, Loc["Imp_Auto"]), new(TextEncodingKind.Utf8, "UTF-8"), new(TextEncodingKind.Gbk, "GBK"),
            new(TextEncodingKind.Utf16, "UTF-16")
        ];
        DelimiterChoices =
        [
            new(KeepKind, Loc["Imp_Auto"]), new(',', Loc["Xfer_DelimComma"]), new('\t', Loc["Xfer_DelimTab"]),
            new(';', Loc["Xfer_DelimSemicolon"]), new('|', Loc["Xfer_DelimPipe"])
        ];
        OnErrorChoices = [new(false, Loc["Imp_OnErrorSkip"]), new(true, Loc["Imp_OnErrorStop"])];
        _encodingChoice = EncodingChoices[0];
        _delimiterChoice = DelimiterChoices[0];
        _onError = OnErrorChoices[0];
        KindChoices =
        [
            new(KeepKind, Loc["Imp_KindKeep"]),
            .. BsonKinds.Editable.Select(static k => new XferOption(k, BsonKinds.MenuName(k)))
        ];
        BrowseCommand = new AsyncCommand(BrowseAsync);
        EditDateCommand = new RelayCommand(() => EditingDate = true);
        ApplyDateCommand = new AsyncCommand(async () =>
        {
            EditingDate = false;
            await DryRunAsync().ConfigureAwait(true);
        });
        StopCommand = new RelayCommand(() => _run?.Cancel(), () => IsRunning);
        OpenReportCommand = new RelayCommand(() =>
        {
            if (_errorReport is { } report)
            {
                Reveal(report);
            }
        });
        SaveProfileCommand = new AsyncCommand(SaveProfileAsync);
        Steps[0].Summary = Loc["Imp_NoFile"];
        _ready = InitializeAsync();
    }

    /// <summary>目标库。</summary>
    public string Database { get; }

    /// <summary>目标集合。</summary>
    public string Collection { get; }

    /// <inheritdoc />
    public override string IconKey => "Mongo.download";

    /// <summary>目标命名空间。</summary>
    public string TargetNamespace => Subtitle;

    // ── 第一步:文件 ─────────────────────────────────────────────────────

    /// <summary>文件路径。</summary>
    public string FilePath
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(FileName), nameof(HasFile));
                _ = OpenFileAsync();
            }
        }
    } = "";

    /// <summary>选了文件。</summary>
    public bool HasFile => _source is not null;

    /// <summary>文件名。</summary>
    public string FileName => FilePath.Length > 0 ? Path.GetFileName(FilePath) : Loc["Imp_NoFile"];

    /// <summary>文件头那一行(<c>12.4 MB · UTF-8 · 逗号分隔 · 1,246,302 行</c>)。</summary>
    public string FileInfo
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>文件图标(CSV 绿表格 / JSON 花括号 / BSON 归档)。</summary>
    public string FileIconKey => _source?.Format switch
    {
        ImportFormat.Json => "Mongo.braces",
        ImportFormat.Bson => "Mongo.archive",
        _ => "Mongo.sheet"
    };

    /// <summary>是 CSV(显示分隔符与表头选项)。</summary>
    public bool IsCsv => _source?.Format == ImportFormat.Csv;

    /// <summary>是文本(显示编码选项与原文预览)。</summary>
    public bool IsText => _source is { Format: not ImportFormat.Bson };

    /// <summary>正在数行。</summary>
    public bool Counting
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>编码选项。</summary>
    public IReadOnlyList<XferOption> EncodingChoices { get; }

    /// <summary>分隔符选项。</summary>
    public IReadOnlyList<XferOption> DelimiterChoices { get; }

    /// <summary>编码。</summary>
    public XferOption EncodingChoice
    {
        get => _encodingChoice;
        set
        {
            if (value is not null && SetProperty(ref _encodingChoice, value))
            {
                _ = ReopenAsync();
            }
        }
    }

    /// <summary>分隔符。</summary>
    public XferOption DelimiterChoice
    {
        get => _delimiterChoice;
        set
        {
            if (value is not null && SetProperty(ref _delimiterChoice, value))
            {
                _ = ReopenAsync();
            }
        }
    }

    /// <summary>首行是列名。</summary>
    public bool HasHeader
    {
        get => _hasHeader;
        set
        {
            if (SetProperty(ref _hasHeader, value))
            {
                _ = ReopenAsync();
            }
        }
    }

    /// <summary>文件开头的原文。</summary>
    public ObservableCollection<string> HeadLines { get; } = [];

    /// <summary>浏览…</summary>
    public AsyncCommand BrowseCommand { get; }

    /// <summary>已保存的导入配置。</summary>
    public ObservableCollection<XferOption> Profiles { get; } = [];

    /// <summary>有保存的配置。</summary>
    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>选中一份配置即套用(映射在选了文件之后套用)。</summary>
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

    // ── 第二步:映射 ─────────────────────────────────────────────────────

    /// <summary>映射表。</summary>
    public ObservableCollection<ImportMappingRow> Mappings { get; } = [];

    /// <summary>可选的目标类型。</summary>
    public IReadOnlyList<XferOption> KindChoices { get; }

    /// <summary>目标集合的验证规则说明(有 validator 时显示)。</summary>
    public string RulesText => _baseRules.Required.Count == 0 && _baseRules.Enums.Count == 0
        ? ""
        : Loc.Format("Imp_RulesText", string.Join(", ", _baseRules.Required), string.Join(", ", _baseRules.Enums.Keys));

    /// <summary>有验证规则。</summary>
    public bool HasRules => RulesText.Length > 0;

    // ── 第三步:预览与校验 ───────────────────────────────────────────────

    /// <summary>预览列。</summary>
    public ObservableCollection<ImportPreviewColumn> PreviewColumns { get; } = [];

    /// <summary>预览行(按芯片筛过)。</summary>
    public ObservableCollection<ImportPreviewRow> PreviewRows { get; } = [];

    /// <summary>预览表的总宽(列宽之和)。</summary>
    public double PreviewWidth => 34 + 46 + PreviewColumns.Sum(static c => c.Width);

    /// <summary>dry-run 进行中。</summary>
    public bool DryRunning
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>芯片:全部。</summary>
    public bool FilterAll
    {
        get => _filter == ImportFilter.All;
        set => SetFilter(value, ImportFilter.All);
    }

    /// <summary>芯片:可导入。</summary>
    public bool FilterOk
    {
        get => _filter == ImportFilter.Ok;
        set => SetFilter(value, ImportFilter.Ok);
    }

    /// <summary>芯片:警告。</summary>
    public bool FilterWarning
    {
        get => _filter == ImportFilter.Warning;
        set => SetFilter(value, ImportFilter.Warning);
    }

    /// <summary>芯片:错误。</summary>
    public bool FilterError
    {
        get => _filter == ImportFilter.Error;
        set => SetFilter(value, ImportFilter.Error);
    }

    /// <summary>全部行数(<c>1,000</c>)。</summary>
    public string AllCountText => BsonText.Grouped(_dryRows.Count);

    /// <summary>可导入行数。</summary>
    public string OkCountText => BsonText.Grouped(_okCount);

    /// <summary>警告行数。</summary>
    public string WarnCountText => BsonText.Grouped(_warnCount);

    /// <summary>错误行数。</summary>
    public string ErrCountText => BsonText.Grouped(_errCount);

    /// <summary>问题分组。</summary>
    public ObservableCollection<ImportIssueRow> Issues { get; } = [];

    /// <summary>没有问题。</summary>
    public bool NoIssues => Issues.Count == 0 && !DryRunning;

    /// <summary>显示「指定日期格式」。</summary>
    public bool ShowDateFix => _dateGuess.Length > 0 || DateFormat.Length > 0;

    /// <summary>「📅 指定日期格式 MM/dd/yy HH:mm…」。</summary>
    public string DateFixText => Loc.Format("Imp_DateFix", DateFormat.Length > 0 ? DateFormat : _dateGuess);

    /// <summary>正在编辑日期格式。</summary>
    public bool EditingDate
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && value && DateFormat.Length == 0)
            {
                DateFormat = _dateGuess;
            }
        }
    }

    /// <summary>日期格式(.NET 写法,<c>MM/dd/yy HH:mm</c>)。</summary>
    public string DateFormat
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(DateFixText), nameof(ShowDateFix));
            }
        }
    } = "";

    /// <summary>开始编辑日期格式。</summary>
    public RelayCommand EditDateCommand { get; }

    /// <summary>应用日期格式并重算。</summary>
    public AsyncCommand ApplyDateCommand { get; }

    /// <summary>写入方式:插入。</summary>
    public bool ModeInsert
    {
        get => _mode == ImportWriteMode.Insert;
        set => SetMode(value, ImportWriteMode.Insert);
    }

    /// <summary>写入方式:按键 upsert。</summary>
    public bool ModeUpsert
    {
        get => _mode == ImportWriteMode.Upsert;
        set => SetMode(value, ImportWriteMode.Upsert);
    }

    /// <summary>写入方式:替换。</summary>
    public bool ModeReplace
    {
        get => _mode == ImportWriteMode.Replace;
        set => SetMode(value, ImportWriteMode.Replace);
    }

    /// <summary>需要匹配键(upsert / 替换)。</summary>
    public bool NeedsKey => _mode != ImportWriteMode.Insert;

    /// <summary>匹配键候选(导入的目标字段)。</summary>
    public ObservableCollection<XferOption> MatchKeys { get; } = [];

    /// <summary>匹配键。</summary>
    public XferOption? MatchKey
    {
        get => _matchKey;
        set
        {
            if (SetProperty(ref _matchKey, value))
            {
                RaisePropertiesChanged(nameof(MatchKeyUnique), nameof(MatchKeyNotUnique));
                if (NeedsKey && value is not null)
                {
                    _ = DryRunAsync();
                }
            }
        }
    }

    /// <summary>匹配键上有唯一索引(绿色「唯一索引 ✓」)。</summary>
    public bool MatchKeyUnique => _matchKey?.Value is string key && _uniqueKeys.Contains(key);

    /// <summary>匹配键上没有唯一索引(橙色提示:upsert 可能命中多份)。</summary>
    public bool MatchKeyNotUnique => _matchKey is not null && !MatchKeyUnique;

    /// <summary>出错时的处理。</summary>
    public IReadOnlyList<XferOption> OnErrorChoices { get; }

    /// <summary>出错时。</summary>
    public XferOption OnError
    {
        get => _onError;
        set
        {
            if (value is not null)
            {
                _ = SetProperty(ref _onError, value);
            }
        }
    }

    /// <summary>批量 ordered: false。</summary>
    public bool Unordered
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>「批大小 1,000 · ordered: false」。</summary>
    public string BatchText => Loc.Format("Imp_Batch", BsonText.Grouped(1000));

    /// <summary>导入前备份将被覆盖的文档。</summary>
    public bool Backup
    {
        get;
        set => SetProperty(ref field, value);
    } = true;

    /// <summary>预计新增。</summary>
    public string EstInsert
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    /// <summary>预计更新(upsert 命中)。</summary>
    public string EstUpdate
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    /// <summary>预计跳过(错误)。</summary>
    public string EstSkip
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    // ── 第四步:导入 ─────────────────────────────────────────────────────

    /// <summary>进度 0–100。</summary>
    public double Progress
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>进度说明。</summary>
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

    /// <summary>速率。</summary>
    public string RateText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>已新增。</summary>
    public string InsertedText => BsonText.Grouped(_inserted);

    /// <summary>已更新。</summary>
    public string UpdatedText => BsonText.Grouped(_updated);

    /// <summary>已跳过。</summary>
    public string SkippedText => BsonText.Grouped(_skipped);

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

    /// <summary>结果成功。</summary>
    public bool ResultOk => ResultText.Length > 0 && _resultTone == "ok";

    /// <summary>结果失败或取消。</summary>
    public bool ResultFailed => ResultText.Length > 0 && _resultTone != "ok";

    /// <summary>错误报告路径。</summary>
    public string ErrorReport => _errorReport ?? "";

    /// <summary>有错误报告。</summary>
    public bool HasErrorReport => _errorReport is not null;

    /// <summary>执行日志。</summary>
    public ObservableCollection<XferLogLine> RunLog { get; } = [];

    /// <summary>停止导入。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>打开错误报告(在资源管理器中显示)。</summary>
    public RelayCommand OpenReportCommand { get; }

    /// <summary>保存为导入配置。</summary>
    public AsyncCommand SaveProfileCommand { get; }

    /// <inheritdoc />
    protected override string StartText => _rowCount is { } rows
        ? Loc.Format("Imp_Start", BsonText.Grouped(rows))
        : Loc["Imp_StartPlain"];

    // ── 加载 ────────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        try
        {
            IReadOnlyList<CollectionInfo> all = Workspace.CollectionsOf(Database);
            if (all.Count == 0 || all.All(c => c.Name != Collection))
            {
                all = await Workspace.Connection.ListCollectionsAsync(Database).ConfigureAwait(true);
            }
            _target = all.FirstOrDefault(c => c.Name == Collection) ?? new CollectionInfo(Database, Collection, CollectionKind.Collection, []);
            _baseRules = ImportRules.FromValidator(_target.Validator, _target.ValidationAction);
            IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(Database, Collection).ConfigureAwait(true);
            _uniqueKeys = [with(StringComparer.Ordinal), "_id"];
            foreach (BsonDocument index in indexes)
            {
                if (index.GetValue("unique", false).ToBoolean() && index.GetValue("key", new BsonDocument()) is BsonDocument key && key.ElementCount == 1)
                {
                    _ = _uniqueKeys.Add(key.GetElement(0).Name);
                }
            }
            IReadOnlyList<BsonDocument> sample = await SchemaSampler.SampleAsync(
                Workspace.Connection.Collection(Database, Collection), null, SampleRows, CancellationToken.None).ConfigureAwait(true);
            _targetKinds = SchemaSampler.Analyze(sample, flatten: true)
                .Where(static f => f.Dominant is not (BsonKind.Null or BsonKind.Missing))
                .ToDictionary(static f => f.Path, static f => f.Dominant, StringComparer.Ordinal);
            RaisePropertiesChanged(nameof(RulesText), nameof(HasRules));
            foreach (SavedItem item in await Workspace.Store.LoadSavedAsync("import", Workspace.ConnectionKey).ConfigureAwait(true))
            {
                Profiles.Add(new XferOption(item, item.Name));
            }
            RaisePropertyChanged(nameof(HasProfiles));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    private async Task BrowseAsync()
    {
        IReadOnlyList<string> files = await Workspace.PickOpenFilesAsync(Loc["Imp_PickFile"],
            [new FileKind(Loc["Imp_KindAll"], "*.csv", "*.tsv", "*.txt", "*.json", "*.jsonl", "*.ndjson", "*.bson", "*.gz"), FileKind.Csv, FileKind.Json, FileKind.Bson, FileKind.Any])
            .ConfigureAwait(true);
        if (files.Count > 0)
        {
            FilePath = files[0];
        }
    }

    /// <summary>选了文件(或换了编码 / 分隔符):认格式、读开头、开始数行、按抽样生成映射。</summary>
    private async Task OpenFileAsync()
    {
        _countCts?.Cancel();
        if (!File.Exists(FilePath))
        {
            _source = null;
            RaisePropertiesChanged(nameof(HasFile), nameof(IsCsv), nameof(IsText), nameof(FileIconKey));
            FileInfo = FilePath.Length > 0 ? Loc["Imp_FileMissing"] : "";
            return;
        }
        try
        {
            var sniffed = ImportSource.Sniff(FilePath);
            if (_encodingChoice.Value is TextEncodingKind encoding)
            {
                sniffed = sniffed with { Encoding = encoding };
            }
            if (_delimiterChoice.Value is char delimiter)
            {
                sniffed = sniffed with { Delimiter = delimiter };
            }
            _source = sniffed with { HasHeader = _hasHeader };
            _rowCount = null;
            RaisePropertiesChanged(nameof(HasFile), nameof(IsCsv), nameof(IsText), nameof(FileIconKey));
            RaiseStartTextChanged();
            HeadLines.Clear();
            if (_source.Format != ImportFormat.Bson)
            {
                foreach (string line in ImportSource.HeadLines(FilePath, _source.Encoding, 8))
                {
                    HeadLines.Add(line);
                }
            }
            UpdateFileInfo();
            Steps[0].Summary = Path.GetFileName(FilePath);
            // 默认目标类型要用到目标集合的 validator 与抽样:等它们就绪再生成映射。
            ImportSource current = _source;
            await _ready.ConfigureAwait(true);
            if (!ReferenceEquals(current, _source))
            {
                return;
            }
            BuildMappings();
            _ = CountAsync(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _source = null;
            FileInfo = Loc.Format("Common_Failed", ex.Message);
            RaisePropertiesChanged(nameof(HasFile), nameof(IsCsv), nameof(IsText));
        }
    }

    private Task ReopenAsync() => FilePath.Length > 0 ? OpenFileAsync() : Task.CompletedTask;

    private async Task CountAsync(ImportSource source)
    {
        var cts = new CancellationTokenSource();
        _countCts = cts;
        Counting = true;
        UpdateFileInfo();
        try
        {
            long count = await Task.Run(() => source.CountRecords(cts.Token), cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested && ReferenceEquals(source, _source))
            {
                _rowCount = count;
                RaiseStartTextChanged();
                UpdateEstimates(null);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Workspace.Log.Info($"Counting rows failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_countCts, cts))
            {
                Counting = false;
                UpdateFileInfo();
            }
        }
    }

    private void UpdateFileInfo()
    {
        if (_source is not { } source)
        {
            FileInfo = "";
            return;
        }
        var parts = new List<string> { BsonText.Bytes(source.Size) };
        switch (source.Format)
        {
            case ImportFormat.Csv:
                parts.Add(TextEncodings.Name(source.Encoding));
                parts.Add(Loc.Format("Imp_Separated", CsvReader.DelimiterName(source.Delimiter, Loc).Split(' ').Last()));
                break;
            case ImportFormat.Json:
                parts.Add(TextEncodings.Name(source.Encoding));
                parts.Add("JSON");
                break;
            default:
                parts.Add("BSON");
                break;
        }
        parts.Add(_rowCount is { } rows ? Loc.Format("Imp_Rows", BsonText.Grouped(rows)) : Loc["Imp_Counting"]);
        FileInfo = string.Join(" · ", parts);
    }

    // ── 映射 ────────────────────────────────────────────────────────────

    private void BuildMappings()
    {
        if (_source is not { } source)
        {
            return;
        }
        var previous = Mappings.GroupBy(static m => m.Source, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);
        Mappings.Clear();
        var samples = new List<ImportRecord>();
        IReadOnlyList<string> header;
        using (IImportReader reader = source.Open())
        {
            header = reader.Header;
            while (samples.Count < SampleRows && reader.TryRead(out ImportRecord record))
            {
                samples.Add(record);
            }
        }
        if (source.Format == ImportFormat.Csv)
        {
            for (int i = 0; i < header.Count; i++)
            {
                int column = i;
                string[] values = [.. samples.Select(r => r.Cells is { } c && column < c.Length ? c[column] : "").Where(static v => v.Length > 0)];
                string target = header[i].Trim();
                BsonKind kind = DefaultKind(target, values);
                AddMapping(header[i], i, values.FirstOrDefault() ?? "", target, new XferOption(kind, BsonKinds.MenuName(kind)), previous);
            }
        }
        else
        {
            var order = new List<string>();
            var firstValue = new Dictionary<string, BsonValue>(StringComparer.Ordinal);
            foreach (BsonDocument doc in samples.Select(static r => r.Document).OfType<BsonDocument>())
            {
                foreach (BsonElement element in doc)
                {
                    if (!firstValue.ContainsKey(element.Name))
                    {
                        firstValue[element.Name] = element.Value;
                        order.Add(element.Name);
                    }
                }
            }
            foreach (string name in order.Where(static n => n == "_id").Concat(order.Where(static n => n != "_id")))
            {
                // JSON 自带类型:默认保持原值;目标集合的 schema 明确是另一种类型时才换算。
                BsonKind? schemaKind = TargetKind(name);
                BsonKind sourceKind = BsonKinds.Of(firstValue[name]);
                XferOption kind = schemaKind is { } k && k != sourceKind && sourceKind is not (BsonKind.Null or BsonKind.Object or BsonKind.Array)
                    ? KindChoices.First(o => o.Value is BsonKind v && v == k)
                    : KindChoices[0];
                AddMapping(name, -1, BsonText.Inline(firstValue[name]), name, kind, previous);
            }
        }
        RefreshMatchKeys();
        UpdateMappingSummary();
    }

    private void AddMapping(string source, int index, string sample, string target, XferOption kind, Dictionary<string, ImportMappingRow> previous)
    {
        XferOption choice = KindChoices.FirstOrDefault(o => Equals(o.Value, kind.Value)) ?? KindChoices[0];
        var row = new ImportMappingRow(source, index, sample.Length > 60 ? sample[..60] + "…" : sample, target, KindChoices, choice, OnMappingChanged);
        if (previous.TryGetValue(source, out ImportMappingRow? old))
        {
            row.Include = old.Include;
            row.Target = old.Target;
            row.Kind = old.Kind;
        }
        row.IsRequired = _baseRules.Required.Contains(row.Target);
        row.IsUnique = _uniqueKeys.Contains(row.Target);
        Mappings.Add(row);
    }

    /// <summary>
    /// 默认目标类型:目标集合 validator 声明的类型 → 目标集合抽样的主类型 → 按源数据猜。
    /// </summary>
    private BsonKind DefaultKind(string target, IReadOnlyList<string> values) =>
        TargetKind(target) ?? GuessKind(values);

    private BsonKind? TargetKind(string target) =>
        _baseRules.Properties.TryGetValue(target, out BsonDocument? schema) && SchemaSampler.KindFromSchema(schema) is { } declared
            ? declared
            : _targetKinds.TryGetValue(target, out BsonKind sampled) ? sampled : null;

    /// <summary>
    /// 按源数据猜类型:九成以上的样例能解析成某种类型就取它 —— 剩下那一成正是 dry-run 要报出来的脏数据
    /// (<c>"1,580.00"</c>、<c>09/01/26</c>),而不是让一两个坏值把整列拖回字符串。
    /// 带前导零的数字串(邮编、编号)留作字符串,不然 00123 会变成 123。
    /// </summary>
    internal static BsonKind GuessKind(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return BsonKind.String;
        }
        int needed = Math.Max(1, (int)Math.Ceiling(values.Count * 0.9));
        bool Mostly(Func<string, bool> test) => values.Count(test) >= needed;
        bool leadingZero = values.Any(static v => v.Length > 1 && v[0] == '0' && char.IsDigit(v[1]));
        if (!leadingZero && Mostly(static v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)))
        {
            return values.Where(static v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                .All(static v => int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                ? BsonKind.Int32
                : BsonKind.Int64;
        }
        if (!leadingZero && Mostly(static v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return BsonKind.Double;
        }
        if (Mostly(static v => v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            return BsonKind.Boolean;
        }
        if (Mostly(static v => ImportValues.TryDate(v, null, out _)))
        {
            return BsonKind.Date;
        }
        if (Mostly(static v => v.Length == 24 && ObjectId.TryParse(v, out _)))
        {
            return BsonKind.ObjectId;
        }
        return BsonKind.String;
    }

    private void OnMappingChanged()
    {
        foreach (ImportMappingRow row in Mappings)
        {
            row.IsRequired = _baseRules.Required.Contains(row.Target.Trim());
            row.IsUnique = _uniqueKeys.Contains(row.Target.Trim());
        }
        RefreshMatchKeys();
        UpdateMappingSummary();
    }

    private void UpdateMappingSummary()
    {
        int fields = Mappings.Count(static m => m.Include && m.Target.Trim().Length > 0);
        Steps[1].Summary = Mappings.Count == 0 ? "" : Loc.Format("Imp_MappingSummary", Mappings.Count, fields);
    }

    private void RefreshMatchKeys()
    {
        string? current = _matchKey?.Value as string;
        string[] targets = [.. Mappings.Where(static m => m.Include && m.Target.Trim().Length > 0).Select(static m => m.Target.Trim()).Distinct()];
        MatchKeys.Clear();
        foreach (string target in targets.OrderBy(t => _uniqueKeys.Contains(t) ? 0 : 1))
        {
            MatchKeys.Add(new XferOption(target, target));
        }
        // 默认挑一个有唯一索引的(_id 以外优先):upsert 按它匹配才不会一次命中多份。
        _matchKey = MatchKeys.FirstOrDefault(k => (string)k.Value == current)
                    ?? MatchKeys.FirstOrDefault(k => (string)k.Value != "_id" && _uniqueKeys.Contains((string)k.Value))
                    ?? MatchKeys.FirstOrDefault(k => _uniqueKeys.Contains((string)k.Value))
                    ?? MatchKeys.FirstOrDefault();
        RaisePropertiesChanged(nameof(MatchKey), nameof(MatchKeyUnique), nameof(MatchKeyNotUnique));
    }

    // ── 预览与校验 ─────────────────────────────────────────────────────

    private ImportRules Rules => _baseRules with
    {
        DateFormat = DateFormat.Trim().Length > 0 ? DateFormat.Trim() : null,
        Mode = _mode,
        MatchKey = _mode == ImportWriteMode.Insert ? null : _matchKey?.Value as string
    };

    private IReadOnlyList<ImportColumn> Columns => [.. Mappings.Select(static m => m.ToColumn())];

    /// <summary>对前 1,000 行做 dry-run。</summary>
    internal async Task DryRunAsync()
    {
        if (_source is not { } source)
        {
            return;
        }
        DryRunning = true;
        RaisePropertyChanged(nameof(NoIssues));
        try
        {
            IReadOnlyList<ImportColumn> columns = Columns;
            ImportRules rules = Rules;
            var converter = new ImportConverter(columns, rules, Loc);
            List<ConvertedRow> rows = await Task.Run(() =>
            {
                var list = new List<ConvertedRow>(DryRunRows);
                using IImportReader reader = source.Open();
                while (list.Count < DryRunRows && reader.TryRead(out ImportRecord record))
                {
                    list.Add(converter.Convert(record));
                }
                return list;
            }).ConfigureAwait(true);
            _dryRows = rows;
            _okCount = rows.Count(static r => r.Status == ImportRowStatus.Ok);
            _warnCount = rows.Count(static r => r.Status == ImportRowStatus.Warning);
            _errCount = rows.Count(static r => r.Status == ImportRowStatus.Error);
            RaisePropertiesChanged(nameof(AllCountText), nameof(OkCountText), nameof(WarnCountText), nameof(ErrCountText));

            PreviewColumns.Clear();
            for (int i = 0; i < converter.Included.Count; i++)
            {
                ImportColumn column = converter.Included[i];
                int maxText = rows.Take(200).Select(r => (r.Raw[i] ?? "").Length).DefaultIfEmpty(0).Max();
                maxText = Math.Max(maxText, column.Target.Length);
                BsonKind? kind = column.Kind ?? (rows.Select(r => r.Values[i]).FirstOrDefault(static v => v is not null) switch
                {
                    { } v => BsonKinds.Of(v),
                    _ => null
                });
                PreviewColumns.Add(new ImportPreviewColumn(column.Target, kind is { } k ? BsonKinds.Name(k) : "—",
                    kind is { } t ? (t == BsonKind.String ? "VelaShellCyan" : BsonKinds.ColorToken(t)) : "VelaTextMuted",
                    ImportPreviewFormat.Width(kind, maxText)));
            }
            RaisePropertyChanged(nameof(PreviewWidth));
            ApplyFilter();

            Issues.Clear();
            foreach (ImportIssueGroup group in ImportDryRun.Group(rows))
            {
                Issues.Add(new ImportIssueRow(ImportIssueText.Title(group.Kind, group.Field, group.Detail, Loc), IssueDetail(group), group.IsError));
            }
            ConvertedRow? badDate = rows.FirstOrDefault(static r => r.Issues.Any(static i => i.Kind == ImportIssueKind.BadDate));
            ImportIssue? dateIssue = badDate?.Issues.First(static i => i.Kind == ImportIssueKind.BadDate);
            _dateGuess = dateIssue is { Column: >= 0 } && badDate!.Raw[dateIssue.Column] is { } rawDate
                ? ImportValues.GuessDateFormat(rawDate, includeTime: true) ?? "yyyy-MM-dd HH:mm:ss"
                : "";
            RaisePropertiesChanged(nameof(ShowDateFix), nameof(DateFixText));
            Steps[2].Summary = Loc.Format("Imp_DryRunSummary", BsonText.Grouped(rows.Count));
            await UpdateEstimatesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            DryRunning = false;
            RaisePropertyChanged(nameof(NoIssues));
        }
    }

    private string IssueDetail(ImportIssueGroup group)
    {
        string rows = Loc.Format("Imp_IssueRows", BsonText.Grouped(group.Rows));
        string tail = group.Kind switch
        {
            ImportIssueKind.BadDate => Loc.Format("Imp_DetailFormat", group.Detail),
            ImportIssueKind.NumberFromText => group.Detail.StartsWith("grouping", StringComparison.Ordinal) ? Loc["Imp_DetailGrouping"]
                : group.Detail.StartsWith("symbols", StringComparison.Ordinal) ? Loc["Imp_DetailSymbols"]
                : Loc["Imp_DetailFromString"],
            ImportIssueKind.EnumCase => group.Detail,
            _ => Loc.Format("Imp_DetailFirst", BsonText.Grouped(group.FirstLine))
        };
        return $"{rows} · {tail}";
    }

    private void SetFilter(bool on, ImportFilter filter)
    {
        if (!on || _filter == filter)
        {
            return;
        }
        _filter = filter;
        RaisePropertiesChanged(nameof(FilterAll), nameof(FilterOk), nameof(FilterWarning), nameof(FilterError));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        PreviewRows.Clear();
        IEnumerable<ConvertedRow> rows = _filter switch
        {
            ImportFilter.Ok => _dryRows.Where(static r => r.Status == ImportRowStatus.Ok),
            ImportFilter.Warning => _dryRows.Where(static r => r.Status == ImportRowStatus.Warning),
            ImportFilter.Error => _dryRows.Where(static r => r.Status == ImportRowStatus.Error),
            _ => _dryRows
        };
        foreach (ConvertedRow row in rows)
        {
            var cells = new ImportPreviewCell[PreviewColumns.Count];
            for (int i = 0; i < cells.Length; i++)
            {
                ImportIssue? issue = row.Issues.Where(c => c.Column == i).OrderByDescending(static c => c.IsError).FirstOrDefault();
                double width = PreviewColumns[i].Width;
                if (issue is { IsError: true })
                {
                    string raw = row.Raw[i] ?? "";
                    cells[i] = new(raw.Length == 0 ? Loc["Imp_Empty"] : raw, "VelaError", width);
                }
                else if (issue is not null)
                {
                    string raw = row.Raw[i] ?? "";
                    cells[i] = new(issue.Kind == ImportIssueKind.NumberFromText ? $"\"{raw.Trim('"')}\"" : raw, "VelaWarning", width);
                }
                else
                {
                    BsonValue? value = row.Values[i];
                    cells[i] = new(ImportPreviewFormat.Text(value), ImportPreviewFormat.Token(value), width);
                }
            }
            PreviewRows.Add(new ImportPreviewRow(row.Line, row.Status, cells));
        }
    }

    private void SetMode(bool on, ImportWriteMode mode)
    {
        if (!on || _mode == mode)
        {
            return;
        }
        _mode = mode;
        RaisePropertiesChanged(nameof(ModeInsert), nameof(ModeUpsert), nameof(ModeReplace), nameof(NeedsKey));
        _ = DryRunAsync();
    }

    /// <summary>预计结果:按 dry-run 的比例外推;upsert 命中(或插入撞唯一索引)用匹配键 <c>$in</c> 实查。</summary>
    private async Task UpdateEstimatesAsync()
    {
        long hits = 0;
        string? key = _mode != ImportWriteMode.Insert
            ? _matchKey?.Value as string
            : Mappings.Where(m => m.Include && m.Target.Trim() != "_id" && _uniqueKeys.Contains(m.Target.Trim())).Select(static m => m.Target.Trim()).FirstOrDefault()
              ?? (Mappings.Any(static m => m.Include && m.Target.Trim() == "_id") ? "_id" : null);
        if (key is not null)
        {
            var values = new BsonArray(_dryRows.Where(static r => r.Status != ImportRowStatus.Error)
                .Select(r => BsonPath.Get(r.Document, key)).OfType<BsonValue>().Distinct());
            if (values.Count > 0)
            {
                hits = await Workspace.Connection.Collection(Database, Collection)
                    .CountDocumentsAsync(new BsonDocument(key, new BsonDocument("$in", values)),
                        new CountOptions { MaxTime = TimeSpan.FromSeconds(10) }).ConfigureAwait(true);
            }
        }
        UpdateEstimates(hits);
    }

    private long _lastHits;

    private void UpdateEstimates(long? hits)
    {
        if (hits is { } h)
        {
            _lastHits = h;
        }
        int n = _dryRows.Count;
        if (n == 0)
        {
            EstInsert = EstUpdate = EstSkip = "—";
            return;
        }
        double total = _rowCount ?? n;
        double scale = total / n;
        long importable = _okCount + _warnCount;
        long update = _mode == ImportWriteMode.Insert ? 0 : _lastHits;
        long duplicate = _mode == ImportWriteMode.Insert ? _lastHits : 0;
        string approx = _rowCount is null || _rowCount > n ? "≈ " : "";
        EstInsert = approx + BsonText.Grouped((long)Math.Round(Math.Max(0, importable - update - duplicate) * scale));
        EstUpdate = approx + BsonText.Grouped((long)Math.Round(update * scale));
        EstSkip = approx + BsonText.Grouped((long)Math.Round((_errCount + duplicate) * scale));
    }

    // ── 校验与执行 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override async Task<bool> ValidateAsync(int step)
    {
        switch (step)
        {
            case 0:
                if (_source is null)
                {
                    await BrowseAsync().ConfigureAwait(true);
                }
                return _source is not null;
            case 1:
                if (!Mappings.Any(static m => m.Include && m.Target.Trim().Length > 0))
                {
                    Workspace.Toast(new() { Title = Loc["Imp_NoMapping"], Kind = ToastKind.Warning });
                    return false;
                }
                if (Mappings.Where(static m => m.Include).GroupBy(static m => m.Target.Trim()).Any(static g => g.Count() > 1))
                {
                    Workspace.Toast(new() { Title = Loc["Imp_DuplicateTarget"], Kind = ToastKind.Warning });
                    return false;
                }
                return true;
            case 2:
                if (NeedsKey && _matchKey is null)
                {
                    Workspace.Toast(new() { Title = Loc["Imp_NoKey"], Kind = ToastKind.Warning });
                    return false;
                }
                return true;
            default:
                return true;
        }
    }

    /// <inheritdoc />
    protected override async Task OnEnteredAsync(int step)
    {
        if (step == 2)
        {
            await DryRunAsync().ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    protected override async Task StartAsync()
    {
        if (_source is not { } source || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        if (Workspace.Guard.IsProduction || Workspace.Guard.ConfirmWrites)
        {
            bool confirmed = await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Imp_ConfirmTitle"],
                Message = Loc.Format("Imp_ConfirmBody", TargetNamespace, ModeName(_mode)),
                ConfirmLabel = Loc["Imp_StartPlain"],
                IconKey = "Mongo.download",
                TypeToConfirm = Collection,
                Facts =
                [
                    new(Loc["Imp_FactFile"], Path.GetFileName(source.Path)),
                    new(Loc["Imp_FactRows"], _rowCount is { } rows ? BsonText.Grouped(rows) : "—"),
                    new(Loc["Imp_FactMode"], ModeName(_mode) + (NeedsKey && _matchKey is { } key ? $" · {key.Label}" : ""))
                ]
            }).ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
        }
        ImportJob job = new()
        {
            Database = Database,
            Collection = Collection,
            Source = source,
            Columns = Columns,
            Rules = Rules,
            StopOnError = (bool)_onError.Value,
            Ordered = !Unordered,
            Backup = Backup && _mode != ImportWriteMode.Insert,
            EstimatedTotal = _rowCount ?? 0,
            ReportDirectory = ReportDirectory(source.Path)
        };
        EnterRunStep();
        RunLog.Clear();
        _errorReport = null;
        _inserted = _updated = _skipped = 0;
        RaisePropertiesChanged(nameof(InsertedText), nameof(UpdatedText), nameof(SkippedText), nameof(ErrorReport), nameof(HasErrorReport));
        ResultText = "";
        Progress = 0;
        StopCommand.RaiseCanExecuteChanged();
        Steps[RunStep].Summary = Loc["Xfer_Running"];
        AddLog(Loc.Format("Imp_LogStart", Path.GetFileName(source.Path), TargetNamespace, ModeName(_mode)), XferTone.Normal);
        _run = new CancellationTokenSource();
        CancellationToken token = _run.Token;
        var progress = new Progress<ImportProgress>(OnProgress);
        try
        {
            ImportResult result = await Task.Run(() => ImportRunner.RunAsync(Workspace.Connection, job, Loc, progress, token), token).ConfigureAwait(true);
            _errorReport = result.ErrorReport;
            RaisePropertiesChanged(nameof(ErrorReport), nameof(HasErrorReport));
            _resultTone = result.Stopped ? "warn" : "ok";
            Progress = 100;
            PercentText = "100%";
            ResultText = Loc.Format(result.Stopped ? "Imp_DoneStopped" : "Imp_Done", BsonText.Grouped(result.Inserted), BsonText.Grouped(result.Updated),
                BsonText.Grouped(result.Skipped), TransferText.Duration(result.Elapsed));
            RaisePropertiesChanged(nameof(ResultOk), nameof(ResultFailed));
            Steps[RunStep].Summary = Loc["Xfer_Completed"];
            AddLog(ResultText, result.Skipped > 0 ? XferTone.Warn : XferTone.Ok);
            if (result.ErrorReport is { } report)
            {
                AddLog(Loc.Format("Imp_LogReport", report), XferTone.Warn);
            }
            if (result.BackupFile is { } backup)
            {
                AddLog(Loc.Format("Imp_LogBackup", backup), XferTone.Muted);
            }
            await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
            FinishRun(new ToastRequest
            {
                Title = Loc.Format("Imp_ToastDone", BsonText.Grouped(result.Inserted + result.Updated)),
                Detail = Loc.Format("Imp_ToastDetail", BsonText.Grouped(result.Inserted), BsonText.Grouped(result.Updated), BsonText.Grouped(result.Skipped)),
                Kind = result.Skipped > 0 ? ToastKind.Warning : ToastKind.Success,
                ActionLabel = result.ErrorReport is not null ? Loc["Imp_OpenReport"] : null,
                Action = result.ErrorReport is { } path ? () =>
                {
                    Reveal(path);
                    return Task.CompletedTask;
                }
                : null,
                Duration = TimeSpan.FromSeconds(8)
            });
        }
        catch (OperationCanceledException)
        {
            _resultTone = "warn";
            ResultText = Loc.Format("Imp_Cancelled", InsertedText, UpdatedText);
            RaisePropertiesChanged(nameof(ResultOk), nameof(ResultFailed));
            Steps[RunStep].Summary = Loc["Xfer_Cancelled"];
            AddLog(ResultText, XferTone.Warn);
            await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
            FinishRun(new ToastRequest { Title = ResultText, Kind = ToastKind.Info });
        }
        catch (Exception ex)
        {
            // 任何失败都要让执行页落到"失败"(而不是永远停在"进行中"),原因写进日志。
            Workspace.Log.Error("Import failed.", ex);
            string reason = MongoConnector.Describe(ex);
            _resultTone = "err";
            ResultText = Loc.Format("Common_Failed", reason);
            RaisePropertiesChanged(nameof(ResultOk), nameof(ResultFailed));
            Steps[RunStep].Summary = Loc["Xfer_Failed"];
            AddLog(ResultText, XferTone.Error);
            FinishRun(new ToastRequest { Title = ResultText, Kind = ToastKind.Error });
        }
        finally
        {
            _run.Dispose();
            _run = null;
            StopCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnProgress(ImportProgress p)
    {
        _inserted = p.Inserted;
        _updated = p.Updated;
        _skipped = p.Skipped;
        RaisePropertiesChanged(nameof(InsertedText), nameof(UpdatedText), nameof(SkippedText));
        double fraction = p.Total > 0 ? Math.Min(1, (double)p.Rows / p.Total) : 0;
        Progress = fraction * 100;
        PercentText = (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        double rate = p.Elapsed.TotalSeconds > 0.2 ? p.Rows / p.Elapsed.TotalSeconds : 0;
        string remaining = rate > 0 && p.Total > p.Rows ? TransferText.Duration(TimeSpan.FromSeconds((p.Total - p.Rows) / rate)) : "—";
        ProgressText = Loc.Format("Imp_ProgressLine", BsonText.Grouped(p.Rows), BsonText.Grouped(p.Total), TransferText.Duration(p.Elapsed), remaining);
        RateText = Loc.Format("Imp_Rate", TransferText.Rate(rate));
        Steps[RunStep].Summary = Loc.Format("Xfer_RunningPercent", PercentText);
    }

    private string ModeName(ImportWriteMode mode) => mode switch
    {
        ImportWriteMode.Upsert => Loc["Imp_ModeUpsert"],
        ImportWriteMode.Replace => Loc["Imp_ModeReplace"],
        _ => Loc["Imp_ModeInsert"]
    };

    /// <summary>错误报告放在源文件旁边;那里写不了(只读介质、网络盘权限)就放临时目录。</summary>
    private static string ReportDirectory(string source)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(source));
        if (folder is not null)
        {
            try
            {
                string probe = Path.Combine(folder, $".velashell-{Guid.NewGuid():N}.tmp");
                using (File.Create(probe, 1, FileOptions.DeleteOnClose))
                {
                }
                return folder;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return Path.Combine(Path.GetTempPath(), "VelaShell", "mongo-import");
    }

    private void AddLog(string text, XferTone tone) => OnUi(() => RunLog.Add(new XferLogLine(Now(), text, tone)));

    // ── 配置 ────────────────────────────────────────────────────────────

    private async Task SaveProfileAsync()
    {
        var profile = new BsonDocument
        {
            { "encoding", _encodingChoice.Value.ToString() },
            { "delimiter", _delimiterChoice.Value.ToString() },
            { "header", _hasHeader },
            { "mode", _mode.ToString() },
            { "matchKey", _matchKey?.Value as string ?? "" },
            { "stopOnError", (bool)_onError.Value },
            { "unordered", Unordered },
            { "backup", Backup },
            { "dateFormat", DateFormat },
            { "columns", new BsonArray(Mappings.Select(static m => new BsonDocument
                {
                    { "source", m.Source },
                    { "include", m.Include },
                    { "target", m.Target },
                    { "kind", m.Kind.Value.ToString() }
                }))
            }
        };
        string name = $"{TargetNamespace} · {(FilePath.Length > 0 ? Path.GetFileName(FilePath) : Loc["Imp_NoFile"])}";
        var item = new SavedItem(name, ExportProfile.Serialize(profile), DateTimeOffset.Now);
        await Workspace.Store.SaveItemAsync("import", Workspace.ConnectionKey, item).ConfigureAwait(true);
        XferOption? existing = Profiles.FirstOrDefault(p => p.Label == name);
        if (existing is not null)
        {
            _ = Profiles.Remove(existing);
        }
        Profiles.Insert(0, new XferOption(item, name));
        RaisePropertyChanged(nameof(HasProfiles));
        Workspace.Toast(new() { Title = Loc.Format("Exp_ProfileSaved", name), Kind = ToastKind.Success });
    }

    /// <summary>套用一份保存的导入配置。</summary>
    internal void ApplyProfile(BsonDocument profile)
    {
        string encoding = profile.GetValue("encoding", KeepKind).AsString;
        string delimiter = profile.GetValue("delimiter", KeepKind).AsString;
        _encodingChoice = EncodingChoices.FirstOrDefault(o => o.Value.ToString() == encoding) ?? EncodingChoices[0];
        _delimiterChoice = DelimiterChoices.FirstOrDefault(o => o.Value.ToString() == delimiter) ?? DelimiterChoices[0];
        _hasHeader = profile.GetValue("header", true).ToBoolean();
        RaisePropertiesChanged(nameof(HasHeader), nameof(EncodingChoice), nameof(DelimiterChoice));
        if (_source is not null)
        {
            // 按配置的编码 / 分隔符重读一遍文件头(元数据早已就绪,这一步同步完成),再往新的映射上套列设置。
            _ = ReopenAsync();
        }
        if (Enum.TryParse(profile.GetValue("mode", "Insert").AsString, out ImportWriteMode mode))
        {
            SetMode(true, mode);
        }
        OnError = OnErrorChoices.FirstOrDefault(o => (bool)o.Value == profile.GetValue("stopOnError", false).ToBoolean()) ?? _onError;
        Unordered = profile.GetValue("unordered", true).ToBoolean();
        Backup = profile.GetValue("backup", true).ToBoolean();
        DateFormat = profile.GetValue("dateFormat", "").AsString;
        if (profile.GetValue("columns", new BsonArray()) is BsonArray columns)
        {
            foreach (BsonDocument saved in columns.OfType<BsonDocument>())
            {
                ImportMappingRow? row = Mappings.FirstOrDefault(m => m.Source == saved.GetValue("source", "").AsString);
                if (row is null)
                {
                    continue;
                }
                row.Include = saved.GetValue("include", true).ToBoolean();
                row.Target = saved.GetValue("target", row.Target).AsString;
                string kind = saved.GetValue("kind", "").AsString;
                row.Kind = KindChoices.FirstOrDefault(k => k.Value.ToString() == kind) ?? row.Kind;
            }
        }
        string key = profile.GetValue("matchKey", "").AsString;
        if (MatchKeys.FirstOrDefault(k => (string)k.Value == key) is { } match)
        {
            MatchKey = match;
        }
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        if (!IsRunning)
        {
            _countCts?.Cancel();
        }
        base.OnClosed();
    }
}
