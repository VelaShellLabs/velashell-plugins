using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>
/// 容器终端的视图。
/// <para>
/// 代码后置是空的:键盘、选区、IME、鼠标上报全归宿主那个终端控件管 ——
/// 早先这里有一段"回车执行、上下键翻历史"的逻辑,那是行式控制台的需要;
/// 真终端里这三个键属于远端的 shell,面板拦下来反而是错的。
/// </para>
/// </summary>
public sealed partial class ContainerTerminalView : UserControl
{
    /// <summary>建视图。</summary>
    public ContainerTerminalView() => AvaloniaXamlLoader.Load(this);
}
