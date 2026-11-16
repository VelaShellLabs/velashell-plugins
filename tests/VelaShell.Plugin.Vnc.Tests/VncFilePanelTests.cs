using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VelaShell.PluginSdk.Testing;
using VelaShell.Vnc;
using static VelaShell.Plugin.Vnc.Tests.VncHeadlessTests;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>The file panel and the audio status in the workspace view, headless, over a scripted file-transfer fake.</summary>
[TestClass]
public sealed class VncFilePanelTests
{
    internal sealed class FakeFiles : IVncFileTransfer
    {
        internal bool Grant { get; set; } = true;
        internal Exception? AccessFailure { get; set; }
        internal int DriveListCount { get; private set; }
        internal List<VncRemoteDrive> Drives { get; } = [new("C:\\", 'l')];
        internal Dictionary<string, List<VncRemoteEntry>> Folders { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);
        internal List<string> Calls { get; } = [];
        public bool IsGranted { get; private set; }

        public Task<bool> RequestAccessAsync(CancellationToken ct = default)
        {
            Calls.Add("access");
            if (AccessFailure is { } failure) { return Task.FromException<bool>(failure); }
            IsGranted = Grant;
            return Task.FromResult(Grant);
        }

        public Task<IReadOnlyList<VncRemoteDrive>> ListDrivesAsync(CancellationToken ct = default)
        {
            DriveListCount++;
            return Task.FromResult<IReadOnlyList<VncRemoteDrive>>(Drives);
        }

        public Task<IReadOnlyList<VncRemoteEntry>> ListAsync(string directory, CancellationToken ct = default)
        {
            Calls.Add("list " + directory);
            return Folders.TryGetValue(directory, out List<VncRemoteEntry>? entries)
                ? Task.FromResult<IReadOnlyList<VncRemoteEntry>>([.. entries])
                : Task.FromException<IReadOnlyList<VncRemoteEntry>>(new VncFileTransferException(VncFileTransferError.Unavailable, "no such folder"));
        }

        public async Task<long> DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            Calls.Add("download " + remotePath);
            byte[] data = Contents[remotePath];
            await destination.WriteAsync(data, ct);
            return data.Length;
        }

        public async Task UploadAsync(Stream source, long length, DateTimeOffset modified, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default)
        {
            var copy = new MemoryStream();
            await source.CopyToAsync(copy, ct);
            Calls.Add($"upload {remotePath} {length}");
            Contents[remotePath] = copy.ToArray();
            progress?.Report(length);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct = default) { Calls.Add("mkdir " + path); return Task.CompletedTask; }
        public Task DeleteAsync(string path, CancellationToken ct = default) { Calls.Add("delete " + path); return Task.CompletedTask; }
        public Task RenameAsync(string path, string newPath, CancellationToken ct = default) { Calls.Add($"rename {path} {newPath}"); return Task.CompletedTask; }
    }

    private sealed class FakeLocal : IVncLocalFiles
    {
        internal List<VncLocalFile> ToOpen { get; } = [];
        internal MemoryStream Saved { get; } = new();
        internal bool Deleted { get; private set; }

        public Task<IReadOnlyList<VncLocalFile>> PickOpenAsync(string title) => Task.FromResult<IReadOnlyList<VncLocalFile>>(ToOpen);

        public Task<VncLocalTarget?> PickSaveAsync(string title, string suggestedName) =>
            Task.FromResult<VncLocalTarget?>(new(suggestedName, () => Task.FromResult<Stream>(Saved), () => { Deleted = true; return Task.CompletedTask; }));
    }

    private static VncSettings Settings() => VncSettings.From(VncPluginTests.Request(), new("en"));

    private static FakeFiles Files()
    {
        var files = new FakeFiles();
        files.Folders["C:\\"] =
        [
            new("docs", true, 0, null),
            new("a.txt", false, 3, DateTimeOffset.Now),
        ];
        files.Folders["C:\\docs\\"] = [];
        files.Contents["C:\\a.txt"] = [1, 2, 3];
        return files;
    }

    /// <summary>A shown view over a fake session with file transfer; the panel open on C:\ with a fake local side.</summary>
    private static async Task<(VncWorkspaceView View, VncFilePanel Panel, Window Window)> OpenAsync(FakeVncSession session, TestPluginContext context, FakeLocal local, string locale = "en")
    {
        var view = new VncWorkspaceView(session, context, Settings(), new(locale));
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        Assert.IsTrue(Find<Button>(view, "VncFilesButton").IsVisible);
        view.ToggleFiles();
        VncFilePanel panel = view.FilePanel!;
        panel.LocalFiles = local;
        await panel.Operation!;
        window.UpdateLayout();
        return (view, panel, window);
    }

    private static void Click(Visual root, string name) => Find<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Select(VncFilePanel panel, string name) =>
        Find<ListBox>(panel, "VncFilesList").SelectedItem = panel.Items.OfType<VncRemoteEntry>().Single(e => e.Name == name);

    [TestMethod]
    public void NoFileButtonWithoutFileTransfer() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        await using var view = new VncWorkspaceView(new FakeVncSession(), context, Settings(), new("en"));
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Assert.IsFalse(Find<Button>(view, "VncFilesButton").IsVisible);
        view.ToggleFiles();
        Assert.IsNull(view.FilePanel);
        window.Close();
    });

    [TestMethod]
    [DataRow(1000, 700)]
    [DataRow(420, 300)]
    public void FileDialogDoesNotResizeTheDesktop(int width, int height) => Run(async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var session = new FakeVncSession(1024, 768) { FileTransfer = Files(), CanResize = true };
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.Desktop.PresentFrame();
        await Until(() => session.Resizes.Count > 0);
        Rect desktopBounds = view.Desktop.Bounds;
        (int Width, int Height)[] initialResizes = [.. session.Resizes];

        for (int i = 0; i < 2; i++)
        {
            view.ToggleFiles();
            VncFilePanel panel = view.FilePanel!;
            await panel.Operation!;
            window.UpdateLayout();
            Assert.AreEqual(desktopBounds, view.Desktop.Bounds, "opening files must not take space from the desktop");
            Point origin = panel.TranslatePoint(default, view)!.Value;
            Assert.AreEqual(width / 2d, origin.X + panel.Bounds.Width / 2, 1d);
            Assert.AreEqual((height - 24) / 2d, origin.Y + panel.Bounds.Height / 2, 1d);
            Assert.IsTrue(origin.X >= 0 && origin.Y >= 0);
            Assert.IsTrue(origin.X + panel.Bounds.Width <= width);
            Assert.IsTrue(origin.Y + panel.Bounds.Height <= height - 24);
            await Task.Delay(500);
            Dispatcher.UIThread.RunJobs();
            Assert.AreSequenceEqual(initialResizes, session.Resizes.ToArray(), "opening files must not send SetDesktopSize");

            if (i == 0) { Click(panel, "VncFilesClose"); }
            else { view.ToggleFiles(); }
            window.UpdateLayout();
            Assert.IsFalse(panel.IsVisible);
            Assert.AreEqual(desktopBounds, view.Desktop.Bounds);
            Assert.IsTrue(view.Desktop.IsFocused);
            await Task.Delay(500);
            Dispatcher.UIThread.RunJobs();
            Assert.AreSequenceEqual(initialResizes, session.Resizes.ToArray(), "closing files must not send SetDesktopSize");
        }
        window.Close();
    });

    [TestMethod]
    public void FileDialogMovesAndResizesWithoutChangingTheRemoteViewport() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var session = new FakeVncSession(1024, 768) { FileTransfer = Files(), CanResize = true };
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(session, context, new FakeLocal());
        await using (view)
        {
            await Until(() => session.Resizes.Count > 0);
            Rect desktopBounds = view.Desktop.Bounds;
            (int Width, int Height)[] initialResizes = [.. session.Resizes];
            Rect initial = panel.Bounds;
            Assert.AreEqual(new Size(800, 600), initial.Size);

            Point title = panel.TitleBar.TranslatePoint(new Point(60, 14), window)!.Value;
            Drag(window, title, new Vector(30, 12));
            Assert.AreEqual(initial.Translate(new Vector(30, 12)), panel.Bounds);
            Rect moved = panel.Bounds;
            Point corner = panel.TranslatePoint(new Point(moved.Width, moved.Height), window)!.Value;
            Drag(window, corner, new Vector(25, 5));
            Assert.AreEqual(new Rect(moved.Position, new Size(825, 605)), panel.Bounds);
            Rect resized = panel.Bounds;

            Click(panel, "VncFilesClose");
            view.ToggleFiles();
            window.UpdateLayout();
            Assert.AreEqual(resized, panel.Bounds, "reopening must retain the chosen size and position");
            Assert.AreEqual(desktopBounds, view.Desktop.Bounds);
            await Task.Delay(500);
            Dispatcher.UIThread.RunJobs();
            Assert.AreSequenceEqual(initialResizes, session.Resizes.ToArray());
            Assert.IsEmpty(session.Pointer, "dragging the dialog must not reach the remote desktop");
        }
        window.Close();
    });

    [TestMethod]
    [DataRow(0d, 0.5d, -20d, 0d)]
    [DataRow(1d, 0.5d, 20d, 0d)]
    [DataRow(0.5d, 0d, 0d, -20d)]
    [DataRow(0.5d, 1d, 0d, 20d)]
    [DataRow(0d, 0d, -20d, -20d)]
    [DataRow(1d, 0d, 20d, -20d)]
    [DataRow(0d, 1d, -20d, 20d)]
    [DataRow(1d, 1d, 20d, 20d)]
    public void FileDialogResizesFromEveryEdgeAndCorner(double x, double y, double dx, double dy) => Run(async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(
            new FakeVncSession { FileTransfer = Files() }, context, new FakeLocal());
        await using (view)
        {
            Rect initial = panel.Bounds;
            Point grip = panel.TranslatePoint(new Point(initial.Width * x, initial.Height * y), window)!.Value;
            Drag(window, grip, new Vector(dx, dy));
            Assert.AreEqual(initial.Left + (x == 0 ? dx : 0), panel.Bounds.Left);
            Assert.AreEqual(initial.Top + (y == 0 ? dy : 0), panel.Bounds.Top);
            Assert.AreEqual(initial.Right + (x == 1 ? dx : 0), panel.Bounds.Right);
            Assert.AreEqual(initial.Bottom + (y == 1 ? dy : 0), panel.Bounds.Bottom);
        }
        window.Close();
    });

    [TestMethod]
    public void FileDialogStaysReachableAndFitsASmallerWindow() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(
            new FakeVncSession { FileTransfer = Files() }, context, new FakeLocal());
        await using (view)
        {
            Point title = panel.TitleBar.TranslatePoint(new Point(60, 14), window)!.Value;
            Drag(window, title, new Vector(-2000, -2000));
            Assert.AreEqual(new Point(16, 16), panel.Bounds.Position);
            Point corner = panel.TranslatePoint(new Point(panel.Bounds.Width, panel.Bounds.Height), window)!.Value;
            Drag(window, corner, new Vector(2000, 2000));
            Assert.AreEqual(new Rect(16, 16, 968, 644), panel.Bounds);
            corner = panel.TranslatePoint(new Point(panel.Bounds.Width, panel.Bounds.Height), window)!.Value;
            Drag(window, corner, new Vector(-2000, -2000));
            Assert.AreEqual(new Size(400, 280), panel.Bounds.Size);
            title = panel.TitleBar.TranslatePoint(new Point(60, 14), window)!.Value;
            Drag(window, title, new Vector(2000, 2000));
            Assert.AreEqual(new Point(584, 380), panel.Bounds.Position);

            window.Width = 360;
            window.Height = 260;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.AreEqual(new Size(360, 260), view.Bounds.Size);
            Assert.AreEqual(new Rect(16, 16, 328, 204), panel.Bounds);
            view.Resources["VelaBgHover"] = Avalonia.Media.Brushes.Transparent;
            Button close = Find<Button>(panel, "VncFilesClose");
            Point closePoint = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
            Assert.IsTrue(new Rect(window.Bounds.Size).Contains(closePoint), $"close button is at {closePoint}, bounds {close.Bounds}");
            window.MouseMove(closePoint);
            window.MouseDown(closePoint, MouseButton.Left);
            window.MouseUp(closePoint, MouseButton.Left);
            Assert.IsFalse(panel.IsVisible, "the close button must remain usable after moving and resizing");
        }
        window.Close();
    });

    private static void Drag(Window window, Point start, Vector delta)
    {
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + delta / 2, RawInputModifiers.LeftMouseButton);
        window.UpdateLayout();
        window.MouseMove(start + delta, RawInputModifiers.LeftMouseButton);
        window.UpdateLayout();
        window.MouseUp(start + delta, MouseButton.Left);
        window.UpdateLayout();
    }

    [TestMethod]
    public void EscapeCancelsThePromptThenClosesTheDialogWithoutRemoteInput() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        FakeFiles files = Files();
        var session = new FakeVncSession { FileTransfer = files };
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(session, context, new FakeLocal());
        await using (view)
        {
            Click(panel, "VncFilesNewFolder");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.IsTrue(panel.PromptVisible);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.IsFalse(panel.PromptVisible);
            Assert.IsTrue(panel.IsVisible);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(panel.IsVisible);
            Assert.IsTrue(view.Desktop.IsFocused);
            await Task.Delay(100);
            Assert.IsEmpty(session.Keys, "dialog dismissal must not send Escape to the remote desktop");
            Assert.IsFalse(files.Calls.Any(static call => call.StartsWith("mkdir", StringComparison.Ordinal)));
        }
        window.Close();
    });

    [TestMethod]
    public void FileDialogKeepsInputLocalAndClosesOnDisconnect() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var session = new FakeVncSession { FileTransfer = Files() };
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(session, context, new FakeLocal());
        await using (view)
        {
            view.Resources["VelaScrim"] = Avalonia.Media.Brushes.Black;
            Assert.IsTrue(panel.IsKeyboardFocusWithin);
            for (int i = 0; i < 20; i++)
            {
                window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Assert.IsTrue(panel.IsKeyboardFocusWithin, $"Tab {i} must stay in the dialog; focused: {window.FocusManager?.GetFocusedElement()}");
            }
            window.MouseDown(new Point(10, 10), MouseButton.Left);
            window.MouseUp(new Point(10, 10), MouseButton.Left);
            await Task.Delay(100);
            Assert.IsEmpty(session.Pointer, "the scrim must block clicks on the remote desktop");
            Assert.IsEmpty(session.Keys);
            Assert.IsTrue(panel.IsVisible);

            view.SetConnected(false, "Disconnected");
            Assert.IsFalse(panel.IsVisible);
            Assert.IsFalse(Find<Grid>(view, "VncFilesOverlay").IsVisible);
            view.ToggleFiles();
            Assert.IsFalse(panel.IsVisible);
        }
        window.Close();
    });

    [TestMethod]
    public void OpeningAsksForAccessEntersTheOnlyDriveAndBrowses() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        FakeFiles files = Files();
        var session = new FakeVncSession { FileTransfer = files };
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(session, context, new FakeLocal());
        await using (view)
        {
            Assert.AreSequenceEqual(new[] { "access", "list C:\\" }, files.Calls.ToArray());
            Assert.AreEqual("C:\\", panel.Directory, "LibVNC's single drive (C:\\ = /) opens directly");
            Assert.HasCount(2, panel.Items);
            Assert.AreEqual(VncFileDialog.DefaultWidth, panel.Bounds.Width);
            Assert.AreEqual(VncFileDialog.DefaultHeight, panel.Bounds.Height);
            StringAssert.Contains(panel.StatusText, "2 items");

            Select(panel, "docs");
            Find<ListBox>(panel, "VncFilesList").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await panel.Operation!;
            Assert.AreEqual("C:\\docs\\", panel.Directory);
            StringAssert.Contains(panel.StatusText, "empty", panel.StatusText);
            Click(panel, "VncFilesUp");
            await panel.Operation!;
            Assert.AreEqual("C:\\", panel.Directory);
            Click(panel, "VncFilesUp");
            await panel.Operation!;
            Assert.IsNull(panel.Directory, "above the drive root: the drive list");
            Assert.IsInstanceOfType<VncRemoteDrive>(panel.Items.Single());

            Click(panel, "VncFilesClose");
            Assert.IsFalse(panel.IsVisible);
            view.ToggleFiles();
            Assert.IsTrue(panel.IsVisible);
            Assert.AreEqual(1, files.Calls.Count(static c => c == "access"), "access is asked once");
        }
        window.Close();
    });

    [TestMethod]
    public void ARefusalIsShownAndNothingIsListed() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        FakeFiles files = Files();
        files.Grant = false;
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(new FakeVncSession { FileTransfer = files }, context, new FakeLocal());
        await using (view)
        {
            Assert.AreEqual(new Loc("en")["FilesDenied"], panel.StatusText);
            Assert.IsEmpty(panel.Items);
            Assert.IsFalse(Find<Button>(panel, "VncFilesUpload").IsEnabled);
        }
        window.Close();
    });

    [TestMethod]
    public void UnsupportedTransferIsLocalizedAndLeavesTheDesktopUsable() => Run(static async () =>
    {
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            using var context = new TestPluginContext { PluginId = "velashell.vnc" };
            FakeFiles files = Files();
            files.AccessFailure = new VncFileTransferException(VncFileTransferError.Unsupported, "capability not advertised");
            var session = new FakeVncSession { FileTransfer = files };
            (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(session, context, new FakeLocal(), locale);
            await using (view)
            {
                Assert.AreEqual(new Loc(locale)["FilesUnsupported"], panel.StatusText);
                Assert.AreEqual(panel.StatusText, ToolTip.GetTip(Find<TextBlock>(panel, "VncFilesStatus")), "the full explanation remains available despite status-line trimming");
                StringAssert.Contains(panel.StatusText, "TigerVNC");
                StringAssert.Contains(panel.StatusText, "SFTP");
                Assert.IsEmpty(panel.Items);
                Assert.AreEqual(0, files.DriveListCount);
                Assert.AreSequenceEqual(new[] { "access" }, files.Calls.ToArray());
                Assert.IsFalse(files.IsGranted);
                Assert.IsFalse(panel.IsBusy);
                Assert.IsFalse(Find<Button>(panel, "VncFilesUpload").IsEnabled);
                Assert.IsFalse(Find<Button>(panel, "VncFilesNewFolder").IsEnabled);
                Assert.IsTrue(view.Desktop.IsEnabled);
                Assert.IsTrue(session.IsConnected);
                Assert.AreEqual(0, session.DisposeCount);
            }
            window.Close();
        }
    });

    [TestMethod]
    public void UploadingOverAnExistingFileAsksThenDeletesItFirst() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        FakeFiles files = Files();
        var local = new FakeLocal();
        local.ToOpen.Add(new("a.txt", 2, DateTimeOffset.Now, () => Task.FromResult<Stream>(new MemoryStream([9, 9]))));
        local.ToOpen.Add(new("new.bin", 1, DateTimeOffset.Now, () => Task.FromResult<Stream>(new MemoryStream([7]))));
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(new FakeVncSession { FileTransfer = files }, context, local);
        await using (view)
        {
            Click(panel, "VncFilesUpload");
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(panel.PromptVisible, "replacing a.txt needs a confirmation");
            Assert.IsFalse(files.Calls.Any(static c => c.StartsWith("upload", StringComparison.Ordinal)));
            panel.Answer("");
            await Until(() => panel.StatusText.Contains("Uploaded", StringComparison.Ordinal));
            await panel.Operation!;
            int delete = files.Calls.IndexOf("delete C:\\a.txt");
            Assert.IsGreaterThanOrEqualTo(0, delete);
            Assert.AreEqual("upload C:\\a.txt 2", files.Calls[delete + 1], "deleted first: UltraVNC would resume onto the old file");
            Assert.Contains("upload C:\\new.bin 1", files.Calls);
            Assert.DoesNotContain("delete C:\\new.bin", files.Calls);
            byte[] uploaded = files.Contents["C:\\a.txt"];
            Assert.AreSequenceEqual(new byte[] { 9, 9 }, uploaded);
            StringAssert.Contains(panel.StatusText, "Uploaded 2 file(s).");
        }
        window.Close();
    });

    [TestMethod]
    public void DeleteNeedsAConfirmationAndDownloadWritesTheLocalFile() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        FakeFiles files = Files();
        var local = new FakeLocal();
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(new FakeVncSession { FileTransfer = files }, context, local);
        await using (view)
        {
            Select(panel, "a.txt");
            Click(panel, "VncFilesDelete");
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(panel.PromptVisible);
            panel.Answer(null);
            Dispatcher.UIThread.RunJobs();
            Assert.IsFalse(panel.PromptVisible);
            Assert.DoesNotContain("delete C:\\a.txt", files.Calls, "cancelled");

            Select(panel, "a.txt");
            Click(panel, "VncFilesDownload");
            await Until(() => panel.Operation is { IsCompleted: true } && files.Calls.Contains("download C:\\a.txt"));
            await panel.Operation!;
            byte[] saved = local.Saved.ToArray();
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3 }, saved);
            Assert.IsFalse(local.Deleted);

            Select(panel, "a.txt");
            Click(panel, "VncFilesRename");
            Dispatcher.UIThread.RunJobs();
            panel.Answer("bad/name");
            Assert.IsTrue(panel.PromptVisible, "an invalid name keeps the prompt open");
            panel.Answer("b.txt");
            await Until(() => files.Calls.Contains("rename C:\\a.txt C:\\b.txt"));
        }
        window.Close();
    });

    [TestMethod]
    public void AFailedListingIsShownInTheStatusLine() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        FakeFiles files = Files();
        files.Folders.Remove("C:\\docs\\");
        (VncWorkspaceView view, VncFilePanel panel, Window window) = await OpenAsync(new FakeVncSession { FileTransfer = files }, context, new FakeLocal());
        await using (view)
        {
            Select(panel, "docs");
            Find<ListBox>(panel, "VncFilesList").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await Until(() => panel.Operation is { IsCompleted: true });
            Assert.AreEqual(new Loc("en").Format("FilesErrorUnavailable", "C:\\docs\\"), panel.StatusText);
            Assert.AreEqual("C:\\", panel.Directory, "still in the folder that could be listed");
        }
        window.Close();
    });

    [TestMethod]
    public void AudioIsShownAndAFailureIsAnnounced() => Run(static async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var session = new FakeVncSession { AudioActive = true };
        await using var view = new VncWorkspaceView(session, context, Settings(), new("en"));
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        StringAssert.Contains(view.StatusText, " · Audio");
        session.AudioActive = false;
        session.FailAudio(new IOException("no device"));
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(new Loc("en").Format("AudioFailed", "no device"), view.StatusText);
        Assert.IsTrue(view.Desktop.IsEnabled, "the desktop stays usable");
        window.Close();
    });

    [TestMethod]
    public void PathHelpers()
    {
        Assert.AreEqual("C:\\a\\", VncFilePanel.ParentOf("C:\\a\\b\\"));
        Assert.AreEqual("C:\\", VncFilePanel.ParentOf("C:\\a\\"));
        Assert.IsNull(VncFilePanel.ParentOf("C:\\"));
        Assert.IsTrue(VncFilePanel.IsValidName("report 2024.txt"));
        Assert.IsFalse(VncFilePanel.IsValidName(" "));
        Assert.IsFalse(VncFilePanel.IsValidName(".."));
        Assert.IsFalse(VncFilePanel.IsValidName("a*b"));
        Assert.AreEqual("1.5 KB", VncFilePanel.FormatSize(1536).Replace(',', '.'));
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.IsTrue(condition());
    }
}
