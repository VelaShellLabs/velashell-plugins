using System.Text;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// RFC 4180 的 CSV 写出:含分隔符、双引号、回车或换行的字段整个加双引号,字段里的双引号写两遍;
/// 行尾一律 CRLF(RFC 原文如此,Excel 也只认这个才不会把最后一列粘上 <c>\r</c>)。
/// 首尾带空格的字段也加引号 —— 不加的话不少读者会顺手 Trim 掉。
/// </summary>
/// <param name="writer">目标。</param>
/// <param name="delimiter">分隔符。</param>
internal sealed class CsvWriter(TextWriter writer, char delimiter)
{
    /// <summary>写一行。</summary>
    public void WriteRow(IReadOnlyList<string?> cells)
    {
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(delimiter);
            }
            WriteField(cells[i]);
        }
        writer.Write("\r\n");
    }

    private void WriteField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        if (!NeedsQuotes(value, delimiter))
        {
            writer.Write(value);
            return;
        }
        writer.Write('"');
        foreach (char c in value)
        {
            if (c == '"')
            {
                writer.Write("\"\"");
            }
            else
            {
                writer.Write(c);
            }
        }
        writer.Write('"');
    }

    /// <summary>这个字段要不要加引号。</summary>
    internal static bool NeedsQuotes(string value, char delimiter)
    {
        if (value.Length == 0)
        {
            return false;
        }
        if (value[0] == ' ' || value[^1] == ' ' || value[0] == '\t' || value[^1] == '\t')
        {
            return true;
        }
        foreach (char c in value)
        {
            if (c == delimiter || c == '"' || c == '\r' || c == '\n')
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>把一行写成字符串(预览用)。</summary>
    public static string FormatRow(IReadOnlyList<string?> cells, char delimiter)
    {
        using var text = new StringWriter();
        new CsvWriter(text, delimiter).WriteRow(cells);
        return text.ToString().TrimEnd('\r', '\n');
    }
}

/// <summary>
/// 流式 CSV 读者(RFC 4180,对常见的不规范写法宽容):引号字段里可以有分隔符、换行与 <c>""</c>;
/// 行尾认 CRLF / LF / CR;未加引号的字段中间出现的引号按字面字符处理(Excel 导出的脏数据常见)。
/// <para>
/// 不把整个文件读进内存:导入向导对一个 1,246,302 行的文件计数、dry-run、正式导入都是一遍流。
/// </para>
/// </summary>
internal sealed class CsvReader
{
    private readonly TextReader _reader;
    private readonly char _delimiter;
    private readonly char[] _buffer = new char[64 * 1024];
    private readonly StringBuilder _field = new();
    private int _length;
    private int _position;
    private long _line = 1;

    /// <summary>构造。</summary>
    public CsvReader(TextReader reader, char delimiter)
    {
        _reader = reader;
        _delimiter = delimiter;
    }

    /// <summary>上一条记录开始的物理行号(从 1 起,引号字段里的换行也算行)。</summary>
    public long RecordLine { get; private set; }

    /// <summary>读一条记录;到文件尾返回 <see langword="false" />。空行跳过。</summary>
    public bool TryRead(out string[] record)
    {
        var fields = new List<string>();
        while (true)
        {
            if (!ReadRecord(fields, out bool blank))
            {
                record = [];
                return false;
            }
            if (!blank)
            {
                record = [.. fields];
                return true;
            }
            fields.Clear();
        }
    }

    private bool ReadRecord(List<string> fields, out bool blank)
    {
        blank = false;
        if (!Fill())
        {
            return false;
        }
        RecordLine = _line;
        _ = _field.Clear();
        bool quoted = false;
        bool fieldStart = true;
        bool sawQuote = false;
        while (true)
        {
            if (_position >= _length && !Fill())
            {
                // 文件尾:最后一条记录没有换行结尾。
                fields.Add(_field.ToString());
                blank = fields.Count == 1 && fields[0].Length == 0 && !sawQuote;
                return true;
            }
            char c = _buffer[_position++];
            if (quoted)
            {
                if (c == '"')
                {
                    if (Peek() == '"')
                    {
                        _position++;
                        _ = _field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (c == '\n' || (c == '\r' && Peek() != '\n'))
                    {
                        _line++;
                    }
                    _ = _field.Append(c);
                }
                continue;
            }
            if (c == '"' && fieldStart)
            {
                quoted = true;
                sawQuote = true;
                fieldStart = false;
                continue;
            }
            if (c == _delimiter)
            {
                fields.Add(_field.ToString());
                _ = _field.Clear();
                fieldStart = true;
                continue;
            }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && Peek() == '\n')
                {
                    _position++;
                }
                _line++;
                fields.Add(_field.ToString());
                blank = fields.Count == 1 && fields[0].Length == 0 && !sawQuote;
                return true;
            }
            fieldStart = false;
            _ = _field.Append(c);
        }
    }

    private int Peek()
    {
        if (_position >= _length && !Fill())
        {
            return -1;
        }
        return _buffer[_position];
    }

    private bool Fill()
    {
        if (_position < _length)
        {
            return true;
        }
        _length = _reader.Read(_buffer, 0, _buffer.Length);
        _position = 0;
        return _length > 0;
    }

    /// <summary>
    /// 快速数记录(文件头那句「1,246,302 行」):只扫引号与换行,不切字段、不分配字符串。
    /// 返回的是**非空记录数**(含表头)。
    /// </summary>
    public static long CountRecords(TextReader reader, CancellationToken cancellationToken = default)
    {
        char[] buffer = new char[128 * 1024];
        long records = 0;
        bool quoted = false;
        bool content = false;
        char previous = '\0';
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c == '"')
                {
                    quoted = !quoted;
                    content = true;
                }
                else if (!quoted && (c == '\n' || c == '\r'))
                {
                    if (!(c == '\n' && previous == '\r') && content)
                    {
                        records++;
                    }
                    content = false;
                }
                else
                {
                    content = true;
                }
                previous = c;
            }
        }
        if (content)
        {
            records++;
        }
        return records;
    }

    /// <summary>
    /// 认分隔符:对前若干行逐个候选数"引号外的出现次数",挑**每一行都出现且次数一致**的那个;
    /// 都不一致时挑总数最多的。全没有就当逗号(单列文件)。
    /// </summary>
    public static char DetectDelimiter(IReadOnlyList<string> lines)
    {
        char[] candidates = [',', '\t', ';', '|'];
        char best = ',';
        int bestScore = -1;
        foreach (char candidate in candidates)
        {
            int[] counts = [.. lines.Where(static l => l.Length > 0).Take(20).Select(l => CountOutsideQuotes(l, candidate))];
            if (counts.Length == 0 || counts.All(static c => c == 0))
            {
                continue;
            }
            bool consistent = counts.All(c => c == counts[0]) && counts[0] > 0;
            int score = (consistent ? 1_000_000 : 0) + counts.Sum();
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        int count = 0;
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == delimiter && !quoted)
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>分隔符给人看的名字。</summary>
    public static string DelimiterName(char delimiter, Loc loc) => delimiter switch
    {
        ',' => loc["Xfer_DelimComma"],
        ';' => loc["Xfer_DelimSemicolon"],
        '\t' => loc["Xfer_DelimTab"],
        '|' => loc["Xfer_DelimPipe"],
        _ => delimiter.ToString()
    };
}
