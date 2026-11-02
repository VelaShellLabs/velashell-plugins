using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// The dynamic virtual channel multiplexer (MS-RDPEDYC) carried over the static "drdynvc" channel.
/// Display Control is always available; explicitly selected devices register additional listeners
/// before the server sends CREATE. Unknown names remain disabled. Instances have independent,
/// bounded reassembly and may share a listener name.
/// </summary>
internal sealed partial class DrdynvcChannel
{
    private const string DisplayControlName = "Microsoft::Windows::RDS::DisplayControl";
    private const int CmdCreate = 0x01, CmdDataFirst = 0x02, CmdData = 0x03, CmdClose = 0x04, CmdCapabilities = 0x05;
    private const uint DispCaps = 0x00000005, DispMonitorLayout = 0x00000002;
    private const uint MonitorPrimary = 0x00000001;

    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly CancellationToken _ct;
    private readonly bool _displayEnabled;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();

    private bool _displayOpen;      // the server created the Display Control channel
    private uint _displayChannelId;
    private bool _displayReady;     // its capabilities arrived; monitor layouts may now be sent
    private uint _maxMonitors = 1;
    private (int Width, int Height, int Scale)? _pending;  // requested before the channel was ready
    private (int Width, int Height, int Scale)? _lastSent; // suppress redundant layouts during a resize drag

    private sealed record Listener(Func<DynamicChannel, IDynamicChannelHandler> Factory, int Limit);
    private sealed class Instance(DynamicChannel channel, IDynamicChannelHandler? handler, int limit)
    {
        internal readonly DynamicChannel Channel = channel;
        internal readonly IDynamicChannelHandler? Handler = handler;
        internal readonly int Limit = limit;
        internal MemoryStream? Buffer;
        internal int Total;
    }
    private readonly Dictionary<string, Listener> _listeners = [with(StringComparer.Ordinal)];
    private readonly Dictionary<uint, Instance> _instances = [];
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private const int MaxDisplayMessage = 64 * 1024;
    private const int MaxBufferedBytes = 32 * 1024 * 1024;
    private int _reservedBytes;

    internal void RegisterListener(string name, Func<DynamicChannel, IDynamicChannelHandler> factory, int maxMessageBytes = MaxDisplayMessage)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.Any(c => c is '\0' or > '\x7f'))
        {
            throw new ArgumentException("A dynamic channel name must be NUL-free ASCII and at most 255 characters.", nameof(name));
        }

        if (maxMessageBytes is < 1 or > MaxBufferedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        }

        lock (_lock)
        {
            _listeners.Add(name, new(factory, maxMessageBytes));
        }
    }

    internal async Task SendAsync(DynamicChannel channel, byte[] message, CancellationToken ct)
    {
        uint id = channel.Id;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ct);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (!_instances.TryGetValue(id, out Instance? instance) || !ReferenceEquals(instance.Channel, channel)
                    || message.Length > instance.Limit)
                {
                    throw new InvalidOperationException("Dynamic channel closed or message too large.");
                }
            }

            int cb = id <= 255 ? 0 : id <= 65535 ? 1 : 2;
            bool lossy = _routes.GetValueOrDefault(id) == 3;
            int fragment = lossy ? 600 : 1500;
            if (lossy && message.Length > fragment)
            { throw new InvalidOperationException("Fragmented outbound DVC messages require a reliable transport."); }
            int offset = 0;
            do
            {
                bool first = offset == 0 && message.Length > fragment;
                int count = Math.Min(fragment, message.Length - offset);
                var w = new ByteWriter(count + 12);
                w.U8((byte)(((first ? CmdDataFirst : CmdData) << 4) | (first ? 8 : 0) | cb));
                WriteVariable(w, id, cb);
                if (first)
                {
                    w.U32LE((uint)message.Length);
                }

                w.Bytes(message.AsSpan(offset, count));
                // Once the first fragment is sent, finish this message; cancelling halfway would
                // make the next response look like a continuation of a truncated message.
                await _send(w.ToArray(), _ct).ConfigureAwait(false);
                offset += count;
            } while (offset < message.Length);
        }
        finally { _ = _sendGate.Release(); }
    }

    internal async Task CloseChannelAsync(DynamicChannel channel)
    {
        Instance? removed = null;
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            uint id = channel.Id;
            lock (_lock)
            {
                if (!_instances.TryGetValue(id, out Instance? instance) || !ReferenceEquals(instance.Channel, channel))
                {
                    return;
                }

                _ = _instances.Remove(id);
                removed = instance;
                ClearBuffer(instance);
                if (id == _displayChannelId) { _displayOpen = _displayReady = false; _lastSent = null; }
            }
            int cb = id <= 255 ? 0 : id <= 65535 ? 1 : 2;
            var w = new ByteWriter(5); w.U8((byte)(0x40 | cb)); WriteVariable(w, id, cb);
            try { await _send(w.ToArray(), _ct).ConfigureAwait(false); }
            finally { _routes.Remove(id); }
        }
        finally
        {
            _ = _sendGate.Release();
            if (removed?.Handler is { } handler)
            {
                await handler.OnCloseAsync().ConfigureAwait(false);
            }
        }
    }

    internal async Task CloseAsync()
    {
        Instance[] instances;
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                instances = [.. _instances.Values];
                _instances.Clear();
                _routes.Clear();
                _reservedBytes = 0;
                _displayReady = _displayOpen = false;
            }
        }
        finally { _ = _sendGate.Release(); }
        List<Exception> failures = [];
        foreach (Instance instance in instances)
        {
            instance.Buffer?.Dispose();
            try
            {
                if (instance.Handler is { } handler)
                {
                    await handler.OnCloseAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("Dynamic channel cleanup failed.", failures);
        }
    }

    internal DrdynvcChannel(Func<byte[], CancellationToken, Task> send, CancellationToken ct, bool displayControl = true,
        Action<string>? log = null, Func<int, Task>? waitForTunnel = null,
        Action<int>? enableTunnel = null, Func<int, byte[], CancellationToken, Task>? sendTunnel = null)
    {
        _tcpSend = send;
        _send = SendRoutedAsync;
        _waitForTunnel = waitForTunnel;
        _enableTunnel = enableTunnel;
        _sendTunnel = sendTunnel;
        _ct = ct;
        _displayEnabled = displayControl;
        _log = log;
    }

    // A device handler failing (a send racing its own close, a driver error during cleanup, …) costs that
    // channel only: it is closed and logged, and the desktop session and the other channels carry on.
    // Cancellation of the whole multiplexer still propagates.
    private bool IsShutdown(Exception ex) => ex is OperationCanceledException && _ct.IsCancellationRequested;

    private async Task RunHandlerAsync(Instance instance, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); }
        catch (Exception ex) when (!IsShutdown(ex))
        {
            Log($"dynamic channel {instance.Channel.Id} handler failed ({ex.GetType().Name}); closing that channel");
            await CloseFailedAsync(instance).ConfigureAwait(false);
        }
    }

    private async Task CloseFailedAsync(Instance instance)
    {
        try { await CloseChannelAsync(instance.Channel).ConfigureAwait(false); }
        catch (Exception ex) when (!IsShutdown(ex)) { Log($"dynamic channel {instance.Channel.Id} close failed ({ex.GetType().Name})"); }
    }

    private async Task RunCloseHandlerAsync(Instance instance, IDynamicChannelHandler handler)
    {
        try { await handler.OnCloseAsync().ConfigureAwait(false); }
        catch (Exception ex) when (!IsShutdown(ex)) { Log($"dynamic channel {instance.Channel.Id} close handler failed ({ex.GetType().Name})"); }
    }

    /// <summary>
    /// A fragment that breaks the declared framing discards the message being reassembled. A device channel
    /// is then closed; the built-in Display Control channel stays open so resizing keeps working.
    /// </summary>
    private (byte[]? Reply, Func<Task>? Work) Reject(Instance instance)
    {
        ClearBuffer(instance);
        return instance.Handler is null ? default : (null, () => CloseFailedAsync(instance));
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); }
        catch { /* Diagnostics must not interrupt the channel worker. */ }
    }

    /// <summary>Handles one drdynvc PDU delivered over the static channel.</summary>
    internal async Task ProcessAsync(byte[] pdu, int tunnelType = 0)
    {
        Func<Task>? work;
        await _sendGate.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            if (pdu.Length > 0 && pdu[0] >> 4 == 8)
            {
                if (tunnelType != 0) { throw new RdpProtocolException("Soft-Sync must use TCP."); }
                await SoftSyncAsync(pdu).ConfigureAwait(false);
                return;
            }
            ValidateTransport(pdu, tunnelType);
            (byte[]? reply, Func<Task>? pending) = Parse(pdu);
            work = pending;
            if (reply is not null)
            {
                await _send(reply, _ct).ConfigureAwait(false);
            }
            if (PduChannel(pdu) is { } id)
            {
                lock (_lock) { if (!_instances.ContainsKey(id)) { _routes.Remove(id); } }
            }
        }
        finally { _ = _sendGate.Release(); }
        if (work is not null)
        {
            await work().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the server to make the remote desktop <paramref name="width"/>×<paramref name="height"/>.
    /// Buffered until the Display Control channel reports its capabilities; a no-op if the server
    /// never opens the channel (the desktop then stays at the negotiated size, scaled to fit).
    /// </summary>
    internal async Task RequestResizeAsync(int width, int height, int scale = 100)
    {
        if (!_displayEnabled)
        {
            return;
        }

        width = Clamp(width) & ~1;   // MS-RDPEDISP: the width must be even; the height need not be
        height = Clamp(height);
        scale = RdpScale.Clamp(scale);
        await _sendGate.WaitAsync(_ct).ConfigureAwait(false);
        try
        {
            byte[] frame;
            lock (_lock)
            {
                if (!_displayReady) { _pending = (width, height, scale); return; }
                if (_lastSent is { } last && last.Width == width && last.Height == height && last.Scale == scale)
                {
                    return;
                }

                _lastSent = (width, height, scale);
                frame = BuildMonitorLayout(_displayChannelId, width, height, scale);
            }
            await _send(frame, _ct).ConfigureAwait(false);
        }
        finally { _ = _sendGate.Release(); }
    }

    private (byte[]? Reply, Func<Task>? Work) Parse(byte[] pdu)
    {
        if (pdu.Length == 0)
        {
            return default;
        }

        var r = new ByteReader(pdu);
        byte head = r.U8();
        int cmd = head >> 4, sp = (head >> 2) & 3, cb = head & 3;
        if (cmd == CmdCapabilities)
        {
            if (r.Remaining < 3)
            {
                return default;
            }

            _ = r.U8();
            ushort version = r.U16LE();
            var caps = new ByteWriter(4);
            caps.U8(0x50); caps.U8(0); caps.U16LE((ushort)Math.Clamp((int)version, 1, 3));
            return (caps.ToArray(), null);
        }
        if (cb == 3 || r.Remaining < CbIdBytes(cb))
        {
            return default;
        }

        uint id = ReadVariable(ref r, cb);
        lock (_lock)
        {
            if (cmd == CmdCreate)
            {
                ReadOnlySpan<byte> bytes = r.Rest();
                int end = bytes.IndexOf((byte)0);
                string? name = end is >= 1 and <= 255 && end == bytes.Length - 1 && !bytes[..end].ContainsAnyInRange((byte)128, byte.MaxValue)
                    ? Encoding.ASCII.GetString(bytes[..end]) : null;
                Instance? created = null;
                if (name is not null && !_instances.ContainsKey(id) && _instances.Count < 64)
                {
                    var channel = new DynamicChannel(this, id);
                    if (_displayEnabled && name == DisplayControlName && !_displayOpen)
                    {
                        _displayOpen = true; _displayChannelId = id;
                        created = new(channel, null, MaxDisplayMessage);
                    }
                    else if (_listeners.TryGetValue(name, out Listener? listener))
                    {
                        try { created = new(channel, listener.Factory(channel), listener.Limit); }
                        catch (InvalidOperationException) { /* Listener capacity or state refused CREATE. */ }
                    }
                    if (created is not null)
                    {
                        _instances.Add(id, created);
                    }
                }
                var w = new ByteWriter(8);
                w.U8((byte)(0x10 | cb)); WriteVariable(w, id, cb);
                w.U32LE(created is not null ? 0u : 0x80004001u);
                return (w.ToArray(), created is { Handler: { } handler } opened
                    ? () => RunHandlerAsync(opened, handler.OnOpenAsync) : null);
            }
            if (!_instances.TryGetValue(id, out Instance? instance))
            {
                return default;
            }

            if (cmd == CmdClose)
            {
                ClearBuffer(instance);
                _ = _instances.Remove(id);
                if (_displayOpen && id == _displayChannelId)
                { _displayOpen = _displayReady = false; _lastSent = null; }
                return (null, instance.Handler is { } handler ? () => RunCloseHandlerAsync(instance, handler) : null);
            }
            if (cmd is not (CmdData or CmdDataFirst))
            {
                return default;
            }

            if (cmd == CmdDataFirst)
            {
                ClearBuffer(instance);
                if (sp == 3 || r.Remaining < CbIdBytes(sp))
                {
                    return Reject(instance);
                }

                uint total = ReadVariable(ref r, sp);
                if (total > instance.Limit || total < r.Remaining || total > MaxBufferedBytes - _reservedBytes)
                {
                    return Reject(instance);
                }

                if (total > r.Remaining)
                {
                    instance.Total = (int)total;
                    _reservedBytes += (int)total;
                    instance.Buffer = new MemoryStream((int)total);
                    instance.Buffer.Write(r.Rest());
                    return default;
                }
            }
            byte[] message;
            if (instance.Buffer is { } buffer)
            {
                if (buffer.Length + r.Remaining > instance.Total)
                {
                    return Reject(instance);
                }

                buffer.Write(r.Rest());
                if (buffer.Length < instance.Total)
                {
                    return default;
                }

                message = buffer.ToArray();
                ClearBuffer(instance);
            }
            else
            {
                if (r.Remaining > instance.Limit)
                {
                    return Reject(instance);
                }

                message = r.Rest().ToArray();
            }
            return instance.Handler is { } receiver
                ? (null, () => RunHandlerAsync(instance, () => receiver.OnMessageAsync(message)))
                : (OnDisplayControl(message), null);
        }
    }

    private void ClearBuffer(Instance instance)
    {
        if (instance.Buffer is null)
        {
            return;
        }

        _reservedBytes -= instance.Total;
        instance.Buffer.Dispose(); instance.Buffer = null; instance.Total = 0;
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
            return BuildMonitorLayout(_displayChannelId, p.Width, p.Height, p.Scale);
        }
    }

    private static byte[] BuildMonitorLayout(uint channelId, int width, int height, int scale = 100)
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
        disp.U32LE((uint)RdpScale.Clamp(scale));  // DesktopScaleFactor (percent)
        disp.U32LE((uint)RdpScale.Device(scale)); // DeviceScaleFactor (100 / 140 / 180)

        int cbId = channelId <= 0xFF ? 0 : channelId <= 0xFFFF ? 1 : 2;
        var w = new ByteWriter(4 + CbIdBytes(cbId) + disp.Length);
        w.U8((byte)((CmdData << 4) | cbId));
        WriteVariable(w, channelId, cbId);
        w.Bytes(disp.Span);
        return w.ToArray();
    }

    private static int Clamp(int value) => Math.Clamp(value, 200, 8192);

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
