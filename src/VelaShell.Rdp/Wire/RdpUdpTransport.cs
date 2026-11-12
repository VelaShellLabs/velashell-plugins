using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace VelaShell.Rdp.Wire;

internal sealed class RdpUdpTransport : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly RdpUdpState _state;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _changed = NewPulse();
    private readonly Task _receive, _timer;
    private Exception? _failure;
    private int _disposed;

    private RdpUdpTransport(IPEndPoint peer, bool lossy)
    {
        // A dual-stack TCP connection reports IPv4 peers as ::ffff:a.b.c.d; a new IPv6-only UDP socket cannot connect to them.
        if (peer.Address.IsIPv4MappedToIPv6) { peer = new IPEndPoint(peer.Address.MapToIPv4(), peer.Port); }
        _socket = new Socket(peer.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try { _socket.Connect(peer); }
        catch { _socket.Dispose(); throw; }
        _state = new(lossy, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)));
        _receive = ReceiveLoopAsync();
        _timer = TimerLoopAsync();
    }

    internal static async Task<RdpUdpTransport> ConnectAsync(IPEndPoint peer, bool lossy, CancellationToken ct)
    {
        var transport = new RdpUdpTransport(peer, lossy);
        try
        {
            byte[] syn = transport._state.Syn().Encode();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                _ = await transport._socket.SendAsync(syn, SocketFlags.None, ct).ConfigureAwait(false);
                try
                {
                    await transport._connected.Task.WaitAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                    return transport;
                }
                catch (TimeoutException) { }
            }
            throw new IOException("RDP-UDP handshake timed out.");
        }
        catch { await transport.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal (double Rtt, long Sent, long Lost) Statistics
    {
        get { lock (_gate) { return (_state.RttMilliseconds, _state.PacketsSent, _state.PacketsLost); } }
    }

    internal void RequireCompleteDelivery()
    {
        lock (_gate) { _state.RequireCompleteDelivery = true; }
    }

    internal async Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] copy = payload.ToArray();
        while (true)
        {
            Task wait;
            List<byte[]>? packets;
            lock (_gate)
            {
                CheckFailure();
                packets = _state.Send(copy, Environment.TickCount64);
                wait = _changed.Task;
            }
            if (packets is not null) { await SendPacketsAsync(packets, ct).ConfigureAwait(false); return; }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    internal async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                CheckFailure();
                byte[]? payload = _state.Read();
                if (payload is not null) { return payload; }
                wait = _changed.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Datagrams dropped as oversized, malformed or inconsistent with the connection state (diagnostics).</summary>
    internal long DroppedDatagrams => Interlocked.Read(ref _droppedDatagrams);
    private long _droppedDatagrams;

    /// <summary>
    /// A single datagram that is oversized, malformed or does not fit the connection state (a late duplicate SYN-ACK,
    /// unknown flags, an acknowledgement of unsent data) is dropped, as datagram transports do: ending the tunnel —
    /// and with it the session, once channels have moved onto it — for one stray packet made UDP less reliable than
    /// TCP. The state machine validates before it changes anything, so a dropped datagram leaves it consistent. A
    /// peer that is really gone is detected by the retransmission and keep-alive limits. Windows also reports an
    /// ICMP port-unreachable for an earlier send as a reset on the next receive; that is ignored the same way.
    /// </summary>
    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[1233];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int size;
                try { size = await _socket.ReceiveAsync(buffer, SocketFlags.None, _stop.Token).ConfigureAwait(false); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.MessageSize or SocketError.ConnectionReset)
                {
                    _ = Interlocked.Increment(ref _droppedDatagrams);
                    continue;
                }

                if (size > 1232)
                {
                    _ = Interlocked.Increment(ref _droppedDatagrams);   // truncated: larger than the negotiated MTU
                    continue;
                }

                List<byte[]> output;
                try
                {
                    var packet = RdpUdpPacket.Decode(buffer.AsSpan(0, size));
                    lock (_gate)
                    {
                        output = _state.Receive(packet, Environment.TickCount64);
                        if (_state.Established) { _ = _connected.TrySetResult(); }
                        Pulse();
                    }
                }
                catch (RdpProtocolException)
                {
                    _ = Interlocked.Increment(ref _droppedDatagrams);
                    continue;
                }

                await SendPacketsAsync(output, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task TimerLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                List<byte[]> output;
                lock (_gate) { output = _state.Tick(Environment.TickCount64); Pulse(); }
                await SendPacketsAsync(output, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async Task SendPacketsAsync(List<byte[]> packets, CancellationToken ct)
    {
        try
        {
            foreach (byte[] packet in packets) { _ = await _socket.SendAsync(packet, SocketFlags.None, ct).ConfigureAwait(false); }
        }
        catch (Exception ex) { Fail(ex); throw; }
    }

    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { TaskCompletionSource old = _changed; _changed = NewPulse(); _ = old.TrySetResult(); }
    private void CheckFailure()
    {
        if (_failure is not null) { throw new IOException("RDP-UDP connection closed.", _failure); }
        _stop.Token.ThrowIfCancellationRequested();
    }
    private void Fail(Exception error)
    {
        lock (_gate) { _failure ??= error; _ = _connected.TrySetException(error); Pulse(); }
        _stop.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _stop.Cancel();
        _socket.Dispose();
        lock (_gate) { Pulse(); }
        await Task.WhenAll(_receive, _timer).ConfigureAwait(false);
        if (_connected.Task.IsFaulted) { _ = _connected.Task.Exception; }
        _stop.Dispose();
    }
}

internal sealed class RdpUdpStream(RdpUdpTransport transport) : Stream
{
    private byte[] _current = [];
    private int _offset;
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) { return 0; }
        while (_offset == _current.Length) { _current = await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false); _offset = 0; }
        int size = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, size).CopyTo(buffer);
        _offset += size;
        return size;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            int size = Math.Min(RdpUdpState.MaxPayload, buffer.Length);
            await transport.SendAsync(buffer[..size], cancellationToken).ConfigureAwait(false);
            buffer = buffer[size..];
        }
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
