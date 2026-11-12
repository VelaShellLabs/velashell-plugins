using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>确认闸门的视图。</summary>
public sealed partial class ConfirmGateView : UserControl
{
    /// <summary>建视图。</summary>
    public ConfirmGateView() => AvaloniaXamlLoader.Load(this);
}
