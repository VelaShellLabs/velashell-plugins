using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>表单弹窗的通用外壳视图。</summary>
public sealed partial class PanelFormView : UserControl
{
    /// <summary>建视图。</summary>
    public PanelFormView() => AvaloniaXamlLoader.Load(this);
}
