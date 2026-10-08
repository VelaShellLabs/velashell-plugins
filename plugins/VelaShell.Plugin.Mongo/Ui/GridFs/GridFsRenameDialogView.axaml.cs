using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>重命名对话框视图:打开即聚焦输入框并选中"最后一段"(只改文件名、不动目录是最常见的情况)。</summary>
public sealed partial class GridFsRenameDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal GridFsRenameDialogView(GridFsRenameDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            _ = NameBox.Focus();
            string text = NameBox.Text ?? "";
            int slash = text.LastIndexOf('/');
            int dot = viewModel.Entry.IsFolder ? -1 : text.LastIndexOf('.');
            NameBox.SelectionStart = slash + 1;
            NameBox.SelectionEnd = dot > slash + 1 ? dot : text.Length;
        });
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.ConfirmCommand.CanExecute(null))
            {
                viewModel.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public GridFsRenameDialogView()
    {
        InitializeComponent();
    }
}
