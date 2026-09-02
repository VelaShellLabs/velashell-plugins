using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace VelaShell.Plugin.Redis;

/// <summary>值的解压环节。</summary>
public enum RedisCompression
{
    /// <summary>不解压。</summary>
    None,

    /// <summary>GZip(魔数 <c>1f 8b</c>)。</summary>
    GZip,

    /// <summary>Deflate / zlib(zlib 头 <c>78 xx</c>,也接受裸 deflate)。</summary>
    Deflate,

    /// <summary>Brotli(无魔数,只能显式选)。</summary>
    Brotli,

    /// <summary>Zstandard(魔数 <c>28 b5 2f fd</c>)。<b>未内置</b>:需要额外的第三方依赖。</summary>
    Zstd,

    /// <summary>LZ4 帧(魔数 <c>04 22 4d 18</c>)。<b>未内置</b>。</summary>
    Lz4,

    /// <summary>Snappy 帧。<b>未内置</b>。</summary>
    Snappy
}

/// <summary>值的反序列化环节。</summary>
public enum RedisSerialization
{
    /// <summary>不反序列化(字节就是文本 / 二进制本身)。</summary>
    None,

    /// <summary>MessagePack。</summary>
    MsgPack,

    /// <summary>Protocol Buffers(无 schema 的线格式转储)。</summary>
    Protobuf,

    /// <summary>PHP <c>serialize()</c>。</summary>
    Php,

    /// <summary>Java 原生序列化(魔数 <c>ac ed 00 05</c>)。<b>未内置</b>。</summary>
    Java,

    /// <summary>Python pickle。<b>未内置</b>。</summary>
    Pickle
}

/// <summary>
/// 值的解码链:<c>原始字节 → 解压 → 反序列化 → 视图</c>。
/// <para>
/// 整条链只有一条纪律:<b>链是否可逆,决定这个值能不能被编辑并写回</b>。
/// 解压这一段两个方向都实现得了(GZip / Deflate / Brotli 走 BCL),所以
/// 「解压 + 文本/JSON/转义视图」这条链是可逆的 —— 编辑框里是什么,按同一条链压回去就是什么。
/// 反序列化这一段只实现了**读**(把二进制结构转成给人看的文本),因此一旦选上它,
/// 保存就必须禁用并说清是哪一步不可逆 —— 把一段人类可读的转储再"编码回去",
/// 猜的成分远大于确定的成分,而猜错的代价是静默改坏一个生产键。
/// </para>
/// <para>
/// Zstd / LZ4 / Snappy / Java / Pickle 一律**如实标为未内置**:它们各自需要一个额外的
/// 第三方依赖(而本仓库对随插件分发的依赖是逐个过许可证的)。识别得出魔数就明说
/// 「认出来了,但没带解码器」—— 这比灰一个按钮、或者解出一堆乱码都诚实。
/// </para>
/// </summary>
public static class RedisValueCodec
{
    /// <summary>
    /// 转储里把一段文本写成 JSON 字面量时用的选项。
    /// <para>
    /// <b>必须用宽松编码器</b>:默认那套会把「张三」写成 <c>张三</c> ——
    /// 转储是给人看的,把中文转成十六进制码点等于白转储一场。
    /// 这些文本只进屏幕、不进 HTML,所以宽松编码器在这里没有注入面。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions DumpText = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>解压环节的全部选项(界面按这个顺序画分段控件)。</summary>
    public static IReadOnlyList<RedisCompression> Compressions { get; } =
    [
        RedisCompression.None, RedisCompression.GZip, RedisCompression.Deflate,
        RedisCompression.Brotli, RedisCompression.Zstd, RedisCompression.Lz4, RedisCompression.Snappy
    ];

    /// <summary>反序列化环节的全部选项。</summary>
    public static IReadOnlyList<RedisSerialization> Serializations { get; } =
    [
        RedisSerialization.None, RedisSerialization.MsgPack, RedisSerialization.Protobuf,
        RedisSerialization.Php, RedisSerialization.Java, RedisSerialization.Pickle
    ];

    /// <summary>这个解压器是否内置。</summary>
    /// <param name="compression">解压环节。</param>
    /// <returns>内置则为 true。</returns>
    public static bool IsAvailable(RedisCompression compression) => compression
        is RedisCompression.None or RedisCompression.GZip
        or RedisCompression.Deflate or RedisCompression.Brotli;

    /// <summary>这个反序列化器是否内置。</summary>
    /// <param name="serialization">反序列化环节。</param>
    /// <returns>内置则为 true。</returns>
    public static bool IsAvailable(RedisSerialization serialization) => serialization
        is RedisSerialization.None or RedisSerialization.MsgPack
        or RedisSerialization.Protobuf or RedisSerialization.Php;

    /// <summary>解压这一步是否可逆(能按同一条链压回去)。</summary>
    /// <param name="compression">解压环节。</param>
    /// <returns>可逆则为 true。</returns>
    public static bool IsReversible(RedisCompression compression) => IsAvailable(compression);

    /// <summary>分段控件上的短标签(不进文案表:它们是格式名,不翻译)。</summary>
    /// <param name="compression">解压环节。</param>
    /// <returns>标签。</returns>
    public static string Label(RedisCompression compression) => compression switch
    {
        RedisCompression.GZip => "GZip",
        RedisCompression.Deflate => "Deflate",
        RedisCompression.Brotli => "Brotli",
        RedisCompression.Zstd => "Zstd",
        RedisCompression.Lz4 => "LZ4",
        RedisCompression.Snappy => "Snappy",
        _ => "—"
    };

    /// <inheritdoc cref="Label(RedisCompression)" />
    /// <param name="serialization">反序列化环节。</param>
    /// <remarks>
    /// 这几项写的是**格式全名**,而不是光一个语言名。单写「Java」「PHP」会被读成"这是哪门语言",
    /// 可这一栏问的是"这段字节是用什么写进来的" —— 而它们恰恰是各自语言里最常见的那个
    /// 序列化器:Spring Data Redis 默认的 <c>JdkSerializationRedisSerializer</c> 吐的就是
    /// <c>ac ed 00 05</c> 开头的 Java 原生序列化,PHP 的会话与多数缓存库存进来的就是
    /// <c>serialize()</c> 的文本。<b>格式名不翻译</b>(与集群那边 master / replica 同一条口径):
    /// 它们是这些格式在文档与错误信息里的原名,译过来反而对不上号。
    /// </remarks>
    public static string Label(RedisSerialization serialization) => serialization switch
    {
        RedisSerialization.MsgPack => "MsgPack",
        RedisSerialization.Protobuf => "Protobuf",
        RedisSerialization.Php => "PHP serialize()",
        RedisSerialization.Java => "Java serialization",
        RedisSerialization.Pickle => "Python pickle",
        _ => "—"
    };

    // ── 识别 ──────────────────────────────────────────────────────

    /// <summary>按魔数认一下这段字节是不是压过的。</summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>认出来的解压环节;认不出为 <see cref="RedisCompression.None" />。</returns>
    public static RedisCompression DetectCompression(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B)
        {
            return RedisCompression.GZip;
        }
        if (raw.Length >= 4 && raw[0] == 0x28 && raw[1] == 0xB5 && raw[2] == 0x2F && raw[3] == 0xFD)
        {
            return RedisCompression.Zstd;
        }
        if (raw.Length >= 4 && raw[0] == 0x04 && raw[1] == 0x22 && raw[2] == 0x4D && raw[3] == 0x18)
        {
            return RedisCompression.Lz4;
        }
        // Snappy 帧格式的首块是 stream identifier:ff 06 00 00 "sNaPpY"。
        if (raw.Length >= 10 && raw[0] == 0xFF && raw[1] == 0x06 && raw[2] == 0x00 && raw[3] == 0x00
            && raw[4] == 0x73 && raw[5] == 0x4E && raw[6] == 0x61 && raw[7] == 0x50 && raw[8] == 0x70 && raw[9] == 0x59)
        {
            return RedisCompression.Snappy;
        }
        // zlib:高四位是 8(deflate),且前两字节构成的大端数能被 31 整除。
        if (raw.Length >= 2 && (raw[0] & 0x0F) == 0x08 && ((raw[0] << 8) | raw[1]) % 31 == 0)
        {
            return RedisCompression.Deflate;
        }
        return RedisCompression.None;
    }

    /// <summary>
    /// 认一下这段字节是不是某种序列化格式。
    /// <para>
    /// 只有 Java 有可靠魔数;MsgPack 与 Protobuf 靠"整段都能解完"来判 —— 解不完就不算,
    /// 免得把一段普通文本当成 protobuf 转储出来一堆假字段。合法 UTF-8 文本一律不猜:
    /// 一段 JSON 恰好也能被 protobuf 的线格式勉强解开,而那个结论毫无意义。
    /// </para>
    /// </summary>
    /// <param name="raw">原始字节。</param>
    /// <returns>认出来的反序列化环节;认不出为 <see cref="RedisSerialization.None" />。</returns>
    public static RedisSerialization DetectSerialization(byte[] raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length >= 4 && raw[0] == 0xAC && raw[1] == 0xED && raw[2] == 0x00 && raw[3] == 0x05)
        {
            return RedisSerialization.Java;
        }
        if (RedisValueText.IsTextSafe(raw))
        {
            // 文本值不猜二进制格式,唯独 PHP 的 serialize() 是**文本**,而且首字符高度特征化。
            return LooksLikePhp(raw) ? RedisSerialization.Php : RedisSerialization.None;
        }
        if (raw.Length >= 2 && raw[0] == 0x80 && raw[1] is >= 0x02 and <= 0x05)
        {
            return RedisSerialization.Pickle;
        }
        if (TryDeserialize(raw, RedisSerialization.MsgPack, out _, out _))
        {
            return RedisSerialization.MsgPack;
        }
        if (TryDeserialize(raw, RedisSerialization.Protobuf, out _, out _))
        {
            return RedisSerialization.Protobuf;
        }
        return RedisSerialization.None;
    }

    private static bool LooksLikePhp(byte[] raw)
    {
        if (raw.Length < 2)
        {
            return false;
        }
        char head = (char)raw[0];
        return head is 'a' or 'O' or 's' or 'i' or 'd' or 'b' && raw[1] == ':'
               || (head == 'N' && raw[1] == ';');
    }

    // ── 解压 / 压回 ───────────────────────────────────────────────

    /// <summary>按指定环节解压。</summary>
    /// <param name="raw">原始字节。</param>
    /// <param name="compression">解压环节。</param>
    /// <param name="plain">解出的字节。</param>
    /// <param name="error">失败原因(英文短句,由调用方本地化包装);成功时为 null。</param>
    /// <returns>解压成功。</returns>
    public static bool TryDecompress(byte[] raw, RedisCompression compression, out byte[] plain, out string? error)
    {
        ArgumentNullException.ThrowIfNull(raw);
        plain = raw;
        error = null;
        if (compression == RedisCompression.None)
        {
            return true;
        }
        if (!IsAvailable(compression))
        {
            plain = [];
            error = $"{Label(compression)} decoder is not bundled";
            return false;
        }
        try
        {
            using var input = new MemoryStream(raw, writable: false);
            using var output = new MemoryStream();
            switch (compression)
            {
                case RedisCompression.GZip:
                    using (var stream = new GZipStream(input, CompressionMode.Decompress))
                    {
                        stream.CopyTo(output);
                    }
                    break;
                case RedisCompression.Brotli:
                    using (var stream = new BrotliStream(input, CompressionMode.Decompress))
                    {
                        stream.CopyTo(output);
                    }
                    break;
                default:
                    // 先按 zlib 试(带两字节头,是 PHP gzcompress / Python zlib.compress 的默认),
                    // 头不对再按裸 deflate 试一次 —— 两者在实际数据里都很常见。
                    if (!TryInflate(raw, zlib: true, output) && !TryInflate(raw, zlib: false, output))
                    {
                        plain = [];
                        error = "not a valid deflate/zlib stream";
                        return false;
                    }
                    break;
            }
            // **解出空来就算失败。** 一段只有 gzip 头、后面被截断(或者压根只是巧合撞上魔数)
            // 的字节,GZipStream 的 CopyTo 会一声不吭地读出 0 个字节 —— 于是界面上那个值
            // 会变成空白,而"这个键是空的"和"这段字节我解不开"是完全不同的两件事。
            // 代价是"压了一个空字符串"的极少数值会被判为解不开,退回按转义显示 —— 那一边的
            // 错是看得见且无害的。
            if (output.Length == 0 && raw.Length > 0)
            {
                plain = [];
                error = $"{Label(compression)} stream decoded to nothing (truncated or not really {Label(compression)})";
                return false;
            }
            plain = output.ToArray();
            return true;
        }
        catch (InvalidDataException ex)
        {
            plain = [];
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            plain = [];
            error = ex.Message;
            return false;
        }
    }

    private static bool TryInflate(byte[] raw, bool zlib, MemoryStream output)
    {
        output.SetLength(0);
        try
        {
            using var input = new MemoryStream(raw, writable: false);
            if (zlib)
            {
                using var stream = new ZLibStream(input, CompressionMode.Decompress);
                stream.CopyTo(output);
            }
            else
            {
                using var stream = new DeflateStream(input, CompressionMode.Decompress);
                stream.CopyTo(output);
            }
            return true;
        }
        catch (InvalidDataException)
        {
            output.SetLength(0);
            return false;
        }
    }

    /// <summary>
    /// 按指定环节压回去(保存路径)。
    /// <para>只有内置的三种压得回去;其余一律拒绝,而不是"就当没压过"地写一段裸字节回去 ——
    /// 那会让下一个读它的进程解压失败,而失败点离这里已经很远了。</para>
    /// </summary>
    /// <param name="plain">明文字节。</param>
    /// <param name="compression">解压环节。</param>
    /// <param name="raw">压好的字节。</param>
    /// <param name="error">失败原因;成功时为 null。</param>
    /// <returns>压缩成功。</returns>
    public static bool TryCompress(byte[] plain, RedisCompression compression, out byte[] raw, out string? error)
    {
        ArgumentNullException.ThrowIfNull(plain);
        raw = plain;
        error = null;
        if (compression == RedisCompression.None)
        {
            return true;
        }
        if (!IsAvailable(compression))
        {
            raw = [];
            error = $"{Label(compression)} encoder is not bundled";
            return false;
        }
        try
        {
            using var output = new MemoryStream();
            switch (compression)
            {
                case RedisCompression.GZip:
                    using (var stream = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        stream.Write(plain, 0, plain.Length);
                    }
                    break;
                case RedisCompression.Brotli:
                    using (var stream = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        stream.Write(plain, 0, plain.Length);
                    }
                    break;
                default:
                    // 压回去一律带 zlib 头:它是这一档最常见的形态,而裸 deflate 的读者
                    // 通常也接受 zlib(反过来不成立)。
                    using (var stream = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        stream.Write(plain, 0, plain.Length);
                    }
                    break;
            }
            raw = output.ToArray();
            return true;
        }
        catch (Exception ex)
        {
            raw = [];
            error = ex.Message;
            return false;
        }
    }

    // ── 反序列化(只读转储)────────────────────────────────────────

    /// <summary>把二进制结构转成给人看的文本。</summary>
    /// <param name="raw">(已解压的)字节。</param>
    /// <param name="serialization">反序列化环节。</param>
    /// <param name="text">转储文本。</param>
    /// <param name="error">失败原因;成功时为 null。</param>
    /// <returns>转储成功。</returns>
    public static bool TryDeserialize(byte[] raw, RedisSerialization serialization, out string text, out string? error)
    {
        ArgumentNullException.ThrowIfNull(raw);
        text = string.Empty;
        error = null;
        switch (serialization)
        {
            case RedisSerialization.None:
                return true;
            case RedisSerialization.MsgPack:
            {
                var reader = new MsgPackReader(raw);
                if (!reader.TryReadValue(out string? dump) || !reader.AtEnd)
                {
                    error = "not a complete MessagePack document";
                    return false;
                }
                text = dump!;
                return true;
            }
            case RedisSerialization.Protobuf:
            {
                if (!ProtobufDump.TryDump(raw, 0, out string? dump))
                {
                    error = "not a valid protobuf message";
                    return false;
                }
                text = dump!;
                return true;
            }
            case RedisSerialization.Php:
            {
                var reader = new PhpReader(Encoding.UTF8.GetString(raw));
                if (!reader.TryReadValue(0, out string? dump) || !reader.AtEnd)
                {
                    error = "not a complete PHP serialize() payload";
                    return false;
                }
                text = dump!;
                return true;
            }
            default:
                error = $"{Label(serialization)} decoder is not bundled";
                return false;
        }
    }

    // ── JSON 视图 ─────────────────────────────────────────────────

    /// <summary>把一段 JSON 缩进排版;不是合法 JSON 就原样返回并说明。</summary>
    /// <param name="text">原文本。</param>
    /// <param name="pretty">排版后的文本。</param>
    /// <param name="error">失败原因;成功时为 null。</param>
    /// <returns>是合法 JSON。</returns>
    public static bool TryPrettyJson(string text, out string pretty, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        pretty = text;
        error = null;
        if (text.Trim().Length == 0)
        {
            return true;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            pretty = JsonSerializer.Serialize(document.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ── MessagePack ───────────────────────────────────────────────

    /// <summary>
    /// 只读的 MessagePack 读取器:把文档转成 JSON 形状的文本。
    /// <para>不建对象树 —— 这一路的唯一去处是屏幕,直接拼字符串省掉一整套中间模型。</para>
    /// </summary>
    private ref struct MsgPackReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _at = 0;

        public readonly bool AtEnd => _at >= _data.Length;

        public bool TryReadValue(out string? text) => TryReadValue(0, out text);

        private bool TryReadValue(int depth, out string? text)
        {
            text = null;
            if (depth > 64 || _at >= _data.Length)
            {
                return false;
            }
            byte head = _data[_at++];
            switch (head)
            {
                case <= 0x7F:
                    text = head.ToString(CultureInfo.InvariantCulture);
                    return true;
                case >= 0xE0:
                    text = ((sbyte)head).ToString(CultureInfo.InvariantCulture);
                    return true;
                case >= 0x80 and <= 0x8F:
                    return TryReadMap(head & 0x0F, depth, out text);
                case >= 0x90 and <= 0x9F:
                    return TryReadArray(head & 0x0F, depth, out text);
                case >= 0xA0 and <= 0xBF:
                    return TryReadString(head & 0x1F, out text);
                case 0xC0:
                    text = "null";
                    return true;
                case 0xC2:
                    text = "false";
                    return true;
                case 0xC3:
                    text = "true";
                    return true;
                case 0xC4 or 0xC5 or 0xC6:
                {
                    int width = head == 0xC4 ? 1 : head == 0xC5 ? 2 : 4;
                    return TryReadLength(width, out int length) && TryReadBinary(length, out text);
                }
                case 0xC7 or 0xC8 or 0xC9:
                {
                    int width = head == 0xC7 ? 1 : head == 0xC8 ? 2 : 4;
                    return TryReadLength(width, out int length) && TryReadExt(length, out text);
                }
                case 0xCA:
                    return TryTake(4, out ReadOnlySpan<byte> f32)
                           && Set(BinaryPrimitives.ReadSingleBigEndian(f32).ToString("R", CultureInfo.InvariantCulture), out text);
                case 0xCB:
                    return TryTake(8, out ReadOnlySpan<byte> f64)
                           && Set(BinaryPrimitives.ReadDoubleBigEndian(f64).ToString("R", CultureInfo.InvariantCulture), out text);
                case 0xCC or 0xCD or 0xCE or 0xCF:
                {
                    int width = 1 << (head - 0xCC);
                    return TryReadUnsigned(width, out ulong value)
                           && Set(value.ToString(CultureInfo.InvariantCulture), out text);
                }
                case 0xD0 or 0xD1 or 0xD2 or 0xD3:
                {
                    int width = 1 << (head - 0xD0);
                    return TryReadSigned(width, out long value)
                           && Set(value.ToString(CultureInfo.InvariantCulture), out text);
                }
                case 0xD4 or 0xD5 or 0xD6 or 0xD7 or 0xD8:
                    return TryReadExt(1 << (head - 0xD4), out text);
                case 0xD9 or 0xDA or 0xDB:
                {
                    int width = head == 0xD9 ? 1 : head == 0xDA ? 2 : 4;
                    return TryReadLength(width, out int length) && TryReadString(length, out text);
                }
                case 0xDC or 0xDD:
                {
                    int width = head == 0xDC ? 2 : 4;
                    return TryReadLength(width, out int count) && TryReadArray(count, depth, out text);
                }
                case 0xDE or 0xDF:
                {
                    int width = head == 0xDE ? 2 : 4;
                    return TryReadLength(width, out int count) && TryReadMap(count, depth, out text);
                }
                default:
                    // 0xC1 在规范里是"永不使用"。碰上它就说明这段字节压根不是 MessagePack。
                    return false;
            }
        }

        private static bool Set(string value, out string? text)
        {
            text = value;
            return true;
        }

        private bool TryTake(int count, out ReadOnlySpan<byte> span)
        {
            if (count < 0 || _at + count > _data.Length)
            {
                span = default;
                return false;
            }
            span = _data.Slice(_at, count);
            _at += count;
            return true;
        }

        private bool TryReadLength(int width, out int length)
        {
            length = 0;
            if (!TryReadUnsigned(width, out ulong raw) || raw > int.MaxValue)
            {
                return false;
            }
            length = (int)raw;
            return true;
        }

        private bool TryReadUnsigned(int width, out ulong value)
        {
            value = 0;
            if (!TryTake(width, out ReadOnlySpan<byte> span))
            {
                return false;
            }
            foreach (byte b in span)
            {
                value = (value << 8) | b;
            }
            return true;
        }

        private bool TryReadSigned(int width, out long value)
        {
            value = 0;
            if (!TryTake(width, out ReadOnlySpan<byte> span))
            {
                return false;
            }
            value = (sbyte)span[0];
            for (int i = 1; i < span.Length; i++)
            {
                value = (value << 8) | span[i];
            }
            return true;
        }

        private bool TryReadString(int length, out string? text)
        {
            text = null;
            if (!TryTake(length, out ReadOnlySpan<byte> span))
            {
                return false;
            }
            text = JsonSerializer.Serialize(Encoding.UTF8.GetString(span), DumpText);
            return true;
        }

        private bool TryReadBinary(int length, out string? text)
        {
            text = null;
            if (!TryTake(length, out ReadOnlySpan<byte> span))
            {
                return false;
            }
            text = $"\"bin({length}) {Convert.ToHexString(span[..Math.Min(span.Length, 32)]).ToLowerInvariant()}\"";
            return true;
        }

        private bool TryReadExt(int length, out string? text)
        {
            text = null;
            if (!TryTake(1, out ReadOnlySpan<byte> typeSpan) || !TryTake(length, out ReadOnlySpan<byte> body))
            {
                return false;
            }
            text = $"\"ext({(sbyte)typeSpan[0]}) {Convert.ToHexString(body[..Math.Min(body.Length, 32)]).ToLowerInvariant()}\"";
            return true;
        }

        private bool TryReadArray(int count, int depth, out string? text)
        {
            text = null;
            var builder = new StringBuilder("[");
            for (int i = 0; i < count; i++)
            {
                if (!TryReadValue(depth + 1, out string? item))
                {
                    return false;
                }
                builder.Append(i == 0 ? string.Empty : ", ").Append(item);
            }
            text = builder.Append(']').ToString();
            return true;
        }

        private bool TryReadMap(int count, int depth, out string? text)
        {
            text = null;
            var builder = new StringBuilder("{");
            for (int i = 0; i < count; i++)
            {
                if (!TryReadValue(depth + 1, out string? key) || !TryReadValue(depth + 1, out string? value))
                {
                    return false;
                }
                builder.Append(i == 0 ? string.Empty : ", ").Append(key).Append(": ").Append(value);
            }
            text = builder.Append('}').ToString();
            return true;
        }
    }

    // ── Protobuf(无 schema 的线格式转储)──────────────────────────

    /// <summary>
    /// 无 schema 的 protobuf 转储。
    /// <para>
    /// 没有 <c>.proto</c> 就拿不到字段名与具体类型 —— 但线格式本身足够自描述:
    /// 字段号、线类型、以及每个长度分隔段"像不像一条嵌套消息 / 像不像一段 UTF-8"。
    /// 把这三件事如实摆出来,已经能回答"这个键里到底存了什么"这个问题的九成。
    /// </para>
    /// </summary>
    private static class ProtobufDump
    {
        public static bool TryDump(ReadOnlySpan<byte> data, int depth, out string? text)
        {
            text = null;
            if (depth > 16 || data.Length == 0)
            {
                return false;
            }
            var builder = new StringBuilder();
            int at = 0;
            string pad = new(' ', (depth + 1) * 2);
            while (at < data.Length)
            {
                if (!TryReadVarint(data, ref at, out ulong key))
                {
                    return false;
                }
                int field = (int)(key >> 3);
                int wire = (int)(key & 0x07);
                if (field == 0)
                {
                    return false;
                }
                builder.Append(pad).Append(field).Append(": ");
                switch (wire)
                {
                    case 0:
                        if (!TryReadVarint(data, ref at, out ulong varint))
                        {
                            return false;
                        }
                        builder.Append(varint.ToString(CultureInfo.InvariantCulture))
                            .Append("            # varint");
                        break;
                    case 1:
                        if (at + 8 > data.Length)
                        {
                            return false;
                        }
                        builder.Append(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8))
                                .ToString(CultureInfo.InvariantCulture))
                            .Append("            # fixed64");
                        at += 8;
                        break;
                    case 5:
                        if (at + 4 > data.Length)
                        {
                            return false;
                        }
                        builder.Append(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4))
                                .ToString(CultureInfo.InvariantCulture))
                            .Append("            # fixed32");
                        at += 4;
                        break;
                    case 2:
                    {
                        if (!TryReadVarint(data, ref at, out ulong length) || length > int.MaxValue
                            || at + (int)length > data.Length)
                        {
                            return false;
                        }
                        ReadOnlySpan<byte> body = data.Slice(at, (int)length);
                        at += (int)length;
                        AppendLengthDelimited(builder, body, depth, pad);
                        break;
                    }
                    default:
                        // 3 / 4 是废弃的 group,6 / 7 不存在:碰上就说明这不是 protobuf。
                        return false;
                }
                builder.Append('\n');
            }
            text = depth == 0
                ? builder.ToString().TrimEnd('\n')
                : "{\n" + builder.ToString().TrimEnd('\n') + "\n" + new string(' ', depth * 2) + "}";
            return true;
        }

        private static void AppendLengthDelimited(StringBuilder builder, ReadOnlySpan<byte> body, int depth, string pad)
        {
            // **可读文本优先于嵌套消息。**
            // 线格式里这两者无法区分:一段 ASCII 字符串几乎总能被当成一条"嵌套消息"解开
            // (`"hi"` = 68 69 会被读成"字段 13,varint 105"),而那串字段号纯属噪音。
            // 反过来,一条真的嵌套消息若恰好全是可打印字节,被渲染成字符串至少还能读出内容。
            // 两个方向的误判都存在,但这一个方向的结果对人有用得多。
            if (IsPrintableUtf8(body))
            {
                builder.Append(JsonSerializer.Serialize(Encoding.UTF8.GetString(body), DumpText))
                    .Append("  # string(").Append(body.Length).Append(" B)");
                return;
            }
            if (body.Length > 0 && TryDump(body, depth + 1, out string? nested))
            {
                builder.Append(nested).Append("  # message(").Append(body.Length).Append(" B)");
                return;
            }
            builder.Append("0x").Append(Convert.ToHexString(body[..Math.Min(body.Length, 32)]).ToLowerInvariant())
                .Append(body.Length > 32 ? "…" : string.Empty)
                .Append("  # bytes(").Append(body.Length).Append(" B)");
        }

        private static bool IsPrintableUtf8(ReadOnlySpan<byte> body)
        {
            if (body.Length == 0)
            {
                return true;
            }
            try
            {
                string decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body);
                foreach (char ch in decoded)
                {
                    if (char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int at, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64; shift += 7)
            {
                if (at >= data.Length)
                {
                    return false;
                }
                byte b = data[at++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    // ── PHP serialize() ───────────────────────────────────────────

    /// <summary>PHP <c>serialize()</c> 的只读读取器。输出 JSON 形状的文本。</summary>
    private sealed class PhpReader(string text)
    {
        private int _at;

        public bool AtEnd => _at >= text.Length || text.AsSpan(_at).Trim().Length == 0;

        public bool TryReadValue(int depth, out string? result)
        {
            result = null;
            if (depth > 64 || _at + 1 >= text.Length)
            {
                return false;
            }
            char kind = text[_at];
            switch (kind)
            {
                case 'N' when text[_at + 1] == ';':
                    _at += 2;
                    result = "null";
                    return true;
                case 'b' when text[_at + 1] == ':':
                    _at += 2;
                    if (!TryReadUntil(';', out string flag))
                    {
                        return false;
                    }
                    result = flag == "1" ? "true" : "false";
                    return true;
                case 'i' when text[_at + 1] == ':':
                case 'd' when text[_at + 1] == ':':
                    _at += 2;
                    if (!TryReadUntil(';', out string number))
                    {
                        return false;
                    }
                    result = number;
                    return true;
                case 's' when text[_at + 1] == ':':
                    return TryReadString(out result);
                case 'a' when text[_at + 1] == ':':
                    _at += 2;
                    return TryReadCollection(depth, header: null, out result);
                case 'O' when text[_at + 1] == ':':
                {
                    _at += 2;
                    if (!TryReadUntil(':', out string nameLength)
                        || !int.TryParse(nameLength, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length)
                        || _at + length + 2 > text.Length)
                    {
                        return false;
                    }
                    // 类名带引号:"MyClass":
                    string className = text.Substring(_at + 1, length);
                    _at += length + 3;
                    return TryReadCollection(depth, className, out result);
                }
                default:
                    return false;
            }
        }

        private bool TryReadString(out string? result)
        {
            result = null;
            _at += 2;
            if (!TryReadUntil(':', out string lengthText)
                || !int.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length))
            {
                return false;
            }
            // 长度是**字节数**,而这里已经是 .NET 的字符串。ASCII 之外两者会分道扬镳,
            // 所以按字节长度回切:先取一段足够长的字符,再按 UTF-8 字节数截。
            if (_at >= text.Length || text[_at] != '"')
            {
                return false;
            }
            int start = _at + 1;
            int end = start;
            int bytes = 0;
            while (end < text.Length && bytes < length)
            {
                bytes += Encoding.UTF8.GetByteCount(text.AsSpan(end, 1));
                end++;
            }
            if (bytes != length || end + 1 >= text.Length || text[end] != '"' || text[end + 1] != ';')
            {
                return false;
            }
            result = JsonSerializer.Serialize(text[start..end], DumpText);
            _at = end + 2;
            return true;
        }

        private bool TryReadCollection(int depth, string? header, out string? result)
        {
            result = null;
            if (!TryReadUntil(':', out string countText)
                || !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                || _at >= text.Length || text[_at] != '{')
            {
                return false;
            }
            _at++;
            var builder = new StringBuilder(header is null ? "{" : $"{header} {{");
            for (int i = 0; i < count; i++)
            {
                if (!TryReadValue(depth + 1, out string? key) || !TryReadValue(depth + 1, out string? value))
                {
                    return false;
                }
                builder.Append(i == 0 ? string.Empty : ", ").Append(key).Append(": ").Append(value);
            }
            if (_at >= text.Length || text[_at] != '}')
            {
                return false;
            }
            _at++;
            result = builder.Append('}').ToString();
            return true;
        }

        private bool TryReadUntil(char terminator, out string value)
        {
            int index = text.IndexOf(terminator, _at);
            if (index < 0)
            {
                value = string.Empty;
                return false;
            }
            value = text[_at..index];
            _at = index + 1;
            return true;
        }
    }
}
