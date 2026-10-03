using System.Diagnostics;
using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 对象列表(设计稿 12):结构脚本生成、对象行的拼装、权限匹配,以及连真实服务器的加载、删除、清空与截图。
/// 写操作只碰自己建的 <c>velashell_objects_*</c> 库,结尾删掉它。
/// </summary>
[TestClass]
public sealed class ObjectsTests
{
    private static readonly Loc Zh = new("zh-CN");

    // ── 结构脚本 ─────────────────────────────────────────────────────────

    /// <summary>时序集合的脚本:剥掉由粒度派生的 bucketMaxSpanSeconds,Int64 收窄成普通数字。</summary>
    [TestMethod]
    public void Ddl_of_timeseries_drops_derived_bucket_span_and_narrows_int64()
    {
        var options = new BsonDocument
        {
            { "timeseries", new BsonDocument { { "timeField", "ts" }, { "metaField", "device" }, { "granularity", "minutes" }, { "bucketMaxSpanSeconds", 86400 } } },
            { "expireAfterSeconds", new BsonInt64(7_776_000) }
        };
        var info = new CollectionInfo("shop", "events", CollectionKind.TimeSeries, options);

        string ddl = ObjectScripts.Ddl(info, [new BsonDocument { { "v", 2 }, { "key", new BsonDocument { { "device", 1 }, { "ts", 1 } } }, { "name", "device_1_ts_1" } }]);

        StringAssert.StartsWith(ddl, "db.createCollection(\n  \"events\",");
        StringAssert.Contains(ddl, "granularity: \"minutes\"");
        StringAssert.Contains(ddl, "expireAfterSeconds: 7776000");
        Assert.IsFalse(ddl.Contains("bucketMaxSpanSeconds", StringComparison.Ordinal), ddl);
        Assert.IsFalse(ddl.Contains("NumberLong", StringComparison.Ordinal), ddl);
        StringAssert.Contains(ddl, "db.events.createIndex({ device: 1, ts: 1 }, { name: \"device_1_ts_1\" })");
    }

    /// <summary>二级索引(含 TTL)与验证规则都进脚本,_id 索引不进。</summary>
    [TestMethod]
    public void Ddl_replays_secondary_indexes_and_validator_but_not_the_id_index()
    {
        var options = new BsonDocument
        {
            { "validator", new BsonDocument("$jsonSchema", new BsonDocument("required", new BsonArray { "name" })) },
            { "validationLevel", "moderate" },
            { "validationAction", "warn" }
        };
        var info = new CollectionInfo("shop", "carts", CollectionKind.Collection, options);
        BsonDocument[] indexes =
        [
            new() { { "v", 2 }, { "key", new BsonDocument("_id", 1) }, { "name", "_id_" } },
            new() { { "v", 2 }, { "key", new BsonDocument("updatedAt", 1) }, { "name", "updatedAt_1" }, { "expireAfterSeconds", 86400 } }
        ];

        string ddl = ObjectScripts.Ddl(info, indexes);

        StringAssert.StartsWith(ddl, "db.createCollection(\"carts\")");
        Assert.IsFalse(ddl.Contains("_id_", StringComparison.Ordinal), ddl);
        StringAssert.Contains(ddl, "db.carts.createIndex({ updatedAt: 1 }, { name: \"updatedAt_1\", expireAfterSeconds: 86400 })");
        StringAssert.Contains(ddl, "collMod: \"carts\"");
        StringAssert.Contains(ddl, "validationLevel: \"moderate\"");
        StringAssert.Contains(ddl, "validationAction: \"warn\"");
    }

    /// <summary>视图写成 createView + 管道。</summary>
    [TestMethod]
    public void Ddl_of_a_view_is_createView_with_the_pipeline()
    {
        var options = new BsonDocument
        {
            { "viewOn", "orders" },
            { "pipeline", new BsonArray { new BsonDocument("$match", new BsonDocument("status", "paid")) } }
        };
        string ddl = ObjectScripts.Ddl(new CollectionInfo("shop", "v_paid", CollectionKind.View, options), []);

        StringAssert.StartsWith(ddl, "db.createView(\n  \"v_paid\",\n  \"orders\",");
        StringAssert.Contains(ddl, "$match");
    }

    /// <summary>不是合法标识符的集合名走 getCollection("…")。</summary>
    [TestMethod]
    public void Odd_collection_names_go_through_getCollection()
    {
        string line = ObjectScripts.CreateIndex("weird-name", new BsonDocument { { "key", new BsonDocument("a", 1) }, { "name", "a_1" } });
        StringAssert.StartsWith(line, "db.getCollection(\"weird-name\").createIndex(");
    }

    // ── 对象行 ───────────────────────────────────────────────────────────

    /// <summary>files / chunks 合成一个桶行,系统集合不列,固定集合徽章带上限。</summary>
    [TestMethod]
    public void Items_merge_gridfs_halves_into_one_bucket_and_hide_system_collections()
    {
        CollectionInfo[] list =
        [
            new("shop", "orders", CollectionKind.Collection, []),
            new("shop", "fs.files", CollectionKind.Collection, []),
            new("shop", "fs.chunks", CollectionKind.Collection, []),
            new("shop", "system.views", CollectionKind.System, []),
            new("shop", "audit_log", CollectionKind.Capped, new BsonDocument { { "capped", true }, { "size", 1L << 30 } }),
            new("shop", "v1", CollectionKind.View, new BsonDocument("viewOn", "orders"))
        ];

        List<ObjectItem> items = ObjectsTabViewModel.BuildItems("shop", list, Zh);

        Assert.AreEqual(4, items.Count);
        ObjectItem bucket = items.Single(static i => i.Kind == ObjectKind.Bucket);
        Assert.AreEqual("fs", bucket.Name);
        Assert.AreEqual("fs.files / chunks", bucket.DisplayName);
        Assert.AreEqual("固定 1 GB", items.Single(static i => i.Name == "audit_log").TypeText);
        Assert.AreEqual("—", items.Single(static i => i.Name == "v1").CountText);
    }

    /// <summary>文档数一千万以下写全,以上缩写。</summary>
    [TestMethod]
    public void Count_column_switches_to_abbreviation_from_ten_million()
    {
        var item = new ObjectItem(new CollectionInfo("shop", "a", CollectionKind.Collection, []), Zh);
        item.SetStats(new CollectionStats { Count = 1_284_902, Size = 10 });
        Assert.AreEqual("1,284,902", item.CountText);
        item.SetStats(new CollectionStats { Count = 42_700_000, Size = 10 });
        Assert.AreEqual("42.7M", item.CountText);
    }

    /// <summary>权限页的资源匹配与服务器一致:空库名 = 任意库,空集合名 = 任意非系统集合,cluster 资源不算。</summary>
    [TestMethod]
    public void Effective_actions_follow_server_resource_matching()
    {
        var privileges = new BsonArray
        {
            new BsonDocument { { "resource", new BsonDocument { { "db", "shop" }, { "collection", "" } } }, { "actions", new BsonArray { "find", "listIndexes" } } },
            new BsonDocument { { "resource", new BsonDocument { { "db", "" }, { "collection", "orders" } } }, { "actions", new BsonArray { "insert" } } },
            new BsonDocument { { "resource", new BsonDocument { { "db", "logs" }, { "collection", "" } } }, { "actions", new BsonArray { "remove" } } },
            new BsonDocument { { "resource", new BsonDocument("cluster", true) }, { "actions", new BsonArray { "serverStatus" } } }
        };

        IReadOnlyCollection<string> orders = ObjectsTabViewModel.EffectiveActions(privileges, "shop", "orders");
        IReadOnlyCollection<string> system = ObjectsTabViewModel.EffectiveActions(privileges, "shop", "system.profile");

        CollectionAssert.AreEquivalent(new[] { "find", "insert", "listIndexes" }, orders.ToArray());
        Assert.AreEqual(0, system.Count);
    }

    // ── 连服务器 ─────────────────────────────────────────────────────────

    /// <summary>真服务器:shop 的对象、种类、统计、过滤、搜索与详情面板。</summary>
    [TestMethod]
    public void Objects_tab_lists_shop_with_kinds_and_stats() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        ObjectsTabViewModel tab = bench.ViewModel.Tabs.OfType<ObjectsTabViewModel>().Single(static t => t.Database == "shop");
        await tab.ReloadAsync();
        await WaitUntilAsync(() => tab.Items.Where(static i => !i.IsView).All(static i => i.HasStats));

        Assert.AreEqual(tab.AllCount, tab.CollectionCount + tab.ViewCount + tab.GridFsCount);
        Assert.IsTrue(tab.Items.Any(static i => i is { Name: "events", Kind: ObjectKind.TimeSeries }), "events should be a time-series row");
        Assert.IsTrue(tab.Items.Any(static i => i is { Name: "fs", Kind: ObjectKind.Bucket }), "fs.files + fs.chunks should merge into one bucket row");
        Assert.IsTrue(tab.Items.Any(static i => i is { Name: "products", HasValidator: true }), "products carries a validator");
        Assert.IsTrue(tab.Items.Any(static i => i is { Name: "carts", IsTtl: true }), "carts has a TTL index");
        Assert.IsFalse(tab.Items.Any(static i => i.Name.StartsWith("system.", StringComparison.Ordinal)));

        tab.Filter = ObjectFilter.Views;
        Assert.IsTrue(tab.Items.All(static i => i.IsView));
        tab.Filter = ObjectFilter.All;
        tab.SearchText = "order";
        Assert.IsTrue(tab.Items.All(static i => i.DisplayName.Contains("order", StringComparison.OrdinalIgnoreCase)));
        tab.SearchText = "";

        // 选中后详情面板有小节、DDL 与权限页(本机无认证)。
        tab.SelectedItem = tab.Items.Single(static i => i.Name == "events");
        await WaitUntilAsync(() => tab.DdlText.Length > 0 && tab.PrivilegeNotice.Length > 0);
        StringAssert.Contains(tab.DdlText, "timeseries");
        Assert.AreEqual("时序选项", tab.Sections[0].Title);
        StringAssert.Contains(tab.StatusText, "events");
    });

    /// <summary>真服务器(自建库):清空与删除都经确认框,有数据的删除要手打名称。</summary>
    [TestMethod]
    public void Drop_and_empty_go_through_confirmation_and_hit_the_server() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_objects_{Guid.NewGuid():N}"[..28];
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        IMongoDatabase database = bench.Connection.Database(db);
        try
        {
            await database.GetCollection<BsonDocument>("doomed").InsertManyAsync([new BsonDocument("n", 1), new BsonDocument("n", 2)]);
            await database.GetCollection<BsonDocument>("keep").InsertManyAsync([new BsonDocument("n", 1), new BsonDocument("n", 2), new BsonDocument("n", 3)]);
            bench.Session.OpenObjects(db);
            ObjectsTabViewModel tab = bench.ViewModel.Tabs.OfType<ObjectsTabViewModel>().Single(t => t.Database == db);
            await tab.ReloadAsync();

            // 清空:确认框不要求手打(开发环境、未开写前确认),点确认即删光文档。
            tab.SelectedItem = tab.Items.Single(static i => i.Name == "keep");
            tab.EmptyCommand.Execute(null);
            ConfirmDialogViewModel empty = await WaitForConfirmAsync(bench);
            Assert.IsFalse(empty.RequiresTyping);
            empty.ConfirmCommand.Execute(null);
            await WaitUntilAsync(() => database.GetCollection<BsonDocument>("keep").CountDocuments(FilterDefinition<BsonDocument>.Empty) == 0);
            long left = await database.GetCollection<BsonDocument>("keep").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(0L, left);

            // 删除:有数据就要手打名称。
            tab.SelectedItem = tab.Items.Single(static i => i.Name == "doomed");
            tab.DropCommand.Execute(null);
            ConfirmDialogViewModel drop = await WaitForConfirmAsync(bench);
            Assert.IsTrue(drop.RequiresTyping);
            Assert.IsFalse(drop.CanConfirm);
            drop.Typed = "doomed";
            drop.ConfirmCommand.Execute(null);
            await WaitUntilAsync(() => tab.Items.All(static i => i.Name != "doomed"));
            List<string> names = await (await database.ListCollectionNamesAsync()).ToListAsync();
            CollectionAssert.DoesNotContain(names, "doomed");
            CollectionAssert.Contains(names, "keep");
        }
        finally
        {
            await bench.Connection.Client.DropDatabaseAsync(db);
        }
    });

    /// <summary>只读模式下删除在确认框之前就被拦下,并提示原因。</summary>
    [TestMethod]
    public void Read_only_mode_blocks_drop_before_any_confirmation() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        ObjectsTabViewModel tab = bench.ViewModel.Tabs.OfType<ObjectsTabViewModel>().Single(static t => t.Database == "shop");
        await tab.ReloadAsync();
        bench.Session.Guard.IsReadOnly = true;
        tab.SelectedItem = tab.Items.Single(static i => i.Name == "orders");
        tab.DropCommand.Execute(null);
        await Screens.PumpAsync(20);
        Assert.IsNull(bench.ViewModel.Dialog, "read-only mode must stop before the confirmation dialog");
        Assert.IsTrue(bench.ViewModel.Toasts.Count > 0, "a toast should explain why nothing happened");
    });

    /// <summary>设计稿 12:shop 的对象列表,选中 events。</summary>
    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board12_objects_list_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        ObjectsTabViewModel tab = bench.ViewModel.Tabs.OfType<ObjectsTabViewModel>().Single(static t => t.Database == "shop");
        await tab.ReloadAsync();
        await WaitUntilAsync(() => tab.Items.Where(static i => !i.IsView).All(static i => i.HasStats));
        tab.SelectedItem = tab.Items.Single(static i => i.Name == "events");
        // 写入速率要两次 top 采样(间隔 1 秒),等它落定再截。
        await WaitUntilAsync(() => tab.Sections.Count == 3 && tab.Sections[2].Facts.All(static f => f.Value != "…"), 8000);
        await Screens.PumpAsync(30);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "12-objects");

        Assert.IsNotNull(frame);
        Assert.AreEqual(3, tab.Sections.Count);

        // 另两种形态顺手各截一张(不对应画板,给人看 DDL 页与大图标视图有没有画坏)。
        tab.IsDdlTab = true;
        tab.IsGridMode = true;
        await Screens.PumpAsync(30);
        Assert.IsNotNull(Screens.Capture(bench.Window, "12-objects-ddl-grid"));
    });

    // ── 工具 ─────────────────────────────────────────────────────────────

    internal static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
        {
            await Screens.PumpAsync(5);
        }
    }

    private static async Task<ConfirmDialogViewModel> WaitForConfirmAsync(Workbench bench)
    {
        await WaitUntilAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
        return bench.ViewModel.Dialog as ConfirmDialogViewModel
               ?? throw new AssertFailedException("expected a confirmation dialog");
    }
}
