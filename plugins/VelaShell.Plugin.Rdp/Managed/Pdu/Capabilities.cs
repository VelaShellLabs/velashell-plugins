using System.Text;
using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Pdu;

/// <summary>
/// Parses the Server Demand Active PDU and builds the Client Confirm Active PDU
/// (MS-RDPBCGR 2.2.1.13). The order capability advertises no drawing-order support,
/// which forces the server to send Bitmap Update PDUs that the managed client decodes.
/// </summary>
internal static class Capabilities
{
    /// <summary>Reads a Demand Active body (after the share control header) and returns the share id.</summary>
    internal static uint ReadDemandActiveShareId(ref ByteReader r)
    {
        uint shareId = r.U32LE();
        ushort lenSource = r.U16LE();
        ushort lenCaps = r.U16LE();
        r.Skip(lenSource);
        _ = lenCaps;
        // The remaining capability sets are not needed; the client renders via bitmap updates.
        return shareId;
    }

    internal static byte[] BuildConfirmActive(uint shareId, ushort userId, int width, int height, int bpp, bool hasChannels)
    {
        var caps = new ByteWriter(512);
        ushort count = 0;
        void Set(Action<ByteWriter> write) { write(caps); count++; }

        Set(w => General(w));
        Set(w => Bitmap(w, width, height, bpp));
        Set(w => Order(w));
        Set(w => Pointer(w));
        Set(w => Input(w));
        Set(w => Brush(w));
        Set(w => GlyphCache(w));
        Set(w => Sound(w));
        if (hasChannels) Set(w => VirtualChannel(w));

        byte[] source = "MSTSC\0"u8.ToArray();
        int combined = 4 + caps.Length; // numberCapabilities(2) + pad(2) + capability sets

        var body = new ByteWriter(16 + source.Length + caps.Length);
        body.U32LE(shareId);
        body.U16LE(0x03EA);               // originatorId = server channel
        body.U16LE((ushort)source.Length);
        body.U16LE((ushort)combined);
        body.Bytes(source);
        body.U16LE(count);
        body.U16LE(0);                    // pad2octets
        body.Bytes(caps.Span);

        return ShareControl.BuildControlPdu(ShareControlType.ConfirmActive, userId, body.Span);
    }

    private static void Header(ByteWriter w, ushort type, ushort length)
    {
        w.U16LE(type);
        w.U16LE(length);
    }

    private static void General(ByteWriter w)
    {
        Header(w, 1, 24);
        w.U16LE(1);        // osMajorType = WINDOWS
        w.U16LE(3);        // osMinorType = WINDOWS_NT
        w.U16LE(0x0200);   // protocolVersion
        w.U16LE(0);        // pad
        w.U16LE(0);        // compressionTypes
        w.U16LE(0x0001 | 0x0004); // extraFlags: FASTPATH_OUTPUT_SUPPORTED | LONG_CREDENTIALS_SUPPORTED
        w.U16LE(0);        // updateCapabilityFlag
        w.U16LE(0);        // remoteUnshareFlag
        w.U16LE(0);        // compressionLevel
        w.U8(0);           // refreshRectSupport
        w.U8(0);           // suppressOutputSupport
    }

    private static void Bitmap(ByteWriter w, int width, int height, int bpp)
    {
        Header(w, 2, 28);
        w.U16LE((ushort)bpp);   // preferredBitsPerPixel
        w.U16LE(1);             // receive1BitPerPixel
        w.U16LE(1);             // receive4BitsPerPixel
        w.U16LE(1);             // receive8BitsPerPixel
        w.U16LE((ushort)width);
        w.U16LE((ushort)height);
        w.U16LE(0);             // pad
        w.U16LE(1);             // desktopResizeFlag
        w.U16LE(1);             // bitmapCompressionFlag
        w.U8(0);                // highColorFlags
        w.U8(0);                // drawingFlags
        w.U16LE(1);             // multipleRectangleSupport
        w.U16LE(0);             // pad
    }

    private static void Order(ByteWriter w)
    {
        Header(w, 3, 88);
        w.Zeros(16);            // terminalDescriptor
        w.U32LE(0);             // pad4octetsA
        w.U16LE(1);             // desktopSaveXGranularity
        w.U16LE(20);            // desktopSaveYGranularity
        w.U16LE(0);             // pad2octetsA
        w.U16LE(1);             // maximumOrderLevel
        w.U16LE(0);             // numberFonts
        w.U16LE(0x002A);        // orderFlags: NEGOTIATE | ZEROBOUNDSDELTAS | COLORINDEX
        w.Zeros(32);            // orderSupport: no orders → server sends bitmap updates
        w.U16LE(0);             // textFlags
        w.U16LE(0);             // orderSupportExFlags
        w.U32LE(0);             // pad4octetsB
        w.U32LE(230400);        // desktopSaveSize
        w.U16LE(0);             // pad2octetsC
        w.U16LE(0);             // pad2octetsD
        w.U16LE(0);             // textANSICodePage
        w.U16LE(0);             // pad2octetsE
    }

    private static void Pointer(ByteWriter w)
    {
        Header(w, 8, 10);
        w.U16LE(1);             // colorPointerFlag
        w.U16LE(20);            // colorPointerCacheSize
        w.U16LE(21);            // pointerCacheSize
    }

    private static void Input(ByteWriter w)
    {
        Header(w, 13, 88);
        // INPUT_FLAG_SCANCODES | INPUT_FLAG_MOUSEX | INPUT_FLAG_FASTPATH_INPUT | INPUT_FLAG_UNICODE | INPUT_FLAG_FASTPATH_INPUT2
        w.U16LE(0x0001 | 0x0004 | 0x0008 | 0x0010 | 0x0020);
        w.U16LE(0);             // pad2octetsA
        w.U32LE(0x409);         // keyboardLayout (US)
        w.U32LE(4);             // keyboardType
        w.U32LE(0);             // keyboardSubType
        w.U32LE(12);            // keyboardFunctionKey
        w.Zeros(64);            // imeFileName
    }

    private static void Brush(ByteWriter w)
    {
        Header(w, 15, 8);
        w.U32LE(0);             // brushSupportLevel = BRUSH_DEFAULT
    }

    private static void GlyphCache(ByteWriter w)
    {
        Header(w, 16, 52);
        // 10 glyph cache entries (cacheEntries, cacheMaximumCellSize)
        ushort[] entries = [254, 4, 254, 4, 254, 8, 254, 8, 254, 16, 254, 32, 254, 64, 254, 128, 254, 256, 64, 2048];
        foreach (ushort value in entries) w.U16LE(value);
        w.U32LE(0);             // FragCache
        w.U16LE(0);             // GlyphSupportLevel = NONE
        w.U16LE(0);             // pad2octets
    }

    private static void Sound(ByteWriter w)
    {
        Header(w, 12, 8);
        w.U16LE(0);             // soundFlags
        w.U16LE(0);             // pad2octetsA
    }

    private static void VirtualChannel(ByteWriter w)
    {
        Header(w, 20, 12);
        w.U32LE(0);             // flags = VCCAPS_DEFAULT (no compression)
        w.U32LE(1600);          // VCChunkSize
    }

    private static byte[] AsciiZ(string value) => Encoding.ASCII.GetBytes(value + "\0");
}
