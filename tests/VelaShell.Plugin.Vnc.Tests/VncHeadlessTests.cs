using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.Plugin.Vnc.Tests;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

[assembly: AvaloniaTestApplication(typeof(VncHeadlessApp))]

namespace VelaShell.Plugin.Vnc.Tests;

public sealed class VncHeadlessApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<VncHeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[TestClass]
public sealed class VncHeadlessTests
{
    internal static void Run(Func<Task> action)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(VncHeadlessApp).Assembly);
        _ = session.Dispatch(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static VncSettings Settings(params (string Key, string Value)[] values) =>
        VncSettings.From(VncPluginTests.Request(values.ToDictionary(static v => v.Key, static v => v.Value)), new("en"));

    [TestMethod]
    public void ChromeStatusAndFrames() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var session = new FakeVncSession { Info = new("3.8", "VeNCrypt (VNC)", "lab", true) };
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        Assert.AreEqual(676d, view.RowDefinitions[0].ActualHeight);
        Assert.AreEqual(24d, view.RowDefinitions[1].ActualHeight);
        session.Emit(2, 1, [0, 0, 255, 255, 255, 255, 255, 255]);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(2, view.Desktop.DesktopWidth);
        StringAssert.Contains(view.StatusText, "2 × 1");
        StringAssert.Contains(view.StatusText, "lab");
        StringAssert.Contains(view.StatusText, "VeNCrypt (VNC) · TLS");
        StringAssert.Contains(view.StatusText, "Tight");
        Button ball = Find<Button>(view, "VncQuickBall");
        Assert.AreEqual("Remote keys", Avalonia.Automation.AutomationProperties.GetName(ball));
        view.ApplyLocale(new("ko"));
        Assert.AreEqual("원격 키", Avalonia.Automation.AutomationProperties.GetName(ball));
        StringAssert.Contains(view.StatusText, "연결됨");
        window.Close();
    });

    [TestMethod]
    public void JpegRectanglesDecodeThroughSkiaInBgrOrder() => Run(static () =>
    {
        // Encode a 16×8 image (left half red, right half blue) as JPEG with the same Skia the decoder uses.
        var source = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(16, 8), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (Avalonia.Platform.ILockedFramebuffer buffer = source.Lock())
        {
            byte[] row = [.. Enumerable.Range(0, 16).SelectMany(static x => x < 8 ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 0, 0, 255 })];
            for (int y = 0; y < 8; y++) { System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length); }
        }
        var jpeg = new MemoryStream();
        source.Save(jpeg, new Avalonia.Media.Imaging.JpegBitmapEncoderOptions { Quality = 95 });
        source.Dispose();

        VncDecodedImage? image = VncJpeg.Decode(jpeg.ToArray());
        Assert.IsNotNull(image);
        Assert.AreEqual((16, 8), (image.Width, image.Height));
        byte[] left = image.Bgra.AsSpan((4 * 16 + 2) * 4, 4).ToArray(), right = image.Bgra.AsSpan((4 * 16 + 13) * 4, 4).ToArray();
        Assert.IsGreaterThan(200, (int)left[2], "red stays in the R byte");
        Assert.IsLessThan(60, (int)left[0]);
        Assert.IsGreaterThan(200, (int)right[0], "blue stays in the B byte");
        Assert.IsLessThan(60, (int)right[2]);
        Assert.IsNull(VncJpeg.Decode([0xFF, 0xD8, 0x00]), "corrupt data is reported, not thrown");
        return Task.CompletedTask;
    });

    [TestMethod]
    public void KeyboardTextAndMouseReachTheSession() => Run(static async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeVncSession(100, 80);
        await using var view = new VncWorkspaceView(session, context, Settings((VncSettings.ScalingKey, "none")), new("en"));
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.Desktop.PresentFrame();
        window.UpdateLayout();
        _ = view.Desktop.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        window.KeyTextInput("é中");
        // Actual size: a desktop smaller than the view is drawn centred.
        Point origin = view.Desktop.TranslatePoint(view.Desktop.DesktopViewport.TopLeft + new Point(10.5, 20.5), window)!.Value;
        window.MouseDown(origin, MouseButton.Left);
        window.MouseUp(origin, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        lock (session)
        {
            Assert.Contains((0xff51u, 0x14bu, true), session.Keys);
            Assert.Contains((0xff51u, 0x14bu, false), session.Keys);
            Assert.Contains((0xE9u, 0u, true), session.Keys);
            Assert.Contains((0x01004E2Du, 0u, false), session.Keys);
            Assert.Contains((VncButtons.Left, 10, 20), session.Pointer);
            Assert.Contains((VncButtons.None, 10, 20), session.Pointer);
        }
        window.Close();
    });

    [TestMethod]
    public void ViewOnlySendsNothingAndHidesTheRemoteKeys() => Run(static async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeVncSession(100, 80);
        await using var view = new VncWorkspaceView(session, context, Settings((VncSettings.ViewOnlyKey, "true")), new("en"));
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(view.QuickBall.IsVisible);
        window.KeyTextInput("x");
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Assert.IsEmpty(session.Keys);
        Assert.IsEmpty(session.Pointer);
        StringAssert.Contains(view.StatusText, "View only");
        window.Close();
    });

    [TestMethod]
    public void RemoteKeysSendChords() => Run(static async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeVncSession();
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        view.QuickBall.Expand();
        Dispatcher.UIThread.RunJobs();
        Find<Button>(view, "QuickSecureAttention").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(100);
        lock (session)
        {
            Assert.AreSequenceEqual(
                new[] { (VncInput.ControlL, 0x1du, true), (VncInput.AltL, 0x38u, true), (VncInput.Delete, 0x153u, true),
                        (VncInput.Delete, 0x153u, false), (VncInput.AltL, 0x38u, false), (VncInput.ControlL, 0x1du, false) },
                session.Keys.ToArray());
        }
        window.Close();
    });

    [TestMethod]
    public void MatchWindowAsksTheServerForTheViewSize() => Run(static async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeVncSession(1024, 768) { CanResize = true };
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = 900, Height = 624, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.Desktop.PresentFrame();
        for (int i = 0; i < 20 && session.Resizes.Count == 0; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.Contains((900, 600), session.Resizes);
        window.Close();
    });

    [TestMethod]
    public void MatchWindowWaitsForALateResizeAnswer() => Run(static async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeVncSession(1024, 768) { CanResize = false };
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = 900, Height = 624, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.Desktop.PresentFrame();
        for (int i = 0; i < 16; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.IsEmpty(session.Resizes, "nothing is asked of a server that has not said it can resize");
        session.CanResize = true;   // the ExtendedDesktopSize answer arrives late
        for (int i = 0; i < 30 && session.Resizes.Count == 0; i++) { await Task.Delay(50); Dispatcher.UIThread.RunJobs(); }
        Assert.Contains((900, 600), session.Resizes);
        window.Close();
    });

    [TestMethod]
    public void ClipboardFollowsFocusAndTheServer() => Run(static async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeVncSession();
        await context.Clipboard.SetTextAsync("local text", default);
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        _ = view.Desktop.Focus();
        for (int i = 0; i < 20 && session.SentClipboard.Count == 0; i++) { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); }
        Assert.AreSequenceEqual(["local text"], session.SentClipboard);
        session.RaiseClipboard("from the server");
        for (int i = 0; i < 20; i++) { await Task.Delay(25); if (await context.Clipboard.GetTextAsync(default) == "from the server") { break; } }
        string? local = await context.Clipboard.GetTextAsync(default);
        Assert.AreEqual("from the server", local);
        // Focusing again does not echo the server's own text back.
        _ = window.Focus();
        _ = view.Desktop.Focus();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.HasCount(1, session.SentClipboard);
        window.Close();
    });

    [TestMethod]
    public void TheDocumentShowsTheDisconnectCardAndReconnects() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var sessions = new List<FakeVncSession>();
        await using var provider = new VncWorkspaceProvider(context, new("en"), (_, _, _, _) =>
        {
            var s = new FakeVncSession();
            sessions.Add(s);
            return Task.FromResult<IVncSession>(s);
        });
        IWorkspaceDocument document = await provider.OpenAsync(VncPluginTests.Request());
        var root = (ContentControl)document.CreateView();
        var window = new Window { Width = 800, Height = 600, Content = root };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        sessions[0].Disconnect(new VncProtocolException("bad rectangle"));
        Dispatcher.UIThread.RunJobs();
        var view = (VncWorkspaceView)root.Content!;
        Assert.IsTrue(view.OverlayVisible);
        StringAssert.Contains(Find<SelectableTextBlock>(view, "VncOverlayDetail").Text!, "bad rectangle");
        Find<Button>(view, "VncReconnect").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 40 && root.Content == view; i++) { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); }
        Assert.AreNotSame(view, root.Content);
        Assert.IsFalse(((VncWorkspaceView)root.Content!).OverlayVisible);
        Assert.AreEqual(1, sessions[0].DisposeCount);
        window.Close();
        await document.DisposeAsync();
    });
}
