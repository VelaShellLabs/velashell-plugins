using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>Bounded NDR32 type serialization (MS-RPCE 2.2.6) for MS-RDPESC structures.</summary>
internal sealed class SmartCardNdr
{
    internal const int MaxBuffer = 66560;
    private readonly byte[] _data;
    private int _offset = 16;

    internal SmartCardNdr(byte[] data)
    {
        if (data.Length < 16 || data.Length > 1024 * 1024 || data[0] != 1 || data[1] != 0x10 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) != data.Length - 16 || (data.Length & 7) != 0)
        {
            throw new InvalidDataException("Invalid smart-card serialization header.");
        }
        _data = data;
    }

    internal uint U32()
    {
        Align();
        return BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
    }

    internal bool Boolean() => U32() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid smart-card Boolean.")
    };

    internal int Count(int maximum = MaxBuffer)
    {
        uint count = U32();
        if (count > maximum) { throw new InvalidDataException("Smart-card buffer exceeds its limit."); }
        return (int)count;
    }

    internal ReadOnlySpan<byte> Bytes(int count)
    {
        if (count < 0 || count > _data.Length - _offset) { throw new InvalidDataException("Truncated smart-card request."); }
        ReadOnlySpan<byte> value = _data.AsSpan(_offset, count);
        _offset += count;
        return value;
    }

    internal void Align() => _ = Bytes((-_offset) & 3);

    internal (int Length, bool Present) BlobHeader(int maximum = MaxBuffer)
    {
        int length = Count(maximum);
        bool present = U32() != 0;
        if (!present && length != 0) { throw new InvalidDataException("Missing smart-card referent."); }
        return (length, present);
    }

    internal byte[] Blob((int Length, bool Present) header)
    {
        if (!header.Present) { return []; }
        if (Count() != header.Length) { throw new InvalidDataException("Inconsistent smart-card conformant array."); }
        return Bytes(header.Length).ToArray();
    }

    internal ulong Handle((int Length, bool Present) header)
    {
        byte[] value = Blob(header);
        return value.Length == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(value) : 0;
    }

    internal string String(bool wide)
    {
        int max = Count(32768);
        uint offset = U32();
        int actual = Count(32768);
        if (offset != 0 || actual == 0 || actual > max) { throw new InvalidDataException("Invalid smart-card string dimensions."); }
        ReadOnlySpan<byte> value = Bytes(checked(actual * (wide ? 2 : 1)));
        if (value[^1] != 0 || (wide && value[^2] != 0)) { throw new InvalidDataException("Unterminated smart-card string."); }
        string text = Decode(value[..^(wide ? 2 : 1)], wide);
        if (text.Contains('\0')) { throw new InvalidDataException("Embedded smart-card string terminator."); }
        return text;
    }

    internal static string Decode(ReadOnlySpan<byte> bytes, bool wide)
    {
        if (wide && (bytes.Length & 1) != 0) { throw new InvalidDataException("Odd UTF-16 smart-card buffer."); }
        return (wide ? new UnicodeEncoding(false, false, true) : Encoding.ASCII).GetString(bytes);
    }

    internal void Complete()
    {
        if (_data.Length - _offset != ((-_offset) & 7)) { throw new InvalidDataException("Unexpected smart-card request data."); }
    }

    internal static byte[] Encode(ByteWriter body)
    {
        var writer = new ByteWriter(body.Length + 24);
        writer.U8(1); writer.U8(0x10); writer.U16LE(8); writer.U32LE(0xCCCCCCCC);
        writer.U32LE((uint)((body.Length + 7) & ~7)); writer.U32LE(0);
        writer.Bytes(body.Span); writer.Zeros((-body.Length) & 7);
        return writer.ToArray();
    }

    internal static void BlobHeader(ByteWriter writer, int count, bool present)
    {
        writer.U32LE((uint)count); writer.U32LE(present ? 0x00020000u : 0);
    }

    internal static void Blob(ByteWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Zeros((-writer.Length) & 3);
        writer.U32LE((uint)value.Length); writer.Bytes(value);
    }

    internal static void Handle(ByteWriter writer, ulong value)
    {
        writer.U32LE(8); writer.U32LE((uint)value); writer.U32LE((uint)(value >> 32));
    }
}
