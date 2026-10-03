using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>新建存储桶对话框视图:打开即聚焦名字;Enter 等同于点新建。</summary>
public sealed partial class GridFsNewBucketDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal GridFsNewBucketDialogView(GridFsNewBucketDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus());
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.CreateCommand.CanExecute(null))
            {
                viewModel.CreateCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public GridFsNewBucketDialogView()
    {
        InitializeComponent();
    }
}
