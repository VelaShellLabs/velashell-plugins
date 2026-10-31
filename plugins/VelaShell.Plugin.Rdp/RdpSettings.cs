using System.Globalization;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

internal sealed record RdpDrive(string Name, string Path);
internal sealed record RdpSettings(string Domain, int Width, int Height, int TimeoutSeconds, bool Clipboard,
    string CertificateHost, string CertificateSha256, IReadOnlyList<RdpDrive> Drives)
{
    internal const string TrustedThumbprintKey = "trustedThumbprint";

    /// <summary>Page key of the drive-mapping page (local-disk checkboxes + custom folders).</summary>
    internal const string DrivesPage = "drives";

    /// <summary>Setting-key prefix of a local-disk checkbox: <c>drive:&lt;LocalDisk.Id&gt;</c>.</summary>
    internal const string DiskKeyPrefix = "drive:";

    public static RdpSettings From(WorkspaceConnectRequest request, Loc loc) => From(request, loc, LocalDisks.Enumerate());

    internal static RdpSettings From(WorkspaceConnectRequest request, Loc loc, IReadOnlyList<LocalDisk> disks)
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
        bool clipboard = false;
        if (request.Settings.TryGetValue("clipboard", out string? clip) && !bool.TryParse(clip, out clipboard))
        {
            Invalid("clipboard");
        }

        string pin = request.GetString(TrustedThumbprintKey).Replace(":", "", StringComparison.Ordinal).Trim();
        if (pin.Length != 0 && (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit)))
        {
            Invalid(TrustedThumbprintKey);
        }

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
            Number("timeout", 15, 1, 120), clipboard, certificateHost, pin.ToUpperInvariant(), drives);
    }

    public static IReadOnlyList<ProtocolSettingField> Declare(Loc loc) => Declare(loc, LocalDisks.Enumerate());

    /// <summary>
    /// Declares the connection form. The drive-mapping page lists one checkbox per local disk (all
    /// unchecked: nothing is shared until the user opts in), followed by the custom-folder field.
    /// </summary>
    internal static IReadOnlyList<ProtocolSettingField> Declare(Loc loc, IReadOnlyList<LocalDisk> disks)
    {
        List<ProtocolSettingField> fields =
        [
            new() { Key = "domain", Label = loc["Domain"], DefaultValue = "" },
            new() { Key = "width", Label = loc["Width"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1920", IsAdvanced = true },
            new() { Key = "height", Label = loc["Height"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1080", IsAdvanced = true },
            new() { Key = "timeout", Label = loc["Timeout"], Kind = ProtocolSettingKind.Integer, DefaultValue = "15", IsAdvanced = true },
            new() { Key = "clipboard", Label = loc["Clipboard"], Hint = loc["ClipboardHint"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "false" }
        ];
        foreach (LocalDisk disk in disks)
        {
            fields.Add(new()
            {
                Key = DiskKeyPrefix + disk.Id,
                Label = DiskLabel(loc, disk),
                Kind = ProtocolSettingKind.Boolean,
                DefaultValue = "false",
                Page = DrivesPage
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
