using System.Globalization;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

internal sealed record RdpDrive(string Name, string Path);
internal sealed record RdpSettings(string Domain, int Width, int Height, int TimeoutSeconds, bool Clipboard,
    string CertificateHost, string CertificateSha256, IReadOnlyList<RdpDrive> Drives)
{
    internal const string TrustedThumbprintKey = "trustedThumbprint";

    public static RdpSettings From(WorkspaceConnectRequest request, Loc loc)
    {
        void Invalid(string field) => throw new ProtocolConnectionException(loc.Format("Invalid", field));
        if (string.IsNullOrWhiteSpace(request.Host) || request.Host.Any(char.IsControl)) Invalid("host");
        if (request.Port is < 1 or > 65535) Invalid("port");
        if (request.Username.Contains('\0') || request.Password.Contains('\0')) Invalid("credentials");
        string domain = request.GetString("domain");
        if (domain.Any(char.IsControl)) Invalid("domain");
        int Number(string key, int fallback, int min, int max)
        {
            if (!request.Settings.TryGetValue(key, out string? raw) || string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
                Invalid(key);
            return value;
        }
        bool clipboard = false;
        if (request.Settings.TryGetValue("clipboard", out string? clip) && !bool.TryParse(clip, out clipboard)) Invalid("clipboard");
        string pin = request.GetString(TrustedThumbprintKey).Replace(":", "", StringComparison.Ordinal).Trim();
        if (pin.Length != 0 && (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit))) Invalid(TrustedThumbprintKey);
        string certificateHost = request.Tunnel?.TargetHost ?? request.Host;
        if (string.IsNullOrWhiteSpace(certificateHost) || certificateHost.Any(char.IsControl)) Invalid("certificate-host");
        var drives = new List<RdpDrive>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in request.GetString("drives").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equal = line.IndexOf('=');
            if (equal < 1) { Invalid("drives"); continue; }
            string name = line[..equal].Trim();
            string path = line[(equal + 1)..].Trim();
            if (name.Length > 8 || drives.Count >= 32 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || !names.Add(name)
                || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) Invalid("drives");
            drives.Add(new(name, Path.GetFullPath(path)));
        }
        return new(domain, Number("width", 1920, 320, 8192), Number("height", 1080, 200, 8192),
            Number("timeout", 15, 1, 120), clipboard, certificateHost, pin.ToUpperInvariant(), drives);
    }

    public static IReadOnlyList<ProtocolSettingField> Declare(Loc loc) =>
    [
        new() { Key = "domain", Label = loc["Domain"], DefaultValue = "" },
        new() { Key = "width", Label = loc["Width"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1920", IsAdvanced = true },
        new() { Key = "height", Label = loc["Height"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1080", IsAdvanced = true },
        new() { Key = "timeout", Label = loc["Timeout"], Kind = ProtocolSettingKind.Integer, DefaultValue = "15", IsAdvanced = true },
        new() { Key = "clipboard", Label = loc["Clipboard"], Hint = loc["ClipboardHint"], Kind = ProtocolSettingKind.Boolean, DefaultValue = "false" },
        new() { Key = "drives", Label = loc["Drives"], Hint = loc["DrivesHint"], DefaultValue = "", IsAdvanced = true },
        new() { Key = "jumpSession", Label = loc["Jump"], Kind = ProtocolSettingKind.SshSession },
        new() { Key = TrustedThumbprintKey, Label = loc["Pin"], IsHidden = true }
    ];
}
