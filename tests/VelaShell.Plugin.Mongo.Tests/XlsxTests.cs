using System.IO.Compression;
using System.Xml.Linq;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 手写的最小 OpenXML:写出来的 xlsx 能被 <see cref="ZipArchive" /> 读回、每个部件都是合法 XML、
/// 关系与内容类型齐全,数字是数字、表头加粗、首行冻结、非法字符被清掉。
/// </summary>
[TestClass]
public sealed class XlsxTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    [TestMethod]
    public void Workbook_parts_are_valid_xml_and_reference_each_other()
    {
        byte[] bytes = Write(xlsx =>
        {
            xlsx.BeginSheet("orders", [12, 20]);
            xlsx.WriteRow([XlsxCell.OfText("id"), XlsxCell.OfText("金额")], header: true);
            xlsx.WriteRow([XlsxCell.OfText("SO-1"), XlsxCell.OfNumber(1299.5, fixed2: true)]);
            xlsx.BeginSheet("customers");
            xlsx.WriteRow([XlsxCell.OfFlag(true)]);
        });

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string[] names = [.. zip.Entries.Select(static e => e.FullName).Order(StringComparer.Ordinal)];
        CollectionAssert.AreEqual(
            new[] { "[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml", "xl/worksheets/sheet2.xml" },
            names);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream stream = entry.Open();
            XDocument.Load(stream); // 不合法的 XML 在这里就抛
        }

        XDocument workbook = Load(zip, "xl/workbook.xml");
        string[] sheets = [.. workbook.Descendants(Main + "sheet").Select(static s => (string)s.Attribute("name")!)];
        CollectionAssert.AreEqual(new[] { "orders", "customers" }, sheets);
        Assert.AreEqual("rId2", (string)workbook.Descendants(Main + "sheet").Last().Attribute(Rel + "id")!);

        XDocument rels = Load(zip, "xl/_rels/workbook.xml.rels");
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        string[] targets = [.. rels.Descendants(pkg + "Relationship").Select(static r => (string)r.Attribute("Target")!)];
        CollectionAssert.AreEqual(new[] { "worksheets/sheet1.xml", "worksheets/sheet2.xml", "styles.xml" }, targets);

        XDocument types = Load(zip, "[Content_Types].xml");
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        Assert.AreEqual(4, types.Descendants(ct + "Override").Count(), "workbook + 2 sheets + styles");
    }

    [TestMethod]
    public void Sheet_writes_numbers_as_values_text_inline_and_freezes_the_header()
    {
        byte[] bytes = Write(xlsx =>
        {
            xlsx.BeginSheet("orders");
            xlsx.WriteRow([XlsxCell.OfText("total"), XlsxCell.OfText("paid")], header: true);
            xlsx.WriteRow([XlsxCell.OfNumber(86.5, fixed2: true), XlsxCell.OfFlag(false)]);
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        XDocument sheet = Load(zip, "xl/worksheets/sheet1.xml");

        XElement pane = sheet.Descendants(Main + "pane").Single();
        Assert.AreEqual("frozen", (string)pane.Attribute("state")!);
        Assert.AreEqual("A2", (string)pane.Attribute("topLeftCell")!);

        XElement[] cells = [.. sheet.Descendants(Main + "c")];
        Assert.AreEqual("A1", (string)cells[0].Attribute("r")!);
        Assert.AreEqual("inlineStr", (string)cells[0].Attribute("t")!);
        Assert.AreEqual("1", (string)cells[0].Attribute("s")!, "header cells use the bold style");
        Assert.AreEqual("total", cells[0].Descendants(Main + "t").Single().Value);
        Assert.IsNull(cells[2].Attribute("t"), "numbers carry no type attribute");
        Assert.AreEqual("2", (string)cells[2].Attribute("s")!, "two-decimals number format");
        Assert.AreEqual("86.5", cells[2].Element(Main + "v")!.Value);
        Assert.AreEqual("b", (string)cells[3].Attribute("t")!);
        Assert.AreEqual("0", cells[3].Element(Main + "v")!.Value);

        XDocument styles = Load(zip, "xl/styles.xml");
        Assert.AreEqual(3, styles.Descendants(Main + "cellXfs").Single().Elements().Count());
        Assert.IsNotNull(styles.Descendants(Main + "font").ElementAt(1).Element(Main + "b"));
    }

    [TestMethod]
    public void Invalid_xml_characters_are_dropped_and_markup_is_escaped()
    {
        byte[] bytes = Write(xlsx =>
        {
            xlsx.BeginSheet("x");
            xlsx.WriteRow([XlsxCell.OfText("a\u0001b<c>&\"d\" 😀"), XlsxCell.OfText(" lead")]);
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        XDocument sheet = Load(zip, "xl/worksheets/sheet1.xml");
        XElement[] texts = [.. sheet.Descendants(Main + "t")];
        Assert.AreEqual("ab<c>&\"d\" 😀", texts[0].Value);
        Assert.AreEqual(" lead", texts[1].Value);
        Assert.AreEqual("preserve", (string)texts[1].Attribute(XNamespace.Xml + "space")!);
    }

    [TestMethod]
    public void Column_names_and_sheet_names_follow_excel_rules()
    {
        Assert.AreEqual("A", XlsxWriter.ColumnName(0));
        Assert.AreEqual("Z", XlsxWriter.ColumnName(25));
        Assert.AreEqual("AA", XlsxWriter.ColumnName(26));
        Assert.AreEqual("AZ", XlsxWriter.ColumnName(51));
        Assert.AreEqual("BA", XlsxWriter.ColumnName(52));
        Assert.AreEqual("XFD", XlsxWriter.ColumnName(16383));
        Assert.AreEqual("ordersv1", XlsxWriter.SanitizeSheetName("orders[v1]"));
        Assert.AreEqual("Sheet1", XlsxWriter.SanitizeSheetName("/:*?"));
        Assert.AreEqual(31, XlsxWriter.SanitizeSheetName(new string('x', 40)).Length);

        byte[] bytes = Write(xlsx =>
        {
            xlsx.BeginSheet("orders");
            xlsx.BeginSheet("orders");
        });
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string[] sheets = [.. Load(zip, "xl/workbook.xml").Descendants(Main + "sheet").Select(static s => (string)s.Attribute("name")!)];
        CollectionAssert.AreEqual(new[] { "orders", "orders (2)" }, sheets, "duplicate sheet names are made unique");
    }

    [TestMethod]
    public void Excel_export_writer_types_cells_from_bson_values()
    {
        var doc = new BsonDocument
        {
            { "_id", ObjectId.Parse("66f5c2a1b04e1c3a5d7e9f01") },
            { "qty", 3 },
            { "total", new BsonDecimal128(Decimal128.Parse("1299.00")) },
            { "paid", true },
            { "customer", new BsonDocument("name", "陈立") }
        };
        ExportColumn[] columns =
        [
            new("_id", "id", CellConversion.Hex), new("qty", "数量", CellConversion.None),
            new("total", "金额", CellConversion.Fixed2), new("paid", "已付", CellConversion.None),
            new("customer.name", "客户", CellConversion.None)
        ];
        using var memory = new MemoryStream();
        using (var writer = new ExcelExportWriter(memory, new ExcelOptions { SheetName = "订单" }))
        {
            writer.Begin("orders", columns);
            writer.Write(doc);
            writer.End();
            writer.Complete();
        }
        using var zip = new ZipArchive(new MemoryStream(memory.ToArray()), ZipArchiveMode.Read);
        Assert.AreEqual("订单", (string)Load(zip, "xl/workbook.xml").Descendants(Main + "sheet").Single().Attribute("name")!);
        XElement[] row = [.. Load(zip, "xl/worksheets/sheet1.xml").Descendants(Main + "row").ElementAt(1).Elements(Main + "c")];
        Assert.AreEqual("66f5c2a1b04e1c3a5d7e9f01", row[0].Descendants(Main + "t").Single().Value);
        Assert.AreEqual("3", row[1].Element(Main + "v")!.Value);
        Assert.AreEqual("1299", row[2].Element(Main + "v")!.Value);
        Assert.AreEqual("b", (string)row[3].Attribute("t")!);
        Assert.AreEqual("陈立", row[4].Descendants(Main + "t").Single().Value);
    }

    private static byte[] Write(Action<XlsxWriter> body)
    {
        var memory = new MemoryStream();
        using (var xlsx = new XlsxWriter(memory, freezeHeader: true))
        {
            body(xlsx);
            xlsx.Complete();
        }
        return memory.ToArray();
    }

    private static XDocument Load(ZipArchive zip, string name)
    {
        using Stream stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }
}
