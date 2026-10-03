using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>缩放预览对话框视图。</summary>
public sealed partial class GridFsImageDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal GridFsImageDialogView(GridFsImageDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public GridFsImageDialogView()
    {
        InitializeComponent();
    }
}
