using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 网格视图:列头与行的水平滚动同步、拖列宽 / 双击分隔线自适应、点列头排序、当前格与内联编辑的键盘手势、
/// 双击对象 / 数组钻入与 Backspace 回退、右键菜单。
/// </summary>
public sealed partial class GridPaneView : UserControl
{
    private CollectionTabViewModel? _viewModel;
    private ScrollViewer? _rowsScroll;

    /// <summary>构造(由集合工作台视图在 XAML 里建,之后 <see cref="Attach" />)。</summary>
    public GridPaneView()
    {
        InitializeComponent();
        GridList.TemplateApplied += (_, e) =>
        {
            _rowsScroll = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
            if (_rowsScroll is not null)
            {
                _rowsScroll.ScrollChanged += (_, _) => HeaderScroll.Offset = new Vector(_rowsScroll.Offset.X, 0);
            }
        };
        GridList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        GridList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        GridList.AddHandler(Button.ClickEvent, OnChoicesClick);
        GridList.DoubleTapped += OnDoubleTapped;
        GridList.ContextRequested += OnContextRequested;
        GridList.SelectionChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.GridSelectedRows = [.. GridList.SelectedItems?.OfType<CollectionRow>() ?? []];
            }
        };
        // 分隔线上双击:按内容自动调列宽。
        ColumnFit.OnGripDoubleClick(HeaderItems, column =>
        {
            if (column is CollectionColumn target)
            {
                AutoFit(target);
            }
        });
    }

    /// <summary>行列表(测试用)。</summary>
    internal ListBox List => GridList;

    /// <summary>接上视图模型。</summary>
    internal void Attach(CollectionTabViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.RowAdded += row => GridList.ScrollIntoView(row);
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        // 钻入后列是嵌套字段,服务器端的排序管不到它们;子表就按数组原本的顺序看。
        if (_viewModel is { IsDrilled: false } vm && (sender as Control)?.DataContext is CollectionColumn column)
        {
            vm.SortByColumnCommand.Execute(column);
        }
    }

    private void OnResizeDelta(object? sender, VectorEventArgs e)
    {
        if ((sender as Control)?.DataContext is CollectionColumn column)
        {
            column.Width += e.Vector.X;
        }
    }

    /// <summary>
    /// 按内容自动调列宽:列头与当前已实现的行里这一列的内容,按不受限宽度量一遍取最大值。
    /// 只看已实现的行 —— 5 万行的页里逐格排版不值当,眼睛看到的就是这几十行。
    /// </summary>
    internal void AutoFit(CollectionColumn column)
    {
        double width = 0;
        foreach (Border header in HeaderItems.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Classes.Contains("hcell") && ReferenceEquals(b.DataContext, column)))
        {
            if (header.GetVisualDescendants().OfType<Button>().FirstOrDefault() is { Content: Control content } button)
            {
                width = Math.Max(width, ColumnFit.Natural(content) + button.Padding.Left + button.Padding.Right + header.BorderThickness.Right);
            }
        }
        foreach (Border cell in GridList.GetVisualDescendants().OfType<Border>()
                     .Where(b => b.Classes.Contains("gcell") && b.DataContext is CollectionCell c && ReferenceEquals(c.Column, column)))
        {
            if (cell.Child is Panel { Children: [Control text, ..] })
            {
                width = Math.Max(width, ColumnFit.Natural(text) + 16 + 2);
            }
        }
        if (width > 0)
        {
            column.Width = Math.Ceiling(width) + 4;
        }
    }

    /// <summary>按下哪一格,哪一格就是当前格(行由 ListBox 自己选)。</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is not null && Find<CollectionCell>(e.Source) is { } cell)
        {
            _viewModel.CurrentColumn = cell.Column;
        }
    }

    /// <summary>双击:对象 / 数组钻进去(Navicat 式),其余开始内联编辑。</summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is not { } vm || Find<CollectionCell>(e.Source) is not { IsEditing: false } cell)
        {
            return;
        }
        if (cell.IsContainer && vm.DrillInto(cell))
        {
            FocusGrid();
            return;
        }
        vm.BeginCellEdit(cell);
    }

    /// <summary>
    /// 键盘:编辑中 Enter 写入 / Esc 取消 / Tab 写入并编辑下一格;
    /// 不在编辑时 Enter 钻进对象 / 数组、其余与 F2 一样编辑当前格,← → 换当前列,
    /// Backspace / Alt+← 回上一层,Del 暂存删除选中行(子表里是删除元素),Ctrl+C 复制当前格的值。
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        if (e.Source is TextBox && Find<CollectionCell>(e.Source) is { IsEditing: true } editing)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    if (vm.CommitCellEdit(editing))
                    {
                        FocusRow(editing.Row);
                    }
                    break;
                case Key.Escape:
                    e.Handled = true;
                    CollectionTabViewModel.CancelCellEdit(editing);
                    FocusRow(editing.Row);
                    break;
                case Key.Tab:
                    e.Handled = true;
                    if (vm.CommitCellEdit(editing))
                    {
                        MoveAndEdit(editing.Row, editing.Column, e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                    }
                    break;
            }
            return;
        }
        if (e.Source is TextBox)
        {
            return;
        }
        if (vm.IsDrilled && (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None
                             || e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt))
        {
            e.Handled = true;
            vm.DrillBack();
            FocusGrid();
            return;
        }
        if (vm.GridSelectedRow is not { } row)
        {
            return;
        }
        CollectionCell? current = vm.CurrentColumn is { } column ? row.CellOf(column) : null;
        switch (e.Key)
        {
            case Key.Enter when current is { IsContainer: true } && e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                if (vm.DrillInto(current))
                {
                    FocusGrid();
                }
                break;
            case Key.Enter or Key.F2 when current is not null && e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                vm.BeginCellEdit(current);
                break;
            case Key.Left or Key.Right when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                MoveCurrent(e.Key == Key.Left ? -1 : 1);
                break;
            case Key.Delete:
                e.Handled = true;
                vm.DeleteCommand.Execute(null);
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && current is not null:
                e.Handled = true;
                _ = vm.CopyValueAsync(current.Value);
                break;
        }
    }

    private void MoveCurrent(int step)
    {
        if (_viewModel is not { } vm || vm.GridColumns.Count == 0)
        {
            return;
        }
        int index = vm.CurrentColumn is { } column ? IndexOf(vm.GridColumns, column) : -1;
        vm.CurrentColumn = vm.GridColumns[Math.Clamp(index + step, 0, vm.GridColumns.Count - 1)];
    }

    private void MoveAndEdit(CollectionRow row, CollectionColumn column, int step)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        int index = IndexOf(vm.GridColumns, column) + step;
        if (index < 0 || index >= vm.GridColumns.Count)
        {
            FocusRow(row);
            return;
        }
        CollectionRow current = vm.GridRows.FirstOrDefault(r => ReferenceEquals(r, row)) ?? row;
        if (current.CellOf(vm.GridColumns[index]) is { } next)
        {
            vm.BeginCellEdit(next);
        }
    }

    private static int IndexOf(IReadOnlyList<CollectionColumn> columns, CollectionColumn column)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (ReferenceEquals(columns[i], column))
            {
                return i;
            }
        }
        return -1;
    }

    private void FocusRow(CollectionRow row)
    {
        if (GridList.ContainerFromItem(row) is Control container)
        {
            container.Focus();
        }
        else
        {
            GridList.Focus();
        }
    }

    /// <summary>换层之后把焦点放回选中行(子表的行是新建的容器,键盘焦点不会自己过去)。</summary>
    private void FocusGrid()
    {
        if (_viewModel?.GridSelectedRow is { } row)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusRow(row), Avalonia.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>编辑器右侧的下拉箭头:候选值(布尔、枚举列、日期的"现在"),点一个即写入。</summary>
    private void OnChoicesClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Classes: var classes } button || !classes.Contains("choices")
            || _viewModel is not { } vm || Find<CollectionCell>(button) is not { Editor: { } editor } cell)
        {
            return;
        }
        e.Handled = true;
        CollectionMenus.Open(button, editor.Choices.Select(choice => (Control)CollectionMenus.Item(choice, "Mongo.type", null, () =>
        {
            editor.Text = choice;
            if (vm.CommitCellEdit(cell))
            {
                FocusRow(cell.Row);
            }
        })));
    }

    /// <summary>
    /// 单元格右键菜单:展开(对象 / 数组)| 复制值 / 复制路径 / 复制为 JSON | 按此值筛选 / 在文档编辑器中编辑 |
    /// 设为 null / 删除字段 / 删除文档(子表里是删除元素),底部是「筛选将生成 …」。
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        CollectionCell? cell = Find<CollectionCell>(e.Source);
        CollectionRow? row = cell?.Row ?? Find<CollectionRow>(e.Source);
        if (row is null)
        {
            return;
        }
        if (!ReferenceEquals(vm.GridSelectedRow, row) && !vm.GridSelectedRows.Contains(row))
        {
            vm.GridSelectedRow = row;
        }
        if (cell is not null)
        {
            vm.CurrentColumn = cell.Column;
        }
        Loc loc = vm.Loc;
        bool writable = vm.IsEditable && !row.IsDeleted;
        // 子表里的筛选去掉数组下标:按 items.2.sku 筛只会命中"第 3 项恰好是它"的文档。
        string? filterPath = cell is null ? null : row.Drill is null ? cell.Path : GridDrill.FilterPath(cell.Path);
        var items = new List<Control>();
        if (cell is { IsContainer: true })
        {
            items.Add(CollectionMenus.Item(loc["Cw_MenuDrill"], "Mongo.table-2", "Enter", () =>
            {
                if (vm.DrillInto(cell))
                {
                    FocusGrid();
                }
            }, accent: true));
            items.Add(CollectionMenus.Separator());
        }
        if (cell is not null)
        {
            string path = cell.Path;
            BsonValue? value = cell.Value;
            items.Add(CollectionMenus.Item(loc["Cw_MenuCopyValue"], "Mongo.copy", "Ctrl+C", () => _ = vm.CopyValueAsync(value)));
            items.Add(CollectionMenus.Item(loc["Cw_MenuCopyPath"], "Mongo.route", path, () => _ = vm.CopyTextAsync(path)));
            items.Add(CollectionMenus.Item(loc["Cw_MenuCopyJson"], "Mongo.braces", null, () => _ = vm.CopyValueAsJsonAsync(value)));
            items.Add(CollectionMenus.Separator());
            items.Add(CollectionMenus.Item(loc["Cw_MenuFilterBy"], "Mongo.funnel", null, () => _ = vm.FilterByAsync(filterPath!, value)));
        }
        items.Add(CollectionMenus.Item(loc["Cw_MenuOpenEditor"], "Mongo.file-pen-line", null, () => vm.OpenInEditor(row.Root), enabled: vm.IsEditable));
        items.Add(CollectionMenus.Separator());
        if (cell is not null && cell.Path != "_id" && !cell.Column.IsElementValue)
        {
            string path = cell.Path;
            items.Add(CollectionMenus.Item(loc["Cw_MenuSetNull"], "Mongo.circle-dashed", null, () => vm.Stage(row, path, BsonNull.Value),
                enabled: writable && !cell.IsNull));
            items.Add(CollectionMenus.Item(loc["Cw_MenuDeleteField"], "Mongo.eraser", null, () => vm.DeleteField(row, path),
                enabled: writable && !cell.IsMissing));
        }
        if (row.Drill is { IsArray: true })
        {
            items.Add(CollectionMenus.Item(loc["Cw_MenuDeleteElement"], "Mongo.trash-2", "Del", () => vm.DeleteCommand.Execute(null),
                danger: true, enabled: writable));
        }
        else if (row.Drill is null)
        {
            items.Add(CollectionMenus.Item(row.IsDeleted ? loc["Cw_MenuUndelete"] : loc["Cw_MenuDeleteDoc"], "Mongo.trash-2", "Del",
                () => vm.StageDelete(vm.SelectedRows.Contains(row) ? vm.SelectedRows : [row]), danger: true, enabled: vm.IsEditable));
        }
        if (cell is not null)
        {
            items.Add(CollectionMenus.FilterHint(loc, filterPath!, cell.Value, () => _ = vm.FilterByAsync(filterPath!, cell.Value)));
        }
        CollectionMenus.Open(e.Source as Control ?? GridList, items);
        e.Handled = true;
    }

    /// <summary>从事件源往上找第一个数据上下文是 <typeparamref name="T" /> 的控件。</summary>
    internal static T? Find<T>(object? source) where T : class
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: T data })
            {
                return data;
            }
        }
        return null;
    }
}
