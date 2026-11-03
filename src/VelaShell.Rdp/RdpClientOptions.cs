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

    /// <summary>Try reliable and lossy UDP side channels when supported; TCP remains the primary transport.</summary>
    public RdpTransportMode Transport { get; init; } = RdpTransportMode.Auto;

    /// <summary>Enable server network detection and adaptive experience hints. Disable to preserve manual performance flags.</summary>
    public bool AdaptiveNetwork { get; init; } = true;

    /// <summary>Set for SSH/proxy tunnels that forward TCP only. Prevents direct UDP connections.</summary>
    public bool TcpTunnel { get; init; }

    /// <summary>Optional RD Gateway. Target traffic stays inside this gateway; direct UDP is disabled.</summary>
    public RdpGatewayOptions? Gateway { get; init; }

    /// <summary>Launch a published RDS application using RAIL instead of a desktop.</summary>
    public RemoteAppOptions? RemoteApp { get; init; }

    /// <summary>Explicitly expose local PC/SC smart-card readers to the remote session. Disabled by default.</summary>
    public bool SmartCardRedirection { get; init; }

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

    /// <summary>
    /// Plays remote PCM audio on the default local output device on Windows, macOS or Linux.
    /// Disabled by default. Failures are reported by <see cref="IRdpAudioSource.AudioPlaybackFailed"/> without ending the desktop session.
    /// </summary>
    public bool AudioPlayback { get; init; }

    /// <summary>
    /// Requested classic colour depth: 15, 16, 24 or 32 (32 decodes RDP 6.0 planar bitmaps).
    /// Offering the managed graphics pipeline requests a 32-bit desktop regardless of this setting.
    /// </summary>
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
    /// Offer the managed RDP graphics pipeline, including H.264 AVC420/AVC444/AVC444v2.
    /// Defaults to true; false selects classic graphics. A server may still choose classic graphics.
    /// Graphics failures after negotiation end the session.
    /// </summary>
    public bool EnableGraphicsPipeline { get; init; } = true;

    /// <summary>Optional graphics initialization/failure diagnostics, called on background threads.</summary>
    public Action<string>? GraphicsLog { get; init; }

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

    /// <summary>Explicit Windows COM/LPT selections; empty disables port redirection. At most 32 unique ports.</summary>
    public IReadOnlyList<PortMapping> Ports
    {
        get;
        init => field = Array.AsReadOnly(PortMapping.Validate(value));
    } = [];

    /// <summary>Explicit installed Windows printer selections; empty disables printing. Requires matching server drivers; no Easy Print/XPS.</summary>
    public IReadOnlyList<PrinterMapping> Printers
    {
        get;
        init => field = Array.AsReadOnly(PrinterMapping.Validate(value));
    } = [];

    /// <summary>One explicitly selected Windows camera; null disables capture. Only native MJPEG formats are offered.</summary>
    public Channels.RdpCameraDevice? Camera { get; init; }

    /// <summary>Explicitly selected local WPD interface IDs; empty disables portable-device redirection.</summary>
    public IReadOnlyList<string> WpdDevices { get; init; } = [];

    /// <summary>Optional device-channel diagnostics (handshake, device results and failures; no file or port contents). Called on background threads.</summary>
    public Action<string>? DriveRedirectionLog { get; init; }

    /// <summary>Which security layer to offer; <see cref="RdpSecurity.Auto"/> by default.</summary>
    public RdpSecurity Security { get; init; } = RdpSecurity.Auto;

    /// <summary>
    /// How long the connection may stay silent before it is kept alive; 30 seconds by default. TCP keep-alive probes
    /// start after this much idle time (three unanswered probes 10 seconds apart end the session with
    /// <see cref="RdpConnectionLostException"/>), and while display updates are paused
    /// (<see cref="IRdpSession.SetDisplayUpdatesAsync"/> with <see langword="false"/>) a small Suppress Output PDU is
    /// re-sent at this interval, so NAT gateways, firewalls and port forwarders do not expire a quiet background
    /// session. <see cref="TimeSpan.Zero"/> disables both.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}
