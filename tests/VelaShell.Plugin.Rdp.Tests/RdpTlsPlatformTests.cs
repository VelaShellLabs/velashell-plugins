using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VelaShell.Rdp;
using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The TLS policy is one rule on every OS (no per-platform protocol caps or cipher lists); a TLS 1.3 failure during
/// NLA is retried once with TLS 1.2; certificate rejection is reported only when the certificate was refused.
/// CI runs this class on Windows, Linux and macOS.
/// </summary>
[TestClass]
public sealed class RdpTlsPlatformTests
{
    [TestMethod]
    public void EveryPlatformOffersTls12And13WithTheDefaultCipherSuites()
    {
        SslClientAuthenticationOptions options = RdpTls.CreateClientAuthenticationOptions("rdp.example", pinned: false);
        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, options.EnabledSslProtocols);
        Assert.IsNull(options.CipherSuitesPolicy, "No platform restricts cipher suites.");
        Assert.IsFalse(options.AllowRenegotiation);
    }

    // ---- Classifying failures ----

    private static IOException TlsFailure() =>
        new("The decryption operation failed, see inner exception.", new Win32Exception(unchecked((int)0x80090326)));

    private static IOException SocketFailure() =>
        new("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionReset));

    [TestMethod]
    public void TlsLayerFailuresAreToldApartFromAClosedConnection()
    {
        Assert.IsTrue(CredSsp.IsTlsLayerFailure(TlsFailure()));
        Assert.IsTrue(CredSsp.IsTlsLayerFailure(new AuthenticationException("handshake")));
        Assert.IsFalse(CredSsp.IsTlsLayerFailure(SocketFailure()), "A reset is a transport failure.");
        Assert.IsFalse(CredSsp.IsTlsLayerFailure(new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        Assert.IsFalse(CredSsp.IsTlsLayerFailure(new RdpProtocolException(CredSsp.ClosedDuringNla)));

        // Every way of losing the connection during NLA counts for the TLS 1.3 retry; a protocol verdict does not.
        Assert.IsTrue(CredSsp.IsConnectionLossDuringNla(TlsFailure()));
        Assert.IsTrue(CredSsp.IsConnectionLossDuringNla(SocketFailure()));
        Assert.IsTrue(CredSsp.IsConnectionLossDuringNla(new IOException("Received an unexpected EOF or 0 bytes from the transport stream.")));
        Assert.IsTrue(CredSsp.IsConnectionLossDuringNla(new RdpProtocolException(CredSsp.ClosedDuringNla)));
        Assert.IsFalse(CredSsp.IsConnectionLossDuringNla(new RdpProtocolException("The server speaks CredSSP v3, which predates the CVE-2018-0886 fix.")));
    }

    [TestMethod]
    public void OnlyATls13FailureDuringNlaIsRetriedWithTls12()
    {
        Assert.IsTrue(RdpManagedSession.IsTls13FailureDuringNla(new RdpConnectionException(RdpConnectStage.Nla, TlsFailure(), SslProtocols.Tls13)));
        Assert.IsFalse(RdpManagedSession.IsTls13FailureDuringNla(new RdpConnectionException(RdpConnectStage.Nla, TlsFailure(), SslProtocols.Tls12)),
            "TLS 1.2 has nothing to fall back to.");
        Assert.IsTrue(RdpManagedSession.IsTls13FailureDuringNla(new RdpConnectionException(RdpConnectStage.Nla, SocketFailure(), SslProtocols.Tls13)),
            "Some TLS stacks report a TLS 1.3 abort as a reset or a bare end of stream.");
        Assert.IsTrue(RdpManagedSession.IsTls13FailureDuringNla(new RdpConnectionException(RdpConnectStage.Nla,
            new RdpProtocolException(CredSsp.ClosedDuringNla), SslProtocols.Tls13)));
        Assert.IsFalse(RdpManagedSession.IsTls13FailureDuringNla(new RdpConnectionException(RdpConnectStage.Nla,
            new RdpProtocolException("NLA public-key echo missing."), SslProtocols.Tls13)), "A protocol verdict is not retried.");
        Assert.IsFalse(RdpManagedSession.IsTls13FailureDuringNla(new RdpConnectionException(RdpConnectStage.Mcs, TlsFailure(), SslProtocols.Tls13)),
            "After NLA the credentials are in use; no silent retry.");

        // A refusal by TLS abort under TLS 1.3 is retried with TLS 1.2 before the TLS-only fallback; a refusal the
        // server stated with an error code, or one on TLS 1.2, is final.
        Assert.IsTrue(RdpManagedSession.IsTls13FailureDuringNla(new RdpNlaRefusedException(null, TlsFailure()).WithTls(null, SslProtocols.Tls13)));
        Assert.IsFalse(RdpManagedSession.IsTls13FailureDuringNla(new RdpNlaRefusedException(null, TlsFailure()).WithTls(null, SslProtocols.Tls12)));
        Assert.IsFalse(RdpManagedSession.IsTls13FailureDuringNla(new RdpNlaRefusedException(0x80090302, null).WithTls(null, SslProtocols.Tls13)));
        Assert.IsTrue(RdpManagedSession.IsTls13FailureDuringNla(new RdpNlaRefusedException(null, SocketFailure()).WithTls(null, SslProtocols.Tls13)));
    }

    [TestMethod]
    public async Task ATls13RecordFailureDuringNlaIsRetriedOnceWithTls12()
    {
        SslProtocols first = SslProtocols.None, second = SslProtocols.None;
        using X509Certificate2 certificate = FakeRdpServer.CreateCertificate("vela-tls13");
        var server = new FakeRdpServer(certificate,
            async connection =>
            {
                await connection.SelectAsync(FakeRdpConnection.Hybrid);
                await using SslStream ssl = await connection.StartTlsAsync(SslProtocols.Tls12 | SslProtocols.Tls13);
                first = ssl.SslProtocol;
                _ = await FakeRdpConnection.ReadDerAsync(ssl);
                // An application-data record that cannot decrypt: the client's TLS layer fails, as when a server
                // aborts TLS 1.3 during CredSSP.
                byte[] bogus = [0x17, 0x03, 0x03, 0x00, 0x20, .. RandomNumberGenerator.GetBytes(0x20)];
                await connection.Net.WriteAsync(bogus);
                await Task.Delay(500);
            },
            async connection =>
            {
                await connection.SelectAsync(FakeRdpConnection.Hybrid);
                await using SslStream ssl = await connection.StartTlsAsync(SslProtocols.Tls12 | SslProtocols.Tls13);
                second = ssl.SslProtocol;
                _ = await FakeRdpConnection.ReadDerAsync(ssl);
                await FakeRdpConnection.SendErrorCodeAsync(ssl, 0xC000006D);
                await Task.Delay(500);
            });
        RdpManagedSession.ForgetTlsFallbacksForTest();

        Exception failure = await ConnectFailureAsync(server);
        int attempts = server.RequestedProtocols.Count;
        // With a single attempt the second script never runs, and stopping the listener then faults the harness.
        try { await server.DisposeAsync(); }
        catch (InvalidOperationException) when (attempts < 2) { }

        if (first != SslProtocols.Tls13)
        {
            Assert.Inconclusive($"This platform's TLS stack negotiated {first} with itself; TLS 1.3 is needed for this test.");
        }
        // The retry reached NLA and got the server's verdict (which exception carries it depends on whether the local
        // package's first token was plain NTLM, see CredSsp.IsNtlmNegotiate).
        uint? status = failure switch
        {
            RdpAuthenticationException auth => auth.Status,
            RdpNlaRefusedException refused => refused.Status,
            _ => null
        };
        Assert.AreEqual(0xC000006Du, status, failure.ToString());
        Assert.AreEqual(2, attempts, "One TLS 1.3 attempt, one TLS 1.2 retry. First failure: " + failure);
        Assert.AreEqual(SslProtocols.Tls12, second, "The retry offers TLS 1.2 only.");
    }

    private static async Task<Exception> ConnectFailureAsync(FakeRdpServer server)
    {
        byte[] pin = server.OwnPin;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var options = new RdpClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            Username = "vela",
            Password = "not-sent",
            CertificatePin = pin,
            // A TLS target name no other test uses: the platform TLS client resumes sessions per target, and a resumed
            // TLS 1.2 session from another test would hide TLS 1.3. The pin makes the name irrelevant to validation.
            CertificateHost = $"vela-{Guid.NewGuid():N}.test",
            Security = RdpSecurity.Nla
        };
        try
        {
            await using RdpManagedSession session = await RdpManagedSession.ConnectAsync(options, cts.Token);
        }
        catch (Exception ex) { return ex; }
        throw new AssertFailedException("The connection should have failed.");
    }

    // ---- Certificates ----

    private static X509Certificate2 SelfSigned(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static async Task<Exception?> HandshakeAsync(X509Certificate2 serverCertificate, byte[]? pin)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serve = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            await using var ssl = new SslStream(accepted.GetStream());
            try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCertificate }); }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        });

        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        Exception? failure = null;
        try
        {
            await using SslStream ssl = await RdpTls.AuthenticateAsync(client.GetStream(), "localhost", pin, cts.Token);
        }
        catch (Exception ex) { failure = ex; }
        await serve.WaitAsync(TimeSpan.FromSeconds(15));
        return failure;
    }

    [TestMethod]
    public async Task APinMatchConnectsAndAMismatchIsReportedAsAChangedCertificate()
    {
        using X509Certificate2 current = SelfSigned("current");
        using X509Certificate2 previous = SelfSigned("previous");

        Assert.IsNull(await HandshakeAsync(current, SHA256.HashData(current.RawData)));

        Exception? changed = await HandshakeAsync(current, SHA256.HashData(previous.RawData));
        Assert.IsInstanceOfType<RdpCertificateRejectedException>(changed);
        var rejected = (RdpCertificateRejectedException)changed!;
        Assert.IsTrue(rejected.PinMismatch);
        string pem = System.Text.Encoding.UTF8.GetString(rejected.Pem);
        using X509Certificate2 offered = X509Certificate2.CreateFromPem(pem);
        Assert.AreEqual(current.Thumbprint, offered.Thumbprint, "The new certificate is the one offered for approval.");
    }

    [TestMethod]
    public void TheHostIsToldWhenAPinnedCertificateChanged()
    {
        using X509Certificate2 certificate = SelfSigned("renewed");
        byte[] pem = System.Text.Encoding.UTF8.GetBytes(certificate.ExportCertificatePem());
        var loc = new Loc("en");

        var changed = (VelaShell.PluginSdk.Protocols.ProtocolCertificateTrustException)RdpConnector.Translate(
            new RdpCertificateRejectedException(pem, new AuthenticationException("pin"), pinMismatch: true), loc);
        Assert.AreEqual(loc["CertificateChanged"], changed.Message);
        Assert.AreEqual(certificate.GetCertHashString(HashAlgorithmName.SHA256), changed.Thumbprint);

        var first = (VelaShell.PluginSdk.Protocols.ProtocolCertificateTrustException)RdpConnector.Translate(
            new RdpCertificateRejectedException(pem, new AuthenticationException("untrusted")), loc);
        Assert.AreEqual(loc["Certificate"], first.Message);
    }

    [TestMethod]
    public async Task AnUntrustedCertificateWithoutAPinIsAFirstTimeApproval()
    {
        using X509Certificate2 current = SelfSigned("untrusted");
        Exception? failure = await HandshakeAsync(current, pin: null);
        Assert.IsInstanceOfType<RdpCertificateRejectedException>(failure);
        Assert.IsFalse(((RdpCertificateRejectedException)failure!).PinMismatch);
    }
}
