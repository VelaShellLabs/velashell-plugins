using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Org.BouncyCastle.Tls;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>A loopback UDP peer; deliberately uses real sockets and drives the deterministic wire state.</summary>
internal sealed class FakeRdpUdpServer : IAsyncDisposable, DatagramTransport
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
    private readonly Lock _gate = new();
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private RdpUdpState _state = null!;
    private Task? _receive, _timer;
    private readonly List<byte[]> _wirePayloads = [];
    internal byte[][] WirePayloads { get { lock (_gate) { return [.. _wirePayloads]; } } }
    internal IPEndPoint Endpoint => (IPEndPoint)_socket.LocalEndPoint!;
    internal CancellationToken Token => _stop.Token;
    internal FakeRdpUdpServer(bool ipv6 = false)
    {
        _socket = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
    }

    internal async Task AcceptAsync(bool lossy)
    {
        byte[] buffer = new byte[1232];
        var anyPeer = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        SocketReceiveFromResult received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, anyPeer, Token);
        _socket.Connect(received.RemoteEndPoint);
        RdpUdpPacket syn = RdpUdpPacket.Decode(buffer.AsSpan(0, received.ReceivedBytes));
        Assert.AreEqual(lossy, syn.Flags.HasFlag(RdpUdpFlags.Lossy));
        _state = new(lossy, 12345);
        _state.Receive(syn with { Ack = 12345, Flags = syn.Flags | RdpUdpFlags.Ack }, Environment.TickCount64);
        byte[] response = (_state.Syn() with { Ack = syn.InitialSequence, Flags = _state.Syn().Flags | RdpUdpFlags.Ack }).Encode();
        await _socket.SendAsync(response, SocketFlags.None, Token);
        _receive = ReceiveLoopAsync();
        _timer = TimerAsync();
    }
    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[1233];
        try
        {
            while (true)
            {
                int count = await _socket.ReceiveAsync(buffer, SocketFlags.None, Token);
                RdpUdpPacket packet = RdpUdpPacket.Decode(buffer.AsSpan(0, count));
                List<byte[]> output;
                lock (_gate)
                {
                    if (packet.Flags.HasFlag(RdpUdpFlags.Data)) { _wirePayloads.Add(packet.Payload); }
                    output = _state.Receive(packet, Environment.TickCount64);
                    while (_state.Read() is { } data) { _incoming.Writer.TryWrite(data); }
                }
                await SendPacketsAsync(output);
            }
        }
        catch (Exception ex) { _incoming.Writer.TryComplete(ex); }
    }
    private async Task TimerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        try
        {
            while (await timer.WaitForNextTickAsync(Token))
            {
                List<byte[]> output;
                lock (_gate)
                {
                    output = _state.Tick(Environment.TickCount64);
                    while (_state.Read() is { } data) { _incoming.Writer.TryWrite(data); }
                }
                await SendPacketsAsync(output);
            }
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
    }
    private async Task SendPacketsAsync(List<byte[]> packets)
    { foreach (byte[] packet in packets) { await _socket.SendAsync(packet, SocketFlags.None, Token); } }
    internal async Task SendAsync(ReadOnlyMemory<byte> data)
    {
        byte[] copy = data.ToArray();
        while (true)
        {
            List<byte[]>? packets;
            lock (_gate) { packets = _state.Send(copy, Environment.TickCount64); }
            if (packets is not null) { await SendPacketsAsync(packets); return; }
            await Task.Delay(5, Token);
        }
    }
    internal async Task<byte[]> ReadAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);
    public int GetReceiveLimit() => 1232;
    public int GetSendLimit() => RdpUdpState.MaxPayload;
    public void Close() { }
    public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);
    public int Receive(Span<byte> buffer, int waitMillis)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        if (waitMillis > 0) { timeout.CancelAfter(waitMillis); }
        try
        {
            byte[] bytes = ReadAsync(timeout.Token).GetAwaiter().GetResult();
            bytes.CopyTo(buffer); return bytes.Length;
        }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested) { return -1; }
    }
    public void Send(byte[] buf, int off, int len) => SendAsync(buf.AsMemory(off, len)).GetAwaiter().GetResult();
    public void Send(ReadOnlySpan<byte> buffer) => SendAsync(buffer.ToArray()).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _socket.Dispose();
        if (_receive is not null) { await _receive; }
        if (_timer is not null) { await _timer; }
        _stop.Dispose();
    }

    internal sealed class ReliableStream(FakeRdpUdpServer server) : Stream
    {
        private byte[] _buffer = [];
        private int _offset;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] b, int o, int c) => WriteAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) { return 0; }
            while (_offset == _buffer.Length) { _buffer = await server.ReadAsync(ct); _offset = 0; }
            int count = Math.Min(buffer.Length, _buffer.Length - _offset);
            _buffer.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, RdpUdpState.MaxPayload);
                await server.SendAsync(buffer[..count]); buffer = buffer[count..];
            }
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
