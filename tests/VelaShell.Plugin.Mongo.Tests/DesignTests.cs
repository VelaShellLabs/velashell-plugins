using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 集合设计(设计稿 07 / 08 / 16)接真实服务器:
/// 写路径(建索引、隐藏、删除、应用验证规则)只在自建的临时库里做,收尾 drop 掉;
/// 三张截图对 <c>shop.orders</c>,只读(验证规则页只把规则放进编辑器,不 collMod)。
/// </summary>
[TestClass]
[TestCategory("Screenshots")]
public sealed class DesignTests
{
    /// <summary>等某个条件成立(边等边泵消息),超时就让断言去报。</summary>
    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 20_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Screens.PumpAsync(5);
        }
    }

    /// <summary>某个动作会弹确认框:等它出来、点确认,再等动作做完。</summary>
    private static async Task ConfirmAsync(Workbench bench, Task action)
    {
        await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel || action.IsCompleted);
        if (bench.ViewModel.Dialog is ConfirmDialogViewModel confirm)
        {
            if (confirm.RequiresTyping)
            {
                confirm.Typed = confirm.Request.TypeToConfirm!;
            }
            confirm.ConfirmCommand.Execute(null);
        }
        await action;
    }

    private static async Task<DesignTabViewModel> OpenDesignAsync(Workbench bench, string db, string collection, DesignPage page = DesignPage.Indexes)
    {
        bench.Session.OpenDesign(db, collection, page);
        var tab = (DesignTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => tab.Indexes.Count > 0 || tab.IsView);
        await Screens.PumpAsync(20);
        return tab;
    }

    [TestMethod]
    public void Indexes_rules_and_options_round_trip_on_a_scratch_database() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_design_{Guid.NewGuid():N}"[..28];
        await using Workbench bench = await Screens.OpenWorkbenchAsync(db);
        IMongoDatabase database = bench.Connection.Database(db);
        try
        {
            IMongoCollection<BsonDocument> items = database.GetCollection<BsonDocument>("items");
            await items.InsertManyAsync(Enumerable.Range(0, 300).Select(static i => new BsonDocument
            {
                { "sku", $"SKU-{i:D4}" },
                { "kind", i % 3 == 0 ? "book" : "toy" },
                { "price", 5 + (i % 50) },
                { "tags", new BsonArray { "a", "b" } },
                { "updatedAt", DateTime.UtcNow.AddMinutes(-i) }
            }));
            await bench.Session.RefreshTreeAsync(db);
            DesignTabViewModel tab = await OpenDesignAsync(bench, db, "items");
            Assert.AreEqual(1, tab.ReadyIndexCount, "only _id_ to begin with");
            StringAssert.StartsWith(tab.StatusText, $"设计 · {db}.items · 1 个索引");

            // ── 新建索引:{ kind: 1, price: -1 },唯一不勾,带部分条件 ──
            tab.OpenCreateCommand.Execute(null);
            Assert.IsTrue(tab.IsCreateOpen);
            tab.NewKeys[0].Field = "kind";
            tab.AddKeyCommand.Execute(null);
            tab.NewKeys[1].Field = "price";
            tab.NewKeys[1].Direction = -1;
            Assert.AreEqual("kind_1_price_-1", tab.NewName);
            tab.NewPartial = true;
            tab.NewPartialText = "{ price: { $gt: 10 } }";
            Assert.AreEqual("", tab.NewError);
            StringAssert.Contains(tab.NewCommand, "createIndex(\n  { kind: 1, price: -1 },\n  { partialFilterExpression: { price: { $gt: 10 } } }\n)");
            tab.MoveKey(1, 0);
            Assert.AreEqual("price_-1_kind_1", tab.NewName, "the name follows the key order until edited by hand");
            tab.MoveKey(0, 1);

            // ── 编辑器的字段表与草稿行:序号、上移 / 下移按钮、末尾的「新建」行跟着表单变 ──
            Assert.AreEqual(1, tab.NewKeys[0].Number);
            Assert.IsFalse(tab.NewKeys[0].CanMoveUp);
            Assert.IsFalse(tab.NewKeys[1].CanMoveDown);
            tab.MoveKeyUpCommand.Execute(tab.NewKeys[1]);
            Assert.AreEqual("price", tab.NewKeys[0].Field);
            Assert.AreEqual(1, tab.NewKeys[0].Number);
            Assert.AreEqual("price_-1_kind_1", tab.DraftIndex?.Name);
            tab.MoveKeyDownCommand.Execute(tab.NewKeys[0]);
            Assert.AreEqual("kind", tab.NewKeys[0].Field);
            Assert.IsTrue(tab.HasDraft);
            Assert.AreEqual("kind_1_price_-1", tab.DraftIndex!.Name);
            CollectionAssert.AreEqual(new[] { "kind", "price" }, tab.DraftIndex.Keys.Select(static k => k.Field).ToArray());
            Assert.AreEqual("复合", tab.DraftIndex.TypeText);
            Assert.IsTrue(tab.DraftIndex.Attributes.Any(static a => a.Text == "部分"));
            StringAssert.StartsWith(tab.DraftIndex.SizeText, "≈ ");
            Assert.IsFalse(tab.Indexes.Any(static r => r.Name == "kind_1_price_-1"), "the draft row is not an index yet");
            Assert.AreEqual($"将在 {db}.items 上执行 createIndexes", tab.NewTarget);
            tab.CreateIndexCommand.Execute(null);
            await WaitAsync(() => tab.Indexes.Any(static r => r.Name == "kind_1_price_-1" && !r.IsBuilding) && !tab.IsCreating);
            IndexRow created = tab.Indexes.Single(static r => r.Name == "kind_1_price_-1");
            Assert.AreEqual("复合", created.TypeText);
            Assert.IsTrue(created.Attributes.Any(static a => a.Text == "部分"));
            Assert.IsFalse(tab.IsCreateOpen);
            Assert.IsFalse(tab.HasDraft);

            // 多键:tags 是数组,执行计划里 isMultiKey 为真。
            _ = await items.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("tags", 1)));
            await tab.LoadIndexesAsync();
            Assert.AreEqual("多键", tab.Indexes.Single(static r => r.Name == "tags_1").TypeText);

            // ── 隐藏 / 取消隐藏(collMod)──
            tab.SelectedIndex = tab.Indexes.Single(static r => r.Name == "kind_1_price_-1");
            await tab.SetHiddenAsync(tab.SelectedIndex, hidden: true);
            List<BsonDocument> specs = await (await items.Indexes.ListAsync()).ToListAsync();
            Assert.IsTrue(specs.Single(static s => s["name"] == "kind_1_price_-1").GetValue("hidden", false).ToBoolean());
            Assert.AreEqual("取消隐藏", tab.HideLabel);
            await tab.SetHiddenAsync(tab.Indexes.Single(static r => r.Name == "kind_1_price_-1"), hidden: false);
            specs = await (await items.Indexes.ListAsync()).ToListAsync();
            Assert.IsFalse(specs.Single(static s => s["name"] == "kind_1_price_-1").GetValue("hidden", false).ToBoolean());

            // ── 删除(确认框)──
            await ConfirmAsync(bench, tab.DropIndexAsync(tab.Indexes.Single(static r => r.Name == "tags_1")));
            specs = await (await items.Indexes.ListAsync()).ToListAsync();
            Assert.IsFalse(specs.Any(static s => s["name"] == "tags_1"));

            // ── Schema 分析 → 生成规则 → 预检 → 应用(确认框)──
            tab.Page = DesignPage.Schema;
            await WaitAsync(() => tab.Schema is not null && !tab.IsAnalyzing && !tab.IsGenChecking);
            Assert.AreEqual(300, tab.Schema!.Sampled);
            Assert.IsTrue(tab.SchemaFields.Any(static f => f.Path == "kind" && f.IsBars));
            Assert.IsTrue(tab.SchemaFields.Any(static f => f.Path == "price" && f.IsHistogram));
            Assert.IsNotNull(tab.Generated);
            Assert.AreEqual(0, tab.GenFailCount, "a rule generated from the whole collection fits it");
            StringAssert.Contains(tab.GeneratedText, "kind: { enum: [ \"toy\", \"book\" ] }");
            StringAssert.StartsWith(tab.StatusText, $"设计 · {db}.items · Schema · ");

            tab.GenLevel = "moderate";
            tab.GenAction = "warn";
            await ConfirmAsync(bench, tab.ApplyValidatorAsync(tab.Generated!, tab.GenLevel, tab.GenAction, tab.GenFailCount));
            BsonDocument options = (await (await database.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", "items") })).FirstAsync())["options"].AsBsonDocument;
            Assert.AreEqual("moderate", options["validationLevel"].AsString);
            Assert.AreEqual("warn", options["validationAction"].AsString);
            Assert.AreEqual(tab.Generated, options["validator"].AsBsonDocument);

            // ── 验证规则页:编辑器里是生效的规则;改紧一点 → 预检出不通过,试写文档给出原因 ──
            tab.Page = DesignPage.Validation;
            await WaitAsync(() => tab.RuleText.Contains("$jsonSchema", StringComparison.Ordinal) && !tab.IsPrechecking && tab.PassPieces.Count > 0);
            Assert.IsFalse(tab.IsModified);
            Assert.AreEqual("$jsonSchema", tab.ValidationTabHint);
            tab.RuleText = "{ $jsonSchema: { bsonType: \"object\", required: [ \"sku\" ], properties: { price: { bsonType: \"int\", minimum: 30 } } } }";
            Assert.IsTrue(tab.IsModified);
            Assert.IsTrue(tab.RuleSyntaxOk);
            await tab.RunPrecheckAsync(resample: false);
            Assert.AreEqual("不通过 150", tab.FailText, "price 5..29 fails minimum 30 for half the documents");
            FailureReasonRow reason = tab.FailureReasons.Single();
            Assert.AreEqual("price", reason.Path);
            Assert.AreEqual("minimum: 30", reason.Keyword);
            (string title, string badge, string body) = tab.KeywordHover("minimum")!.Value;
            Assert.AreEqual("minimum", title);
            Assert.AreEqual("数值约束", badge);
            StringAssert.Contains(body, "抽样中有 150 份文档违反它(price)");
            Assert.IsNull(tab.KeywordHover("price"), "field names are not keywords");

            tab.TryDocText = "{ sku: \"X\", price: 3 }";
            await tab.EvaluateTryDocAsync();
            Assert.IsTrue(tab.TryFailed);
            StringAssert.Contains(tab.TryTitle, "仍会写入并记录警告");
            StringAssert.StartsWith(tab.TryDetail, "properties.price.minimum:");
            Assert.AreEqual(1, tab.TryMarks.Count);

            tab.TryDocText = "{ sku: \"X\", price: 31 }";
            await tab.EvaluateTryDocAsync();
            Assert.IsTrue(tab.TryOk);

            tab.RuleText = "{ $jsonSchema: { bsonType: ";
            Assert.IsFalse(tab.RuleSyntaxOk);
            Assert.IsFalse(tab.ApplyRuleCommand.CanExecute(null));
            tab.DiscardRuleCommand.Execute(null);
            Assert.IsFalse(tab.IsModified);

            // ── 历史版本:再应用一次,上一版进历史 ──
            tab.RuleLevel = "strict";
            await ConfirmAsync(bench, tab.ApplyValidatorAsync(ShellJsonOf(tab.RuleText), "strict", "error", 0));
            Assert.AreEqual(1, tab.RuleHistory.Count);
            Assert.AreEqual("moderate", tab.RuleHistory[0].Level);

            // ── 选项:TTL 秒数(collMod index)──
            _ = await items.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("updatedAt", 1),
                new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) }));
            tab.Page = DesignPage.Options;
            await tab.RefreshAsync();
            TtlRow ttl = tab.TtlRows.Single();
            Assert.AreEqual("30 天", ttl.Human);
            ttl.Seconds = "3600";
            Assert.IsTrue(ttl.IsDirty);
            await ConfirmAsync(bench, tab.ApplyTtlAsync(ttl));
            specs = await (await items.Indexes.ListAsync()).ToListAsync();
            Assert.AreEqual(3600, specs.Single(static s => s["name"] == "updatedAt_1")["expireAfterSeconds"].ToInt32());

            // ── 统计 ──
            tab.Page = DesignPage.Stats;
            await WaitAsync(() => tab.StatCards.Count > 0);
            Assert.AreEqual("300", tab.StatCards[0].Value);
            Assert.IsTrue(tab.IndexSizeBars.Count >= 3);

            // ── 只读模式拦下写路径 ──
            bench.Session.Guard.IsReadOnly = true;
            await tab.SetHiddenAsync(tab.Indexes.Single(static r => r.Name == "kind_1_price_-1"), hidden: true);
            specs = await (await items.Indexes.ListAsync()).ToListAsync();
            Assert.IsFalse(specs.Single(static s => s["name"] == "kind_1_price_-1").GetValue("hidden", false).ToBoolean());
        }
        finally
        {
            await bench.Connection.Client.DropDatabaseAsync(db);
        }
    });

    private static BsonDocument ShellJsonOf(string text) => Bson.ShellJson.ParseDocument(text);

    /// <summary>
    /// 设计稿 07:shop.orders 的索引表 + 建议 + 右侧新建索引面板(由「创建此索引」带入建议键,并加上部分条件)。
    /// 先在 shop 上跑两类慢查询(只读,用 <c>$where: sleep</c> 拖慢,让 level 1 的 profiler 记下它们):
    /// 一类全表扫描 + 按 total 排序(→ 建议 { status: 1, total: -1 }),一类走 customer.id_1 但要内存排序(→ 可扩展)。
    /// </summary>
    [TestMethod]
    public void Board07_Design_indexes() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        IMongoCollection<BsonDocument> orders = bench.Connection.Collection(Screens.Database, "orders");
        BsonDocument? sample = await orders.Find(FilterDefinition<BsonDocument>.Empty).Limit(1).FirstOrDefaultAsync();
        if (sample is null)
        {
            Assert.Inconclusive("shop.orders is empty");
        }
        // profiler 的级别是库级配置:没开就临时开到 level 1(只为让慢查询进 system.profile),截完图恢复原值。
        int level = (await bench.Connection.RunCommandAsync(Screens.Database, new BsonDocument("profile", -1)))["was"].ToInt32();
        if (level == 0)
        {
            _ = await bench.Connection.RunCommandAsync(Screens.Database, new BsonDocument { { "profile", 1 }, { "slowms", 100 } });
        }
        try
        {
            for (int i = 0; i < 2; i++)
            {
                _ = await orders.Find(BsonDocument.Parse("{ status: 'paid', total: { $gte: NumberDecimal('100') }, $where: 'sleep(1) || true' }"),
                        new FindOptions { Hint = new BsonDocument("$natural", 1) })
                    .Sort(new BsonDocument("total", -1)).Limit(300).ToListAsync();
            }
            BsonValue customerId = sample["customer"]["id"];
            _ = await orders.Find(new BsonDocument { { "customer.id", customerId }, { "$where", "sleep(5) || true" } },
                    new FindOptions { Hint = "customer.id_1" })
                .Sort(new BsonDocument("createdAt", -1)).ToListAsync();
        }
        finally
        {
            if (level == 0)
            {
                _ = await bench.Connection.RunCommandAsync(Screens.Database, new BsonDocument("profile", 0));
            }
        }

        DesignTabViewModel tab = await OpenDesignAsync(bench, Screens.Database, "orders");
        tab.SelectedIndex = tab.Indexes.FirstOrDefault(static r => r.Name == "status_1_createdAt_-1");
        await WaitAsync(() => tab.Advice.Count > 0 || tab.HasAdviceEmpty);
        AdviceCard? create = tab.Advice.FirstOrDefault(static a => a.Suggestion?.Kind == AdvisorKind.CreateIndex);
        if (create is not null)
        {
            await create.Action();
        }
        else
        {
            tab.OpenCreatePanel(BsonDocument.Parse("{ status: 1, total: -1 }"), null);
        }
        tab.NewPartial = true;
        tab.NewPartialText = "{ paid: true }";
        await WaitAsync(() => !tab.IsAnalyzing && tab.FieldOptions.Count > 0);
        await Screens.PumpAsync(40);
        WriteableBitmap? frame = Screens.Capture(bench.Window, "07-design-indexes");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.Indexes.Count >= 7);
        Assert.IsTrue(tab.IsCreateOpen);
        Assert.AreEqual("status_1_total_-1", tab.NewName);
    });

    /// <summary>
    /// 选项与统计两页(设计稿没有单独画板):shop.carts 的 TTL、shop.events 的时序参数、shop.orders 的统计。
    /// 只读 —— 不点「应用」。
    /// </summary>
    [TestMethod]
    public void Design_options_and_stats_render() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        await bench.Session.RefreshTreeAsync(Screens.Database);
        DesignTabViewModel carts = await OpenDesignAsync(bench, Screens.Database, "carts", DesignPage.Options);
        await WaitAsync(() => carts.TtlRows.Count > 0);
        await Screens.PumpAsync(20);
        _ = Screens.Capture(bench.Window, "design-options");
        Assert.AreEqual("updatedAt", carts.TtlRows.Single().Field);

        bench.Session.OpenDesign(Screens.Database, "events", DesignPage.Options);
        var events = (DesignTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => events.TsTimeField.Length > 0);
        Assert.IsTrue(events.IsTimeSeries);
        Assert.AreEqual("ts", events.TsTimeField);
        Assert.AreEqual("minutes", events.TsGranularity);
        Assert.IsFalse(events.CanValidate);

        DesignTabViewModel orders = await OpenDesignAsync(bench, Screens.Database, "orders", DesignPage.Stats);
        await WaitAsync(() => orders.StatCards.Count > 0);
        await Screens.PumpAsync(20);
        _ = Screens.Capture(bench.Window, "design-stats");
        Assert.AreEqual(6, orders.StatCards.Count);
        StringAssert.Contains(orders.StatsRaw, "indexSizes");
    });

    /// <summary>设计稿 08:shop.orders 的 Schema 分析(抽样 1,000)与由抽样生成的验证规则。</summary>
    [TestMethod]
    public void Board08_Design_schema() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        DesignTabViewModel tab = await OpenDesignAsync(bench, Screens.Database, "orders");
        tab.Page = DesignPage.Schema;
        await WaitAsync(() => tab.Schema is not null && !tab.IsAnalyzing && !tab.IsGenChecking && tab.GenFailCount >= 0);
        await Screens.PumpAsync(40);
        WriteableBitmap? frame = Screens.Capture(bench.Window, "08-design-schema");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.SchemaFields.Count > 8);
        Assert.IsTrue(tab.SchemaFields.Any(static f => f.Path == "status" && f.IsBars));
        Assert.IsTrue(tab.SchemaFields.Any(static f => f.Path == "note" && f.HintWarn));
        StringAssert.Contains(tab.StatusText, "个字段路径");
    });

    /// <summary>
    /// 设计稿 16:验证规则页。shop.orders 本身没有规则 —— 编辑器里放一条与设计稿同形的规则(不应用),
    /// total 的下限收紧到 500,让抽样预检有真实的不通过;试写一份 total 为 -5 的文档。
    /// </summary>
    [TestMethod]
    public void Board16_Design_validation() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        DesignTabViewModel tab = await OpenDesignAsync(bench, Screens.Database, "orders");
        tab.Page = DesignPage.Validation;
        await WaitAsync(() => tab.RuleText.Length > 0 && !tab.IsPrechecking);
        tab.RuleText = """
            {
              $jsonSchema: {
                bsonType: "object",
                required: [ "orderNo", "status", "total", "customer" ],
                additionalProperties: true,
                properties: {
                  orderNo: { bsonType: "string", pattern: "^SO\\d{4}-\\d{5}$" },
                  status: { enum: [ "pending", "paid", "shipped", "refunded", "cancelled" ] },
                  total: { bsonType: "decimal", minimum: 500 },
                  discount: { bsonType: [ "double", "decimal" ], minimum: 0 },
                  customer: {
                    bsonType: "object",
                    required: [ "id", "level" ],
                    properties: { level: { enum: [ "普通", "VIP", "SVIP" ] } }
                  },
                  items: { bsonType: "array", minItems: 1 },
                  createdAt: { bsonType: "date" },
                  note: { bsonType: [ "string", "null" ], maxLength: 500 }
                }
              }
            }
            """;
        tab.RuleLevel = "moderate";
        tab.RuleAction = "warn";
        await tab.RunPrecheckAsync(resample: false);
        tab.TryDocText = "{ orderNo: \"SO2609-10420\",\n  status: \"paid\",\n  total: NumberDecimal(\"-5\"),\n  customer: { id: ObjectId(\"66f5c2a1e3b7a0c1d2e391c0\"), level: \"VIP\" },\n  items: [ { sku: \"SKU-1182\" } ] }";
        await tab.EvaluateTryDocAsync();
        await Screens.PumpAsync(40);
        WriteableBitmap? frame = Screens.Capture(bench.Window, "16-design-validation");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.IsModified);
        Assert.IsTrue(tab.RuleSyntaxOk);
        Assert.IsTrue(tab.TryFailed);
        StringAssert.Contains(tab.StatusText, "validationLevel moderate · action warn");
    });
}
