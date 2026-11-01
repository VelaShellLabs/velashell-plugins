using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPEPNP control and file channels, restricted to the backend's explicit selection.</summary>
internal sealed class WpdRedirection(IWpdDeviceBackend backend) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly HashSet<uint> _announced = [];
    private bool _disposed;
    private Task? _disposing;
    private readonly HashSet<uint> _allowed = backend.Devices.Select(d => d.DeviceId).ToHashSet();
    private readonly List<WpdFileChannel> _files = [];
    private Control? _control;
    private bool _authenticated;

    internal void Register(DrdynvcChannel channels)
    {
        channels.RegisterListener("PNPDR", channel => CreateControl(
            data => channel.SendAsync(data), () => channel.CloseAsync()), 2 * WpdPolicy.MaxTransfer);
        channels.RegisterListener("FileRedirectorChannel", channel => CreateFile(
            data => channel.SendAsync(data), () => channel.CloseAsync()), 2 * WpdPolicy.MaxTransfer + 32);
        StartMonitoring();
    }

    internal void StartMonitoring()
    {
        if (backend is IWpdDeviceMonitor monitor) monitor.Start(RemoveDeviceAsync, AddDeviceAsync);
    }

    private async Task AddDeviceAsync(WpdDeviceDescription device)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Control? control;
            lock (_gate)
            {
                if (_disposed || !_allowed.Add(device.DeviceId)) return;
                control = _authenticated ? _control : null;
            }
            if (control is not null)
            {
                await control.SendAdditionAsync(device).ConfigureAwait(false);
                _announced.Add(device.DeviceId);
            }
        }
        finally { _changes.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_disposing ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate) { _disposed = true; _authenticated = false; }
        try { if (backend is IWpdDeviceMonitor monitor) await monitor.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            WpdFileChannel[] files;
            lock (_gate) { _authenticated = false; files = _files.ToArray(); }
            await Task.WhenAll(files.Select(file => file.CloseAsync())).ConfigureAwait(false);
        }
    }

    internal IDynamicChannelHandler CreateControl(Func<byte[], Task> send, Func<Task> close)
    {
        lock (_gate)
        {
            if (_disposed || _control is not null) throw new InvalidOperationException("WPD control channel is closed or already open.");
            _control = new Control(this, send, close);
            return _control;
        }
    }

    internal WpdFileChannel CreateFile(Func<byte[], Task> send, Func<Task> close)
    {
        lock (_gate)
        {
            if (_disposed || _files.Count >= 16) throw new InvalidOperationException("WPD channel is closed or its limit was exceeded.");
            WpdFileChannel? file = null;
            file = new WpdFileChannel(backend,
                () => { lock (_gate) return _authenticated; },
                id => { lock (_gate) return _allowed.Contains(id); }, send, close,
                () => { lock (_gate) _files.Remove(file!); }, RemoveDeviceAsync);
            _files.Add(file);
            return file;
        }
    }

    internal async Task RemoveDeviceAsync(uint deviceId)
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            Control? control;
            WpdFileChannel[] files;
            lock (_gate)
            {
                if (!_allowed.Remove(deviceId)) return;
                control = _authenticated ? _control : null;
                files = _files.Where(f => f.DeviceId == deviceId).ToArray();
            }
            await Task.WhenAll(files.Select(file => file.CloseAsync())).ConfigureAwait(false);
            if (_announced.Remove(deviceId) && control is not null) await control.SendRemovalAsync(deviceId).ConfigureAwait(false);
        }
        finally { _changes.Release(); }
    }

    private sealed class Control(WpdRedirection owner, Func<byte[], Task> send, Func<Task> close) : IDynamicChannelHandler
    {
        private bool _version, _logon, _closed;
        public Task OnOpenAsync() => Task.CompletedTask;
        public async Task OnMessageAsync(byte[] message)
        {
            lock (owner._gate) { if (_closed) return; }
            if (message.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(message) != message.Length)
            { await close().ConfigureAwait(false); return; }
            uint packet = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4));
            if (packet == 0x65 && !_version && message.Length == 20 &&
                BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(8)) == 1 &&
                BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(16)) == 1)
            {
                _version = true;
                await send(ControlPacket(0x65, writer => { writer.Write(1u); writer.Write(5u); writer.Write(1u); })).ConfigureAwait(false);
            }
            else if (packet == 0x67 && _version && !_logon && message.Length == 8)
            {
                _logon = true;
                await owner._changes.WaitAsync().ConfigureAwait(false);
                try
                {
                    WpdDeviceDescription[] devices;
                    lock (owner._gate)
                    {
                        if (_closed || owner._disposed) return;
                        owner._authenticated = true;
                        devices = owner.SelectedDevices();
                    }
                    if (devices.Length != 0) await send(Addition(devices)).ConfigureAwait(false);
                    owner._announced.Clear();
                    owner._announced.UnionWith(devices.Select(d => d.DeviceId));
                }
                finally { owner._changes.Release(); }
            }
            else await close().ConfigureAwait(false);

        }
        public async Task OnCloseAsync()
        {
            WpdFileChannel[] files;
            lock (owner._gate)
            {
                if (_closed) return;
                _closed = true;
                owner._authenticated = false;
                owner._control = null;
                files = owner._files.ToArray();
                owner._files.Clear();
            }
            await Task.WhenAll(files.Select(file => file.CloseAsync())).ConfigureAwait(false);
        }
        internal Task SendRemovalAsync(uint deviceId) => send(ControlPacket(0x68, writer => writer.Write(deviceId)));
        internal Task SendAdditionAsync(WpdDeviceDescription device) => send(Addition([device]));
    }

    private WpdDeviceDescription[] SelectedDevices() => backend.Devices.Where(d => _allowed.Contains(d.DeviceId)).ToArray();

    internal static byte[] Addition(IReadOnlyList<WpdDeviceDescription> devices) => ControlPacket(0x66, writer =>
    {
        writer.Write((uint)devices.Count);
        foreach (WpdDeviceDescription device in devices)
        {
            long start = writer.BaseStream.Position;
            writer.Write(device.DeviceId);
            writer.Write(0u);
            Field(writer, WpdPolicy.InterfaceClass.ToByteArray());
            Field(writer, device.HardwareIds);
            Field(writer, device.CompatibleIds);
            Field(writer, Encoding.Unicode.GetBytes(device.Name));
            writer.Write(4u);
            writer.Write(0u);
            long end = writer.BaseStream.Position;
            writer.BaseStream.Position = start + 4;
            writer.Write(checked((uint)(end - start)));
            writer.BaseStream.Position = end;
        }
    });

    private static void Field(BinaryWriter writer, byte[] value) { writer.Write((uint)value.Length); writer.Write(value); }
    private static byte[] ControlPacket(uint id, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        writer.Write(0u);
        writer.Write(id);
        body(writer);
        stream.Position = 0;
        writer.Write(checked((uint)stream.Length));
        return stream.ToArray();
    }
}
