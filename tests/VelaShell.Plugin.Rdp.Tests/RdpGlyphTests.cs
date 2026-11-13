using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>The glyph's colour comes from host tokens it resolves itself (it has no styled property; see RdpGlyph).</summary>
[TestClass]
public sealed class RdpGlyphTests
{
    [TestMethod]
    public void IconButtonGlyph_UsesRestToken_SwitchesToHoverToken_AndFollowsResourceChanges() => RdpHeadlessTests.Run(static () =>
    {
        var rest = new SolidColorBrush(Colors.Red);
        var hover = new SolidColorBrush(Colors.Blue);
        var glyph = new RdpGlyph { Data = Geometry.Parse("M4 4L20 20"), Width = 16, Height = 16 };
        var button = new Button { Content = glyph, Width = 40, Height = 40, Background = Brushes.Transparent };
        RdpChrome.AddIconButtonStyles(button, "TestRest", "TestHover");
        var window = new Window { Width = 200, Height = 200, Content = new Panel { Children = { button } } };
        window.Resources["TestRest"] = rest;
        window.Resources["TestHover"] = hover;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.AreSame(rest, glyph.Foreground);
            // Headless pointer tracking is unreliable; the pseudo-class is what pointer-over sets.
            ((IPseudoClasses)button.Classes).Set(":pointerover", true);
            Dispatcher.UIThread.RunJobs();
            Assert.AreSame(hover, glyph.Foreground);
            ((IPseudoClasses)button.Classes).Set(":pointerover", false);
            Assert.AreSame(rest, glyph.Foreground);
            Dispatcher.UIThread.RunJobs();
            var changed = new SolidColorBrush(Colors.Green);
            window.Resources["TestRest"] = changed;
            Dispatcher.UIThread.RunJobs();
            Assert.AreSame(changed, glyph.Foreground, "A theme / accent change must reach the glyph.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
}
