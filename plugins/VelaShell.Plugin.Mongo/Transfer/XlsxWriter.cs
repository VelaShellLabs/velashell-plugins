using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>一个 Excel 单元格。</summary>
internal readonly record struct XlsxCell
{
    /// <summary>文本(内联字符串);数值单元格为 <see langword="null" />。</summary>
    public string? Text { get; init; }

    /// <summary>数值。</summary>
    public double? Number { get; init; }

    /// <summary>布尔。</summary>
    public bool? Flag { get; init; }

    /// <summary>数值用两位小数格式显示。</summary>
    public bool Fixed2 { get; init; }

    /// <summary>文本单元格。</summary>
    public static XlsxCell OfText(string? text) => new() { Text = text ?? "" };

    /// <summary>数值单元格。</summary>
    public static XlsxCell OfNumber(double number, bool fixed2 = false) => new() { Number = number, Fixed2 = fixed2 };

    /// <summary>布尔单元格。</summary>
    public static XlsxCell OfFlag(bool flag) => new() { Flag = flag };
}

/// <summary>
/// 最小可用的 xlsx 写出器:用 <see cref="ZipArchive" /> 手写 OpenXML 的五个部件
/// (<c>[Content_Types].xml</c>、<c>_rels/.rels</c>、<c>xl/workbook.xml</c> + 关系、<c>xl/styles.xml</c>、各工作表)。
/// <para>
/// 为什么不引 ClosedXML / EPPlus:前者把整张表建在内存里(百万行导出直接吃光内存),
/// 后者有商业许可;而导出要的只是"数字是数字、表头加粗、首行冻结"。
/// 这里**逐行流式**写工作表、字符串一律内联(<c>inlineStr</c>)—— 不需要共享字符串表,
/// 也就不必把所有字符串攒到最后。
/// </para>
/// <para>
/// 一张表超过 Excel 的 1,048,576 行上限时,由调用方换一张新表(<see cref="MaxRows" />)。
/// </para>
/// </summary>
internal sealed class XlsxWriter : IDisposable
{
    /// <summary>Excel 单表最大行数。</summary>
    public const int MaxRows = 1_048_576;

    /// <summary>单元格文本上限(Excel 规格)。</summary>
    private const int MaxCellText = 32_767;

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private readonly ZipArchive _zip;
    private readonly List<string> _sheets = [];
    private readonly bool _freezeHeader;
    private Stream? _sheetStream;
    private XmlWriter? _sheet;
    private bool _completed;

    /// <summary>构造。</summary>
    /// <param name="output">输出流(会被接管并在 <see cref="Dispose" /> 时关闭)。</param>
    /// <param name="freezeHeader">冻结首行。</param>
    public XlsxWriter(Stream output, bool freezeHeader)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
        _freezeHeader = freezeHeader;
    }

    /// <summary>当前表已写的行数。</summary>
    public int RowsInSheet { get; private set; }

    /// <summary>已建的表数。</summary>
    public int SheetCount => _sheets.Count;

    /// <summary>开一张新表(上一张自动收尾)。</summary>
    /// <param name="name">表名(自动清洗非法字符并去重)。</param>
    /// <param name="columnWidths">各列宽度(字符数);可空。</param>
    public void BeginSheet(string name, IReadOnlyList<double>? columnWidths = null)
    {
        EndSheet();
        string sheetName = UniqueSheetName(SanitizeSheetName(name));
        _sheets.Add(sheetName);
        ZipArchiveEntry entry = _zip.CreateEntry($"xl/worksheets/sheet{_sheets.Count}.xml", CompressionLevel.Fastest);
        _sheetStream = entry.Open();
        _sheet = XmlWriter.Create(_sheetStream, XmlSettings());
        _sheet.WriteStartDocument(standalone: true);
        _sheet.WriteStartElement("worksheet", MainNs);
        _sheet.WriteAttributeString("xmlns", "r", null, RelNs);
        if (_freezeHeader)
        {
            _sheet.WriteStartElement("sheetViews", MainNs);
            _sheet.WriteStartElement("sheetView", MainNs);
            _sheet.WriteAttributeString("workbookViewId", "0");
            _sheet.WriteStartElement("pane", MainNs);
            _sheet.WriteAttributeString("ySplit", "1");
            _sheet.WriteAttributeString("topLeftCell", "A2");
            _sheet.WriteAttributeString("activePane", "bottomLeft");
            _sheet.WriteAttributeString("state", "frozen");
            _sheet.WriteEndElement();
            _sheet.WriteStartElement("selection", MainNs);
            _sheet.WriteAttributeString("pane", "bottomLeft");
            _sheet.WriteEndElement();
            _sheet.WriteEndElement();
            _sheet.WriteEndElement();
        }
        _sheet.WriteStartElement("sheetFormatPr", MainNs);
        _sheet.WriteAttributeString("defaultRowHeight", "15");
        _sheet.WriteEndElement();
        if (columnWidths is { Count: > 0 })
        {
            _sheet.WriteStartElement("cols", MainNs);
            for (int i = 0; i < columnWidths.Count; i++)
            {
                string index = (i + 1).ToString(CultureInfo.InvariantCulture);
                _sheet.WriteStartElement("col", MainNs);
                _sheet.WriteAttributeString("min", index);
                _sheet.WriteAttributeString("max", index);
                _sheet.WriteAttributeString("width", Math.Clamp(columnWidths[i], 6, 80).ToString("0.##", CultureInfo.InvariantCulture));
                _sheet.WriteAttributeString("customWidth", "1");
                _sheet.WriteEndElement();
            }
            _sheet.WriteEndElement();
        }
        _sheet.WriteStartElement("sheetData", MainNs);
        RowsInSheet = 0;
    }

    /// <summary>写一行。</summary>
    /// <param name="cells">单元格。</param>
    /// <param name="header">表头行(加粗)。</param>
    public void WriteRow(IReadOnlyList<XlsxCell> cells, bool header = false)
    {
        if (_sheet is null)
        {
            throw new InvalidOperationException("BeginSheet first.");
        }
        if (RowsInSheet >= MaxRows)
        {
            throw new InvalidOperationException("The sheet is full.");
        }
        RowsInSheet++;
        string rowRef = RowsInSheet.ToString(CultureInfo.InvariantCulture);
        _sheet.WriteStartElement("row", MainNs);
        _sheet.WriteAttributeString("r", rowRef);
        for (int i = 0; i < cells.Count; i++)
        {
            XlsxCell cell = cells[i];
            _sheet.WriteStartElement("c", MainNs);
            _sheet.WriteAttributeString("r", ColumnName(i) + rowRef);
            if (cell.Number is { } number)
            {
                if (cell.Fixed2)
                {
                    _sheet.WriteAttributeString("s", "2");
                }
                _sheet.WriteElementString("v", MainNs, number.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (cell.Flag is { } flag)
            {
                _sheet.WriteAttributeString("t", "b");
                _sheet.WriteElementString("v", MainNs, flag ? "1" : "0");
            }
            else
            {
                _sheet.WriteAttributeString("t", "inlineStr");
                if (header)
                {
                    _sheet.WriteAttributeString("s", "1");
                }
                string text = Clean(cell.Text ?? "");
                _sheet.WriteStartElement("is", MainNs);
                _sheet.WriteStartElement("t", MainNs);
                if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains('\n')))
                {
                    _sheet.WriteAttributeString("xml", "space", null, "preserve");
                }
                _sheet.WriteString(text);
                _sheet.WriteEndElement();
                _sheet.WriteEndElement();
            }
            _sheet.WriteEndElement();
        }
        _sheet.WriteEndElement();
    }

    /// <summary>收尾当前表。</summary>
    public void EndSheet()
    {
        if (_sheet is null)
        {
            return;
        }
        _sheet.WriteEndElement(); // sheetData
        _sheet.WriteEndElement(); // worksheet
        _sheet.WriteEndDocument();
        _sheet.Dispose();
        _sheetStream!.Dispose();
        _sheet = null;
        _sheetStream = null;
    }

    /// <summary>写出工作簿部件并关闭压缩包。</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }
        if (_sheets.Count == 0)
        {
            BeginSheet("Sheet1");
        }
        EndSheet();
        WritePart("[Content_Types].xml", w =>
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            w.WriteStartElement("Types", ns);
            Default(w, ns, "rels", "application/vnd.openxmlformats-package.relationships+xml");
            Default(w, ns, "xml", "application/xml");
            Override(w, ns, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
            for (int i = 1; i <= _sheets.Count; i++)
            {
                Override(w, ns, $"/xl/worksheets/sheet{i}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
            }
            Override(w, ns, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
            w.WriteEndElement();
        });
        WritePart("_rels/.rels", w =>
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
            w.WriteStartElement("Relationships", ns);
            Relationship(w, ns, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
            w.WriteEndElement();
        });
        WritePart("xl/workbook.xml", w =>
        {
            w.WriteStartElement("workbook", MainNs);
            w.WriteAttributeString("xmlns", "r", null, RelNs);
            w.WriteStartElement("sheets", MainNs);
            for (int i = 0; i < _sheets.Count; i++)
            {
                w.WriteStartElement("sheet", MainNs);
                w.WriteAttributeString("name", _sheets[i]);
                w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("id", RelNs, $"rId{i + 1}");
                w.WriteEndElement();
            }
            w.WriteEndElement();
            w.WriteEndElement();
        });
        WritePart("xl/_rels/workbook.xml.rels", w =>
        {
            const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
            w.WriteStartElement("Relationships", ns);
            for (int i = 1; i <= _sheets.Count; i++)
            {
                Relationship(w, ns, $"rId{i}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i}.xml");
            }
            Relationship(w, ns, $"rId{_sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
            w.WriteEndElement();
        });
        WritePart("xl/styles.xml", WriteStyles);
        _completed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sheet?.Dispose();
        _sheetStream?.Dispose();
        _zip.Dispose();
    }

    /// <summary>列号 → 列名(0 → A,26 → AA)。</summary>
    public static string ColumnName(int index)
    {
        var name = new StringBuilder();
        int n = index + 1;
        while (n > 0)
        {
            int rem = (n - 1) % 26;
            _ = name.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return name.ToString();
    }

    /// <summary>工作表名:去掉 <c>[]:*?/\</c>,最长 31 个字符,不能为空。</summary>
    public static string SanitizeSheetName(string name)
    {
        char[] chars = name.Where(static c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\') && !char.IsControl(c)).ToArray();
        string clean = new string(chars).Trim('\'').Trim();
        if (clean.Length == 0)
        {
            clean = "Sheet1";
        }
        return clean.Length > 31 ? clean[..31] : clean;
    }

    private string UniqueSheetName(string name)
    {
        if (!_sheets.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return name;
        }
        for (int i = 2; ; i++)
        {
            string suffix = $" ({i})";
            string candidate = (name.Length + suffix.Length > 31 ? name[..(31 - suffix.Length)] : name) + suffix;
            if (!_sheets.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }
    }

    /// <summary>XML 1.0 不允许的控制字符去掉(BSON 字符串里什么都可能有),超长截断。</summary>
    private static string Clean(string text)
    {
        if (text.Length > MaxCellText)
        {
            text = text[..MaxCellText];
        }
        bool dirty = false;
        foreach (char c in text)
        {
            if (!IsXmlChar(c))
            {
                dirty = true;
                break;
            }
        }
        if (!dirty)
        {
            return text;
        }
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                _ = builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (IsXmlChar(c) && !char.IsSurrogate(c))
            {
                _ = builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private static bool IsXmlChar(char c) =>
        c is '\t' or '\n' or '\r' or >= (char)0x20 and <= (char)0xFFFD and not '￾' and not '￿';

    private static XmlWriterSettings XmlSettings() => new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = false,
        CloseOutput = false
    };

    private void WritePart(string path, Action<XmlWriter> body)
    {
        ZipArchiveEntry entry = _zip.CreateEntry(path, CompressionLevel.Fastest);
        using Stream stream = entry.Open();
        using var writer = XmlWriter.Create(stream, XmlSettings());
        writer.WriteStartDocument(standalone: true);
        body(writer);
        writer.WriteEndDocument();
    }

    private static void Default(XmlWriter w, string ns, string extension, string type)
    {
        w.WriteStartElement("Default", ns);
        w.WriteAttributeString("Extension", extension);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Override(XmlWriter w, string ns, string part, string type)
    {
        w.WriteStartElement("Override", ns);
        w.WriteAttributeString("PartName", part);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Relationship(XmlWriter w, string ns, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", ns);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    /// <summary>样式:0 = 默认,1 = 加粗(表头),2 = 两位小数(内置格式 2 = <c>0.00</c>)。</summary>
    private static void WriteStyles(XmlWriter w)
    {
        w.WriteStartElement("styleSheet", MainNs);

        w.WriteStartElement("fonts", MainNs);
        w.WriteAttributeString("count", "2");
        Font(w, bold: false);
        Font(w, bold: true);
        w.WriteEndElement();

        w.WriteStartElement("fills", MainNs);
        w.WriteAttributeString("count", "2");
        foreach (string pattern in new[] { "none", "gray125" })
        {
            w.WriteStartElement("fill", MainNs);
            w.WriteStartElement("patternFill", MainNs);
            w.WriteAttributeString("patternType", pattern);
            w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();

        w.WriteStartElement("borders", MainNs);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("border", MainNs);
        foreach (string side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            w.WriteElementString(side, MainNs, "");
        }
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteStartElement("cellStyleXfs", MainNs);
        w.WriteAttributeString("count", "1");
        Xf(w, numFmt: 0, font: 0, withXfId: false);
        w.WriteEndElement();

        w.WriteStartElement("cellXfs", MainNs);
        w.WriteAttributeString("count", "3");
        Xf(w, numFmt: 0, font: 0, withXfId: true);
        Xf(w, numFmt: 0, font: 1, withXfId: true);
        Xf(w, numFmt: 2, font: 0, withXfId: true);
        w.WriteEndElement();

        w.WriteStartElement("cellStyles", MainNs);
        w.WriteAttributeString("count", "1");
        w.WriteStartElement("cellStyle", MainNs);
        w.WriteAttributeString("name", "Normal");
        w.WriteAttributeString("xfId", "0");
        w.WriteAttributeString("builtinId", "0");
        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();
    }

    private static void Font(XmlWriter w, bool bold)
    {
        w.WriteStartElement("font", MainNs);
        if (bold)
        {
            w.WriteElementString("b", MainNs, "");
        }
        w.WriteStartElement("sz", MainNs);
        w.WriteAttributeString("val", "11");
        w.WriteEndElement();
        w.WriteStartElement("name", MainNs);
        w.WriteAttributeString("val", "Calibri");
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Xf(XmlWriter w, int numFmt, int font, bool withXfId)
    {
        w.WriteStartElement("xf", MainNs);
        w.WriteAttributeString("numFmtId", numFmt.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("fontId", font.ToString(CultureInfo.InvariantCulture));
        w.WriteAttributeString("fillId", "0");
        w.WriteAttributeString("borderId", "0");
        if (withXfId)
        {
            w.WriteAttributeString("xfId", "0");
            if (font != 0)
            {
                w.WriteAttributeString("applyFont", "1");
            }
            if (numFmt != 0)
            {
                w.WriteAttributeString("applyNumberFormat", "1");
            }
        }
        w.WriteEndElement();
    }
}
