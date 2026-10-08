using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>重置密码对话框视图。打开即聚焦新密码框;Enter 在可保存时等同于点保存。</summary>
public sealed partial class ResetPasswordDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal ResetPasswordDialogView(ResetPasswordDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => PasswordBox.Focus());
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.CanApply)
            {
                viewModel.ApplyCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ResetPasswordDialogView()
    {
        InitializeComponent();
    }
}
