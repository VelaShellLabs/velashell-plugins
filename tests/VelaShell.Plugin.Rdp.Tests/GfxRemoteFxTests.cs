using System.Resources;
using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class GfxRemoteFxTests
{
    [TestMethod]
    public void ClassicProtocolExampleMatchesIndependentReferenceImage()
    {
        var assembly = typeof(GfxRemoteFxTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("GfxRemoteFx.fixture.resources", StringComparison.Ordinal));
        using var reader = new ResourceReader(assembly.GetManifestResourceStream(resource)!);
        var values = reader.Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        byte[] encoded = Convert.FromBase64String(values["Encoded"]);
        byte[] expected = Convert.FromBase64String(values["Expected"]);
        byte[] actual = new byte[64 * 64 * 4];
        new GfxRemoteFxCodec().Decode(encoded, 64, 64, actual, 256);
        for (int i = 0; i < actual.Length; i++)
            Assert.IsTrue(Math.Abs(actual[i] - expected[i]) <= (i % 4 == 3 ? 0 : 1), $"Reference pixel channel {i}: {expected[i]} != {actual[i]}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ClassicSolidColorClipsPartialEdgeTilesAndPreservesStride(bool rlgr3)
    {
        byte[] pixels = Enumerable.Repeat((byte)0xCD, 32 * 3).ToArray();
        var codec = new GfxRemoteFxCodec();
        codec.Decode(Classic(rlgr3, new RfxRect(2, 1, 3, 2)), 7, 3, pixels, 32);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 8; x++)
                AssertPixel(pixels, (y * 8 + x) * 4, x is >= 2 and < 5 && y >= 1 ? [123, 134, 196, 255] : [0xCD, 0xCD, 0xCD, 0xCD]);
        codec.Reset();
        Assert.ThrowsExactly<RdpProtocolException>(() => codec.Decode(Classic(rlgr3, new RfxRect(0, 0, 7, 3), header: false), 7, 3, pixels, 32));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BothEntropyModesDecodeDenseSignedCoefficients(bool rlgr3)
    {
        string encoded = rlgr3
            ? "h8fT2o05OaiTk5qJOTmok5OaiTk5qJOTmok5OaiTk5qJOTmok5OaiTk5qJOTmok5OaiTk5qJOTmgAAAeDA=="
            : "hevNCOXmgjl5oI5eSCOXmgjl5II5eaCOXkgjl5oI5eSCOXmgjl5II5eaCOXkgjl5oI5eSAAAAeDA";
        int[] decoded = RfxPrimitives.Rlgr(Convert.FromBase64String(encoded), rlgr3);
        int[] pattern = [1, -1, 2, -2, 3, -3, 0, 0];
        for (int i = 0; i < decoded.Length; i++) Assert.AreEqual(i < 128 ? pattern[i % 8] : 0, decoded[i], $"Coefficient {i}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProgressiveSimpleProducesKnownColorAndReusesClippedTiles(bool reduced)
    {
        var codec = new GfxProgressiveCodec();
        byte[] pixels = new byte[16384];
        codec.Decode(Progressive(Simple(reduced), reduced), 64, 64, pixels, 256);
        for (int i = 0; i < pixels.Length; i += 4) AssertPixel(pixels, i, [123, 134, 196, 255]);
        Array.Fill(pixels, (byte)77);
        codec.Decode(Progressive([], reduced, rectangle: new RfxRect(62, 61, 2, 3)), 64, 64, pixels, 256);
        AssertPixel(pixels, (61 * 64 + 62) * 4, [123, 134, 196, 255]);
        AssertPixel(pixels, (61 * 64 + 61) * 4, [77, 77, 77, 77]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProgressiveUpgradesRetainPositiveNegativeAndNewlyNonzeroSigns(bool reduced)
    {
        var codec = new GfxProgressiveCodec();
        byte[] pixels = new byte[16384], expected = new byte[16384];
        codec.Decode(Progressive(First(reduced), reduced, extraQuant: 2), 64, 64, pixels, 256);
        codec.Decode(Progressive(Upgrade(reduced, second: false), reduced, extraQuant: 1), 64, 64, pixels, 256);
        new GfxProgressiveCodec().Decode(Progressive(Simple(reduced, after: 1), reduced), 64, 64, expected, 256);
        CollectionAssert.AreEqual(expected, pixels);
        codec.Decode(Progressive(Upgrade(reduced, second: true), reduced), 64, 64, pixels, 256);
        new GfxProgressiveCodec().Decode(Progressive(Simple(reduced, after: 2), reduced), 64, 64, expected, 256);
        CollectionAssert.AreEqual(expected, pixels);
    }

    [TestMethod]
    public void EncodingContextDeletionRetainsSurfaceDifferenceReferenceAndReleasesBudget()
    {
        var budget = new GfxProgressiveMemoryBudget(96 * 1024);
        var surface = new GfxProgressiveSurfaceState(budget);
        var codec = new GfxProgressiveCodec(surface);
        byte[] pixels = new byte[16384];
        codec.Decode(Progressive(First(false), false, extraQuant: 2), 64, 64, pixels, 256);
        byte[] reference = (byte[])pixels.Clone();
        codec.ResetEncodingContext();
        var replacement = new GfxProgressiveCodec(surface);
        replacement.Decode(Progressive(Simple(false, difference: true, zero: true), false), 64, 64, pixels, 256);
        CollectionAssert.AreEqual(reference, pixels);
        Assert.ThrowsExactly<RdpProtocolException>(() => replacement.Decode(Progressive(Upgrade(false, false), false, extraQuant: 1), 64, 64, pixels, 256));
        var otherSurface = new GfxProgressiveSurfaceState(budget);
        var other = new GfxProgressiveCodec(otherSurface);
        Assert.ThrowsExactly<RdpProtocolException>(() => other.Decode(Progressive(Simple(false), false), 64, 64, pixels, 256));
        surface.Reset();
        other.Decode(Progressive(Simple(false), false), 64, 64, pixels, 256);
        AssertPixel(pixels, 0, [123, 134, 196, 255]);
    }

    [TestMethod]
    public void DispatcherRetainsProgressiveSurfaceDifferenceAfterDeletingEncodingContext()
    {
        using var dispatcher = new GfxCodecDispatcher();
        byte[] pixels = Enumerable.Repeat((byte)73, 16384).ToArray();
        GfxDecodeRequest Request(uint context, byte[] data) =>
            new(5, context, 9, 0x20, 0, 0, 64, 64, data, pixels, 64, 64);
        dispatcher.Decode(Request(7, Progressive(First(false), false, extraQuant: 2)));
        byte[] reference = (byte[])pixels.Clone();
        AssertPixel(pixels, pixels.Length - 4, [123, 134, 196, 73]);

        dispatcher.DeleteContext(5, 7);
        Array.Fill(pixels, (byte)73);
        byte[] difference = Progressive(Simple(false, difference: true, zero: true), false);
        dispatcher.Decode(Request(8, difference));
        CollectionAssert.AreEqual(reference, pixels);

        dispatcher.Reset();
        Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request(9, difference)));
    }

    [TestMethod]
    [DataRow(0, 127, 128, 129)]
    [DataRow(7, 119, 141, 255)]
    public void ProgressiveScalarQuantizationUsesItsWireBandOrder(int llQuant, int blue, int green, int red)
    {
        byte[] pixels = new byte[16384];
        new GfxProgressiveCodec().Decode(Progressive(Simple(true), true, llQuant: llQuant), 64, 64, pixels, 256);
        for (int i = 0; i < pixels.Length; i += 4) AssertPixel(pixels, i, [(byte)blue, (byte)green, (byte)red, 255]);
    }

    [TestMethod]
    public void ProgressiveMultipleTilesClipTheRightSurfaceEdge()
    {
        byte[] pixels = Enumerable.Repeat((byte)23, 276 * 2).ToArray();
        byte[] tiles = Join(Simple(true), Simple(true, x: 1));
        new GfxProgressiveCodec().Decode(Progressive(tiles, true, rectangle: new RfxRect(0, 0, 67, 2), tileCount: 2), 67, 2, pixels, 276);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 69; x++)
                AssertPixel(pixels, y * 276 + x * 4, x < 67 ? [123, 134, 196, 255] : [23, 23, 23, 23]);
    }

    [TestMethod]
    public void FailedUpgradeDoesNotChangePixelsOrPersistPartialCoefficients()
    {
        var codec = new GfxProgressiveCodec();
        byte[] pixels = new byte[16384], expected = new byte[16384];
        codec.Decode(Progressive(First(false), false, extraQuant: 2), 64, 64, pixels, 256);
        byte[] before = (byte[])pixels.Clone();
        byte[] truncated = Progressive(Upgrade(false, false), false, extraQuant: 1)[..^1];
        Assert.ThrowsExactly<RdpProtocolException>(() => codec.Decode(truncated, 64, 64, pixels, 256));
        CollectionAssert.AreEqual(before, pixels);
        codec.Decode(Progressive(Upgrade(false, false), false, extraQuant: 1), 64, 64, pixels, 256);
        new GfxProgressiveCodec().Decode(Progressive(Simple(false, after: 1), false), 64, 64, expected, 256);
        CollectionAssert.AreEqual(expected, pixels);
        codec.Reset();
        Assert.ThrowsExactly<RdpProtocolException>(() => codec.Decode(Progressive(Upgrade(false, true), false), 64, 64, pixels, 256));
    }

    [TestMethod]
    public void RejectsTruncatedEntropyOutOfBoundsAndInvalidQuantization()
    {
        byte[] pixels = new byte[16384];
        Assert.ThrowsExactly<RdpProtocolException>(() => new GfxRemoteFxCodec().Decode([0xC0, 0xCC, 6], 64, 64, pixels, 256));
        Assert.ThrowsExactly<RdpProtocolException>(() => RfxPrimitives.Rlgr([0xFF], false));
        Assert.ThrowsExactly<RdpProtocolException>(() => new GfxProgressiveCodec().Decode(Progressive(Simple(false), false), 64, 64, pixels, 255));
        byte[] invalidIndex = Simple(false);
        invalidIndex[6] = 1;
        Assert.ThrowsExactly<RdpProtocolException>(() => new GfxProgressiveCodec().Decode(Progressive(invalidIndex, false), 64, 64, pixels, 256));
        Assert.ThrowsExactly<RdpProtocolException>(() => new GfxProgressiveCodec().Decode(Progressive(Simple(false, x: 1), false), 64, 64, pixels, 256));
        Assert.ThrowsExactly<RdpProtocolException>(() => new GfxProgressiveCodec().Decode(Progressive(Simple(false), false, rectangle: new RfxRect(63, 63, 2, 2)), 64, 64, pixels, 256));
    }

    private static void AssertPixel(byte[] pixels, int offset, byte[] expected)
    {
        for (int i = 0; i < 4; i++) Assert.AreEqual(expected[i], pixels[offset + i], $"Pixel channel {offset + i}");
    }

    // Independently encoded analytical coefficient vectors: LL3 is differentially coded,
    // all other coefficients are zero except the three HL1 terms used by the upgrade tests.
    private static byte[][] Components(bool reduced, bool first = false, int after = 0, bool zero = false)
    {
        string[] values = zero ? ["AAAICA==", "AAAICA==", "AAAICA=="] : first
            ? reduced ? ["hAAAB659KAA=", "AAAez2UA", "AAAezdlA"] : ["hAAAB799H4A=", "AAAfE2Pw", "AAAfEdj8"]
            : reduced ? [after == 1 ? "m/cAAAAPWv/2UA==" : after == 2 ? "nP2gAAAD1r/+SgA=" : "AAAezf/ZQA==", "AAAez/2UAA==", "AAAezf/9lAA="]
            : [after == 1 ? "m/cAAAAPfP/2Pw==" : after == 2 ? "nP2gAAAD3z/+R+A=" : "AAAfEf/Y/A==", "AAAfE/2PwA==", "AAAfEf/9j8A="];
        return values.Select(Convert.FromBase64String).ToArray();
    }

    private static byte[] Simple(bool reduced, int after = 0, bool difference = false, bool zero = false, int x = 0) =>
        Tile(reduced, first: false, after, difference, zero, x);
    private static byte[] First(bool reduced) => Tile(reduced, first: true, 0, false, false, 0);
    private static byte[] Tile(bool reduced, bool first, int after, bool difference, bool zero, int x)
    {
        byte[][] components = Components(reduced, first, after, zero);
        return Block(first ? 0xCCC6 : 0xCCC5, w =>
        {
            w.Write(new byte[3]); U16(w, x, 0); w.Write((byte)(difference ? 1 : 0));
            if (first) w.Write((byte)0);
            U16(w, components[0].Length, components[1].Length, components[2].Length, 0);
            foreach (byte[] component in components) w.Write(component);
        });
    }

    private static byte[] Upgrade(bool reduced, bool second)
    {
        int ll = reduced ? 81 : 64;
        byte[] srl = SrlZeros(4096 - ll - (second ? 3 : 2), firstNonzero: !second);
        byte[] chromaSrl = SrlZeros(4096 - ll, firstNonzero: false);
        var raw = new BitWriter();
        for (int i = 0; i < ll + (second ? 3 : 2); i++) raw.Put(1, 1);
        byte[][] data = [srl, raw.Bytes(), chromaSrl, new byte[(ll + 7) / 8], chromaSrl, new byte[(ll + 7) / 8]];
        return Block(0xCCC7, w =>
        {
            w.Write(new byte[3]); U16(w, 0, 0); w.Write((byte)(second ? 255 : 0));
            foreach (byte[] bytes in data) U16(w, bytes.Length);
            foreach (byte[] bytes in data) w.Write(bytes);
        });
    }

    private static byte[] SrlZeros(int count, bool firstNonzero)
    {
        var bits = new BitWriter();
        int kp = 8;
        if (firstNonzero)
        {
            bits.Put(1, 1); bits.Put(0, 1); bits.Put(0, 1);
            kp = 2; count--;
        }
        while (count > 0)
        {
            bits.Put(0, 1); count -= 1 << (kp >> 3); kp = Math.Min(80, kp + 4);
        }
        return bits.Bytes();
    }

    private static byte[] Progressive(byte[] tile, bool reduced, int extraQuant = 0, RfxRect? rectangle = null, int llQuant = 6, int tileCount = 1)
    {
        RfxRect rect = rectangle ?? new(0, 0, 64, 64);
        return Join(Block(0xCCC1, w => { w.Write(0u); U16(w, 1); }), Block(0xCCC4, w =>
        {
            w.Write((byte)64); U16(w, 1); w.Write((byte)1); w.Write((byte)(extraQuant > 0 ? 1 : 0));
            w.Write((byte)(reduced ? 1 : 0)); U16(w, tile.Length == 0 ? 0 : tileCount); w.Write((uint)tile.Length);
            U16(w, rect.X, rect.Y, rect.Width, rect.Height); w.Write(new byte[] { (byte)(0x60 | llQuant), 0x66, 0x66, 0x66, 0x66 });
            if (extraQuant > 0) { w.Write((byte)100); w.Write(Enumerable.Repeat((byte)(extraQuant * 17), 15).ToArray()); }
            w.Write(tile);
        }), Block(0xCCC2, _ => { }));
    }

    private static byte[] Classic(bool rlgr3, RfxRect rectangle, bool header = true)
    {
        byte[][] components = Components(false);
        byte[] tile = Block(0xCAC3, w =>
        {
            w.Write(new byte[3]); U16(w, 0, 0); U16(w, components[0].Length, components[1].Length, components[2].Length);
            foreach (byte[] bytes in components) w.Write(bytes);
        });
        byte[] context = header ? Block(0xCCC3, w =>
        {
            w.Write((byte)1); w.Write((byte)255); w.Write((byte)0); U16(w, 64, 0x2028 | ((rlgr3 ? 4 : 1) << 9));
        }) : [];
        return Join(context, Block(0xCCC4, w => { U16(w, 1); w.Write(0u); U16(w, 1); }),
            Block(0xCCC6, w => { U16(w, 1); w.Write((byte)1); U16(w, 1, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, 0xCAC1, 1); }),
            Block(0xCCC7, w =>
            {
                U16(w, 1, 0xCAC2, 0, 0x4051 | ((rlgr3 ? 4 : 1) << 10)); w.Write((byte)1); w.Write((byte)64);
                U16(w, 1); w.Write((uint)tile.Length); w.Write(Enumerable.Repeat((byte)0x66, 5).ToArray()); w.Write(tile);
            }), Block(0xCCC5, w => U16(w, 1)));
    }

    private static byte[] Block(int type, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        U16(writer, type); writer.Write(0u); write(writer);
        stream.Position = 2; writer.Write((uint)stream.Length);
        return stream.ToArray();
    }
    private static void U16(BinaryWriter writer, params int[] values) { foreach (int value in values) writer.Write((ushort)value); }
    private static byte[] Join(params byte[][] values) => values.SelectMany(v => v).ToArray();
    private sealed class BitWriter
    {
        private readonly List<int> _bits = [];
        internal void Put(int value, int count) { for (int i = count - 1; i >= 0; i--) _bits.Add((value >> i) & 1); }
        internal byte[] Bytes()
        {
            var result = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++) result[i / 8] |= (byte)(_bits[i] << (7 - i % 8));
            return result;
        }
    }
}
