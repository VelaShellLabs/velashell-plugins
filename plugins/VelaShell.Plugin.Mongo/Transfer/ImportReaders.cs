using System.Text;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>导入文件的格式。</summary>
internal enum ImportFormat
{
    /// <summary>CSV / TSV。</summary>
    Csv,

    /// <summary>JSON:一行一份(JSONL / mongoexport 默认)、一个大数组、或若干文档首尾相接 —— 读者都认。</summary>
    Json,

    /// <summary>裸 BSON(mongodump 的 .bson,可带 .gz)。</summary>
    Bson
}

/// <summary>认出来的导入文件。</summary>
internal sealed record ImportSource
{
    /// <summary>路径。</summary>
    public required string Path { get; init; }

    /// <summary>字节数。</summary>
    public long Size { get; init; }

    /// <summary>格式。</summary>
    public ImportFormat Format { get; init; }

    /// <summary>文本编码(BSON 无意义)。</summary>
    public TextEncodingKind Encoding { get; init; } = TextEncodingKind.Utf8;

    /// <summary>CSV 分隔符。</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>CSV 首行是列名。</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>
    /// 认文件:扩展名优先(.bson / .gz 一定是 BSON;.csv / .tsv 一定是 CSV),
    /// 认不出时看第一个非空字符 —— <c>{</c> / <c>[</c> 是 JSON,其余当 CSV。
    /// </summary>
    public static ImportSource Sniff(string path)
    {
        var file = new FileInfo(path);
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".bson" or ".gz")
        {
            return new() { Path = path, Size = file.Length, Format = ImportFormat.Bson };
        }
        TextEncodingKind encoding = TextEncodings.Detect(path);
        IReadOnlyList<string> head = HeadLines(path, encoding, 20);
        ImportFormat format = ext switch
        {
            ".csv" or ".tsv" => ImportFormat.Csv,
            ".json" or ".jsonl" or ".ndjson" => ImportFormat.Json,
            _ => head.FirstOrDefault(static l => l.Trim().Length > 0)?.TrimStart() is { Length: > 0 } first && first[0] is '{' or '['
                ? ImportFormat.Json
                : ImportFormat.Csv
        };
        char delimiter = ext == ".tsv" ? '\t' : CsvReader.DetectDelimiter(head);
        return new()
        {
            Path = path,
            Size = file.Length,
            Format = format,
            Encoding = encoding,
            Delimiter = delimiter
        };
    }

    /// <summary>读文件开头若干行(第一步的原文预览、分隔符探测)。</summary>
    public static IReadOnlyList<string> HeadLines(string path, TextEncodingKind encoding, int count)
    {
        var lines = new List<string>();
        using FileStream stream = File.OpenRead(path);
        using StreamReader reader = TextEncodings.OpenReader(stream, encoding);
        while (lines.Count < count && reader.ReadLine() is { } line)
        {
            lines.Add(line.Length > 400 ? line[..400] + "…" : line);
        }
        return lines;
    }

    /// <summary>开读者。</summary>
    public IImportReader Open() => Format switch
    {
        ImportFormat.Csv => new CsvImportReader(this),
        ImportFormat.Json => new JsonImportReader(this),
        _ => new BsonImportReader(this)
    };

    /// <summary>流式数记录数(文件头里那句「1,246,302 行」;CSV 不含表头)。</summary>
    public long CountRecords(CancellationToken cancellationToken)
    {
        switch (Format)
        {
            case ImportFormat.Bson:
                return BsonDump.Count(Path, cancellationToken);
            case ImportFormat.Json:
            {
                using FileStream stream = File.OpenRead(Path);
                using StreamReader reader = TextEncodings.OpenReader(stream, Encoding);
                var scanner = new JsonDocumentScanner(reader);
                long count = 0;
                while (scanner.Skip())
                {
                    if ((++count & 0xFFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                return count;
            }
            default:
            {
                using FileStream stream = File.OpenRead(Path);
                using StreamReader reader = TextEncodings.OpenReader(stream, Encoding);
                long records = CsvReader.CountRecords(reader, cancellationToken);
                return HasHeader ? Math.Max(0, records - 1) : records;
            }
        }
    }
}

/// <summary>读出来的一条记录。</summary>
/// <param name="Line">在文件里开始的物理行号(BSON 是第几份文档)。</param>
/// <param name="Cells">CSV 的单元格。</param>
/// <param name="Document">JSON / BSON 的文档。</param>
/// <param name="Error">这一条本身就解析不了(坏 JSON)时的原因。</param>
internal sealed record ImportRecord(long Line, string[]? Cells, BsonDocument? Document, string? Error = null);

/// <summary>导入读者:一条一条地读,不把文件装进内存。</summary>
internal interface IImportReader : IDisposable
{
    /// <summary>CSV 的列名(无表头时是 <c>column1</c>…);JSON / BSON 为空(列由抽样决定)。</summary>
    IReadOnlyList<string> Header { get; }

    /// <summary>读一条;读完返回 <see langword="false" />。</summary>
    bool TryRead(out ImportRecord record);
}

/// <summary>CSV 读者。</summary>
internal sealed class CsvImportReader : IImportReader
{
    private readonly FileStream _stream;
    private readonly StreamReader _text;
    private readonly CsvReader _csv;
    private string[]? _pending;
    private long _pendingLine;

    /// <summary>构造(读掉表头)。</summary>
    public CsvImportReader(ImportSource source)
    {
        _stream = File.OpenRead(source.Path);
        _text = TextEncodings.OpenReader(_stream, source.Encoding);
        _csv = new CsvReader(_text, source.Delimiter);
        if (_csv.TryRead(out string[] first))
        {
            if (source.HasHeader)
            {
                Header = [.. first.Select(static (h, i) => h.Trim().Length > 0 ? h.Trim() : $"column{i + 1}")];
            }
            else
            {
                Header = [.. first.Select(static (_, i) => $"column{i + 1}")];
                _pending = first;
                _pendingLine = _csv.RecordLine;
            }
        }
        else
        {
            Header = [];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Header { get; }

    /// <inheritdoc />
    public bool TryRead(out ImportRecord record)
    {
        if (_pending is { } pending)
        {
            _pending = null;
            record = new(_pendingLine, pending, null);
            return true;
        }
        if (_csv.TryRead(out string[] cells))
        {
            record = new(_csv.RecordLine, cells, null);
            return true;
        }
        record = new(0, null, null);
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _text.Dispose();
        _stream.Dispose();
    }
}

/// <summary>JSON 读者(JSONL / 数组 / 首尾相接的文档流都认;值的写法认 Relaxed / Canonical / mongosh)。</summary>
internal sealed class JsonImportReader : IImportReader
{
    private readonly FileStream _stream;
    private readonly StreamReader _text;
    private readonly JsonDocumentScanner _scanner;

    /// <summary>构造。</summary>
    public JsonImportReader(ImportSource source)
    {
        _stream = File.OpenRead(source.Path);
        _text = TextEncodings.OpenReader(_stream, source.Encoding);
        _scanner = new JsonDocumentScanner(_text);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Header => [];

    /// <inheritdoc />
    public bool TryRead(out ImportRecord record)
    {
        if (!_scanner.Next(out string json, out long line))
        {
            record = new(0, null, null);
            return false;
        }
        try
        {
            record = new(line, null, BsonDocument.Parse(json));
        }
        catch (Exception ex) when (ex is FormatException or BsonSerializationException or ArgumentException or InvalidOperationException)
        {
            record = new(line, null, null, ex.Message);
        }
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _text.Dispose();
        _stream.Dispose();
    }
}

/// <summary>BSON 读者。</summary>
/// <param name="source">来源。</param>
internal sealed class BsonImportReader(ImportSource source) : IImportReader
{
    private readonly Stream _stream = BsonDump.OpenRead(source.Path);
    private long _index;

    /// <inheritdoc />
    public IReadOnlyList<string> Header => [];

    /// <inheritdoc />
    public bool TryRead(out ImportRecord record)
    {
        BsonDocument? document = BsonDump.Read(_stream);
        if (document is null)
        {
            record = new(0, null, null);
            return false;
        }
        record = new(++_index, null, document);
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// 从字符流里切出一份一份的**顶层 JSON 文档**:不管它们是一行一份、包在一个大数组里,
/// 还是美化过、跨了很多行首尾相接。只认括号深度与字符串,不解析值 ——
/// 解析交给驱动的 <see cref="BsonDocument.Parse(string)" />(它认 mongosh 的 <c>ObjectId("…")</c> 那些写法)。
/// 这样一个几百 MB 的数组文件也只需要一份文档大小的内存。
/// </summary>
internal sealed class JsonDocumentScanner(TextReader reader)
{
    private readonly char[] _buffer = new char[64 * 1024];
    private readonly StringBuilder _current = new();
    private int _length;
    private int _position;
    private long _line = 1;

    /// <summary>取下一份文档的原文。</summary>
    public bool Next(out string json, out long line) => Scan(capture: true, out json, out line);

    /// <summary>跳过下一份文档(计数用)。</summary>
    public bool Skip() => Scan(capture: false, out _, out _);

    private bool Scan(bool capture, out string json, out long line)
    {
        json = "";
        line = 0;
        _current.Clear();
        int depth = 0;
        bool inString = false;
        bool escape = false;
        char quote = '"';
        while (true)
        {
            if (_position >= _length)
            {
                _length = reader.Read(_buffer, 0, _buffer.Length);
                _position = 0;
                if (_length <= 0)
                {
                    return false;
                }
            }
            char c = _buffer[_position++];
            if (c == '\n')
            {
                _line++;
            }
            if (depth == 0)
            {
                // 文档之间:跳过空白、数组的 [ ] 与逗号、行注释之外的一切杂质。
                if (c == '{')
                {
                    depth = 1;
                    line = _line;
                    if (capture)
                    {
                        _current.Append(c);
                    }
                }
                continue;
            }
            if (capture)
            {
                _current.Append(c);
            }
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == quote)
                {
                    inString = false;
                }
                continue;
            }
            switch (c)
            {
                case '"' or '\'':
                    inString = true;
                    quote = c;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                    {
                        json = capture ? _current.ToString() : "";
                        return true;
                    }
                    break;
            }
        }
    }
}
