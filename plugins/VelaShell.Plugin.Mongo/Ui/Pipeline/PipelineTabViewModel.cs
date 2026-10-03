using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>管道构建器的两种编辑方式。</summary>
internal enum PipelineMode
{
    /// <summary>阶段卡片。</summary>
    Stages,

    /// <summary>整条管道一段文本。</summary>
    Text
}

/// <summary>
/// 聚合管道构建器(设计稿 04):阶段卡片 + 逐阶段输出预览 + 整条运行的输出表。
/// <para>
/// 三件事分开算:**预览**只跑抽样(<c>$limit N</c> 之后的前缀管道),便宜、自动、每次改动后防抖重算;
/// **运行**跑整条管道,按按钮才跑,结果进底部的输出表;**执行计划**交给查询编辑器的 explain 去画。
/// 把预览和运行混成一件事,要么预览慢得没法边打边看,要么"运行"的数字其实是抽样 —— 两头都不诚实。
/// </para>
/// </summary>
internal sealed class PipelineTabViewModel : WorkspaceTab
{
    /// <summary>输出表最多装多少份(再多请去结果标签里翻页)。</summary>
    public const int ResultLimit = 500;

    /// <summary>抽样数的可选值。</summary>
    public static IReadOnlyList<int> SampleChoices { get; } = [10, 20, 50, 100, 500, 1000];

    private bool _syncingText;
    private bool _bulk;
    private CancellationTokenSource? _previewDebounce;
    private CancellationTokenSource? _previewRun;
    private CancellationTokenSource? _textDebounce;
    private CancellationTokenSource? _runCts;
    private string? _runComment;
    private TimeSpan? _previewElapsed;
    private IReadOnlyList<BsonDocument> _inputSample = [];
    private string _savedSnapshot;
    private string? _savedName;
    private string _activity = "";

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="info">源集合(或视图)。</param>
    /// <param name="pipeline">带进来的管道(视图的定义、查询编辑器里的 aggregate);空则从一个 <c>$match</c> 起步。</param>
    public PipelineTabViewModel(IMongoWorkspace workspace, CollectionInfo info, BsonArray? pipeline)
        : base(workspace)
    {
        Info = info;
        Title = Loc.Format("Pipe_Title", info.Name);
        Scope = "@" + info.Database;
        Stages.CollectionChanged += OnStagesChanged;

        _bulk = true;
        if (pipeline is { Count: > 0 })
        {
            foreach (BsonValue item in pipeline)
            {
                if (item is BsonDocument { ElementCount: > 0 } stage)
                {
                    BsonElement element = stage.GetElement(0);
                    Stages.Add(new PipelineStage(this, element.Name, PipelineText.FormatBody(element.Value)));
                }
            }
        }
        if (Stages.Count == 0)
        {
            Stages.Add(new PipelineStage(this, "$match", PipelineText.DefaultBody("$match")));
        }
        Stages[0].IsExpanded = true;
        _bulk = false;
        _savedSnapshot = Snapshot();

        RunCommand = new AsyncCommand(RunAsync, () => !IsRunning);
        StopCommand = new RelayCommand(StopRun);
        ExplainCommand = new RelayCommand(Explain);
        SaveCommand = new AsyncCommand(SaveAsync);
        CreateViewCommand = new AsyncCommand(CreateViewAsync);
        ExportCodeCommand = new RelayCommand(ExportCode);
        OpenResultsCommand = new RelayCommand(OpenInResults);
        SaveAsCollectionCommand = new AsyncCommand(SaveAsCollectionAsync);
        ToggleAllowDiskUseCommand = new RelayCommand(() => AllowDiskUse = !AllowDiskUse);
        RefreshPreviewCommand = new AsyncCommand(RefreshPreviewAsync);
        UpdateStatus();
    }

    /// <summary>源集合。</summary>
    public CollectionInfo Info { get; }

    /// <summary>库名。</summary>
    public string Database => Info.Database;

    /// <summary>集合名。</summary>
    public string CollectionName => Info.Name;

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Pipeline;

    /// <inheritdoc />
    public override string Key => $"pipeline:{Info.Namespace}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.workflow";

    /// <inheritdoc />
    public override string IconToken => "VelaAccent";

    /// <summary>阶段卡片。</summary>
    public ObservableCollection<PipelineStage> Stages { get; } = [];

    // ── 工具行 ─────────────────────────────────────────────────────────────

    /// <summary>源集合芯片的文字(<c>shop.orders</c>)。</summary>
    public string SourceLabel => Info.Namespace;

    /// <summary>源集合的文档数缩写(<c>1.28M</c>)。</summary>
    public string SourceCount
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>源集合图标(视图是眼睛,时序集合是柱状)。</summary>
    public string SourceIcon => Info.Kind switch
    {
        CollectionKind.View => "Mongo.eye",
        CollectionKind.TimeSeries => "Mongo.chart-no-axes-column",
        _ => "Mongo.table-2"
    };

    /// <summary>当前编辑方式。</summary>
    public PipelineMode Mode { get; private set; } = PipelineMode.Stages;

    /// <summary>「阶段」分段。</summary>
    public bool IsStagesMode
    {
        get => Mode == PipelineMode.Stages;
        set
        {
            if (value)
            {
                SwitchMode(PipelineMode.Stages);
            }
        }
    }

    /// <summary>「文本」分段。</summary>
    public bool IsTextMode
    {
        get => Mode == PipelineMode.Text;
        set
        {
            if (value)
            {
                SwitchMode(PipelineMode.Text);
            }
        }
    }

    /// <summary>自动预览。</summary>
    public bool AutoPreview
    {
        get; set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }
            RaisePropertiesChanged(nameof(PreviewSummary), nameof(CanRefreshPreview));
            foreach (PipelineStage stage in Stages)
            {
                stage.RefreshNote();
            }
            if (value)
            {
                SchedulePreview(immediate: true);
            }
            else
            {
                _previewDebounce?.Cancel();
            }
        }
    } = true;

    /// <summary>预览的抽样输入文档数。</summary>
    public int SampleSize
    {
        get; set
        {
            value = Math.Clamp(value, 1, 100_000);
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(SampleText), nameof(SampleTip), nameof(PreviewSummary));
                if (AutoPreview)
                {
                    SchedulePreview(immediate: true);
                }
                UpdateStatus();
            }
        }
    } = 20;

    /// <summary><c>抽样 20</c>。</summary>
    public string SampleText => Loc.Format("Pipe_Sample", SampleSize);

    /// <summary>抽样数的悬停说明。</summary>
    public string SampleTip => Loc.Format("Pipe_SampleTip", SampleSize);

    /// <summary>allowDiskUse(底栏右侧可切)。</summary>
    public bool AllowDiskUse
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(AllowDiskUseText));
            }
        }
    } = true;

    /// <summary><c>allowDiskUse · 已开启</c>。</summary>
    public string AllowDiskUseText => Loc.Format("Pipe_AllowDiskUse", Loc[AllowDiskUse ? "Pipe_On" : "Pipe_Off"]);

    // ── 文本模式 ───────────────────────────────────────────────────────────

    /// <summary>整条管道的文本(文本模式的编辑器双向绑定)。</summary>
    public string PipelineTextValue
    {
        get; set
        {
            if (!SetProperty(ref field, value ?? "") || _syncingText)
            {
                return;
            }
            ScheduleTextSync();
        }
    } = "";

    /// <summary>文本的诊断。</summary>
    public IReadOnlyList<EditorDiagnostic> TextDiagnostics { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>文本解析失败的原因;同步成功为空。</summary>
    public string TextError
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasTextError));
            }
        }
    } = "";

    /// <summary>文本有错(停在文本模式)。</summary>
    public bool HasTextError => TextError.Length > 0;

    /// <summary>文本模式编辑器的补全(阶段名、运算符、源集合的字段)。</summary>
    public Func<CompletionRequest, Task<CompletionSet?>> TextCompletionProvider => request =>
        Task.FromResult(PipelineFields.Complete(request, null, PipelineFields.Sample(_inputSample), SampleSize, Loc));

    // ── 预览与底栏 ─────────────────────────────────────────────────────────

    /// <summary>正在算预览。</summary>
    public bool IsPreviewing
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(PreviewSummary));
            }
        }
    }

    /// <summary>底栏左侧:<c>6 个阶段 · 5 个启用</c>(有错时加「· 1 个有错」)。</summary>
    public string StageSummary
    {
        get
        {
            int enabled = Stages.Count(static s => s.IsEnabled);
            string text = Loc.Format("Pipe_StageSummary", Stages.Count, enabled);
            int errors = ErrorCount;
            return errors > 0 ? text + " · " + Loc.Format("Pipe_ErrorCount", errors) : text;
        }
    }

    /// <summary>有错的阶段数(启用的阶段里)。</summary>
    public int ErrorCount => Stages.Count(static s => s.IsEnabled && s.HasError);

    /// <summary>底栏的状态图标。</summary>
    public string SummaryIcon => ErrorCount > 0 || HasTextError ? "Mongo.circle-x" : "Mongo.circle-check";

    /// <summary>底栏状态图标颜色。</summary>
    public string SummaryToken => ErrorCount > 0 || HasTextError ? "VelaError" : "VelaStatusConnected";

    /// <summary>底栏中间:<c>预览基于前 20 份输入文档 · 64 ms</c>。</summary>
    public string PreviewSummary
    {
        get
        {
            if (!AutoPreview && _previewElapsed is null)
            {
                return Loc["Pipe_PreviewOffHint"];
            }
            if (IsPreviewing)
            {
                return Loc.Format("Pipe_Previewing", SampleSize);
            }
            if (field.Length > 0)
            {
                return Loc.Format("Pipe_PreviewFailed", field);
            }
            return _previewElapsed is { } elapsed
                ? Loc.Format("Pipe_PreviewBasis", SampleSize, PipelineResults.Elapsed(elapsed))
                : Loc.Format("Pipe_PreviewBasisPending", SampleSize);
        }

        private set;
    } = "";

    /// <summary>自动预览关着时,底栏那句可以点一下手动算一次。</summary>
    public bool CanRefreshPreview => !AutoPreview;

    // ── 输出表 ─────────────────────────────────────────────────────────────

    /// <summary>输出表的列。</summary>
    public IReadOnlyList<PipelineColumn> ResultColumns { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>输出表的行。</summary>
    public IReadOnlyList<PipelineRow> ResultRows { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>输出表的总宽(横向滚动用)。</summary>
    public double ResultWidth => PipelineResults.NumberWidth + ResultColumns.Sum(static c => c.Width);

    /// <summary>输出表的高度:最多十二行,再多在表里滚。</summary>
    public double ResultListHeight => Math.Max(1, Math.Min(ResultRows.Count, 12)) * PipelineResults.RowHeight;

    /// <summary>跑过一次(输出卡片才出现)。</summary>
    public bool HasRun { get; private set => SetProperty(ref field, value); }

    /// <summary>有输出行。</summary>
    public bool HasRows => ResultRows.Count > 0;

    /// <summary>跑完了、没出错、但一份文档也没有(给一句话,而不是一张空表)。</summary>
    public bool ShowEmptyResult => HasRun && !IsRunning && RunError.Length == 0 && ResultRows.Count == 0;

    /// <summary>正在运行。</summary>
    public bool IsRunning
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RunCommand.RaiseCanExecuteChanged();
                RaisePropertiesChanged(nameof(ResultSummary), nameof(ShowEmptyResult));
            }
        }
    }

    /// <summary>运行的报错。</summary>
    public string RunError
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(HasRunError), nameof(ShowEmptyResult));
            }
        }
    } = "";

    /// <summary>运行失败。</summary>
    public bool HasRunError => RunError.Length > 0;

    /// <summary>输出卡片标题旁:<c>10 份文档 · 完整运行 1.4 s</c>。</summary>
    public string ResultSummary { get => IsRunning ? Loc["Pipe_Running"] : field; private set; } = "";

    // ── 命令 ───────────────────────────────────────────────────────────────

    /// <summary>运行整条管道。</summary>
    public AsyncCommand RunCommand { get; }

    /// <summary>停止运行(取消 + 按 comment killOp)。</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>执行计划(交给查询编辑器 explain)。</summary>
    public RelayCommand ExplainCommand { get; }

    /// <summary>保存管道。</summary>
    public AsyncCommand SaveCommand { get; }

    /// <summary>创建视图。</summary>
    public AsyncCommand CreateViewCommand { get; }

    /// <summary>导出为代码。</summary>
    public RelayCommand ExportCodeCommand { get; }

    /// <summary>在结果标签中打开。</summary>
    public RelayCommand OpenResultsCommand { get; }

    /// <summary>另存为集合($out / $merge)。</summary>
    public AsyncCommand SaveAsCollectionCommand { get; }

    /// <summary>切 allowDiskUse。</summary>
    public RelayCommand ToggleAllowDiskUseCommand { get; }

    /// <summary>手动算一次预览。</summary>
    public AsyncCommand RefreshPreviewCommand { get; }

    // ── 生命周期 ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task LoadAsync()
    {
        await LoadSourceCountAsync().ConfigureAwait(true);
        if (AutoPreview)
        {
            await RefreshPreviewAsync().ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public override async Task RefreshAsync()
    {
        await LoadSourceCountAsync().ConfigureAwait(true);
        await RefreshPreviewAsync().ConfigureAwait(true);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        foreach (CancellationTokenSource? cts in new[] { _previewDebounce, _previewRun, _textDebounce, _runCts })
        {
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        base.Dispose();
    }

    private async Task LoadSourceCountAsync()
    {
        try
        {
            long? count = await Workspace.Connection.EstimatedCountAsync(Database, CollectionName).ConfigureAwait(true);
            SourceCount = count is { } n ? BsonText.Count(n) : "";
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            SourceCount = "";
        }
    }

    // ── 阶段增删改 ─────────────────────────────────────────────────────────

    /// <summary>加一个阶段(默认在末尾),展开它。</summary>
    internal PipelineStage AddStage(string op, int? index = null)
    {
        var stage = new PipelineStage(this, op, PipelineText.DefaultBody(op)) { IsExpanded = true };
        int at = Math.Clamp(index ?? Stages.Count, 0, Stages.Count);
        Stages.Insert(at, stage);
        return stage;
    }

    /// <summary>
    /// 换运算符。阶段体还是旧运算符的默认模板(或者是空的)时一并换成新模板 ——
    /// 用户写过的阶段体不动(从 <c>$project</c> 换成 <c>$addFields</c> 时原样保留恰恰是对的)。
    /// </summary>
    internal void ChangeOperator(PipelineStage stage, string op)
    {
        if (stage.Operator == op)
        {
            return;
        }
        bool untouched = string.IsNullOrWhiteSpace(stage.Body)
                         || Squash(stage.Body) == Squash(PipelineText.DefaultBody(stage.Operator));
        _bulk = true;
        stage.Operator = op;
        if (untouched)
        {
            stage.Body = PipelineText.DefaultBody(op);
        }
        _bulk = false;
        OnPipelineChanged();

        static string Squash(string text) => string.Concat(text.Where(static c => !char.IsWhiteSpace(c)));
    }

    /// <summary>删除一个阶段(提示里带「撤销」)。</summary>
    internal void RemoveStage(PipelineStage stage)
    {
        int index = Stages.IndexOf(stage);
        if (index < 0)
        {
            return;
        }
        Stages.RemoveAt(index);
        Workspace.Toast(new()
        {
            Title = Loc.Format("Pipe_Removed", index + 1, stage.Operator),
            ActionLabel = Loc["Pipe_Undo"],
            Action = () =>
            {
                Stages.Insert(Math.Min(index, Stages.Count), stage);
                return Task.CompletedTask;
            },
            Duration = TimeSpan.FromSeconds(8)
        });
    }

    /// <summary>复制一张卡片,插在它后面。</summary>
    internal void DuplicateStage(PipelineStage stage)
    {
        int index = Stages.IndexOf(stage);
        var copy = new PipelineStage(this, stage.Operator, stage.Body, stage.IsEnabled) { IsExpanded = stage.IsExpanded };
        Stages.Insert(index + 1, copy);
    }

    /// <summary>上下移一格。</summary>
    internal void MoveStage(PipelineStage stage, int delta)
    {
        int from = Stages.IndexOf(stage);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= Stages.Count)
        {
            return;
        }
        Stages.Move(from, to);
    }

    /// <summary>拖到某个位置(拖拽重排)。</summary>
    internal void MoveStageTo(PipelineStage stage, int to)
    {
        int from = Stages.IndexOf(stage);
        to = Math.Clamp(to, 0, Stages.Count - 1);
        if (from >= 0 && from != to)
        {
            Stages.Move(from, to);
        }
    }

    /// <summary>放大看某个阶段的全部预览。</summary>
    internal void ShowStagePreview(PipelineStage stage)
    {
        if (!stage.CanShowPreview)
        {
            return;
        }
        Workspace.ShowDialog(new PipelinePreviewDialogViewModel(
            Workspace,
            Loc.Format("Pipe_PreviewTitle", stage.Index, stage.Operator),
            Loc.Format("Pipe_PreviewSubtitle", stage.PreviewDocuments.Count, stage.CountText, SampleSize),
            stage.PreviewDocuments));
    }

    /// <summary>
    /// 用一段管道文本整体替换阶段(截图与单测用;<paramref name="markClean" /> 时视为刚打开、未修改)。
    /// </summary>
    internal bool LoadFromText(string text, bool markClean = false)
    {
        PipelineParse parse = PipelineText.Parse(text);
        if (!parse.Ok)
        {
            return false;
        }
        SyncStages(parse.Stages);
        if (markClean)
        {
            _savedSnapshot = Snapshot();
            UpdateModified();
        }
        return true;
    }

    private void OnStagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (PipelineStage stage in e.OldItems)
            {
                stage.Changed -= OnStageChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (PipelineStage stage in e.NewItems)
            {
                stage.Changed -= OnStageChanged;
                stage.Changed += OnStageChanged;
            }
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (PipelineStage stage in Stages)
            {
                stage.Changed -= OnStageChanged;
                stage.Changed += OnStageChanged;
            }
        }
        for (int i = 0; i < Stages.Count; i++)
        {
            Stages[i].Index = i + 1;
        }
        if (!_bulk)
        {
            OnPipelineChanged();
        }
    }

    private void OnStageChanged(PipelineStage stage)
    {
        if (!_bulk)
        {
            OnPipelineChanged();
        }
    }

    /// <summary>管道变了:更新底栏、未保存标记,排一次预览。</summary>
    private void OnPipelineChanged()
    {
        RaisePropertiesChanged(nameof(StageSummary), nameof(ErrorCount), nameof(SummaryIcon), nameof(SummaryToken));
        UpdateModified();
        if (AutoPreview)
        {
            SchedulePreview();
        }
    }

    private void UpdateModified()
    {
        IsModified = Snapshot() != _savedSnapshot;
        UpdateStatus();
    }

    private string Snapshot() => PipelineText.Format(Stages.Select(static s => s.ToSpec()));

    // ── 两种模式之间 ───────────────────────────────────────────────────────

    private void SwitchMode(PipelineMode mode)
    {
        if (Mode == mode)
        {
            return;
        }
        if (mode == PipelineMode.Text)
        {
            _syncingText = true;
            PipelineTextValue = Snapshot();
            _syncingText = false;
            TextError = "";
            TextDiagnostics = [];
            Mode = mode;
        }
        else
        {
            _textDebounce?.Cancel();
            // 解析失败就停在文本模式:切回卡片会丢掉文本里那段改动,而用户多半正改到一半。
            if (!ApplyText())
            {
                Workspace.Toast(new() { Title = Loc["Pipe_TextInvalid"], Detail = TextError, Kind = ToastKind.Warning });
                RaisePropertiesChanged(nameof(IsStagesMode), nameof(IsTextMode));
                return;
            }
            Mode = mode;
        }
        RaisePropertiesChanged(nameof(Mode), nameof(IsStagesMode), nameof(IsTextMode));
    }

    private void ScheduleTextSync()
    {
        _textDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _textDebounce = cts;
        _ = SyncTextLaterAsync(cts.Token);
    }

    private async Task SyncTextLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        _ = ApplyText();
    }

    /// <summary>把文本解析回卡片;失败时标错并返回 <see langword="false" />(卡片不动)。</summary>
    internal bool ApplyText()
    {
        PipelineParse parse = PipelineText.Parse(PipelineTextValue);
        if (!parse.Ok)
        {
            string message = Loc.Format(parse.ErrorKey!, parse.ErrorArgument);
            TextError = message;
            TextDiagnostics = [new EditorDiagnostic(Math.Clamp(parse.ErrorOffset, 0, Math.Max(0, PipelineTextValue.Length - 1)), parse.ErrorLength, message)];
            RaisePropertiesChanged(nameof(SummaryIcon), nameof(SummaryToken));
            return false;
        }
        TextError = "";
        TextDiagnostics = [];
        RaisePropertiesChanged(nameof(SummaryIcon), nameof(SummaryToken));
        SyncStages(parse.Stages);
        return true;
    }

    /// <summary>按位置就地更新卡片(保留展开状态),多出的加、少了的删。</summary>
    private void SyncStages(IReadOnlyList<PipelineStageSpec> specs)
    {
        _bulk = true;
        try
        {
            for (int i = 0; i < specs.Count; i++)
            {
                PipelineStageSpec spec = specs[i];
                if (i < Stages.Count)
                {
                    PipelineStage stage = Stages[i];
                    stage.Operator = spec.Operator;
                    stage.Body = spec.Body;
                    stage.IsEnabled = spec.Enabled;
                }
                else
                {
                    Stages.Add(new PipelineStage(this, spec.Operator, spec.Body, spec.Enabled));
                }
            }
            while (Stages.Count > specs.Count)
            {
                Stages.RemoveAt(Stages.Count - 1);
            }
        }
        finally
        {
            _bulk = false;
        }
        OnPipelineChanged();
    }

    // ── 预览 ───────────────────────────────────────────────────────────────

    private IMongoCollection<BsonDocument> Collection => Workspace.Connection.Collection(Database, CollectionName);

    private TimeSpan? MaxTime => Workspace.Connection.Settings.MaxTimeMs > 0
        ? TimeSpan.FromMilliseconds(Workspace.Connection.Settings.MaxTimeMs)
        : null;

    private void SchedulePreview(bool immediate = false)
    {
        _previewDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _previewDebounce = cts;
        _ = PreviewLaterAsync(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(400), cts.Token);
    }

    private async Task PreviewLaterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            await RefreshPreviewAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 算一次逐阶段预览(新的一次作废上一次:用户连着改,只有最后那份结果该落到卡片上)。
    /// </summary>
    internal async Task RefreshPreviewAsync()
    {
        _previewRun?.Cancel();
        var cts = new CancellationTokenSource();
        _previewRun = cts;
        List<PipelineStage> stages = [.. Stages];
        List<(BsonDocument? Stage, bool Enabled)> input = [.. stages.Select(static s => (s.Document, s.IsEnabled))];
        IsPreviewing = true;
        try
        {
            var options = new AggregateOptions
            {
                AllowDiskUse = AllowDiskUse,
                MaxTime = MaxTime,
                Comment = "velashell-pipeline-preview"
            };
            PipelinePreviewRun run = await PipelinePreview.RunAsync(Collection, input, SampleSize, options, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            ApplyPreview(stages, run);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 预览从不写,任何失败都只是"这次没算出来":记在底栏,不弹错误框打断用户打字。
            if (!cts.IsCancellationRequested)
            {
                PreviewSummary = MongoConnector.Describe(ex);
                RaisePropertyChanged(nameof(PreviewSummary));
            }
        }
        finally
        {
            if (ReferenceEquals(_previewRun, cts))
            {
                IsPreviewing = false;
            }
        }
    }

    private void ApplyPreview(List<PipelineStage> stages, PipelinePreviewRun run)
    {
        PreviewSummary = "";
        _inputSample = run.Input;
        IReadOnlyList<PipelineFieldSample> upstream = PipelineFields.Sample(run.Input);
        for (int i = 0; i < stages.Count && i < run.Stages.Count; i++)
        {
            PipelineStage stage = stages[i];
            PipelineStageOutcome outcome = run.Stages[i];
            stage.ApplyOutcome(outcome);
            stage.UpstreamFields = upstream;
            if (stage.IsEnabled && outcome.State == PipelineStageState.Ok)
            {
                upstream = PipelineFields.Sample(outcome.Documents);
            }
        }
        _previewElapsed = run.Elapsed;
        _activity = Loc.Format("Pipe_StatusPreview", PipelineResults.Elapsed(run.Elapsed), SampleSize);
        RaisePropertiesChanged(nameof(PreviewSummary), nameof(StageSummary), nameof(ErrorCount), nameof(SummaryIcon), nameof(SummaryToken));
        UpdateStatus();
    }

    // ── 运行 ───────────────────────────────────────────────────────────────

    /// <summary>启用的阶段(运行、保存为视图、导出用)。</summary>
    internal List<BsonDocument> EnabledPipeline() =>
        [.. Stages.Where(static s => s.IsEnabled).Select(static s => s.Document).OfType<BsonDocument>()];

    /// <summary>
    /// 跑之前的检查:文本模式先同步,启用的阶段都要解析得了。
    /// 返回 <see langword="false" /> 时已经提示过用户。
    /// </summary>
    private bool EnsureRunnable()
    {
        if (Mode == PipelineMode.Text && !ApplyText())
        {
            Workspace.Toast(new() { Title = Loc["Pipe_TextInvalid"], Detail = TextError, Kind = ToastKind.Warning });
            return false;
        }
        if (Stages.FirstOrDefault(static s => s.IsEnabled && s.Document is null) is { } invalid)
        {
            invalid.IsExpanded = true;
            Workspace.Toast(new() { Title = Loc.Format("Pipe_RunInvalid", invalid.Index), Detail = invalid.ParseError, Kind = ToastKind.Warning });
            return false;
        }
        return true;
    }

    /// <summary>运行整条管道,结果进输出表。</summary>
    internal async Task RunAsync()
    {
        if (IsRunning || !EnsureRunnable())
        {
            return;
        }
        List<BsonDocument> pipeline = EnabledPipeline();
        (string Database, string Collection)? target = WriteTarget(pipeline);
        if (target is { } write && !await ConfirmWriteAsync(pipeline[^1].GetElement(0).Name, write).ConfigureAwait(true))
        {
            return;
        }
        await ExecuteAsync(pipeline, target).ConfigureAwait(true);
    }

    private async Task ExecuteAsync(List<BsonDocument> pipeline, (string Database, string Collection)? target)
    {
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _runComment = $"velashell-pipeline-{Guid.NewGuid():N}";
        IsRunning = true;
        RunError = "";
        HasRun = true;
        var watch = Stopwatch.StartNew();
        try
        {
            var options = new AggregateOptions { AllowDiskUse = AllowDiskUse, MaxTime = MaxTime, Comment = _runComment };
            using IAsyncCursor<BsonDocument> cursor = await Collection
                .AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline), options, cts.Token)
                .ConfigureAwait(true);
            var docs = new List<BsonDocument>();
            bool more = false;
            while (!more && await cursor.MoveNextAsync(cts.Token).ConfigureAwait(true))
            {
                foreach (BsonDocument doc in cursor.Current)
                {
                    if (docs.Count >= ResultLimit)
                    {
                        more = true;
                        break;
                    }
                    docs.Add(doc);
                }
            }
            watch.Stop();
            IReadOnlyList<PipelineGhost> ghosts = target is null ? await GhostsAsync(cts.Token).ConfigureAwait(true) : [];
            SetResults(docs, more, watch.Elapsed, ghosts);
            if (target is { } write)
            {
                Workspace.Toast(new()
                {
                    Title = Loc.Format("Pipe_WriteDone", $"{write.Database}.{write.Collection}"),
                    Detail = PipelineResults.Elapsed(watch.Elapsed),
                    Kind = ToastKind.Success,
                    ActionLabel = Loc["Pipe_Open"],
                    Action = () =>
                    {
                        Workspace.OpenCollection(write.Database, write.Collection);
                        return Task.CompletedTask;
                    },
                    Duration = TimeSpan.FromSeconds(8)
                });
                await Workspace.RefreshTreeAsync(write.Database).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            RunError = Loc["Pipe_RunCancelled"];
            ResultSummary = "";
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            RunError = MongoConnector.Describe(ex);
            ResultSummary = "";
            ResultColumns = [];
            ResultRows = [];
            RaisePropertiesChanged(nameof(HasRows), nameof(ResultWidth), nameof(ResultListHeight), nameof(ShowEmptyResult));
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", RunError), Kind = ToastKind.Error });
        }
        finally
        {
            if (ReferenceEquals(_runCts, cts))
            {
                _runCts = null;
            }
            IsRunning = false;
            RaisePropertyChanged(nameof(ResultSummary));
        }
    }

    private void StopRun()
    {
        if (_runCts is not { } cts)
        {
            return;
        }
        cts.Cancel();
        // 取消只断了客户端的等待;服务器上那条聚合还在跑,按 comment 找到它 killOp。
        if (_runComment is { } comment)
        {
            _ = KillAsync(comment);
        }
    }

    private async Task KillAsync(string comment)
    {
        try
        {
            _ = await Workspace.Connection.KillByCommentAsync(comment).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"killOp for '{comment}' failed: {ex.Message}");
        }
    }

    private void SetResults(List<BsonDocument> docs, bool more, TimeSpan elapsed, IReadOnlyList<PipelineGhost> ghosts)
    {
        (IReadOnlyList<PipelineColumn> columns, IReadOnlyList<PipelineRow> rows) = PipelineResults.Build(docs, ghosts, Loc);
        ResultColumns = columns;
        ResultRows = rows;
        string count = BsonText.Grouped(docs.Count);
        ResultSummary = more
            ? Loc.Format("Pipe_ResultSummaryMore", count, PipelineResults.Elapsed(elapsed))
            : Loc.Format("Pipe_ResultSummary", count, PipelineResults.Elapsed(elapsed));
        _activity = Loc.Format("Pipe_StatusRun", more ? count + "+" : count, PipelineResults.Elapsed(elapsed));
        RaisePropertiesChanged(nameof(ResultSummary), nameof(HasRows), nameof(ResultWidth), nameof(ResultListHeight), nameof(ShowEmptyResult));
        UpdateStatus();
    }

    /// <summary>停用阶段会产出的字段(<c>$lookup</c> 的 from 集合抽一个字段名,画成 <c>product.name</c>)。</summary>
    private async Task<IReadOnlyList<PipelineGhost>> GhostsAsync(CancellationToken cancellationToken)
    {
        List<(string Operator, BsonValue? Value, bool Enabled)> specs = [.. Stages.Select(static s => (s.Operator, s.Value, s.IsEnabled))];
        var samples = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach ((string op, BsonValue? value, bool enabled) in specs)
        {
            if (enabled || op != "$lookup" || value is not BsonDocument body
                || !body.TryGetValue("from", out BsonValue from) || !from.IsString || samples.ContainsKey(from.AsString))
            {
                continue;
            }
            try
            {
                BsonDocument? one = await Workspace.Connection.Collection(Database, from.AsString)
                    .Find(FilterDefinition<BsonDocument>.Empty).Limit(1).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(true);
                samples[from.AsString] = one?.Names.FirstOrDefault(static n => n != "_id");
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                samples[from.AsString] = null;
            }
        }
        return PipelineResults.Ghosts(specs, from => samples.GetValueOrDefault(from));
    }

    /// <summary>管道末尾是 <c>$out</c> / <c>$merge</c> 时它写到哪里;不写返回 <see langword="null" />。</summary>
    internal (string Database, string Collection)? WriteTarget(IReadOnlyList<BsonDocument> pipeline)
    {
        if (pipeline.Count == 0 || pipeline[^1] is not { ElementCount: > 0 } last)
        {
            return null;
        }
        BsonElement element = last.GetElement(0);
        BsonValue spec = element.Value;
        switch (element.Name)
        {
            case "$out":
                return spec switch
                {
                    BsonString name => (Database, name.Value),
                    BsonDocument doc => (doc.GetValue("db", Database).ToString()!, doc.GetValue("coll", "").ToString()!),
                    _ => (Database, spec.ToString()!)
                };
            case "$merge":
                {
                    BsonValue into = spec is BsonDocument merge ? merge.GetValue("into", "") : spec;
                    return into switch
                    {
                        BsonString name => (Database, name.Value),
                        BsonDocument doc => (doc.GetValue("db", Database).ToString()!, doc.GetValue("coll", "").ToString()!),
                        _ => (Database, into.ToString()!)
                    };
                }
            default:
                return null;
        }
    }

    /// <summary>写入阶段的确认:只读拦截 → 写清后果(<c>$out</c> 是整体替换,红色)→ 生产连接手打集合名。</summary>
    private async Task<bool> ConfirmWriteAsync(string op, (string Database, string Collection) target)
    {
        if (!Workspace.EnsureWritable(target.Database))
        {
            return false;
        }
        return await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Pipe_WriteTitle"],
            Message = Loc.Format(op == "$out" ? "Pipe_OutBody" : "Pipe_MergeBody", $"{target.Database}.{target.Collection}"),
            ConfirmLabel = Loc["Pipe_WriteConfirm"],
            IconKey = "Mongo.save",
            Danger = op == "$out",
            TypeToConfirm = Workspace.Guard.ConfirmWrites ? target.Collection : null
        }).ConfigureAwait(true);
    }

    // ── 工具行与输出卡上的动作 ─────────────────────────────────────────────

    /// <summary>mongosh 写法的 aggregate 调用(执行计划、在结果标签中打开)。</summary>
    internal string AggregateCall(bool withoutWrites = false)
    {
        IEnumerable<PipelineStageSpec> specs = Stages.Where(static s => s.IsEnabled).Select(static s => s.ToSpec());
        if (withoutWrites)
        {
            specs = specs.Where(static s => !PipelinePreview.WriteStages.Contains(s.Operator));
        }
        string options = AllowDiskUse ? ", { allowDiskUse: true }" : "";
        return $"db.{MongoWorkspaceViewModel.ShellCollectionRef(CollectionName)}.aggregate({PipelineText.Format(specs)}{options})";
    }

    private void Explain()
    {
        if (!EnsureRunnable())
        {
            return;
        }
        // explain 不该真写:去掉 $out / $merge 再看计划(写入阶段本身不影响读取那一段的计划)。
        Workspace.OpenQuery(Database, AggregateCall(withoutWrites: true) + ".explain(\"executionStats\")", run: true);
    }

    private void OpenInResults()
    {
        if (!EnsureRunnable())
        {
            return;
        }
        // 带写入阶段时只打开不自动运行 —— 换个标签就悄悄写了一次库,那是事故。
        bool writes = WriteTarget(EnabledPipeline()) is not null;
        Workspace.OpenQuery(Database, AggregateCall(), run: !writes);
    }

    private void ExportCode()
    {
        if (!EnsureRunnable())
        {
            return;
        }
        Workspace.ShowDialog(new PipelineCodeDialogViewModel(Workspace, Database, CollectionName, EnabledPipeline(), AllowDiskUse));
    }

    /// <summary>保存管道(按连接存放,同名覆盖)。</summary>
    private async Task SaveAsync()
    {
        if (Mode == PipelineMode.Text && !ApplyText())
        {
            Workspace.Toast(new() { Title = Loc["Pipe_TextInvalid"], Detail = TextError, Kind = ToastKind.Warning });
            return;
        }
        IReadOnlyList<SavedItem> existing = await Workspace.Store.LoadSavedAsync("pipeline", Workspace.ConnectionKey).ConfigureAwait(true);
        var dialog = new PipelineNameDialogViewModel(Workspace, new()
        {
            Title = Loc["Pipe_SaveTitle"],
            IconKey = "Mongo.save",
            Label = Loc["Pipe_SaveName"],
            Initial = _savedName ?? $"{CollectionName} · {DateTime.Now:MM-dd HH:mm}",
            ConfirmLabel = Loc["Common_Save"],
            Hint = Loc["Pipe_SaveHint"],
            Suggestions = [.. existing.Select(static i => i.Name).Take(12)],
            Validate = name => name.Trim().Length == 0 ? Loc["Pipe_NameRequired"] : null
        });
        Workspace.ShowDialog(dialog);
        if (await dialog.Result.ConfigureAwait(true) is not { } chosen)
        {
            return;
        }
        string name = chosen.Name.Trim();
        await SavePipelineAsync(name).ConfigureAwait(true);
        Workspace.Toast(new() { Title = Loc.Format("Pipe_SaveDone", name), Kind = ToastKind.Success });
    }

    /// <summary>保存的正文:第一行注释写明命名空间,其后是整条管道的文本(停用的阶段照样以注释保存)。</summary>
    internal async Task SavePipelineAsync(string name)
    {
        string snapshot = Snapshot();
        string content = $"// {Info.Namespace}\n{snapshot}";
        await Workspace.Store.SaveItemAsync("pipeline", Workspace.ConnectionKey, new SavedItem(name, content, DateTimeOffset.Now)).ConfigureAwait(true);
        _savedName = name;
        _savedSnapshot = snapshot;
        UpdateModified();
    }

    /// <summary>创建视图:输入视图名 → 只读拦截 → 确认 → <c>create { viewOn, pipeline }</c>。</summary>
    private async Task CreateViewAsync()
    {
        if (!EnsureRunnable())
        {
            return;
        }
        List<BsonDocument> pipeline = EnabledPipeline();
        if (WriteTarget(pipeline) is not null)
        {
            Workspace.Toast(new() { Title = Loc["Pipe_ViewNoWrite"], Kind = ToastKind.Warning });
            return;
        }
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        var dialog = new PipelineNameDialogViewModel(Workspace, new()
        {
            Title = Loc["Pipe_ViewTitle"],
            IconKey = "Mongo.eye",
            Label = Loc["Pipe_ViewName"],
            Initial = $"v_{CollectionName}",
            ConfirmLabel = Loc["Pipe_ViewConfirm"],
            Hint = Loc.Format("Pipe_ViewHint", Info.Namespace, pipeline.Count),
            Validate = ValidateNewCollectionName
        });
        Workspace.ShowDialog(dialog);
        if (await dialog.Result.ConfigureAwait(true) is not { } chosen)
        {
            return;
        }
        string name = chosen.Name.Trim();
        if (!await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Pipe_ViewTitle"],
            Message = Loc.Format("Pipe_ViewConfirmBody", name, CollectionName, Database),
            ConfirmLabel = Loc["Pipe_ViewConfirm"],
            IconKey = "Mongo.eye",
            Danger = false,
            TypeToConfirm = Workspace.Guard.ConfirmWrites ? name : null
        }).ConfigureAwait(true))
        {
            return;
        }
        _ = await CreateViewCoreAsync(name, pipeline).ConfigureAwait(true);
    }

    /// <summary>真正建视图(单测直接调)。</summary>
    internal async Task<bool> CreateViewCoreAsync(string name, IReadOnlyList<BsonDocument> pipeline)
    {
        try
        {
            _ = await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "create", name },
                { "viewOn", CollectionName },
                { "pipeline", new BsonArray(pipeline) }
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
            return false;
        }
        Workspace.Toast(new()
        {
            Title = Loc.Format("Pipe_ViewDone", $"{Database}.{name}"),
            Kind = ToastKind.Success,
            ActionLabel = Loc["Pipe_Open"],
            Action = () =>
            {
                Workspace.OpenCollection(Database, name);
                return Task.CompletedTask;
            },
            Duration = TimeSpan.FromSeconds(8)
        });
        await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
        return true;
    }

    /// <summary>另存为集合:选目标与方式($out 整体替换 / $merge 合并)→ 确认 → 跑带写入阶段的管道。</summary>
    private async Task SaveAsCollectionAsync()
    {
        if (!EnsureRunnable())
        {
            return;
        }
        List<BsonDocument> pipeline = EnabledPipeline();
        if (WriteTarget(pipeline) is not null)
        {
            Workspace.Toast(new() { Title = Loc.Format("Pipe_SaveAsHasWrite", pipeline[^1].GetElement(0).Name), Kind = ToastKind.Warning });
            return;
        }
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        var dialog = new PipelineNameDialogViewModel(Workspace, new()
        {
            Title = Loc["Pipe_SaveAsTitle"],
            IconKey = "Mongo.save",
            Label = Loc["Pipe_SaveAsName"],
            Initial = $"{CollectionName}_out",
            ConfirmLabel = Loc["Common_Next"],
            OfferMerge = true,
            Validate = name =>
            {
                string trimmed = name.Trim();
                if (trimmed.Length == 0)
                {
                    return Loc["Pipe_NameRequired"];
                }
                if (trimmed == CollectionName)
                {
                    return Loc["Pipe_SaveAsSelf"];
                }
                return trimmed.Contains('$', StringComparison.Ordinal) || trimmed.StartsWith("system.", StringComparison.Ordinal)
                    ? Loc["Pipe_NameInvalid"]
                    : null;
            }
        });
        Workspace.ShowDialog(dialog);
        if (await dialog.Result.ConfigureAwait(true) is not { } chosen)
        {
            return;
        }
        string target = chosen.Name.Trim();
        BsonDocument write = chosen.Merge
            ? new BsonDocument("$merge", new BsonDocument { { "into", target }, { "on", "_id" }, { "whenMatched", "merge" }, { "whenNotMatched", "insert" } })
            : new BsonDocument("$out", target);
        List<BsonDocument> full = [.. pipeline, write];
        if (!await ConfirmWriteAsync(write.GetElement(0).Name, (Database, target)).ConfigureAwait(true))
        {
            return;
        }
        await ExecuteAsync(full, (Database, target)).ConfigureAwait(true);
    }

    /// <summary>新集合 / 视图名的校验(不能空、不能已存在、不能含 <c>$</c>)。</summary>
    private string? ValidateNewCollectionName(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return Loc["Pipe_NameRequired"];
        }
        if (trimmed.Contains('$', StringComparison.Ordinal) || trimmed.StartsWith("system.", StringComparison.Ordinal) || trimmed.Contains('\0'))
        {
            return Loc["Pipe_NameInvalid"];
        }
        return Workspace.CollectionsOf(Database).Any(c => c.Name == trimmed) ? Loc.Format("Pipe_NameExists", trimmed, Database) : null;
    }

    // ── 状态栏 ─────────────────────────────────────────────────────────────

    private void UpdateStatus()
    {
        string state = _savedName is null
            ? Loc["Pipe_Unsaved"]
            : IsModified ? Loc.Format("Pipe_SavedModified", _savedName) : Loc.Format("Pipe_SavedState", _savedName);
        StatusText = _activity.Length > 0 ? $"{Info.Namespace} · {state} · {_activity}" : $"{Info.Namespace} · {state}";
    }
}
