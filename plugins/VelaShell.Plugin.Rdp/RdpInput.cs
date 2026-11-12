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

    internal static uint ScanCode(PhysicalKey key) => Codes.GetValueOrDefault(key);
    internal static uint Wheel(double delta, bool horizontal)
    {
        int rotation = Math.Clamp((int)Math.Round(delta * 120), -255, 255);
        return (horizontal ? 0x0400u : 0x0200u) | (rotation < 0 ? 0x0100u : 0u) | ((uint)rotation & 0xff);
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
