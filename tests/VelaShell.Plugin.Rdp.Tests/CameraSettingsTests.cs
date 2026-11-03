using VelaShell.PluginSdk.Protocols;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class CameraSettingsTests
{
    [TestMethod]
    public void DisabledSelectionNeverEnumeratesAndMissingDeviceNeverFallsBack()
    {
        Assert.IsNull(LocalCameras.Selected(RdpSettingsTests.Request(), new("en"),
            () => throw new AssertFailedException("Disabled camera must not enumerate.")));
        var camera = new RdpCameraDevice("camera-one", "Camera");
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() => LocalCameras.Selected(
            RdpSettingsTests.Request(("camera", LocalCameras.Key(camera))), new("en"), () => []));
    }

    [TestMethod]
    public void SingleChoiceResolvesExactDeviceAndEnablesDvcWithFixedDesktop()
    {
        var camera = new RdpCameraDevice("camera-one", "Camera");
        ProtocolSettingField field = RdpSettings.Declare(new("en"), [], cameras: [camera]).Single(f => f.Key == "camera");
        Assert.AreEqual(ProtocolSettingKind.Choice, field.Kind);
        Assert.AreEqual("", field.DefaultValue);
        var settings = RdpSettings.From(RdpSettingsTests.Request(("camera", LocalCameras.Key(camera))), new("en"), [], enumerateCameras: () => [camera]);
        Assert.AreSame(camera, settings.Camera);
        var options = new RdpClientOptions { Host = "example", DynamicResolution = false, Camera = settings.Camera };
        Assert.Contains("drdynvc", [.. RdpManagedSession.BuildChannels(options).Select(c => c.Name)]);
        Assert.IsEmpty(RdpManagedSession.BuildChannels(options with { Camera = null }));
    }
}
