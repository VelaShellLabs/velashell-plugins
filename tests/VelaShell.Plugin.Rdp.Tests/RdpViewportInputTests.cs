using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.Plugin.Rdp.Ui;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpViewportInputTests
{
    [TestMethod]
    public void FitLetterboxAndActualDpiScrollInverseMap()
    {
        RdpViewport fit = RdpViewport.Create(new Size(1000, 800), 1920, 1080, RdpScaleMode.Fit, 2);
        Assert.AreEqual(118.75, fit.Desktop.Y, 0.001);
        Assert.IsFalse(fit.TryMap(new Point(500, 50), false, out _));
        Assert.IsTrue(fit.TryMap(new Point(500, 400), false, out PixelPoint center));
        Assert.AreEqual(new PixelPoint(960, 540), center);
        Assert.IsTrue(fit.TryMap(new Point(-100, 1000), true, out PixelPoint edge));
        Assert.AreEqual(new PixelPoint(0, 1079), edge);
        foreach (double dpi in new[] { 1d, 1.25d, 2d })
        {
            RdpViewport actual = RdpViewport.Create(new Size(500, 300), 1920, 1080, RdpScaleMode.Actual, dpi, new Vector(100, 40));
            Assert.AreEqual(1920 / dpi, actual.Desktop.Width, 0.001);
            Assert.IsTrue(actual.TryMap(new Point(20, 10), false, out PixelPoint pixel));
            Assert.AreEqual(new PixelPoint((int)(120 * dpi), (int)(50 * dpi)), pixel);
        }
    }

    [TestMethod]
    public void PhysicalScancodesDistinguishSidesNumpadAndExtendedKeys()
    {
        Assert.AreEqual((ushort)0x1d, RdpInputMapper.ScanCode(PhysicalKey.ControlLeft));
        Assert.AreEqual((ushort)0x11d, RdpInputMapper.ScanCode(PhysicalKey.ControlRight));
        Assert.AreEqual((ushort)0x2a, RdpInputMapper.ScanCode(PhysicalKey.ShiftLeft));
        Assert.AreEqual((ushort)0x36, RdpInputMapper.ScanCode(PhysicalKey.ShiftRight));
        Assert.AreEqual((ushort)0x38, RdpInputMapper.ScanCode(PhysicalKey.AltLeft));
        Assert.AreEqual((ushort)0x138, RdpInputMapper.ScanCode(PhysicalKey.AltRight));
        Assert.AreEqual((ushort)0x11c, RdpInputMapper.ScanCode(PhysicalKey.NumPadEnter));
        Assert.AreEqual((ushort)0x14b, RdpInputMapper.ScanCode(PhysicalKey.ArrowLeft));
        Assert.AreEqual((ushort)0, RdpInputMapper.ScanCode(PhysicalKey.Pause));
    }

    [TestMethod]
    public void CopyRowsRespectsBothSourceAndFramebufferPadding()
    {
        byte[] pixels = [1, 2, 3, 4, 5, 6, 7, 8, 90, 90, 90, 90, 11, 12, 13, 14, 15, 16, 17, 18, 90, 90, 90, 90];
        IntPtr destination = Marshal.AllocHGlobal(32);
        try
        {
            byte[] initialized = Enumerable.Repeat((byte)77, 32).ToArray();
            Marshal.Copy(initialized, 0, destination, 32);
            RdpDesktopSurface.CopyRows(pixels, new(2, 2, 12, 1), destination, 16);
            byte[] result = new byte[32];
            Marshal.Copy(destination, result, 0, 32);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, result[..8]);
            CollectionAssert.AreEqual(new byte[] { 11, 12, 13, 14, 15, 16, 17, 18 }, result[16..24]);
            Assert.IsTrue(result[8..16].All(x => x == 77));
            Assert.IsTrue(result[24..32].All(x => x == 77));
        }
        finally { Marshal.FreeHGlobal(destination); }
    }

    [TestMethod]
    public async Task QueueOverflowReleasesAndRejectsUntilReconnect()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new RdpUiFakeSession
        {
            InputHandler = (input, _) =>
            {
                if (input.Kind == RdpInputKind.KeyDown && input.ScanCode == 0x1e)
                { entered.TrySetResult(); return new ValueTask(blocked.Task); }
                return ValueTask.CompletedTask;
            }
        };
        int failed = 0;
        var queue = new RdpInputQueue(session, () => Interlocked.Increment(ref failed));
        Assert.IsTrue(queue.Enqueue(new(RdpInputKind.KeyDown, 0x1e)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 128; i++) Assert.IsTrue(queue.Enqueue(new(RdpInputKind.KeyDown, 0x30)));
        Assert.IsFalse(queue.Enqueue(new(RdpInputKind.KeyUp, 0x1e)));
        Assert.IsFalse(queue.Enqueue(new(RdpInputKind.KeyDown, 0x2e)));
        blocked.SetResult();
        await queue.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, failed);
        CollectionAssert.AreEqual(new[] { RdpInputKind.KeyDown, RdpInputKind.ReleaseAll }, session.Inputs.Select(x => x.Kind).ToArray());
    }

    [TestMethod]
    public async Task QueueFailureAttemptsReleaseThenDisconnectAndObservesCleanup()
    {
        var session = new RdpUiFakeSession
        {
            InputHandler = (input, _) => input.Kind == RdpInputKind.KeyDown
                ? ValueTask.FromException(new IOException("test input failure")) : ValueTask.CompletedTask
        };
        int failed = 0;
        var queue = new RdpInputQueue(session, () => Interlocked.Increment(ref failed));
        queue.Enqueue(new(RdpInputKind.KeyDown, 0x1e));
        await queue.Drained.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(RdpSessionState.Disconnected, session.Status.State);
        CollectionAssert.AreEqual(new[] { RdpInputKind.KeyDown, RdpInputKind.ReleaseAll }, session.Inputs.Select(x => x.Kind).ToArray());
        Assert.IsTrue(failed >= 1);
    }
}
