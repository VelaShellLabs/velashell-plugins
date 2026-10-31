using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

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
    public void RedirectionRequiresExplicitOptIn()
    {
        var settings = RdpSettings.From(Request(), new("en"));
        Assert.IsFalse(settings.Clipboard);
        Assert.IsEmpty(settings.Drives);
        Assert.AreEqual(1920, settings.Width);
        Assert.AreEqual(1080, settings.Height);
    }
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
        CollectionAssert.AreEqual(new[] { "C", "work" }, settings.Drives.Select(drive => drive.Name).ToArray());
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
        var invalid = Assert.ThrowsExactly<ProtocolConnectionException>(
            () => RdpSettings.From(Request(("drives", "docs=relative")), loc, []));
        Assert.Contains("「docs=relative」", invalid.Message);
        Assert.Contains(example, invalid.Message);

        string work = TempDir("work");
        var duplicate = Assert.ThrowsExactly<ProtocolConnectionException>(
            () => RdpSettings.From(Request(("drives", $"work={work};WORK={work}")), loc, []));
        Assert.Contains("「WORK」", duplicate.Message);

        string tooMany = string.Join(';', Enumerable.Range(0, 33).Select(i => $"d{i}={work}"));
        var limit = Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request(("drives", tooMany)), loc, []));
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
