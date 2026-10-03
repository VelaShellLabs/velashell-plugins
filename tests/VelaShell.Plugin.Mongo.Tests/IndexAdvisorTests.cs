using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 索引顾问(设计稿 07 下半部分):查询形状与 ESR 键、慢查询归组、三类建议的触发条件、默认索引名与索引形态。
/// </summary>
[TestClass]
public sealed class IndexAdvisorTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static BsonDocument Find(BsonDocument filter, BsonDocument? sort = null, string plan = "COLLSCAN", double millis = 820,
        long examined = 1_280_000, long returned = 300, bool sortStage = false)
    {
        var command = new BsonDocument { { "find", "orders" }, { "filter", filter } };
        if (sort is not null)
        {
            command["sort"] = sort;
        }
        return new BsonDocument
        {
            { "op", "query" },
            { "ns", "shop.orders" },
            { "command", command },
            { "planSummary", plan },
            { "millis", millis },
            { "docsExamined", examined },
            { "nreturned", returned },
            { "hasSortStage", sortStage },
            { "ts", Now }
        };
    }

    private static AdvisorIndex Index(string name, BsonDocument key, long? ops = 100, int ageDays = 30, BsonDocument? extra = null)
    {
        var spec = new BsonDocument { { "v", 2 }, { "key", key }, { "name", name } };
        if (extra is not null)
        {
            _ = spec.Merge(extra);
        }
        return new AdvisorIndex(name, key, spec, ops, Now.AddDays(-ageDays), 64L << 20);
    }

    [TestMethod]
    public void Esr_puts_equality_then_sort_then_range()
    {
        EsrShape shape = EsrShape.From(
            BsonDocument.Parse("{ total: { $gte: 100 }, status: 'paid', 'customer.level': { $in: ['VIP', 'SVIP'] } }"),
            BsonDocument.Parse("{ createdAt: -1 }"))!;
        CollectionAssert.AreEqual(new[] { "status", "customer.level" }, shape.Equality.ToArray());
        CollectionAssert.AreEqual(new[] { "total" }, shape.Range.ToArray());
        Assert.AreEqual(BsonDocument.Parse("{ status: 1, 'customer.level': 1, createdAt: -1, total: 1 }"), shape.EsrKey());
        Assert.AreEqual("E", shape.RoleOf("status"));
        Assert.AreEqual("S", shape.RoleOf("createdAt"));
        Assert.AreEqual("R", shape.RoleOf("total"));
    }

    [TestMethod]
    public void A_field_used_for_both_sort_and_range_takes_the_sort_slot()
    {
        EsrShape shape = EsrShape.From(BsonDocument.Parse("{ status: 'paid', total: { $gt: 5000 } }"), BsonDocument.Parse("{ total: -1 }"))!;
        Assert.AreEqual(BsonDocument.Parse("{ status: 1, total: -1 }"), shape.EsrKey());
    }

    [TestMethod]
    public void Unindexable_predicates_are_skipped_and_and_is_flattened()
    {
        EsrShape shape = EsrShape.From(
            BsonDocument.Parse("{ $and: [ { status: 'paid' }, { qty: { $lt: 3 } } ], $where: 'sleep(1) || true', $or: [ { a: 1 }, { b: 2 } ] }"),
            null)!;
        Assert.AreEqual(BsonDocument.Parse("{ status: 1, qty: 1 }"), shape.EsrKey());
        Assert.IsNull(EsrShape.From(BsonDocument.Parse("{ $expr: { $gt: ['$a', '$b'] } }"), null));
    }

    [TestMethod]
    public void Slow_queries_group_by_shape_and_ignore_cheap_index_scans()
    {
        BsonDocument[] profile = new[]
        {
            Find(BsonDocument.Parse("{ status: 'paid', total: { $gte: 1 } }"), BsonDocument.Parse("{ total: -1 }")),
            Find(BsonDocument.Parse("{ status: 'shipped', total: { $gte: 9 } }"), BsonDocument.Parse("{ total: -1 }"), millis: 600),
            Find(BsonDocument.Parse("{ orderNo: 'SO-1' }"), plan: "IXSCAN { orderNo: 1 }", examined: 1, returned: 1),
            new BsonDocument { { "op", "getmore" }, { "command", new BsonDocument("getMore", 1L) } }
        };
        IReadOnlyList<AdvisorQueryGroup> groups = IndexAdvisor.GroupSlowQueries(profile);
        Assert.AreEqual(1, groups.Count);
        AdvisorQueryGroup group = groups[0];
        Assert.AreEqual(2, group.Count);
        Assert.AreEqual(710, group.AverageMillis, 0.001);
        Assert.IsTrue(group.Collscan);
    }

    [TestMethod]
    public void Aggregate_and_update_commands_are_understood()
    {
        var aggregate = new BsonDocument
        {
            { "op", "command" },
            { "command", BsonDocument.Parse("{ aggregate: 'orders', pipeline: [ { $match: { status: 'paid' } }, { $sort: { createdAt: -1 } }, { $group: { _id: '$x' } } ] }") },
            { "planSummary", "COLLSCAN" }
        };
        Assert.IsTrue(IndexAdvisor.TryExtract(aggregate, out BsonDocument filter, out BsonDocument? sort));
        Assert.AreEqual(BsonDocument.Parse("{ status: 'paid' }"), filter);
        Assert.AreEqual(BsonDocument.Parse("{ createdAt: -1 }"), sort);

        var update = new BsonDocument { { "op", "update" }, { "command", BsonDocument.Parse("{ q: { sku: 'A' }, u: { $set: { x: 1 } } }") } };
        Assert.IsTrue(IndexAdvisor.TryExtract(update, out filter, out _));
        Assert.AreEqual(BsonDocument.Parse("{ sku: 'A' }"), filter);
    }

    [TestMethod]
    public void Collscan_without_a_usable_index_suggests_creating_the_esr_key()
    {
        IReadOnlyList<AdvisorQueryGroup> groups = IndexAdvisor.GroupSlowQueries(
            [Find(BsonDocument.Parse("{ status: 'paid', total: { $gte: 1 } }"), BsonDocument.Parse("{ total: -1 }"))]);
        AdvisorIndex[] indexes =
        [
            Index("_id_", BsonDocument.Parse("{ _id: 1 }")),
            Index("status_1_createdAt_-1", BsonDocument.Parse("{ status: 1, createdAt: -1 }"))
        ];
        AdvisorSuggestion advice = IndexAdvisor.Advise(groups, indexes, Now).Single();
        Assert.AreEqual(AdvisorKind.CreateIndex, advice.Kind);
        Assert.AreEqual(BsonDocument.Parse("{ status: 1, total: -1 }"), advice.Key);
    }

    [TestMethod]
    public void An_existing_covering_index_silences_the_suggestion()
    {
        IReadOnlyList<AdvisorQueryGroup> groups = IndexAdvisor.GroupSlowQueries(
            [Find(BsonDocument.Parse("{ status: 'paid', total: { $gte: 1 } }"), BsonDocument.Parse("{ total: -1 }"))]);
        AdvisorIndex[] indexes = [Index("status_1_total_-1_x_1", BsonDocument.Parse("{ status: 1, total: -1, x: 1 }"))];
        Assert.AreEqual(0, IndexAdvisor.Advise(groups, indexes, Now).Count);

        AdvisorIndex[] hidden = [Index("status_1_total_-1", BsonDocument.Parse("{ status: 1, total: -1 }"), extra: new BsonDocument("hidden", true))];
        Assert.AreEqual(AdvisorKind.CreateIndex, IndexAdvisor.Advise(groups, hidden, Now).First().Kind,
            "a hidden index is not used by the optimizer, so it does not count");
    }

    [TestMethod]
    public void A_single_field_prefix_of_the_esr_key_is_offered_as_an_extension()
    {
        IReadOnlyList<AdvisorQueryGroup> groups = IndexAdvisor.GroupSlowQueries(
        [
            Find(BsonDocument.Parse("{ 'customer.id': 7 }"), BsonDocument.Parse("{ createdAt: -1 }"),
                plan: "IXSCAN { customer.id: 1 }", examined: 40, returned: 40, sortStage: true)
        ]);
        AdvisorIndex[] indexes = [Index("customer.id_1", BsonDocument.Parse("{ 'customer.id': 1 }"))];
        AdvisorSuggestion advice = IndexAdvisor.Advise(groups, indexes, Now).Single();
        Assert.AreEqual(AdvisorKind.ExtendIndex, advice.Kind);
        Assert.AreEqual("customer.id_1", advice.Index!.Name);
        Assert.AreEqual(BsonDocument.Parse("{ 'customer.id': 1, createdAt: -1 }"), advice.Key);
        Assert.IsTrue(advice.Queries!.InMemorySort);
    }

    [TestMethod]
    public void Prefix_duplicates_are_redundant_unless_unique_or_partial()
    {
        AdvisorIndex[] indexes =
        [
            Index("_id_", BsonDocument.Parse("{ _id: 1 }")),
            Index("status_1", BsonDocument.Parse("{ status: 1 }")),
            Index("status_1_createdAt_-1", BsonDocument.Parse("{ status: 1, createdAt: -1 }")),
            Index("orderNo_1", BsonDocument.Parse("{ orderNo: 1 }"), extra: new BsonDocument("unique", true)),
            Index("orderNo_1_x_1", BsonDocument.Parse("{ orderNo: 1, x: 1 }")),
            Index("status_-1", BsonDocument.Parse("{ status: -1 }"))
        ];
        IReadOnlyList<AdvisorSuggestion> advice = IndexAdvisor.Advise([], indexes, Now);
        AdvisorSuggestion redundant = advice.Single(a => a.Kind == AdvisorKind.RedundantIndex);
        Assert.AreEqual("status_1", redundant.Index!.Name);
        Assert.AreEqual("status_1_createdAt_-1", redundant.Other!.Name);
    }

    [TestMethod]
    public void Unused_needs_a_full_observation_window()
    {
        AdvisorIndex[] indexes =
        [
            Index("note_text", BsonDocument.Parse("{ _fts: 'text', _ftsx: 1 }"), ops: 0, ageDays: 30),
            Index("paidAt_1", BsonDocument.Parse("{ paidAt: 1 }"), ops: 0, ageDays: 2),
            Index("sku_1", BsonDocument.Parse("{ sku: 1 }"), ops: 5, ageDays: 30),
            Index("x_1", BsonDocument.Parse("{ x: 1 }"), ops: null, ageDays: 30)
        ];
        AdvisorSuggestion unused = IndexAdvisor.Advise([], indexes, Now).Single();
        Assert.AreEqual(AdvisorKind.UnusedIndex, unused.Kind);
        Assert.AreEqual("note_text", unused.Index!.Name);
        Assert.AreEqual(30, unused.UnusedDays);
    }

    [TestMethod]
    public void Default_names_follow_the_server_rule()
    {
        Assert.AreEqual("status_1_createdAt_-1", IndexAdvisor.DefaultName(BsonDocument.Parse("{ status: 1, createdAt: -1 }")));
        Assert.AreEqual("note_text", IndexAdvisor.DefaultName(BsonDocument.Parse("{ note: 'text' }")));
        Assert.AreEqual("loc_2dsphere", IndexAdvisor.DefaultName(BsonDocument.Parse("{ loc: '2dsphere' }")));
        Assert.AreEqual("customer.id_1", IndexAdvisor.DefaultName(BsonDocument.Parse("{ 'customer.id': 1 }")));
    }

    [TestMethod]
    public void Index_shapes_and_text_keys_display_like_the_design()
    {
        var text = BsonDocument.Parse("{ v: 2, key: { _fts: 'text', _ftsx: 1 }, name: 'note_text', weights: { note: 1 } }");
        Assert.AreEqual(IndexForm.Text, IndexAdvisor.ShapeOf(text));
        (string field, BsonValue value) = IndexAdvisor.DisplayKeys(text).Single();
        Assert.AreEqual("note", field);
        Assert.AreEqual("text", value.AsString);

        Assert.AreEqual(IndexForm.Single, IndexAdvisor.ShapeOf(BsonDocument.Parse("{ key: { a: 1 } }")));
        Assert.AreEqual(IndexForm.Compound, IndexAdvisor.ShapeOf(BsonDocument.Parse("{ key: { a: 1, b: -1 } }")));
        Assert.AreEqual(IndexForm.MultiKey, IndexAdvisor.ShapeOf(BsonDocument.Parse("{ key: { 'items.sku': 1 } }"), multiKey: true));
        Assert.AreEqual(IndexForm.Hashed, IndexAdvisor.ShapeOf(BsonDocument.Parse("{ key: { a: 'hashed' } }")));
        Assert.AreEqual(IndexForm.Geo, IndexAdvisor.ShapeOf(BsonDocument.Parse("{ key: { loc: '2dsphere' } }")));
        Assert.AreEqual(IndexForm.Wildcard, IndexAdvisor.ShapeOf(BsonDocument.Parse("{ key: { 'attrs.$**': 1 } }")));
    }

    [TestMethod]
    public void Estimates_scale_with_documents()
    {
        long small = IndexAdvisor.EstimateSize(1_000, 20);
        long large = IndexAdvisor.EstimateSize(1_000_000, 20);
        Assert.AreEqual(small * 1000, large, large / 100.0);
        Assert.IsTrue(IndexAdvisor.EstimateBuildSeconds(1_284_902, 42L << 30) > 60);
    }
}
