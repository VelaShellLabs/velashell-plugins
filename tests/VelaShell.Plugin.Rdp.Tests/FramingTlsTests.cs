using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class FramingTlsTests
{
    [TestMethod]
    public async Task ExactFraming_HalfPacketsAndCoalescedPackets_PreservesFollowingTlsBytes()
    {
        byte[] packet = [3, 0, 0, 7, 1, 2, 3];
        using var stream = new ChunkedStream([.. packet, 22, 3, 3, 0, 1]);
        byte[] actual = await RdpFraming.ReadRdpAsync(stream, CancellationToken.None);
        CollectionAssert.AreEqual(packet, actual);
        Assert.AreEqual(22, stream.ReadByte());
    }
    [TestMethod]
    public async Task FastpathAndCredssp_LongLength_AreReadExactly()
    {
        using var fast = new ChunkedStream([0, 0x80, 5, 7, 8]);
        byte[] actual = await RdpFraming.ReadRdpAsync(fast, CancellationToken.None);
        Assert.HasCount(5, actual);
        byte[] der = [0x30, 0x81, 0x80, .. new byte[128]];
        using var ts = new ChunkedStream(der);
        byte[] request = await RdpFraming.ReadCredsspAsync(ts, CancellationToken.None);
        CollectionAssert.AreEqual(der, request);
    }
    [TestMethod]
    public async Task TruncatedAndMaliciousFrames_FailInsteadOfAllocating()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => RdpFraming.ReadRdpAsync(new MemoryStream([3, 0, 0, 8, 1]), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => RdpFraming.ReadRdpAsync(new MemoryStream([3, 0, 0, 2]), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => RdpFraming.ReadCredsspAsync(new MemoryStream([0x30, 0x84, 0xff, 0xff, 0xff, 0xff]), CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => RdpFraming.ReadCredsspAsync(new MemoryStream([0x30, 0x80]), CancellationToken.None));
    }
    [TestMethod]
    public void CertificateTrust_Sha256PinIsRequiredEvenForSystemValidCertificate()
    {
        using var certificate = Certificate("target.example");
        string pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        using var unpinned = new RdpTlsTrust("");
        Assert.IsTrue(unpinned.Validate(this, certificate, null, SslPolicyErrors.None));
        Assert.IsFalse(unpinned.Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateNameMismatch));
        using var pinned = new RdpTlsTrust(pin);
        Assert.IsTrue(pinned.Validate(this, certificate, null, SslPolicyErrors.RemoteCertificateChainErrors));
        using var changed = new RdpTlsTrust(new string('0', 64));
        Assert.IsFalse(changed.Validate(this, certificate, null, SslPolicyErrors.None));
        var rejection = (ProtocolCertificateTrustException)changed.Rejection();
        Assert.AreEqual(pin, rejection.Thumbprint);
        StringAssert.Contains(rejection.PolicyErrors, "changed");
        CollectionAssert.AreEqual(certificate.GetPublicKey(), pinned.ServerPublicKey);
        Assert.IsFalse(pinned.Validate(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }
    [TestMethod]
    public void NativeErrorsAndCredentialStatus_AreNotConflated()
    {
        byte[] rejection = Status(0xc000006d);
        Assert.Throws<ProtocolAuthenticationException>(() => RdpSessionFactory.ThrowCredentialStatus(rejection));
        RdpSessionFactory.ThrowCredentialStatus(Status(0xc0000022)); // policy access denied is not necessarily bad credentials.
        Assert.Throws<ProtocolConnectionException>(() => RdpLimits.DesktopBytes(65535, 65535));
        Assert.AreEqual(1280 * 720 * 4, RdpLimits.DesktopBytes(1280, 720));
    }
    [TestMethod]
    public async Task LoopbackOpen_CancellationDuringX224_ClosesSocketAndPreservesToken()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var credentials = new RdpCredentials("unit-test", "not-a-secret", "");
        using var cancel = new CancellationTokenSource();
        Task<IRdpSession> connect = RdpSessionFactory.Instance.ConnectAsync(Options(port), credentials, cancel.Token);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        byte[] bytes = new byte[4096];
        Assert.IsGreaterThan(0, await peer.GetStream().ReadAsync(bytes, deadline.Token));
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<OperationCanceledException>(async () => { await connect; });
        Assert.AreEqual(cancel.Token, error.CancellationToken);
        int eof = await peer.GetStream().ReadAsync(bytes, deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, eof);
    }
    [TestMethod]
    public async Task LoopbackOpen_DeadlineIsNotAuthenticationFailure_ClosesSocket()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var credentials = new RdpCredentials("unit-test", "not-a-secret", "");
        Task<IRdpSession> connect = RdpSessionFactory.Instance.ConnectAsync(Options(port) with { ConnectTimeout = TimeSpan.FromSeconds(1) }, credentials);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        byte[] bytes = new byte[4096];
        Assert.IsGreaterThan(0, await peer.GetStream().ReadAsync(bytes, deadline.Token));
        await Assert.ThrowsAsync<TimeoutException>(async () => { await connect; });
        Assert.AreEqual(0, await peer.GetStream().ReadAsync(bytes, deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [TestMethod]
    public async Task LoopbackTls_UntrustedCertificateRequestsTrust_AndNoNlaIsSent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var certificate = Certificate("rdp-target.test");
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var credentials = new RdpCredentials("unit-test", "not-a-secret", "");
        Task<IRdpSession> connect = RdpSessionFactory.Instance.ConnectAsync(Options(port) with { TargetHost = "rdp-target.test", ConnectTimeout = TimeSpan.FromSeconds(8) }, credentials);
        using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
        byte[] negotiation = await RdpFraming.ReadRdpAsync(peer.GetStream(), deadline.Token);
        Assert.AreEqual(2, BitConverter.ToInt32(negotiation, negotiation.Length - 4));
        await peer.GetStream().WriteAsync(new byte[] { 3, 0, 0, 19, 14, 0xd0, 0, 0, 0, 0, 0, 2, 0, 8, 0, 2, 0, 0, 0 }, deadline.Token);
        using var tls = new SslStream(peer.GetStream(), false);
        bool authenticated = false;
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
            authenticated = true;
        }
        catch (System.Security.Authentication.AuthenticationException) { }
        var rejection = await Assert.ThrowsAsync<ProtocolCertificateTrustException>(async () => { await connect.WaitAsync(deadline.Token); });
        Assert.AreEqual(certificate.GetCertHashString(HashAlgorithmName.SHA256), rejection.Thumbprint);
        if (authenticated)
        {
            var application = new byte[4096];
            try { Assert.AreEqual(0, await tls.ReadAsync(application, deadline.Token)); }
            catch (IOException) { /* A TLS fatal alert/reset is also a close without NLA application data. */ }
        }
    }
    private static RdpConnectionOptions Options(int port) => new("127.0.0.1", port, "localhost", 3389, 320, 200, TimeSpan.FromSeconds(5), "");
    private static X509Certificate2 Certificate(string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName(host); request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel's server credential requires a persisted-key-capable certificate, not the RSA object's ephemeral handle.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
    private static byte[] Status(uint code)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence(); writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)); writer.WriteInteger(code);
        writer.PopSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)); writer.PopSequence(); return writer.Encode();
    }
    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], ct);
    }
}
