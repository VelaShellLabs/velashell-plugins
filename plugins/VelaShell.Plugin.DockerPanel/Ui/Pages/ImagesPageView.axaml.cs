using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>ImagesPageView 的视图。</summary>
public sealed partial class ImagesPageView : UserControl
{
    // 列宽拖拽与抽屉布局都是四个页面共用的通用件,这里只交代这一页的一个数:
    // 列宽之外的固定占位(勾选框 26 + 行尾动作 120)。
    private readonly ColumnResizer _columns;
    private readonly DrawerLayout _drawer;

    /// <summary>建视图。</summary>
    public ImagesPageView()
    {
        AvaloniaXamlLoader.Load(this);
        _columns = new(this, "HeaderGrid", chrome: 26 + 120 + 8);
        _drawer = new(this, "Root", "DrawerSplitter");
    }

    /// <summary>
    /// 点整行 = 打开详情抽屉。行尾那几颗动作按钮的 Tapped 会一路冒泡到这里,
    /// 所以先看事件是不是从某个按钮里出来的,是就让开。
    /// </summary>
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source &&
            (source.FindAncestorOfType<Button>(true) is not null ||
             source.FindAncestorOfType<CheckBox>(true) is not null))
        {
            return;
        }
        if (sender is Control { DataContext: ImageRow row } && DataContext is ImagesPageViewModel page)
        {
            page.OpenDetailCommand.Execute(row);
        }
    }
}
