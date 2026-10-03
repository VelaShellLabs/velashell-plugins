using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一张阶段卡片(设计稿 04 的一行 <c>① $match 筛选文档 … → 1,284 份文档</c>)。
/// <para>
/// 阶段体以**原文**为准(<see cref="Body" />),解析结果是派生出来的:
/// 用户敲到一半的阶段体解析不了是常态,卡片要能带着错误继续存在,而不是把用户的字吞掉。
/// </para>
/// </summary>
internal sealed class PipelineStage : ObservableObject
{
    private readonly PipelineTabViewModel _owner;
    private string _operator;
    private string _body;
    private bool _isEnabled;
    private PipelineBodyParse _parse = new(null);

    /// <summary>构造。</summary>
    /// <param name="owner">所在的管道。</param>
    /// <param name="op">运算符。</param>
    /// <param name="body">阶段体原文。</param>
    /// <param name="enabled">是否启用。</param>
    public PipelineStage(PipelineTabViewModel owner, string op, string body, bool enabled = true)
    {
        _owner = owner;
        _operator = op;
        _body = body;
        _isEnabled = enabled;
        Reparse();
        CompletionProvider = request => Task.FromResult(
            PipelineFields.Complete(request, Operator, UpstreamFields, _owner.SampleSize, _owner.Loc));
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        RemoveCommand = new RelayCommand(() => _owner.RemoveStage(this));
        DuplicateCommand = new RelayCommand(() => _owner.DuplicateStage(this));
        MoveUpCommand = new RelayCommand(() => _owner.MoveStage(this, -1));
        MoveDownCommand = new RelayCommand(() => _owner.MoveStage(this, +1));
        ShowPreviewCommand = new RelayCommand(() => _owner.ShowStagePreview(this));
    }

    /// <summary>运算符、阶段体或启用状态变了(管道据此重算预览、标记未保存)。</summary>
    public event Action<PipelineStage>? Changed;

    /// <summary>文案。</summary>
    public Loc Loc => _owner.Loc;

    /// <summary>运算符(<c>$match</c>)。</summary>
    public string Operator
    {
        get => _operator;
        set
        {
            if (SetProperty(ref _operator, value))
            {
                RaisePropertiesChanged(nameof(Description), nameof(Tip));
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>阶段体原文(编辑器双向绑定)。</summary>
    public string Body
    {
        get => _body;
        set
        {
            if (SetProperty(ref _body, value ?? ""))
            {
                Reparse();
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>启用(停用的卡片整体变淡并标「已停用」,预览与运行都跳过它)。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                RaisePropertiesChanged(nameof(Description), nameof(CountText), nameof(OperatorToken), nameof(IndexToken),
                    nameof(IndexBackgroundToken), nameof(InlineMuted));
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>展开(左编辑器 + 右输出预览)。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ChevronKey), nameof(ShowInline));
            }
        }
    }

    /// <summary>序号(1 起;拖动重排后由管道重新编号)。</summary>
    public int Index
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>灰色说明(<c>筛选文档</c>;停用时加「· 已停用」)。</summary>
    public string Description
    {
        get
        {
            string key = "Pipe_Desc_" + Operator.TrimStart('$');
            string text = Loc[key];
            if (text == key)
            {
                text = Loc["Pipe_Desc_custom"];
            }
            return IsEnabled ? text : text + " · " + Loc["Pipe_Disabled"];
        }
    }

    /// <summary>运算符下拉的悬停说明(词汇表里的完整一句)。</summary>
    public string Tip =>
        Shell.MongoVocabulary.Stages.FirstOrDefault(s => s.Name == Operator)?.Describe(Loc.IsChinese) ?? Operator;

    /// <summary>运算符文字颜色(停用时退成三级文字色)。</summary>
    public string OperatorToken => IsEnabled ? "VelaShellMagenta" : "VelaTextTertiary";

    /// <summary>序号圆标文字色。</summary>
    public string IndexToken => IsEnabled ? "VelaAccent" : "VelaTextTertiary";

    /// <summary>序号圆标底色。</summary>
    public string IndexBackgroundToken => IsEnabled ? "VelaAccentDim" : "VelaBgActive";

    /// <summary>折叠箭头。</summary>
    public string ChevronKey => IsExpanded ? "Mongo.chevron-up" : "Mongo.chevron-down";

    /// <summary>
    /// 编辑器高度:按行数给一个确定的高度(至少 3 行)。卡片在竖向 StackPanel 里拿到的是无限高,
    /// 让 AvaloniaEdit 自己"按内容撑开"会一路撑到上限 —— 它的滚动区在无限高下量不出内容高度。
    /// 阶段体整段都摆出来、编辑器自己不竖向滚动:滚轮交给外面那一列卡片,长阶段体就是一张高卡片。
    /// </summary>
    public double EditorHeight => Math.Clamp(_body.Count(static c => c == '\n') + 1, 3, 400) * EditorLineHeight + 16;

    /// <summary>编辑器一行的高度(11px 等宽字)。</summary>
    internal const double EditorLineHeight = 16;

    /// <summary>折叠态的一行内联阶段体。</summary>
    public string InlineBody => PipelineTokens.OneLine(Body);

    /// <summary>内联阶段体是否显示(只在折叠态)。</summary>
    public bool ShowInline => !IsExpanded;

    /// <summary>内联阶段体整体压成弱色(停用时)。</summary>
    public bool InlineMuted => !IsEnabled;

    /// <summary>解析出的阶段体;解析不了为 <see langword="null" />。</summary>
    public BsonValue? Value => _parse.Value;

    /// <summary>整个阶段 <c>{ $op: body }</c>;解析不了为 <see langword="null" />。</summary>
    public BsonDocument? Document => _parse.Value is { } value ? new BsonDocument(Operator, value) : null;

    /// <summary>阶段体的语法错误(本地化后的一句);没有为 <see langword="null" />。</summary>
    public string? ParseError => _parse.Ok ? null : Loc.Format(_parse.ErrorKey!, _parse.ErrorArgument);

    /// <summary>编辑器诊断(波浪线 + 行尾提示)。</summary>
    public IReadOnlyList<EditorDiagnostic> Diagnostics => _parse.Ok
        ? []
        : [new EditorDiagnostic(Math.Clamp(_parse.Offset, 0, Math.Max(0, Body.Length - 1)), Math.Max(1, _parse.Length), ParseError!,
            DiagnosticSeverity.Error, _parse.Fix, _parse.Fix is null ? null : Loc["Pipe_FixHint"])];

    /// <summary>服务器在预览里对这个阶段的报错。</summary>
    public string? ServerError => Outcome.State == PipelineStageState.Error ? Outcome.Error : null;

    /// <summary>卡片右上角的错误标记文字;没有为空。</summary>
    public string ErrorText => ParseError ?? ServerError ?? "";

    /// <summary>有错(卡片描红)。</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>错误标记上的短字(语法错误 / 运行出错)。</summary>
    public string ErrorBadge => ParseError is not null ? Loc["Pipe_SyntaxError"] : Loc["Pipe_ServerError"];

    /// <summary>预览结局。</summary>
    public PipelineStageOutcome Outcome { get; private set; } = PipelineStageOutcome.Pending;

    /// <summary>预览带回的文档(放大看全部)。</summary>
    public IReadOnlyList<BsonDocument> PreviewDocuments => Outcome.Documents;

    /// <summary>卡片右侧的两张小文档卡。</summary>
    public IReadOnlyList<PipelineMiniDoc> PreviewCards { get; private set; } = [];

    /// <summary>有没有预览卡。</summary>
    public bool HasPreviewCards => PreviewCards.Count > 0;

    /// <summary>右上角计数:<c>1,284</c>;没算出来为「—」。</summary>
    public string CountText => IsEnabled && Outcome.State == PipelineStageState.Ok ? BsonText.Grouped(Outcome.Count) : "—";

    /// <summary>计数的悬停说明(说清是抽样上的计数)。</summary>
    public string CountTip => Outcome.State == PipelineStageState.Ok
        ? Loc.Format("Pipe_CountTip", BsonText.Grouped(Outcome.Count), _owner.SampleSize)
        : "";

    /// <summary>预览面板的副标题:<c>前 2 / 1,284</c>。</summary>
    public string PreviewHeader => Outcome.State == PipelineStageState.Ok
        ? Loc.Format("Pipe_PreviewFirst", PreviewCards.Count, BsonText.Grouped(Outcome.Count))
        : "";

    /// <summary>预览面板没有卡片时的那一句(计算中 / 无输出 / 上游出错 / 写入阶段不预览 …)。</summary>
    public string PreviewNote => Outcome.State switch
    {
        PipelineStageState.Ok when Outcome.Count == 0 => Loc["Pipe_PreviewEmpty"],
        PipelineStageState.Ok => "",
        PipelineStageState.Error => Outcome.Error ?? "",
        PipelineStageState.Blocked => Loc["Pipe_PreviewBlocked"],
        PipelineStageState.Invalid => Loc["Pipe_PreviewInvalid"],
        PipelineStageState.Skipped => Loc["Pipe_PreviewSkipped"],
        PipelineStageState.WriteStage => Loc["Pipe_PreviewWrite"],
        _ => _owner.AutoPreview ? Loc["Pipe_PreviewPending"] : Loc["Pipe_PreviewOff"]
    };

    /// <summary>预览说明是不是一条错误(红字)。</summary>
    public bool PreviewNoteIsError => Outcome.State == PipelineStageState.Error;

    /// <summary>能不能放大看全部预览。</summary>
    public bool CanShowPreview => Outcome.State == PipelineStageState.Ok && Outcome.Documents.Count > 0;

    /// <summary>上游字段(补全用:前一个启用阶段的输出预览里抽出来的)。</summary>
    public IReadOnlyList<PipelineFieldSample> UpstreamFields { get; set => SetProperty(ref field, value); } = [];

    /// <summary>阶段编辑器的补全来源。</summary>
    public Func<CompletionRequest, Task<CompletionSet?>> CompletionProvider { get; }

    /// <summary>展开 / 折叠。</summary>
    public RelayCommand ToggleExpandCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>复制一张同样的卡片(插在它后面)。</summary>
    public RelayCommand DuplicateCommand { get; }

    /// <summary>上移。</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>下移。</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>放大看全部预览。</summary>
    public RelayCommand ShowPreviewCommand { get; }

    /// <summary>把一次预览的结局挂到卡片上。</summary>
    internal void ApplyOutcome(PipelineStageOutcome outcome)
    {
        Outcome = outcome;
        PreviewCards = outcome.State == PipelineStageState.Ok ? [.. outcome.Documents.Take(2).Select(d => PipelineResults.Mini(d))] : [];
        RaisePropertiesChanged(nameof(Outcome), nameof(PreviewDocuments), nameof(PreviewCards), nameof(HasPreviewCards), nameof(CountText),
            nameof(CountTip), nameof(PreviewHeader), nameof(PreviewNote), nameof(PreviewNoteIsError), nameof(CanShowPreview),
            nameof(ServerError), nameof(ErrorText), nameof(HasError), nameof(ErrorBadge));
    }

    /// <summary>自动预览开关变了:没算过的卡片要换一句说明。</summary>
    internal void RefreshNote() => RaisePropertyChanged(nameof(PreviewNote));

    /// <summary>当前的文本形态(文本模式、保存、导出用)。</summary>
    internal PipelineStageSpec ToSpec() => new(Operator, Body, IsEnabled);

    private void Reparse()
    {
        _parse = PipelineText.ParseBody(_body);
        RaisePropertiesChanged(nameof(Value), nameof(Document), nameof(ParseError), nameof(Diagnostics), nameof(InlineBody),
            nameof(ErrorText), nameof(HasError), nameof(ErrorBadge), nameof(EditorHeight));
    }
}
