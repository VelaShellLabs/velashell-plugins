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
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ProtocolConnectionException(loc["ConnectTimedOut"], ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex, loc);
        }
    }

    internal static Exception Translate(Exception ex, Loc loc) => ex switch
    {
        ProtocolConnectionException or ProtocolAuthenticationException
            or ProtocolCertificateTrustException or ProtocolUnsupportedException => ex,
        RdpCertificateRejectedException cert => CertificateTrust(cert, loc),
        // A credentials problem must surface as an authentication failure (not a network error), with
        // the reason the server gave.
        RdpAuthenticationException auth => new ProtocolAuthenticationException(AuthenticationMessage(auth.Status, loc), auth),
        RdpConnectionException failed => new ProtocolConnectionException(
            loc.Format("ConnectFailedAt", StageName(failed.Stage, loc), failed.Detail), failed),
        _ => new ProtocolConnectionException(RdpConnectionException.Describe(ex), ex)
    };

    /// <summary>Turns the NTSTATUS/HRESULT a server reports for a rejected NLA logon into a readable reason.</summary>
    internal static string AuthenticationMessage(uint? status, Loc loc)
    {
        string? reason = status switch
        {
            0xC000006D or 0xC000006A or 0xC0000064 or 0x8009030C => loc["NlaBadCredentials"],   // logon failure / wrong password / no such user / SEC_E_LOGON_DENIED
            0xC0000071 or 0xC0000224 => loc["NlaPasswordExpired"],                             // password expired / must change
            0xC0000234 => loc["NlaAccountLocked"],
            0xC0000072 or 0xC0000193 => loc["NlaAccountDisabled"],                             // disabled / expired account
            0xC000006E or 0xC000015B or 0xC000006F or 0xC0000070 or 0xC0000413 => loc["NlaNotAllowed"], // restrictions / logon type not granted / hours / workstation / auth firewall
            _ => null
        };
        return (status, reason) switch
        {
            ({ } code, { } known) => loc.Format("NlaRejected", known, code),
            ({ } code, null) => $"{loc["AuthFailed"]} (0x{code:X8})",
            _ => loc["AuthFailed"]
        };
    }

    private static string StageName(RdpConnectStage stage, Loc loc) => loc["Stage" + stage];

    private static ProtocolCertificateTrustException CertificateTrust(RdpCertificateRejectedException ex, Loc loc)
    {
        using var certificate = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(ex.Pem));
        return new ProtocolCertificateTrustException(loc["Certificate"], certificate.Subject, certificate.Issuer,
            certificate.NotAfter, certificate.GetCertHashString(HashAlgorithmName.SHA256), loc["Certificate"]);
    }
}
