using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>Issue #174: Windows key combinations go to the focused remote desktop, not to both computers.</summary>
[TestClass]
public sealed partial class RdpWindowsKeysTests
{
    private const uint Down = 0, Up = RdpWindowsKeyCapture.FlagUp, Ext = RdpWindowsKeyCapture.FlagExtended, Alt = RdpWindowsKeyCapture.FlagAltDown;
    private const uint VkLWin = 0x5b, VkE = 0x45, VkTab = 0x09, VkEsc = 0x1b, VkA = 0x41, VkShift = 0xa0, VkLeft = 0x25;

    private sealed class Recorder
    {
        internal readonly RdpWindowsKeyCapture Capture = new();
        internal readonly List<string> Sent = [];
        internal bool Key(uint vk, uint scan, uint flags, bool control = false, bool foreground = true) =>
            Capture.Process(vk, scan, flags, control, foreground, (code, down) => Sent.Add($"{code:x}{(down ? "+" : "-")}"));
    }

    [TestMethod]
    public void LoneWindowsKeyGoesOnlyToTheRemote()
    {
        var r = new Recorder();
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Down | Ext));
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Down | Ext), "Auto-repeat stays captured.");
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Up | Ext));
        Assert.AreSequenceEqual(["15b+", "15b+", "15b-"], r.Sent);
        Assert.IsEmpty(r.Capture.Held);
    }

    [TestMethod]
    public void KeysPressedWhileWindowsIsHeldGoToTheRemoteEvenIfReleasedLast()
    {
        var r = new Recorder();
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Down | Ext));
        Assert.IsTrue(r.Key(VkShift, 0x2a, Down));
        Assert.IsTrue(r.Key(VkLeft, 0x4b, Down | Ext));
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Up | Ext));
        Assert.IsTrue(r.Key(VkLeft, 0x4b, Up | Ext), "A key captured on press is captured on release.");
        Assert.IsTrue(r.Key(VkShift, 0x2a, Up));
        Assert.IsFalse(r.Key(VkE, 0x12, Down), "With Win released, E is ordinary input again.");
        Assert.AreSequenceEqual(["15b+", "2a+", "14b+", "15b-", "14b-", "2a-"], r.Sent);
    }

    [TestMethod]
    public void ShellCombinationsWithAltOrCtrlAreCaptured()
    {
        var r = new Recorder();
        Assert.IsTrue(r.Key(VkTab, 0x0f, Down | Alt), "Alt+Tab");
        Assert.IsTrue(r.Key(VkTab, 0x0f, Up | Alt));
        Assert.IsTrue(r.Key(VkEsc, 0x01, Down | Alt), "Alt+Esc");
        Assert.IsTrue(r.Key(VkEsc, 0x01, Up));
        Assert.IsTrue(r.Key(VkEsc, 0x01, Down, control: true), "Ctrl+Esc and Ctrl+Shift+Esc");
        Assert.IsTrue(r.Key(VkEsc, 0x01, Up));
        Assert.AreSequenceEqual(["f+", "f-", "1+", "1-", "1+", "1-"], r.Sent);
    }

    [TestMethod]
    public void OrdinaryKeysAndModifiersStayOnTheAvaloniaPath()
    {
        var r = new Recorder();
        Assert.IsFalse(r.Key(VkA, 0x1e, Down));
        Assert.IsFalse(r.Key(VkA, 0x1e, Up));
        Assert.IsFalse(r.Key(0x12, 0x38, Down | Alt), "Alt itself");
        Assert.IsFalse(r.Key(VkA, 0x1e, Down | Alt), "Alt+A is an ordinary shortcut");
        Assert.IsFalse(r.Key(VkTab, 0x0f, Down), "Tab");
        Assert.IsFalse(r.Key(VkEsc, 0x01, Down), "Esc");
        Assert.IsFalse(r.Key(VkE, 0, Down | Ext), "No scan code: left alone");
        Assert.IsEmpty(r.Sent);
    }

    [TestMethod]
    public void NothingNewIsCapturedOutsideTheForegroundButHeldKeysAreStillReleased()
    {
        var r = new Recorder();
        Assert.IsFalse(r.Key(VkLWin, 0x5b, Down | Ext, foreground: false));
        Assert.IsFalse(r.Key(VkLWin, 0x5b, Up | Ext, foreground: false));
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Down | Ext));
        Assert.IsTrue(r.Key(VkLWin, 0x5b, Up | Ext, foreground: false));
        Assert.AreSequenceEqual(["15b+", "15b-"], r.Sent);
    }

    [TestMethod]
    public void ReleaseAllLetsGoOfEverythingStillCaptured()
    {
        var r = new Recorder();
        _ = r.Key(VkLWin, 0x5b, Down | Ext);
        _ = r.Key(VkE, 0x12, Down);
        var released = new List<uint>();
        r.Capture.ReleaseAll((code, down) => { Assert.IsFalse(down); released.Add(code); });
        CollectionAssert.AreEquivalent(new[] { 0x15bu, 0x12u }, released);
        Assert.IsEmpty(r.Capture.Held);
    }

    [TestMethod]
    public void ScanCodesMatchTheAvaloniaKeyPath()
    {
        // The hook only runs on Windows, so compare with the Windows mapping: on a macOS test host the default
        // overload sends Command (MetaLeft) as Ctrl.
        static uint Windows(Avalonia.Input.PhysicalKey key) => RdpInput.ScanCode(key, RdpInput.KeyboardPlatform.Windows);
        Assert.AreEqual(Windows(Avalonia.Input.PhysicalKey.MetaLeft), RdpWindowsKeyCapture.Code(0x5b, 0, 0), "Injected Win without scan code");
        Assert.AreEqual(Windows(Avalonia.Input.PhysicalKey.MetaRight), RdpWindowsKeyCapture.Code(0x5c, 0x5c, Ext));
        Assert.AreEqual(Windows(Avalonia.Input.PhysicalKey.ArrowLeft), RdpWindowsKeyCapture.Code(VkLeft, 0x4b, Ext));
        Assert.AreEqual(Windows(Avalonia.Input.PhysicalKey.Tab), RdpWindowsKeyCapture.Code(VkTab, 0x0f, 0));
    }

    [TestMethod]
    [DataRow(nameof(RdpWindowsKeys.Remote), WindowState.Normal, true)]
    [DataRow(nameof(RdpWindowsKeys.Remote), WindowState.FullScreen, true)]
    [DataRow(nameof(RdpWindowsKeys.FullScreen), WindowState.Maximized, false)]
    [DataRow(nameof(RdpWindowsKeys.FullScreen), WindowState.FullScreen, true)]
    [DataRow(nameof(RdpWindowsKeys.Local), WindowState.FullScreen, false)]
    public void ModeFollowsTheWindowState(string mode, WindowState state, bool expected) =>
        Assert.AreEqual(expected, RdpDesktopControl.WantsWindowsKeys(Enum.Parse<RdpWindowsKeys>(mode), state));

    [TestMethod]
    public void SettingDefaultsToRemoteAndIsDeclaredOnlyForWindowsHosts()
    {
        var loc = new Loc("en");
        Assert.AreEqual(RdpWindowsKeys.Remote, RdpSettings.From(RdpSettingsTests.Request(), loc, []).WindowsKeys);
        Assert.AreEqual(RdpWindowsKeys.FullScreen, RdpSettings.From(RdpSettingsTests.Request(("windowsKeys", "FullScreen")), loc, []).WindowsKeys);
        Assert.AreEqual(RdpWindowsKeys.Local, RdpSettings.From(RdpSettingsTests.Request(("windowsKeys", "local")), loc, []).WindowsKeys);
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(RdpSettingsTests.Request(("windowsKeys", "both")), loc, []));

        ProtocolSettingField field = RdpSettings.Declare(loc, [], windowsDevices: true).Single(static f => f.Key == "windowsKeys");
        Assert.AreEqual("remote", field.DefaultValue);
        Assert.AreEqual(RdpSettings.ExperiencePage, field.Page);
        Assert.AreSequenceEqual(["remote", "fullscreen", "local"], field.Choices!.Select(static c => c.Value).ToArray());
        Assert.IsFalse(RdpSettings.Declare(loc, [], windowsDevices: false).Any(static f => f.Key == "windowsKeys"));
    }

    /// <summary>
    /// Installs the real WH_KEYBOARD_LL hook and injects Win, Win+F24 through SendInput: the hook must send them to the
    /// remote and swallow them, so the local shell never sees the Windows key (GetAsyncKeyState stays up and no Start
    /// menu opens). Opt-in because it injects into the interactive desktop.
    /// </summary>
    [TestMethod]
    public void RealHookCapturesInjectedWindowsKeys()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VELA_RDP_KEYBOARD_HOOK_TEST") != "1")
        {
            Assert.Inconclusive("Set VELA_RDP_KEYBOARD_HOOK_TEST=1 on Windows to inject keys into the interactive desktop.");
            return;
        }

        RunRealHook();
    }

    [SupportedOSPlatform("windows")]
    private static void RunRealHook()
    {
        nint foreground = Native.GetForegroundWindow();
        if (foreground == 0) { Assert.Inconclusive("No foreground window (non-interactive session)."); return; }
        var sent = new List<string>();
        bool winSeenLocally = false;
        using (RdpWindowsKeyboardHook? hook = RdpWindowsKeyboardHook.Install(foreground, (code, down) =>
        {
            sent.Add($"{code:x}{(down ? "+" : "-")}");
            winSeenLocally |= (Native.GetAsyncKeyState(0x5b) & 0x8000) != 0;
        }))
        {
            Assert.IsNotNull(hook, "SetWindowsHookEx failed: " + Marshal.GetLastPInvokeError());
            // Inject from another thread: the hook runs on this one, which must keep pumping messages meanwhile.
            Task<uint> inject = Task.Run(() => Native.Send(
                (0x5b, 0x5b, 1), (0x87, 0x76, 0), (0x87, 0x76, 2), (0x5b, 0x5b, 1 | 2)));
            long deadline = Environment.TickCount64 + 3000;
            while ((!inject.IsCompleted || sent.Count < 4) && Environment.TickCount64 < deadline) { Native.Pump(); Thread.Sleep(5); }
            if (inject.Result != 4) { Assert.Inconclusive("SendInput was blocked (UIPI or a locked desktop)."); return; }
            if (sent.Count == 0 && Native.GetForegroundWindow() != foreground) { Assert.Inconclusive("Foreground changed during the test."); return; }
        }

        Assert.AreSequenceEqual(["15b+", "76+", "76-", "15b-"], sent);
        Assert.IsFalse(winSeenLocally, "The local system saw the Windows key.");
    }

    [SupportedOSPlatform("windows")]
    private static partial class Native
    {
        [LibraryImport("user32.dll")] internal static partial nint GetForegroundWindow();
        [LibraryImport("user32.dll")] internal static partial short GetAsyncKeyState(int key);
        [LibraryImport("user32.dll", SetLastError = true)] private static partial uint SendInput(uint count, Span<Input> inputs, int size);
        [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);
        [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(in Msg message);

        internal static void Pump()
        {
            while (PeekMessage(out Msg message, 0, 0, 0, 1)) { _ = DispatchMessageW(message); }
        }

        /// <summary>Each key is (virtual key, scan code, KEYEVENTF_EXTENDEDKEY 1 | KEYEVENTF_KEYUP 2).</summary>
        internal static uint Send(params (ushort Vk, ushort Scan, uint Flags)[] keys)
        {
            Span<Input> inputs = stackalloc Input[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                inputs[i] = new Input { Type = 1, Key = new KeybdInput { Vk = keys[i].Vk, Scan = keys[i].Scan, Flags = keys[i].Flags } };
            }
            return SendInput((uint)keys.Length, inputs, Marshal.SizeOf<Input>());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg { public nint Hwnd; public uint Message; public nint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeybdInput { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public nint ExtraInfo; }

        // INPUT is a union of MOUSEINPUT (the largest member), KEYBDINPUT and HARDWAREINPUT; pad to MOUSEINPUT's size.
        [StructLayout(LayoutKind.Explicit)]
        private struct Input
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public KeybdInput Key;
            [FieldOffset(8)] private MouseInput _mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput { public int X; public int Y; public uint Data; public uint Flags; public uint Time; public nint ExtraInfo; }
    }
}
