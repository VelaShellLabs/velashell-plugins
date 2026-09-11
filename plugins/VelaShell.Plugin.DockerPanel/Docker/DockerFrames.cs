using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>一段输出来自哪条流。</summary>
public enum DockerStreamKind
{
    /// <summary>标准输入回显(attach 时才可能出现)。</summary>
    StdIn = 0,

    /// <summary>标准输出。</summary>
    StdOut = 1,

    /// <summary>标准错误。</summary>
    StdErr = 2
}

/// <summary>解出来的一行日志。</summary>
/// <param name="Kind">来自哪条流。</param>
/// <param name="Text">一行文本(不含行尾换行)。</param>
/// <param name="Timestamp">带 <c>timestamps=1</c> 时解出的时间戳,否则 <see langword="null" />。</param>
public readonly record struct DockerLogLine(DockerStreamKind Kind, string Text, DateTimeOffset? Timestamp);

/// <summary>
/// Docker 输出流的解帧器。
/// <para>
/// 容器**没有** TTY 时,daemon 把 stdout/stderr 复用在一条连接上,每块前面加一个
/// 8 字节头:<c>[类型][0][0][0][长度(大端 4 字节)]</c>。有 TTY 时没有头,就是裸字节。
/// 这就是这条通道必须是二进制的原因之一 —— 长度字段里出现 <c>0x0A</c> 是常态,
/// 按行切分会把一帧劈成两半。
/// </para>
/// <para>
/// 解出来的字节再按 UTF-8 解码并切行。跨帧的半个字符与半行都被缓存住,
/// 不会吐出替换字符或半行 —— 半行开头的日志比少几行更难读。
/// </para>
/// </summary>
public sealed class DockerFrameDecoder(bool tty, bool timestamps)
{
    private readonly byte[] _header = new byte[8];
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private char[] _chars = new char[4096];

    /// <summary>
    /// 从流里持续解出日志行,直到流结束或取消。
    /// </summary>
    /// <param name="stream">已经定位到帧起点的响应体流。</param>
    /// <param name="onLine">逐行回调(在读流的线程上同步调用,应快速返回)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ReadAsync(Stream stream, Action<DockerLogLine> onLine, CancellationToken cancellationToken)
    {
        var payload = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            DockerStreamKind kind;
            int length;
            if (tty)
            {
                // 有 TTY:没有帧头,读到多少算多少,全部归 stdout
                // ——(TTY 本来就把 stderr 并进了同一条流,这不是我们丢的)。
                kind = DockerStreamKind.StdOut;
                length = await stream.ReadAsync(payload, cancellationToken).ConfigureAwait(false);
                if (length <= 0)
                {
                    break;
                }
                Emit(kind, payload.AsSpan(0, length), onLine);
                continue;
            }
            if (!await ReadExactlyAsync(stream, _header, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
            kind = (DockerStreamKind)_header[0];
            length = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(4, 4));
            if (length < 0)
            {
                break;
            }
            if (length > payload.Length)
            {
                payload = new byte[length];
            }
            if (!await ReadExactlyAsync(stream, payload.AsMemory(0, length), cancellationToken).ConfigureAwait(false))
            {
                break;
            }
            Emit(kind, payload.AsSpan(0, length), onLine);
        }
        // 收尾:流结束时缓冲里可能还剩最后一行(没有行尾换行的那一行)。
        FlushRemainder(DockerStreamKind.StdOut, onLine);
        FlushRemainder(DockerStreamKind.StdErr, onLine);
    }

    private void Emit(DockerStreamKind kind, ReadOnlySpan<byte> bytes, Action<DockerLogLine> onLine)
    {
        var needed = _decoder.GetCharCount(bytes, flush: false);
        if (needed > _chars.Length)
        {
            _chars = new char[needed];
        }
        var produced = _decoder.GetChars(bytes, _chars, flush: false);
        var buffer = kind == DockerStreamKind.StdErr ? _stderr : _stdout;
        for (var i = 0; i < produced; i++)
        {
            var c = _chars[i];
            if (c == '\n')
            {
                PublishLine(kind, buffer, onLine);
            }
            else if (c != '\r')
            {
                buffer.Append(c);
            }
        }
    }

    private void FlushRemainder(DockerStreamKind kind, Action<DockerLogLine> onLine)
    {
        var buffer = kind == DockerStreamKind.StdErr ? _stderr : _stdout;
        if (buffer.Length > 0)
        {
            PublishLine(kind, buffer, onLine);
        }
    }

    private void PublishLine(DockerStreamKind kind, StringBuilder buffer, Action<DockerLogLine> onLine)
    {
        var text = buffer.ToString();
        buffer.Clear();
        DateTimeOffset? stamp = null;
        if (timestamps && TrySplitTimestamp(text, out var parsed, out var rest))
        {
            stamp = parsed;
            text = rest;
        }
        onLine(new(kind, text, stamp));
    }

    /// <summary>
    /// 带 <c>timestamps=1</c> 时,daemon 会在每行前面拼一个 RFC3339 时间戳加一个空格。
    /// 把它拆出来交给界面,而不是让时间戳混在正文里被搜索命中。
    /// </summary>
    internal static bool TrySplitTimestamp(string line, out DateTimeOffset timestamp, out string rest)
    {
        timestamp = default;
        rest = line;
        var space = line.IndexOf(' ');
        // RFC3339 至少是 "2026-08-21T09:41:22Z" 这么长。
        if (space is < 20 or > 40)
        {
            return false;
        }
        if (!DateTimeOffset.TryParse(line.AsSpan(0, space), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out timestamp))
        {
            return false;
        }
        rest = line[(space + 1)..];
        return true;
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }
}
