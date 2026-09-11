using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ContainerDetailView 的视图。</summary>
public sealed partial class ContainerDetailView : UserControl
{
    /// <summary>建视图。</summary>
    public ContainerDetailView() => AvaloniaXamlLoader.Load(this);
}
