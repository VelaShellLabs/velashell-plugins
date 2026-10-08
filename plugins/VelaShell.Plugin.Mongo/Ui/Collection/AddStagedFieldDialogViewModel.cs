using Avalonia.Controls;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>类型下拉里的一项(色块 + 名字)。</summary>
/// <param name="Kind">类型。</param>
internal sealed record ValueKindChoice(BsonKind Kind)
{
    /// <summary>显示名(<c>Binary · UUID</c>)。</summary>
    public string Name => BsonKinds.MenuName(Kind);

    /// <summary>色块令牌。</summary>
    public string Token => BsonKinds.ColorToken(Kind);

    /// <summary>可选的全部类型。</summary>
    public static IReadOnlyList<ValueKindChoice> All { get; } = [.. BsonKinds.Editable.Select(static k => new ValueKindChoice(k))];
}

/// <summary>
/// 「添加字段」小对话框(检查器的「添加字段 Ctrl+Enter」、树的「添加字段… Ctrl+D」):名字 + 类型 + 值。
/// 结果交给调用方写进暂存区 —— 与别的修改一样等 Ctrl+S 才提交。
/// </summary>
internal sealed class AddStagedFieldDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly BsonDocument _document;
    private readonly string _parent;
    private readonly Func<string, BsonValue, bool> _stage;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="document">要加字段的文档(查重名)。</param>
    /// <param name="parentPath">加在哪个对象下;顶层为空串。</param>
    /// <param name="stage">写入暂存区;返回是否写成。</param>
    public AddStagedFieldDialogViewModel(IMongoWorkspace workspace, BsonDocument document, string parentPath, Func<string, BsonValue, bool> stage)
        : base(workspace)
    {
        _document = document;
        _parent = parentPath;
        _stage = stage;
        Title = Loc["Cw_AddFieldTitle"];
        Subtitle = parentPath.Length == 0 ? Loc["Cw_AddFieldRoot"] : parentPath;
        ConfirmCommand = new RelayCommand(Confirm);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.plus";

    /// <inheritdoc />
    public override double Width => 420;

    /// <summary>字段名。</summary>
    public string Name
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                Error = null;
            }
        }
    } = "";

    /// <summary>可选类型。</summary>
    public IReadOnlyList<ValueKindChoice> Kinds => ValueKindChoice.All;

    /// <summary>选中的类型。</summary>
    public ValueKindChoice Kind
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ValueKindChoice.All[0]))
            {
                Error = null;
                RaisePropertyChanged(nameof(ValueHint));
            }
        }
    } = ValueKindChoice.All[0];

    /// <summary>值(按类型解析;空 = 该类型的空值)。</summary>
    public string ValueText
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                Error = null;
            }
        }
    } = "";

    /// <summary>值框的占位提示。</summary>
    public string ValueHint => Loc.Format("Cw_AddFieldValueHint", BsonText.Literal(BsonEdit.Empty(Kind.Kind)));

    /// <summary>错误。</summary>
    public string? Error
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有错。</summary>
    public bool HasError => Error is not null;

    /// <summary>添加。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <inheritdoc />
    public Control CreateView() => new AddStagedFieldDialogView { DataContext = this };

    private void Confirm()
    {
        string name = Name.Trim();
        if (name.Length == 0 || name.StartsWith('$') || name.Contains('.'))
        {
            Error = Loc["Cw_AddFieldBadName"];
            return;
        }
        string path = _parent.Length == 0 ? name : $"{_parent}.{name}";
        if (BsonPath.Get(_document, path) is not null)
        {
            Error = Loc["Cw_AddFieldExists"];
            return;
        }
        BsonValue value;
        if (ValueText.Length == 0)
        {
            value = BsonEdit.Empty(Kind.Kind);
        }
        else if (!BsonEdit.TryParse(ValueText, Kind.Kind, out value, out string? error))
        {
            Error = Loc[error ?? "Edit_BadLiteral"];
            return;
        }
        if (_stage(path, value))
        {
            Close();
        }
    }
}
