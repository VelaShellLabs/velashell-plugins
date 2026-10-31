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
