using Avalonia.Controls;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 重命名对话框(文件或虚拟目录)。
/// <para>
/// 文件改名改的是**同名的全部版本**;目录改名改的是前缀下的全部文件 —— 两种情况下影响多少份都写在框里。
/// 名字里带 <c>/</c> 即"移到别的目录":GridFS 的目录本来就只是文件名的前缀。
/// 校验(重名、移进自己)要查库,所以确认是异步的:出错就留在框里显示,成功才关。
/// </para>
/// </summary>
internal sealed class GridFsRenameDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly Func<string, Task<string?>> _apply;
    private string _name;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="entry">要改名的行。</param>
    /// <param name="affected">受影响的份数(文件的版本数 / 目录下的文件数)。</param>
    /// <param name="apply">执行改名;返回错误文案,成功为 <see langword="null" />。</param>
    public GridFsRenameDialogViewModel(IMongoWorkspace workspace, GridFsEntry entry, long affected, Func<string, Task<string?>> apply)
        : base(workspace)
    {
        _apply = apply;
        Entry = entry;
        Title = Loc["Fs_RenameTitle"];
        Subtitle = entry.Path;
        _name = entry.IsFolder ? entry.Path.TrimEnd('/') : entry.Path;
        Hint = entry.IsFolder
            ? Loc.Format("Fs_RenameHintFolder", BsonText.Grouped(affected))
            : Loc.Format("Fs_RenameHintFile", BsonText.Grouped(affected));
        ConfirmCommand = new AsyncCommand(ConfirmAsync, () => _name.Trim().Length > 0);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.pencil-line";

    /// <inheritdoc />
    public override double Width => 440;

    /// <summary>要改名的行。</summary>
    public GridFsEntry Entry { get; }

    /// <summary>新名字(完整路径)。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value ?? ""))
            {
                Error = null;
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>影响范围说明。</summary>
    public string Hint { get; }

    /// <summary>错误(重名、移进自己…)。</summary>
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

    /// <summary>有错误。</summary>
    public bool HasError => Error is not null;

    /// <summary>重命名。</summary>
    public AsyncCommand ConfirmCommand { get; }

    private async Task ConfirmAsync()
    {
        string? error = await _apply(_name).ConfigureAwait(true);
        if (error is null)
        {
            Close();
        }
        else
        {
            Error = error;
        }
    }

    /// <inheritdoc />
    public Control CreateView() => new GridFsRenameDialogView(this);
}
