using System.ComponentModel;
using Avalonia.Controls;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 把 <see cref="DrawerState" />(开着没有、最大化没有、多宽)翻译成外层那三列的宽度。
/// 四个带抽屉的页面共用这一份。
/// <para>
/// 列定义由代码管而不是绑上去:分割条改的就是列定义本身,再往上套一层绑定只会两边打架 ——
/// 宿主的侧栏与文件面板也是这么写的。
/// </para>
/// </summary>
public sealed class DrawerLayout
{
    private static readonly GridLength Track = new(5);
    private static readonly GridLength Collapsed = new(0);
    private static readonly GridLength Fill = new(1, GridUnitType.Star);

    private readonly Control _host;
    private readonly Grid _root;
    private readonly double _listReserve;
    private DrawerState? _drawer;
    private double _hostWidth;

    /// <summary>接上一个页面的外层布局。</summary>
    /// <param name="host">页面视图。</param>
    /// <param name="rootName">外层那张三列 Grid 的名字(列表 / 5px 分割条 / 抽屉)。</param>
    /// <param name="splitterName">分割条的名字。</param>
    /// <param name="listReserve">
    /// 抽屉再宽也要给列表留下的宽度。抽屉比面板还宽的话,它的头(还原 / 关闭)会被顶出可视区,
    /// 而那是回到列表的唯一入口;抽屉与列表同屏对照,本来也是这个布局存在的理由。
    /// </param>
    public DrawerLayout(Control host, string rootName, string splitterName, double listReserve = 360)
    {
        _host = host;
        _root = host.GetControl<Grid>(rootName);
        _listReserve = listReserve;
        host.GetControl<GridSplitter>(splitterName).DragCompleted += (_, _) => Capture();
        host.DataContextChanged += (_, _) => Rebind();
        host.SizeChanged += (_, e) =>
        {
            _hostWidth = e.NewSize.Width;
            Apply();
        };
        Rebind();
    }

    private void Rebind()
    {
        if (_drawer is { } previous)
        {
            previous.PropertyChanged -= OnDrawerChanged;
        }
        _drawer = (_host.DataContext as PageViewModel)?.Drawer;
        if (_drawer is { } drawer)
        {
            drawer.PropertyChanged += OnDrawerChanged;
        }
        Apply();
    }

    private void OnDrawerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DrawerState.IsOpen) or nameof(DrawerState.Maximized)
            or nameof(DrawerState.Width))
        {
            Apply();
        }
    }

    private void Apply()
    {
        if (_drawer is not { } drawer || _root.ColumnDefinitions.Count < 3)
        {
            return;
        }
        ColumnDefinition list = _root.ColumnDefinitions[0];
        ColumnDefinition splitter = _root.ColumnDefinitions[1];
        ColumnDefinition panel = _root.ColumnDefinitions[2];

        if (!drawer.IsOpen)
        {
            list.Width = Fill;
            splitter.Width = Collapsed;
            panel.Width = Collapsed;
            panel.MinWidth = 0;
            return;
        }
        if (drawer.Maximized)
        {
            list.Width = Collapsed;
            splitter.Width = Collapsed;
            panel.MinWidth = 0;
            panel.MaxWidth = double.PositiveInfinity;
            panel.Width = Fill;
            return;
        }
        double max = Math.Max(DrawerState.MinWidth, _hostWidth - _listReserve);
        list.Width = Fill;
        splitter.Width = Track;
        panel.MinWidth = DrawerState.MinWidth;
        panel.MaxWidth = max;
        panel.Width = new(Math.Clamp(drawer.Width, DrawerState.MinWidth, max));
    }

    /// <summary>拖完把拖出来的实际宽度写回去 —— 与宿主侧栏那两条分割条一样。</summary>
    private void Capture()
    {
        if (_drawer is { } drawer && _root.ColumnDefinitions.Count >= 3 &&
            _root.ColumnDefinitions[2].ActualWidth > 0)
        {
            drawer.Width = _root.ColumnDefinitions[2].ActualWidth;
        }
    }
}
