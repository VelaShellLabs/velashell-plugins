using System.Threading.Channels;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Mcs;

namespace VelaShell.Rdp;

/// <summary>
/// Drives a pure-managed <see cref="RdpConnection"/> behind the <see cref="IRdpSession"/> contract:
/// a single background task pumps the connection while frames are coalesced and snapshotted on
/// demand, and input, clipboard and dynamic-resolution requests are forwarded to the wire.
/// </summary>
public sealed class RdpManagedSession : IRdpSession, IRdpPointerSource
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
    private Channel<byte[]>? _drdynvcQueue;
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

    private RdpManagedSession(RdpConnection connection)
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
    public static async Task<RdpManagedSession> ConnectAsync(RdpClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Security != RdpSecurity.Auto)
        {
            return await ConnectOnceAsync(options, options.Security, ct).ConfigureAwait(false);
        }

        try
        {
            return await ConnectOnceAsync(options, RdpSecurity.Auto, ct).ConfigureAwait(false);
        }
        catch (RdpNlaRefusedException refused)
        {
            // Nothing secret was sent. Offer TLS only — same certificate validation — so a server whose
            // "require NLA" setting is off can still log the user on itself.
            try
            {
                RdpManagedSession session = await ConnectOnceAsync(options, RdpSecurity.Tls, ct).ConfigureAwait(false);
                session.NlaRefusal = refused;
                return session;
            }
            catch (RdpConnectionException tls) when (tls.NegotiationFailure == RdpNegotiationFailure.HybridRequiredByServer)
            {
                throw refused.RequiredByServer();
            }
        }
    }

    /// <summary>The security layer in use: <see cref="RdpSecurity.Nla"/> or <see cref="RdpSecurity.Tls"/>.</summary>
    public RdpSecurity Security => Connection.Security;

    /// <summary>
    /// Set when <see cref="RdpSecurity.Auto"/> fell back to TLS because the server refused NLA before any
    /// credentials were sent; carries the server's reason. <see langword="null" /> otherwise.
    /// </summary>
    public RdpNlaRefusedException? NlaRefusal { get; private set; }

    internal static IReadOnlyList<ChannelDef> BuildChannels(RdpClientOptions options)
    {
        var channels = new List<ChannelDef>();
        if (options.Clipboard)
        {
            channels.Add(new ChannelDef("cliprdr", 0xC0A00000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP|SHOW_PROTOCOL
        }

        if (options.Drives.Count > 0 || options.Ports.Count > 0 || options.Printers.Count > 0)
        {
            channels.Add(new ChannelDef("rdpdr", 0xC0800000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP
            // Windows device redirection also needs the sound channel registered, even without playback.
            channels.Add(new ChannelDef("rdpsnd", 0xC0000000)); // INITIALIZED|ENCRYPT_RDP
        }

        if (options.DynamicResolution || options.Camera is not null || options.WpdDevices.Count != 0)
        {
            channels.Add(new ChannelDef("drdynvc", 0xC0800000)); // dynamic virtual channels: display-control resize
        }

        return channels;
    }

    private static async Task<RdpManagedSession> ConnectOnceAsync(RdpClientOptions options, RdpSecurity security, CancellationToken ct)
    {
        IReadOnlyList<ChannelDef> channels = BuildChannels(options);
        var connectOptions = new RdpConnectOptions(options.Host, options.Port, options.Username, options.Password,
            options.Domain, options.Width, options.Height, options.ColorDepth, options.CertificatePin,
            string.IsNullOrEmpty(options.CertificateHost) ? options.Host : options.CertificateHost, channels, security,
            options.Performance, options.AudioPlayback, RdpScale.Clamp(options.DesktopScaleFactor), options.DrawingOrders,
            options.KeepAliveInterval);

        var session = new RdpManagedSession(new RdpConnection(connectOptions)) { _driveLog = options.DriveRedirectionLog };
        session.LogDrive($"configured drives={options.Drives.Count}; engine={typeof(RdpManagedSession).Assembly.Location}");
        try
        {
            if (options.DynamicResolution || options.Camera is not null || options.WpdDevices.Count != 0)
            {
                session.EnableDynamicChannels(options);
            }
            if (options.Clipboard)
            {
                session.EnableClipboard();
            }
            if (options.Drives.Count > 0 || options.Ports.Count > 0 || options.Printers.Count > 0)
            {
                session.EnableDrives(options.Drives, options.Ports, options.Printers);
            }
            await session.Connection.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RdpConnectStage stage = session.Connection.Stage;
            System.Security.Authentication.SslProtocols? tls = session.Connection.TlsProtocol;
            string? tlsTrace = session.Connection.TlsTrace;
            await session.DisposeAsync().ConfigureAwait(false);
            // Cancellation, certificate trust and rejected credentials each mean something specific to
            // the caller; everything else gets the failing stage and a readable cause chain.
            if (ex is OperationCanceledException or RdpCertificateRejectedException or RdpAuthenticationException or RdpNlaUnavailableException)
            {
                throw;
            }

            if (ex is RdpNlaRefusedException refused)
            {
                throw refused.WithTlsTrace(tlsTrace);
            }

            throw new RdpConnectionException(stage, ex, tls, tlsTrace);
        }
        session.LogDrive($"connection active; rdpdr channelId={session.Connection.ChannelId("rdpdr")}");
        Volatile.Write(ref session._isConnected, 1);
        // PumpLoopAsync yields at the first ReadPduAsync, so this returns immediately; no Task.Run needed.
        session._pump = session.PumpLoopAsync();
        session._keepAlive = session.KeepAliveLoopAsync(options.KeepAliveInterval);
        return session;
    }

    private void EnableDynamicChannels(RdpClientOptions options)
    {
        // Servers fragment dynamic-channel messages into ~1.6 KB PDUs, so one 1–2 MB WPD request is about a
        // thousand queue entries, and the worker can be held behind a large outbound message (a WPD read
        // reply, a camera frame). Overflow drops the session, so the 32 MB byte budget in OnChannelData is
        // the real bound; the count only caps per-entry overhead for tiny PDUs.
        _drdynvcQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16384) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _drdynvc = new DrdynvcChannel((pdu, token) => Connection.SendVirtualChannelAsync("drdynvc", pdu, token), _stop.Token,
            options.DynamicResolution, LogDrive);
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

    private void EnableDrives(IReadOnlyList<DriveMapping> mappings, IReadOnlyList<PortMapping> ports, IReadOnlyList<PrinterMapping> printers)
    {
        _rdpdrQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _rdpdr = new RdpdrChannel(pdu => Connection.SendVirtualChannelAsync("rdpdr", pdu, _stop.Token), mappings, LogDrive,
            [.. ports.Select(p => (IRdpdrDevice)new PortDevice(p))
, .. printers.Select((p, i) => (IRdpdrDevice)new PrinterDevice(p, i + 1))], _stop.Token);
        _rdpdrWorker = DrainRdpdrAsync();
    }

    private void OnChannelData(ChannelPayload payload)
    {
        if (payload.ChannelName == "cliprdr")
        {
            if (_clipboardFailure is not null) { return; }
            long queued = Interlocked.Add(ref _clipboardQueuedBytes, payload.Data.Length);
            if (queued > 32 * 1024 * 1024 || _cliprdrQueue?.Writer.TryWrite(payload.Data) != true)
            {
                _ = Interlocked.Add(ref _clipboardQueuedBytes, -payload.Data.Length);
                FailClipboard(new IOException("RDP clipboard queue capacity exceeded."));
            }
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
                || _drdynvcQueue?.Writer.TryWrite(payload.Data) != true)
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
            await foreach (byte[] pdu in _drdynvcQueue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _ = Interlocked.Add(ref _drdynvcQueuedBytes, -pdu.Length);
                await _drdynvc.ProcessAsync(pdu).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _dynamicChannelFailure = new IOException("RDP dynamic-channel processing failed; reconnect the session.", ex);
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
            failure = DisconnectCause(failure ?? _dynamicChannelFailure, Volatile.Read(ref _errorInfo));
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
        if (_pump is { } pump) { try { await pump.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_keepAlive is { } keepAlive) { try { await keepAlive.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_cliprdrWorker is { } worker) { try { await worker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_rdpdrWorker is { } driveWorker) { try { await driveWorker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_drdynvcWorker is { } dvcWorker) { try { await dvcWorker.ConfigureAwait(false); } catch { /* ignore */ } }
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
