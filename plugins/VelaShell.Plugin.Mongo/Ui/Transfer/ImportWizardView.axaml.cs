using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>视图(占位)。</summary>
public sealed partial class ImportWizardView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal ImportWizardView(ImportWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ImportWizardView()
    {
        InitializeComponent();
    }
}
