using VelaShell.PluginSdk.Network;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The main RDP connection is dialed by the host (<see cref="INetworkApi" />), so it follows the host's network proxy
/// setting: the target goes to the host unchanged, and a host proxy failure reaches the user with the host's
/// localized message rather than as a generic "TCP stage failed".
/// </summary>
[TestClass]
public sealed class RdpHostNetworkTests
{
    private static (WorkspaceConnectRequest Request, RdpSettings Settings, Loc Loc) Setup()
    {
        WorkspaceConnectRequest request = RdpSettingsTests.Request(("timeout", "5"));
        var loc = new Loc("en");
        return (request, RdpSettings.From(request, loc), loc);
    }

    [TestMethod]
    public async Task MainConnection_IsDialedByTheHost()
    {
        (WorkspaceConnectRequest request, RdpSettings settings, Loc loc) = Setup();
        // An empty stream: the server "closes" at once, so the connect fails after the dial — which is all this checks.
        var network = new FakeNetwork { Handler = static (_, _) => new MemoryStream() };

        await Assert.ThrowsAsync<ProtocolConnectionException>(
            () => RdpConnector.ConnectAsync(request, settings, loc, default, null, network));

        (string host, int port, TcpConnectOptions options) = network.Connected.Single();
        Assert.AreEqual(("desktop.example", 3389), (host, port));
        Assert.IsTrue(options.NoDelay);
        Assert.IsGreaterThan(TimeSpan.Zero, options.KeepAliveIdle, "a paused desktop is silent; NAT must not drop it");
        Assert.AreEqual(0, network.ActiveConnections, "the failed connection must release the host's stream");
    }

    [TestMethod]
    public async Task HostProxyFailure_KeepsTheHostMessage()
    {
        (WorkspaceConnectRequest request, RdpSettings settings, Loc loc) = Setup();
        var network = new FakeNetwork { Failure = new NetworkProxyException("Proxy refused (via socks5 10.0.0.1:1080 → desktop.example:3389)") };

        ProtocolConnectionException error = await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(
            () => RdpConnector.ConnectAsync(request, settings, loc, default, null, network));

        Assert.AreEqual("Proxy refused (via socks5 10.0.0.1:1080 → desktop.example:3389)", error.Message);
    }

    [TestMethod]
    public void Translate_FindsANestedProxyFailure()
    {
        var proxy = new NetworkProxyException("bad proxy");
        Exception translated = RdpConnector.Translate(new IOException("wrapped", proxy), new Loc("en"));

        Assert.IsInstanceOfType<ProtocolConnectionException>(translated);
        Assert.AreEqual("bad proxy", translated.Message);
    }
}
