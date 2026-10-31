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
    private int _length;

    internal ByteWriter(int capacity = 256) => _buffer = new byte[Math.Max(16, capacity)];

    internal int Length => _length;

    internal void U8(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    internal void U16BE(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    internal void U16LE(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    internal void U32BE(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    internal void U32LE(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    internal void Bytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    internal void Zeros(int count)
    {
        Ensure(count);
        _buffer.AsSpan(_length, count).Clear();
        _length += count;
    }

    /// <summary>Writes a fixed-length UTF-16LE field, truncating/padding to <paramref name="byteCount"/> bytes.</summary>
    internal void Unicode(string value, int byteCount)
    {
        Ensure(byteCount);
        Span<byte> target = _buffer.AsSpan(_length, byteCount);
        target.Clear();
        int written = Encoding.Unicode.GetBytes(value.AsSpan(0, Math.Min(value.Length, byteCount / 2)), target);
        _ = written;
        _length += byteCount;
    }

    /// <summary>Reserves two bytes for a length placeholder and returns its offset for <see cref="PatchU16LE"/>.</summary>
    internal int Reserve16()
    {
        int offset = _length;
        U16LE(0);
        return offset;
    }

    internal void PatchU16LE(int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(offset), value);

    internal void PatchU16BE(int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(offset), value);

    internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    internal ReadOnlySpan<byte> Span => _buffer.AsSpan(0, _length);

    private void Ensure(int count)
    {
        if (_length + count <= _buffer.Length) return;
        int capacity = _buffer.Length * 2;
        while (capacity < _length + count) capacity *= 2;
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
    private int _position;

    internal readonly int Remaining => _data.Length - _position;
    internal readonly int Position => _position;

    internal byte U8() => Take(1)[0];

    internal ushort U16BE() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    internal ushort U16LE() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    internal uint U32BE() => BinaryPrimitives.ReadUInt32BigEndian(Take(4));
    internal uint U32LE() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    internal ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new RdpProtocolException("Truncated RDP PDU.");
        ReadOnlySpan<byte> slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    internal void Skip(int count) => Take(count);

    internal readonly ReadOnlySpan<byte> Peek(int count)
    {
        if (count < 0 || count > Remaining) throw new RdpProtocolException("Truncated RDP PDU.");
        return _data.Slice(_position, count);
    }

    internal ReadOnlySpan<byte> Rest() => Take(Remaining);
}
