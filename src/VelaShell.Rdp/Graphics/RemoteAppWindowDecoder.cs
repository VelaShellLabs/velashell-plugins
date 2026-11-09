using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Graphics;

/// <summary>MS-RDPERP 2.2.1.3 window orders. Owns immutable snapshots; never allocates server-sized surfaces.</summary>
internal sealed class RemoteAppWindowDecoder
{
    internal const int MaxWindows = 256;
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, RemoteAppWindow> _windows = [];
    private List<uint> _zOrder = [];
    private HashSet<uint>? _synchronizing;
    internal event Action? Changed;
    internal uint ActiveWindow { get; private set; }
    internal IReadOnlyList<RemoteAppWindow> Snapshot()
    {
        lock (_gate)
        {
            return _zOrder.Where(_windows.ContainsKey).Select(id => _windows[id])
                .Concat(_windows.Values.Where(w => !_zOrder.Contains(w.Id))).ToArray();
        }
    }
    internal bool Contains(uint id) { lock (_gate) { return _windows.ContainsKey(id); } }
    internal void Clear()
    {
        lock (_gate) { _windows.Clear(); _zOrder.Clear(); _synchronizing = null; ActiveWindow = 0; }
        Changed?.Invoke();
    }

    internal void Process(ReadOnlySpan<byte> payload)
    {
        var r = new ByteReader(payload);
        uint flags = r.U32LE();
        lock (_gate)
        {
            switch (flags & 0x07000000)
            {
                case 0x01000000: ReadWindow(ref r, flags); break;
                case 0x04000000: ReadDesktop(ref r, flags); break;
                case 0x02000000:
                    // Notification icons are shell integration, not application pixels. They are bounded by
                    // the outer order length and deliberately not surfaced in the host's system tray.
                    r.Skip(r.Remaining);
                    break;
                default: throw new RdpProtocolException("Unknown RAIL window order class.");
            }
            if (r.Remaining != 0) { throw new RdpProtocolException("Trailing bytes in RAIL window order."); }
        }
        Changed?.Invoke();
    }

    private void ReadWindow(ref ByteReader r, uint flags)
    {
        uint id = r.U32LE();
        if ((flags & 0x20000000) != 0)
        {
            _windows.Remove(id); _zOrder.Remove(id); _synchronizing?.Remove(id);
            if (ActiveWindow == id) { ActiveWindow = 0; }
            return;
        }
        if ((flags & 0x40000000) != 0) { ReadIcon(ref r); return; }
        if ((flags & 0x80000000) != 0) { r.Skip(3); return; }
        bool isNew = (flags & 0x10000000) != 0;
        if (!_windows.TryGetValue(id, out RemoteAppWindow? window))
        {
            if (!isNew) { throw new RdpProtocolException("RAIL updated an unknown window."); }
            if (_windows.Count >= MaxWindows) { throw new RdpProtocolException("Too many RemoteApp windows."); }
            window = new RemoteAppWindow(id);
        }
        const uint known = 0x11000000 | 0x00000002 | 0x00000008 | 0x00000010 | 0x00000004 |
            0x00004000 | 0x00010000 | 0x00000080 | 0x08000000 | 0x00020000 | 0x00040000 |
            0x00000800 | 0x00008000 | 0x00000400 | 0x00000100 | 0x00001000 | 0x00000200 |
            0x00400000 | 0x00200000 | 0x00800000 | 0x00080000 | 0x00000040 | 0x00000001;
        if ((flags & ~known) != 0) { throw new RdpProtocolException("Unsupported RemoteApp window fields."); }
        if (Has(2)) { window = window with { OwnerId = r.U32LE() }; }
        if (Has(8)) { window = window with { Style = r.U32LE(), ExtendedStyle = r.U32LE() }; }
        if (Has(0x10))
        {
            byte show = r.U8();
            if (show is not (0 or 2 or 3 or 5)) { throw new RdpProtocolException("Invalid RemoteApp show state."); }
            window = window with { ShowState = show };
        }
        if (Has(4)) { window = window with { Title = ReadString(ref r) }; }
        if (Has(0x4000)) { window = window with { ClientX = Coordinate(ref r), ClientY = Coordinate(ref r) }; }
        if (Has(0x10000)) { window = window with { ClientWidth = Dimension(ref r), ClientHeight = Dimension(ref r) }; }
        if (Has(0x80)) { _ = Dimension(ref r); _ = Dimension(ref r); }
        if (Has(0x08000000)) { _ = Dimension(ref r); _ = Dimension(ref r); }
        if (Has(0x20000) && r.U8() != 0) { throw new RdpProtocolException("RemoteApp render plug-ins are not supported."); }
        if (Has(0x40000)) { _ = r.U32LE(); }
        if (Has(0x800)) { window = window with { X = Coordinate(ref r), Y = Coordinate(ref r) }; }
        if (Has(0x8000)) { window = window with { ClientDeltaX = Coordinate(ref r), ClientDeltaY = Coordinate(ref r) }; }
        if (Has(0x400)) { window = window with { Width = Dimension(ref r), Height = Dimension(ref r) }; }
        if ((long)window.Width * window.Height > Pdu.Capabilities.MaxDesktopPixels) { throw new RdpProtocolException("RemoteApp window exceeds the pixel budget."); }
        if (Has(0x100)) { window = window with { WindowRects = ReadRects(ref r) }; }
        if (Has(0x1000)) { window = window with { VisibleX = Coordinate(ref r), VisibleY = Coordinate(ref r) }; }
        if (Has(0x200)) { window = window with { VisibilityRects = ReadRects(ref r) }; }
        if (Has(0x400000)) { _ = ReadString(ref r); }
        if (Has(0x800000)) { _ = r.U8(); }
        if (Has(0x80000)) { window = window with { IsCloaked = r.U8() != 0 }; }
        if (Has(0x40)) { _ = r.U8(); }
        if (Has(1)) { _ = r.U8(); }
        if ((long)Math.Max(1, window.WindowRects.Count) * window.VisibilityRects.Count > 4096 ||
            _windows.Values.Where(w => w.Id != id).Sum(w => w.WindowRects.Count + w.VisibilityRects.Count)
                + window.WindowRects.Count + window.VisibilityRects.Count > 16384)
        {
            throw new RdpProtocolException("RemoteApp regions exceed the rendering complexity budget.");
        }
        _windows[id] = window;
        if (!_zOrder.Contains(id)) { _zOrder.Insert(0, id); }
        _synchronizing?.Add(id);
        bool Has(uint mask) => (flags & mask) != 0;
    }

    private void ReadDesktop(ref ByteReader r, uint flags)
    {
        if ((flags & ~0x0400003f) != 0) { throw new RdpProtocolException("Unsupported RemoteApp desktop fields."); }
        if ((flags & 1) != 0) { _windows.Clear(); _zOrder.Clear(); _synchronizing = null; ActiveWindow = 0; return; }
        if ((flags & 8) != 0) { _windows.Clear(); _zOrder.Clear(); ActiveWindow = 0; _synchronizing = []; }
        if ((flags & 0x20) != 0) { ActiveWindow = r.U32LE(); }
        if ((flags & 0x10) != 0)
        {
            int count = r.U8();
            var order = new List<uint>(count);
            for (int i = 0; i < count; i++)
            {
                uint id = r.U32LE();
                if (order.Contains(id)) { throw new RdpProtocolException("Duplicate RemoteApp z-order entry."); }
                order.Add(id);
            }
            _zOrder = order;
        }
        if ((flags & 4) != 0 && _synchronizing is { } seen)
        {
            foreach (uint id in _windows.Keys.Where(id => !seen.Contains(id)).ToArray()) { _windows.Remove(id); _zOrder.Remove(id); }
            _synchronizing = null;
        }
    }

    private static IReadOnlyList<RdpFrameRegion> ReadRects(ref ByteReader r)
    {
        int count = r.U16LE();
        if (count > 2048) { throw new RdpProtocolException("Too many RemoteApp region rectangles."); }
        var result = new RdpFrameRegion[count];
        for (int i = 0; i < count; i++)
        {
            int x = r.U16LE(), y = r.U16LE(), right = r.U16LE(), bottom = r.U16LE();
            if (right < x || bottom < y) { throw new RdpProtocolException("Invalid RemoteApp region."); }
            result[i] = new(x, y, right - x, bottom - y);
        }
        return Array.AsReadOnly(result);
    }
    private static int Coordinate(ref ByteReader r)
    {
        int value = unchecked((int)r.U32LE());
        if (value is < -32768 or > 32767) { throw new RdpProtocolException("RemoteApp coordinate is out of range."); }
        return value;
    }
    private static int Dimension(ref ByteReader r)
    {
        uint value = r.U32LE();
        if (value > 8192) { throw new RdpProtocolException("RemoteApp dimension is out of range."); }
        return (int)value;
    }
    private static string ReadString(ref ByteReader r)
    {
        int size = r.U16LE();
        if (size > 520 || (size & 1) != 0) { throw new RdpProtocolException("Invalid RemoteApp title length."); }
        return Encoding.Unicode.GetString(r.Take(size)).TrimEnd('\0');
    }
    private static void ReadIcon(ref ByteReader r)
    {
        r.Skip(3);
        int bpp = r.U8();
        int width = r.U16LE(), height = r.U16LE();
        if (bpp is not (1 or 4 or 8 or 16 or 24 or 32) || width > 96 || height > 96) { throw new RdpProtocolException("Invalid RemoteApp icon."); }
        int colors = bpp <= 8 ? r.U16LE() : 0;
        int mask = r.U16LE(), pixels = r.U16LE();
        r.Skip(mask + colors + pixels);
    }
}
