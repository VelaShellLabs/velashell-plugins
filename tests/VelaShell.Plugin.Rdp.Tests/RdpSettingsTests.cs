using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpSettingsTests
{
    internal static WorkspaceConnectRequest Request(params (string Key, string Value)[] settings) => new()
    {
        SessionId = "test",
        Host = "desktop.example",
        Port = 3389,
        Username = "alice",
        Password = "not-logged",
        Settings = settings.ToDictionary(pair => pair.Key, pair => pair.Value)
    };
    [TestMethod]
    public void ClipboardIsEnabledByDefaultWhileSensitiveRedirectionStaysOptIn()
    {
        var settings = RdpSettings.From(Request(), new("en"));
        Assert.IsTrue(settings.Clipboard);
        Assert.IsFalse(RdpSettings.From(Request(("clipboard", "false")), new("en")).Clipboard);
        Assert.IsFalse(settings.SmartCardRedirection);
        Assert.AreEqual("true", RdpSettings.Declare(new("en"), []).Single(field => field.Key == "clipboard").DefaultValue);
        Assert.IsNull(settings.Gateway);
        Assert.IsEmpty(settings.Drives);
        Assert.AreEqual(1920, settings.Width);
        Assert.AreEqual(1080, settings.Height);
    }

    [TestMethod]
    public void GatewayCredentialsAndCertificateTrustStaySeparateFromDesktop()
    {
        var settings = RdpSettings.From(Request((RdpSettings.GatewayHostKey, "gateway.example"),
            (RdpSettings.GatewayReuseCredentialsKey, "false"), (RdpSettings.GatewayUsernameKey, "gateway-user"),
            (RdpSettings.GatewayPasswordKey, "gateway-secret"), (RdpSettings.GatewayDomainKey, "EDGE"),
            (RdpSettings.GatewayPinKey, new string('b', 64)), (RdpSettings.TrustedThumbprintKey, new string('a', 64)),
            (RdpSettings.SmartCardKey, "true")), new("en"), []);
        Assert.IsNotNull(settings.Gateway);
        Assert.IsFalse(settings.Gateway.UseRdpCredentials);
        Assert.AreEqual("gateway-secret", settings.Gateway.Password);
        Assert.AreEqual(new string('B', 64), Convert.ToHexString(settings.Gateway.CertificatePin!));
        Assert.AreEqual(new string('A', 64), settings.CertificateSha256);
        Assert.IsFalse(settings.ToString().Contains("gateway-secret", StringComparison.Ordinal));
        Assert.IsTrue(settings.SmartCardRedirection);
        ProtocolSettingField password = RdpSettings.Declare(new("en"), []).Single(f => f.Key == RdpSettings.GatewayPasswordKey);
        Assert.IsTrue(password.IsSecret);
        Assert.AreEqual(ProtocolSettingKind.Password, password.Kind);
    }

    [TestMethod]
    [DataRow("gatewayHost", "https://gateway.example")]
    [DataRow("gatewayPort", "0")]
    [DataRow("gatewayCertificateSha256", "abc")]
    [DataRow("gatewayUseRdpCredentials", "false")]
    [DataRow("smartCard", "yes")]
    public void RejectsInvalidGatewayAndSmartCardConfiguration(string key, string value)
    {
        var values = new Dictionary<string, string> { [RdpSettings.GatewayHostKey] = "gateway.example", [key] = value };
        WorkspaceConnectRequest request = Request() with { Settings = values };
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(request, new("en"), []));
    }

    [TestMethod]
    public void RemoteAppRequiresPublishedAliasAndPreservesArguments()
    {
        Assert.IsNull(RdpSettings.From(Request(), new("en"), []).RemoteApp);
        var settings = RdpSettings.From(Request((RdpSettings.RemoteAppProgramKey, "||notepad"),
            (RdpSettings.RemoteAppArgumentsKey, "\"C:\\shared files\\note.txt\""),
            (RdpSettings.RemoteAppWorkingDirectoryKey, "C:\\shared files")), new("en"), []);
        Assert.IsNotNull(settings.RemoteApp);
        Assert.AreEqual("||notepad", settings.RemoteApp.Program);
        Assert.AreEqual("\"C:\\shared files\\note.txt\"", settings.RemoteApp.Arguments);
        Assert.AreEqual("C:\\shared files", settings.RemoteApp.WorkingDirectory);
        var shorthand = RdpSettings.From(Request((RdpSettings.RemoteAppProgramKey, "notepad")), new("en"), []);
        Assert.AreEqual("||notepad", shorthand.RemoteApp!.Program);
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((RdpSettings.RemoteAppProgramKey, "notepad.exe")), new("en"), []));
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((RdpSettings.RemoteAppArgumentsKey, "orphan")), new("en"), []));
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((RdpSettings.RemoteAppProgramKey, "||app"),
            (RdpSettings.RemoteAppArgumentsKey, "bad\0argument")), new("en"), []));
    }

    [TestMethod]
    public void GatewayDoesNotSilentlyBypassAnSshJump()
    {
        WorkspaceConnectRequest request = Request((RdpSettings.GatewayHostKey, "gateway.example")) with
        { Host = "127.0.0.1", Port = 12345, Tunnel = new("private.example", 3389, "Jump") };
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(request, new("en"), []));
    }

    [TestMethod]
    public void ExperienceTogglesDefaultToTheBandwidthSavingProfile()
    {
        var settings = RdpSettings.From(Request(), new("en"));
        Assert.IsFalse(settings.Audio);
        Assert.AreEqual(
            RdpPerformanceFlags.DisableWallpaper | RdpPerformanceFlags.DisableFullWindowDrag
                | RdpPerformanceFlags.DisableMenuAnimations | RdpPerformanceFlags.DisableThemes,
            settings.Performance);
    }

    [TestMethod]
    public void ExperienceTogglesMapToPerformanceFlags()
    {
        var settings = RdpSettings.From(Request(
            ("audio", "true"), ("wallpaper", "true"), ("windowDrag", "true"),
            ("animations", "true"), ("themes", "true"), ("fontSmoothing", "true")), new("en"));
        Assert.IsTrue(settings.Audio);
        // Every "show/enable" box ticked: no disable bits remain, font smoothing turned on.
        Assert.AreEqual(RdpPerformanceFlags.EnableFontSmoothing, settings.Performance);
    }

    [TestMethod]
    public void ExperienceFieldsLiveOnTheirOwnPage()
    {
        IReadOnlyList<ProtocolSettingField> fields = RdpSettings.Declare(new("en"), []);
        foreach (string key in new[] { "audio", "wallpaper", "windowDrag", "animations", "themes", "fontSmoothing" })
        {
            ProtocolSettingField field = fields.Single(f => f.Key == key);
            Assert.AreEqual(ProtocolSettingKind.Boolean, field.Kind);
            Assert.AreEqual("false", field.DefaultValue);
            Assert.AreEqual(RdpSettings.ExperiencePage, field.Page);
        }
    }

    [TestMethod]
    [DataRow("audio")]
    [DataRow("wallpaper")]
    [DataRow("fontSmoothing")]
    public void ExperienceTogglesRejectNonBooleanValues(string key) =>
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((key, "maybe")), new("en")));
    [TestMethod]
    [DataRow("width", "319")]
    [DataRow("height", "8193")]
    [DataRow("timeout", "0")]
    [DataRow("timeout", "121")]
    [DataRow("clipboard", "yes")]
    [DataRow("trustedThumbprint", "0123")]
    [DataRow("domain", "bad\0domain")]
    [DataRow("drives", "docs=relative")]
    public void InvalidSettingsFailBeforeCallingNative(string key, string value) =>
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((key, value)), new("en")));

    [TestMethod]
    public void TunnelConnectsLocallyButVerifiesRealServer()
    {
        string pin = new('a', 64);
        WorkspaceConnectRequest request = Request(("trustedThumbprint", pin)) with
        { Host = "127.0.0.1", Port = 12345, Tunnel = new("real.example", 3389, "Jump") };
        var settings = RdpSettings.From(request, new("en"));
        Assert.AreEqual("real.example", settings.CertificateHost);
        Assert.AreEqual(pin.ToUpperInvariant(), settings.CertificateSha256);
    }
    [TestMethod]
    public void DriveMappingsAcceptMultipleExplicitExistingPathsAndRejectDuplicateNames()
    {
        string path = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var settings = RdpSettings.From(Request(("drives", $"docs={path}\r\nfiles={path}")), new("en"));
        Assert.HasCount(2, settings.Drives);
        Assert.AreEqual(path, settings.Drives[1].Path);
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request(("drives", $"Docs={path}\ndocs={path}")), new("en")));
    }
    [TestMethod]
    [DataRow("too_long_name")]
    [DataRow("with-dash")]
    [DataRow("非ASCII")]
    public void InvalidShareNamesAreRejected(string name) => Assert.ThrowsExactly<ProtocolConnectionException>(
        () => RdpSettings.From(Request(("drives", $"{name}={Path.GetTempPath()}")), new("en")));

    [TestMethod]
    public void TooManySharesAreRejected()
    {
        string mappings = string.Join('\n', Enumerable.Range(0, 33).Select(i => $"d{i}={Path.GetTempPath()}"));
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request(("drives", mappings)), new("en")));
    }

    private static string TempDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vela-rdp-drives-" + name);
        _ = Directory.CreateDirectory(dir);
        return dir;
    }

    [TestMethod]
    public void CustomFoldersAcceptSemicolonsInTheSingleLineField()
    {
        string work = TempDir("work"), docs = TempDir("docs");
        var settings = RdpSettings.From(Request(("drives", $"work={work}; docs={docs};")), new("en"), []);
        Assert.HasCount(2, settings.Drives);
        Assert.AreEqual("work", settings.Drives[0].Name);
        Assert.AreEqual(work, settings.Drives[0].Path);
        Assert.AreEqual("docs", settings.Drives[1].Name);
    }

    [TestMethod]
    public void TickedDisksAreSharedWhileAbsentOrStaleOnesAreSkipped()
    {
        string c = TempDir("c"), d = TempDir("d");
        LocalDisk[] disks =
        [
            new("C", "C", c, LocalDiskKind.Fixed),
            new("D", "D", d, LocalDiskKind.Fixed),
            new("E", "E", Path.Combine(c, "unplugged-usb"), LocalDiskKind.Removable)
        ];
        // C ticked; D unticked; E ticked but not present right now; Z ticked on another machine.
        var settings = RdpSettings.From(
            Request(("drive:C", "true"), ("drive:D", "false"), ("drive:E", "true"), ("drive:Z", "true")), new("en"), disks);
        Assert.HasCount(1, settings.Drives);
        Assert.AreEqual("C", settings.Drives[0].Name);
        Assert.AreEqual(c, settings.Drives[0].Path);
    }

    [TestMethod]
    public void TickedDisksComeBeforeCustomFoldersAndNamesStayUnique()
    {
        string c = TempDir("c"), work = TempDir("work");
        LocalDisk[] disks = [new("C", "C", c, LocalDiskKind.Fixed)];
        var settings = RdpSettings.From(Request(("drive:C", "true"), ("drives", $"work={work}")), new("en"), disks);
        Assert.AreSequenceEqual(new[] { "C", "work" }, [.. settings.Drives.Select(drive => drive.Name)]);
        _ = Assert.ThrowsExactly<ProtocolConnectionException>(() =>
            RdpSettings.From(Request(("drive:C", "true"), ("drives", $"c={work}")), new("en"), disks));
    }

    [TestMethod]
    public void DisksAndCustomFoldersShareTheDriveMappingPage()
    {
        LocalDisk[] disks = [new("C", "C", @"C:\", LocalDiskKind.Fixed), new("E", "E", @"E:\", LocalDiskKind.Removable)];
        IReadOnlyList<ProtocolSettingField> fields = RdpSettings.Declare(new("zh-Hans"), disks);
        ProtocolSettingField c = fields.Single(field => field.Key == "drive:C");
        Assert.AreEqual(ProtocolSettingKind.Boolean, c.Kind);
        Assert.AreEqual("false", c.DefaultValue);
        Assert.AreEqual(RdpSettings.DrivesPage, c.Page);
        Assert.AreEqual("本地磁盘 (C:)", c.Label);
        Assert.AreEqual("可移动磁盘 (E:)", fields.Single(field => field.Key == "drive:E").Label);
        ProtocolSettingField custom = fields.Single(field => field.Key == "drives");
        Assert.AreEqual(RdpSettings.DrivesPage, custom.Page);
        Assert.AreEqual("自定义文件夹", custom.Label);
        List<string> keys = [.. fields.Select(field => field.Key)];
        Assert.IsTrue(keys.IndexOf("drive:C") < keys.IndexOf("drive:E") && keys.IndexOf("drive:E") < keys.IndexOf("drives"));
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("zh-Hans")]
    [DataRow("zh-Hant")]
    [DataRow("ja")]
    [DataRow("ko")]
    public void DeviceCheckboxGroupsPreserveKeysAndDefaultToOff(string locale)
    {
        var loc = new Loc(locale);
        LocalDisk[] disks = [new("C", "C", @"C:\", LocalDiskKind.Fixed), new("E", "E", @"E:\", LocalDiskKind.Removable)];
        var printer = new PrinterMapping("Office", "Driver");
        var wpd = new RdpWpdDevice("portable-device", "Phone");
        var camera = new RdpCameraDevice("camera-one", "Camera");
        IReadOnlyList<ProtocolSettingField> fields = RdpSettings.Declare(loc, disks, [printer], [camera], [wpd]);
        (string Key, string Group, string Label)[] expected =
        [
            ("drive:C", "drives", loc["DriveGroup"]),
            ("drive:E", "drives", loc["DriveGroup"]),
            (LocalPrinters.Key(printer), "printers", loc["Printers"]),
            (LocalWpdDevices.Key(wpd), "wpd", loc["WpdDevices"])
        ];
        Assert.HasCount(expected.Length, fields.Where(field => field.SelectionGroup is not null).ToArray());
        foreach ((string key, string group, string label) in expected)
        {
            ProtocolSettingField field = fields.Single(field => field.Key == key);
            Assert.AreEqual(ProtocolSettingKind.Boolean, field.Kind);
            Assert.AreEqual("false", field.DefaultValue);
            Assert.AreEqual(RdpSettings.DrivesPage, field.Page);
            Assert.AreEqual(new ProtocolSettingGroup(group, label), field.SelectionGroup);
        }
        Assert.AreEqual(printer.PrinterName, fields.Single(field => field.Key == LocalPrinters.Key(printer)).Label);
        Assert.AreEqual(wpd.Name, fields.Single(field => field.Key == LocalWpdDevices.Key(wpd)).Label);

        ProtocolSettingField cameraField = fields.Single(field => field.Key == LocalCameras.SettingKey);
        Assert.IsNull(cameraField.SelectionGroup);
        Assert.AreEqual(ProtocolSettingKind.Choice, cameraField.Kind);
        Assert.AreEqual("", cameraField.DefaultValue);
        Assert.AreSequenceEqual(new[] { "", LocalCameras.Key(camera) }, cameraField.Choices.Select(choice => choice.Value).ToArray());
        foreach (string key in new[] { RdpSettings.PortsKey, "drives" })
        {
            ProtocolSettingField field = fields.Single(field => field.Key == key);
            Assert.IsNull(field.SelectionGroup);
            Assert.AreEqual(ProtocolSettingKind.Text, field.Kind);
            Assert.AreEqual("", field.DefaultValue);
        }

        var settings = RdpSettings.From(Request(expected.Select(field => (field.Key, "false")).ToArray()), loc, disks,
            () => [printer], () => [camera], () => [wpd]);
        Assert.IsEmpty(settings.Drives);
        Assert.IsEmpty(settings.Printers);
        Assert.IsEmpty(settings.WpdDevices);
        Assert.IsNull(settings.Camera);
    }

    [TestMethod]
    public void CustomFolderFieldShowsConcreteExamplesThatStayVisible()
    {
        (string single, string several) = RdpSettings.CustomFolderExamples();
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            ProtocolSettingField custom = RdpSettings.Declare(new(locale), []).Single(field => field.Key == "drives");
            // The hint is always shown under the box; the placeholder disappears once anything is typed.
            string[] lines = custom.Hint!.Split('\n');
            Assert.HasCount(3, lines, locale);
            Assert.Contains(single, lines[1], locale);
            Assert.Contains(several, lines[1], locale);
            Assert.Contains(@"\\tsclient\work", lines[2], locale);
            Assert.AreEqual(several, custom.Placeholder, locale);
        }
        // The examples themselves must be accepted by the parser on this OS (paths aside, which must exist).
        Assert.IsTrue(single.StartsWith("work=", StringComparison.Ordinal) && Path.IsPathFullyQualified(single["work=".Length..]));
    }

    [TestMethod]
    public void CustomFolderErrorsNameTheEntryAndShowTheFormat()
    {
        var loc = new Loc("zh-Hans");
        string example = RdpSettings.CustomFolderExamples().Single;
        ProtocolConnectionException invalid = Assert.ThrowsExactly<ProtocolConnectionException>(
            () => RdpSettings.From(Request(("drives", "docs=relative")), loc, []));
        Assert.Contains("「docs=relative」", invalid.Message);
        Assert.Contains(example, invalid.Message);

        string work = TempDir("work");
        ProtocolConnectionException duplicate = Assert.ThrowsExactly<ProtocolConnectionException>(
            () => RdpSettings.From(Request(("drives", $"work={work};WORK={work}")), loc, []));
        Assert.Contains("「WORK」", duplicate.Message);

        string tooMany = string.Join(';', Enumerable.Range(0, 33).Select(i => $"d{i}={work}"));
        ProtocolConnectionException limit = Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request(("drives", tooMany)), loc, []));
        Assert.Contains("32", limit.Message);
    }

    [TestMethod]
    public void UnixDiskLabelsNameRootHomeAndMounts()
    {
        var loc = new Loc("en");
        Assert.AreEqual("Root (/)", RdpSettings.DiskLabel(loc, new("/", "root", "/", LocalDiskKind.Root)));
        Assert.AreEqual("Home (~)", RdpSettings.DiskLabel(loc, new("~", "home", "/home/joe", LocalDiskKind.Home)));
        Assert.AreEqual("USB (/media/joe/USB)", RdpSettings.DiskLabel(loc, new("/media/joe/USB", "USB", "/media/joe/USB", LocalDiskKind.Mount)));
    }

    [TestMethod]
    public void TranslationsCoverEveryVisibleAndErrorString()
    {
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            var loc = new Loc(locale);
            foreach (string key in Loc.Keys)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(loc[key]), $"{locale}: {key}");
            }
        }
    }
}
