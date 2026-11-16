using System.Net.Security;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.Vnc;
using VelaShell.Vnc.Security;
using static VelaShell.Plugin.Vnc.Tests.ServerConnection;

namespace VelaShell.Plugin.Vnc.Tests;

[TestClass]
public sealed class VncHandshakeTests
{
    internal static VncClientOptions Options(int port, string password = "", VncSecurity security = VncSecurity.Auto, string username = "",
        byte[]? pin = null, bool audio = false, bool fileTransfer = false, Func<VelaShell.Vnc.Audio.IAudioOutput>? audioOutput = null,
        bool continuousUpdates = true) => new()
        {
            ContinuousUpdates = continuousUpdates,
            AudioPlayback = audio,
            FileTransfer = fileTransfer,
            AudioOutputFactory = audioOutput,
            Host = "127.0.0.1",
            Port = port,
            Password = password,
            Username = username,
            Security = security,
            CertificatePin = pin,
            CertificateHost = "vnc.test",
            JpegDecoder = null
        };

    internal static async Task<VncSession> ConnectAsync(VncClientOptions options) =>
        await VncSession.ConnectAsync(options, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);

    [TestMethod]
    public async Task NoneOn38NegotiatesAndAsksForTheDesktop()
    {
        int[] encodings = [];
        bool shared = false;
        var initialized = new TaskCompletionSource();
        await using var server = new FakeVncServer(async (c, index) =>
        {
            Assert.AreEqual("RFB 003.008\n", await c.VersionAsync());
            Assert.AreEqual(1, await c.OfferAsync(1));
            await c.SecurityResultAsync();
            (shared, encodings) = await c.InitAsync(800, 600, "büro");
            initialized.SetResult();
            await c.HoldAsync();
        });
        await using VncSession session = await ConnectAsync(Options(server.Port));
        // The client returns once its init messages are flushed; wait for the server to have read them.
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(session.IsConnected);
        Assert.AreEqual("3.8", session.Info.ProtocolVersion);
        Assert.AreEqual("None", session.Info.Security);
        Assert.AreEqual("büro", session.Info.DesktopName);
        Assert.AreEqual(800, session.DesktopWidth);
        Assert.IsTrue(shared);
        // Preferred encodings and the pseudo-encodings the session relies on (JPEG only with a decoder).
        Assert.AreEqual(1, encodings[0], "CopyRect first");
        Assert.AreEqual(7, encodings[1], "Tight preferred");
        Assert.Contains(-308, encodings);
        Assert.Contains(-239, encodings);
        Assert.Contains(-258, encodings);
        Assert.Contains(unchecked((int)0xC0A1E5CE), encodings);
        Assert.DoesNotContain(-24, encodings);
    }

    [TestMethod]
    public async Task VncAuthenticationSendsTheDesResponse()
    {
        byte[] challenge = RandomNumberGenerator.GetBytes(16);
        byte[]? response = null;
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            Assert.AreEqual(2, await c.OfferAsync(16, 2));   // Tight (16) is not supported; VNC auth is
            await c.WriteAsync(challenge);
            response = await c.ReadAsync(16);
            await c.SecurityResultAsync();
            await c.InitAsync();
            await c.HoldAsync();
        });
        await using VncSession session = await ConnectAsync(Options(server.Port, "secret"));
        Assert.AreEqual(Convert.ToHexString(VncAuthentication.Respond(challenge, "secret")), Convert.ToHexString(response!));
        Assert.AreEqual("VNC", session.Info.Security);
    }

    [TestMethod]
    public async Task ARejectedPasswordCarriesTheServersReason()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.OfferAsync(2);
            await c.WriteAsync(new byte[16]);
            _ = await c.ReadAsync(16);
            await c.SecurityResultAsync(1, "Authentication failure");
        });
        VncAuthenticationException ex = await Assert.ThrowsExactlyAsync<VncAuthenticationException>(() => ConnectAsync(Options(server.Port, "wrong")));
        Assert.AreEqual("Authentication failure", ex.ServerReason);
        Assert.IsFalse(ex.CredentialsMissing);
    }

    [TestMethod]
    public async Task AMissingPasswordIsReportedBeforeAnythingIsSent()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.OfferAsync(2);
            await c.WriteAsync(new byte[16]);
        });
        VncAuthenticationException ex = await Assert.ThrowsExactlyAsync<VncAuthenticationException>(() => ConnectAsync(Options(server.Port)));
        Assert.IsTrue(ex.CredentialsMissing);
        Assert.IsFalse(ex.UsernameRequired);
    }

    [TestMethod]
    [DataRow("RFB 003.003\n", "RFB 003.003\n")]
    [DataRow("RFB 003.005\n", "RFB 003.003\n")]
    [DataRow("RFB 003.007\n", "RFB 003.007\n")]
    [DataRow("RFB 003.889\n", "RFB 003.008\n")]   // Apple
    [DataRow("RFB 004.001\n", "RFB 003.008\n")]
    public async Task ProtocolVersionIsAnsweredWithTheHighestCommonOne(string offered, string expected)
    {
        string? answered = null;
        await using var server = new FakeVncServer(async (c, index) =>
        {
            answered = await c.VersionAsync(offered);
            if (answered == "RFB 003.003\n")
            {
                await c.WriteAsync(U32(1));   // 3.3: the server decides; None has no SecurityResult
            }
            else
            {
                await c.OfferAsync(1);
                if (answered == "RFB 003.008\n") { await c.SecurityResultAsync(); }
            }
            await c.InitAsync();
            await c.HoldAsync();
        });
        await using VncSession session = await ConnectAsync(Options(server.Port));
        Assert.AreEqual(expected, answered);
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    public async Task ANonVncServerFailsAtTheVersionStage()
    {
        await using var server = new FakeVncServer(async (c, index) => await c.WriteAsync("SSH-2.0-Open"u8.ToArray()));
        VncConnectionException ex = await Assert.ThrowsExactlyAsync<VncConnectionException>(() => ConnectAsync(Options(server.Port)));
        Assert.AreEqual(VncConnectStage.ProtocolVersion, ex.Stage);
    }

    [TestMethod]
    public async Task AServerRefusalBeforeSecurityCarriesItsReason()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.WriteAsync([0], Str("Too many security failures"));
        });
        VncConnectionException ex = await Assert.ThrowsExactlyAsync<VncConnectionException>(() => ConnectAsync(Options(server.Port)));
        Assert.AreEqual(VncConnectStage.Security, ex.Stage);
        Assert.AreEqual("Too many security failures", ex.Detail);
    }

    [TestMethod]
    public async Task UnsupportedSecurityTypesAreListed()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.WriteAsync([2, 6, 113]);
            await c.HoldAsync();
        });
        VncSecurityUnsupportedException ex = await Assert.ThrowsExactlyAsync<VncSecurityUnsupportedException>(() => ConnectAsync(Options(server.Port)));
        Assert.AreSequenceEqual([6, 113], ex.Offered.ToArray());
        StringAssert.Contains(ex.Message, "RA2 (6)");
    }

    [TestMethod]
    public async Task AnExplicitSecurityTypeTheServerLacksIsNotSubstituted()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.WriteAsync([2, 1, 2]);
            await c.HoldAsync();
        });
        _ = await Assert.ThrowsExactlyAsync<VncSecurityUnsupportedException>(() => ConnectAsync(Options(server.Port, "x", VncSecurity.VeNCrypt)));
    }

    [TestMethod]
    public async Task AppleRemoteDesktopSendsEncryptedCredentials()
    {
        var p = new BigInteger(VncSecurityTests.OakleyPrime, isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true);
        (string User, string Password) received = default;
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync("RFB 003.889\n");
            Assert.AreEqual(30, await c.OfferAsync(30, 2));   // with a user name, ARD wins over VNC authentication
            byte[] pub = BigInteger.ModPow(2, y, p).ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] padded = new byte[128];
            pub.CopyTo(padded, 128 - pub.Length);
            await c.WriteAsync(U16(2), U16(128), VncSecurityTests.OakleyPrime, padded);
            byte[] response = await c.ReadAsync(256);
            received = VncSecurityTests.ArdServerDecrypt(response, p, y, 128);
            await c.SecurityResultAsync();
            await c.InitAsync();
            await c.HoldAsync();
        });
        await using VncSession session = await ConnectAsync(Options(server.Port, "pa55", username: "admin"));
        Assert.AreEqual(("admin", "pa55"), received);
        Assert.AreEqual("Apple Remote Desktop", session.Info.Security);
    }

    [TestMethod]
    public async Task AppleRemoteDesktopWithoutAUserNameAsksForOne()
    {
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await c.VersionAsync();
            await c.OfferAsync(30);
            await c.HoldAsync();
        });
        VncAuthenticationException ex = await Assert.ThrowsExactlyAsync<VncAuthenticationException>(() => ConnectAsync(Options(server.Port, "pw")));
        Assert.IsTrue(ex.CredentialsMissing);
        Assert.IsTrue(ex.UsernameRequired);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=vnc.test", key, HashAlgorithmName.SHA256);
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // SChannel needs a persisted-style key: round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    /// <summary>The server side of VeNCrypt 0.2 up to and including the TLS handshake.</summary>
    private static async Task VeNCryptServerAsync(ServerConnection c, X509Certificate2 certificate, uint subtype)
    {
        await c.VersionAsync();
        Assert.AreEqual(19, await c.OfferAsync(19, 2));
        await c.WriteAsync([0, 2]);
        byte[] version = await c.ReadAsync(2);
        Assert.AreSequenceEqual(new byte[] { 0, 2 }, version);
        await c.WriteAsync([0, 1], U32(subtype));
        Assert.AreEqual(subtype, await c.U32Async());
        await c.WriteAsync([1]);
        var tls = new SslStream(c.Stream);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate });
        c.Stream = tls;
    }

    [TestMethod]
    public async Task VeNCryptTlsAsksToTrustASelfSignedCertificateThenConnectsWithThePin()
    {
        using X509Certificate2 certificate = SelfSigned();
        await using var server = new FakeVncServer(async (c, attempt) =>
        {
            try { await VeNCryptServerAsync(c, certificate, 261); }
            catch (Exception) when (attempt == 0) { return; }   // the client rejects the certificate the first time
            byte[] challenge = RandomNumberGenerator.GetBytes(16);
            await c.WriteAsync(challenge);
            byte[] response = await c.ReadAsync(16);
            Assert.AreEqual(Convert.ToHexString(VncAuthentication.Respond(challenge, "pw")), Convert.ToHexString(response));
            await c.SecurityResultAsync();
            await c.InitAsync();
            await c.HoldAsync();
        });

        VncCertificateRejectedException rejected = await Assert.ThrowsExactlyAsync<VncCertificateRejectedException>(() => ConnectAsync(Options(server.Port, "pw")));
        Assert.IsFalse(rejected.PinMismatch);
        using var offered = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(rejected.Pem));
        Assert.AreEqual(certificate.Thumbprint, offered.Thumbprint);

        await using VncSession session = await ConnectAsync(Options(server.Port, "pw", pin: SHA256.HashData(certificate.RawData)));
        Assert.IsTrue(session.Info.Tls);
        Assert.AreEqual("VeNCrypt (VNC)", session.Info.Security);
    }

    [TestMethod]
    public async Task VeNCryptPlainSendsTheUserNameAndPasswordInsideTls()
    {
        using X509Certificate2 certificate = SelfSigned();
        (string, string) received = default;
        await using var server = new FakeVncServer(async (c, index) =>
        {
            await VeNCryptServerAsync(c, certificate, 262);
            int userLength = (int)await c.U32Async(), passwordLength = (int)await c.U32Async();
            received = (Encoding.UTF8.GetString(await c.ReadAsync(userLength)), Encoding.UTF8.GetString(await c.ReadAsync(passwordLength)));
            await c.SecurityResultAsync();
            await c.InitAsync();
            await c.HoldAsync();
        });
        await using VncSession session = await ConnectAsync(Options(server.Port, "Pässwort", username: "ops", pin: SHA256.HashData(certificate.RawData)));
        Assert.AreEqual(("ops", "Pässwort"), received);
    }

    [TestMethod]
    public async Task AChangedCertificateIsReportedAsAPinMismatch()
    {
        using X509Certificate2 certificate = SelfSigned();
        await using var server = new FakeVncServer(async (c, index) =>
        {
            try { await VeNCryptServerAsync(c, certificate, 260); } catch (Exception) { }
        });
        VncCertificateRejectedException ex = await Assert.ThrowsExactlyAsync<VncCertificateRejectedException>(
            () => ConnectAsync(Options(server.Port, pin: new byte[32])));
        Assert.IsTrue(ex.PinMismatch);
    }

    [TestMethod]
    public async Task AutoFallsBackFromAnonymousOnlyVeNCryptToVncAuthentication()
    {
        int[] chosen = [0, 0];
        await using var server = new FakeVncServer(async (c, attempt) =>
        {
            await c.VersionAsync();
            chosen[attempt] = await c.OfferAsync(19, 2);
            if (attempt == 0)
            {
                await c.WriteAsync([0, 2]);
                _ = await c.ReadAsync(2);
                await c.WriteAsync([0, 2], U32(257), U32(258));   // anonymous TLS only
                await c.HoldAsync();
                return;
            }
            await c.WriteAsync(new byte[16]);
            _ = await c.ReadAsync(16);
            await c.SecurityResultAsync();
            await c.InitAsync();
            await c.HoldAsync();
        });
        await using VncSession session = await ConnectAsync(Options(server.Port, "pw"));
        Assert.AreSequenceEqual([19, 2], chosen);
        Assert.AreEqual("VNC", session.Info.Security);
    }

    [TestMethod]
    public async Task ADialFailureIsATransportError()
    {
        var options = new VncClientOptions { Host = "127.0.0.1", Port = 1, Dialer = (_, _, _) => throw new IOException("refused") };
        VncConnectionException ex = await Assert.ThrowsExactlyAsync<VncConnectionException>(() => ConnectAsync(options));
        Assert.AreEqual(VncConnectStage.Transport, ex.Stage);
    }
}
