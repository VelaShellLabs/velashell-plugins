using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>视图(占位)。</summary>
public sealed partial class ConflictDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal ConflictDialogView(ConflictDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ConflictDialogView()
    {
        InitializeComponent();
    }
}
