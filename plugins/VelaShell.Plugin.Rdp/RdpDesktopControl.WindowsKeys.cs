using Avalonia.Controls;
using Avalonia.Input;

namespace VelaShell.Plugin.Rdp;

internal sealed partial class RdpDesktopControl
{
    private RdpWindowsKeyboardHook? _keyboardHook;

    /// <summary>Where Windows key combinations go while this desktop has focus (Windows hosts only).</summary>
    internal RdpWindowsKeys WindowsKeys
    {
        get;
        set { field = value; UpdateKeyboardHook(); }
    } = RdpWindowsKeys.Remote;

    /// <summary>Whether the low-level hook is capturing Windows key combinations for the remote right now.</summary>
    internal bool CapturesWindowsKeys => _keyboardHook is not null;

    /// <summary>
    /// Whether the setting wants the remote to receive Windows key combinations in the window's current state.
    /// Pure so tests can cover it on every OS; <see cref="UpdateKeyboardHook" /> adds the focus conditions.
    /// </summary>
    internal static bool WantsWindowsKeys(RdpWindowsKeys mode, WindowState state) => mode switch
    {
        RdpWindowsKeys.Remote => true,
        RdpWindowsKeys.FullScreen => state == WindowState.FullScreen,
        _ => false
    };

    /// <summary>
    /// Installs the hook while this desktop is focused in the active window (and the mode asks for it), and removes it
    /// otherwise, releasing on the remote whatever it still held. Called on every focus, activation and window-state
    /// change. Windows only: there the shell acts on the Windows key before the application, so without the hook a
    /// lone Win opened both Start menus (issue #174); other hosts never install one (see RdpWindowsKeyboardHook).
    /// </summary>
    private void UpdateKeyboardHook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool wanted = _disposed == 0 && _attached && IsFocused && IsEffectivelyVisible
            && _window is { IsActive: true } window && WantsWindowsKeys(WindowsKeys, window.WindowState);
        if (wanted == (_keyboardHook is not null))
        {
            return;
        }

        if (!wanted)
        {
            _keyboardHook!.Dispose();
            _keyboardHook = null;
            return;
        }

        nint handle = _window!.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle != 0)
        {
            _keyboardHook = RdpWindowsKeyboardHook.Install(handle, SendCapturedKey);
        }
    }

    private void RemoveKeyboardHook()
    {
        if (OperatingSystem.IsWindows()) { _keyboardHook?.Dispose(); }
        _keyboardHook = null;
    }

    private void SendCapturedKey(uint code, bool down)
    {
        if (_disposed != 0) { return; }
        Queue(() => _session.KeyAsync(code, down));
    }

    /// <summary>
    /// On a Windows host the Windows key never takes the ordinary key path: the hook sends it when the remote should
    /// have it, and otherwise it belongs to this computer alone — forwarding it as well opened the Start menu on both
    /// sides, and a Win+E opened Explorer on both. The same holds for keys pressed while it is held.
    /// </summary>
    private static bool IsLocalWindowsKey(KeyEventArgs e, RdpInput.KeyboardPlatform platform) =>
        platform == RdpInput.KeyboardPlatform.Windows
        && (e.PhysicalKey is PhysicalKey.MetaLeft or PhysicalKey.MetaRight || e.KeyModifiers.HasFlag(KeyModifiers.Meta));
}
