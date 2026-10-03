using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;
using VelaShell.Plugin.Mongo.Ui;
using Calendar = Avalonia.Controls.Calendar;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 内联编辑的手感:日期格的日历、检查器编辑框失焦即写入、检查器里点对象 / 数组整行展开收起。
/// 只往暂存区写(收尾清掉),不提交 —— shop 库当只读对待。
/// </summary>
[TestClass]
public sealed class InlineEditingTests
{
    private static async Task<CollectionTabViewModel> OpenOrdersAsync(Workbench bench)
    {
        bench.Session.OpenCollection(Screens.Database, "orders");
        var tab = (CollectionTabViewModel)bench.ViewModel.ActiveTab!;
        for (int i = 0; i < 200 && (tab.IsLoading || tab.Rows.Count == 0); i++)
        {
            await Screens.PumpAsync(5);
        }
        await Screens.PumpAsync(20);
        return tab;
    }

    private static CollectionTabView ViewOf(Workbench bench) =>
        bench.Window.GetVisualDescendants().OfType<CollectionTabView>().First(static v => v.IsEffectivelyVisible);

    private static DocInspectorView InspectorOf(Workbench bench) =>
        ViewOf(bench).GetVisualDescendants().OfType<DocInspectorView>().First(static v => v.IsEffectivelyVisible);

    private static DateTime LocalOf(BsonValue value) => value.AsBsonDateTime.ToUniversalTime().ToLocalTime();

    [TestMethod]
    public void A_date_cell_opens_a_calendar_and_the_picked_day_is_staged() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenOrdersAsync(bench);
        CollectionColumn createdAt = tab.Columns.Single(static c => c.Name == "createdAt");
        CollectionRow row = tab.Rows[0];
        DateTime original = LocalOf(row.Document["createdAt"]);
        tab.SelectedRow = row;
        await Screens.PumpAsync(5);
        CollectionCell cell = row.CellOf(createdAt)!;
        tab.BeginCellEdit(cell);
        await Screens.PumpAsync(10);

        InlineValueEditor editor = cell.Editor!;
        Assert.IsTrue(editor.IsDate);
        Assert.IsEmpty(editor.Choices, "a date offers a calendar, not a one-item text list");
        Assert.IsTrue(editor.HasDropDown);
        Assert.AreEqual("Mongo.calendar", editor.DropDownIcon);

        GridPaneView grid = ViewOf(bench).Grid;
        Button button = grid.List.GetVisualDescendants().OfType<Button>()
            .Single(static b => b.Classes.Contains("choices") && b.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Screens.PumpAsync(10);
        Flyout picker = grid.DatePicker!;
        var content = (StackPanel)picker.Content!;
        var calendar = (Calendar)content.Children[0];
        Assert.AreEqual(original.Date, calendar.SelectedDate, "the calendar opens on the cell's current day");

        calendar.SelectedDate = new DateTime(2026, 2, 14);
        await Screens.PumpAsync(10);
        Assert.IsFalse(cell.IsEditing, "picking a day writes it and closes the editor");
        StagedEdit edit = tab.Staging.EditOf(row.Id)!;
        DateTime staged = LocalOf(edit.Find("createdAt")!.Value!);
        Assert.AreEqual(new DateTime(2026, 2, 14), staged.Date);
        Assert.AreEqual(new TimeSpan(original.Hour, original.Minute, original.Second), staged.TimeOfDay, "the time of day is kept");

        // 只改时刻:时刻框里写 8:05,点「应用」。写错了不关、不写。
        tab.BeginCellEdit(row.CellOf(createdAt)!);
        await Screens.PumpAsync(10);
        cell = row.CellOf(createdAt)!;
        button = grid.List.GetVisualDescendants().OfType<Button>().Single(static b => b.Classes.Contains("choices") && b.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Screens.PumpAsync(10);
        content = (StackPanel)grid.DatePicker!.Content!;
        var bar = (DockPanel)content.Children[1];
        TextBox time = bar.Children.OfType<TextBox>().Single();
        Button apply = ((StackPanel)bar.Children[0]).Children.OfType<Button>().Last();
        time.Text = "25:99";
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Screens.PumpAsync(5);
        Assert.IsTrue(cell.IsEditing, "an impossible time is refused");
        time.Text = "8:05";
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Screens.PumpAsync(10);
        Assert.IsFalse(cell.IsEditing);
        staged = LocalOf(tab.Staging.EditOf(row.Id)!.Find("createdAt")!.Value!);
        Assert.AreEqual(new DateTime(2026, 2, 14, 8, 5, 0), staged);
        tab.Staging.Clear();
    });

    [TestMethod]
    public void Leaving_an_inspector_edit_writes_it_and_drops_the_editing_look() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenOrdersAsync(bench);
        CollectionRow row = tab.Rows[1];
        tab.SelectedRow = row;
        await Screens.PumpAsync(10);
        DocInspectorView inspector = InspectorOf(bench);

        InspectorField status = tab.Inspector.Fields.First(static f => f.Path == "status");
        tab.Inspector.BeginEdit(status);
        await Screens.PumpAsync(20);
        Assert.IsTrue(status.IsEditing);
        TextBox box = inspector.GetVisualDescendants().OfType<GridEditBox>().Single();
        Assert.IsTrue(box.IsKeyboardFocusWithin, "the editor takes focus when it appears");
        status.Editor!.Text = "on-hold";

        // 点回网格:焦点离开编辑框 → 写进暂存区,编辑框与高亮的类型下拉一起收掉。
        ((Control)ViewOf(bench).Grid.List.ContainerFromItem(row)!).Focus();
        await Screens.PumpAsync(10);
        Assert.IsFalse(tab.Inspector.Fields.Any(static f => f.IsEditing), "no field is left in editing state");
        Assert.AreEqual("on-hold", tab.Staging.EditOf(row.Id)!.Find("status")!.Value!.AsString);
        Assert.IsFalse(inspector.GetVisualDescendants().OfType<Border>()
            .Any(static b => b.Classes.Contains("typetag") && b.Classes.Contains("on") && b.IsEffectivelyVisible));

        // 选中行在焦点离开检查器时不着色;焦点回来又亮。
        InspectorField selected = tab.Inspector.SelectedField!;
        var item = (ListBoxItem)inspector.Fields.ContainerFromItem(selected)!;
        ContentPresenter presenter = item.GetVisualDescendants().OfType<ContentPresenter>().First();
        Assert.AreEqual(0, ((ISolidColorBrush?)presenter.Background)?.Color.A ?? 0, "an unfocused selection is not painted");
        item.Focus();
        await Screens.PumpAsync(5);
        Assert.AreNotEqual(0, ((ISolidColorBrush?)presenter.Background)?.Color.A ?? 0, "the focused selection is painted");

        // 写不进去的值(Decimal 里打字母)失焦时不悄悄丢:编辑框留着、红框。
        InspectorField total = tab.Inspector.Fields.First(static f => f.Path == "total");
        tab.Inspector.BeginEdit(total);
        await Screens.PumpAsync(20);
        total.Editor!.Text = "abc";
        ((Control)ViewOf(bench).Grid.List.ContainerFromItem(row)!).Focus();
        await Screens.PumpAsync(10);
        Assert.IsTrue(total.IsEditing);
        Assert.IsTrue(total.Editor!.HasError);
        tab.Staging.Clear();
    });

    [TestMethod]
    public void Clicking_anywhere_on_an_array_row_in_the_inspector_toggles_it() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenOrdersAsync(bench);
        tab.SelectedRow = tab.Rows[0];
        await Screens.PumpAsync(10);
        DocInspectorView inspector = InspectorOf(bench);

        InspectorField Tags() => tab.Inspector.Fields.First(static f => f.Path == "tags");
        Assert.IsTrue(Tags().IsExpandable);
        Assert.IsFalse(Tags().IsExpanded, "a short array of strings starts collapsed");

        async Task ClickAsync(double x)
        {
            var container = (Control)inspector.Fields.ContainerFromItem(Tags())!;
            container.BringIntoView();
            await Screens.PumpAsync(5);
            Point at = container.TranslatePoint(new Point(x, container.Bounds.Height / 2), bench.Window)!.Value;
            bench.Window.MouseDown(at, MouseButton.Left);
            bench.Window.MouseUp(at, MouseButton.Left);
            await Screens.PumpAsync(10);
        }

        // 点值(不是箭头)展开;再点键收起 —— 两次落点隔得远,不算双击。
        await ClickAsync(150);
        Assert.IsTrue(Tags().IsExpanded, "clicking the value expands the array");
        Assert.IsTrue(tab.Inspector.Fields.Any(static f => f.Path == "tags.0"));
        Assert.AreEqual("tags", tab.Inspector.SelectedField?.Path, "the clicked row becomes the selection");
        await ClickAsync(30);
        Assert.IsFalse(Tags().IsExpanded, "clicking the key collapses it again");
        Assert.IsFalse(tab.Inspector.Fields.Any(static f => f.Path == "tags.0"));

        // 双击只翻一次(第二下不再翻回去,也不进入编辑整个数组)。
        var row = (Control)inspector.Fields.ContainerFromItem(Tags())!;
        Point same = row.TranslatePoint(new Point(150, row.Bounds.Height / 2), bench.Window)!.Value;
        bench.Window.MouseDown(same, MouseButton.Left);
        bench.Window.MouseUp(same, MouseButton.Left);
        await Screens.PumpAsync(2);
        bench.Window.MouseDown(same, MouseButton.Left);
        bench.Window.MouseUp(same, MouseButton.Left);
        await Screens.PumpAsync(10);
        Assert.IsTrue(Tags().IsExpanded);
        Assert.IsFalse(tab.Inspector.Fields.Any(static f => f.IsEditing));
    });
}
