using Avalonia.Controls;
using Avalonia.Input;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「上传到 GridFS」对话框视图。Ctrl+Enter 等同于点上传(metadata 编辑框里 Enter 是换行)。</summary>
public sealed partial class GridFsUploadDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal GridFsUploadDialogView(GridFsUploadDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || ReferenceEquals(e.Source, TargetBox))
                && viewModel.ConfirmCommand.CanExecute(null))
            {
                viewModel.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public GridFsUploadDialogView()
    {
        InitializeComponent();
    }
}
