using System.Buffers.Binary;
using ManagedCodecs;
using VelaShell.Rdp;
using VelaShell.Rdp.Graphics.Gfx;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class GfxAvcTests
{
    [TestMethod]
    public void Avc420DecodesIdrAndReferencedPFrameAndOnlyPaintsMetadataRegion()
    {
        using var dispatcher = new GfxCodecDispatcher();
        byte[] pixels = [.. Enumerable.Repeat((byte)17, 16 * 16 * 4)];
        dispatcher.Decode(Request(11, Block(Fixture("idr"), 2, 3, 8, 9), pixels));
        AssertPixel(pixels, 2, 3, 96, 96, 96, 17);
        AssertPixel(pixels, 1, 3, 17, 17, 17, 17);
        dispatcher.Decode(Request(11, Block(Fixture("predicted")), pixels));
        AssertPixel(pixels, 0, 0, 160, 160, 160, 17);
        dispatcher.DeleteSurface(1);
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request(11, Block(Fixture("predicted")), pixels)));
    }

    [TestMethod]
    [DataRow(14)]
    [DataRow(15)]
    public void Avc444MainAndAuxiliaryShareOnePredictionChain(int codec)
    {
        byte[] pixels = new byte[16 * 16 * 4];
        using var dispatcher = new GfxCodecDispatcher();
        dispatcher.Decode(Request((ushort)codec, Combined(Block(Fixture("idr")), Block(Fixture("predicted"))), pixels));
        // The P picture has no SPS/PPS and depends on the preceding main IDR.
        // Its Y=160 becomes the full-resolution U/V of an odd-row/odd-column pixel.
        AssertPixel(pixels, 1, 1, 155, 75, 146, 0);
    }

    [TestMethod]
    [DataRow(14)]
    [DataRow(15)]
    public void Avc444AuxiliaryOnlyUpdateKeepsPreviousLuma(int codec)
    {
        byte[] pixels = new byte[16 * 16 * 4];
        using var dispatcher = new GfxCodecDispatcher();
        dispatcher.Decode(Request((ushort)codec, Single(1, Block(Fixture("idr"))), pixels));
        AssertPixel(pixels, 1, 1, 96, 96, 96, 0);
        dispatcher.Decode(Request((ushort)codec, Single(2, Block(Fixture("predicted"))), pixels));
        AssertPixel(pixels, 1, 1, 155, 75, 146, 0);
        dispatcher.Reset();
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request((ushort)codec, Single(2, Block(Fixture("idr"))), pixels)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Avc444ReconstructsNonuniformChromaAcrossAllPackingPositions(bool version2)
    {
        using var main = new VideoFrame(32, 32, PixelFormat.Yuv420P);
        using var aux = new VideoFrame(32, 32, PixelFormat.Yuv420P);
        main.Planes[0].AsSpan().Fill(100);
        main.Planes[1].AsSpan().Fill(128);
        main.Planes[2].AsSpan().Fill(128);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                byte u = Chroma(x, y), v = (byte)(256 - u);
                if (version2)
                {
                    if ((x & 1) != 0)
                    {
                        aux.Planes[0][y * 32 + x / 2] = u;
                        aux.Planes[0][y * 32 + x / 2 + 16] = v;
                    }
                    else if ((y & 1) != 0)
                    {
                        int plane = (x & 2) == 0 ? 1 : 2;
                        aux.Planes[plane][y / 2 * 16 + x / 4] = u;
                        aux.Planes[plane][y / 2 * 16 + x / 4 + 8] = v;
                    }
                }
                else if ((y & 1) != 0)
                {
                    int row = y / 16 * 16 + y % 16 / 2;
                    aux.Planes[0][row * 32 + x] = u;
                    aux.Planes[0][(row + 8) * 32 + x] = v;
                }
                else if ((x & 1) != 0)
                {
                    aux.Planes[1][y / 2 * 16 + x / 2] = u;
                    aux.Planes[2][y / 2 * 16 + x / 2] = v;
                }
            }
        }

        var codec = new GfxAvcCodec(32, 32);
        GfxAvcRectangle[] rects = [new(0, 0, 32, 32)];
        codec.UpdateMain(main, rects);
        codec.UpdateAuxiliary(aux, rects, version2);
        byte[] pixels = [.. Enumerable.Repeat((byte)77, 32 * 32 * 4)];
        codec.Render(pixels, rects);
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                byte[] expected = Chroma(x, y) switch
                {
                    64 => [0, 82, 200, 77],
                    96 => [40, 91, 150, 77],
                    160 => [159, 109, 49, 77],
                    192 => [218, 118, 0, 77],
                    _ => throw new AssertFailedException()
                };
                AssertBytes(expected, pixels.AsSpan((y * 32 + x) * 4, 4), $"({x},{y})");
            }
        }
        // Rendering twice must not apply the inverse filter twice to persistent chroma.
        byte[] previous = [.. pixels];
        codec.Render(pixels, rects);
        AssertBytes(previous, pixels);
    }

    [TestMethod]
    public void InvalidMetadataAndOversizedSpsAreRejectedBeforeAllocation()
    {
        using var dispatcher = new GfxCodecDispatcher();
        byte[] pixels = new byte[16 * 16 * 4];
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request(11, Block(Fixture("idr"), 0, 0, 17, 16), pixels)));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request(14, Single(3, Block(Fixture("idr"))), pixels)));
        // Baseline SPS declares a 4096x16 picture on a 16x16 surface.
        byte[] oversizedSps = [0, 0, 0, 1, 0x67, 0x42, 0, 0x1E, 0xDA, 0, 0x40, 0x39];
        var decoder = new ManagedH264Decoder(16, 16);
        RdpProtocolException error = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Decode(oversizedSps));
        Assert.Contains("dimensions", error.Message);
        for (int length = 0; length < 5; length++)
        {
            byte[] data = new byte[length];
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => dispatcher.Decode(Request(11, data, pixels)));
        }
    }

    private static byte Chroma(int x, int y)
    {
        int index = ((y & 1) << 1) | (x & 1);
        byte[] values = (x / 2 + y / 2) % 2 == 0 ? [64, 96, 160, 192] : [192, 160, 96, 64];
        return values[index];
    }

    internal static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Gfx", name + ".h264"));
    internal static GfxDecodeRequest Request(ushort codec, byte[] data, byte[] pixels) => new(1, 0, codec, 0x20, 0, 0, 16, 16, data, pixels, 16, 16);
    internal static byte[] Block(byte[] h264, ushort left = 0, ushort top = 0, ushort right = 16, ushort bottom = 16)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u);
        writer.Write(left); writer.Write(top); writer.Write(right); writer.Write(bottom);
        writer.Write((ushort)0);
        writer.Write(h264);
        return stream.ToArray();
    }
    private static byte[] Single(int mode, byte[] stream)
    {
        byte[] result = new byte[stream.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)mode << 30);
        stream.CopyTo(result, 4);
        return result;
    }
    private static byte[] Combined(byte[] main, byte[] aux)
    {
        byte[] result = new byte[4 + main.Length + aux.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, main.Length);
        main.CopyTo(result, 4);
        aux.CopyTo(result, 4 + main.Length);
        return result;
    }
    private static void AssertPixel(byte[] pixels, int x, int y, byte b, byte g, byte r, byte a) =>
        AssertBytes([b, g, r, a], pixels.AsSpan((y * 16 + x) * 4, 4));
    private static void AssertBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string? message = null) =>
        Assert.AreSequenceEqual(expected, actual, message);
}
