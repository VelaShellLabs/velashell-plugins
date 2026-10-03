using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>名字对话框的视图(打开即把焦点与全选给输入框)。</summary>
public sealed partial class PipelineNameDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal PipelineNameDialogView(PipelineNameDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public PipelineNameDialogView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Dispatcher.UIThread.Post(() =>
        {
            _ = NameBox.Focus();
            NameBox.SelectAll();
        });
    }
}
