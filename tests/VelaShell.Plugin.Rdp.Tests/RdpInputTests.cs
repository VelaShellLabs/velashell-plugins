using Avalonia;
using Avalonia.Input;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpInputTests
{
    [TestMethod]
    public void LetterboxingDoesNotSendClicksOutsideDesktop()
    {
        Rect viewport = RdpInput.Viewport(new(1000, 1000), 1920, 1080, true);
        Assert.AreEqual(1000d, viewport.Width, 0.000001);
        Assert.AreEqual(562.5d, viewport.Height, 0.000001);
        Assert.IsFalse(RdpInput.Map(new(500, 10), viewport, 1920, 1080, false, out _, out _));
        Assert.IsTrue(RdpInput.Map(new(500, 500), viewport, 1920, 1080, false, out uint x, out uint y));
        Assert.AreEqual(960u, x);
        Assert.AreEqual(540u, y);
    }
    [TestMethod]
    public void CapturedDragClampsToLastValidPixelAndActualSizePreservesCoordinates()
    {
        Rect viewport = RdpInput.Viewport(new(800, 600), 1920, 1080, false);
        Assert.IsTrue(RdpInput.Map(new(-10000, 10000), viewport, 1920, 1080, true, out uint x, out uint y));
        Assert.AreEqual(0u, x);
        Assert.AreEqual(1079u, y);
        Assert.IsTrue(RdpInput.Map(new(400, 300), viewport, 1920, 1080, false, out x, out y));
        Assert.AreEqual(960u, x);
        Assert.AreEqual(540u, y);
        Assert.IsFalse(RdpInput.Map(default, default, 1920, 1080, true, out _, out _));
        Assert.IsFalse(RdpInput.Map(new(double.NaN, 0), viewport, 1920, 1080, true, out _, out _));
    }
    [TestMethod]
    public void PhysicalShortcutsDistinguishExtendedAndNormalKeys()
    {
        Assert.AreEqual(0x1du, RdpInput.ScanCode(PhysicalKey.ControlLeft));
        Assert.AreEqual(0x11du, RdpInput.ScanCode(PhysicalKey.ControlRight));
        Assert.AreEqual(0x153u, RdpInput.ScanCode(PhysicalKey.Delete));
        Assert.AreEqual(0x2fu, RdpInput.ScanCode(PhysicalKey.V));
        Assert.AreEqual(0x278u, RdpInput.Wheel(1, false));
        Assert.AreEqual(0x388u, RdpInput.Wheel(-1, false));
        Assert.AreEqual(0x478u, RdpInput.Wheel(1, true));
    }
}
