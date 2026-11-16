namespace VelaShell.Vnc;

/// <summary>Where in the RFB handshake a connection failed (RFC 6143 §7.1–7.3).</summary>
public enum VncConnectStage
{
    Transport,
    ProtocolVersion,
    Security,
    Tls,
    Authentication,
    Initialization
}

/// <summary>The connection could not be established; <see cref="Stage"/> says how far it got.</summary>
public class VncConnectionException(VncConnectStage stage, string detail, Exception? inner = null)
    : Exception($"VNC connection failed during {stage}: {detail}", inner)
{
    public VncConnectStage Stage { get; } = stage;

    /// <summary>The reason without the stage prefix (for localized wording).</summary>
    public string Detail { get; } = detail;
}

/// <summary>The server rejected the credentials. <see cref="ServerReason"/> is the server's own text (RFB 3.8), if any.</summary>
public sealed class VncAuthenticationException(string? serverReason, bool credentialsMissing = false, bool usernameRequired = false)
    : Exception(serverReason is { Length: > 0 } ? $"VNC authentication failed: {serverReason}" : "VNC authentication failed.")
{
    public string? ServerReason { get; } = serverReason;

    /// <summary>The selected security type needs a password (or user name) and none was given.</summary>
    public bool CredentialsMissing { get; } = credentialsMissing;

    /// <summary>The selected security type (Apple Remote Desktop, VeNCrypt Plain) needs a user name as well.</summary>
    public bool UsernameRequired { get; } = usernameRequired;
}

/// <summary>
/// No security type both sides support. <see cref="Offered"/> lists what the server offered (RFC 6143 §7.1.2 numbers),
/// so the message can name them.
/// </summary>
public sealed class VncSecurityUnsupportedException(IReadOnlyList<int> offered, string detail)
    : Exception(detail)
{
    public IReadOnlyList<int> Offered { get; } = offered;
}

/// <summary>
/// The server's TLS certificate (VeNCrypt X.509) is not trusted: neither a valid chain for the host name nor the pinned
/// SHA-256 thumbprint. The caller asks the user and connects again with <see cref="VncClientOptions.CertificatePin"/>.
/// </summary>
public sealed class VncCertificateRejectedException(byte[] pem, bool pinMismatch)
    : Exception(pinMismatch ? "The VNC server certificate does not match the trusted thumbprint." : "The VNC server certificate is not trusted.")
{
    /// <summary>The server certificate, PEM-encoded (ASCII).</summary>
    public byte[] Pem { get; } = pem;

    /// <summary>A pin was given and the certificate no longer matches it.</summary>
    public bool PinMismatch { get; } = pinMismatch;
}

/// <summary>The server sent something this client cannot parse; the session ends.</summary>
public sealed class VncProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The network connection dropped (as opposed to a protocol failure): connecting again is worth trying.</summary>
public sealed class VncConnectionLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Why a file-transfer operation failed (UltraVNC file transfer).</summary>
public enum VncFileTransferError
{
    /// <summary>The server refused file transfer for this session (disabled, or a view-only session).</summary>
    Denied,

    /// <summary>The server did not answer in time.</summary>
    Timeout,

    /// <summary>The server could not open, list or create the path.</summary>
    Unavailable,

    /// <summary>The server reported a failed command or aborted a transfer.</summary>
    Failed,

    /// <summary>The session ended while the operation was running.</summary>
    Disconnected
}

/// <summary>A file-transfer operation failed; the desktop session itself goes on (unless <see cref="Error"/> is Disconnected).</summary>
public sealed class VncFileTransferException(VncFileTransferError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public VncFileTransferError Error { get; } = error;
}

/// <summary>
/// The server closed the connection right after file transfer was requested: it does not implement UltraVNC file
/// transfer (unknown message type 7), or a LibVNC server has it disabled and drops the client. Reported through
/// <see cref="IVncSession.Disconnected"/>; connecting again works, with file transfer left alone.
/// </summary>
public sealed class VncFileTransferUnsupportedException(string message) : Exception(message);
