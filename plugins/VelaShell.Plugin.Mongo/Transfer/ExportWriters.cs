using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// 一种导出格式的写出器:一个文件、一个(或多个)集合、一份一份文档地写。
/// 整条链路是**流式**的 —— 游标给一份写一份,内存里从不攒整张表。
/// </summary>
internal abstract class ExportWriter : IDisposable
{
    /// <summary>开始一个集合。</summary>
    /// <param name="collection">集合名。</param>
    /// <param name="columns">CSV / Excel 的列(其余格式忽略)。</param>
    public abstract void Begin(string collection, IReadOnlyList<ExportColumn> columns);

    /// <summary>写一份文档。</summary>
    public abstract void Write(BsonDocument document);

    /// <summary>收尾当前集合。</summary>
    public abstract void End();

    /// <summary>收尾整个文件。</summary>
    public virtual void Complete()
    {
    }

    /// <inheritdoc />
    public abstract void Dispose();
}

/// <summary>JSON(行分隔或数组)。</summary>
/// <param name="output">输出流。</param>
/// <param name="options">选项。</param>
internal sealed class JsonExportWriter(Stream output, JsonOptions options) : ExportWriter
{
    private readonly StreamWriter _writer = new(output, new UTF8Encoding(false), 1 << 16);
    private bool _first = true;

    /// <inheritdoc />
    public override void Begin(string collection, IReadOnlyList<ExportColumn> columns)
    {
        _first = true;
        if (!options.Lines)
        {
            _writer.Write('[');
        }
    }

    /// <inheritdoc />
    public override void Write(BsonDocument document)
    {
        string json = BsonText.Compact(document, options.Mode);
        if (options.Lines)
        {
            _writer.Write(json);
            _writer.Write('\n');
            return;
        }
        _writer.Write(_first ? "\n" : ",\n");
        _writer.Write(json);
        _first = false;
    }

    /// <inheritdoc />
    public override void End()
    {
        if (!options.Lines)
        {
            _writer.Write(_first ? "]\n" : "\n]\n");
        }
        _writer.Flush();
    }

    /// <inheritdoc />
    public override void Dispose() => _writer.Dispose();
}

/// <summary>CSV。</summary>
internal sealed class CsvExportWriter : ExportWriter
{
    private readonly StreamWriter _writer;
    private readonly CsvOptions _options;
    private readonly CsvWriter _csv;
    private IReadOnlyList<ExportColumn> _columns = [];
    private string[] _cells = [];

    /// <summary>构造。</summary>
    public CsvExportWriter(Stream output, CsvOptions options)
    {
        _options = options;
        _writer = new StreamWriter(output, TextEncodings.Get(options.Encoding), 1 << 16);
        _csv = new CsvWriter(_writer, options.Delimiter);
    }

    /// <inheritdoc />
    public override void Begin(string collection, IReadOnlyList<ExportColumn> columns)
    {
        _columns = columns;
        _cells = new string[columns.Count];
        if (_options.WriteHeader)
        {
            _csv.WriteRow([.. columns.Select(static c => c.Header)]);
        }
    }

    /// <inheritdoc />
    public override void Write(BsonDocument document)
    {
        for (int i = 0; i < _columns.Count; i++)
        {
            _cells[i] = CellFormatter.Format(BsonPath.Get(document, _columns[i].Path), _columns[i].Conversion, _options.NullAsEmpty);
        }
        _csv.WriteRow(_cells);
    }

    /// <inheritdoc />
    public override void End() => _writer.Flush();

    /// <inheritdoc />
    public override void Dispose() => _writer.Dispose();
}

/// <summary>Excel(每个集合一张表;超过单表行数上限时自动续一张)。</summary>
internal sealed class ExcelExportWriter : ExportWriter
{
    private readonly XlsxWriter _xlsx;
    private readonly ExcelOptions _options;
    private IReadOnlyList<ExportColumn> _columns = [];
    private XlsxCell[] _cells = [];
    private string _sheetName = "";

    /// <summary>构造。</summary>
    public ExcelExportWriter(Stream output, ExcelOptions options)
    {
        _options = options;
        _xlsx = new XlsxWriter(output, options.FreezeHeader && options.WriteHeader);
    }

    /// <inheritdoc />
    public override void Begin(string collection, IReadOnlyList<ExportColumn> columns)
    {
        _columns = columns;
        _cells = new XlsxCell[columns.Count];
        _sheetName = _options.SheetName.Trim().Length > 0 && _xlsx.SheetCount == 0 ? _options.SheetName.Trim() : collection;
        StartSheet();
    }

    private void StartSheet()
    {
        double[] widths = [.. _columns.Select(static c => Math.Max(10, WidthOf(c.Header) + 2))];
        _xlsx.BeginSheet(_sheetName, widths);
        if (_options.WriteHeader)
        {
            _xlsx.WriteRow([.. _columns.Select(static c => XlsxCell.OfText(c.Header))], header: true);
        }
    }

    /// <inheritdoc />
    public override void Write(BsonDocument document)
    {
        if (_xlsx.RowsInSheet >= XlsxWriter.MaxRows)
        {
            StartSheet();
        }
        for (int i = 0; i < _columns.Count; i++)
        {
            ExportColumn column = _columns[i];
            BsonValue? value = BsonPath.Get(document, column.Path);
            _cells[i] = CellFormatter.Number(value, column.Conversion) is { } number
                ? XlsxCell.OfNumber(number, column.Conversion == CellConversion.Fixed2)
                : value is BsonBoolean flag
                    ? XlsxCell.OfFlag(flag.Value)
                    : XlsxCell.OfText(CellFormatter.Format(value, column.Conversion, nullAsEmpty: true));
        }
        _xlsx.WriteRow(_cells);
    }

    /// <inheritdoc />
    public override void End()
    {
    }

    /// <inheritdoc />
    public override void Complete() => _xlsx.Complete();

    /// <inheritdoc />
    public override void Dispose() => _xlsx.Dispose();

    /// <summary>列宽按字符估(中日韩字符算两个)。</summary>
    private static double WidthOf(string text) => text.Sum(static c => c > 0x2E80 ? 2 : 1);
}

/// <summary>
/// mongosh 脚本:<c>db.getSiblingDB("shop").getCollection("orders").insertMany([ … ]);</c>,
/// 每 <see cref="ShellOptions.BatchSize" /> 份一条语句。值用 mongosh 字面量
/// (<c>ObjectId("…")</c>、<c>ISODate("…")</c>、<c>NumberDecimal("…")</c>),粘进 mongosh 原样可跑。
/// </summary>
/// <param name="output">输出流。</param>
/// <param name="database">库名。</param>
/// <param name="options">选项。</param>
internal sealed class ShellExportWriter(Stream output, string database, ShellOptions options) : ExportWriter
{
    private readonly StreamWriter _writer = new(output, new UTF8Encoding(false), 1 << 16);
    private string _target = "";
    private int _inBatch;
    private bool _header;

    /// <inheritdoc />
    public override void Begin(string collection, IReadOnlyList<ExportColumn> columns)
    {
        if (!_header)
        {
            _writer.Write("// VelaShell MongoDB export · ");
            _writer.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture));
            _writer.Write('\n');
            _header = true;
        }
        _target = $"db.getSiblingDB({BsonText.Quote(database)}).getCollection({BsonText.Quote(collection)})";
        _inBatch = 0;
    }

    /// <inheritdoc />
    public override void Write(BsonDocument document)
    {
        if (_inBatch == 0)
        {
            _writer.Write(_target);
            _writer.Write(".insertMany([\n");
        }
        else
        {
            _writer.Write(",\n");
        }
        _writer.Write("  ");
        _writer.Write(BsonText.Literal(document));
        _inBatch++;
        if (_inBatch >= Math.Max(1, options.BatchSize))
        {
            CloseBatch();
        }
    }

    private void CloseBatch()
    {
        if (_inBatch > 0)
        {
            _writer.Write("\n]);\n");
            _inBatch = 0;
        }
    }

    /// <inheritdoc />
    public override void End()
    {
        CloseBatch();
        _writer.Flush();
    }

    /// <inheritdoc />
    public override void Dispose() => _writer.Dispose();
}

/// <summary>BSON 转储里一个集合的数据文件(裸 BSON 首尾相接)。</summary>
/// <param name="output">输出流(gzip 时已经套好压缩)。</param>
internal sealed class BsonExportWriter(Stream output) : ExportWriter
{
    /// <inheritdoc />
    public override void Begin(string collection, IReadOnlyList<ExportColumn> columns)
    {
    }

    /// <inheritdoc />
    public override void Write(BsonDocument document) => BsonDump.Write(output, document);

    /// <inheritdoc />
    public override void End() => output.Flush();

    /// <inheritdoc />
    public override void Dispose() => output.Dispose();
}

/// <summary>给流计字节数(进度里的「已写出 38 KB」)。</summary>
/// <param name="inner">被包的流。</param>
internal sealed class CountingStream(Stream inner) : Stream
{
    private long _written;

    /// <summary>已写字节。</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => Written;

    /// <inheritdoc />
    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref _written, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Interlocked.Add(ref _written, buffer.Length);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
