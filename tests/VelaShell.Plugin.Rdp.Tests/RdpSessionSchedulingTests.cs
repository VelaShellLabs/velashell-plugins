using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpSessionSchedulingTests
{
    [TestMethod]
    public void ContinuouslyReadyNetworkAndInput_AlternateBoundedTurns()
    {
        bool preferNetwork = true;
        for (int turn = 0; turn < 100; turn++)
            Assert.AreEqual(turn % 2 == 0, RdpSession.SelectNetwork(true, true, ref preferNetwork));
        Assert.IsFalse(RdpSession.SelectNetwork(false, true, ref preferNetwork));
        Assert.IsTrue(RdpSession.SelectNetwork(true, false, ref preferNetwork));
        Assert.IsFalse(RdpSession.SelectNetwork(true, true, ref preferNetwork));
    }

    [TestMethod]
    public async Task DirtyBatch_NoConsumerSkipsFill_AttachPublishesFinalImageOnce()
    {
        await using var frames = new RdpFrameBuffer(2, 2, () => { });
        var publication = new RdpSession.FramePublication();
        int fills = 0;
        void Fill(byte[] pixels) { fills++; Array.Fill(pixels, (byte)77); }
        for (int update = 0; update < 100; update++)
        {
            publication.MarkDirty();
            Assert.IsFalse(publication.TryPublish(frames, Fill));
        }
        Assert.AreEqual(0, fills);
        publication.SetConsumer(true);
        Assert.IsTrue(publication.TryPublish(frames, Fill));
        Assert.IsFalse(publication.TryPublish(frames, Fill));
        Assert.AreEqual(1, fills);
        Assert.IsTrue(frames.TryAcquire(out var lease));
        using (lease) Assert.IsTrue(lease.Pixels.Span.ToArray().All(value => value == 77));
        publication.SetConsumer(false);
        publication.MarkDirty();
        Assert.IsFalse(publication.TryPublish(frames, Fill));
        publication.SetConsumer(true);
        Assert.IsTrue(publication.TryPublish(frames, Fill));
        Assert.AreEqual(2, fills);
    }

    [TestMethod]
    public async Task BusyBackBuffer_LeaseWakeRetriesLastDirtyFrameWithoutMoreGraphics()
    {
        int wakes = 0, fills = 0;
        await using var frames = new RdpFrameBuffer(2, 2, () => wakes++);
        var publication = new RdpSession.FramePublication();
        publication.SetConsumer(true);
        void Fill(byte[] pixels) { fills++; Array.Fill(pixels, checked((byte)fills)); }
        Assert.IsTrue(publication.TryPublish(frames, Fill));
        Assert.IsTrue(frames.TryAcquire(out var lease));
        publication.MarkDirty();
        Assert.IsTrue(publication.TryPublish(frames, Fill));
        for (int update = 0; update < 20; update++) publication.MarkDirty();
        Assert.IsFalse(publication.TryPublish(frames, Fill));
        Assert.IsTrue(frames.Pending);
        Assert.AreEqual(2, fills);
        lease.Dispose();
        Assert.AreEqual(1, wakes);
        Assert.IsTrue(publication.TryPublish(frames, Fill));
        Assert.IsFalse(frames.Pending);
        Assert.IsFalse(publication.TryPublish(frames, Fill));
        Assert.AreEqual(3, fills);
    }

    [TestMethod]
    public async Task ResizeAndAttachDuringFill_DoNotClearNewDirtyGeneration()
    {
        await using var frames = new RdpFrameBuffer(2, 2, () => { });
        var publication = new RdpSession.FramePublication();
        publication.SetConsumer(true);
        Assert.IsTrue(publication.TryPublish(frames, _ =>
        {
            publication.SetConsumer(false);
            publication.SetConsumer(true);
        }));
        Assert.IsTrue(publication.TryPublish(frames, _ => { }));
        frames.Resize(3, 1);
        publication.MarkDirty();
        Assert.IsTrue(publication.TryPublish(frames, pixels => Assert.HasCount(12, pixels)));
        Assert.AreEqual(3, frames.Info.Width);
        Assert.AreEqual(1, frames.Info.Height);
    }
}
