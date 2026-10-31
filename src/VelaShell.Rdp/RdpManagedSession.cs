using System.Threading.Channels;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Mcs;

namespace VelaShell.Rdp;

/// <summary>
/// Drives a pure-managed <see cref="RdpConnection"/> behind the <see cref="IRdpSession"/> contract:
/// a single background task pumps the connection while frames are coalesced and snapshotted on
/// demand, and input, clipboard and dynamic-resolution requests are forwarded to the wire.
/// </summary>
public sealed class RdpManagedSession : IRdpSession
{
    private readonly RdpConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private int _dirty;
    private int _isConnected;
    private int _disposed;
    private uint _errorInfo;
    private CliprdrChannel? _cliprdr;
    private Channel<byte[]>? _cliprdrQueue;
    private Task? _cliprdrWorker;
    private RdpdrChannel? _rdpdr;
    private Channel<byte[]>? _rdpdrQueue;
    private Task? _rdpdrWorker;
    private DrdynvcChannel? _drdynvc;
    private Channel<byte[]>? _drdynvcQueue;
    private Task? _drdynvcWorker;

    public bool IsConnected => Volatile.Read(ref _isConnected) != 0;
    public event Action? FrameAvailable;
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
        _connection = connection;
        _connection.FrameUpdated += OnFrameUpdated;
        _connection.ErrorInfoReceived += code => Volatile.Write(ref _errorInfo, code);
        _connection.ChannelData += OnChannelData;
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
    public RdpSecurity Security => _connection.Security;

    /// <summary>
    /// Set when <see cref="RdpSecurity.Auto"/> fell back to TLS because the server refused NLA before any
    /// credentials were sent; carries the server's reason. <see langword="null" /> otherwise.
    /// </summary>
    public RdpNlaRefusedException? NlaRefusal { get; private set; }

    private static async Task<RdpManagedSession> ConnectOnceAsync(RdpClientOptions options, RdpSecurity security, CancellationToken ct)
    {
        var channels = new List<ChannelDef>();
        if (options.Clipboard)
        {
            channels.Add(new ChannelDef("cliprdr", 0xC0A00000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP|SHOW_PROTOCOL
        }

        if (options.Drives.Count > 0)
        {
            channels.Add(new ChannelDef("rdpdr", 0xC0800000)); // INITIALIZED|ENCRYPT_RDP|COMPRESS_RDP
        }

        channels.Add(new ChannelDef("drdynvc", 0xC0800000)); // dynamic virtual channels: display-control resize
        var connectOptions = new RdpConnectOptions(options.Host, options.Port, options.Username, options.Password,
            options.Domain, options.Width, options.Height, options.ColorDepth, options.CertificatePin,
            string.IsNullOrEmpty(options.CertificateHost) ? options.Host : options.CertificateHost, channels, security);

        var session = new RdpManagedSession(new RdpConnection(connectOptions));
        session.EnableDynamicChannels();
        if (options.Clipboard)
        {
            session.EnableClipboard();
        }

        if (options.Drives.Count > 0)
        {
            session.EnableDrives(options.Drives);
        }

        try
        {
            await session._connection.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RdpConnectStage stage = session._connection.Stage;
            System.Security.Authentication.SslProtocols? tls = session._connection.TlsProtocol;
            string? tlsTrace = session._connection.TlsTrace;
            await session.DisposeAsync().ConfigureAwait(false);
            // Cancellation, certificate trust and rejected credentials each mean something specific to
            // the caller; everything else gets the failing stage and a readable cause chain.
            if (ex is OperationCanceledException or RdpCertificateRejectedException or RdpAuthenticationException)
            {
                throw;
            }

            if (ex is RdpNlaRefusedException refused)
            {
                throw refused.WithTlsTrace(tlsTrace);
            }

            throw new RdpConnectionException(stage, ex, tls, tlsTrace);
        }
        Volatile.Write(ref session._isConnected, 1);
        // PumpLoopAsync yields at the first ReadPduAsync, so this returns immediately; no Task.Run needed.
        session._pump = session.PumpLoopAsync();
        return session;
    }

    private void EnableDynamicChannels()
    {
        _drdynvcQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        _drdynvc = new DrdynvcChannel((pdu, token) => _connection.SendVirtualChannelAsync("drdynvc", pdu, token), _stop.Token);
        _drdynvcWorker = DrainDrdynvcAsync();
    }

    private void EnableClipboard()
    {
        _cliprdrQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        _cliprdr = new CliprdrChannel(
            send: pdu => _connection.SendVirtualChannelAsync("cliprdr", pdu, _stop.Token),
            raise: RaiseClipboard);
        _cliprdrWorker = DrainCliprdrAsync();
    }

    private void EnableDrives(IReadOnlyList<DriveMapping> mappings)
    {
        _rdpdrQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _rdpdr = new RdpdrChannel(pdu => _connection.SendVirtualChannelAsync("rdpdr", pdu, _stop.Token), mappings);
        _rdpdrWorker = DrainRdpdrAsync();
    }

    private void OnChannelData(ChannelPayload payload)
    {
        if (payload.ChannelName == "cliprdr")
        {
            _ = (_cliprdrQueue?.Writer.TryWrite(payload.Data));
        }
        else if (payload.ChannelName == "rdpdr")
        {
            _ = (_rdpdrQueue?.Writer.TryWrite(payload.Data));
        }
        else if (payload.ChannelName == "drdynvc")
        {
            _ = (_drdynvcQueue?.Writer.TryWrite(payload.Data));
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
                try { await _rdpdr.ProcessAsync(pdu).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch { /* a malformed rdpdr PDU must not drop the session */ }
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
                try { await _drdynvc.ProcessAsync(pdu).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch { /* a malformed dynamic-channel PDU must not drop the session */ }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    /// <summary>Asks the server, via the display-control channel, to resize the desktop to match the view.</summary>
    public Task RequestResizeAsync(int width, int height, CancellationToken cancellationToken) =>
        _drdynvc?.RequestResizeAsync(width, height) ?? Task.CompletedTask;

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
                try { await _cliprdr.ProcessAsync(pdu).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch { /* a malformed clipboard PDU must not drop the session */ }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
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

        (byte[] bgra, int width, int height) = _connection.SnapshotFrame();
        return new RdpFrame(width, height, bgra);
    }

    private async Task PumpLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_stop.IsCancellationRequested && await _connection.PumpAsync(_stop.Token).ConfigureAwait(false)) { }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            Volatile.Write(ref _isConnected, 0);
            // A clean end (the server dropped the session) after a Set Error Info PDU means the server told us
            // why: surface that as the disconnect cause so the reason is not lost (e.g. signed out, licence).
            uint errorInfo = Volatile.Read(ref _errorInfo);
            failure ??= errorInfo != 0 ? new RdpDisconnectException(errorInfo) : null;
            if (Volatile.Read(ref _disposed) == 0) { try { Disconnected?.Invoke(failure); } catch { /* ignore */ } }
        }
    }

    public Task KeyAsync(uint code, bool down, CancellationToken ct = default) =>
        Guard(() => _connection.SendScancodeAsync(code, down, ct));

    public Task UnicodeAsync(string text, CancellationToken ct = default) => Guard(async () =>
    {
        foreach (char unit in text)
        {
            await _connection.SendUnicodeAsync(unit, down: true, ct).ConfigureAwait(false);
            await _connection.SendUnicodeAsync(unit, down: false, ct).ConfigureAwait(false);
        }
    });

    public Task MouseAsync(uint flags, uint x, uint y, CancellationToken ct = default) =>
        Guard(() => _connection.SendMouseAsync(flags, x, y, ct));

    // ---- Clipboard transport → cliprdr channel (inert when clipboard is disabled) ----
    public Task OfferFormatsAsync(string formats, CancellationToken cancellationToken) => _cliprdr?.OfferFormatsAsync(formats) ?? Task.CompletedTask;
    public Task RequestFormatAsync(uint format, CancellationToken cancellationToken) => _cliprdr?.RequestFormatAsync(format) ?? Task.CompletedTask;
    public Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken) => _cliprdr?.RespondFormatAsync(data, success) ?? Task.CompletedTask;
    public Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken) => _cliprdr?.RequestFileAsync(stream, index, flags, offset, length) ?? Task.CompletedTask;
    public Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken) => _cliprdr?.RespondFileAsync(stream, data, success) ?? Task.CompletedTask;

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
        if (_cliprdrWorker is { } worker) { try { await worker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_rdpdrWorker is { } driveWorker) { try { await driveWorker.ConfigureAwait(false); } catch { /* ignore */ } }
        if (_drdynvcWorker is { } dvcWorker) { try { await dvcWorker.ConfigureAwait(false); } catch { /* ignore */ } }
        _rdpdr?.CloseAll();
        await _connection.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        FrameAvailable = null;
        Disconnected = null;
        lock (_clipGate)
        {
            _clipboardHandlers = null;
        }
    }
}
