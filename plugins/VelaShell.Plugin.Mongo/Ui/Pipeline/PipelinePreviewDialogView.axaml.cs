using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>阶段输出预览"看全部"的视图。</summary>
public sealed partial class PipelinePreviewDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal PipelinePreviewDialogView(PipelinePreviewDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public PipelinePreviewDialogView()
    {
        InitializeComponent();
    }
}
