using System.Buffers.Binary;
using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Mcs;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpGfxTests
{
    [TestMethod]
    public void ClassicSkipsBackendAndManagedGraphicsIsAdvertisedOnlyWhenPrepared()
    {
        var log = new List<string>();
        var options = new RdpClientOptions { Host = "example", DynamicResolution = false, GraphicsLog = log.Add };
        Assert.IsNull(RdpManagedSession.PrepareGraphics(options with { EnableGraphicsPipeline = false },
            (_, _) => throw new AssertFailedException("Classic must not initialize the graphics backend.")));
        Assert.DoesNotContain(c => c.Name == "drdynvc", RdpManagedSession.BuildChannels(options, false));
        Assert.AreEqual(0, EarlyFlags(false) & 0x100);
        using IRdpGfxBackend? ready = RdpManagedSession.PrepareGraphics(options);
        _ = Assert.IsInstanceOfType<ManagedRdpGfx>(ready);
        Assert.Contains(c => c.Name == "drdynvc", RdpManagedSession.BuildChannels(options, ready is not null));
        Assert.AreEqual(0x103, EarlyFlags(ready is not null));
        Assert.Contains(line => line.Contains("managed graphics pipeline ready", StringComparison.Ordinal), log);
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => RdpManagedSession.PrepareGraphics(options,
            (_, _) => throw new RdpProtocolException("Initialization failed")));
    }

    private static ushort EarlyFlags(bool ready)
    {
        byte[] gcc = GccData.BuildClientData(3, 2, 32, 1, "test", 0x409, [], graphicsPipeline: ready);
        int core = gcc.AsSpan().IndexOf(new byte[] { 1, 0xc0, 0xd8, 0, 4, 0, 8, 0 });
        Assert.IsGreaterThanOrEqualTo(0, core);
        return BinaryPrimitives.ReadUInt16LittleEndian(gcc.AsSpan(core + 144));
    }

    [TestMethod]
    [DataRow("", true)]
    [DataRow("auto", true)]
    [DataRow("classic", false)]
    public void GraphicsSettingPreservesAutoDefaultAndAllowsClassic(string mode, bool expected)
    {
        var settings = RdpSettings.From(RdpSettingsTests.Request((RdpSettings.GraphicsModeKey, mode)), new("en"), []);
        Assert.AreEqual(expected, settings.EnableGraphicsPipeline);
    }

    [TestMethod]
    public void UnknownGraphicsSettingIsRejected() => Assert.ThrowsExactly<ProtocolConnectionException>(static () =>
        RdpSettings.From(RdpSettingsTests.Request((RdpSettings.GraphicsModeKey, "sometimes")), new("en"), []));

    [TestMethod]
    public void ManagedDirtyCopyHonorsStrideAndDoesNotTouchOtherPixels()
    {
        using var backend = new Backend();
        var framebuffer = new Framebuffer(3, 2);
        byte[] destination = new byte[24];
        _ = framebuffer.CopyTo(destination, 12, true);
        RdpGfxFrame frame = backend.Frame;
        frame.X = 1; frame.Y = 1; frame.W = 2; frame.H = 1;
        framebuffer.Blit(frame);
        RdpFrameRegion dirty = framebuffer.CopyTo(destination, 12, false);
        Assert.AreEqual(new RdpFrameRegion(1, 1, 2, 1), dirty);
        Assert.AreSequenceEqual(new byte[] { 20, 21, 22, 23, 24, 25, 26, 27 }, destination.AsSpan(16, 8).ToArray());
        Assert.AreSequenceEqual(new byte[] { 0, 0, 0, 255 }, destination.AsSpan(12, 4).ToArray());
        frame.Stride = 11;
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => framebuffer.Blit(frame));
        frame = backend.Frame; frame.X = int.MaxValue;
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => framebuffer.Blit(frame));
    }

    [TestMethod]
    public async Task ResizingCopiesWholeSurfaceAndClassicActivationCannotReplaceIt()
    {
        using var backend = new Backend();
        await using var connection = new RdpConnection(new("example", 3389, "", "", "", 1, 1, 32, null, "example", []));
        int notifications = 0;
        connection.FrameUpdated += () => notifications++;
        RdpGfxFrame frame = backend.Frame;
        frame.X = 1; frame.Y = 1; frame.W = 1; frame.H = 1;
        connection.ApplyGraphicsFrame(frame);
        Assert.AreEqual(3, connection.Framebuffer.Width);
        Assert.AreEqual(2, connection.Framebuffer.Height);
        Assert.AreEqual((byte)0, connection.Framebuffer.Bgra[0]);
        Assert.AreEqual((byte)1, connection.Framebuffer.Bgra[1]);
        Assert.AreEqual((byte)27, connection.Framebuffer.Bgra[23]);
        Framebuffer retained = connection.Framebuffer;
        connection.StartSurface(32);
        Assert.AreSame(retained, connection.Framebuffer);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public async Task FrameIsCopiedBeforeResponseReadsAndCleanupWaitsForPendingSend()
    {
        using var stop = new CancellationTokenSource();
        var backend = new Backend { Dirty = true, InvalidateOnRead = true };
        backend.Responses.Enqueue([7, 8, 9]);
        var surface = new Framebuffer(3, 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dvc = new DrdynvcChannel(async (packet, cancellationToken) =>
        {
            if ((packet[0] >> 4) != 3) { return; }
            _ = entered.TrySetResult();
            await release.Task;
            Assert.AreSequenceEqual(new byte[] { 0x30, 1, 7, 8, 9 }, packet);
        }, stop.Token);
        await using var graphics = new RdpGfxChannel(backend, surface.Blit, ex => Assert.Fail(ex.ToString()), stop.Token);
        graphics.Register(dvc);
        Task opening = dvc.ProcessAsync(Create(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual((byte)1, surface.Bgra[1]);
        Assert.AreEqual((byte)27, surface.Bgra[23]);
        Task disposing = graphics.DisposeAsync().AsTask();
        Assert.IsFalse(disposing.IsCompleted);
        Assert.AreEqual(0, backend.Disposals);
        release.SetResult();
        await Task.WhenAll(opening, disposing).WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await dvc.CloseAsync();
        Assert.AreEqual(1, backend.Disposals);
        Assert.AreSequenceEqual(["open", "frame", "frame", "read", "read", "dispose"], backend.Calls);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task GraphicsFailureCancelsSessionEvenWhenMultiplexerSwallowsHandlerErrors(bool failOpen)
    {
        using var stop = new CancellationTokenSource();
        var backend = new Backend { FailOpen = failOpen, FailFeed = !failOpen };
        Exception? failure = null;
        var dvc = new DrdynvcChannel((_, _) => Task.CompletedTask, stop.Token);
        await using var graphics = new RdpGfxChannel(backend, _ => { }, ex => { failure = ex; stop.Cancel(); }, stop.Token);
        graphics.Register(dvc);
        await dvc.ProcessAsync(Create(1));
        if (!failOpen) { await dvc.ProcessAsync([0x30, 1, 9]); }
        Assert.IsTrue(stop.IsCancellationRequested);
        _ = Assert.IsInstanceOfType<RdpProtocolException>(failure);
        await dvc.CloseAsync();
        Assert.AreEqual(1, backend.Disposals);
    }

    [TestMethod]
    public async Task OneBackendOpensOnceAcceptsLargeMessagesAndUnexpectedCloseIsFatal()
    {
        using var stop = new CancellationTokenSource();
        var backend = new Backend();
        var errors = new List<Exception>();
        var responses = new List<byte[]>();
        var dvc = new DrdynvcChannel((packet, _) => { responses.Add(packet); return Task.CompletedTask; }, stop.Token);
        await using var graphics = new RdpGfxChannel(backend, _ => { }, ex => { errors.Add(ex); stop.Cancel(); }, stop.Token);
        graphics.Register(dvc);
        await dvc.ProcessAsync(Create(1));
        await dvc.ProcessAsync(Create(2));
        Assert.ContainsSingle(call => call == "open", backend.Calls);
        Assert.AreNotEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(responses.Last().AsSpan(2)));
        byte[] message = new byte[128 * 1024];
        message[0] = 0x30; message[1] = 1;
        await dvc.ProcessAsync(message);
        Assert.AreEqual(message.Length - 2, backend.LastMessageLength);
        await dvc.ProcessAsync([0x40, 1]);
        Assert.HasCount(1, errors);
        Assert.IsTrue(stop.IsCancellationRequested);
        Assert.AreEqual(1, backend.Disposals);
        await dvc.CloseAsync();
    }

    private static byte[] Create(byte id) => [0x10, id, .. Encoding.ASCII.GetBytes(RdpGfxChannel.ChannelName), 0];

    private sealed class Backend : IRdpGfxBackend
    {
        internal readonly List<string> Calls = [];
        internal readonly Queue<byte[]> Responses = new();
        internal bool Dirty, InvalidateOnRead, FailOpen, FailFeed;
        internal int Disposals, LastMessageLength;
        internal readonly RdpGfxFrame Frame;

        internal Backend() => Frame = new RdpGfxFrame { Pixels = [.. Enumerable.Range(0, 32).Select(static i => (byte)i)], Width = 3, Height = 2, Stride = 16, W = 3, H = 2 };
        public void Open()
        {
            Calls.Add("open");
            if (FailOpen) { throw new RdpProtocolException("graphics open failed"); }
        }
        public void Feed(byte[] message)
        {
            Calls.Add("feed"); LastMessageLength = message.Length;
            if (FailFeed) { throw new RdpProtocolException("graphics feed failed"); }
        }
        public bool TryGetFrame(out RdpGfxFrame frame)
        {
            Calls.Add("frame"); frame = Frame;
            bool dirty = Dirty; Dirty = false; return dirty;
        }
        public byte[]? ReadResponse()
        {
            Calls.Add("read");
            if (InvalidateOnRead) { Frame.Pixels.AsSpan().Clear(); }
            return Responses.TryDequeue(out byte[]? response) ? response : null;
        }
        public void Dispose()
        {
            Calls.Add("dispose");
            Disposals++;
        }
    }
}
