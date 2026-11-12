using Avalonia;
using Avalonia.Input;

namespace VelaShell.Plugin.Rdp;

internal static class RdpInput
{
    /// <summary>
    /// Where the remote desktop is drawn, in DIPs. A desktop that is (almost) exactly the view's size in device pixels
    /// — the normal case once dynamic resolution has matched it — is drawn at one device pixel per remote pixel,
    /// centred with its origin on the device-pixel grid: resampling by a factor like 1080/1079 used to soften every
    /// glyph. Only a real mismatch (a server that cannot follow the window) is scaled to fit.
    /// </summary>
    internal static Rect Viewport(Size available, int width, int height, bool fit, double renderScaling = 1)
    {
        if (width <= 0 || height <= 0 || available.Width <= 0 || available.Height <= 0
            || !double.IsFinite(available.Width) || !double.IsFinite(available.Height))
        {
            return default;
        }

        double s = renderScaling > 0 && double.IsFinite(renderScaling) ? renderScaling : 1;
        double nativeWidth = width / s, nativeHeight = height / s;   // 1:1 device pixels, in DIPs
        double scale = !fit || IsNearlyNative(available, width, height, s)
            ? 1
            : Math.Min(available.Width / nativeWidth, available.Height / nativeHeight);
        double w = nativeWidth * scale, h = nativeHeight * scale;
        double x = (available.Width - w) / 2, y = (available.Height - h) / 2;
        if (scale == 1)
        {
            x = Math.Round(x * s) / s;
            y = Math.Round(y * s) / s;
        }

        return new Rect(x, y, w, h);
    }

    /// <summary>The desktop is within a few device pixels of the view (up to 16 smaller, at most 1 larger).</summary>
    internal static bool IsNearlyNative(Size available, int width, int height, double renderScaling)
    {
        double gapX = available.Width * renderScaling - width, gapY = available.Height * renderScaling - height;
        return gapX is >= -1 and <= 16 && gapY is >= -1 and <= 16;
    }

    internal static bool Map(Point point, Rect viewport, int width, int height, bool captured, out uint x, out uint y)
    {
        x = y = 0;
        if (viewport.Width <= 0 || viewport.Height <= 0 || width <= 0 || height <= 0
            || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || (!captured && (point.X < viewport.X || point.Y < viewport.Y || point.X >= viewport.Right || point.Y >= viewport.Bottom)))
        {
            return false;
        }

        x = (uint)Math.Clamp(Math.Floor((point.X - viewport.X) * width / viewport.Width), 0, width - 1);
        y = (uint)Math.Clamp(Math.Floor((point.Y - viewport.Y) * height / viewport.Height), 0, height - 1);
        return true;
    }

    internal static bool IsTextKey(KeyEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.KeySymbol))
        {
            return !e.KeySymbol.Any(char.IsControl);
        }

        // IMEs and dead keys can omit KeySymbol before the first preedit update. Use the
        // logical key so NumLock-off keypad navigation remains a physical key event.
        return e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9
            or >= Key.NumPad0 and <= Key.NumPad9
            or Key.Space or Key.Multiply or Key.Add or Key.Separator or Key.Subtract or Key.Decimal or Key.Divide
            or Key.Oem1 or Key.OemPlus or Key.OemComma or Key.OemMinus or Key.OemPeriod
            or Key.Oem2 or Key.Oem3 or Key.Oem4 or Key.Oem5 or Key.Oem6 or Key.Oem7 or Key.Oem8 or Key.Oem102;
    }

    /// <summary>The local keyboard conventions that change how keys map onto a remote Windows desktop.</summary>
    internal enum KeyboardPlatform { Windows, MacOS, Other }

    internal static KeyboardPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? KeyboardPlatform.Windows : OperatingSystem.IsMacOS() ? KeyboardPlatform.MacOS : KeyboardPlatform.Other;

    internal static uint ScanCode(PhysicalKey key) => ScanCode(key, CurrentPlatform);

    /// <summary>
    /// The scan code sent for <paramref name="key" />. On macOS, Command is sent as Control: Mac users press
    /// Cmd+C/V/Z/A for what Windows does with Ctrl, so the remote desktop receives the shortcut they meant. The Windows
    /// key remains reachable through the remote-keys ball.
    /// </summary>
    internal static uint ScanCode(PhysicalKey key, KeyboardPlatform platform) => (platform, key) switch
    {
        (KeyboardPlatform.MacOS, PhysicalKey.MetaLeft) => Codes[PhysicalKey.ControlLeft],
        (KeyboardPlatform.MacOS, PhysicalKey.MetaRight) => Codes[PhysicalKey.ControlRight],
        _ => Codes.GetValueOrDefault(key)
    };

    /// <summary>The modifiers as the remote desktop sees them: on macOS Command counts as Control.</summary>
    internal static KeyModifiers RemoteModifiers(KeyModifiers modifiers, KeyboardPlatform platform) =>
        platform == KeyboardPlatform.MacOS && modifiers.HasFlag(KeyModifiers.Meta)
            ? (modifiers & ~KeyModifiers.Meta) | KeyModifiers.Control
            : modifiers;

    /// <summary>
    /// Whether a key press goes to the remote as a shortcut (paired physical scan codes) rather than as typed text.
    /// A modifier chord that types a printable character on the local layout is text: Option on macOS (Option+L is
    /// "@" on a German Mac) and AltGr on Windows, which Windows reports as Ctrl+Alt. Sent as scan codes those would
    /// reach the remote as Alt+L or Ctrl+Alt+Q unless both ends used the same layout. Elsewhere (Linux) AltGr is its
    /// own modifier, so Ctrl+Alt stays a shortcut.
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

    /// <summary>Ctrl+V on the remote (Cmd+V on macOS): the local clipboard is published before the key is sent.</summary>
    internal static bool IsPaste(PhysicalKey key, KeyModifiers modifiers, KeyboardPlatform platform)
    {
        KeyModifiers remote = RemoteModifiers(modifiers, platform);
        return key == PhysicalKey.V && remote.HasFlag(KeyModifiers.Control) && (remote & (KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
    }

    /// <summary>Scan codes of modifier and lock keys, which a release of Command on macOS must not let go of.</summary>
    internal static bool IsModifierScanCode(uint code) => code is 0x1d or 0x11d or 0x2a or 0x36 or 0x38 or 0x138 or 0x15b or 0x15c or 0x3a or 0x45 or 0x46;
    internal static uint Wheel(double delta, bool horizontal) =>
        WheelFlags(Math.Clamp((int)Math.Round(delta * 120), -MaxWheelRotation, MaxWheelRotation), horizontal);

    /// <summary>Largest rotation one fast-path wheel event carries (a 9-bit two's-complement field, MS-RDPBCGR 2.2.8.1.2.2.3).</summary>
    internal const int MaxWheelRotation = 255;

    /// <summary>PTRFLAGS_WHEEL or PTRFLAGS_HWHEEL with the rotation (in 1/120 notches) as WheelRotationMask plus the negative flag.</summary>
    internal static uint WheelFlags(int rotation, bool horizontal) =>
        (horizontal ? 0x0400u : 0x0200u) | (rotation < 0 ? 0x0100u : 0u) | ((uint)rotation & 0xff);

    /// <summary>
    /// Turns wheel deltas (in notches, fractional on precise devices) into whole wheel events. Each event used to be
    /// rounded on its own: a macOS trackpad or Linux smooth-scroll device sends many deltas below half a 1/120 step,
    /// which all rounded to zero (slow scrolling and momentum tails were lost), and a fast fling above ~2 notches was
    /// clamped to 255. The fraction now carries over to the next delta, and large rotations are split into several
    /// events. A change of direction drops the carried fraction so reversing responds at once.
    /// </summary>
    internal sealed class WheelAccumulator
    {
        private double _carry;

        internal List<int> Add(double notches)
        {
            if (double.IsNaN(notches) || double.IsInfinity(notches))
            {
                return [];
            }

            if (_carry != 0 && Math.Sign(_carry) != Math.Sign(notches))
            {
                _carry = 0;
            }

            double total = _carry + notches * 120;
            // Bound one burst (100 notches) so a bogus delta cannot queue thousands of events.
            int whole = (int)Math.Clamp(Math.Truncate(total), -12000, 12000);
            _carry = Math.Abs(whole) == 12000 ? 0 : total - whole;
            var rotations = new List<int>();
            while (whole != 0)
            {
                int step = Math.Clamp(whole, -MaxWheelRotation, MaxWheelRotation);
                rotations.Add(step);
                whole -= step;
            }
            return rotations;
        }
    }
    // Keyed by the enum, not its name, so a misspelt key fails to compile instead of silently mapping to 0.
    // Values are PC/AT set-1 scan codes; 0x1xx carries the KBDFLAGS_EXTENDED prefix (E0).
    private static readonly Dictionary<PhysicalKey, uint> Codes = new()
    {
        [PhysicalKey.Escape] = 0x01,
        [PhysicalKey.Digit1] = 0x02,
        [PhysicalKey.Digit2] = 0x03,
        [PhysicalKey.Digit3] = 0x04,
        [PhysicalKey.Digit4] = 0x05,
        [PhysicalKey.Digit5] = 0x06,
        [PhysicalKey.Digit6] = 0x07,
        [PhysicalKey.Digit7] = 0x08,
        [PhysicalKey.Digit8] = 0x09,
        [PhysicalKey.Digit9] = 0x0a,
        [PhysicalKey.Digit0] = 0x0b,
        [PhysicalKey.Minus] = 0x0c,
        [PhysicalKey.Equal] = 0x0d,
        [PhysicalKey.Backspace] = 0x0e,
        [PhysicalKey.Tab] = 0x0f,
        [PhysicalKey.Q] = 0x10,
        [PhysicalKey.W] = 0x11,
        [PhysicalKey.E] = 0x12,
        [PhysicalKey.R] = 0x13,
        [PhysicalKey.T] = 0x14,
        [PhysicalKey.Y] = 0x15,
        [PhysicalKey.U] = 0x16,
        [PhysicalKey.I] = 0x17,
        [PhysicalKey.O] = 0x18,
        [PhysicalKey.P] = 0x19,
        [PhysicalKey.BracketLeft] = 0x1a,
        [PhysicalKey.BracketRight] = 0x1b,
        [PhysicalKey.Enter] = 0x1c,
        [PhysicalKey.ControlLeft] = 0x1d,
        [PhysicalKey.A] = 0x1e,
        [PhysicalKey.S] = 0x1f,
        [PhysicalKey.D] = 0x20,
        [PhysicalKey.F] = 0x21,
        [PhysicalKey.G] = 0x22,
        [PhysicalKey.H] = 0x23,
        [PhysicalKey.J] = 0x24,
        [PhysicalKey.K] = 0x25,
        [PhysicalKey.L] = 0x26,
        [PhysicalKey.Semicolon] = 0x27,
        [PhysicalKey.Quote] = 0x28,
        [PhysicalKey.Backquote] = 0x29,
        [PhysicalKey.ShiftLeft] = 0x2a,
        [PhysicalKey.Backslash] = 0x2b,
        [PhysicalKey.Z] = 0x2c,
        [PhysicalKey.X] = 0x2d,
        [PhysicalKey.C] = 0x2e,
        [PhysicalKey.V] = 0x2f,
        [PhysicalKey.B] = 0x30,
        [PhysicalKey.N] = 0x31,
        [PhysicalKey.M] = 0x32,
        [PhysicalKey.Comma] = 0x33,
        [PhysicalKey.Period] = 0x34,
        [PhysicalKey.Slash] = 0x35,
        [PhysicalKey.ShiftRight] = 0x36,
        [PhysicalKey.NumPadMultiply] = 0x37,
        [PhysicalKey.AltLeft] = 0x38,
        [PhysicalKey.Space] = 0x39,
        [PhysicalKey.CapsLock] = 0x3a,
        [PhysicalKey.F1] = 0x3b,
        [PhysicalKey.F2] = 0x3c,
        [PhysicalKey.F3] = 0x3d,
        [PhysicalKey.F4] = 0x3e,
        [PhysicalKey.F5] = 0x3f,
        [PhysicalKey.F6] = 0x40,
        [PhysicalKey.F7] = 0x41,
        [PhysicalKey.F8] = 0x42,
        [PhysicalKey.F9] = 0x43,
        [PhysicalKey.F10] = 0x44,
        [PhysicalKey.NumLock] = 0x45,
        [PhysicalKey.ScrollLock] = 0x46,
        [PhysicalKey.NumPad7] = 0x47,
        [PhysicalKey.NumPad8] = 0x48,
        [PhysicalKey.NumPad9] = 0x49,
        [PhysicalKey.NumPadSubtract] = 0x4a,
        [PhysicalKey.NumPad4] = 0x4b,
        [PhysicalKey.NumPad5] = 0x4c,
        [PhysicalKey.NumPad6] = 0x4d,
        [PhysicalKey.NumPadAdd] = 0x4e,
        [PhysicalKey.NumPad1] = 0x4f,
        [PhysicalKey.NumPad2] = 0x50,
        [PhysicalKey.NumPad3] = 0x51,
        [PhysicalKey.NumPad0] = 0x52,
        [PhysicalKey.NumPadDecimal] = 0x53,
        [PhysicalKey.IntlBackslash] = 0x56,
        [PhysicalKey.F11] = 0x57,
        [PhysicalKey.F12] = 0x58,
        [PhysicalKey.NumPadEnter] = 0x11c,
        [PhysicalKey.ControlRight] = 0x11d,
        [PhysicalKey.NumPadDivide] = 0x135,
        [PhysicalKey.PrintScreen] = 0x137,
        [PhysicalKey.AltRight] = 0x138,
        [PhysicalKey.Home] = 0x147,
        [PhysicalKey.ArrowUp] = 0x148,
        [PhysicalKey.PageUp] = 0x149,
        [PhysicalKey.ArrowLeft] = 0x14b,
        [PhysicalKey.ArrowRight] = 0x14d,
        [PhysicalKey.End] = 0x14f,
        [PhysicalKey.ArrowDown] = 0x150,
        [PhysicalKey.PageDown] = 0x151,
        [PhysicalKey.Insert] = 0x152,
        [PhysicalKey.Delete] = 0x153,
        [PhysicalKey.MetaLeft] = 0x15b,
        [PhysicalKey.MetaRight] = 0x15c,
        [PhysicalKey.ContextMenu] = 0x15d,
        [PhysicalKey.NumPadEqual] = 0x59,
        [PhysicalKey.NumPadComma] = 0x7e,
        [PhysicalKey.IntlRo] = 0x73,
        [PhysicalKey.IntlYen] = 0x7d,
        [PhysicalKey.KanaMode] = 0x70,
        [PhysicalKey.Convert] = 0x79,
        [PhysicalKey.NonConvert] = 0x7b,
        [PhysicalKey.F13] = 0x64,
        [PhysicalKey.F14] = 0x65,
        [PhysicalKey.F15] = 0x66,
        [PhysicalKey.F16] = 0x67,
        [PhysicalKey.F17] = 0x68,
        [PhysicalKey.F18] = 0x69,
        [PhysicalKey.F19] = 0x6a,
        [PhysicalKey.F20] = 0x6b,
        [PhysicalKey.F21] = 0x6c,
        [PhysicalKey.F22] = 0x6d,
        [PhysicalKey.F23] = 0x6e,
        [PhysicalKey.F24] = 0x76
    };
}
