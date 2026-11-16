using System.Buffers.Binary;
using System.Runtime.InteropServices;
using VelaShell.Vnc.Wire;

namespace VelaShell.Vnc.Graphics;

/// <summary>RFB encoding numbers (RFC 6143 §7.7–7.8 and the registered pseudo-encodings).</summary>
internal static class VncEncoding
{
    internal const int Raw = 0, CopyRect = 1, Rre = 2, Hextile = 5, Tight = 7, Zrle = 16;
    internal const int JpegQuality0 = -32, CompressLevel0 = -256;
    internal const int DesktopSize = -223, LastRect = -224, Cursor = -239, QemuExtendedKey = -258,
        DesktopName = -307, ExtendedDesktopSize = -308;
    internal const int ExtendedClipboard = unchecked((int)0xC0A1E5CE);

    internal static string Name(int encoding) => encoding switch
    {
        Raw => "Raw",
        CopyRect => "CopyRect",
        Rre => "RRE",
        Hextile => "Hextile",
        Tight => "Tight",
        Zrle => "ZRLE",
        _ => encoding.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// Decodes the pixel-carrying encodings into the framebuffer. The client always sets its pixel format to 32 bpp, depth 24,
/// little-endian true colour with red at bit 16 (SetPixelFormat), so a PIXEL is B, G, R, X in memory — the framebuffer's
/// own layout — a ZRLE CPIXEL is B, G, R and a Tight TPIXEL is R, G, B.
/// </summary>
internal sealed class RectangleDecoder(VncFramebuffer framebuffer, VncJpegDecoder? jpeg) : IDisposable
{
    private const int MaxCompressed = 64 * 1024 * 1024;
    private readonly ZlibChannel _zrle = new();
    private readonly ZlibChannel[] _tight = [new(), new(), new(), new()];

    internal async ValueTask DecodeAsync(RfbReader reader, int encoding, int x, int y, int width, int height, CancellationToken ct)
    {
        framebuffer.Check(x, y, width, height);
        switch (encoding)
        {
            case VncEncoding.Raw: await RawAsync(reader, x, y, width, height, ct).ConfigureAwait(false); break;
            case VncEncoding.CopyRect:
                int sourceX = await reader.U16Async(ct).ConfigureAwait(false), sourceY = await reader.U16Async(ct).ConfigureAwait(false);
                framebuffer.CopyRect(sourceX, sourceY, x, y, width, height);
                break;
            case VncEncoding.Rre: await RreAsync(reader, x, y, width, height, ct).ConfigureAwait(false); break;
            case VncEncoding.Hextile: await HextileAsync(reader, x, y, width, height, ct).ConfigureAwait(false); break;
            case VncEncoding.Zrle: await ZrleAsync(reader, x, y, width, height, ct).ConfigureAwait(false); break;
            case VncEncoding.Tight: await TightAsync(reader, x, y, width, height, ct).ConfigureAwait(false); break;
            default: throw new VncProtocolException($"The server used encoding {encoding}, which this client did not ask for.");
        }
    }

    private static uint Pixel(ReadOnlySpan<byte> bgrx) => BinaryPrimitives.ReadUInt32LittleEndian(bgrx) & 0xFFFFFF;

    // ---- Raw (§7.7.1) ----

    private async ValueTask RawAsync(RfbReader reader, int x, int y, int width, int height, CancellationToken ct)
    {
        if (width == 0 || height == 0) { return; }
        int rowBytes = width * 4, rowsPerChunk = Math.Max(1, (1 << 20) / rowBytes);
        byte[] buffer = new byte[Math.Min(height, rowsPerChunk) * rowBytes];
        for (int row = 0; row < height; row += rowsPerChunk)
        {
            int rows = Math.Min(rowsPerChunk, height - row);
            await reader.ReadAsync(buffer.AsMemory(0, rows * rowBytes), ct).ConfigureAwait(false);
            framebuffer.Blit(x, y + row, width, rows, buffer, rowBytes);
        }
    }

    // ---- RRE (§7.7.3) ----

    private async ValueTask RreAsync(RfbReader reader, int x, int y, int width, int height, CancellationToken ct)
    {
        uint count = await reader.U32Async(ct).ConfigureAwait(false);
        if (count > (uint)Math.Max(1, width * height))
        {
            throw new VncProtocolException("An RRE rectangle has more subrectangles than pixels.");
        }

        byte[] background = await reader.BytesAsync(4, ct).ConfigureAwait(false);
        framebuffer.Fill(x, y, width, height, Pixel(background));
        byte[] chunk = new byte[12 * 4096];
        for (uint done = 0; done < count;)
        {
            int batch = (int)Math.Min(4096, count - done);
            await reader.ReadAsync(chunk.AsMemory(0, batch * 12), ct).ConfigureAwait(false);
            for (int i = 0; i < batch; i++)
            {
                ReadOnlySpan<byte> s = chunk.AsSpan(i * 12, 12);
                int sx = BinaryPrimitives.ReadUInt16BigEndian(s[4..]), sy = BinaryPrimitives.ReadUInt16BigEndian(s[6..]);
                int sw = BinaryPrimitives.ReadUInt16BigEndian(s[8..]), sh = BinaryPrimitives.ReadUInt16BigEndian(s[10..]);
                if (sx + sw > width || sy + sh > height) { throw new VncProtocolException("An RRE subrectangle lies outside its rectangle."); }
                framebuffer.Fill(x + sx, y + sy, sw, sh, Pixel(s));
            }
            done += (uint)batch;
        }
    }

    // ---- Hextile (§7.7.4) ----

    private async ValueTask HextileAsync(RfbReader reader, int x, int y, int width, int height, CancellationToken ct)
    {
        const int RawTile = 1, BackgroundSpecified = 2, ForegroundSpecified = 4, AnySubrects = 8, SubrectsColoured = 16;
        uint background = 0, foreground = 0;
        uint[] tile = new uint[16 * 16];
        byte[] raw = new byte[16 * 16 * 4];
        byte[] subrects = new byte[255 * 6];
        byte[] pixel = new byte[4];
        for (int ty = 0; ty < height; ty += 16)
        {
            for (int tx = 0; tx < width; tx += 16)
            {
                int tw = Math.Min(16, width - tx), th = Math.Min(16, height - ty);
                byte mask = await reader.U8Async(ct).ConfigureAwait(false);
                if ((mask & RawTile) != 0)
                {
                    await reader.ReadAsync(raw.AsMemory(0, tw * th * 4), ct).ConfigureAwait(false);
                    framebuffer.Blit(x + tx, y + ty, tw, th, raw, tw * 4);
                    continue;
                }

                if ((mask & BackgroundSpecified) != 0)
                {
                    await reader.ReadAsync(pixel, ct).ConfigureAwait(false);
                    background = Pixel(pixel);
                }

                if ((mask & ForegroundSpecified) != 0)
                {
                    await reader.ReadAsync(pixel, ct).ConfigureAwait(false);
                    foreground = Pixel(pixel);
                }

                int count = (mask & AnySubrects) != 0 ? await reader.U8Async(ct).ConfigureAwait(false) : 0;
                if (count == 0)
                {
                    framebuffer.Fill(x + tx, y + ty, tw, th, background);
                    continue;
                }

                bool coloured = (mask & SubrectsColoured) != 0;
                int size = coloured ? 6 : 2;
                await reader.ReadAsync(subrects.AsMemory(0, count * size), ct).ConfigureAwait(false);
                Array.Fill(tile, background | 0xFF000000, 0, tw * th);
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> s = subrects.AsSpan(i * size, size);
                    uint colour = coloured ? Pixel(s) : foreground;
                    int xy = s[size - 2], wh = s[size - 1];
                    int sx = xy >> 4, sy = xy & 15, sw = (wh >> 4) + 1, sh = (wh & 15) + 1;
                    if (sx + sw > tw || sy + sh > th) { throw new VncProtocolException("A Hextile subrectangle lies outside its tile."); }
                    for (int row = sy; row < sy + sh; row++) { tile.AsSpan(row * tw + sx, sw).Fill(colour | 0xFF000000); }
                }
                framebuffer.Blit(x + tx, y + ty, tw, th, MemoryMarshal.AsBytes(tile.AsSpan(0, tw * th)), tw * 4);
            }
        }
    }

    // ---- ZRLE (§7.7.6) ----

    private static async ValueTask<byte[]> CompressedAsync(RfbReader reader, int length, CancellationToken ct)
    {
        if (length is < 0 or > MaxCompressed)
        {
            throw new VncProtocolException($"A compressed rectangle claims {length} bytes.");
        }
        return await reader.BytesAsync(length, ct).ConfigureAwait(false);
    }

    private async ValueTask ZrleAsync(RfbReader reader, int x, int y, int width, int height, CancellationToken ct)
    {
        byte[] compressed = await CompressedAsync(reader, (int)Math.Min(int.MaxValue, await reader.U32Async(ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
        if (width == 0 || height == 0) { return; }
        long tiles = (long)((width + 63) / 64) * ((height + 63) / 64);
        int limit = (int)Math.Min(int.MaxValue, (long)width * height * 4 + tiles * 512 + 1024);
        byte[] data = _zrle.InflateAll(compressed, limit);
        DecodeZrle(data, x, y, width, height);
    }

    private void DecodeZrle(ReadOnlySpan<byte> data, int x, int y, int width, int height)
    {
        var r = new SpanReader(data);
        Span<uint> palette = stackalloc uint[128];
        uint[] tile = new uint[64 * 64];
        for (int ty = 0; ty < height; ty += 64)
        {
            for (int tx = 0; tx < width; tx += 64)
            {
                int tw = Math.Min(64, width - tx), th = Math.Min(64, height - ty), pixels = tw * th;
                int sub = r.U8();
                switch (sub)
                {
                    case 0:
                        for (int i = 0; i < pixels; i++) { tile[i] = Cpixel(ref r); }
                        break;
                    case 1:
                        framebuffer.Fill(x + tx, y + ty, tw, th, Cpixel(ref r));
                        continue;
                    case >= 2 and <= 16:
                    {
                        for (int i = 0; i < sub; i++) { palette[i] = Cpixel(ref r); }
                        int bits = sub == 2 ? 1 : sub <= 4 ? 2 : 4, mask = (1 << bits) - 1;
                        for (int row = 0; row < th; row++)
                        {
                            int shift = 8, current = 0;
                            for (int column = 0; column < tw; column++)
                            {
                                if (shift == 8) { current = r.U8(); shift = 0; }
                                shift += bits;
                                int index = (current >> (8 - shift)) & mask;
                                if (index >= sub) { throw new VncProtocolException("A ZRLE palette index is out of range."); }
                                tile[row * tw + column] = palette[index];
                            }
                        }
                        break;
                    }
                    case 128:
                        for (int i = 0; i < pixels;)
                        {
                            uint colour = Cpixel(ref r);
                            int run = RunLength(ref r);
                            if (run > pixels - i) { throw new VncProtocolException("A ZRLE run overflows its tile."); }
                            tile.AsSpan(i, run).Fill(colour);
                            i += run;
                        }
                        break;
                    case >= 130:
                    {
                        int size = sub - 128;
                        for (int i = 0; i < size; i++) { palette[i] = Cpixel(ref r); }
                        for (int i = 0; i < pixels;)
                        {
                            int index = r.U8(), run = 1;
                            if ((index & 128) != 0) { index &= 127; run = RunLength(ref r); }
                            if (index >= size) { throw new VncProtocolException("A ZRLE palette index is out of range."); }
                            if (run > pixels - i) { throw new VncProtocolException("A ZRLE run overflows its tile."); }
                            tile.AsSpan(i, run).Fill(palette[index]);
                            i += run;
                        }
                        break;
                    }
                    default:
                        throw new VncProtocolException($"ZRLE subencoding {sub} is not defined.");
                }

                for (int i = 0; i < pixels; i++) { tile[i] |= 0xFF000000; }
                framebuffer.Blit(x + tx, y + ty, tw, th, MemoryMarshal.AsBytes(tile.AsSpan(0, pixels)), tw * 4);
            }
        }
    }

    private static uint Cpixel(ref SpanReader r)
    {
        ReadOnlySpan<byte> p = r.Take(3);
        return p[0] | ((uint)p[1] << 8) | ((uint)p[2] << 16);
    }

    private static int RunLength(ref SpanReader r)
    {
        int run = 1, b;
        do
        {
            b = r.U8();
            run += b;
            if (run > 64 * 64) { throw new VncProtocolException("A ZRLE run overflows its tile."); }
        }
        while (b == 255);
        return run;
    }

    // ---- Tight (TightVNC / rfbproto "Tight Encoding") ----

    private static async ValueTask<int> CompactLengthAsync(RfbReader reader, CancellationToken ct)
    {
        int b = await reader.U8Async(ct).ConfigureAwait(false), length = b & 0x7F;
        if ((b & 0x80) != 0)
        {
            b = await reader.U8Async(ct).ConfigureAwait(false);
            length |= (b & 0x7F) << 7;
            if ((b & 0x80) != 0)
            {
                length |= await reader.U8Async(ct).ConfigureAwait(false) << 14;
            }
        }
        return length;
    }

    private static uint Tpixel(ReadOnlySpan<byte> rgb) => ((uint)rgb[0] << 16) | ((uint)rgb[1] << 8) | rgb[2];

    private async ValueTask TightAsync(RfbReader reader, int x, int y, int width, int height, CancellationToken ct)
    {
        int control = await reader.U8Async(ct).ConfigureAwait(false);
        for (int stream = 0; stream < 4; stream++)
        {
            if ((control & (1 << stream)) != 0) { _tight[stream].Reset(); }
        }

        int kind = control >> 4;
        if (kind == 0x8)
        {
            byte[] rgb = await reader.BytesAsync(3, ct).ConfigureAwait(false);
            framebuffer.Fill(x, y, width, height, Tpixel(rgb));
            return;
        }

        if (kind == 0x9)
        {
            byte[] data = await CompressedAsync(reader, await CompactLengthAsync(reader, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            if (width == 0 || height == 0) { return; }
            VncDecodedImage image = (jpeg ?? throw new VncProtocolException("The server sent JPEG although this client did not ask for it."))(data)
                ?? throw new VncProtocolException("A Tight JPEG rectangle could not be decoded.");
            if (image.Width != width || image.Height != height || image.Bgra.Length < width * height * 4)
            {
                throw new VncProtocolException($"A Tight JPEG image is {image.Width}×{image.Height}, not {width}×{height}.");
            }
            framebuffer.Blit(x, y, width, height, image.Bgra, width * 4);
            return;
        }

        if (kind > 0x9)
        {
            throw new VncProtocolException($"Tight compression type 0x{kind:X} is not supported.");
        }

        // Basic compression: bits 4–5 pick the zlib stream, bit 6 says a filter id follows.
        int streamId = kind & 3, filter = (kind & 4) != 0 ? await reader.U8Async(ct).ConfigureAwait(false) : 0;
        uint[] palette = [];
        int dataSize;
        switch (filter)
        {
            case 0:
            case 2:
                dataSize = width * height * 3;
                break;
            case 1:
            {
                int colours = await reader.U8Async(ct).ConfigureAwait(false) + 1;
                if (colours < 2) { throw new VncProtocolException("A Tight palette has fewer than two colours."); }
                byte[] rgb = await reader.BytesAsync(colours * 3, ct).ConfigureAwait(false);
                palette = new uint[colours];
                for (int i = 0; i < colours; i++) { palette[i] = Tpixel(rgb.AsSpan(i * 3)); }
                dataSize = colours == 2 ? (width + 7) / 8 * height : width * height;
                break;
            }
            default:
                throw new VncProtocolException($"Tight filter {filter} is not defined.");
        }

        byte[] raw = dataSize < 12
            ? await reader.BytesAsync(dataSize, ct).ConfigureAwait(false)
            : _tight[streamId].Inflate(await CompressedAsync(reader, await CompactLengthAsync(reader, ct).ConfigureAwait(false), ct).ConfigureAwait(false), dataSize);
        if (width == 0 || height == 0) { return; }

        uint[] pixels = new uint[width * height];
        switch (filter)
        {
            case 0:
                for (int i = 0; i < pixels.Length; i++) { pixels[i] = Tpixel(raw.AsSpan(i * 3)); }
                break;
            case 1 when palette.Length == 2:
            {
                int rowBytes = (width + 7) / 8;
                for (int row = 0; row < height; row++)
                {
                    for (int column = 0; column < width; column++)
                    {
                        int bit = (raw[row * rowBytes + column / 8] >> (7 - column % 8)) & 1;
                        pixels[row * width + column] = palette[bit];
                    }
                }
                break;
            }
            case 1:
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (raw[i] >= palette.Length) { throw new VncProtocolException("A Tight palette index is out of range."); }
                    pixels[i] = palette[raw[i]];
                }
                break;
            case 2:
                Gradient(raw, pixels, width, height);
                break;
        }

        for (int i = 0; i < pixels.Length; i++) { pixels[i] |= 0xFF000000; }
        framebuffer.Blit(x, y, width, height, MemoryMarshal.AsBytes(pixels.AsSpan()), width * 4);
    }

    /// <summary>
    /// The gradient filter: each channel is sent as the difference from left + above − above-left (clamped to 0–255),
    /// with pixels outside the rectangle taken as zero.
    /// </summary>
    private static void Gradient(ReadOnlySpan<byte> data, Span<uint> pixels, int width, int height)
    {
        int[] above = new int[width * 3], current = new int[width * 3];
        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int left = column > 0 ? current[(column - 1) * 3 + c] : 0;
                    int up = above[column * 3 + c];
                    int upLeft = column > 0 ? above[(column - 1) * 3 + c] : 0;
                    int predicted = Math.Clamp(left + up - upLeft, 0, 255);
                    current[column * 3 + c] = (predicted + data[(row * width + column) * 3 + c]) & 0xFF;
                }
                pixels[row * width + column] = ((uint)current[column * 3] << 16) | ((uint)current[column * 3 + 1] << 8) | (uint)current[column * 3 + 2];
            }
            (above, current) = (current, above);
        }
    }

    // ---- Cursor pseudo-encoding (§7.8.1) ----

    internal static async ValueTask<VncPointer> CursorAsync(RfbReader reader, int hotspotX, int hotspotY, int width, int height, CancellationToken ct)
    {
        if (width > 256 || height > 256)
        {
            throw new VncProtocolException($"The server's cursor is {width}×{height}; at most 256×256 is accepted.");
        }

        int maskStride = (width + 7) / 8;
        byte[] pixels = await reader.BytesAsync(width * height * 4, ct).ConfigureAwait(false);
        byte[] mask = await reader.BytesAsync(maskStride * height, ct).ConfigureAwait(false);
        if (width == 0 || height == 0)
        {
            return new VncPointer(0, 0, 0, 0, []);
        }

        for (int row = 0; row < height; row++)
        {
            for (int column = 0; column < width; column++)
            {
                bool visible = ((mask[row * maskStride + column / 8] >> (7 - column % 8)) & 1) != 0;
                pixels[(row * width + column) * 4 + 3] = visible ? (byte)255 : (byte)0;
            }
        }

        return new VncPointer(width, height, Math.Clamp(hotspotX, 0, width - 1), Math.Clamp(hotspotY, 0, height - 1), pixels);
    }

    public void Dispose()
    {
        _zrle.Dispose();
        foreach (ZlibChannel channel in _tight) { channel.Dispose(); }
    }
}
