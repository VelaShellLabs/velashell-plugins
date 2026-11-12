using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>Following an RD Connection Broker's Server Redirection PDU (MS-RDPBCGR 3.1.5.2.1, 2.2.13).</summary>
[TestClass]
public sealed class RdpRedirectionTests
{
    private static readonly byte[] Token = Encoding.ASCII.GetBytes("Cookie: msts=3640205228.15629.0000\r\n");

    /// <summary>The redirection packet that follows the share control header (pad, securityFlags, length, body).</summary>
    private static byte[] RedirectionPacket(uint sessionId, uint flags, string? fqdn = null, string? address = null, byte[]? loadBalance = null)
    {
        var body = new ByteWriter(256);
        body.U32LE(sessionId);
        body.U32LE(flags);
        if (address is not null) { Unicode(body, address); }
        if (loadBalance is not null) { body.U32LE((uint)loadBalance.Length); body.Bytes(loadBalance); }
        if (fqdn is not null) { Unicode(body, fqdn); }
        byte[] fields = body.ToArray();
        var w = new ByteWriter(fields.Length + 8);
        w.U16LE(0);                              // pad2Octets
        w.U16LE(0x0400);                         // SEC_REDIRECTION_PKT
        w.U16LE((ushort)(fields.Length + 4));    // length, from securityFlags
        w.Bytes(fields);
        return w.ToArray();

        static void Unicode(ByteWriter w, string value)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
            w.U32LE((uint)bytes.Length);
            w.Bytes(bytes);
        }
    }

    private static ServerRedirection Parse(byte[] packet)
    {
        var r = new ByteReader(packet);
        return ServerRedirectionParser.Parse(ref r);
    }

    private static RdpClientOptions Options(bool tunnel = false) => new()
    {
        Host = "broker.example",
        Port = 3389,
        CertificatePin = new byte[32],
        TcpTunnel = tunnel
    };

    [TestMethod]
    public void TheLoadBalanceInfoIsKeptForTheNextConnection()
    {
        ServerRedirection redirection = Parse(RedirectionPacket(0x2A, RedirectionFlags.TargetFqdn | RedirectionFlags.LoadBalanceInfo,
            fqdn: "host7.example", loadBalance: Token));
        Assert.AreEqual("host7.example", redirection.TargetFqdn);
        CollectionAssert.AreEqual(Token, redirection.LoadBalanceInfo);
        Assert.AreEqual(Token.Length, redirection.LoadBalanceInfoLength);
    }

    [TestMethod]
    public void TheRoutingTokenReplacesTheCookieInTheConnectionRequest()
    {
        byte[] request = RdpTransport.BuildConnectionRequest("alice", SecurityProtocol.Ssl, Token);
        string text = Encoding.ASCII.GetString(request);
        Assert.Contains("Cookie: msts=3640205228.15629.0000\r\n", text);
        Assert.DoesNotContain("mstshash", text);

        // A token without its terminator gets one; one with CR/LF inside cannot be sent.
        string unterminated = Encoding.ASCII.GetString(RdpTransport.BuildConnectionRequest("alice", SecurityProtocol.Ssl, "tsv://Plugin.1.Pool"u8.ToArray()));
        Assert.Contains("tsv://Plugin.1.Pool\r\n", unterminated);
        _ = Assert.Throws<RdpProtocolException>(() => RdpTransport.BuildConnectionRequest("alice", SecurityProtocol.Ssl, "a\r\nb"u8.ToArray()));

        Assert.Contains("Cookie: mstshash=alice", Encoding.ASCII.GetString(RdpTransport.BuildConnectionRequest("alice", SecurityProtocol.Ssl)));
    }

    [TestMethod]
    public void TheClusterDataNamesTheRedirectedSession()
    {
        byte[] plain = GccData.BuildCluster(1);
        Assert.AreEqual(0x0Du, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(4)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(8)));

        byte[] redirected = GccData.BuildCluster(1, 0x2A);
        Assert.AreEqual(0x0Fu, BinaryPrimitives.ReadUInt32LittleEndian(redirected.AsSpan(4)), "REDIRECTED_SESSIONID_FIELD_VALID");
        Assert.AreEqual(0x2Au, BinaryPrimitives.ReadUInt32LittleEndian(redirected.AsSpan(8)));
    }

    [TestMethod]
    public void ADifferentTargetIsConnectedWithoutTheBrokersPin()
    {
        ServerRedirection redirection = Parse(RedirectionPacket(0x2A, RedirectionFlags.TargetFqdn | RedirectionFlags.TargetNetAddress | RedirectionFlags.LoadBalanceInfo,
            fqdn: "host7.example", address: "10.0.0.7", loadBalance: Token));
        RdpClientOptions next = RdpManagedSession.Redirect(Options(), redirection)!;
        Assert.AreEqual("host7.example", next.Host, "The FQDN is preferred: it matches the certificate and the Kerberos SPN.");
        Assert.AreEqual("host7.example", next.CertificateHost);
        Assert.IsNull(next.CertificatePin, "A pin belongs to the broker's certificate, not the target's.");
        CollectionAssert.AreEqual(Token, next.RoutingToken);
        Assert.AreEqual(0x2Au, next.RedirectedSessionId);
        Assert.AreEqual(3389, next.Port);

        ServerRedirection addressOnly = Parse(RedirectionPacket(1, RedirectionFlags.TargetNetAddress, address: "10.0.0.7"));
        Assert.AreEqual("10.0.0.7", RdpManagedSession.Redirect(Options(), addressOnly)!.Host);
    }

    [TestMethod]
    public void NoRedirectAsksTheSameServerAgainWithTheRoutingToken()
    {
        ServerRedirection redirection = Parse(RedirectionPacket(5, RedirectionFlags.NoRedirect | RedirectionFlags.TargetFqdn | RedirectionFlags.LoadBalanceInfo,
            fqdn: "host7.example", loadBalance: Token));
        RdpClientOptions next = RdpManagedSession.Redirect(Options(), redirection)!;
        Assert.AreEqual("broker.example", next.Host);
        Assert.IsNotNull(next.CertificatePin, "Same host, same certificate pin.");
        CollectionAssert.AreEqual(Token, next.RoutingToken);

        // Through an SSH tunnel the same-server case still works ...
        Assert.IsNotNull(RdpManagedSession.Redirect(Options(tunnel: true), redirection));
    }

    [TestMethod]
    public void RedirectionsThatCannotBeFollowedSurface()
    {
        // ... but another host is not reachable through the tunnel.
        ServerRedirection elsewhere = Parse(RedirectionPacket(1, RedirectionFlags.TargetFqdn | RedirectionFlags.LoadBalanceInfo,
            fqdn: "host7.example", loadBalance: Token));
        Assert.IsNull(RdpManagedSession.Redirect(Options(tunnel: true), elsewhere));
        Assert.IsNotNull(RdpManagedSession.Redirect(Options(tunnel: true) with { Gateway = new RdpGatewayOptions { Host = "gw.example" } }, elsewhere),
            "An RD Gateway can reach the new host by name.");

        // The same server again without a routing token would only repeat itself.
        ServerRedirection nothing = Parse(RedirectionPacket(1, RedirectionFlags.TargetFqdn, fqdn: "BROKER.example"));
        Assert.IsNull(RdpManagedSession.Redirect(Options(), nothing));
    }
}
