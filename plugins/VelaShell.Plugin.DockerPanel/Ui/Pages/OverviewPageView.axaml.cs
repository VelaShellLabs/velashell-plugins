using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>总览页的视图。</summary>
public sealed partial class OverviewPageView : UserControl
{
    /// <summary>建视图。</summary>
    public OverviewPageView() => AvaloniaXamlLoader.Load(this);

    /// <summary>“需要关注”里那条的动作天生是个 Action,不必为它造一个 ICommand。</summary>
    private void OnAttentionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: AttentionItem item })
        {
            item.Action();
        }
    }
}
