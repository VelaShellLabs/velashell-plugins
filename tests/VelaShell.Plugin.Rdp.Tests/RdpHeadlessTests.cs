using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Plugin.Rdp.Tests;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

[assembly: AvaloniaTestApplication(typeof(RdpHeadlessApp))]

namespace VelaShell.Plugin.Rdp.Tests;

public sealed class RdpHeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<RdpHeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[TestClass]
public sealed class RdpHeadlessTests
{
    [TestMethod]
    public void WorkbenchUsesFixedChromeAndDisposesAfterLocaleChange() => Run(async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var session = new FakeRdpSession();
        var settings = RdpSettings.From(RdpSettingsTests.Request(), new("en"));
        await using var view = new RdpWorkspaceView(session, context, settings, new("en"));
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        // No document header any more: the desktop takes everything above the 24px status bar.
        Assert.HasCount(2, view.RowDefinitions);
        Assert.AreEqual(676d, view.RowDefinitions[0].ActualHeight);
        Assert.AreEqual(24d, view.RowDefinitions[1].ActualHeight);
        // Ctrl+Alt+Delete lives in the floating remote-keys button, docked to the right edge.
        Button ball = Find<Button>(view, "RdpQuickBall");
        Assert.AreEqual("Remote keys", Avalonia.Automation.AutomationProperties.GetName(ball));
        Assert.AreEqual("Ctrl+Alt+Del", Avalonia.Automation.AutomationProperties.GetName(Find<Button>(view, "QuickSecureAttention")));
        Assert.IsTrue(view.QuickBall.IsDockedRight);
        view.ApplyLocale(new("ja"));
        Assert.AreEqual("リモートキー", Avalonia.Automation.AutomationProperties.GetName(ball));
        Assert.IsTrue(session.IsConnected);
        session.Emit(new(2, 1, [0, 0, 0, 255, 255, 255, 255, 255]));
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(2, view.Desktop.DesktopWidth);
        Assert.AreEqual(1, view.Desktop.DesktopHeight);
        window.Close();
    });

    [TestMethod]
    public void TransferProgressNeverChangesDesktopViewportHeight() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        var settings = RdpSettings.From(RdpSettingsTests.Request(), new("en"));
        await using var view = new RdpWorkspaceView(session, context, settings, new("en"));
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        double desktopHeight = view.RowDefinitions[0].ActualHeight;
        foreach (string state in new[] { "Ready", "Preparing", "Transferring", "Retrying", "Sent", "Delivered", "Completed", "Cancelled", "Failed" })
        {
            view.UpdateTransfers(new(state, 12, 100, new string('x', 260)), new(state, 24, 100));
            window.UpdateLayout();
            Assert.AreEqual(desktopHeight, view.RowDefinitions[0].ActualHeight);
            Assert.AreEqual(24d, view.RowDefinitions[1].ActualHeight);
        }
        view.UpdateTransfers(null, null);
        window.UpdateLayout();
        Assert.AreEqual(desktopHeight, view.RowDefinitions[0].ActualHeight);
        session.Emit(new(2, 1, new byte[8]));
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(2, view.Desktop.DesktopWidth);
        window.Close();
    });

    [TestMethod]
    public void TransferBarsDoNotOverlapCancelIcons() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var settings = RdpSettings.From(RdpSettingsTests.Request(), new("en"));
        await using var view = new RdpWorkspaceView(new FakeRdpSession(), context, settings, new("en"));
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        foreach (int width in new[] { 400, 800, 1200 })
        {
            window.Width = width;
            view.UpdateTransfers(new("Transferring", 50, 100, new string('x', 260)), new("Transferring", 50, 100));
            window.UpdateLayout();
            var footer = (Border)view.Children[3];
            foreach (string name in new[] { "ClipboardDownload", "ClipboardUpload" })
            {
                var controls = footer.GetVisualDescendants().OfType<Control>().ToList();
                ProgressBar bar = controls.OfType<ProgressBar>().Single(control => control.Name == name + "Bar");
                Button cancel = controls.OfType<Button>().Single(control => control.Name == "Cancel" + name);
                var slot = (Control)cancel.Parent!;
                Assert.IsLessThanOrEqualTo(cancel.Bounds.Left, bar.Bounds.Right, $"Overlap at width {width}: {name} bar {bar.Bounds} cancel {cancel.Bounds} visible {cancel.IsVisible}");
                Assert.IsLessThanOrEqualTo(slot.Bounds.Width, cancel.Bounds.Right, $"Cancel clipped at width {width}");
                // Both transfers stay inside the status bar, side by side, even when it is narrow.
                Assert.IsLessThanOrEqualTo(footer.Bounds.Width, slot.TranslatePoint(new Point(slot.Bounds.Width, 0), footer)!.Value.X, $"Slot past the edge at width {width}");
                // Host-sized ×: a 16px button around a 10px glyph, not a chrome box around a 14px+ icon.
                Assert.AreEqual(16d, cancel.Bounds.Width);
                Assert.AreEqual(10d, ((Control)cancel.Content!).Bounds.Width);
                Assert.AreEqual("Cancel", Avalonia.Automation.AutomationProperties.GetName(cancel));
            }
            Assert.AreEqual(24d, view.RowDefinitions[1].ActualHeight);
        }
        window.Close();
    });

    [TestMethod]
    public void TransferStatusPrioritizesMetricsOverTheFileName() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var settings = RdpSettings.From(RdpSettingsTests.Request(), new("en"));
        await using var view = new RdpWorkspaceView(new FakeRdpSession(), context, settings, new("en"));
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        view.UpdateTransfers(new("Transferring", 50, 100, "a-very-long-file-name-that-must-stay-in-the-tooltip", null, 1024 * 1024, TimeSpan.FromSeconds(8)), null);
        window.UpdateLayout();

        TextBlock text = Find<TextBlock>(view, "ClipboardDownloadProgress");
        Panel slot = text.FindAncestorOfType<Panel>()!;
        TextBlock state = slot.Children.OfType<TextBlock>().Single(control => control != text);
        var tooltip = (TextBlock)ToolTip.GetTip(slot)!;
        Assert.DoesNotContain("a-very-long-file-name", text.Text);
        Assert.Contains("MB/s", state.Text);
        Assert.Contains("8s", state.Text);
        Assert.AreEqual(TextWrapping.Wrap, tooltip.TextWrapping);
        Assert.AreEqual(520d, tooltip.MinWidth);
        Assert.AreEqual(900d, tooltip.MaxWidth);
        Assert.Contains("Remote → local", tooltip.Text);
        Assert.Contains("Transferring: 50B/100B", tooltip.Text);
        Assert.Contains("File name: a-very-long-file-name-that-must-stay-in-the-tooltip", tooltip.Text);
        Assert.Contains("Speed: 1.0MB/s · Remaining time: 8s", tooltip.Text);
        window.Close();
    });

    [TestMethod]
    public void FocusPublicationDoesNotHoldTheKeyboardQueue() => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 800, 600);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.PublishClipboard = () => publish.Task;
        var window = new Window { Content = desktop };
        window.Show();
        _ = desktop.Focus();
        var input = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Queue(() => { input.SetResult(); return Task.CompletedTask; });
        try { await input.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
        finally { _ = publish.TrySetResult(); window.Close(); }
    });

    [TestMethod]
    public void ReconnectReplacesDesktopInsideTheSameHostView() => Run(async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        var sessions = new List<FakeRdpSession>();
        await using var provider = new RdpWorkspaceProvider(context, new("en"), (_, _, _, _) =>
        {
            var created = new FakeRdpSession();
            sessions.Add(created);
            return Task.FromResult<IRdpSession>(created);
        });
        IWorkspaceDocument document = await provider.OpenAsync(RdpSettingsTests.Request());
        var root = (ContentControl)document.CreateView();
        var initial = (RdpWorkspaceView)root.Content!;
        var window = new Window { Width = 800, Height = 600, Content = root };
        window.Show();
        sessions[0].Disconnect();
        await document.ReconnectAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.AreNotSame(initial, root.Content);
        Assert.IsTrue(((RdpWorkspaceView)root.Content!).Desktop.IsEnabled);
        provider.Loc = new("ko");
        Assert.AreSame(root, document.CreateView());
        await document.DisposeAsync();
        Assert.IsNull(root.Content);
        Assert.AreEqual(1, sessions[0].DisposeCount);
        Assert.AreEqual(1, sessions[1].DisposeCount);
        window.Close();
    });

    [TestMethod]
    [DataRow(false, null)]
    [DataRow(false, "")]
    [DataRow(true, null)]
    public void ImeCommitsDoNotLeakTheirInitialLetters(bool imeProcessed, string? symbol) => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 800, 600);
        var request = new TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
        desktop.RaiseEvent(request);
        Assert.IsNotNull(request.Client);
        foreach ((Key key, PhysicalKey physicalKey, string? preedit, string? text) in new[]
        {
            (Key.C, PhysicalKey.C, "ceshi", "测试"),
            (Key.S, PhysicalKey.S, "shuru", "输入")
        })
        {
            // The first key arrives before the IME supplies any preedit text.
            var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = imeProcessed ? Key.ImeProcessed : key, PhysicalKey = physicalKey, KeySymbol = symbol };
            desktop.RaiseEvent(down);
            Assert.IsFalse(down.Handled, "The local IME must still receive the key.");
            request.Client.SetPreeditText(preedit);
            desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, PhysicalKey = physicalKey });
            desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, PhysicalKey = PhysicalKey.Enter });
            request.Client.SetPreeditText(null);
            desktop.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
            desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter, PhysicalKey = PhysicalKey.Enter });
        }
        await desktop.DisposeAsync();
        Assert.AreSequenceEqual(new[] { "'测试'", "'输入'" }, session.Input);
    });

    [TestMethod]
    [DataRow(Key.ImeProcessed, PhysicalKey.Enter, KeyModifiers.Control)]
    [DataRow(Key.ImeProcessed, PhysicalKey.ArrowDown, KeyModifiers.None)]
    [DataRow(Key.DeadCharProcessed, PhysicalKey.Quote, KeyModifiers.None)]
    public void ImeOwnedKeysWithoutPreeditNeverReachTheServer(Key key, PhysicalKey physicalKey, KeyModifiers modifiers) => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 800, 600);
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physicalKey, KeyModifiers = modifiers });
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, PhysicalKey = physicalKey, KeyModifiers = modifiers });
        await desktop.DisposeAsync();
        Assert.IsEmpty(session.Input);
    });

    [TestMethod]
    [DataRow(Key.A, PhysicalKey.A, KeyModifiers.None, "a")]
    [DataRow(Key.A, PhysicalKey.A, KeyModifiers.Shift, "A")]
    [DataRow(Key.D1, PhysicalKey.Digit1, KeyModifiers.None, "1")]
    [DataRow(Key.OemQuotes, PhysicalKey.Quote, KeyModifiers.None, "é")]
    [DataRow(Key.Space, PhysicalKey.Space, KeyModifiers.None, " ")]
    [DataRow(Key.NumPad1, PhysicalKey.NumPad1, KeyModifiers.None, "1")]
    public void TextWithoutKeySymbolIsSentOnlyOnce(Key key, PhysicalKey physicalKey, KeyModifiers modifiers, string text) => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 800, 600);
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physicalKey, KeyModifiers = modifiers });
        desktop.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, PhysicalKey = physicalKey, KeyModifiers = modifiers });
        await desktop.DisposeAsync();
        Assert.AreSequenceEqual(new[] { $"'{text}'" }, session.Input);
    });

    [TestMethod]
    [DataRow(Key.C, PhysicalKey.C, KeyModifiers.Control, "c", 0x2e)]
    [DataRow(Key.A, PhysicalKey.A, KeyModifiers.Alt, "a", 0x1e)]
    [DataRow(Key.D, PhysicalKey.D, KeyModifiers.Meta, "d", 0x20)]
    [DataRow(Key.Enter, PhysicalKey.Enter, KeyModifiers.None, null, 0x1c)]
    [DataRow(Key.Back, PhysicalKey.Backspace, KeyModifiers.None, null, 0x0e)]
    [DataRow(Key.Left, PhysicalKey.ArrowLeft, KeyModifiers.None, null, 0x14b)]
    [DataRow(Key.End, PhysicalKey.End, KeyModifiers.None, null, 0x14f)]
    public void ShortcutsAndNavigationKeepPhysicalKeyPairs(Key key, PhysicalKey physicalKey, KeyModifiers modifiers, string? symbol, int code) => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 800, 600);
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physicalKey, KeyModifiers = modifiers, KeySymbol = symbol });
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, PhysicalKey = physicalKey, KeyModifiers = modifiers, KeySymbol = symbol });
        await desktop.DisposeAsync();
        Assert.AreSequenceEqual(new (uint, bool)[] { ((uint)code, true), ((uint)code, false) }, session.Keys);
    });

    [TestMethod]
    public void ControlVPastesOnlyAfterClipboardPublicationAndReleasesKeys() => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 1280, 720);
        var publish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.PublishClipboard = () => publish.Task;
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, PhysicalKey = PhysicalKey.V, KeyModifiers = KeyModifiers.Control });
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.V, PhysicalKey = PhysicalKey.V, KeyModifiers = KeyModifiers.Control });
        Assert.IsEmpty(session.Keys);
        publish.SetResult();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Queue(() => { drained.SetResult(); return Task.CompletedTask; });
        await drained.Task;
        Assert.AreSequenceEqual(new (uint, bool)[] { (0x2f, true), (0x2f, false) }, session.Keys);
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftCtrl, PhysicalKey = PhysicalKey.ControlLeft });
        desktop.ReleaseInput();
        await desktop.DisposeAsync();
        Assert.AreEqual((0x1du, false), session.Keys[^1]);
    });
    [TestMethod]
    public void ControlVWaitsForCliprdrServerAcknowledgement() => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 1280, 720);
        var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new CliprdrChannel(_ => { offered.SetResult(); return Task.CompletedTask; }, _ => { });
        desktop.PublishClipboard = () => channel.OfferFormatsAsync("13\t\n");
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.V, PhysicalKey = PhysicalKey.V, KeyModifiers = KeyModifiers.Control });
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.V, PhysicalKey = PhysicalKey.V, KeyModifiers = KeyModifiers.Control });
        await offered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsEmpty(session.Keys);
        await channel.ProcessAsync([3, 0, 1, 0, 0, 0, 0, 0]);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Queue(() => { drained.SetResult(); return Task.CompletedTask; });
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreSequenceEqual(new (uint, bool)[] { (0x2f, true), (0x2f, false) }, session.Keys);
    });

    [TestMethod]
    public void CoalescedMouseMovesCannotCrossButtonTransitions() => Run(async () =>
    {
        var session = new FakeRdpSession();
        await using var desktop = new RdpDesktopControl(session, 640, 480);
        var window = new Window { Width = 640, Height = 480, Content = desktop };
        window.Show();
        session.Emit(new(640, 480, new byte[640 * 480 * 4]));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Queue(() => gate.Task);
        window.MouseMove(new Point(10, 10));
        window.MouseDown(new Point(20, 20), MouseButton.Left);
        window.MouseMove(new Point(30, 30), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(30, 30), MouseButton.Left);
        gate.SetResult();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Queue(() => { drained.SetResult(); return Task.CompletedTask; });
        await drained.Task;
        int down = session.Mouse.FindIndex(mouse => mouse.Flags == 0x9000);
        int up = session.Mouse.FindIndex(mouse => mouse.Flags == 0x1000);
        Assert.IsGreaterThanOrEqualTo(0, down);
        Assert.IsGreaterThan(down, up);
        Assert.AreEqual((0x9000u, 20u, 20u), session.Mouse[down]);
        Assert.Contains(mouse => mouse == (0x0800u, 30u, 30u), session.Mouse.Skip(down + 1).Take(up - down - 1));
        window.Close();
    });

    // In-app verification: the real RdpWorkspaceView / RdpDesktopControl rendering frames from a
    // live managed session against xrdp, captured through Avalonia's headless Skia pipeline — the
    // same control tree the host loads, without launching the full desktop app. Skipped in CI.
    [TestMethod]
    public void ManagedEngineRendersInsideRealWorkspaceView() => Run(async () =>
    {
        if (RdpManagedInteropTests.Target() is not { } target) { Assert.Inconclusive("VELA_RDP_TEST_TARGET not set."); return; }
        byte[] pin = await RdpManagedInteropTests.LearnPinAsync(target);
        WorkspaceConnectRequest request = RdpSettingsTests.Request(("trustedThumbprint", Convert.ToHexString(pin)), ("width", "1024"), ("height", "768"))
            with
        { Host = target.Host, Port = target.Port, Username = RdpManagedInteropTests.User, Password = RdpManagedInteropTests.Pass };
        var loc = new Loc("en");
        var settings = RdpSettings.From(request, loc);

        using var context = new TestPluginContext { PluginId = "velashell.rdp" };
        IRdpSession session = await RdpConnector.ConnectAsync(request, settings, loc, default);
        try
        {
            await using var view = new RdpWorkspaceView(session, context, settings, loc);
            // Only the 24px status bar sits below the desktop, so a 792px window leaves a 1024×768 desktop viewport.
            var window = new Window { Width = 1024, Height = 792, Content = view };
            window.Show();
            window.UpdateLayout();

            // Pump the UI while the engine paints; frame updates arrive via Dispatcher.UIThread.Post.
            for (int i = 0; i < 60; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(100); }

            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.IsNotNull(frame, "Headless capture returned no frame.");
            string? dump = Environment.GetEnvironmentVariable("VELA_RDP_TEST_DUMP");
            if (!string.IsNullOrEmpty(dump)) { _ = Directory.CreateDirectory(dump); frame.Save(Path.Combine(dump, "managed-rdp-inapp.png"), PngBitmapEncoderOptions.Default); }

            // Dynamic resolution: the view asked the server to match the remote to its viewport,
            // so the desktop control reports the viewport size, not the initial 1024×768 setting.
            Assert.AreEqual(1024, view.Desktop.DesktopWidth);
            Assert.AreEqual(768, view.Desktop.DesktopHeight);
            Assert.IsTrue(session.IsConnected, "Session dropped while rendering in the host view.");
            window.Close();
        }
        finally { await session.DisposeAsync(); }
    });

    internal static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    internal static void Run(Func<Task> action)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RdpHeadlessApp).Assembly);
        _ = session.Dispatch(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
