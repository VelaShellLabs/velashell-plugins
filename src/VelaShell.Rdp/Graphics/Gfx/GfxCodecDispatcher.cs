using System.Buffers;
using System.Buffers.Binary;
using VelaShell.Rdp.Graphics.Gfx.RemoteFx;

namespace VelaShell.Rdp.Graphics;

internal sealed record GfxDecodeRequest(
    ushort SurfaceId, uint ContextId, ushort CodecId, byte PixelFormat,
    int Left, int Top, int Right, int Bottom, byte[] Data,
    byte[] Destination, int SurfaceWidth, int SurfaceHeight);

internal sealed class GfxCodecDispatcher : IDisposable
{
    private readonly GfxClearCodec _clear = new();
    private readonly Dictionary<(ushort Surface, uint Context), GfxRemoteFxCodec> _remoteFx = [];
    private readonly Dictionary<(ushort Surface, uint Context), GfxProgressiveCodec> _progressive = [];
    private readonly Dictionary<ushort, GfxProgressiveSurfaceState> _progressiveSurfaces = [];
    private readonly GfxProgressiveMemoryBudget _progressiveBudget = new();
    private readonly Dictionary<ushort, GfxAvcCodec> _avc = [];
    private const int MaxContexts = 256;

    internal void Decode(GfxDecodeRequest request)
    {
        int width = request.Right - request.Left;
        int height = request.Bottom - request.Top;
        if (request.SurfaceWidth <= 0 || request.SurfaceHeight <= 0 ||
            (long)request.SurfaceWidth * request.SurfaceHeight * 4 > request.Destination.Length ||
            width <= 0 || height <= 0 || request.Left < 0 || request.Top < 0 ||
            request.Right > request.SurfaceWidth || request.Bottom > request.SurfaceHeight ||
            request.PixelFormat is not (0x20 or 0x21))
        {
            throw new RdpProtocolException("Invalid GFX codec destination.");
        }

        int stride = checked(request.SurfaceWidth * 4);
        Span<byte> destination = request.Destination.AsSpan(request.Top * stride + request.Left * 4);
        if (request.CodecId is 11 or 14 or 15)
        {
            if (!_avc.TryGetValue(request.SurfaceId, out GfxAvcCodec? codec))
            {
                CheckContextLimit();
                ushort surfaceId = request.SurfaceId;
                codec = new GfxAvcCodec(request.SurfaceWidth, request.SurfaceHeight, budget =>
                {
                    long total = budget;
                    foreach (KeyValuePair<ushort, GfxAvcCodec> entry in _avc)
                    {
                        if (entry.Key != surfaceId)
                        {
                            total += entry.Value.MemoryBudget;
                        }
                    }

                    if (total > 1024L * 1024 * 1024)
                    {
                        throw new RdpProtocolException("GFX AVC contexts exceed the memory budget.");
                    }
                });
                _avc.Add(request.SurfaceId, codec);
            }
            codec.Decode(request);
            return;
        }
        if (request.CodecId == 12)
        {
            DecodeAlpha(request.Data, destination, width, height, stride);
            return;
        }
        if (request.CodecId == 0)
        {
            if (request.Data.Length != (long)width * height * 4)
            {
                throw new RdpProtocolException("Invalid GFX uncompressed bitmap length.");
            }

            CopyRgb(request.Data, destination, width, height, stride);
            return;
        }
        if (request.CodecId == 10)
        {
            byte[] pixels = ArrayPool<byte>.Shared.Rent(checked(width * height * 4));
            try
            {
                Planar.Decompress(request.Data, pixels, width, height, bottomUp: false);
                CopyRgb(pixels, destination, width, height, stride);
            }
            finally { ArrayPool<byte>.Shared.Return(pixels); }
            return;
        }

        // Color codecs must preserve the independently updated AlphaCodec plane.
        byte[] alpha = ArrayPool<byte>.Shared.Rent(checked(width * height));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                alpha[y * width + x] = destination[y * stride + x * 4 + 3];
            }
        }

        try
        {
            (ushort SurfaceId, uint ContextId) key = (request.SurfaceId, request.ContextId);
            switch (request.CodecId)
            {
                case 3:
                    if (!_remoteFx.TryGetValue(key, out GfxRemoteFxCodec? remoteFx))
                    {
                        CheckContextLimit();
                        _remoteFx.Add(key, remoteFx = new GfxRemoteFxCodec());
                    }
                    remoteFx.Decode(request.Data, width, height, destination, stride);
                    break;
                case 8:
                    _clear.Decode(request.Data, width, height, destination, stride);
                    break;
                case 9:
                    if (!_progressive.TryGetValue(key, out GfxProgressiveCodec? progressive))
                    {
                        CheckContextLimit();
                        if (!_progressiveSurfaces.TryGetValue(request.SurfaceId, out GfxProgressiveSurfaceState? surface))
                        {
                            _progressiveSurfaces.Add(request.SurfaceId, surface = new GfxProgressiveSurfaceState(_progressiveBudget));
                        }

                        _progressive.Add(key, progressive = new GfxProgressiveCodec(surface));
                    }
                    progressive.Decode(request.Data, width, height, destination, stride);
                    break;
                default:
                    throw new RdpProtocolException($"Unsupported GFX codec {request.CodecId}.");
            }
        }
        finally
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    destination[y * stride + x * 4 + 3] = alpha[y * width + x];
                }
            }

            ArrayPool<byte>.Shared.Return(alpha);
        }
    }

    private void CheckContextLimit()
    {
        if (_remoteFx.Count + _progressive.Count + _avc.Count >= MaxContexts)
        {
            throw new RdpProtocolException("Too many GFX codec contexts.");
        }
    }

    private static void CopyRgb(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int stride)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                source.Slice((y * width + x) * 4, 3).CopyTo(destination.Slice(y * stride + x * 4, 3));
            }
        }
    }

    private static void DecodeAlpha(ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int stride)
    {
        if (source.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(source) != 0x414C)
        {
            throw new RdpProtocolException("Invalid GFX alpha signature.");
        }

        bool compressed = BinaryPrimitives.ReadUInt16LittleEndian(source[2..]) != 0;
        source = source[4..];
        int count = checked(width * height);
        if (!compressed)
        {
            if (source.Length != count)
            {
                throw new RdpProtocolException("Invalid GFX alpha plane length.");
            }

            for (int i = 0; i < count; i++)
            {
                destination[(i / width) * stride + (i % width) * 4 + 3] = source[i];
            }

            return;
        }
        int offset = 0;
        while (!source.IsEmpty)
        {
            if (source.Length < 2)
            {
                throw new RdpProtocolException("Truncated GFX alpha run.");
            }

            byte value = source[0];
            uint run = source[1];
            source = source[2..];
            if (run == 255)
            {
                if (source.Length < 2)
                {
                    throw new RdpProtocolException("Truncated GFX alpha run.");
                }

                run = BinaryPrimitives.ReadUInt16LittleEndian(source);
                source = source[2..];
                if (run == 65535)
                {
                    if (source.Length < 4)
                    {
                        throw new RdpProtocolException("Truncated GFX alpha run.");
                    }

                    run = BinaryPrimitives.ReadUInt32LittleEndian(source);
                    source = source[4..];
                }
            }
            if (run == 0 || run > count - offset)
            {
                throw new RdpProtocolException("Invalid GFX alpha run length.");
            }

            int end = offset + (int)run;
            for (; offset < end; offset++)
            {
                destination[(offset / width) * stride + (offset % width) * 4 + 3] = value;
            }
        }
        if (offset != count)
        {
            throw new RdpProtocolException("Incomplete GFX alpha plane.");
        }
    }

    internal void DeleteSurface(ushort surfaceId)
    {
        foreach ((ushort Surface, uint Context) key in _remoteFx.Keys.Where(key => key.Surface == surfaceId).ToArray())
        {
            _ = _remoteFx.Remove(key);
        }

        foreach ((ushort Surface, uint Context) key in _progressive.Keys.Where(key => key.Surface == surfaceId).ToArray())
        {
            _progressive[key].ResetEncodingContext();
            _ = _progressive.Remove(key);
        }
        if (_progressiveSurfaces.Remove(surfaceId, out GfxProgressiveSurfaceState? surface))
        {
            surface.Reset();
        }

        _ = _avc.Remove(surfaceId);
    }

    internal void DeleteContext(ushort surfaceId, uint contextId)
    {
        _ = _remoteFx.Remove((surfaceId, contextId));
        // Sub-band differences belong to the surface and outlive encoding contexts.
        if (_progressive.Remove((surfaceId, contextId), out GfxProgressiveCodec? progressive))
        {
            progressive.ResetEncodingContext();
        }
    }

    internal void Reset()
    {
        _clear.Reset();
        _remoteFx.Clear();
        foreach (GfxProgressiveCodec codec in _progressive.Values)
        {
            codec.ResetEncodingContext();
        }

        _progressive.Clear();
        foreach (GfxProgressiveSurfaceState surface in _progressiveSurfaces.Values)
        {
            surface.Reset();
        }

        _progressiveSurfaces.Clear();
        _avc.Clear();
    }

    public void Dispose() => Reset();
}
