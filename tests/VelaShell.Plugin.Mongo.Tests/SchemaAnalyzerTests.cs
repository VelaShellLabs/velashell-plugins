using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// Schema 抽样分析(设计稿 08 的每一行):计数口径、类型分布、提示措辞、值分布的画法。
/// 纯函数,不连服务器。
/// </summary>
[TestClass]
public sealed class SchemaAnalyzerTests
{
    private static readonly Loc Zh = new("zh-CN");

    /// <summary>与 shop.orders 同形的一批文档(状态四种、customer.level 有 3% 缺失、note 混合 String / Null / 缺失)。</summary>
    private static List<BsonDocument> Orders(int count = 200)
    {
        string[] statuses = ["paid", "paid", "paid", "shipped", "pending", "refunded", "paid", "shipped"];
        string[] levels = ["普通", "VIP", "普通", "SVIP", "普通"];
        var start = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<BsonDocument>();
        for (int i = 0; i < count; i++)
        {
            var customer = new BsonDocument("id", ObjectId.GenerateNewId());
            if (i % 33 != 0)
            {
                customer["level"] = levels[i % levels.Length];
            }
            var doc = new BsonDocument
            {
                { "_id", ObjectId.GenerateNewId() },
                { "orderNo", $"SO2609-{10400 + i}" },
                { "status", statuses[i % statuses.Length] },
                { "total", new BsonDecimal128(new Decimal128(86.5m + (i * 37 % 12000))) },
                { "createdAt", start.AddHours(i * 3) },
                { "customer", customer },
                {
                    "items", new BsonArray(Enumerable.Range(0, 1 + (i % 3))
                        .Select(k => new BsonDocument { { "sku", $"SKU-{k}" }, { "qty", 1 + k } }))
                }
            };
            if (i % 5 == 0)
            {
                doc["note"] = BsonNull.Value;
            }
            else if (i % 5 is 1 or 2)
            {
                doc["note"] = "加急";
            }
            list.Add(doc);
        }
        return list;
    }

    [TestMethod]
    public void Counts_per_document_and_keeps_first_seen_order()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(Orders());
        Assert.AreEqual(200, schema.Sampled);
        string[] paths = [.. schema.Fields.Select(static f => f.Path)];
        CollectionAssert.IsSubsetOf(new[] { "_id", "orderNo", "status", "total", "createdAt", "customer", "customer.id", "customer.level", "items", "items.sku", "items.qty", "note" }, paths);
        Assert.IsTrue(Array.IndexOf(paths, "customer") < Array.IndexOf(paths, "customer.level"), "parents come before children");

        AnalyzedField level = schema["customer.level"]!;
        Assert.AreEqual(200 - 7, level.Present, "i % 33 == 0 → 7 documents without customer.level");
        Assert.AreEqual(7, level.Missing);
        Assert.AreEqual("customer", level.Parent);

        AnalyzedField sku = schema["items.sku"]!;
        Assert.IsTrue(sku.InArray);
        Assert.AreEqual(200, sku.Present, "presence is per document, not per array element");
        Assert.IsTrue(sku.ScalarCount > 200, "values are counted per element");
    }

    [TestMethod]
    public void Mixed_types_are_flagged_with_the_dominant_value_type()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(Orders());
        AnalyzedField note = schema["note"]!;
        Assert.IsTrue(note.IsMixed);
        Assert.AreEqual(BsonKind.String, note.DominantValueKind);
        (string text, bool warning) = SchemaAnalyzer.Describe(note, Zh);
        Assert.IsTrue(warning);
        Assert.AreEqual("混合类型 ⚠ 建议统一为 String", text);
        Assert.AreEqual(AnalyzedViz.Text, SchemaAnalyzer.VizOf(note));
        StringAssert.StartsWith(SchemaAnalyzer.VizText(note, Zh), "\"加急\" 100%");
    }

    [TestMethod]
    public void Low_cardinality_strings_read_as_equality_prefixes_and_draw_bars()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(Orders());
        AnalyzedField status = schema["status"]!;
        Assert.IsTrue(SchemaAnalyzer.IsLowCardinality(status));
        Assert.AreEqual("String · 4 个取值 · 适合作等值前缀", SchemaAnalyzer.Describe(status, Zh).Text);
        Assert.AreEqual(AnalyzedViz.Bars, SchemaAnalyzer.VizOf(status));
        IReadOnlyList<AnalyzedBucket> top = SchemaAnalyzer.TopValues(status);
        Assert.AreEqual("\"paid\"", top[0].Label);
        Assert.AreEqual(0.5, top[0].Ratio, 0.001);
        Assert.AreEqual(1.0, top.Sum(static b => b.Ratio), 0.001);
    }

    [TestMethod]
    public void Missing_fields_suggest_a_default()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(Orders());
        Assert.AreEqual("String · 4% 缺失 · 建议补默认值", SchemaAnalyzer.Describe(schema["customer.level"]!, Zh).Text);
    }

    [TestMethod]
    public void Ids_are_unique_and_numbers_get_median_and_p95()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(Orders());
        Assert.AreEqual("ObjectId · 唯一", SchemaAnalyzer.Describe(schema["_id"]!, Zh).Text);
        StringAssert.StartsWith(SchemaAnalyzer.VizText(schema["_id"]!, Zh), "200 个唯一值 · 时间戳 ");

        AnalyzedField total = schema["total"]!;
        StringAssert.StartsWith(SchemaAnalyzer.Describe(total, Zh).Text, "Decimal128 · 中位数 ");
        Assert.AreEqual(AnalyzedViz.Histogram, SchemaAnalyzer.VizOf(total));
        (double[] bins, double min, double max, bool _) = SchemaAnalyzer.Histogram(total);
        Assert.AreEqual(SchemaAnalyzer.HistogramBins, bins.Length);
        Assert.AreEqual(200, bins.Sum(), "every value lands in exactly one bin");
        Assert.AreEqual(0, min, "non-negative values close to zero start the axis at 0");
        Assert.IsTrue(max > 7000);
    }

    [TestMethod]
    public void Dates_bin_by_day_and_arrays_by_length()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(Orders());
        AnalyzedField created = schema["createdAt"]!;
        Assert.AreEqual(AnalyzedViz.Timeline, SchemaAnalyzer.VizOf(created));
        (double[] bins, DateTime start, DateTime end, int perBin) = SchemaAnalyzer.Timeline(created);
        Assert.AreEqual(1, perBin);
        Assert.AreEqual(new DateTime(2026, 8, 27), start);
        Assert.AreEqual((end - start).Days + 1, bins.Length);
        Assert.AreEqual(200, bins.Sum());

        AnalyzedField items = schema["items"]!;
        Assert.AreEqual(AnalyzedViz.Lengths, SchemaAnalyzer.VizOf(items));
        Assert.AreEqual("Array<Object> · 子字段 100% 出现", SchemaAnalyzer.Describe(items, Zh).Text);
        IReadOnlyList<AnalyzedBucket> lengths = SchemaAnalyzer.Lengths(items);
        CollectionAssert.AreEqual(new[] { "1", "2", "3", "4", "5+" }, lengths.Select(static b => b.Label).ToArray());
        Assert.AreEqual(0, lengths[3].Count);
    }

    [TestMethod]
    public void Weekend_dip_needs_two_weeks_of_evidence()
    {
        var docs = new List<BsonDocument>();
        var monday = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Local);
        for (int day = 0; day < 21; day++)
        {
            DateTime date = monday.AddDays(day);
            int perDay = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 2 : 10;
            for (int i = 0; i < perDay; i++)
            {
                docs.Add(new BsonDocument("at", date.ToUniversalTime()));
            }
        }
        AnalyzedSchema schema = SchemaAnalyzer.Analyze(docs);
        Assert.IsTrue(SchemaAnalyzer.WeekendDip(schema["at"]!));
        Assert.AreEqual("Date · 周末低谷明显", SchemaAnalyzer.Describe(schema["at"]!, Zh).Text);

        AnalyzedSchema shortSpan = SchemaAnalyzer.Analyze(docs.Take(40).ToList());
        Assert.IsFalse(SchemaAnalyzer.WeekendDip(shortSpan["at"]!), "four days are not enough to call a weekly pattern");
    }

    [TestMethod]
    public void Empty_sample_yields_no_fields()
    {
        AnalyzedSchema schema = SchemaAnalyzer.Analyze([]);
        Assert.AreEqual(0, schema.Sampled);
        Assert.AreEqual(0, schema.Fields.Count);
    }
}
