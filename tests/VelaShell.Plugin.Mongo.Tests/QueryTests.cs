using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 查询编辑器(设计稿 03 / 14)打真实的 shop 库:运行、补全、诊断、执行计划、写护栏,外加两张截图。
/// shop 只读;要写的测试自己建 <c>velashell_query_*</c> 库并在收尾删掉。
/// </summary>
[TestClass]
public sealed class QueryTests
{
    /// <summary>设计稿 03 那段脚本(能跑的版本:点路径加了引号,第三个阶段写完整)。</summary>
    internal const string RunnableScript = """
        // 近 30 天高价值订单 · 按客户汇总
        use("shop")

        db.orders.find(
          { status: "paid", total: { $gte: 500 }, createdAt: { $gte: ISODate("2026-08-27") } },
          { orderNo: 1, "customer.name": 1, total: 1 }
        ).sort({ createdAt: -1 }).limit(100)

        db.orders.aggregate([
          { $match: { status: "paid", createdAt: { $gte: ISODate("2026-08-27") } } },
          { $group: { _id: "$customer.id", name: { $first: "$customer.name" }, orders: { $sum: 1 }, sum: { $sum: "$total" } } },
          { $sort: { sum: -1 } }
        ])
        """;

    /// <summary>设计稿 03 截图那一刻的文本:第 6 行点路径没加引号,第 12 行正在敲 <c>{ $so</c>。</summary>
    internal const string DesignScript = """
        // 近 30 天高价值订单 · 按客户汇总
        use("shop")

        db.orders.find(
          { status: "paid", total: { $gte: 500 }, createdAt: { $gte: ISODate("2026-08-27") } },
          { orderNo: 1, customer.name: 1, total: 1 }
        ).sort({ createdAt: -1 }).limit(100)

        db.orders.aggregate([
          { $match: { status: "paid", createdAt: { $gte: ISODate("2026-08-27") } } },
          { $group: { _id: "$customer.id", name: { $first: "$customer.name" }, orders: { $sum: 1 }, sum: { $sum: "$total" } } },
          { $so
        ])
        """;

    private static QueryTabViewModel Open(Workbench bench, string text, string database = "shop")
    {
        var tab = new QueryTabViewModel(bench.Session, database, text, false, 90);
        return tab;
    }

    private static async Task WaitAsync(Func<bool> condition, int rounds = 300)
    {
        for (int i = 0; i < rounds && !condition(); i++)
        {
            await Screens.PumpAsync(2);
        }
        Assert.IsTrue(condition(), "timed out waiting for the condition");
    }

    /// <summary>
    /// 有未保存修改的标签照样能关:平时标签上是橙点、× 藏着,鼠标移到标签上换成 ×;
    /// 点 × 先弹「放弃未保存的修改?」—— 取消就留着,确认「放弃并关闭」就关掉。
    /// </summary>
    [TestMethod]
    public void Modified_tab_closes_after_confirming_discard() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenQuery("shop", "db.orders.find({})");
        await Screens.PumpAsync();
        QueryTabViewModel tab = bench.ViewModel.Tabs.OfType<QueryTabViewModel>().Last();
        tab.Text += " ";
        await Screens.PumpAsync(5);
        Assert.IsTrue(tab.IsModified);

        ListBox strip = bench.View.GetVisualDescendants().OfType<ListBox>().Single(static l => l.Name == "TabStrip");
        var item = (ListBoxItem)strip.ContainerFromItem(tab)!;
        Button close = item.GetVisualDescendants().OfType<Button>().Single(static b => b.Classes.Contains("tabclose"));
        Avalonia.Controls.Shapes.Ellipse dot = item.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()
            .Single(static e => e.Classes.Contains("dirty"));
        Assert.IsTrue(dot.IsVisible, "an edited tab shows the orange dot");
        Assert.IsFalse(close.IsVisible);

        // 鼠标移到标签上:橙点换成 ×。
        bench.Window.MouseMove(item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), bench.Window)!.Value, RawInputModifiers.None);
        await Screens.PumpAsync(5);
        Assert.IsTrue(close.IsVisible, "hovering an edited tab reveals its close button");
        Assert.IsFalse(dot.IsVisible);

        // 点 × → 确认框;取消 → 标签还在。
        close.Command!.Execute(null);
        await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
        var confirm = (ConfirmDialogViewModel)bench.ViewModel.Dialog!;
        Assert.AreEqual("放弃未保存的修改?", confirm.Request.Title);
        Assert.AreEqual("放弃并关闭", confirm.Request.ConfirmLabel);
        Assert.IsTrue(confirm.Request.Danger);
        confirm.CloseCommand.Execute(null);
        await Screens.PumpAsync(5);
        Assert.Contains(tab, bench.ViewModel.Tabs);
        Assert.IsTrue(tab.IsModified);

        // 再点 × → 放弃并关闭 → 标签关掉。
        close.Command!.Execute(null);
        await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
        ((ConfirmDialogViewModel)bench.ViewModel.Dialog!).ConfirmCommand.Execute(null);
        await WaitAsync(() => !bench.ViewModel.Tabs.Contains(tab));
        Assert.DoesNotContain(tab, bench.ViewModel.Tabs);
    });

    /// <summary>
    /// 执行目标(Navicat 查询窗口的「连接 ▾ 数据库 ▾」):切到另一条没连着的连接 —— 先安静地连上(不开占位标签、不开对象列表),
    /// 查询标签原位换成那条连接上的,文本与未保存状态带过去、库沿用。换库之后集合在新库里解析:
    /// 新库里没有的集合标橙、字段页写明;脚本里的 use 优先于下拉。
    /// </summary>
    [TestMethod]
    public void Target_switches_connection_in_place_and_resolves_collections_in_the_selected_database() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var url = new MongoUrl(TestServer.Uri);
        var profile = new MongoProfile { Name = "mongo-replica-02", Host = url.Server.Host, Port = url.Server.Port };
        profile.Set(MongoSettings.KeyDirect, "true");
        await bench.ViewModel.SaveProfileAsync(profile, null, connect: false);
        ConnectionEntry other = bench.ViewModel.Connections.Single(static c => c.Name == "mongo-replica-02");
        Assert.AreEqual(ConnectionState.Disconnected, other.State);

        const string script = "db.orders.find({}).limit(5)\n\n// 末行";
        bench.Session.OpenQuery("shop", script);
        await Screens.PumpAsync();
        QueryTabViewModel tab = bench.ViewModel.Tabs.OfType<QueryTabViewModel>().Last();
        tab.Text = script + " ";
        tab.CaretOffset = tab.Text.Length;
        Assert.AreEqual("mongo-inner-01", tab.ConnectionName);
        Assert.HasCount(2, tab.ConnectionChoices);
        Assert.IsTrue(tab.IsCurrentConnection(bench.Session.Entry));
        int index = bench.ViewModel.Tabs.IndexOf(tab);
        int count = bench.ViewModel.Tabs.Count;

        QueryTabViewModel? moved = await tab.SwitchConnectionAsync(other);
        await Screens.PumpAsync();
        Assert.IsNotNull(moved);
        Assert.AreEqual(ConnectionState.Connected, other.State);
        Assert.AreSame(other.Session, moved.Owner);
        Assert.AreSame(moved, bench.ViewModel.ActiveTab);
        Assert.AreEqual(index, bench.ViewModel.Tabs.IndexOf(moved), "the tab is replaced in place");
        Assert.AreEqual(count, bench.ViewModel.Tabs.Count, "a quiet connect opens no placeholder or object list");
        Assert.DoesNotContain(tab, bench.ViewModel.Tabs);
        Assert.AreEqual("shop", moved.Database);
        Assert.AreEqual(script + " ", moved.Text);
        Assert.IsTrue(moved.IsModified, "unsaved edits stay unsaved on the other connection");
        Assert.AreEqual("mongo-replica-02", moved.ConnectionName);
        Assert.AreEqual("在 mongo-replica-02 / shop 上执行", moved.TargetText);

        await moved.RunAsync(all: true);
        Assert.HasCount(5, moved.Panes.OfType<QueryResultSet>().Single().Documents);

        // 换到 admin:orders 在那里不存在 → 集合名下橙色波浪线,字段页写明抽的是哪个库。
        moved.SelectDatabaseCommand.Execute("admin");
        await WaitAsync(() => moved.Diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Warning) && moved.HelperDatabase == "@admin");
        EditorDiagnostic missing = moved.Diagnostics.Single(static d => d.Severity == DiagnosticSeverity.Warning);
        Assert.AreEqual("orders", moved.Text.Substring(missing.Offset, missing.Length));
        Assert.AreEqual("库 admin 里没有集合 orders —— 核对上方的数据库或 use()", missing.Message);
        await WaitAsync(() => moved.HasHelperMissing);

        // 脚本里的 use 优先于下拉:同一条 find 跟在 use("shop") 后面就不报。
        moved.Text = "use(\"shop\")\n" + script;
        moved.CaretOffset = moved.Text.Length;
        await WaitAsync(() => moved.Diagnostics.Count == 0);

        // 回到 shop:不再报。
        moved.Text = script;
        moved.SelectDatabaseCommand.Execute("shop");
        moved.CaretOffset = moved.Text.Length;
        await WaitAsync(() => moved.Diagnostics.Count == 0 && !moved.HasHelperMissing && moved.HelperDatabase == "@shop");
    });

    [TestMethod]
    public void Run_DesignScriptProducesTwoResultsAndHistory() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        QueryTabViewModel tab = Open(bench, RunnableScript, "admin");
        await tab.RunAsync(all: true);

        List<QueryResultSet> results = [.. tab.Panes.OfType<QueryResultSet>()];
        Assert.HasCount(2, results);
        Assert.AreEqual("find", results[0].Result.Operation);
        Assert.AreEqual(100, results[0].Documents.Count);
        Assert.AreEqual("aggregate", results[1].Result.Operation);
        Assert.IsTrue(results[1].IsSelected, "跑完选中最后一份结果");
        Assert.AreEqual("_id,name,orders,sum", string.Join(",", results[1].Columns.Select(static c => c.Name)));
        Assert.AreEqual("shop", tab.Database, "脚本里的 use 要改当前库");
        Assert.AreEqual("@shop", tab.Scope);
        Assert.HasCount(2, tab.History, "每条查询一条历史(use 不记)");
        Assert.HasCount(2, tab.Messages.Messages);
        Assert.StartsWith("2 条语句执行完成", tab.ExecText);
        Assert.Contains("find", tab.ExecTimes);
        Assert.Contains("aggregate", tab.ExecTimes);
        ShellStatement find = ShellScript.Split(RunnableScript)[1];
        Assert.AreEqual(100, tab.LensFor(find)!.Count);

        // 再跑一次:未固定的结果被替换,固定的留着。
        results[0].IsPinned = true;
        await tab.RunAsync(all: true);
        Assert.HasCount(3, tab.Panes.OfType<QueryResultSet>().ToList());
        Assert.IsTrue(tab.Panes.OfType<QueryResultSet>().First().IsPinned);
    });

    [TestMethod]
    public void Run_CurrentStatementOnlyAndParseErrorsStop() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        QueryTabViewModel tab = Open(bench, DesignScript);
        tab.CaretOffset = DesignScript.IndexOf("$group", StringComparison.Ordinal);
        await tab.RunAsync(all: false);
        // 光标在没写完的 aggregate 里:解析失败,只报这一条,不碰别的语句。
        Assert.IsEmpty(tab.Panes.OfType<QueryResultSet>().ToList());
        Assert.AreEqual(QueryMessageKind.Error, tab.Messages.Messages[^1].Kind);
        Assert.IsTrue(tab.Messages.IsSelected);

        tab.Text = RunnableScript;
        tab.CaretOffset = RunnableScript.IndexOf(".limit(100)", StringComparison.Ordinal);
        await tab.RunAsync(all: false);
        QueryResultSet only = tab.Panes.OfType<QueryResultSet>().Single();
        Assert.AreEqual("find", only.Result.Operation);
    });

    [TestMethod]
    public void Diagnostics_DottedKeyWithQuickFix() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        const string text = "db.orders.find({}, { orderNo: 1, customer.name: 1 })\n";
        QueryTabViewModel tab = Open(bench, text);
        tab.CaretOffset = text.Length;
        Assert.HasCount(1, tab.Diagnostics);
        EditorDiagnostic diagnostic = tab.Diagnostics[0];
        Assert.AreEqual(text.IndexOf("customer.name", StringComparison.Ordinal), diagnostic.Offset);
        Assert.AreEqual("含点号的字段名需加引号 → \"customer.name\"", diagnostic.Message);
        Assert.AreEqual("Alt+↵ 修复", diagnostic.FixLabel);
        Assert.AreEqual("1 个问题", tab.ProblemText);

        tab.CaretOffset = 5;
        Assert.IsTrue(tab.ApplyQuickFix());
        Assert.AreEqual("db.orders.find({}, { orderNo: 1, \"customer.name\": 1 })\n", tab.Text);
        await Screens.PumpAsync(40);
        Assert.IsEmpty(tab.Diagnostics);
    });

    [TestMethod]
    public void Completion_UsesSampledFieldsAndUpstreamGroup() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        QueryTabViewModel tab = Open(bench, "");

        CompletionSet? collections = await tab.ProvideCompletionAsync(new CompletionRequest("use(\"shop\")\ndb.or", 16, false));
        Assert.IsNotNull(collections);
        Assert.IsTrue(collections.Items.Any(static i => i.Label == "orders"));
        Assert.IsTrue(collections.Items.Any(static i => i.Label == "getCollection"));

        const string filter = "db.orders.find({ cust";
        CompletionSet? fields = await tab.ProvideCompletionAsync(new CompletionRequest(filter, filter.Length, false));
        Assert.IsNotNull(fields);
        CompletionItem name = fields.Items.First(static i => i.Label == "customer.name");
        Assert.AreEqual("字段 · 抽样", name.Category);
        Assert.AreEqual("\"customer.name\": |", name.InsertText, "点路径作键时自动加引号");
        Assert.AreEqual(filter.Length - 4, fields.ReplaceOffset);

        int caret = DesignScript.IndexOf("$so", StringComparison.Ordinal) + 3;
        CompletionSet? stages = await tab.ProvideCompletionAsync(new CompletionRequest(DesignScript, caret, false));
        Assert.IsNotNull(stages);
        CompletionItem sort = stages.Items.First(static i => i.Label == "$sort");
        Assert.AreEqual("上游 $group 输出的字段", sort.ChipsTitle);
        Assert.AreEqual("_id,name,orders,sum", string.Join(",", sort.Chips));
        Assert.AreEqual("聚合阶段", sort.Category);
        Assert.IsTrue(stages.Items.Any(static i => i.Label == "$sort 降序…"));
        Assert.IsFalse(stages.Items.Any(static i => i.Label == "sum"), "以 $ 起头时只给阶段");

        string bare = DesignScript.Replace("{ $so", "{ su", StringComparison.Ordinal);
        int bareCaret = bare.IndexOf("{ su", StringComparison.Ordinal) + 4;
        CompletionSet? upstream = await tab.ProvideCompletionAsync(new CompletionRequest(bare, bareCaret, false));
        CompletionItem sum = upstream!.Items.First(static i => i.Label == "sum");
        Assert.AreEqual("上游字段 · Decimal", sum.Category, "$sum: \"$total\" 的类型从抽样推出来");

        const string op = "db.orders.find({ total: { $g";
        CompletionSet? operators = await tab.ProvideCompletionAsync(new CompletionRequest(op, op.Length, false));
        Assert.IsTrue(operators!.Items.Any(static i => i.Label == "$gte"));
        Assert.IsFalse(operators.Items.Any(static i => i.Label == "$or"), "字段的运算符对象里不给逻辑运算符");

        const string path = "db.orders.aggregate([{ $group: { _id: \"$cust";
        CompletionSet? paths = await tab.ProvideCompletionAsync(new CompletionRequest(path, path.Length, false));
        Assert.IsTrue(paths!.Items.Any(static i => i.Label == "$customer.id" && i.InsertText == "$customer.id\""));
    });

    [TestMethod]
    public void Explain_FromEditorShowsPlanAndAdvice() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        QueryTabViewModel tab = Open(bench, RunnableScript);
        tab.CaretOffset = RunnableScript.IndexOf("orderNo", StringComparison.Ordinal);
        await tab.ExplainCurrentAsync(null);

        Assert.IsTrue(tab.Explain.IsSelected);
        Assert.IsTrue(tab.Explain.HasPlan, tab.Explain.Error ?? "");
        Assert.AreEqual("find", tab.Explain.Command!.Method!.Name);
        Assert.AreEqual("db.orders.find(…).sort({ createdAt: -1 }).limit(100)", tab.Explain.CommandText);
        ExplainStageCard first = tab.Explain.FlowItems.OfType<ExplainStageCard>().First();
        Assert.AreEqual("IXSCAN", first.Name);
        Assert.AreEqual("VelaStatusConnected", first.StrokeToken);
        Assert.HasCount(6, tab.Explain.Stats);
        Assert.IsTrue(tab.Explain.Candidates[0].Winner);
        Assert.StartsWith("IXSCAN status_1_createdAt_-1", tab.PlanText);

        tab.Explain.ViewMode = "raw";
        Assert.Contains("executionStats", tab.Explain.RawJson);
    });

    [TestMethod]
    public void Explain_keeps_the_split_height_the_user_dragged() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenQuery("shop", RunnableScript);
        await Screens.PumpAsync();
        QueryTabViewModel tab = bench.ViewModel.Tabs.OfType<QueryTabViewModel>().Last();
        await tab.RunAsync(all: true);
        await Screens.PumpAsync(20);
        QueryTabView view = bench.Window.GetVisualDescendants().OfType<QueryTabView>().First(static v => v.IsEffectivelyVisible);
        RowDefinition editorRow = view.Root.RowDefinitions[1];
        // 用户把分隔条拖到了 300(结果区随之变高)。
        editorRow.Height = new GridLength(300);
        await Screens.PumpAsync(5);

        tab.CaretOffset = RunnableScript.IndexOf("orderNo", StringComparison.Ordinal);
        await tab.ExplainCurrentAsync(null);
        await Screens.PumpAsync(20);
        Assert.IsTrue(tab.Explain.IsSelected);
        Assert.AreEqual(300d, editorRow.Height.Value, "switching to the plan must not resize the panes");

        tab.SelectPaneCommand.Execute(tab.Panes.OfType<QueryResultSet>().First());
        await Screens.PumpAsync(10);
        Assert.AreEqual(300d, editorRow.Height.Value, "switching back to the results keeps the user's height too");
    });

    [TestMethod]
    public void Writes_RespectReadOnlyAndRefreshCatalog() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        string database = "velashell_query_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            QueryTabViewModel tab = Open(bench, "db.items.insertOne({ a: 1 })", database);
            bench.Session.Guard.IsReadOnly = true;
            await tab.RunAsync(all: true);
            Assert.AreEqual(QueryMessageKind.Warning, tab.Messages.Messages[^1].Kind, "只读模式下写被拦下");
            long blocked = await bench.Connection.Collection(database, "items").EstimatedDocumentCountAsync();
            Assert.AreEqual(0L, blocked);

            bench.Session.Guard.IsReadOnly = false;
            await tab.RunAsync(all: true);
            long written = await bench.Connection.Collection(database, "items").EstimatedDocumentCountAsync();
            Assert.AreEqual(1L, written);
        }
        finally
        {
            bench.Session.Guard.IsReadOnly = false;
            await bench.Connection.Client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    public void Format_SaveAndSnippets() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        QueryTabViewModel tab = Open(bench, "db.orders.find({status:\"paid\"})");
        tab.FormatCommand.Execute(null);
        Assert.AreEqual("db.orders.find({ status: \"paid\" })", tab.Text);
        Assert.IsTrue(tab.IsModified);
        Assert.Contains("未保存", tab.StatusText);

        await tab.SaveAsAsync("高价值订单");
        Assert.IsFalse(tab.IsModified);
        await Screens.PumpAsync(20);
        Assert.IsTrue(tab.Snippets.Any(static s => s.IsSaved && s.Label == "高价值订单"));
        Assert.IsTrue(tab.Snippets.Any(static s => !s.IsSaved));
    });

    // ── 截图 ────────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board03_QueryEditorWithCompletion() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenQuery("shop", RunnableScript);
        await Screens.PumpAsync();
        QueryTabViewModel tab = bench.ViewModel.Tabs.OfType<QueryTabViewModel>().Last();
        await tab.RunAsync(all: true);
        await Screens.PumpAsync();

        QueryTabView view = bench.Window.GetVisualDescendants().OfType<QueryTabView>().First();
        CodeEditor editor = view.CodeEditor;
        editor.Editor.Document.Text = DesignScript;
        int caret = DesignScript.IndexOf("$so", StringComparison.Ordinal) + 3;
        editor.Editor.CaretOffset = caret;
        editor.FocusEditor();
        await Screens.PumpAsync(40);
        // headless 下弹出层默认是独立的顶层窗口,截不进主窗口:让它走覆盖层。
        foreach (Popup popup in editor.GetVisualDescendants().OfType<Popup>())
        {
            popup.ShouldUseOverlayLayer = true;
        }
        editor.RequestCompletion();
        await Screens.PumpAsync(80);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "03-query");
        Assert.IsNotNull(frame);
        Assert.HasCount(2, tab.Panes.OfType<QueryResultSet>().ToList());
        Assert.IsTrue(tab.HasProblems);
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board03_AlternateViewsAndDialogs() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenQuery("shop", RunnableScript);
        await Screens.PumpAsync();
        QueryTabViewModel tab = bench.ViewModel.Tabs.OfType<QueryTabViewModel>().Last();
        await tab.RunAsync(all: true);

        tab.SelectPaneCommand.Execute(tab.Panes.OfType<QueryResultSet>().First());
        tab.ResultView = "tree";
        await Screens.PumpAsync();
        QueryResultSet first = tab.Panes.OfType<QueryResultSet>().First();
        first.TreeRows[0].ToggleCommand.Execute(null);
        await Screens.PumpAsync();
        Assert.IsGreaterThan(first.Documents.Count, first.TreeRows.Count, "展开第一份文档后多出它的字段行");
        _ = Screens.Capture(bench.Window, "03-query-tree");

        tab.ResultView = "json";
        await Screens.PumpAsync();
        Assert.StartsWith("[", first.JsonText);
        _ = Screens.Capture(bench.Window, "03-query-json");

        tab.SelectPaneCommand.Execute(tab.Messages);
        tab.HelperTab = "history";
        await Screens.PumpAsync();
        _ = Screens.Capture(bench.Window, "03-query-messages");

        tab.CaretOffset = RunnableScript.IndexOf("orderNo", StringComparison.Ordinal);
        tab.ExportCodeCommand.Execute(null);
        await Screens.PumpAsync();
        _ = Assert.IsInstanceOfType<CodeExportDialogViewModel>(bench.ViewModel.Dialog);
        _ = Screens.Capture(bench.Window, "03-query-export");
        bench.ViewModel.CloseDialog(bench.ViewModel.Dialog!);

        await tab.ExplainCurrentAsync(null);
        await tab.CompareWithHintAsync();
        await Screens.PumpAsync(60);
        var hint = (HintCompareDialogViewModel)bench.ViewModel.Dialog!;
        hint.SelectedIndex = "_id_";
        hint.CompareCommand.Execute(null);
        await Screens.PumpAsync(80);
        Assert.AreEqual("hint · _id_", hint.HintedTitle);
        _ = Screens.Capture(bench.Window, "14-explain-hint");
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board14_ExplainPlan() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenQuery("shop", RunnableScript);
        await Screens.PumpAsync();
        QueryTabViewModel tab = bench.ViewModel.Tabs.OfType<QueryTabViewModel>().Last();
        await tab.RunAsync(all: true);
        tab.CaretOffset = RunnableScript.IndexOf("orderNo", StringComparison.Ordinal);
        await tab.ExplainCurrentAsync(null);
        await Screens.PumpAsync(80);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "14-explain");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.Explain.HasPlan, tab.Explain.Error ?? "");
    });
}
