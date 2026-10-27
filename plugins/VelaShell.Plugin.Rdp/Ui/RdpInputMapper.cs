using Avalonia.Input;

namespace VelaShell.Plugin.Rdp.Ui;

/// <summary>Physical PC set-1 positions, not layout-dependent Key or TextInput characters.</summary>
internal static class RdpInputMapper
{
    public static ushort ScanCode(PhysicalKey key) => key switch
    {
        PhysicalKey.Escape => 0x01,
        PhysicalKey.Digit1 => 0x02, PhysicalKey.Digit2 => 0x03, PhysicalKey.Digit3 => 0x04,
        PhysicalKey.Digit4 => 0x05, PhysicalKey.Digit5 => 0x06, PhysicalKey.Digit6 => 0x07,
        PhysicalKey.Digit7 => 0x08, PhysicalKey.Digit8 => 0x09, PhysicalKey.Digit9 => 0x0a,
        PhysicalKey.Digit0 => 0x0b, PhysicalKey.Minus => 0x0c, PhysicalKey.Equal => 0x0d,
        PhysicalKey.Backspace => 0x0e, PhysicalKey.Tab => 0x0f,
        PhysicalKey.Q => 0x10, PhysicalKey.W => 0x11, PhysicalKey.E => 0x12, PhysicalKey.R => 0x13,
        PhysicalKey.T => 0x14, PhysicalKey.Y => 0x15, PhysicalKey.U => 0x16, PhysicalKey.I => 0x17,
        PhysicalKey.O => 0x18, PhysicalKey.P => 0x19, PhysicalKey.BracketLeft => 0x1a,
        PhysicalKey.BracketRight => 0x1b, PhysicalKey.Enter => 0x1c, PhysicalKey.ControlLeft => 0x1d,
        PhysicalKey.A => 0x1e, PhysicalKey.S => 0x1f, PhysicalKey.D => 0x20, PhysicalKey.F => 0x21,
        PhysicalKey.G => 0x22, PhysicalKey.H => 0x23, PhysicalKey.J => 0x24, PhysicalKey.K => 0x25,
        PhysicalKey.L => 0x26, PhysicalKey.Semicolon => 0x27, PhysicalKey.Quote => 0x28,
        PhysicalKey.Backquote => 0x29, PhysicalKey.ShiftLeft => 0x2a, PhysicalKey.Backslash => 0x2b,
        PhysicalKey.Z => 0x2c, PhysicalKey.X => 0x2d, PhysicalKey.C => 0x2e, PhysicalKey.V => 0x2f,
        PhysicalKey.B => 0x30, PhysicalKey.N => 0x31, PhysicalKey.M => 0x32, PhysicalKey.Comma => 0x33,
        PhysicalKey.Period => 0x34, PhysicalKey.Slash => 0x35, PhysicalKey.ShiftRight => 0x36,
        PhysicalKey.NumPadMultiply => 0x37, PhysicalKey.AltLeft => 0x38, PhysicalKey.Space => 0x39,
        PhysicalKey.CapsLock => 0x3a,
        PhysicalKey.F1 => 0x3b, PhysicalKey.F2 => 0x3c, PhysicalKey.F3 => 0x3d, PhysicalKey.F4 => 0x3e,
        PhysicalKey.F5 => 0x3f, PhysicalKey.F6 => 0x40, PhysicalKey.F7 => 0x41, PhysicalKey.F8 => 0x42,
        PhysicalKey.F9 => 0x43, PhysicalKey.F10 => 0x44, PhysicalKey.NumLock => 0x45, PhysicalKey.ScrollLock => 0x46,
        PhysicalKey.NumPad7 => 0x47, PhysicalKey.NumPad8 => 0x48, PhysicalKey.NumPad9 => 0x49,
        PhysicalKey.NumPadSubtract => 0x4a, PhysicalKey.NumPad4 => 0x4b, PhysicalKey.NumPad5 => 0x4c,
        PhysicalKey.NumPad6 => 0x4d, PhysicalKey.NumPadAdd => 0x4e, PhysicalKey.NumPad1 => 0x4f,
        PhysicalKey.NumPad2 => 0x50, PhysicalKey.NumPad3 => 0x51, PhysicalKey.NumPad0 => 0x52,
        PhysicalKey.NumPadDecimal => 0x53, PhysicalKey.IntlBackslash => 0x56,
        PhysicalKey.F11 => 0x57, PhysicalKey.F12 => 0x58,
        PhysicalKey.NumPadEnter => 0x11c, PhysicalKey.ControlRight => 0x11d,
        PhysicalKey.NumPadDivide => 0x135, PhysicalKey.PrintScreen => 0x137, PhysicalKey.AltRight => 0x138,
        PhysicalKey.Home => 0x147, PhysicalKey.ArrowUp => 0x148, PhysicalKey.PageUp => 0x149,
        PhysicalKey.ArrowLeft => 0x14b, PhysicalKey.ArrowRight => 0x14d, PhysicalKey.End => 0x14f,
        PhysicalKey.ArrowDown => 0x150, PhysicalKey.PageDown => 0x151, PhysicalKey.Insert => 0x152,
        PhysicalKey.Delete => 0x153, PhysicalKey.MetaLeft => 0x15b, PhysicalKey.MetaRight => 0x15c,
        PhysicalKey.ContextMenu => 0x15d,
        // Pause uses an E1 sequence that the managed engine contract does not expose.
        _ => 0
    };
}
