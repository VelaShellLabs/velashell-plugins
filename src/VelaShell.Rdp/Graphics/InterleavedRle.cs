namespace VelaShell.Rdp.Graphics;

/// <summary>
/// Interleaved RLE bitmap decompressor (MS-RDPBCGR 3.1.9). Ported value-for-value from
/// FreeRDP's <c>interleaved.c</c> / <c>include/bitmap.h</c> (Apache-2.0), parameterised by
/// bytes-per-pixel (1/2/3 for 8/15-16/24 bpp). Output is bottom-up DIB order; callers flip
/// vertically for a top-down surface. Written without closures so it decodes straight from
/// the wire span into a caller-provided (poolable) destination — no per-rectangle allocation.
/// </summary>
internal static class InterleavedRle
{
    private const int RegularBgRun = 0x00, MegaMegaBgRun = 0xF0;
    private const int RegularFgRun = 0x01, MegaMegaFgRun = 0xF1;
    private const int LiteSetFgFgRun = 0x0C, MegaMegaSetFgRun = 0xF6;
    private const int LiteDitheredRun = 0x0E, MegaMegaDitheredRun = 0xF8;
    private const int RegularColorRun = 0x03, MegaMegaColorRun = 0xF3;
    private const int RegularFgBgImage = 0x02, MegaMegaFgBgImage = 0xF2;
    private const int LiteSetFgFgBgImage = 0x0D, MegaMegaSetFgBgImage = 0xF7;
    private const int RegularColorImage = 0x04, MegaMegaColorImage = 0xF4;
    private const int SpecialFgBg1 = 0xF9, SpecialFgBg2 = 0xFA;
    private const int SpecialWhite = 0xFD, SpecialBlack = 0xFE;
    private const byte MaskSpecialFgBg1 = 0x03, MaskSpecialFgBg2 = 0x05;
    private const byte MaskRegularRunLength = 0x1F, MaskLiteRunLength = 0x0F;
    private const uint BlackPixel = 0x000000;

    /// <summary>Decompresses into <paramref name="dst"/> (bottom-up, width*height*bpp bytes).</summary>
    internal static void Decompress(ReadOnlySpan<byte> src, Span<byte> dst, int width, int height, int bpp)
    {
        int rowDelta = width * bpp;
        uint white = bpp switch { 1 => 0xFF, 2 => 0xFFFF, _ => 0xFFFFFF };
        int srcEnd = src.Length;
        int dstEnd = rowDelta * height;
        int s = 0, d = 0;
        uint fgPel = white;
        bool insertFgPel = false;
        bool firstLine = true;

        while (s < srcEnd)
        {
            if (firstLine && d >= rowDelta) { firstLine = false; insertFgPel = false; }

            int code = ExtractCodeId(src[s]);

            if (code is RegularBgRun or MegaMegaBgRun)
            {
                int runLength = ExtractRunLength(code, src, s, srcEnd, out int advance);
                if (advance == 0)
                {
                    throw new RdpProtocolException("Invalid RLE run length.");
                }

                s += advance;
                if (firstLine)
                {
                    if (insertFgPel) { WritePixel(dst, ref d, bpp, fgPel, dstEnd); runLength--; }
                    for (int i = 0; i < runLength; i++)
                    {
                        WritePixel(dst, ref d, bpp, BlackPixel, dstEnd);
                    }
                }
                else
                {
                    if (insertFgPel) { WritePixel(dst, ref d, bpp, ReadPixel(dst, d - rowDelta, bpp) ^ fgPel, dstEnd); runLength--; }
                    for (int i = 0; i < runLength; i++)
                    {
                        WritePixel(dst, ref d, bpp, ReadPixel(dst, d - rowDelta, bpp), dstEnd);
                    }
                }
                insertFgPel = true;
                continue;
            }

            insertFgPel = false;

            switch (code)
            {
                case RegularFgRun:
                case MegaMegaFgRun:
                case LiteSetFgFgRun:
                case MegaMegaSetFgRun:
                    {
                        int runLength = ExtractRunLength(code, src, s, srcEnd, out int advance);
                        if (advance == 0)
                        {
                            throw new RdpProtocolException("Invalid RLE run length.");
                        }

                        s += advance;
                        if (code is LiteSetFgFgRun or MegaMegaSetFgRun)
                        {
                            fgPel = ReadSrc(src, ref s, bpp, srcEnd);
                        }

                        if (firstLine)
                        {
                            for (int i = 0; i < runLength; i++)
                            {
                                WritePixel(dst, ref d, bpp, fgPel, dstEnd);
                            }
                        }
                        else
                        {
                            for (int i = 0; i < runLength; i++)
                            {
                                WritePixel(dst, ref d, bpp, ReadPixel(dst, d - rowDelta, bpp) ^ fgPel, dstEnd);
                            }
                        }

                        break;
                    }
                case LiteDitheredRun:
                case MegaMegaDitheredRun:
                    {
                        int runLength = ExtractRunLength(code, src, s, srcEnd, out int advance);
                        if (advance == 0)
                        {
                            throw new RdpProtocolException("Invalid RLE run length.");
                        }

                        s += advance;
                        uint pixelA = ReadSrc(src, ref s, bpp, srcEnd);
                        uint pixelB = ReadSrc(src, ref s, bpp, srcEnd);
                        for (int i = 0; i < runLength; i++) { WritePixel(dst, ref d, bpp, pixelA, dstEnd); WritePixel(dst, ref d, bpp, pixelB, dstEnd); }
                        break;
                    }
                case RegularColorRun:
                case MegaMegaColorRun:
                    {
                        int runLength = ExtractRunLength(code, src, s, srcEnd, out int advance);
                        if (advance == 0)
                        {
                            throw new RdpProtocolException("Invalid RLE run length.");
                        }

                        s += advance;
                        uint pixelA = ReadSrc(src, ref s, bpp, srcEnd);
                        for (int i = 0; i < runLength; i++)
                        {
                            WritePixel(dst, ref d, bpp, pixelA, dstEnd);
                        }

                        break;
                    }
                case RegularFgBgImage:
                case MegaMegaFgBgImage:
                case LiteSetFgFgBgImage:
                case MegaMegaSetFgBgImage:
                    {
                        int runLength = ExtractRunLength(code, src, s, srcEnd, out int advance);
                        if (advance == 0)
                        {
                            throw new RdpProtocolException("Invalid RLE run length.");
                        }

                        s += advance;
                        if (code is LiteSetFgFgBgImage or MegaMegaSetFgBgImage)
                        {
                            fgPel = ReadSrc(src, ref s, bpp, srcEnd);
                        }

                        while (runLength > 8)
                        {
                            if (s >= srcEnd)
                            {
                                throw new RdpProtocolException("RLE source underrun.");
                            }

                            byte bitmask = src[s++];
                            WriteFgBg(dst, ref d, rowDelta, bpp, bitmask, fgPel, 8, firstLine, white, dstEnd);
                            runLength -= 8;
                        }
                        if (runLength > 0)
                        {
                            if (s >= srcEnd)
                            {
                                throw new RdpProtocolException("RLE source underrun.");
                            }

                            byte bitmask = src[s++];
                            WriteFgBg(dst, ref d, rowDelta, bpp, bitmask, fgPel, runLength, firstLine, white, dstEnd);
                        }
                        break;
                    }
                case RegularColorImage:
                case MegaMegaColorImage:
                    {
                        int runLength = ExtractRunLength(code, src, s, srcEnd, out int advance);
                        if (advance == 0)
                        {
                            throw new RdpProtocolException("Invalid RLE run length.");
                        }

                        s += advance;
                        for (int i = 0; i < runLength; i++)
                        {
                            WritePixel(dst, ref d, bpp, ReadSrc(src, ref s, bpp, srcEnd), dstEnd);
                        }

                        break;
                    }
                case SpecialFgBg1:
                    s += 1;
                    WriteFgBg(dst, ref d, rowDelta, bpp, MaskSpecialFgBg1, fgPel, 8, firstLine, white, dstEnd);
                    break;
                case SpecialFgBg2:
                    s += 1;
                    WriteFgBg(dst, ref d, rowDelta, bpp, MaskSpecialFgBg2, fgPel, 8, firstLine, white, dstEnd);
                    break;
                case SpecialWhite:
                    s += 1;
                    WritePixel(dst, ref d, bpp, white, dstEnd);
                    break;
                case SpecialBlack:
                    s += 1;
                    WritePixel(dst, ref d, bpp, BlackPixel, dstEnd);
                    break;
                default:
                    throw new RdpProtocolException($"Invalid RLE code 0x{code:X2}.");
            }
        }
    }

    private static uint ReadSrc(ReadOnlySpan<byte> src, ref int s, int bpp, int srcEnd)
    {
        if (s + bpp > srcEnd)
        {
            throw new RdpProtocolException("RLE source underrun.");
        }

        uint v = bpp switch
        {
            1 => src[s],
            2 => (uint)(src[s] | (src[s + 1] << 8)),
            _ => (uint)(src[s] | (src[s + 1] << 8) | (src[s + 2] << 16))
        };
        s += bpp;
        return v;
    }

    private static uint ReadPixel(ReadOnlySpan<byte> dst, int pos, int bpp) => bpp switch
    {
        1 => dst[pos],
        2 => (uint)(dst[pos] | (dst[pos + 1] << 8)),
        _ => (uint)(dst[pos] | (dst[pos + 1] << 8) | (dst[pos + 2] << 16))
    };

    private static void WritePixel(Span<byte> dst, ref int d, int bpp, uint pel, int dstEnd)
    {
        if (d + bpp > dstEnd)
        {
            throw new RdpProtocolException("RLE bitmap overran destination.");
        }

        dst[d] = (byte)pel;
        if (bpp >= 2)
        {
            dst[d + 1] = (byte)(pel >> 8);
        }

        if (bpp >= 3)
        {
            dst[d + 2] = (byte)(pel >> 16);
        }

        d += bpp;
    }

    private static void WriteFgBg(Span<byte> dst, ref int d, int rowDelta, int bpp, byte bitmask, uint fg,
        int cBits, bool firstLine, uint white, int dstEnd)
    {
        _ = white;
        byte mask = 0x01;
        for (int i = 0; i < cBits; i++)
        {
            uint value = firstLine
                ? ((bitmask & mask) != 0 ? fg : BlackPixel)
                : ((bitmask & mask) != 0 ? ReadPixel(dst, d - rowDelta, bpp) ^ fg : ReadPixel(dst, d - rowDelta, bpp));
            WritePixel(dst, ref d, bpp, value, dstEnd);
            mask = (byte)((mask << 1) & 0xFF);
        }
    }

    private static int ExtractCodeId(byte header)
    {
        if ((header & 0xC0) != 0xC0)
        {
            return header >> 5;        // regular
        }

        if ((header & 0xF0) == 0xF0)
        {
            return header;             // mega / special
        }

        return header >> 4;                                     // lite
    }

    private static int ExtractRunLength(int code, ReadOnlySpan<byte> src, int pos, int end, out int advance)
    {
        advance = 1;
        int run;
        switch (code)
        {
            case RegularFgBgImage:
                run = src[pos] & MaskRegularRunLength;
                if (run == 0) { if (pos + 2 > end) { advance = 0; return 0; } run = src[pos + 1] + 1; advance++; }
                else
                {
                    run *= 8;
                }

                return run;
            case LiteSetFgFgBgImage:
                run = src[pos] & MaskLiteRunLength;
                if (run == 0) { if (pos + 2 > end) { advance = 0; return 0; } run = src[pos + 1] + 1; advance++; }
                else
                {
                    run *= 8;
                }

                return run;
            case RegularBgRun:
            case RegularFgRun:
            case RegularColorRun:
            case RegularColorImage:
                run = src[pos] & MaskRegularRunLength;
                if (run == 0) { if (pos + 2 > end) { advance = 0; return 0; } run = src[pos + 1] + 32; advance++; }
                return run;
            case LiteSetFgFgRun:
            case LiteDitheredRun:
                run = src[pos] & MaskLiteRunLength;
                if (run == 0) { if (pos + 2 > end) { advance = 0; return 0; } run = src[pos + 1] + 16; advance++; }
                return run;
            case MegaMegaBgRun:
            case MegaMegaFgRun:
            case MegaMegaSetFgRun:
            case MegaMegaDitheredRun:
            case MegaMegaColorRun:
            case MegaMegaFgBgImage:
            case MegaMegaSetFgBgImage:
            case MegaMegaColorImage:
                if (pos + 3 > end) { advance = 0; return 0; }
                run = src[pos + 1] | (src[pos + 2] << 8);
                advance += 2;
                return run;
            default:
                advance = 0;
                return 0;
        }
    }
}
