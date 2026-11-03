using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpGatewayTests
{
    [TestMethod]
    public async Task NegotiatesTargetAndPreservesStreamAcrossPacketsAndWebSocketFragments()
    {
        var socket = new ScriptedSocket([.. Responses(), Packet(13, []), Packet(10, [0, 0]), Packet(10, [3, 0, 8, 9, 10]), Packet(16, new byte[4])]);
        using RdpGatewayStream stream = await RdpGatewayStream.CreateForTestAsync(socket, "desktop.example", 3390, 0, CancellationToken.None);
        Assert.AreSequenceEqual(new byte[] { 1, 0, 0, 0, 14, 0, 0, 0, 1, 0, 0, 0, 0, 0 }, socket.Sent[0]);
        Assert.AreSequenceEqual(new byte[] { 4, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, socket.Sent[1]);
        byte[] channel = socket.Sent[3];
        Assert.AreEqual((byte)1, channel[8]);
        Assert.AreEqual((ushort)3390, BinaryPrimitives.ReadUInt16LittleEndian(channel.AsSpan(10)));
        Assert.AreEqual((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(channel.AsSpan(12)));
        Assert.AreEqual("desktop.example\0", Encoding.Unicode.GetString(channel.AsSpan(16)));
        Assert.AreEqual(channel.Length - 16, BinaryPrimitives.ReadUInt16LittleEndian(channel.AsSpan(14)));
        var read = new byte[3];
        await stream.ReadExactlyAsync(read);
        Assert.AreSequenceEqual(new byte[] { 8, 9, 10 }, read);
        var outgoing = new byte[70000];
        RandomNumberGenerator.Fill(outgoing);
        await stream.WriteAsync(outgoing);
        byte[][] sent = socket.Sent.Skip(4).ToArray();
        Assert.HasCount(2, sent);
        Assert.AreEqual(65535, BinaryPrimitives.ReadUInt16LittleEndian(sent[0].AsSpan(8)));
        Assert.AreSequenceEqual(outgoing, sent.SelectMany(p => p.Skip(10)).ToArray());
        int eof = await stream.ReadAsync(read);
        Assert.AreEqual(0, eof);
        Assert.AreEqual((ushort)17, BinaryPrimitives.ReadUInt16LittleEndian(socket.Sent[^1]));
    }

    [TestMethod]
    [DataRow(0x00000008u, 0x00000008u)]
    [DataRow(0x40000000u, 0x00000020u)]
    public async Task GatewayPolicyRejectsForbiddenRedirectionBeforeTargetConnect(uint restrictions, uint requested)
    {
        byte[][] replies = Responses();
        replies[2] = Packet(7, new byte[12]);
        replies[2][12] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(replies[2].AsSpan(16), restrictions);
        var socket = new ScriptedSocket(replies);
        var options = new RdpClientOptions { Host = "server", Clipboard = requested == 8, SmartCardRedirection = requested == 0x20 };
        await Assert.ThrowsExactlyAsync<IOException>(() => RdpGatewayStream.CreateForTestAsync(socket, "server", 3389,
            RdpManagedSession.GatewayRedirections(options), CancellationToken.None));
        Assert.HasCount(3, socket.Sent);
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    public async Task RejectsGatewayFailureConsentAndMalformedLengths()
    {
        byte[][] replies = Responses();
        replies[1][10] = 5;
        var denied = new ScriptedSocket(replies);
        await Assert.ThrowsExactlyAsync<IOException>(() => RdpGatewayStream.CreateForTestAsync(denied, "server", 3389, 0, CancellationToken.None));
        Assert.HasCount(2, denied.Sent);
        replies = Responses();
        replies[1][14] = 0x10;
        var consent = new ScriptedSocket(replies);
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => RdpGatewayStream.CreateForTestAsync(consent, "server", 3389, 0, CancellationToken.None));
        var huge = new ScriptedSocket([new byte[] { 2, 0, 0, 0, 255, 255, 255, 127 }]);
        await Assert.ThrowsExactlyAsync<RdpProtocolException>(() => RdpGatewayStream.CreateForTestAsync(huge, "server", 3389, 0, CancellationToken.None));
        var mismatch = new ScriptedSocket([.. Responses(), Packet(10, [10, 0, 1])]);
        using RdpGatewayStream stream = await RdpGatewayStream.CreateForTestAsync(mismatch, "server", 3389, 0, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<RdpProtocolException>(async () => { _ = await stream.ReadAsync(new byte[1]); });
    }

    [TestMethod]
    public async Task CancellationClosesIncompleteTunnel()
    {
        var socket = new ScriptedSocket([]) { WaitForCancellation = true };
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(() => RdpGatewayStream.CreateForTestAsync(socket, "server", 3389, 0, ct.Token));
        Assert.IsTrue(socket.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConnectsThroughRealTlsAndWebSocketWithoutResolvingTargetLocally(bool throughRdpConnection)
    {
        using X509Certificate2 certificate = Certificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string requestText = "";
        Task server = Task.Run(async () =>
        {
            using TcpClient tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(tcp.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            requestText = await ReadHeadersAsync(tls, timeout.Token);
            string key = requestText.Split("\r\n").Single(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), timeout.Token);
            using WebSocket ws = WebSocket.CreateFromStream(tls, true, null, Timeout.InfiniteTimeSpan);
            byte[][] replies = Responses();
            int[] types = [1, 4, 6, 8];
            for (int i = 0; i < replies.Length; i++)
            {
                byte[] incoming = await ReceiveMessageAsync(ws, timeout.Token);
                Assert.AreEqual(types[i], BinaryPrimitives.ReadUInt16LittleEndian(incoming));
                if (i == 3) { Assert.AreEqual("private.invalid\0", Encoding.Unicode.GetString(incoming.AsSpan(16))); }
                await ws.SendAsync(replies[i].AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
            byte[] data = await ReceiveMessageAsync(ws, timeout.Token);
            if (throughRdpConnection)
            {
                Assert.AreEqual((ushort)10, BinaryPrimitives.ReadUInt16LittleEndian(data));
                Assert.AreEqual((byte)3, data[10]); // The inner RDP TPKT/X.224 connection request.
                Assert.AreEqual((byte)0xe0, data[15]);
                await ws.SendAsync(Packet(16, new byte[4]).AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
            else
            {
                Assert.AreSequenceEqual(new byte[] { 3, 0, 1, 2, 3 }, data.AsSpan(8).ToArray());
                await ws.SendAsync(Packet(10, [2, 0, 4, 5]).AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
            byte[] close = await ReceiveMessageAsync(ws, timeout.Token);
            Assert.AreEqual(throughRdpConnection ? (ushort)17 : (ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(close));
        });
        var options = new RdpGatewayOptions { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, CertificatePin = certificate.GetCertHash(HashAlgorithmName.SHA256) };
        if (throughRdpConnection)
        {
            await using var connection = new RdpConnection(new("private.invalid", 3389, "user", "secret", "realm",
                640, 480, 32, null, "private.invalid", [], Gateway: options));
            await Assert.ThrowsExactlyAsync<RdpConnectionLostException>(() => connection.ConnectAsync(timeout.Token));
            Assert.AreEqual(RdpConnectStage.Negotiation, connection.Stage);
        }
        else
        {
            await using RdpGatewayStream stream = await RdpGatewayStream.ConnectAsync(options, "private.invalid", 3389, "user", "secret", "realm", 0, timeout.Token);
            await stream.WriteAsync(new byte[] { 1, 2, 3 }, timeout.Token);
            var result = new byte[2];
            await stream.ReadExactlyAsync(result, timeout.Token);
            Assert.AreSequenceEqual(new byte[] { 4, 5 }, result);
        }
        await server;
        StringAssert.StartsWith(requestText, "GET /remoteDesktopGateway/ HTTP/1.1");
        StringAssert.Contains(requestText, "RDG-Connection-Id:");
        Assert.IsFalse(requestText.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false, "HTTP/1.1 302 Found\r\nLocation: https://other.invalid/\r\nContent-Length: 0\r\n\r\n")]
    [DataRow(false, "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"gateway\"\r\nContent-Length: 0\r\n\r\n")]
    [DataRow(true, "")]
    public async Task RejectsRedirectBasicOnlyAuthenticationAndUntrustedGateway(bool rejectCertificate, string response)
    {
        using X509Certificate2 certificate = Certificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string request = "";
        Task server = Task.Run(async () =>
        {
            using TcpClient tcp = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var tls = new SslStream(tcp.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                request = await ReadHeadersAsync(tls, timeout.Token);
                if (!rejectCertificate) { await tls.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token); }
            }
            catch (Exception ex) when (rejectCertificate && ex is AuthenticationException or IOException) { }
        });
        var options = new RdpGatewayOptions
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            CertificatePin = rejectCertificate ? null : certificate.GetCertHash(HashAlgorithmName.SHA256)
        };
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => RdpGatewayStream.ConnectAsync(options, "target", 3389, "user", "secret", "", 0, timeout.Token));
        await server;
        if (rejectCertificate) { StringAssert.Contains(error.Message, "certificate validation failed"); Assert.AreEqual("", request); }
        Assert.IsFalse(request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(error.ToString().Contains("secret", StringComparison.Ordinal));
    }

    private static X509Certificate2 Certificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static async Task<string> ReadHeadersAsync(Stream stream, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (text.Length < 32768)
        {
            int count = await stream.ReadAsync(one, ct);
            if (count == 0) { throw new EndOfStreamException(); }
            text.Append((char)one[0]);
            if (text.Length >= 4 && text.ToString(text.Length - 4, 4) == "\r\n\r\n") { return text.ToString(); }
        }
        throw new InvalidDataException();
    }

    private static async Task<byte[]> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            ValueWebSocketReceiveResult read = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            Assert.AreEqual(WebSocketMessageType.Binary, read.MessageType);
            result.Write(buffer, 0, read.Count);
            if (read.EndOfMessage) { return result.ToArray(); }
        }
    }

    private static byte[][] Responses()
    {
        var handshake = new byte[10];
        handshake[4] = 1;
        // A server may advertise extended authentication without requiring it.
        handshake[8] = 3;
        return [Packet(2, handshake), Packet(5, new byte[10]), Packet(7, new byte[8]), Packet(9, new byte[8])];
    }

    private static byte[] Packet(ushort type, byte[] body)
    {
        var result = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, type);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)result.Length);
        body.CopyTo(result, 8);
        return result;
    }

    private sealed class ScriptedSocket(byte[][] input) : WebSocket
    {
        private readonly MemoryStream _input = new(input.SelectMany(p => p).ToArray());
        internal List<byte[]> Sent { get; } = [];
        internal bool Disposed { get; private set; }
        internal bool WaitForCancellation { get; init; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override void Abort() => Disposed = true;
        public override void Dispose() { Disposed = true; _input.Dispose(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
        {
            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (WaitForCancellation) { await Task.Delay(Timeout.Infinite, ct); }
            int count = _input.Read(buffer.AsSpan()[..Math.Min(3, buffer.Count)]);
            return new WebSocketReceiveResult(count, count == 0 ? WebSocketMessageType.Close : WebSocketMessageType.Binary, false);
        }
    }
}
