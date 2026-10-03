using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// BSON 的呈现、解析与编辑规则。网格、检查器、JSON 视图、表单与查询编辑器都靠这一层,
/// 同一个值在五处必须长得一样、改出来的也必须是同一个类型。
/// </summary>
[TestClass]
public sealed class BsonTests
{
    [TestMethod]
    public void Kinds_ClassifyUuidAndMissingSeparately()
    {
        Assert.AreEqual(BsonKind.Uuid, BsonKinds.Of(new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard)));
        Assert.AreEqual(BsonKind.Binary, BsonKinds.Of(new BsonBinaryData([1, 2, 3])));
        Assert.AreEqual(BsonKind.Missing, BsonKinds.Of(null));
        Assert.AreEqual(BsonKind.Null, BsonKinds.Of(BsonNull.Value));
        Assert.AreEqual("VelaShellGreen", BsonKinds.ColorToken(BsonKind.Decimal128));
        Assert.AreEqual("VelaShellBlue", BsonKinds.ColorToken(BsonKind.ObjectId));
    }

    [TestMethod]
    public void Cell_FormatsLikeTheDesignGrid()
    {
        Assert.AreEqual("1,299.00", BsonText.Cell(new BsonDecimal128(Decimal128.Parse("1299.00"))));
        Assert.AreEqual("8,740.00", BsonText.Cell(new BsonDecimal128(Decimal128.Parse("8740.00"))));
        Assert.AreEqual("6172.86", BsonText.Cell(new BsonDouble(6172.86)));
        Assert.AreEqual("12.0", BsonText.Cell(new BsonDouble(12)), "整数值的 Double 要能和 Int32 区分开");
        Assert.AreEqual("1,284,902", BsonText.Cell(new BsonInt64(1284902)));
        Assert.AreEqual("paid", BsonText.Cell(new BsonString("paid")));
        Assert.AreEqual("—", BsonText.Cell(null));
        Assert.AreEqual("null", BsonText.Cell(BsonNull.Value));
        Assert.AreEqual("{3} 陈立 · VIP", BsonText.Cell(new BsonDocument { { "id", ObjectId.GenerateNewId() }, { "name", "陈立" }, { "level", "VIP" } }));
        Assert.AreEqual("[2] SKU-1182…", BsonText.Cell(new BsonArray { new BsonDocument("sku", "SKU-1182"), new BsonDocument("sku", "SKU-2044") }));
    }

    [TestMethod]
    public void Cell_DatesAreLocalTime()
    {
        var utc = new DateTime(2026, 9, 26, 1, 12, 0, DateTimeKind.Utc);
        string expected = utc.ToLocalTime().ToString(BsonText.DateFormat, CultureInfo.InvariantCulture);
        Assert.AreEqual(expected, BsonText.Cell(new BsonDateTime(utc)));
    }

    [TestMethod]
    public void Pretty_ShellModeMatchesMongosh()
    {
        var doc = new BsonDocument
        {
            { "_id", ObjectId.Parse("66f5c2a1b04e97d2c3a1f58e") },
            { "orderNo", "SO2609-10402" },
            { "total", new BsonDecimal128(Decimal128.Parse("516.00")) },
            { "createdAt", new BsonDateTime(new DateTime(2026, 9, 26, 20, 51, 27, DateTimeKind.Utc)) },
            { "tags", new BsonArray { "企业", "开票" } },
            { "weird key", 1 }
        };
        string expected = """
            {
              _id: ObjectId("66f5c2a1b04e97d2c3a1f58e"),
              orderNo: "SO2609-10402",
              total: NumberDecimal("516.00"),
              createdAt: ISODate("2026-09-26T20:51:27Z"),
              tags: [ "企业", "开票" ],
              "weird key": 1
            }
            """;
        Assert.AreEqual(expected.ReplaceLineEndings("\n"), BsonText.Pretty(doc));
    }

    [TestMethod]
    public void Pretty_RelaxedAndCanonicalComeFromTheDriver()
    {
        var doc = new BsonDocument("n", 5);
        StringAssert.Contains(BsonText.Pretty(doc, EjsonMode.Relaxed), "\"n\" : 5");
        StringAssert.Contains(BsonText.Pretty(doc, EjsonMode.Canonical), "$numberInt");
    }

    [TestMethod]
    public void ShellJson_ParsesMongoshLiterals()
    {
        BsonDocument doc = ShellJson.ParseDocument("""
            // 近 30 天高价值订单
            { status: 'paid', total: { $gte: 500 }, createdAt: { $gte: ISODate("2026-08-27") },
              id: ObjectId("66f5c2a1b04e97d2c3a1f58e"), amount: NumberDecimal("8740.00"), name: /^张/i, }
            """);
        Assert.AreEqual("paid", doc["status"].AsString);
        Assert.AreEqual(500, doc["total"]["$gte"].AsInt32);
        Assert.AreEqual(BsonType.DateTime, doc["createdAt"]["$gte"].BsonType);
        Assert.AreEqual(BsonType.ObjectId, doc["id"].BsonType);
        Assert.AreEqual(BsonType.Decimal128, doc["amount"].BsonType);
        Assert.AreEqual(BsonType.RegularExpression, doc["name"].BsonType);
    }

    [TestMethod]
    public void ShellJson_DottedBareKeysAreAnError()
    {
        IReadOnlyList<ShellDiagnostic> diagnostics = ShellJson.Diagnose("{ orderNo: 1, customer.name: 1 }");
        ShellDiagnostic diagnostic = diagnostics.Single();
        Assert.AreEqual("Shell_DottedKey", diagnostic.MessageKey);
        Assert.AreEqual("\"customer.name\"", diagnostic.Fix);
        Assert.AreEqual(14, diagnostic.Offset);
        Assert.ThrowsExactly<ShellJsonException>(() => ShellJson.ParseDocument("{ customer.name: 1 }"));
        Assert.AreEqual(1, ShellJson.ParseDocument("{ \"customer.name\": 1 }")["customer.name"].AsInt32);
    }

    [TestMethod]
    public void ShellJson_EmptyTextIsAnEmptyFilter() => Assert.AreEqual(0, ShellJson.ParseDocument("  ").ElementCount);

    [TestMethod]
    public void ShellJson_ParsesPipelines()
    {
        BsonArray pipeline = ShellJson.ParseArray("[ { $match: { status: \"paid\" } }, { $group: { _id: \"$customer.id\", n: { $sum: 1 } } } ]");
        Assert.AreEqual(2, pipeline.Count);
        Assert.AreEqual("$customer.id", pipeline[1]["$group"]["_id"].AsString);
    }

    [TestMethod]
    public void Edit_ParsesEachKind()
    {
        Assert.IsTrue(BsonEdit.TryParse("1,580", BsonKind.Int32, out BsonValue i32, out _));
        Assert.AreEqual(1580, i32.AsInt32);
        Assert.IsFalse(BsonEdit.TryParse("3000000000", BsonKind.Int32, out _, out string? overflow));
        Assert.AreEqual("Edit_NotInt32", overflow);
        Assert.IsTrue(BsonEdit.TryParse("1580.00", BsonKind.Decimal128, out BsonValue dec, out _));
        Assert.AreEqual("1580.00", dec.AsDecimal128.ToString());
        Assert.IsTrue(BsonEdit.TryParse("是", BsonKind.Boolean, out BsonValue yes, out _));
        Assert.IsTrue(yes.AsBoolean);
        Assert.IsTrue(BsonEdit.TryParse("ObjectId(\"66f5c2a1b04e97d2c3a1f58e\")", BsonKind.ObjectId, out BsonValue oid, out _));
        Assert.AreEqual("66f5c2a1b04e97d2c3a1f58e", oid.AsObjectId.ToString());
        Assert.IsTrue(BsonEdit.TryParse("2026-09-27T02:30:00Z", BsonKind.Date, out BsonValue date, out _));
        Assert.AreEqual(new DateTime(2026, 9, 27, 2, 30, 0, DateTimeKind.Utc), date.ToUniversalTime());
        Assert.IsTrue(BsonEdit.TryParse("[\"企业\", \"开票\"]", BsonKind.Array, out BsonValue array, out _));
        Assert.AreEqual(2, array.AsBsonArray.Count);
        Assert.IsFalse(BsonEdit.TryParse("{", BsonKind.Object, out _, out string? broken));
        Assert.AreEqual("Edit_NotObject", broken);
    }

    [TestMethod]
    public void Edit_TextRoundTrips()
    {
        BsonValue[] values =
        [
            new BsonString("SO2609-10403"), new BsonInt32(42), new BsonInt64(1L << 40), new BsonDouble(3.25),
            new BsonDecimal128(Decimal128.Parse("8740.00")), BsonBoolean.True, ObjectId.Parse("66f5c2a1b04e97d2c3a1f58e"),
            new BsonBinaryData(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), GuidRepresentation.Standard)
        ];
        foreach (BsonValue value in values)
        {
            Assert.IsTrue(BsonEdit.TryParse(BsonEdit.EditText(value), BsonKinds.Of(value), out BsonValue back, out _), value.ToString());
            Assert.AreEqual(value, back);
        }
    }

    [TestMethod]
    public void Edit_ConvertBetweenKinds()
    {
        Assert.AreEqual(8740, BsonEdit.Convert(new BsonString("8740"), BsonKind.Int32, out bool reset1).AsInt32);
        Assert.IsFalse(reset1);
        Assert.AreEqual("8740.5", BsonEdit.Convert(new BsonDouble(8740.5), BsonKind.Decimal128, out _).AsDecimal128.ToString());
        BsonValue garbage = BsonEdit.Convert(new BsonString("abc"), BsonKind.Int32, out bool reset2);
        Assert.IsTrue(reset2);
        Assert.AreEqual(0, garbage.AsInt32);
        Assert.AreEqual("true", BsonEdit.Convert(BsonBoolean.True, BsonKind.String, out _).AsString);
    }

    [TestMethod]
    public void Path_ReadsWritesAndRemoves()
    {
        var doc = new BsonDocument
        {
            { "customer", new BsonDocument { { "name", "张伟" } } },
            { "items", new BsonArray { new BsonDocument("sku", "SKU-1182"), new BsonDocument("sku", "SKU-2044") } }
        };
        Assert.AreEqual("SKU-2044", BsonPath.Get(doc, "items.1.sku")!.AsString);
        Assert.IsNull(BsonPath.Get(doc, "items.5.sku"));
        Assert.IsTrue(BsonPath.Set(doc, "customer.level", "SVIP"));
        Assert.IsTrue(BsonPath.Set(doc, "invoice.title", "云帆"), "缺的文档层自动补上(同 $set)");
        Assert.AreEqual("云帆", doc["invoice"]["title"].AsString);
        Assert.IsTrue(BsonPath.Unset(doc, "items.0"));
        Assert.AreEqual(1, doc["items"].AsBsonArray.Count);
        Assert.IsFalse(BsonPath.Unset(doc, "nope"));
        string[] paths = [.. BsonPath.Walk(doc).Select(static p => p.Path)];
        CollectionAssert.Contains(paths, "items.sku", "数组里的对象合并成一条路径");
        CollectionAssert.Contains(paths, "customer.level");
    }

    [TestMethod]
    public void Sizes_AndCountsAbbreviate()
    {
        Assert.AreEqual("42.1 GB", BsonText.Bytes(45_204_000_000));
        Assert.AreEqual("312 KB", BsonText.Bytes(319_488));
        Assert.AreEqual("1.28M", BsonText.Count(1_284_902));
        Assert.AreEqual("86.4K", BsonText.Count(86_410));
        Assert.AreEqual("640", BsonText.Count(640));
        Assert.AreEqual("66f5c2a1…f58e", BsonText.Shorten("66f5c2a1b04e97d2c3a1f58e"));
    }
}
