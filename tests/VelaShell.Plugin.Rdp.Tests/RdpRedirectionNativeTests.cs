using Devolutions.IronRdp;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpRedirectionNativeTests
{
    [TestMethod]
    public async Task ExtensionAttachesBothChannelsAndRejectsDuplicateOrLateAttach()
    {
        if (!RdpClipboardNative.IsAvailable)
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var options = new RdpConnectionOptions("127.0.0.1", port, "localhost", port, 320, 200,
                TimeSpan.FromSeconds(2), "");
            using var credentials = new RdpCredentials("offline", "not-a-real-password", "");
            await Assert.ThrowsExactlyAsync<VelaShell.PluginSdk.Protocols.ProtocolUnsupportedException>(
                () => RdpSessionFactory.Instance.ConnectAsync(options with { RedirectClipboard = true }, credentials));
            await Assert.ThrowsExactlyAsync<VelaShell.PluginSdk.Protocols.ProtocolUnsupportedException>(
                () => RdpSessionFactory.Instance.ConnectAsync(options with { Drives = [new(4, "D", "D:\\", true)] }, credentials));
            Assert.IsFalse(listener.Pending(), "Unsupported redirection must fail before opening a network connection.");
            return;
        }
        using var builder = ConfigBuilder.New();
        builder.WithUsernameAndPassword("offline", "not-a-real-password");
        builder.SetDesktopSize(200, 320);
        builder.SetEnableTls(true);
        builder.SetEnableCredssp(true);
        builder.SetClientName("VelaShell");
        builder.SetClientDir("C:\\Windows\\System32\\mstscax.dll");
        builder.SetKeyboardType(KeyboardType.IbmEnhanced);
        using var performance = PerformanceFlags.NewDefault();
        builder.SetPerformanceFlags(performance);
        using var config = builder.Build();
        using var connector = ClientConnector.New(config, "127.0.0.1:49152");
        RdpClipboardNative.Attach(connector);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardNative.Attach(connector));
        if (OperatingSystem.IsWindows())
        {
            RdpDriveNative.Attach(connector, [new(4, "D", "D:\\", true)]);
            try
            {
                RdpDriveNative.Attach(connector, [new(5, "E", "E:\\", true)]);
                Assert.Fail("Duplicate RDPDR channel was accepted.");
            }
            catch (IronRdpException error) { error.Inner.Dispose(); }
        }
        using var output = WriteBuf.New();
        using var written = connector.InitiateWithSecurityProtocol(2, output);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardNative.Attach(connector));
    }
}
