using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// CSV 的写出(RFC 4180 引号规则、嵌套展开、单元格换算、编码)与读入(引号字段里的换行与 <c>""</c>、
/// 行号、计数、认分隔符)。纯函数,不需要服务器。
/// </summary>
[TestClass]
public sealed class CsvTests
{
    [TestMethod]
    public void Writer_quotes_only_fields_that_need_it_and_doubles_quotes()
    {
        string row = CsvWriter.FormatRow(["plain", "a,b", "say \"hi\"", "two\nlines", "cr\rhere", " padded ", "", null], ',');
        Assert.AreEqual("plain,\"a,b\",\"say \"\"hi\"\"\",\"two\nlines\",\"cr\rhere\",\" padded \",,", row);
    }

    [TestMethod]
    public void Writer_respects_the_chosen_delimiter()
    {
        Assert.AreEqual("a,b;c", CsvWriter.FormatRow(["a,b", "c"], ';'), "a comma needs no quotes when ';' separates");
        Assert.AreEqual("\"a;b\";c", CsvWriter.FormatRow(["a;b", "c"], ';'));
        Assert.AreEqual("x\ty", CsvWriter.FormatRow(["x", "y"], '\t'));
    }

    [TestMethod]
    public void Writer_ends_every_row_with_crlf()
    {
        using var text = new StringWriter();
        var csv = new CsvWriter(text, ',');
        csv.WriteRow(["a", "b"]);
        csv.WriteRow(["c", "d"]);
        Assert.AreEqual("a,b\r\nc,d\r\n", text.ToString());
    }

    [TestMethod]
    public void Reader_round_trips_quotes_delimiters_and_embedded_newlines()
    {
        string[][] rows =
        [
            ["id", "note", "price"],
            ["1", "line one\r\nline two", "1,299.00"],
            ["2", "she said \"ok\"", ""],
            ["3", " spaced ", "x"]
        ];
        using var text = new StringWriter();
        var writer = new CsvWriter(text, ',');
        foreach (string[] row in rows)
        {
            writer.WriteRow(row);
        }
        var reader = new CsvReader(new StringReader(text.ToString()), ',');
        var read = new List<string[]>();
        var lines = new List<long>();
        while (reader.TryRead(out string[] record))
        {
            read.Add(record);
            lines.Add(reader.RecordLine);
        }
        Assert.HasCount(rows.Length, read);
        for (int i = 0; i < rows.Length; i++)
        {
            CollectionAssert.AreEqual(rows[i], read[i], $"row {i}");
        }
        CollectionAssert.AreEqual(new long[] { 1, 2, 4, 5 }, lines, "the quoted newline makes record 2 span two physical lines");
    }

    [TestMethod]
    public void Reader_accepts_lf_cr_and_trailing_rows_without_newline_and_skips_blank_lines()
    {
        var reader = new CsvReader(new StringReader("a,b\n\nc,d\re,f"), ',');
        var read = new List<string>();
        while (reader.TryRead(out string[] record))
        {
            read.Add(string.Join("|", record));
        }
        CollectionAssert.AreEqual(new[] { "a|b", "c|d", "e|f" }, read);
    }

    [TestMethod]
    public void Reader_is_lenient_about_stray_quotes_inside_unquoted_fields()
    {
        var reader = new CsvReader(new StringReader("5\" disk,ok\r\n"), ',');
        Assert.IsTrue(reader.TryRead(out string[] record));
        CollectionAssert.AreEqual(new[] { "5\" disk", "ok" }, record);
    }

    [TestMethod]
    public void Counting_ignores_newlines_inside_quotes_and_blank_lines()
    {
        const string text = "h1,h2\r\n1,\"a\r\nb\"\r\n\r\n2,c\r\n3,d";
        Assert.AreEqual(4, CsvReader.CountRecords(new StringReader(text)));
    }

    [TestMethod]
    public void Delimiter_detection_prefers_a_consistent_count()
    {
        Assert.AreEqual(';', CsvReader.DetectDelimiter(["a;b;c", "1;2,5;3", "4;5;6"]));
        Assert.AreEqual('\t', CsvReader.DetectDelimiter(["a\tb", "1\t2"]));
        Assert.AreEqual(',', CsvReader.DetectDelimiter(["a,\"b;c\",d", "1,2,3"]), "delimiters inside quotes do not count");
        Assert.AreEqual(',', CsvReader.DetectDelimiter(["single column"]));
    }

    [TestMethod]
    public void Export_writer_flattens_nested_fields_and_applies_conversions()
    {
        var id = ObjectId.Parse("66f5c2a1b04e1c3a5d7e9f01");
        var created = new DateTime(2026, 9, 27, 7, 0, 0, DateTimeKind.Utc);
        var doc = new BsonDocument
        {
            { "_id", id },
            { "orderNo", "SO2609-10400" },
            { "customer", new BsonDocument { { "name", "陈立" }, { "level", "VIP" } } },
            { "total", new BsonDecimal128(Decimal128.Parse("1299.5")) },
            { "createdAt", created },
            { "items", new BsonArray { new BsonDocument("sku", "SKU-1"), 2 } },
            { "note", BsonNull.Value }
        };
        IReadOnlyList<XferField> fields = SchemaSampler.Analyze([doc], flatten: true);
        CollectionAssert.AreEqual(
            new[] { "_id", "orderNo", "customer.name", "customer.level", "total", "createdAt", "items", "note" },
            fields.Select(static f => f.Path).ToArray());

        ExportColumn[] columns =
        [
            .. fields.Select(static f => new ExportColumn(f.Path, f.Path == "_id" ? "id" : f.Path, f.DefaultConversion))
        ];
        using var memory = new MemoryStream();
        using (var writer = new CsvExportWriter(memory, new CsvOptions { Encoding = TextEncodingKind.Utf8 }))
        {
            writer.Begin("orders", columns);
            writer.Write(doc);
            writer.End();
        }
        var reader = new CsvReader(new StringReader(Encoding.UTF8.GetString(memory.ToArray())), ',');
        Assert.IsTrue(reader.TryRead(out string[] header));
        Assert.AreEqual("id,orderNo,customer.name,customer.level,total,createdAt,items,note", string.Join(",", header));
        Assert.IsTrue(reader.TryRead(out string[] cells));
        string local = created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        CollectionAssert.AreEqual(new[] { "66f5c2a1b04e1c3a5d7e9f01", "SO2609-10400", "陈立", "VIP", "1299.50", local }, cells[..6]);
        BsonArray items = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(cells[6]);
        Assert.AreEqual(doc["items"], items, "arrays are written as JSON strings that parse back to the same value");
        Assert.AreEqual("", cells[7], "null is left empty by default");
    }

    [TestMethod]
    public void Json_nested_mode_keeps_objects_as_one_column()
    {
        var doc = new BsonDocument { { "a", 1 }, { "b", new BsonDocument("c", "x") } };
        IReadOnlyList<XferField> fields = SchemaSampler.Analyze([doc], flatten: false);
        CollectionAssert.AreEqual(new[] { "a", "b" }, fields.Select(static f => f.Path).ToArray());
        Assert.AreEqual(CellConversion.Json, fields[1].DefaultConversion);
        Assert.AreEqual(doc["b"], BsonDocument.Parse(CellFormatter.Format(doc["b"], CellConversion.Json, nullAsEmpty: true)));
    }

    [TestMethod]
    public void Cell_formatter_handles_nulls_missing_and_numbers()
    {
        Assert.AreEqual("", CellFormatter.Format(null, CellConversion.None, nullAsEmpty: false), "missing is always empty");
        Assert.AreEqual("", CellFormatter.Format(BsonNull.Value, CellConversion.None, nullAsEmpty: true));
        Assert.AreEqual("null", CellFormatter.Format(BsonNull.Value, CellConversion.None, nullAsEmpty: false));
        Assert.AreEqual("1234567", CellFormatter.Format(new BsonInt32(1234567), CellConversion.None, true), "no thousands separators");
        Assert.AreEqual("86.50", CellFormatter.Format(new BsonDecimal128(Decimal128.Parse("86.5")), CellConversion.Fixed2, true));
        Assert.AreEqual("86.5", CellFormatter.Format(new BsonDecimal128(Decimal128.Parse("86.5")), CellConversion.None, true));
        Assert.AreEqual("0.13", CellFormatter.Format(new BsonDouble(0.125), CellConversion.Fixed2, true), "midpoints round away from zero");
        Assert.AreEqual("2026-09-27T07:00:00Z", CellFormatter.Format(new BsonDateTime(new DateTime(2026, 9, 27, 7, 0, 0, DateTimeKind.Utc)), CellConversion.IsoTime, true));
        Assert.AreEqual("true", CellFormatter.Format(BsonBoolean.True, CellConversion.None, true));
    }

    [TestMethod]
    public void Utf8_bom_and_gbk_encodings_are_written_and_detected()
    {
        byte[] bom = WriteCsv(TextEncodingKind.Utf8Bom, "名称");
        Assert.AreEqual(0xEF, bom[0]);
        Assert.AreEqual(0xBB, bom[1]);
        Assert.AreEqual(0xBF, bom[2]);
        Assert.AreEqual(TextEncodingKind.Utf8Bom, TextEncodings.Detect(bom, complete: true));

        byte[] gbk = WriteCsv(TextEncodingKind.Gbk, "陈立,普通");
        Assert.AreEqual(TextEncodingKind.Gbk, TextEncodings.Detect(gbk, complete: true));
        Assert.AreEqual("陈立,普通\r\n", TextEncodings.Gbk.GetString(gbk));

        byte[] plain = WriteCsv(TextEncodingKind.Utf8, "陈立");
        Assert.AreEqual(TextEncodingKind.Utf8, TextEncodings.Detect(plain, complete: true));
        // 截断在一个多字节字符中间的开头片段,不该被误判成 GBK。
        Assert.AreEqual(TextEncodingKind.Utf8, TextEncodings.Detect(plain.AsSpan(0, 2), complete: false));
    }

    private static byte[] WriteCsv(TextEncodingKind encoding, string line)
    {
        using var memory = new MemoryStream();
        using (var writer = new StreamWriter(memory, TextEncodings.Get(encoding)))
        {
            writer.Write(line);
            writer.Write("\r\n");
        }
        return memory.ToArray();
    }
}
