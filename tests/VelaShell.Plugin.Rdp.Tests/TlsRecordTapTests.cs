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
/// The TLS record framing seen under SslStream — what tells "the server aborted after our first NLA message"
/// apart from a local decryption problem when SChannel only says SEC_E_INTERNAL_ERROR.
/// </summary>
[TestClass]
public sealed class TlsRecordTapTests
{
    private static byte[] Record(byte type, int length) => [type, 3, 3, (byte)(length >> 8), (byte)length, .. new byte[length]];

    /// <summary>Reads <paramref name="handshake" /> through the tap, marks it complete, then reads the rest in small chunks.</summary>
    private static async Task<TlsRecordTap> FeedAsync(byte[] handshake, byte[] afterHandshake, int chunk)
    {
        var tap = new TlsRecordTap(new MemoryStream([.. handshake, .. afterHandshake]));
        byte[] buffer = new byte[chunk];
        for (int remaining = handshake.Length; remaining > 0;)
        {
            remaining -= await tap.ReadAsync(buffer.AsMemory(0, Math.Min(chunk, remaining)));
        }
        tap.MarkHandshakeComplete();
        while (await tap.ReadAsync(buffer) > 0)
        {
        }
        return tap;
    }

    [TestMethod]
    public async Task RecordsAfterTheHandshakeAreReportedWithTypeAndLength()
    {
        Assert.IsNull(new TlsRecordTap(new MemoryStream()).DescribeSinceHandshake(), "nothing to report before the handshake");
        byte[] handshake = [.. Record(22, 100), .. Record(20, 1), .. Record(22, 40)];
        TlsRecordTap tap = await FeedAsync(handshake, Record(21, 26), chunk: 3);
        Assert.AreEqual("Alert 26 B", tap.DescribeSinceHandshake());
    }

    [TestMethod]
    public async Task AnUnencryptedAlertIsDecoded()
    {
        TlsRecordTap tap = await FeedAsync(Record(22, 10), [0x15, 3, 3, 0, 2, 2, 80], chunk: 1);
        Assert.AreEqual("Alert fatal internal_error(80)", tap.DescribeSinceHandshake());
    }

    [TestMethod]
    public async Task OnlyTheLatestRecordsAreKept()
    {
        byte[] eight = [.. Enumerable.Range(0, 8).SelectMany(_ => Record(23, 10))];
        TlsRecordTap tap = await FeedAsync([], [.. eight, .. Record(21, 26)], chunk: 7);
        string described = tap.DescribeSinceHandshake()!;
        Assert.StartsWith("… 3 more, ", described);
        Assert.EndsWith("ApplicationData 10 B, Alert 26 B", described);
    }

    [TestMethod]
    public async Task NonTlsBytesAndSilenceAreReportedAsSuch()
    {
        TlsRecordTap garbage = await FeedAsync([], "HTTP/1.1 400"u8.ToArray(), chunk: 64);
        Assert.AreEqual("unparseable TLS framing", garbage.DescribeSinceHandshake());
        TlsRecordTap silent = await FeedAsync(Record(22, 10), [], chunk: 64);
        Assert.AreEqual("nothing", silent.DescribeSinceHandshake());
    }

    [TestMethod]
    public async Task StoppedTapsIgnoreLaterRecords()
    {
        var tap = new TlsRecordTap(new MemoryStream([.. Record(23, 5), .. Record(21, 26)]));
        tap.MarkHandshakeComplete();
        byte[] buffer = new byte[10];
        _ = await tap.ReadAsync(buffer);
        tap.Stop();
        while (await tap.ReadAsync(buffer) > 0)
        {
        }
        Assert.AreEqual("ApplicationData 5 B", tap.DescribeSinceHandshake());
    }

    /// <summary>
    /// The reported failure's shape, end to end: X.224 selects NLA, TLS 1.2 completes, the client sends its
    /// first TSRequest and the server aborts at the TLS layer instead of answering. The failure must name the
    /// stage, the TLS version and what the server sent.
    /// </summary>
    [TestMethod]
    public async Task AServerAbortAfterTheFirstNlaMessageIsReportedWithWhatTheServerSent()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=vela-fake-rdp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel cannot serve TLS from an ephemeral key; a PFX round trip gives it a usable one.
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream net = client.GetStream();
            byte[] tpkt = new byte[4];
            await net.ReadExactlyAsync(tpkt);
            await net.ReadExactlyAsync(new byte[((tpkt[2] << 8) | tpkt[3]) - 4]);
            // X.224 Connection Confirm with RDP_NEG_RSP selecting PROTOCOL_HYBRID.
            await net.WriteAsync(new byte[] { 3, 0, 0, 19, 14, 0xD0, 0, 0, 0, 0, 0, 0x02, 0, 8, 0, 2, 0, 0, 0 });
            using var ssl = new SslStream(net, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12
            });
            _ = await ssl.ReadAsync(new byte[4096]);     // the client's first TSRequest
            await net.WriteAsync(new byte[] { 0x15, 3, 3, 0, 2, 2, 80 });   // abort: fatal internal_error, in clear
            await net.FlushAsync();
            client.Client.Shutdown(SocketShutdown.Send);
            await Task.Delay(500);
        });

        var options = new RdpClientOptions
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "vela",
            Password = "not-sent",
            CertificatePin = SHA256.HashData(certificate.RawData)
        };
        RdpConnectionException failure;
        try
        {
            failure = await Assert.ThrowsAsync<RdpConnectionException>(() => RdpManagedSession.ConnectAsync(options, default));
        }
        finally
        {
            listener.Stop();
        }
        await server;
        Console.WriteLine($"Reported: {failure.Message}");

        Assert.AreEqual(RdpConnectStage.Nla, failure.Stage);
        Assert.AreEqual(SslProtocols.Tls12, failure.TlsProtocol);
        Assert.IsNotNull(failure.TlsTrace);
        Assert.EndsWith("server sent: Alert fatal internal_error(80)", failure.TlsTrace);
        Assert.Contains(failure.TlsTrace, failure.Message);
        Assert.DoesNotContain(failure.TlsTrace, failure.Detail);
    }
}
