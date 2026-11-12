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
    public void KeypadKeysHaveScanCodes(PhysicalKey key, uint expected) => Assert.AreEqual(expected, RdpInput.ScanCode(key));

    [TestMethod]
    public void EveryKeyOfAStandardKeyboardHasAScanCode()
    {
        // A key without a scan code is dropped unless it produces text, so the whole 104/105-key layout must map.
        PhysicalKey[] unmapped = [.. Enum.GetValues<PhysicalKey>()
            .Where(static k => k is >= PhysicalKey.A and <= PhysicalKey.Z
                or >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9
                or >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9
                or >= PhysicalKey.F1 and <= PhysicalKey.F24
                or PhysicalKey.NumPadAdd or PhysicalKey.NumPadSubtract or PhysicalKey.NumPadMultiply
                or PhysicalKey.NumPadDivide or PhysicalKey.NumPadDecimal or PhysicalKey.NumPadEnter
                or PhysicalKey.Enter or PhysicalKey.Escape or PhysicalKey.Backspace or PhysicalKey.Tab or PhysicalKey.Space
                or PhysicalKey.ArrowUp or PhysicalKey.ArrowDown or PhysicalKey.ArrowLeft or PhysicalKey.ArrowRight
                or PhysicalKey.Home or PhysicalKey.End or PhysicalKey.PageUp or PhysicalKey.PageDown
                or PhysicalKey.Insert or PhysicalKey.Delete or PhysicalKey.IntlBackslash)
            .Where(static k => RdpInput.ScanCode(k) == 0)];
        Assert.IsEmpty(unmapped, "Unmapped: " + string.Join(", ", unmapped));
    }

    private const RdpInput.KeyboardPlatform Mac = RdpInput.KeyboardPlatform.MacOS;
    private const RdpInput.KeyboardPlatform Win = RdpInput.KeyboardPlatform.Windows;
    private const RdpInput.KeyboardPlatform Linux = RdpInput.KeyboardPlatform.Other;

    [TestMethod]
    public void MacCommandIsSentAsControlAndOtherPlatformsKeepTheWindowsKey()
    {
        Assert.AreEqual(0x1du, RdpInput.ScanCode(PhysicalKey.MetaLeft, Mac));
        Assert.AreEqual(0x11du, RdpInput.ScanCode(PhysicalKey.MetaRight, Mac));
        Assert.AreEqual(0x1du, RdpInput.ScanCode(PhysicalKey.ControlLeft, Mac));
        Assert.AreEqual(0x15bu, RdpInput.ScanCode(PhysicalKey.MetaLeft, Win));
        Assert.AreEqual(0x15bu, RdpInput.ScanCode(PhysicalKey.MetaLeft, Linux));

        Assert.IsTrue(RdpInput.IsPaste(PhysicalKey.V, KeyModifiers.Meta, Mac), "Cmd+V pastes on a Mac.");
        Assert.IsTrue(RdpInput.IsPaste(PhysicalKey.V, KeyModifiers.Control, Mac));
        Assert.IsTrue(RdpInput.IsPaste(PhysicalKey.V, KeyModifiers.Control, Win));
        Assert.IsFalse(RdpInput.IsPaste(PhysicalKey.V, KeyModifiers.Meta, Win), "Win+V is the clipboard history, not paste.");
        Assert.IsFalse(RdpInput.IsPaste(PhysicalKey.V, KeyModifiers.Control | KeyModifiers.Alt, Win));
    }

    [TestMethod]
    [DataRow(nameof(RdpInput.KeyboardPlatform.MacOS), KeyModifiers.Alt, "@", false, DisplayName = "Option+L on a German Mac types @")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.MacOS), KeyModifiers.Alt, null, true, DisplayName = "Option+F4 is Alt+F4")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.MacOS), KeyModifiers.Alt, "	", true, DisplayName = "Option+Tab is Alt+Tab")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.MacOS), KeyModifiers.Meta, "c", true, DisplayName = "Cmd+C is a shortcut")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.MacOS), KeyModifiers.Alt | KeyModifiers.Meta, "@", true, DisplayName = "Cmd+Option is a shortcut")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.Windows), KeyModifiers.Control | KeyModifiers.Alt, "@", false, DisplayName = "AltGr+Q on a German PC types @")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.Windows), KeyModifiers.Control | KeyModifiers.Alt, null, true, DisplayName = "Ctrl+Alt+End stays a shortcut")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.Windows), KeyModifiers.Alt, "a", true, DisplayName = "Alt+A is a menu shortcut on Windows")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.Other), KeyModifiers.Control | KeyModifiers.Alt, "t", true, DisplayName = "Ctrl+Alt+T stays a shortcut on Linux")]
    [DataRow(nameof(RdpInput.KeyboardPlatform.Other), KeyModifiers.Shift, "A", false, DisplayName = "Shift alone is typing")]
    public void OnlyModifierChordsThatDoNotTypeAreShortcuts(string platform, KeyModifiers modifiers, string? symbol, bool shortcut) => Assert.AreEqual(shortcut, RdpInput.IsShortcut(modifiers, symbol, Enum.Parse<RdpInput.KeyboardPlatform>(platform)));

    [TestMethod]
    public void ReleasingCommandOnlyLetsGoOfOrdinaryKeys()
    {
        Assert.IsTrue(RdpInput.IsModifierScanCode(RdpInput.ScanCode(PhysicalKey.ShiftLeft, Mac)));
        Assert.IsTrue(RdpInput.IsModifierScanCode(RdpInput.ScanCode(PhysicalKey.MetaLeft, Mac)));
        Assert.IsTrue(RdpInput.IsModifierScanCode(RdpInput.ScanCode(PhysicalKey.CapsLock, Mac)));
        Assert.IsFalse(RdpInput.IsModifierScanCode(RdpInput.ScanCode(PhysicalKey.C, Mac)));
        Assert.IsFalse(RdpInput.IsModifierScanCode(RdpInput.ScanCode(PhysicalKey.ArrowLeft, Mac)));
    }

    [TestMethod]
    public void PreciseWheelDeltasAccumulateInsteadOfRoundingToNothing()
    {
        var wheel = new RdpInput.WheelAccumulator();
        int sent = 0;
        for (int i = 0; i < 100; i++)
        {
            // A trackpad's slow scroll: 1/300 of a notch per event, each below half a 1/120 step.
            sent += wheel.Add(1.0 / 300).Sum();
        }
        Assert.AreEqual(40, sent, "100 × 0.4 steps is 40 steps, not 0.");
    }

    [TestMethod]
    public void AFastFlingIsSplitIntoEventsTheFieldCanCarry()
    {
        var wheel = new RdpInput.WheelAccumulator();
        List<int> rotations = wheel.Add(-5);   // 600 steps
        Assert.AreSequenceEqual([-255, -255, -90], rotations);
        Assert.AreEqual(0x0200u | 0x0100u | 0x01u, RdpInput.WheelFlags(-255, false));
        Assert.AreEqual(0x0400u | 0x78u, RdpInput.WheelFlags(120, true));
    }

    [TestMethod]
    public void ReversingTheWheelDropsTheCarriedFraction()
    {
        var wheel = new RdpInput.WheelAccumulator();
        Assert.IsEmpty(wheel.Add(0.004));                 // 0.48 of a step carried
        Assert.AreSequenceEqual([-1], wheel.Add(-0.01));   // -1.2 steps, not -0.72
        Assert.IsEmpty(wheel.Add(double.NaN));
    }
}
