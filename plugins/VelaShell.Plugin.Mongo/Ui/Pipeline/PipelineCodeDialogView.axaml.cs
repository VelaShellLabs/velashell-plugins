using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「导出为代码」对话框的视图。</summary>
public sealed partial class PipelineCodeDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal PipelineCodeDialogView(PipelineCodeDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public PipelineCodeDialogView()
    {
        InitializeComponent();
    }
}
