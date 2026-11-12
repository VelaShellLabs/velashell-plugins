using System.Buffers.Binary;

namespace VelaShell.Rdp.Graphics.Gfx;

internal sealed class GfxClearCodec
{
    private readonly byte[]?[] _glyphs = new byte[4000][];
    private readonly byte[]?[] _bars = new byte[32768][];
    private readonly byte[]?[] _shortBars = new byte[16384][];
    private int _barCursor, _shortCursor;
    private byte _sequence;

    internal void Reset()
    {
        Array.Clear(_glyphs);
        Array.Clear(_bars);
        Array.Clear(_shortBars);
        _barCursor = _shortCursor = 0;
        _sequence = 0;
    }

    internal void Decode(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        if (width <= 0 || height <= 0 || width > 65535 || height > 65535 || stride < width * 4 ||
            (long)(height - 1) * stride + width * 4 > destination.Length)
        {
            throw new RdpProtocolException("Invalid ClearCodec destination.");
        }

        var reader = new GfxClearReader(source);
        int flags = reader.Byte();
        if ((flags & ~7) != 0 || ((flags & 2) != 0 && (flags & 1) == 0))
        {
            throw new RdpProtocolException("Invalid ClearCodec flags.");
        }

        if (reader.Byte() != _sequence)
        {
            throw new RdpProtocolException("ClearCodec sequence mismatch.");
        }

        _sequence = unchecked((byte)(_sequence + 1));
        if ((flags & 4) != 0)
        {
            _barCursor = _shortCursor = 0;
        }

        int glyphIndex = -1;
        if ((flags & 1) != 0)
        {
            glyphIndex = reader.UInt16();
            if (glyphIndex >= _glyphs.Length || (long)width * height > 1024)
            {
                throw new RdpProtocolException("Invalid ClearCodec glyph index or size.");
            }

            if ((flags & 2) != 0)
            {
                byte[] glyph = _glyphs[glyphIndex] ?? throw new RdpProtocolException("Unknown ClearCodec glyph.");
                if (glyph.Length != width * height * 4 || !reader.Empty)
                {
                    throw new RdpProtocolException("Invalid ClearCodec glyph reference.");
                }

                for (int y = 0; y < height; y++)
                {
                    glyph.AsSpan(y * width * 4, width * 4).CopyTo(destination[(y * stride)..]);
                }

                return;
            }
        }
        int residualLength = reader.Length();
        int bandsLength = reader.Length();
        int subcodecLength = reader.Length();
        ReadOnlySpan<byte> residual = reader.Take(residualLength);
        ReadOnlySpan<byte> bands = reader.Take(bandsLength);
        ReadOnlySpan<byte> subcodecs = reader.Take(subcodecLength);
        if (!reader.Empty)
        {
            throw new RdpProtocolException("Trailing ClearCodec payload.");
        }

        DecodeResidual(residual, width, height, destination, stride);
        DecodeBands(bands, width, height, destination, stride);
        DecodeSubcodecs(subcodecs, width, height, destination, stride);
        if (glyphIndex >= 0)
        {
            byte[] glyph = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                destination.Slice(y * stride, width * 4).CopyTo(glyph.AsSpan(y * width * 4));
            }

            _glyphs[glyphIndex] = glyph;
        }
    }

    private static void DecodeResidual(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        var reader = new GfxClearReader(source);
        int index = 0, count = checked(width * height);
        while (!reader.Empty)
        {
            ReadOnlySpan<byte> color = reader.Take(3);
            int run = reader.Run();
            if (run == 0 || run > count - index)
            {
                throw new RdpProtocolException("Invalid ClearCodec residual run.");
            }

            for (int end = index + run; index < end; index++)
            {
                SetPixel(destination, stride, index % width, index / width, color);
            }
        }
    }

    private void DecodeBands(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        var reader = new GfxClearReader(source);
        while (!reader.Empty)
        {
            int left = reader.UInt16(), right = reader.UInt16(), top = reader.UInt16(), bottom = reader.UInt16();
            int barHeight = bottom - top + 1;
            if (left > right || top > bottom || right >= width || bottom >= height || barHeight > 52)
            {
                throw new RdpProtocolException("Invalid ClearCodec band rectangle.");
            }

            ReadOnlySpan<byte> background = reader.Take(3);
            for (int x = left; x <= right; x++)
            {
                int header = reader.UInt16();
                byte[] bar;
                if ((header & 0x8000) != 0)
                {
                    bar = _bars[header & 0x7FFF] ?? throw new RdpProtocolException("Unknown ClearCodec V-Bar.");
                    if (bar.Length != barHeight * 3)
                    {
                        throw new RdpProtocolException("ClearCodec V-Bar height mismatch.");
                    }
                }
                else
                {
                    int start;
                    byte[] shortBar;
                    if ((header & 0x4000) != 0)
                    {
                        shortBar = _shortBars[header & 0x3FFF] ?? throw new RdpProtocolException("Unknown ClearCodec short V-Bar.");
                        start = reader.Byte();
                    }
                    else
                    {
                        start = header & 255;
                        int end = header >> 8;
                        if (start > end || end > barHeight)
                        {
                            throw new RdpProtocolException("Invalid ClearCodec short V-Bar bounds.");
                        }

                        shortBar = reader.Take((end - start) * 3).ToArray();
                        _shortBars[_shortCursor] = shortBar;
                        _shortCursor = (_shortCursor + 1) & 16383;
                    }
                    if (start > barHeight || shortBar.Length > (barHeight - start) * 3)
                    {
                        throw new RdpProtocolException("ClearCodec short V-Bar exceeds its band.");
                    }

                    bar = new byte[barHeight * 3];
                    for (int y = 0; y < barHeight; y++)
                    {
                        background.CopyTo(bar.AsSpan(y * 3, 3));
                    }

                    shortBar.CopyTo(bar, start * 3);
                    _bars[_barCursor] = bar;
                    _barCursor = (_barCursor + 1) & 32767;
                }
                for (int y = 0; y < barHeight; y++)
                {
                    SetPixel(destination, stride, x, top + y, bar.AsSpan(y * 3, 3));
                }
            }
        }
    }

    private static void DecodeSubcodecs(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        var reader = new GfxClearReader(source);
        while (!reader.Empty)
        {
            int left = reader.UInt16(), top = reader.UInt16(), w = reader.UInt16(), h = reader.UInt16();
            int length = reader.Length();
            int codec = reader.Byte();
            // A bound, not an exact size (the raw subcodec checks its exact size below): NSCodec with unencoded planes
            // carries Y, Co, Cg and an optional alpha plane plus its 20-byte header, and with chroma subsampling the
            // luma rows are padded to a multiple of 8 — more than w*h*3 on its own, especially for narrow rectangles.
            if (w == 0 || h == 0 || left + w > width || top + h > height || length > (long)(w + 8) * (h + 1) * 4 + 32)
            {
                throw new RdpProtocolException("Invalid ClearCodec subcodec rectangle or size.");
            }

            ReadOnlySpan<byte> data = reader.Take(length);
            Span<byte> target = destination[(top * stride + left * 4)..];
            switch (codec)
            {
                case 0:
                    if (length != w * h * 3)
                    {
                        throw new RdpProtocolException("Invalid ClearCodec raw subcodec size.");
                    }

                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            SetPixel(target, stride, x, y, data.Slice((y * w + x) * 3, 3));
                        }
                    }

                    break;
                case 1:
                    GfxNsCodec.Decode(data, w, h, target, stride);
                    break;
                case 2:
                    DecodeRlex(data, w, h, target, stride);
                    break;
                default:
                    throw new RdpProtocolException("Unknown ClearCodec subcodec.");
            }
        }
    }

    private static void DecodeRlex(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        var reader = new GfxClearReader(source);
        int count = reader.Byte();
        if (count is < 1 or > 127)
        {
            throw new RdpProtocolException("Invalid ClearCodec palette size.");
        }

        ReadOnlySpan<byte> palette = reader.Take(count * 3);
        int bits = 0;
        while ((1 << bits) < count)
        {
            bits++;
        }

        int index = 0, pixelCount = checked(width * height);
        while (!reader.Empty)
        {
            int header = reader.Byte();
            int stop = header & ((1 << bits) - 1), depth = header >> bits;
            int start = stop - depth;
            int run = reader.Run();
            if (start < 0 || stop >= count || (long)run + depth + 1 > pixelCount - index)
            {
                throw new RdpProtocolException("Invalid ClearCodec RLEX segment.");
            }

            ReadOnlySpan<byte> color = palette.Slice(start * 3, 3);
            for (int end = index + run; index < end; index++)
            {
                SetPixel(destination, stride, index % width, index / width, color);
            }

            for (int p = start; p <= stop; p++, index++)
            {
                SetPixel(destination, stride, index % width, index / width, palette.Slice(p * 3, 3));
            }
        }
        if (index != pixelCount)
        {
            throw new RdpProtocolException("Incomplete ClearCodec RLEX bitmap.");
        }
    }

    private static void SetPixel(Span<byte> destination, int stride, int x, int y, ReadOnlySpan<byte> color)
    {
        int index = y * stride + x * 4;
        color.CopyTo(destination.Slice(index, 3));
        destination[index + 3] = 255;
    }
}

internal ref struct GfxClearReader(ReadOnlySpan<byte> source)
{
    private ReadOnlySpan<byte> _remaining = source;
    internal readonly bool Empty => _remaining.IsEmpty;
    internal ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > _remaining.Length)
        {
            throw new RdpProtocolException("Truncated GFX codec data.");
        }

        ReadOnlySpan<byte> result = _remaining[..count];
        _remaining = _remaining[count..];
        return result;
    }
    internal byte Byte() => Take(1)[0];
    internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    internal int Length()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        if (value > int.MaxValue)
        {
            throw new RdpProtocolException("GFX codec length is too large.");
        }

        return (int)value;
    }
    internal int Run()
    {
        int value = Byte();
        if (value == 255)
        {
            value = UInt16();
            if (value == 65535)
            {
                value = Length();
            }
        }
        return value;
    }
}
