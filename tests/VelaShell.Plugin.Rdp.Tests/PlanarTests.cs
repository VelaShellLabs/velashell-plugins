using System.IO.Compression;
using System.Security.Cryptography;
using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The RDP 6.0 planar decoder (<see cref="Planar"/>): reference sample streams, hand-built raw /
/// RLE / YCoCg / chroma-subsampled streams, and hostile input that must only ever surface as
/// <see cref="RdpProtocolException"/>.
/// </summary>
[TestClass]
public sealed class PlanarTests
{
    private const byte Rle = 0x10, NoAlpha = 0x20, ChromaSubsampling = 0x08;

    // ── Reference sample vectors ──────────────────────────────────────────────────────────────

    /// <summary>
    /// FreeRDP 3.32.2 <c>libfreerdp/codec/test/planar</c>: <c>planar-enc-*</c> is FreeRDP's encoder
    /// output (RLE | NoAlpha, planes split bottom-up from an RGBX32 / RGB16 source) and the expected
    /// image is the matching <c>planar-bmp-*</c> source converted to top-down BGRA. FreeRDP's own
    /// <c>planar-dec-*</c> outputs (decoded with vFlip = FALSE, i.e. upside down) flipped back agree
    /// row for row. <c>Legacy03</c> is the FreeRDP 2.x <c>TEST_RLE_BITMAP_EXPERIMENTAL_03_RLE</c>
    /// stream, whose expected image is built from the <c>TEST_64X64_{RED,GREEN,BLUE}_PLANE</c> planes
    /// with plane row 0 at the bottom.
    /// </summary>
    [TestMethod]
    [DataRow(nameof(Vectors.Experimental01), 64, 64, "161A1BFF", "111517FF")]
    [DataRow(nameof(Vectors.Experimental02), 64, 64, "171C1CFF", "161A1BFF")]
    [DataRow(nameof(Vectors.Experimental03), 64, 64, "232A27FF", "1E2225FF")]
    [DataRow(nameof(Vectors.Uncompressed16Bpp), 32, 32, "FFFFFFFF", "FFFFFFFF")]
    [DataRow(nameof(Vectors.Legacy03), 64, 64, "25221EFF", "272A23FF")]
    public void ReferenceVectorsDecodeToTheSourceBitmapTopDown(string name, int width, int height, string topLeft, string bottomLeft)
    {
        (byte[] stream, string sha256) = Vectors.Get(name);
        byte[] dst = new byte[width * height * 4];

        Planar.Decompress(stream, dst, width, height);

        Assert.AreEqual(topLeft, Convert.ToHexString(dst, 0, 4), "Top-left pixel (BGRA).");
        Assert.AreEqual(bottomLeft, Convert.ToHexString(dst, (height - 1) * width * 4, 4), "Bottom-left pixel (BGRA).");
        Assert.AreEqual(sha256, Convert.ToHexString(SHA256.HashData(dst)), "Whole-image SHA-256.");
    }

    [TestMethod]
    public void EveryTruncationOfAnRleVectorIsRejected()
    {
        (byte[] stream, _) = Vectors.Get(nameof(Vectors.Uncompressed16Bpp));
        byte[] dst = new byte[32 * 32 * 4];
        for (int length = 0; length < stream.Length; length++)
        {
            byte[] prefix = stream[..length];
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(prefix, dst, 32, 32), $"Prefix of {length} bytes.");
        }
    }

    [TestMethod]
    public void TruncationsOfALargerRleVectorAreRejected()
    {
        (byte[] stream, _) = Vectors.Get(nameof(Vectors.Experimental03));
        byte[] dst = new byte[64 * 64 * 4];
        for (int length = 0; length < stream.Length; length += 37)
        {
            byte[] prefix = stream[..length];
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(prefix, dst, 64, 64), $"Prefix of {length} bytes.");
        }

        byte[] lastByteMissing = stream[..^1];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(lastByteMissing, dst, 64, 64));
    }

    // ── Hand-built raw planes ────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void NoAlphaRawPlanesAreFlippedTopDownWithOpaqueAlpha(bool withPad)
    {
        // 3x2, planes in wire order (plane row 0 = bottom image row): R, G, B, optional pad.
        byte[] stream =
        [
            NoAlpha,
            1, 2, 3, 4, 5, 6,         // red
            11, 12, 13, 14, 15, 16,   // green
            21, 22, 23, 24, 25, 26,   // blue
            .. (withPad ? new byte[] { 0 } : [])
        ];
        byte[] dst = new byte[3 * 2 * 4];

        Planar.Decompress(stream, dst, 3, 2);

        byte[] expected =
        [
            24, 14, 4, 0xFF, 25, 15, 5, 0xFF, 26, 16, 6, 0xFF,   // top row = plane row 1
            21, 11, 1, 0xFF, 22, 12, 2, 0xFF, 23, 13, 3, 0xFF,   // bottom row = plane row 0
        ];
        Assert.AreSequenceEqual(expected, dst);
    }

    [TestMethod]
    public void RawAlphaPlaneIsKept()
    {
        byte[] stream = [0x00, /* A */ 0x00, 0x80, /* R */ 1, 2, /* G */ 3, 4, /* B */ 5, 6, /* pad */ 0];
        byte[] dst = new byte[2 * 4];

        Planar.Decompress(stream, dst, 2, 1);

        Assert.AreSequenceEqual(new byte[] { 5, 3, 1, 0x00, 6, 4, 2, 0x80 }, dst);
    }

    [TestMethod]
    public void OnlyTheImageAreaOfALargerDestinationIsWritten()
    {
        byte[] stream = [NoAlpha, 7, 8, 9];
        byte[] dst = new byte[16];
        dst.AsSpan().Fill(0xA5);

        Planar.Decompress(stream, dst, 1, 1);

        byte[] expected = [9, 8, 7, 0xFF, .. Repeat(0xA5, 12)];
        Assert.AreSequenceEqual(expected, dst);
    }

    // ── Hand-built RLE planes ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void RleEscapesRunsAndDeltasDecode()
    {
        // 60x2 with an alpha plane. Each plane: first scanline absolute, second delta-coded.
        byte[] red =
        [
            0x13, 0x50,              // 1 raw (0x50) + run 3
            0x21,                    // run escape 1: 2 + 16 = 18 more of 0x50
            0x02,                    // run escape 2: 0 + 32 = 32 more of 0x50
            0x60, 1, 2, 3, 4, 5, 6,  // 6 raw
            0x01,                    // run 16, delta 0 (copy previous scanline)
            0x23, 0x03, 0x04,        // 2 raw deltas (-2, +2) + run 3 of +2
            0x0F,                    // run 15 of +2
            0x51,                    // run escape 1: 5 + 16 = 21 of +2
            0x30, 0xFF, 0x00, 0xFE,  // 3 raw deltas (-128, 0, +127), wrapping modulo 256
        ];
        byte[] green =
        [
            0x02,                    // run 32 at scanline start: zeros
            0x1F, 0x10,              // 1 raw (0x10) + run 15
            0x0C,                    // run 12 of 0x10
            0x0F,                    // run 15 copying the previous scanline
            0x1F, 0x06,              // 1 raw delta (+3) + run 15 of +3
            0x0F,                    // run 15 of +3
            0x0E,                    // run 14 of +3
        ];
        byte[] alpha =
        [
            0x1F, 0xC0, 0x52, 0x07,        // 0xC0 x 60
            0x13, 0x41, 0x82, 0x01,        // delta -33 x 60
        ];
        byte[] stream = [Rle, .. alpha, .. red, .. green, .. red];   // blue reuses the red plane
        byte[] dst = new byte[60 * 2 * 4];

        Planar.Decompress(stream, dst, 60, 2);

        byte[] red0 = [.. Repeat(0x50, 54), 1, 2, 3, 4, 5, 6];
        byte[] red1 = [.. Repeat(0x50, 16), 0x4E, .. Repeat(0x52, 37), 3, 4, 5, 0x84, 5, 0x85];
        byte[] green0 = [.. Repeat(0x00, 32), .. Repeat(0x10, 28)];
        byte[] green1 = [.. Repeat(0x00, 15), .. Repeat(0x03, 17), .. Repeat(0x13, 28)];
        byte[] expected =
        [
            .. Bgra(red1, green1, red1, Repeat(0x9F, 60)),   // top row = plane row 1
            .. Bgra(red0, green0, red0, Repeat(0xC0, 60)),   // bottom row = plane row 0
        ];
        Assert.AreSequenceEqual(expected, dst);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RandomImagesRoundTripThroughRawAndRlePlanes(bool rle)
    {
        const int width = 37, height = 23;
        var random = new Random(rle ? 1337 : 4242);
        byte[] a = new byte[width * height], r = new byte[width * height], g = new byte[width * height], b = new byte[width * height];
        random.NextBytes(a);
        random.NextBytes(r);
        random.NextBytes(g);
        random.NextBytes(b);

        byte[] stream = rle
            ? [Rle, .. EncodeRlePlane(a, width, height), .. EncodeRlePlane(r, width, height), .. EncodeRlePlane(g, width, height), .. EncodeRlePlane(b, width, height)]
            : [0x00, .. a, .. r, .. g, .. b, 0];
        byte[] dst = new byte[width * height * 4];

        Planar.Decompress(stream, dst, width, height);

        byte[] expected = new byte[width * height * 4];
        for (int planeY = 0; planeY < height; planeY++)
        {
            int y = height - 1 - planeY;
            for (int x = 0; x < width; x++)
            {
                int i = planeY * width + x, d = (y * width + x) * 4;
                expected[d] = b[i];
                expected[d + 1] = g[i];
                expected[d + 2] = r[i];
                expected[d + 3] = a[i];
            }
        }
        Assert.AreSequenceEqual(expected, dst);
    }

    // ── YCoCg (color loss levels) and chroma subsampling ─────────────────────────────────────

    /// <summary>
    /// Expected values follow the MS-RDPEGDI YCoCg-to-RGB conversion: Co/Cg are shifted left by
    /// CLL - 1, truncated to a signed byte, then T = Y - Cg, R = T - Co, G = Y + Cg, B = T + Co,
    /// each clipped to 0..255.
    /// </summary>
    [TestMethod]
    // CLL 1: (Y 100, Co +16, Cg -8) -> R 92 G 92 B 124; (Y 200, Co -16, Cg +127) -> R 89 G 327→255 B 57.
    [DataRow(new byte[] { NoAlpha | 1, 100, 200, 0x10, 0xF0, 0xF8, 0x7F, 0 }, 2, new byte[] { 124, 92, 92, 0xFF, 57, 255, 89, 0xFF })]
    // CLL 3 (shift 2): Co 0x30 -> 0xC0 = -64, Cg 0x05 -> 20; Co 0x50 -> 0x140 truncates to +64.
    [DataRow(new byte[] { NoAlpha | 3, 50, 10, 0x30, 0x50, 0x05, 0x00, 0 }, 2, new byte[] { 0, 70, 94, 0xFF, 74, 10, 0, 0xFF })]
    // CLL 7 (shift 6): Co 0x01 -> +64, Cg 0x03 -> 0xC0 = -64; B = 192 + 64 clips to 255.
    [DataRow(new byte[] { NoAlpha | 7, 128, 0x01, 0x03 }, 1, new byte[] { 255, 64, 128, 0xFF })]
    // Alpha plane present with YCoCg: alpha is kept.
    [DataRow(new byte[] { 0x01, 0x42, 100, 0, 0 }, 1, new byte[] { 100, 100, 100, 0x42 })]
    public void ColorLossLevelsConvertYCoCg(byte[] stream, int width, byte[] expected)
    {
        byte[] dst = new byte[width * 4];

        Planar.Decompress(stream, dst, width, 1);

        Assert.AreSequenceEqual(expected, dst);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChromaSubsamplingExpandsEachSampleOverATwoByTwoBlockAnchoredAtTheBottom(bool rle)
    {
        // 3x3 at CLL 1: Y = 100 everywhere, Cg = 0, Co samples 10/20 (plane row 0) and 30/40
        // (plane row 1) on a 2x2 chroma grid, so B = 100 + Co, G = 100, R = 100 - Co.
        const int width = 3, height = 3;
        byte[] alpha = [1, 2, 3, 4, 5, 6, 7, 8, 9];
        byte[] luma = [.. Repeat(100, 9)];
        byte[] co = [10, 20, 30, 40];
        byte[] cg = [0, 0, 0, 0];
        byte[] stream = rle
            ? [Rle | ChromaSubsampling | 1, .. EncodeRlePlane(alpha, 3, 3), .. EncodeRlePlane(luma, 3, 3), .. EncodeRlePlane(co, 2, 2), .. EncodeRlePlane(cg, 2, 2)]
            : [NoAlpha | ChromaSubsampling | 1, .. luma, .. co, .. cg, 0];
        byte[] dst = new byte[width * height * 4];

        Planar.Decompress(stream, dst, width, height);

        byte[] expected =
        [
            130, 100, 70, 0, 130, 100, 70, 0, 140, 100, 60, 0,   // top: plane row 2 → chroma row 1
            110, 100, 90, 0, 110, 100, 90, 0, 120, 100, 80, 0,   // plane row 1 → chroma row 0
            110, 100, 90, 0, 110, 100, 90, 0, 120, 100, 80, 0,   // bottom: plane row 0 → chroma row 0
        ];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                expected[((y * width) + x) * 4 + 3] = rle ? alpha[(height - 1 - y) * width + x] : (byte)0xFF;
            }
        }
        Assert.AreSequenceEqual(expected, dst);
    }

    [TestMethod]
    public void TruncatedChromaSubsampledRawPlanesAreRejected()
    {
        // 3x3 with CS needs 1 + 9 + 4 + 4 bytes.
        byte[] stream = [NoAlpha | ChromaSubsampling | 1, .. new byte[9 + 4 + 3]];
        byte[] dst = new byte[3 * 3 * 4];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(stream, dst, 3, 3));

        byte[] enough = [.. stream, 0];
        Planar.Decompress(enough, dst, 3, 3);
    }

    // ── Hostile input ────────────────────────────────────────────────────────────────────────

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 0)]
    [DataRow(-1, 4)]
    [DataRow(4, -1)]
    [DataRow(int.MinValue, int.MinValue)]
    public void NonPositiveDimensionsAreRejected(int width, int height)
    {
        byte[] stream = [NoAlpha, 1, 2, 3];
        byte[] dst = new byte[64];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(stream, dst, width, height));
    }

    [TestMethod]
    [DataRow(2, 2, 15)]
    [DataRow(65536, 65536, 64)]
    [DataRow(int.MaxValue, int.MaxValue, 64)]
    [DataRow(int.MaxValue, 1, 64)]
    public void DestinationsTooSmallForTheImageAreRejectedWithoutOverflow(int width, int height, int dstLength)
    {
        byte[] stream = [NoAlpha, .. new byte[3 * 4]];
        byte[] dst = new byte[dstLength];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(stream, dst, width, height));
    }

    [TestMethod]
    public void EmptyStreamIsRejected()
    {
        byte[] dst = new byte[4];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress([], dst, 1, 1));
    }

    [TestMethod]
    [DataRow((byte)0x40)]
    [DataRow((byte)0x80)]
    [DataRow((byte)(0xC0 | NoAlpha))]
    [DataRow(ChromaSubsampling)]                    // CS without YCoCg
    [DataRow((byte)(ChromaSubsampling | NoAlpha))]
    [DataRow((byte)(ChromaSubsampling | NoAlpha | Rle))]
    public void InvalidFormatHeadersAreRejected(byte formatHeader)
    {
        byte[] stream = [formatHeader, .. new byte[64]];
        byte[] dst = new byte[2 * 2 * 4];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(stream, dst, 2, 2));
    }

    [TestMethod]
    [DataRow(new byte[] { NoAlpha, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })]   // 3 raw planes need 12 bytes
    [DataRow(new byte[] { 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 })] // 4 raw planes need 16
    [DataRow(new byte[] { NoAlpha | Rle, 0x05 })]                           // run 5 overruns a 4-pixel scanline
    [DataRow(new byte[] { NoAlpha | Rle, 0x31 })]                           // escaped run 19 overruns it
    [DataRow(new byte[] { NoAlpha | Rle, 0x40, 1, 2, 3 })]                  // 4 raw bytes announced, 3 present
    [DataRow(new byte[] { NoAlpha | Rle, 0xF0, 1, 2, 3, 4 })]               // 15 raw bytes overrun the scanline
    [DataRow(new byte[] { NoAlpha | Rle, 0x00, 0x00, 0x00 })]               // zero-length segments until the data runs out
    public void MalformedPlanesAreRejected(byte[] stream)
    {
        byte[] dst = new byte[4 * 1 * 4];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => Planar.Decompress(stream, dst, 4, 1));
    }

    [TestMethod]
    public void CorruptedStreamsOnlyEverThrowRdpProtocolException()
    {
        var random = new Random(0x504C4E52);
        byte[][] seeds =
        [
            Vectors.Get(nameof(Vectors.Experimental03)).Stream,
            Vectors.Get(nameof(Vectors.Uncompressed16Bpp)).Stream,
            [0x00, .. new byte[4 * 9 * 9], 0],
            [NoAlpha | ChromaSubsampling | 2, .. new byte[(9 * 9) + (2 * 5 * 5)], 0],
            [Rle | ChromaSubsampling | 1, .. EncodeRlePlane(new byte[81], 9, 9), .. EncodeRlePlane(new byte[81], 9, 9),
                .. EncodeRlePlane(new byte[25], 5, 5), .. EncodeRlePlane(new byte[25], 5, 5)],
        ];

        for (int iteration = 0; iteration < 3000; iteration++)
        {
            byte[] seed = seeds[random.Next(seeds.Length)];
            int length = random.Next(4) == 0 ? random.Next(seed.Length + 64) : seed.Length;
            byte[] stream = new byte[length];
            random.NextBytes(stream);
            seed.AsSpan(0, Math.Min(length, seed.Length)).CopyTo(stream);
            if (length > 0)
            {
                int mutations = random.Next(0, 9);
                for (int m = 0; m < mutations; m++)
                {
                    stream[random.Next(length)] = (byte)random.Next(256);
                }

                if (random.Next(3) == 0)
                {
                    stream[0] = (byte)random.Next(256);
                }
            }

            int width = random.Next(1, 70), height = random.Next(1, 70);
            int needed = width * height * 4;
            byte[] dst = new byte[needed + 16];
            dst.AsSpan(needed).Fill(0xA5);

            try
            {
                Planar.Decompress(stream, dst, width, height);
            }
            catch (RdpProtocolException)
            {
                // Expected for most corruptions.
            }
            catch (Exception ex)
            {
                Assert.Fail($"Iteration {iteration} ({width}x{height}, {length} bytes, header 0x{(length > 0 ? stream[0] : 0):X2}) threw {ex.GetType().Name}: {ex.Message}");
            }

            Assert.AreEqual(-1, dst.AsSpan(needed).IndexOfAnyExcept((byte)0xA5), $"Iteration {iteration} wrote past the image.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static byte[] Repeat(int value, int count) => [.. Enumerable.Repeat((byte)value, count)];

    private static byte[] Bgra(byte[] r, byte[] g, byte[] b, byte[] a)
    {
        byte[] pixels = new byte[r.Length * 4];
        for (int i = 0; i < r.Length; i++)
        {
            pixels[i * 4] = b[i];
            pixels[i * 4 + 1] = g[i];
            pixels[i * 4 + 2] = r[i];
            pixels[i * 4 + 3] = a[i];
        }
        return pixels;
    }

    /// <summary>
    /// A deliberately simple RLE plane encoder: delta-codes every scanline after the first
    /// (MS-RDPEGDI delta encoding), then emits only raw segments
    /// (control byte <c>n &lt;&lt; 4</c>, up to 15 values each, never a run).
    /// </summary>
    private static byte[] EncodeRlePlane(byte[] plane, int width, int height)
    {
        var output = new List<byte>();
        for (int y = 0; y < height; y++)
        {
            byte[] values = new byte[width];
            for (int x = 0; x < width; x++)
            {
                int current = plane[y * width + x];
                if (y == 0)
                {
                    values[x] = (byte)current;
                    continue;
                }

                int delta = unchecked((sbyte)(current - plane[(y - 1) * width + x]));
                values[x] = (byte)(delta >= 0 ? delta << 1 : ((-delta) << 1) - 1);
            }

            for (int x = 0; x < width; x += 15)
            {
                int count = Math.Min(15, width - x);
                output.Add((byte)(count << 4));
                output.AddRange(values.AsSpan(x, count));
            }
        }
        return [.. output];
    }

    /// <summary>
    /// FreeRDP sample streams (Apache-2.0, from FreeRDP 3.32.2 <c>libfreerdp/codec/test/planar</c>),
    /// stored Brotli-compressed and Base64-encoded to keep this file small.
    /// </summary>
    private static class Vectors
    {
        internal static (byte[] Stream, string Sha256) Get(string name)
        {
            (string data, int length, string sha256) = name switch
            {
                nameof(Experimental01) => (Experimental01, Experimental01Length, Experimental01Sha256),
                nameof(Experimental02) => (Experimental02, Experimental02Length, Experimental02Sha256),
                nameof(Experimental03) => (Experimental03, Experimental03Length, Experimental03Sha256),
                nameof(Uncompressed16Bpp) => (Uncompressed16Bpp, Uncompressed16BppLength, Uncompressed16BppSha256),
                nameof(Legacy03) => (Legacy03, Legacy03Length, Legacy03Sha256),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown vector."),
            };

            using var compressed = new MemoryStream(Convert.FromBase64String(data));
            using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            brotli.CopyTo(plain);
            byte[] stream = plain.ToArray();
            Assert.HasCount(length, stream, $"Vector {name} length.");
            return (stream, sha256);
        }

        /// <summary>SHA-256 of the expected top-down BGRA output of <see cref="Experimental01"/>.</summary>
        internal const string Experimental01Sha256 = "DF7116685C7088E60BF8F73DB5F85DE5F92686F085D17976966A59B4B9425147";
        internal const int Experimental01Length = 13113;

        internal const string Experimental01 = """
            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
            """;

        /// <summary>SHA-256 of the expected top-down BGRA output of <see cref="Experimental02"/>.</summary>
        internal const string Experimental02Sha256 = "108D23CBFFA1FBD963D076D68B67926C25E6335B4E18C48C2B3842CEB0C010A8";
        internal const int Experimental02Length = 13124;

        internal const string Experimental02 = """
            G0MzJaoTp/uEQp1iohusxr0TQFU9JDfGwB5EC3ItSKRv/U3/z6yz5mb9NzQqTDJvsfBJahURjSgQtci4EBbLLq+qkGBSORTTmzRs
            m0Xeb3TyTn2iSvyWxLpk7w53NbgYBIaazLH5oXcdBKKMkGQWqv+baO92534FFGCCOOpaUObEyWUYSgghlFKGYXkjVvnX5p1+p3qf
            W++Zc7DAwArDwufxo2KViLZq2nnTpspGyli2//7Rrqmpra3Xbo2PVtc/Qg3ISkZGNjNyvoAE8QAYAANIGIj46P/7q1LS1Ve9elNT
            W9szS21g+gbgBQp9NjIAR46Qx20ADCEkyq+Bluwk9QV4yLn1MM+x+KX1VnV+YUbS7el0+9ch8g+gHGLGJCFoA2ZEemhcPqBRpcGq
            W4qMABKUXuJNvrftczJks1RVbr2p3sSOojcxo7ygVbM/QD5l/f+mNfv/bG53ktt7b+bqXCWhzi4tR9PLKeRJUG7ooWdDC6jSQt91
            lKblZGlt6VlKaQ6PcSiEciBk6cI5bBxRU6VyeIt4+O9k75pai9LQhiNn+7Xpk3E07B5V1QYFjdjNufEyVlWBEr5SE/DKm/Rb9tvi
            jS+m/uPcv8e3APFBxuKGZbR5tV6VUoVVMa9DTK+qttbKzXkesDturPhbEO5QM+p2UmBu3KkjuqzXi/eFtKTWq6z4usupN/laNPaX
            ZO5uv2DH5IPCrSo4nRJrfviXuHiBlpKqDUDr/0BjOofS677eL4l7gwQnN/4M9ipbg+1raC1TQQG/2xcJ7UeglljfUJErOk+m6t5z
            fXua1XLapaJrirQqVG3yN2Y9DaEMFVF3fdq90Ri6tGCxKC6Ek0asCBkYEdXwBBCSJYu927nGLgUkEFeDaQUTDnXDpxkjDLabzaaz
            6qK1KSVqoaTBCEePkVVtVJyrzTvKGC5Da2y1AUaxymeeKO0tAgQNIDULTC+Q6fbYQYSl0w+3WqJsaMiswwK8iCsh9BPy5WI5+nez
            c1culBIojNg/cc4VTXtPVyu9wwbkBIQxB16HKLH8apiwxX52jlQgEHg+bQGMQAWvIDVwJar9jAj8Xh/P91YzNmQnJbctUtbjLi4e
            Pn78nheOZxFbR0Vd9avfGT9/TuciTjjs6nOc22E8+wTtVu30A69NadOsRY//XgPacimUAposIHEI4mzQZSN3nC6gNYAI24apNUQ6
            IfAFi1MT9KdPaGODskphIPJooJCw4SOS9F4iMrBUlw1AGqZTEKhBAnjWoPbOk5SEEkChhTANTmoCRBvET0VQKJPeUFMCcjBQsGdu
            SL/ePnusdX+8fvH3G7k7rimgeVJBGtPngjhfvw5Ex0kn0wuKlHpKQlpgFXbu64e/PBLm+bnnj3qc8wPMyU9uDn6votMtMbdPpIDd
            09L59um5htIJ1eGQ/xOmqq30TcekJ1oo+rY9KnFUe239Ww+w940QA6qiSn0F9SlSRQh1kYcyEbsNXZINtKdpp74sz31lgMRChcIl
            Nhy6e0v21VogFVrF8JKRstR9ouVvibhem2tt9jQCXaW1LMDGKlxPYKIoFlZdihZQu94B+I1GSiblgpx17+3qORCiLR1KnVOnnQ6o
            TRFDyzvvNyLRVH7y8dAzh5naukBKaBySw/A4YA6VWgsV2aJAo2OV6zSmNASuhNCKBsbM/tY0hpSgE64tVufDAY/TUAG7kYzjS593
            HaJyI2HY2Q0oHvY6BDcQBxJu9EwtxUdJCUEy9jlNJhqd/U61KZI3eDpoi6DXxALzC1hVC70uhfTj4l/vzoUHj9uGEt/g6gv+e6bl
            +MsBdgIbFIAMecgzzUWSNRIA1uoxqp+e+n89ZdvTm3vVLAlq//BWWKWWKwWud88sFXdkT5PQjBb0iLzP4JOubh5+vn68cDCYxB2E
            pIx+V81L9k3nzBP3iuW+B92T0JMz0R1jfj/O7ElRm049fDVej1RDAq+ZWa7bB6AmbAPMDIEBU40O3QZBEn4S2mIlMp1ciCo8HYgW
            7CwuBgIgWhWiNJiYQ71dHUEYYDBBsy5qCxBsh+PpoavBFYBA4ZpAYxNmzFI6YC0KV5hK6KEmLQQcOBkuFDzpxb6NdX85bo/72+/F
            WI2F8Azz0yDMVuapdNgEdmpzISqYQEwYnmGrWsFZUbOuf0uXHz834x/NcX+OPbla/JHQ50c6xZv26ZFqzunht3M+Ny3LApU4vmaB
            UVOgSneOhAyRlroulhO7GyxY3/Jr3U300YiySVzRmjeWxTx735cfBUTbzTr/m+bm2rbz926meffSSiS37PSNZz6GtBXh130tylWF
            nlWFK7qliALJ+u37Bo6BtVC137i2tcPNSeHRFT1TC3KA0iUV03uTTfBLpVVNZXU60+89Tc95r3XfzErn9cjUSYCsY+W0fdKXoUUE
            Pzk1RGWgLMhErNG7aIgb2cEityKUvMvMslT1hqCwYTQwdruVwtIF7RJ53ltQYRQK5Vy7Lc7GbMX+5wSoLF7m/q6m322ieJmKnTHN
            DYjDvodcklX3YIydTLYaF1rZpzQN9rIZQZQ6fReANqrEkOfpMuMB/mcYmOgjWSVAt72UU1rHp6WTX3XuRsASdY2mZKsHUBZixKPw
            wcJGM2MQfSOc3mC75em7actIBZ+aCjzun95uiEQa7b0AAoNRoN6LgjWecnXx9PTy5n283F0cKm0Kxma9ABG37JVOpqZd52aqk4h0
            GrmfMYOe2VNatenQrUMPr8fvexOMBiAQVVFABf+AQAWdoGoZV4mJHu0G8SJNpwvknwC2E6jshKAsDBSI+3+URiCw0Z7mklTuQIIN
            hyiGQUKM668QItEI0kkhJgi0Finl0EmC1hECBoaKIcgGCQiRajgVzwDqf5fHcVwej9fb6y/RTnWW6FEiopdOIzxI01JQGsB2gHPg
            AP1TiPcsi7nPWcn703+qOfeMRT/dmsS65kh1wskj+J7QHwk8yz35mvRVusf+tceN1C1Sai9MN5HrNtGiFinzlmQ6xhKB9V1O3WBT
            ys/Tg4SXTI7RwYvddeXMEJkH7cVbu+PAzk+DP99HysUWKVgUTd4qYlN71aHYF0DBKjArH14/9a6FnLzAwhcgrk9A6vXGJuyblNqc
            BvfGurtA1sQUF3RrI0XVC64ivoCiyj5/Tp9jHcmtySTzdFc1XZqFSKOrbYscJgQCtPms+rSZQ6K4FIgiZoZHMx4hxY2jcu92U4qd
            GWCmlwWGhgXUnF6Ho9aYu0jzwsdFjxUG27tdmy7vRIDenIAsADHnE5pNBfWxyzzlYsbw16X+y5CGWBJ4/mOBEG2XdUMlMMtAJgvD
            7QA+9VhttWRal7ykYsuxy+Kmtd1uFrJ880f8PTxc4M7RDDabjVmE3DOwDSG2zC7HsYoCobEKYG8JLlqWzW0yn7+3x3i4eoqDZI6G
            MdDmCEsSgXkYHRHYYV56Y1//wqPdOsW0AWgBOHAe6DaSMy3pPOwO78k5SbyjnB9cd6pPZsY0IRGJ1VsHGOVNkJSQ6SGhqxUpDhCa
            gLIBGFA8zsBJtlr5FRBPt/J62uHRAp4U0QMynejYAIIBKwjZAnhtB44hBHtbysUNVJCTT4GxWXIILCwyIfJfH0bSHOnoE4I8AkRp
            QlstiFI9T3Ef+vRY3sftuN3evnh/+2rdQTY1CSUIIYN9OYMnQSt/J5D/e6ZFlh1pgk8V+PKlNHMlZU82lVMKzE4GIvOGx2eEklSU
            soR6drKf//hVnhUiSL6xEDeOeFWF0BURbV66ML2rjlzlm/4cMB2llvUlxTtyjzuMq5gby2jSi3xv3uVESusNKb5uKPUG9rKxvwXl
            7vgFKZYeFO4qwt0psc6H1yqSAEZ0cGsXoaKMnp4vpdd9v7fMvUiA03f8DF+rHE3t3CC1TXUV8Kd9kch+DHiR9W2qVc1KdyXqnm6d
            moMqp+VUQjUlK2oji+IPZsllqASwqj6rOMeCTCUKJJQxm3HJVKIMSx9hGgRjoRHmRGmkCgmvlGYgzTnCiLurQ9L2bdq/3zBMwyzo
            WeOw0IIjHpB8/3/7DfOL7e833k8m6SNAdGALMAJk8+xfnnNy0p71+S+enHzx/12wgJFGEagQ814/Le/zZ076y6TDJKEkAOrnd29e
            vf//1YGX/9D3bdsmV7pxSP+wezH15dCCR1Dt7VcFAuWIDRvzcHWCWatQ6KXIlVy+9N92sP7+dpVSqq2x46mlwJ0QLM4pUY1zj2sp
            Vdn5geNF5JtSr/gjz71L5ox+fL9GPBZ2I8MkSdzHi2GETUBeGEoOQeJGIyIWqrSCOMYLKtnqiMQytwpDAXSBk3QqS+rzv/yf32l3
            uwF3BuiaCDFJZWLMtH5AZTKNxViYMp1RhBgOCmmZwzgMUoZyWApTlIhxdk2qQPQzVp/oT5JNoPH47EW8ff6XE6ZzuhAOK2T2Ky3D
            qDoyaxFvjDlln9f3v7u3NaDP70X3edRaTT59e/QjKj1t0wSPhqIm3Qnap922M7JsM+B9O7pWx93TSa5NJMYCoMlVq6xwv+rF9Bdc
            69qMCS9IcC5ZR1wgV5V7RIu2RPFKGrF1rFbMzrnLTbjMp6FQnells8phJBOMSbjc8/gCV1hXtVDrxelR05egWFzAKGJIc1EFG8vR
            Yla91tsg1PtMxV7b3JP8KYGIUryUoKqNZ7EhOhcjUnm59xKSitT6+WrNgQkUhrZcCArVUNRjF3vWmC42MMiUDBl2cyiSpzDhIWIl
            Ss2FQyfXf+ePDCkNMmzTkK68VnsjKbdAQjMZR5f+0pAmSWOnVF2XgOVYa4BoRCGwzn/TLSvKTn506NOfP/En/ZN6GcwNxQdYVcup
            y7TOybm2vJMv/r0d0QK5QCszlH759cvByY3NWf/jx20pbfKl7+IdvR9709tbcoqP1pkgJhKUqJEB9nCMsrFCRGul1P7AxcyIGOXt
            N48+8cVAqpVr1ca4YXZcOw1hOio93fPLo8M+TcBtrnLF/Rs+Hf70jrjjL//lv2IhF8DAUolMrlhIJrU0YULlhTKKYokhrDsJjFQJ
            C8NUKBk2TRxJoYrQjVrhRuQEicGX+v4q/x8MOoGPdWSoTk+AoomkxEmI+kQk5JDKDDQ6va9hRJ8kbKSN9MSRGcTphBDNKNACSauh
            NEP3Mo7HY93ur2+/BEDioLmDZlgPkonDLHArYRbjJATOkqj9rnX/yCfk6aN+YoR+SOdhOQvdO0fmec6xMocMg4j0WExIqBmJWrMP
            KwFnQZQ1rqiqGJ+Gr31LmsMmTRMpWYLPWqlA6dPCczssAAaO4nxnPqBzd63X0s5QHPk63ZrzMalNX9X7nQFSr1sFk2YEBn6h+nVN
            7eLaAPfCtcQlLHaDj6HPyaVtFFAoWzQqEjNcsmqqerLyc6qkGSVi3zaFymMm05OFoiMo9fFaeMemscDfKzzHqAtCZVsedlpwzFGL
            HLuU5iDTipJatmgNSKlkT4z9XBdWS2uYkTC3/LHr21KSLQ1p7r+85CiSpeYgqsD9jZ3P7t0tyTTN09bxsGOragVoNgcTct3sJ4c+
            /8VzJ+04nzx38vmfLy5BRWAzJkPOcsmnvC9++lw7zsn33SolZQwJq2GgBzfYJTOSs+//hn/09F16G9GpJY9o7JUdVuQGnjiSTCxx
            My6NqLAsRXudIXo9xgSSYrmYZhcf3HryQQ+i77q1+WWdrBjco9Sf7FLKPZvSwPHlk7e72fRHdjBarXaGo3GvU7hdmrPJVDXZrMKn
            MQtDKSM3ihBidVI6NfVxCuaiDJlUZREy6YbRJJ3BEwulyn/6ZBupwF026MCkGq2hlSmCOqFNgp0mRODZlcCaxqDd5UgZbkCeFICk
            NEWlSHRyTtWhqTAyMdSxhFJ9vKp/e/H9b/efgoJGIWuQ98VDoHRytLByWpXi6YCimqWqzpofGv5Ph33PabM/CU8nvDZZa/QHyH3a
            fkQihkr6Gzko5hyi36zHIcQJS4ngk7D6xqAndlFJxyIiERDU+0XjGquG/PF4yNxqErbJtKNyaF4WlqAkJn2YKWlREk1Z4K4lL/m1
            MFlFhFW8/CMdvFGERnBS+kmBfL/wQgmNExiHFAd3p8waWF5FjoKr2FxTfQt7rkcfKrcyxTXI/NIbeTbQgtWaiXq0ShhYBUbsgKUl
            kZihd+G4oMBFVci00gyRYAEqEdyFR6gICuCkNY2xZwxxDq1GAX1s7B+T47qmIWnxDdOq+zm8BiUBzZCy5HHzf14Gtpz+rHk6ds/2
            ZClgJSYNjJS4fWiB53z55KQd60S+jF/936AYFNkaTsFW3SJxrE+fa88696sJZI6RRMYqxuqisFQR2L/w69upPS/voW1zcTBKBmC1
            AMoG4QHFDdzOEKuwAF4FrC6VCZZrOW9Zm/9sgo31auBRYHO5iuHfwFfAVS6DwHGox6nNg6aeOgB/ErRh6sPxqW0rZvbuW2zanrWB
            EAcYuIDSAJ6DWBa4cRMMQ8lUiWwSszIgkYxbMHUzLZx40o91fXJy99Ipb1Aa3vGikvBSg9FAL3QMNAKmV1LVwoArAxepgbUFUhA8
            tvIUJdNWgbqFSgleQCz3gTmEWjeyvtBNSjZY53y9XOCz9V/+pQHCNEAbpZS3e9ajVuIJFfSHD48P7x+Pj6+vnzw+fny8fOzH68f7
            q6jwPhyVl/Xb2+Pjw5WUffzks2+f7Zf87fXD1MPUOEglkXzoGkymwlSXoaQn21N1LZCLLgIp1+Wgi/TUPH2Szo5x+GgUkGeK43C1
            Mc0zVT3VWEZ2qANGypmu0EsFNAEMz8Q8blkKrqxLvQodg1E1JirSVQSO2la25uUASHVfNG/nEHP1JFLvBtLzouiQ+uNPcidTpp05
            dMUR5JCoHqcNjcLflAoygN9nHjbQQIiA0598oVIHcg1EE8LWQPKApADOX0AEADUIG2fUhSdKyQLEEFcj1QgwkhqPuKFUqCg7O0ni
            JDnRfHMNqBEZh4Zv+elEJRT+y07uDdT1BDC0AoRGFCWOeoAgioKAUlp9Oh34TTYMtFvwAAss3uwTEd+ZMgHd7IclIBUgAf3szbsP
            L069+4W9y/6FKgyJFIVxHCaq/3Jlz2svvfuULyxRoYXEbAZmWk9FgoAz6rVxWAIu1+XZtZM2+lw2j6Hmo83kAZqDvXOHCGTBtE3D
            MGrjDIZTMZU+GbNudt934NSVe89efcr9rwYQbCGTyyaSGZIGYYWKncEAoAw6uJrQmCK+oiS+C2hgsSJCTu19Xbu4nEFH54/efPi4
            9+vPKgUwCUj30kegtEKXE2vbVQFSKnaxHZwag+wBQ9oxzxwd+czd+4TasRUGw01sJGJkTlJHL9eQ5bpigI0bPsUClrlzUXsORxZc
            YVcMsDR00ofAAsIc01YrpuQFqlyoTkzEc2xI3y+vY+Tl5f72bU+8LuAByiA8EzG9Bg5IP5rRdxo54RhBEOeLFrEI1N7vIvgrOM89
            v/cT5NSa44ceaIpyTJYgH0Eq1DBW5ntGuaZtibRgCIq2LEc5PDNIuEVBMpIjgh219hKrVgktJ6OBYE+ZgPv/y0NKkJjw5lqsmJZK
            5d/2XRqUbKN8IXU21FmiBWttbtBwDE2nRyyg3i2urb1WOYGKF25uC0uuWnUZQ0VQKCxcEuJatWqbwNdqiQ1JryA1kguycxRYO4lw
            BM1RA1VGWXHrtnTvCdEdEuSBil+k3rdROGSZGywABTZmMm3vPIeKzaIcKZBg3Tz8xRZDigjKbIhRRm7axdf64zhOKAnjbKXiVRUN
            IG7OCTnWYf119hulSXOV1/QUkB17DcDNRWBY51/xh+hTmqZUOgrS5afuymoLJbhRn6ilgFMPNgVBRMHf6K9drwiA5EKt1OB+cGXX
            C+vb5z43fGGUlIqJVEiK3hx65VO+cvdwcxSkWLZsNTARTmxQ4iqR6nW28Da2junGgGz2JTwBD/AYXAaLTrQ2t1ecTRqGYjFMm9Zr
            9ZpNHbtSVxxK3fHDrvVcOHTm2r1nvd/xhVqAQyKRy5VL6aSWKhNLsTMAhQDLLQPollB5hDEEqFoIiKCrLGZZRILAgKVLF28+ft37
            8WeRAuDUNabrzgEohF2ARaAwIO7VhV0QLCJPQlvKRPYgF+aceDpgbdhjcnFiAkSnw5yBsFB4drqyCAMMYWzaReMABHvA8cypq8k1
            BQJHukMaGwjREa4JVIsjHaYy9dCQFgIJXDkpMDw5gn3RcX/kcbnfv22Bai5KZgXPgmA0MU5CUSHYi+3K/5QDMiF4RqzZqpwlFvP8
            QVg5fq7MD/t4niN1LosfCXk+0rN4FZ0+NJ5nZurb059r0wxJRbH+20JRDK+K02S5WTYvXFiypQiF/2K8sdeF2MMmKRN1SSlbF0mP
            FHFutIurbCWSweS2194bWy0j86yYuPO4pBfluX69qloXTB3JLEK0IJm2mFNYXLs2kJt41XhheFE94SqxwQWbsim9isHaq6SIKvFe
            XmmDsy0iqhsWaE4E8DCkNJQKzvDlvrPjwYr1QN9aKkbhCCDKhoadf04Yw8si2yqrDWs64VLYkA2BCILMuZCH6nKGg1vuG77zH39C
            pShUs5bcb2NrQrLcAkAtXHL25f/IzVf0IvbHHFsXPCIujWJqGMsyL3lH6hNGaeqQlSj1fhQ+QzHYGkvkuM68JBWkIwro/fo8FzQr
            GIJW4UB3d8ruEHleF/7378c7enDBevOld1b5/RQE3Q25GrgCloUIyS94YUEhiokeQCqdx1MbnHlBKvr5/ild9TfOaqpRqIboD4fj
            Ll39cPbs/rA/Vu8oGR119fL7K7W/6xev71rz3o9+9JPrR7e99TqAnacQbNUbsHm1KJOUwQUQVcFIWSp2WikD0PUq8U8CVB5aGlRd
            Mejo0vnrF2/2vnpx/0LlZxMQiLs44KLIidS5g2taiF96gObsLp9pbq24Ry8IdpUJOZmtDPfEhhnGikkZ6tyFAXSRGDaoJHjgkotM
            R8AE5PpUtTnQIXAeY2K4BCYVMsBhlu5e97Eusepx3L5OiNikO7bpMcVg3saC4UkH0JLy6SYGJEltnVXvW/W3f1NXtmWsOSAdcch1
            tB7BVbrj0gfgLqf1JWUw/1cdG2KgL7Q4MZDrEGgzm7Q5DxmUANaq+ha5gNv278dNDQo7hFkQFHZeH8S7I3KnPu0je60AVhcEkhcF
            vMRV1RRsYkEGUD0SBruWPHov7F0jWJdjiFgvFChduWgiWTC5pAWULMjFPL7g4lZVSZ+dAy6VkhqaCC/cx18NI2AhvHchnt0BylaN
            yGQR+16t9or9L8K1TE8TnhOJsBCFKmREaeCKUUA5hlovHMmRhqnhZhoNTSMJHa3mrkYxh6393U9RGCfqcjJUeW7xte44UA1IQnIu
            /n52EipKsrOf9S+qGZIBrAX23wCqJG7u8I3UgCPJz5MdMjkosDjUnAvLDb4TReko1b+lPqU+ZYHYliiRrTPWQmI869C+tz5/kHo2
            lC85QdoEoNY5bROKpKg1tGSSEKGsYqgukQmWa7O97Hbr6ild1Ce+syjzwusfnz1c/9AVix8f73ZHR0eHw+H4hme7cun94ej6R8eP
            WjR7mqa93s1R+KgFFLIBauI8EM25FyaapukoiOu6msoDKQGA5EEHiZpip0HVZSAGzF25UMcGee3KCHlrBek2ZWbJzNWuKQkQPgBn
            Awhg6uUaOB3tMfVKmPqwOeYzLjrbgCsNXJA5iJ7bIJMTtSHUFiDnTsNlCcHeNuTiRncQE0+DsHXjIhCLGg6RHmlUjYExAEhfYRij
            SEw6vClTMVWvp+TwfI65Ro/buhxvb7fXz387ATZ9EU4SBkzt27VwOtSmXwXW7iPmmHHReh03Py0B
            """;

        /// <summary>SHA-256 of the expected top-down BGRA output of <see cref="Experimental03"/>.</summary>
        internal const string Experimental03Sha256 = "CAE8C3F92FDD8FB09F8576DE40956FD9FB38817E264D95CCB905EC2641BBA987";
        internal const int Experimental03Length = 11118;

        internal const string Experimental03 = """
            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
            """;

        /// <summary>SHA-256 of the expected top-down BGRA output of <see cref="Uncompressed16Bpp"/>.</summary>
        internal const string Uncompressed16BppSha256 = "CAD027862069355AA9C66A4CBC131028506DF131CF7488C53CB83262826C0687";
        internal const int Uncompressed16BppLength = 871;

        internal const string Uncompressed16Bpp = """
            G2YDAJ6HsbG0uWosfBfiFOmVlt8ztz/NqHbTRqgbdw38LrQlHnESCp7H6xAGtwMMyjCJsx/YT0DjkpL0PwGuzwdv8s63aFkYFliK
            71nZAwoDi+k8w0QSCkBzO80bv9nbfn7Uuv1k/h7//Yg66dbrgD0NWzOz7Q9hYGTKzEsAznVwkP/ACeBuB0ggLncXYGwR5xZj8BTM
            /jIdWMA39nTIisifWsBvAO/LQA/pHvBDPwlv8ebQigeSjPsrMy3ZxbS5/pWCEsy7e9IY8ANA56bRKaZk208+X8n7Ej6DEV6Vqg6h
            oISrqta2tCQbCKEXJDEfuqDmgthKlpoFbVQMKmCf+AoXyTwEX1G78E4e7DJ7fwZUWLTwDh5UvBd8BoPCh1/VeQ==
            """;

        /// <summary>SHA-256 of the expected top-down BGRA output of <see cref="Legacy03"/>.</summary>
        internal const string Legacy03Sha256 = "962A326AD0DDBF516BB10961446FA9525548560314DD415F22C9744A7E4D94ED";
        internal const int Legacy03Length = 11160;

        internal const string Legacy03 = """
            G5croxExbBzAAFSvAf5x4JRpjx9OcUn6SvRGVz84pR4Jl9SI6NY9dPjB6T34/77fl9Xa65x73/PvHhEewSmFlEFSpogS0JOxgFSl
            IogiVtGsIZoYZ21WMGxS0/w2DHuquf5rGkTyACpmBDOMIgqmMMEJ+eUU/m1DjIf77nm37j2Ep+1N17MKKRlA3cv+06maAXQWzObl
            Mtb7dtyttTgXKAl12NhA5QgWqEAYqpfLCwSrBauqzpiMkqt0X1xGFJ9fqYQaj1t5IjV38dTejfOoZxvZdFLXohBXNTVxvz9KaHrS
            Ce0LhsGDq2qKGq2c/RSZGkONiF/Y99l9ozG3/+zt9dLXnlqrri4oMjf3iawqSSkmBS/Z/LpQEUM71++NtbeZslTu/EE8uYHS9oLV
            29xcT6sZNyZSc81JKiK6t4sKkWknVZxB57e80jkIS+kHewqUK66HJumv1UKb1ZaftMlKkQiVpCe/LqJCU/ZfPnl6rc3VBYisU2vF
            6raj9G1MZrrN0owNYo0i408mwUbyv0CBfsgPCK8krHnNXO2aLX6b52ZXsbpbRYjIfXtP7P5GRa5kAeq1jVIxQiVnLypoTBvM65qt
            EvpLAXINLCFILtfXmc7H8XVlknk2TyU2QpYZpAUCVR9YhFpvPhThxTl0LybgCWKvbFaep7o71YE3pNlCFQL9JQGvgxXghV/Ul2pL
            mCnFuAQOreDl795I1tYi/BkmkDA9xX548T/0XOSymH7y8sP39Nd8CVt8+IPkpJsg1N9suxmAfmBh2X78KfM+IsLZT162QcQ3vBvM
            775DUEmHX0kZIs+LX1jwdon7/JOwnthF2hRdOI8CfZpbH1wqAcOvC2YIJPnWlRi54hKc+8q3rjswz7wHrKs8J+8KkRJs61/2BCEg
            FxeWwO0Lh4dADi/m08i2yKeJyxL0xQZVycKUFIRQdQt4fgVUOJ3jGOb6pFzMvwmOS+HS8yKCmFFaYRo+T4vn7mEs4eb5C48AnQ9Z
            Cly+AK5dA/h8CEq7GgLo7IRxsurCCKTIPTWwYRPCAMnb5xc+KQNYT2cZ3GqIULkfsDNfAmEFCPXChj0A5Ve4+dX5796xBazx0r5t
            hfH5SL8DmmWEhECG3oJ5J5A1L3XvvPCC+uEPs1+z7rwz4uUDnCr1vXcAYfGukwmzRUPuQypwsnwyX3zi9SCAwf76jyioeSciKcA0
            2Hp+FQJZuZMGWKkCO/ftKfC1gqz9XYr33Q84Qwlw/ut/bu/b2yElP4e/eRP8inmGRfyXLUwqEe9BloDAi19cC68v8tC9FzT7u47y
            nZ3V9PUReBmsXD8gOYIDwcIHxqRDvhtF1v69f586CAVnoQRrJwBvoLypyUQgR48cCiNUkbeSFgkV/HwH+ElFZOlDe+X0KQV/7IST
            yLSPDB+mjLtD00fWUfLmGo1I/iUViEazvRWy+uaKENbSyKsTtvcgBwgIVE4uEO+QQ73vPn+pWUpiC4HkFuZnVGrd6NzOdNVx5XCu
            vay7PET9DHx+a7T3W3qqxRtWiNYowZ8TRZbI/sGfey4m715wkybqLFldf8jMwqN34hIWolBKI30X8qysfnTG7PRNtbu9lL1/Jk0u
            eOcLnkECMyOvSUNegvKcjNUnV9R8MiKThDd9Ew1FPVTahLbg1Y62d4FvBhdWcnmWdc5gi0Dl1xPtzhBHnoA8LnfDSwlLRS5/k0rw
            KoCnVpxIp6oG5CyQ/6cBEgHDEhBCkqOHDbTBKYMzTLBbMRvmsgkynR0mCyQXjVK/7xhxiX0cZ5Ucv2m0v832lovoVVRC3kpVQEbB
            /JvQyShTLJSv/JpMIZOpIDsXD+Akoe1bt2nHviPXXjk5KCSkKwcWDYlndbcQJagPIinBBeMHfnKxXTJyRrnb76NF3WO3VBIR69yR
            NdP9auafAXZsW0Yq8fcgUeR+zzmQ1T4zvcGTEWrbxtE6Pne0XbhwdO54HI2RLSWtqsKeJkjlx6uAcF/ThEVeio+igaayEJIwANNL
            hDFoKyOs7oAOkNIzoBbHD3uDkQRZTmmgA7arpRNjEmSFghRVSOUM6UWNVDdJClwkhCoTkiywIsZpy/8loChVCafYsorUQNTOGoUM
            DB30V2icXfydqm0gJj6YNARZHiZ7t92ivXZStDLRYjFSqRSG6EJDSigDZ1R6MU9M4Qg9qv5OSWJC+DsGgoKrp0VCjxB4VWlqsd8S
            CmzR4ke3TWdYlYD2bLodC0zRQQb6Gpiw8K3y3DtUKrp2R6IkbaiVKkcldAVZCWJPSWNfBkvuyU1gUsJizm5sE8EFtdmBoHMuRlAq
            xahwRuGCS+ld2qJDLZJsSTAxWdpTFKYJr3Xb5k/SCSG4cH7+YLbYdLOKMJtgU9MVqgJJCpPXSgr/JD013YwlvGy2aJGaaypVTgOB
            RexEMJJaYVJRDE3OQlJr//IV8fT0cHKyH/bpXE5nRsBR6GI2EIpCaikQR0DglOjl9PmiIyynnz66e+PqhYWZho2SinAqy+Scbais
            GiNhihOoBYlodYtdy8PZF7cePH35tqOzs7M9DJRCPn7n+p4d8ftF7nBY0qKBHuIyIBFD9BS8k7DiPFzLujO4g6ev3nf2B558o07I
            L1K9a3AlakCSMvebIkl/fSCs2CenatbubWb/oDBQCAyeFzThlDRFlS4riUnszuAl5F54Qr4k4Dr/8naxvVpI3dxRZW7OJSAosyrj
            ebhT78LkS0F5a1h9qbJkcvm5K3NSEU7alwA+aFvb+BUS+YtJICSsBASlT7XafvCtdDmipopMh4yj8WoyyqyqUaBEhS8L8nsFlLCU
            qI9uVDqyd3cif5OZqdNAsn6Pk5OLg4RxYtvysF1Ojfz2KCRcziIB9nuriU/dzWzhwckOptv1uVeL8+v2nDj3AHLxC0koAkUBEss1
            dUMPsgHyiRiuZHFyq3Ux2lfHUohM6hRb29HRuW3bjo8vSvz1YBjpQ91zD7w8IBFp14TB8+9BQEGWQU/OXpdaTSZlwlNipewkJAwy
            1tHR8UUpkGnO61+4pYAEwx9GvSSggC8DebC/bb9SeVfhemESW60k/RJGrbEdHV3MlBb1n0SVBBH6k3vY8J0JhNedj055x+MHCEPb
            jFBAlu4HoxlVx+NonbsaHjN/oyCsKtKKT0Lui/34SfVFM5NC6dGdgW5iyu6VjmsXjPybw5lMKpVJpzN3D+cy6Uwml/5npo/sShBh
            +8zQTqZ0tHQwJnOHfe6PL1k8908Fld2V/uSeFpGpfVdXlQbEjciKSIKJyFm3c/3mj2pPnUHWL3LSpxqyJ00+Vn//3uxuMlfwc/s6
            7KlsSGBvlhXA2Z0Ezja6/63XVjuW50bDmBjkiuvOztDqwkRvfC/G9MQfp0jMapFErZrUKd6/7dIA/z7WCrKuDWzeJtdnErMhbm9q
            2RllyjEq5E+aaCvUbzdwuBMDtQ63Gqdrv2fiXZnaR1itc2Jrpej7zEPF7m8U5MNJWOqjF7NTyZZwkGQRIIot5PNbAn0X42Mh3A0r
            uT5LeWaawx7aGel/XlgtpjUyEh9AJ1K1P4lx2TWeevWxI8El6+OI4cjNq8Xg6XK7HQZI509uYQSC/YcEFFnhQ39LtWkGf3W3Vv2u
            y5rFR3/bVopBK2H+SzCVbtr64eX75kH2B+mh3pHZeP6nfxhbvvqNXNsGhPxK95QKH1gYR3/u3Z0/kdMiMd/1A2ew9Be/gh1+/5Nk
            VUh4IwWp7gTit65SX/jBd8B6+yM/Xubo4wRat4zHv3nlBFr0X2IztIV8YEGeua7OLObqT6tor0xFtH7Bm4Pg9PP7ybbS/Xus7sRw
            S24vLPDcVeFxkEtT1ytzYLD8XeTDQdAf/axQQxaVpmxIjXDqRYRhuHF+XATM7/zn6SDxPThX3fDQBzeTshjBUBppbuFFBEXunb9q
            AN3v9VAM8glu3QT44VtEs6RJlaANtxgnXxrX2bDCO57Vr+zCQFOfWPuWviMC4F3PkzpSb0hblRI6vRKSkBDqIz+ZLoJfyeMXF37h
            V/aB/7F96Hcd6mydT7J/cbISqhrMEPk9ioCs/camNFMalG/8tpGfXr8l1HNDh4Rn3O99HqCLL5+SoadMK9wnJNCvQFm+q9X+53+1
            oxgc/uYf3/mv5V/P/OyBdi0Uv4Ssp12irVSV3cWr3SRc9gZYJ1+8xX/hn8TphwGXfiFe7fI+HWnsdU9FgDXeP1Kl2G2c9y3S7WEJ
            fPgHQ8TtYwkZ5jz/9vDZ35tcXZsfSU9D1u1XWTc8DZGgr0PSCd+tIuv87/3/gQhN31P01eVZgriDaq1CiAxw9MS7CY9HiK4gSBhF
            f34G/CkRZPn408Jwk5jnhU8VKXaaTRTGle2pPU5ZbofIe8QiYiWVBDiL5g8qArL68ZOJpVwTPrLFI62kqgwkFVXVlSU4vGC++k+T
            l5NvjxDvhwT4yyoism6tTf1fL3waSjjL5ncIe9CmK+Ds9dlOG34HKm90EB1bCdazkCVy8s1/uTDdX2o2U+mhtTYfE/MjL8NzX9qU
            fhmG1+NkNnlNQZ6VNZ/cbe8bX1hfnhkNG9nUgi98iI+OwhQPtKWKXIafQxmrT2NmzU6HSMLtXwoDp/DUNmdQUr4qxv4TooCcrOTh
            fQkh1/iCPfHXU+KkgFEfU3lKHjpngId/sSDJOo6TFUJs5TGmwlkpfx5BEnlclgSS4ze3z1X9StJFJfZoZE6ZhvAnpbuFZqnken2I
            3DDbzIX5t+Nh5cJj7U0Cn5SaDOtEgB+rMpKKo9LCA8lIA0vhJ/9ZXlZOWl5RRV0TqMsKUtGu3YPc4srqKgqyQAxk3gwGPEnalqXY
            lIJJzkCXHP2hv2a8OGw3u7PIbTvQEZFXUkFOSlSQA2nXhsUqigbYc0fZKoaWK3mjMU169qhiyXtf28JuG1Xb0XZuXRw5d3xVknw+
            /sJRohg1ViWxp2CTWhWhQAxyNtss+NhjnsLEJvwdYlJJyGpDU1tM1Varp8HZBWp/HqhennsLmwhUgNUNTSCoLIyVBhN0FSVjkEqn
            OAvUYmwVG0ghCyshhRJYagVN2jb/jzQqpkaK5agUJeg0aww6ZUMnumicPWhJtlo0VpruqeZMzPKwM3vSEuw1oVTaoouVkUpRCYlz
            dac6W5ehwhnFXJycbypW7uHPpZNqKFYQGmi0XVbXwP0EbM8yzeFpP4lNJKyjIVKRkErs1faep5imgFpFFEKHJmdNsvR9MZJR+HZG
            IJ5NbavGljGSaCAr1KSzO6oTzway4HnlQJGdIrxmz54ukBYiamAV2hvtoMTiT2pVBHrBDffQtqFl/J4QjkNYRp3dCLQroWe3kaRz
            X8sKC+cPnkhJN+0qyGxQUu0Kg8ISmyaroDSADZwplSt+8j0MFUnY/85Eq2zCKqLYT0P4MymkQO63iVAL1IefuBmJ+GXhVNhN2/c1
            JyVstqKlksMgbEuU5BRO8YPVxZnx/u5S5vnDB3evDJCrRZ3fzxz2venOSgoqPaeayyD08trO7nW6nnhT7R2eXlnbWF9Zmg9S4wOV
            9M3j2RibXGn2gsYZK0WoFRDbWbt5J4bveD9/szJncP2lb3RufS9oT4cR6YjC7Puc4ArYSJMMuI+VSr6/gbAS52kk745jrv5oemYU
            pEpNBpYicm2R0UsqYAHKOAUvR+6GZ2Wp5z/6y9mVxcn+/Pd5BcnMbANEoF5NBjv1XRH0htyYtHoZXtwaraX6BV2gdk/pXboegzwW
            Xk54XJYgzGvWHDz8M5+Olo4q3N7UOApsdQvmKzW24jk5tyLeu5hfmajGZf0i9aCVhGL9h7q6hoq0IDcBYdeaiZJv2zyVopunP7x4
            2rew2fF26OV6CJ3ZrB+yPnXkwhO/oKyShp6xn6BkdijDyXa+XdRHiieYZgWnT66EfunkP9WocYKM1/Hx0cX2Ui5L4YBPT6RQcYco
            pKivpSQlQKJbl3B9eP5r/36mu44pU30KVzJnZaSoSm3reLvoC8brq8zvhaQUlCQMU9oirrZD2D/5O/KLtVRZZFYyKnS3GSlWjWw1
            tuMLl6POtOWL4IKP0yM+aGMkrAQCBPDw5IrBfz55oxBO21JJRbKwd6KOMY6Pj9flsLHFra4ggNJnl5OKT0M+73zb1+IPuVQqnUH5
            +tz0WLfhWGY+l83lkPNsfoC8BgazD1SQm0Z2c6Cx0s/2BRm2wfz9OYUU7mQ6uz1cU03LRa1ex9ZkZp89DVmjhBKoYC+bpIAiyBnt
            vt4x0yioItfd4jys85dihd7SQiev+rnW1U22YYPu05Wjarp2LbfiLHT+rfNavuVapkKGyy4ONyuthmul/VyKXjCqIima7jeUtumM
            SaV/v1eSpqtBWRE4XNHMSstNisyi0e46Gkk51lZdVaGdkfxH0ICHnbAq9OGS1pmrpEVIr7FlZ07BUPmTaPYD8dA/ZSLyNSZhebjj
            uknBuVup/N8GO9BVXVv1XYy3hiUQydHVlJFgWYebFUsAnT1NrRjStilNxruhrCjk+C0TE47hDMf+F/fs9xohQLdDfj3NFjZCoH9e
            QFmXywZ+RdMizPELo55cQXmm6yiQhql/MlR6BuqHl+cZH/N860e0pfHbOljcfAjas5HG+pvM2b2hLRldOs/xlxpJrvHUKiB4Nwh8
            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
            """;
    }
}
