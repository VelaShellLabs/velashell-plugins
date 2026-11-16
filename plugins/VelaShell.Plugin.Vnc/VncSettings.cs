using System.Globalization;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>How the remote desktop is fitted into the tab.</summary>
internal enum VncScaling
{
    /// <summary>Ask the server to resize to the tab (ExtendedDesktopSize); scale when it cannot.</summary>
    Remote,

    /// <summary>Scale to fit, never resize the remote.</summary>
    Fit,

    /// <summary>One remote pixel per device pixel, with scroll bars.</summary>
    None
}

/// <summary>The parsed connection settings of one VNC workspace.</summary>
internal sealed record VncSettings(VncSecurity Security, bool Shared, bool ViewOnly, bool Clipboard, VncScaling Scaling,
    int? JpegQuality, VncEncodingPreference Encoding, int CompressionLevel, int TimeoutSeconds, bool CommandAsControl,
    string CertificateHost, string CertificateSha256)
{
    internal const string DisplayPage = "display";
    internal const string SecurityKey = "security", SharedKey = "shared", ViewOnlyKey = "viewOnly", ClipboardKey = "clipboard",
        ScalingKey = "scaling", QualityKey = "quality", EncodingKey = "encoding", CompressionKey = "compression",
        TimeoutKey = "timeout", MacCommandKey = "macCommand", JumpKey = "jumpSession", TrustedThumbprintKey = "trustedThumbprint";

    /// <summary>JPEG quality per choice (TigerVNC's own defaults: 8 high, 6 medium, 2 low).</summary>
    internal static int? Quality(string choice) => choice switch
    {
        "lossless" => null,
        "medium" => 6,
        "low" => 2,
        _ => 8
    };

    internal static VncSettings From(WorkspaceConnectRequest request, Loc loc)
    {
        void Invalid(string field) => throw new ProtocolConnectionException(loc.Format("Invalid", field));
        if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Any(char.IsControl))
        {
            Invalid("host");
        }

        if (request.Port is < 1 or > 65535)
        {
            Invalid("port");
        }

        if (request.Username.Contains('\0') || request.Password.Contains('\0'))
        {
            Invalid("credentials");
        }

        bool Flag(string key, bool fallback)
        {
            string raw = request.GetString(key).Trim();
            if (raw.Length == 0) { return fallback; }
            if (!bool.TryParse(raw, out bool value)) { Invalid(key); }
            return value;
        }

        int Number(string key, int fallback, int min, int max)
        {
            string raw = request.GetString(key).Trim();
            if (raw.Length == 0) { return fallback; }
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            {
                Invalid(key);
            }
            return value;
        }

        string Choice(string key, string fallback, params string[] allowed)
        {
            string raw = request.GetString(key).Trim().ToLowerInvariant();
            if (raw.Length == 0) { return fallback; }
            if (!allowed.Contains(raw)) { Invalid(key); }
            return raw;
        }

        VncSecurity security = Choice(SecurityKey, "auto", "auto", "none", "vnc", "vencrypt", "ard") switch
        {
            "none" => VncSecurity.None,
            "vnc" => VncSecurity.VncAuthentication,
            "vencrypt" => VncSecurity.VeNCrypt,
            "ard" => VncSecurity.AppleRemoteDesktop,
            _ => VncSecurity.Auto
        };
        VncScaling scaling = Choice(ScalingKey, "remote", "remote", "fit", "none") switch
        {
            "fit" => VncScaling.Fit,
            "none" => VncScaling.None,
            _ => VncScaling.Remote
        };
        VncEncodingPreference encoding = Choice(EncodingKey, "auto", "auto", "tight", "zrle", "hextile", "raw") switch
        {
            "tight" => VncEncodingPreference.Tight,
            "zrle" => VncEncodingPreference.Zrle,
            "hextile" => VncEncodingPreference.Hextile,
            "raw" => VncEncodingPreference.Raw,
            _ => VncEncodingPreference.Auto
        };
        int? quality = Quality(Choice(QualityKey, "high", "lossless", "high", "medium", "low"));
        bool commandAsControl = Choice(MacCommandKey, "ctrl", "ctrl", "super") == "ctrl";

        string pin = request.GetString(TrustedThumbprintKey).Replace(":", "", StringComparison.Ordinal).Trim();
        if (pin.Length != 0 && (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit)))
        {
            Invalid(TrustedThumbprintKey);
        }

        // Behind an SSH tunnel the certificate names the real server, not the local forwarding endpoint.
        string certificateHost = request.Tunnel?.TargetHost ?? request.Host;
        return new(security, Flag(SharedKey, true), Flag(ViewOnlyKey, false), Flag(ClipboardKey, true), scaling, quality, encoding,
            Number(CompressionKey, 2, 0, 9), Number(TimeoutKey, 15, 1, 120), commandAsControl, certificateHost, pin.ToUpperInvariant());
    }

    /// <summary>The connection form: the everyday switches on the general page, picture tuning on its own page.</summary>
    internal static IReadOnlyList<ProtocolSettingField> Declare(Loc loc) =>
    [
        new()
        {
            Key = SecurityKey,
            Label = loc["Security"],
            Hint = loc["SecurityHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "auto",
            Choices =
            [
                new("auto", loc["SecurityAuto"]),
                new("vnc", loc["SecurityVnc"]),
                new("vencrypt", loc["SecurityVeNCrypt"]),
                new("ard", loc["SecurityArd"]),
                new("none", loc["SecurityNone"])
            ]
        },
        new() { Key = SharedKey, Label = loc["Shared"], Hint = loc["SharedHint"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "true" },
        new() { Key = ViewOnlyKey, Label = loc["ViewOnly"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "false" },
        new() { Key = ClipboardKey, Label = loc["Clipboard"], Hint = loc["ClipboardHint"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "true" },
        new()
        {
            Key = ScalingKey,
            Label = loc["Scaling"],
            Hint = loc["ScalingHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "remote",
            Page = DisplayPage,
            Choices = [new("remote", loc["ScalingRemote"]), new("fit", loc["ScalingFit"]), new("none", loc["ScalingNone"])]
        },
        new()
        {
            Key = QualityKey,
            Label = loc["Quality"],
            Hint = loc["QualityHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "high",
            Page = DisplayPage,
            Choices = [new("lossless", loc["QualityLossless"]), new("high", loc["QualityHigh"]), new("medium", loc["QualityMedium"]), new("low", loc["QualityLow"])]
        },
        new()
        {
            Key = EncodingKey,
            Label = loc["Encoding"],
            Hint = loc["EncodingHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "auto",
            Page = DisplayPage,
            Choices = [new("auto", loc["EncodingAuto"]), new("zrle", "ZRLE"), new("hextile", "Hextile"), new("raw", "Raw")]
        },
        new() { Key = CompressionKey, Label = loc["Compression"], Hint = loc["CompressionHint"], Kind = ProtocolSettingKind.Integer, DefaultValue = "2", Page = DisplayPage },
        new() { Key = TimeoutKey, Label = loc["Timeout"], Kind = ProtocolSettingKind.Integer, DefaultValue = "15", IsAdvanced = true },
        new()
        {
            Key = MacCommandKey,
            Label = loc["MacCommand"],
            Hint = loc["MacCommandHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "ctrl",
            IsAdvanced = true,
            Choices = [new("ctrl", loc["MacCommandCtrl"]), new("super", loc["MacCommandSuper"])]
        },
        new() { Key = JumpKey, Label = loc["Jump"], Kind = ProtocolSettingKind.SshSession },
        new() { Key = TrustedThumbprintKey, Label = loc["Pin"], IsHidden = true }
    ];
}
