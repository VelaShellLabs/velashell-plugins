using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// Where Windows key combinations go while the remote desktop has keyboard focus (mstsc's "Apply Windows key
/// combinations", RDCMan's "Windows key combinations"). Only the Windows host has a shell that takes these keys before
/// the application sees them (the Start menu, Alt+Tab, Win+E …); see <see cref="RdpSettings.WindowsKeysKey" />.
/// </summary>
internal enum RdpWindowsKeys
{
    /// <summary>To the remote while the desktop is focused in the active window (the default).</summary>
    Remote,

    /// <summary>To the remote only while the host window is full screen; otherwise as <see cref="Local" />.</summary>
    FullScreen,

    /// <summary>To this computer only: the remote never sees the Windows key (use the remote-keys button).</summary>
    Local
}

/// <summary>
/// Decides, for each low-level key event, whether it belongs to the remote desktop: the Windows keys and every key
/// pressed while one is held, Alt+Tab / Alt+Esc, and Ctrl+Esc / Ctrl+Shift+Esc — the combinations the Windows shell
/// would otherwise act on before (or as well as) the remote. A captured key is swallowed locally and its scan code
/// is sent instead; its release is captured too, even if the window lost the foreground meanwhile, so the remote
/// never keeps a key down. Everything else passes through to the ordinary Avalonia key path untouched, so text
/// input, IMEs and dead keys keep working.
/// </summary>
internal sealed class RdpWindowsKeyCapture
{
    internal const uint VkTab = 0x09, VkEscape = 0x1b, VkLeftWin = 0x5b, VkRightWin = 0x5c;
    internal const uint FlagExtended = 0x01, FlagAltDown = 0x20, FlagUp = 0x80;
    internal const uint LeftWinScanCode = 0x15b, RightWinScanCode = 0x15c;

    private readonly HashSet<uint> _held = [];

    /// <summary>The scan codes captured and not yet released.</summary>
    internal IReadOnlyCollection<uint> Held => _held;

    /// <summary>
    /// Handles one KBDLLHOOKSTRUCT. Returns <see langword="true" /> when the event must be swallowed; the remote key
    /// is then reported through <paramref name="send" /> (scan code, pressed).
    /// </summary>
    internal bool Process(uint vk, uint scanCode, uint flags, bool controlDown, bool foreground, Action<uint, bool> send)
    {
        uint code = Code(vk, scanCode, flags);
        if ((flags & FlagUp) != 0)
        {
            if (code == 0 || !_held.Remove(code)) { return false; }
            send(code, false);
            return true;
        }

        if (code == 0 || !foreground) { return false; }
        bool alt = (flags & FlagAltDown) != 0;
        bool capture = vk is VkLeftWin or VkRightWin
            || _held.Contains(LeftWinScanCode) || _held.Contains(RightWinScanCode)
            || _held.Contains(code)   // auto-repeat of a captured key
            || alt && vk is VkTab or VkEscape
            || controlDown && vk == VkEscape;
        if (!capture) { return false; }
        _ = _held.Add(code);
        send(code, true);
        return true;
    }

    /// <summary>Releases every captured key on the remote (the hook is going away).</summary>
    internal void ReleaseAll(Action<uint, bool> send)
    {
        foreach (uint code in _held) { send(code, false); }
        _held.Clear();
    }

    /// <summary>
    /// The set-1 scan code with the E0 prefix as 0x100, as <see cref="RdpInput" /> uses. Software that injects keys
    /// with no scan code still gets the Windows keys mapped; any other key without one is left alone.
    /// </summary>
    internal static uint Code(uint vk, uint scanCode, uint flags) => vk switch
    {
        VkLeftWin => LeftWinScanCode,
        VkRightWin => RightWinScanCode,
        _ when scanCode is 0 or > 0xff => 0,
        _ => scanCode | ((flags & FlagExtended) != 0 ? 0x100u : 0u)
    };
}

/// <summary>
/// A WH_KEYBOARD_LL hook that feeds <see cref="RdpWindowsKeyCapture" />. It runs on the UI thread that installs it
/// (the hook is called through that thread's message loop), only exists while the remote desktop is focused in the
/// active window, and checks that the window is still the foreground window before capturing a new key. Windows only:
/// macOS sends Command as Ctrl and has no Start menu competing for it, and Linux desktops have no single equivalent
/// (X11 grabs and the Wayland shortcuts-inhibit protocol are owned by the host toolkit, not a plugin).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class RdpWindowsKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int VkControl = 0x11;

    // A low-level hook procedure is process-wide state; only the focused desktop owns it.
    private static RdpWindowsKeyboardHook? s_active;

    private readonly RdpWindowsKeyCapture _capture = new();
    private readonly nint _window;
    private readonly Action<uint, bool> _send;
    private nint _hook;

    private RdpWindowsKeyboardHook(nint window, Action<uint, bool> send)
    {
        _window = window;
        _send = send;
    }

    /// <summary>Installs the hook for <paramref name="window" />; null when Windows refused it (keys then stay local).</summary>
    internal static RdpWindowsKeyboardHook? Install(nint window, Action<uint, bool> send)
    {
        s_active?.Dispose();
        var hook = new RdpWindowsKeyboardHook(window, send);
        hook._hook = Native.SetWindowsHookExW(WhKeyboardLl, &Callback, Native.GetModuleHandleW(null), 0);
        if (hook._hook == 0) { return null; }
        s_active = hook;
        return hook;
    }

    public void Dispose()
    {
        if (_hook == 0) { return; }
        _ = Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
        if (ReferenceEquals(s_active, this)) { s_active = null; }
        _capture.ReleaseAll(_send);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Callback(int code, nint message, nint data)
    {
        try
        {
            if (code >= 0 && s_active is { } hook)
            {
                var key = (KbdLlHookStruct*)data;
                bool foreground = Native.GetForegroundWindow() == hook._window;
                bool control = (Native.GetAsyncKeyState(VkControl) & 0x8000) != 0;
                if (hook._capture.Process(key->VkCode, key->ScanCode, key->Flags, control, foreground, hook._send))
                {
                    return 1;
                }
            }
        }
        catch (Exception)
        {
            // An exception must not unwind into user32; the key then simply goes to the local system.
        }

        return Native.CallNextHookEx(0, code, message, data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private static partial class Native
    {
        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nint, nint, nint> proc, nint module, uint threadId);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnhookWindowsHookEx(nint hook);

        [LibraryImport("user32.dll")]
        internal static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();

        [LibraryImport("user32.dll")]
        internal static partial short GetAsyncKeyState(int key);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint GetModuleHandleW(string? moduleName);
    }
}
