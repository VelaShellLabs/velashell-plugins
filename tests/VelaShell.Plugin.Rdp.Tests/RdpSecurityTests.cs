using System.Net.Security;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The security-layer choice and what happens when a server refuses NLA before any credentials are sent —
/// the reported Windows server answers the first CredSSP message with errorCode 0x80090302 or a TLS abort.
/// </summary>
[TestClass]
public sealed class RdpSecurityTests
{
    private const uint NtlmRefused = 0x80090302;   // SEC_E_UNSUPPORTED_FUNCTION

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ClientInfoRequestsADesktopSessionWithoutAudio(bool autoLogon)
    {
        byte[] packet = VelaShell.Rdp.Pdu.ClientInfo.Build("", "vela", "password", autoLogon, 0x07, audioPlayback: false);
        uint flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));

        // MS-RDPBCGR TS_INFO_PACKET: RemoteApp with no application can end immediately after logon.
        Assert.AreEqual(0u, flags & 0x00008000u, "Desktop logons must not set INFO_RAIL.");
        Assert.AreNotEqual(0u, flags & 0x00080000u, "Audio playback must be disabled with INFO_NOAUDIOPLAYBACK.");
        Assert.AreEqual(autoLogon, (flags & 0x00000008u) != 0);
    }

    private static RdpClientOptions Options(FakeRdpServer server, RdpSecurity security) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Username = "vela",
        Password = "not-sent",
        CertificatePin = FakeRdpServer.Pin,
        Security = security
    };

    private static async Task<Exception> ConnectFailureAsync(RdpClientOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await using RdpManagedSession session = await RdpManagedSession.ConnectAsync(options, cts.Token);
        }
        catch (Exception ex)
        {
            return ex;
        }
        Assert.Fail("The connection unexpectedly succeeded.");
        return null!;
    }

    /// <summary>Selects NLA, completes TLS, then refuses the client's first TSRequest with an error code.</summary>
    private static async Task RefuseNtlmWithErrorCode(FakeRdpConnection connection)
    {
        await connection.SelectAsync(FakeRdpConnection.Hybrid);
        using SslStream ssl = await connection.StartTlsAsync();
        _ = await FakeRdpConnection.ReadDerAsync(ssl);   // the NTLM NEGOTIATE: no user name, no proof
        await FakeRdpConnection.SendErrorCodeAsync(ssl, NtlmRefused);
    }

    [TestMethod]
    public async Task AutoRetriesWithTlsOnlyWhenTheServerRefusesNlaUpFront()
    {
        await using var server = new FakeRdpServer(RefuseNtlmWithErrorCode, async connection =>
        {
            await connection.SelectAsync(FakeRdpConnection.Ssl);
            using SslStream ssl = await connection.StartTlsAsync();
            _ = await ssl.ReadAsync(new byte[4096]);   // MCS Connect Initial: the TLS-only attempt got past security
        });

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Auto));

        // The fake ends the second connection after TLS; reaching MCS proves the retry offered TLS only and used it.
        var later = Assert.IsInstanceOfType<RdpConnectionException>(failure);
        Assert.AreEqual(RdpConnectStage.Mcs, later.Stage);
        CollectionAssert.AreEqual(new uint[] { 3, 1 }, server.RequestedProtocols.ToArray());
    }

    [TestMethod]
    public async Task AutoExplainsAServerThatRequiresNlaButRefusesNtlm()
    {
        await using var server = new FakeRdpServer(RefuseNtlmWithErrorCode, connection => connection.RefuseAsync(5));

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Auto));

        var refused = Assert.IsInstanceOfType<RdpNlaRefusedException>(failure);
        Assert.IsTrue(refused.ServerRequiresNla);
        Assert.AreEqual(NtlmRefused, refused.Status);
        CollectionAssert.AreEqual(new uint[] { 3, 1 }, server.RequestedProtocols.ToArray());

        // A server policy, not a credentials problem: shown as a connection error with the server-side fix.
        Exception shown = RdpConnector.Translate(refused, new Loc("zh-Hans"));
        var connectionError = Assert.IsInstanceOfType<ProtocolConnectionException>(shown);
        Assert.Contains("与用户名和密码无关", connectionError.Message);
        Assert.Contains("0x80090302", connectionError.Message);
        Assert.Contains("限制 NTLM: 传入 NTLM 流量", connectionError.Message);
    }

    [TestMethod]
    public async Task NlaOnlyNeverFallsBackAndReportsAnAbortedFirstReply()
    {
        await using var server = new FakeRdpServer(async connection =>
        {
            await connection.SelectAsync(FakeRdpConnection.Hybrid);
            using SslStream ssl = await connection.StartTlsAsync();
            _ = await FakeRdpConnection.ReadDerAsync(ssl);
            await connection.Net.WriteAsync(new byte[] { 0x15, 3, 3, 0, 2, 2, 80 });   // abort: fatal internal_error, in clear
            await connection.Net.FlushAsync();
            await Task.Delay(300);
        });

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Nla));

        var refused = Assert.IsInstanceOfType<RdpNlaRefusedException>(failure);
        Assert.IsNull(refused.Status);
        Assert.IsFalse(refused.ServerRequiresNla);
        Assert.IsNotNull(refused.TlsTrace);
        Assert.EndsWith("server sent: Alert fatal internal_error(80)", refused.TlsTrace);
        Assert.Contains(refused.TlsTrace, refused.Message);
        CollectionAssert.AreEqual(new uint[] { 3 }, server.RequestedProtocols.ToArray());
        Assert.Contains("Set the security layer to Auto or TLS only", RdpConnector.Translate(refused, new Loc("en")).Message);
    }

    [TestMethod]
    public async Task NlaOnlyRejectsAServerThatSelectsTls()
    {
        await using var server = new FakeRdpServer(connection => connection.SelectAsync(FakeRdpConnection.Ssl));

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Nla));

        var failed = Assert.IsInstanceOfType<RdpConnectionException>(failure);
        Assert.AreEqual(RdpConnectStage.Negotiation, failed.Stage);
        Assert.Contains("did not select NLA", failed.Detail);
    }

    [TestMethod]
    public async Task TlsOnlyOffersOnlyTlsAndNamesAServerThatRequiresNla()
    {
        await using var server = new FakeRdpServer(connection => connection.RefuseAsync(5));

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Tls));

        var failed = Assert.IsInstanceOfType<RdpConnectionException>(failure);
        Assert.AreEqual(RdpNegotiationFailure.HybridRequiredByServer, failed.NegotiationFailure);
        CollectionAssert.AreEqual(new uint[] { 1 }, server.RequestedProtocols.ToArray());
        var loc = new Loc("en");
        Assert.AreEqual(loc["NegotiationNlaRequired"], RdpConnector.Translate(failed, loc).Message);
    }

    [TestMethod]
    [DataRow("", RdpSecurity.Auto)]
    [DataRow("auto", RdpSecurity.Auto)]
    [DataRow("NLA", RdpSecurity.Nla)]
    [DataRow(" tls ", RdpSecurity.Tls)]
    public void SecurityLayerSettingIsParsed(string value, RdpSecurity expected)
    {
        RdpSettings settings = RdpSettings.From(RdpSettingsTests.Request(("security", value)), new Loc("en"), []);
        Assert.AreEqual(expected, settings.Security);
    }

    [TestMethod]
    public void UnknownSecurityLayerIsRejected()
    {
        _ = Assert.Throws<ProtocolConnectionException>(() => RdpSettings.From(RdpSettingsTests.Request(("security", "rdp")), new Loc("en"), []));
    }

    [TestMethod]
    public void SecurityLayerIsAnAdvancedChoiceDefaultingToAuto()
    {
        ProtocolSettingField field = RdpSettings.Declare(new Loc("en"), []).Single(f => f.Key == "security");
        Assert.AreEqual(ProtocolSettingKind.Choice, field.Kind);
        Assert.AreEqual("auto", field.DefaultValue);
        Assert.IsTrue(field.IsAdvanced);
        CollectionAssert.AreEqual(new[] { "auto", "nla", "tls" }, field.Choices!.Select(c => c.Value).ToArray());
    }
}
