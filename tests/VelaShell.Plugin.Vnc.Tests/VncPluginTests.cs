using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc.Tests;

[TestClass]
public sealed partial class VncPluginTests
{
    internal static WorkspaceConnectRequest Request(Dictionary<string, string>? settings = null, WorkspaceTunnelInfo? tunnel = null,
        string password = "secret") => new()
        {
            SessionId = "s1",
            Host = "desktop.example",
            Port = 5900,
            Username = "",
            Password = password,
            DisplayName = "desk",
            Settings = settings ?? [],
            Tunnel = tunnel
        };

    // ---- manifest and registration ----

    [TestMethod]
    public async Task ManifestMatchesRegistrationAndLocaleKeepsTheProvider()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vnc-plugin.json")));
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        context.HostInfo.Locale = "en";
        var plugin = new VncPlugin();
        await plugin.ActivateAsync(context, default);
        WorkspaceDescriptor descriptor = context.RecordingWorkspaces.Registered.Single();
        Assert.AreEqual(manifest.RootElement.GetProperty("id").GetString(), descriptor.Id);
        Assert.AreEqual("onWorkspace:" + descriptor.Id, manifest.RootElement.GetProperty("activationEvents")[0].GetString());
        Assert.AreEqual(manifest.RootElement.GetProperty("contributes").GetProperty("workspaces")[0].GetProperty("defaultPort").GetInt32(), descriptor.DefaultPort);
        Assert.IsTrue(descriptor.Features.HasFlag(WorkspaceFeatures.CertificateTrust | WorkspaceFeatures.SshTunnel));
        Assert.IsTrue(descriptor.Fields.Single(f => f.Key == descriptor.TrustedThumbprintSettingKey).IsHidden);
        Assert.AreEqual(VncSettings.DisplayPage, descriptor.Fields.Single(f => f.Key == VncSettings.ScalingKey).Page);
        Assert.AreEqual(ProtocolSettingKind.SshSession, descriptor.Fields.Single(f => f.Key == VncSettings.JumpKey).Kind);
        IWorkspaceProvider? before = context.RecordingWorkspaces.GetProvider(descriptor.Id);
        context.HostEvents.RaiseLocaleChanged("zh-Hans");
        Assert.AreSame(before, context.RecordingWorkspaces.GetProvider(descriptor.Id));
        Assert.AreEqual("服务器地址", context.RecordingWorkspaces.Registered.Single().HostLabel);
        await plugin.DeactivateAsync(default);
        Assert.IsEmpty(context.RecordingWorkspaces.Registered);
    }

    [TestMethod]
    public void EveryStringHasAllFiveLanguagesWithTheSamePlaceholders()
    {
        foreach ((string key, string[] row) in Loc.Keys.Zip(Loc.Rows))
        {
            Assert.HasCount(5, row, key);
            string[] placeholders = [.. Placeholder().Matches(row[0]).Select(static m => m.Value).Order()];
            foreach (string text in row)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(text), key);
                Assert.AreSequenceEqual(placeholders, Placeholder().Matches(text).Select(static m => m.Value).Order().ToArray(), key);
            }
        }
        Assert.AreEqual("VNC 认证失败：bad", new Loc("zh-CN").Format("AuthFailedReason", "bad"));
        Assert.AreEqual("サーバーアドレス", new Loc("ja-JP")["Host"]);
    }

    [GeneratedRegex(@"\{\d\}")]
    private static partial Regex Placeholder();

    [TestMethod]
    public void EveryFieldLabelAndChoiceResolves()
    {
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            WorkspaceDescriptor descriptor = VncPlugin.Describe(new Loc(locale));
            Assert.IsTrue(descriptor.Fields.All(static f => f.Label.Length > 0));
            Assert.IsTrue(descriptor.Fields.Where(static f => f.Kind == ProtocolSettingKind.Choice).All(static f => f.Choices.Count > 1 && f.Choices.Any(c => c.Value == f.DefaultValue)));
        }
    }

    // ---- settings ----

    [TestMethod]
    public void DefaultsAreTheSecureResponsiveProfile()
    {
        var settings = VncSettings.From(Request(), new("en"));
        Assert.AreEqual(VncSecurity.Auto, settings.Security);
        Assert.IsTrue(settings.Shared);
        Assert.IsFalse(settings.ViewOnly);
        Assert.IsTrue(settings.Clipboard);
        Assert.AreEqual(VncScaling.Remote, settings.Scaling);
        Assert.AreEqual(8, settings.JpegQuality);
        Assert.AreEqual(2, settings.CompressionLevel);
        Assert.AreEqual(15, settings.TimeoutSeconds);
        Assert.IsTrue(settings.CommandAsControl);
        Assert.IsTrue(settings.ContinuousUpdates, "on by default: servers without it ignore the pseudo-encodings");
        Assert.AreEqual("desktop.example", settings.CertificateHost);
        Assert.IsFalse(settings.Audio, "audio is opt-in, as in the RDP plugin");
        Assert.IsFalse(settings.FileTransfer, "file transfer is opt-in");
        Assert.IsFalse(settings.AllowUnadvertisedFileTransfer, "legacy connections must retain capability checks");
        VncClientOptions options = VncConnector.CreateOptions(Request(), settings);
        Assert.IsFalse(options.FileTransfer);
        Assert.IsFalse(options.AllowUnadvertisedFileTransfer);
        // Every declared default parses to the same thing as an empty (pre-existing) connection.
        var declared = VncSettings.Declare(new("en")).Where(static f => f.DefaultValue is not null).ToDictionary(static f => f.Key, static f => f.DefaultValue!);
        Assert.AreEqual(settings, VncSettings.From(Request(declared), new("en")));
    }

    [TestMethod]
    public void ChoicesParseAndTheTunnelTargetNamesTheCertificate()
    {
        var settings = VncSettings.From(Request(new()
        {
            [VncSettings.SecurityKey] = "VeNCrypt",
            [VncSettings.SharedKey] = "false",
            [VncSettings.ViewOnlyKey] = "true",
            [VncSettings.ScalingKey] = "none",
            [VncSettings.QualityKey] = "lossless",
            [VncSettings.EncodingKey] = "zrle",
            [VncSettings.CompressionKey] = "9",
            [VncSettings.MacCommandKey] = "super",
            [VncSettings.ContinuousUpdatesKey] = "false",
            [VncSettings.AudioKey] = "true",
            [VncSettings.FileTransferKey] = "true",
            [VncSettings.TrustedThumbprintKey] = string.Join(":", Enumerable.Repeat("ab", 32))
        }, new WorkspaceTunnelInfo("10.0.0.5", 5901, "bastion")), new("en"));
        Assert.AreEqual(VncSecurity.VeNCrypt, settings.Security);
        Assert.IsFalse(settings.Shared);
        Assert.IsTrue(settings.ViewOnly);
        Assert.AreEqual(VncScaling.None, settings.Scaling);
        Assert.IsNull(settings.JpegQuality);
        Assert.AreEqual(VncEncodingPreference.Zrle, settings.Encoding);
        Assert.AreEqual(9, settings.CompressionLevel);
        Assert.IsFalse(settings.CommandAsControl);
        Assert.IsFalse(settings.ContinuousUpdates);
        Assert.AreEqual("10.0.0.5", settings.CertificateHost);
        Assert.IsTrue(settings.Audio);
        Assert.IsTrue(settings.FileTransfer);
        Assert.AreEqual(new string('A', 0) + string.Concat(Enumerable.Repeat("AB", 32)), settings.CertificateSha256);
    }

    [TestMethod]
    [DataRow(VncSettings.SecurityKey, "rsa")]
    [DataRow(VncSettings.ScalingKey, "stretch")]
    [DataRow(VncSettings.CompressionKey, "10")]
    [DataRow(VncSettings.TimeoutKey, "0")]
    [DataRow(VncSettings.SharedKey, "maybe")]
    [DataRow(VncSettings.FileTransferKey, "yes")]
    [DataRow(VncSettings.AllowUnadvertisedFileTransferKey, "yes")]
    [DataRow(VncSettings.TrustedThumbprintKey, "abcd")]
    public void InvalidValuesNameTheField(string key, string value)
    {
        ProtocolConnectionException ex = Assert.ThrowsExactly<ProtocolConnectionException>(() => VncSettings.From(Request(new() { [key] = value }), new("en")));
        StringAssert.Contains(ex.Message, key);
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    [DataRow("", false)]
    [DataRow("  TRUE  ", true)]
    public void LegacyFileTransferRequiresAnExplicitBoolean(string raw, bool expected)
    {
        VncSettings settings = VncSettings.From(Request(new()
        {
            [VncSettings.FileTransferKey] = "true",
            ["allowUnadvertisedFileTransfer"] = raw
        }), new("en"));
        Assert.AreEqual(expected, settings.AllowUnadvertisedFileTransfer);
    }

    [TestMethod]
    public void LegacyFileTransferIsAnAdvancedOptInVisibleOnlyForFileTransfer()
    {
        foreach (string locale in new[] { "en", "zh-Hans", "zh-Hant", "ja", "ko" })
        {
            var loc = new Loc(locale);
            ProtocolSettingField field = VncSettings.Declare(loc).Single(f => f.Key == "allowUnadvertisedFileTransfer");
            Assert.AreEqual(ProtocolSettingKind.Boolean, field.Kind);
            Assert.AreEqual("false", field.DefaultValue);
            Assert.IsTrue(field.IsAdvanced);
            Assert.IsNull(field.Page);
            Assert.AreEqual(loc["AllowUnadvertisedFileTransfer"], field.Label);
            Assert.AreEqual(loc["AllowUnadvertisedFileTransferHint"], field.Hint);
            Assert.IsNotNull(field.VisibleWhen);
            Assert.AreEqual(VncSettings.FileTransferKey, field.VisibleWhen.Key);
            Assert.AreSequenceEqual(new[] { "true" }, field.VisibleWhen.Values.ToArray());
            Assert.IsTrue(field.VisibleWhen.IsSatisfiedBy(key => key == VncSettings.FileTransferKey ? "true" : null));
            Assert.IsFalse(field.VisibleWhen.IsSatisfiedBy(_ => "false"));
            Assert.IsFalse(field.VisibleWhen.IsSatisfiedBy(_ => null));
        }
    }

    [TestMethod]
    [DataRow(false, false, false, false, false)]
    [DataRow(true, false, false, true, false)]
    [DataRow(true, true, false, true, true)]
    [DataRow(true, true, true, false, false)]
    [DataRow(false, true, false, false, false)]
    public void ConnectorMapsFileTransferOnlyWhenEnabledAndNotViewOnly(bool enabled, bool allowLegacy, bool viewOnly,
        bool expectedTransfer, bool expectedLegacy)
    {
        WorkspaceConnectRequest request = Request(new()
        {
            [VncSettings.FileTransferKey] = enabled.ToString(),
            [VncSettings.AllowUnadvertisedFileTransferKey] = allowLegacy.ToString(),
            [VncSettings.ViewOnlyKey] = viewOnly.ToString()
        });
        VncSettings settings = VncSettings.From(request, new("en"));
        VncClientOptions options = VncConnector.CreateOptions(request, settings);
        Assert.AreEqual(expectedTransfer, options.FileTransfer);
        Assert.AreEqual(expectedLegacy, options.AllowUnadvertisedFileTransfer);
        Assert.AreEqual(allowLegacy, settings.AllowUnadvertisedFileTransfer, "hidden values remain saved but must not bypass effective settings");
    }

    // ---- input ----

    [TestMethod]
    public void KeysymsAndScanCodes()
    {
        Assert.AreEqual((0x61u, 0x1eu), VncInput.Key(PhysicalKey.A, VncInput.KeyboardPlatform.Windows, true));
        Assert.AreEqual((0xff52u, 0x148u), VncInput.Key(PhysicalKey.ArrowUp, VncInput.KeyboardPlatform.Other, true));
        Assert.AreEqual((VncInput.ControlL, 0x1du), VncInput.Key(PhysicalKey.MetaLeft, VncInput.KeyboardPlatform.MacOS, true));
        Assert.AreEqual((VncInput.SuperL, 0x15bu), VncInput.Key(PhysicalKey.MetaLeft, VncInput.KeyboardPlatform.MacOS, false));
        Assert.AreEqual((VncInput.SuperL, 0x15bu), VncInput.Key(PhysicalKey.MetaLeft, VncInput.KeyboardPlatform.Windows, true));
        Assert.AreEqual((0u, 0u), VncInput.Key(PhysicalKey.None, VncInput.KeyboardPlatform.Windows, true));
        Assert.AreEqual(0x41u, VncInput.CharacterKeysym(new Rune('A')));
        Assert.AreEqual(0xE9u, VncInput.CharacterKeysym(new Rune('é')));
        Assert.AreEqual(0x01004E2Du, VncInput.CharacterKeysym(new Rune('中')));
        Assert.AreEqual(0x0101F600u, VncInput.CharacterKeysym(Rune.GetRuneAt("😀", 0)));
        Assert.AreEqual(VncInput.Return, VncInput.CharacterKeysym(new Rune('\n')));
        Assert.AreEqual("a\nb\tc", VncInput.NormalizeTypedText("a\r\nb\tc\u0007"));
    }

    [TestMethod]
    public void AltGrAndOptionTypeText()
    {
        Assert.IsFalse(VncInput.IsShortcut(KeyModifiers.Control | KeyModifiers.Alt, "@", VncInput.KeyboardPlatform.Windows));
        Assert.IsTrue(VncInput.IsShortcut(KeyModifiers.Control | KeyModifiers.Alt, "@", VncInput.KeyboardPlatform.Other));
        Assert.IsFalse(VncInput.IsShortcut(KeyModifiers.Alt, "@", VncInput.KeyboardPlatform.MacOS));
        Assert.IsTrue(VncInput.IsShortcut(KeyModifiers.Control, "c", VncInput.KeyboardPlatform.Windows));
        Assert.IsFalse(VncInput.IsShortcut(KeyModifiers.Shift, "A", VncInput.KeyboardPlatform.Windows));
    }

    [TestMethod]
    public void WheelNotchesCarryTheRemainder()
    {
        var wheel = new VncInput.WheelAccumulator();
        Assert.AreEqual(0, wheel.Add(0.4));
        Assert.AreEqual(1, wheel.Add(0.7));
        Assert.AreEqual(0, wheel.Add(-0.2), "a reversal drops the carried fraction");
        Assert.AreEqual(-3, wheel.Add(-3.1));
        Assert.AreEqual(0, wheel.Add(double.NaN));
    }

    [TestMethod]
    public void ViewportIsOneToOneWhenNativeAndMapsIntoTheDesktop()
    {
        Rect native = VncInput.Viewport(new Size(1280, 720), 1920, 1080, fit: true, renderScaling: 1.5);
        Assert.AreEqual(1280d, native.Width, 1e-9);
        Rect scaled = VncInput.Viewport(new Size(800, 600), 1600, 900, fit: true);
        Assert.AreEqual(800d, scaled.Width, 1e-9);
        Assert.AreEqual(450d, scaled.Height, 1e-9);
        Rect actual = VncInput.Viewport(new Size(800, 600), 1600, 900, fit: false);
        Assert.AreEqual(new Rect(0, 0, 1600, 900), actual);
        Assert.IsTrue(VncInput.Map(new Point(400, 300), scaled, 1600, 900, false, out int x, out int y));
        Assert.AreEqual((800, 450), (x, y));
        Assert.IsFalse(VncInput.Map(new Point(400, 10), scaled, 1600, 900, false, out _, out _), "letterbox");
        Assert.IsTrue(VncInput.Map(new Point(-50, 10), scaled, 1600, 900, true, out x, out y), "captured drags clamp");
        Assert.AreEqual((0, 0), (x, y));
    }

    // ---- connector ----

    [TestMethod]
    public void LibraryFailuresBecomeHostExceptions()
    {
        var loc = new Loc("en");
        VncSettings auto = VncSettings.From(Request(), loc), explicitArd = VncSettings.From(Request(new() { [VncSettings.SecurityKey] = "ard" }), loc);
        Assert.IsInstanceOfType<ProtocolAuthenticationException>(VncConnector.Translate(new VncAuthenticationException("nope"), auto, loc));
        StringAssert.Contains(VncConnector.Translate(new VncAuthenticationException("nope"), auto, loc).Message, "nope");
        Assert.AreEqual(loc["PasswordRequired"], VncConnector.Translate(new VncAuthenticationException(null, true), auto, loc).Message);
        Assert.AreEqual(loc["CredentialsRequired"], VncConnector.Translate(new VncAuthenticationException(null, true, true), explicitArd, loc).Message);
        Exception unsupported = VncConnector.Translate(new VncSecurityUnsupportedException([5, 16], "x"), auto, loc);
        Assert.IsInstanceOfType<ProtocolConnectionException>(unsupported);
        StringAssert.Contains(unsupported.Message, "Tight (16)");
        StringAssert.Contains(VncConnector.Translate(new VncSecurityUnsupportedException([2], "x"), explicitArd, loc).Message, "does not offer");
        StringAssert.Contains(VncConnector.Translate(new VncConnectionException(VncConnectStage.Tls, "handshake failed"), auto, loc).Message, "TLS handshake");
        Assert.IsNull(VncConnector.DisconnectReason(new VncConnectionLostException("reset"), loc));
        Assert.AreEqual(loc["ByeServerClosed"], VncConnector.DisconnectReason(null, loc));
        Assert.AreEqual(loc["ByeFileTransfer"], VncConnector.DisconnectReason(new VncFileTransferUnsupportedException("x"), loc));
    }

    [TestMethod]
    public void ARejectedCertificateBecomesATrustPrompt()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=vnc.test", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        byte[] pem = Encoding.ASCII.GetBytes(certificate.ExportCertificatePem());
        var trust = (ProtocolCertificateTrustException)VncConnector.Translate(new VncCertificateRejectedException(pem, pinMismatch: true), VncSettings.From(Request(), new("en")), new("en"));
        Assert.AreEqual("CN=vnc.test", trust.Subject);
        Assert.AreEqual(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), trust.Thumbprint);
        Assert.AreEqual(new Loc("en")["CertificateChanged"], trust.Message);
    }

    // ---- workspace lifecycle ----

    private static VncWorkspaceProvider Provider(TestPluginContext context, List<FakeVncSession> sessions, Func<int, Exception?>? fail = null) =>
        new(context, new("en"), (_, _, _, _) =>
        {
            int index = sessions.Count;
            if (fail?.Invoke(index) is { } failure) { sessions.Add(null!); return Task.FromException<IVncSession>(failure); }
            var session = new FakeVncSession();
            sessions.Add(session);
            return Task.FromResult<IVncSession>(session);
        });

    [TestMethod]
    public async Task AServerCloseFaultsTheTabAndAManualReconnectReplacesTheSession()
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var sessions = new List<FakeVncSession>();
        await using VncWorkspaceProvider provider = Provider(context, sessions);
        var document = (VncWorkspaceDocument)await provider.OpenAsync(Request());
        sessions[0].Disconnect(null);
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        Assert.AreEqual(new Loc("en")["ByeServerClosed"], document.Status.Message);
        Assert.IsNull(document.AutoReconnectTask, "a deliberate close is not reconnected automatically");
        await document.ReconnectAsync();
        Assert.AreEqual(1, sessions[0].DisposeCount);
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        await document.DisposeAsync();
        await document.DisposeAsync();
        Assert.AreEqual(1, sessions[1].DisposeCount);
        Assert.AreEqual(ProtocolSessionState.Closed, document.Status.State);
    }

    [TestMethod]
    public async Task ALostConnectionReconnectsByItself()
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var sessions = new List<FakeVncSession>();
        await using VncWorkspaceProvider provider = Provider(context, sessions, index => index == 1 ? new ProtocolConnectionException("still down") : null);
        var document = (VncWorkspaceDocument)await provider.OpenAsync(Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(10)];
        sessions[0].Disconnect(new VncConnectionLostException("reset"));
        await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(3, sessions, "one failed attempt, then success");
        Assert.AreEqual(ProtocolSessionState.Connected, document.Status.State);
        await document.DisposeAsync();
    }

    [TestMethod]
    public async Task AutomaticReconnectStopsAtAChangedPassword()
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var sessions = new List<FakeVncSession>();
        await using VncWorkspaceProvider provider = Provider(context, sessions, index => index >= 1 ? new ProtocolAuthenticationException("bad password", null!) : null);
        var document = (VncWorkspaceDocument)await provider.OpenAsync(Request());
        document.AutoReconnectDelays = [TimeSpan.FromMilliseconds(10)];
        sessions[0].Disconnect(new VncConnectionLostException("reset"));
        await document.AutoReconnectTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.HasCount(2, sessions);
        Assert.AreEqual(ProtocolSessionState.Faulted, document.Status.State);
        Assert.AreEqual("bad password", document.Status.Message);
        await document.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposingTheProviderClosesOpenTabs()
    {
        using var context = new TestPluginContext { PluginId = "velashell.vnc" };
        var sessions = new List<FakeVncSession>();
        VncWorkspaceProvider provider = Provider(context, sessions);
        IWorkspaceDocument a = await provider.OpenAsync(Request()), b = await provider.OpenAsync(Request());
        await provider.DisposeAsync();
        Assert.AreEqual(ProtocolSessionState.Closed, a.Status.State);
        Assert.AreEqual(ProtocolSessionState.Closed, b.Status.State);
        Assert.IsTrue(sessions.All(static s => s.DisposeCount == 1));
        _ = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => provider.OpenAsync(Request()));
    }
}
