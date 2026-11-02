using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using VelaShell.Rdp;
using VelaShell.Rdp.Wire;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpTunnelTests
{
    private static readonly Lazy<X509Certificate2> Certificate = new(() =>
    {
        using RSA rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    });
    private sealed class DtlsServer : DefaultTlsServer
    {
        private readonly BcTlsCrypto _crypto;
        internal DtlsServer() : this(new BcTlsCrypto(new SecureRandom())) { }
        private DtlsServer(BcTlsCrypto crypto) : base(crypto) => _crypto = crypto;
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.DTLSv12];
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256];
        protected override TlsCredentialedSigner GetRsaSignerCredentials()
        {
            using RSA rsa = Certificate.Value.GetRSAPrivateKey()!;
            return new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), _crypto,
                PrivateKeyFactory.CreateKey(rsa.ExportPkcs8PrivateKey()),
                new BcCertificate([_crypto.CreateCertificate(Certificate.Value.RawData)]),
                new SignatureAndHashAlgorithm(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.rsa));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EncryptedTunnelBindsCookieAndDefersDeliveryUntilSoftSync(bool lossy)
    {
        await using var server = new FakeRdpUdpServer();
        byte[] cookie = RandomNumberGenerator.GetBytes(16);
        Task serving = ServeAsync(server, lossy, cookie, rejectCertificate: false);
        RdpMultitransport connected;
        try
        {
            connected = await RdpMultitransport.ConnectAsync(server.Endpoint, "localhost",
                SHA256.HashData(Certificate.Value.RawData), lossy, 42, cookie, new(true), server.Token);
        }
        catch { await serving.WaitAsync(TimeSpan.FromSeconds(5)); throw; }
        await using RdpMultitransport tunnel = connected;
        var delivered = System.Threading.Channels.Channel.CreateBounded<byte[]>(2);
        tunnel.Start(bytes => { delivered.Writer.TryWrite(bytes); return Task.CompletedTask; }, ex => delivered.Writer.TryComplete(ex));
        Assert.IsFalse(tunnel.Reading);
        Assert.IsFalse(delivered.Reader.TryPeek(out _));
        tunnel.EnableReading();
        byte[] received = await delivered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        byte[] second = await delivered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSequenceEqual(new byte[] { 0x30, 1, 9 }, received);
        Assert.AreSequenceEqual(new byte[] { 0x30, 1, 8 }, second);
        await tunnel.SendAsync([0x30, 1, 7], server.Token);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(server.WirePayloads.Any(p => p.AsSpan().IndexOf(cookie) >= 0), "Cookie must be inside TLS/DTLS encryption.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DifferentLeafCertificateCannotCreateSideTunnel(bool lossy)
    {
        await using var server = new FakeRdpUdpServer();
        Task serving = ServeAsync(server, lossy, new byte[16], rejectCertificate: true);
        Exception rejected = await Assert.ThrowsAsync<Exception>(() => RdpMultitransport.ConnectAsync(server.Endpoint, "localhost",
            new byte[32], lossy, 42, new byte[16], new(true), server.Token));
        Assert.IsTrue(lossy ? rejected is TlsFatalAlert { AlertDescription: AlertDescription.bad_certificate }
            : rejected is AuthenticationException, rejected.ToString());
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task ServeAsync(FakeRdpUdpServer server, bool lossy, byte[] cookie, bool rejectCertificate)
    {
        await server.AcceptAsync(lossy);
        SslStream? tls = null;
        DtlsTransport? dtls = null;
        try
        {
            if (lossy) { dtls = await Task.Run(() => new DtlsServerProtocol().Accept(new DtlsServer(), server), server.Token); }
            else
            {
                tls = new(new FakeRdpUdpServer.ReliableStream(server));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = Certificate.Value, EnabledSslProtocols = SslProtocols.Tls12 }, server.Token);
            }
            if (rejectCertificate) { return; }
            byte[] request = await ReadFrameAsync(tls, dtls, server.Token);
            Assert.AreEqual((byte)0, request[0]);
            Assert.AreEqual(42u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(4)));
            Assert.AreSequenceEqual(cookie, request.AsSpan(12).ToArray());
            await WriteFrameAsync(tls, dtls, [1,4,0], server.Token);
            await WriteFrameAsync(tls, dtls, [4,0,0,0,0,
                .. RdpMultitransport.Frame(2, [0x30,1,9]), .. RdpMultitransport.Frame(2, [0x30,1,8])], server.Token);
            byte[] response = await ReadFrameAsync(tls, dtls, server.Token);
            Assert.AreSequenceEqual(new byte[] { 2,3,0,4,0x30,1,7 }, response);
        }
        catch (Exception) when (rejectCertificate) { }
        catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        finally { tls?.Dispose(); }
    }
    private static async Task WriteFrameAsync(SslStream? tls, DtlsTransport? dtls, byte[] frame, CancellationToken ct)
    {
        if (tls is not null) { await tls.WriteAsync(frame, ct); }
        else { await Task.Run(() => dtls!.Send(frame, 0, frame.Length), ct); }
    }
    private static async Task<byte[]> ReadFrameAsync(SslStream? tls, DtlsTransport? dtls, CancellationToken ct)
    {
        if (tls is not null)
        {
            byte[] header = new byte[4]; await tls.ReadExactlyAsync(header, ct);
            byte[] frame = new byte[RdpMultitransport.FrameLength(header)]; header.CopyTo(frame, 0);
            await tls.ReadExactlyAsync(frame.AsMemory(4), ct); return frame;
        }
        return await Task.Run(() =>
        {
            byte[] frame = new byte[2048];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int count = dtls!.Receive(frame, 0, frame.Length, 100);
                if (count >= 0) { return frame[..count]; }
            }
        }, ct);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task MappedIpv4AndNativeIpv6PeersCompleteUdpHandshakeAndTransferData(bool lossy, bool ipv6)
    {
        if (ipv6 && !System.Net.Sockets.Socket.OSSupportsIPv6) { Assert.Inconclusive("IPv6 is unavailable."); }
        var server = new FakeRdpUdpServer(ipv6);
        CancellationToken serverToken = server.Token;
        Task accepting = server.AcceptAsync(lossy);
        var peer = new System.Net.IPEndPoint(server.Endpoint.Address.MapToIPv6(), server.Endpoint.Port);
        try
        {
            await using var transport = await RdpUdpTransport.ConnectAsync(peer, lossy, server.Token);
            await accepting;
            await transport.SendAsync(new byte[] { 1, 2, 3 }, server.Token);
            byte[] received = await server.ReadAsync(server.Token);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, received);
            await server.SendAsync(new byte[] { 4, 5, 6 });
            byte[] response = await transport.ReceiveAsync(server.Token);
            Assert.AreSequenceEqual(new byte[] { 4, 5, 6 }, response);
        }
        finally
        {
            await server.DisposeAsync();
            try { await accepting; }
            catch (OperationCanceledException) when (serverToken.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    public async Task UdpHandshakeCancellationClosesSocketAndBackgroundLoops()
    {
        await using var silent = new FakeRdpUdpServer();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAsync<OperationCanceledException>(() => RdpUdpTransport.ConnectAsync(silent.Endpoint, false, stop.Token));
    }
}
