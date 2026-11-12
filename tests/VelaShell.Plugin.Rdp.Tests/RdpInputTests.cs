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

    [TestMethod]
    [DataRow(PhysicalKey.NumPad0, 0x52u)]
    [DataRow(PhysicalKey.NumPad7, 0x47u)]
    [DataRow(PhysicalKey.NumPadDecimal, 0x53u)]
    [DataRow(PhysicalKey.NumPadAdd, 0x4eu)]
    [DataRow(PhysicalKey.NumPadSubtract, 0x4au)]
    [DataRow(PhysicalKey.NumPadMultiply, 0x37u)]
    [DataRow(PhysicalKey.NumPadDivide, 0x135u)]
    [DataRow(PhysicalKey.NumPadEnter, 0x11cu)]
    [DataRow(PhysicalKey.NumLock, 0x45u)]
    public void KeypadKeysHaveScanCodes(PhysicalKey key, uint expected)
    {
        Assert.AreEqual(expected, RdpInput.ScanCode(key));
    }

    [TestMethod]
    public void EveryKeyOfAStandardKeyboardHasAScanCode()
    {
        // A key without a scan code is dropped unless it produces text, so the whole 104/105-key layout must map.
        PhysicalKey[] unmapped = Enum.GetValues<PhysicalKey>()
            .Where(k => k is >= PhysicalKey.A and <= PhysicalKey.Z
                or >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9
                or >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9
                or >= PhysicalKey.F1 and <= PhysicalKey.F24
                or PhysicalKey.NumPadAdd or PhysicalKey.NumPadSubtract or PhysicalKey.NumPadMultiply
                or PhysicalKey.NumPadDivide or PhysicalKey.NumPadDecimal or PhysicalKey.NumPadEnter
                or PhysicalKey.Enter or PhysicalKey.Escape or PhysicalKey.Backspace or PhysicalKey.Tab or PhysicalKey.Space
                or PhysicalKey.ArrowUp or PhysicalKey.ArrowDown or PhysicalKey.ArrowLeft or PhysicalKey.ArrowRight
                or PhysicalKey.Home or PhysicalKey.End or PhysicalKey.PageUp or PhysicalKey.PageDown
                or PhysicalKey.Insert or PhysicalKey.Delete or PhysicalKey.IntlBackslash)
            .Where(k => RdpInput.ScanCode(k) == 0)
            .ToArray();
        Assert.IsEmpty(unmapped, "Unmapped: " + string.Join(", ", unmapped));
    }
}
