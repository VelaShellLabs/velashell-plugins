using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>PullImageView 的视图。</summary>
public sealed partial class PullImageView : UserControl
{
    /// <summary>建视图。</summary>
    public PullImageView() => AvaloniaXamlLoader.Load(this);
}
