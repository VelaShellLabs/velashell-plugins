using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class FrameBufferTests
{
    [TestMethod]
    public async Task PinnedConsumer_DoesNotBlockPublisher_AndPendingRefreshKeepsLatestCompleteFrame()
    {
        int refresh = 0;
        await using var frames = new RdpFrameBuffer(2, 2, () => Interlocked.Increment(ref refresh));
        Assert.IsTrue(frames.Publish(bytes => Array.Fill(bytes, (byte)1)));
        Assert.IsTrue(frames.TryAcquire(out var held));
        Assert.IsFalse(frames.TryAcquire(out _)); // Exactly one copying consumer.
        Assert.IsTrue(frames.Publish(bytes => Array.Fill(bytes, (byte)2)));
        Assert.IsFalse(frames.Publish(_ => Assert.Fail("The pinned back buffer cannot be overwritten.")));
        Assert.IsTrue(frames.Pending);
        Assert.IsTrue(held.Pixels.ToArray().All(b => b == 1));
        held.Dispose(); held.Dispose();
        Assert.AreEqual(1, refresh);
        Assert.IsTrue(frames.Publish(bytes => Array.Fill(bytes, (byte)3)));
        var pixels = new byte[16];
        Assert.IsTrue(frames.TryCopy(pixels, out var info));
        Assert.AreEqual(3L, info.Sequence);
        Assert.IsTrue(pixels.All(b => b == 3));
        Assert.IsFalse(frames.Pending);
    }
    [TestMethod]
    public async Task MultipleResizes_DoNotWaitForCopy_RetiredPixelsAreZeroedOnReturn()
    {
        await using var frames = new RdpFrameBuffer(2, 2, () => { });
        byte[]? original = null;
        frames.Publish(bytes => { Array.Fill(bytes, (byte)0x7f); original = bytes; });
        Assert.IsTrue(frames.TryAcquire(out var held));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task consumer = Task.Run(async () => { entered.SetResult(); await release.Task; held.Dispose(); });
        await entered.Task;
        // Writer proceeds while the copying consumer owns the old frame. No network/UI rendezvous.
        frames.Resize(3, 2); frames.Publish(bytes => Array.Fill(bytes, (byte)2));
        frames.Resize(4, 2); frames.Publish(bytes => Array.Fill(bytes, (byte)3));
        Assert.IsTrue(original!.All(b => b == 0x7f));
        release.SetResult(); await consumer.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(original.All(b => b == 0));
        var pixels = new byte[32];
        Assert.IsTrue(frames.TryCopy(pixels, out var info));
        Assert.AreEqual(4, info.Width); Assert.AreEqual(2, info.Height);
        Assert.IsTrue(pixels.All(b => b == 3));
    }
    [TestMethod]
    public async Task Dispose_WaitsForManagedConsumer_ThenClearsBothOwnedFrames()
    {
        var frames = new RdpFrameBuffer(2, 1, () => { });
        var owned = new List<byte[]>();
        frames.Publish(bytes => { Array.Fill(bytes, (byte)1); owned.Add(bytes); });
        frames.Publish(bytes => { Array.Fill(bytes, (byte)2); owned.Add(bytes); });
        Assert.IsTrue(frames.TryAcquire(out var held));
        Task close = frames.DisposeAsync().AsTask();
        Assert.IsFalse(close.IsCompleted);
        Assert.IsFalse(frames.Publish(_ => Assert.Fail("Closed frames cannot publish.")));
        held.Dispose();
        await close.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(owned.All(bytes => bytes.All(b => b == 0)));
        Assert.IsFalse(frames.TryAcquire(out _));
        await frames.DisposeAsync();
    }
    [TestMethod]
    public async Task SmallDestination_DoesNotLeakLease_AndServerDimensionsStayBounded()
    {
        await using var frames = new RdpFrameBuffer(2, 2, () => { });
        Assert.IsFalse(frames.TryCopy(new byte[1], out var info));
        Assert.AreEqual(16, info.ByteLength);
        Assert.IsTrue(frames.TryCopy(new byte[16], out _));
        Assert.Throws<VelaShell.PluginSdk.Protocols.ProtocolConnectionException>(() => frames.Resize(65535, 65535));
    }
}
