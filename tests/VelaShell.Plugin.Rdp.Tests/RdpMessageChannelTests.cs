using System.Buffers.Binary;
using System.Net.Security;
using System.Reflection;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpMessageChannelTests
{
    [TestMethod]
    public async Task EarlyNetworkProbeAndUnsupportedUdpRequestDoNotInterruptTcpActivation()
    {
        var initiate = new ByteWriter();
        initiate.U16LE(2); initiate.U16LE(0); initiate.U32LE(42);
        initiate.U16LE(1); initiate.U16LE(0); initiate.Bytes(new byte[16]);
        byte[] demand = [6, 0, 0x11, 0, 0xEA, 3];
        using var wire = new LicensingTests.ScriptedStream([
            .. LicensingTests.Frame([0,0x10,0,0,6,0,7,0,1,0], 1007),
            .. LicensingTests.Frame(initiate.ToArray(), 1007), .. LicensingTests.Frame(demand)]);
        await using RdpConnection connection = LicensingTests.Connection(wire);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mcs = (McsLayer)typeof(RdpConnection).GetField("_mcs", flags)!.GetValue(connection)!;
        typeof(McsLayer).GetProperty("Server", flags)!.SetValue(mcs,
            new GccServerData { IoChannelId = 1003, MessageChannelId = 1007, MultitransportFlags = 0x205 });
        byte[] received = await LicensingTests.Receive(connection);
        Assert.AreSequenceEqual(demand, received);
        byte[] output = wire.Output.ToArray();
        int firstLength = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(2));
        Assert.AreEqual((ushort)1007, BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(10)));
        Assert.AreSequenceEqual(new byte[] { 0, 0x20, 0, 0, 6, 1, 7, 0, 0, 0 }, output.AsSpan(14, firstLength - 14).ToArray());
        Assert.AreSequenceEqual(new byte[] { 4, 0, 0, 0, 42, 0, 0, 0, 4, 0x40, 0, 0x80 }, output.AsSpan(firstLength + 14).ToArray());
    }

    [TestMethod]
    [DataRow(true, false, "auto", true)]
    [DataRow(true, true, "auto", false)]
    [DataRow(false, false, "auto", false)]
    [DataRow(true, false, "tcp", false)]
    public async Task ConnectorNegotiatesUdpOnlyForDirectExtendedAutoConnections(bool extended, bool jump, string mode, bool expectUdp)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var captured = new TaskCompletionSource<byte[]>();
        await using var server = new FakeRdpServer(async peer =>
        {
            await peer.SelectAsync(FakeRdpConnection.Ssl, extended ? (byte)1 : (byte)0);
            using SslStream tls = await peer.StartTlsAsync();
            byte[] header = new byte[4]; await tls.ReadExactlyAsync(header, stop.Token);
            byte[] body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) - 4];
            await tls.ReadExactlyAsync(body, stop.Token);
            captured.SetResult(body);
        });
        WorkspaceConnectRequest request = RdpSettingsTests.Request(("transport", mode), ("security", "tls"),
            ("trustedThumbprint", Convert.ToHexString(FakeRdpServer.Pin))) with
        { Host = "127.0.0.1", Port = server.Port, Tunnel = jump ? new("localhost", 3389, "Jump") : null };
        var settings = RdpSettings.From(request, new("en"), []);
        _ = await Assert.ThrowsAsync<VelaShell.PluginSdk.Protocols.ProtocolConnectionException>(
            () => RdpConnector.ConnectAsync(request, settings, new("en"), stop.Token));
        byte[] gcc = await captured.Task.WaitAsync(stop.Token);
        Assert.AreEqual(expectUdp, gcc.AsSpan().IndexOf(new byte[] { 10, 0xC0, 8, 0, 5, 2, 0, 0 }) >= 0);
    }

    [TestMethod]
    public void ExtendedClientBlocksRequireMessageChannelAndAutodetectSetsCoreHint()
    {
        byte[] enabled = GccData.BuildClientData(800, 600, 32, 1, "test", 0, [],
            messageChannel: true, multitransport: true, adaptiveNetwork: true);
        byte[] legacy = GccData.BuildClientData(800, 600, 32, 1, "test", 0, [],
            messageChannel: false, multitransport: true, adaptiveNetwork: true);
        byte[] extensions = [6, 0xC0, 8, 0, 0, 0, 0, 0, 10, 0xC0, 8, 0, 5, 2, 0, 0];
        Assert.IsTrue(enabled.AsSpan().EndsWith(extensions));
        Assert.IsFalse(legacy.AsSpan().EndsWith(extensions));
        int core = enabled.AsSpan().IndexOf(new byte[] { 1, 0xC0, 0xD8, 0, 4, 0, 8, 0 });
        Assert.IsGreaterThanOrEqualTo(0, core);
        Assert.AreEqual(0xA0, BinaryPrimitives.ReadUInt16LittleEndian(enabled.AsSpan(core + 144)) & 0xA0);
        Assert.AreEqual((byte)7, enabled[core + 210]);
    }
}
