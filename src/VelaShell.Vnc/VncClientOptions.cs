namespace VelaShell.Vnc;

/// <summary>Which RFB security type to use (RFC 6143 §7.2 plus the registered VeNCrypt and Apple types).</summary>
public enum VncSecurity
{
    /// <summary>
    /// Pick the strongest type the server offers that this client can complete: VeNCrypt with X.509 TLS, then Apple
    /// Remote Desktop (when a user name is given), then VNC authentication, then None.
    /// </summary>
    Auto,

    /// <summary>Security type 1: no authentication.</summary>
    None,

    /// <summary>Security type 2: DES challenge-response with a password of at most 8 characters.</summary>
    VncAuthentication,

    /// <summary>Security type 19 with an X.509 subtype: TLS (certificate checked or pinned), then None, VNC or Plain authentication.</summary>
    VeNCrypt,

    /// <summary>Security type 30: Apple Remote Desktop (Diffie-Hellman + AES-128), user name and password — macOS Screen Sharing.</summary>
    AppleRemoteDesktop
}

/// <summary>The encoding the client asks for first; the server may still choose any other listed encoding.</summary>
public enum VncEncodingPreference
{
    /// <summary>Tight, then ZRLE, Hextile, RRE and Raw.</summary>
    Auto,
    Tight,
    Zrle,
    Hextile,
    Raw
}

/// <summary>A raw TCP stream to the server, dialed by the caller (e.g. through the host's proxy setting).</summary>
public delegate Task<Stream> VncDialer(string host, int port, CancellationToken ct);

/// <summary>
/// Decodes a JPEG image (Tight encoding) to top-down BGRA32, or returns null when it cannot. Supplied by the UI layer,
/// which already has an image library; without one the client does not ask for JPEG.
/// </summary>
public delegate VncDecodedImage? VncJpegDecoder(ReadOnlySpan<byte> jpeg);

/// <summary>A decoded image: top-down BGRA32, <see cref="Width"/> × <see cref="Height"/> × 4 bytes.</summary>
public sealed record VncDecodedImage(int Width, int Height, byte[] Bgra);

/// <summary>Everything <see cref="VncSession.ConnectAsync"/> needs.</summary>
public sealed class VncClientOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 5900;

    /// <summary>User name for VeNCrypt Plain and Apple Remote Desktop; ignored by the other types.</summary>
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public VncSecurity Security { get; init; } = VncSecurity.Auto;

    /// <summary>ClientInit shared-flag: leave other viewers connected (true) or ask the server to disconnect them.</summary>
    public bool Shared { get; init; } = true;
    public VncEncodingPreference Encoding { get; init; } = VncEncodingPreference.Auto;

    /// <summary>Tight JPEG quality 0–9, or null for lossless only. Ignored without <see cref="JpegDecoder"/>.</summary>
    public int? JpegQuality { get; init; } = 8;

    /// <summary>Compression level 0–9 the server is asked to use for zlib-based encodings.</summary>
    public int CompressionLevel { get; init; } = 2;

    /// <summary>SHA-256 of a certificate the user trusted before (VeNCrypt X.509).</summary>
    public byte[]? CertificatePin { get; init; }

    /// <summary>Name the TLS certificate must match; the real server behind an SSH tunnel. Defaults to <see cref="Host"/>.</summary>
    public string? CertificateHost { get; init; }

    /// <summary>Opens the TCP stream; null connects directly with a <see cref="System.Net.Sockets.TcpClient"/>.</summary>
    public VncDialer? Dialer { get; init; }
    public VncJpegDecoder? JpegDecoder { get; init; }

    /// <summary>
    /// Plays the server's audio through the default output device (QEMU Audio extension: QEMU / libvirt consoles). Other
    /// servers ignore the request; a failing output device stops audio only, never the desktop.
    /// </summary>
    public bool AudioPlayback { get; init; }

    /// <summary>
    /// Enables <see cref="IVncSession.FileTransfer"/> (UltraVNC file transfer, RFB message type 7: UltraVNC Server, x11vnc
    /// with <c>-ultrafilexfer</c>). Access is requested only after compatible SupportedMessages capabilities arrive.
    /// Missing capabilities fail locally, leaving the desktop connected.
    /// </summary>
    public bool FileTransfer { get; init; }

    /// <summary>
    /// Explicit opt-in for a known compatible legacy UltraVNC server that does not advertise SupportedMessages.
    /// Never overrides an explicit negative capability. An incompatible server may disconnect the desktop when this
    /// is enabled; do not enable it for TigerVNC, QEMU or macOS Screen Sharing.
    /// </summary>
    public bool AllowUnadvertisedFileTransfer { get; init; }

    /// <summary>Test seam: the PCM output used for audio (default: the platform device).</summary>
    internal Func<Audio.IAudioOutput>? AudioOutputFactory { get; init; }

    /// <summary>Diagnostic lines (never contains credentials).</summary>
    public Action<string>? Log { get; init; }
}
