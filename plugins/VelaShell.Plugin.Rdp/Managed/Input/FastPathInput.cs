using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Input;

/// <summary>
/// Builds Client Fast-Path Input PDUs (MS-RDPBCGR 2.2.8.1.2). These travel directly over
/// the transport with no MCS/share headers. Each PDU here carries a single input event.
/// </summary>
internal static class FastPathInput
{
    private const int EventScancode = 0;
    private const int EventMouse = 1;
    private const int EventUnicode = 4;
    private const byte KbdFlagsRelease = 0x01;
    private const byte KbdFlagsExtended = 0x02;

    internal static byte[] Scancode(uint scanCode, bool down)
    {
        byte flags = 0;
        if (!down) flags |= KbdFlagsRelease;
        if ((scanCode & 0x100) != 0) flags |= KbdFlagsExtended;
        var e = new ByteWriter(4);
        e.U8((byte)((EventScancode << 5) | flags));
        e.U8((byte)(scanCode & 0xFF));
        return Frame(e.Span);
    }

    internal static byte[] Unicode(ushort code, bool down)
    {
        var e = new ByteWriter(4);
        e.U8((byte)((EventUnicode << 5) | (down ? 0 : KbdFlagsRelease)));
        e.U16LE(code);
        return Frame(e.Span);
    }

    internal static byte[] Mouse(uint pointerFlags, uint x, uint y)
    {
        var e = new ByteWriter(8);
        e.U8(EventMouse << 5);
        e.U16LE((ushort)pointerFlags);
        e.U16LE((ushort)x);
        e.U16LE((ushort)y);
        return Frame(e.Span);
    }

    /// <summary>Wraps one event in the fast-path header (action=FASTPATH, numEvents=1) and length.</summary>
    private static byte[] Frame(ReadOnlySpan<byte> singleEvent)
    {
        // header: action(2b)=0, numberEvents(4b)=1 → 0x04; length is the per-style 1/2-byte total.
        int bodyLength = singleEvent.Length;
        int totalOneByte = 2 + bodyLength;           // header(1) + length(1) + body
        var w = new ByteWriter(totalOneByte + 1);
        w.U8(0x04);
        if (totalOneByte <= 0x7F)
        {
            w.U8((byte)totalOneByte);
        }
        else
        {
            int total = 3 + bodyLength;              // header(1) + length(2) + body
            w.U8((byte)(0x80 | (total >> 8)));
            w.U8((byte)(total & 0xFF));
        }
        w.Bytes(singleEvent);
        return w.ToArray();
    }
}
