using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpSettingsTests
{
    private static readonly Loc English = new("en");
    internal static WorkspaceConnectRequest Request(params (string Key, string Value)[] fields) => new()
    {
        SessionId = "rdp-test", Host = "windows.example", Port = 3389, Username = "user", Password = "private-test-secret",
        Settings = fields.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
    };

    [TestMethod]
    public void DefaultsMatchDeclarationsAndAdvancedFields()
    {
        RdpSettings settings = RdpSettings.From(Request(), English);
        Assert.AreEqual(1280, settings.DesktopWidth);
        Assert.AreEqual(720, settings.DesktopHeight);
        Assert.AreEqual(15000, settings.ConnectTimeoutMs);
        Assert.AreEqual(RdpScaleMode.Fit, settings.ScaleMode);
        var fields = RdpSettings.Declare(English);
        Assert.HasCount(10, fields);
        Assert.IsFalse(settings.RedirectClipboard);
        Assert.HasCount(0, settings.Drives);
        Assert.AreEqual("1280", fields.Single(f => f.Key == "desktopWidth").DefaultValue);
        Assert.AreEqual("720", fields.Single(f => f.Key == "desktopHeight").DefaultValue);
        Assert.AreEqual("15000", fields.Single(f => f.Key == "connectTimeout").DefaultValue);
        Assert.IsTrue(fields.Single(f => f.Key == "desktopWidth").IsAdvanced);
        Assert.IsTrue(fields.Single(f => f.Key == "scaleMode").IsAdvanced);
        Assert.IsFalse(fields.Single(f => f.Key == "domain").IsAdvanced);
        Assert.AreEqual(ProtocolSettingKind.SshSession, fields.Single(f => f.Key == "jumpSession").Kind);
        Assert.IsTrue(fields.Single(f => f.Key == RdpSettings.TrustedThumbprintKey).IsHidden);
        Assert.IsFalse(settings.ToString().Contains("private-test-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("desktopWidth", "319")]
    [DataRow("desktopWidth", "7681")]
    [DataRow("desktopWidth", "4294968576")]
    [DataRow("desktopHeight", "0")]
    [DataRow("desktopHeight", "4321")]
    [DataRow("connectTimeout", "999")]
    [DataRow("connectTimeout", "120001")]
    [DataRow("connectTimeout", "1e5")]
    [DataRow("desktopHeight", "")]
    [DataRow("scaleMode", "resize-server")]
    [DataRow("trustedThumbprint", "0123456789012345678901234567890123456789")]
    [DataRow("domain", "a\\b")]
    [DataRow("jumpSession", "a\nb")]
    public void InvalidSettingsAreRejectedInsteadOfClamped(string key, string value) =>
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((key, value)), English));

    [TestMethod]
    public void DriveSelectionIsExplicitNormalizedAndReadOnlyByDefault()
    {
        var settings = RdpSettings.From(Request(("redirectDrives", "d:\\;E:\\"), ("redirectClipboard", "true")), English);
        Assert.IsTrue(settings.RedirectClipboard);
        Assert.HasCount(2, settings.Drives);
        Assert.AreEqual("D:\\", settings.Drives[0].Root);
        Assert.AreEqual(4u, settings.Drives[0].DeviceId);
        Assert.IsTrue(settings.Drives.All(d => d.ReadOnly));
        var writable = RdpSettings.From(Request(("redirectDrives", "D:\\"), ("drivesReadOnly", "false")), English);
        Assert.IsFalse(writable.Drives[0].ReadOnly);
    }

    [TestMethod]
    [DataRow("C:\\folder")]
    [DataRow("C:")]
    [DataRow("C:/")]
    [DataRow("*")]
    [DataRow("\\\\server\\share")]
    [DataRow("C:\\;c:\\")]
    [DataRow("C:\\;")]
    public void InvalidDriveSelectionIsRejected(string value) =>
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request(("redirectDrives", value)), English));

    [TestMethod]
    [DataRow("redirectClipboard")]
    [DataRow("drivesReadOnly")]
    public void InvalidRedirectionBooleanIsRejected(string key) =>
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request((key, "yes")), English));

    [TestMethod]
    public void Sha256PinAcceptsDisplaySeparatorsButNotSha1()
    {
        string pin = string.Join(":", Enumerable.Repeat("ab", 32));
        RdpSettings settings = RdpSettings.From(Request(("trustedThumbprint", pin)), English);
        Assert.AreEqual(string.Concat(Enumerable.Repeat("AB", 32)), settings.TrustedThumbprint);
    }

    [TestMethod]
    [DataRow("user", "", "user", "")]
    [DataRow("DOMAIN\\user", "", "user", "DOMAIN")]
    [DataRow("DOMAIN\\user", "domain", "user", "DOMAIN")]
    [DataRow("user", "COMPUTER", "user", "COMPUTER")]
    [DataRow("user@domain.example", "", "user@domain.example", "")]
    public void AccountSyntaxIsNormalized(string input, string domain, string expectedUser, string expectedDomain)
    {
        var settings = RdpSettings.From(Request(("domain", domain)), English);
        var account = settings.ParseAccount(input, English);
        Assert.AreEqual(expectedUser, account.Username);
        Assert.AreEqual(expectedDomain, account.Domain);
    }

    [TestMethod]
    [DataRow("DOMAIN\\user", "OTHER")]
    [DataRow("user@domain.example", "DOMAIN")]
    [DataRow("a\\b\\c", "")]
    [DataRow("", "")]
    [DataRow("a\0b", "")]
    [DataRow("a@", "")]
    [DataRow("a@@b", "")]
    [DataRow("a@b\\user", "")]
    public void InvalidAccountIsConfigurationFailureNotLoginRetry(string input, string domain)
    {
        var settings = RdpSettings.From(Request(("domain", domain)), English);
        Assert.ThrowsExactly<ProtocolConnectionException>(() => settings.ParseAccount(input, English));
    }

    [TestMethod]
    [DataRow("", 3389)]
    [DataRow("http://host", 3389)]
    [DataRow("host name", 3389)]
    [DataRow("host", 0)]
    [DataRow("host", 65536)]
    public void InvalidTransportEndpointRejected(string host, int port) =>
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(Request() with { Host = host, Port = port }, English));

    [TestMethod]
    public void TunnelTargetIsAlsoValidated()
    {
        var request = Request() with { Tunnel = new("bad target", 3389, "jump") };
        Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.From(request, English));
    }

    [TestMethod]
    public void EmbeddedNulPasswordIsRejectedWithoutEchoingSecret()
    {
        var exception = Assert.ThrowsExactly<ProtocolConnectionException>(() => RdpSettings.ValidatePassword("private\0secret", English));
        Assert.IsFalse(exception.Message.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocaleKeysMirrorAndUnknownLocalesFallBackToEnglish()
    {
        Assert.IsTrue(Loc.KeysOf(true).ToHashSet(StringComparer.Ordinal).SetEquals(Loc.KeysOf(false)));
        var fallback = new Loc("ja");
        foreach (string key in Loc.KeysOf(false))
        {
            Assert.AreEqual(English[key], fallback[key]);
            Assert.AreNotEqual(key, new Loc("zh-Hans")[key]);
        }
    }
}
