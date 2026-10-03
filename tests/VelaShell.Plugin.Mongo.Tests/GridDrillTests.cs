using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Shell;
using VelaShell.Plugin.Mongo.Staging;
using VelaShell.Plugin.Mongo.Ui;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 这一轮修的几处交互:网格钻入(Navicat 式双击对象 / 数组)、列宽拖动与双击自适应、面板分隔条、
/// 系统库默认隐藏、右键菜单文字颜色、工具栏不再有「连接」按钮。
/// </summary>
[TestClass]
public sealed class GridDrillTests
{
    // ── 纯逻辑 ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Filter_paths_drop_array_indexes_and_crumbs_mark_them()
    {
        Assert.AreEqual("items.sku", GridDrill.FilterPath("items.2.sku"));
        Assert.AreEqual("a.b.c", GridDrill.FilterPath("a.0.b.10.c"));
        Assert.AreEqual("customer.name", GridDrill.FilterPath("customer.name"));
        Assert.AreEqual("[2]", GridDrill.SegmentLabel("2"));
        Assert.AreEqual("items", GridDrill.SegmentLabel("items"));
    }

    [TestMethod]
    public void Show_system_databases_defaults_off()
    {
        Assert.IsFalse(MongoSettings.From(Request(new())).ShowSystemDatabases);
        Assert.IsTrue(MongoSettings.From(Request(new() { ["showSystemDatabases"] = "true" })).ShowSystemDatabases);
    }

    private static WorkspaceConnectRequest Request(Dictionary<string, string> settings) => new()
    {
        SessionId = "s",
        Host = "127.0.0.1",
        Port = 27017,
        Settings = settings
    };

    [TestMethod]
    public void Menu_items_leave_the_foreground_to_the_host_theme() => Screens.OnUi(() =>
    {
        MenuItem plain = MenuKit.Command("刷新", "Mongo.refresh-cw", new RelayCommand(static () => { }));
        Assert.IsFalse(plain.IsSet(TemplatedControl.ForegroundProperty),
            "a local Foreground (even null) beats the host ContextMenu style and the text turns invisible until hovered");

        MenuItem danger = MenuKit.Command("删除集合", "Mongo.trash-2", new RelayCommand(static () => { }), danger: true);
        Assert.IsTrue(danger.IsSet(TemplatedControl.ForegroundProperty));
        Assert.IsNotNull(danger.Foreground);
        return Task.CompletedTask;
    });

    // ── 对象树:系统库 ───────────────────────────────────────────────────────────

    private static bool Lists(Workbench bench, string database) =>
        bench.ViewModel.VisibleNodes.Any(n => n.Kind == NodeKind.Database && n.Name == database);

    [TestMethod]
    public void System_databases_are_hidden_until_the_eye_is_toggled() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        Assert.IsFalse(bench.ViewModel.ShowSystemDatabases);
        foreach (string system in new[] { "admin", "config", "local" })
        {
            Assert.IsFalse(Lists(bench, system), $"{system} is hidden by default");
            Assert.IsFalse(bench.Session.Databases.Contains(system), "pickers follow the tree");
        }
        Assert.IsTrue(Lists(bench, Screens.Database));

        bench.ViewModel.ToggleSystemDatabasesCommand.Execute(null);
        await Screens.PumpAsync(5);
        Assert.IsTrue(bench.ViewModel.ShowSystemDatabases);
        Assert.IsTrue(Lists(bench, "admin"));
        Assert.IsTrue(bench.Session.Databases.Contains("admin"));
        StringAssert.Contains(bench.ViewModel.SystemDatabasesTip, "隐藏");

        bench.ViewModel.ToggleSystemDatabasesCommand.Execute(null);
        await Screens.PumpAsync(5);
        Assert.IsFalse(Lists(bench, "admin"));
    });

    [TestMethod]
    public void A_system_database_named_in_the_connection_stays_listed() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync(database: "admin");
        Assert.IsTrue(Lists(bench, "admin"), "whoever connects to admin on purpose must not get an empty tree");
        Assert.IsFalse(Lists(bench, "config"));
    });

    [TestMethod]
    public void The_setting_opens_the_tree_with_system_databases() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync(settings: new Dictionary<string, string> { ["showSystemDatabases"] = "true" });
        Assert.IsTrue(bench.ViewModel.ShowSystemDatabases);
        Assert.IsTrue(Lists(bench, "local"));
    });

    // ── 网格钻入:真实服务器,临时库 ───────────────────────────────────────────────

    private static async Task<(IMongoClient Client, string Database)> SeedAsync()
    {
        var client = new MongoClient(TestServer.Uri);
        string database = "velashell_drill_" + Guid.NewGuid().ToString("N")[..8];
        await client.GetDatabase(database).GetCollection<BsonDocument>("orders").InsertManyAsync(
        [
            new BsonDocument
            {
                { "_id", 1 },
                { "orderNo", "SO-1" },
                {
                    "items", new BsonArray
                    {
                        new BsonDocument { { "sku", "A-1" }, { "qty", 2 }, { "attrs", new BsonDocument("color", "red") } },
                        new BsonDocument { { "sku", "B-2" }, { "qty", 1 }, { "note", "gift" } }
                    }
                },
                { "tags", new BsonArray { "vip", "rush" } },
                { "customer", new BsonDocument { { "name", "陈立" }, { "level", "VIP" } } }
            },
            new BsonDocument
            {
                { "_id", 2 },
                { "orderNo", "SO-2" },
                { "items", new BsonArray() },
                { "tags", new BsonArray() },
                { "customer", new BsonDocument("name", "王芳") }
            }
        ]);
        return (client, database);
    }

    private static async Task<CollectionTabViewModel> OpenTabAsync(Workbench bench, string database)
    {
        bench.Session.OpenCollection(database, "orders");
        var tab = (CollectionTabViewModel)bench.ViewModel.ActiveTab!;
        for (int i = 0; i < 200 && (tab.IsLoading || tab.Rows.Count == 0); i++)
        {
            await Screens.PumpAsync(5);
        }
        await Screens.PumpAsync(20);
        return tab;
    }

    private static CollectionCell Cell(CollectionRow row, IReadOnlyList<CollectionColumn> columns, string name) =>
        row.CellOf(columns.Single(c => c.Name == name))!;

    private static GridPaneView GridOf(Workbench bench) =>
        bench.Window.GetVisualDescendants().OfType<CollectionTabView>().First(static v => v.IsEffectivelyVisible).Grid;

    [TestMethod]
    public void An_array_of_documents_opens_as_a_table_and_edits_stage_absolute_paths() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database) = await SeedAsync();
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database);
            CollectionRow first = tab.Rows.Single(static r => r.Id == 1);
            CollectionCell items = Cell(first, tab.Columns, "items");
            Assert.IsTrue(items.IsContainer);

            Assert.IsTrue(tab.DrillInto(items));
            await Screens.PumpAsync(5);
            Assert.IsTrue(tab.IsDrilled);
            CollectionAssert.AreEqual(new[] { "sku", "qty", "attrs", "note" }, tab.GridColumns.Select(static c => c.Name).ToArray());
            Assert.AreEqual(2, tab.GridRows.Count);
            CollectionAssert.AreEqual(new[] { "0", "1" }, tab.GridRows.Select(static r => r.NumberText).ToArray(), "rows are numbered by array index");
            CollectionAssert.AreEqual(new[] { "文档 #1", "items" }, tab.DrillCrumbs.Select(static c => c.Label).ToArray());
            Assert.AreEqual("数组 · 2 项", tab.DrillSummary);
            Assert.AreSame(first, tab.SelectedRow, "the inspector keeps describing the document");
            Assert.AreEqual(2, GridOf(bench).List.ItemCount, "the grid shows the elements, not the documents");

            // 改第 2 个元素的 qty:暂存的是从文档根算起的路径。
            CollectionCell qty = Cell(tab.GridRows[1], tab.GridColumns, "qty");
            Assert.AreEqual("items.1.qty", qty.Path);
            tab.BeginCellEdit(qty);
            qty.Editor!.Text = "5";
            Assert.IsTrue(tab.CommitCellEdit(qty));
            await Screens.PumpAsync(5);
            Assert.AreEqual(5, tab.Staging.EditOf(first.Id)!.Find("items.1.qty")!.Value!.AsInt32);
            Assert.AreEqual(5, Cell(tab.GridRows[1], tab.GridColumns, "qty").Value!.AsInt32);
            Assert.IsTrue(Cell(tab.GridRows[1], tab.GridColumns, "qty").IsModified);
            Assert.AreEqual(CollectionRowState.Modified, first.State);

            // 删第 1 个元素:剩下的那个带着刚才的修改。
            tab.GridSelectedRows = [tab.GridRows[0]];
            tab.DeleteCommand.Execute(null);
            await Screens.PumpAsync(5);
            Assert.AreEqual(1, tab.GridRows.Count);
            Assert.AreEqual("B-2", Cell(tab.GridRows[0], tab.GridColumns, "sku").Value!.AsString);
            Assert.AreEqual(5, Cell(tab.GridRows[0], tab.GridColumns, "qty").Value!.AsInt32);
            Assert.AreEqual(1, tab.Staging.EditOf(first.Id)!.Changes.Count, "items.1.qty merges into the items change");

            tab.DrillBack();
            await Screens.PumpAsync(5);
            Assert.IsFalse(tab.IsDrilled);
            Assert.AreSame(tab.Rows, tab.GridRows);
            Assert.AreEqual("items", tab.CurrentColumn!.Name, "back lands on the cell it came from");
            Assert.AreEqual(1, tab.SelectedRow!.Id!.AsInt32);
            tab.Staging.Clear();
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    public void Scalars_objects_and_empty_arrays_drill_too() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database) = await SeedAsync();
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database);
            CollectionRow first = tab.Rows.Single(static r => r.Id == 1);

            Assert.IsTrue(tab.DrillInto(Cell(first, tab.Columns, "tags")));
            CollectionColumn value = tab.GridColumns.Single();
            Assert.IsTrue(value.IsElementValue);
            Assert.AreEqual("值", value.Name);
            Assert.AreEqual("tags.1", tab.GridRows[1].CellOf(value)!.Path);
            Assert.AreEqual("rush", tab.GridRows[1].CellOf(value)!.Value!.AsString);
            tab.ExitDrill();

            Assert.IsTrue(tab.DrillInto(Cell(first, tab.Columns, "customer")));
            Assert.AreEqual(1, tab.GridRows.Count);
            Assert.AreEqual("", tab.GridRows[0].NumberText);
            CollectionAssert.AreEqual(new[] { "name", "level" }, tab.GridColumns.Select(static c => c.Name).ToArray());
            Assert.AreEqual("customer.level", Cell(tab.GridRows[0], tab.GridColumns, "level").Path);
            Assert.AreEqual("对象 · 2 个字段", tab.DrillSummary);
            tab.ExitDrill();

            // 空数组也进得去,底栏「+」往里加元素。
            CollectionRow second = tab.Rows.Single(static r => r.Id == 2);
            Assert.IsTrue(tab.DrillInto(Cell(second, tab.Columns, "items")));
            Assert.AreEqual(0, tab.GridRows.Count);
            Assert.IsTrue(tab.GridColumns.Single().IsElementValue, "an empty array still gets a header");
            tab.AddRowCommand.Execute(null);
            await Screens.PumpAsync(5);
            Assert.AreEqual(1, tab.GridRows.Count);
            Assert.IsNotNull(tab.Staging.EditOf(second.Id));
            tab.ExitDrill();
            tab.Staging.Clear();

            // 标量不是容器:不钻,调用方照常内联编辑。
            Assert.IsFalse(tab.DrillInto(Cell(first, tab.Columns, "orderNo")));
            Assert.IsFalse(tab.IsDrilled);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    public void Nested_drills_keep_a_history_and_crumbs_jump_back() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database) = await SeedAsync();
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database);
            CollectionRow first = tab.Rows.Single(static r => r.Id == 1);
            Assert.IsTrue(tab.DrillInto(Cell(first, tab.Columns, "items")));
            Assert.IsTrue(tab.DrillInto(Cell(tab.GridRows[0], tab.GridColumns, "attrs")));
            Assert.AreEqual("items.0.attrs", tab.Drill!.Path);
            CollectionAssert.AreEqual(new[] { "文档 #1", "items", "[0]", "attrs" }, tab.DrillCrumbs.Select(static c => c.Label).ToArray());
            Assert.IsTrue(tab.DrillCrumbs[^1].IsLast);

            tab.DrillBack();
            Assert.AreEqual("items", tab.Drill!.Path, "back returns to the array, not to the element as a one-row object");
            Assert.AreEqual(0, tab.GridSelectedRow!.Number);
            Assert.AreEqual("attrs", tab.CurrentColumn!.Name);

            Assert.IsTrue(tab.DrillInto(Cell(tab.GridRows[0], tab.GridColumns, "attrs")));
            tab.DrillToCommand.Execute(tab.DrillCrumbs[0]);
            Assert.IsFalse(tab.IsDrilled, "the document crumb leaves the drill altogether");
            Assert.AreEqual("items", tab.CurrentColumn!.Name);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    public void Enter_drills_into_the_current_cell_and_backspace_returns() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database) = await SeedAsync();
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database);
            CollectionRow first = tab.Rows.Single(static r => r.Id == 1);
            tab.SelectedRow = first;
            tab.CurrentColumn = tab.Columns.Single(static c => c.Name == "items");
            await Screens.PumpAsync(5);
            ((Control)GridOf(bench).List.ContainerFromItem(first)!).Focus();
            await Screens.PumpAsync(5);

            bench.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Screens.PumpAsync(10);
            Assert.IsTrue(tab.IsDrilled, "Enter on an array cell opens it instead of a text editor");
            Assert.IsFalse(tab.GridRows.SelectMany(static r => r.Cells).Any(static c => c.IsEditing));

            bench.Window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            await Screens.PumpAsync(10);
            Assert.IsFalse(tab.IsDrilled);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    public void Double_clicking_a_column_divider_fits_the_content() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database) = await SeedAsync();
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database);
            CollectionColumn orderNo = tab.Columns.Single(static c => c.Name == "orderNo");
            orderNo.Width = 48;
            await Screens.PumpAsync(5);
            GridOf(bench).AutoFit(orderNo);
            await Screens.PumpAsync(5);
            Assert.IsTrue(orderNo.Width > 70, $"fitted width {orderNo.Width} should hold 'orderNo String' and 'SO-1'");
            Assert.IsTrue(orderNo.Width < 200, $"fitted width {orderNo.Width} should not balloon");
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    [TestCategory("Screenshot")]
    public void Screenshot_drilled_items() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database);
        CollectionRow? row = tab.Rows.FirstOrDefault(static r => r.Document.GetValue("items", BsonNull.Value) is BsonArray { Count: > 1 });
        Assert.IsNotNull(row, "the seeded orders carry item arrays");
        Assert.IsTrue(tab.DrillInto(Cell(row, tab.Columns, "items")));
        await Screens.PumpAsync(30);
        Assert.IsNotNull(Screens.Capture(bench.Window, "01-collection-drill-items"));
    });

    // ── 查询结果网格:钻入、拖宽、自适应(不连服务器) ─────────────────────────────

    [TestMethod]
    public void Result_grid_drills_into_containers_and_fits_columns() => Screens.OnUi(async () =>
    {
        var loc = new Loc("zh-CN");
        BsonDocument[] docs =
        [
            new() { { "_id", 1 }, { "items", new BsonArray { new BsonDocument { { "sku", "A-1" }, { "qty", 2 } }, 7 } } },
            new() { { "_id", 2 }, { "items", new BsonArray() } }
        ];
        var result = new ShellResult { Kind = ShellResultKind.Documents, Operation = "find", Database = "shop", Collection = "orders", Documents = docs };
        var set = new QueryResultSet(loc, EjsonMode.Relaxed, 1, result, new ShellStatement(0, "db.orders.find()", 0));
        var grid = new ResultGrid { Source = set };
        var window = new Window
        {
            Width = 900,
            Height = 500,
            Content = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
        };
        window.Show();
        await Screens.PumpAsync(5);

        CollectionAssert.AreEqual(new[] { "_id", "items" }, grid.ColumnNames.ToArray());
        Assert.IsFalse(grid.DrillInto(0, 0), "an Int32 is not a container");
        Assert.IsTrue(grid.DrillInto(0, 1));
        Assert.AreEqual(1, grid.Depth);
        Assert.AreEqual("items", grid.CurrentPath);
        CollectionAssert.AreEqual(new[] { "值", "sku", "qty" }, grid.ColumnNames.ToArray(), "a mixed array gets a value column for its scalars");

        double before = grid.WidthOf(1);
        grid.AutoFit(1);
        Assert.IsTrue(grid.WidthOf(1) >= 40 && grid.WidthOf(1) <= before + 1, "a short column fits down to its content");

        ResultCellEventArgs? seen = null;
        grid.CellActivated += (_, e) => seen = e;
        grid.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        await Screens.PumpAsync(2);
        Assert.IsNotNull(seen);
        Assert.AreEqual(0, seen.DocumentIndex, "events name the document the drilled row belongs to");
        Assert.AreSame(docs[0], seen.Document);

        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        await Screens.PumpAsync(2);
        Assert.AreEqual(0, grid.Depth);
        window.Close();
    });

    // ── 表格列宽:列头与各行共用一套 ──────────────────────────────────────────────

    [TestMethod]
    public void Table_columns_share_widths_between_header_and_rows() => Screens.OnUi(async () =>
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("120,80,*") };
        TableColumns.SetHeader(header, "t");
        header.Children.Add(new TextBlock { Text = "名称" });
        Grid Row(string name)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,80,*") };
            TableColumns.SetRow(row, "t");
            var cell = new TextBlock { Text = name };
            Grid.SetColumn(cell, 0);
            row.Children.Add(cell);
            return row;
        }
        Grid shortRow = Row("a");
        Grid longRow = Row("a-very-long-collection-name-that-needs-room");
        var view = new UserControl { Content = new StackPanel { Children = { header, shortRow, longRow } } };
        view.Styles.Add(new StyleInclude(new Uri("avares://VelaShell.Plugin.Mongo/")) { Source = new Uri("avares://VelaShell.Plugin.Mongo/Ui/MongoStyles.axaml") });
        var window = new Window { Width = 800, Height = 300, Content = view };
        window.Show();
        await Screens.PumpAsync(5);

        TableColumns.Group group = TableColumns.GroupOf(header)!;
        Assert.IsNotNull(group);
        Assert.AreEqual(2, header.Children.OfType<Thumb>().Count(), "a grip after each column but the trailing * column");
        Assert.IsNotNull(header.Children.OfType<Thumb>().First().Theme, "grips use the shared MongoColumnGrip theme");

        group.Set(1, 150);
        Assert.AreEqual(150, shortRow.ColumnDefinitions[1].Width.Value);
        Assert.AreEqual(150, longRow.ColumnDefinitions[1].Width.Value);

        group.AutoFit(0);
        await Screens.PumpAsync(5);
        double fitted = header.ColumnDefinitions[0].Width.Value;
        Assert.IsTrue(fitted > 120, $"the long name needs more than 120px, got {fitted}");
        Assert.AreEqual(fitted, shortRow.ColumnDefinitions[0].Width.Value);

        // 之后才实现出来的行(虚拟化列表滚出来的)也拿到同样的列宽。
        Grid late = Row("b");
        ((StackPanel)view.Content!).Children.Add(late);
        await Screens.PumpAsync(5);
        Assert.AreEqual(fitted, late.ColumnDefinitions[0].Width.Value);
        Assert.AreEqual(150, late.ColumnDefinitions[1].Width.Value);
        window.Close();
    });
}
