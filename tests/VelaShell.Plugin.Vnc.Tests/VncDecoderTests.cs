using System.IO.Compression;
using VelaShell.Vnc;
using VelaShell.Vnc.Graphics;
using VelaShell.Vnc.Wire;
using static VelaShell.Plugin.Vnc.Tests.ServerConnection;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>One zlib stream compressed with a sync flush per rectangle, the way VNC servers do it.</summary>
internal sealed class ZlibWriter : IDisposable
{
    private readonly MemoryStream _output = new();
    private readonly ZLibStream _zlib;
    private long _taken;

    internal ZlibWriter() => _zlib = new ZLibStream(_output, CompressionLevel.Optimal, leaveOpen: true);

    internal byte[] Chunk(byte[] data)
    {
        _zlib.Write(data);
        _zlib.Flush();
        byte[] chunk = _output.ToArray()[(int)_taken..];
        _taken = _output.Length;
        return chunk;
    }

    public void Dispose() => _zlib.Dispose();
}

[TestClass]
public sealed class VncDecoderTests
{
    private const uint Red = 0xFF0000, Green = 0x00FF00, Blue = 0x0000FF, White = 0xFFFFFF, Black = 0;

    private static (VncFramebuffer Framebuffer, RectangleDecoder Decoder) Create(int width = 32, int height = 32, VncJpegDecoder? jpeg = null)
    {
        var framebuffer = new VncFramebuffer();
        framebuffer.Resize(width, height);
        return (framebuffer, new RectangleDecoder(framebuffer, jpeg));
    }

    private static async Task DecodeAsync(RectangleDecoder decoder, int encoding, int x, int y, int w, int h, byte[] body)
    {
        var reader = new RfbReader(new MemoryStream(body));
        await decoder.DecodeAsync(reader, encoding, x, y, w, h, CancellationToken.None);
        Assert.AreEqual(0, reader.Buffered, "the decoder consumed exactly the rectangle's bytes");
    }

    private static byte[] Px(uint rgb) => Pixel((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static byte[] Cpixel(uint rgb) => [(byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16)];
    private static byte[] Tpixel(uint rgb) => [(byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb];

    private static byte[] Compact(int length) => length < 0x80 ? [(byte)length]
        : length < 0x4000 ? [(byte)(length | 0x80), (byte)(length >> 7)]
        : [(byte)(length | 0x80), (byte)((length >> 7) | 0x80), (byte)(length >> 14)];

    [TestMethod]
    public async Task RawAndCopyRectWithOverlap()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create();
        byte[] raw = [.. Enumerable.Range(0, 4 * 2).SelectMany(i => Px((uint)(i * 0x010203)))];
        await DecodeAsync(decoder, VncEncoding.Raw, 1, 1, 4, 2, raw);
        Assert.AreEqual(0x000000u, fb.Pixel(1, 1));
        Assert.AreEqual(0x070E15u, fb.Pixel(4, 2));
        // Copy the block one pixel right and down onto itself: overlapping copies must not smear.
        await DecodeAsync(decoder, VncEncoding.CopyRect, 2, 2, 4, 2, [.. U16(1), .. U16(1)]);
        Assert.AreEqual(0x000000u, fb.Pixel(2, 2));
        Assert.AreEqual(0x030609u, fb.Pixel(5, 2));
        Assert.AreEqual(0x070E15u, fb.Pixel(5, 3));

        VncFrameCopy first = Copy(fb, out _);
        Assert.AreEqual(new VncRegion(0, 0, 32, 32), first.Changed, "the first copy after a resize is everything");
        await DecodeAsync(decoder, VncEncoding.Raw, 10, 20, 1, 1, Px(White));
        VncFrameCopy second = Copy(fb, out byte[] surface);
        Assert.AreEqual(new VncRegion(10, 20, 1, 1), second.Changed);
        Assert.AreEqual(255, surface[(20 * 32 + 10) * 4 + 3], "alpha is opaque");
    }

    private static VncFrameCopy Copy(VncFramebuffer fb, out byte[] surface)
    {
        surface = new byte[fb.Width * fb.Height * 4];
        return fb.CopyTo(surface, fb.Width * 4, fb.Width, fb.Height, full: false);
    }

    [TestMethod]
    public async Task RreFillsTheBackgroundThenSubrectangles()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create();
        byte[] body = [.. U32(2), .. Px(Blue), .. Px(Red), .. U16(1), .. U16(1), .. U16(2), .. U16(2), .. Px(Green), .. U16(0), .. U16(3), .. U16(4), .. U16(1)];
        await DecodeAsync(decoder, VncEncoding.Rre, 4, 4, 4, 4, body);
        Assert.AreEqual(Blue, fb.Pixel(4, 4));
        Assert.AreEqual(Red, fb.Pixel(5, 5));
        Assert.AreEqual(Red, fb.Pixel(6, 6));
        Assert.AreEqual(Green, fb.Pixel(7, 7));
        Assert.AreEqual(Black, fb.Pixel(8, 8), "outside the rectangle");
        _ = await Assert.ThrowsExactlyAsync<VncProtocolException>(() => DecodeAsync(decoder, VncEncoding.Rre, 0, 0, 2, 2,
            [.. U32(1), .. Px(Blue), .. Px(Red), .. U16(1), .. U16(1), .. U16(2), .. U16(2)]));
    }

    [TestMethod]
    public async Task HextileTilesKeepBackgroundAndForegroundAcrossTiles()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create(48, 20);
        byte[] body =
        [
            // tile (0,0) 16×16: background blue, foreground red, one subrect at (2,3) 4×5
            2 | 4 | 8, .. Px(Blue), .. Px(Red), 1, 0x23, 0x34,
            // tile (16,0): no flags — same background, nothing else
            0,
            // tile (32,0): coloured subrects
            8 | 16, 2, .. Px(Green), 0x00, 0x00, .. Px(White), 0xFF, 0x00,
            // second tile row, 4 px high: raw tile, then background only, then foreground subrect
            1, .. Enumerable.Range(0, 16 * 4).SelectMany(_ => Px(White)),
            2, .. Px(Black),
            8, 1, 0x11, 0x00
        ];
        await DecodeAsync(decoder, VncEncoding.Hextile, 0, 0, 48, 20, body);
        Assert.AreEqual(Blue, fb.Pixel(0, 0));
        Assert.AreEqual(Red, fb.Pixel(2, 3));
        Assert.AreEqual(Red, fb.Pixel(5, 7));
        Assert.AreEqual(Blue, fb.Pixel(6, 7));
        Assert.AreEqual(Blue, fb.Pixel(20, 10), "a flag-less tile reuses the previous background");
        Assert.AreEqual(Green, fb.Pixel(32, 0));
        Assert.AreEqual(White, fb.Pixel(47, 15));
        Assert.AreEqual(Blue, fb.Pixel(33, 1));
        Assert.AreEqual(White, fb.Pixel(15, 19));
        Assert.AreEqual(Black, fb.Pixel(16, 16));
        Assert.AreEqual(Red, fb.Pixel(33, 17), "the foreground carries over too");
    }

    [TestMethod]
    public async Task ZrleDecodesEverySubencodingAndKeepsOneStream()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create(80, 70);
        using var zlib = new ZlibWriter();
        // An 80×70 rectangle is four tiles: 64×64, 16×64, 64×6, 16×6.
        var tiles = new List<byte>();
        tiles.AddRange([1, .. Cpixel(Red)]);                                         // solid
        tiles.AddRange([2, .. Cpixel(Black), .. Cpixel(White)]);                     // 2-colour packed palette, 1 bit per pixel
        for (int row = 0; row < 64; row++) { tiles.AddRange([0b10101010, 0b10101010]); }
        tiles.Add(128);                                                              // plain RLE: 300 green then 84 blue
        tiles.AddRange([.. Cpixel(Green), 255, 44, .. Cpixel(Blue), 83]);
        tiles.Add(130 + 1);                                                          // palette RLE, 3 colours
        tiles.AddRange([.. Cpixel(Red), .. Cpixel(Green), .. Cpixel(Blue)]);
        tiles.AddRange([0x80 | 2, 47, 1]);                                          // 48 × blue, then one green, …
        for (int i = 0; i < 16 * 6 - 49; i++) { tiles.Add(0); }                     // … and red for the rest
        byte[] chunk = zlib.Chunk([.. tiles]);
        await DecodeAsync(decoder, VncEncoding.Zrle, 0, 0, 80, 70, [.. U32((uint)chunk.Length), .. chunk]);
        Assert.AreEqual(Red, fb.Pixel(10, 10));
        Assert.AreEqual(White, fb.Pixel(64, 0));
        Assert.AreEqual(Black, fb.Pixel(65, 0));
        Assert.AreEqual(Green, fb.Pixel(43, 68), "300 green pixels in a 64-wide tile end at row 4, column 43");
        Assert.AreEqual(Blue, fb.Pixel(44, 68));
        Assert.AreEqual(Blue, fb.Pixel(79, 66));
        Assert.AreEqual(Green, fb.Pixel(64, 67));
        Assert.AreEqual(Red, fb.Pixel(79, 69));

        // The next rectangle continues the same zlib stream (its dictionary).
        byte[] next = zlib.Chunk([0, .. Enumerable.Range(0, 4).SelectMany(_ => Cpixel(White))]);
        await DecodeAsync(decoder, VncEncoding.Zrle, 0, 0, 2, 2, [.. U32((uint)next.Length), .. next]);
        Assert.AreEqual(White, fb.Pixel(1, 1));
    }

    [TestMethod]
    public async Task ZrleRejectsAnOutOfRangePaletteIndex()
    {
        (_, RectangleDecoder decoder) = Create();
        using var zlib = new ZlibWriter();
        byte[] chunk = zlib.Chunk([3, .. Cpixel(Red), .. Cpixel(Green), .. Cpixel(Blue), 0b11000000, 0]);
        _ = await Assert.ThrowsExactlyAsync<VncProtocolException>(() => DecodeAsync(decoder, VncEncoding.Zrle, 0, 0, 2, 1, [.. U32((uint)chunk.Length), .. chunk]));
    }

    [TestMethod]
    public async Task TightFillCopyPaletteAndSmallUncompressedData()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create();
        await DecodeAsync(decoder, VncEncoding.Tight, 0, 0, 32, 32, [0x80, .. Tpixel(Green)]);
        Assert.AreEqual(Green, fb.Pixel(31, 31));

        using var stream0 = new ZlibWriter();
        byte[] copy = [.. Enumerable.Range(0, 8 * 2).SelectMany(i => Tpixel((uint)(i << 16 | i)))];
        byte[] compressed = stream0.Chunk(copy);
        await DecodeAsync(decoder, VncEncoding.Tight, 0, 0, 8, 2, [0x00, .. Compact(compressed.Length), .. compressed]);
        Assert.AreEqual(0x0F000Fu, fb.Pixel(7, 1));

        // Fewer than 12 bytes travel uncompressed: a 3×1 copy is 9 bytes.
        await DecodeAsync(decoder, VncEncoding.Tight, 10, 10, 3, 1, [0x00, .. Tpixel(Red), .. Tpixel(Green), .. Tpixel(Blue)]);
        Assert.AreEqual(Blue, fb.Pixel(12, 10));

        // Two-colour palette, stream 1, explicit filter: 1 bit per pixel, rows padded to a byte (10 px → 2 bytes per row).
        byte[] bits = [0b1010_0000, 0b0100_0000, 0b0000_0000, 0b0000_0000, 0b1111_1111, 0b1100_0000];
        await DecodeAsync(decoder, VncEncoding.Tight, 0, 20, 10, 3, [0x40 | 0x10, 1, 1, .. Tpixel(Black), .. Tpixel(White), .. bits]);
        Assert.AreEqual(White, fb.Pixel(0, 20));
        Assert.AreEqual(Black, fb.Pixel(1, 20));
        Assert.AreEqual(White, fb.Pixel(9, 20));
        Assert.AreEqual(Black, fb.Pixel(9, 21));
        Assert.AreEqual(White, fb.Pixel(9, 22));

        // Four-colour palette: one byte per pixel, compressed on stream 2.
        using var stream2 = new ZlibWriter();
        byte[] indices = [.. Enumerable.Range(0, 16).Select(static i => (byte)(i % 4))];
        byte[] packed = stream2.Chunk(indices);
        await DecodeAsync(decoder, VncEncoding.Tight, 16, 0, 16, 1, [0x40 | 0x20, 1, 3, .. Tpixel(Red), .. Tpixel(Green), .. Tpixel(Blue), .. Tpixel(White), .. Compact(packed.Length), .. packed]);
        Assert.AreEqual(Red, fb.Pixel(16, 0));
        Assert.AreEqual(White, fb.Pixel(31, 0));
    }

    [TestMethod]
    public async Task TightGradientFilterReconstructsPixels()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create();
        const int w = 6, h = 5;
        uint[] expected = [.. Enumerable.Range(0, w * h).Select(static i => (uint)((i * 37 % 256) << 16 | (i * 11 % 256) << 8 | (255 - i * 7)))];
        // Forward transform: send each channel minus its prediction (left + up − up-left, clamped).
        byte[] filtered = new byte[w * h * 3];
        int Channel(int x, int y, int c) => x < 0 || y < 0 ? 0 : (int)(expected[y * w + x] >> (16 - 8 * c)) & 0xFF;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int predicted = Math.Clamp(Channel(x - 1, y, c) + Channel(x, y - 1, c) - Channel(x - 1, y - 1, c), 0, 255);
                    filtered[(y * w + x) * 3 + c] = (byte)(Channel(x, y, c) - predicted);
                }
            }
        }
        using var stream3 = new ZlibWriter();
        byte[] compressed = stream3.Chunk(filtered);
        await DecodeAsync(decoder, VncEncoding.Tight, 2, 2, w, h, [0x40 | 0x30, 2, .. Compact(compressed.Length), .. compressed]);
        for (int i = 0; i < w * h; i++) { Assert.AreEqual(expected[i], fb.Pixel(2 + i % w, 2 + i / w), $"pixel {i}"); }
    }

    [TestMethod]
    public async Task TightResetBitStartsAFreshStream()
    {
        (VncFramebuffer fb, RectangleDecoder decoder) = Create();
        byte[] data = [.. Enumerable.Range(0, 16).SelectMany(_ => Tpixel(Red))];
        using (var first = new ZlibWriter())
        {
            byte[] c = first.Chunk(data);
            await DecodeAsync(decoder, VncEncoding.Tight, 0, 0, 16, 1, [0x00, .. Compact(c.Length), .. c]);
        }
        using var second = new ZlibWriter();   // a brand-new stream: decodable only after the reset bit
        byte[] fresh = second.Chunk([.. Enumerable.Range(0, 16).SelectMany(_ => Tpixel(Blue))]);
        await DecodeAsync(decoder, VncEncoding.Tight, 0, 1, 16, 1, [0x01, .. Compact(fresh.Length), .. fresh]);
        Assert.AreEqual(Blue, fb.Pixel(15, 1));
    }

    [TestMethod]
    public async Task TightJpegGoesThroughTheInjectedDecoder()
    {
        byte[]? seen = null;
        VncDecodedImage Decode(ReadOnlySpan<byte> jpeg)
        {
            seen = jpeg.ToArray();
            return new VncDecodedImage(2, 2, [.. Enumerable.Range(0, 4).SelectMany(_ => Px(Green))]);
        }
        (VncFramebuffer fb, RectangleDecoder decoder) = Create(jpeg: Decode);
        await DecodeAsync(decoder, VncEncoding.Tight, 3, 3, 2, 2, [0x90, 4, 0xFF, 0xD8, 0xFF, 0xD9]);
        Assert.AreSequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, seen!);
        Assert.AreEqual(Green, fb.Pixel(4, 4));

        (_, RectangleDecoder without) = Create();
        _ = await Assert.ThrowsExactlyAsync<VncProtocolException>(() => DecodeAsync(without, VncEncoding.Tight, 0, 0, 2, 2, [0x90, 1, 0]));
    }

    [TestMethod]
    public async Task RectanglesOutsideTheDesktopAreRefused()
    {
        (_, RectangleDecoder decoder) = Create(8, 8);
        _ = await Assert.ThrowsExactlyAsync<VncProtocolException>(() => DecodeAsync(decoder, VncEncoding.Raw, 6, 6, 4, 4, new byte[64]));
        _ = await Assert.ThrowsExactlyAsync<VncProtocolException>(() => DecodeAsync(decoder, VncEncoding.CopyRect, 0, 0, 4, 4, [.. U16(6), .. U16(0)]));
        var fb = new VncFramebuffer();
        _ = Assert.ThrowsExactly<VncProtocolException>(() => fb.Resize(70000, 10));
    }

    [TestMethod]
    public async Task CursorMaskBecomesAlpha()
    {
        byte[] pixels = [.. Px(Red), .. Px(Green), .. Px(Blue), .. Px(White)];
        byte[] mask = [0b1000_0000, 0b0100_0000];
        VncPointer pointer = await RectangleDecoder.CursorAsync(new RfbReader(new MemoryStream([.. pixels, .. mask])), 1, 5, 2, 2, CancellationToken.None);
        Assert.AreEqual(2, pointer.Width);
        Assert.AreEqual(1, pointer.HotspotY, "a hot spot outside the image is clamped");
        Assert.AreEqual(255, pointer.Bgra[3]);
        Assert.AreEqual(0, pointer.Bgra[7]);
        Assert.AreEqual(0, pointer.Bgra[11]);
        Assert.AreEqual(255, pointer.Bgra[15]);
        VncPointer hidden = await RectangleDecoder.CursorAsync(new RfbReader(new MemoryStream([])), 0, 0, 0, 0, CancellationToken.None);
        Assert.IsTrue(hidden.IsHidden);
    }

    [TestMethod]
    public void ResizeKeepsTheOverlappingPixels()
    {
        var fb = new VncFramebuffer();
        fb.Resize(4, 4);
        fb.Fill(0, 0, 4, 4, Red);
        fb.Resize(6, 2);
        Assert.AreEqual(Red, fb.Pixel(3, 1));
        Assert.AreEqual(Black, fb.Pixel(5, 1));
        byte[] small = new byte[4 * 4 * 4];
        Assert.IsTrue(fb.CopyTo(small, 16, 4, 4, false).SizeChanged);
    }
}
