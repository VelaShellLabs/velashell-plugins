namespace VelaShell.Rdp.Graphics.Gfx.RemoteFx;

/// <summary>A connection-wide bound on retained progressive coefficient, sign, and pixel buffers.</summary>
internal sealed class GfxProgressiveMemoryBudget(long maximumBytes = 256L * 1024 * 1024)
{
    private long _usedBytes;
    internal void Check(long oldBytes, long newBytes) =>
        RfxGuard.Require(newBytes >= 0 && _usedBytes - oldBytes + newBytes <= maximumBytes,
            "RemoteFX Progressive tile memory limit exceeded.");
    internal void Replace(long oldBytes, long newBytes)
    {
        Check(oldBytes, newBytes);
        _usedBytes += newBytes - oldBytes;
    }
}

/// <summary>Surface coefficient history outlives individual progressive encoding contexts.</summary>
internal sealed class GfxProgressiveSurfaceState(GfxProgressiveMemoryBudget? budget = null)
{
    // Include conservative allowances for the dictionaries and managed array headers.
    private const int TileBytes = 67 * 1024;
    private const int ContextTileBytes = 14 * 1024;
    private readonly GfxProgressiveMemoryBudget _budget = budget ?? new();
    private readonly Dictionary<GfxProgressiveCodec, int> _contexts = [];
    internal Dictionary<int, GfxProgressiveTile> Tiles { get; private set; } = [];

    /// <summary>Upper bound on region rectangles remembered for one logical frame.</summary>
    private const int MaxFrameRects = 65536;

    /// <summary>
    /// Region rectangles received so far in the current logical frame (StartFrame … EndFrame). MS-RDPEGFX
    /// 2.2.4.2.1.5: one frame can span several WIRE_TO_SURFACE_2 messages, and a rectangle of one message may be
    /// covered by a tile that only arrives in a later message of the same frame.
    /// </summary>
    internal List<RfxRect> FrameRects { get; } = [];

    /// <summary>A new logical frame begins: rectangles of earlier frames are final.</summary>
    internal void BeginFrame() => FrameRects.Clear();

    internal void AddFrameRects(List<RfxRect> rects)
    {
        RfxGuard.Require(FrameRects.Count + rects.Count <= MaxFrameRects, "Too many RemoteFX Progressive region rectangles in one frame.");
        FrameRects.AddRange(rects);
    }

    internal void Check(GfxProgressiveCodec codec, int tiles, int contextTiles)
    {
        _ = _contexts.TryGetValue(codec, out int previous);
        _budget.Check((long)Tiles.Count * TileBytes + (long)previous * ContextTileBytes,
            (long)tiles * TileBytes + (long)contextTiles * ContextTileBytes);
    }

    internal void Commit(GfxProgressiveCodec codec, Dictionary<int, GfxProgressiveTile> tiles, int contextTiles)
    {
        _ = _contexts.TryGetValue(codec, out int previous);
        _budget.Replace((long)Tiles.Count * TileBytes + (long)previous * ContextTileBytes,
            (long)tiles.Count * TileBytes + (long)contextTiles * ContextTileBytes);
        Tiles = tiles;
        if (contextTiles == 0)
        {
            _ = _contexts.Remove(codec);
        }
        else
        {
            _contexts[codec] = contextTiles;
        }
    }

    internal void ResetContext(GfxProgressiveCodec codec)
    {
        if (_contexts.Remove(codec, out int previous))
        {
            _budget.Replace((long)previous * ContextTileBytes, 0);
        }
    }

    internal void Reset()
    {
        long bytes = (long)Tiles.Count * TileBytes;
        foreach (KeyValuePair<GfxProgressiveCodec, int> pair in _contexts)
        {
            bytes += (long)pair.Value * ContextTileBytes;
            pair.Key.ClearContextState();
        }
        _budget.Replace(bytes, 0);
        _contexts.Clear();
        Tiles.Clear();
        FrameRects.Clear();
    }
}

internal sealed record GfxProgressiveTile(int[][] Coefficients, byte[] Pixels, bool Reduced);

/// <summary>MS-RDPEGFX RemoteFX Progressive (codec 9), with original and reduce-extrapolate DWT.</summary>
internal sealed class GfxProgressiveCodec
{
    private readonly GfxProgressiveSurfaceState _surface;
    private Dictionary<int, ProgressiveState> _states = [];

    internal GfxProgressiveCodec() : this(new GfxProgressiveSurfaceState()) { }
    internal GfxProgressiveCodec(GfxProgressiveSurfaceState surface) => _surface = surface;

    internal void Reset()
    {
        _surface.Reset();
        ResetEncodingContext();
    }

    internal void ResetEncodingContext()
    {
        _surface.ResetContext(this);
        ClearContextState();
    }

    internal void ClearContextState() => _states.Clear();

    /// <summary>Paints the tiles under <paramref name="rect" /> (only those in <paramref name="only" />, when given).</summary>
    private static void Paint(RfxRect rect, Dictionary<int, GfxProgressiveTile> tiles, HashSet<int>? only, Span<byte> destination, int stride)
    {
        for (int y = rect.Y / 64; y <= (rect.Y + rect.Height - 1) / 64; y++)
        {
            for (int x = rect.X / 64; x <= (rect.X + rect.Width - 1) / 64; x++)
            {
                int key = Key(x, y);
                if ((only is null || only.Contains(key)) && tiles.TryGetValue(key, out GfxProgressiveTile? tile))
                {
                    RfxPrimitives.Copy(tile.Pixels, x, y, rect, destination, stride);
                }
            }
        }
    }

    internal void Decode(ReadOnlySpan<byte> source, int width, int height, Span<byte> destination, int stride)
    {
        RfxGuard.Destination(width, height, destination, stride);
        RfxGuard.Require(!source.IsEmpty, "Empty RemoteFX Progressive stream.");
        var reader = new RfxReader(source);
        var tiles = new Dictionary<int, GfxProgressiveTile>(_surface.Tiles);
        var states = new Dictionary<int, ProgressiveState>(_states);
        var rectangles = new List<RfxRect>();
        bool begun = false, ended = false;
        while (reader.Remaining > 0)
        {
            RfxReader block = reader.Block(out ushort type);
            switch (type)
            {
                case 0xCCC0:
                    _ = block.UInt32();
                    _ = block.UInt16();
                    break;
                case 0xCCC1:
                    RfxGuard.Require(!begun, "Nested or duplicate RemoteFX Progressive frame.");
                    _ = block.UInt32();
                    _ = block.UInt16(); // Region count can exceed the number actually transmitted.
                    begun = true;
                    break;
                case 0xCCC2:
                    ended = begun;
                    break;
                case 0xCCC3:
                    _ = block.Byte();
                    RfxGuard.Require(block.UInt16() == 64, "Invalid RemoteFX Progressive tile size.");
                    _ = block.Byte();
                    break;
                case 0xCCC4:
                    if (begun && !ended)
                    {
                        Region(ref block, width, height, tiles, states, rectangles);
                    }
                    else
                    {
                        _ = block.Take(block.Remaining); // The protocol requires ignoring out-of-frame regions.
                    }

                    break;
                default:
                    _ = block.Take(block.Remaining);
                    break;
            }
            block.End();
        }
        // MS-RDPEGFX 2.2.4.2.1.5: the rectangles of a logical frame are covered by the tiles of the whole frame,
        // which can span several messages. So the new rectangles are painted from every tile known now, and the
        // frame's earlier rectangles are repainted where this message brought a new or upgraded tile — otherwise
        // they would keep the tile content painted before the update arrived. A tile not received yet is skipped:
        // it may still come later in the frame.
        HashSet<int> updated = [];
        foreach ((int key, GfxProgressiveTile tile) in tiles)
        {
            if (!_surface.Tiles.TryGetValue(key, out GfxProgressiveTile? previous) || !ReferenceEquals(previous, tile))
            {
                _ = updated.Add(key);
            }
        }
        foreach (RfxRect rect in _surface.FrameRects)
        {
            Paint(rect, tiles, updated, destination, stride);
        }
        foreach (RfxRect rect in rectangles)
        {
            Paint(rect, tiles, null, destination, stride);
        }

        _surface.AddFrameRects(rectangles);
        _surface.Commit(this, tiles, states.Count);
        _states = states;
    }

    private static int Key(int x, int y) => (y << 16) | x;

    private void Region(ref RfxReader reader, int width, int height, Dictionary<int, GfxProgressiveTile> tiles,
        Dictionary<int, ProgressiveState> states, List<RfxRect> rectangles)
    {
        RfxGuard.Require(reader.Byte() == 64, "Invalid RemoteFX Progressive region tile size.");
        int rectangleCount = reader.UInt16(), quantCount = reader.Byte(), progressiveCount = reader.Byte();
        bool reduced = (reader.Byte() & 1) != 0;
        int tileCount = reader.UInt16(), tileSize = reader.Length32();
        RfxGuard.Require(rectangleCount > 0 && rectangleCount <= reader.Remaining / 8 && quantCount <= 7,
            "Invalid RemoteFX Progressive region counts.");
        for (int i = 0; i < rectangleCount; i++)
        {
            rectangles.Add(RfxRect.Read(ref reader, width, height));
        }

        int[][] quants = new int[quantCount][];
        for (int i = 0; i < quantCount; i++)
        {
            quants[i] = RfxPrimitives.Quant(ref reader, progressive: true);
        }

        int[][][] progressive = new int[progressiveCount][][];
        for (int i = 0; i < progressiveCount; i++)
        {
            _ = reader.Byte();
            progressive[i] = [RfxPrimitives.Quant(ref reader, true, true), RfxPrimitives.Quant(ref reader, true, true), RfxPrimitives.Quant(ref reader, true, true)];
        }
        var data = new RfxReader(reader.Take(tileSize));
        RfxGuard.Require(tileCount <= data.Remaining / 22, "Truncated RemoteFX Progressive tiles.");
        for (int i = 0; i < tileCount; i++)
        {
            RfxReader tile = data.Block(out ushort type);
            RfxGuard.Require(type is 0xCCC5 or 0xCCC6 or 0xCCC7, "Invalid RemoteFX Progressive tile type.");
            int[] indices = [tile.Byte(), tile.Byte(), tile.Byte()];
            RfxGuard.Require(indices.All(index => index < quantCount), "Invalid RemoteFX Progressive quantization index.");
            int x = tile.UInt16(), y = tile.UInt16();
            RfxGuard.Tile(x, y, width, height);
            int key = Key(x, y);
            int nextStates = states.Count + (type == 0xCCC5 ? (states.ContainsKey(key) ? -1 : 0) : (states.ContainsKey(key) ? 0 : 1));
            _surface.Check(this, tiles.Count + (tiles.ContainsKey(key) ? 0 : 1), nextStates);
            bool upgrade = type == 0xCCC7;
            bool difference = !upgrade && (tile.Byte() & 1) != 0;
            int quality = type == 0xCCC5 ? 255 : tile.Byte();
            RfxGuard.Require(quality == 255 || quality < progressiveCount, "Invalid RemoteFX Progressive quality index.");
            int[][] bitPositions = new int[3][];
            for (int c = 0; c < 3; c++)
            {
                bitPositions[c] = new int[10];
                for (int band = 0; band < 10; band++)
                {
                    bitPositions[c][band] = quants[indices[c]][band] + (quality == 255 ? 0 : progressive[quality][c][band]);
                }
            }
            _ = tiles.TryGetValue(key, out GfxProgressiveTile? previous);
            RfxGuard.Require(!(upgrade || difference) || (previous is not null && previous.Reduced == reduced),
                "RemoteFX Progressive difference or upgrade has no matching reference tile.");
            int[][] coefficients;
            sbyte[][] signs;
            if (upgrade)
            {
                RfxGuard.Require(states.TryGetValue(key, out ProgressiveState? state) && state.Reduced == reduced,
                    "RemoteFX Progressive upgrade has no first pass in this encoding context.");
                int[] lengths = [tile.UInt16(), tile.UInt16(), tile.UInt16(), tile.UInt16(), tile.UInt16(), tile.UInt16()];
                coefficients = new int[3][];
                signs = new sbyte[3][];
                for (int c = 0; c < 3; c++)
                {
                    ReadOnlySpan<byte> srl = tile.Take(lengths[c * 2]);
                    ReadOnlySpan<byte> raw = tile.Take(lengths[c * 2 + 1]);
                    // Both references are established by the protocol guards above.
                    coefficients[c] = (int[])previous!.Coefficients[c].Clone();
                    signs[c] = (sbyte[])state!.Signs[c].Clone();
                    Upgrade(srl, raw, coefficients[c], signs[c], state.BitPositions[c], bitPositions[c], reduced);
                }
            }
            else
            {
                int[] lengths = [tile.UInt16(), tile.UInt16(), tile.UInt16()];
                int tailLength = tile.UInt16();
                coefficients = new int[3][];
                signs = new sbyte[3][];
                for (int c = 0; c < 3; c++)
                {
                    coefficients[c] = RfxPrimitives.Rlgr(tile.Take(lengths[c]), rlgr3: false);
                    signs[c] = new sbyte[4096];
                    for (int n = 0; n < 4096; n++)
                    {
                        signs[c][n] = (sbyte)Math.Sign(coefficients[c][n]);
                    }

                    RfxPrimitives.Differential(coefficients[c], reduced);
                    RfxPrimitives.Dequantize(coefficients[c], bitPositions[c], reduced);
                    if (difference)
                    {
                        for (int n = 0; n < 4096; n++)
                        {
                            coefficients[c][n] = Add(previous!.Coefficients[c][n], coefficients[c][n]);
                        }
                    }
                }
                _ = tile.Take(tailLength); // Reserved, explicitly ignored by MS-RDPEGFX.
            }
            tile.End();
            if (type == 0xCCC5)
            {
                _ = states.Remove(key);
            }
            else
            {
                states[key] = new ProgressiveState(signs, bitPositions, reduced);
            }

            tiles[key] = new GfxProgressiveTile(coefficients, RfxPrimitives.Pixels(coefficients, reduced), reduced);
        }
        data.End();
    }

    private static void Upgrade(ReadOnlySpan<byte> srlBytes, ReadOnlySpan<byte> rawBytes, int[] coefficients,
        sbyte[] signs, int[] oldPositions, int[] newPositions, bool reduced)
    {
        var srl = new RfxSrl(srlBytes);
        var raw = new RfxBits(rawBytes);
        ReadOnlySpan<int> lengths = RfxPrimitives.BandLengths(reduced);
        int offset = 0;
        for (int band = 0; band < 10; band++)
        {
            int count = oldPositions[band] - newPositions[band];
            RfxGuard.Require(count is >= 0 and <= 23, "Invalid RemoteFX Progressive quality progression.");
            for (int n = 0; n < lengths[band]; n++, offset++)
            {
                if (count == 0)
                {
                    continue;
                }

                int input;
                if (band == 9)
                {
                    input = raw.Read(count);
                }
                else if (signs[offset] != 0)
                {
                    input = raw.Read(count) * signs[offset];
                }
                else
                {
                    input = srl.Read(count);
                    signs[offset] = (sbyte)Math.Sign(input);
                }
                coefficients[offset] = Add(coefficients[offset], RfxPrimitives.Scale(input, newPositions[band]));
            }
        }
        raw.Finish();
        srl.Finish();
    }

    private static int Add(int left, int right)
    {
        int result = left + right;
        RfxGuard.Require(result is >= short.MinValue and <= short.MaxValue, "RemoteFX Progressive coefficient overflow.");
        return result;
    }

    private sealed record ProgressiveState(sbyte[][] Signs, int[][] BitPositions, bool Reduced);
}
