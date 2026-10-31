using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using VelaShell.Plugin.Rdp.Tests;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

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
        Assert.AreEqual(36d, view.RowDefinitions[0].ActualHeight);
        Assert.AreEqual(24d, view.RowDefinitions[2].ActualHeight);
        view.ApplyLocale(new("ja"));
        Assert.IsTrue(session.IsConnected);
        session.Emit(new(2, 1, [0, 0, 0, 255, 255, 255, 255, 255]));
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(2, view.Desktop.DesktopWidth);
        Assert.AreEqual(1, view.Desktop.DesktopHeight);
        window.Close();
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
        CollectionAssert.AreEqual(new (uint, bool)[] { (0x2f, true), (0x2f, false) }, session.Keys);
        desktop.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftCtrl, PhysicalKey = PhysicalKey.ControlLeft });
        desktop.ReleaseInput();
        await desktop.DisposeAsync();
        Assert.AreEqual((0x1du, false), session.Keys[^1]);
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
        Assert.IsTrue(session.Mouse.Skip(down + 1).Take(up - down - 1).Any(mouse => mouse == (0x0800u, 30u, 30u)));
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
            // 36px toolbar + 24px status, so a 828px window leaves a 1024×768 desktop viewport.
            var window = new Window { Width = 1024, Height = 828, Content = view };
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

    private static void Run(Func<Task> action)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RdpHeadlessApp).Assembly);
        _ = session.Dispatch(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
