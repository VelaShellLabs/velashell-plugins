using Avalonia.Controls;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「输入一个名字」对话框的配置(保存管道、创建视图、另存为集合共用)。</summary>
internal sealed record PipelineNameOptions
{
    /// <summary>标题。</summary>
    public required string Title { get; init; }

    /// <summary>标题栏图标。</summary>
    public string IconKey { get; init; } = "Mongo.save";

    /// <summary>输入框上方的标签。</summary>
    public required string Label { get; init; }

    /// <summary>初始值。</summary>
    public string Initial { get; init; } = "";

    /// <summary>确认按钮文字。</summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>输入框下方的说明。</summary>
    public string? Hint { get; init; }

    /// <summary>可点选的已有名字(保存管道时列出已保存的,点一下即覆盖它)。</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>校验:返回错误文字,通过返回 <see langword="null" />。</summary>
    public Func<string, string?>? Validate { get; init; }

    /// <summary>另存为集合:多给一组「$out 整体替换 / $merge 合并」。</summary>
    public bool OfferMerge { get; init; }
}

/// <summary>名字对话框的结果。</summary>
/// <param name="Name">名字。</param>
/// <param name="Merge">选了 <c>$merge</c>(否则 <c>$out</c>)。</param>
internal sealed record PipelineNameResult(string Name, bool Merge);

/// <summary>
/// 输入一个名字的小对话框。三处用它:保存管道、创建视图(视图名)、另存为集合(目标集合 + 写入方式)。
/// 只收集输入;只读拦截与"写清后果"的确认由调用方在它之后做 —— 那两步对每种写法不一样。
/// </summary>
internal sealed class PipelineNameDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly TaskCompletionSource<PipelineNameResult?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _name;
    private bool _merge;

    /// <summary>构造。</summary>
    public PipelineNameDialogViewModel(IMongoWorkspace workspace, PipelineNameOptions options)
        : base(workspace)
    {
        Options = options;
        Title = options.Title;
        _name = options.Initial;
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        PickCommand = new RelayCommand<string>(name => Name = name);
    }

    /// <summary>配置。</summary>
    public PipelineNameOptions Options { get; }

    /// <inheritdoc />
    public override string IconKey => Options.IconKey;

    /// <inheritdoc />
    public override double Width => 440;

    /// <summary>名字。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? ""))
            {
                RaisePropertiesChanged(nameof(Error), nameof(HasError), nameof(CanConfirm));
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>校验错误。</summary>
    public string Error => Options.Validate?.Invoke(_name) ?? "";

    /// <summary>有错。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>能确认。</summary>
    public bool CanConfirm => !HasError && _name.Trim().Length > 0;

    /// <summary>有没有说明。</summary>
    public bool HasHint => !HasError && !string.IsNullOrEmpty(Options.Hint);

    /// <summary>有没有可点选的已有名字。</summary>
    public bool HasSuggestions => Options.Suggestions.Count > 0;

    /// <summary>选了 <c>$out</c>。</summary>
    public bool IsOut
    {
        get => !_merge;
        set
        {
            if (value && _merge)
            {
                _merge = false;
                RaisePropertiesChanged(nameof(IsOut), nameof(IsMerge), nameof(WriteHint));
            }
        }
    }

    /// <summary>选了 <c>$merge</c>。</summary>
    public bool IsMerge
    {
        get => _merge;
        set
        {
            if (value && !_merge)
            {
                _merge = true;
                RaisePropertiesChanged(nameof(IsOut), nameof(IsMerge), nameof(WriteHint));
            }
        }
    }

    /// <summary>写入方式的说明。</summary>
    public string WriteHint => Loc[_merge ? "Pipe_SaveAsMergeHint" : "Pipe_SaveAsOutHint"];

    /// <summary>确认。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>点选一个已有名字。</summary>
    public RelayCommand<string> PickCommand { get; }

    /// <summary>结果;取消为 <see langword="null" />。</summary>
    public Task<PipelineNameResult?> Result => _result.Task;

    /// <inheritdoc />
    public Control CreateView() => new PipelineNameDialogView(this);

    private void Confirm()
    {
        if (!CanConfirm)
        {
            return;
        }
        _result.TrySetResult(new(_name.Trim(), _merge));
        Close();
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        _result.TrySetResult(null);
        base.OnClosed();
    }
}

/// <summary>「导出为代码」对话框:五种语言分段切换,只读编辑器展示,一键复制。</summary>
internal sealed class PipelineCodeDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly string _database;
    private readonly string _collection;
    private readonly IReadOnlyList<BsonDocument> _stages;
    private PipelineCodeLanguage _language = PipelineCodeLanguage.Mongosh;
    private bool _allowDiskUse;
    private string _code = "";

    /// <summary>构造。</summary>
    public PipelineCodeDialogViewModel(IMongoWorkspace workspace, string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse)
        : base(workspace)
    {
        _database = database;
        _collection = collection;
        _stages = stages;
        _allowDiskUse = allowDiskUse;
        Title = Loc["Pipe_CodeTitle"];
        Subtitle = $"{database}.{collection}";
        CopyCommand = new AsyncCommand(CopyAsync);
        Regenerate();
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.code-xml";

    /// <inheritdoc />
    public override double Width => 760;

    /// <inheritdoc />
    public override double Height => 540;

    /// <summary>当前语言。</summary>
    public PipelineCodeLanguage Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
            {
                RaisePropertiesChanged(nameof(IsMongosh), nameof(IsCSharp), nameof(IsPython), nameof(IsNode), nameof(IsJava));
                Regenerate();
            }
        }
    }

    /// <summary>mongosh 分段。</summary>
    public bool IsMongosh
    {
        get => _language == PipelineCodeLanguage.Mongosh;
        set
        {
            if (value)
            {
                Language = PipelineCodeLanguage.Mongosh;
            }
        }
    }

    /// <summary>C# 分段。</summary>
    public bool IsCSharp
    {
        get => _language == PipelineCodeLanguage.CSharp;
        set
        {
            if (value)
            {
                Language = PipelineCodeLanguage.CSharp;
            }
        }
    }

    /// <summary>Python 分段。</summary>
    public bool IsPython
    {
        get => _language == PipelineCodeLanguage.Python;
        set
        {
            if (value)
            {
                Language = PipelineCodeLanguage.Python;
            }
        }
    }

    /// <summary>Node.js 分段。</summary>
    public bool IsNode
    {
        get => _language == PipelineCodeLanguage.Node;
        set
        {
            if (value)
            {
                Language = PipelineCodeLanguage.Node;
            }
        }
    }

    /// <summary>Java 分段。</summary>
    public bool IsJava
    {
        get => _language == PipelineCodeLanguage.Java;
        set
        {
            if (value)
            {
                Language = PipelineCodeLanguage.Java;
            }
        }
    }

    /// <summary>代码里带不带 allowDiskUse。</summary>
    public bool AllowDiskUse
    {
        get => _allowDiskUse;
        set
        {
            if (SetProperty(ref _allowDiskUse, value))
            {
                Regenerate();
            }
        }
    }

    /// <summary>生成的代码。</summary>
    public string Code
    {
        get => _code;
        private set => SetProperty(ref _code, value);
    }

    /// <summary>底栏说明(<c>5 个启用阶段 · 停用的阶段不导出</c>)。</summary>
    public string Note => Loc.Format("Pipe_CodeNote", _stages.Count);

    /// <summary>复制。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <inheritdoc />
    public Control CreateView() => new PipelineCodeDialogView(this);

    private void Regenerate() => Code = PipelineCode.Generate(_language, _database, _collection, _stages, _allowDiskUse);

    private async Task CopyAsync()
    {
        await Workspace.CopyAsync(Code).ConfigureAwait(true);
        Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
    }
}

/// <summary>某个阶段输出预览的"看全部"(卡片上只摆得下两张小文档卡)。</summary>
internal sealed class PipelinePreviewDialogViewModel : DialogViewModel, IViewFactory
{
    /// <summary>构造。</summary>
    public PipelinePreviewDialogViewModel(IMongoWorkspace workspace, string title, string subtitle, IReadOnlyList<BsonDocument> documents)
        : base(workspace)
    {
        Title = title;
        Subtitle = subtitle;
        Text = string.Join(",\n", documents.Select(static d => BsonText.Pretty(d, EjsonMode.Shell)));
        CopyCommand = new AsyncCommand(async () =>
        {
            await Workspace.CopyAsync("[\n" + Text + "\n]").ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
        });
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.maximize-2";

    /// <inheritdoc />
    public override double Width => 760;

    /// <inheritdoc />
    public override double Height => 560;

    /// <summary>文档(mongosh 写法,逐份排开)。</summary>
    public string Text { get; }

    /// <summary>复制全部。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <inheritdoc />
    public Control CreateView() => new PipelinePreviewDialogView(this);
}
