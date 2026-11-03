using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class PrinterSettingsTests
{
    [TestMethod]
    public void NoSelectionDoesNotEnumerateOrEnableAnyPrinter()
    {
        var printer = new PrinterMapping("Office", "Driver");
        IReadOnlyList<PrinterMapping> selected = LocalPrinters.Selected(RdpSettingsTests.Request((LocalPrinters.Key(printer), "false")), new("en"),
            () => throw new AssertFailedException("An unselected printer must not require discovery."));
        Assert.IsEmpty(selected);
    }

    [TestMethod]
    public void FormAndOptionsPreserveExplicitSelection()
    {
        var printer = new PrinterMapping("Office", "Driver", true);
        IReadOnlyList<ProtocolSettingField> fields = RdpSettings.Declare(new("en"), [], [printer]);
        ProtocolSettingField field = fields.Single(f => f.Key == LocalPrinters.Key(printer));
        Assert.AreEqual(ProtocolSettingKind.Boolean, field.Kind);
        Assert.AreEqual("false", field.DefaultValue);
        Assert.AreEqual(RdpSettings.DrivesPage, field.Page);
        var settings = RdpSettings.From(RdpSettingsTests.Request((field.Key, "true")), new("en"), [], () => [printer]);
        var options = new RdpClientOptions { Host = "example", Printers = settings.Printers };
        Assert.AreSame(printer, options.Printers.Single());
        Assert.IsEmpty(new RdpClientOptions { Host = "example" }.Printers);
        string[] channels = [.. RdpManagedSession.BuildChannels(options).Select(channel => channel.Name)];
        Assert.Contains("rdpdr", channels);
        Assert.Contains("rdpsnd", channels);
    }

    [TestMethod]
    public async Task BlockingQueueDiscoveryDoesNotBlockActivationOrDeactivation()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var context = new VelaShell.PluginSdk.Testing.TestPluginContext { PluginId = "velashell.rdp" };
        var plugin = new RdpPlugin(() => [], TimeSpan.FromHours(1), () =>
        {
            entered.Set();
            _ = release.Wait(TimeSpan.FromSeconds(10));
            return [new PrinterMapping("Office", "Driver")];
        });
        try
        {
            await plugin.ActivateAsync(context, default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await plugin.DeactivateAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsEmpty(context.RecordingWorkspaces.Registered);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public void OnlyExplicitlySelectedInstalledQueuesAreResolved()
    {
        var first = new PrinterMapping("Office; East", "Driver", true);
        var other = new PrinterMapping("Office West", "Driver");
        IReadOnlyList<PrinterMapping> selected = LocalPrinters.Selected(RdpSettingsTests.Request((LocalPrinters.Key(first), "true")), new("en"), () => [first, other]);
        Assert.HasCount(1, selected);
        Assert.AreSame(first, selected[0]);
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() => LocalPrinters.Selected(
            RdpSettingsTests.Request((LocalPrinters.Key(first), "true")), new("en"), () => [other]));
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() => LocalPrinters.Selected(
            RdpSettingsTests.Request((LocalPrinters.Key(first), "yes")), new("en"), () => [first]));
    }
}
