using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Rdp.Wire;

/// <summary>
/// Grow-only little/big-endian writer. RDP mixes endianness: TPKT/X.224 and the
/// ASN.1 PER/BER layers are big-endian, while most MS-RDPBCGR structures are
/// little-endian. Each method states its endianness in its name.
/// </summary>
internal sealed class ByteWriter
{
    private byte[] _buffer;

    internal ByteWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }

    internal int Length { get; private set; }

    internal void U8(byte value)
    {
        Ensure(1);
        _buffer[Length++] = value;
    }

    internal void U16BE(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(Length), value);
        Length += 2;
    }

    internal void U16LE(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(Length), value);
        Length += 2;
    }

    internal void U32BE(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(Length), value);
        Length += 4;
    }

    internal void U32LE(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Length), value);
        Length += 4;
    }

    internal void Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(Length));
        Length += value.Length;
    }

    internal void Zeros(int count)
    {
        Ensure(count);
        _buffer.AsSpan(Length, count).Clear();
        Length += count;
    }

    /// <summary>Writes a fixed-length UTF-16LE field, truncating/padding to <paramref name="byteCount"/> bytes.</summary>
    internal void Unicode(string value, int byteCount)
    {
        Ensure(byteCount);
        Span<byte> target = _buffer.AsSpan(Length, byteCount);
        target.Clear();
        int written = Encoding.Unicode.GetBytes(value.AsSpan(0, Math.Min(value.Length, byteCount / 2)), target);
        _ = written;
        Length += byteCount;
    }

    /// <summary>Reserves two bytes for a length placeholder and returns its offset for <see cref="PatchU16LE"/>.</summary>
    internal int Reserve16()
    {
        int offset = Length;
        U16LE(0);
        return offset;
    }

    internal void PatchU16LE(int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(offset), value);

    internal void PatchU16BE(int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(offset), value);

    internal byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    internal ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

    private void Ensure(int count)
    {
        if (Length + count <= _buffer.Length)
        {
            return;
        }

        int capacity = _buffer.Length * 2;
        while (capacity < Length + count)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }
}

/// <summary>
/// Bounds-checked sequential reader over a received buffer. Every accessor throws
/// <see cref="RdpProtocolException"/> rather than <see cref="IndexOutOfRangeException"/>
/// so a malformed server PDU surfaces as a protocol fault, never an unhandled crash.
/// </summary>
internal ref struct ByteReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    internal readonly int Remaining => _data.Length - Position;
    internal int Position { get; private set; }

    internal byte U8() => Take(1)[0];

    internal ushort U16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    internal ushort U16LE() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    internal uint U32BE() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    internal uint U32LE() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    internal ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new RdpProtocolException("Truncated RDP PDU.");
        }

        ReadOnlySpan<byte> slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    internal void Skip(int count) => Take(count);

    internal readonly ReadOnlySpan<byte> Peek(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new RdpProtocolException("Truncated RDP PDU.");
        }

        return _data.Slice(Position, count);
    }

    internal ReadOnlySpan<byte> Rest() => Take(Remaining);
}
