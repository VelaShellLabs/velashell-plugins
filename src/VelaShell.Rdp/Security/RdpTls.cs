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
            CertificateRevocationCheckMode = pinned ? X509RevocationMode.NoCheck : X509RevocationMode.Online
        };
    }

    internal static async Task<SslStream> AuthenticateAsync(Stream inner, string targetHost, byte[]? pin, CancellationToken ct,
        SslProtocols protocols = DefaultProtocols)
    {
        // Set only when this callback refuses the certificate, so that a later failure of an accepted handshake
        // (a timeout, a reset) is never reported as a trust problem.
        byte[]? rejectedPem = null;

        bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return false;
            }

            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            bool accepted = pin is not null ? MatchesPin(leaf, pin) : IsTrusted(leaf, errors);
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
        catch (Exception ex) when (rejectedPem is not null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            // A pinned certificate that no longer matches (e.g. a self-signed RDP certificate that Windows renewed)
            // goes back to the caller for re-approval, flagged so it can say the certificate changed.
            throw new RdpCertificateRejectedException(rejectedPem, ex, pinMismatch: pin is not null);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return ssl;
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
                foreach (System.Security.Cryptography.Oid oid in eku.EnhancedKeyUsages)
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
