using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>SystemPageView 的视图。</summary>
public sealed partial class SystemPageView : UserControl
{
    /// <summary>建视图。</summary>
    public SystemPageView() => AvaloniaXamlLoader.Load(this);
}
