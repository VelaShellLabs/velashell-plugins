using Avalonia;
using Avalonia.Input;

namespace VelaShell.Plugin.Rdp;

internal static class RdpInput
{
    internal static Rect Viewport(Size available, int width, int height, bool fit)
    {
        if (width <= 0 || height <= 0 || available.Width <= 0 || available.Height <= 0
            || !double.IsFinite(available.Width) || !double.IsFinite(available.Height))
        {
            return default;
        }

        double scale = fit ? Math.Min(available.Width / width, available.Height / height) : 1;
        return new Rect((available.Width - width * scale) / 2, (available.Height - height * scale) / 2, width * scale, height * scale);
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

    internal static uint ScanCode(PhysicalKey key) => Codes.GetValueOrDefault(key.ToString());
    internal static uint Wheel(double delta, bool horizontal)
    {
        int rotation = Math.Clamp((int)Math.Round(delta * 120), -255, 255);
        return (horizontal ? 0x0400u : 0x0200u) | (rotation < 0 ? 0x0100u : 0u) | ((uint)rotation & 0xff);
    }
    private static readonly Dictionary<string, uint> Codes = new(StringComparer.Ordinal)
    {
        ["Escape"] = 0x01,
        ["Digit1"] = 0x02,
        ["Digit2"] = 0x03,
        ["Digit3"] = 0x04,
        ["Digit4"] = 0x05,
        ["Digit5"] = 0x06,
        ["Digit6"] = 0x07,
        ["Digit7"] = 0x08,
        ["Digit8"] = 0x09,
        ["Digit9"] = 0x0a,
        ["Digit0"] = 0x0b,
        ["Minus"] = 0x0c,
        ["Equal"] = 0x0d,
        ["Backspace"] = 0x0e,
        ["Tab"] = 0x0f,
        ["Q"] = 0x10,
        ["W"] = 0x11,
        ["E"] = 0x12,
        ["R"] = 0x13,
        ["T"] = 0x14,
        ["Y"] = 0x15,
        ["U"] = 0x16,
        ["I"] = 0x17,
        ["O"] = 0x18,
        ["P"] = 0x19,
        ["BracketLeft"] = 0x1a,
        ["BracketRight"] = 0x1b,
        ["Enter"] = 0x1c,
        ["ControlLeft"] = 0x1d,
        ["A"] = 0x1e,
        ["S"] = 0x1f,
        ["D"] = 0x20,
        ["F"] = 0x21,
        ["G"] = 0x22,
        ["H"] = 0x23,
        ["J"] = 0x24,
        ["K"] = 0x25,
        ["L"] = 0x26,
        ["Semicolon"] = 0x27,
        ["Quote"] = 0x28,
        ["Backquote"] = 0x29,
        ["ShiftLeft"] = 0x2a,
        ["Backslash"] = 0x2b,
        ["Z"] = 0x2c,
        ["X"] = 0x2d,
        ["C"] = 0x2e,
        ["V"] = 0x2f,
        ["B"] = 0x30,
        ["N"] = 0x31,
        ["M"] = 0x32,
        ["Comma"] = 0x33,
        ["Period"] = 0x34,
        ["Slash"] = 0x35,
        ["ShiftRight"] = 0x36,
        ["NumpadMultiply"] = 0x37,
        ["AltLeft"] = 0x38,
        ["Space"] = 0x39,
        ["CapsLock"] = 0x3a,
        ["F1"] = 0x3b,
        ["F2"] = 0x3c,
        ["F3"] = 0x3d,
        ["F4"] = 0x3e,
        ["F5"] = 0x3f,
        ["F6"] = 0x40,
        ["F7"] = 0x41,
        ["F8"] = 0x42,
        ["F9"] = 0x43,
        ["F10"] = 0x44,
        ["NumLock"] = 0x45,
        ["ScrollLock"] = 0x46,
        ["Numpad7"] = 0x47,
        ["Numpad8"] = 0x48,
        ["Numpad9"] = 0x49,
        ["NumpadSubtract"] = 0x4a,
        ["Numpad4"] = 0x4b,
        ["Numpad5"] = 0x4c,
        ["Numpad6"] = 0x4d,
        ["NumpadAdd"] = 0x4e,
        ["Numpad1"] = 0x4f,
        ["Numpad2"] = 0x50,
        ["Numpad3"] = 0x51,
        ["Numpad0"] = 0x52,
        ["NumpadDecimal"] = 0x53,
        ["IntlBackslash"] = 0x56,
        ["F11"] = 0x57,
        ["F12"] = 0x58,
        ["NumpadEnter"] = 0x11c,
        ["ControlRight"] = 0x11d,
        ["NumpadDivide"] = 0x135,
        ["PrintScreen"] = 0x137,
        ["AltRight"] = 0x138,
        ["Home"] = 0x147,
        ["ArrowUp"] = 0x148,
        ["PageUp"] = 0x149,
        ["ArrowLeft"] = 0x14b,
        ["ArrowRight"] = 0x14d,
        ["End"] = 0x14f,
        ["ArrowDown"] = 0x150,
        ["PageDown"] = 0x151,
        ["Insert"] = 0x152,
        ["Delete"] = 0x153,
        ["MetaLeft"] = 0x15b,
        ["MetaRight"] = 0x15c,
        ["ContextMenu"] = 0x15d
    };
}
