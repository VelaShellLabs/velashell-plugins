namespace VelaShell.Plugin.Rdp.Managed.Wire;

/// <summary>
/// ASN.1 Basic Encoding Rules for the MCS Connect-Initial / Connect-Response envelope.
/// Only the tags MCS needs are implemented. Lengths use the definite form.
/// </summary>
internal static class Ber
{
    internal const byte ClassApplication = 0x40;
    internal const byte ClassContext = 0x80;
    internal const byte Constructed = 0x20;
    internal const byte TagBoolean = 0x01;
    internal const byte TagInteger = 0x02;
    internal const byte TagOctetString = 0x04;
    internal const byte TagEnumerated = 0x0A;
    internal const byte TagSequence = 0x10;

    internal static void WriteLength(ByteWriter w, int length)
    {
        if (length > 0xFF)
        {
            w.U8(0x80 | 2);
            w.U16BE((ushort)length);
        }
        else if (length > 0x7F)
        {
            w.U8(0x80 | 1);
            w.U8((byte)length);
        }
        else
        {
            w.U8((byte)length);
        }
    }

    internal static int ReadLength(ref ByteReader r)
    {
        byte first = r.U8();
        if ((first & 0x80) == 0) return first;
        int count = first & 0x7F;
        if (count is < 1 or > 4) throw new RdpProtocolException("Unsupported BER length form.");
        int length = 0;
        for (int i = 0; i < count; i++) length = (length << 8) | r.U8();
        return length;
    }

    /// <summary>Number of bytes <see cref="WriteLength"/> will emit for <paramref name="length"/>.</summary>
    internal static int LengthOfLength(int length) => length > 0xFF ? 3 : length > 0x7F ? 2 : 1;

    internal static void WriteUniversalTag(ByteWriter w, byte tag, bool constructed) =>
        w.U8((byte)((0x00 | (constructed ? Constructed : 0)) | (0x1F & tag)));

    internal static void WriteApplicationTag(ByteWriter w, int tag, int length)
    {
        if (tag > 30)
        {
            w.U8(ClassApplication | Constructed | 0x1F);
            w.U8((byte)tag);
        }
        else
        {
            w.U8((byte)(ClassApplication | Constructed | (0x1F & tag)));
        }
        WriteLength(w, length);
    }

    internal static int ReadApplicationTag(ref ByteReader r, int tag)
    {
        if (tag > 30)
        {
            byte first = r.U8();
            if (first != (ClassApplication | Constructed | 0x1F)) throw new RdpProtocolException("Unexpected BER application tag.");
            if (r.U8() != tag) throw new RdpProtocolException("Unexpected BER application tag id.");
        }
        else if (r.U8() != (ClassApplication | Constructed | (0x1F & tag)))
        {
            throw new RdpProtocolException("Unexpected BER application tag.");
        }
        return ReadLength(ref r);
    }

    internal static void WriteSequenceTag(ByteWriter w, int length)
    {
        w.U8(Constructed | (0x1F & TagSequence)); // universal class (0x00) | constructed | SEQUENCE = 0x30
        WriteLength(w, length);
    }

    internal static int ReadSequenceTag(ref ByteReader r)
    {
        if (r.U8() != (Constructed | TagSequence)) throw new RdpProtocolException("Expected BER sequence.");
        return ReadLength(ref r);
    }

    internal static void WriteInteger(ByteWriter w, uint value)
    {
        WriteUniversalTag(w, TagInteger, false);
        if (value < 0x80)
        {
            WriteLength(w, 1);
            w.U8((byte)value);
        }
        else if (value < 0x8000)
        {
            WriteLength(w, 2);
            w.U16BE((ushort)value);
        }
        else if (value < 0x800000)
        {
            WriteLength(w, 3);
            w.U8((byte)(value >> 16));
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
        if (r.U8() != TagInteger) throw new RdpProtocolException("Expected BER integer.");
        int length = ReadLength(ref r);
        if (length is < 1 or > 4) throw new RdpProtocolException("Unsupported BER integer length.");
        uint value = 0;
        for (int i = 0; i < length; i++) value = (value << 8) | r.U8();
        return value;
    }

    internal static void WriteBoolean(ByteWriter w, bool value)
    {
        WriteUniversalTag(w, TagBoolean, false);
        WriteLength(w, 1);
        w.U8(value ? (byte)0xFF : (byte)0x00);
    }

    internal static void WriteOctetStringTag(ByteWriter w, int length)
    {
        WriteUniversalTag(w, TagOctetString, false);
        WriteLength(w, length);
    }

    internal static void WriteOctetString(ByteWriter w, ReadOnlySpan<byte> data)
    {
        WriteOctetStringTag(w, data.Length);
        w.Bytes(data);
    }

    internal static ReadOnlySpan<byte> ReadOctetString(ref ByteReader r)
    {
        if (r.U8() != TagOctetString) throw new RdpProtocolException("Expected BER octet string.");
        int length = ReadLength(ref r);
        return r.Take(length);
    }

    internal static byte ReadEnumerated(ref ByteReader r)
    {
        if (r.U8() != TagEnumerated) throw new RdpProtocolException("Expected BER enumerated.");
        if (ReadLength(ref r) != 1) throw new RdpProtocolException("Unexpected BER enumerated length.");
        return r.U8();
    }

    /// <summary>Reads and discards an INTEGER (used to skip DomainParameters fields).</summary>
    internal static void SkipInteger(ref ByteReader r) => ReadInteger(ref r);
}
