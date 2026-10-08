using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>确认框视图。要手打名称时,焦点直接落在输入框里;Enter 在可确认时等同于点确认。</summary>
public sealed partial class ConfirmDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal ConfirmDialogView(ConfirmDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.RequiresTyping)
            {
                _ = TypedBox.Focus();
            }
            else
            {
                _ = ConfirmButton.Focus();
            }
        });
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.CanConfirm)
            {
                viewModel.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ConfirmDialogView()
    {
        InitializeComponent();
    }
}
