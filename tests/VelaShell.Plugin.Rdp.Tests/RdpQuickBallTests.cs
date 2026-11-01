using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VelaShell.PluginSdk.Testing;
using static VelaShell.Plugin.Rdp.Tests.RdpHeadlessTests;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpQuickBallTests
{
    private const double Item = RdpQuickBall.ItemSize, Box = RdpQuickBall.BoxWidth;

    [TestMethod]
    [DataRow(1000, 700)]
    [DataRow(1000, 330)]
    [DataRow(420, 700)]
    public void FanOpensInwardsWithEveryActionInsideTheViewAndNothingOverlapping(int width, int height) => Run(async () =>
    {
        using var context = new TestPluginContext();
        await using var view = Open(new FakeRdpSession(), context, out Window window, width, height);
        RdpQuickBall quick = view.QuickBall;
        foreach (bool right in new[] { true, false })
        {
            if (!right) { quick.DragTo(new Point(40, quick.Bounds.Height / 2)); quick.Drop(); }
            Assert.AreEqual(right, quick.IsDockedRight);
            quick.Expand();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(quick.IsExpanded);
            Assert.IsTrue(Find<Border>(view, "RdpQuickBallScrim").IsVisible);
            Assert.HasCount(9, quick.Targets);
            var area = new Rect(quick.Bounds.Size);
            double s = quick.FanScale;   // small views shrink the whole fan, items included
            Assert.IsTrue(s is >= 0.6 and <= 1, $"scale {s}");
            var shapes = new List<(string Id, Rect Shape)> { ("ball", Circle(quick.BallCenter, RdpQuickBall.BallSize)) };
            foreach ((string id, Point c) in quick.Targets)
            {
                var box = new Rect(c.X - s * Box / 2, c.Y - s * Item / 2, s * Box, s * RdpQuickBall.BoxHeight);
                Assert.IsTrue(area.Contains(box), $"{id} at {box} leaves {area} ({width}×{height}, right={right})");
                Assert.AreEqual(right, c.X < quick.BallCenter.X, $"{id} must fan out away from the docked edge");
                shapes.Add((id, Circle(c, s * Item)));
                shapes.Add((id, new Rect(c.X - s * (Box - 10) / 2, c.Y + s * (Item / 2 + 4), s * (Box - 10), s * 14)));    // label pill
            }
            foreach (var a in shapes)
            {
                foreach (var b in shapes.Where(b => b.Id != a.Id))
                {
                    Assert.IsFalse(a.Shape.Intersects(b.Shape), $"{a.Id} {a.Shape} overlaps {b.Id} {b.Shape} ({width}×{height}, right={right})");
                }
            }
            quick.Collapse();
            Assert.IsFalse(Find<Border>(view, "RdpQuickBallScrim").IsVisible);
        }
        window.Close();
    });

    [TestMethod]
    [DataRow("SecureAttention", "1d+ 38+ 153+ 153- 38- 1d-")]
    [DataRow("TaskManager", "1d+ 2a+ 1+ 1- 2a- 1d-")]
    [DataRow("SwitchWindow", "38+ f+ f- 38-")]
    [DataRow("Start", "15b+ 15b-")]
    [DataRow("Run", "15b+ 13+ 13- 15b-")]
    [DataRow("ShowDesktop", "15b+ 20+ 20- 15b-")]
    [DataRow("Lock", "15b+ 26+ 26- 15b-")]
    [DataRow("PrintScreen", "137+ 137-")]
    public void EachKeyActionSendsItsChordAndFoldsTheFan(string id, string expected) => Run(async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        await using var view = Open(session, context, out Window window);
        view.QuickBall.Expand();
        Click(view, id);
        await WaitFor(() => session.Input.Count == expected.Split(' ').Length);
        Assert.AreEqual(expected, string.Join(' ', session.Input));
        Assert.IsFalse(view.QuickBall.IsExpanded);
        window.Close();
    });

    [TestMethod]
    public void TypeClipboardTypesShortTextAndAsksBeforeRiskyText() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        await using var view = Open(session, context, out Window window);
        RdpQuickBall quick = view.QuickBall;

        // A short single line (a password) goes straight through.
        context.FakeClipboard.Text = "P@ss word";
        quick.Expand();
        Click(view, "TypeClipboard");
        await WaitFor(() => session.Input.Count == 1);
        Assert.AreEqual("'P@ss word'", session.Input[0]);
        Assert.IsFalse(quick.IsExpanded);

        // Line breaks would press Enter (a console runs them): the first click only arms and explains.
        session.Input.Clear();
        context.FakeClipboard.Text = "user\tpass\r\nnext";
        quick.Expand();
        Click(view, "TypeClipboard");
        await Settle();
        Assert.IsEmpty(session.Input);
        Assert.IsTrue(quick.IsArmed("TypeClipboard"));
        Assert.AreEqual("Click again", LabelOf(view, "TypeClipboard"));
        StringAssertContains(Find<TextBlock>(view, "RdpStatus").Text, "Line breaks are sent as Enter");
        Click(view, "TypeClipboard");
        await WaitFor(() => session.Input.Count == 7);
        Assert.AreEqual("'user' f+ f- 'pass' 1c+ 1c- 'next'", string.Join(' ', session.Input));
        Assert.IsFalse(quick.IsExpanded);

        // Long text is confirmed against the text that was shown: a changed clipboard re-arms instead of typing.
        session.Input.Clear();
        context.FakeClipboard.Text = new string('a', 150);
        quick.Expand();
        Click(view, "TypeClipboard");
        await Settle();
        context.FakeClipboard.Text = new string('b', 150);
        Click(view, "TypeClipboard");
        await Settle();
        Assert.IsEmpty(session.Input);
        Assert.IsTrue(quick.IsArmed("TypeClipboard"));
        Click(view, "TypeClipboard");
        await WaitFor(() => session.Input.Count == 1);
        Assert.AreEqual("'" + new string('b', 150) + "'", session.Input[0]);

        // Over the limit or empty: refused with a status-bar notice, nothing typed.
        foreach ((string? text, string notice) in new[] { (new string('c', RdpWorkspaceView.TypeLimit + 1), "too long"), ((string?)null, "no text") })
        {
            session.Input.Clear();
            context.FakeClipboard.Text = text;
            quick.Expand();
            Click(view, "TypeClipboard");
            await Settle();
            Assert.IsEmpty(session.Input);
            Assert.IsFalse(quick.IsExpanded);
            StringAssertContains(Find<TextBlock>(view, "RdpStatus").Text, notice);
        }
        window.Close();
    });

    [TestMethod]
    public void EscapeFoldsAnOpenFanWithoutReachingTheRemote() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        await using var view = Open(session, context, out Window window);
        view.Desktop.Focus();
        view.QuickBall.Expand();
        PressEscape(view.Desktop);
        Assert.IsFalse(view.QuickBall.IsExpanded);
        await Settle();
        Assert.IsEmpty(session.Input);
        // With the fan closed, Esc is an ordinary remote key again.
        PressEscape(view.Desktop);
        await WaitFor(() => session.Input.Count == 1);
        Assert.AreEqual("1+", session.Input[0]);
        window.Close();
    });

    [TestMethod]
    public void DraggingTheBallSnapsItToTheNearerEdgeAndDoesNotOpenTheFan() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        await using var view = Open(session, context, out Window window);
        RdpQuickBall quick = view.QuickBall;
        Point start = quick.BallCenter;
        Assert.AreEqual(1000 - RdpQuickBall.EdgeGap - RdpQuickBall.BallSize / 2, start.X);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(new Point(start.X - 40, start.Y + 20));
        window.MouseMove(new Point(200, 500));
        window.MouseUp(new Point(200, 500), MouseButton.Left);
        await Animate();
        window.UpdateLayout();
        Assert.IsFalse(quick.IsExpanded, "A drag must not also count as a click.");
        Assert.IsFalse(quick.IsDockedRight);
        Assert.AreEqual(new Point(RdpQuickBall.EdgeGap + RdpQuickBall.BallSize / 2, 500), quick.BallCenter);
        // In the app the release that ends a drag is followed by the Button's Click; it must not open the fan …
        // (headless Button clicks need a fresh pointer-over scene, so raise Click the way that release would)
        Button ball = Find<Button>(view, "RdpQuickBall");
        ball.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(quick.IsExpanded, "The click that ends a drag must be swallowed.");
        // … while the next, ordinary click does.
        ball.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsTrue(quick.IsExpanded);
        // Clicking the dimmed desktop folds it and does not reach the remote.
        window.MouseDown(new Point(600, 300), MouseButton.Left);
        window.MouseUp(new Point(600, 300), MouseButton.Left);
        Assert.IsFalse(quick.IsExpanded);
        await Settle();
        Assert.IsEmpty(session.Mouse, "Pointer input on the ball or the dimmed backdrop must not reach the remote.");
        window.Close();
    });

    [TestMethod]
    public void BallRingsSitAboveTheButtonFollowTheAccentAndAnimateBetweenStates() => Run(async () =>
    {
        using var context = new TestPluginContext();
        await using var view = Open(new FakeRdpSession(), context, out Window window);
        // A user accent like the one in the bug report (Material pink): the ring must use it, whatever it is.
        var accent = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E91E63"));
        window.Resources["VelaAccent"] = accent;
        window.UpdateLayout();
        RdpQuickBall quick = view.QuickBall;
        Button button = Find<Button>(view, "RdpQuickBall");
        Border body = Find<Border>(view, "RdpQuickBallBody"), ring = Find<Border>(view, "RdpQuickBallRing"), halo = Find<Border>(view, "RdpQuickBallHalo");
        var layers = ((Panel)body.Parent!).Children;
        // The hover fill (the button) exactly fills the body, and both rings are separate layers above it,
        // so the fill can no longer slide over half of the ring.
        Assert.AreEqual(new Rect(0, 0, RdpQuickBall.BallSize, RdpQuickBall.BallSize), button.Bounds);
        Assert.IsGreaterThan(layers.IndexOf(body), layers.IndexOf(ring));
        Assert.IsLessThan(layers.IndexOf(body), layers.IndexOf(halo));
        Assert.IsFalse(ring.IsHitTestVisible);
        Assert.AreSame(accent, ring.BorderBrush);
        Assert.AreSame(accent, halo.BorderBrush);

        await Animate();
        Assert.AreEqual(0d, ring.Opacity, 0.01);
        Assert.AreEqual(0d, halo.Opacity, 0.01);
        quick.SetPointerOver(true);
        await Animate();
        Assert.AreEqual(0.7, ring.Opacity, 0.01);
        Assert.AreEqual(0.35, halo.Opacity, 0.01);
        quick.Expand();
        await Animate();
        Assert.AreEqual(1d, ring.Opacity, 0.01);
        var glyphs = ((Panel)button.Content!).Children;
        Assert.AreEqual(0d, glyphs[0].Opacity, 0.01, "keyboard glyph fades out");
        Assert.AreEqual(1d, glyphs[1].Opacity, 0.01, "× fades in");
        quick.SetPointerOver(false);
        quick.Collapse();
        await Animate();
        Assert.AreEqual(0d, ring.Opacity, 0.01);
        Assert.AreEqual(1d, glyphs[0].Opacity, 0.01);

        // Fan items: same layering; hover and "armed" light the accent ring.
        quick.Expand();
        Button task = Find<Button>(view, "QuickTaskManager");
        var face = (Panel)((Border)task.Parent!).Parent!;
        Border itemRing = face.Children.OfType<Border>().Single(b => b.Classes.Contains("accent"));
        Assert.IsGreaterThan(face.Children.IndexOf((Border)task.Parent!), face.Children.IndexOf(itemRing));
        await Animate();
        Assert.AreEqual(0d, itemRing.Opacity, 0.01);
        ((Avalonia.Controls.IPseudoClasses)face.Classes).Set(":pointerover", true);
        await Animate();
        Assert.AreEqual(1d, itemRing.Opacity, 0.01);
        ((Avalonia.Controls.IPseudoClasses)face.Classes).Set(":pointerover", false);
        quick.Arm("TaskManager");
        await Animate();
        Assert.AreEqual(1d, itemRing.Opacity, 0.01, "an armed item keeps its accent ring");
        window.Close();
    });

    [TestMethod]
    public void DisconnectFoldsAndHidesTheBall() => Run(async () =>
    {
        using var context = new TestPluginContext();
        await using var view = Open(new FakeRdpSession(), context, out Window window);
        view.QuickBall.Expand();
        view.SetConnected(false);
        Assert.IsFalse(view.QuickBall.IsExpanded);
        Assert.IsFalse(view.QuickBall.IsVisible);
        view.SetConnected(true);
        Assert.IsTrue(view.QuickBall.IsVisible);
        window.Close();
    });

    private static RdpWorkspaceView Open(FakeRdpSession session, TestPluginContext context, out Window window, int width = 1000, int height = 700)
    {
        var view = new RdpWorkspaceView(session, context, RdpSettings.From(RdpSettingsTests.Request(), new("en")), new("en"));
        window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        window.UpdateLayout();
        return view;
    }

    private static Rect Circle(Point center, double size) => new(center.X - size / 2, center.Y - size / 2, size, size);

    private static void Click(RdpWorkspaceView view, string id) =>
        Find<Button>(view, "Quick" + id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static string LabelOf(RdpWorkspaceView view, string id) =>
        Find<Button>(view, "Quick" + id).FindAncestorOfType<StackPanel>()!.Children.OfType<Border>().Last().Child is TextBlock label ? label.Text! : "";

    private static void PressEscape(Control target) => target.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, PhysicalKey = PhysicalKey.Escape, Source = target
    });

    private static void StringAssertContains(string? text, string part) =>
        Assert.IsTrue(text?.Contains(part, StringComparison.OrdinalIgnoreCase) == true, $"\"{text}\" should contain \"{part}\"");

    /// <summary>Lets transitions (the snap to the edge) finish: wait out their duration, then tick the render clock.</summary>
    private static async Task Animate()
    {
        for (int i = 0; i < 4; i++) { await Task.Delay(80); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    }

    private static async Task Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Assert.IsTrue(condition(), "Timed out waiting for the remote input.");
    }
}
