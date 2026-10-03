using Avalonia.Controls;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 「导出为代码」:当前语句 → mongosh / C# / Python / Node.js / Java 的驱动写法,顶部分段切语言,
/// 下面只读代码框,底栏一键复制。
/// </summary>
internal sealed class CodeExportDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly ShellCommand _command;
    private readonly string _database;
    private readonly string _connectionString;
    private string _target;

    /// <summary>构造。</summary>
    public CodeExportDialogViewModel(IMongoWorkspace workspace, ShellCommand command, string database, string connectionString, CodeTarget target)
        : base(workspace)
    {
        _command = command;
        _database = database;
        _connectionString = connectionString;
        _target = target.ToString();
        Title = workspace.Loc["Query_ExportCodeTitle"];
        Subtitle = command.Collection is { } c ? $"{command.Database ?? database}.{c} · {command.Operation}" : command.Operation;
        CopyCommand = new AsyncCommand(CopyAsync);
        Generate();
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.code-xml";

    /// <inheritdoc />
    public override double Width => 720;

    /// <inheritdoc />
    public override double Height => 520;

    /// <summary>目标语言(分段控件绑定的字符串:<c>Mongosh</c> / <c>CSharp</c> / …)。</summary>
    public string Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                Generate();
            }
        }
    }

    /// <summary>生成的代码。</summary>
    public string Code
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>复制。</summary>
    public AsyncCommand CopyCommand { get; }

    private CodeTarget Parsed => Enum.TryParse(_target, out CodeTarget t) ? t : CodeTarget.CSharp;

    private void Generate() => Code = CodeExport.Generate(_command, _database, Parsed, _connectionString);

    private async Task CopyAsync()
    {
        await Workspace.CopyAsync(Code).ConfigureAwait(true);
        Workspace.Toast(new ToastRequest { Title = Loc.Format("Query_CodeCopied", CodeExport.Name(Parsed)), Kind = ToastKind.Success });
    }

    /// <inheritdoc />
    public Control CreateView() => new CodeExportDialogView(this);
}
