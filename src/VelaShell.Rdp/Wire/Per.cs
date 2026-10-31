namespace VelaShell.Rdp.Wire;

/// <summary>
/// ASN.1 Packed Encoding Rules (ALIGNED variant) for the subset MCS and GCC use.
/// Mirrors FreeRDP's <c>per.c</c>, which is the proven-interoperable encoding; the
/// routines are intentionally minimal and big-endian as PER requires.
/// </summary>
internal static class Per
{
    internal static void WriteLength(ByteWriter w, int length)
    {
        if (length > 0x7F)
        {
            w.U8((byte)(0x80 | (length >> 8)));
            w.U8((byte)(length & 0xFF));
        }
        else
        {
            w.U8((byte)length);
        }
    }

    internal static int ReadLength(ref ByteReader r)
    {
        byte first = r.U8();
        return (first & 0x80) != 0 ? ((first & 0x7F) << 8) | r.U8() : first;
    }

    internal static void WriteChoice(ByteWriter w, byte choice) => w.U8(choice);
    internal static byte ReadChoice(ref ByteReader r) => r.U8();
    internal static void WriteSelection(ByteWriter w, byte selection) => w.U8(selection);
    internal static byte ReadSelection(ref ByteReader r) => r.U8();
    internal static void WriteNumberOfSets(ByteWriter w, byte number) => w.U8(number);
    internal static byte ReadNumberOfSets(ref ByteReader r) => r.U8();
    internal static void WritePadding(ByteWriter w, int length) => w.Zeros(length);

    internal static void WriteEnumerated(ByteWriter w, byte enumerated) => w.U8(enumerated);

    internal static byte ReadEnumerated(ref ByteReader r, byte count)
    {
        byte value = r.U8();
        if (value >= count)
        {
            throw new RdpProtocolException("PER enumerated value out of range.");
        }

        return value;
    }

    internal static void WriteInteger(ByteWriter w, uint value)
    {
        if (value <= 0xFF)
        {
            WriteLength(w, 1);
            w.U8((byte)value);
        }
        else if (value <= 0xFFFF)
        {
            WriteLength(w, 2);
            w.U16BE((ushort)value);
        }
        else
        {
            WriteLength(w, 4);
            w.U32BE(value);
        }
    }

    internal static uint ReadInteger(ref ByteReader r)
    {
        int length = ReadLength(ref r);
        return length switch
        {
            0 => 0,
            1 => r.U8(),
            2 => r.U16BE(),
            4 => r.U32BE(),
            _ => throw new RdpProtocolException("Unsupported PER integer length.")
        };
    }

    internal static void WriteInteger16(ByteWriter w, ushort value, ushort min) => w.U16BE((ushort)(value - min));

    internal static ushort ReadInteger16(ref ByteReader r, ushort min)
    {
        int value = r.U16BE() + min;
        if (value > ushort.MaxValue)
        {
            throw new RdpProtocolException("PER integer16 overflow.");
        }

        return (ushort)value;
    }

    /// <summary>Encodes a 6-element object identifier, e.g. {0 0 20 124 0 1} for GCC.</summary>
    internal static void WriteObjectIdentifier(ByteWriter w, ReadOnlySpan<byte> oid)
    {
        WriteLength(w, 5);
        w.U8((byte)(((oid[0] << 4) & 0xF0) | (oid[1] & 0x0F)));
        w.U8(oid[2]);
        w.U8(oid[3]);
        w.U8(oid[4]);
        w.U8(oid[5]);
    }

    internal static void ReadObjectIdentifier(ref ByteReader r, ReadOnlySpan<byte> expected)
    {
        int length = ReadLength(ref r);
        if (length != 5)
        {
            throw new RdpProtocolException("Unexpected PER object identifier length.");
        }

        ReadOnlySpan<byte> body = r.Take(5);
        byte t0 = (byte)((body[0] >> 4) & 0x0F);
        byte t1 = (byte)(body[0] & 0x0F);
        if (t0 != expected[0] || t1 != expected[1] || body[1] != expected[2] ||
            body[2] != expected[3] || body[3] != expected[4] || body[4] != expected[5])
        {
            throw new RdpProtocolException("Unexpected GCC object identifier.");
        }
    }

    internal static void WriteOctetString(ByteWriter w, ReadOnlySpan<byte> data, int min)
    {
        int length = data.Length - min >= 0 ? data.Length - min : min;
        WriteLength(w, length);
        w.Bytes(data);
    }

    /// <summary>Reads a PER octet string whose constrained minimum length is <paramref name="min"/>.</summary>
    internal static ReadOnlySpan<byte> ReadOctetString(ref ByteReader r, int min)
    {
        int length = ReadLength(ref r) + min;
        return r.Take(length);
    }

    /// <summary>Encodes a NumericString (two BCD-packed digits per byte), e.g. the conference name "1".</summary>
    internal static void WriteNumericString(ByteWriter w, string digits, int min)
    {
        int length = digits.Length - min >= 0 ? digits.Length - min : min;
        WriteLength(w, length);
        for (int i = 0; i < digits.Length; i += 2)
        {
            int c1 = (digits[i] - '0') % 10;
            int c2 = i + 1 < digits.Length ? (digits[i + 1] - '0') % 10 : 0;
            w.U8((byte)((c1 << 4) | c2));
        }
    }

    /// <summary>Number of bytes <see cref="WriteLength"/> emits for <paramref name="length"/>.</summary>
    internal static int DeterminantSize(int length) => length > 0x7F ? 2 : 1;
}
