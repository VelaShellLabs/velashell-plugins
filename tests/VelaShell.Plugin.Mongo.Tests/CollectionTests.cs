using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 集合工作台:暂存区的路径合并与乐观并发、提交 / 撤销 / 冲突(打真实服务器,用自己建的临时库)、
/// JSON 排版与光标路径、执行计划摘要,以及设计稿 01 / 02 / 13 的截图。
/// </summary>
[TestClass]
public sealed class CollectionTests
{
    // ── 暂存区:纯逻辑 ─────────────────────────────────────────────────────────

    private static BsonDocument Order() => new()
    {
        { "_id", new ObjectId("66f5c2a1f9a7056304b0e3b7") },
        { "orderNo", "SO2609-10403" },
        { "customer", new BsonDocument { { "name", "张伟" }, { "level", "SVIP" } } },
        { "items", new BsonArray { new BsonDocument { { "sku", "SKU-7710" }, { "qty", 2 } } } },
        { "status", "paid" },
        { "updatedAt", new BsonDateTime(new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)) }
    };

    [TestMethod]
    public void Setting_a_descendant_after_its_ancestor_is_merged_into_the_ancestor()
    {
        var edit = new StagedEdit(Order());
        edit.Set("customer", new BsonDocument("name", "李四"));
        edit.Set("customer.level", "VIP");

        Assert.AreEqual(1, edit.Changes.Count, "MongoDB rejects $set on both customer and customer.level");
        Assert.AreEqual("customer", edit.Changes[0].Path);
        Assert.AreEqual(new BsonDocument { { "name", "李四" }, { "level", "VIP" } }, edit.Changes[0].Value);
    }

    [TestMethod]
    public void Setting_an_ancestor_supersedes_its_staged_descendants()
    {
        var edit = new StagedEdit(Order());
        edit.Set("customer.name", "李四");
        edit.Set("customer.level", "VIP");
        edit.Set("customer", new BsonDocument("name", "王五"));

        Assert.AreEqual(1, edit.Changes.Count);
        Assert.AreEqual("customer", edit.Changes[0].Path);
        Assert.AreEqual(ChangeRelation.InsideChange, edit.RelationOf("customer.name"));

        var nested = new StagedEdit(Order());
        nested.Set("customer.name", "李四");
        Assert.AreEqual(ChangeRelation.ContainsChange, nested.RelationOf("customer"));
        Assert.AreEqual(ChangeRelation.Exact, nested.RelationOf("customer.name"));
    }

    [TestMethod]
    public void Changing_a_value_back_drops_the_change_and_type_changes_count()
    {
        var edit = new StagedEdit(Order());
        edit.Set("status", "shipped");
        edit.Set("status", "paid");
        Assert.IsTrue(edit.IsEmpty, "changed and changed back is not a pending change");

        edit.Set("items.0.qty", new BsonDouble(2));
        Assert.AreEqual(1, edit.Changes.Count, "Int32 2 → Double 2.0 is a type change and must be kept");
    }

    [TestMethod]
    public void Update_filter_carries_original_values_missing_fields_and_updatedAt()
    {
        var edit = new StagedEdit(Order());
        edit.Set("status", "shipped");
        edit.Set("note", "加急");
        edit.Set("orderNo", null);
        var op = new UpdateOperation(edit);

        BsonDocument filter = op.Filter;
        Assert.AreEqual(new BsonDocument("$eq", "paid"), filter["status"]);
        Assert.AreEqual(new BsonDocument("$exists", false), filter["note"]);
        Assert.AreEqual(new BsonDocument("$eq", "SO2609-10403"), filter["orderNo"]);
        Assert.IsTrue(filter.Contains("updatedAt"), "a document stamped with updatedAt is guarded by it as well");

        BsonDocument update = op.Update;
        Assert.AreEqual("shipped", update["$set"]["status"].AsString);
        Assert.AreEqual("加急", update["$set"]["note"].AsString);
        Assert.IsTrue(update["$unset"].AsBsonDocument.Contains("orderNo"));
    }

    [TestMethod]
    public void Deleting_supersedes_edits_and_snapshots_restore_everything()
    {
        var area = new StagingArea();
        BsonDocument order = Order();
        area.SetField(order, "status", "shipped");
        area.AddInsert(new BsonDocument("orderNo", "SO-NEW"));
        StagingArea.Snapshot snapshot = area.TakeSnapshot();

        area.MarkDeleted(order);
        Assert.AreEqual(0, area.EditCount, "a staged delete drops the staged edits of that document");
        Assert.AreEqual(1, area.DeleteCount);

        area.Clear();
        Assert.IsTrue(area.IsEmpty);
        area.Restore(snapshot);
        Assert.AreEqual(1, area.EditCount);
        Assert.AreEqual(1, area.InsertCount);
        Assert.AreEqual("shipped", area.Current(order)["status"].AsString);
    }

    [TestMethod]
    public void Commit_plan_respects_the_scope()
    {
        var area = new StagingArea();
        BsonDocument a = Order();
        BsonDocument b = Order();
        b["_id"] = ObjectId.GenerateNewId();
        area.SetField(a, "status", "shipped");
        area.SetField(b, "status", "refunded");
        StagedInsert insert = area.AddInsert(new BsonDocument("n", 1));

        Assert.AreEqual(3, StagingCommitter.Plan(area, CommitScope.All).Count);
        IReadOnlyList<StagedOperation> onlyA = StagingCommitter.Plan(area, new CommitScope(a["_id"]));
        Assert.AreEqual(1, onlyA.Count);
        Assert.AreEqual(a["_id"], ((UpdateOperation)onlyA[0]).Edit.Id);
        IReadOnlyList<StagedOperation> onlyInsert = StagingCommitter.Plan(area, new CommitScope(OnlyInsert: insert));
        Assert.IsInstanceOfType<InsertOperation>(onlyInsert.Single());
    }

    [TestMethod]
    public void Document_diff_recurses_into_documents_and_replaces_arrays()
    {
        BsonDocument before = Order();
        BsonDocument after = Order();
        after["customer"]["name"] = "李四";
        after["items"][0]["qty"] = 3;
        after.Remove("status");
        after["invoice"] = new BsonDocument("title", "上海云帆");

        List<(string Path, BsonValue? Value)> diff = DocumentDiff.Compute(before, after);
        CollectionAssert.AreEquivalent(new[] { "customer.name", "items", "status", "invoice" }, diff.Select(static d => d.Path).ToArray());
        Assert.IsNull(diff.Single(static d => d.Path == "status").Value, "a removed field becomes $unset");
    }

    // ── 排版、光标路径、执行计划 ────────────────────────────────────────────────

    [TestMethod]
    public void Card_printer_keeps_short_containers_inline_and_maps_lines_to_paths()
    {
        PrintedCard printed = CardPrinter.Print(Order(), EjsonMode.Shell);
        string[] lines = printed.Text.Split('\n');

        int customer = printed.LineOf("customer");
        StringAssert.Contains(lines[customer - 1], "customer: { name: \"张伟\", level: \"SVIP\" }");
        Assert.AreEqual(customer, printed.LineOf("customer.name"), "a field printed inline maps to its container's line");
        StringAssert.Contains(lines[printed.LineOf("status") - 1], "status: \"paid\"");

        PrintedCard relaxed = CardPrinter.Print(Order(), EjsonMode.Relaxed);
        StringAssert.Contains(relaxed.Text, "\"status\": \"paid\"");

        PrintedCard preview = CardPrinter.Print(Order(), EjsonMode.Shell, static n => $"…{n} 项");
        StringAssert.Contains(preview.Text, "items: [ …1 项 ]", "the read-only preview folds arrays of documents");
        Assert.AreEqual(preview.LineOf("items"), preview.LineOf("items.0.qty"), "a change inside a folded array marks the array line");
    }

    [TestMethod]
    public void Caret_scanner_finds_the_field_under_the_caret()
    {
        const string text = "{ status: \"pa";
        CaretPathScanner.Result value = CaretPathScanner.Scan(text, text.Length);
        Assert.IsTrue(value.InValue);
        Assert.AreEqual("status", value.Path);
        Assert.AreEqual(text.IndexOf('"'), value.TokenStart);

        const string nested = "{ total: { $gte: ";
        CaretPathScanner.Result inner = CaretPathScanner.Scan(nested, nested.Length);
        Assert.AreEqual("total.$gte", inner.Path);

        const string key = "{\n  customer: {\n    na";
        CaretPathScanner.Result keyPosition = CaretPathScanner.Scan(key, key.Length);
        Assert.IsFalse(keyPosition.InValue);
        Assert.AreEqual("customer", keyPosition.ParentPath);
        Assert.AreEqual(2, CaretPathScanner.Scan(key, -1).KeyLines["customer"]);
    }

    [TestMethod]
    public void Plan_summary_names_the_winning_scan()
    {
        BsonDocument classic = BsonDocument.Parse("""
            { queryPlanner: { winningPlan: { stage: "LIMIT", inputStage: { stage: "FETCH",
              inputStage: { stage: "IXSCAN", indexName: "status_1_createdAt_-1" } } } } }
            """);
        Assert.AreEqual("IXSCAN status_1_createdAt_-1", CollectionTabViewModel.SummarizePlan(classic));

        BsonDocument sbe = BsonDocument.Parse("{ queryPlanner: { winningPlan: { queryPlan: { stage: \"COLLSCAN\" } } } }");
        Assert.AreEqual("COLLSCAN", CollectionTabViewModel.SummarizePlan(sbe));
    }

    [TestMethod]
    public void Filter_by_value_quotes_dotted_paths()
    {
        Assert.AreEqual("{ \"items.sku\": \"SKU-7710\" }", CollectionTabViewModel.FilterFor("items.sku", "SKU-7710"));
        Assert.AreEqual("{ note: { $exists: false } }", CollectionTabViewModel.FilterFor("note", null));
    }

    [TestMethod]
    public void Column_widths_follow_the_kind()
    {
        Assert.AreEqual(180d, CollectionTabViewModel.EstimateWidth("_id", BsonKind.ObjectId, 24));
        Assert.AreEqual(156d, CollectionTabViewModel.EstimateWidth("createdAt", BsonKind.Date, 19));
        Assert.IsTrue(CollectionTabViewModel.EstimateWidth("note", BsonKind.String, 200) <= 280);
    }

    [TestMethod]
    public void Localization_tables_load_with_collection_and_state_keys()
    {
        var zh = new Loc("zh-CN");
        var en = new Loc("en");
        Assert.AreEqual("待提交：", zh["Cw_PendingPrefix"]);
        Assert.AreEqual("Edit conflict", en["State_ConflictTitle"]);
        Assert.IsTrue(Loc.AllKeys.Count(static k => k.StartsWith("Cw_", StringComparison.Ordinal)) > 100);
    }

    // ── 提交 / 撤销 / 冲突:真实服务器,临时库 ────────────────────────────────────

    private static async Task<(IMongoClient Client, string Database, IMongoCollection<BsonDocument> Collection, BsonDocument[] Docs)> SeedAsync()
    {
        var client = new MongoClient(TestServer.Uri);
        string database = "velashell_collection_" + Guid.NewGuid().ToString("N")[..8];
        IMongoCollection<BsonDocument> collection = client.GetDatabase(database).GetCollection<BsonDocument>("orders");
        BsonDocument[] docs =
        [
            new() { { "_id", 1 }, { "status", "paid" }, { "customer", new BsonDocument("name", "陈立") }, { "qty", 2 } },
            new() { { "_id", 2 }, { "status", "pending" }, { "customer", new BsonDocument("name", "王芳") }, { "qty", 1 } },
            new() { { "_id", 3 }, { "status", "paid" }, { "customer", new BsonDocument("name", "李娜") }, { "qty", 5 } }
        ];
        await collection.InsertManyAsync(docs.Select(static d => (BsonDocument)d.DeepClone()));
        return (client, database, collection, docs);
    }

    [TestMethod]
    public async Task Commit_sends_one_batch_and_undo_reverts_it()
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database, IMongoCollection<BsonDocument> collection, BsonDocument[] docs) = await SeedAsync();
        try
        {
            var area = new StagingArea();
            area.SetField(docs[0], "status", "shipped");
            area.SetField(docs[0], "customer.name", "陈立(改)");
            area.MarkDeleted(docs[1]);
            area.AddInsert(new BsonDocument { { "status", "new" }, { "qty", 9 } });

            CommitOutcome outcome = await StagingCommitter.CommitAsync(collection, area, CommitScope.All);
            Assert.AreEqual(1, outcome.Updated);
            Assert.AreEqual(1, outcome.Inserted);
            Assert.AreEqual(1, outcome.Deleted);
            Assert.AreEqual(0, outcome.Conflicts.Count);
            Assert.IsTrue(area.IsEmpty, "everything that was written leaves the staging area");

            BsonDocument first = await collection.Find(new BsonDocument("_id", 1)).FirstAsync();
            Assert.AreEqual("shipped", first["status"].AsString);
            Assert.AreEqual("陈立(改)", first["customer"]["name"].AsString);
            long afterCommit = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(3L, afterCommit);

            (int restored, int skipped) = await outcome.Undo.ExecuteAsync(collection);
            Assert.AreEqual(3, restored);
            Assert.AreEqual(0, skipped);
            BsonDocument reverted = await collection.Find(new BsonDocument("_id", 1)).FirstAsync();
            Assert.AreEqual("paid", reverted["status"].AsString);
            Assert.AreEqual("陈立", reverted["customer"]["name"].AsString);
            long back = await collection.CountDocumentsAsync(new BsonDocument("_id", 2));
            Assert.AreEqual(1L, back, "the deleted document is inserted back");
            long gone = await collection.CountDocumentsAsync(new BsonDocument("status", "new"));
            Assert.AreEqual(0L, gone, "the inserted document is removed again");
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    [TestMethod]
    public async Task A_concurrent_change_is_reported_as_a_conflict_and_can_be_overwritten()
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database, IMongoCollection<BsonDocument> collection, BsonDocument[] docs) = await SeedAsync();
        try
        {
            var area = new StagingArea();
            area.SetField(docs[0], "status", "shipped");
            area.SetField(docs[2], "qty", 6);
            // 另一个会话在我提交前改了 #1 的 status。
            await collection.UpdateOneAsync(new BsonDocument("_id", 1), new BsonDocument("$set", new BsonDocument("status", "refunded")));

            CommitOutcome outcome = await StagingCommitter.CommitAsync(collection, area, CommitScope.All);
            Assert.AreEqual(1, outcome.Updated, "the unrelated document still goes through");
            Assert.AreEqual(1, outcome.Conflicts.Count);
            EditConflict conflict = outcome.Conflicts[0];
            Assert.AreEqual("refunded", conflict.Server!["status"].AsString);
            Assert.AreEqual(1, area.EditCount, "the conflicting edit stays staged");
            BsonDocument untouched = await collection.Find(new BsonDocument("_id", 1)).FirstAsync();
            Assert.AreEqual("refunded", untouched["status"].AsString, "a conflict never overwrites the other session's write");

            area.Rebase(conflict.Edit.Id, conflict.Server, new HashSet<string>(StringComparer.Ordinal) { "status" });
            CommitOutcome retry = await StagingCommitter.CommitAsync(collection, area, new CommitScope(conflict.Edit.Id));
            Assert.AreEqual(1, retry.Updated);
            BsonDocument overwritten = await collection.Find(new BsonDocument("_id", 1)).FirstAsync();
            Assert.AreEqual("shipped", overwritten["status"].AsString);
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    // ── 工作台:真实服务器上的端到端 ───────────────────────────────────────────

    private static async Task<CollectionTabViewModel> OpenTabAsync(Workbench bench, string database, string collection)
    {
        bench.Session.OpenCollection(database, collection);
        var tab = (CollectionTabViewModel)bench.ViewModel.ActiveTab!;
        for (int i = 0; i < 200 && (tab.IsLoading || tab.Rows.Count == 0); i++)
        {
            await Screens.PumpAsync(5);
        }
        await Screens.PumpAsync(20);
        return tab;
    }

    [TestMethod]
    public void Editing_a_cell_stages_it_and_apply_writes_it() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        (IMongoClient client, string database, IMongoCollection<BsonDocument> collection, _) = await SeedAsync();
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database, "orders");
            Assert.AreEqual(3, tab.Rows.Count);
            CollectionAssert.AreEqual(new[] { "_id", "status", "customer", "qty" }, tab.Columns.Select(static c => c.Name).ToArray());

            CollectionRow row = tab.Rows[0];
            CollectionCell status = row.CellOf(tab.Columns.Single(static c => c.Name == "status"))!;
            tab.BeginCellEdit(status);
            Assert.IsNotNull(status.Editor);
            status.Editor!.Text = "shipped";
            Assert.IsTrue(tab.CommitCellEdit(status));
            Assert.IsTrue(tab.IsModified, "the tab shows the orange dot while something is staged");
            Assert.AreEqual(CollectionRowState.Modified, tab.Rows[0].State);
            StringAssert.Contains(tab.PendingText, "1");
            StringAssert.Contains(tab.StatusText, "1 项待提交");

            CollectionCell qty = tab.Rows[1].CellOf(tab.Columns.Single(static c => c.Name == "qty"))!;
            tab.BeginCellEdit(qty);
            qty.Editor!.Text = "abc";
            Assert.IsFalse(tab.CommitCellEdit(qty), "text that does not parse as Int32 keeps the editor open");
            Assert.IsNotNull(qty.Editor.Error);
            CollectionTabViewModel.CancelCellEdit(qty);

            tab.ApplyCommand.Execute(null);
            for (int i = 0; i < 100 && tab.HasPending; i++)
            {
                await Screens.PumpAsync(5);
            }
            await Screens.PumpAsync(20);
            Assert.IsFalse(tab.HasPending);
            BsonDocument written = await collection.Find(new BsonDocument("_id", 1)).FirstAsync();
            Assert.AreEqual("shipped", written["status"].AsString);
            Assert.IsTrue(bench.ViewModel.Toasts.Any(static t => t.HasAction), "the success toast carries an undo action");
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    [TestMethod]
    public void Read_only_mode_blocks_staging_and_shows_the_banner() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "orders");
        bench.Session.Guard.IsReadOnly = true;
        await Screens.PumpAsync(5);

        Assert.IsTrue(tab.ShowReadOnlyBanner);
        Assert.IsFalse(tab.CanEdit);
        CollectionCell cell = tab.Rows[0].CellOf(tab.Columns.Single(static c => c.Name == "status"))!;
        tab.BeginCellEdit(cell);
        Assert.IsNull(cell.Editor, "read-only mode refuses to start an edit");
        Assert.IsTrue(tab.Staging.IsEmpty);
        await Screens.PumpAsync(5);
        Assert.IsTrue(bench.ViewModel.Toasts.Count > 0, "the refusal explains how to unlock");
    });

    [TestMethod]
    public void A_view_is_never_editable() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "v_order_summary");
        Assert.IsFalse(tab.IsEditable);
        Assert.IsFalse(tab.CanEdit);
        Assert.IsTrue(tab.ShowViewBanner);
        Assert.IsTrue(tab.Rows.Count > 0);
    });

    [TestMethod]
    public void Filter_errors_are_flagged_before_anything_is_sent() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "orders");

        tab.FilterText = "{ items.sku: \"SKU-7710\" }";
        Assert.IsTrue(tab.HasFilterError, "dotted bare keys must be quoted, as in mongosh");
        Assert.IsTrue(tab.FilterDiagnostics.Count > 0);
        tab.FilterText = "{ status: \"refunded\", total: { $gte: 1e12 } }";
        Assert.IsFalse(tab.HasFilterError);
        await tab.RunQueryAsync(resetPage: true);
        await Screens.PumpAsync(10);
        Assert.IsTrue(tab.IsNoResult, "an empty result with a filter shows the no-result card, not the empty-collection card");
        Assert.IsFalse(tab.IsEmptyCollection);
    });

    [TestMethod]
    public void Enter_in_the_filter_box_runs_the_query_instead_of_adding_a_line() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "orders");
        CodeEditor filter = ViewOf(bench).FindControl<CodeEditor>("FilterEditor")!;
        tab.FilterText = "{ status: \"shipped\" }";
        await Screens.PumpAsync(5);
        filter.FocusEditor();
        filter.Editor.CaretOffset = filter.Editor.Document.TextLength;
        await Screens.PumpAsync(5);
        bench.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Screens.PumpAsync(10);
        for (int i = 0; i < 100 && tab.IsLoading; i++)
        {
            await Screens.PumpAsync(5);
        }
        Assert.AreEqual("{ status: \"shipped\" }", tab.FilterText, "Enter must not insert a line break into the one-line filter");
        Assert.AreEqual(tab.FilterText, tab.LastRunFilter);
        Assert.IsTrue(tab.Rows.Count > 0);
        Assert.IsTrue(tab.Rows.All(static r => r.Document["status"].AsString == "shipped"));
    });

    [TestMethod]
    public void Keyboard_editing_in_the_grid_stages_the_value() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "orders");
        CollectionRow row = tab.Rows[0];
        tab.SelectedRow = row;
        tab.CurrentColumn = tab.Columns.Single(static c => c.Name == "status");
        await Screens.PumpAsync(5);
        ((Control)ViewOf(bench).Grid.List.ContainerFromItem(row)!).Focus();
        await Screens.PumpAsync(5);
        bench.Window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
        await Screens.PumpAsync(10);
        CollectionCell cell = row.CellOf(tab.CurrentColumn)!;
        Assert.IsTrue(cell.IsEditing, "F2 edits the current cell");
        bench.Window.KeyTextInput("on-hold");
        await Screens.PumpAsync(5);
        bench.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        await Screens.PumpAsync(10);
        StagedEdit? edit = tab.Staging.EditOf(row.Id);
        Assert.IsNotNull(edit, "Enter writes the value into the staging area");
        Assert.AreEqual("on-hold", edit.Find("status")!.Value!.AsString);
        tab.Staging.Clear();
    });

    [TestMethod]
    public void Fifty_thousand_rows_stay_responsive() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        var client = new MongoClient(TestServer.Uri);
        string database = "velashell_collection_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            IMongoCollection<BsonDocument> collection = client.GetDatabase(database).GetCollection<BsonDocument>("big");
            await collection.InsertManyAsync(Enumerable.Range(0, 50_000).Select(static i => new BsonDocument
            {
                { "n", i },
                { "name", "row-" + i },
                { "tags", new BsonArray { "a", "b" } },
                { "at", new BsonDateTime(DateTime.UtcNow) }
            }));
            await using Workbench bench = await Screens.OpenWorkbenchAsync(database);
            CollectionTabViewModel tab = await OpenTabAsync(bench, database, "big");
            tab.LimitText = "50000";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await tab.RunQueryAsync(resetPage: true);
            await Screens.PumpAsync(10);
            watch.Stop();
            Assert.AreEqual(50_000, tab.Rows.Count);
            Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(8), $"50k rows took {watch.Elapsed}");

            var stage = System.Diagnostics.Stopwatch.StartNew();
            tab.Stage(tab.Rows[10], "name", "changed");
            stage.Stop();
            Assert.IsTrue(stage.Elapsed < TimeSpan.FromMilliseconds(500), $"staging one cell in a 50k page took {stage.Elapsed}");
            ViewOf(bench).Grid.List.ScrollIntoView(tab.Rows[40_000]);
            await Screens.PumpAsync(10);
            tab.Staging.Clear();
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    });

    // ── 截图(设计稿 01 / 02 / 13) ──────────────────────────────────────────────

    private static CollectionTabView ViewOf(Workbench bench) =>
        bench.Window.GetVisualDescendants().OfType<CollectionTabView>().First(static v => v.IsEffectivelyVisible);

    /// <summary>
    /// 把 orders 摆成设计稿的样子:按 createdAt 倒序、每页 17 行(新增行落在可视区里),
    /// 筛选框里放设计稿那条筛选(只显示、不执行)。之后的修改都只进暂存区 —— 不提交,不碰 shop 的数据。
    /// </summary>
    private static async Task<CollectionTabViewModel> PrepareOrdersAsync(Workbench bench, int limit = 17)
    {
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "orders");
        tab.SortText = "{ createdAt: -1 }";
        tab.LimitText = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await tab.RunQueryAsync(resetPage: true);
        await Screens.PumpAsync(20);
        tab.FilterText = "{ status: \"paid\", total: { $gte: 500 }, createdAt: { $gte: ISODate(\"2026-08-27\") } }";
        tab.ProjectionText = "{ note: 0 }";
        return tab;
    }

    /// <summary>换一个与现值不同的状态(暂存一个与原值相同的值是空操作)。</summary>
    private static string OtherStatus(CollectionRow row) => row.Document.GetValue("status", "").AsString == "shipped" ? "refunded" : "shipped";

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board01_collection_grid_with_inspector() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await PrepareOrdersAsync(bench);
        CollectionColumn total = tab.Columns.Single(static c => c.Name == "total");
        tab.Stage(tab.Rows[2], "status", OtherStatus(tab.Rows[2]));
        tab.StageDelete([tab.Rows[8]]);
        tab.AddRowCommand.Execute(null);
        await Screens.PumpAsync(10);
        CollectionRow added = tab.Rows[^1];
        tab.Stage(added, "orderNo", "SO2609-10418");
        tab.Stage(added, "status", "pending");

        tab.SelectedRow = tab.Rows[3];
        await Screens.PumpAsync(10);
        tab.BeginCellEdit(tab.Rows[3].CellOf(total)!);
        InspectorField name = tab.Inspector.Fields.First(static f => f.Path == "customer.name");
        tab.Inspector.BeginEdit(name);
        await Screens.PumpAsync(30);
        CollectionTabView view = ViewOf(bench);
        view.Grid.List.ScrollIntoView(tab.Rows[0]);
        await Screens.PumpAsync(30);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "01-collection");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.Inspector.Fields.Count > 5, "the inspector lists the selected document's fields");
        Assert.AreEqual(1, tab.Staging.EditCount);
        Assert.AreEqual(1, tab.Staging.InsertCount);
        Assert.AreEqual(1, tab.Staging.DeleteCount);

        tab.Inspector.Page = 1;
        await Screens.PumpAsync(20);
        Screens.Capture(bench.Window, "01-collection-inspector-json");
        StringAssert.Contains(tab.Inspector.JsonText, "SO2609-10403");
        tab.Inspector.Page = 2;
        for (int i = 0; i < 60 && tab.Stats is null; i++)
        {
            await Screens.PumpAsync(5);
        }
        await Screens.PumpAsync(10);
        Screens.Capture(bench.Window, "01-collection-inspector-info");
        Assert.IsTrue(tab.Inspector.InfoLines.Count > 5, "the collection page lists count, sizes and indexes");
        tab.Staging.Clear();
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board02_collection_json_cards() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await PrepareOrdersAsync(bench);
        tab.Ejson = EjsonMode.Shell;
        tab.Stage(tab.Rows[2], "status", OtherStatus(tab.Rows[2]));
        tab.ViewMode = CollectionViewMode.Json;
        await Screens.PumpAsync(20);
        JsonCardViewModel card = tab.Cards[3];
        card.BeginEdit();
        string status = tab.Rows[3].Document.GetValue("status", "").AsString;
        card.EditText = card.EditText.Replace($"status: \"{status}\"", "status: \"\"", StringComparison.Ordinal)
            .Replace("\n}", ",\n  invoice: { title: \"上海云帆科技有限公司\", taxNo: \"91310115MA1K4100\" }\n}", StringComparison.Ordinal);
        await Screens.PumpAsync(30);
        CollectionTabView view = ViewOf(bench);
        view.Json.List.ScrollIntoView(tab.Cards[3]);
        await Screens.PumpAsync(10);
        view.Json.List.ScrollIntoView(tab.Cards[2]);
        await Screens.PumpAsync(30);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "02-collection-json");
        Assert.IsNotNull(frame);
        Assert.IsTrue(card.Outline.Count > 5, "the outline lists the edited card's fields");
        Assert.IsTrue(card.EditMarks.Count >= 2, "the edited and the added field are marked while editing");
        Assert.IsTrue(tab.Cards[2].Marks.Count > 0, "a staged change is marked on the read-only card");
        tab.Staging.Clear();
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board13_collection_tree() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await PrepareOrdersAsync(bench, limit: 50);
        CollectionRow second = tab.Rows.First(static r => r.Document.GetValue("items", new BsonArray()).AsBsonArray.Count > 1);
        tab.ViewMode = CollectionViewMode.Tree;
        tab.ExpandTreePath(second, "customer");
        tab.ExpandTreePath(second, "items.0");
        int qty = second.Document["items"][0]["qty"].ToInt32();
        tab.Stage(second, "items.0.qty", qty + 1);
        await Screens.PumpAsync(10);
        tab.SelectedTreeRow = tab.TreeRows.FirstOrDefault(r => ReferenceEquals(r.Row, second) && r.Path == "items.0.sku");
        await Screens.PumpAsync(30);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "13-collection-tree");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.TreeRows.Any(static r => r.IsModified), "the staged qty is marked in the tree");
        tab.Staging.Clear();
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board22_collection_states_and_conflict() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        CollectionTabViewModel tab = await OpenTabAsync(bench, Screens.Database, "orders");
        tab.FilterText = "{ status: \"refunded\", total: { $gte: 50000 } }";
        await tab.RunQueryAsync(resetPage: true);
        bench.Session.Guard.IsReadOnly = true;
        await Screens.PumpAsync(30);
        WriteableBitmap? states = Screens.Capture(bench.Window, "22-collection-states");
        Assert.IsNotNull(states);
        Assert.IsTrue(tab.IsNoResult);
        Assert.IsTrue(tab.ShowReadOnlyBanner);

        var stamp = new BsonDateTime(new DateTime(2026, 9, 27, 13, 8, 51, DateTimeKind.Utc));
        var original = new BsonDocument { { "_id", new ObjectId("66f5c2a1f9a7056304b0e3b7") }, { "status", "paid" }, { "note", BsonNull.Value }, { "updatedAt", stamp } };
        var edit = new StagedEdit(original);
        edit.Set("status", "shipped");
        edit.Set("shippedAt", new BsonDateTime(new DateTime(2026, 9, 27, 2, 30, 0, DateTimeKind.Utc)));
        edit.Set("note", "加急");
        var server = (BsonDocument)original.DeepClone();
        server["status"] = "refunded";
        server["note"] = "客户申请退款";
        server["updatedAt"] = new BsonDateTime(stamp.ToUniversalTime().AddSeconds(39));
        var dialog = new ConflictDialogViewModel(bench.Session, new EditConflict(edit, server), static _ => Task.CompletedTask);
        bench.ViewModel.ShowDialog(dialog);
        await Screens.PumpAsync(30);
        WriteableBitmap? conflict = Screens.Capture(bench.Window, "22-collection-conflict");
        Assert.IsNotNull(conflict);
        Assert.AreEqual(3, dialog.Rows.Count);
        Assert.IsTrue(dialog.Rows.Single(static r => r.Path == "status").BothChanged);
        Assert.IsFalse(dialog.Rows.Single(static r => r.Path == "shippedAt").BothChanged);
        dialog.Rows[0].PickServerCommand.Execute(null);
        Assert.IsTrue(dialog.Rows[0].UseServer);
        bench.Session.Guard.IsReadOnly = false;
    });
}
