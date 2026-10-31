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

    /// <summary>Optional logon domain.</summary>
    public string Domain { get; init; } = "";

    /// <summary>Initial desktop width; the session also tracks the viewport when the server supports display control.</summary>
    public int Width { get; init; } = 1920;

    /// <summary>Initial desktop height.</summary>
    public int Height { get; init; } = 1080;

    /// <summary>Requested colour depth in bits per pixel.</summary>
    public int ColorDepth { get; init; } = 16;

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
}
