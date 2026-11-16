using System.Buffers.Binary;

namespace VelaShell.Vnc.Graphics;

/// <summary>
/// The client's copy of the remote desktop: top-down BGRA32 with alpha forced to 255, plus the region changed since the
/// UI last copied it. Decoders write on the receive thread and the UI copies on its own, so every operation takes the
/// lock; each one covers at most one rectangle or tile, so neither side waits long.
/// </summary>
internal sealed class VncFramebuffer
{
    /// <summary>Largest desktop side accepted from a server (a bogus size must not allocate gigabytes).</summary>
    internal const int MaxSide = 16384;

    private readonly Lock _gate = new();
    private byte[] _pixels = [];
    private int _dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom;
    private bool _dirty;

    internal int Width { get; private set; }
    internal int Height { get; private set; }

    internal void Resize(int width, int height)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide)
        {
            throw new VncProtocolException($"The server's desktop size {width}×{height} is out of range.");
        }

        lock (_gate)
        {
            if (width == Width && height == Height)
            {
                return;
            }

            byte[] pixels = new byte[width * height * 4];
            // Keep what overlaps (a resize usually adds or removes an edge) and make the rest opaque black.
            for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
            int rows = Math.Min(height, Height), bytes = Math.Min(width, Width) * 4;
            for (int y = 0; y < rows; y++)
            {
                Buffer.BlockCopy(_pixels, y * Width * 4, pixels, y * width * 4, bytes);
            }

            _pixels = pixels;
            Width = width;
            Height = height;
            MarkLocked(0, 0, width, height);
        }
    }

    /// <summary>Rejects a rectangle that does not lie inside the desktop.</summary>
    internal void Check(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || x + width > Width || y + height > Height)
        {
            throw new VncProtocolException($"The server sent a rectangle ({x},{y} {width}×{height}) outside the {Width}×{Height} desktop.");
        }
    }

    private void MarkLocked(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (!_dirty)
        {
            (_dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom, _dirty) = (x, y, x + width, y + height, true);
            return;
        }

        _dirtyLeft = Math.Min(_dirtyLeft, x);
        _dirtyTop = Math.Min(_dirtyTop, y);
        _dirtyRight = Math.Max(_dirtyRight, x + width);
        _dirtyBottom = Math.Max(_dirtyBottom, y + height);
    }

    /// <summary>Fills a rectangle with one colour (B, G, R in the low three bytes of <paramref name="bgr"/>).</summary>
    internal void Fill(int x, int y, int width, int height, uint bgr)
    {
        Check(x, y, width, height);
        if (width == 0 || height == 0) { return; }
        uint pixel = bgr | 0xFF000000;
        lock (_gate)
        {
            for (int row = 0; row < height; row++)
            {
                Span<uint> span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan(((y + row) * Width + x) * 4, width * 4));
                span.Fill(pixel);
            }
            MarkLocked(x, y, width, height);
        }
    }

    /// <summary>
    /// Copies BGRX pixels (4 bytes each, <paramref name="sourceStride"/> bytes per row) into a rectangle; the fourth byte
    /// is replaced by 255.
    /// </summary>
    internal void Blit(int x, int y, int width, int height, ReadOnlySpan<byte> source, int sourceStride)
    {
        Check(x, y, width, height);
        if (width == 0 || height == 0) { return; }
        if (source.Length < (height - 1) * sourceStride + width * 4)
        {
            throw new VncProtocolException("A rectangle's pixel data is shorter than its size.");
        }

        lock (_gate)
        {
            for (int row = 0; row < height; row++)
            {
                Span<byte> target = _pixels.AsSpan(((y + row) * Width + x) * 4, width * 4);
                source.Slice(row * sourceStride, width * 4).CopyTo(target);
                for (int i = 3; i < target.Length; i += 4) { target[i] = 255; }
            }
            MarkLocked(x, y, width, height);
        }
    }

    /// <summary>CopyRect: moves an on-screen rectangle (the regions may overlap).</summary>
    internal void CopyRect(int sourceX, int sourceY, int x, int y, int width, int height)
    {
        Check(x, y, width, height);
        Check(sourceX, sourceY, width, height);
        if (width == 0 || height == 0) { return; }
        lock (_gate)
        {
            int bytes = width * 4;
            if (sourceY < y)
            {
                for (int row = height - 1; row >= 0; row--) { Buffer.BlockCopy(_pixels, ((sourceY + row) * Width + sourceX) * 4, _pixels, ((y + row) * Width + x) * 4, bytes); }
            }
            else
            {
                for (int row = 0; row < height; row++) { Buffer.BlockCopy(_pixels, ((sourceY + row) * Width + sourceX) * 4, _pixels, ((y + row) * Width + x) * 4, bytes); }
            }
            MarkLocked(x, y, width, height);
        }
    }

    /// <summary>Reads one pixel (B, G, R in the low bytes) — the Tight gradient filter and tests use it.</summary>
    internal uint Pixel(int x, int y)
    {
        lock (_gate)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(_pixels.AsSpan((y * Width + x) * 4)) & 0xFFFFFF;
        }
    }

    /// <summary>See <see cref="IVncSession.CopyFrame"/>.</summary>
    internal VncFrameCopy CopyTo(Span<byte> destination, int stride, int width, int height, bool full)
    {
        lock (_gate)
        {
            if (Width == 0 || width != Width || height != Height)
            {
                return new(Width, Height, default, Width != 0 && (width != Width || height != Height));
            }

            if (stride < width * 4 || destination.Length < (height - 1) * stride + width * 4)
            {
                throw new ArgumentException("The destination surface is too small.", nameof(destination));
            }

            if (full)
            {
                (_dirtyLeft, _dirtyTop, _dirtyRight, _dirtyBottom, _dirty) = (0, 0, Width, Height, true);
            }

            if (!_dirty)
            {
                return new(Width, Height, default, false);
            }

            int left = _dirtyLeft, top = _dirtyTop, right = _dirtyRight, bottom = _dirtyBottom;
            int bytes = (right - left) * 4;
            for (int row = top; row < bottom; row++)
            {
                _pixels.AsSpan((row * Width + left) * 4, bytes).CopyTo(destination.Slice(row * stride + left * 4, bytes));
            }

            _dirty = false;
            return new(Width, Height, new(left, top, right - left, bottom - top), false);
        }
    }
}
