using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「用 hint 对比」对话框的视图。</summary>
public sealed partial class HintCompareDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal HintCompareDialogView(HintCompareDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public HintCompareDialogView()
    {
        InitializeComponent();
    }
}
