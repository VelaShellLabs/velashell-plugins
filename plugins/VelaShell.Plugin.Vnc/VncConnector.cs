using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VelaShell.PluginSdk.Network;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// Bridges the host to the UI-agnostic <c>VelaShell.Vnc</c> library: maps the connect request and parsed
/// <see cref="VncSettings"/> to <see cref="VncClientOptions"/>, applies the connect timeout, dials through the host's
/// proxy setting, and translates the library's exceptions into the host's protocol exceptions with localized text.
/// </summary>
internal static class VncConnector
{
    internal static VncClientOptions CreateOptions(WorkspaceConnectRequest request, VncSettings settings,
        Action<string>? log = null, INetworkApi? network = null, VncJpegDecoder? jpeg = null) => new()
        {
            Host = request.Host,
            Port = request.Port,
            Username = request.Username,
            Password = request.Password,
            Security = settings.Security,
            Shared = settings.Shared,
            Encoding = settings.Encoding,
            JpegQuality = settings.JpegQuality,
            CompressionLevel = settings.CompressionLevel,
            CertificatePin = settings.CertificateSha256.Length == 64 ? Convert.FromHexString(settings.CertificateSha256) : null,
            CertificateHost = settings.CertificateHost,
            // The host dials through its own network proxy setting (an SSH tunnel endpoint is loopback, so direct).
            Dialer = network is null ? null : (host, port, token) => DialAsync(network, host, port, settings.TimeoutSeconds, token),
            JpegDecoder = jpeg ?? VncJpeg.Decode,
            AudioPlayback = settings.Audio,
            ContinuousUpdates = settings.ContinuousUpdates,
            // View only sends nothing to the server, files included.
            FileTransfer = settings.FileTransfer && !settings.ViewOnly,
            AllowUnadvertisedFileTransfer = settings.AllowUnadvertisedFileTransfer && settings.FileTransfer && !settings.ViewOnly,
            Log = log
        };

    internal static async Task<IVncSession> ConnectAsync(WorkspaceConnectRequest request, VncSettings settings, Loc loc,
        CancellationToken ct, Action<string>? log = null, INetworkApi? network = null, VncJpegDecoder? jpeg = null)
    {
        VncClientOptions options = CreateOptions(request, settings, log, network, jpeg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await VncSession.ConnectAsync(options, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ProtocolConnectionException(loc["ConnectTimedOut"], ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex, settings, loc);
        }
    }

    private static async Task<Stream> DialAsync(INetworkApi network, string host, int port, int timeoutSeconds, CancellationToken ct)
    {
        HostTcpConnection connection = await network.ConnectTcpAsync(host, port, new TcpConnectOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
            // VNC has no keep-alive of its own: an idle desktop sends nothing, and NATs drop silent connections.
            KeepAliveIdle = TimeSpan.FromSeconds(30)
        }, ct).ConfigureAwait(false);
        return connection.Stream;
    }

    /// <summary>The host's proxy failure inside a library exception: its message is already localized and names the proxy.</summary>
    private static NetworkProxyException? ProxyFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is NetworkProxyException proxy) { return proxy; }
        }
        return null;
    }

    internal static Exception Translate(Exception ex, VncSettings settings, Loc loc) => ex switch
    {
        _ when ProxyFailure(ex) is { } proxy => new ProtocolConnectionException(proxy.Message, ex),
        ProtocolConnectionException or ProtocolAuthenticationException or ProtocolCertificateTrustException or ProtocolUnsupportedException => ex,
        VncCertificateRejectedException cert => CertificateTrust(cert, loc),
        // A credentials problem surfaces as an authentication failure, so the host offers to re-enter them.
        VncAuthenticationException { CredentialsMissing: true } missing => new ProtocolAuthenticationException(
            loc[missing.UsernameRequired ? "CredentialsRequired" : "PasswordRequired"], missing),
        VncAuthenticationException { ServerReason: { Length: > 0 } reason } auth => new ProtocolAuthenticationException(loc.Format("AuthFailedReason", reason), auth),
        VncAuthenticationException auth => new ProtocolAuthenticationException(loc["AuthFailed"], auth),
        VncSecurityUnsupportedException unsupported => new ProtocolConnectionException(
            loc.Format(settings.Security == VncSecurity.Auto ? "SecurityUnsupported" : "SecurityNotOffered",
                string.Join(", ", unsupported.Offered.Select(VncSession.SecurityName))), unsupported),
        VncConnectionException failed => new ProtocolConnectionException(loc.Format("ConnectFailedAt", loc["Stage" + failed.Stage], failed.Detail), failed),
        _ => new ProtocolConnectionException(ex.Message, ex)
    };

    /// <summary>
    /// A readable reason for a mid-session end: the server closing the connection (null failure), or a protocol error.
    /// Returns null for a dropped network connection, which keeps the plain "disconnected" message.
    /// </summary>
    internal static string? DisconnectReason(Exception? failure, Loc loc) => failure switch
    {
        null => loc["ByeServerClosed"],
        VncConnectionLostException => null,
        VncFileTransferUnsupportedException => loc["ByeFileTransfer"],
        _ => loc.Format("ByeProtocol", failure.Message)
    };

    private static ProtocolCertificateTrustException CertificateTrust(VncCertificateRejectedException ex, Loc loc)
    {
        using var certificate = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(ex.Pem));
        string reason = ex.PinMismatch ? loc["CertificateChanged"] : loc["Certificate"];
        return new ProtocolCertificateTrustException(reason, certificate.Subject, certificate.Issuer,
            certificate.NotAfter, certificate.GetCertHashString(HashAlgorithmName.SHA256), reason);
    }
}
