using Avalonia.Controls;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「添加字段」对话框的视图(打开即聚焦名字框)。</summary>
public sealed partial class AddStagedFieldDialogView : UserControl
{
    /// <summary>构造。</summary>
    public AddStagedFieldDialogView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Input);
    }
}
