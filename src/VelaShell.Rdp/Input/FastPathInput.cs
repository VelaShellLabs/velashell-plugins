using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Input;

/// <summary>
/// Builds Client Fast-Path Input PDUs (MS-RDPBCGR 2.2.8.1.2). These travel directly over
/// the transport with no MCS/share headers. Keys and mouse carry one event per PDU; text is batched.
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
        if (!down)
        {
            flags |= KbdFlagsRelease;
        }

        if ((scanCode & 0x100) != 0)
        {
            flags |= KbdFlagsExtended;
        }

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

    /// <summary>Most events one fast-path PDU can carry (numberEvents is 4 bits).</summary>
    internal const int MaxEventsPerPdu = 15;

    /// <summary>
    /// Text as Unicode key events, press and release per UTF-16 unit, batched up to <see cref="MaxEventsPerPdu"/>
    /// events per PDU (7 characters): typing a password or a pasted line used to cost two PDUs — two TLS records —
    /// per character. A character's press and release never straddle two PDUs.
    /// </summary>
    internal static IEnumerable<byte[]> UnicodeText(string text)
    {
        const int charsPerPdu = MaxEventsPerPdu / 2;
        for (int start = 0; start < text.Length; start += charsPerPdu)
        {
            int count = Math.Min(charsPerPdu, text.Length - start);
            var events = new ByteWriter(count * 6);
            for (int i = start; i < start + count; i++)
            {
                events.U8(EventUnicode << 5);
                events.U16LE(text[i]);
                events.U8((byte)((EventUnicode << 5) | KbdFlagsRelease));
                events.U16LE(text[i]);
            }
            yield return Frame(events.Span, count * 2);
        }
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

    /// <summary>Wraps events in the fast-path header (action=FASTPATH, numberEvents) and length.</summary>
    private static byte[] Frame(ReadOnlySpan<byte> events, int count = 1)
    {
        // header: action(2b)=0, numberEvents(4b) in bits 2–5; length is the per-style 1/2-byte total.
        int bodyLength = events.Length;
        int totalOneByte = 2 + bodyLength;           // header(1) + length(1) + body
        var w = new ByteWriter(totalOneByte + 1);
        w.U8((byte)(count << 2));
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
        w.Bytes(events);
        return w.ToArray();
    }
}
