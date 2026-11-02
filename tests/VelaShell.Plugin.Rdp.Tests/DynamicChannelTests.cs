using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class DynamicChannelTests
{
    private sealed class Handler : IDynamicChannelHandler
    {
        internal readonly List<byte[]> Messages = [];
        internal int Opens, Closes;
        internal Func<Task>? Closing;
        public Task OnOpenAsync() { Opens++; return Task.CompletedTask; }
        public Task OnMessageAsync(byte[] bytes) { Messages.Add(bytes); return Task.CompletedTask; }
        public Task OnCloseAsync() { Closes++; return Closing?.Invoke() ?? Task.CompletedTask; }
    }
    private static byte[] Create(byte id) => [0x10, id, .. Encoding.ASCII.GetBytes("test"), 0];
    [TestMethod]
    public async Task SameNameInstancesReassembleIndependentlyAndCloseReleasesState()
    {
        var sent = new List<byte[]>(); var handlers = new List<Handler>();
        var dvc = new DrdynvcChannel((bytes, _) => { sent.Add(bytes); return Task.CompletedTask; }, CancellationToken.None);
        dvc.RegisterListener("test", _ => { var h = new Handler(); handlers.Add(h); return h; }, 8);
        await dvc.ProcessAsync(Create(1)); await dvc.ProcessAsync(Create(2));
        await dvc.ProcessAsync([0x20, 1, 4, 1, 2]);
        await dvc.ProcessAsync([0x20, 2, 3, 7]);
        await dvc.ProcessAsync([0x30, 1, 3, 4]);
        await dvc.ProcessAsync([0x30, 2, 8, 9]);
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4 }, handlers[0].Messages.Single());
        Assert.AreSequenceEqual(new byte[] { 7, 8, 9 }, handlers[1].Messages.Single());
        await dvc.ProcessAsync([0x20, 1, 8, 1]);
        await dvc.ProcessAsync([0x40, 1]);
        await dvc.ProcessAsync(Create(1)); await dvc.ProcessAsync([0x30, 1, 5]);
        Assert.AreSequenceEqual(new byte[] { 5 }, handlers[2].Messages.Single());
        Assert.AreEqual(1, handlers[0].Closes);
        await dvc.CloseAsync(); Assert.AreEqual(1, handlers[1].Closes); Assert.AreEqual(1, handlers[2].Closes);
    }
    [TestMethod]
    public async Task CompressedFragmentsKeepPerChannelHistoryAndReleaseItOnClose()
    {
        var sent = new List<byte[]>(); var handlers = new List<Handler>();
        var dvc = new DrdynvcChannel((bytes, _) => { sent.Add(bytes); return Task.CompletedTask; }, default);
        dvc.RegisterListener("test", _ => { var h = new Handler(); handlers.Add(h); return h; }, 8);
        await dvc.ProcessAsync(Create(1)); await dvc.ProcessAsync(Create(2));
        await dvc.ProcessAsync([0x60, 1, 6, 0xe0, 4, 0x49, 0x4a, 0x4b]);
        await dvc.ProcessAsync([0x70, 2, 0xe0, 4, 0x51, 0x52, 0x53]);
        byte[] match = ZgfxTests.Compressed("10001 00011 0");
        await dvc.ProcessAsync([0x70, 1, .. match]);
        await dvc.ProcessAsync([0x70, 2, .. match]);
        Assert.AreSequenceEqual("IJKIJK"u8.ToArray(), handlers[0].Messages.Single());
        Assert.HasCount(2, handlers[1].Messages);
        Assert.AreSequenceEqual("QRS"u8.ToArray(), handlers[1].Messages[1]);
        await dvc.ProcessAsync([0x60, 1, 4, 0xe0, 4, 7]);
        await dvc.ProcessAsync([0x30, 1, 8, 9, 10]);
        Assert.AreSequenceEqual(new byte[] { 7, 8, 9, 10 }, handlers[0].Messages[1]);
        await dvc.ProcessAsync([0x40, 1]);
        await dvc.ProcessAsync(Create(1));
        await dvc.ProcessAsync([0x70, 1, .. match]);
        Assert.IsEmpty(handlers[2].Messages, "A reused id must not retain compression history.");
        Assert.AreEqual(1, handlers[2].Closes);
        Assert.AreSequenceEqual(new byte[] { 0x40, 1 }, sent.Last());
        await dvc.CloseAsync();
    }

    [TestMethod]
    public async Task CompressedExpansionCannotExceedMessageOrRemainingFragmentLimit()
    {
        var sent = new List<byte[]>(); var handlers = new List<Handler>();
        var dvc = new DrdynvcChannel((bytes, _) => { sent.Add(bytes); return Task.CompletedTask; }, default);
        dvc.RegisterListener("test", _ => { var h = new Handler(); handlers.Add(h); return h; }, 8);
        await dvc.ProcessAsync(Create(1)); await dvc.ProcessAsync(Create(2));
        byte[] tenBytes = ZgfxTests.Compressed("0 01001001 10001 00001 110 001");
        await dvc.ProcessAsync([0x70, 1, .. tenBytes]);
        await dvc.ProcessAsync([0x60, 2, 4, 0xe0, 4, 1, 2]);
        await dvc.ProcessAsync([0x70, 2, 0xe0, 4, 3, 4, 5]);
        Assert.IsTrue(handlers.All(h => h.Messages.Count == 0 && h.Closes == 1));
        await dvc.CloseAsync();
    }

    [TestMethod]
    public async Task ReusedIdRejectsOldSendAndOldClose()
    {
        var sent = new List<byte[]>(); var channels = new List<DynamicChannel>();
        var handlers = new List<Handler>();
        var dvc = new DrdynvcChannel((bytes, _) => { sent.Add(bytes); return Task.CompletedTask; }, default);
        dvc.RegisterListener("test", channel =>
        {
            channels.Add(channel); var handler = new Handler(); handlers.Add(handler); return handler;
        });
        await dvc.ProcessAsync(Create(1));
        await dvc.ProcessAsync([0x40, 1]);
        await dvc.ProcessAsync(Create(1));
        int before = sent.Count;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => channels[0].SendAsync([9]));
        await channels[0].CloseAsync();
        Assert.AreEqual(before, sent.Count);
        Assert.AreEqual(0, handlers[1].Closes);
        await channels[1].SendAsync([7]);
        Assert.AreSequenceEqual(new byte[] { 0x30, 1, 7 }, sent.Last());
        await dvc.CloseAsync();
    }

    [TestMethod]
    public async Task CloseWaitsForFragmentsAndReleasesGateBeforeCallback()
    {
        var sent = new List<byte[]>(); var channels = new List<DynamicChannel>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Handler();
        var dvc = new DrdynvcChannel(async (bytes, _) =>
        {
            sent.Add(bytes);
            if ((bytes[0] >> 4) == 2) { entered.TrySetResult(); await release.Task; }
        }, default);
        dvc.RegisterListener("test", channel => { channels.Add(channel); return channels.Count == 1 ? first : new Handler(); });
        await dvc.ProcessAsync(Create(1)); await dvc.ProcessAsync(Create(2));
        first.Closing = () => channels[1].SendAsync([8]);
        Task sending = channels[0].SendAsync(new byte[4000]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task closing = channels[0].CloseAsync();
        Assert.IsFalse(closing.IsCompleted);
        Assert.IsFalse(sent.Any(bytes => (bytes[0] >> 4) == 4));
        release.SetResult();
        await Task.WhenAll(sending, closing).WaitAsync(TimeSpan.FromSeconds(3));
        int close = sent.FindIndex(bytes => (bytes[0] >> 4) == 4);
        Assert.AreEqual(5, close);
        Assert.AreSequenceEqual(new byte[] { 0x30, 2, 8 }, sent.Last());
        await dvc.CloseAsync();
    }

    [TestMethod]
    public async Task AFailingHandlerClosesOnlyItsOwnChannel()
    {
        var sent = new List<byte[]>(); var log = new List<string>();
        var dvc = new DrdynvcChannel((bytes, _) => { sent.Add(bytes); return Task.CompletedTask; }, default, log: log.Add);
        var healthy = new Handler();
        var failing = new ThrowingHandler();
        dvc.RegisterListener("bad", _ => failing);
        dvc.RegisterListener("test", _ => healthy);
        await dvc.ProcessAsync([0x10, 1, .. Encoding.ASCII.GetBytes("bad"), 0]);
        await dvc.ProcessAsync(Create(2));

        // The handler throws on its message and again while being closed: neither escapes the multiplexer.
        await dvc.ProcessAsync([0x30, 1, 5]);
        Assert.AreSequenceEqual(new byte[] { 0x40, 1 }, sent.Last());
        Assert.AreEqual(1, failing.Closes);
        Assert.HasCount(2, log);

        // The channel is gone; the other channel still works.
        await dvc.ProcessAsync([0x30, 1, 6]);
        Assert.AreEqual(1, failing.Messages);
        await dvc.ProcessAsync([0x30, 2, 7]);
        Assert.AreSequenceEqual(new byte[] { 7 }, healthy.Messages.Single());
        await dvc.CloseAsync();
    }

    private sealed class ThrowingHandler : IDynamicChannelHandler
    {
        internal int Messages, Closes;
        public Task OnOpenAsync() => Task.CompletedTask;
        public Task OnMessageAsync(byte[] bytes) { Messages++; throw new InvalidOperationException("Dynamic channel closed or message too large."); }
        public Task OnCloseAsync() { Closes++; throw new IOException("Camera flush did not complete."); }
    }

    [TestMethod]
    public async Task OutboundFragmentsRoundTripAndOversizedTotalsAreDiscarded()
    {
        var packets = new List<byte[]>(); DynamicChannel? channel = null;
        var sender = new DrdynvcChannel((bytes, _) => { packets.Add(bytes); return Task.CompletedTask; }, CancellationToken.None);
        sender.RegisterListener("test", c => { channel = c; return new Handler(); }, 10000);
        await sender.ProcessAsync(Create(7)); packets.Clear();
        byte[] payload = [.. Enumerable.Range(0, 5000).Select(i => (byte)i)];
        await channel!.SendAsync(payload);
        Assert.AreEqual((byte)0x28, packets[0][0]);
        Assert.AreEqual(5000u, BinaryPrimitives.ReadUInt32LittleEndian(packets[0].AsSpan(2)));
        var receiver = new DrdynvcChannel((_, _) => Task.CompletedTask, CancellationToken.None);
        var handler = new Handler(); receiver.RegisterListener("test", _ => handler, 10000);
        await receiver.ProcessAsync(Create(7));
        foreach (byte[] packet in packets) await receiver.ProcessAsync(packet);
        Assert.AreSequenceEqual(payload, handler.Messages.Single());
        await receiver.ProcessAsync([0x28, 7, 0xff, 0xff, 0xff, 0x7f, 1]);
        Assert.HasCount(1, handler.Messages);
        await receiver.ProcessAsync([0x20, 7, 1, 1, 2]);
        Assert.HasCount(1, handler.Messages);
        await channel.CloseAsync(); Assert.AreEqual((byte)0x40, packets.Last()[0]);
        await receiver.CloseAsync();
    }
}
