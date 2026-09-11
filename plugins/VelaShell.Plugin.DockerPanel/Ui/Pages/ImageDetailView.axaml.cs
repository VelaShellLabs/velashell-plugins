using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ImageDetailView 的视图。</summary>
public sealed partial class ImageDetailView : UserControl
{
    /// <summary>建视图。</summary>
    public ImageDetailView() => AvaloniaXamlLoader.Load(this);
}
