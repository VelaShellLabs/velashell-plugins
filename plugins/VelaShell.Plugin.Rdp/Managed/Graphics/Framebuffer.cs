using System.Buffers;
using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Graphics;

/// <summary>
/// A top-down BGRA32 desktop surface that bitmap updates paint into. RDP bitmap data is a
/// bottom-up DIB, so each decoded rectangle is blitted with a vertical flip. Alpha is forced
/// opaque to match the former native bridge's output contract.
/// </summary>
internal sealed class Framebuffer(int width, int height)
{
    internal int Width { get; private set; } = width;
    internal int Height { get; private set; } = height;
    internal byte[] Bgra { get; private set; } = NewOpaque(width, height);

    private static byte[] NewOpaque(int width, int height)
    {
        byte[] buffer = new byte[checked(width * height * 4)];
        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
        return buffer;
    }

    internal void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        Bgra = NewOpaque(width, height);
    }

    /// <summary>Paints one TS_BITMAP_DATA rectangle. <paramref name="data"/> is already past any compression header.</summary>
    internal void ApplyRect(int left, int top, int rectWidth, int rectHeight, int bpp, bool compressed, ReadOnlySpan<byte> data)
    {
        if (rectWidth <= 0 || rectHeight <= 0) return;
        int bytesPerPixel = bpp switch { 8 => 1, 15 => 2, 16 => 2, 24 => 3, 32 => 4, _ => 0 };
        if (bytesPerPixel == 0) throw new RdpProtocolException($"Unsupported bitmap bpp {bpp}.");
        int scanline = rectWidth * bytesPerPixel;
        int needed = scanline * rectHeight;

        byte[]? rented = null;
        ReadOnlySpan<byte> pixels;
        if (compressed)
        {
            rented = ArrayPool<byte>.Shared.Rent(needed);
            InterleavedRle.Decompress(data, rented.AsSpan(0, needed), rectWidth, rectHeight, bytesPerPixel);
            pixels = rented;
        }
        else
        {
            if (data.Length < needed) throw new RdpProtocolException("Bitmap data too short.");
            pixels = data;
        }

        try
        {
            for (int row = 0; row < rectHeight; row++)
            {
                int y = top + (rectHeight - 1 - row); // bottom-up DIB → top-down surface
                if (y < 0 || y >= Height) continue;
                int srcBase = row * scanline;
                int dstBase = (y * Width + left) * 4;
                for (int col = 0; col < rectWidth; col++)
                {
                    int x = left + col;
                    if (x < 0 || x >= Width) continue;
                    Convert(pixels, srcBase + col * bytesPerPixel, bytesPerPixel, Bgra, dstBase + col * 4);
                }
            }
        }
        finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
    }

    private static void Convert(ReadOnlySpan<byte> src, int sp, int bytesPerPixel, byte[] dst, int dp)
    {
        byte r, g, b;
        switch (bytesPerPixel)
        {
            case 2:
            {
                int p = src[sp] | (src[sp + 1] << 8);
                // Distinguish RGB565 vs RGB555 by range is impossible per-pixel; RDP 16bpp is RGB565.
                int r5 = (p >> 11) & 0x1F, g6 = (p >> 5) & 0x3F, b5 = p & 0x1F;
                r = (byte)((r5 << 3) | (r5 >> 2));
                g = (byte)((g6 << 2) | (g6 >> 4));
                b = (byte)((b5 << 3) | (b5 >> 2));
                break;
            }
            case 3:
                b = src[sp]; g = src[sp + 1]; r = src[sp + 2]; // PIXEL_FORMAT_BGR24
                break;
            case 4:
                b = src[sp]; g = src[sp + 1]; r = src[sp + 2]; // BGRX32
                break;
            default:
                b = g = r = src[sp]; // 8bpp without palette: render as grey
                break;
        }
        dst[dp] = b;
        dst[dp + 1] = g;
        dst[dp + 2] = r;
        dst[dp + 3] = 255;
    }
}
