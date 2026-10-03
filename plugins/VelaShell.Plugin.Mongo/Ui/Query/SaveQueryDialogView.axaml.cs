using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「保存查询」对话框的视图。</summary>
public sealed partial class SaveQueryDialogView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal SaveQueryDialogView(SaveQueryDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        NameBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && viewModel.CanSave)
            {
                viewModel.SaveCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public SaveQueryDialogView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus();
        NameBox.SelectAll();
    }
}
