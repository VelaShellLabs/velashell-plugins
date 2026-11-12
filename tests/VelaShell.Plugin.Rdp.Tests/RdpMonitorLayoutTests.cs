using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpMonitorLayoutTests
{
    [TestMethod]
    public void LayoutCopiesAndBoundsMonitors()
    {
        var monitors = new List<RdpMonitor>
        {
            new(0, 0, 1920, 1080, Primary: true),
            new(1920, 0, 2560, 1440)
        };

        var layout = new RdpMonitorLayout(monitors);
        monitors[0] = new RdpMonitor(0, 0, 1, 1, Primary: true);

        Assert.AreEqual(4480, layout.DesktopWidth);
        Assert.AreEqual(1440, layout.DesktopHeight);
        Assert.AreEqual(1920, layout.Monitors[0].Width);
        Assert.HasCount(2, layout.Monitors);
    }

    [TestMethod]
    public void LayoutRequiresExactlyOnePrimary()
    {
        Assert.Throws<ArgumentException>(static () => new RdpMonitorLayout(
        [new(0, 0, 100, 100), new(100, 0, 100, 100)]));

        Assert.Throws<ArgumentException>(static () => new RdpMonitorLayout(
        [new(0, 0, 100, 100, Primary: true), new(100, 0, 100, 100, Primary: true)]));

        _ = new RdpMonitorLayout(
        [new(0, 0, 100, 100, Primary: true), new(100, 0, 100, 100)]);
    }

    [TestMethod]
    public void LayoutRejectsOverlapAndNegativeOrigin()
    {
        Assert.Throws<ArgumentException>(static () => new RdpMonitorLayout(
        [
            new(0, 0, 100, 100, Primary: true),
            new(50, 50, 100, 100)
        ]));

        Assert.Throws<ArgumentException>(static () => new RdpMonitorLayout(
        [new(-1, 0, 100, 100, Primary: true)]));
    }
}
