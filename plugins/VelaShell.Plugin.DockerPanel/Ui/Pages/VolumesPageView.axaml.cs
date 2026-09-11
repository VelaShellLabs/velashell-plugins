using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>VolumesPageView 的视图。</summary>
public sealed partial class VolumesPageView : UserControl
{
    // 列宽拖拽与抽屉布局都是四个页面共用的通用件,这里只交代这一页的一个数:
    // 列宽之外的固定占位(名称列左边 12 的留白 + 行尾动作 104)。
    private readonly ColumnResizer _columns;
    private readonly DrawerLayout _drawer;

    /// <summary>建视图。</summary>
    public VolumesPageView()
    {
        AvaloniaXamlLoader.Load(this);
        _columns = new(this, "HeaderGrid", chrome: 12 + 104 + 8);
        _drawer = new(this, "Root", "DrawerSplitter");
    }
}
