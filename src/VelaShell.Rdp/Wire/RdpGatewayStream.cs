using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace VelaShell.Rdp.Wire;

/// <summary>MS-TSGU RDGHTTP packets over RFC 6455, exposed as the target's ordered byte stream.</summary>
internal sealed class RdpGatewayStream : Stream
{
    private const int MaxPacketLength = 1024 * 1024;
    private const uint ServiceMessageCapability = 0x00000008;
    private readonly WebSocket _socket;
    private readonly IDisposable? _http;
    private readonly Action<string>? _serviceMessageReceived;
    private readonly SemaphoreSlim _writer = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task _keepAlive = Task.CompletedTask;
    private byte[] _pending = [];
    private int _offset;
    private bool _closed;
    private int _disposed;

    private RdpGatewayStream(WebSocket socket, IDisposable? http = null, Action<string>? serviceMessageReceived = null)
    {
        _socket = socket;
        _http = http;
        _serviceMessageReceived = serviceMessageReceived;
    }

    internal static async Task<RdpGatewayStream> ConnectAsync(RdpGatewayOptions options, string targetHost, int targetPort,
        string username, string password, string domain, uint requestedRedirections, CancellationToken ct)
    {
        options.Validate();
        if (!OperatingSystem.IsWindows()) { AppContext.SetSwitch("System.Net.Security.UseManagedNtlm", true); }
        var endpoint = new UriBuilder("https", options.Host, options.Port, "/remoteDesktopGateway/").Uri;
        var credential = options.UseRdpCredentials
            ? new NetworkCredential(username, password, domain)
            : new NetworkCredential(options.Username, options.Password, options.Domain);
        var credentials = new CredentialCache { { endpoint, "Negotiate", credential }, { endpoint, "NTLM", credential } };
        byte[]? pin = options.CertificatePin?.ToArray();
        bool certificateRejected = false;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = credentials,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    bool accepted = certificate is not null && (pin is null ? errors == SslPolicyErrors.None
                        : CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), pin));
                    certificateRejected |= !accepted;
                    return accepted;
                }
            }
        };
        var http = new HttpMessageInvoker(handler);
        var socket = new ClientWebSocket();
        socket.Options.HttpVersion = HttpVersion.Version11;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        socket.Options.SetRequestHeader("RDG-Connection-Id", Guid.NewGuid().ToString("B"));
        socket.Options.SetRequestHeader("Cache-Control", "no-cache");
        socket.Options.SetRequestHeader("Pragma", "no-cache");
        var stream = new RdpGatewayStream(socket, http, options.ServiceMessageReceived);
        try
        {
            await socket.ConnectAsync(new UriBuilder(endpoint) { Scheme = "wss" }.Uri, http, ct).ConfigureAwait(false);
            await stream.InitializeAsync(targetHost, targetPort, requestedRedirections, ct).ConfigureAwait(false);
            stream._keepAlive = stream.KeepAliveAsync();
            return stream;
        }
        catch (GatewayPolicyException)
        {
            // Already a specific, actionable reason; the generic "gateway must support …" hint would hide it.
            stream.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stream.Dispose();
            throw new IOException(certificateRejected
                ? "RD Gateway certificate validation failed. Trust the gateway CA or configure its separate SHA-256 pin."
                : "RD Gateway connection failed. The gateway must support RDGHTTP over WebSocket and HTTP Negotiate/NTLM authentication. " + ex.Message, ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static async Task<RdpGatewayStream> CreateForTestAsync(WebSocket socket, string host, int port,
        uint requestedRedirections, CancellationToken ct, Action<string>? serviceMessageReceived = null)
    {
        var stream = new RdpGatewayStream(socket, serviceMessageReceived: serviceMessageReceived);
        try
        {
            await stream.InitializeAsync(host, port, requestedRedirections, ct).ConfigureAwait(false);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private async Task InitializeAsync(string targetHost, int targetPort, uint requestedRedirections, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetHost) || targetHost.Any(char.IsControl) || targetPort is < 1 or > 65535)
        {
            throw new ArgumentException("RD Gateway target is invalid.");
        }
        byte[] target = UnicodeString(targetHost);
        await SendPacketAsync(1, new byte[] { 1, 0, 0, 0, 0, 0 }, ct).ConfigureAwait(false);
        byte[] handshake = await ExpectAsync(2, 10, 0, ct).ConfigureAwait(false);
        if (handshake[4] != 1 || handshake[5] != 0)
        {
            throw new NotSupportedException("RD Gateway selected an unsupported RDGHTTP version.");
        }
        // Advertise only the implemented advisory service-message capability. UDP, NAP, consent UI
        // and reauthentication still require protocol behavior that this stream does not implement.
        var tunnelRequest = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tunnelRequest, ServiceMessageCapability);
        await SendPacketAsync(4, tunnelRequest, ct).ConfigureAwait(false);
        byte[] tunnel = await ExpectAsync(5, 10, 2, ct).ConfigureAwait(false);
        ushort fields = BinaryPrimitives.ReadUInt16LittleEndian(tunnel.AsSpan(6));
        if ((fields & ~3) != 0) { throw new NotSupportedException("RD Gateway requires unsupported tunnel authorization or consent."); }
        int position = 10;
        if ((fields & 1) != 0) { Require(tunnel, position, 4); position += 4; }
        if ((fields & 2) != 0)
        {
            Require(tunnel, position, 4);
            _ = BinaryPrimitives.ReadUInt32LittleEndian(tunnel.AsSpan(position));
            position += 4;
        }
        if (position != tunnel.Length) { throw new RdpProtocolException("Invalid RD Gateway tunnel response length."); }
        byte[] clientName = UnicodeString(Environment.MachineName);
        var authorization = new byte[2 + clientName.Length];
        clientName.CopyTo(authorization, 2);
        await SendPacketAsync(6, authorization, ct).ConfigureAwait(false);
        byte[] auth = await ExpectAsync(7, 8, 0, ct).ConfigureAwait(false);
        fields = BinaryPrimitives.ReadUInt16LittleEndian(auth.AsSpan(4));
        position = 8;
        if ((fields & ~3) != 0) { throw new NotSupportedException("RD Gateway requires unsupported health authorization."); }
        if ((fields & 1) != 0)
        {
            Require(auth, position, 4);
            uint restrictions = BinaryPrimitives.ReadUInt32LittleEndian(auth.AsSpan(position));
            if (IsRedirectionForbidden(restrictions, requestedRedirections))
            {
                throw new GatewayPolicyException("RD Gateway policy forbids a requested redirection. Disable clipboard/device redirection in the connection settings.");
            }
            position += 4;
        }
        if ((fields & 2) != 0) { Require(auth, position, 4); position += 4; }
        if (position != auth.Length) { throw new RdpProtocolException("Invalid RD Gateway authorization length."); }
        var channel = new byte[6 + target.Length];
        channel[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(2), (ushort)targetPort);
        BinaryPrimitives.WriteUInt16LittleEndian(channel.AsSpan(4), 3);
        target.CopyTo(channel, 6);
        await SendPacketAsync(8, channel, ct).ConfigureAwait(false);
        byte[] response = await ExpectAsync(9, 8, 0, ct).ConfigureAwait(false);
        fields = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4));
        position = 8;
        if ((fields & ~7) != 0) { throw new RdpProtocolException("Invalid RD Gateway channel response fields."); }
        if ((fields & 1) != 0) { Require(response, position, 4); position += 4; }
        if ((fields & 2) != 0) { Require(response, position, 2); position += 2; }
        if ((fields & 4) != 0)
        {
            Require(response, position, 2);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(position));
            position += 2;
            Require(response, position, count);
            position += count;
        }
        if (position != response.Length) { throw new RdpProtocolException("Invalid RD Gateway channel response length."); }
    }

    private static byte[] UnicodeString(string value)
    {
        int bytes = Encoding.Unicode.GetByteCount(value) + 2;
        if (bytes > ushort.MaxValue) { throw new ArgumentException("RD Gateway string is too long."); }
        var result = new byte[bytes + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(result, (ushort)bytes);
        Encoding.Unicode.GetBytes(value, result.AsSpan(2));
        return result;
    }

    private static void Require(byte[] data, int position, int count)
    {
        if (position < 0 || count < 0 || count > data.Length - position)
        {
            throw new RdpProtocolException("Truncated RD Gateway packet.");
        }
    }

    private static string ReadServiceMessage(byte[] body)
    {
        Require(body, 0, 2);
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if ((length & 1) != 0 || length != body.Length - 2)
        {
            throw new RdpProtocolException("Invalid RD Gateway service-message length.");
        }
        return Encoding.Unicode.GetString(body, 2, length).TrimEnd('\0');
    }

    private async Task<byte[]> ExpectAsync(ushort expected, int minimum, int errorOffset, CancellationToken ct)
    {
        (ushort type, byte[] body) = await ReceivePacketAsync(ct).ConfigureAwait(false);
        if (type != expected) { throw new RdpProtocolException($"Unexpected RD Gateway packet 0x{type:X4}; expected 0x{expected:X4}."); }
        Require(body, 0, minimum);
        uint error = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(errorOffset));
        if (error != 0) { throw new IOException($"RD Gateway rejected request 0x{expected:X4} (0x{error:X8})."); }
        return body;
    }

    private async Task<(ushort Type, byte[] Body)> ReceivePacketAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await ReceiveExactlyAsync(header, ct).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (size is < 8 or > MaxPacketLength) { throw new RdpProtocolException("Invalid RD Gateway packet length."); }
        var body = new byte[size - 8];
        await ReceiveExactlyAsync(body, ct).ConfigureAwait(false);
        return (BinaryPrimitives.ReadUInt16LittleEndian(header), body);
    }

    private async Task ReceiveExactlyAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (!buffer.IsEmpty)
        {
            ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) { throw new EndOfStreamException("RD Gateway closed the tunnel."); }
            if (result.MessageType != WebSocketMessageType.Binary) { throw new RdpProtocolException("RD Gateway sent a non-binary message."); }
            buffer = buffer[result.Count..];
        }
    }

    private async Task SendPacketAsync(ushort type, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        await _writer.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var packet = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(packet, type);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)packet.Length);
            body.CopyTo(packet.AsMemory(8));
            await _socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
        }
        finally { _writer.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (buffer.IsEmpty || _closed) { return 0; }
        while (_offset == _pending.Length)
        {
            (ushort type, byte[] body) = await ReceivePacketAsync(cancellationToken).ConfigureAwait(false);
            switch (type)
            {
                case 10:
                    Require(body, 0, 2);
                    int size = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    if (body.Length != size + 2) { throw new RdpProtocolException("Invalid RD Gateway data length."); }
                    _pending = body;
                    _offset = 2;
                    break;
                case 13:
                    if (body.Length != 0) { throw new RdpProtocolException("Invalid RD Gateway keepalive."); }
                    break;
                case 16:
                    Require(body, 0, 4);
                    _closed = true;
                    await SendPacketAsync(17, new byte[4], cancellationToken).ConfigureAwait(false);
                    uint status = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    if (status != 0) { throw new IOException($"RD Gateway closed the channel (0x{status:X8})."); }
                    return 0;
                case 12:
                    throw new NotSupportedException("RD Gateway requested reauthentication; reconnect the session.");
                case 11:
                    string message = ReadServiceMessage(body);
                    try { _serviceMessageReceived?.Invoke(message); }
                    catch { /* An observer must not affect protocol ownership or transport progress. */ }
                    break;
                default:
                    throw new RdpProtocolException($"Unexpected RD Gateway data packet 0x{type:X4}.");
            }
        }
        int count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_closed) { throw new IOException("RD Gateway channel is closed."); }
        while (!buffer.IsEmpty)
        {
            int count = Math.Min(buffer.Length, ushort.MaxValue);
            var body = new byte[count + 2];
            BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)count);
            buffer[..count].CopyTo(body.AsMemory(2));
            await SendPacketAsync(10, body, cancellationToken).ConfigureAwait(false);
            buffer = buffer[count..];
        }
    }

    private async Task KeepAliveAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await SendPacketAsync(13, ReadOnlyMemory<byte>.Empty, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { _socket.Abort(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
            _socket.Abort();
            _socket.Dispose();
            _http?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed == 0 && !_closed && _socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await SendPacketAsync(16, new byte[4], timeout.Token).ConfigureAwait(false); }
            catch (Exception) { }
        }
        Dispose();
        await _keepAlive.ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override bool CanRead => _disposed == 0;
    public override bool CanWrite => _disposed == 0 && !_closed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// The tunnel's redirFlags (MS-TSGU HTTP_TUNNEL_AUTH_RESPONSE): HTTP_TUNNEL_REDIR_ENABLE_ALL (0x80000000) allows
    /// everything and the other bits are then ignored; DISABLE_ALL (0x40000000) forbids every redirection; otherwise
    /// DISABLE_DRIVE/PRINTER/PORT/CLIPBOARD/PNP (0x01–0x10) forbid one kind each.
    /// </summary>
    internal static bool IsRedirectionForbidden(uint restrictions, uint requestedRedirections)
    {
        if ((restrictions & 0x80000000) != 0)
        {
            return false;
        }

        return (restrictions & requestedRedirections & 0x1F) != 0
            || ((restrictions & 0x40000000) != 0 && requestedRedirections != 0);
    }

    /// <summary>The gateway's policy refuses a redirection this connection requested.</summary>
    private sealed class GatewayPolicyException(string message) : IOException(message);
}
