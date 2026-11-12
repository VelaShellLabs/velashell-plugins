using System.Buffers;

namespace VelaShell.Rdp.Graphics;

/// <summary>
/// RDP 6.0 planar bitmap decompressor (MS-RDPEGDI 2.2.2.5.1 RDP6_BITMAP_STREAM, 3.1.9 "RDP 6.0
/// Bitmap Compression"), the codec Windows servers use for 32 bpp compressed bitmap updates.
/// Code adapted from FreeRDP <c>libfreerdp/codec/planar.c</c> and
/// <c>libfreerdp/primitives/prim_YCoCg.c</c> (Apache-2.0; see THIRD-PARTY-NOTICES.txt); MS-RDPEGDI, not that source,
/// defines behaviour.
/// <para>
/// Supported: raw and RLE planes (the RLE variant is delta-coded against the previous plane
/// scanline), an optional alpha plane (absent when the NoAlpha flag 0x20 is set, in which case
/// alpha is forced to 0xFF; otherwise the decoded alpha is kept), ARGB (color loss level 0) and
/// AYCoCg with color loss levels 1–7, and chroma subsampling (0x08) of the Co/Cg planes.
/// </para>
/// <para>
/// Orientation: in a bitmap update the planes are stored bottom-up, like every TS_BITMAP_DATA
/// payload — the first plane scanline is the bottom image row. Bitmap updates therefore need a
/// vertical flip; this entry point flips by default and writes a
/// <b>top-down</b> image, so callers must not flip the result again. The RDPGFX planar codec
/// stores top-down planes and passes <c>bottomUp = false</c>.
/// </para>
/// <para>
/// Not implemented: a red/blue swap heuristic for
/// streams that carry an alpha plane when dynamic color fidelity was negotiated (a workaround for
/// servers that are inconsistent about R/B order with YCoCg). Like the interleaved decoder this is
/// written without closures and decodes from the wire span into a caller-provided destination;
/// the only scratch memory is one pooled buffer for decoded RLE planes, at most 4 × width × height
/// bytes. Malformed input raises <see cref="RdpProtocolException"/>.
/// </para>
/// </summary>
internal static class Planar
{
    private const byte ColorLossLevelMask = 0x07;
    private const byte ChromaSubsampling = 0x08;
    private const byte RunLengthEncoding = 0x10;
    private const byte NoAlpha = 0x20;
    private const byte ReservedMask = 0xC0;

    /// <summary>
    /// Decompresses one RDP6_BITMAP_STREAM into <paramref name="dstBgra"/> as a top-down 32-bit
    /// B,G,R,A image with stride <c>width * 4</c>. Only the first <c>width * height * 4</c> bytes
    /// of the destination are written.
    /// </summary>
    internal static void Decompress(ReadOnlySpan<byte> src, Span<byte> dstBgra, int width, int height, bool bottomUp = true)
    {
        if (width <= 0 || height <= 0)
        {
            throw new RdpProtocolException($"Invalid planar bitmap size {width}x{height}.");
        }

        // width * height < 2^62 always fits a long; multiplying by 4 as well could wrap it.
        long pixelCount = (long)width * height;
        if (pixelCount > dstBgra.Length / 4)
        {
            throw new RdpProtocolException($"Planar bitmap {width}x{height} does not fit the destination buffer.");
        }

        if (src.Length < 1)
        {
            throw new RdpProtocolException("Planar bitmap stream is empty.");
        }

        // From here on width * height * 4 <= dstBgra.Length <= int.MaxValue, so the plane
        // sizes below (all at most width * height) cannot overflow an int.
        byte formatHeader = src[0];
        if ((formatHeader & ReservedMask) != 0)
        {
            throw new RdpProtocolException($"Planar bitmap format header 0x{formatHeader:X2} sets reserved bits.");
        }

        int colorLossLevel = formatHeader & ColorLossLevelMask;
        bool chromaSubsampling = (formatHeader & ChromaSubsampling) != 0;
        bool rle = (formatHeader & RunLengthEncoding) != 0;
        bool hasAlpha = (formatHeader & NoAlpha) == 0;

        if (chromaSubsampling && colorLossLevel == 0)
        {
            // Chroma subsampling only applies to YCoCg planes; with RGB (CLL 0) the stream is malformed.
            throw new RdpProtocolException("Planar chroma subsampling requires YCoCg (a non-zero color loss level).");
        }

        int planeSize = width * height;
        int chromaWidth = chromaSubsampling ? (width / 2) + (width % 2) : width;
        int chromaHeight = chromaSubsampling ? (height / 2) + (height % 2) : height;
        int chromaSize = chromaWidth * chromaHeight;

        byte[]? scratch = null;
        try
        {
            ReadOnlySpan<byte> alphaPlane = default;
            ReadOnlySpan<byte> lumaOrRed;
            ReadOnlySpan<byte> orangeChromaOrGreen;
            ReadOnlySpan<byte> greenChromaOrBlue;

            if (!rle)
            {
                // Raw planes in wire order: [Alpha] LumaOrRed, OrangeChromaOrGreen,
                // GreenChromaOrBlue, then an optional 1-byte pad that is ignored.
                long needed = 1L + (hasAlpha ? planeSize : 0) + planeSize + (2L * chromaSize);
                if (src.Length < needed)
                {
                    throw new RdpProtocolException($"Raw planar bitmap needs {needed} bytes, got {src.Length}.");
                }

                int pos = 1;
                if (hasAlpha)
                {
                    alphaPlane = src.Slice(pos, planeSize);
                    pos += planeSize;
                }

                lumaOrRed = src.Slice(pos, planeSize);
                pos += planeSize;
                orangeChromaOrGreen = src.Slice(pos, chromaSize);
                pos += chromaSize;
                greenChromaOrBlue = src.Slice(pos, chromaSize);
            }
            else
            {
                int scratchSize = (hasAlpha ? planeSize : 0) + planeSize + (2 * chromaSize);
                scratch = ArrayPool<byte>.Shared.Rent(scratchSize);
                Span<byte> planes = scratch.AsSpan(0, scratchSize);

                int pos = 1;
                int offset = 0;
                if (hasAlpha)
                {
                    pos += DecodeRlePlane(src, pos, planes.Slice(offset, planeSize), width, height);
                    offset += planeSize;
                }

                pos += DecodeRlePlane(src, pos, planes.Slice(offset, planeSize), width, height);
                offset += planeSize;
                pos += DecodeRlePlane(src, pos, planes.Slice(offset, chromaSize), chromaWidth, chromaHeight);
                offset += chromaSize;
                _ = DecodeRlePlane(src, pos, planes.Slice(offset, chromaSize), chromaWidth, chromaHeight);

                ReadOnlySpan<byte> decoded = planes;
                offset = 0;
                if (hasAlpha)
                {
                    alphaPlane = decoded[..planeSize];
                    offset = planeSize;
                }

                lumaOrRed = decoded.Slice(offset, planeSize);
                orangeChromaOrGreen = decoded.Slice(offset + planeSize, chromaSize);
                greenChromaOrBlue = decoded.Slice(offset + planeSize + chromaSize, chromaSize);
            }

            if (colorLossLevel == 0)
            {
                ComposeArgb(lumaOrRed, orangeChromaOrGreen, greenChromaOrBlue, alphaPlane, hasAlpha, dstBgra, width, height, bottomUp);
            }
            else
            {
                ComposeAYCoCg(lumaOrRed, orangeChromaOrGreen, greenChromaOrBlue, alphaPlane, hasAlpha,
                    dstBgra, width, height, colorLossLevel, chromaSubsampling, chromaWidth, bottomUp);
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>
    /// Decodes one RLE plane (MS-RDPEGDI RDP6_RLE_SEGMENTS) of
    /// <paramref name="width"/> × <paramref name="height"/> bytes starting at
    /// <paramref name="pos"/>, in stream scanline order. Returns the number of bytes consumed.
    /// </summary>
    private static int DecodeRlePlane(ReadOnlySpan<byte> src, int pos, Span<byte> plane, int width, int height)
    {
        int start = pos;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int previousRow = row - width;
            int x = 0;
            int pixel = 0; // last absolute value (first scanline) or last delta (later scanlines)

            while (x < width)
            {
                if (pos >= src.Length)
                {
                    throw new RdpProtocolException("Planar RLE plane truncated.");
                }

                byte controlByte = src[pos++];
                int runLength = controlByte & 0x0F;
                int rawBytes = controlByte >> 4;

                // A run length of 1 or 2 is an escape: the raw-bytes nibble extends the run.
                if (runLength == 1)
                {
                    runLength = rawBytes + 16;
                    rawBytes = 0;
                }
                else if (runLength == 2)
                {
                    runLength = rawBytes + 32;
                    rawBytes = 0;
                }

                if (rawBytes + runLength > width - x)
                {
                    throw new RdpProtocolException("Planar RLE segment overruns the scanline.");
                }

                if (rawBytes > src.Length - pos)
                {
                    throw new RdpProtocolException("Planar RLE plane truncated.");
                }

                if (y == 0)
                {
                    // First scanline: absolute values; a run repeats the last raw value.
                    for (; rawBytes > 0; rawBytes--)
                    {
                        pixel = src[pos++];
                        plane[row + x] = (byte)pixel;
                        x++;
                    }

                    for (; runLength > 0; runLength--)
                    {
                        plane[row + x] = (byte)pixel;
                        x++;
                    }
                }
                else
                {
                    // Later scanlines: sign-magnitude deltas against the previous scanline
                    // (LSB set = negative); a run repeats the last delta. Sums wrap modulo 256.
                    for (; rawBytes > 0; rawBytes--)
                    {
                        int deltaValue = src[pos++];
                        pixel = (deltaValue & 1) != 0 ? -((deltaValue >> 1) + 1) : deltaValue >> 1;
                        plane[row + x] = unchecked((byte)(plane[previousRow + x] + pixel));
                        x++;
                    }

                    for (; runLength > 0; runLength--)
                    {
                        plane[row + x] = unchecked((byte)(plane[previousRow + x] + pixel));
                        x++;
                    }
                }
            }
        }

        return pos - start;
    }

    /// <summary>Writes ARGB planes (color loss level 0) as top-down BGRA; plane row 0 is the bottom row.</summary>
    private static void ComposeArgb(ReadOnlySpan<byte> red, ReadOnlySpan<byte> green, ReadOnlySpan<byte> blue,
        ReadOnlySpan<byte> alpha, bool hasAlpha, Span<byte> dst, int width, int height, bool bottomUp = true)
    {
        for (int y = 0; y < height; y++)
        {
            int planeRow = (bottomUp ? height - 1 - y : y) * width;
            int d = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = planeRow + x;
                dst[d] = blue[i];
                dst[d + 1] = green[i];
                dst[d + 2] = red[i];
                dst[d + 3] = hasAlpha ? alpha[i] : (byte)0xFF;
                d += 4;
            }
        }
    }

    /// <summary>
    /// Converts AYCoCg planes to top-down BGRA (MS-RDPEGDI YCoCg-to-RGB conversion), expanding
    /// subsampled chroma: each chroma sample covers a
    /// 2 × 2 block anchored at the first plane scanline (the bottom image row).
    /// </summary>
    private static void ComposeAYCoCg(ReadOnlySpan<byte> luma, ReadOnlySpan<byte> orangeChroma, ReadOnlySpan<byte> greenChroma,
        ReadOnlySpan<byte> alpha, bool hasAlpha, Span<byte> dst, int width, int height, int colorLossLevel,
        bool chromaSubsampling, int chromaWidth, bool bottomUp)
    {
        // The chroma planes were shifted right by the color loss level on the server; shifting
        // left by one less builds in the halving of Co and Cg in the inverse transform.
        int shift = colorLossLevel - 1;
        for (int y = 0; y < height; y++)
        {
            int planeY = bottomUp ? height - 1 - y : y;
            int planeRow = planeY * width;
            int chromaRow = chromaSubsampling ? (planeY / 2) * chromaWidth : planeRow;
            int d = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int i = planeRow + x;
                int c = chromaSubsampling ? chromaRow + (x / 2) : i;

                // Shift before the sign conversion, truncating to 8 bits like the C cast to INT8.
                int cg = unchecked((sbyte)(greenChroma[c] << shift));
                int co = unchecked((sbyte)(orangeChroma[c] << shift));
                int yValue = luma[i];
                int t = yValue - cg;

                dst[d] = Clip(t + co);          // B
                dst[d + 1] = Clip(yValue + cg); // G
                dst[d + 2] = Clip(t - co);      // R
                dst[d + 3] = hasAlpha ? alpha[i] : (byte)0xFF;
                d += 4;
            }
        }
    }

    private static byte Clip(int value) => value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)value;
}
