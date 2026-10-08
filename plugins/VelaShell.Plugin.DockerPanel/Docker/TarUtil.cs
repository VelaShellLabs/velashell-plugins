using System.Text;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 只够用的 tar 读写。
/// <para>
/// <c>/containers/{id}/archive</c> 收发的是 tar 流,而 .NET 自带的
/// <c>System.Formats.Tar</c> 完全能用 —— 这里仍然手写,是因为面板只做**单文件**的
/// 读与写,手写这一小段换来的是零额外依赖,以及对 Docker 那几个怪癖
/// (目录项以 <c>/</c> 结尾、长度补齐到 512、两个全零块收尾)的完全掌控。
/// </para>
/// </summary>
internal static class TarUtil
{
    private const int BlockSize = 512;

    /// <summary>把一个文件打成一个最小的 tar(ustar 格式)。</summary>
    /// <param name="name">tar 内的文件名(不带路径分隔前缀)。</param>
    /// <param name="content">文件内容。</param>
    /// <param name="mode">权限位,默认 0644。</param>
    /// <param name="modified">修改时间;默认取当前时间。</param>
    public static byte[] CreateSingleFile(string name, ReadOnlySpan<byte> content, int mode = 0b110_100_100,
        DateTimeOffset? modified = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        byte[] header = new byte[BlockSize];
        WriteString(header, 0, 100, name);
        WriteOctal(header, 100, 8, (ulong)mode);
        WriteOctal(header, 108, 8, 0);                       // uid
        WriteOctal(header, 116, 8, 0);                       // gid
        WriteOctal(header, 124, 12, (ulong)content.Length);  // size
        WriteOctal(header, 136, 12, (ulong)(modified ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        header[156] = (byte)'0';                             // typeflag: 普通文件
        WriteString(header, 257, 6, "ustar");
        header[263] = (byte)'0';
        header[264] = (byte)'0';
        // 校验和的算法要求先把校验和字段当成 8 个空格再求和 —— 顺序反了 tar 会说文件损坏。
        for (int i = 148; i < 156; i++)
        {
            header[i] = (byte)' ';
        }
        uint checksum = 0;
        foreach (byte b in header)
        {
            checksum += b;
        }
        WriteOctal(header, 148, 7, checksum);
        header[155] = (byte)' ';

        int padded = (content.Length + BlockSize - 1) / BlockSize * BlockSize;
        byte[] result = new byte[BlockSize + padded + BlockSize * 2];
        header.CopyTo(result, 0);
        content.CopyTo(result.AsSpan(BlockSize));
        return result;
    }

    /// <summary>
    /// 从一条 tar 流里取出第一个普通文件的内容。
    /// 找不到普通文件返回 <see langword="null" />(比如路径指的是目录)。
    /// </summary>
    public static async Task<(string Name, byte[] Content)?> ReadFirstFileAsync(Stream tar, long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[BlockSize];
        while (true)
        {
            if (!await ReadExactlyAsync(tar, header, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            // 两个全零块 = 归档结束。
            if (header.All(b => b == 0))
            {
                return null;
            }
            string name = ReadString(header, 0, 100);
            long size = (long)ReadOctal(header, 124, 12);
            char type = (char)header[156];
            int padded = (int)((size + BlockSize - 1) / BlockSize * BlockSize);
            if (type is '0' or '\0')
            {
                if (size > maxBytes)
                {
                    throw new InvalidOperationException(
                        $"文件 {name} 有 {size:N0} 字节,超过了面板允许在线编辑的上限 {maxBytes:N0} 字节。");
                }
                byte[] content = new byte[size];
                if (!await ReadExactlyAsync(tar, content, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
                await SkipAsync(tar, padded - size, cancellationToken).ConfigureAwait(false);
                return (name, content);
            }
            await SkipAsync(tar, padded, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把 tar 流里第一个普通文件<b>边读边写</b>到目标流,返回写出的字节数;
    /// 没有普通文件返回 <c>0</c>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ReadFirstFileAsync" /> 的分工:那一个是给在线编辑用的,要整份内容
    /// 在手上,所以有 2 MB 上限;这一个是给"另存为"用的,只是把字节搬过去 ——
    /// 给它同样的上限就等于宣布几百兆的日志不许下载,而那是这个功能最常见的用途。
    /// </remarks>
    public static async Task<long> ExtractFirstFileAsync(Stream tar, Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[BlockSize];
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            if (!await ReadExactlyAsync(tar, header, cancellationToken).ConfigureAwait(false)
                || header.All(b => b == 0))
            {
                return 0;
            }
            long size = (long)ReadOctal(header, 124, 12);
            char type = (char)header[156];
            long padded = (size + BlockSize - 1) / BlockSize * BlockSize;
            if (type is not ('0' or '\0'))
            {
                await SkipAsync(tar, padded, cancellationToken).ConfigureAwait(false);
                continue;
            }
            long remaining = size;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await tar.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    // 流在中途断了。已经写出去的那部分是残缺的,必须说出来 ——
                    // 一个静默截断的文件比一个下载失败的提示危险得多。
                    throw new EndOfStreamException(
                        $"tar 流在读到 {size - remaining:N0} / {size:N0} 字节时结束了,文件不完整。");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
            await SkipAsync(tar, padded - size, cancellationToken).ConfigureAwait(false);
            return size;
        }
    }

    private static void WriteString(byte[] buffer, int offset, int length, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int n = Math.Min(bytes.Length, length - 1);
        Array.Copy(bytes, 0, buffer, offset, n);
    }

    private static void WriteOctal(byte[] buffer, int offset, int length, ulong value)
    {
        string text = Convert.ToString((long)value, 8).PadLeft(length - 1, '0');
        WriteString(buffer, offset, length, text);
    }

    private static string ReadString(byte[] buffer, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && buffer[end] != 0)
        {
            end++;
        }
        return Encoding.UTF8.GetString(buffer, offset, end - offset);
    }

    private static ulong ReadOctal(byte[] buffer, int offset, int length)
    {
        ulong value = 0;
        for (int i = offset; i < offset + length; i++)
        {
            byte b = buffer[i];
            if (b is 0 or (byte)' ')
            {
                continue;
            }
            if (b is < (byte)'0' or > (byte)'7')
            {
                break;
            }
            value = value * 8 + (ulong)(b - '0');
        }
        return value;
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        byte[] scratch = new byte[Math.Min(count, 64 * 1024) is var n && n > 0 ? n : 1];
        long left = count;
        while (left > 0)
        {
            int read = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(left, scratch.Length)), cancellationToken)
                                   .ConfigureAwait(false);
            if (read <= 0)
            {
                return;
            }
            left -= read;
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }
            read += n;
        }
        return true;
    }
}
