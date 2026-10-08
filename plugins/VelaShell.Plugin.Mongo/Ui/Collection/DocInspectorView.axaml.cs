using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 文档检查器:点对象 / 数组那一行(任何位置)展开收起、双击值编辑、点类型标签换类型(色块菜单)、
/// 日期值的日历、右键菜单与键盘手势。编辑中焦点离开(点回网格、点别的字段)即写入 ——
/// 不然编辑框与高亮着的类型下拉会一直挂在那儿,看着像还在改。
/// </summary>
public sealed partial class DocInspectorView : UserControl
{
    /// <summary>构造。</summary>
    public DocInspectorView()
    {
        InitializeComponent();
        FieldList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        FieldList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        FieldList.AddHandler(LostFocusEvent, OnEditorLostFocus);
        FieldList.AddHandler(Button.ClickEvent, OnDatePickClick);
        FieldList.DoubleTapped += OnDoubleTapped;
        FieldList.ContextRequested += OnContextRequested;
    }

    private DocInspectorViewModel? ViewModel => DataContext as DocInspectorViewModel;

    /// <summary>字段列表(测试用)。</summary>
    internal ListBox Fields => FieldList;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || GridPaneView.Find<InspectorField>(e.Source) is not { } field
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (IsInside<Border>(e.Source, "typetag") is { } tag)
        {
            e.Handled = true;
            vm.SelectedField = field;
            OpenTypeMenu(tag, vm, field);
            return;
        }
        // 对象 / 数组:点这一行的任何地方(箭头、键、值)都展开 / 收起,不必瞄准 11px 的小箭头。
        // 双击的第二下(ClickCount 2)不再翻回去;编辑中的那一行点的是编辑框,不动。
        if (field is { IsExpandable: true, IsEditing: false })
        {
            e.Handled = true;
            if (e.ClickCount == 1)
            {
                vm.SelectedField = field;
                vm.Toggle(field);
            }
            FocusSelected();
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // 对象 / 数组在按下第一下时就展开 / 收起过了,双击不再改成编辑整个容器的字面量(那用 Enter / 右键「编辑值」)。
        if (ViewModel is not { } vm || GridPaneView.Find<InspectorField>(e.Source) is not { IsEditing: false, IsExpandable: false } field)
        {
            return;
        }
        vm.BeginEdit(field);
    }

    /// <summary>
    /// 编辑框失焦:焦点落定后若已在编辑框之外(而且不是进了类型菜单、日历这些弹出层),就把值写进暂存区并收起编辑框。
    /// 解析不了的值留着编辑框和红框,不悄悄丢掉用户打的字。
    /// </summary>
    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TextBox box || ViewModel is not { Row: { } row } vm
            || GridPaneView.Find<InspectorField>(box) is not { Editor: { } editor } field)
        {
            return;
        }
        // 点网格的另一行时检查器紧接着就换了文档 —— 这次编辑属于失焦那一刻的那份文档。
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(field.Editor, editor) || box.IsKeyboardFocusWithin || FocusIsInPopup())
            {
                return;
            }
            _ = vm.CommitEdit(field, row);
        });
    }

    private bool FocusIsInPopup() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
        && focused.GetSelfAndVisualAncestors().Any(static v => v is PopupRoot or OverlayPopupHost);

    /// <summary>日期值编辑框右边的日历按钮:选一天 / 改时刻即写入暂存区。</summary>
    private void OnDatePickClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Classes: var classes } button || !classes.Contains("datepick")
            || ViewModel is not { Row: { } row } vm || GridPaneView.Find<InspectorField>(button) is not { Editor: { } editor } field)
        {
            return;
        }
        e.Handled = true;
        _ = DatePickFlyout.Show(button, vm.Loc, editor.Text, text =>
        {
            editor.Text = text;
            if (vm.CommitEdit(field, row))
            {
                FocusSelected();
            }
        });
    }

    /// <summary>
    /// 展开 / 收起会整表重建字段行,原来拿着焦点的那一行容器没了 —— 把焦点交给新的选中行,
    /// 方向键与 Enter 才接得上。
    /// </summary>
    private void FocusSelected() => Dispatcher.UIThread.Post(() =>
    {
        if (ViewModel?.SelectedField is { } selected && FieldList.ContainerFromItem(selected) is Control container)
        {
            _ = container.Focus();
        }
        else
        {
            _ = FieldList.Focus();
        }
    }, DispatcherPriority.Loaded);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        if (e.Source is TextBox && GridPaneView.Find<InspectorField>(e.Source) is { IsEditing: true } editing)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (vm.CommitEdit(editing))
                {
                    _ = FieldList.Focus();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DocInspectorViewModel.CancelEdit(editing);
                _ = FieldList.Focus();
            }
            return;
        }
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (control && e.Key == Key.Enter)
        {
            e.Handled = true;
            vm.AddFieldCommand.Execute(null);
            return;
        }
        if (vm.SelectedField is not { } field || vm.Row is not { } row)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter or Key.F2:
                e.Handled = true;
                vm.BeginEdit(field);
                break;
            case Key.Right when field.IsExpandable && !field.IsExpanded:
            case Key.Left when field.IsExpandable && field.IsExpanded:
                e.Handled = true;
                vm.Toggle(field);
                break;
            case Key.Delete when !field.IsMissing && field.Path != "_id":
                e.Handled = true;
                vm.Owner.DeleteField(row, field.Path);
                break;
            case Key.C when control:
                e.Handled = true;
                _ = vm.Owner.CopyValueAsync(field.Value);
                break;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { Row: { } row } vm || GridPaneView.Find<InspectorField>(e.Source) is not { } field)
        {
            return;
        }
        vm.SelectedField = field;
        CollectionTabViewModel owner = vm.Owner;
        Loc loc = vm.Loc;
        bool writable = owner.IsEditable && !row.IsDeleted && field.Path != "_id";
        BsonValue? value = field.Value;
        string path = field.Path;
        var items = new List<Control>
        {
            CollectionMenus.Item(loc["Cw_MenuEditValue"], "Mongo.pencil", "Enter", () => vm.BeginEdit(field), accent: true, enabled: writable),
            CollectionMenus.Submenu(loc["Cw_MenuChangeType"], "Mongo.shapes", CollectionMenus.KindItems(field.Kind, kind => vm.ChangeType(field, kind)), writable),
            CollectionMenus.Item(loc["Cw_MenuAddField"], "Mongo.plus", "Ctrl+↵",
                () => owner.PromptAddField(row, field.Kind == Bson.BsonKind.Object ? path : ParentOf(path)), enabled: owner.IsEditable),
            CollectionMenus.Separator(),
            CollectionMenus.Item(loc["Cw_MenuCopyValue"], "Mongo.copy", "Ctrl+C", () => _ = owner.CopyValueAsync(value)),
            CollectionMenus.Item(loc["Cw_MenuCopyPath"], "Mongo.route", path, () => _ = owner.CopyTextAsync(path)),
            CollectionMenus.Item(loc["Cw_MenuCopyJson"], "Mongo.braces", null, () => _ = owner.CopyValueAsJsonAsync(value)),
            CollectionMenus.Separator(),
            CollectionMenus.Item(loc["Cw_MenuFilterBy"], "Mongo.funnel", null, () => _ = owner.FilterByAsync(CollectionSample.NormalizePath(path), value)),
            CollectionMenus.Separator(),
            CollectionMenus.Item(loc["Cw_MenuDeleteField"], "Mongo.trash-2", "Del", () => owner.DeleteField(row, path), danger: true,
                enabled: writable && !field.IsMissing),
            CollectionMenus.FilterHint(loc, CollectionSample.NormalizePath(path), value, () => _ = owner.FilterByAsync(CollectionSample.NormalizePath(path), value))
        };
        CollectionMenus.Open(e.Source as Control ?? FieldList, items);
        e.Handled = true;
    }

    private static void OpenTypeMenu(Control anchor, DocInspectorViewModel vm, InspectorField field)
    {
        if (field.Path == "_id")
        {
            return;
        }
        CollectionMenus.Open(anchor, CollectionMenus.KindItems(field.Kind, kind => vm.ChangeType(field, kind)));
    }

    private static string ParentOf(string path) => path.LastIndexOf('.') is var dot and > 0 ? path[..dot] : "";

    private static T? IsInside<T>(object? source, string className) where T : Control
    {
        for (var visual = source as Avalonia.Visual; visual is not null; visual = Avalonia.VisualTree.VisualExtensions.GetVisualParent(visual))
        {
            if (visual is T control && control.Classes.Contains(className))
            {
                return control;
            }
            if (visual is ListBoxItem)
            {
                break;
            }
        }
        return null;
    }
}
