using System.Runtime.InteropServices;
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
        Assert.IsTrue(CopyPixels(frames, pixels, out var info));
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
        Assert.IsTrue(CopyPixels(frames, pixels, out var info));
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
        Assert.IsFalse(CopyPixels(frames, new byte[1], out var info));
        Assert.AreEqual(16, info.ByteLength);
        Assert.IsTrue(CopyPixels(frames, new byte[16], out _));
        Assert.Throws<VelaShell.PluginSdk.Protocols.ProtocolConnectionException>(() => frames.Resize(65535, 65535));
    }

    [TestMethod]
    public async Task DirectCopyValidatesGeometryAndCapacityBeforeWritingAndPreservesPadding()
    {
        await using var frames = new RdpFrameBuffer(2, 2, static () => { });
        byte[] source = Enumerable.Range(1, 16).Select(x => (byte)x).ToArray();
        frames.Publish(target => source.CopyTo(target, 0));
        IntPtr target = Marshal.AllocHGlobal(24);
        try
        {
            byte[] sentinel = Enumerable.Repeat((byte)77, 24).ToArray();
            Marshal.Copy(sentinel, 0, target, sentinel.Length);
            Assert.IsFalse(frames.TryCopy(target, 1, 4, 4, 24, out var info));
            Assert.AreEqual(2, info.Width);
            Assert.IsFalse(frames.TryCopy(target, 2, 2, 7, 24, out _));
            Assert.IsFalse(frames.TryCopy(target, 2, 2, 12, 23, out _));
            Assert.IsFalse(frames.TryCopy(IntPtr.Zero, 2, 2, 8, 16, out _));
            Assert.IsFalse(frames.TryCopy(target, 2, 2, int.MaxValue, 24, out _));
            byte[] actual = new byte[24];
            Marshal.Copy(target, actual, 0, actual.Length);
            CollectionAssert.AreEqual(sentinel, actual);
            Assert.IsTrue(frames.TryCopy(target, 2, 2, 12, 24, out _));
            Marshal.Copy(target, actual, 0, actual.Length);
            CollectionAssert.AreEqual(source[..8], actual[..8]);
            CollectionAssert.AreEqual(source[8..], actual[12..20]);
            Assert.IsTrue(actual[8..12].Concat(actual[20..24]).All(x => x == 77));
        }
        finally { Marshal.FreeHGlobal(target); }
    }

    private static bool CopyPixels(RdpFrameBuffer frames, byte[] pixels, out RdpFrameInfo info)
    {
        RdpFrameInfo advertised = frames.Info;
        IntPtr destination = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            bool copied = frames.TryCopy(destination, advertised.Width, advertised.Height,
                advertised.Stride, pixels.Length, out info);
            if (copied) Marshal.Copy(destination, pixels, 0, pixels.Length);
            return copied;
        }
        finally { Marshal.FreeHGlobal(destination); }
    }
}
