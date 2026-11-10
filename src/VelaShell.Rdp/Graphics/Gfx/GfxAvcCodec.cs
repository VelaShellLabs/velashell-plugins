using System.Buffers.Binary;
using ManagedCodecs;

namespace VelaShell.Rdp.Graphics.Gfx;

internal readonly record struct GfxAvcRectangle(int Left, int Top, int Right, int Bottom);

// AVC444 reconstruction follows MS-RDPEGFX 3.3.8.3 and FreeRDP prim_YUV.c
// (Apache-2.0; see THIRD-PARTY-NOTICES.txt).
internal sealed class GfxAvcCodec
{
    private readonly int _width, _height;
    private readonly ManagedH264Decoder _decoder;
    private byte[]? _y, _u, _v, _valid;
    internal long MemoryBudget { get; private set; }

    internal GfxAvcCodec(int width, int height, Action<long>? reserve = null)
    {
        _width = width;
        _height = height;
        _decoder = new ManagedH264Decoder(width, height, budget =>
        {
            budget = Math.Max(MemoryBudget, budget);
            reserve?.Invoke(budget);
            MemoryBudget = budget;
        });
    }

    internal void Decode(GfxDecodeRequest request)
    {
        ReadOnlySpan<byte> data = request.Data;
        if (request.CodecId == 11)
        {
            GfxAvcRectangle[] rectangles = ReadMetadata(ref data);
            using VideoFrame frame = _decoder.Decode(data);
            foreach (GfxAvcRectangle rect in rectangles)
            {
                for (int y = rect.Top; y < rect.Bottom; y++)
                {
                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        WriteRgb(request.Destination.AsSpan((y * _width + x) * 4),
                            Sample(frame, 0, x, y), Sample(frame, 1, x / 2, y / 2), Sample(frame, 2, x / 2, y / 2));
                    }
                }
            }

            return;
        }
        if (data.Length < 4)
        {
            throw new RdpProtocolException("Truncated AVC444 header.");
        }

        uint info = BinaryPrimitives.ReadUInt32LittleEndian(data);
        int mode = (int)(info >> 30);
        int firstLength = (int)(info & 0x3FFFFFFF);
        data = data[4..];
        if (mode == 3)
        {
            throw new RdpProtocolException("Invalid AVC444 LC mode.");
        }

        if (mode == 0)
        {
            if (firstLength <= 0 || firstLength >= data.Length)
            {
                throw new RdpProtocolException("Invalid AVC444 first stream length.");
            }

            ReadOnlySpan<byte> main = data[..firstLength];
            ReadOnlySpan<byte> auxiliary = data[firstLength..];
            GfxAvcRectangle[] mainRectangles = ReadMetadata(ref main);
            GfxAvcRectangle[] auxRectangles = ReadMetadata(ref auxiliary);
            using (VideoFrame frame = _decoder.Decode(main))
            {
                UpdateMain(frame, mainRectangles);
            }

            using (VideoFrame frame = _decoder.Decode(auxiliary))
            {
                UpdateAuxiliary(frame, auxRectangles, request.CodecId == 15);
            }

            Render(request.Destination, mainRectangles);
            Render(request.Destination, auxRectangles);
        }
        else
        {
            GfxAvcRectangle[] rectangles = ReadMetadata(ref data);
            using VideoFrame frame = _decoder.Decode(data);
            if (mode == 1)
            {
                UpdateMain(frame, rectangles);
            }
            else
            {
                UpdateAuxiliary(frame, rectangles, request.CodecId == 15);
            }

            Render(request.Destination, rectangles);
        }
    }

    private GfxAvcRectangle[] ReadMetadata(ref ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
        {
            throw new RdpProtocolException("Truncated AVC region metadata.");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (count > 65536 || count * 10L + 4 >= data.Length)
        {
            throw new RdpProtocolException("Invalid AVC region count.");
        }

        var rectangles = new GfxAvcRectangle[count];
        for (int i = 0; i < rectangles.Length; i++)
        {
            ReadOnlySpan<byte> rect = data.Slice(4 + i * 8, 8);
            int left = BinaryPrimitives.ReadUInt16LittleEndian(rect);
            int top = BinaryPrimitives.ReadUInt16LittleEndian(rect[2..]);
            int right = BinaryPrimitives.ReadUInt16LittleEndian(rect[4..]);
            int bottom = BinaryPrimitives.ReadUInt16LittleEndian(rect[6..]);
            if (left >= right || top >= bottom || right > _width || bottom > _height)
            {
                throw new RdpProtocolException("AVC region exceeds the surface.");
            }

            rectangles[i] = new(left, top, right, bottom);
        }
        data = data[(4 + rectangles.Length * 10)..];
        return rectangles;
    }

    internal void UpdateMain(VideoFrame frame, ReadOnlySpan<GfxAvcRectangle> rectangles)
    {
        EnsurePlanes();
        foreach (GfxAvcRectangle rect in rectangles)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    int index = y * _width + x;
                    _y![index] = Sample(frame, 0, x, y);
                    _u![index] = Sample(frame, 1, x / 2, y / 2);
                    _v![index] = Sample(frame, 2, x / 2, y / 2);
                    _valid![index] = 1;
                }
            }
        }
    }

    internal void UpdateAuxiliary(VideoFrame frame, ReadOnlySpan<GfxAvcRectangle> rectangles, bool version2)
    {
        if (_valid is null)
        {
            throw new RdpProtocolException("AVC444 auxiliary update precedes its main picture.");
        }

        if ((frame.Width & 15) != 0 || (frame.Height & 15) != 0)
        {
            throw new RdpProtocolException("AVC444 auxiliary dimensions must be aligned to 16 pixels.");
        }

        foreach (GfxAvcRectangle rect in rectangles)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    int index = y * _width + x;
                    if (_valid[index] == 0)
                    {
                        throw new RdpProtocolException("AVC444 auxiliary region has no main picture.");
                    }

                    if (!version2)
                    {
                        if ((y & 1) != 0)
                        {
                            int row = (y / 16) * 16 + (y % 16) / 2;
                            _u![index] = Sample(frame, 0, x, row);
                            _v![index] = Sample(frame, 0, x, row + 8);
                        }
                        else if ((x & 1) != 0)
                        {
                            _u![index] = Sample(frame, 1, x / 2, y / 2);
                            _v![index] = Sample(frame, 2, x / 2, y / 2);
                        }
                    }
                    else if ((x & 1) != 0)
                    {
                        _u![index] = Sample(frame, 0, x / 2, y);
                        _v![index] = Sample(frame, 0, x / 2 + frame.Width / 2, y);
                    }
                    else if ((y & 1) != 0)
                    {
                        int plane = (x & 2) == 0 ? 1 : 2;
                        _u![index] = Sample(frame, plane, x / 4, y / 2);
                        _v![index] = Sample(frame, plane, x / 4 + frame.Width / 4, y / 2);
                    }
                }
            }
        }
    }

    internal void Render(Span<byte> destination, ReadOnlySpan<GfxAvcRectangle> rectangles)
    {
        foreach (GfxAvcRectangle rect in rectangles)
        {
            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                for (int x = rect.Left; x < rect.Right; x++)
                {
                    int index = y * _width + x;
                    int u = _u![index], v = _v![index];
                    if ((x & 1) == 0 && (y & 1) == 0 && x + 1 < _width && y + 1 < _height)
                    {
                        u = ReverseFilter(_u, index, _width);
                        v = ReverseFilter(_v, index, _width);
                    }
                    WriteRgb(destination[(index * 4)..], _y![index], u, v);
                }
            }
        }
    }

    private void EnsurePlanes()
    {
        if (_y is not null)
        {
            return;
        }

        int size = checked(_width * _height);
        _y = new byte[size];
        _u = new byte[size];
        _v = new byte[size];
        _valid = new byte[size];
    }

    private static int ReverseFilter(byte[] plane, int index, int stride)
    {
        int original = plane[index];
        int reconstructed = Math.Clamp(4 * original - plane[index + 1] - plane[index + stride] - plane[index + stride + 1], 0, 255);
        return Math.Abs(reconstructed - original) < 30 ? original : reconstructed;
    }

    private static byte Sample(VideoFrame frame, int plane, int x, int y) =>
        frame.Planes[plane][frame.Offsets[plane] + y * frame.Strides[plane] + x];

    private static void WriteRgb(Span<byte> pixel, int y, int u, int v)
    {
        u -= 128;
        v -= 128;
        pixel[0] = (byte)Math.Clamp((256 * y + 475 * u) >> 8, 0, 255);
        pixel[1] = (byte)Math.Clamp((256 * y - 48 * u - 120 * v) >> 8, 0, 255);
        pixel[2] = (byte)Math.Clamp((256 * y + 403 * v) >> 8, 0, 255);
    }
}
