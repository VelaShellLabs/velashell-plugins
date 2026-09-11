using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ComposePageView 的视图。</summary>
public sealed partial class ComposePageView : UserControl
{
    // 服务表的列拖拽走的是与另外四张表同一份通用件,这里只交代这一页的一个数:
    // 列宽之外那些不可拖的固定占位。左右各 14 的留白**不算** —— 那是列头 Border 的
    // Padding,量到的 Grid 宽度里本来就没有它;再减一次会让"还能拖多宽"凭空少 28。
    private readonly ColumnResizer _columns;

    /// <summary>建视图。</summary>
    public ComposePageView()
    {
        AvaloniaXamlLoader.Load(this);
        _columns = new(this, "ServicesHeader", chrome: 84);
    }
}
