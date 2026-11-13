using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VelaShell.Rdp.Security;

/// <summary>
/// Establishes the RDP TLS channel and enforces the same certificate policy as the
/// former native bridge: without a pin, OS trust plus matching hostname/IP, validity
/// and the server-authentication purpose; with a pin, an exact constant-time match of
/// the leaf certificate's DER SHA-256 that replaces chain, name and expiry checks.
/// <para>
/// The protocol policy is the same on every OS: TLS 1.2 and 1.3, with the platform's
/// default cipher suites. Servers whose TLS 1.3 breaks during CredSSP are handled by a
/// one-time TLS 1.2 retry in <see cref="RdpManagedSession" />, not by a per-OS cap, so a
/// TLS 1.3-only server stays reachable from every platform.
/// </para>
/// </summary>
internal static class RdpTls
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>The protocols offered on a first attempt, on every platform.</summary>
    internal const SslProtocols DefaultProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    internal static SslClientAuthenticationOptions CreateClientAuthenticationOptions(string targetHost, bool pinned,
        SslProtocols protocols = DefaultProtocols)
    {
        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = protocols,
            AllowRenegotiation = false,
            // Every RDP connection gets a full handshake. By default SslStream resumes the previous TLS session to
            // the same host within the process (the abbreviated handshake carries no certificate), and Windows'
            // server-side CredSSP aborts NLA on a resumed session: the first TSRequest is answered with a fatal
            // internal_error alert (SEC_E_INTERNAL_ERROR 0x80090304). That is the "first connection works, every
            // reconnect fails until the app restarts" failure. Resumption saves one round trip; NLA needs a fresh session.
            AllowTlsResume = false,
            CertificateRevocationCheckMode = pinned ? X509RevocationMode.NoCheck : X509RevocationMode.Online
        };
    }

    internal static async Task<SslStream> AuthenticateAsync(Stream inner, string targetHost, byte[]? pin, CancellationToken ct,
        SslProtocols protocols = DefaultProtocols)
    {
        // Set only when this callback refuses the certificate, so that a later failure of an accepted handshake
        // (a timeout, a reset) is never reported as a trust problem.
        byte[]? rejectedPem = null;
        bool revocationUnknown = false, revoked = false;

        bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return false;
            }

            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            bool accepted = pin is not null ? MatchesPin(leaf, pin) : IsTrusted(leaf, errors);
            if (!accepted && pin is null)
            {
                switch (ClassifyRevocation(errors, chain))
                {
                    case RevocationProblem.Revoked:
                        revoked = true;   // not offered for approval at all
                        return false;
                    case RevocationProblem.Unknown:
                        revocationUnknown = true;
                        break;
                }
            }

            if (!accepted)
            {
                rejectedPem = System.Text.Encoding.UTF8.GetBytes(leaf.ExportCertificatePem());
            }

            return accepted;
        }

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, Validate);
        SslClientAuthenticationOptions options = CreateClientAuthenticationOptions(targetHost, pin is not null, protocols);
        try
        {
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (revoked)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            // Like mstsc: a revoked certificate is refused outright, with no option to trust it anyway.
            throw new RdpProtocolException("The RDP server's certificate has been revoked by its issuer; the connection is refused.", ex);
        }
        catch (Exception ex) when (rejectedPem is not null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            // A pinned certificate that no longer matches (e.g. a self-signed RDP certificate that Windows renewed)
            // goes back to the caller for re-approval, flagged so it can say the certificate changed. A certificate
            // whose only problem is that revocation could not be checked is offered for approval too, flagged.
            throw new RdpCertificateRejectedException(rejectedPem, ex, pinMismatch: pin is not null)
            {
                RevocationUnknown = revocationUnknown
            };
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return ssl;
    }

    internal enum RevocationProblem { None, Unknown, Revoked }

    /// <summary>
    /// The revocation side of a chain failure, decided the same way on every OS (as mstsc does): a certificate in the
    /// chain is revoked → refused outright; the only problem is that revocation status could not be determined
    /// (no reachable CRL/OCSP, e.g. an LDAP-only distribution point) → the user is asked, told why; anything else is
    /// an ordinary trust failure.
    /// </summary>
    internal static RevocationProblem ClassifyRevocation(SslPolicyErrors errors, X509Chain? chain)
    {
        if (chain is null)
        {
            return RevocationProblem.None;
        }

        X509ChainStatusFlags flags = X509ChainStatusFlags.NoError;
        foreach (X509ChainStatus status in chain.ChainStatus) { flags |= status.Status; }
        foreach (X509ChainElement element in chain.ChainElements)
        {
            foreach (X509ChainStatus status in element.ChainElementStatus) { flags |= status.Status; }
        }

        return ClassifyRevocation(errors, flags);
    }

    internal static RevocationProblem ClassifyRevocation(SslPolicyErrors errors, X509ChainStatusFlags flags)
    {
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0)
        {
            return RevocationProblem.None;
        }

        if ((flags & X509ChainStatusFlags.Revoked) != 0)
        {
            return RevocationProblem.Revoked;
        }

        const X509ChainStatusFlags unknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        return errors == SslPolicyErrors.RemoteCertificateChainErrors && (flags & unknown) != 0 && (flags & ~unknown) == 0
            ? RevocationProblem.Unknown
            : RevocationProblem.None;
    }

    private static bool MatchesPin(X509Certificate2 leaf, byte[] pin)
    {
        // A pin replaces every other check with an exact leaf-identity comparison.
        Span<byte> digest = stackalloc byte[32];
        _ = SHA256.HashData(leaf.RawData, digest);
        return CryptographicOperations.FixedTimeEquals(digest, pin);
    }

    private static bool IsTrusted(X509Certificate2 leaf, SslPolicyErrors errors)
    {
        if (errors != SslPolicyErrors.None)
        {
            return false;
        }

        // Match the native Windows chain policy's server-auth requirement: if the leaf
        // declares an EKU it must include serverAuth (no EKU means valid for all uses).
        foreach (X509Extension extension in leaf.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension eku)
            {
                foreach (Oid oid in eku.EnhancedKeyUsages)
                {
                    if (oid.Value == ServerAuthOid)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        return true;
    }
}
