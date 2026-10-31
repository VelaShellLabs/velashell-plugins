using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// Bridges the host to the UI-agnostic <c>VelaShell.Rdp</c> library: maps the connect request and
/// parsed <see cref="RdpSettings"/> to <see cref="RdpClientOptions"/>, applies the connect timeout,
/// and translates the library's exceptions into the host's protocol exceptions with localized text.
/// </summary>
internal static class RdpConnector
{
    internal static async Task<IRdpSession> ConnectAsync(WorkspaceConnectRequest request, RdpSettings settings, Loc loc, CancellationToken ct)
    {
        byte[]? pin = settings.CertificateSha256.Length == 64 ? Convert.FromHexString(settings.CertificateSha256) : null;
        var options = new RdpClientOptions
        {
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            Password = request.Password,
            Domain = settings.Domain,
            Width = settings.Width,
            Height = settings.Height,
            ColorDepth = 16,
            CertificatePin = pin,
            CertificateHost = settings.CertificateHost,
            Clipboard = settings.Clipboard,
            Drives = [.. settings.Drives.Select(d => new DriveMapping(d.Name, d.Path))]
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await RdpManagedSession.ConnectAsync(options, linked.Token).ConfigureAwait(false);
        }
        catch (RdpCertificateRejectedException ex)
        {
            throw CertificateTrust(ex, loc);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ProtocolConnectionException(loc["ConnectTimedOut"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex, loc);
        }
    }

    private static Exception Translate(Exception ex, Loc loc) => ex switch
    {
        ProtocolConnectionException or ProtocolAuthenticationException
            or ProtocolCertificateTrustException or ProtocolUnsupportedException => ex,
        RdpCertificateRejectedException cert => CertificateTrust(cert, loc),
        _ => new ProtocolConnectionException(ex.Message)
    };

    private static ProtocolCertificateTrustException CertificateTrust(RdpCertificateRejectedException ex, Loc loc)
    {
        using var certificate = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(ex.Pem));
        return new ProtocolCertificateTrustException(loc["Certificate"], certificate.Subject, certificate.Issuer,
            certificate.NotAfter, certificate.GetCertHashString(HashAlgorithmName.SHA256), loc["Certificate"]);
    }
}
