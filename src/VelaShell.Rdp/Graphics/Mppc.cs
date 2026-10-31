namespace VelaShell.Rdp.Graphics;

/// <summary>
/// MPPC bulk decompressor (MS-RDPBCGR 3.1.8, RDP 4/8K and RDP 5/64K) for server→client
/// compressed PDUs. Ported from FreeRDP's <c>mppc.c</c>. The history buffer persists across
/// PDUs — matches reference earlier output — so a single instance decompresses the whole
/// server output stream. Decompression writes into the history and returns a view of the new
/// bytes. Compression itself is never needed (the client does not compress its small input).
/// </summary>
internal sealed class Mppc
{
    internal const byte PacketCompressed = 0x20;
    internal const byte PacketAtFront = 0x40;
    internal const byte PacketFlushed = 0x80;
    internal const byte CompressionTypeMask = 0x0F;

    private readonly byte[] _history = new byte[65536];
    private int _historyPtr;
    private readonly int _historyBufferSize;
    private readonly int _compressionLevel; // 0 = RDP4 (8K), 1 = RDP5 (64K)

    /// <param name="level">0 for PACKET_COMPR_TYPE_8K, 1 for PACKET_COMPR_TYPE_64K.</param>
    internal Mppc(int level)
    {
        _compressionLevel = level;
        _historyBufferSize = level != 0 ? 65536 : 8192;
    }

    /// <summary>
    /// Decompresses one compressed PDU; returns the decompressed bytes as a view of the history
    /// buffer (valid until the next call). If the PACKET_COMPRESSED flag is clear the input was
    /// not compressed and <paramref name="passthrough"/> is set.
    /// </summary>
    internal ReadOnlySpan<byte> Decompress(ReadOnlySpan<byte> src, byte flags, out bool passthrough)
    {
        passthrough = false;
        if ((flags & PacketAtFront) != 0)
        {
            _historyPtr = 0;
        }

        if ((flags & PacketFlushed) != 0) { _historyPtr = 0; Array.Clear(_history, 0, _historyBufferSize); }

        if ((flags & PacketCompressed) == 0) { passthrough = true; return default; }

        int historyEnd = _historyBufferSize - 1;
        int start = _historyPtr;
        int historyPtr = _historyPtr;
        int offsetMask = _compressionLevel != 0 ? 0xFFFF : 0x1FFF;

        var bs = new MppcBitStream(src);
        while (bs.RemainingBits >= 8)
        {
            if (historyPtr > historyEnd)
            {
                throw new RdpProtocolException("MPPC history overflow.");
            }

            uint acc = bs.Accumulator;

            // Literal.
            if ((acc & 0x80000000) == 0) { _history[historyPtr++] = (byte)((acc & 0x7F000000) >> 24); bs.Shift(8); continue; }
            if ((acc & 0xC0000000) == 0x80000000) { _history[historyPtr++] = (byte)(((acc & 0x3F800000) >> 23) + 0x80); bs.Shift(9); continue; }

            // Copy offset.
            uint copyOffset;
            if (_compressionLevel != 0) // RDP5
            {
                if ((acc & 0xF8000000) == 0xF8000000) { copyOffset = (acc >> 21) & 0x3F; bs.Shift(11); }
                else if ((acc & 0xF8000000) == 0xF0000000) { copyOffset = ((acc >> 19) & 0xFF) + 64; bs.Shift(13); }
                else if ((acc & 0xF0000000) == 0xE0000000) { copyOffset = ((acc >> 17) & 0x7FF) + 320; bs.Shift(15); }
                else if ((acc & 0xE0000000) == 0xC0000000) { copyOffset = ((acc >> 13) & 0xFFFF) + 2368; bs.Shift(19); }
                else
                {
                    throw new RdpProtocolException("Invalid MPPC copy-offset.");
                }
            }
            else // RDP4
            {
                if ((acc & 0xF0000000) == 0xF0000000) { copyOffset = (acc >> 22) & 0x3F; bs.Shift(10); }
                else if ((acc & 0xF0000000) == 0xE0000000) { copyOffset = ((acc >> 20) & 0xFF) + 64; bs.Shift(12); }
                else if ((acc & 0xE0000000) == 0xC0000000) { copyOffset = ((acc >> 16) & 0x1FFF) + 320; bs.Shift(16); }
                else
                {
                    throw new RdpProtocolException("Invalid MPPC copy-offset.");
                }
            }

            // Length of match.
            acc = bs.Accumulator;
            uint length;
            if ((acc & 0x80000000) == 0) { length = 3; bs.Shift(1); }
            else if ((acc & 0xC0000000) == 0x80000000) { length = ((acc >> 28) & 0x3) + 4; bs.Shift(4); }
            else if ((acc & 0xE0000000) == 0xC0000000) { length = ((acc >> 26) & 0x7) + 8; bs.Shift(6); }
            else if ((acc & 0xF0000000) == 0xE0000000) { length = ((acc >> 24) & 0xF) + 16; bs.Shift(8); }
            else if ((acc & 0xF8000000) == 0xF0000000) { length = ((acc >> 22) & 0x1F) + 32; bs.Shift(10); }
            else if ((acc & 0xFC000000) == 0xF8000000) { length = ((acc >> 20) & 0x3F) + 64; bs.Shift(12); }
            else if ((acc & 0xFE000000) == 0xFC000000) { length = ((acc >> 18) & 0x7F) + 128; bs.Shift(14); }
            else if ((acc & 0xFF000000) == 0xFE000000) { length = ((acc >> 16) & 0xFF) + 256; bs.Shift(16); }
            else if ((acc & 0xFF800000) == 0xFF000000) { length = ((acc >> 14) & 0x1FF) + 512; bs.Shift(18); }
            else if ((acc & 0xFFC00000) == 0xFF800000) { length = ((acc >> 12) & 0x3FF) + 1024; bs.Shift(20); }
            else if ((acc & 0xFFE00000) == 0xFFC00000) { length = ((acc >> 10) & 0x7FF) + 2048; bs.Shift(22); }
            else if ((acc & 0xFFF00000) == 0xFFE00000) { length = ((acc >> 8) & 0xFFF) + 4096; bs.Shift(24); }
            else if ((acc & 0xFFF80000) == 0xFFF00000 && _compressionLevel != 0) { length = ((acc >> 6) & 0x1FFF) + 8192; bs.Shift(26); }
            else if ((acc & 0xFFFC0000) == 0xFFF80000 && _compressionLevel != 0) { length = ((acc >> 4) & 0x3FFF) + 16384; bs.Shift(28); }
            else if ((acc & 0xFFFE0000) == 0xFFFC0000 && _compressionLevel != 0) { length = ((acc >> 2) & 0x7FFF) + 32768; bs.Shift(30); }
            else
            {
                throw new RdpProtocolException("Invalid MPPC length-of-match.");
            }

            if (historyPtr + (int)length - 1 > historyEnd)
            {
                throw new RdpProtocolException("MPPC match overflows history.");
            }

            int srcIdx = (historyPtr - (int)copyOffset) & offsetMask;
            if (srcIdx < 0 || srcIdx + (int)length > _historyBufferSize)
            {
                throw new RdpProtocolException("MPPC copy-offset out of bounds.");
            }

            for (uint i = 0; i < length; i++)
            {
                _history[historyPtr++] = _history[srcIdx++];
            }
        }

        _historyPtr = historyPtr;
        return _history.AsSpan(start, historyPtr - start);
    }
}

/// <summary>Big-endian, MSB-first bit reader with a 32-bit look-ahead accumulator (WinPR wBitStream).</summary>
internal ref struct MppcBitStream
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly int _lengthBits;
    private int _position;

    internal MppcBitStream(ReadOnlySpan<byte> data)
    {
        _data = data;
        _lengthBits = data.Length * 8;
        _position = 0;
    }

    internal readonly int RemainingBits => _lengthBits - _position;

    /// <summary>The 32 bits starting at the current position, with that bit as the MSB.</summary>
    internal readonly uint Accumulator
    {
        get
        {
            int bytePos = _position >> 3;
            int bitOff = _position & 7;
            ulong acc = 0;
            for (int i = 0; i < 5; i++)
            {
                acc = (acc << 8) | (bytePos + i < _data.Length ? _data[bytePos + i] : 0u);
            }

            return (uint)((acc >> (8 - bitOff)) & 0xFFFFFFFF);
        }
    }

    internal void Shift(int bits) => _position += bits;
}
