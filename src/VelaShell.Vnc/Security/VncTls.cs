using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VelaShell.Vnc.Security;

/// <summary>
/// The TLS layer of VeNCrypt's X.509 subtypes. Policy, identical on every OS: without a pin the platform must trust the
/// chain for <c>targetHost</c> (validity, name, server-auth purpose); with a pin the leaf's DER SHA-256 must match exactly,
/// which replaces chain, name and expiry checks (VNC servers almost always present a self-signed certificate, which the
/// user approves once). A revoked certificate is refused outright and never offered for approval.
/// </summary>
internal static class VncTls
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    internal static async Task<SslStream> AuthenticateAsync(Stream inner, string targetHost, byte[]? pin, CancellationToken ct)
    {
        byte[]? rejectedPem = null;
        bool revoked = false;

        bool Validate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null)
            {
                return false;
            }

            using X509Certificate2 leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            bool accepted = pin is not null ? MatchesPin(leaf, pin) : IsTrusted(leaf, errors);
            if (!accepted && pin is null && IsRevoked(chain))
            {
                revoked = true;
                return false;
            }

            if (!accepted)
            {
                rejectedPem = System.Text.Encoding.ASCII.GetBytes(leaf.ExportCertificatePem());
            }

            return accepted;
        }

        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, Validate);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowRenegotiation = false,
            CertificateRevocationCheckMode = pin is null ? X509RevocationMode.Online : X509RevocationMode.NoCheck
        };
        try
        {
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (revoked)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new VncConnectionException(VncConnectStage.Tls, "the server certificate has been revoked by its issuer", ex);
        }
        catch (Exception) when (rejectedPem is not null)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new VncCertificateRejectedException(rejectedPem, pinMismatch: pin is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new VncConnectionException(VncConnectStage.Tls, ex.Message, ex);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return ssl;
    }

    private static bool IsRevoked(X509Chain? chain)
    {
        if (chain is null)
        {
            return false;
        }

        foreach (X509ChainElement element in chain.ChainElements)
        {
            foreach (X509ChainStatus status in element.ChainElementStatus)
            {
                if ((status.Status & X509ChainStatusFlags.Revoked) != 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool MatchesPin(X509Certificate2 leaf, byte[] pin)
    {
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

        // A leaf that declares an extended key usage must include serverAuth (no EKU means valid for all uses).
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
