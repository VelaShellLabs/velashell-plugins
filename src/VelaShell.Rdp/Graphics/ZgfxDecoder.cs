using System.Buffers.Binary;

namespace VelaShell.Rdp.Graphics;

/// <summary>MS-RDPEGFX 3.1.9.1: segmented RDP8 bulk compression, with connection-local history.</summary>
internal sealed class ZgfxDecoder
{
    internal const int MaxOutputBytes = 32 * 1024 * 1024;
    private const int HistorySize = 2_500_000;
    private const int SegmentLimit = 65_535;
    private readonly byte[] _history = new byte[HistorySize];
    private readonly byte[] _segment = new byte[SegmentLimit];
    private int _cursor, _historyCount;

    private readonly record struct Token(int Bits, int Prefix, int Extra, int Value, bool Match = false);
    private static readonly Token[] Tokens =
    [
        new(1, 0, 8, 0), new(5, 17, 5, 0, true), new(5, 18, 7, 32, true),
        new(5, 19, 9, 160, true), new(5, 20, 10, 672, true), new(5, 21, 12, 1696, true),
        new(5, 24, 0, 0), new(5, 25, 0, 1),
        new(6, 44, 14, 5792, true), new(6, 45, 15, 22176, true),
        new(6, 52, 0, 2), new(6, 53, 0, 3), new(6, 54, 0, 255),
        new(7, 92, 18, 54944, true), new(7, 93, 20, 317088, true),
        new(7, 110, 0, 4), new(7, 111, 0, 5), new(7, 112, 0, 6), new(7, 113, 0, 7),
        new(7, 114, 0, 8), new(7, 115, 0, 9), new(7, 116, 0, 10), new(7, 117, 0, 11),
        new(7, 118, 0, 0x3a), new(7, 119, 0, 0x3b), new(7, 120, 0, 0x3c),
        new(7, 121, 0, 0x3d), new(7, 122, 0, 0x3e), new(7, 123, 0, 0x3f),
        new(7, 124, 0, 0x40), new(7, 125, 0, 0x80),
        new(8, 188, 20, 1365664, true), new(8, 189, 21, 2414240, true),
        new(8, 252, 0, 0x0c), new(8, 253, 0, 0x38), new(8, 254, 0, 0x39), new(8, 255, 0, 0x66)
    ];

    private static readonly Token?[] Huffman = BuildHuffman();

    private static Token?[] BuildHuffman()
    {
        var table = new Token?[512];
        foreach (Token token in Tokens) { table[(1 << token.Bits) + token.Prefix] = token; }
        return table;
    }

    internal byte[] Decode(ReadOnlySpan<byte> input, int maxOutputBytes = MaxOutputBytes)
    {
        if (maxOutputBytes is < 0 or > MaxOutputBytes) { throw new ArgumentOutOfRangeException(nameof(maxOutputBytes)); }
        if (input.Length is < 2 or > MaxOutputBytes) { throw Error("Invalid message size."); }
        if (input[0] == 0xe0)
        {
            int count = DecodeSegment(input[1..]);
            if (count > maxOutputBytes) { throw Error("Output exceeds the channel message limit."); }
            return _segment.AsSpan(0, count).ToArray();
        }
        if (input[0] != 0xe1 || input.Length < 7) { throw Error("Invalid segmentation header."); }
        int segments = BinaryPrimitives.ReadUInt16LittleEndian(input[1..]);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(input[3..]);
        if (segments == 0 || size > maxOutputBytes || size > (long)segments * SegmentLimit
            || (long)segments * 5 > input.Length - 7) { throw Error("Invalid multipart size."); }
        // Validate every encoded segment before allocating the declared output buffer.
        int offset = 7;
        for (int i = 0; i < segments; i++)
        {
            if (input.Length - offset < 4) { throw Error("Truncated segment size."); }
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(input[offset..]);
            offset += 4;
            if (length < 1 || length > SegmentLimit + 1000 || length > input.Length - offset)
            { throw Error("Invalid segment size."); }
            offset += (int)length;
        }
        if (offset != input.Length) { throw Error("Trailing multipart data."); }
        byte[] output = new byte[(int)size];
        offset = 7;
        int written = 0;
        for (int i = 0; i < segments; i++)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(input[offset..]);
            offset += 4;
            int count = DecodeSegment(input.Slice(offset, length));
            if (count > output.Length - written) { throw Error("Multipart output exceeds its declared size."); }
            _segment.AsSpan(0, count).CopyTo(output.AsSpan(written));
            written += count;
            offset += length;
        }
        if (written != output.Length) { throw Error("Multipart output does not match its declared size."); }
        return output;
    }

    private int DecodeSegment(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 1 || segment.Length > SegmentLimit + 1000 || (segment[0] & 0x0f) != 4)
        { throw Error("Invalid RDP8 segment header."); }
        ReadOnlySpan<byte> data = segment[1..];
        int count = 0;
        if ((segment[0] & 0x20) == 0)
        {
            if (data.Length > SegmentLimit) { throw Error("Uncompressed segment exceeds 65535 bytes."); }
            foreach (byte value in data) { Emit(value, ref count); }
            return count;
        }
        if (data.Length == 0 || data[^1] > 7) { throw Error("Invalid bitstream trailer."); }
        int bits = (data.Length - 1) * 8 - data[^1];
        if (bits < 0) { throw Error("Invalid padding length."); }
        var reader = new Bits(data[..^1], bits);
        while (reader.Remaining > 0)
        {
            int prefix = 0;
            Token? found = null;
            for (int length = 1; length <= 8 && found is null; length++)
            {
                prefix = (prefix << 1) | reader.Read(1);
                found = Huffman[(1 << length) + prefix];
            }
            if (found is not { } t) { throw Error("Reserved Huffman token."); }
            int value = t.Value + reader.Read(t.Extra);
            if (!t.Match)
            {
                if (t.Bits == 1 && IsShortLiteral(value)) { throw Error("Reserved literal encoding."); }
                Emit((byte)value, ref count);
                continue;
            }
            if (value == 0)
            {
                int length = reader.Read(15);
                reader.Align();
                if (length > SegmentLimit - count || (long)length * 8 > reader.Remaining)
                { throw Error("Invalid unencoded run."); }
                for (int i = 0; i < length; i++) { Emit((byte)reader.Read(8), ref count); }
                continue;
            }
            if (value > HistorySize || value > _historyCount) { throw Error("Match exceeds available history."); }
            int matchLength = 3;
            if (reader.Read(1) != 0)
            {
                int extra = 2;
                while (reader.Read(1) != 0)
                {
                    if (++extra > 15) { throw Error("Match length exceeds segment limit."); }
                }
                matchLength = (1 << extra) + reader.Read(extra);
            }
            if (matchLength > SegmentLimit - count) { throw Error("Decoded segment exceeds 65535 bytes."); }
            for (int i = 0; i < matchLength; i++)
            {
                int source = _cursor - value;
                if (source < 0) { source += HistorySize; }
                Emit(_history[source], ref count);
            }
        }
        return count;
    }

    private void Emit(byte value, ref int count)
    {
        if (count == SegmentLimit) { throw Error("Decoded segment exceeds 65535 bytes."); }
        _segment[count++] = value;
        _history[_cursor++] = value;
        if (_cursor == HistorySize) { _cursor = 0; }
        if (_historyCount < HistorySize) { _historyCount++; }
    }

    private static bool IsShortLiteral(int value) => value is <= 0x0c or 0x38 or 0x39 or 0x3a or 0x3b
        or 0x3c or 0x3d or 0x3e or 0x3f or 0x40 or 0x66 or 0x80 or 0xff;
    private static RdpProtocolException Error(string detail) => new($"Invalid ZGFX data: {detail}");

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly int _length;
        private int _position;
        internal Bits(ReadOnlySpan<byte> data, int length) { _data = data; _length = length; }
        internal readonly int Remaining => _length - _position;
        internal int Read(int count)
        {
            if (count > Remaining) { throw Error("Truncated Huffman token."); }
            int value = 0;
            while (count-- > 0)
            {
                value = (value << 1) | ((_data[_position >> 3] >> (7 - (_position & 7))) & 1);
                _position++;
            }
            return value;
        }
        internal void Align() => _ = Read((8 - (_position & 7)) & 7);
    }
}
