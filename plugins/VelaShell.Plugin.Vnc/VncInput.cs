using System.Text;
using Avalonia;
using Avalonia.Input;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// Keyboard, pointer and layout helpers for the desktop control. VNC keys are X11 keysyms (RFC 6143 §7.5.4): typed text
/// goes as the keysym of each character, so it arrives as typed whatever the two keyboard layouts are; keys without
/// text (arrows, function keys, modifiers) and shortcuts go as the keysym of the physical key on a US layout plus its
/// XT scan code, which servers with the QEMU extended key event use instead.
/// </summary>
internal static class VncInput
{
    // ---- view geometry (same rules as the RDP desktop) ----

    /// <summary>
    /// Where the desktop is drawn, in DIPs. Without fitting, or when the desktop is (almost) the view's size in device
    /// pixels, it is drawn one device pixel per remote pixel, its origin on the device-pixel grid; otherwise scaled to fit.
    /// </summary>
    internal static Rect Viewport(Size available, int width, int height, bool fit, double renderScaling = 1)
    {
        if (width <= 0 || height <= 0 || available.Width <= 0 || available.Height <= 0
            || !double.IsFinite(available.Width) || !double.IsFinite(available.Height))
        {
            return default;
        }

        double s = renderScaling > 0 && double.IsFinite(renderScaling) ? renderScaling : 1;
        double nativeWidth = width / s, nativeHeight = height / s;
        double scale = !fit || IsNearlyNative(available, width, height, s)
            ? 1
            : Math.Min(available.Width / nativeWidth, available.Height / nativeHeight);
        double w = nativeWidth * scale, h = nativeHeight * scale;
        double x = Math.Max(0, (available.Width - w) / 2), y = Math.Max(0, (available.Height - h) / 2);
        if (scale == 1)
        {
            x = Math.Round(x * s) / s;
            y = Math.Round(y * s) / s;
        }

        return new Rect(x, y, w, h);
    }

    internal static bool IsNearlyNative(Size available, int width, int height, double renderScaling)
    {
        double gapX = available.Width * renderScaling - width, gapY = available.Height * renderScaling - height;
        return gapX is >= -1 and <= 16 && gapY is >= -1 and <= 16;
    }

    internal static bool Map(Point point, Rect viewport, int width, int height, bool captured, out int x, out int y)
    {
        x = y = 0;
        if (viewport.Width <= 0 || viewport.Height <= 0 || width <= 0 || height <= 0
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || (!captured && (point.X < viewport.X || point.Y < viewport.Y || point.X >= viewport.Right || point.Y >= viewport.Bottom)))
        {
            return false;
        }

        x = (int)Math.Clamp(Math.Floor((point.X - viewport.X) * width / viewport.Width), 0, width - 1);
        y = (int)Math.Clamp(Math.Floor((point.Y - viewport.Y) * height / viewport.Height), 0, height - 1);
        return true;
    }

    // ---- keyboard ----

    internal enum KeyboardPlatform { Windows, MacOS, Other }

    internal static KeyboardPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? KeyboardPlatform.Windows : OperatingSystem.IsMacOS() ? KeyboardPlatform.MacOS : KeyboardPlatform.Other;

    internal const uint ControlL = 0xffe3, ControlR = 0xffe4, ShiftL = 0xffe1, ShiftR = 0xffe2, AltL = 0xffe9, AltR = 0xffea,
        SuperL = 0xffeb, SuperR = 0xffec, Return = 0xff0d, Tab = 0xff09, Delete = 0xffff, Escape = 0xff1b, BackSpace = 0xff08,
        Print = 0xff61, F2 = 0xffbf, F4 = 0xffc1;

    /// <summary>
    /// The keysym and scan code sent for a physical key. On macOS, Command goes as Control when
    /// <paramref name="commandAsControl"/> (Cmd+C on a Windows or Linux desktop), otherwise as Super (a Mac server
    /// maps Super to Command).
    /// </summary>
    internal static (uint Keysym, uint ScanCode) Key(PhysicalKey key, KeyboardPlatform platform, bool commandAsControl)
    {
        if (platform == KeyboardPlatform.MacOS && commandAsControl && key is PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            return key == PhysicalKey.MetaLeft ? (ControlL, 0x1du) : (ControlR, 0x11du);
        }

        return Keys.TryGetValue(key, out (uint Keysym, uint ScanCode) entry) ? entry : (0, 0);
    }

    /// <summary>The keysym of one character (rune): Latin-1 directly, everything else as 0x01000000 + code point.</summary>
    internal static uint CharacterKeysym(Rune rune) => rune.Value switch
    {
        '\n' => Return,
        '\t' => Tab,
        >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF => (uint)rune.Value,
        _ => 0x01000000u | (uint)rune.Value
    };

    /// <summary>Line breaks become '\n'; control characters other than '\n' and '\t' are dropped.</summary>
    internal static string NormalizeTypedText(string text) =>
        string.Concat(text.ReplaceLineEndings("\n").Where(static c => !char.IsControl(c) || c is '\n' or '\t'));

    internal static bool IsTextKey(KeyEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.KeySymbol))
        {
            return !e.KeySymbol.Any(char.IsControl);
        }

        return e.Key is >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z or >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9
            or Avalonia.Input.Key.Space or Avalonia.Input.Key.Oem1 or Avalonia.Input.Key.OemPlus or Avalonia.Input.Key.OemComma
            or Avalonia.Input.Key.OemMinus or Avalonia.Input.Key.OemPeriod or Avalonia.Input.Key.Oem2 or Avalonia.Input.Key.Oem3
            or Avalonia.Input.Key.Oem4 or Avalonia.Input.Key.Oem5 or Avalonia.Input.Key.Oem6 or Avalonia.Input.Key.Oem7
            or Avalonia.Input.Key.Oem8 or Avalonia.Input.Key.Oem102;
    }

    /// <summary>
    /// Whether a key press goes as a shortcut (physical keysym) rather than as typed text. A chord that types a printable
    /// character on the local layout is text: Option on macOS and AltGr on Windows (reported as Ctrl+Alt). Elsewhere
    /// AltGr is its own modifier, so Ctrl+Alt stays a shortcut.
    /// </summary>
    internal static bool IsShortcut(KeyModifiers modifiers, string? keySymbol, KeyboardPlatform platform)
    {
        KeyModifiers chord = modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta);
        if (chord == KeyModifiers.None)
        {
            return false;
        }

        bool printable = !string.IsNullOrEmpty(keySymbol) && !keySymbol.Any(char.IsControl);
        return !printable || platform switch
        {
            KeyboardPlatform.MacOS => chord != KeyModifiers.Alt,
            KeyboardPlatform.Windows => chord != (KeyModifiers.Control | KeyModifiers.Alt),
            _ => true
        };
    }

    /// <summary>Left/right Ctrl and Alt: lifted around text typed with Option or AltGr.</summary>
    internal static bool IsCharacterModifier(uint keysym) => keysym is ControlL or ControlR or AltL or AltR;

    internal static bool IsModifier(uint keysym) => keysym is >= ShiftL and <= 0xffee or 0xff7f or 0xff14;

    /// <summary>Turns wheel deltas (fractional notches on precise devices) into whole notches; the remainder carries over.</summary>
    internal sealed class WheelAccumulator
    {
        private double _carry;

        internal int Add(double notches)
        {
            if (!double.IsFinite(notches)) { return 0; }
            if (_carry != 0 && Math.Sign(_carry) != Math.Sign(notches)) { _carry = 0; }
            double total = _carry + notches;
            int whole = (int)Math.Clamp(Math.Truncate(total), -100, 100);
            _carry = total - whole;
            return whole;
        }
    }

    private static readonly Dictionary<PhysicalKey, (uint Keysym, uint ScanCode)> Keys = new()
    {
        [PhysicalKey.Escape] = (Escape, 0x01),
        [PhysicalKey.Digit1] = ('1', 0x02),
        [PhysicalKey.Digit2] = ('2', 0x03),
        [PhysicalKey.Digit3] = ('3', 0x04),
        [PhysicalKey.Digit4] = ('4', 0x05),
        [PhysicalKey.Digit5] = ('5', 0x06),
        [PhysicalKey.Digit6] = ('6', 0x07),
        [PhysicalKey.Digit7] = ('7', 0x08),
        [PhysicalKey.Digit8] = ('8', 0x09),
        [PhysicalKey.Digit9] = ('9', 0x0a),
        [PhysicalKey.Digit0] = ('0', 0x0b),
        [PhysicalKey.Minus] = ('-', 0x0c),
        [PhysicalKey.Equal] = ('=', 0x0d),
        [PhysicalKey.Backspace] = (BackSpace, 0x0e),
        [PhysicalKey.Tab] = (Tab, 0x0f),
        [PhysicalKey.Q] = ('q', 0x10),
        [PhysicalKey.W] = ('w', 0x11),
        [PhysicalKey.E] = ('e', 0x12),
        [PhysicalKey.R] = ('r', 0x13),
        [PhysicalKey.T] = ('t', 0x14),
        [PhysicalKey.Y] = ('y', 0x15),
        [PhysicalKey.U] = ('u', 0x16),
        [PhysicalKey.I] = ('i', 0x17),
        [PhysicalKey.O] = ('o', 0x18),
        [PhysicalKey.P] = ('p', 0x19),
        [PhysicalKey.BracketLeft] = ('[', 0x1a),
        [PhysicalKey.BracketRight] = (']', 0x1b),
        [PhysicalKey.Enter] = (Return, 0x1c),
        [PhysicalKey.ControlLeft] = (ControlL, 0x1d),
        [PhysicalKey.A] = ('a', 0x1e),
        [PhysicalKey.S] = ('s', 0x1f),
        [PhysicalKey.D] = ('d', 0x20),
        [PhysicalKey.F] = ('f', 0x21),
        [PhysicalKey.G] = ('g', 0x22),
        [PhysicalKey.H] = ('h', 0x23),
        [PhysicalKey.J] = ('j', 0x24),
        [PhysicalKey.K] = ('k', 0x25),
        [PhysicalKey.L] = ('l', 0x26),
        [PhysicalKey.Semicolon] = (';', 0x27),
        [PhysicalKey.Quote] = ('\'', 0x28),
        [PhysicalKey.Backquote] = ('`', 0x29),
        [PhysicalKey.ShiftLeft] = (ShiftL, 0x2a),
        [PhysicalKey.Backslash] = ('\\', 0x2b),
        [PhysicalKey.Z] = ('z', 0x2c),
        [PhysicalKey.X] = ('x', 0x2d),
        [PhysicalKey.C] = ('c', 0x2e),
        [PhysicalKey.V] = ('v', 0x2f),
        [PhysicalKey.B] = ('b', 0x30),
        [PhysicalKey.N] = ('n', 0x31),
        [PhysicalKey.M] = ('m', 0x32),
        [PhysicalKey.Comma] = (',', 0x33),
        [PhysicalKey.Period] = ('.', 0x34),
        [PhysicalKey.Slash] = ('/', 0x35),
        [PhysicalKey.ShiftRight] = (ShiftR, 0x36),
        [PhysicalKey.NumPadMultiply] = (0xffaa, 0x37),
        [PhysicalKey.AltLeft] = (AltL, 0x38),
        [PhysicalKey.Space] = (' ', 0x39),
        [PhysicalKey.CapsLock] = (0xffe5, 0x3a),
        [PhysicalKey.F1] = (0xffbe, 0x3b),
        [PhysicalKey.F2] = (F2, 0x3c),
        [PhysicalKey.F3] = (0xffc0, 0x3d),
        [PhysicalKey.F4] = (F4, 0x3e),
        [PhysicalKey.F5] = (0xffc2, 0x3f),
        [PhysicalKey.F6] = (0xffc3, 0x40),
        [PhysicalKey.F7] = (0xffc4, 0x41),
        [PhysicalKey.F8] = (0xffc5, 0x42),
        [PhysicalKey.F9] = (0xffc6, 0x43),
        [PhysicalKey.F10] = (0xffc7, 0x44),
        [PhysicalKey.NumLock] = (0xff7f, 0x45),
        [PhysicalKey.ScrollLock] = (0xff14, 0x46),
        // Keypad without NumLock: its navigation keysyms (with NumLock the keys type digits through the text path).
        [PhysicalKey.NumPad7] = (0xff95, 0x47),
        [PhysicalKey.NumPad8] = (0xff97, 0x48),
        [PhysicalKey.NumPad9] = (0xff9a, 0x49),
        [PhysicalKey.NumPadSubtract] = (0xffad, 0x4a),
        [PhysicalKey.NumPad4] = (0xff96, 0x4b),
        [PhysicalKey.NumPad5] = (0xff9d, 0x4c),
        [PhysicalKey.NumPad6] = (0xff98, 0x4d),
        [PhysicalKey.NumPadAdd] = (0xffab, 0x4e),
        [PhysicalKey.NumPad1] = (0xff9c, 0x4f),
        [PhysicalKey.NumPad2] = (0xff99, 0x50),
        [PhysicalKey.NumPad3] = (0xff9b, 0x51),
        [PhysicalKey.NumPad0] = (0xff9e, 0x52),
        [PhysicalKey.NumPadDecimal] = (0xff9f, 0x53),
        [PhysicalKey.IntlBackslash] = ('<', 0x56),
        [PhysicalKey.F11] = (0xffc8, 0x57),
        [PhysicalKey.F12] = (0xffc9, 0x58),
        [PhysicalKey.NumPadEnter] = (0xff8d, 0x11c),
        [PhysicalKey.ControlRight] = (ControlR, 0x11d),
        [PhysicalKey.NumPadDivide] = (0xffaf, 0x135),
        [PhysicalKey.PrintScreen] = (Print, 0x137),
        [PhysicalKey.AltRight] = (AltR, 0x138),
        [PhysicalKey.Pause] = (0xff13, 0),   // E1-prefixed in XT: no single scan code, keysym only
        [PhysicalKey.Home] = (0xff50, 0x147),
        [PhysicalKey.ArrowUp] = (0xff52, 0x148),
        [PhysicalKey.PageUp] = (0xff55, 0x149),
        [PhysicalKey.ArrowLeft] = (0xff51, 0x14b),
        [PhysicalKey.ArrowRight] = (0xff53, 0x14d),
        [PhysicalKey.End] = (0xff57, 0x14f),
        [PhysicalKey.ArrowDown] = (0xff54, 0x150),
        [PhysicalKey.PageDown] = (0xff56, 0x151),
        [PhysicalKey.Insert] = (0xff63, 0x152),
        [PhysicalKey.Delete] = (Delete, 0x153),
        [PhysicalKey.MetaLeft] = (SuperL, 0x15b),
        [PhysicalKey.MetaRight] = (SuperR, 0x15c),
        [PhysicalKey.ContextMenu] = (0xff67, 0x15d),
        [PhysicalKey.NumPadEqual] = (0xffbd, 0x59),
        [PhysicalKey.F13] = (0xffca, 0x64),
        [PhysicalKey.F14] = (0xffcb, 0x65),
        [PhysicalKey.F15] = (0xffcc, 0x66),
        [PhysicalKey.F16] = (0xffcd, 0x67),
        [PhysicalKey.F17] = (0xffce, 0x68),
        [PhysicalKey.F18] = (0xffcf, 0x69),
        [PhysicalKey.F19] = (0xffd0, 0x6a),
        [PhysicalKey.F20] = (0xffd1, 0x6b),
        [PhysicalKey.F21] = (0xffd2, 0x6c),
        [PhysicalKey.F22] = (0xffd3, 0x6d),
        [PhysicalKey.F23] = (0xffd4, 0x6e),
        [PhysicalKey.F24] = (0xffd5, 0x76)
    };
}
