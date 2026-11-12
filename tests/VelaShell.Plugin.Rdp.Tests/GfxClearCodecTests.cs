using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Graphics.Gfx;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class GfxClearCodecTests
{
    [TestMethod]
    public void CompositeLayersAndGlyphCachePreserveStrideAndSequence()
    {
        var codec = new GfxClearCodec();
        byte[] pixels = new byte[24];
        byte[] overlay = Subcodec(1, 1, 1, 1, 0, [9, 8, 7]);
        codec.Decode(Packet(1, 0, [1, 2, 3, 4], [], overlay, 12), 2, 2, pixels, 16);
        AssertBytes(new byte[] { 1, 2, 3, 255 }, pixels.AsSpan(0, 4));
        AssertBytes(new byte[] { 9, 8, 7, 255 }, pixels.AsSpan(20, 4));
        AssertBytes(new byte[8], pixels.AsSpan(8, 8));
        byte[] replay = new byte[16];
        codec.Decode([3, 1, 12, 0], 2, 2, replay, 8);
        AssertBytes(pixels.AsSpan(16, 8), replay.AsSpan(8, 8));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => codec.Decode([3, 1, 12, 0], 2, 2, replay, 8));
        codec.Reset();
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => codec.Decode([3, 0, 12, 0], 2, 2, replay, 8));
    }

    [TestMethod]
    public void BandsReuseBothCachesAndResetTheirWriteCursors()
    {
        var codec = new GfxClearCodec();
        byte[] pixels = new byte[3 * 3 * 4];
        // Three 3-pixel columns: short miss at y=1, short hit at y=0, full hit.
        byte[] bands = Bytes(w =>
        {
            w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)0); w.Write((ushort)2);
            w.Write(new byte[] { 10, 20, 30 });
            w.Write((ushort)0x0201); w.Write(new byte[] { 40, 50, 60 });
            w.Write((ushort)0x4000); w.Write((byte)0);
            w.Write((ushort)0x8000);
        });
        codec.Decode(Packet(0, 0, [], bands, []), 3, 3, pixels, 12);
        AssertBytes(new byte[] { 40, 50, 60, 255 }, pixels.AsSpan(12, 4));
        AssertBytes(new byte[] { 40, 50, 60, 255 }, pixels.AsSpan(4, 4));
        AssertBytes(new byte[] { 40, 50, 60, 255 }, pixels.AsSpan(20, 4));
        codec.Decode(Packet(4, 1, [], bands, []), 3, 3, pixels, 12);
        AssertBytes(new byte[] { 40, 50, 60, 255 }, pixels.AsSpan(20, 4));
    }

    [TestMethod]
    public void RlexCombinesRunAndPaletteSuiteAndRejectsOverrun()
    {
        var codec = new GfxClearCodec();
        byte[] pixels = new byte[16];
        // palette count=2, stop=1, depth=1, three start pixels (run=2 plus suite start), then stop.
        byte[] rlex = [2, 1, 2, 3, 9, 8, 7, 3, 2];
        codec.Decode(Packet(0, 0, [], [], Subcodec(0, 0, 4, 1, 2, rlex)), 4, 1, pixels, 16);
        AssertBytes(new byte[] { 1, 2, 3, 255, 1, 2, 3, 255, 1, 2, 3, 255, 9, 8, 7, 255 }, pixels);
        rlex[^1] = 3;
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => codec.Decode(Packet(0, 1, [], [], Subcodec(0, 0, 4, 1, 2, rlex)), 4, 1, pixels, 16));
    }

    [TestMethod]
    public void NsCodecDecodesRleAndSubsampledTopDownColor()
    {
        byte[] ns = Bytes(w =>
        {
            w.Write(7u); w.Write(4u); w.Write(4u); w.Write(0u);
            w.Write((byte)1); w.Write((byte)1); w.Write((ushort)0);
            w.Write(new byte[] { 100, 100, 10, 120, 120, 120, 120 });
            w.Write(new byte[] { 10, 20, 30, 40 });
            w.Write(new byte[] { 0, 0, 0, 0 });
        });
        byte[] pixels = new byte[64];
        new GfxClearCodec().Decode(Packet(0, 0, [], [], Subcodec(0, 0, 8, 2, 1, ns)), 8, 2, pixels, 32);
        AssertBytes(new byte[] { 90, 100, 110, 255 }, pixels.AsSpan(0, 4));
        AssertBytes(new byte[] { 80, 120, 160, 255 }, pixels.AsSpan(60, 4));
    }

    [TestMethod]
    public void AnUnencodedNsCodecSubcodecLargerThanThreeBytesPerPixelIsAccepted()
    {
        // 1x1, no subsampling, all four planes unencoded: 20-byte header + 4 bytes, above the old w*h*3 bound.
        byte[] ns = Bytes(w =>
        {
            w.Write(1u); w.Write(1u); w.Write(1u); w.Write(1u);
            w.Write((byte)1); w.Write((byte)0); w.Write((ushort)0);
            w.Write(new byte[] { 100, 0, 0, 200 });
        });
        Assert.HasCount(24, ns);
        byte[] pixels = new byte[4];
        new GfxClearCodec().Decode(Packet(0, 0, [], [], Subcodec(0, 0, 1, 1, 1, ns)), 1, 1, pixels, 4);
        AssertBytes(new byte[] { 100, 100, 100 }, pixels.AsSpan(0, 3));
    }

    [TestMethod]
    public void ExtendedRunsAndEveryTruncationAreBounded()
    {
        byte[] packet = Packet(0, 0, [1, 2, 3, 255, 4, 0], [], []);
        byte[] pixels = new byte[16];
        new GfxClearCodec().Decode(packet, 4, 1, pixels, 16);
        for (int i = 0; i < packet.Length; i++)
        {
            byte[] truncated = packet[..i];
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => new GfxClearCodec().Decode(truncated, 4, 1, pixels, 16));
        }
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => new GfxClearCodec().Decode(Packet(0, 0, [1, 2, 3, 0], [], []), 4, 1, pixels, 16));
    }

    [TestMethod]
    public void DispatcherKeepsAlphaSeparateAndUsesTopDownPlanarRows()
    {
        using var dispatcher = new GfxCodecDispatcher();
        byte[] pixels = Enumerable.Repeat((byte)99, 16).ToArray();
        GfxDecodeRequest Request(ushort codec, byte[] data) => new(1, 0, codec, 0x21, 0, 0, 2, 2, data, pixels, 2, 2);
        dispatcher.Decode(Request(10, [0x20, 10, 20, 30, 40, 1, 2, 3, 4, 5, 6, 7, 8]));
        AssertBytes(new byte[] { 5, 1, 10, 99 }, pixels.AsSpan(0, 4));
        AssertBytes(new byte[] { 7, 3, 30, 99 }, pixels.AsSpan(8, 4));
        dispatcher.Decode(Request(12, [0x4C, 0x41, 1, 0, 77, 4]));
        AssertBytes(new byte[] { 5, 1, 10, 77 }, pixels.AsSpan(0, 4));
        dispatcher.Decode(Request(0, Enumerable.Repeat((byte)42, 16).ToArray()));
        AssertBytes(new byte[] { 42, 42, 42, 77 }, pixels.AsSpan(0, 4));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request(12, [0x4C, 0x41, 1, 0, 0, 5])));
    }

    private static byte[] Packet(byte flags, byte sequence, byte[] residual, byte[] bands, byte[] subcodecs, ushort glyph = 0) => Bytes(w =>
    {
        w.Write(flags); w.Write(sequence);
        if ((flags & 1) != 0)
        {
            w.Write(glyph);
        }

        w.Write(residual.Length); w.Write(bands.Length); w.Write(subcodecs.Length);
        w.Write(residual); w.Write(bands); w.Write(subcodecs);
    });
    private static byte[] Subcodec(ushort x, ushort y, ushort width, ushort height, byte codec, byte[] data) => Bytes(w =>
    {
        w.Write(x); w.Write(y); w.Write(width); w.Write(height); w.Write(data.Length); w.Write(codec); w.Write(data);
    });
    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
    private static void AssertBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string? message = null) =>
        Assert.AreSequenceEqual(expected, actual, message);
}
