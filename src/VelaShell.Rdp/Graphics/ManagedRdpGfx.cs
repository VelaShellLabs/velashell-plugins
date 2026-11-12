using System.Buffers.Binary;
using VelaShell.Rdp.Graphics.Gfx;

namespace VelaShell.Rdp.Graphics;

/// <summary>Desktop MS-RDPEGFX state machine. The DVC transport remains owned by the managed session.</summary>
internal sealed class ManagedRdpGfx : IRdpGfxBackend
{
    private const long SurfaceBudget = 512L * 1024 * 1024;
    private const int SmallCacheBytes = 16 * 1024 * 1024;
    private static readonly uint[] Versions = [0x80004, 0x80105, 0xa0002, 0xa0100, 0xa0200, 0xa0301, 0xa0400, 0xa0502, 0xa0600, 0xa0701];
    private readonly ZgfxDecoder _zgfx = new();
    private readonly GfxCodecDispatcher _codecs = new();
    private readonly Dictionary<ushort, Surface> _surfaces = [];
    private readonly Dictionary<ushort, CacheEntry> _cache = [];
    private readonly HashSet<(ushort Surface, uint Context)> _contexts = [];
    private readonly Queue<byte[]> _responses = new();
    private byte[] _output, _input = [];
    private int _width, _height, _position, _cacheBytes, _cacheLimit = SmallCacheBytes, _cacheSlots = 4096;
    private long _surfaceBytes;
    private bool _opened, _disposed, _confirmed, _inFrame, _frameReady;
    private uint _version, _capFlags, _frameId, _decodedFrames;
    private Rect _dirty;
    private readonly Action<string>? _log;

    internal ManagedRdpGfx(int width, int height, Action<string>? log = null)
    {
        _log = log;
        ValidateDimensions(width, height);
        _width = width; _height = height;
        _output = AllocatePixels(width, height);
    }

    public void Open()
    {
        ThrowIfDisposed();
        if (_opened) { throw Error("Channel opened twice."); }
        _opened = true;
        byte[] caps = Pdu(0x12, 2 + Versions.Sum(static version => version == 0xa0100 ? 24 : 12));
        Put16(caps, 8, (ushort)Versions.Length);
        int offset = 10;
        foreach (uint version in Versions)
        {
            int size = version == 0xa0100 ? 16 : 4;
            Put32(caps, offset, version); Put32(caps, offset + 4, (uint)size);
            if (size == 4) { Put32(caps, offset + 8, version == 0x80105 ? 0x12u : version == 0xa0301 ? 0u : 2u); }
            offset += 8 + size;
        }
        Enqueue(caps);
        Log("graphics DVC opened; advertising capabilities");
    }

    public void Feed(byte[] message)
    {
        ThrowIfDisposed();
        if (!_opened || _position != _input.Length || _frameReady) { throw Error("Previous graphics message has not been consumed."); }
        _input = _zgfx.Decode(message);
        _position = 0;
        ProcessUntilFrame();
    }

    public bool TryGetFrame(out RdpGfxFrame frame)
    {
        ThrowIfDisposed();
        if (!_frameReady) { ProcessUntilFrame(); }
        if (!_frameReady) { frame = default; return false; }
        frame = new RdpGfxFrame
        {
            Pixels = _output,
            Width = _width,
            Height = _height,
            Stride = _width * 4,
            X = _dirty.Left,
            Y = _dirty.Top,
            W = _dirty.Width,
            H = _dirty.Height
        };
        _frameReady = false;
        _dirty = default;
        return true;
    }

    public byte[]? ReadResponse()
    {
        ThrowIfDisposed();
        // The channel must copy every completed frame before any acknowledgements are released.
        if (_frameReady || _position != _input.Length) { throw Error("Frames must be presented before responses are read."); }
        return _responses.TryDequeue(out byte[]? response) ? response : null;
    }

    private void ProcessUntilFrame()
    {
        while (!_frameReady && _position < _input.Length)
        {
            ReadOnlySpan<byte> remaining = _input.AsSpan(_position);
            if (remaining.Length < 8) { throw Error("Truncated PDU header."); }
            ushort command = BinaryPrimitives.ReadUInt16LittleEndian(remaining);
            if (BinaryPrimitives.ReadUInt16LittleEndian(remaining[2..]) != 0) { throw Error("Invalid reserved PDU flags."); }
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(remaining[4..]);
            if (length < 8 || length > remaining.Length) { throw Error("Invalid PDU length."); }
            var reader = new Reader(remaining[8..(int)length]);
            if (!_confirmed && command != 0x13) { throw Error("Graphics command precedes capability confirmation."); }
            Process(command, ref reader);
            reader.End();
            _position += (int)length;
        }
        if (_position == _input.Length) { _input = []; _position = 0; }
    }

    private void Process(ushort command, ref Reader reader)
    {
        switch (command)
        {
            case 1: WireToSurface(ref reader, progressive: false); break;
            case 2: WireToSurface(ref reader, progressive: true); break;
            case 3:
                {
                    ushort id = reader.U16(); _ = GetSurface(id);
                    uint context = reader.U32(); reader.End();
                    if (!_contexts.Remove((id, context))) { throw Error("Unknown encoding context."); }
                    _codecs.DeleteContext(id, context);
                    break;
                }
            case 4: SolidFill(ref reader); break;
            case 5: SurfaceToSurface(ref reader); break;
            case 6: SurfaceToCache(ref reader); break;
            case 7: CacheToSurface(ref reader); break;
            case 8:
                {
                    ushort slot = reader.U16(); reader.End();
                    if (!_cache.Remove(slot, out CacheEntry? entry)) { throw Error("Cannot evict an unknown cache entry."); }
                    _cacheBytes -= entry.Pixels.Length;
                    break;
                }
            case 9: CreateSurface(ref reader); break;
            case 10:
                {
                    ushort id = reader.U16(); reader.End();
                    Surface surface = GetSurface(id);
                    _codecs.DeleteSurface(id);
                    _ = _contexts.RemoveWhere(context => context.Surface == id);
                    _ = _surfaces.Remove(id); _surfaceBytes -= surface._pixels.Length;
                    break;
                }
            case 11:
                {
                    _ = reader.U32(); uint id = reader.U32(); reader.End();
                    if (_inFrame) { throw Error("Nested logical frame."); }
                    _frameId = id; _inFrame = true;
                    break;
                }
            case 12: EndFrame(ref reader); break;
            case 14: ResetGraphics(ref reader); break;
            case 15: MapOutput(ref reader, scaled: false); break;
            case 17: throw Error("Unsolicited persistent cache import reply.");
            case 19: ConfirmCapabilities(ref reader); break;
            case 23: MapOutput(ref reader, scaled: true); break;
            case 21:
            case 24: throw Error("RAIL surface mapping was received for a desktop-only session.");
            default: throw Error($"Unsupported server command 0x{command:X4}.");
        }
    }

    private void ConfirmCapabilities(ref Reader reader)
    {
        if (_confirmed) { throw Error("Capabilities were confirmed twice."); }
        uint version = reader.U32(), length = reader.U32();
        if (length != reader.Remaining) { throw Error("Invalid capability data length."); }
        if (!Versions.Contains(version))
        {
            // MS-RDPEGFX 3.3.5.19 requires unknown capability sets to be ignored.
            _ = reader.Bytes((int)length);
            return;
        }
        if (length != (version == 0xa0100 ? 16u : 4u)) { throw Error("Invalid capability data length."); }
        uint flags = version == 0xa0100 ? 0 : reader.U32();
        if (version == 0xa0100) { _ = reader.Bytes(16); }
        reader.End();
        _version = version; _capFlags = flags; _confirmed = true;
        Log($"server confirmed graphics capabilities: version=0x{version:X8}, flags=0x{flags:X8}");
        // The server's confirmed flags determine the cache size, even if it omits our small-cache preference.
        if (version != 0xa0301 && (flags & 3) == 0) { _cacheLimit = 100 * 1024 * 1024; _cacheSlots = 25600; }
        // No persistent cache is implemented. Do not initiate cache import with an empty offer.
    }

    private void CreateSurface(ref Reader reader)
    {
        ushort id = reader.U16(); int width = reader.U16(), height = reader.U16(); byte format = reader.U8(); reader.End();
        ValidateDimensions(width, height); ValidatePixelFormat(format);
        long bytes = (long)width * height * 4;
        if (_surfaces.ContainsKey(id) || _surfaces.Count >= 256 || bytes > SurfaceBudget - _surfaceBytes)
        { throw Error("Duplicate surface or surface memory budget exceeded."); }
        _surfaces.Add(id, new Surface(width, height, format, AllocatePixels(width, height)));
        _surfaceBytes += bytes;
    }

    private void WireToSurface(ref Reader reader, bool progressive)
    {
        RequireFrame();
        ushort id = reader.U16(), codec = reader.U16();
        uint context = progressive ? reader.U32() : 0;
        byte format = reader.U8(); ValidatePixelFormat(format);
        Surface surface = GetSurface(id);
        Rect rectangle = progressive ? new(0, 0, surface._width, surface._height) : reader.Rect();
        ValidateRect(rectangle, surface._width, surface._height);
        uint length = reader.U32();
        if (length != reader.Remaining) { throw Error("Bitmap length does not match the PDU."); }
        if (progressive ? codec != 9 : codec is not (0 or 3 or 8 or 10 or 11 or 12 or 14 or 15))
        { throw Error("Codec is not valid for this wire-to-surface command."); }
        if (codec is 11 or 14 or 15)
        {
            bool avc = _version == 0x80105 ? (_capFlags & 0x10) != 0 : _version >= 0xa0002 && (_capFlags & 0x20) == 0;
            if (!avc || (codec is 14 or 15 && _version < 0xa0002) || (codec == 15 && _version < 0xa0100))
            { throw Error("Server used an AVC mode that was not negotiated."); }
        }
        if (progressive && !_contexts.Contains((id, context)) && _contexts.Count >= 8192)
        { throw Error("Encoding context budget exceeded."); }
        _codecs.Decode(new GfxDecodeRequest(id, context, codec, format, rectangle.Left, rectangle.Top,
            rectangle.Right, rectangle.Bottom, reader.Bytes((int)length).ToArray(), surface._pixels, surface._width, surface._height));
        if (progressive) { _ = _contexts.Add((id, context)); }
        // AVC metadata may describe updates outside the wire command's bounding rectangle.
        surface._dirty = Rect.Union(surface._dirty, codec is 11 or 14 or 15 ? new(0, 0, surface._width, surface._height) : rectangle);
    }

    private void SolidFill(ref Reader reader)
    {
        RequireFrame();
        Surface surface = GetSurface(reader.U16());
        uint color = reader.U32(); int count = reader.U16();
        if (surface._format == 0x20) { color |= 0xff000000; }
        reader.Require(count * 8);
        for (int i = 0; i < count; i++)
        {
            Rect rectangle = reader.Rect(); ValidateRect(rectangle, surface._width, surface._height);
            for (int y = rectangle.Top; y < rectangle.Bottom; y++)
            {
                Span<byte> row = surface._pixels.AsSpan((y * surface._width + rectangle.Left) * 4, rectangle.Width * 4);
                for (int x = 0; x < row.Length; x += 4) { BinaryPrimitives.WriteUInt32LittleEndian(row[x..], color); }
            }
            surface._dirty = Rect.Union(surface._dirty, rectangle);
        }
    }

    private void SurfaceToSurface(ref Reader reader)
    {
        RequireFrame();
        Surface source = GetSurface(reader.U16()), target = GetSurface(reader.U16());
        Rect rectangle = reader.Rect(); ValidateRect(rectangle, source._width, source._height);
        int count = reader.U16();
        ReadOnlySpan<byte> points = reader.Bytes(count * 4); reader.End();
        for (int i = 0; i < count; i++)
        {
            int x = BinaryPrimitives.ReadUInt16LittleEndian(points[(i * 4)..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(points[(i * 4 + 2)..]);
            ValidateRect(new(x, y, x + rectangle.Width, y + rectangle.Height), target._width, target._height);
        }
        if (count == 0) { return; }
        // One immutable source snapshot also handles multiple destinations that overlap the source or each other.
        byte[] pixels = CopyRectangle(source, rectangle);
        for (int i = 0; i < count; i++)
        {
            int x = BinaryPrimitives.ReadUInt16LittleEndian(points[(i * 4)..]);
            int y = BinaryPrimitives.ReadUInt16LittleEndian(points[(i * 4 + 2)..]);
            CopyTo(pixels, rectangle.Width, rectangle.Height, target, x, y);
        }
    }

    private void SurfaceToCache(ref Reader reader)
    {
        RequireFrame();
        Surface source = GetSurface(reader.U16());
        ulong key = reader.U64(); ushort slot = reader.U16();
        Rect rectangle = reader.Rect(); reader.End(); ValidateRect(rectangle, source._width, source._height);
        ValidateSlot(slot);
        int bytes = rectangle.Width * rectangle.Height * 4;
        int oldBytes = _cache.TryGetValue(slot, out CacheEntry? old) ? old.Pixels.Length : 0;
        if (bytes > _cacheLimit - _cacheBytes + oldBytes) { throw Error("Bitmap cache memory budget exceeded."); }
        _cache[slot] = new CacheEntry(key, rectangle.Width, rectangle.Height, CopyRectangle(source, rectangle));
        _cacheBytes += bytes - oldBytes;
    }

    private void CacheToSurface(ref Reader reader)
    {
        RequireFrame();
        ushort slot = reader.U16(); Surface surface = GetSurface(reader.U16());
        int count = reader.U16(); reader.Require(count * 4);
        if (!_cache.TryGetValue(slot, out CacheEntry? entry)) { throw Error("Unknown bitmap cache slot."); }
        for (int i = 0; i < count; i++) { CopyTo(entry.Pixels, entry.Width, entry.Height, surface, reader.U16(), reader.U16()); }
    }

    private void ResetGraphics(ref Reader reader)
    {
        if (reader.Remaining != 332 || _inFrame) { throw Error("Invalid graphics reset."); }
        int width = reader.Dimension(), height = reader.Dimension(); uint monitors = reader.U32();
        if (monitors > 16) { throw Error("Too many monitor definitions."); }
        for (int i = 0; i < monitors; i++)
        {
            int left = reader.I32(), top = reader.I32(), right = reader.I32(), bottom = reader.I32();
            _ = reader.U32();
            if (right < left || bottom < top || (long)right - left + 1 > Framebuffer.MaxBitmapSide
                || (long)bottom - top + 1 > Framebuffer.MaxBitmapSide) { throw Error("Invalid monitor rectangle."); }
        }
        _ = reader.Bytes(reader.Remaining);
        _width = width; _height = height; _output = AllocatePixels(width, height);
        foreach (Surface surface in _surfaces.Values) { surface._dirty = new(0, 0, surface._width, surface._height); }
        _dirty = new(0, 0, width, height); _frameReady = true;
        Log($"graphics reset: {width}x{height}, monitors={monitors}");
    }

    private void MapOutput(ref Reader reader, bool scaled)
    {
        Surface surface = GetSurface(reader.U16());
        if (reader.U16() != 0) { throw Error("Invalid reserved mapping field."); }
        uint x = reader.U32(), y = reader.U32();
        int width = scaled ? reader.Dimension() : surface._width, height = scaled ? reader.Dimension() : surface._height;
        reader.End();
        if (scaled && (_version < 0xa0502 || (_version == 0xa0701 && (_capFlags & 0x80) != 0)))
        { throw Error("Scaled output mapping was not negotiated."); }
        if (x > Framebuffer.MaxBitmapSide || y > Framebuffer.MaxBitmapSide) { throw Error("Output origin exceeds desktop limits."); }
        surface._mapping = new Mapping((int)x, (int)y, width, height);
        surface._dirty = new(0, 0, surface._width, surface._height);
    }

    private void EndFrame(ref Reader reader)
    {
        uint id = reader.U32(); reader.End();
        if (!_inFrame || id != _frameId) { throw Error("Frame end does not match its start."); }
        foreach (Surface surface in _surfaces.Values)
        {
            if (!surface._dirty.Empty && surface._mapping is { } mapping) { Compose(surface, mapping); }
            surface._dirty = default;
        }
        _inFrame = false;
        if (_decodedFrames == 0) { Log($"first graphics frame decoded: id={id}"); }
        _decodedFrames = unchecked(_decodedFrames + 1);
        byte[] ack = Pdu(13, 12);
        Put32(ack, 12, id); Put32(ack, 16, _decodedFrames);
        Enqueue(ack);
        _frameReady = !_dirty.Empty;
    }

    private void Compose(Surface surface, Mapping mapping)
    {
        Rect source = surface._dirty;
        int left = mapping.X + (int)((long)source.Left * mapping.Width / surface._width);
        int top = mapping.Y + (int)((long)source.Top * mapping.Height / surface._height);
        int right = Math.Min(_width, mapping.X + (int)(((long)source.Right * mapping.Width + surface._width - 1) / surface._width));
        int bottom = Math.Min(_height, mapping.Y + (int)(((long)source.Bottom * mapping.Height + surface._height - 1) / surface._height));
        if (left >= right || top >= bottom) { return; }
        if (mapping.Width == surface._width && mapping.Height == surface._height)
        {
            for (int y = top; y < bottom; y++)
            {
                surface._pixels.AsSpan(((y - mapping.Y) * surface._width + left - mapping.X) * 4, (right - left) * 4)
                    .CopyTo(_output.AsSpan((y * _width + left) * 4));
            }
        }
        else
        {
            for (int y = top; y < bottom; y++)
            {
                int sy = (int)((long)(y - mapping.Y) * surface._height / mapping.Height);
                for (int x = left; x < right; x++)
                {
                    int sx = (int)((long)(x - mapping.X) * surface._width / mapping.Width);
                    surface._pixels.AsSpan((sy * surface._width + sx) * 4, 4).CopyTo(_output.AsSpan((y * _width + x) * 4, 4));
                }
            }
        }
        _dirty = Rect.Union(_dirty, new(left, top, right, bottom));
    }

    private static byte[] CopyRectangle(Surface source, Rect rectangle)
    {
        byte[] result = new byte[rectangle.Width * rectangle.Height * 4];
        for (int y = 0; y < rectangle.Height; y++)
        {
            source._pixels.AsSpan(((rectangle.Top + y) * source._width + rectangle.Left) * 4, rectangle.Width * 4)
                .CopyTo(result.AsSpan(y * rectangle.Width * 4));
        }
        return result;
    }

    private static void CopyTo(byte[] pixels, int width, int height, Surface target, int x, int y)
    {
        var rectangle = new Rect(x, y, x + width, y + height);
        ValidateRect(rectangle, target._width, target._height);
        for (int row = 0; row < height; row++)
        {
            pixels.AsSpan(row * width * 4, width * 4).CopyTo(target._pixels.AsSpan(((y + row) * target._width + x) * 4));
        }
        target._dirty = Rect.Union(target._dirty, rectangle);
    }

    private Surface GetSurface(ushort id) => _surfaces.TryGetValue(id, out Surface? surface) ? surface : throw Error($"Unknown surface {id}.");
    private void RequireFrame() { if (!_inFrame) { throw Error("Drawing command outside a logical frame."); } }
    private void ValidateSlot(ushort slot)
    {
        // Servers in use also assign slot zero; accept it alongside the specification's one-based slots.
        if (slot > _cacheSlots || (!_cache.ContainsKey(slot) && _cache.Count >= _cacheSlots)) { throw Error("Bitmap cache slot exceeds negotiated limit."); }
    }
    private static void ValidateDimensions(int width, int height)
    {
        if (width is < 1 or > Framebuffer.MaxBitmapSide || height is < 1 or > Framebuffer.MaxBitmapSide)
        { throw Error("Surface dimensions exceed supported desktop limits."); }
    }
    private static void ValidatePixelFormat(byte format)
    {
        if (format is not (0x20 or 0x21)) { throw Error("Unknown surface pixel format."); }
    }
    private static void ValidateRect(Rect rect, int width, int height)
    {
        if (rect.Left < 0 || rect.Top < 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top || rect.Right > width || rect.Bottom > height)
        { throw Error("Rectangle lies outside its surface."); }
    }
    private static byte[] AllocatePixels(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
        return pixels;
    }
    private void Enqueue(byte[] response)
    {
        if (_responses.Count >= 8192) { throw Error("Too many pending graphics responses."); }
        _responses.Enqueue(response);
    }
    private static byte[] Pdu(ushort command, int bodyLength)
    {
        byte[] result = new byte[8 + bodyLength]; Put16(result, 0, command); Put32(result, 4, (uint)result.Length); return result;
    }
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static RdpProtocolException Error(string detail) => new($"Invalid RDPGFX data: {detail}");
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _codecs.Dispose(); _surfaces.Clear(); _cache.Clear(); _contexts.Clear(); _responses.Clear();
        _input = []; _output = []; _surfaceBytes = 0; _cacheBytes = 0;
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch { /* Diagnostics must not interrupt graphics processing. */ }
    }

    private sealed class Surface(int width, int height, byte format, byte[] pixels)
    {
        internal readonly int _width = width, _height = height;
        internal readonly byte _format = format;
        internal readonly byte[] _pixels = pixels;
        internal Rect _dirty;
        internal Mapping? _mapping;
    }
    private sealed record CacheEntry(ulong Key, int Width, int Height, byte[] Pixels);
    private sealed record Mapping(int X, int Y, int Width, int Height);
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
        internal bool Empty => Width <= 0 || Height <= 0;
        internal static Rect Union(Rect a, Rect b) => a.Empty ? b : b.Empty ? a :
            new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    }
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;
        internal Reader(ReadOnlySpan<byte> data) => _data = data;
        internal readonly int Remaining => _data.Length - _position;
        internal readonly void Require(int count) { if (count < 0 || count > Remaining) { throw Error("Truncated PDU body."); } }
        internal ReadOnlySpan<byte> Bytes(int count) { Require(count); ReadOnlySpan<byte> result = _data.Slice(_position, count); _position += count; return result; }
        internal byte U8() => Bytes(1)[0];
        internal ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        internal int I32() => unchecked((int)U32());
        internal ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
        internal Rect Rect() => new(U16(), U16(), U16(), U16());
        internal int Dimension()
        {
            uint value = U32();
            if (value is < 1 or > Framebuffer.MaxBitmapSide) { throw Error("Invalid output dimensions."); }
            return (int)value;
        }
        internal readonly void End() { if (Remaining != 0) { throw Error("Unexpected trailing PDU bytes."); } }
    }
}
