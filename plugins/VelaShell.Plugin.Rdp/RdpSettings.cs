using System.Globalization;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Pdu;

namespace VelaShell.Plugin.Rdp;

internal sealed record RdpDrive(string Name, string Path);
internal sealed record RdpSettings(string Domain, int Width, int Height, int TimeoutSeconds, bool Clipboard,
    string CertificateHost, string CertificateSha256, IReadOnlyList<RdpDrive> Drives, RdpSecurity Security = RdpSecurity.Auto,
    bool Audio = false,
    RdpPerformanceFlags Performance = RdpPerformanceFlags.DisableWallpaper | RdpPerformanceFlags.DisableFullWindowDrag
        | RdpPerformanceFlags.DisableMenuAnimations | RdpPerformanceFlags.DisableThemes,
    int ColorDepth = RdpSettings.DefaultColorDepth)
{
    internal AutoReconnectCookie? AutoReconnectCookie { get; set; }
    internal IReadOnlyList<PortMapping> Ports { get; init; } = [];
    internal IReadOnlyList<PrinterMapping> Printers { get; init; } = [];
    internal RdpCameraDevice? Camera { get; init; }
    internal IReadOnlyList<string> WpdDevices { get; init; } = [];
    internal RdpGatewayOptions? Gateway { get; init; }
    internal RemoteAppOptions? RemoteApp { get; init; }
    internal bool SmartCardRedirection { get; init; }
    internal const string GatewayPage = "gateway", RemoteAppPage = "remoteApp";
    internal const string GatewayHostKey = "gatewayHost", GatewayPortKey = "gatewayPort",
        GatewayReuseCredentialsKey = "gatewayUseRdpCredentials", GatewayUsernameKey = "gatewayUsername",
        GatewayPasswordKey = "gatewayPassword", GatewayDomainKey = "gatewayDomain", GatewayPinKey = "gatewayCertificateSha256",
        SmartCardKey = "smartCard", RemoteAppProgramKey = "remoteAppProgram", RemoteAppArgumentsKey = "remoteAppArguments",
        RemoteAppWorkingDirectoryKey = "remoteAppWorkingDirectory";
    internal bool EnableGraphicsPipeline { get; init; } = true;
    internal const string GraphicsModeKey = "graphicsMode";
    internal RdpTransportMode Transport { get; init; } = RdpTransportMode.Auto;
    internal bool AdaptiveNetwork { get; init; } = true;
    internal const string TransportKey = "transport", AdaptiveNetworkKey = "adaptiveNetwork";
    internal const string PortsKey = "ports";
    internal const string TrustedThumbprintKey = "trustedThumbprint";

    /// <summary>Setting key of the security-layer choice (<c>auto</c> / <c>nla</c> / <c>tls</c>).</summary>
    internal const string SecurityKey = "security";

    /// <summary>Setting key of the colour depth (<c>16</c> / <c>24</c> / <c>32</c>), on the experience page.</summary>
    internal const string ColorDepthKey = "colorDepth";

    /// <summary>Colour depth for connections that never chose one: 32-bit "highest quality" like mstsc — lossless RDP 6.0
    /// planar with the alpha plane skipped, so no 16-bit banding (verified pixel-identical to 24-bit against xrdp).</summary>
    internal const int DefaultColorDepth = 32;

    /// <summary>Page key of the drive-mapping page (local-disk checkboxes + custom folders).</summary>
    internal const string DrivesPage = "drives";

    /// <summary>Page key of the experience page (audio and the desktop-feature toggles).</summary>
    internal const string ExperiencePage = "experience";

    /// <summary>Setting keys of the experience toggles, each a Boolean defaulting to off (the responsiveness-first profile).</summary>
    internal const string AudioKey = "audio", WallpaperKey = "wallpaper", WindowDragKey = "windowDrag",
        AnimationsKey = "animations", ThemesKey = "themes", FontSmoothingKey = "fontSmoothing";

    /// <summary>Setting-key prefix of a local-disk checkbox: <c>drive:&lt;LocalDisk.Id&gt;</c>.</summary>
    internal const string DiskKeyPrefix = "drive:";

    public static RdpSettings From(WorkspaceConnectRequest request, Loc loc) => From(request, loc, LocalDisks.Enumerate());

    /// <summary>
    /// COM/LPT ports (overlapped Win32 I/O), printers (Winspool), the camera (Media Foundation) and portable devices
    /// (WPD) have Windows backends only. Elsewhere their fields are not declared, and settings saved on Windows (for
    /// example synced to a Mac) are ignored rather than failing the connection. Smart cards, drives, clipboard and
    /// audio work on every platform and are unaffected.
    /// </summary>
    internal static bool WindowsDevicesAvailable => OperatingSystem.IsWindows();

    internal static RdpSettings From(WorkspaceConnectRequest request, Loc loc, IReadOnlyList<LocalDisk> disks,
        Func<IReadOnlyList<PrinterMapping>>? enumeratePrinters = null,
        Func<IReadOnlyList<RdpCameraDevice>>? enumerateCameras = null,
        Func<IReadOnlyList<RdpWpdDevice>>? enumerateWpdDevices = null,
        bool? windowsDevices = null)
    {
        bool devices = windowsDevices ?? WindowsDevicesAvailable;
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

        string domain = request.GetString("domain");
        if (domain.Any(char.IsControl))
        {
            Invalid("domain");
        }

        int Number(string key, int fallback, int min, int max)
        {
            if (!request.Settings.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
            {
                Invalid(key);
            }

            return value;
        }
        bool Flag(string key, bool fallback = false)
        {
            if (!request.Settings.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (!bool.TryParse(raw, out bool value))
            {
                Invalid(key);
            }

            return value;
        }
        bool clipboard = Flag("clipboard", true);
        RdpGatewayOptions? gateway = null;
        string gatewayHost = request.GetString(GatewayHostKey).Trim();
        if (gatewayHost.Length != 0)
        {
            if (request.Tunnel is not null) { throw new ProtocolConnectionException(loc["GatewaySshConflict"]); }
            string gatewayPin = request.GetString(GatewayPinKey).Replace(":", "", StringComparison.Ordinal).Trim();
            if (gatewayPin.Length != 0 && (gatewayPin.Length != 64 || !gatewayPin.All(char.IsAsciiHexDigit)))
            {
                Invalid(GatewayPinKey);
            }
            gateway = new RdpGatewayOptions
            {
                Host = gatewayHost,
                Port = Number(GatewayPortKey, 443, 1, 65535),
                UseRdpCredentials = Flag(GatewayReuseCredentialsKey, true),
                Username = request.GetString(GatewayUsernameKey),
                Password = request.GetString(GatewayPasswordKey),
                Domain = request.GetString(GatewayDomainKey),
                CertificatePin = gatewayPin.Length == 0 ? null : Convert.FromHexString(gatewayPin)
            };
            if (Uri.CheckHostName(gateway.Host) == UriHostNameType.Unknown || gateway.Host.Any(char.IsControl)) { Invalid(GatewayHostKey); }
            if (!gateway.UseRdpCredentials && (string.IsNullOrWhiteSpace(gateway.Username) || gateway.Username.Contains('\0')
                || gateway.Password.Contains('\0') || gateway.Domain.Any(char.IsControl))) { Invalid(GatewayUsernameKey); }
        }

        RemoteAppOptions? remoteApp = null;
        string program = request.GetString(RemoteAppProgramKey).Trim();
        string arguments = request.GetString(RemoteAppArgumentsKey);
        string workingDirectory = request.GetString(RemoteAppWorkingDirectoryKey);
        if (program.Length != 0 || arguments.Length != 0 || workingDirectory.Length != 0)
        {
            if (program.Length != 0 && !program.StartsWith("||", StringComparison.Ordinal))
            {
                if (program.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                {
                    Invalid(RemoteAppProgramKey);
                }

                program = "||" + program;
            }

            if (!program.StartsWith("||", StringComparison.Ordinal) || program.Length is <= 2 or > 260 || program.Any(char.IsControl))
            {
                Invalid(RemoteAppProgramKey);
            }
            if (arguments.Length > 8000 || arguments.Contains('\0')) { Invalid(RemoteAppArgumentsKey); }
            if (workingDirectory.Length > 260 || workingDirectory.Contains('\0')) { Invalid(RemoteAppWorkingDirectoryKey); }
            remoteApp = new RemoteAppOptions { Program = program, Arguments = arguments, WorkingDirectory = workingDirectory };
        }

        // Experience toggles: each is phrased as "turn the feature on", so an unticked box (the default)
        // keeps the bandwidth-saving profile. Font smoothing is an enable bit; the rest are disable bits.
        bool audio = Flag(AudioKey);
        RdpPerformanceFlags performance = RdpPerformanceFlags.None;
        if (!Flag(WallpaperKey)) { performance |= RdpPerformanceFlags.DisableWallpaper; }
        if (!Flag(WindowDragKey)) { performance |= RdpPerformanceFlags.DisableFullWindowDrag; }
        if (!Flag(AnimationsKey)) { performance |= RdpPerformanceFlags.DisableMenuAnimations; }
        if (!Flag(ThemesKey)) { performance |= RdpPerformanceFlags.DisableThemes; }
        if (Flag(FontSmoothingKey)) { performance |= RdpPerformanceFlags.EnableFontSmoothing; }

        string pin = request.GetString(TrustedThumbprintKey).Replace(":", "", StringComparison.Ordinal).Trim();
        if (pin.Length != 0 && (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit)))
        {
            Invalid(TrustedThumbprintKey);
        }

        // Empty = a connection saved before the setting existed: Auto, which behaves as before unless the
        // server refuses NLA up front.
        RdpSecurity security = request.GetString(SecurityKey).Trim().ToLowerInvariant() switch
        {
            "" or "auto" => RdpSecurity.Auto,
            "nla" => RdpSecurity.Nla,
            "tls" => RdpSecurity.Tls,
            _ => throw new ProtocolConnectionException(loc.Format("Invalid", SecurityKey))
        };

        bool graphics = request.GetString(GraphicsModeKey).Trim().ToLowerInvariant() switch
        {
            "" or "auto" => true,
            "classic" => false,
            _ => throw new ProtocolConnectionException(loc.Format("Invalid", GraphicsModeKey))
        };
        int colorDepth = request.GetString(ColorDepthKey).Trim() switch
        {
            "" => DefaultColorDepth,
            "16" => 16,
            "24" => 24,
            "32" => 32,
            _ => throw new ProtocolConnectionException(loc.Format("Invalid", ColorDepthKey))
        };

        string certificateHost = request.Tunnel?.TargetHost ?? request.Host;
        if (string.IsNullOrWhiteSpace(certificateHost) || certificateHost.Any(char.IsControl))
        {
            Invalid("certificate-host");
        }

        var drives = new List<RdpDrive>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Ticked local disks first. One that is absent right now (USB unplugged, network drive offline)
        // is skipped instead of failing the connection — the same as mstsc.
        foreach (LocalDisk disk in disks)
        {
            if (request.Settings.TryGetValue(DiskKeyPrefix + disk.Id, out string? ticked)
                && bool.TryParse(ticked, out bool on) && on
                && drives.Count < LocalDisks.MaxDisks && Directory.Exists(disk.Path) && names.Add(disk.ShareName))
            {
                drives.Add(new(disk.ShareName, disk.Path));
            }
        }

        // Then custom folders. The host renders text settings as a single-line box, so entries are
        // separated by ';' — newlines are still accepted for values saved by earlier versions.
        // Errors name the entry and show the format with an example: "RDP setting invalid: drives"
        // told the user neither which entry was wrong nor how to write it.
        string example = CustomFolderExamples().Single;
        foreach (string entry in request.GetString("drives").Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (drives.Count >= LocalDisks.MaxDisks)
            {
                throw new ProtocolConnectionException(loc.Format("DrivesTooMany", LocalDisks.MaxDisks));
            }

            int equal = entry.IndexOf('=');
            string name = equal < 1 ? "" : entry[..equal].Trim();
            string path = equal < 1 ? "" : entry[(equal + 1)..].Trim();
            if (name.Length is < 1 or > 8 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
                || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            {
                throw new ProtocolConnectionException(loc.Format("DrivesInvalidEntry", entry, example));
            }

            if (!names.Add(name))
            {
                throw new ProtocolConnectionException(loc.Format("DrivesDuplicateName", name));
            }

            drives.Add(new(name, Path.GetFullPath(path)));
        }
        return new(domain, Number("width", 1920, 320, 8192), Number("height", 1080, 200, 8192),
            Number("timeout", 15, 1, 120), clipboard, certificateHost, pin.ToUpperInvariant(), drives, security, audio, performance, colorDepth)
        {
            Gateway = gateway,
            RemoteApp = remoteApp,
            SmartCardRedirection = Flag(SmartCardKey),
            EnableGraphicsPipeline = graphics,
            Transport = request.GetString(TransportKey).Trim().ToLowerInvariant() switch
            {
                "" or "auto" => RdpTransportMode.Auto,
                "tcp" => RdpTransportMode.Tcp,
                _ => throw new ProtocolConnectionException(loc.Format("Invalid", TransportKey))
            },
            AdaptiveNetwork = Flag(AdaptiveNetworkKey, true),
            Ports = devices ? ParsePorts(request.GetString(PortsKey), loc) : [],
            Printers = devices ? LocalPrinters.Selected(request, loc, enumeratePrinters ?? WindowsPrinterCatalog.GetPrinters) : [],
            Camera = devices ? LocalCameras.Selected(request, loc, enumerateCameras ?? RdpCameraDevices.Enumerate) : null,
            WpdDevices = devices ? LocalWpdDevices.Selected(request, loc, enumerateWpdDevices ?? RdpWpdDevices.Enumerate) : []
        };
    }

    private static PortMapping[] ParsePorts(string text, Loc loc)
    {
        try
        {
            PortMapping[] ports = string.IsNullOrWhiteSpace(text) ? []
                : [.. text.Split(';', StringSplitOptions.TrimEntries).Select(p => new PortMapping(p))];
            if (ports.Length > 32 || ports.DistinctBy(p => p.Name).Count() != ports.Length)
            {
                throw new ArgumentException("Port mappings must contain at most 32 unique entries.", nameof(text));
            }
            return ports;
        }
        catch (ArgumentException) { throw new ProtocolConnectionException(loc.Format("Invalid", loc["Ports"])); }
    }

    public static IReadOnlyList<ProtocolSettingField> Declare(Loc loc) => Declare(loc, LocalDisks.Enumerate());

    /// <summary>
    /// Declares the connection form. The drive-mapping page lists one checkbox per local disk (all
    /// unchecked: nothing is shared until the user opts in), followed by the custom-folder field.
    /// </summary>
    internal static IReadOnlyList<ProtocolSettingField> Declare(Loc loc, IReadOnlyList<LocalDisk> disks,
        IReadOnlyList<PrinterMapping>? printers = null, IReadOnlyList<RdpCameraDevice>? cameras = null,
        IReadOnlyList<RdpWpdDevice>? wpdDevices = null, bool? windowsDevices = null)
    {
        bool devices = windowsDevices ?? WindowsDevicesAvailable;
        List<ProtocolSettingField> fields =
        [
            new() { Key = "domain", Label = loc["Domain"], DefaultValue = "" },
            new() { Key = GatewayHostKey, Label = loc["GatewayHost"], Hint = loc["GatewayHint"], DefaultValue = "", Page = GatewayPage },
            new() { Key = GatewayPortKey, Label = loc["GatewayPort"], Kind = ProtocolSettingKind.Integer, DefaultValue = "443", Page = GatewayPage },
            new() { Key = GatewayReuseCredentialsKey, Label = loc["GatewayReuseCredentials"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "true", Page = GatewayPage },
            new() { Key = GatewayUsernameKey, Label = loc["User"], DefaultValue = "", Page = GatewayPage },
            new() { Key = GatewayPasswordKey, Label = loc["Password"], Kind = ProtocolSettingKind.Password, IsSecret = true, DefaultValue = "", Page = GatewayPage },
            new() { Key = GatewayDomainKey, Label = loc["Domain"], DefaultValue = "", Page = GatewayPage },
            new() { Key = GatewayPinKey, Label = loc["GatewayPin"], Hint = loc["GatewayPinHint"], DefaultValue = "", Page = GatewayPage },
            new() { Key = RemoteAppProgramKey, Label = loc["RemoteAppProgram"], Hint = loc["RemoteAppHint"], DefaultValue = "", Page = RemoteAppPage },
            new() { Key = RemoteAppArgumentsKey, Label = loc["RemoteAppArguments"], DefaultValue = "", Page = RemoteAppPage },
            new() { Key = RemoteAppWorkingDirectoryKey, Label = loc["RemoteAppWorkingDirectory"], DefaultValue = "", Page = RemoteAppPage },
            new() { Key = SmartCardKey, Label = loc["SmartCard"], Hint = loc["SmartCardHint"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "false", Page = DrivesPage },
            new() { Key = "width", Label = loc["Width"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1920", IsAdvanced = true },
            new() { Key = "height", Label = loc["Height"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1080", IsAdvanced = true },
            new() { Key = "timeout", Label = loc["Timeout"], Kind = ProtocolSettingKind.Integer, DefaultValue = "15", IsAdvanced = true },
            new()
            {
                Key = SecurityKey,
                Label = loc["Security"],
                Hint = loc["SecurityHint"],
                Kind = ProtocolSettingKind.Choice,
                DefaultValue = "auto",
                IsAdvanced = true,
                Choices =
                [
                    new("auto", loc["SecurityAuto"]),
                    new("nla", loc["SecurityNla"]),
                    new("tls", loc["SecurityTls"])
                ]
            },
            new() { Key = "clipboard", Label = loc["Clipboard"], Hint = loc["ClipboardHint"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "true" }
        ];
        if (devices)
        {
            fields.Insert(fields.FindIndex(field => field.Key == SmartCardKey) + 1,
                new() { Key = PortsKey, Label = loc["Ports"], Hint = loc["PortsHint"], DefaultValue = "", Page = DrivesPage });
        }
        foreach (LocalDisk disk in disks)
        {
            fields.Add(new()
            {
                Key = DiskKeyPrefix + disk.Id,
                Label = DiskLabel(loc, disk),
                Kind = ProtocolSettingKind.Boolean,
                DefaultValue = "false",
                Page = DrivesPage,
                SelectionGroup = new("drives", loc["DriveGroup"])
            });
        }

        foreach (PrinterMapping printer in devices ? printers ?? [] : [])
        {
            fields.Add(new()
            {
                Key = LocalPrinters.Key(printer),
                Label = printer.PrinterName,
                Hint = loc["PrintersHint"],
                Kind = ProtocolSettingKind.Boolean,
                DefaultValue = "false",
                Page = DrivesPage,
                SelectionGroup = new("printers", loc["Printers"])
            });
        }

        foreach (RdpWpdDevice device in devices ? wpdDevices ?? [] : [])
        {
            fields.Add(new()
            {
                Key = LocalWpdDevices.Key(device),
                Label = device.Name,
                Hint = loc["WpdHint"],
                Kind = ProtocolSettingKind.Boolean,
                DefaultValue = "false",
                Page = DrivesPage,
                SelectionGroup = new("wpd", loc["WpdDevices"])
            });
        }

        if (devices)
        {
            fields.Add(new()
            {
                Key = LocalCameras.SettingKey,
                Label = loc["Camera"],
                Hint = loc["CameraHint"],
                Kind = ProtocolSettingKind.Choice,
                DefaultValue = "",
                Page = DrivesPage,
                Choices = [new("", loc["CameraNone"]), .. (cameras ?? []).Select(camera => new ProtocolSettingChoice(LocalCameras.Key(camera), camera.Name))]
            });
        }

        // The hint stays visible under the box (the placeholder vanishes as soon as anything is typed),
        // so it carries the format plus concrete examples for this OS.
        (string single, string several) = CustomFolderExamples();
        fields.Add(new()
        {
            Key = "drives",
            Label = loc["Drives"],
            Hint = loc.Format("DrivesHint", single, several),
            Placeholder = several,
            DefaultValue = "",
            Page = DrivesPage
        });
        // Colour depth heads the experience page (mstsc puts it on its display tab): 16-bit saves bandwidth, 24 and 32
        // bit are full colour (32 uses the RDP 6.0 planar codec).
        fields.Add(new()
        {
            Key = ColorDepthKey,
            Label = loc["ColorDepth"],
            Hint = loc["ColorDepthHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = DefaultColorDepth.ToString(CultureInfo.InvariantCulture),
            Page = ExperiencePage,
            Choices = [new("16", loc["ColorDepth16"]), new("24", loc["ColorDepth24"]), new("32", loc["ColorDepth32"])]
        });
        fields.Add(new()
        {
            Key = GraphicsModeKey,
            Label = loc["GraphicsMode"],
            Hint = loc["GraphicsModeHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "auto",
            Page = ExperiencePage,
            Choices = [new("auto", loc["GraphicsAuto"]), new("classic", loc["GraphicsClassic"])]
        });
        fields.Add(new()
        {
            Key = TransportKey,
            Label = loc["Transport"],
            Hint = loc["TransportHint"],
            Kind = ProtocolSettingKind.Choice,
            DefaultValue = "auto",
            Page = ExperiencePage,
            Choices = [new("auto", loc["TransportAuto"]), new("tcp", "TCP")]
        });
        fields.Add(new()
        {
            Key = AdaptiveNetworkKey,
            Label = loc["AdaptiveNetwork"],
            Hint = loc["AdaptiveNetworkHint"],
            Kind = ProtocolSettingKind.Boolean,
            DefaultValue = "true",
            Page = ExperiencePage
        });
        // Experience toggles live on their own page, like mstsc's "Experience" tab. All unticked by
        // default: the responsiveness-first profile (no wallpaper, animations, themes or audio).
        foreach ((string key, string label) in new[]
        {
            (AudioKey, loc["ExpAudio"]), (WallpaperKey, loc["ExpWallpaper"]), (WindowDragKey, loc["ExpWindowDrag"]),
            (AnimationsKey, loc["ExpAnimations"]), (ThemesKey, loc["ExpThemes"]), (FontSmoothingKey, loc["ExpFontSmoothing"])
        })
        {
            fields.Add(new() { Key = key, Label = label, Kind = ProtocolSettingKind.Boolean, DefaultValue = "false", Page = ExperiencePage });
        }

        fields.Add(new() { Key = "jumpSession", Label = loc["Jump"], Kind = ProtocolSettingKind.SshSession });
        fields.Add(new() { Key = TrustedThumbprintKey, Label = loc["Pin"], IsHidden = true });
        return fields;
    }

    /// <summary>OS-appropriate custom-folder examples for the hint, the placeholder and error messages.</summary>
    internal static (string Single, string Several) CustomFolderExamples() =>
        OperatingSystem.IsWindows() ? (@"work=C:\Work", @"work=C:\Work;docs=D:\Docs")
        : OperatingSystem.IsMacOS() ? ("work=/Users/me/Work", "work=/Users/me/Work;docs=/Users/me/Documents")
        : ("work=/home/me/work", "work=/home/me/work;docs=/srv/docs");

    /// <summary>The checkbox label, Explorer-style: "Local Disk (C:)", "Removable Disk (E:)", "Root (/)".</summary>
    internal static string DiskLabel(Loc loc, LocalDisk disk) => disk.Kind switch
    {
        LocalDiskKind.Fixed => $"{loc["DiskLocal"]} ({disk.Id}:)",
        LocalDiskKind.Removable => $"{loc["DiskRemovable"]} ({disk.Id}:)",
        LocalDiskKind.Network => $"{loc["DiskNetwork"]} ({disk.Id}:)",
        LocalDiskKind.Optical => $"{loc["DiskOptical"]} ({disk.Id}:)",
        LocalDiskKind.Root => $"{loc["DiskRoot"]} (/)",
        LocalDiskKind.Home => $"{loc["DiskHome"]} (~)",
        _ => $"{Path.GetFileName(disk.Path)} ({disk.Path})"
    };
}
