using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class LocalWpdDevicesTests
{
    [TestMethod]
    public void SettingsKeepSelectionExplicitAndEnableDvcWithoutDesktopResize()
    {
        var device = new RdpWpdDevice("fake-local-interface", "Portable device");
        var field = RdpSettings.Declare(new("en"), [], wpdDevices: [device]).Single(f => f.Key == LocalWpdDevices.Key(device));
        Assert.AreEqual(ProtocolSettingKind.Boolean, field.Kind);
        Assert.AreEqual("false", field.DefaultValue);
        var settings = RdpSettings.From(RdpSettingsTests.Request((field.Key, "true")), new("en"), [], enumerateWpdDevices: () => [device]);
        Assert.AreSequenceEqual(new[] { device.Id }, settings.WpdDevices);
        var options = new VelaShell.Rdp.RdpClientOptions { Host = "example", DynamicResolution = false, WpdDevices = settings.WpdDevices };
        Assert.Contains("drdynvc", VelaShell.Rdp.RdpManagedSession.BuildChannels(options).Select(c => c.Name).ToArray());
        Assert.IsEmpty(VelaShell.Rdp.RdpManagedSession.BuildChannels(options with { WpdDevices = [] }));
        Assert.IsEmpty(RdpSettings.From(RdpSettingsTests.Request(), new("en"), [],
            enumerateWpdDevices: () => throw new AssertFailedException("Disabled WPD must not enumerate.")).WpdDevices);
    }

    [TestMethod]
    public void EmptyOrUncheckedSelectionNeverEnumeratesDevices()
    {
        var loc = new Loc("en");
        var device = new RdpWpdDevice("fake-selected-interface", "Portable device");
        IReadOnlyList<RdpWpdDevice> Fail() => throw new AssertFailedException("Unselected devices must not be enumerated during connect.");
        Assert.IsEmpty(LocalWpdDevices.Selected(RdpSettingsTests.Request(), loc, Fail));
        Assert.IsEmpty(LocalWpdDevices.Selected(RdpSettingsTests.Request((LocalWpdDevices.Key(device), "false")), loc, Fail));
    }

    [TestMethod]
    public void SelectionResolvesOnlyCurrentLocalMetadataAndRejectsMissingOrMalformedSelections()
    {
        var loc = new Loc("en");
        var selected = new RdpWpdDevice("fake-selected-interface", "Portable device");
        var other = new RdpWpdDevice("fake-other-interface", "Other device");
        var request = RdpSettingsTests.Request((LocalWpdDevices.Key(selected), "true"));
        IReadOnlyList<string> actual = LocalWpdDevices.Selected(request, loc, () => [other, selected]);
        Assert.AreSequenceEqual(new[] { selected.Id }, actual);
        Assert.Throws<ProtocolConnectionException>(() => LocalWpdDevices.Selected(request, loc, () => [other]));
        Assert.Throws<ProtocolConnectionException>(() => LocalWpdDevices.Selected(
            RdpSettingsTests.Request((LocalWpdDevices.Key(selected), "yes")), loc, () => [selected]));
        Assert.Throws<ProtocolConnectionException>(() => LocalWpdDevices.Selected(
            RdpSettingsTests.Request(("wpd:C:\\arbitrary-file", "true")), loc, () => [selected]));
    }
}
