using System.Text;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// 文本编码:导出时按用户选的写,导入时从文件头认。
/// <para>
/// GBK 走 <see cref="CodePagesEncodingProvider" /> **直接取**,不调 <c>Encoding.RegisterProvider</c>:
/// 后者是进程级的全局登记,插件不该替宿主改这种东西。
/// </para>
/// </summary>
internal static class TextEncodings
{
    /// <summary>GBK 的代码页。</summary>
    private const int GbkCodePage = 936;

    /// <summary>取编码对象。UTF-8 带 BOM 的那个会让 <see cref="StreamWriter" /> 先写三个字节的 BOM。</summary>
    public static Encoding Get(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        TextEncodingKind.Gbk => Gbk,
        TextEncodingKind.Utf16 => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
    };

    /// <summary>GBK 编码。</summary>
    public static Encoding Gbk => CodePagesEncodingProvider.Instance.GetEncoding(GbkCodePage) ?? Encoding.UTF8;

    /// <summary>给人看的名字(文件头那一行:<c>UTF-8</c> / <c>GBK</c>)。</summary>
    public static string Name(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom => "UTF-8 BOM",
        TextEncodingKind.Gbk => "GBK",
        TextEncodingKind.Utf16 => "UTF-16",
        _ => "UTF-8"
    };

    /// <summary>
    /// 从文件头认编码:先看 BOM;没有 BOM 时按严格 UTF-8 解一遍,解不通就当 GBK ——
    /// 国内导出的 CSV 不是 UTF-8 的,十有八九是 GBK(Excel "另存为 CSV" 的默认)。
    /// </summary>
    /// <param name="head">文件开头的若干字节(64 KB 足够)。</param>
    /// <param name="complete">这段是不是整个文件(否则末尾可能截断了一个多字节字符,要宽容)。</param>
    public static TextEncodingKind Detect(ReadOnlySpan<byte> head, bool complete)
    {
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            return TextEncodingKind.Utf8Bom;
        }
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            return TextEncodingKind.Utf16;
        }
        ReadOnlySpan<byte> body = head;
        if (!complete)
        {
            // 截掉末尾可能被切开的那个多字节字符(UTF-8 最长 4 字节)。
            int cut = body.Length;
            for (int i = 1; i <= 3 && i <= body.Length; i++)
            {
                byte b = body[^i];
                if ((b & 0xC0) == 0xC0)
                {
                    cut = body.Length - i;
                    break;
                }
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }
            body = body[..cut];
        }
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(body);
            return TextEncodingKind.Utf8;
        }
        catch (DecoderFallbackException)
        {
            return TextEncodingKind.Gbk;
        }
    }

    /// <summary>读文件开头一段并认编码。</summary>
    public static TextEncodingKind Detect(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] buffer = new byte[64 * 1024];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Detect(buffer.AsSpan(0, read), complete: read < buffer.Length);
    }

    /// <summary>按编码开一个读文本的读者(会自己跳过 BOM)。</summary>
    public static StreamReader OpenReader(Stream stream, TextEncodingKind kind) =>
        new(stream, Get(kind), detectEncodingFromByteOrderMarks: kind != TextEncodingKind.Gbk, bufferSize: 64 * 1024);
}
