using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 数据区右侧面板的收起(Navicat 右下角那颗按钮):挂在「内容 | 分隔条 | 面板」三列的 <see cref="Grid" /> 上,
/// 收起时最后一列宽度归零、分隔条与面板藏起来;展开时恢复收起前用户拖到的宽度。
/// </summary>
public static class SidePanel
{
    /// <summary>收起右侧面板。</summary>
    public static readonly AttachedProperty<bool> CollapsedProperty =
        AvaloniaProperty.RegisterAttached<Grid, bool>("Collapsed", typeof(SidePanel));

    private static readonly ConditionalWeakTable<Grid, Saved> SavedWidths = [];

    static SidePanel()
    {
        _ = CollapsedProperty.Changed.AddClassHandler<Grid>(static (grid, _) => Apply(grid));
    }

    /// <summary>读。</summary>
    public static bool GetCollapsed(Grid grid) => grid.GetValue(CollapsedProperty);

    /// <summary>写。</summary>
    public static void SetCollapsed(Grid grid, bool value) => grid.SetValue(CollapsedProperty, value);

    private static void Apply(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 3)
        {
            return;
        }
        int panel = grid.ColumnDefinitions.Count - 1;
        ColumnDefinition column = grid.ColumnDefinitions[panel];
        bool collapsed = GetCollapsed(grid);
        if (collapsed)
        {
            if (column.Width.Value > 0 || column.MinWidth > 0)
            {
                SavedWidths.AddOrUpdate(grid, new Saved(column.Width, column.MinWidth));
            }
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
        else if (SavedWidths.TryGetValue(grid, out Saved? saved))
        {
            column.Width = saved.Width;
            column.MinWidth = saved.MinWidth;
        }
        foreach (Control child in grid.Children)
        {
            if (Grid.GetColumn(child) >= panel - 1)
            {
                child.IsVisible = !collapsed;
            }
        }
    }

    private sealed record Saved(GridLength Width, double MinWidth);
}
