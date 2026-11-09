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
/// </summary>
internal static class RdpTls
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    // Some RDP servers advertise TLS 1.3 but fail during the CredSSP application-data exchange
    // when reached through Linux OpenSSL. TLS 1.2 is the interoperable RDP baseline already
    // covered by the live-server matrix, so keep Linux on that version until those servers are fixed.
    private static SslProtocols EnabledProtocols => OperatingSystem.IsLinux()
        ? SslProtocols.Tls12
        : SslProtocols.Tls12 | SslProtocols.Tls13;

    // AppleCrypto has a known failure mode with the static RSA-GCM suites used by
    // some RDP servers. Keep TLS 1.3 and ephemeral ECDHE suites, while retaining
    // ECDHE-CBC for older servers; this avoids the affected RSA key-transport path
    // without replacing macOS's stable Secure Transport backend process-wide.
    private static readonly TlsCipherSuite[] MacOsCipherSuites =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA384
    ];

    internal static SslClientAuthenticationOptions CreateClientAuthenticationOptions(string targetHost, bool pinned)
    {
        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = EnabledProtocols,
            AllowRenegotiation = false,
            CertificateRevocationCheckMode = pinned ? X509RevocationMode.NoCheck : X509RevocationMode.Online,
            CipherSuitesPolicy = OperatingSystem.IsMacOS() ? new CipherSuitesPolicy(MacOsCipherSuites) : null
        };
    }

    internal static async Task<SslStream> AuthenticateAsync(Stream inner, string targetHost, byte[]? pin, CancellationToken ct)
    {
        byte[]? capturedPem = null;

        bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return false;
            }

            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            capturedPem = System.Text.Encoding.UTF8.GetBytes(leaf.ExportCertificatePem());
            if (pin is not null)
            {
                // A pin replaces every other check with an exact leaf-identity comparison.
                Span<byte> digest = stackalloc byte[32];
                _ = SHA256.HashData(leaf.RawData, digest);
                return CryptographicOperations.FixedTimeEquals(digest, pin);
            }
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
                    bool serverAuth = false;
                    foreach (Oid oid in eku.EnhancedKeyUsages)
                    {
                        if (oid.Value == ServerAuthOid) { serverAuth = true; break; }
                    }

                    if (!serverAuth)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, Validate);
        SslClientAuthenticationOptions options = CreateClientAuthenticationOptions(targetHost, pin is not null);
        try
        {
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (capturedPem is not null && pin is null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new RdpCertificateRejectedException(capturedPem, ex);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return ssl;
    }
}
