using System.Buffers;
using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Graphics;

/// <summary>
/// A top-down BGRA32 desktop surface that bitmap updates paint into (alpha forced opaque).
/// <para>
/// Decoding happens on the pump thread <i>outside</i> the surface lock: each rectangle is decompressed, clipped to its
/// destination rectangle and the surface, converted row by row into a pooled staging buffer, and only then copied
/// in under the lock. The lock is therefore held for memcpy-sized moments, so a UI thread copying the frame never
/// waits on decoding. The surface keeps the union of everything painted since the last <see cref="CopyTo"/>, so the
/// UI copies only what changed instead of the whole desktop every frame.
/// </para>
/// </summary>
internal sealed class Framebuffer
{
    /// <summary>Largest bitmap side accepted from the wire (the desktop itself is capped at 8192).</summary>
    internal const int MaxBitmapSide = 8192;

    private readonly Lock _gate = new();
    private int _dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom;   // exclusive right/bottom; empty when right <= left

    internal Framebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Bgra = new byte[checked(width * height * 4)];
        MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan()).Fill(0xFF000000);
        MarkAllDirty();
    }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>The pixels. Read it under no lock only in tests; the UI goes through <see cref="CopyTo"/>.</summary>
    internal byte[] Bgra { get; }

    /// <summary>
    /// Paints one TS_BITMAP_DATA rectangle. <paramref name="right"/>/<paramref name="bottom"/> are the inclusive
    /// destination bounds: a bitmap is padded to a multiple of 4 pixels wide and the padding must not be painted.
    /// <paramref name="data"/> is already past any compression header. Hostile sizes are refused before any work.
    /// </summary>
    internal void ApplyRect(int left, int top, int right, int bottom, int width, int height, int bpp, bool compressed, ReadOnlySpan<byte> data)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int bytesPerPixel = bpp switch { 8 => 1, 15 => 2, 16 => 2, 24 => 3, 32 => 4, _ => 0 };
        if (bytesPerPixel == 0)
        {
            throw new RdpProtocolException($"Unsupported bitmap bpp {bpp}.");
        }

        if (width > MaxBitmapSide || height > MaxBitmapSide)
        {
            throw new RdpProtocolException("Bitmap rectangle too large.");
        }

        // What actually lands on the surface: the destination rectangle (never the padding), clipped to the surface.
        int x1 = Math.Min(Math.Min(left + width, right + 1), Width);
        int y1 = Math.Min(Math.Min(top + height, bottom + 1), Height);
        if (x1 <= left || y1 <= top)
        {
            return;   // nothing visible: skip the decode entirely
        }

        int scanline = width * bytesPerPixel;
        int needed = scanline * height;   // ≤ 8192 × 8192 × 4, fits an int
        byte[]? decoded = null;
        ReadOnlySpan<byte> pixels;
        bool topDown = false;
        if (compressed && bpp == 32)
        {
            // 32 bpp compressed bitmaps are RDP 6.0 planar (MS-RDPEGDI 2.2.2.5.1), not interleaved RLE. The planar
            // decoder writes a top-down BGRA image, so it is not flipped again below.
            decoded = ArrayPool<byte>.Shared.Rent(needed);
            Planar.Decompress(data, decoded.AsSpan(0, needed), width, height);
            pixels = decoded.AsSpan(0, needed);
            topDown = true;
        }
        else if (compressed)
        {
            decoded = ArrayPool<byte>.Shared.Rent(needed);
            int written = InterleavedRle.Decompress(data, decoded.AsSpan(0, needed), width, height, bytesPerPixel);
            // A stream that ends early leaves the rest of the rented buffer as whatever another user left there;
            // paint black, never stale memory.
            decoded.AsSpan(written, needed - written).Clear();
            pixels = decoded.AsSpan(0, needed);
        }
        else if (data.Length < needed)
        {
            throw new RdpProtocolException("Bitmap data too short.");
        }
        else
        {
            pixels = data;
        }

        int visibleWidth = x1 - left, visibleHeight = y1 - top;
        byte[] staging = ArrayPool<byte>.Shared.Rent(visibleWidth * visibleHeight * 4);
        try
        {
            for (int row = 0; row < visibleHeight; row++)
            {
                // Bottom-up DIB: destination row (top + row) is source row (height - 1 - row); planar output is top-down.
                ConvertRow(pixels.Slice((topDown ? row : height - 1 - row) * scanline, visibleWidth * bytesPerPixel),
                    staging.AsSpan(row * visibleWidth * 4, visibleWidth * 4), bpp);
            }

            Blit(left, top, visibleWidth, visibleHeight, staging);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(staging);
            if (decoded is not null)
            {
                ArrayPool<byte>.Shared.Return(decoded);
            }
        }
    }

    /// <summary>Copies already-converted, already-clipped top-down BGRA rows (stride = width × 4) into the surface.</summary>
    internal void Blit(int x, int y, int width, int height, ReadOnlySpan<byte> rows)
    {
        int rowBytes = width * 4;
        lock (_gate)
        {
            for (int row = 0; row < height; row++)
            {
                rows.Slice(row * rowBytes, rowBytes).CopyTo(Bgra.AsSpan(((y + row) * Width + x) * 4, rowBytes));
            }

            Union(x, y, x + width, y + height);
        }
    }

    /// <summary>
    /// Copies what changed since the last call (everything when <paramref name="full"/>) into a top-down BGRA
    /// surface of exactly this size, then forgets it. Returns the copied region (empty when nothing changed).
    /// </summary>
    internal RdpFrameRegion CopyTo(Span<byte> destination, int stride, bool full)
    {
        if (stride < Width * 4 || destination.Length < (long)(Height - 1) * stride + Width * 4)
        {
            throw new ArgumentException("Destination is smaller than the desktop.", nameof(destination));
        }

        lock (_gate)
        {
            if (full)
            {
                MarkAllDirty();
            }

            if (_dirtyRight <= _dirtyLeft || _dirtyBottom <= _dirtyTop)
            {
                return default;
            }

            var region = new RdpFrameRegion(_dirtyLeft, _dirtyTop, _dirtyRight - _dirtyLeft, _dirtyBottom - _dirtyTop);
            int rowBytes = region.Width * 4;
            for (int y = region.Y; y < region.Y + region.Height; y++)
            {
                Bgra.AsSpan((y * Width + region.X) * 4, rowBytes).CopyTo(destination.Slice(y * stride + region.X * 4, rowBytes));
            }

            _dirtyLeft = _dirtyTop = _dirtyRight = _dirtyBottom = 0;
            return region;
        }
    }

    /// <summary>A full copy of the surface (the older <see cref="IRdpSession.TakeFrame"/> path); clears the change record.</summary>
    internal byte[] Snapshot()
    {
        lock (_gate)
        {
            _dirtyLeft = _dirtyTop = _dirtyRight = _dirtyBottom = 0;
            return (byte[])Bgra.Clone();
        }
    }

    private void MarkAllDirty() => (_dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom) = (0, 0, Width, Height);

    private void Union(int left, int top, int right, int bottom)
    {
        if (_dirtyRight <= _dirtyLeft || _dirtyBottom <= _dirtyTop)
        {
            (_dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom) = (left, top, right, bottom);
            return;
        }

        _dirtyLeft = Math.Min(_dirtyLeft, left);
        _dirtyTop = Math.Min(_dirtyTop, top);
        _dirtyRight = Math.Max(_dirtyRight, right);
        _dirtyBottom = Math.Max(_dirtyBottom, bottom);
    }

    // ---- Pixel conversion: one tight loop per format, writing whole BGRA words (little-endian: B,G,R,A). ----

    private static readonly Lazy<uint[]> Lut565 = new(() => BuildLut(rgb555: false));
    private static readonly Lazy<uint[]> Lut555 = new(() => BuildLut(rgb555: true));

    private static uint[] BuildLut(bool rgb555)
    {
        uint[] lut = new uint[65536];
        for (int p = 0; p < lut.Length; p++)
        {
            int r5 = rgb555 ? (p >> 10) & 0x1F : (p >> 11) & 0x1F;
            int g = rgb555 ? ((p >> 5) & 0x1F) << 3 | ((p >> 5) & 0x1F) >> 2 : ((p >> 5) & 0x3F) << 2 | ((p >> 5) & 0x3F) >> 4;
            int b5 = p & 0x1F;
            int r = (r5 << 3) | (r5 >> 2), b = (b5 << 3) | (b5 >> 2);
            lut[p] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }
        return lut;
    }

    internal static void ConvertRow(ReadOnlySpan<byte> src, Span<byte> dst, int bpp)
    {
        Span<uint> output = MemoryMarshal.Cast<byte, uint>(dst);
        switch (bpp)
        {
            case 15:
            case 16:
                {
                    ReadOnlySpan<ushort> input = MemoryMarshal.Cast<byte, ushort>(src);
                    uint[] lut = bpp == 15 ? Lut555.Value : Lut565.Value;
                    for (int i = 0; i < output.Length; i++)
                    {
                        output[i] = lut[input[i]];
                    }

                    break;
                }
            case 24:
                for (int i = 0, s = 0; i < output.Length; i++, s += 3)
                {
                    output[i] = 0xFF000000u | src[s] | ((uint)src[s + 1] << 8) | ((uint)src[s + 2] << 16);   // BGR24
                }

                break;
            case 32:
                {
                    ReadOnlySpan<uint> input = MemoryMarshal.Cast<byte, uint>(src);
                    for (int i = 0; i < output.Length; i++)
                    {
                        output[i] = input[i] | 0xFF000000u;   // BGRX32
                    }

                    break;
                }
            default:
                for (int i = 0; i < output.Length; i++)
                {
                    uint grey = src[i];   // 8 bpp without a palette: grey
                    output[i] = 0xFF000000u | (grey << 16) | (grey << 8) | grey;
                }

                break;
        }
    }
}
