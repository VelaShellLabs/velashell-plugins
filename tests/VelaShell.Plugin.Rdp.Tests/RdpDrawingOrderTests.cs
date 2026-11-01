using System.Runtime.InteropServices;
using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Drawing orders on the framebuffer (<see cref="IOrderTarget"/>), their negotiation and their dispatch from both
/// update paths. The order wire format itself is covered by <see cref="OrderDecoderTests"/>.
/// </summary>
[TestClass]
public sealed class RdpDrawingOrderTests
{
    private const uint Red = 0xFFFF0000, Blue = 0xFF0000FF, Black = 0xFF000000;

    private static uint Px(Framebuffer fb, int x, int y) => MemoryMarshal.Cast<byte, uint>(fb.Bgra.AsSpan())[y * fb.Width + x];

    /// <summary>A surface whose every pixel is distinct: 0xFF00yyxx.</summary>
    private static Framebuffer Numbered(int width, int height)
    {
        var fb = new Framebuffer(width, height);
        Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(fb.Bgra.AsSpan());
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = Original(x, y);
            }
        }

        return fb;
    }

    private static uint Original(int x, int y) => 0xFF000000u | (uint)(y << 8) | (uint)x;

    private static RdpFrameRegion TakeChanges(Framebuffer fb) => fb.CopyTo(new byte[fb.Width * fb.Height * 4], fb.Width * 4, full: false);

    // ---- negotiation ----

    /// <summary>Walks a Confirm Active PDU and returns each capability set's body by type.</summary>
    private static Dictionary<int, byte[]> CapabilitySets(byte[] pdu)
    {
        var r = new ByteReader(pdu);
        r.Skip(6);                    // share control header
        r.Skip(4 + 2);                // shareId, originatorId
        int sourceLength = r.U16LE();
        r.Skip(2 + sourceLength);     // lengthCombinedCapabilities, sourceDescriptor
        int count = r.U16LE();
        r.Skip(2);
        var sets = new Dictionary<int, byte[]>();
        for (int i = 0; i < count; i++)
        {
            int type = r.U16LE();
            int length = r.U16LE();
            sets[type] = r.Take(length - 4).ToArray();
        }

        return sets;
    }

    [TestMethod]
    public void OrdersAndTheBitmapCacheAreAdvertisedOnlyWhenEnabled()
    {
        Dictionary<int, byte[]> plain = CapabilitySets(Capabilities.BuildConfirmActive(1, 1007, 1024, 768, 32, false));
        Assert.IsFalse(plain.ContainsKey(19), "no revision 2 bitmap cache without orders");
        Assert.IsTrue(plain[3].AsSpan(32, 32).IndexOfAnyExcept((byte)0) < 0, "every orderSupport index is zero");

        Dictionary<int, byte[]> sets = CapabilitySets(Capabilities.BuildConfirmActive(1, 1007, 1024, 768, 32, false, drawingOrders: true));
        byte[] support = sets[3].AsSpan(32, 32).ToArray();
        int[] enabled = [.. Enumerable.Range(0, 32).Where(i => support[i] != 0)];
        CollectionAssert.AreEqual(new[] { 0x00, 0x01, 0x02, 0x03, 0x12 }, enabled, "DstBlt, PatBlt+OpaqueRect, ScrBlt, MemBlt, MultiOpaqueRect");

        byte[] cache = sets[19];
        Assert.HasCount(36, cache, "TS_BITMAPCACHE_CAPABILITYSET_REV2 is 40 bytes with its header");
        Assert.AreEqual(3, cache[3], "NumCellCaches");
        Assert.AreEqual(600u, BitConverter.ToUInt32(cache, 4));
        Assert.AreEqual(600u, BitConverter.ToUInt32(cache, 8));
        Assert.AreEqual(2048u, BitConverter.ToUInt32(cache, 12));
        Assert.AreEqual(0u, BitConverter.ToUInt32(cache, 16));
    }

    [TestMethod]
    public void TheDemandActiveCarriesTheSessionColourDepth()
    {
        var w = new ByteWriter(64);
        w.U32LE(1); w.U16LE(6); w.U16LE(0); w.Bytes("MSTSC\0"u8); w.U16LE(1); w.U16LE(0);
        w.U16LE(2); w.U16LE(28);                       // CAPSTYPE_BITMAP
        w.U16LE(24); w.U16LE(1); w.U16LE(1); w.U16LE(1);
        w.U16LE(1024); w.U16LE(768);
        w.Zeros(12);
        var r = new ByteReader(w.Span);
        Capabilities.DemandActive active = Capabilities.ReadDemandActive(ref r);
        Assert.AreEqual(24, active.Bpp, "a server capping a 32 bpp request at 24 encodes order colours in 24 bpp");
        Assert.AreEqual(1024, active.Width);
    }

    // ---- dispatch ----

    private static RdpConnection Connection(bool drawingOrders = true)
    {
        var connection = new RdpConnection(new RdpConnectOptions("h", 3389, "", "", "", 64, 48, 32, null, "h", [], DrawingOrders: drawingOrders));
        connection.StartSurface(32);
        return connection;
    }

    /// <summary>One OpaqueRect primary order with every field: TS_STANDARD | TS_TYPE_CHANGE, type 0x0A, fieldFlags 0x7F.</summary>
    private static byte[] OpaqueRect(int x, int y, int width, int height, byte red, byte green, byte blue)
    {
        var w = new ByteWriter(16);
        w.U8(0x09); w.U8(0x0A); w.U8(0x7F);
        w.U16LE((ushort)x); w.U16LE((ushort)y); w.U16LE((ushort)width); w.U16LE((ushort)height);
        w.U8(red); w.U8(green); w.U8(blue);
        return w.ToArray();
    }

    [TestMethod]
    public void OrdersArriveThroughTheFastPathAndTheSlowPath()
    {
        RdpConnection connection = Connection();
        byte[] order = OpaqueRect(2, 3, 4, 5, 0x11, 0x22, 0x33);
        var fast = new ByteWriter(32);
        fast.U16LE(1);                                 // numberOrders
        fast.Bytes(order);
        Assert.IsTrue(connection.ApplyFastPathUpdate(0x0, fast.Span), "an orders update paints");
        Assert.AreEqual(0xFF112233u, Px(connection.Framebuffer, 2, 3), "wire red, green, blue → BGRA");
        Assert.AreEqual(0xFF112233u, Px(connection.Framebuffer, 5, 7));
        Assert.AreEqual(Black, Px(connection.Framebuffer, 6, 7));

        var slow = new ByteWriter(32);
        slow.U16LE(0x0000);                            // UPDATETYPE_ORDERS
        slow.U16LE(0);
        slow.U16LE(1);
        slow.U16LE(0);
        slow.Bytes(OpaqueRect(10, 10, 1, 1, 0xFF, 0, 0));
        var r = new ByteReader(slow.Span);
        connection.ProcessDataPdu(ShareDataType.Update, ref r);
        Assert.AreEqual(Red, Px(connection.Framebuffer, 10, 10));
    }

    [TestMethod]
    public void OrdersAreIgnoredWhenTheyWereNotNegotiated()
    {
        RdpConnection connection = Connection(drawingOrders: false);
        var fast = new ByteWriter(32);
        fast.U16LE(1);
        fast.Bytes(OpaqueRect(0, 0, 4, 4, 0xFF, 0, 0));
        Assert.IsFalse(connection.ApplyFastPathUpdate(0x0, fast.Span));
        Assert.AreEqual(Black, Px(connection.Framebuffer, 0, 0));
    }

    [TestMethod]
    public void AReactivationKeepsDrawingOnTheNewSurface()
    {
        RdpConnection connection = Connection();
        Framebuffer before = connection.Framebuffer;
        connection.StartSurface(32);                   // a resize: new surface, primary-order state reset
        Assert.AreNotSame(before, connection.Framebuffer);
        var fast = new ByteWriter(32);
        fast.U16LE(1);
        fast.Bytes(OpaqueRect(1, 1, 1, 1, 0xFF, 0, 0));
        _ = connection.ApplyFastPathUpdate(0x0, fast.Span);
        Assert.AreEqual(Red, Px(connection.Framebuffer, 1, 1));
        Assert.AreEqual(Black, Px(before, 1, 1), "the old surface is left alone");
    }

    // ---- the framebuffer as an order target ----

    [TestMethod]
    public void FillsAreClippedToTheSurfaceAndTheBoundsAndRecordedAsChanged()
    {
        var fb = new Framebuffer(8, 8);
        _ = TakeChanges(fb);
        fb.FillRect(new OrderRect(-5, -5, 10, 10), Red, new OrderRect(1, 1, 2, 2));
        Assert.AreEqual(Red, Px(fb, 1, 1));
        Assert.AreEqual(Red, Px(fb, 2, 2));
        Assert.AreEqual(Black, Px(fb, 0, 0));
        Assert.AreEqual(Black, Px(fb, 3, 3), "outside the bounds");
        Assert.AreEqual(new RdpFrameRegion(1, 1, 2, 2), TakeChanges(fb), "only the painted area is copied to the UI");

        fb.FillRect(new OrderRect(100, 100, 5, 5), Red, null);
        Assert.IsTrue(TakeChanges(fb).IsEmpty, "an order entirely off the desktop changes nothing");
    }

    [TestMethod]
    public void ScreenCopiesAreOverlapSafeInEveryDirection()
    {
        // Scroll up one line: each row takes the row below it.
        Framebuffer up = Numbered(8, 8);
        up.ScreenCopy(0, 1, new OrderRect(0, 0, 8, 7), 0xCC, null);
        Assert.AreEqual(Original(3, 1), Px(up, 3, 0));
        Assert.AreEqual(Original(3, 7), Px(up, 3, 6));
        Assert.AreEqual(Original(3, 7), Px(up, 3, 7), "the last row is not a destination");

        // Scroll down one line: copying top-down would smear row 0 over everything.
        Framebuffer down = Numbered(8, 8);
        down.ScreenCopy(0, 0, new OrderRect(0, 1, 8, 7), 0xCC, null);
        Assert.AreEqual(Original(3, 0), Px(down, 3, 1));
        Assert.AreEqual(Original(3, 6), Px(down, 3, 7));

        // Sideways within the same rows.
        Framebuffer right = Numbered(8, 8);
        right.ScreenCopy(0, 0, new OrderRect(1, 0, 7, 8), 0xCC, null);
        Assert.AreEqual(Original(0, 4), Px(right, 1, 4));
        Assert.AreEqual(Original(6, 4), Px(right, 7, 4));
    }

    [TestMethod]
    public void AScreenCopyOnlyReadsPixelsOnTheSurface()
    {
        Framebuffer fb = Numbered(8, 8);
        fb.ScreenCopy(-2, 0, new OrderRect(0, 0, 4, 4), 0xCC, null);
        Assert.AreEqual(Original(0, 0), Px(fb, 0, 0), "its source is off the desktop: left as it was");
        Assert.AreEqual(Original(0, 0), Px(fb, 2, 0), "source x 0 lands where the source enters the desktop");
        Assert.AreEqual(Original(1, 3), Px(fb, 3, 3));

        fb.ScreenCopy(6, 6, new OrderRect(0, 0, 4, 4), 0xCC, null);   // the source runs off the bottom-right corner
        Assert.AreEqual(Original(7, 7), Px(fb, 1, 1));
        Assert.AreEqual(Original(0, 1), Px(fb, 2, 1), "nothing beyond the source's edge is copied (still the first copy's pixel)");
        Assert.AreEqual(Original(1, 2), Px(fb, 1, 2));
    }

    [TestMethod]
    [DataRow((byte)0xCC, 0x00C0C0C0u)]   // SRCCOPY: S
    [DataRow((byte)0xF0, 0x00F0F000u)]   // PATCOPY: P
    [DataRow((byte)0xAA, 0x00AA00AAu)]   // NOP: D
    [DataRow((byte)0x55, 0x0055FF55u)]   // DSTINVERT: ~D
    [DataRow((byte)0x88, 0x00800080u)]   // SRCAND: S & D
    [DataRow((byte)0xEE, 0x00EAC0EAu)]   // SRCPAINT: S | D
    [DataRow((byte)0x66, 0x006AC06Au)]   // SRCINVERT: S ^ D
    [DataRow((byte)0x5A, 0x005AF0AAu)]   // PATINVERT: P ^ D
    [DataRow((byte)0xC0, 0x00C0C000u)]   // MERGECOPY: P & S
    [DataRow((byte)0x00, 0x00000000u)]   // BLACKNESS
    [DataRow((byte)0xFF, 0x00FFFFFFu)]   // WHITENESS
    public void TernaryRasterOperationsFollowTheirTruthTable(byte rop3, uint expected)
    {
        const uint pattern = 0x00F0F000, source = 0x00C0C0C0, destination = 0x00AA00AA;
        Assert.AreEqual(expected | 0xFF000000u, Framebuffer.Rop3(rop3, pattern, source, destination), $"ROP 0x{rop3:X2}");
    }

    [TestMethod]
    public void PatternBrushesTileFromTheBrushOrigin()
    {
        var fb = new Framebuffer(16, 8);
        // One set bit (top-left of the 8×8 pattern) paints the background colour; clear bits the foreground.
        var brush = new OrderBrush(1, 0, 3, 0, [0x80, 0, 0, 0, 0, 0, 0, 0], Red, Blue);
        fb.PatBlt(new OrderRect(0, 0, 16, 8), 0xF0, brush, null);
        Assert.AreEqual(Blue, Px(fb, 1, 0), "the pattern starts at the brush origin");
        Assert.AreEqual(Blue, Px(fb, 9, 0), "and repeats every 8 pixels");
        Assert.AreEqual(Red, Px(fb, 0, 0));
        Assert.AreEqual(Red, Px(fb, 1, 1));

        // A null brush paints nothing.
        fb.PatBlt(new OrderRect(0, 0, 16, 8), 0xF0, new OrderBrush(0, 0, 1, 0, new byte[8], Black, Black), null);
        Assert.AreEqual(Blue, Px(fb, 1, 0));
    }

    [TestMethod]
    public void CachedBitmapsAreBlittedFromTheirSourceOffsetAndClipped()
    {
        byte[] tile = new byte[4 * 4 * 4];
        Span<uint> tilePixels = MemoryMarshal.Cast<byte, uint>(tile.AsSpan());
        for (int i = 0; i < 16; i++)
        {
            tilePixels[i] = 0xFF100000u | (uint)i;
        }

        var bitmap = new CachedBitmap(4, 4, tile);
        var fb = new Framebuffer(8, 8);
        fb.CachedBlit(bitmap, 1, 1, new OrderRect(2, 2, 4, 4), 0xCC, null);
        Assert.AreEqual(0xFF100005u, Px(fb, 2, 2), "tile (1,1)");
        Assert.AreEqual(0xFF10000Fu, Px(fb, 4, 4), "tile (3,3)");
        Assert.AreEqual(Black, Px(fb, 5, 5), "past the tile's edge nothing is drawn");

        fb.CachedBlit(bitmap, 0, 0, new OrderRect(6, 6, 4, 4), 0xCC, new OrderRect(0, 0, 7, 7));
        Assert.AreEqual(0xFF100000u, Px(fb, 6, 6));
        Assert.AreEqual(Black, Px(fb, 7, 7), "outside the bounds");
    }
}
