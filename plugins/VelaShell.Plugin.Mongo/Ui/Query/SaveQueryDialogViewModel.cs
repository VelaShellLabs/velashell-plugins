using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「保存查询」:给这段脚本起个名字(同名覆盖),存进 <c>MongoStore</c> 的 <c>query</c> 类。</summary>
internal sealed class SaveQueryDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly Func<string, Task> _save;
    private string _name;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="suggested">默认名(<c>查询 1</c>)。</param>
    /// <param name="save">确认后的保存动作。</param>
    public SaveQueryDialogViewModel(IMongoWorkspace workspace, string suggested, Func<string, Task> save)
        : base(workspace)
    {
        _save = save;
        _name = suggested;
        Title = workspace.Loc["Query_SaveTitle"];
        Subtitle = workspace.ConnectionName;
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.save";

    /// <inheritdoc />
    public override double Width => 400;

    /// <summary>名字。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                RaisePropertyChanged(nameof(CanSave));
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>名字非空才能存。</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(_name);

    /// <summary>保存。</summary>
    public AsyncCommand SaveCommand { get; }

    private async Task SaveAsync()
    {
        string name = _name.Trim();
        Close();
        await _save(name).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Control CreateView() => new SaveQueryDialogView(this);
}
