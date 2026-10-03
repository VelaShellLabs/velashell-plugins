using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>"新密码只显示一次"对话框视图。</summary>
public sealed partial class RevealPasswordDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal RevealPasswordDialogView(RevealPasswordDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public RevealPasswordDialogView()
    {
        InitializeComponent();
    }
}
