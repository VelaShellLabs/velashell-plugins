using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 「导出为代码」:同一条语句翻成五种写法,字面量保留 BSON 类型(日期是日期、金额是 Decimal128、ObjectId 是 ObjectId)。
/// </summary>
[TestClass]
public sealed class ShellCodeExportTests
{
    private static ShellCommand Parse(string text) => ShellParser.Parse(ShellScript.Split(text)[0]);

    private static readonly ShellCommand Find = Parse(
        "db.orders.find({ status: \"paid\", total: { $gte: NumberDecimal(\"500\") }, createdAt: { $gte: ISODate(\"2026-08-27\") } }, " +
        "{ orderNo: 1, \"customer.name\": 1 }).sort({ createdAt: -1 }).limit(100)");

    private static readonly ShellCommand Aggregate = Parse(
        "db.orders.aggregate([{ $match: { \"customer.id\": ObjectId(\"66e01d9fbf275beacb43ec2c\") } }, { $group: { _id: \"$status\", n: { $sum: 1 } } }])");

    [TestMethod]
    public void CSharp_UsesTypedBsonLiterals()
    {
        string code = CodeExport.Generate(Find, "shop", CodeTarget.CSharp, "mongodb://127.0.0.1:27017");
        Assert.Contains("using MongoDB.Driver;", code);
        Assert.Contains("client.GetDatabase(\"shop\")", code);
        Assert.Contains("GetCollection<BsonDocument>(\"orders\")", code);
        Assert.Contains("{ \"total\", new BsonDocument(\"$gte\", Decimal128.Parse(\"500\")) }", code);
        Assert.Contains("new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc)", code);
        Assert.Contains(".Sort(sort)", code);
        Assert.Contains(".Limit(100)", code);
        Assert.Contains("ToListAsync()", code);

        string pipeline = CodeExport.Generate(Aggregate, "shop", CodeTarget.CSharp);
        Assert.Contains("new ObjectId(\"66e01d9fbf275beacb43ec2c\")", pipeline);
        Assert.Contains("collection.Aggregate<BsonDocument>(pipeline)", pipeline);
    }

    [TestMethod]
    public void Python_UsesPyMongoConventions()
    {
        string code = CodeExport.Generate(Find, "shop", CodeTarget.Python);
        Assert.Contains("from pymongo import MongoClient", code);
        Assert.Contains("from bson.decimal128 import Decimal128", code);
        Assert.Contains("datetime(2026, 8, 27, tzinfo=timezone.utc)", code);
        Assert.Contains(".sort([(\"createdAt\", -1)])", code);
        Assert.Contains("collection.find(filter, projection)", code);
        Assert.Contains("from bson import ObjectId", CodeExport.Generate(Aggregate, "shop", CodeTarget.Python));
    }

    [TestMethod]
    public void Node_AndJava()
    {
        string node = CodeExport.Generate(Find, "shop", CodeTarget.NodeJs);
        Assert.Contains("require(\"mongodb\")", node);
        Assert.Contains("Decimal128.fromString(\"500\")", node);
        Assert.Contains("new Date(\"2026-08-27T00:00:00Z\")", node);
        Assert.Contains("projection: { orderNo: 1, \"customer.name\": 1 }", node);
        Assert.Contains(".toArray()", node);

        string java = CodeExport.Generate(Find, "shop", CodeTarget.Java);
        Assert.Contains("import org.bson.Document;", java);
        Assert.Contains("new Document(\"status\", \"paid\")", java);
        Assert.Contains("new Decimal128(new BigDecimal(\"500\"))", java);
        Assert.Contains("Date.from(Instant.parse(\"2026-08-27T00:00:00Z\"))", java);
        Assert.Contains(".limit(100)", java);
    }

    [TestMethod]
    public void Mongosh_IsNormalizedAndReparseable()
    {
        string code = CodeExport.Generate(Find, "shop", CodeTarget.Mongosh);
        Assert.StartsWith("use(\"shop\");", code);
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(code);
        Assert.HasCount(2, statements);
        ShellCommand again = ShellParser.Parse(statements[1]);
        Assert.AreEqual("find", again.Method!.Name);
        Assert.AreEqual(100, again.Modifier("limit")!.Arg(0)!.AsInt32);
    }

    [TestMethod]
    public void Writes_AndUnsupported()
    {
        ShellCommand update = Parse("db.orders.updateMany({ status: \"open\" }, { $set: { status: \"paid\" } }, { upsert: true })");
        Assert.Contains("UpdateManyAsync(filter, update, new UpdateOptions { IsUpsert = true })", CodeExport.Generate(update, "shop", CodeTarget.CSharp));
        Assert.Contains("update_many(filter, update, upsert=True)", CodeExport.Generate(update, "shop", CodeTarget.Python));
        Assert.Contains("updateMany(filter, update, { upsert: true })", CodeExport.Generate(update, "shop", CodeTarget.NodeJs));

        ShellCommand show = Parse("show dbs");
        Assert.Contains("no driver equivalent", CodeExport.Generate(show, "shop", CodeTarget.CSharp));
    }
}
