using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.PluginSdk.Testing;
using VelaShell.Rdp;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;
using static VelaShell.Plugin.Rdp.Tests.RdpHeadlessTests;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>Picture quality, frame-rate and bandwidth behaviour added after the 2026-11-01 review.</summary>
[TestClass]
public sealed class RdpRenderingTests
{
    [TestMethod]
    [DataRow(1.0, 1920, 1080, 1920.0, 1080.0)]   // exact match
    [DataRow(1.0, 1914, 1075, 1920.0, 1080.0)]   // a few pixels short (resize jitter skip): drawn 1:1, not upscaled
    [DataRow(1.5, 1920, 1080, 1280.0, 720.0)]    // 150%: 1920 device px are 1280 DIPs
    [DataRow(1.25, 1599, 899, 1280.0, 720.0)]    // odd device size at 125%
    public void ADesktopTheSizeOfTheViewIsDrawnOneDevicePixelPerPixel(double scaling, int width, int height, double viewWidth, double viewHeight)
    {
        Rect viewport = RdpInput.Viewport(new(viewWidth, viewHeight), width, height, fit: true, scaling);
        Assert.AreEqual(width, viewport.Width * scaling, 1e-6, "no horizontal resampling");
        Assert.AreEqual(height, viewport.Height * scaling, 1e-6, "no vertical resampling");
        Assert.AreEqual(Math.Round(viewport.X * scaling), viewport.X * scaling, 1e-6, "origin on the device-pixel grid");
        Assert.AreEqual(Math.Round(viewport.Y * scaling), viewport.Y * scaling, 1e-6);
    }

    [TestMethod]
    public void ARealMismatchIsStillScaledToFit()
    {
        // A server that cannot follow the window: 1024×768 in a 1920×1080 view is scaled up, not shown postage-stamp size.
        Rect viewport = RdpInput.Viewport(new(1920, 1080), 1024, 768, fit: true, 1.0);
        Assert.AreEqual(1080d, viewport.Height, 1e-6);
        // Larger than the view: scaled down so nothing is cropped.
        Rect shrunk = RdpInput.Viewport(new(800, 600), 1920, 1080, fit: true, 1.0);
        Assert.AreEqual(800d, shrunk.Width, 1e-6);
    }

    [TestMethod]
    public void TheClientCoreDataCarriesTheDisplayScaleOnlyWhenItIsNotOneHundredPercent()
    {
        byte[] plain = GccData.BuildClientData(1920, 1080, 32, 1, "VelaShell", 0x409, []);
        byte[] scaled = GccData.BuildClientData(1920, 1080, 32, 1, "VelaShell", 0x409, [], scaleFactor: 150);
        Assert.AreEqual(18, scaled.Length - plain.Length, "physical w/h, orientation, desktop and device scale");
        // The tail sits at the end of CS_CORE: desktopScaleFactor 150, deviceScaleFactor 140 (the nearest allowed bucket).
        int at = IndexOf(scaled, [0x96, 0, 0, 0, 0x8C, 0, 0, 0]);
        Assert.IsGreaterThan(0, at);
    }

    [TestMethod]
    public void TheServerSaysWhetherItTakesSuppressOutputAndRefreshRect()
    {
        var w = new ByteWriter(64);
        w.U32LE(1); w.U16LE(6); w.U16LE(0); w.Bytes("MSTSC\0"u8); w.U16LE(1); w.U16LE(0);
        w.U16LE(1); w.U16LE(24);                       // CAPSTYPE_GENERAL
        w.Zeros(18);
        w.U8(1); w.U8(1);                              // refreshRectSupport, suppressOutputSupport
        var r = new ByteReader(w.Span);
        Capabilities.DemandActive active = Capabilities.ReadDemandActive(ref r);
        Assert.IsTrue(active.RefreshRect);
        Assert.IsTrue(active.SuppressOutput);
    }

    [TestMethod]
    [DataRow("", 32)]
    [DataRow("16", 16)]
    [DataRow("24", 24)]
    [DataRow("32", 32)]
    public void ColourDepthDefaultsToHighestQuality(string value, int expected)
    {
        var settings = RdpSettings.From(RdpSettingsTests.Request((RdpSettings.ColorDepthKey, value)), new("en"));
        Assert.AreEqual(expected, settings.ColorDepth);
    }

    [TestMethod]
    public void AnUnknownColourDepthIsRejected() =>
        Assert.Throws<VelaShell.PluginSdk.Protocols.ProtocolConnectionException>(() =>
            RdpSettings.From(RdpSettingsTests.Request((RdpSettings.ColorDepthKey, "8")), new("en")));

    [TestMethod]
    public void AHiddenViewPausesServerUpdatesAndShowingItResumesThem() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        await using var view = new RdpWorkspaceView(session, context, RdpSettings.From(RdpSettingsTests.Request(), new("en")), new("en"));
        var window = new Window { Width = 800, Height = 600, Content = view };
        window.Show();
        window.UpdateLayout();
        await Settle();
        Assert.IsEmpty(session.DisplayUpdates, "shown from the start: nothing to say");

        window.Content = null;          // the host detaches an inactive tab's view
        await Settle();
        session.Emit(new RdpFrame(2, 1, new byte[8]));
        Dispatcher.UIThread.RunJobs();
        Assert.AreNotEqual(2, view.Desktop.DesktopWidth, "a hidden view presents nothing");

        window.Content = view;          // back to this tab
        window.UpdateLayout();
        await Settle();
        Assert.AreSequenceEqual(new[] { false, true }, [.. session.DisplayUpdates]);
        Assert.AreEqual(2, view.Desktop.DesktopWidth, "the change made while hidden is presented on return");
        window.Close();
    });

    [TestMethod]
    public void ResizeRequestsCarryTheDisplayScaleAndAreRecordedForTheNextConnection() => Run(async () =>
    {
        RdpViewMetrics.ResetForTest();
        using var context = new TestPluginContext();
        var session = new FakeRdpSession();
        await using var view = new RdpWorkspaceView(session, context, RdpSettings.From(RdpSettingsTests.Request(), new("en")), new("en"));
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();
        window.UpdateLayout();
        for (int i = 0; i < 60 && session.ResizeScales.Count == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(20); }
        Assert.IsNotEmpty(session.ResizeScales, "the debounced resize was not sent");
        Assert.AreEqual(100, session.ResizeScales[^1]);   // the headless window renders at 1.0
        Assert.AreEqual((1000, 676, 100), RdpViewMetrics.Last);
        window.Close();
        RdpViewMetrics.ResetForTest();
    });

    private static async Task Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }
        return -1;
    }
}
