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
        NegotiationFailure = inner is Wire.RdpNegotiationFailureException refused ? (RdpNegotiationFailure)refused.Code : null;
    }

    /// <summary>The connection step that failed.</summary>
    public RdpConnectStage Stage { get; }

    /// <summary>The server's RDP_NEG_FAILURE code when it refused the security negotiation itself.</summary>
    public RdpNegotiationFailure? NegotiationFailure { get; }

    /// <summary>The TLS version negotiated before the failure; <see langword="null" /> if TLS never completed.</summary>
    public SslProtocols? TlsProtocol { get; }

    /// <summary>
    /// For failures after the TLS handshake: the cipher suite and the framing of the TLS records the server
    /// sent since (e.g. "…; server sent: Alert 26 B"), which separates a server-side abort from a local
    /// decryption problem. Part of <see cref="Exception.Message" /> (and so of logs), not of <see cref="Detail" />.
    /// </summary>
    public string? TlsTrace { get; }

    // Servers differ in how they handle TLS versions, so a failure after the handshake names the version
    // that was in use.
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
/// The server refused NLA <em>before any credentials were sent</em>: it answered the client's first CredSSP
/// message (the start of NTLM negotiation) with an error code such as <c>0x80090302</c>
/// (SEC_E_UNSUPPORTED_FUNCTION), or by aborting the TLS session. This is a server-side policy — typically
/// incoming NTLM is restricted — not a wrong user name or password, so re-entering credentials cannot help.
/// With <see cref="RdpSecurity.Auto"/> the client then retries with TLS only; this exception surfaces when
/// that is not possible (<see cref="ServerRequiresNla"/>) or when <see cref="RdpSecurity.Nla"/> was requested.
/// </summary>
public sealed class RdpNlaRefusedException : Exception
{
    internal RdpNlaRefusedException(uint? status, Exception? inner, string? tlsTrace = null, bool serverRequiresNla = false, string? auth = null)
        : base(Compose(status, inner, tlsTrace, serverRequiresNla, auth), inner)
    {
        Status = status;
        TlsTrace = tlsTrace;
        ServerRequiresNla = serverRequiresNla;
        Auth = auth;
        Detail = status is { } code ? $"0x{code:X8}" : inner is null ? "aborted" : RdpConnectionException.Describe(inner);
    }

    /// <summary>Which NLA provider ran and what first token it sent (for diagnosing a refusal), if recorded.</summary>
    public string? Auth { get; }

    /// <summary>The error code the server returned, or <see langword="null" /> when it aborted the TLS session instead.</summary>
    public uint? Status { get; }

    /// <summary>The reason as one line: the hex status, or the cause chain of the aborted read.</summary>
    public string Detail { get; }

    /// <summary>A TLS-only attempt was refused too, because the server requires NLA.</summary>
    public bool ServerRequiresNla { get; }

    /// <summary>The cipher suite and the TLS records the server sent after the handshake, when known.</summary>
    public string? TlsTrace { get; }

    /// <summary>The TLS version of the refused attempt, when the handshake had completed.</summary>
    internal SslProtocols? TlsProtocol { get; private init; }

    internal RdpNlaRefusedException WithTls(string? tlsTrace, SslProtocols? tlsProtocol) =>
        new(Status, InnerException, tlsTrace, ServerRequiresNla, Auth) { TlsProtocol = tlsProtocol };

    internal RdpNlaRefusedException RequiredByServer() =>
        new(Status, InnerException, TlsTrace, serverRequiresNla: true, Auth) { TlsProtocol = TlsProtocol };

    private static string Compose(uint? status, Exception? inner, string? tlsTrace, bool serverRequiresNla, string? auth)
    {
        string reason = status is { } code ? $"0x{code:X8}" : inner is null ? "aborted" : RdpConnectionException.Describe(inner);
        string message = $"The server refused NLA before any credentials were sent ({reason}).";
        if (auth is not null)
        {
            message += $" [{auth}]";
        }
        if (serverRequiresNla)
        {
            message += " A TLS-only connection was refused too: the server requires NLA.";
        }
        return tlsTrace is null ? message : $"{message} — {tlsTrace}";
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

/// <summary>
/// The server sent a Server Redirection PDU that could not be followed. Redirections received while connecting (an RD
/// Connection Broker assigning a session host) are followed automatically; this surfaces when that is impossible —
/// another host behind an SSH tunnel, a redirection that would change nothing, too many redirections — and, through
/// <see cref="RdpManagedSession.Disconnected" />, when a redirection ends an established session; pass it to
/// <see cref="RdpManagedSession.ConnectRedirectedAsync" /> to follow it.
/// </summary>
public sealed class RdpServerRedirectionException : Exception
{
    internal RdpServerRedirectionException(Pdu.ServerRedirection redirection)
        : base(BuildMessage(redirection))
    {
        SessionId = redirection.SessionId;
        Flags = redirection.Flags;
        TargetNetAddress = redirection.TargetNetAddress;
        TargetFqdn = redirection.TargetFqdn;
        TargetNetBiosName = redirection.TargetNetBiosName;
        TargetNetAddresses = redirection.TargetNetAddresses;
        LoadBalanceInfoLength = redirection.LoadBalanceInfoLength;
        TsvUrlLength = redirection.TsvUrlLength;
        RedirectionGuidLength = redirection.RedirectionGuidLength;
        HasPasswordOrCookie = redirection.HasPasswordOrCookie;
        HasTargetCertificate = redirection.HasTargetCertificate;
        Redirection = redirection;
    }

    /// <summary>The parsed PDU, including the load-balance info the session sends back on the redirected connection.</summary>
    internal Pdu.ServerRedirection Redirection { get; }

    /// <summary>The redirected session identifier.</summary>
    public uint SessionId { get; }

    /// <summary>The raw redirection flags from the server.</summary>
    public uint Flags { get; }

    /// <summary>The server-provided target address, when present.</summary>
    public string? TargetNetAddress { get; }

    /// <summary>The server-provided target FQDN, when present.</summary>
    public string? TargetFqdn { get; }

    /// <summary>The server-provided target NetBIOS name, when present.</summary>
    public string? TargetNetBiosName { get; }

    /// <summary>The server-provided target addresses, when present.</summary>
    public IReadOnlyList<string> TargetNetAddresses { get; }

    /// <summary>The opaque load-balancing data length; its contents are intentionally not retained.</summary>
    public int LoadBalanceInfoLength { get; }

    /// <summary>The opaque TSV URL data length; its contents are intentionally not retained.</summary>
    public int TsvUrlLength { get; }

    /// <summary>The opaque redirection GUID length; its contents are intentionally not retained.</summary>
    public int RedirectionGuidLength { get; }

    /// <summary>Whether the packet carried a password or opaque redirection cookie.</summary>
    public bool HasPasswordOrCookie { get; }

    /// <summary>Whether the packet carried a target certificate.</summary>
    public bool HasTargetCertificate { get; }

    private static string BuildMessage(Pdu.ServerRedirection redirection)
    {
        string target = redirection.TargetFqdn ?? redirection.TargetNetAddress ?? redirection.TargetNetBiosName
            ?? (redirection.TargetNetAddresses.Count == 0 ? "unspecified target" : "target address list");
        return $"The RDP server requested redirection to {target} (session 0x{redirection.SessionId:X8}, flags 0x{redirection.Flags:X8}); it could not be followed (another host through an SSH tunnel, a repeated redirection, or a redirection during an established session).";
    }
}

/// <summary>
/// An established session ended because the server sent a Set Error Info PDU (MS-RDPBCGR 2.2.5.1) carrying a
/// non-zero <see cref="ErrorInfo" />. Many of these are server-side policy: the server signed the user out,
/// another connection took over the session, or no Remote Desktop licence was available. Raised on
/// <see cref="RdpManagedSession.Disconnected" />, never thrown from connect.
/// </summary>
public sealed class RdpDisconnectException(uint errorInfo)
    : Exception($"The RDP session was disconnected by the server: {RdpErrorInfo.Describe(errorInfo)} (0x{errorInfo:X8}).")
{
    /// <summary>The MS-RDPBCGR errorInfo code the server reported.</summary>
    public uint ErrorInfo { get; } = errorInfo;

    /// <summary>A neutral one-line description of <see cref="ErrorInfo" />.</summary>
    public string Reason { get; } = RdpErrorInfo.Describe(errorInfo);
}

/// <summary>
/// The Set Error Info codes a server sends before dropping a session (MS-RDPBCGR 2.2.5.1.1), with neutral
/// descriptions for logs. The plugin layer localises the common ones; this is the fallback and the source of
/// truth for the code names.
/// </summary>
public static class RdpErrorInfo
{
    /// <summary>A short, neutral description of an errorInfo code; a generic line for codes not listed.</summary>
    public static string Describe(uint code) => code switch
    {
        0x00000001 => "the server initiated the disconnect (RPC_INITIATED_DISCONNECT)",
        0x00000002 => "the server signed the user out (RPC_INITIATED_LOGOFF)",
        0x00000003 => "the idle timeout was reached (IDLE_TIMEOUT)",
        0x00000004 => "the logon timeout was reached (LOGON_TIMEOUT)",
        0x00000005 => "another connection took over the session (DISCONNECTED_BY_OTHERCONNECTION)",
        0x00000006 => "the server ran out of memory (OUT_OF_MEMORY)",
        0x00000007 => "the server denied the connection (SERVER_DENIED_CONNECTION)",
        0x00000009 => "the account has insufficient privileges (SERVER_INSUFFICIENT_PRIVILEGES)",
        0x0000000A => "fresh credentials are required (SERVER_FRESH_CREDENTIALS_REQUIRED)",
        0x0000000B => "the user ended the connection (DISCONNECTED_BY_USER)",
        0x0000000C => "the user signed out (LOGOFF_BY_USER)",
        0x00000100 => "a licensing internal error occurred (LICENSE_INTERNAL)",
        0x00000101 => "there is no Remote Desktop licence server (LICENSE_NO_LICENSE_SERVER)",
        0x00000102 => "there is no available Remote Desktop licence (LICENSE_NO_LICENSE)",
        0x00000103 or 0x00000105 => "the Remote Desktop licence message was rejected (LICENSE_BAD_CLIENT_MSG)",
        0x00000104 => "the hardware id does not match the licence (LICENSE_HWID_DOESNT_MATCH_LICENSE)",
        0x00000106 or 0x00000107 => "the licensing protocol could not complete (LICENSE_CANT_FINISH_PROTOCOL)",
        0x00000108 => "the licence message encryption was bad (LICENSE_BAD_CLIENT_ENCRYPTION)",
        0x00000109 => "the licence could not be upgraded (LICENSE_CANT_UPGRADE_LICENSE)",
        0x0000010A => "the server has no Remote Desktop licences available for new connections (LICENSE_NO_REMOTE_CONNECTIONS)",
        0x0000112D => "the server does not support the graphics mode (GRAPHICS_MODE_NOT_SUPPORTED)",
        0x0000112E => "the server could not reset its graphics subsystem (GRAPHICS_SUBSYSTEM_RESET_FAILED)",
        0x0000112F => "the server graphics subsystem failed and cannot continue graphics encoding (GRAPHICS_SUBSYSTEM_FAILED)",
        _ => "the server ended the session"
    };

    /// <summary>Whether the code names a Remote Desktop licensing failure (0x00000100–0x0000010A).</summary>
    public static bool IsLicensing(uint code) => code is >= 0x00000100 and <= 0x0000010A;
}

/// <summary>
/// <see cref="RdpSecurity.Nla"/> was required but the server's X.224 answer selected TLS without NLA. The
/// negotiation travels in the clear, so for a server that used NLA before this is exactly what a man-in-the-middle
/// forcing a downgrade looks like (it would then receive the password inside its own TLS) — or the server's
/// "require NLA" setting was switched off. No credentials have been sent when this is raised.
/// </summary>
public sealed class RdpNlaUnavailableException : Exception
{
    internal RdpNlaUnavailableException()
        : base("The server did not select NLA, and the security layer requires NLA.") { }
}
