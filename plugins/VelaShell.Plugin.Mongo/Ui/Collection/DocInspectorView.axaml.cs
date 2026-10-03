using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 文档检查器:点箭头展开、双击值编辑、点类型标签换类型(色块菜单)、右键菜单与键盘手势。
/// </summary>
public sealed partial class DocInspectorView : UserControl
{
    /// <summary>构造。</summary>
    public DocInspectorView()
    {
        InitializeComponent();
        FieldList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        FieldList.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
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
        if (e.Source is Glyph { Classes: var classes } && classes.Contains("chevron"))
        {
            e.Handled = true;
            vm.Toggle(field);
            return;
        }
        if (IsInside<Border>(e.Source, "typetag") is { } tag)
        {
            e.Handled = true;
            vm.SelectedField = field;
            OpenTypeMenu(tag, vm, field);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is not { } vm || GridPaneView.Find<InspectorField>(e.Source) is not { IsEditing: false } field)
        {
            return;
        }
        if (field.IsExpandable && field.Kind is Bson.BsonKind.Object)
        {
            vm.Toggle(field);
            return;
        }
        vm.BeginEdit(field);
    }

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
                    FieldList.Focus();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DocInspectorViewModel.CancelEdit(editing);
                FieldList.Focus();
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
