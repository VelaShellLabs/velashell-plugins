using Avalonia.Controls;
using Avalonia.Input;
using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Input;
using VelaShell.Rdp.Wire;
using static VelaShell.Plugin.Rdp.Tests.RdpHeadlessTests;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpPointerAndInputTests
{
    private static uint Pixel(RdpPointer pointer, int x, int y) => BitConverter.ToUInt32(pointer.Bgra, (y * pointer.Width + x) * 4);

    [TestMethod]
    public void MonochromeMasksFollowTheFourRules()
    {
        // 4×1, rows padded to 2 bytes. AND 0011, XOR 0101 → black, white, transparent, inverted.
        RdpPointer? pointer = PointerDecoder.Decode(4, 1, 0, 0, 1, xorMask: [0x50, 0x00], andMask: [0x30, 0x00]);
        Assert.IsNotNull(pointer);
        Assert.AreEqual(0xFF000000u, Pixel(pointer, 0, 0), "AND 0, XOR 0: black");
        Assert.AreEqual(0xFFFFFFFFu, Pixel(pointer, 1, 0), "AND 0, XOR 1: white");
        Assert.AreEqual(0x00000000u, Pixel(pointer, 2, 0), "AND 1, XOR 0: transparent");
        Assert.AreEqual(0xFF000000u, Pixel(pointer, 3, 0), "AND 1, XOR 1: inverted → checkerboard (x+y odd: black)");
    }

    [TestMethod]
    public void ColourPointersAreBottomUpWithEvenPaddedRows()
    {
        // 2×2 at 24 bpp: xor rows are 6 bytes (already even); the FIRST row in the data is the BOTTOM of the image.
        byte[] xor = [0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF,    // bottom: red, red (BGR)
                      0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00];   // top: blue, blue
        RdpPointer? pointer = PointerDecoder.Decode(2, 2, 1, 1, 24, xor, andMask: [0, 0, 0, 0]);
        Assert.IsNotNull(pointer);
        Assert.AreEqual(0xFF0000FFu, Pixel(pointer, 0, 0), "top row blue");
        Assert.AreEqual(0xFFFF0000u, Pixel(pointer, 1, 1), "bottom row red");
        Assert.AreEqual(1, pointer.HotspotX);
    }

    [TestMethod]
    public void ThirtyTwoBitPointersKeepTheirAlpha()
    {
        byte[] xor = [0x10, 0x20, 0x30, 0x80];   // 1×1 BGRA, half transparent
        RdpPointer? pointer = PointerDecoder.Decode(1, 1, 0, 0, 32, xor, andMask: []);
        Assert.AreEqual(0x80302010u, Pixel(pointer!, 0, 0));
    }

    [TestMethod]
    public void CachedPointersAreTheSameInstanceAndBadOnesAreRefused()
    {
        var decoder = new PointerDecoder(21);
        var w = new ByteWriter(64);
        w.U16LE(3);                                    // cacheIndex
        w.U16LE(0); w.U16LE(0);                        // hot spot
        w.U16LE(1); w.U16LE(1);                        // 1×1
        w.U16LE(2); w.U16LE(4);                        // AND length (1 row × 2), XOR length (3 bytes padded to 4)
        w.Bytes([0x00, 0xFF, 0x00, 0x00]);             // green
        w.Bytes([0x00, 0x00]);
        RdpPointer? colour = decoder.FastPath(PointerDecoder.FastPathColor, w.Span);
        Assert.IsNotNull(colour);
        Assert.AreSame(colour, decoder.FastPath(PointerDecoder.FastPathCached, [3, 0]));
        Assert.AreSame(RdpPointer.Hidden, decoder.FastPath(PointerDecoder.FastPathNull, []));
        Assert.AreSame(RdpPointer.Default, decoder.FastPath(PointerDecoder.FastPathDefault, []));
        Assert.IsNull(decoder.FastPath(PointerDecoder.FastPathCached, [99, 0]), "an empty or out-of-range slot");
        Assert.IsNull(PointerDecoder.Decode(200, 200, 0, 0, 32, new byte[200 * 200 * 4], []), "bigger than a non-large pointer may be");
        Assert.IsNull(PointerDecoder.Decode(8, 8, 0, 0, 24, new byte[10], new byte[16]), "XOR mask too short");
        _ = Assert.Throws<RdpProtocolException>(() => decoder.FastPath(PointerDecoder.FastPathColor, [3, 0, 0]));   // truncated header
    }

    [TestMethod]
    public void TheDesktopShowsTheRemotePointer() => Run(async () =>
    {
        using var context = new VelaShell.PluginSdk.Testing.TestPluginContext();
        await using var view = new RdpWorkspaceView(new FakeRdpSession(), context, RdpSettings.From(RdpSettingsTests.Request(), new("en")), new("en"));
        var window = new Window { Content = view };
        window.Show();
        RdpPointer? image = PointerDecoder.Decode(2, 2, 1, 1, 32, new byte[16], []);
        view.Desktop.ApplyPointer(image!);
        Cursor? custom = view.Desktop.Cursor;
        Assert.IsNotNull(custom);
        view.Desktop.ApplyPointer(image!);
        Assert.AreSame(custom, view.Desktop.Cursor, "the same remote pointer reuses its platform cursor");
        view.Desktop.ApplyPointer(RdpPointer.Default);
        Assert.IsNull(view.Desktop.Cursor);
        view.Desktop.ApplyPointer(RdpPointer.Hidden);
        Assert.IsNotNull(view.Desktop.Cursor);
        window.Close();
    });

    [TestMethod]
    public void TypedTextIsBatchedSevenCharactersPerPdu()
    {
        byte[][] pdus = [.. FastPathInput.UnicodeText("abcdefghi")];
        Assert.HasCount(2, pdus);
        Assert.AreEqual(14 << 2, pdus[0][0], "14 events: 7 characters, press and release");
        Assert.AreEqual(4 << 2, pdus[1][0], "the remaining 2 characters");
        Assert.AreEqual(pdus[0].Length, pdus[0][1], "one-byte length covers the whole PDU");
        // First event: Unicode press of 'a'; second: its release.
        Assert.AreEqual(0x80, pdus[0][2]);
        Assert.AreEqual((byte)'a', pdus[0][3]);
        Assert.AreEqual(0x81, pdus[0][5]);
        Assert.AreEqual((byte)'a', pdus[0][6]);
    }
}
