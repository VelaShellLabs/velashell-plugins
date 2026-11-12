using System.Security.Authentication;
using System.Threading.Channels;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Security;

namespace VelaShell.Rdp;

/// <summary>
/// Drives a pure-managed <see cref="RdpConnection"/> behind the <see cref="IRdpSession"/> contract:
/// a single background task pumps the connection while frames are coalesced and snapshotted on
/// demand, and input, clipboard and dynamic-resolution requests are forwarded to the wire.
/// </summary>
public sealed partial class RdpManagedSession : IRdpSession, IRdpPointerSource, IRdpNetworkSource, IRdpRemoteAppSource
{
    internal RdpConnection Connection { get; }
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _dirty;
    private int _isConnected;
    private int _disposed;
    private uint _errorInfo;
    private int _logonInfo = -1;
    private int _reachedLogon;
    private CliprdrChannel? _cliprdr;
    private Channel<byte[]>? _cliprdrQueue;
    private Exception? _clipboardFailure;
    private long _clipboardQueuedBytes;
    private long _rdpdrQueuedBytes;
    private Task? _cliprdrWorker;
    private RdpdrChannel? _rdpdr;
    private Channel<byte[]>? _rdpdrQueue;
    private Task? _rdpdrWorker;
    private Action<string>? _driveLog;

    private void LogDrive(string message)
    {
        try { _driveLog?.Invoke($"RDPDR {message}"); }
        catch { /* Diagnostics must not interrupt the channel worker. */ }
    }
    private DrdynvcChannel? _drdynvc;
    private WpdRedirection? _wpd;
    public RdpNetworkStatus NetworkStatus => Connection.Network.Status;
    public event Action<RdpNetworkStatus>? NetworkChanged
    {
        add => Connection.Network.Changed += value;
        remove => Connection.Network.Changed -= value;
    }

    private Channel<ChannelPayload>? _drdynvcQueue;
    private Task? _drdynvcWorker;
    private long _drdynvcQueuedBytes;
    private Exception? _dynamicChannelFailure;

    public bool IsConnected => Volatile.Read(ref _isConnected) != 0;

    /// <summary>
    /// Whether the server sent a logon notification (Save Session Info) — the credentials were accepted and a
    /// Windows session started. A disconnect after this is the server ending an established session, not an
    /// authentication or protocol failure. <see cref="LogonInfoType"/> carries the notification type if any.
    /// </summary>
    public bool ReachedLogon => Volatile.Read(ref _reachedLogon) != 0;

    /// <summary>The Save Session Info infoType the server sent (0/1 logon, 2 plain-notify, 3 extended), or null.</summary>
    public uint? LogonInfoType => Volatile.Read(ref _logonInfo) is var v && v >= 0 ? (uint)v : null;

    public event Action? FrameAvailable;

    /// <summary>The server changed the mouse pointer (raised on the receive thread).</summary>
    public event Action<RdpPointer>? PointerChanged;
    public event Action<Exception?>? Disconnected;

    private readonly Lock _clipGate = new();
    private Action<RdpClipboardEvent>? _clipboardHandlers;
    private RdpClipboardEvent? _lastReady;
    private RdpClipboardEvent? _lastFormats;

    /// <summary>Replays the clipboard handshake (ready + current formats) to late subscribers, like the native session.</summary>
    public event Action<RdpClipboardEvent>? ClipboardEvent
    {
        add
        {
            lock (_clipGate)
            {
                _clipboardHandlers += value;
                if (_clipboardFailure is { } error)
                {
                    SafeClip(value, new RdpClipboardEvent(10, 0, 0, System.Text.Encoding.UTF8.GetBytes(error.Message)));
                    return;
                }
                if (_lastReady is { } ready)
                {
                    SafeClip(value, ready);
                }

                if (_lastFormats is { } formats)
                {
                    SafeClip(value, formats);
                }
            }
        }
        remove
        {
            lock (_clipGate)
            {
                _clipboardHandlers -= value;
            }
        }
    }

    private void RaiseClipboard(RdpClipboardEvent ev)
    {
        lock (_clipGate)
        {
            if (ev.Kind == 2)
            {
                _lastReady = ev;
            }

            if (ev.Kind is 3 or 9)
            {
                _lastFormats = ev;
            }

            SafeClip(_clipboardHandlers, ev);
        }
    }

    private static void SafeClip(Action<RdpClipboardEvent>? handler, RdpClipboardEvent ev)
    {
        try { handler?.Invoke(ev); } catch { /* never unwind into the pump */ }
    }

    internal RdpManagedSession(RdpConnection connection)
    {
        Connection = connection;
        Connection.FrameUpdated += OnFrameUpdated;
        Connection.PointerChanged += pointer =>
        {
            try { PointerChanged?.Invoke(pointer); } catch { /* never unwind into the pump */ }
        };
        Connection.ErrorInfoReceived += code => Volatile.Write(ref _errorInfo, code);
        Connection.LogonInfoReceived += type =>
        {
            Volatile.Write(ref _logonInfo, (int)type);
            Volatile.Write(ref _reachedLogon, 1);
            LogDrive($"server logon notification type={type}");
        };
        Connection.ChannelData += OnChannelData;
    }

    /// <summary>
    /// Connects to an RDP server and begins pumping desktop frames. On failure the partial session
    /// is disposed and throws: <see cref="RdpCertificateRejectedException"/> when the server certificate
    /// is untrusted (its PEM is on the exception), <see cref="RdpAuthenticationException"/> when the server
    /// rejects the credentials during NLA (with its status code), <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> fires, otherwise an <see cref="RdpConnectionException"/> naming the
    /// failing <see cref="RdpConnectStage"/> and the cause chain (including TLS/socket error codes).
    /// <para>
    /// With <see cref="RdpSecurity.Auto"/>, a server that refuses NLA before any credentials are sent is retried
    /// once offering TLS only (see <see cref="NlaRefusal"/>); <see cref="RdpNlaRefusedException"/> is thrown when
    /// that is refused too because the server requires NLA, or when <see cref="RdpSecurity.Nla"/> was requested.
    /// </para>
    /// </summary>
    public static Task<RdpManagedSession> ConnectAsync(RdpClientOptions options, CancellationToken ct = default) =>
        ConnectCoreAsync(options, null, ct);

    internal static Task<RdpManagedSession> ConnectAsyncWithCookie(RdpClientOptions options, AutoReconnectCookie? cookie, CancellationToken ct = default) =>
        ConnectCoreAsync(options, cookie, ct);

    /// <summary>Server Redirection PDUs followed before giving up (a broker normally redirects once).</summary>
    internal const int MaxRedirections = 3;

    /// <summary>
    /// Follows Server Redirection PDUs received while connecting (MS-RDPBCGR 3.1.5.2.1): an RD Connection Broker
    /// answers the first connection with the session host to use and a routing token. The connection is then made
    /// again to that target, with the token in the X.224 request and the session id in the cluster data. The PDU
    /// arrives over the already validated TLS channel; the target's certificate is validated in full on the new
    /// connection, and the user's own credentials are used.
    /// </summary>
    private static async Task<RdpManagedSession> ConnectCoreAsync(RdpClientOptions options, AutoReconnectCookie? cookie, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        for (int redirections = 0; ; redirections++)
        {
            try
            {
                RdpManagedSession session = await ConnectSecurityAsync(options, cookie, ct).ConfigureAwait(false);
                session.RedirectedTo = redirections == 0 ? null : options.CertificateHost ?? options.Host;
                return session;
            }
            catch (RdpServerRedirectionException redirect) when (redirections < MaxRedirections && Redirect(options, redirect.Redirection) is { } next)
            {
                options = next;
                cookie = null;   // the auto-reconnect cookie belongs to the session on the previous server
            }
        }
    }

    /// <summary>The host this session was redirected to by a connection broker, or <see langword="null" />.</summary>
    public string? RedirectedTo { get; private set; }

    /// <summary>
    /// The options for following <paramref name="redirection" />, or <see langword="null" /> when it cannot be followed
    /// (the redirection exception then surfaces). The target is TargetFqdn, else TargetNetAddress, else the first of
    /// TargetNetAddresses; with NoRedirect, or no target, the same server is asked again with the routing token. A
    /// certificate pin belongs to one host, so it is kept only when the host stays the same. Through an SSH tunnel only
    /// the same server is reachable.
    /// </summary>
    internal static RdpClientOptions? Redirect(RdpClientOptions options, Pdu.ServerRedirection redirection)
    {
        string current = string.IsNullOrEmpty(options.CertificateHost) ? options.Host : options.CertificateHost;
        string? target = (redirection.Flags & Pdu.RedirectionFlags.NoRedirect) != 0 ? null
            : redirection.TargetFqdn ?? redirection.TargetNetAddress ?? redirection.TargetNetAddresses.FirstOrDefault();
        bool sameHost = string.IsNullOrWhiteSpace(target) || target.Equals(current, StringComparison.OrdinalIgnoreCase);
        if (sameHost && redirection.LoadBalanceInfo is not { Length: > 0 })
        {
            return null;   // nothing would change: connecting again would only loop
        }

        RdpClientOptions next = options with
        {
            RoutingToken = redirection.LoadBalanceInfo,
            RedirectedSessionId = redirection.SessionId
        };
        if (sameHost)
        {
            return next;
        }

        if (options.TcpTunnel && options.Gateway is null)
        {
            return null;   // the tunnel leads to the original server only
        }

        return next with { Host = target!, CertificateHost = target, CertificatePin = null };
    }

    private static async Task<RdpManagedSession> ConnectSecurityAsync(RdpClientOptions options, AutoReconnectCookie? cookie, CancellationToken ct)
    {
        if (options.AudioInput is { } audioInput)
        {
            audioInput.Validate();
            throw new RdpAudioInputNotSupportedException();
        }
        if (options.Security != RdpSecurity.Auto)
        {
            return await ConnectWithTlsFallbackAsync(options, options.Security, cookie, ct).ConfigureAwait(false);
        }

        try
        {
            return await ConnectWithTlsFallbackAsync(options, RdpSecurity.Auto, cookie, ct).ConfigureAwait(false);
        }
        catch (RdpNlaRefusedException refused)
        {
            // Nothing secret was sent. Offer TLS only — same certificate validation — so a server whose
            // "require NLA" setting is off can still log the user on itself.
            try
            {
                RdpManagedSession session = await ConnectWithTlsFallbackAsync(options, RdpSecurity.Tls, cookie, ct).ConfigureAwait(false);
                session.NlaRefusal = refused;
                return session;
            }
            catch (RdpConnectionException tls) when (tls.NegotiationFailure == RdpNegotiationFailure.HybridRequiredByServer)
            {
                throw refused.RequiredByServer();
            }
        }
    }

    /// <summary>
    /// Endpoints (certificate host and port) whose TLS 1.3 failed during NLA in this process; later connections
    /// to them start with TLS 1.2 instead of paying for a failed attempt each time.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Tls13FailedDuringNla = new(StringComparer.OrdinalIgnoreCase);

    internal static void ForgetTlsFallbacksForTest() => Tls13FailedDuringNla.Clear();

    private static string TlsEndpoint(RdpClientOptions options) =>
        $"{(string.IsNullOrEmpty(options.CertificateHost) ? options.Host : options.CertificateHost)}:{options.Port}";

    /// <summary>
    /// Some servers negotiate TLS 1.3 and then abort it during the CredSSP exchange (a fatal alert such as
    /// internal_error, or records that no longer decrypt). Under TLS 1.3 that looks the same as a server refusing
    /// NTLM (which also aborts with an alert), so before reporting either the connection is retried once, on a new
    /// TCP connection, with TLS 1.2 only; whatever that attempt yields — success, a refusal that then leads to the
    /// TLS-only fallback, an authentication verdict — is the result. The rule is the same on every OS. The
    /// certificate is validated again on the retry, so credentials only ever go to the same verified server.
    /// The endpoint is remembered for TLS 1.2 only when the retry succeeds, i.e. when TLS 1.3 was the problem.
    /// </summary>
    private static async Task<RdpManagedSession> ConnectWithTlsFallbackAsync(RdpClientOptions options, RdpSecurity security,
        AutoReconnectCookie? cookie, CancellationToken ct)
    {
        string endpoint = TlsEndpoint(options);
        if (Tls13FailedDuringNla.ContainsKey(endpoint))
        {
            RdpManagedSession remembered = await ConnectOnceAsync(options, security, cookie, SslProtocols.Tls12, ct).ConfigureAwait(false);
            remembered.TlsDowngraded = true;
            return remembered;
        }

        Exception first;
        try
        {
            return await ConnectOnceAsync(options, security, cookie, RdpTls.DefaultProtocols, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTls13FailureDuringNla(ex))
        {
            first = ex;
        }

        RdpManagedSession session = await ConnectOnceAsync(options, security, cookie, SslProtocols.Tls12, ct).ConfigureAwait(false);
        _ = Tls13FailedDuringNla.TryAdd(endpoint, 0);
        session.TlsDowngraded = true;
        session.TlsFallbackCause = first;
        return session;
    }

    /// <summary>
    /// The attempt negotiated TLS 1.3 and the TLS layer itself failed during NLA: either reported as a connection
    /// failure at the NLA stage, or as a refusal whose cause was a TLS abort rather than a server error code.
    /// </summary>
    internal static bool IsTls13FailureDuringNla(Exception failure) => failure switch
    {
        RdpConnectionException failed => failed.Stage == RdpConnectStage.Nla && failed.TlsProtocol == SslProtocols.Tls13
            && CredSsp.IsConnectionLossDuringNla(failed.InnerException),
        RdpNlaRefusedException refused => refused.Status is null && refused.TlsProtocol == SslProtocols.Tls13
            && CredSsp.IsConnectionLossDuringNla(refused.InnerException),
        _ => false
    };

    /// <summary>
    /// <see langword="true" /> when this session uses TLS 1.2 because TLS 1.3 to the same endpoint failed during NLA
    /// (on this attempt or an earlier one in this process).
    /// </summary>
    public bool TlsDowngraded { get; private set; }

    /// <summary>
    /// The TLS 1.3 failure (an <see cref="RdpConnectionException" /> or an <see cref="RdpNlaRefusedException" />) that
    /// caused <see cref="TlsDowngraded" /> on this attempt, for logging; otherwise <see langword="null" />.
    /// </summary>
    public Exception? TlsFallbackCause { get; private set; }

    /// <summary>The security layer in use: <see cref="RdpSecurity.Nla"/> or <see cref="RdpSecurity.Tls"/>.</summary>
    public RdpSecurity Security => Connection.Security;

    internal AutoReconnectCookie? AutoReconnectCookie => Connection.AutoReconnectCookie;

    /// <summary>
    /// Set when <see cref="RdpSecurity.Auto"/> fell back to TLS because the server refused NLA before any
    /// credentials were sent; carries the server's reason. <see langword="null" /> otherwise.
    /// </summary>
    public RdpNlaRefusedException? NlaRefusal { get; private set; }

    internal static IReadOnlyList<ChannelDef> BuildChannels(RdpClientOptions options, bool graphicsReady = false)
    {
        var channels = new List<ChannelDef>();
        if (options.RemoteApp is not null)
        {
            options.RemoteApp.Validate();
            channels.Add(new ChannelDef("rail", 0xC0A00000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP|SHOW_PROTOCOL
        }
        if (options.Clipboard)
        {
            channels.Add(new ChannelDef("cliprdr", 0xC0A00000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP|SHOW_PROTOCOL
        }

        // MS-RDPEA 2.1, Windows note: without RDPDR, the server sends nothing on RDPSND.
        if (options.AudioPlayback || options.SmartCardRedirection || options.Drives.Count > 0 || options.Ports.Count > 0 || options.Printers.Count > 0)
        {
            channels.Add(new ChannelDef("rdpdr", 0xC0800000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP
        }

        // Windows device redirection also needs the sound channel registered, even without playback.
        if (options.AudioPlayback || options.SmartCardRedirection || options.Drives.Count > 0 || options.Ports.Count > 0 || options.Printers.Count > 0)
        {
            channels.Add(new ChannelDef("rdpsnd", 0xC0000000)); // INITIALIZED|ENCRYPT_RDP
        }

        if (graphicsReady || ((options.DynamicResolution || options.MonitorLayout is not null) && options.RemoteApp is null)
            || options.Camera is not null || options.WpdDevices.Count != 0)
        {
            channels.Add(new ChannelDef("drdynvc", 0xC0800000)); // dynamic graphics, display control and device channels
        }

        return channels;
    }

    internal static uint GatewayRedirections(RdpClientOptions options)
    {
        uint flags = 0;
        if (options.Drives.Count != 0) { flags |= 1; }
        if (options.Printers.Count != 0) { flags |= 2; }
        if (options.Ports.Count != 0) { flags |= 4; }
        if (options.Clipboard) { flags |= 8; }
        if (options.WpdDevices.Count != 0 || options.Camera is not null) { flags |= 0x10; }
        // These have no individual RDG policy bit, but are still subject to DISABLE_ALL.
        if (options.SmartCardRedirection || options.AudioPlayback) { flags |= 0x20; }
        return flags;
    }

    private static async Task<RdpManagedSession> ConnectOnceAsync(RdpClientOptions options, RdpSecurity security, AutoReconnectCookie? cookie,
        SslProtocols tlsProtocols, CancellationToken ct)
    {
        if (options.RemoteApp is not null && options.MonitorLayout is not null)
        {
            throw new ArgumentException("Monitor layouts are unavailable for RemoteApp sessions.", nameof(options));
        }

        int width = options.MonitorLayout?.DesktopWidth ?? options.Width;
        int height = options.MonitorLayout?.DesktopHeight ?? options.Height;
        IRdpGfxBackend? backend = PrepareGraphics(options);
        IReadOnlyList<ChannelDef> channels;
        RdpManagedSession session;
        try
        {
            channels = BuildChannels(options, backend is not null);
            var connectOptions = new RdpConnectOptions(options.Host, options.Port, options.Username, options.Password,
                options.Domain, width, height, backend is not null ? 32 : options.ColorDepth, options.CertificatePin,
                string.IsNullOrEmpty(options.CertificateHost) ? options.Host : options.CertificateHost, channels, security,
                options.Performance, options.AudioPlayback, RdpScale.Clamp(options.DesktopScaleFactor), options.DrawingOrders,
                options.KeepAliveInterval, options.Transport, options.AdaptiveNetwork, options.TcpTunnel, backend is not null,
                options.RemoteApp, options.Gateway, GatewayRedirections(options), cookie, tlsProtocols,
                options.RoutingToken, options.RedirectedSessionId);
            session = new RdpManagedSession(new RdpConnection(connectOptions))
            {
                _driveLog = options.DriveRedirectionLog,
                _graphicsLog = options.GraphicsLog
            };
            if (backend is not null)
            {
                session._graphics = new RdpGfxChannel(backend, session.Connection.ApplyGraphicsFrame, session.FailGraphics, session._stop.Token);
            }
        }
        catch
        {
            backend?.Dispose();
            throw;
        }
        session.LogDrive($"configured drives={options.Drives.Count}; engine={typeof(RdpManagedSession).Assembly.Location}");
        session.LogAudio($"configured playback={options.AudioPlayback}; requested={channels.Any(channel => channel.Name == "rdpsnd")}; backend={(OperatingSystem.IsWindows() ? "WinMM" : OperatingSystem.IsMacOS() ? "AudioQueue" : "ALSA")}");
        try
        {
            if (options.RemoteApp is { } remoteApp) { session.EnableRemoteApp(remoteApp, options.Width, options.Height); }
            if (channels.Any(channel => channel.Name == "rdpsnd"))
            {
                session.EnableAudio(options.AudioPlayback);
            }
            if (channels.Any(channel => channel.Name == "drdynvc"))
            {
                session.EnableDynamicChannels(options);
            }
            if (options.Clipboard)
            {
                session.EnableClipboard();
            }
            if (channels.Any(channel => channel.Name == "rdpdr"))
            {
                session.EnableDrives(options.Drives, options.Ports, options.Printers, options.SmartCardRedirection);
            }
            using var connectStop = CancellationTokenSource.CreateLinkedTokenSource(ct, session._stop.Token);
            await session.Connection.ConnectAsync(connectStop.Token).ConfigureAwait(false);
            if (session._dynamicChannelFailure is { } failure) { throw failure; }
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && session._dynamicChannelFailure is { } failure) { ex = failure; }
            RdpConnectStage stage = session.Connection.Stage;
            System.Security.Authentication.SslProtocols? tls = session.Connection.TlsProtocol;
            string? tlsTrace = session.Connection.TlsTrace;
            await session.DisposeAsync().ConfigureAwait(false);
            // Cancellation, certificate trust and rejected credentials each mean something specific to
            // the caller; everything else gets the failing stage and a readable cause chain.
            if (ex is OperationCanceledException or RdpCertificateRejectedException or RdpAuthenticationException or RdpNlaUnavailableException
                or RdpServerRedirectionException)
            {
                throw;
            }

            if (ex is RdpNlaRefusedException refused)
            {
                throw refused.WithTls(tlsTrace, tls);
            }

            throw new RdpConnectionException(stage, ex, tls, tlsTrace);
        }
        session.LogDrive($"connection active; rdpdr channelId={session.Connection.ChannelId("rdpdr")}");
        session.LogAudio($"connection active; rdpsnd channelId={session.Connection.ChannelId("rdpsnd")}");
        Volatile.Write(ref session._isConnected, 1);
        // PumpLoopAsync yields at the first ReadPduAsync, so this returns immediately; no Task.Run needed.
        session._pump = session.PumpLoopAsync();
        session._keepAlive = session.KeepAliveLoopAsync(options.KeepAliveInterval);
        if (session._rail is { } rail)
        {
            try { await rail.Launched.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
        }
        return session;
    }

    private void EnableDynamicChannels(RdpClientOptions options)
    {
        // Servers fragment dynamic-channel messages into ~1.6 KB PDUs, so one 1–2 MB WPD request is about a
        // thousand queue entries, and the worker can be held behind a large outbound message (a WPD read
        // reply, a camera frame). Overflow drops the session, so the 32 MB byte budget in OnChannelData is
        // the real bound; the count only caps per-entry overhead for tiny PDUs.
        _drdynvcQueue = Channel.CreateBounded<ChannelPayload>(new BoundedChannelOptions(16384) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _drdynvc = new DrdynvcChannel((pdu, token) => Connection.SendVirtualChannelAsync("drdynvc", pdu, token), _stop.Token,
            (options.DynamicResolution || options.MonitorLayout is not null) && options.RemoteApp is null,
            LogDrive, Connection.WaitForTunnelAsync, Connection.EnableTunnelReading, Connection.SendTunnelAsync,
            options.MonitorLayout, options.DesktopScaleFactor);
        if (options.Camera is { } camera)
        {
            _ = new CameraChannel(_drdynvc, camera, () => OperatingSystem.IsWindows()
                ? new WindowsCameraBackend(camera.Id)
                : throw new PlatformNotSupportedException("Camera redirection requires Windows."));
        }
        if (options.WpdDevices.Count != 0)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("WPD redirection requires Windows.");
            }

            _wpd = new WpdRedirection(new WindowsWpdBackend(options.WpdDevices, LogDrive));
            _wpd.Register(_drdynvc);
        }
        _graphics?.Register(_drdynvc);
        _drdynvcWorker = DrainDrdynvcAsync();
    }

    private void EnableClipboard()
    {
        _cliprdrQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _cliprdr = new CliprdrChannel(
            send: async (pdu, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
                await Connection.SendVirtualChannelAsync("cliprdr", pdu, linked.Token).ConfigureAwait(false);
            },
            raise: RaiseClipboard);
        _cliprdrWorker = DrainCliprdrAsync();
    }

    private void EnableDrives(IReadOnlyList<DriveMapping> mappings, IReadOnlyList<PortMapping> ports,
        IReadOnlyList<PrinterMapping> printers, bool smartCards)
    {
        var devices = new List<IRdpdrDevice>();
        try
        {
            if (smartCards) { devices.Add(new SmartCardDevice()); }
            devices.AddRange(ports.Select(p => (IRdpdrDevice)new PortDevice(p)));
            devices.AddRange(printers.Select((p, i) => (IRdpdrDevice)new PrinterDevice(p, i + 1)));
            _rdpdrQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            _rdpdr = new RdpdrChannel(pdu => Connection.SendVirtualChannelAsync("rdpdr", pdu, _stop.Token), mappings, LogDrive,
                devices, _stop.Token);
        }
        catch
        {
            foreach (IRdpdrDevice device in devices) { device.Dispose(); }
            throw;
        }
        _rdpdrWorker = DrainRdpdrAsync();
    }

    private void OnChannelData(ChannelPayload payload)
    {
        if (payload.ChannelName == "rail")
        {
            ReceiveRail(payload.Data);
        }
        else if (payload.ChannelName == "cliprdr")
        {
            if (_clipboardFailure is not null) { return; }
            long queued = Interlocked.Add(ref _clipboardQueuedBytes, payload.Data.Length);
            if (queued > 32 * 1024 * 1024 || _cliprdrQueue?.Writer.TryWrite(payload.Data) != true)
            {
                _ = Interlocked.Add(ref _clipboardQueuedBytes, -payload.Data.Length);
                FailClipboard(new IOException("RDP clipboard queue capacity exceeded."));
            }
        }
        else if (payload.ChannelName == "rdpsnd")
        {
            ReceiveAudio(payload.Data);
        }
        else if (payload.ChannelName == "rdpdr")
        {
            // Bounded by bytes as well as by count: 256 queued requests of up to 32 MB each is no bound at all.
            long queued = Interlocked.Add(ref _rdpdrQueuedBytes, payload.Data.Length);
            if (queued > 64 * 1024 * 1024 || _rdpdrQueue?.Writer.TryWrite(payload.Data) != true)
            {
                _ = Interlocked.Add(ref _rdpdrQueuedBytes, -payload.Data.Length);
                throw new IOException("RDP drive request queue capacity exceeded; reconnect the session.");
            }
        }
        else if (payload.ChannelName == "drdynvc")
        {
            long queued = Interlocked.Add(ref _drdynvcQueuedBytes, payload.Data.Length);
            if (payload.Data.Length > 1024 * 1024 || queued > 32 * 1024 * 1024
                || _drdynvcQueue?.Writer.TryWrite(payload) != true)
            {
                _ = Interlocked.Add(ref _drdynvcQueuedBytes, -payload.Data.Length);
                throw new IOException("RDP dynamic-channel queue capacity exceeded; reconnect the session.");
            }
        }
    }

    private async Task DrainRdpdrAsync()
    {
        if (_rdpdrQueue is null || _rdpdr is null)
        {
            return;
        }

        try
        {
            await foreach (byte[] pdu in _rdpdrQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _ = Interlocked.Add(ref _rdpdrQueuedBytes, -pdu.Length);
                try { await _rdpdr.ProcessAsync(pdu).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { LogDrive($"packet processing failed: {ex.GetType().Name}; bytes={pdu.Length}"); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task DrainDrdynvcAsync()
    {
        if (_drdynvcQueue is null || _drdynvc is null)
        {
            return;
        }

        try
        {
            await foreach (ChannelPayload pdu in _drdynvcQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _ = Interlocked.Add(ref _drdynvcQueuedBytes, -pdu.Data.Length);
                await _drdynvc.ProcessAsync(pdu.Data, pdu.TunnelType).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _ = Interlocked.CompareExchange(ref _dynamicChannelFailure,
                new IOException("RDP dynamic-channel processing failed; reconnect the session.", ex), null);
            _stop.Cancel();
        }
        finally
        {
            try
            {
                if (_wpd is not null)
                {
                    await _wpd.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) { LogDrive($"WPD cleanup failed: {ex.GetType().Name}"); }
            try { await _drdynvc.CloseAsync().ConfigureAwait(false); }
            catch (Exception ex) { LogDrive($"dynamic-channel cleanup failed: {ex.GetType().Name}"); }
            if (_graphics is not null) { await _graphics.DisposeAsync().ConfigureAwait(false); }
        }
    }

    /// <summary>Asks the server, via the display-control channel, to resize the desktop to match the view.</summary>
    public Task RequestResizeAsync(int width, int height, CancellationToken cancellationToken) =>
        _drdynvc?.RequestResizeAsync(width, height) ?? Task.CompletedTask;

    /// <summary>
    /// Like <see cref="RequestResizeAsync(int, int, CancellationToken)"/>, also passing the display scale in percent
    /// (150 for a 150% local display) so the remote UI renders at the local text size.
    /// </summary>
    public Task RequestResizeAsync(int width, int height, int scalePercent, CancellationToken cancellationToken = default) =>
        _drdynvc?.RequestResizeAsync(width, height, scalePercent) ?? Task.CompletedTask;

    /// <summary>Requests a multi-monitor virtual desktop layout through MS-RDPEDISP.</summary>
    public Task RequestMonitorLayoutAsync(RdpMonitorLayout layout, int scalePercent = 100, CancellationToken cancellationToken = default) =>
        _drdynvc?.RequestMonitorLayoutAsync(layout, scalePercent) ?? Task.CompletedTask;

    public Task SetDisplayUpdatesAsync(bool visible, CancellationToken cancellationToken = default) => Guard(async () =>
    {
        // Serialized with the keep-alive, which re-sends the paused state: a keep-alive that saw "paused" just before
        // the view came back must not land after the resume and leave the desktop frozen.
        await _displayGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Connection.ServerSupportsSuppressOutput)
            {
                Volatile.Write(ref _displayPaused, visible ? 0 : 1);
                await Connection.SendSuppressOutputAsync(visible, cancellationToken).ConfigureAwait(false);
            }

            // Updates suppressed while hidden were never sent; ask for a full repaint when the view comes back.
            if (visible && Connection.ServerSupportsRefreshRect)
            {
                await Connection.SendRefreshRectAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _ = _displayGate.Release(); }
    });

    private readonly SemaphoreSlim _displayGate = new(1, 1);
    private int _displayPaused;
    private Task? _keepAlive;

    /// <summary>Keep-alive PDUs sent while display updates were paused (diagnostics and tests).</summary>
    internal int KeepAlivesSent => Volatile.Read(ref _keepAlivesSent);
    private int _keepAlivesSent;

    /// <summary>
    /// While display updates are paused the server sends nothing at all, and a silent connection is exactly what NAT
    /// gateways, firewalls and port forwarders expire — TCP keep-alive probes carry no data, so a user-space forwarder
    /// (an SSH or frp tunnel, an nginx stream proxy) never sees them. Re-sending the paused state changes nothing on the
    /// server and keeps every hop on the path busy.
    /// </summary>
    private async Task KeepAliveLoopAsync(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Volatile.Read(ref _displayPaused) == 0)
                {
                    continue;   // a shown desktop has its own traffic, and TCP keep-alive covers a static one
                }

                await _displayGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    if (Volatile.Read(ref _displayPaused) != 0 && IsConnected)
                    {
                        await Connection.SendSuppressOutputAsync(false, _stop.Token).ConfigureAwait(false);
                        _ = Interlocked.Increment(ref _keepAlivesSent);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* a dead connection ends the pump, which reports it */ }
                finally { _ = _displayGate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainCliprdrAsync()
    {
        if (_cliprdrQueue is null || _cliprdr is null)
        {
            return;
        }

        try
        {
            await foreach (byte[] pdu in _cliprdrQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _ = Interlocked.Add(ref _clipboardQueuedBytes, -pdu.Length);
                if (_clipboardFailure is not null) { continue; }
                try { await _cliprdr.ProcessAsync(pdu, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { FailClipboard(ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void FailClipboard(Exception error)
    {
        if (Interlocked.CompareExchange(ref _clipboardFailure, error, null) is not null) { return; }
        _ = (_cliprdrQueue?.Writer.TryComplete());
        RaiseClipboard(new RdpClipboardEvent(10, 0, 0, System.Text.Encoding.UTF8.GetBytes(error.Message)));
    }

    private void OnFrameUpdated()
    {
        _ = Interlocked.Exchange(ref _dirty, 1);
        try { FrameAvailable?.Invoke(); } catch { /* never unwind into the pump */ }
    }

    public RdpFrame? TakeFrame()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return null;
        }

        (byte[] bgra, int width, int height) = Connection.SnapshotFrame();
        return new RdpFrame(width, height, bgra);
    }

    public RdpFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full = false)
    {
        _ = Interlocked.Exchange(ref _dirty, 0);
        RdpFrameCopy copy = Connection.CopyFrame(destination, stride, width, height, full);
        if (copy.SizeChanged)
        {
            _ = Interlocked.Exchange(ref _dirty, 1);   // still pending until the caller has a surface of the new size
        }

        return copy;
    }

    private async Task PumpLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_stop.IsCancellationRequested && await Connection.PumpAsync(_stop.Token).ConfigureAwait(false)) { }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Volatile.Write(ref _isConnected, 0);
            _stop.Cancel();
            failure = DisconnectCause(_dynamicChannelFailure ?? failure, Volatile.Read(ref _errorInfo));
            _rail?.Stop(failure ?? new RdpProtocolException("RemoteApp session ended before launch completed."));
            Connection.RemoteWindows?.Clear();
            if (Volatile.Read(ref _disposed) == 0) { try { Disconnected?.Invoke(failure); } catch { /* ignore */ } }
        }
    }

    /// <summary>
    /// What <see cref="Disconnected"/> reports. A server that sent Set Error Info told us why it is ending the session
    /// (signed out, taken over, licence …): that reason wins over a clean end and over the connection closing right
    /// after it, which would otherwise look like a network loss. Everything else is reported as it happened —
    /// <see cref="RdpConnectionLostException"/> for the network, protocol and channel failures as themselves.
    /// </summary>
    internal static Exception? DisconnectCause(Exception? failure, uint errorInfo) =>
        errorInfo != 0 && failure is null or RdpConnectionLostException ? new RdpDisconnectException(errorInfo) : failure;

    public Task KeyAsync(uint code, bool down, CancellationToken ct = default) =>
        Guard(() => Connection.SendScancodeAsync(code, down, ct));

    public Task UnicodeAsync(string text, CancellationToken ct = default) =>
        Guard(() => Connection.SendUnicodeTextAsync(text, ct));   // up to 7 characters per fast-path PDU

    public Task MouseAsync(uint flags, uint x, uint y, CancellationToken ct = default) =>
        Guard(() => Connection.SendMouseAsync(flags, x, y, ct));

    // ---- Clipboard transport → cliprdr channel (inert when clipboard is disabled) ----
    public Task OfferFormatsAsync(string formats, CancellationToken cancellationToken) => Clipboard(channel => channel.OfferFormatsAsync(formats, cancellationToken));
    public Task RequestFormatAsync(uint format, CancellationToken cancellationToken) => Clipboard(channel => channel.RequestFormatAsync(format, cancellationToken));
    public Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken) => Clipboard(channel => channel.RespondFormatAsync(data, success, cancellationToken));
    public bool SupportsClipboardLock => _cliprdr?.SupportsClipboardLock == true;
    public Task LockClipboardAsync(uint id, bool locked, CancellationToken cancellationToken) => Clipboard(channel => channel.LockClipboardAsync(id, locked, cancellationToken));
    public Task RequestFileVersionAsync(uint stream, uint index, uint flags, ulong offset, uint length, uint? clipDataId, CancellationToken cancellationToken) => Clipboard(channel => channel.RequestFileAsync(stream, index, flags, offset, length, clipDataId, cancellationToken));
    public Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken) => Clipboard(channel => channel.RequestFileAsync(stream, index, flags, offset, length, ct: cancellationToken));
    public Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken) => Clipboard(channel => channel.RespondFileAsync(stream, data, success, cancellationToken));

    private Task Clipboard(Func<CliprdrChannel, Task> action)
    {
        if (_clipboardFailure is { } error) { return Task.FromException(new IOException("RDP clipboard sharing has stopped.", error)); }
        return _cliprdr is { } channel ? action(channel) : Task.CompletedTask;
    }

    private async Task Guard(Func<Task> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected)
        {
            throw new InvalidOperationException("The RDP session is not connected.");
        }

        await action().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { if (_pump is { } p) { await p.ConfigureAwait(false); } return; }
        _stop.Cancel();
        Volatile.Write(ref _isConnected, 0);
        _ = (_cliprdrQueue?.Writer.TryComplete());
        _ = (_rdpdrQueue?.Writer.TryComplete());
        _ = (_drdynvcQueue?.Writer.TryComplete());
        _ = (_audioQueue?.Writer.TryComplete());
        _ = (_railQueue?.Writer.TryComplete());
        _rail?.Stop();
        if (_railWorker is { } railWorker) { await railWorker.ConfigureAwait(false); }
        Connection.RemoteWindows?.Clear();
        RemoteWindowsChanged = null;
        if (_audioWorker is { } audioWorker) { await audioWorker.ConfigureAwait(false); }
        _audioStop?.Dispose();
        lock (_audioGate) { _audioHandlers = null; }
        if (_pump is { } pump) { try { await pump.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_keepAlive is { } keepAlive) { try { await keepAlive.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_cliprdrWorker is { } worker) { try { await worker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_rdpdrWorker is { } driveWorker) { try { await driveWorker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_drdynvcWorker is { } dvcWorker) { try { await dvcWorker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_graphics is not null) { await _graphics.DisposeAsync().ConfigureAwait(false); }
        if (_wpd is not null)
        {
            try { await _wpd.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { LogDrive($"WPD cleanup failed: {ex.GetType().Name}"); }
        }
        _rdpdr?.CloseAll();
        if (_rdpdr is { } devices)
        {
            try { await devices.DrainDevicesAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { LogDrive("native device cancellation still pending; resources retained until driver completion"); }
        }
        await Connection.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        FrameAvailable = null;
        PointerChanged = null;
        Disconnected = null;
        lock (_clipGate)
        {
            _clipboardHandlers = null;
        }
    }
}
