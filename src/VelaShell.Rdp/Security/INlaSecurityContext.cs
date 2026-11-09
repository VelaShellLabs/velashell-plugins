using System.Buffers;
using System.Net;
using System.Net.Security;

namespace VelaShell.Rdp.Security;

/// <summary>
/// The security-package side of CredSSP/NLA: produces the authentication tokens carried in the TSRequest
/// negoTokens and seals/unseals the public-key binding and the credentials. The CredSSP envelope, the leg
/// sequence and the public-key binding live in <see cref="CredSsp" />; this abstracts only where the tokens
/// come from: Windows uses SSPI, while Unix uses .NET SPNEGO with system Kerberos and managed NTLM.
/// </summary>
internal interface INlaSecurityContext : IDisposable
{
    /// <summary>Whether the authentication handshake has finished; after this the next token carries pubKeyAuth.</summary>
    bool IsComplete { get; }

    /// <summary>Consumes the server's token (empty on the first call) and returns the next token to send.</summary>
    byte[] Step(byte[] incoming);

    /// <summary>Seals (signs + encrypts) a blob for the pubKeyAuth / authInfo fields.</summary>
    byte[] Seal(byte[] plaintext);

    /// <summary>Unseals a blob the server sent (the public-key echo).</summary>
    byte[] Unseal(byte[] wrapped);
}

/// <summary>Chooses the NLA security provider for the current platform.</summary>
internal static class NlaSecurityContext
{
    /// <summary>Test seam: forces the managed NTLM path even on Windows, so both providers can be exercised.</summary>
    internal static bool ForceManagedForTest;

    /// <summary>
    /// Uses SSPI on Windows and .NET Negotiate on Unix; both can negotiate Kerberos or NTLM.
    /// </summary>
    internal static INlaSecurityContext Create(string domain, string username, string password, string targetHost) =>
        OperatingSystem.IsWindows() && !ForceManagedForTest
            ? new SspiNegotiate(domain, username, password, targetHost)
            : new NegotiateSecurityContext(domain, username, password, targetHost, ForceManagedForTest ? "NTLM" : "Negotiate");
}

/// <summary>
/// .NET SPNEGO uses system GSSAPI for Kerberos and managed NTLM when Kerberos is unavailable.
/// The explicit NTLM package is retained for compatibility tests.
/// </summary>
internal sealed class NegotiateSecurityContext : INlaSecurityContext
{
    private readonly NegotiateAuthentication _auth;

    internal NegotiateSecurityContext(string domain, string username, string password, string targetHost, string package = "Negotiate")
    {
        // This enables managed SPNEGO/NTLM without disabling GSSAPI Kerberos. The runtime caches
        // the switch, so embedding hosts must also enable it in runtimeconfig before any auth API use.
        if (!OperatingSystem.IsWindows())
        {
            AppContext.SetSwitch("System.Net.Security.UseManagedNtlm", true);
        }

        _auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = package,
            Credential = new NetworkCredential(username, password, domain),
            TargetName = ServiceTarget(targetHost),
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign
        });
    }

    internal static string ServiceTarget(string targetHost) =>
        targetHost.Length > 0 && !IPAddress.TryParse(targetHost.Trim('[', ']'), out _)
            ? $"TERMSRV/{targetHost}"
            : string.Empty;

    public bool IsComplete { get; private set; }

    public byte[] Step(byte[] incoming)
    {
        byte[]? token = _auth.GetOutgoingBlob(incoming, out NegotiateAuthenticationStatusCode status);
        if (status == NegotiateAuthenticationStatusCode.Completed)
        {
            IsComplete = true;
        }
        else if (status != NegotiateAuthenticationStatusCode.ContinueNeeded)
        {
            throw new RdpAuthenticationException($"NLA negotiation failed ({status}). For Kerberos, check the server DNS name, TERMSRV SPN, user realm, system GSSAPI configuration and KDC connectivity.");
        }

        return token ?? [];
    }

    public byte[] Seal(byte[] plaintext)
    {
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = _auth.Wrap(plaintext, output, requestEncryption: true, out bool encrypted);
        return status == NegotiateAuthenticationStatusCode.Completed && encrypted
            ? output.WrittenSpan.ToArray()
            : throw new RdpProtocolException($"NLA wrap failed ({status}).");
    }

    public byte[] Unseal(byte[] wrapped)
    {
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = _auth.Unwrap(wrapped, output, out bool encrypted);
        return status == NegotiateAuthenticationStatusCode.Completed && encrypted
            ? output.WrittenSpan.ToArray()
            : throw new RdpProtocolException($"NLA unwrap failed ({status}).");
    }

    public void Dispose() => _auth.Dispose();
}
