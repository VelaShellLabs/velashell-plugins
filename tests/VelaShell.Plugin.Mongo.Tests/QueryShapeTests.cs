using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>查询形状归一化、mongosh 语句还原与 ESR 索引建议。</summary>
[TestClass]
public sealed class QueryShapeTests
{
    [TestMethod]
    public void Literals_become_question_marks_but_operators_stay()
    {
        BsonDocument filter = BsonDocument.Parse("""{ "customer.level": "SVIP", total: { $gte: 5000 } }""");
        Assert.AreEqual("{ customer.level: ?, total: { $gte: ? } }", QueryShape.Normalize(filter));
    }

    [TestMethod]
    public void In_arrays_collapse_to_one_placeholder()
    {
        BsonDocument a = BsonDocument.Parse("{ status: { $in: ['paid', 'shipped'] } }");
        BsonDocument b = BsonDocument.Parse("{ status: { $in: ['paid', 'shipped', 'void', 'new'] } }");
        Assert.AreEqual("{ status: { $in: ? } }", QueryShape.Normalize(a));
        Assert.AreEqual(QueryShape.Normalize(a), QueryShape.Normalize(b));
    }

    [TestMethod]
    public void Logical_branches_keep_their_structure()
    {
        BsonDocument filter = BsonDocument.Parse("{ $or: [ { a: 1 }, { b: { $gt: 2 } } ], c: { $exists: true } }");
        Assert.AreEqual("{ $or: [{ a: ? }, { b: { $gt: ? } }], c: { $exists: true } }", QueryShape.Normalize(filter));
    }

    [TestMethod]
    public void Expr_keeps_field_paths()
    {
        BsonDocument filter = BsonDocument.Parse("{ $expr: { $gt: ['$spent', '$budget'] }, kind: 'x' }");
        Assert.AreEqual("{ $expr: { $gt: [$spent, $budget] }, kind: ? }", QueryShape.Normalize(filter));
    }

    [TestMethod]
    public void Empty_filter_is_braces()
    {
        Assert.AreEqual("{}", QueryShape.Normalize(new BsonDocument()));
    }

    [TestMethod]
    public void Pipeline_masks_match_values_and_keeps_group_paths()
    {
        var pipeline = BsonArray.Create(new[]
        {
            BsonDocument.Parse("{ $match: { day: '2026-09-30' } }"),
            BsonDocument.Parse("{ $group: { _id: '$device', n: { $sum: 1 } } }"),
            BsonDocument.Parse("{ $sort: { n: -1 } }"),
            BsonDocument.Parse("{ $limit: 10 }")
        });
        Assert.AreEqual("[$match { day: ? }, $group { _id: $device, n: { $sum: 1 } }, $sort { n: -1 }, $limit 10]",
            QueryShape.NormalizePipeline(pipeline));
    }

    [TestMethod]
    public void Find_entry_shape_has_sort_and_limit()
    {
        BsonDocument entry = Find("""{ "customer.level": "SVIP", total: { $gte: 5000 } }""", "{ total: -1 }", limit: 50);
        QueryShape shape = QueryShape.FromProfile(entry);
        Assert.AreEqual("orders.find", shape.Title);
        Assert.AreEqual("{ customer.level: ?, total: { $gte: ? } }  sort { total: -1 }  limit 50", shape.Summary);
    }

    [TestMethod]
    public void Different_literals_share_a_shape_but_different_sorts_do_not()
    {
        QueryShape a = QueryShape.FromProfile(Find("""{ "customer.level": "SVIP", total: { $gte: 5000 } }""", "{ total: -1 }"));
        QueryShape b = QueryShape.FromProfile(Find("""{ "customer.level": "VIP", total: { $gte: 12 } }""", "{ total: -1 }"));
        QueryShape c = QueryShape.FromProfile(Find("""{ "customer.level": "VIP", total: { $gte: 12 } }""", "{ total: 1 }"));
        Assert.AreEqual(a.Key, b.Key);
        Assert.AreNotEqual(a.Key, c.Key);
    }

    [TestMethod]
    public void Getmore_joins_the_originating_find()
    {
        BsonDocument find = Find("{ sku: 'A-1' }", null);
        var getMore = new BsonDocument
        {
            { "op", "getmore" },
            { "ns", "shop.orders" },
            { "command", new BsonDocument { { "getMore", 123L }, { "collection", "orders" } } },
            { "originatingCommand", find["command"] }
        };
        Assert.AreEqual(QueryShape.FromProfile(find).Key, QueryShape.FromProfile(getMore).Key);
    }

    [TestMethod]
    public void Writes_are_named_by_multiplicity()
    {
        var update = new BsonDocument
        {
            { "op", "update" },
            { "ns", "shop.carts" },
            { "command", BsonDocument.Parse("{ q: { expireAt: { $lt: 1 } }, u: { $set: { x: 1 } }, multi: true }") }
        };
        var remove = new BsonDocument
        {
            { "op", "remove" },
            { "ns", "shop.carts" },
            { "command", BsonDocument.Parse("{ q: { _id: 3 }, limit: 1 }") }
        };
        Assert.AreEqual("carts.updateMany", QueryShape.FromProfile(update).Title);
        Assert.AreEqual("{ expireAt: { $lt: ? } }", QueryShape.FromProfile(update).Summary);
        Assert.AreEqual("carts.deleteOne", QueryShape.FromProfile(remove).Title);
    }

    [TestMethod]
    public void Aggregate_entry_uses_the_pipeline_shape()
    {
        var entry = new BsonDocument
        {
            { "op", "command" },
            { "ns", "shop.events" },
            { "command", BsonDocument.Parse("{ aggregate: 'events', pipeline: [ { $match: { day: 'x' } }, { $group: { _id: '$device' } } ], cursor: {} }") }
        };
        QueryShape shape = QueryShape.FromProfile(entry);
        Assert.AreEqual("events.aggregate", shape.Title);
        Assert.AreEqual("[$match { day: ? }, $group { _id: $device }]", shape.Summary);
    }

    [TestMethod]
    public void Rebuilt_find_matches_the_design_sample()
    {
        BsonDocument entry = Find("""{ "customer.level": "SVIP", total: { $gte: 5000 } }""", "{ total: -1 }");
        (string statement, string explain) = QueryShape.Rebuild(entry);
        Assert.AreEqual("db.orders.find({\n  \"customer.level\": \"SVIP\",\n  total: { $gte: 5000 }\n}).sort({ total: -1 })", statement);
        Assert.AreEqual(statement + ".explain(\"executionStats\")", explain);
    }

    [TestMethod]
    public void Rebuilt_write_explains_without_executing()
    {
        var entry = new BsonDocument
        {
            { "op", "update" },
            { "ns", "shop.carts" },
            { "command", BsonDocument.Parse("{ q: { qty: { $lt: 1 } }, u: { $set: { gone: true } }, multi: true }") }
        };
        (string statement, string explain) = QueryShape.Rebuild(entry);
        Assert.AreEqual("db.carts.updateMany({ qty: { $lt: 1 } }, { $set: { gone: true } })", statement);
        // 写操作的执行计划一定走 db.coll.explain(...).method(...):在语句末尾接 explain 会先把更新执行一遍。
        Assert.AreEqual("db.carts.explain(\"executionStats\").updateMany({ qty: { $lt: 1 } }, { $set: { gone: true } })", explain);
    }

    [TestMethod]
    public void Rebuilt_aggregate_puts_one_stage_per_line()
    {
        var entry = new BsonDocument
        {
            { "op", "command" },
            { "ns", "shop.events" },
            { "command", BsonDocument.Parse("{ aggregate: 'events', pipeline: [ { $match: { day: 'x' } }, { $count: 'n' } ], cursor: {}, lsid: { id: 1 } }") }
        };
        (string statement, _) = QueryShape.Rebuild(entry);
        Assert.AreEqual("db.events.aggregate([\n  { $match: { day: \"x\" } },\n  { $count: \"n\" }\n])", statement);
    }

    [TestMethod]
    public void Odd_collection_names_use_getCollection()
    {
        BsonDocument entry = Find("{}", null, collection: "order-items");
        (string statement, _) = QueryShape.Rebuild(entry);
        Assert.AreEqual("db.getCollection(\"order-items\").find({})", statement);
    }

    [TestMethod]
    public void Esr_puts_equality_then_sort_then_range()
    {
        IndexAdvice? advice = IndexAdvice.Suggest(
            BsonDocument.Parse("{ status: 'paid', createdAt: { $gt: 1 }, region: { $in: ['a', 'b'] } }"),
            BsonDocument.Parse("{ amount: -1 }"));
        Assert.IsNotNull(advice);
        Assert.AreEqual("{ status: 1, region: 1, amount: -1, createdAt: 1 }", advice.KeysText);
        CollectionAssert.AreEqual(new[] { "status", "region" }, advice.Equality.ToArray());
        CollectionAssert.AreEqual(new[] { "amount" }, advice.Sort.ToArray());
        CollectionAssert.AreEqual(new[] { "createdAt" }, advice.Range.ToArray());
    }

    [TestMethod]
    public void A_field_used_for_sort_and_range_sits_in_the_sort_slot()
    {
        IndexAdvice? advice = IndexAdvice.Suggest(
            BsonDocument.Parse("""{ "customer.level": "SVIP", total: { $gte: 5000 } }"""),
            BsonDocument.Parse("{ total: -1 }"));
        Assert.IsNotNull(advice);
        Assert.AreEqual("{ \"customer.level\": 1, total: -1 }", advice.KeysText);
        CollectionAssert.AreEqual(new[] { "total" }, advice.SortAndRange.ToArray());
        Assert.AreEqual(0, advice.Range.Count);
    }

    [TestMethod]
    public void Or_filters_get_no_single_index()
    {
        Assert.IsNull(IndexAdvice.Suggest(BsonDocument.Parse("{ $or: [ { a: 1 }, { b: 2 } ] }"), null));
    }

    [TestMethod]
    public void And_branches_are_flattened()
    {
        IndexAdvice? advice = IndexAdvice.Suggest(BsonDocument.Parse("{ $and: [ { a: 1 }, { b: { $lt: 3 } } ] }"), null);
        Assert.IsNotNull(advice);
        Assert.AreEqual("{ a: 1, b: 1 }", advice.KeysText);
    }

    [TestMethod]
    public void Existing_prefix_or_reversed_index_covers_the_advice()
    {
        IndexAdvice advice = IndexAdvice.Suggest(BsonDocument.Parse("{ a: 1 }"), BsonDocument.Parse("{ t: -1 }"))!;
        Assert.IsTrue(advice.CoveredBy(BsonDocument.Parse("{ a: 1, t: -1, z: 1 }")));
        Assert.IsTrue(advice.CoveredBy(BsonDocument.Parse("{ a: -1, t: 1 }")));
        Assert.IsFalse(advice.CoveredBy(BsonDocument.Parse("{ a: 1, t: 1 }")));
        Assert.IsFalse(advice.CoveredBy(BsonDocument.Parse("{ t: -1 }")));
    }

    private static BsonDocument Find(string filter, string? sort, int limit = 0, string collection = "orders")
    {
        var command = new BsonDocument { { "find", collection }, { "filter", BsonDocument.Parse(filter) } };
        if (sort is not null)
        {
            command["sort"] = BsonDocument.Parse(sort);
        }
        if (limit > 0)
        {
            command["limit"] = limit;
        }
        command["lsid"] = new BsonDocument("id", 1);
        command["$db"] = "shop";
        return new BsonDocument { { "op", "query" }, { "ns", $"shop.{collection}" }, { "command", command } };
    }
}
