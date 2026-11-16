using System.Buffers.Binary;

namespace VelaShell.Vnc.Wire;

/// <summary>Builds one client message in memory (big-endian), so a message always goes out in one write.</summary>
internal sealed class RfbWriter(int capacity = 32)
{
    private byte[] _buffer = new byte[capacity];

    internal int Length { get; private set; }

    internal ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    internal byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    private Span<byte> Take(int count)
    {
        if (Length + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        }

        Span<byte> span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }

    internal RfbWriter U8(int value)
    {
        Take(1)[0] = (byte)value;
        return this;
    }

    internal RfbWriter U16(int value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(Take(2), (ushort)value);
        return this;
    }

    internal RfbWriter U32(uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(Take(4), value);
        return this;
    }

    internal RfbWriter S32(int value) => U32(unchecked((uint)value));

    internal RfbWriter Zeros(int count)
    {
        Take(count).Clear();
        return this;
    }

    internal RfbWriter Bytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(Take(bytes.Length));
        return this;
    }
}
