namespace VelaShell.Rdp;

/// <summary>
/// The RDP security layer the client offers in the X.224 negotiation. Legacy "standard RDP security"
/// (no TLS, no server certificate) is never offered or accepted.
/// </summary>
public enum RdpSecurity
{
    /// <summary>
    /// Offer TLS and NLA and let the server choose (a server that supports NLA picks it). If the server refuses
    /// NLA <em>before any credentials are sent</em> — it will not even start NTLM, typically because incoming NTLM
    /// is restricted — reconnect once offering TLS only, where the server itself checks the password sent in the
    /// client info. The server certificate is validated exactly as on the first attempt. A rejected password is
    /// never retried this way.
    /// </summary>
    Auto,

    /// <summary>NLA (CredSSP) only: fails when the server does not select NLA, and never falls back to TLS.</summary>
    Nla,

    /// <summary>
    /// TLS only: the credentials travel in the client info PDU inside the validated TLS channel and the server
    /// performs the logon (as with a server whose "require NLA" setting is off).
    /// </summary>
    Tls
}

/// <summary>The failure codes of an X.224 RDP_NEG_FAILURE (MS-RDPBCGR 2.2.1.2.2).</summary>
public enum RdpNegotiationFailure : uint
{
    /// <summary>The server requires TLS, which the client did not offer.</summary>
    SslRequiredByServer = 1,

    /// <summary>The server only allows legacy standard RDP security.</summary>
    SslNotAllowedByServer = 2,

    /// <summary>The server has no certificate for TLS.</summary>
    SslCertNotOnServer = 3,

    /// <summary>The client's requested-protocol flags were inconsistent.</summary>
    InconsistentFlags = 4,

    /// <summary>The server requires NLA (CredSSP), which the client did not offer.</summary>
    HybridRequiredByServer = 5,

    /// <summary>The server requires TLS with user authentication.</summary>
    SslWithUserAuthRequiredByServer = 6
}
