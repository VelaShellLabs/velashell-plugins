using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>LogsView 的视图。</summary>
public sealed partial class LogsView : UserControl
{
    /// <summary>建视图。</summary>
    public LogsView() => AvaloniaXamlLoader.Load(this);
}
