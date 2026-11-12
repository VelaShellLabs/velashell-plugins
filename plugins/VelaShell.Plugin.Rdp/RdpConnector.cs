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
    internal static async Task<IRdpSession> ConnectAsync(WorkspaceConnectRequest request, RdpSettings settings, Loc loc, CancellationToken ct, Action<string>? driveLog = null)
    {
        byte[]? pin = settings.CertificateSha256.Length == 64 ? Convert.FromHexString(settings.CertificateSha256) : null;
        // Connect at the size and scale the view will have (the last view's, recorded when it laid out): starting at
        // the configured size meant a full repaint, an immediate resize and a second full repaint. The configured
        // size remains the fallback before any view exists, and for a server that cannot follow the window.
        (int width, int height, int scale) = RdpViewMetrics.Last ?? (settings.Width, settings.Height, 100);
        var options = new RdpClientOptions
        {
            Host = request.Host,
            Port = request.Port,
            Transport = request.Tunnel is null && settings.Gateway is null ? settings.Transport : RdpTransportMode.Tcp,
            TcpTunnel = request.Tunnel is not null || settings.Gateway is not null,
            Gateway = settings.Gateway,
            RemoteApp = settings.RemoteApp,
            SmartCardRedirection = settings.SmartCardRedirection,
            AdaptiveNetwork = settings.AdaptiveNetwork,
            Username = request.Username,
            Password = request.Password,
            Domain = settings.Domain,
            Width = width,
            Height = height,
            DesktopScaleFactor = scale,
            ColorDepth = settings.ColorDepth,
            CertificatePin = pin,
            CertificateHost = settings.CertificateHost,
            Clipboard = settings.Clipboard,
            Drives = [.. settings.Drives.Select(static d => new DriveMapping(d.Name, d.Path))],
            Ports = settings.Ports,
            Printers = settings.Printers,
            Camera = settings.Camera,
            WpdDevices = settings.WpdDevices,
            DriveRedirectionLog = driveLog,
            GraphicsLog = driveLog,
            EnableGraphicsPipeline = settings.EnableGraphicsPipeline,
            Security = settings.Security,
            Performance = settings.Performance,
            AudioPlayback = settings.Audio
            // DynamicResolution left at its default (true): the plugin always follows the window.
        };
        // RemoteApp includes the RAIL handshake and published-program launch after the base RDP connection.
        // Keep the host timeout from cancelling that phase before the library's 30-second launch wait can finish.
        int timeoutSeconds = settings.RemoteApp is null ? settings.TimeoutSeconds : Math.Max(settings.TimeoutSeconds, 45);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await RdpManagedSession.ConnectAsyncWithCookie(options, settings.AutoReconnectCookie, linked.Token).ConfigureAwait(false);
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
        // Refused before any credentials were sent: a server policy, so not an authentication failure — the
        // host must not suggest re-entering the password.
        RdpNlaRefusedException refused => new ProtocolConnectionException(
            loc.Format(refused.ServerRequiresNla ? "NlaRefusedRequired" : "NlaRefused", refused.Detail), refused),
        RdpNlaUnavailableException unavailable => new ProtocolConnectionException(loc["NlaUnavailable"], unavailable),
        RdpServerRedirectionException redirection => new ProtocolConnectionException(redirection.Message, redirection),
        RdpConnectionException { NegotiationFailure: RdpNegotiationFailure.HybridRequiredByServer } failed =>
            new ProtocolConnectionException(loc["NegotiationNlaRequired"], failed),
        RdpConnectionException { NegotiationFailure: RdpNegotiationFailure.SslNotAllowedByServer } failed =>
            new ProtocolConnectionException(loc["NegotiationLegacyOnly"], failed),
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

    /// <summary>
    /// A readable reason for a mid-session disconnect. The server's Set Error Info code is mapped to a
    /// localized line for the common cases (signed out, taken over, no Remote Desktop licence); other codes
    /// fall back to the library's neutral text plus the hex code. Returns <see langword="null" /> for an
    /// ordinary drop (no error-info, e.g. network loss), which keeps the plain "disconnected" message.
    /// </summary>
    internal static string? DisconnectReason(Exception? failure, Loc loc)
    {
        if (failure is RdpServerRedirectionException redirection)
        {
            return redirection.Message;
        }

        if (failure is not RdpDisconnectException disconnect)
        {
            return null;
        }

        string? known = disconnect.ErrorInfo switch
        {
            0x00000002 or 0x0000000C => loc["ByeLoggedOff"],
            0x00000005 => loc["ByeTakenOver"],
            0x00000001 or 0x0000000B => loc["ByeByServer"],
            0x00000007 or 0x00000009 => loc["ByeDenied"],
            0x00000003 or 0x00000004 => loc["ByeTimeout"],
            0x0000112D => loc["ByeGraphicsMode"],
            0x0000112E => loc["ByeGraphicsReset"],
            0x0000112F => loc["ByeGraphicsFailed"],
            _ when RdpErrorInfo.IsLicensing(disconnect.ErrorInfo) => loc["ByeLicense"],
            _ => null
        };
        return known is null
            ? loc.Format("ByeGeneric", disconnect.Reason, disconnect.ErrorInfo)
            : loc.Format("ByeKnown", known, disconnect.ErrorInfo);
    }

    private static ProtocolCertificateTrustException CertificateTrust(RdpCertificateRejectedException ex, Loc loc)
    {
        using var certificate = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(ex.Pem));
        // A pinned certificate that no longer matches is offered again, but says so: Windows renews self-signed RDP
        // certificates, and the user must be able to tell that from a first-time approval before trusting it.
        string reason = ex.PinMismatch ? loc["CertificateChanged"] : loc["Certificate"];
        return new ProtocolCertificateTrustException(reason, certificate.Subject, certificate.Issuer,
            certificate.NotAfter, certificate.GetCertHashString(HashAlgorithmName.SHA256), reason);
    }
}
