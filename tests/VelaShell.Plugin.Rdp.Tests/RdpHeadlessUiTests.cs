using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Plugin.Rdp.Engine;
using VelaShell.Plugin.Rdp.Ui;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RdpHeadlessUiTests
{
    private static HeadlessUnitTestSession Session => HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RdpHeadlessUiTests).Assembly);
    // Explicit generic Func<Task<bool>> chooses the async-aware overload. No unobserved nested Task.
    private static void OnUi(Func<Task> body) => Session.Dispatch<bool>(async () =>
    {
        await body();
        return true;
    }, CancellationToken.None).GetAwaiter().GetResult();
    private static async Task Pump()
    {
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }
    private static T Find<T>(Control view, string name) where T : Control => view.FindControl<T>(name)!;
    private static Point WindowPoint(Control control, Window window, double x, double y) => control.TranslatePoint(new Point(x, y), window)!.Value;
    private static void Click(Window window, Button button)
    {
        Point point = WindowPoint(button, window, button.Bounds.Width / 2, button.Bounds.Height / 2);
        Assert.IsTrue(button.Bounds.Width > 0 && button.Bounds.Height > 0, "Button must be arranged.");
        Assert.IsTrue(new Rect(window.ClientSize).Contains(point), $"Button is outside viewport: {point}; {window.ClientSize}.");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
    private static RdpWorkspaceDocument Document(TestPluginContext context, RdpUiFakeSession session, IRdpSessionFactory? factory = null) =>
        new(context, factory ?? new FakeFactory(), new("localhost", 3389, "windows.test", 3389, 800, 600, TimeSpan.FromSeconds(5), ""),
            new RdpCredentials("user", "password", ""), session, RdpScaleMode.Fit, "Windows", "windows.test:3389", new Loc("en"),
            _ => { }, CancellationToken.None);
    private sealed class FakeFactory : IRdpSessionFactory
    {
        public RdpUiFakeSession Next { get; } = new();
        public Exception? Failure { get; set; }
        public Task<IRdpSession> ConnectAsync(RdpConnectionOptions options, RdpCredentials credentials, CancellationToken cancellationToken = default) =>
            Failure is null ? Task.FromResult<IRdpSession>(Next) : Task.FromException<IRdpSession>(Failure);
    }

    [TestMethod]
    public void ViewLoadsButtonsLocaleStatusAndExternalReconnect() => OnUi(async () =>
    {
        using var context = new TestPluginContext();
        var remote = new RdpUiFakeSession(); remote.SetFrame(800, 600);
        var factory = new FakeFactory(); factory.Next.SetFrame(1000, 700);
        var document = Document(context, remote, factory);
        var view = new RdpWorkspaceView(document);
        var window = new Window { Width = 1100, Height = 750, Content = view };
        window.Show();
        try
        {
            await Pump();
            var desktop = Find<RdpDesktopSurface>(view, "Desktop");
            Assert.IsNotNull(desktop.Bitmap);
            Assert.AreEqual("Connected", Find<TextBlock>(view, "StatusText").Text);
            Assert.IsTrue(Find<Button>(view, "CtrlAltDelButton").IsEnabled);
            document.UpdateLocale(new Loc("zh-Hans"));
            await Pump();
            Assert.AreEqual("断开", Find<Button>(view, "DisconnectButton").Content);
            Assert.AreSame(remote, document.Session);
            Click(window, Find<Button>(view, "CtrlAltDelButton"));
            await desktop.InputDrained;
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.CtrlAltDelete));
            Click(window, Find<Button>(view, "ActualButton")); await Pump();
            Assert.AreEqual(RdpScaleMode.Actual, desktop.ScaleMode);
            remote.SetStatus(RdpSessionState.Faulted); await Pump();
            Assert.IsFalse(Find<Button>(view, "CtrlAltDelButton").IsEnabled);
            Assert.IsTrue(Find<Border>(view, "ErrorBanner").IsVisible);
            await document.ReconnectAsync(); await Pump();
            Assert.AreSame(factory.Next, document.Session);
            Assert.AreEqual(1000, desktop.Frame.Width);
            Assert.IsFalse(Find<Border>(view, "ErrorBanner").IsVisible);
            Click(window, Find<Button>(view, "DisconnectButton")); await Pump();
            Assert.IsNull(document.Session);
            Assert.IsFalse(Find<Button>(view, "DisconnectButton").IsEnabled);
        }
        finally { window.Close(); await document.DisposeAsync(); }
    });

    [TestMethod]
    public void FramesCoalesceReuseBitmapClearSensitiveBuffersAndIgnoreOldCallbacks() => OnUi(async () =>
    {
        var remote = new RdpUiFakeSession(); remote.SetFrame(4, 2, padding: 8);
        var desktop = new RdpDesktopSurface(); desktop.SetSession(remote);
        var window = new Window { Width = 400, Height = 300, Content = desktop };
        window.Show();
        try
        {
            await Pump();
            var bitmap = desktop.Bitmap; Assert.IsNotNull(bitmap);
            int copied = remote.CopyCount;
            remote.SetFrame(4, 2, padding: 8, value: 44);
            for (int i = 0; i < 1000; i++) remote.NotifyFrame();
            Assert.IsTrue(desktop.HasPendingUpdate);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(copied + 1, remote.CopyCount);
            Assert.AreSame(bitmap, desktop.Bitmap);
            byte[] firstBuffer = remote.LastCopyBuffer!;
            byte[] actual = new byte[32];
            using (ILockedFramebuffer locked = desktop.Bitmap!.Lock())
            {
                for (int y = 0; y < 2; y++) System.Runtime.InteropServices.Marshal.Copy(locked.Address + y * locked.RowBytes, actual, y * 16, 16);
            }
            Assert.AreEqual((byte)44, actual[0]);
            Assert.AreEqual((byte)1, actual[17]);
            Action? staleFrame = remote.CaptureFrameCallback();
            Action<RdpSessionStatus>? staleStatus = remote.CaptureStatusCallback();
            remote.SetFrame(5, 2); await Pump();
            Assert.IsTrue(firstBuffer.All(x => x == 0));
            Assert.AreNotSame(bitmap, desktop.Bitmap);
            byte[] resizeBuffer = remote.LastCopyBuffer!;
            remote.NotifyFrame();
            window.Content = null;
            staleFrame?.Invoke(); staleStatus?.Invoke(new(RdpSessionState.Faulted));
            await Pump();
            Assert.IsNull(desktop.Bitmap);
            Assert.IsTrue(resizeBuffer.All(x => x == 0));
            Assert.IsFalse(remote.Disposed);
            window.Content = desktop; await Pump();
            Assert.IsNotNull(desktop.Bitmap);
            var next = new RdpUiFakeSession(); next.SetFrame(7, 3);
            desktop.SetSession(next);
            staleFrame?.Invoke(); staleStatus?.Invoke(new(RdpSessionState.Faulted));
            await Pump();
            Assert.AreEqual(7, desktop.Frame.Width);
            Assert.AreEqual(RdpSessionState.Connected, desktop.Status.State);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void UnavailableAndDisconnectedFramesNeverSelfSchedule() => OnUi(async () =>
    {
        var remote = new RdpUiFakeSession(); remote.SetFrame(40, 20);
        var desktop = new RdpDesktopSurface(); desktop.SetSession(remote);
        var window = new Window { Width = 400, Height = 300, Content = desktop }; window.Show();
        try
        {
            await Pump();
            var bitmap = desktop.Bitmap; Assert.IsNotNull(bitmap);
            remote.CopyUnavailable = true;
            remote.SetFrame(40, 20, value: 30);
            int previous = remote.CopyAttempts;
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            int attempts = remote.CopyAttempts;
            Assert.AreEqual(previous + 1, attempts);
            Assert.IsFalse(desktop.HasPendingUpdate);
            await Pump();
            Assert.AreEqual(attempts, remote.CopyAttempts);
            remote.SetStatus(RdpSessionState.Disconnected);
            remote.NotifyFrame();
            await Pump();
            Assert.AreEqual(attempts, remote.CopyAttempts);
            Assert.IsFalse(desktop.HasPendingUpdate);
            Assert.AreSame(bitmap, desktop.Bitmap);
            remote.SetStatus(RdpSessionState.Connected);
            remote.CopyUnavailable = false;
            remote.NotifyFrame(); await Pump();
            Assert.AreEqual(attempts + 1, remote.CopyAttempts);
            Assert.AreEqual(remote.FrameInfo.Sequence, desktop.Frame.Sequence);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void RealPhysicalKeyRepeatPointerDragWheelAndFocusLossRelease() => OnUi(async () =>
    {
        var remote = new RdpUiFakeSession(); remote.SetFrame(400, 200);
        var desktop = new RdpDesktopSurface(); desktop.SetSession(remote);
        var focusTarget = new Button { Content = "focus", Height = 32 };
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,32") };
        grid.Children.Add(desktop); Grid.SetRow(focusTarget, 1); grid.Children.Add(focusTarget);
        var window = new Window { Width = 800, Height = 600, Content = grid };
        window.Show();
        try
        {
            await Pump();
            Point point = WindowPoint(desktop, window, desktop.Bounds.Width / 2, desktop.Bounds.Height / 2);
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(new Point(point.X + 20, point.Y + 10), RawInputModifiers.LeftMouseButton);
            window.MouseWheel(point, new Vector(1, -2), RawInputModifiers.LeftMouseButton);
            window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ControlRight, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ShiftLeft, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.ShiftRight, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None); // OS repeat: two downs, a single tracked held key.
            Assert.AreEqual(5, desktop.HeldKeyCount);
            Assert.AreEqual(1, desktop.HeldButtonCount);
            await desktop.InputDrained;
            Assert.AreEqual(2, remote.Inputs.Count(x => x.Kind == RdpInputKind.KeyDown && x.ScanCode == 0x1e));
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.KeyDown && x.ScanCode == 0x11d));
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.PointerMove && x.X > 200));
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.Wheel && x.WheelDelta == -240 && !x.Horizontal));
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.Wheel && x.WheelDelta == 120 && x.Horizontal));
            focusTarget.Focus();
            Assert.AreEqual(0, desktop.HeldKeyCount);
            Assert.AreEqual(0, desktop.HeldButtonCount);
            await desktop.InputDrained;
            Assert.AreEqual(RdpInputKind.ReleaseAll, remote.Inputs.Last().Kind);
            window.MouseUp(point, MouseButton.Left);
            desktop.Focus();
            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            await desktop.InputDrained;
            window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            await desktop.InputDrained;
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.KeyUp && x.ScanCode == 0x14b));
            window.MouseDown(point, MouseButton.XButton1); await desktop.InputDrained;
            window.MouseUp(point, MouseButton.XButton1); await desktop.InputDrained;
            window.MouseDown(point, MouseButton.XButton2); await desktop.InputDrained;
            window.MouseUp(point, MouseButton.XButton2); await desktop.InputDrained;
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.ButtonDown && x.Button == RdpMouseButton.X1));
            Assert.IsTrue(remote.Inputs.Any(x => x.Kind == RdpInputKind.ButtonUp && x.Button == RdpMouseButton.X2));
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            remote.SetStatus(RdpSessionState.Disconnected); await Pump();
            Assert.AreEqual(0, desktop.HeldKeyCount);
            await desktop.InputDrained;
            Assert.AreEqual(RdpInputKind.ReleaseAll, remote.Inputs.Last().Kind);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ActualSizeUsesDisplayPixelsAndScrollCoordinatesAfterDpiChange() => OnUi(async () =>
    {
        var remote = new RdpUiFakeSession(); remote.SetFrame(1600, 1000);
        var desktop = new RdpDesktopSurface { ScaleMode = RdpScaleMode.Actual }; desktop.SetSession(remote);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = desktop
        };
        var window = new Window { Width = 600, Height = 400, Content = scroll }; window.Show();
        try
        {
            await Pump();
            window.SetRenderScaling(2); await Pump();
            Assert.AreEqual(800d, desktop.Bounds.Width, 0.01);
            scroll.Offset = new Vector(100, 50); await Pump();
            Point local = new(150, 90);
            Point point = desktop.TranslatePoint(local, window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); await desktop.InputDrained;
            RdpInput input = remote.Inputs.Last(x => x.Kind == RdpInputKind.ButtonDown);
            Assert.AreEqual(300, input.X); Assert.AreEqual(180, input.Y);
            window.MouseUp(point, MouseButton.Left); await desktop.InputDrained;
            window.SetRenderScaling(1.25); await Pump();
            Assert.AreEqual(1280d, desktop.Bounds.Width, 0.01);
            Assert.IsTrue(desktop.Viewport.TryMap(local, false, out PixelPoint changed));
            Assert.AreEqual(new PixelPoint(187, 112), changed);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void OverflowClearsLocalTrackingAndExternalReconnectClearsError() => OnUi(async () =>
    {
        using var context = new TestPluginContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new RdpUiFakeSession { InputHandler = (input, _) =>
        {
            if (input.Kind == RdpInputKind.KeyDown) { entered.TrySetResult(); return new ValueTask(blocked.Task); }
            return ValueTask.CompletedTask;
        }};
        remote.SetFrame(400, 200);
        var factory = new FakeFactory(); factory.Next.SetFrame(400, 200);
        var document = Document(context, remote, factory);
        var view = new RdpWorkspaceView(document);
        var window = new Window { Width = 1000, Height = 600, Content = view }; window.Show();
        try
        {
            await Pump();
            var desktop = Find<RdpDesktopSurface>(view, "Desktop"); desktop.Focus();
            window.KeyPressQwerty(PhysicalKey.ControlLeft, RawInputModifiers.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 130; i++) window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            Assert.AreEqual(0, desktop.HeldKeyCount);
            blocked.SetResult(); await desktop.InputDrained; await Pump();
            Assert.AreEqual(RdpInputKind.ReleaseAll, remote.Inputs.Last().Kind);
            Assert.AreEqual(document.Loc["Rdp_SendInputError"], Find<TextBlock>(view, "ErrorText").Text);
            int count = remote.Inputs.Count;
            window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None); await desktop.InputDrained;
            Assert.AreEqual(count, remote.Inputs.Count);
            await document.ReconnectAsync(); await Pump();
            Assert.IsFalse(Find<Border>(view, "ErrorBanner").IsVisible);
            desktop.Focus(); window.KeyPressQwerty(PhysicalKey.B, RawInputModifiers.None); await desktop.InputDrained;
            Assert.IsTrue(factory.Next.Inputs.Any(x => x.Kind == RdpInputKind.KeyDown && x.ScanCode == 0x30));
        }
        finally { blocked.TrySetResult(); window.Close(); await document.DisposeAsync(); }
    });

    [TestMethod]
    public void ReconnectButtonPreservesDocumentCertificateInstructions() => OnUi(async () =>
    {
        using var context = new TestPluginContext();
        var remote = new RdpUiFakeSession(); remote.SetFrame(100, 100);
        var factory = new FakeFactory { Failure = new ProtocolCertificateTrustException("secret", "subject", "issuer",
            DateTimeOffset.UtcNow.AddDays(1), new string('A', 64), "Changed") };
        var document = Document(context, remote, factory);
        var view = new RdpWorkspaceView(document);
        var window = new Window { Width = 1100, Height = 650, Content = view }; window.Show();
        try
        {
            await Pump(); Click(window, Find<Button>(view, "ReconnectButton")); await Pump();
            Assert.AreEqual(document.Loc["Rdp_CertificateReconnect"], Find<TextBlock>(view, "ErrorText").Text);
            Assert.IsFalse(Find<TextBlock>(view, "ErrorText").Text!.Contains("secret", StringComparison.Ordinal));
        }
        finally { window.Close(); await document.DisposeAsync(); }
    });

    [TestMethod]
    public void PointerShapeHiddenDefaultAndCapturedLossAreHandled() => OnUi(async () =>
    {
        var remote = new RdpUiFakeSession(); remote.SetFrame(100, 100);
        var desktop = new RdpDesktopSurface(); desktop.SetSession(remote);
        var window = new Window { Width = 300, Height = 300, Content = desktop }; window.Show();
        try
        {
            await Pump();
            remote.SetPointer(new(RdpPointerKind.Hidden)); await Pump(); Assert.IsNotNull(desktop.Cursor);
            remote.SetPointer(new(RdpPointerKind.Bitmap, 1, 1, Rgba: [255, 0, 0, 255])); await Pump(); Assert.IsNotNull(desktop.Cursor);
            remote.SetPointer(new(RdpPointerKind.Default)); await Pump(); Assert.IsNull(desktop.Cursor);
            Point point = WindowPoint(desktop, window, 150, 150);
            window.MouseDown(point, MouseButton.Left); await desktop.InputDrained;
            window.Content = new Button(); // Removes the captured surface while its button remains down.
            Assert.AreEqual(0, desktop.HeldButtonCount);
            await desktop.InputDrained;
            Assert.AreEqual(RdpInputKind.ReleaseAll, remote.Inputs.Last().Kind);
        }
        finally { window.Close(); }
    });
}
