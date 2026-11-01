namespace VelaShell.Rdp;

/// <summary>
/// Connection settings for <see cref="RdpManagedSession.ConnectAsync(RdpClientOptions, System.Threading.CancellationToken)"/>.
/// Only <see cref="Host"/> is required; the rest carry sensible defaults.
/// </summary>
public sealed record RdpClientOptions
{
    /// <summary>Server host name or IP address.</summary>
    public required string Host { get; init; }

    /// <summary>TCP port; defaults to the standard RDP port 3389.</summary>
    public int Port { get; init; } = 3389;

    public string Username { get; init; } = "";
    public string Password { get; init; } = "";

    /// <summary>Never prints <see cref="Password"/> (the compiler-generated record ToString would).</summary>
    public override string ToString() => $"{nameof(RdpClientOptions)} {{ Host = {Host}, Port = {Port}, Username = {Username}, Domain = {Domain} }}";

    /// <summary>Optional logon domain.</summary>
    public string Domain { get; init; } = "";

    /// <summary>Initial desktop width; the session also tracks the viewport when the server supports display control.</summary>
    public int Width { get; init; } = 1920;

    /// <summary>Initial desktop height.</summary>
    public int Height { get; init; } = 1080;

    /// <summary>
    /// When <see langword="true" /> (the default) the dynamic display-control channel is advertised, so a
    /// caller may follow the viewport with <see cref="RdpManagedSession.RequestResizeAsync(int, int, System.Threading.CancellationToken)"/>
    /// and the desktop resizes to match. Set to <see langword="false" /> for a fixed desktop locked to
    /// <see cref="Width"/>×<see cref="Height"/> (the channel is then never opened and resize requests are ignored).
    /// </summary>
    public bool DynamicResolution { get; init; } = true;

    /// <summary>
    /// Desktop features to suppress for bandwidth (wallpaper, animations, themes …). The default disables
    /// wallpaper, full-window drag and menu animations, matching a responsiveness-first profile.
    /// </summary>
    public RdpPerformanceFlags Performance { get; init; } =
        RdpPerformanceFlags.DisableWallpaper | RdpPerformanceFlags.DisableFullWindowDrag | RdpPerformanceFlags.DisableMenuAnimations;

    /// <summary>When <see langword="true" />, remote audio plays on this computer; the default (<see langword="false" />) disables audio playback.</summary>
    public bool AudioPlayback { get; init; }

    /// <summary>Requested colour depth in bits per pixel: 15, 16, 24 or 32 (32 decodes RDP 6.0 planar bitmaps).</summary>
    public int ColorDepth { get; init; } = 16;

    /// <summary>
    /// Display scale in percent (100 = 96 dpi, 150 for a 150% local display), sent so the remote renders its UI at
    /// the same size as local text instead of tiny on a high-DPI screen. 100..500; the pixel count is unchanged, so it
    /// costs no bandwidth. Follow later changes with <see cref="RdpManagedSession.RequestResizeAsync(int, int, int, System.Threading.CancellationToken)"/>.
    /// </summary>
    public int DesktopScaleFactor { get; init; } = 100;

    /// <summary>
    /// When <see langword="true" /> (the default) the client accepts GDI drawing orders — solid fills, screen-to-screen
    /// copies and blits from a bitmap cache — besides bitmaps: a server that uses them (xrdp, older Windows) then scrolls
    /// and redraws repeated tiles without resending pixels. Newer Windows servers send bitmaps either way.
    /// </summary>
    public bool DrawingOrders { get; init; } = true;

    /// <summary>
    /// When set, the server's leaf certificate must match this SHA-256 hash (pinning); CA, name and
    /// expiry checks are then skipped. When null, the OS trust chain is used and an untrusted
    /// certificate raises <see cref="RdpCertificateRejectedException"/> carrying the leaf PEM.
    /// </summary>
    public byte[]? CertificatePin { get; init; }

    /// <summary>Host name used for certificate validation; defaults to <see cref="Host"/> (set this when tunnelling).</summary>
    public string? CertificateHost { get; init; }

    /// <summary>Enables the bidirectional clipboard channel (cliprdr).</summary>
    public bool Clipboard { get; init; }

    /// <summary>Folders shared to the remote desktop with read/write access (rdpdr); empty by default.</summary>
    public IReadOnlyList<DriveMapping> Drives { get; init; } = [];

    /// <summary>Which security layer to offer; <see cref="RdpSecurity.Auto"/> by default.</summary>
    public RdpSecurity Security { get; init; } = RdpSecurity.Auto;
}
