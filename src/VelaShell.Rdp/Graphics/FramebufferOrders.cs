using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Graphics;

/// <summary>
/// Drawing orders on the framebuffer (<see cref="IOrderTarget"/>): solid fills, destination / pattern raster operations,
/// screen-to-screen copies (scrolling) and blits from the bitmap cache. Every operation is clipped to the surface and to
/// the order's bounds, runs under the surface lock for memcpy-sized work, and adds to the changed region like a bitmap
/// update. Raster operations evaluate the ROP3 truth table generically (bit P·4 + S·2 + D of the code), with fast paths
/// for the codes servers actually send (PATCOPY 0xF0, SRCCOPY 0xCC, BLACKNESS, WHITENESS, DSTINVERT).
/// </summary>
internal sealed partial class Framebuffer : IOrderTarget
{
    private const byte RopBlackness = 0x00, RopDstInvert = 0x55, RopNoOp = 0xAA, RopSrcCopy = 0xCC, RopPatCopy = 0xF0, RopWhiteness = 0xFF;

    int IOrderTarget.Width => Width;

    int IOrderTarget.Height => Height;

    public void FillRect(OrderRect rect, uint bgra, OrderRect? clip)
    {
        if (!Clip(rect, clip, out int x0, out int y0, out int x1, out int y1))
        {
            return;
        }

        lock (_gate)
        {
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan());
            for (int y = y0; y < y1; y++)
            {
                pixels.Slice(y * Width + x0, x1 - x0).Fill(bgra | 0xFF000000u);
            }

            Union(x0, y0, x1, y1);
        }
    }

    public void DstRop(OrderRect rect, byte rop3, OrderRect? clip)
    {
        if (rop3 == RopNoOp || !Clip(rect, clip, out int x0, out int y0, out int x1, out int y1))
        {
            return;
        }

        if (rop3 is RopBlackness or RopWhiteness)
        {
            FillRect(new OrderRect(x0, y0, x1 - x0, y1 - y0), rop3 == RopBlackness ? 0xFF000000u : 0xFFFFFFFFu, null);
            return;
        }

        lock (_gate)
        {
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan());
            for (int y = y0; y < y1; y++)
            {
                Span<uint> row = pixels.Slice(y * Width + x0, x1 - x0);
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = rop3 == RopDstInvert ? ~row[i] | 0xFF000000u : Rop3(rop3, 0, 0, row[i]);
                }
            }

            Union(x0, y0, x1, y1);
        }
    }

    public void PatBlt(OrderRect rect, byte rop3, OrderBrush brush, OrderRect? clip)
    {
        if (brush.Style == 1 /* BS_NULL */ || !Clip(rect, clip, out int x0, out int y0, out int x1, out int y1))
        {
            return;
        }

        if (brush.Style == 0 /* BS_SOLID */ && rop3 == RopPatCopy)
        {
            FillRect(new OrderRect(x0, y0, x1 - x0, y1 - y0), brush.ForeBgra, null);
            return;
        }

        // Hatched and pattern brushes arrive as an 8×8 bitmap (the decoder expands the hatch); solid ones paint ForeBgra.
        ReadOnlySpan<byte> pattern = brush.Style is 2 or 3 && brush.Pattern8x8.Length >= 8 ? brush.Pattern8x8.AsSpan(0, 8) : default;
        lock (_gate)
        {
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan());
            for (int y = y0; y < y1; y++)
            {
                Span<uint> row = pixels.Slice(y * Width + x0, x1 - x0);
                byte bits = pattern.IsEmpty ? (byte)0 : pattern[(y - brush.OriginY) & 7];
                for (int i = 0; i < row.Length; i++)
                {
                    int x = x0 + i;
                    bool background = !pattern.IsEmpty && (bits & (0x80 >> ((x - brush.OriginX) & 7))) != 0;
                    uint p = background ? brush.BackBgra : brush.ForeBgra;
                    row[i] = rop3 == RopPatCopy ? p | 0xFF000000u : Rop3(rop3, p, 0, row[i]);
                }
            }

            Union(x0, y0, x1, y1);
        }
    }

    public void ScreenCopy(int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip)
    {
        if (!Clip(dest, clip, out int x0, out int y0, out int x1, out int y1))
        {
            return;
        }

        // The source moves with the clipped destination, and must itself lie on the surface.
        int sx0 = srcX + (x0 - dest.X), sy0 = srcY + (y0 - dest.Y);
        if (sx0 < 0) { x0 -= sx0; sx0 = 0; }
        if (sy0 < 0) { y0 -= sy0; sy0 = 0; }
        x1 = Math.Min(x1, x0 + (Width - sx0));
        y1 = Math.Min(y1, y0 + (Height - sy0));
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        int w = x1 - x0, h = y1 - y0;
        lock (_gate)
        {
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan());
            // Overlap-safe: walk rows away from the destination (Span.CopyTo is memmove within a row).
            bool down = sy0 < y0;
            uint[]? row = rop3 == RopSrcCopy ? null : new uint[w];
            for (int k = 0; k < h; k++)
            {
                int r = down ? h - 1 - k : k;
                Span<uint> source = pixels.Slice((sy0 + r) * Width + sx0, w);
                Span<uint> target = pixels.Slice((y0 + r) * Width + x0, w);
                if (row is null)
                {
                    source.CopyTo(target);
                    continue;
                }

                source.CopyTo(row);
                for (int i = 0; i < w; i++)
                {
                    target[i] = Rop3(rop3, 0, row[i], target[i]);
                }
            }

            Union(x0, y0, x1, y1);
        }
    }

    public void CachedBlit(CachedBitmap bitmap, int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip)
    {
        if (!Clip(dest, clip, out int x0, out int y0, out int x1, out int y1))
        {
            return;
        }

        int sx0 = srcX + (x0 - dest.X), sy0 = srcY + (y0 - dest.Y);
        if (sx0 < 0) { x0 -= sx0; sx0 = 0; }
        if (sy0 < 0) { y0 -= sy0; sy0 = 0; }
        x1 = Math.Min(x1, x0 + (bitmap.Width - sx0));
        y1 = Math.Min(y1, y0 + (bitmap.Height - sy0));
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        int w = x1 - x0, h = y1 - y0;
        ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(bitmap.Bgra.AsSpan());
        lock (_gate)
        {
            Span<uint> pixels = MemoryMarshal.Cast<byte, uint>(Bgra.AsSpan());
            for (int r = 0; r < h; r++)
            {
                ReadOnlySpan<uint> from = source.Slice((sy0 + r) * bitmap.Width + sx0, w);
                Span<uint> target = pixels.Slice((y0 + r) * Width + x0, w);
                if (rop3 == RopSrcCopy)
                {
                    from.CopyTo(target);
                    continue;
                }

                for (int i = 0; i < w; i++)
                {
                    target[i] = Rop3(rop3, 0, from[i], target[i]);
                }
            }

            Union(x0, y0, x1, y1);
        }
    }

    /// <summary>The order's rectangle clipped to the surface and its bounds; false when nothing remains.</summary>
    private bool Clip(OrderRect rect, OrderRect? clip, out int x0, out int y0, out int x1, out int y1)
    {
        long right = (long)rect.X + rect.Width, bottom = (long)rect.Y + rect.Height;
        x0 = Math.Max(rect.X, 0);
        y0 = Math.Max(rect.Y, 0);
        x1 = (int)Math.Min(right, Width);
        y1 = (int)Math.Min(bottom, Height);
        if (clip is { } c)
        {
            x0 = Math.Max(x0, c.X);
            y0 = Math.Max(y0, c.Y);
            x1 = (int)Math.Min(x1, (long)c.X + c.Width);
            y1 = (int)Math.Min(y1, (long)c.Y + c.Height);
        }

        return x1 > x0 && y1 > y0;
    }

    /// <summary>A ternary raster operation on 24-bit colours: bit (P·4 + S·2 + D) of <paramref name="rop3"/> gives each result bit.</summary>
    internal static uint Rop3(byte rop3, uint pattern, uint source, uint destination)
    {
        uint result = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((rop3 & (1 << i)) == 0)
            {
                continue;
            }

            result |= ((i & 4) != 0 ? pattern : ~pattern) & ((i & 2) != 0 ? source : ~source) & ((i & 1) != 0 ? destination : ~destination);
        }

        return (result & 0x00FFFFFFu) | 0xFF000000u;
    }
}
