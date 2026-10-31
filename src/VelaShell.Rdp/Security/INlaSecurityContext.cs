using System.Buffers;
using System.Net;
using System.Net.Security;

namespace VelaShell.Rdp.Security;

/// <summary>
/// The security-package side of CredSSP/NLA: produces the authentication tokens carried in the TSRequest
/// negoTokens and seals/unseals the public-key binding and the credentials. The CredSSP envelope, the leg
/// sequence and the public-key binding live in <see cref="CredSsp" />; this abstracts only where the tokens
/// come from, so Windows can use the native stack (SPNEGO, exactly like mstsc) while other platforms fall
/// back to the managed NTLM.
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
    /// The native Windows Negotiate (SPNEGO) provider on Windows — hardened servers reject the bare NTLM that
    /// the managed stack emits, so this matches what Windows' own RDP client sends — and the managed NTLM
    /// fallback elsewhere.
    /// </summary>
    internal static INlaSecurityContext Create(string domain, string username, string password, string targetHost) =>
        OperatingSystem.IsWindows() && !ForceManagedForTest
            ? new SspiNegotiate(domain, username, password)
            : new ManagedNtlm(domain, username, password, targetHost);
}

/// <summary>
/// The portable fallback: <see cref="NegotiateAuthentication" /> with the NTLM package. Works against servers
/// that accept bare NTLM (and the local test servers); a server that requires SPNEGO refuses it, which on
/// Windows never happens because <see cref="SspiNegotiate" /> is used instead.
/// </summary>
internal sealed class ManagedNtlm : INlaSecurityContext
{
    private readonly NegotiateAuthentication _auth;

    internal ManagedNtlm(string domain, string username, string password, string targetHost) =>
        _auth = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "NTLM",
            Credential = new NetworkCredential(username, password, domain),
            TargetName = $"TERMSRV/{targetHost}",
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign
        });

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
            throw new RdpProtocolException($"NLA negotiation failed ({status}).");
        }

        return token ?? [];
    }

    public byte[] Seal(byte[] plaintext)
    {
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = _auth.Wrap(plaintext, output, requestEncryption: true, out _);
        return status == NegotiateAuthenticationStatusCode.Completed
            ? output.WrittenSpan.ToArray()
            : throw new RdpProtocolException($"NLA wrap failed ({status}).");
    }

    public byte[] Unseal(byte[] wrapped)
    {
        var output = new ArrayBufferWriter<byte>();
        NegotiateAuthenticationStatusCode status = _auth.Unwrap(wrapped, output, out _);
        return status == NegotiateAuthenticationStatusCode.Completed
            ? output.WrittenSpan.ToArray()
            : throw new RdpProtocolException($"NLA unwrap failed ({status}).");
    }

    public void Dispose() => _auth.Dispose();
}
