using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// The dynamic virtual channel multiplexer (MS-RDPEDYC) carried over the static "drdynvc" channel.
/// The managed client opens exactly one dynamic channel — the Display Control Channel
/// (MS-RDPEDISP, "Microsoft::Windows::RDS::DisplayControl") — and declines every other channel the
/// server tries to create (audio, graphics pipeline, …) so the server keeps to the classic bitmap
/// path the renderer understands. Display Control lets the client ask the server to resize the
/// remote desktop to match the workspace tab, the way mstsc and Remmina do.
/// </summary>
internal sealed class DrdynvcChannel
{
    private const string DisplayControlName = "Microsoft::Windows::RDS::DisplayControl";
    private const int CmdCreate = 0x01, CmdDataFirst = 0x02, CmdData = 0x03, CmdClose = 0x04, CmdCapabilities = 0x05;
    private const uint DispCaps = 0x00000005, DispMonitorLayout = 0x00000002;
    private const uint MonitorPrimary = 0x00000001;

    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly CancellationToken _ct;
    private readonly Lock _lock = new();

    private bool _displayOpen;      // the server created the Display Control channel
    private uint _displayChannelId;
    private bool _displayReady;     // its capabilities arrived; monitor layouts may now be sent
    private uint _maxMonitors = 1;
    private (int Width, int Height)? _pending;  // requested before the channel was ready
    private (int Width, int Height)? _lastSent; // suppress redundant layouts during a resize drag

    // Reassembly of a fragmented dynamic-channel message (DATA_FIRST + DATA).
    private uint _reassembleChannel;
    private int _reassembleTotal;
    private MemoryStream? _reassemble;

    internal DrdynvcChannel(Func<byte[], CancellationToken, Task> send, CancellationToken ct)
    {
        _send = send;
        _ct = ct;
    }

    /// <summary>Handles one drdynvc PDU delivered over the static channel.</summary>
    internal async Task ProcessAsync(byte[] pdu)
    {
        byte[]? reply = Parse(pdu);
        if (reply is not null)
        {
            await _send(reply, _ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the server to make the remote desktop <paramref name="width"/>×<paramref name="height"/>.
    /// Buffered until the Display Control channel reports its capabilities; a no-op if the server
    /// never opens the channel (the desktop then stays at the negotiated size, scaled to fit).
    /// </summary>
    internal async Task RequestResizeAsync(int width, int height)
    {
        width = Clamp(width);
        height = Clamp(height);
        byte[]? frame;
        lock (_lock)
        {
            if (!_displayReady) { _pending = (width, height); return; }
            if (_lastSent is { } last && last.Width == width && last.Height == height)
            {
                return;
            }

            _lastSent = (width, height);
            frame = BuildMonitorLayout(_displayChannelId, width, height);
        }
        await _send(frame, _ct).ConfigureAwait(false);
    }

    /// <summary>Parses one PDU and returns the bytes to send back, or null when nothing is owed.</summary>
    private byte[]? Parse(byte[] pdu)
    {
        if (pdu.Length == 0)
        {
            return null;
        }

        var r = new ByteReader(pdu);
        byte head = r.U8();
        int cmd = (head >> 4) & 0x0F;
        int sp = (head >> 2) & 0x03;
        int cbId = head & 0x03;
        return cmd switch
        {
            CmdCapabilities => BuildCapabilitiesResponse(ref r),
            CmdCreate => HandleCreate(ref r, cbId),
            CmdDataFirst => HandleData(ref r, cbId, sp, first: true),
            CmdData => HandleData(ref r, cbId, sp, first: false),
            CmdClose => HandleClose(ref r, cbId),
            _ => null
        };
    }

    private static byte[] BuildCapabilitiesResponse(ref ByteReader r)
    {
        _ = r.U8();                                 // pad
        ushort version = r.Remaining >= 2 ? r.U16LE() : (ushort)1;
        if (version is < 1 or > 3)
        {
            version = 3;
        }
        // [Cmd=CAPABILITIES|Sp=0|cbId=0][pad][Version].
        var w = new ByteWriter(4);
        w.U8(CmdCapabilities << 4);
        w.U8(0);
        w.U16LE(version);
        return w.ToArray();
    }

    private byte[]? HandleCreate(ref ByteReader r, int cbId)
    {
        if (r.Remaining < CbIdBytes(cbId))
        {
            return null;
        }

        uint channelId = ReadVariable(ref r, cbId);
        string name = ReadAsciiZ(ref r);
        bool accept = string.Equals(name, DisplayControlName, StringComparison.Ordinal);
        lock (_lock)
        {
            if (accept && !_displayOpen) { _displayOpen = true; _displayChannelId = channelId; }
            else if (accept)
            {
                accept = channelId == _displayChannelId; // tolerate a duplicate create
            }
        }
        // Echo the channel id at the same width; CreationStatus 0 accepts, non-zero declines.
        var w = new ByteWriter(8);
        w.U8((byte)((CmdCreate << 4) | cbId));
        WriteVariable(w, channelId, cbId);
        w.U32LE(accept ? 0u : unchecked(0x80004001)); // E_NOTIMPL for unsupported channels
        return w.ToArray();
    }

    private byte[]? HandleData(ref ByteReader r, int cbId, int sp, bool first)
    {
        if (r.Remaining < CbIdBytes(cbId))
        {
            return null;
        }

        uint channelId = ReadVariable(ref r, cbId);
        int total = -1;
        if (first)
        {
            if (r.Remaining < CbIdBytes(sp))
            {
                return null;
            }

            total = (int)ReadVariable(ref r, sp);
        }
        ReadOnlySpan<byte> data = r.Rest();

        byte[] message;
        if (first)
        {
            _reassemble = null;
            if (total > data.Length)
            {
                _reassembleChannel = channelId;
                _reassembleTotal = total;
                _reassemble = new MemoryStream(total);
                _reassemble.Write(data);
                return null;
            }
            message = data.ToArray();
        }
        else if (_reassemble is { } buffer && channelId == _reassembleChannel)
        {
            if (buffer.Length + data.Length > _reassembleTotal) { _reassemble = null; return null; }
            buffer.Write(data);
            if (buffer.Length < _reassembleTotal)
            {
                return null;
            }

            message = buffer.ToArray();
            _reassemble = null;
        }
        else
        {
            message = data.ToArray();
        }

        bool isDisplay;
        lock (_lock)
        {
            isDisplay = _displayOpen && channelId == _displayChannelId;
        }

        return isDisplay ? OnDisplayControl(message) : null;
    }

    private byte[]? HandleClose(ref ByteReader r, int cbId)
    {
        if (r.Remaining < CbIdBytes(cbId))
        {
            return null;
        }

        uint channelId = ReadVariable(ref r, cbId);
        lock (_lock)
        {
            if (_displayOpen && channelId == _displayChannelId)
            {
                _displayOpen = false;
                _displayReady = false;
                _lastSent = null;
            }
        }
        return null;
    }

    /// <summary>Processes a Display Control PDU; returns a buffered monitor layout once capabilities arrive.</summary>
    private byte[]? OnDisplayControl(byte[] message)
    {
        var r = new ByteReader(message);
        if (r.Remaining < 8)
        {
            return null;
        }

        uint type = r.U32LE();
        _ = r.U32LE();                              // Length
        if (type != DispCaps || r.Remaining < 4)
        {
            return null;
        }

        uint maxMonitors = r.U32LE();
        lock (_lock)
        {
            _maxMonitors = maxMonitors == 0 ? 1 : maxMonitors;
            _displayReady = true;
            if (_pending is not { } p)
            {
                return null;
            }

            _pending = null;
            _lastSent = p;
            return BuildMonitorLayout(_displayChannelId, p.Width, p.Height);
        }
    }

    private static byte[] BuildMonitorLayout(uint channelId, int width, int height)
    {
        // DISPLAYCONTROL_MONITOR_LAYOUT_PDU (MS-RDPEDISP 2.2.2.2) for a single primary monitor.
        var disp = new ByteWriter(56);
        disp.U32LE(DispMonitorLayout);          // Header.Type
        disp.U32LE(56);                         // Header.Length = 8 + 8 + 40
        disp.U32LE(40);                         // MonitorLayoutSize
        disp.U32LE(1);                          // NumMonitors
        disp.U32LE(MonitorPrimary);             // Flags
        disp.U32LE(0);                          // Left
        disp.U32LE(0);                          // Top
        disp.U32LE((uint)width);                // Width
        disp.U32LE((uint)height);               // Height
        disp.U32LE(0);                          // PhysicalWidth
        disp.U32LE(0);                          // PhysicalHeight
        disp.U32LE(0);                          // Orientation = landscape
        disp.U32LE(100);                        // DesktopScaleFactor
        disp.U32LE(100);                        // DeviceScaleFactor

        int cbId = channelId <= 0xFF ? 0 : channelId <= 0xFFFF ? 1 : 2;
        var w = new ByteWriter(4 + CbIdBytes(cbId) + disp.Length);
        w.U8((byte)((CmdData << 4) | cbId));
        WriteVariable(w, channelId, cbId);
        w.Bytes(disp.Span);
        return w.ToArray();
    }

    private static int Clamp(int value) => value < 200 ? 200 : value > 8192 ? 8192 : value & ~1; // even, 200..8192

    private static int CbIdBytes(int cb) => cb switch { 0 => 1, 1 => 2, _ => 4 };

    private static uint ReadVariable(ref ByteReader r, int cb) => cb switch
    {
        0 => r.U8(),
        1 => r.U16LE(),
        _ => r.U32LE()
    };

    private static void WriteVariable(ByteWriter w, uint value, int cb)
    {
        switch (cb)
        {
            case 0: w.U8((byte)value); break;
            case 1: w.U16LE((ushort)value); break;
            default: w.U32LE(value); break;
        }
    }

    private static string ReadAsciiZ(ref ByteReader r)
    {
        ReadOnlySpan<byte> rest = r.Rest();
        int end = rest.IndexOf((byte)0);
        if (end < 0)
        {
            end = rest.Length;
        }

        return Encoding.ASCII.GetString(rest[..end]);
    }
}
