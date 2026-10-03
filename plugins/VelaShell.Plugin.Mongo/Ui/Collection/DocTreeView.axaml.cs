using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 树视图:点箭头 / 双击文档行展开,Enter 编辑值、Ctrl+D 添加字段、Del 删除字段、Ctrl+C 复制值,
/// 右键菜单与设计稿 13 逐项一致(底部「筛选将生成 …」)。修改走与网格同一个暂存区。
/// </summary>
public sealed partial class DocTreeView : UserControl
{
    private CollectionTabViewModel? _viewModel;

    /// <summary>构造。</summary>
    public DocTreeView()
    {
        InitializeComponent();
        TreeList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        TreeList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        TreeList.AddHandler(Button.ClickEvent, OnDatePickClick);
        TreeList.DoubleTapped += OnDoubleTapped;
        TreeList.ContextRequested += OnContextRequested;
    }

    /// <summary>树列表(测试用)。</summary>
    internal ListBox List => TreeList;

    /// <summary>接上视图模型。</summary>
    internal void Attach(CollectionTabViewModel viewModel) => _viewModel = viewModel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        for (var visual = e.Source as Avalonia.Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
        {
            if (visual is Panel { Classes: var classes } panel && classes.Contains("chevron") && panel.DataContext is DocTreeRow row)
            {
                e.Handled = true;
                _viewModel.SelectedTreeRow = row;
                _viewModel.ToggleTree(row);
                return;
            }
            if (visual is ListBoxItem)
            {
                return;
            }
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || GridPaneView.Find<DocTreeRow>(e.Source) is not { IsEditing: false } row)
        {
            return;
        }
        if (row.IsExpandable)
        {
            _viewModel.ToggleTree(row);
        }
        else
        {
            BeginEdit(row);
        }
    }

    private void BeginEdit(DocTreeRow row)
    {
        if (_viewModel is not { } vm || row.IsDocument || row.Row.IsDeleted)
        {
            return;
        }
        if (row.Path == "_id" && row.Row.Insert is null)
        {
            vm.Workspace.Toast(new() { Title = vm.Loc["Cw_IdImmutable"], Kind = ToastKind.Warning });
            return;
        }
        if (!vm.EnsureCanWrite())
        {
            return;
        }
        foreach (DocTreeRow other in vm.TreeRows.Where(static r => r.IsEditing))
        {
            other.Editor = null;
        }
        vm.SelectedTreeRow = row;
        row.Editor = vm.CreateEditor(row.Value, row.Path, vm.Sample.KindOf(row.Path));
    }

    /// <summary>日期值编辑框右边的日历按钮:选一天 / 改时刻即写入暂存区。</summary>
    private void OnDatePickClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Classes: var classes } button || !classes.Contains("datepick")
            || _viewModel is not { } vm || GridPaneView.Find<DocTreeRow>(button) is not { Editor: { } editor } row)
        {
            return;
        }
        e.Handled = true;
        _ = DatePickFlyout.Show(button, vm.Loc, editor.Text, text =>
        {
            editor.Text = text;
            if (vm.CommitEditor(editor, row.Row, row.Path))
            {
                row.Editor = null;
                _ = TreeList.Focus();
            }
        });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        if (e.Source is TextBox && GridPaneView.Find<DocTreeRow>(e.Source) is { IsEditing: true, Editor: { } editor } editing)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (vm.CommitEditor(editor, editing.Row, editing.Path))
                {
                    editing.Editor = null;
                    _ = TreeList.Focus();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                editing.Editor = null;
                _ = TreeList.Focus();
            }
            return;
        }
        if (e.Source is TextBox || vm.SelectedTreeRow is not { } row)
        {
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter or Key.F2 when !control:
                e.Handled = true;
                if (row.IsExpandable && e.Key == Key.Enter)
                {
                    vm.ToggleTree(row);
                }
                else
                {
                    BeginEdit(row);
                }
                break;
            case Key.Right when row.IsExpandable && !row.IsExpanded:
            case Key.Left when row.IsExpandable && row.IsExpanded:
                e.Handled = true;
                vm.ToggleTree(row);
                break;
            case Key.D when control:
                e.Handled = true;
                vm.PromptAddField(row.Row, ContainerFor(row));
                break;
            case Key.C when control:
                e.Handled = true;
                _ = vm.CopyValueAsync(row.IsDocument ? row.Row.Document : row.Value);
                break;
            case Key.Delete:
                e.Handled = true;
                if (row.IsDocument)
                {
                    vm.StageDelete([row.Row]);
                }
                else if (row.Path != "_id")
                {
                    vm.DeleteField(row.Row, row.Path);
                }
                break;
        }
    }

    /// <summary>右键菜单(设计稿 13 逐项)。</summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_viewModel is not { } vm || GridPaneView.Find<DocTreeRow>(e.Source) is not { } row)
        {
            return;
        }
        vm.SelectedTreeRow = row;
        Loc loc = vm.Loc;
        CollectionRow doc = row.Row;
        bool writable = vm.IsEditable && !doc.IsDeleted;
        BsonValue? value = row.IsDocument ? doc.Document : row.Value;
        string path = row.Path;
        string? arrayPath = row.Kind == BsonKind.Array ? path : row.IsArrayElement ? row.ParentPath : null;
        var items = new List<Control>
        {
            CollectionMenus.Item(loc["Cw_MenuEditValue"], "Mongo.pencil", "Enter", () => BeginEdit(row), accent: true,
                enabled: writable && !row.IsDocument && path != "_id"),
            CollectionMenus.Submenu(loc["Cw_MenuChangeType"], "Mongo.shapes",
                CollectionMenus.KindItems(row.Kind, kind => vm.ChangeType(doc, path, kind)), writable && !row.IsDocument && path != "_id"),
            CollectionMenus.Item(loc["Cw_MenuAddFieldEllipsis"], "Mongo.plus", "Ctrl+D", () => vm.PromptAddField(doc, ContainerFor(row)), enabled: writable),
            CollectionMenus.Item(loc["Cw_MenuInsertElement"], "Mongo.list-plus", null, () => vm.InsertArrayElement(doc, arrayPath!),
                enabled: writable && arrayPath is not null),
            CollectionMenus.Separator(),
            CollectionMenus.Item(loc["Cw_MenuCopyValue"], "Mongo.copy", "Ctrl+C", () => _ = vm.CopyValueAsync(value)),
            CollectionMenus.Item(loc["Cw_MenuCopyPath"], "Mongo.route", path.Length == 0 ? null : path, () => _ = vm.CopyTextAsync(path),
                enabled: path.Length > 0),
            CollectionMenus.Item(loc["Cw_MenuCopyJson"], "Mongo.braces", null, () => _ = vm.CopyValueAsJsonAsync(value)),
            CollectionMenus.Separator()
        };
        if (!row.IsDocument)
        {
            string filterPath = row.FilterPath;
            items.Add(CollectionMenus.Item(loc["Cw_MenuFilterBy"], "Mongo.funnel", null, () => _ = vm.FilterByAsync(filterPath, value)));
            items.Add(CollectionMenus.Item(loc["Cw_MenuOpenQuery"], "Mongo.file-code", null, () => vm.OpenInQuery(filterPath, value)));
            items.Add(CollectionMenus.Separator());
            items.Add(CollectionMenus.Item(loc["Cw_MenuDeleteField"], "Mongo.trash-2", "Del", () => vm.DeleteField(doc, path), danger: true,
                enabled: writable && path != "_id"));
            items.Add(CollectionMenus.FilterHint(loc, filterPath, value, () => _ = vm.FilterByAsync(filterPath, value)));
        }
        else
        {
            items.Add(CollectionMenus.Item(loc["Cw_MenuOpenEditor"], "Mongo.file-pen-line", null, () => vm.OpenInEditor(doc), enabled: vm.IsEditable));
            items.Add(CollectionMenus.Separator());
            items.Add(CollectionMenus.Item(doc.IsDeleted ? loc["Cw_MenuUndelete"] : loc["Cw_MenuDeleteDoc"], "Mongo.trash-2", "Del",
                () => vm.StageDelete([doc]), danger: true, enabled: vm.IsEditable));
        }
        CollectionMenus.Open(e.Source as Control ?? TreeList, items);
        e.Handled = true;
    }

    /// <summary>「添加字段」加在哪个对象下:文档行加在顶层,对象行加在它里面,其余加在它的父对象里。</summary>
    private static string ContainerFor(DocTreeRow row) =>
        row.IsDocument ? ""
        : row.Kind == BsonKind.Object ? row.Path
        : row.IsArrayElement ? (row.ParentPath.LastIndexOf('.') is var dot and > 0 ? row.ParentPath[..dot] : "")
        : row.ParentPath;
}
