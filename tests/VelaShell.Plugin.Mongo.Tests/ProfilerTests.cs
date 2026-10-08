using System.Diagnostics;
using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>慢查询与当前操作:profile 记录解析、按形状聚合、命令摘要,以及设计稿 15 的截图。</summary>
[TestClass]
public sealed class ProfilerTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 13, 8, 44, DateTimeKind.Utc);

    [TestMethod]
    public void Profile_entry_reads_plan_and_in_memory_sort_size()
    {
        var e = ProfileEntry.Parse(FindEntry(T0, 820, docs: 1_284_902, returned: 38, plan: "COLLSCAN", sort: true));
        Assert.AreEqual(820, e.Millis, 1e-9);
        Assert.AreEqual(1_284_902, e.DocsExamined);
        Assert.AreEqual(38, e.Returned);
        Assert.AreEqual(18L << 20, e.SortBytes);
        Assert.AreEqual(SlowPlanKind.CollectionScan, e.Plan, "COLLSCAN outranks the in-memory sort");
        Assert.AreEqual("orders.find", e.Shape.Title);
        Assert.AreEqual("10.0.8.44", e.Client);
    }

    [TestMethod]
    public void Plans_are_classified()
    {
        Assert.AreEqual(SlowPlanKind.IndexScan, ProfileEntry.ClassifyPlan("IXSCAN { sku: 1, rating: 1 }", false));
        Assert.AreEqual(SlowPlanKind.InMemorySort, ProfileEntry.ClassifyPlan("IXSCAN { customer.id: 1 }", true));
        Assert.AreEqual(SlowPlanKind.CollectionScan, ProfileEntry.ClassifyPlan("COLLSCAN", true));
        Assert.AreEqual(SlowPlanKind.IndexScan, ProfileEntry.ClassifyPlan("IDHACK", false));
        Assert.AreEqual(SlowPlanKind.Other, ProfileEntry.ClassifyPlan("", false));
    }

    [TestMethod]
    public void Shape_rows_count_runs_but_not_getmores()
    {
        BsonDocument first = FindEntry(T0, 800, docs: 1000, returned: 101, plan: "COLLSCAN", sort: false);
        BsonDocument second = FindEntry(T0.AddMinutes(1), 400, docs: 1000, returned: 101, plan: "COLLSCAN", sort: false, level: "VIP");
        var getMore = new BsonDocument
        {
            { "op", "getmore" },
            { "ns", "shop.orders" },
            { "command", new BsonDocument { { "getMore", 9L }, { "collection", "orders" } } },
            { "originatingCommand", first["command"] },
            { "millis", 0 },
            { "docsExamined", 0 },
            { "nreturned", 232 },
            { "planSummary", "COLLSCAN" },
            { "ts", T0.AddMinutes(2) }
        };
        List<ProfileEntry> entries = [ProfileEntry.Parse(first), ProfileEntry.Parse(second), ProfileEntry.Parse(getMore)];
        Assert.AreEqual(1, entries.Select(static e => e.Shape.Key).Distinct().Count(), "same shape regardless of literal values");
        var row = new ProfilerShapeRow(entries, 2400);
        Assert.AreEqual(2, row.Count);
        Assert.AreEqual(600, row.AvgMillis, 1e-9);
        Assert.AreEqual("800 ms", row.MaxText);
        Assert.AreEqual(0.5, row.Share, 1e-9);
        Assert.AreEqual("COLLSCAN", row.PlanText);
        Assert.IsTrue(row.PlanErr);
        Assert.AreEqual(T0.AddMinutes(1), row.Latest.Time, "the latest real run, not the getmore");
        Assert.AreEqual("5 : 1", row.RatioText, "2,000 scanned for 434 returned");
    }

    [TestMethod]
    public void Detail_suggests_an_esr_index_for_a_collscan()
    {
        var loc = new Loc("zh-CN");
        var e = ProfileEntry.Parse(FindEntry(T0, 820, docs: 1_284_902, returned: 38, plan: "COLLSCAN", sort: true));
        var detail = new ProfilerDetail(new ProfilerShapeRow([e], 820), e, loc);
        Assert.AreEqual("orders.find · COLLSCAN", detail.Title);
        Assert.AreEqual("1 次 · 占慢查询总耗时 100%", detail.Subtitle);
        Assert.AreEqual("{ \"customer.level\": 1, total: -1 }", detail.AdviceKeys);
        Assert.AreEqual("customer.level 为等值条件,total 同时用于排序与范围,按 ESR 建议:", detail.AdviceText);
        Assert.IsTrue(detail.CanCreateIndex);
        Assert.AreEqual("是 · 18.0 MB", detail.InMemorySort);
        StringAssert.StartsWith(detail.Statement, "db.orders.find({");
    }

    [TestMethod]
    public void Detail_does_not_push_an_index_on_a_healthy_ixscan()
    {
        var loc = new Loc("zh-CN");
        var e = ProfileEntry.Parse(FindEntry(T0, 188, docs: 6, returned: 6, plan: "IXSCAN { customer.level: 1, total: -1 }", sort: false));
        var detail = new ProfilerDetail(new ProfilerShapeRow([e], 188), e, loc);
        Assert.IsFalse(detail.CanCreateIndex);
        Assert.IsFalse(detail.HasAdviceKeys);
    }

    [TestMethod]
    public void Command_summaries_follow_the_design()
    {
        var find = BsonDocument.Parse("""{ op: "query", ns: "shop.orders", command: { find: "orders", filter: { "customer.level": "SVIP", total: { $gte: 5000 } } } }""");
        Assert.AreEqual("find { customer.level: \"SVIP\", total: { $gte: 5000 } }", ProfilerTabViewModel.CommandSummary(find));

        var update = BsonDocument.Parse("""{ op: "update", ns: "shop.carts", command: { q: { expireAt: { $lt: 5 } }, u: { $set: { x: 1 } }, multi: true } }""");
        Assert.AreEqual("updateMany { expireAt: { $lt: 5 } }", ProfilerTabViewModel.CommandSummary(update));

        var getMore = BsonDocument.Parse("""
            { op: "getmore", ns: "shop.events", command: { getMore: 73519, collection: "events" },
              cursor: { originatingCommand: { aggregate: "events", pipeline: [ { $match: {} }, { $group: { _id: 1 } } ] } } }
            """);
        Assert.AreEqual("aggregate [$match, $group] · cursor 7351…", ProfilerTabViewModel.CommandSummary(getMore));

        var build = BsonDocument.Parse("""{ op: "command", ns: "shop.$cmd", command: { createIndexes: "orders", indexes: [ { key: { paidAt: 1 }, name: "paidAt_1" } ] } }""");
        Assert.AreEqual("createIndexes orders.paidAt_1", ProfilerTabViewModel.CommandSummary(build));
    }

    [TestMethod]
    public void Metadata_commands_are_not_slow_queries()
    {
        Assert.IsTrue(ProfileEntry.Parse(BsonDocument.Parse("{ op: 'command', ns: 'x.orders', command: { listIndexes: 'orders' } }")).IsDiagnostic);
        Assert.IsTrue(ProfileEntry.Parse(BsonDocument.Parse(
            "{ op: 'command', ns: 'x.orders', command: { aggregate: 'orders', pipeline: [ { $collStats: { storageStats: {} } } ] } }")).IsDiagnostic);
        Assert.IsFalse(ProfileEntry.Parse(BsonDocument.Parse(
            "{ op: 'command', ns: 'x.orders', command: { aggregate: 'orders', pipeline: [ { $match: { a: 1 } } ] } }")).IsDiagnostic);
        Assert.IsFalse(ProfileEntry.Parse(BsonDocument.Parse("{ op: 'query', ns: 'x.orders', command: { find: 'orders' } }")).IsDiagnostic);
    }

    [TestMethod]
    public void Settings_inputs_are_validated()
    {
        Assert.IsTrue(ProfilerTabViewModel.TryParseSlowMs("100", out int ms) && ms == 100);
        Assert.IsFalse(ProfilerTabViewModel.TryParseSlowMs("-1", out _));
        Assert.IsTrue(ProfilerTabViewModel.TryParseSampleRate("0.5", out double rate) && Math.Abs(rate - 0.5) < 1e-9);
        Assert.IsFalse(ProfilerTabViewModel.TryParseSampleRate("0", out _));
        Assert.IsFalse(ProfilerTabViewModel.TryParseSampleRate("1.5", out _));
    }

    [TestMethod]
    public void Millis_and_ratios_are_formatted_like_the_design()
    {
        Assert.AreEqual("820 ms", ProfilerFormat.Millis(820));
        Assert.AreEqual("41.2 s", ProfilerFormat.Millis(41_200));
        Assert.AreEqual("42 : 1", ProfilerFormat.Ratio(42, 1));
        Assert.AreEqual("1,284 : 0", ProfilerFormat.Ratio(1284, 0));
        Assert.IsTrue(ProfilerFormat.RatioHigh(18, 1));
        Assert.IsFalse(ProfilerFormat.RatioHigh(6, 1));
    }

    // ── 真实服务器 ────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board15_Profiler_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using MongoConnection load = await TestServer.OpenAsync();
        string db = "velashell_profiler_" + Guid.NewGuid().ToString("N")[..8];
        const string tag = "velashell-profiler-test";
        var background = new List<Task>();
        try
        {
            await SeedAsync(load, db);
            // level 2 只作用于这个临时库(slowms / sampleRate 是全局的,不碰)。
            _ = await load.RunCommandAsync(db, new BsonDocument("profile", 2));
            await RunWorkloadAsync(load, db);

            // 两条长时间运行的操作:一条超过 10 秒(红行),一条短一些 —— 截图时它们正在 $currentOp 里。
            background.Add(Task.Run(() => SlowFindAsync(load, db, "orders", 120, 260, tag)));
            await Task.Delay(1500);
            background.Add(Task.Run(() => SlowFindAsync(load, db, "events", 60, 200, tag)));

            EnsureEditorTheme();
            await using Workbench bench = await Screens.OpenWorkbenchAsync(db);
            bench.Session.OpenProfiler(db);
            var tab = (ProfilerTabViewModel)bench.ViewModel.ActiveTab!;
            var watch = Stopwatch.StartNew();
            while ((tab.Shapes.Count < 5 || tab.Ops.Count(o => o.Namespace.StartsWith(db, StringComparison.Ordinal)) < 2 ||
                    !tab.Ops.Any(o => o.IsLong)) && watch.Elapsed < TimeSpan.FromSeconds(40))
            {
                await Screens.PumpAsync(10);
                if (tab.Shapes.Count < 5)
                {
                    await tab.LoadSlowAsync(CancellationToken.None);
                }
            }
            await Screens.PumpAsync(40);

            WriteableBitmap? frame = Screens.Capture(bench.Window, "15-profiler");
            Assert.IsNotNull(frame);
            Assert.AreEqual(2, tab.Level);
            Assert.IsTrue(tab.Shapes.Count >= 5, $"{tab.Shapes.Count} shapes");
            Assert.IsTrue(tab.Shapes.Any(s => s.Title == "orders.find" && s.PlanErr), "the unindexed orders.find is a COLLSCAN");
            Assert.IsTrue(tab.Shapes.Any(s => s.Title == "reviews.find" && s.PlanOk), "reviews.find hits its index");
            Assert.IsNotNull(tab.Detail);
            Assert.IsTrue(tab.Ops.Any(o => o.IsLong), "a > 10 s operation is listed in red");
            Assert.IsFalse(tab.Ops.Any(o => o.Summary.Contains("$currentOp", StringComparison.Ordinal)), "our own $currentOp is hidden");
            StringAssert.Contains(tab.StatusText, "system.profile");
        }
        finally
        {
            _ = await load.KillByCommentAsync(tag);
            foreach (Task task in background)
            {
                try
                {
                    await task;
                }
                catch (Exception)
                {
                    // 被 killOp 打断的那两条查询会抛 Interrupted —— 正是预期。
                }
            }
            _ = await load.RunCommandAsync(db, new BsonDocument("profile", 0));
            await load.Client.DropDatabaseAsync(db);
        }
    });

    /// <summary>
    /// headless 宿主没有装 AvaloniaEdit 的控件主题(真宿主装了):不补上的话样本代码块里的
    /// TextEditor 没有模板,截出来是一个空框。只补一次,不影响别的测试 —— 它们只会因此多看到编辑器的字。
    /// </summary>
    private static void EnsureEditorTheme()
    {
        Avalonia.Application app = Avalonia.Application.Current!;
        if (app.Styles.OfType<Avalonia.Markup.Xaml.Styling.StyleInclude>().Any(static s => s.Source?.ToString().Contains("AvaloniaEdit", StringComparison.Ordinal) == true))
        {
            return;
        }
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://VelaShell.Plugin.Mongo.Tests/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        });
    }

    private static async Task SeedAsync(MongoConnection c, string db)
    {
        var random = new Random(15);
        string[] levels = ["VIP", "VIP", "VIP", "GOLD", "SVIP"];
        await c.Collection(db, "orders").InsertManyAsync(Enumerable.Range(0, 60_000).Select(i => new BsonDocument
        {
            { "customer", new BsonDocument { { "id", i % 900 }, { "level", levels[i % levels.Length] } } },
            { "total", random.Next(1, 9000) },
            { "status", i % 7 == 0 ? "paid" : "new" },
            { "createdAt", DateTime.UtcNow.AddMinutes(-i) },
            { "tags", new BsonArray { "a", i % 2 == 0 ? "red" : "blue" } }
        }));
        _ = await c.Collection(db, "orders").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("customer.id", 1)));
        await c.Collection(db, "events").InsertManyAsync(Enumerable.Range(0, 20_000).Select(i => new BsonDocument
        {
            { "day", $"2026-09-{1 + (i % 30):00}" }, { "device", $"d{i % 12}" }, { "v", i }
        }));
        await c.Collection(db, "reviews").InsertManyAsync(Enumerable.Range(0, 5_000).Select(i => new BsonDocument
        {
            { "sku", $"SKU-{i % 300}" }, { "rating", 1 + (i % 5) }
        }));
        _ = await c.Collection(db, "reviews").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { { "sku", 1 }, { "rating", 1 } }));
        await c.Collection(db, "carts").InsertManyAsync(Enumerable.Range(0, 3_000).Select(i => new BsonDocument
        {
            { "expireAt", DateTime.UtcNow.AddMinutes(i - 1500) }, { "items", i % 4 }
        }));
        _ = await c.Collection(db, "carts").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("expireAt", 1)));
        await c.Collection(db, "products").InsertManyAsync(Enumerable.Range(0, 8_000).Select(i => new BsonDocument
        {
            { "name", $"p{i}" }, { "tags", new BsonArray { i % 3 == 0 ? "sale" : "new", "x" } }
        }));
    }

    /// <summary>与设计稿 15 同形的几类慢查询(全表扫描 + 内存排序、聚合、索引 + 内存排序、走索引、批量更新、带 limit 的扫描)。</summary>
    private static async Task RunWorkloadAsync(MongoConnection c, string db)
    {
        IMongoCollection<BsonDocument> orders = c.Collection(db, "orders");
        for (int i = 0; i < 12; i++)
        {
            _ = await orders.Find(BsonDocument.Parse($$"""{ "customer.level": "SVIP", total: { $gte: {{5000 + i}} } }"""))
                .Sort(new BsonDocument("total", -1)).Limit(40).ToListAsync();
        }
        IMongoCollection<BsonDocument> events = c.Collection(db, "events");
        for (int i = 0; i < 6; i++)
        {
            _ = await events.Aggregate<BsonDocument>(new[]
            {
                new BsonDocument("$match", new BsonDocument("day", $"2026-09-{10 + i}")),
                new BsonDocument("$group", new BsonDocument { { "_id", "$device" }, { "n", new BsonDocument("$sum", 1) } })
            }).ToListAsync();
        }
        for (int i = 0; i < 8; i++)
        {
            _ = await orders.Find(new BsonDocument("customer.id", 40 + i)).Sort(new BsonDocument("createdAt", -1)).ToListAsync();
        }
        IMongoCollection<BsonDocument> reviews = c.Collection(db, "reviews");
        for (int i = 0; i < 5; i++)
        {
            _ = await reviews.Find(BsonDocument.Parse($$"""{ sku: "SKU-{{i}}", rating: { $gte: 4 } }""")).ToListAsync();
        }
        IMongoCollection<BsonDocument> carts = c.Collection(db, "carts");
        for (int i = 0; i < 3; i++)
        {
            _ = await carts.UpdateManyAsync(new BsonDocument("expireAt", new BsonDocument("$lt", DateTime.UtcNow.AddMinutes(-i))),
                new BsonDocument("$set", new BsonDocument("expired", true)));
        }
        IMongoCollection<BsonDocument> products = c.Collection(db, "products");
        for (int i = 0; i < 4; i++)
        {
            _ = await products.Find(new BsonDocument("tags", "sale")).Limit(50).ToListAsync();
        }
    }

    /// <summary>一条按文档 sleep 的慢查询(<c>$where</c>),跑在后台,打上标记以便收尾时 killOp。</summary>
    private static async Task SlowFindAsync(MongoConnection c, string db, string collection, int sleepMs, int docs, string tag)
    {
        _ = await c.Collection(db, collection)
            .Find(new BsonDocument("$where", $"sleep({sleepMs}) || true"), new FindOptions { Comment = tag, BatchSize = docs })
            .Limit(docs)
            .ToListAsync();
    }

    private static BsonDocument FindEntry(DateTime ts, double millis, long docs, long returned, string plan, bool sort, string level = "SVIP")
    {
        var entry = new BsonDocument
        {
            { "op", "query" },
            { "ns", "shop.orders" },
            { "command", new BsonDocument
                {
                    { "find", "orders" },
                    { "filter", BsonDocument.Parse($$"""{ "customer.level": "{{level}}", total: { $gte: 5000 } }""") },
                    { "sort", new BsonDocument("total", -1) },
                    { "$db", "shop" }
                }
            },
            { "docsExamined", docs },
            { "nreturned", returned },
            { "hasSortStage", sort },
            { "millis", millis },
            { "planSummary", plan },
            { "ts", ts },
            { "client", "10.0.8.44" },
            { "appName", "order-sweeper" },
            { "user", "" }
        };
        if (sort)
        {
            entry["execStats"] = new BsonDocument
            {
                { "stage", "SORT" },
                { "totalDataSizeSorted", 18L << 20 },
                { "usedDisk", false },
                { "inputStage", new BsonDocument("stage", "COLLSCAN") }
            };
        }
        return entry;
    }
}
