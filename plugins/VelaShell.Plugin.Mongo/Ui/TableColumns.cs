using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 表格列宽:列头一个 <see cref="Grid" />、每一行各一个 <see cref="Grid" />,<c>ColumnDefinitions</c> 写成一样,
/// 再挂上同一个组名 —— 列头那一个用 <c>ui:TableColumns.Header</c>,行用 <c>ui:TableColumns.Row</c>。
/// <list type="bullet">
/// <item>列头的每条列间分隔线上自动放一个拖把(<c>MongoColumnGrip</c>):拖动改宽,双击按内容自适应;</item>
/// <item>改动同步到同组的每一行(包括之后才被虚拟化列表实现出来的行)。</item>
/// </list>
/// 组按所在的 <see cref="UserControl" /> 隔开:两个标签页里同名的表各管各的。
/// 用户没动过的列保持声明时的样子(<c>*</c> 列照旧填满剩余宽度)。
/// </summary>
public static class TableColumns
{
    /// <summary>列头那个 Grid 的组名。</summary>
    public static readonly AttachedProperty<string?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<Grid, string?>("Header", typeof(TableColumns));

    /// <summary>行里那个 Grid 的组名。</summary>
    public static readonly AttachedProperty<string?> RowProperty =
        AvaloniaProperty.RegisterAttached<Grid, string?>("Row", typeof(TableColumns));

    /// <summary>列头上不放拖把的列(逗号分隔的下标,如勾选框列、图标列)。</summary>
    public static readonly AttachedProperty<string?> FixedProperty =
        AvaloniaProperty.RegisterAttached<Grid, string?>("Fixed", typeof(TableColumns));

    private static readonly ConditionalWeakTable<Control, Dictionary<string, Group>> Scopes = [];

    static TableColumns()
    {
        _ = HeaderProperty.Changed.AddClassHandler<Grid>((grid, e) => Hook(grid, e.NewValue as string, isHeader: true));
        _ = RowProperty.Changed.AddClassHandler<Grid>((grid, e) => Hook(grid, e.NewValue as string, isHeader: false));
    }

    /// <summary>取列头组名。</summary>
    public static string? GetHeader(Grid grid) => grid.GetValue(HeaderProperty);

    /// <summary>设列头组名。</summary>
    public static void SetHeader(Grid grid, string? value) => grid.SetValue(HeaderProperty, value);

    /// <summary>取行组名。</summary>
    public static string? GetRow(Grid grid) => grid.GetValue(RowProperty);

    /// <summary>设行组名。</summary>
    public static void SetRow(Grid grid, string? value) => grid.SetValue(RowProperty, value);

    /// <summary>取不放拖把的列。</summary>
    public static string? GetFixed(Grid grid) => grid.GetValue(FixedProperty);

    /// <summary>设不放拖把的列。</summary>
    public static void SetFixed(Grid grid, string? value) => grid.SetValue(FixedProperty, value);

    /// <summary>某个 Grid 所在的组(测试用);还没挂上可视树为 <see langword="null" />。</summary>
    internal static Group? GroupOf(Grid grid)
    {
        string? name = GetHeader(grid) ?? GetRow(grid);
        return name is not null && ScopeOf(grid) is { } scope && Scopes.TryGetValue(scope, out Dictionary<string, Group>? groups)
               && groups.TryGetValue(name, out Group? group)
            ? group
            : null;
    }

    private static void Hook(Grid grid, string? name, bool isHeader)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }
        grid.AttachedToVisualTree += (_, _) => Join(grid, name, isHeader);
        grid.DetachedFromVisualTree += (_, _) => Leave(grid);
        if (grid.IsAttachedToVisualTree())
        {
            Join(grid, name, isHeader);
        }
    }

    private static Control? ScopeOf(Grid grid) => (Control?)grid.FindAncestorOfType<UserControl>() ?? TopLevel.GetTopLevel(grid);

    private static void Join(Grid grid, string name, bool isHeader)
    {
        if (ScopeOf(grid) is not { } scope)
        {
            return;
        }
        Dictionary<string, Group> groups = Scopes.GetOrCreateValue(scope);
        if (!groups.TryGetValue(name, out Group? group))
        {
            group = new Group();
            groups[name] = group;
        }
        group.Add(grid, isHeader);
    }

    private static void Leave(Grid grid)
    {
        foreach (Group group in AllGroupsOf(grid))
        {
            group.Remove(grid);
        }
    }

    private static IEnumerable<Group> AllGroupsOf(Grid grid)
    {
        string? name = GetHeader(grid) ?? GetRow(grid);
        if (name is null)
        {
            yield break;
        }
        foreach (KeyValuePair<Control, Dictionary<string, Group>> scope in Scopes)
        {
            if (scope.Value.TryGetValue(name, out Group? group) && group.Contains(grid))
            {
                yield return group;
            }
        }
    }

    /// <summary>一组共享列宽的 Grid。</summary>
    internal sealed class Group
    {
        private readonly Dictionary<int, double> _widths = [];
        private readonly List<WeakReference<Grid>> _members = [];
        private WeakReference<Grid>? _header;

        /// <summary>用户改过的列宽(下标 → 像素)。</summary>
        public IReadOnlyDictionary<int, double> Widths => _widths;

        /// <summary>列头。</summary>
        public Grid? Header => _header is not null && _header.TryGetTarget(out Grid? grid) ? grid : null;

        /// <summary>还活着的成员(列头 + 行)。</summary>
        public IEnumerable<Grid> Members
        {
            get
            {
                _ = _members.RemoveAll(static w => !w.TryGetTarget(out _));
                foreach (WeakReference<Grid> member in _members)
                {
                    if (member.TryGetTarget(out Grid? grid))
                    {
                        yield return grid;
                    }
                }
            }
        }

        public bool Contains(Grid grid) => Members.Any(m => ReferenceEquals(m, grid));

        public void Add(Grid grid, bool isHeader)
        {
            if (!Contains(grid))
            {
                _members.Add(new WeakReference<Grid>(grid));
            }
            if (isHeader)
            {
                _header = new WeakReference<Grid>(grid);
                AddGrips(grid);
            }
            Apply(grid);
        }

        public void Remove(Grid grid) => _members.RemoveAll(w => !w.TryGetTarget(out Grid? g) || ReferenceEquals(g, grid));

        /// <summary>把第 <paramref name="column" /> 列设成 <paramref name="width" /> 像素,同步到每个成员。</summary>
        public void Set(int column, double width)
        {
            _widths[column] = Math.Round(Math.Clamp(width, 24, 1600));
            foreach (Grid grid in Members)
            {
                Apply(grid);
            }
        }

        /// <summary>按内容自适应第 <paramref name="column" /> 列:量每个成员这一列里内容的自然宽度,取最大。</summary>
        public void AutoFit(int column)
        {
            double width = 0;
            foreach (Grid grid in Members)
            {
                foreach (Control child in grid.Children)
                {
                    if (child is Thumb || !child.IsVisible || Grid.GetColumn(child) != column || Grid.GetColumnSpan(child) != 1)
                    {
                        continue;
                    }
                    width = Math.Max(width, ColumnFit.Natural(child));
                }
            }
            if (width > 0)
            {
                Set(column, Math.Ceiling(width) + 8);
            }
        }

        private void Apply(Grid grid)
        {
            foreach ((int column, double width) in _widths)
            {
                if (column < grid.ColumnDefinitions.Count)
                {
                    grid.ColumnDefinitions[column].Width = new GridLength(width);
                }
            }
        }

        private void AddGrips(Grid header)
        {
            if (header.Children.OfType<Thumb>().Any(static t => t.Classes.Contains("tablegrip")))
            {
                return;
            }
            HashSet<int> fixedColumns =
            [
                .. (GetFixed(header) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static s => int.TryParse(s, out int i) ? i : -1)
            ];
            ControlTheme? theme = header.TryFindResource("MongoColumnGrip", out object? found) ? found as ControlTheme : null;
            // 最后一列多半是 * 列,右边没有可拖的分隔线;是固定宽度时也给一个(拖宽之后表就比视口宽,横向滚动)。
            int count = header.ColumnDefinitions.Count;
            for (int i = 0; i < count; i++)
            {
                if (fixedColumns.Contains(i) || (i == count - 1 && header.ColumnDefinitions[i].Width.IsStar))
                {
                    continue;
                }
                int column = i;
                var grip = new Thumb { Classes = { "tablegrip" }, Tag = column };
                if (theme is not null)
                {
                    grip.Theme = theme;
                }
                Grid.SetColumn(grip, column);
                grip.DragDelta += (_, e) =>
                {
                    double current = header.ColumnDefinitions[column].ActualWidth;
                    Set(column, current + e.Vector.X);
                };
                grip.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
                {
                    if (e.ClickCount == 2)
                    {
                        e.Handled = true;
                        AutoFit(column);
                    }
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                header.Children.Add(grip);
            }
        }
    }
}
