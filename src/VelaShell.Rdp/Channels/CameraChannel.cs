using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPECAM version 1, one explicitly selected camera and one colour capture stream.</summary>
internal sealed class CameraChannel
{
    internal const string EnumeratorName = "RDCamera_Device_Enumerator";
    private readonly RdpCameraDevice _device;
    private readonly string _channelName = "VelaCamera_" + Guid.NewGuid().ToString("N");
    private readonly Func<ICameraBackend> _factory;
    private readonly Lock _gate = new();
    private bool _claimed;
    private readonly HashSet<DynamicChannel> _enumerators = [];
    private bool _removed;

    private async Task RemovedAsync()
    {
        DynamicChannel[] enumerators;
        lock (_gate)
        {
            if (_removed)
            {
                return;
            }

            _removed = true; enumerators = [.. _enumerators];
        }
        byte[] removed = [1, 6, .. Encoding.ASCII.GetBytes(_channelName), 0];
        foreach (DynamicChannel enumerator in enumerators)
        {
            await enumerator.SendAsync(removed).ConfigureAwait(false);
        }
    }

    internal CameraChannel(DrdynvcChannel channels, RdpCameraDevice device, Func<ICameraBackend> factory)
    {
        _device = device; _factory = factory;
        channels.RegisterListener(_channelName, channel => new Device(this, channel), 16 * 1024 * 1024);
        channels.RegisterListener(EnumeratorName, channel => new Enumerator(this, channel));
    }

    private sealed class Enumerator(CameraChannel owner, DynamicChannel channel) : IDynamicChannelHandler
    {
        private bool _negotiated;
        public Task OnOpenAsync() => channel.SendAsync([1, 3]);
        public async Task OnMessageAsync(byte[] message)
        {
            if (_negotiated || !message.AsSpan().SequenceEqual(new byte[] { 1, 4 }))
            {
                return;
            }

            _negotiated = true;
            lock (owner._gate)
            {
                if (owner._removed)
                {
                    return;
                }

                _ = owner._enumerators.Add(channel);
            }
            byte[] friendly = Encoding.Unicode.GetBytes(owner._device.Name.Replace("\0", "") + "\0");
            byte[] name = Encoding.ASCII.GetBytes(owner._channelName + "\0");
            byte[] added = new byte[2 + friendly.Length + name.Length]; added[0] = 1; added[1] = 5;
            friendly.CopyTo(added, 2); name.CopyTo(added, 2 + friendly.Length);
            await channel.SendAsync(added).ConfigureAwait(false);
        }
        public Task OnCloseAsync()
        {
            _negotiated = false;
            lock (owner._gate)
            {
                _ = owner._enumerators.Remove(channel);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Device(CameraChannel owner, DynamicChannel channel) : IDynamicChannelHandler
    {
        private ICameraBackend? _backend;
        private CancellationTokenSource? _stream;
        private Task _sample = Task.CompletedTask;
        private bool _closed;
        public Task OnOpenAsync() => Task.CompletedTask;
        private Task Error(uint code, byte? stream = null)
        {
            byte[] bytes = new byte[stream.HasValue ? 7 : 6]; bytes[0] = 1; bytes[1] = stream.HasValue ? (byte)0x13 : (byte)2;
            if (stream.HasValue)
            {
                bytes[2] = stream.Value;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), code);
            return channel.SendAsync(bytes);
        }
        private Task Success() => channel.SendAsync([1, 1]);
        public async Task OnMessageAsync(byte[] message)
        {
            if (_closed)
            {
                return;
            }

            if (message.Length < 2 || message[0] != 1) { await Error(2).ConfigureAwait(false); return; }
            byte command = message[1];
            int expected = command switch { 7 or 8 or 9 or 0x10 => 2, 0x0b or 0x0d or 0x11 => 3, 0x0f => 29, _ => -1 };
            if (message.Length != expected)
            {
                await Error(2, command == 0x11 ? (message.Length > 2 ? message[2] : (byte)0) : null).ConfigureAwait(false);
                return;
            }
            if (command is 0x0b or 0x0d or 0x0f or 0x11 && message[2] != 0)
            { await Error(5, command == 0x11 ? message[2] : null).ConfigureAwait(false); return; }
            try
            {
                if (command == 7)
                {
                    if (_backend is not null) { await Error(4).ConfigureAwait(false); return; }
                    bool claimed;
                    lock (owner._gate)
                    {
                        claimed = !owner._claimed && !owner._removed; if (claimed)
                        {
                            owner._claimed = true;
                        }
                    }
                    if (!claimed) { await Error(4).ConfigureAwait(false); return; }
                    try { _backend = owner._factory(); _backend.Activate(); }
                    catch { _backend?.Dispose(); _backend = null; lock (owner._gate) { owner._claimed = false; } throw; }
                    await Success().ConfigureAwait(false); return;
                }
                if (_backend is null) { await Error(3, command == 0x11 ? 0 : null).ConfigureAwait(false); return; }
                switch (command)
                {
                    case 8:
                        await StopAsync().ConfigureAwait(false);
                        _backend.Deactivate(); _backend.Dispose(); _backend = null;
                        lock (owner._gate)
                        {
                            owner._claimed = false;
                        }

                        await Success().ConfigureAwait(false); break;
                    case 9:
                        await channel.SendAsync([1, 0x0a, 1, 0, 1, (byte)(_stream is null ? 0 : 1), 0]).ConfigureAwait(false); break;
                    case 0x0b:
                        await channel.SendAsync([1, 0x0c, .. _backend.MediaTypes.SelectMany(t => t.Encode())]).ConfigureAwait(false); break;
                    case 0x0d:
                        await channel.SendAsync([1, 0x0e, .. _backend.CurrentMediaType.Encode()]).ConfigureAwait(false); break;
                    case 0x0f:
                        if (_stream is not null) { await Error(4).ConfigureAwait(false); break; }
                        var type = CameraMediaType.Decode(message.AsSpan(3));
                        if (!_backend.MediaTypes.Contains(type)) { await Error(6).ConfigureAwait(false); break; }
                        _backend.Start(type); _stream = new();
                        await Success().ConfigureAwait(false); break;
                    case 0x10:
                        await StopAsync().ConfigureAwait(false); await Success().ConfigureAwait(false); break;
                    case 0x11:
                        if (_stream is null || !_sample.IsCompleted) { await Error(4, 0).ConfigureAwait(false); break; }
                        _sample = SendSampleAsync(_backend, _stream.Token); break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await Error(ex is OutOfMemoryException ? 7u : 1u, command == 0x11 ? 0 : null).ConfigureAwait(false);
                if (ex is CameraDeviceRemovedException)
                {
                    await owner.RemovedAsync().ConfigureAwait(false);
                }
            }
        }
        private async Task SendSampleAsync(ICameraBackend backend, CancellationToken ct)
        {
            try
            {
                byte[] sample = await backend.ReadSampleAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (sample.Length is 0 or > 16 * 1024 * 1024 - 3) { await Error(7, 0).ConfigureAwait(false); return; }
                byte[] response = new byte[sample.Length + 3]; response[0] = 1; response[1] = 0x12;
                sample.CopyTo(response, 3);
                await channel.SendAsync(response, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (!_closed)
                {
                    await Error(4, 0).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!_closed && !ct.IsCancellationRequested)
                {
                    await Error(1, 0).ConfigureAwait(false);
                    if (ex is CameraDeviceRemovedException)
                    {
                        backend.Deactivate();
                        await owner.RemovedAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        private async Task StopAsync()
        {
            if (_stream is null)
            {
                return;
            }

            _stream.Cancel();
            try { _backend?.Stop(); }
            finally
            {
                try { await _sample.ConfigureAwait(false); }
                finally { _stream.Dispose(); _stream = null; }
            }
        }
        public async Task OnCloseAsync()
        {
            _closed = true;
            try { await StopAsync().ConfigureAwait(false); }
            finally
            {
                if (_backend is not null)
                {
                    _backend.Dispose(); _backend = null;
                    lock (owner._gate)
                    {
                        owner._claimed = false;
                    }
                }
            }
        }
    }
}
