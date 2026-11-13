using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Offline replay of an RDPGFX capture written with <c>VELASHELL_RDP_GFX_CAPTURE</c> (see
/// <see cref="ManagedRdpGfx.CaptureVariable" />). Set <c>VELASHELL_RDP_GFX_REPLAY</c> to the .vgfx file; next to it
/// the replay writes <c>.replay/</c> with output snapshots (PNG), an alpha map, a command trace and a "last writer"
/// grid (which trace line last painted each 16×16 output cell). Skipped when the variable is not set.
/// </summary>
[TestClass]
public sealed class GfxCaptureReplayTests
{
    private const int Cell = 16;
    private const int SnapshotEvery = 50;

    [TestMethod]
    public void ReplayCapture()
    {
        if (Environment.GetEnvironmentVariable("VELASHELL_RDP_GFX_REPLAY") is not { Length: > 0 } path || !File.Exists(path))
        {
            Assert.Inconclusive("Set VELASHELL_RDP_GFX_REPLAY to a .vgfx capture to replay it.");
            return;
        }
        byte[] capture = File.ReadAllBytes(path);
        Assert.IsTrue(capture.AsSpan(0, 8).SequenceEqual(ManagedRdpGfx.CaptureMagic), "Not a VelaShell RDPGFX capture.");
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(capture.AsSpan(8));
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(capture.AsSpan(12));
        string outDir = path + ".replay";
        Directory.CreateDirectory(outDir);

        using var gfx = new ManagedRdpGfx(width, height);
        var trace = new Tracer();
        gfx.CommandObserver = trace.Observe;
        gfx.Open();
        while (gfx.ReadResponse() is not null) { }

        int offset = 16, messages = 0, frames = 0;
        while (offset + 4 <= capture.Length)
        {
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(capture.AsSpan(offset));
            byte[] message = capture.AsSpan(offset + 4, length).ToArray();
            offset += 4 + length;
            messages++;
            try { gfx.Feed(message); }
            catch (Exception ex)
            {
                trace.Line($"!! message {messages}: {ex.GetType().Name}: {ex.Message}");
                break;
            }
            while (gfx.TryGetFrame(out _))
            {
                frames++;
                if (frames % SnapshotEvery == 0)
                {
                    WritePng(Path.Combine(outDir, $"frame-{frames:D6}.png"), gfx.Output, gfx.OutputWidth, gfx.OutputHeight, alphaMap: false);
                }
            }
            while (gfx.ReadResponse() is not null) { }
        }

        WritePng(Path.Combine(outDir, "final.png"), gfx.Output, gfx.OutputWidth, gfx.OutputHeight, alphaMap: false);
        WritePng(Path.Combine(outDir, "final-alpha.png"), gfx.Output, gfx.OutputWidth, gfx.OutputHeight, alphaMap: true);
        int translucent = 0;
        for (int i = 3; i < gfx.Output.Length; i += 4) { if (gfx.Output[i] != 255) { translucent++; } }
        trace.Line($"== {messages} messages, {frames} frames, output {gfx.OutputWidth}x{gfx.OutputHeight}, pixels with alpha<255: {translucent}");
        File.WriteAllLines(Path.Combine(outDir, "trace.txt"), trace.Lines);
        File.WriteAllText(Path.Combine(outDir, "last-writer.txt"), trace.Grid(gfx.OutputWidth, gfx.OutputHeight));
        Console.WriteLine($"Replayed {messages} messages / {frames} frames into {outDir}");
    }

    /// <summary>Turns decoded commands into trace lines and an output-space "last writer" grid.</summary>
    private sealed class Tracer
    {
        internal List<string> Lines { get; } = [];
        private readonly Dictionary<ushort, (int X, int Y)> _origin = [];
        private readonly Dictionary<ushort, (int W, int H)> _cacheSize = [];
        private readonly Dictionary<(int X, int Y), int> _writer = [];
        private uint _frame;

        internal void Line(string text) => Lines.Add(text);

        internal void Observe(ushort command, byte[] body)
        {
            var r = new BodyReader(body);
            int line = Lines.Count + 1;
            switch (command)
            {
                case 1:
                    {
                        ushort s = r.U16(), codec = r.U16(); _ = r.U8();
                        (int l, int t, int rr, int b) = r.Rect();
                        Line($"[{_frame}] WireToSurface1 surface={s} codec=0x{codec:X4} rect={l},{t}-{rr},{b} bytes={body.Length}");
                        Mark(s, l, t, rr, b, line);
                        break;
                    }
                case 2:
                    {
                        ushort s = r.U16(), codec = r.U16(); uint context = r.U32(); _ = r.U8(); uint length = r.U32();
                        List<(int, int, int, int)> rects = ProgressiveRects(r.Rest((int)length));
                        Line($"[{_frame}] WireToSurface2 surface={s} codec=0x{codec:X4} context={context} rects={string.Join(" ", rects.Select(x => $"{x.Item1},{x.Item2}+{x.Item3}x{x.Item4}"))}");
                        foreach ((int x, int y, int w, int h) in rects) { Mark(s, x, y, x + w, y + h, line); }
                        break;
                    }
                case 3: Line($"[{_frame}] DeleteEncodingContext surface={r.U16()} context={r.U32()}"); break;
                case 4:
                    {
                        ushort s = r.U16(); uint color = r.U32(); int count = r.U16();
                        var rects = new List<string>();
                        for (int i = 0; i < count; i++) { (int l, int t, int rr, int b) = r.Rect(); rects.Add($"{l},{t}-{rr},{b}"); Mark(s, l, t, rr, b, line); }
                        Line($"[{_frame}] SolidFill surface={s} color=0x{color:X8} rects={string.Join(" ", rects)}");
                        break;
                    }
                case 5:
                    {
                        ushort src = r.U16(), dst = r.U16(); (int l, int t, int rr, int b) = r.Rect(); int count = r.U16();
                        var points = new List<string>();
                        for (int i = 0; i < count; i++)
                        {
                            int x = r.I16(), y = r.I16(); points.Add($"{x},{y}");
                            Mark(dst, x, y, x + rr - l, y + b - t, line);
                        }
                        Line($"[{_frame}] SurfaceToSurface {src}->{dst} src={l},{t}-{rr},{b} to {string.Join(" ", points)}");
                        break;
                    }
                case 6:
                    {
                        ushort s = r.U16(); ulong key = r.U64(); ushort slot = r.U16(); (int l, int t, int rr, int b) = r.Rect();
                        _cacheSize[slot] = (rr - l, b - t);
                        Line($"[{_frame}] SurfaceToCache surface={s} slot={slot} key={key:X16} rect={l},{t}-{rr},{b}");
                        break;
                    }
                case 7:
                    {
                        ushort slot = r.U16(), s = r.U16(); int count = r.U16();
                        (int w, int h) = _cacheSize.TryGetValue(slot, out var size) ? size : (0, 0);
                        var points = new List<string>();
                        for (int i = 0; i < count; i++) { int x = r.I16(), y = r.I16(); points.Add($"{x},{y}"); Mark(s, x, y, x + w, y + h, line); }
                        Line($"[{_frame}] CacheToSurface slot={slot} ({w}x{h}{(w == 0 ? ", size unknown" : "")}) surface={s} at {string.Join(" ", points)}");
                        break;
                    }
                case 8: Line($"[{_frame}] EvictCache slot={r.U16()}"); break;
                case 9: Line($"[{_frame}] CreateSurface id={r.U16()} {r.U16()}x{r.U16()} format=0x{r.U8():X2}"); break;
                case 10: { ushort s = r.U16(); _origin.Remove(s); Line($"[{_frame}] DeleteSurface id={s}"); break; }
                case 11: { _ = r.U32(); _frame = r.U32(); Line($"[{_frame}] StartFrame"); break; }
                case 12: Line($"[{_frame}] EndFrame"); break;
                case 14: Line($"[{_frame}] ResetGraphics {r.U32()}x{r.U32()}"); break;
                case 15:
                case 23:
                    {
                        ushort s = r.U16(); _ = r.U16(); int x = (int)r.U32(), y = (int)r.U32();
                        _origin[s] = (x, y);
                        Line($"[{_frame}] MapSurfaceToOutput{(command == 23 ? "(scaled)" : "")} surface={s} at {x},{y}");
                        break;
                    }
                default: Line($"[{_frame}] command 0x{command:X4} ({body.Length} B)"); break;
            }
        }

        private void Mark(ushort surface, int left, int top, int right, int bottom, int line)
        {
            if (!_origin.TryGetValue(surface, out (int X, int Y) o)) { return; }
            for (int y = (top + o.Y) / Cell; y <= (bottom + o.Y - 1) / Cell; y++)
            {
                for (int x = (left + o.X) / Cell; x <= (right + o.X - 1) / Cell; x++) { _writer[(x, y)] = line; }
            }
        }

        internal string Grid(int width, int height)
        {
            var text = new StringBuilder($"# last trace line that painted each {Cell}x{Cell} output cell (row = y/{Cell}, column = x/{Cell}); 0 = never painted\n");
            for (int y = 0; y < (height + Cell - 1) / Cell; y++)
            {
                text.Append(y.ToString("D3")).Append(':');
                for (int x = 0; x < (width + Cell - 1) / Cell; x++)
                {
                    text.Append(' ').Append(_writer.TryGetValue((x, y), out int line) ? line : 0);
                }
                text.Append('\n');
            }
            return text.ToString();
        }

        /// <summary>RFX_PROGRESSIVE_REGION rectangles (MS-RDPEGFX 2.2.4.2.1.5) inside a WIRE_TO_SURFACE_2 bitmap stream.</summary>
        private static List<(int, int, int, int)> ProgressiveRects(ReadOnlySpan<byte> stream)
        {
            var rects = new List<(int, int, int, int)>();
            while (stream.Length >= 6)
            {
                ushort type = BinaryPrimitives.ReadUInt16LittleEndian(stream);
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream[2..]);
                if (length < 6 || length > stream.Length) { break; }
                if (type == 0xCCC4 && length >= 18)
                {
                    int count = BinaryPrimitives.ReadUInt16LittleEndian(stream[7..]);
                    for (int i = 0; i < count && 18 + i * 8 + 8 <= length; i++)
                    {
                        ReadOnlySpan<byte> rect = stream[(18 + i * 8)..];
                        rects.Add((BinaryPrimitives.ReadUInt16LittleEndian(rect), BinaryPrimitives.ReadUInt16LittleEndian(rect[2..]),
                            BinaryPrimitives.ReadUInt16LittleEndian(rect[4..]), BinaryPrimitives.ReadUInt16LittleEndian(rect[6..])));
                    }
                }
                stream = stream[length..];
            }
            return rects;
        }
    }

    private ref struct BodyReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        internal byte U8() => _data[_position++];
        internal ushort U16() { ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]); _position += 2; return v; }
        internal short I16() { short v = BinaryPrimitives.ReadInt16LittleEndian(_data[_position..]); _position += 2; return v; }
        internal uint U32() { uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]); _position += 4; return v; }
        internal ulong U64() { ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_position..]); _position += 8; return v; }
        internal (int, int, int, int) Rect() => (U16(), U16(), U16(), U16());
        internal ReadOnlySpan<byte> Rest(int length) { ReadOnlySpan<byte> v = _data.Slice(_position, Math.Min(length, _data.Length - _position)); _position += v.Length; return v; }
    }

    /// <summary>Minimal PNG writer (RGB, or grey alpha map where opaque pixels are black).</summary>
    private static void WritePng(string path, byte[] bgra, int width, int height, bool alphaMap)
    {
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                if (alphaMap) { byte a = (byte)(255 - bgra[i + 3]); raw.WriteByte(a); raw.WriteByte(a); raw.WriteByte(a); }
                else { raw.WriteByte(bgra[i + 2]); raw.WriteByte(bgra[i + 1]); raw.WriteByte(bgra[i]); }
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) { raw.Position = 0; raw.CopyTo(z); }

        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;   // 8-bit RGB
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        file.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        file.Write(typeBytes);
        file.Write(data);
        uint crc = Crc32([.. typeBytes, .. data]);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        file.Write(crcBytes);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) { crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        }
        return ~crc;
    }
}
