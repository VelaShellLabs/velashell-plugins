using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal delegate Task<IRdpSession> RdpSessionFactory(WorkspaceConnectRequest request, RdpSettings settings, Loc loc, CancellationToken ct);

internal sealed class RdpWorkspaceDocument : IWorkspaceDocument
{
    private readonly IPluginContext _context;
    private readonly RdpSettings _settings;
    private readonly RdpSessionFactory _factory;
    private readonly Action<RdpWorkspaceDocument> _closed;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _disposeGate = new();
    private WorkspaceConnectRequest? _request;
    private IRdpSession? _session;
    private Action<Exception?>? _disconnectedHandler;
    private Action<Exception>? _audioFailedHandler;
    private Exception? _audioError;
    private ContentControl? _root;
    private RdpWorkspaceView? _view;
    private Task? _disposeTask;
    private Loc _loc;
    private WorkspaceStatus _status = new(ProtocolSessionState.Connected);
    private int _disposed;

    internal RdpWorkspaceDocument(IRdpSession session, WorkspaceConnectRequest request, RdpSettings settings,
        IPluginContext context, Loc loc, RdpSessionFactory factory, Action<RdpWorkspaceDocument> closed, CancellationToken lifetime)
    {
        _session = session;
        _request = request;
        _settings = settings;
        _context = context;
        _loc = loc;
        _factory = factory;
        _closed = closed;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        AttachSession(session);
    }
    public WorkspaceStatus Status { get { lock (_disposeGate) { return _status; } } }
    public event EventHandler<WorkspaceStatus>? StatusChanged;
    public object CreateView()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_root is not null)
        {
            return _root;
        }

        _root = new ContentControl();
        BindView();
        return _root;
    }
    private void BindView()
    {
        if (_root is null || _session is null || _disposed != 0)
        {
            return;
        }

        _view = new(_session, _context, _settings, _loc) { Reconnect = ReconnectAsync };
        _view.SetConnected(Status.State == ProtocolSessionState.Connected);
        _view.SetAudioError(_audioError?.Message);
        _root.Content = _view;
        // Reconnected while the tab is in the background (the host keeps an inactive tab's view out of the visual
        // tree): pause display updates until it is shown, like the view it replaces.
        if (TopLevel.GetTopLevel(_root) is null)
        {
            _view.Desktop.PauseUntilShown();
        }
    }
    internal void SetLocale(Loc loc)
    {
        lock (_disposeGate)
        {
            _loc = loc;
            if (_status.State == ProtocolSessionState.Faulted)
            {
                SetStatus(_status with { Message = loc["Disconnected"] });
            }
            else if (_status.State == ProtocolSessionState.Connected && _audioError is { } error)
            {
                SetStatus(_status with { Message = loc.Format("AudioFailed", error.Message) });
            }
        }
        if (_root is not null)
        {
            Dispatcher.UIThread.Post(() => { if (_disposed == 0) { _view?.ApplyLocale(loc); } });
        }
    }
    private void AttachSession(IRdpSession session)
    {
        lock (_disposeGate)
        {
            _session = session;
            _audioError = null;
            _attachedAt = Environment.TickCount64;
            _disconnectedHandler = failure => OnDisconnected(session, failure);
            session.Disconnected += _disconnectedHandler;
            SetStatus(new(session.IsConnected ? ProtocolSessionState.Connected : ProtocolSessionState.Faulted), session);
        }
        if (session is IRdpAudioSource audio)
        {
            _audioFailedHandler = failure => OnAudioFailed(session, failure);
            audio.AudioPlaybackFailed += _audioFailedHandler;
        }
    }
    private void OnAudioFailed(IRdpSession session, Exception failure)
    {
        lock (_disposeGate)
        {
            if (_disposed != 0 || !ReferenceEquals(_session, session) || !session.IsConnected) { return; }
            _audioError = failure;
            SetStatus(new(ProtocolSessionState.Connected, _loc.Format("AudioFailed", failure.Message)), session);
        }
        _context.Log.Warn($"RDP audio playback stopped: {failure.Message}");
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed == 0 && ReferenceEquals(_session, session)) { _view?.SetAudioError(failure.Message); }
        });
    }
    private void OnDisconnected(IRdpSession session, Exception? failure)
    {
        if (_disposed != 0)
        {
            return;
        }

        if (failure is RdpConnectionLostException lost && AutoReconnectDelays.Length > 0)
        {
            // The network dropped, not the server: the remote Windows session waits in the disconnected state, so
            // reconnect on our own like mstsc instead of leaving a dead tab for the user to find.
            _context.Log.Warn($"RDP {_request?.Host}:{_request?.Port} connection lost ({lost.Message.TrimEnd('.')}); reconnecting automatically.");
            StartAutoReconnect(session);
            return;
        }

        string? reason = RdpConnector.DisconnectReason(failure, _loc);
        if (reason is not null)
        {
            // Whether a Windows session actually started before the drop bisects the cause: logon reached =>
            // the server ended an established session (policy); not reached => it never got that far.
            string logon = session is RdpManagedSession managed
                ? managed.ReachedLogon ? $"; a Windows logon was reached (Save Session Info type {managed.LogonInfoType})" : "; no Windows logon was reached"
                : "";
            _context.Log.Warn($"RDP {_request?.Host}:{_request?.Port} disconnected: {failure!.Message}{logon}");
        }
        else if (failure is not null)
        {
            _context.Log.Warn($"RDP {_request?.Host}:{_request?.Port} disconnected: {failure.GetType().Name}: {failure.Message}");
        }

        SetStatus(new(ProtocolSessionState.Faulted, reason ?? _loc["Disconnected"]), session);
        if (_root is not null)
        {
            Dispatcher.UIThread.Post(() =>
        {
            if (_disposed == 0 && ReferenceEquals(_session, session))
            {
                _view?.SetConnected(false, reason);
            }
        });
        }
    }
    private void SetStatus(WorkspaceStatus status, IRdpSession? source = null)
    {
        lock (_disposeGate)
        {
            if (source is not null && !ReferenceEquals(_session, source))
            {
                return;
            }

            _status = status;
        }
        try { StatusChanged?.Invoke(this, status); }
        catch { /* Host observers must not prevent connection cleanup. */ }
    }
    public Task ReconnectAsync(CancellationToken cancellationToken = default) => ReconnectCoreAsync(null, cancellationToken);

    /// <summary>Waits before each automatic attempt; the last one repeats until <see cref="AutoReconnectAttempts"/>.</summary>
    internal TimeSpan[] AutoReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>Automatic attempts before the tab falls back to the manual Reconnect button (about 8 minutes of waiting).</summary>
    internal int AutoReconnectAttempts { get; set; } = 20;

    /// <summary>A session that lasts this long starts a fresh series of attempts the next time it drops.</summary>
    internal TimeSpan AutoReconnectStableAfter { get; set; } = TimeSpan.FromMinutes(1);

    private CancellationTokenSource? _autoCancel;
    private Task? _autoReconnect;
    private int _autoAttempt;
    private long _attachedAt;

    /// <summary>The running automatic reconnect, if any (tests).</summary>
    internal Task? AutoReconnectTask { get { lock (_disposeGate) { return _autoReconnect; } } }

    private void StartAutoReconnect(IRdpSession lost)
    {
        CancellationTokenSource? superseded;
        lock (_disposeGate)
        {
            if (_disposed != 0)
            {
                return;
            }

            // Dropped again soon after reconnecting: continue that series instead of starting over, so a server or
            // path that keeps failing gives up after the attempts rather than looping forever.
            if (Environment.TickCount64 - _attachedAt >= (long)AutoReconnectStableAfter.TotalMilliseconds)
            {
                _ = Interlocked.Exchange(ref _autoAttempt, 0);
            }

            // A series still waiting for an older session (the user reconnected meanwhile and the new one dropped too)
            // would only see its session replaced and stop; this one takes over. The old source is cancelled, not
            // disposed: its loop may still be reading the token.
            superseded = _autoCancel;
            _autoCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            CancellationToken token = _autoCancel.Token;
            _autoReconnect = Task.Run(() => AutoReconnectAsync(lost, token));
        }

        superseded?.Cancel();
    }

    private async Task AutoReconnectAsync(IRdpSession lost, CancellationToken ct)
    {
        Exception? last = null;
        while (!ct.IsCancellationRequested)
        {
            int attempt = Interlocked.Increment(ref _autoAttempt);
            if (attempt > AutoReconnectAttempts)
            {
                break;
            }

            ShowDisconnected(lost, _loc.Format("AutoReconnecting", attempt, AutoReconnectAttempts));
            try
            {
                await Task.Delay(AutoReconnectDelays[Math.Min(attempt, AutoReconnectDelays.Length) - 1], ct).ConfigureAwait(false);
                if (await ReconnectCoreAsync(lost, ct).ConfigureAwait(false))
                {
                    _context.Log.Info($"RDP {_request?.Host}:{_request?.Port} reconnected automatically (attempt {attempt}).");
                }

                return;   // reconnected — by this attempt, or by the user / host in the meantime
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (_disposed != 0) { return; }
            catch (Exception ex) when (ex is ProtocolAuthenticationException or ProtocolCertificateTrustException)
            {
                last = ex;   // the password or the certificate changed: only the user can resolve that
                break;
            }
            catch (Exception ex) { last = ex; }   // the factory logged it; try again after the next delay
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        _context.Log.Warn($"RDP {_request?.Host}:{_request?.Port}: automatic reconnect gave up after {Math.Min(_autoAttempt, AutoReconnectAttempts)} attempt(s): {last?.Message}");
        ShowDisconnected(lost, last is ProtocolAuthenticationException or ProtocolCertificateTrustException ? last.Message : _loc["AutoReconnectFailed"]);
    }

    /// <summary>The disconnected card and status for <paramref name="session"/>, unless it has been replaced since.</summary>
    private void ShowDisconnected(IRdpSession session, string message)
    {
        SetStatus(new(ProtocolSessionState.Faulted, message), session);
        if (_root is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed == 0 && ReferenceEquals(_session, session))
                {
                    _view?.SetConnected(false, message);
                }
            });
        }
    }

    /// <summary>
    /// Replaces the session. With <paramref name="expected"/> (an automatic attempt) it only does so while that lost
    /// session is still current, and returns false when the user or the host already reconnected.
    /// </summary>
    private async Task<bool> ReconnectCoreAsync(IRdpSession? expected, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (expected is not null && !ReferenceEquals(_session, expected))
            {
                return false;
            }

            // Establish the new session before tearing down the old one, so a failed attempt leaves
            // the current (disconnected) view in place — its in-view Reconnect button stays usable
            // instead of the workspace going blank.
            IRdpSession session = await _factory(_request!, _settings, _loc, linked.Token).ConfigureAwait(false);
            await CloseConnectionAsync().ConfigureAwait(false);
            AttachSession(session);
            if (_root is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(BindView);
            }

            return true;
        }
        finally { _ = _gate.Release(); }
    }
    private async Task CloseConnectionAsync()
    {
        IRdpSession? session;
        lock (_disposeGate) { session = _session; _session = null; }
        session?.Disconnected -= _disconnectedHandler;
        if (session is IRdpAudioSource audio) { audio.AudioPlaybackFailed -= _audioFailedHandler; }
        _audioFailedHandler = null;
        _disconnectedHandler = null;
        // Abort the transport before draining clipboard/input tasks that may be awaiting a server reply.
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (_view is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                RdpWorkspaceView? view = _view;
                _view = null;
                _root?.Content = null;
                if (view is not null)
                {
                    await view.DisposeAsync();
                }
            });
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
        // Cancelled with the lifetime; it gives up the gate (or never takes it) before the close below needs it.
        if (AutoReconnectTask is { } reconnecting)
        {
            try { await reconnecting.ConfigureAwait(false); } catch { /* reported by the loop itself */ }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseConnectionAsync().ConfigureAwait(false); }
        finally
        {
            _request = null;
            SetStatus(new(ProtocolSessionState.Closed, _loc["Closed"]));
            StatusChanged = null;
            _closed(this);
            _ = _gate.Release();
            lock (_disposeGate) { _autoCancel?.Dispose(); _autoCancel = null; }
            _lifetime.Dispose();
        }
    }
}
