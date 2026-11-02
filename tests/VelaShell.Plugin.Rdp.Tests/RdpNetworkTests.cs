using System.Buffers.Binary;
using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpNetworkTests
{
    [TestMethod]
    public void MeasurementsDegradeImmediatelyAndRequireThreeHealthySamplesToRecover()
    {
        var monitor = new RdpNetworkMonitor(true);
        monitor.Measure(300, 500);
        Assert.AreEqual(RdpNetworkQuality.Poor, monitor.Status.Quality);
        Assert.AreEqual(50, monitor.Status.PointerIntervalMilliseconds);
        monitor.Measure(20, 10000); monitor.Measure(20, 10000);
        Assert.AreEqual(RdpNetworkQuality.Poor, monitor.Status.Quality);
        monitor.Measure(20, 10000);
        Assert.AreEqual(RdpNetworkQuality.Good, monitor.Status.Quality);
        monitor.Transport(true, false, true);
        Assert.AreEqual(20u, monitor.Status.RoundTripMilliseconds);
        var manual = new RdpNetworkMonitor(false);
        manual.Measure(1000, 200);
        Assert.AreEqual(0, manual.Status.PointerIntervalMilliseconds);
    }

    private static byte[] Request(ushort type, byte[]? payload = null)
    {
        var w = new ByteWriter();
        w.U8((byte)(type is 2 or 0x2B ? 8 : 6 + (payload?.Length ?? 0)));
        w.U8(0); w.U16LE(42); w.U16LE(type); w.Bytes(payload ?? []);
        return w.ToArray();
    }

    [TestMethod]
    public void NetworkDetectionEchoesSequenceAndCountsEachBandwidthPayloadOnce()
    {
        var monitor = new RdpNetworkMonitor(true);
        var detect = new RdpAutoDetect(monitor);
        Assert.AreSequenceEqual(new byte[] { 6, 1, 42, 0, 0, 0 }, detect.Process(Request(1), 1)!);
        detect.Process(Request(0x1014), 10);
        detect.Process(Request(2, [3,0,7,8,9]), 11);
        detect.Observe(100);
        byte[] result = detect.Process(Request(0x2B, [1,0,10]), 20)!;
        Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(10)));
        Assert.AreEqual(10u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(6)));
        detect.Process(Request(0x14), 30);
        detect.Observe(15);
        detect.Process(Request(2, [3,0,7,8,9]), 31);
        result = detect.Process(Request(0x429), 40)!;
        Assert.AreEqual(15u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(10)));
        detect.Process(Request(0x880, [0xE8,3,0,0, 0x2C,1,0,0]), 50);
        Assert.AreEqual(RdpNetworkQuality.Poor, monitor.Status.Quality);
        Assert.ThrowsExactly<RdpProtocolException>(() => detect.Process(Request(2, [10,0,1]), 51));
    }

    [TestMethod]
    public void NetworkSettingsSupportManualOverrideAndRejectUnknownValues()
    {
        var settings = RdpSettings.From(RdpSettingsTests.Request(("transport", "tcp"), ("adaptiveNetwork", "false")), new("en"), []);
        Assert.AreEqual(RdpTransportMode.Tcp, settings.Transport);
        Assert.IsFalse(settings.AdaptiveNetwork);
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(RdpSettingsTests.Request(("transport", "udp-only")), new("en"), []));
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(RdpSettingsTests.Request(("adaptiveNetwork", "maybe")), new("en"), []));
    }

    private sealed class Sink : IDynamicChannelHandler
    {
        internal List<byte[]> Messages = [];
        public Task OnOpenAsync() => Task.CompletedTask;
        public Task OnCloseAsync() => Task.CompletedTask;
        public Task OnMessageAsync(byte[] data) { Messages.Add(data); return Task.CompletedTask; }
    }
    private static byte[] Sync(params (int Type, uint[] Channels)[] tunnels)
    {
        var w = new ByteWriter();
        w.U8(0x80); w.U8(0); w.U32LE((uint)(8 + tunnels.Sum(t => 6 + t.Channels.Length * 4)));
        w.U16LE(3); w.U16LE((ushort)tunnels.Length);
        foreach (var tunnel in tunnels)
        { w.U32LE((uint)tunnel.Type); w.U16LE((ushort)tunnel.Channels.Length); foreach (uint id in tunnel.Channels) { w.U32LE(id); } }
        return w.ToArray();
    }

    [TestMethod]
    public async Task LossyRouteRejectsOutboundFragmentationAndClosedIdsCanReturnToTcp()
    {
        List<byte[]> tcp = [], udp = [];
        var dvc = new DrdynvcChannel((p, _) => { tcp.Add(p); return Task.CompletedTask; }, default,
            waitForTunnel: _ => Task.CompletedTask, enableTunnel: _ => { },
            sendTunnel: (_, p, _) => { udp.Add(p); return Task.CompletedTask; });
        DynamicChannel? channel = null;
        dvc.RegisterListener("test", c => { channel = c; return new Sink(); });
        byte[] create = [0x10, 1, .. Encoding.ASCII.GetBytes("test"), 0];
        await dvc.ProcessAsync(create);
        await dvc.ProcessAsync(Sync((3, [1])));
        await channel!.SendAsync([9]);
        Assert.AreSequenceEqual(new byte[] { 0x30, 1, 9 }, udp.Single());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => channel.SendAsync(new byte[601]));
        Assert.HasCount(1, udp);
        await dvc.ProcessAsync([0x40, 1], 3);
        await dvc.ProcessAsync(create);
        await channel.SendAsync([8]);
        Assert.AreSequenceEqual(new byte[] { 0x30, 1, 8 }, tcp.Last());
        await dvc.ProcessAsync([0x10, 2, (byte)'?', 0], 3);
        Assert.AreEqual((byte)0x10, udp.Last()[0], "CREATE refusal uses the incoming tunnel before removing its route.");
        await dvc.ProcessAsync([0x30, 2, 9]);
        await dvc.CloseAsync();
    }

    [TestMethod]
    public async Task SoftSyncWaitsForSuccessAndMovesOnlySelectedChannelsAfterTcpResponse()
    {
        List<byte[]> tcp = [], udp = [];
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool reading = false;
        var dvc = new DrdynvcChannel((pdu, _) => { tcp.Add(pdu); return Task.CompletedTask; }, default,
            waitForTunnel: _ => ready.Task, enableTunnel: _ => { Assert.AreEqual((byte)0x90, tcp.Last()[0]); reading = true; },
            sendTunnel: (type, pdu, _) => { Assert.AreEqual(1, type); udp.Add(pdu); return Task.CompletedTask; });
        var channels = new List<DynamicChannel>(); var sink = new Sink();
        dvc.RegisterListener("test", c => { channels.Add(c); return sink; });
        await dvc.ProcessAsync([0x10, 1, .. Encoding.ASCII.GetBytes("test"), 0]);
        await dvc.ProcessAsync([0x10, 2, .. Encoding.ASCII.GetBytes("test"), 0]);
        Task syncing = dvc.ProcessAsync(Sync((1, [1])));
        Assert.IsFalse(syncing.IsCompleted);
        Task sending = channels[0].SendAsync([7]);
        Assert.IsFalse(sending.IsCompleted);
        ready.SetResult();
        await Task.WhenAll(syncing, sending);
        Assert.IsTrue(reading);
        Assert.AreSequenceEqual(new byte[] { 0x30, 1, 7 }, udp.Single());
        await channels[1].SendAsync([8]);
        Assert.AreSequenceEqual(new byte[] { 0x30, 2, 8 }, tcp.Last());
        await dvc.ProcessAsync([0x30, 1, 9], 1);
        Assert.AreSequenceEqual(new byte[] { 9 }, sink.Messages.Single());
        await dvc.ProcessAsync([0x60, 1, 2, 0xe0, 4, 10], 1);
        await dvc.ProcessAsync([0x70, 1, 0xe0, 4, 11], 1);
        Assert.AreSequenceEqual(new byte[] { 10, 11 }, sink.Messages[1]);
        await Assert.ThrowsExactlyAsync<RdpProtocolException>(() => dvc.ProcessAsync([0x60, 1, 2, 0xe0, 4, 10]));
        await Assert.ThrowsExactlyAsync<RdpProtocolException>(() => dvc.ProcessAsync([0x70, 1, 0xe0, 4, 11]));
        await Assert.ThrowsExactlyAsync<RdpProtocolException>(() => dvc.ProcessAsync([0x30, 1, 9]));
        await Assert.ThrowsExactlyAsync<RdpProtocolException>(() => dvc.ProcessAsync(Sync((1, [1]), (3, [1]))));
        await dvc.CloseAsync();
    }
}
