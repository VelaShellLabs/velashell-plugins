using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;

namespace VelaShell.Rdp;

/// <summary>The step of the RDP connection sequence that was running when a connection attempt failed.</summary>
public enum RdpConnectStage
{
    /// <summary>Opening the TCP connection.</summary>
    Tcp,

    /// <summary>X.224 security-protocol negotiation.</summary>
    Negotiation,

    /// <summary>The TLS handshake.</summary>
    Tls,

    /// <summary>Network Level Authentication (CredSSP over TLS).</summary>
    Nla,

    /// <summary>MCS / GCC connect, erect-domain, attach-user and channel joins.</summary>
    Mcs,

    /// <summary>Sending the client info (logon) PDU.</summary>
    Login,

    /// <summary>The licensing exchange.</summary>
    Licensing,

    /// <summary>Capability exchange and connection finalization.</summary>
    Capabilities
}

/// <summary>
/// A connection attempt failed. Carries the <see cref="Stage" /> it failed in and a readable
/// <see cref="Detail" /> of the cause chain — including the Win32/SChannel or socket error code, which is
/// what tells a TLS-level failure ("The decryption operation failed") apart: the outer message alone
/// only says "see inner exception".
/// </summary>
public sealed class RdpConnectionException : Exception
{
    internal RdpConnectionException(RdpConnectStage stage, Exception inner, SslProtocols? tls = null, string? tlsTrace = null)
        : base($"RDP connection failed during {stage}: {WithTls(Describe(inner), tls)}"
            + (tlsTrace is null ? "" : $" — {tlsTrace}"), inner)
    {
        Stage = stage;
        TlsProtocol = tls;
        TlsTrace = tlsTrace;
        Detail = WithTls(Describe(inner), tls);
    }

    /// <summary>The connection step that failed.</summary>
    public RdpConnectStage Stage { get; }

    /// <summary>The TLS version negotiated before the failure; <see langword="null" /> if TLS never completed.</summary>
    public SslProtocols? TlsProtocol { get; }

    /// <summary>
    /// For failures after the TLS handshake: the cipher suite and the framing of the TLS records the server
    /// sent since (e.g. "…; server sent: Alert 26 B"), which separates a server-side abort from a local
    /// decryption problem. Part of <see cref="Exception.Message" /> (and so of logs), not of <see cref="Detail" />.
    /// </summary>
    public string? TlsTrace { get; }

    // Servers differ in how they handle TLS versions (FreeRDP keeps /tls:enforce:1.2 for "buggy TLS
    // version negotiation"), so a failure after the handshake names the version that was in use.
    private static string WithTls(string detail, SslProtocols? tls) => tls switch
    {
        SslProtocols.Tls13 => detail + " [TLS 1.3]",
        SslProtocols.Tls12 => detail + " [TLS 1.2]",
        { } other => $"{detail} [{other}]",
        null => detail
    };

    /// <summary>The cause chain as one line, e.g. "The decryption operation failed → The specified data could not be decrypted (0x80090330)".</summary>
    public string Detail { get; }

    /// <summary>Flattens an exception chain into one readable line with native error codes.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var parts = new List<string>();
        for (Exception? e = exception; e is not null && parts.Count < 6; e = e.InnerException)
        {
            string message = e.Message.Replace(", see inner exception.", "", StringComparison.Ordinal).Trim();
            string code = e switch
            {
                SocketException socket => $" ({socket.SocketErrorCode})",
                Win32Exception win32 => $" (0x{win32.NativeErrorCode:X8})",
                _ => ""
            };
            string part = message + code;
            if (parts.Count == 0 || !string.Equals(parts[^1], part, StringComparison.Ordinal))
            {
                parts.Add(part);
            }
        }
        return string.Join(" → ", parts);
    }
}

/// <summary>
/// The server rejected the credentials during NLA. <see cref="Status" /> is the NTSTATUS/HRESULT the server
/// reported (e.g. <c>0xC000006D</c> STATUS_LOGON_FAILURE), or <see langword="null" /> when the failure
/// was detected on the client side.
/// </summary>
public sealed class RdpAuthenticationException : Exception
{
    internal RdpAuthenticationException(string message, uint? status = null) : base(message) => Status = status;

    /// <summary>The server-reported status code, if any.</summary>
    public uint? Status { get; }
}
