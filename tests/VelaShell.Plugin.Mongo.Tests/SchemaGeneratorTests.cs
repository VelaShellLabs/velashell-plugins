using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 由抽样生成 <c>$jsonSchema</c>(设计稿 08 右栏):required 只收 100% 出现的字段、enum 只给稳定的少量取值、
/// 全非负数值给 minimum 0、混合类型给 bsonType 数组、嵌套文档递归;以及按宽度折行的排版能被原样解析回来。
/// </summary>
[TestClass]
public sealed class SchemaGeneratorTests
{
    private static List<BsonDocument> Sample()
    {
        string[] statuses = ["paid", "shipped", "pending", "refunded"];
        var list = new List<BsonDocument>();
        for (int i = 0; i < 120; i++)
        {
            var doc = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "orderNo", $"SO-{i:D5}" },
                { "status", statuses[i % statuses.Length] },
                { "total", new BsonDecimal128(new Decimal128(10.5m + i)) },
                { "qty", i % 2 == 0 ? new BsonInt32(i) : new BsonDouble(i + 0.5) },
                { "createdAt", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i) },
                { "customer", new BsonDocument { { "id", ObjectId.GenerateNewId() }, { "level", i % 2 == 0 ? "VIP" : "普通" } } },
                { "items", new BsonArray { new BsonDocument("sku", "SKU-1") } }
            };
            if (i % 3 == 0)
            {
                doc["note"] = i % 2 == 0 ? BsonNull.Value : "加急";
            }
            if (i == 7)
            {
                doc["delta"] = -1;
            }
            list.Add(doc);
        }
        return list;
    }

    private static BsonDocument Generate() => JsonSchemaGenerator.Generate(SchemaAnalyzer.Analyze(Sample()));

    [TestMethod]
    public void Required_lists_fields_present_in_every_document_but_not_id()
    {
        BsonDocument schema = Generate()["$jsonSchema"].AsBsonDocument;
        Assert.AreEqual("object", schema["bsonType"].AsString);
        string[] required = [.. schema["required"].AsBsonArray.Select(static v => v.AsString)];
        CollectionAssert.AreEquivalent(new[] { "orderNo", "status", "total", "qty", "createdAt", "customer", "items" }, required);
        Assert.IsFalse(schema["properties"].AsBsonDocument.Contains("_id"));
    }

    [TestMethod]
    public void Stable_small_string_sets_become_enum()
    {
        BsonDocument properties = Generate()["$jsonSchema"]["properties"].AsBsonDocument;
        BsonDocument status = properties["status"].AsBsonDocument;
        Assert.IsFalse(status.Contains("bsonType"), "enum already pins the type");
        CollectionAssert.AreEquivalent(new[] { "paid", "shipped", "pending", "refunded" },
            status["enum"].AsBsonArray.Select(static v => v.AsString).ToArray());
        Assert.AreEqual("string", properties["orderNo"]["bsonType"].AsString, "high-cardinality strings get no enum");
    }

    [TestMethod]
    public void Rare_values_do_not_make_an_enum()
    {
        var docs = Enumerable.Range(0, 100).Select(static i => new BsonDocument("kind", i == 0 ? "rare" : i % 2 == 0 ? "a" : "b")).ToList();
        BsonDocument kind = JsonSchemaGenerator.Generate(SchemaAnalyzer.Analyze(docs))["$jsonSchema"]["properties"]["kind"].AsBsonDocument;
        Assert.IsFalse(kind.Contains("enum"), "a value seen once is probably a long tail the sample did not finish");
        Assert.AreEqual("string", kind["bsonType"].AsString);
    }

    [TestMethod]
    public void Numbers_get_minimum_zero_only_when_never_negative_and_mixed_numbers_become_number()
    {
        BsonDocument properties = Generate()["$jsonSchema"]["properties"].AsBsonDocument;
        Assert.AreEqual("decimal", properties["total"]["bsonType"].AsString);
        Assert.AreEqual(0, properties["total"]["minimum"].AsInt32);
        Assert.AreEqual("number", properties["qty"]["bsonType"].AsString);
        Assert.IsFalse(properties["delta"].AsBsonDocument.Contains("minimum"));
        Assert.AreEqual("date", properties["createdAt"]["bsonType"].AsString);
    }

    [TestMethod]
    public void Nullable_fields_list_both_types_and_nested_documents_recurse()
    {
        BsonDocument properties = Generate()["$jsonSchema"]["properties"].AsBsonDocument;
        CollectionAssert.AreEquivalent(new[] { "string", "null" }, properties["note"]["bsonType"].AsBsonArray.Select(static v => v.AsString).ToArray());
        BsonDocument customer = properties["customer"].AsBsonDocument;
        Assert.AreEqual("object", customer["bsonType"].AsString);
        CollectionAssert.AreEquivalent(new[] { "id", "level" }, customer["required"].AsBsonArray.Select(static v => v.AsString).ToArray());
        Assert.AreEqual("objectId", customer["properties"]["id"]["bsonType"].AsString);
        BsonDocument items = properties["items"].AsBsonDocument;
        Assert.AreEqual("array", items["bsonType"].AsString);
        Assert.AreEqual(1, items["minItems"].AsInt32);
    }

    [TestMethod]
    public void Format_wraps_by_width_and_round_trips_through_the_shell_parser()
    {
        BsonDocument validator = Generate();
        string text = JsonSchemaGenerator.Format(validator, width: 60);
        Assert.IsTrue(text.Split('\n').Length > 5, "a long schema spans several lines");
        Assert.IsTrue(text.Split('\n').All(static line => line.Length <= 90), "lines stay near the width");
        StringAssert.Contains(text, "$jsonSchema: {");
        BsonDocument parsed = ShellJson.ParseDocument(text);
        Assert.AreEqual(validator, parsed);

        string compact = JsonSchemaGenerator.Format(validator, width: 54, compactRoot: true);
        StringAssert.StartsWith(compact, "{ $jsonSchema: {\n  bsonType: \"object\",");
        StringAssert.EndsWith(compact, "} }");
        Assert.AreEqual(validator, ShellJson.ParseDocument(compact));
    }

    [TestMethod]
    public void Short_documents_stay_on_one_line()
    {
        var small = new BsonDocument("$jsonSchema", new BsonDocument { { "bsonType", "object" }, { "required", new BsonArray { "a" } } });
        Assert.AreEqual("{ $jsonSchema: { bsonType: \"object\", required: [ \"a\" ] } }", JsonSchemaGenerator.Format(small));
    }
}
