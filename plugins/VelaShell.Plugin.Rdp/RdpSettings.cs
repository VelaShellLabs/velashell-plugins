using System.Globalization;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

internal enum RdpScaleMode { Fit, Actual }

/// <summary>Validated non-secret settings. Field declarations and parsers share this file.</summary>
internal sealed record RdpSettings
{
    public const string TrustedThumbprintKey = "trustedThumbprint";
    public string Domain { get; init; } = "";
    public int DesktopWidth { get; init; } = 1280;
    public int DesktopHeight { get; init; } = 720;
    public RdpScaleMode ScaleMode { get; init; }
    public int ConnectTimeoutMs { get; init; } = 15000;
    public string TrustedThumbprint { get; init; } = "";
    public string JumpSessionId { get; init; } = "";

    public static RdpSettings From(WorkspaceConnectRequest request, Loc loc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateHost(request.Host, loc);
        ValidatePort(request.Port, loc);
        if (request.Tunnel is { } tunnel)
        {
            ValidateHost(tunnel.TargetHost, loc);
            ValidatePort(tunnel.TargetPort, loc);
        }
        string scale = Get(request, "scaleMode", "fit");
        string domain = Get(request, "domain").Trim();
        if (domain.Length > 255 || domain.Any(char.IsControl) || domain.IndexOfAny(['\\', '/', '@']) >= 0)
        {
            throw Invalid(loc, "domain");
        }
        string pin = Get(request, TrustedThumbprintKey).Replace(":", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (pin.Length != 0 && (pin.Length != 64 || !pin.All(Uri.IsHexDigit)))
        {
            throw Invalid(loc, "SHA-256 certificate pin");
        }
        string jump = Get(request, "jumpSession");
        if (jump.Length > 512 || jump.Any(char.IsControl))
        {
            throw Invalid(loc, "jumpSession");
        }
        return new()
        {
            Domain = domain,
            DesktopWidth = ParseInt(request, "desktopWidth", 1280, 320, 7680, loc),
            DesktopHeight = ParseInt(request, "desktopHeight", 720, 200, 4320, loc),
            ScaleMode = scale switch { "fit" => RdpScaleMode.Fit, "actual" => RdpScaleMode.Actual, _ => throw Invalid(loc, "scaleMode") },
            ConnectTimeoutMs = ParseInt(request, "connectTimeout", 15000, 1000, 120000, loc),
            TrustedThumbprint = pin.ToUpperInvariant(),
            JumpSessionId = jump
        };
    }

    /// <summary>Normalizes account syntax without retaining the password or request.</summary>
    public (string Username, string Domain) ParseAccount(string username, Loc loc)
    {
        username = username.Trim();
        if (username.Length is 0 or > 512 || username.Any(char.IsControl) || username.Contains('/'))
        {
            throw new ProtocolConnectionException(loc["Rdp_InvalidAccount"]);
        }
        int slash = username.IndexOf('\\');
        if (slash >= 0)
        {
            string prefix = username[..slash];
            string user = username[(slash + 1)..];
            if (prefix.Length == 0 || prefix.Contains('@') || user.Length == 0 || user.Contains('\\') || user.Contains('@')
                || (Domain.Length != 0 && !Domain.Equals(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProtocolConnectionException(loc["Rdp_InvalidAccount"]);
            }
            if (prefix.Length > 255)
            {
                throw new ProtocolConnectionException(loc["Rdp_InvalidAccount"]);
            }
            return (user, prefix);
        }
        int at = username.IndexOf('@');
        if (at >= 0 && (Domain.Length != 0 || at == 0 || at == username.Length - 1 || username.LastIndexOf('@') != at))
        {
            throw new ProtocolConnectionException(loc["Rdp_InvalidAccount"]);
        }
        return (username, Domain);
    }

    public static void ValidatePassword(string password, Loc loc)
    {
        // Native UTF-8 strings cannot represent embedded NUL. Otherwise preserve the exact secret.
        if (password.Length > 4096 || password.Contains('\0'))
        {
            throw Invalid(loc, "password");
        }
    }

    // Explicit arrays avoid compiler-generated IReadOnlyList wrappers belonging to this ALC.
    // The host may retain this pure SDK descriptor after deactivation while a form stays open.
    public static ProtocolSettingField[] Declare(Loc loc) =>
    [
        new() { Key = "domain", Label = loc["Rdp_Domain"], Hint = loc["Rdp_DomainHint"], DefaultValue = "" },
        new() { Key = "jumpSession", Label = loc["Rdp_JumpSession"], Kind = ProtocolSettingKind.SshSession, Hint = loc["Rdp_JumpSessionHint"] },
        new() { Key = "desktopWidth", Label = loc["Rdp_Width"], Kind = ProtocolSettingKind.Integer, DefaultValue = "1280", IsAdvanced = true },
        new() { Key = "desktopHeight", Label = loc["Rdp_Height"], Kind = ProtocolSettingKind.Integer, DefaultValue = "720", IsAdvanced = true },
        new() { Key = "scaleMode", Label = loc["Rdp_Scale"], Kind = ProtocolSettingKind.Choice, DefaultValue = "fit", IsAdvanced = true,
            Choices = (ProtocolSettingChoice[])[new("fit", loc["Rdp_ScaleFit"]), new("actual", loc["Rdp_ScaleActual"])] },
        new() { Key = "connectTimeout", Label = loc["Rdp_ConnectTimeout"], Kind = ProtocolSettingKind.Integer, DefaultValue = "15000", IsAdvanced = true },
        new() { Key = TrustedThumbprintKey, Label = "TLS SHA-256", IsHidden = true }
    ];

    private static string Get(WorkspaceConnectRequest request, string key, string fallback = "") =>
        request.Settings.TryGetValue(key, out string? value) ? value : fallback;

    private static int ParseInt(WorkspaceConnectRequest request, string key, int fallback, int minimum, int maximum, Loc loc)
    {
        if (!request.Settings.TryGetValue(key, out string? raw))
        {
            return fallback;
        }
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
        {
            throw Invalid(loc, key);
        }
        return value;
    }

    private static void ValidateHost(string host, Loc loc)
    {
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw Invalid(loc, "host");
        }
    }

    private static void ValidatePort(int port, Loc loc)
    {
        if (port is < 1 or > 65535)
        {
            throw Invalid(loc, "port");
        }
    }

    private static ProtocolConnectionException Invalid(Loc loc, string field) => new(loc.Format("Rdp_InvalidSetting", field));
}
