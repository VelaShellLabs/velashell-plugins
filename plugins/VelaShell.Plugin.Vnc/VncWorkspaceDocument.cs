using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

internal delegate Task<IVncSession> VncSessionFactory(WorkspaceConnectRequest request, VncSettings settings, Loc loc, CancellationToken ct);

/// <summary>
/// One open VNC tab: owns the session and its view. A dropped network connection reconnects automatically (the remote
/// desktop is still there); a server that closed the connection on purpose is not reconnected to — with a non-shared
/// viewer elsewhere that would start a tug of war — and the card offers Reconnect instead.
/// </summary>
internal sealed class VncWorkspaceDocument : IWorkspaceDocument
{
    private readonly IPluginContext _context;
    private readonly VncSettings _settings;
    private readonly VncSessionFactory _factory;
    private readonly Action<VncWorkspaceDocument> _closed;
    private readonly CancellationTokenSource _lifetime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();
    private WorkspaceConnectRequest? _request;
    private IVncSession? _session;
    private Action<Exception?>? _disconnectedHandler;
    private Func<Loc, string>? _faultMessage;
    private ContentControl? _root;
    private VncWorkspaceView? _view;
    private Task? _disposeTask;
    private Loc _loc;
    private WorkspaceStatus _status = new(ProtocolSessionState.Connected);
    private CancellationTokenSource? _autoCancel;
    private int _autoAttempt;
    private long _attachedAt;
    private int _disposed;

    internal VncWorkspaceDocument(IVncSession session, WorkspaceConnectRequest request, VncSettings settings,
        IPluginContext context, Loc loc, VncSessionFactory factory, Action<VncWorkspaceDocument> closed, CancellationToken lifetime)
    {
        _request = request;
        _settings = settings;
        _context = context;
        _loc = loc;
        _factory = factory;
        _closed = closed;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        AttachSession(session);
    }

    public WorkspaceStatus Status { get { lock (_stateGate) { return _status; } } }
    public event EventHandler<WorkspaceStatus>? StatusChanged;

    /// <summary>Waits before each automatic attempt; the last one repeats until <see cref="AutoReconnectAttempts"/>.</summary>
    internal TimeSpan[] AutoReconnectDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    internal int AutoReconnectAttempts { get; set; } = 20;

    /// <summary>A session that lasts this long starts a fresh series of attempts the next time it drops.</summary>
    internal TimeSpan AutoReconnectStableAfter { get; set; } = TimeSpan.FromMinutes(1);

    internal Task? AutoReconnectTask
    {
        get { lock (_stateGate) { return field; } }
        private set;
    }

    public object CreateView()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_root is not null) { return _root; }
        _root = new ContentControl();
        BindView();
        return _root;
    }

    private void BindView()
    {
        if (_root is null || _session is null || _disposed != 0) { return; }
        _view = new(_session, _context, _settings, _loc) { Reconnect = ReconnectAsync };
        _view.SetConnected(Status.State == ProtocolSessionState.Connected);
        _root.Content = _view;
        if (TopLevel.GetTopLevel(_root) is null)
        {
            _view.Desktop.PauseUntilShown();
        }
    }

    internal void SetLocale(Loc loc)
    {
        WorkspaceStatus? relocalized = null;
        lock (_stateGate)
        {
            _loc = loc;
            if (_status.State == ProtocolSessionState.Faulted)
            {
                relocalized = _status with { Message = _faultMessage?.Invoke(loc) ?? loc["Disconnected"] };
            }
        }

        if (relocalized is { } status) { SetStatus(status); }
        if (_root is not null)
        {
            Dispatcher.UIThread.Post(() => { if (_disposed == 0) { _view?.ApplyLocale(loc); } });
        }
    }

    private void AttachSession(IVncSession session)
    {
        lock (_stateGate)
        {
            _session = session;
            _faultMessage = null;
            _attachedAt = Environment.TickCount64;
            _disconnectedHandler = failure => OnDisconnected(session, failure);
            session.Disconnected += _disconnectedHandler;
        }
        SetStatus(new(session.IsConnected ? ProtocolSessionState.Connected : ProtocolSessionState.Faulted), session);
    }

    private void OnDisconnected(IVncSession session, Exception? failure)
    {
        if (_disposed != 0) { return; }
        if (failure is VncConnectionLostException lost && AutoReconnectDelays.Length > 0)
        {
            _context.Log.Warn($"VNC {_request?.Host}:{_request?.Port} connection lost ({lost.Message.TrimEnd('.')}); reconnecting automatically.");
            StartAutoReconnect(session);
            return;
        }

        _context.Log.Warn($"VNC {_request?.Host}:{_request?.Port} disconnected: {(failure is null ? "closed by the server" : failure.GetType().Name + ": " + failure.Message)}");
        ShowDisconnected(session, loc => VncConnector.DisconnectReason(failure, loc) ?? loc["Disconnected"]);
    }

    private void SetStatus(WorkspaceStatus status, IVncSession? source = null)
    {
        lock (_stateGate)
        {
            if (source is not null && !ReferenceEquals(_session, source)) { return; }
            _status = status;
        }
        try { StatusChanged?.Invoke(this, status); }
        catch { /* host observers must not prevent connection cleanup */ }
    }

    public Task ReconnectAsync(CancellationToken cancellationToken = default) => ReconnectCoreAsync(null, cancellationToken);

    private void StartAutoReconnect(IVncSession lost)
    {
        CancellationTokenSource? superseded;
        lock (_stateGate)
        {
            if (_disposed != 0) { return; }
            if (Environment.TickCount64 - _attachedAt >= (long)AutoReconnectStableAfter.TotalMilliseconds)
            {
                _ = Interlocked.Exchange(ref _autoAttempt, 0);
            }
            superseded = _autoCancel;
            _autoCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            CancellationToken token = _autoCancel.Token;
            AutoReconnectTask = Task.Run(() => AutoReconnectAsync(lost, token));
        }
        superseded?.Cancel();
    }

    private async Task AutoReconnectAsync(IVncSession lost, CancellationToken ct)
    {
        Exception? last = null;
        while (!ct.IsCancellationRequested)
        {
            int attempt = Interlocked.Increment(ref _autoAttempt);
            if (attempt > AutoReconnectAttempts) { break; }
            int shown = attempt;
            ShowDisconnected(lost, loc => loc.Format("AutoReconnecting", shown, AutoReconnectAttempts));
            try
            {
                await Task.Delay(AutoReconnectDelays[Math.Min(attempt, AutoReconnectDelays.Length) - 1], ct).ConfigureAwait(false);
                if (await ReconnectCoreAsync(lost, ct).ConfigureAwait(false))
                {
                    _context.Log.Info($"VNC {_request?.Host}:{_request?.Port} reconnected automatically (attempt {attempt}).");
                }
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) when (_disposed != 0) { return; }
            catch (Exception ex) when (ex is ProtocolAuthenticationException or ProtocolCertificateTrustException)
            {
                last = ex;   // only the user can resolve a changed password or certificate
                break;
            }
            catch (Exception ex) { last = ex; }
        }

        if (ct.IsCancellationRequested) { return; }
        _context.Log.Warn($"VNC {_request?.Host}:{_request?.Port}: automatic reconnect gave up after {Math.Min(_autoAttempt, AutoReconnectAttempts)} attempt(s): {last?.Message}");
        Exception? final = last;
        ShowDisconnected(lost, loc => final is ProtocolAuthenticationException or ProtocolCertificateTrustException ? final.Message : loc["AutoReconnectFailed"]);
    }

    private void ShowDisconnected(IVncSession session, Func<Loc, string> wording)
    {
        string message;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_session, session)) { return; }
            _faultMessage = wording;
            message = wording(_loc);
        }

        SetStatus(new(ProtocolSessionState.Faulted, message), session);
        if (_root is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed == 0 && ReferenceEquals(_session, session)) { _view?.SetConnected(false, message); }
            });
        }
    }

    /// <summary>
    /// Replaces the session. With <paramref name="expected"/> (an automatic attempt) it only does so while that lost session
    /// is still current, and returns false when the user or the host already reconnected.
    /// </summary>
    private async Task<bool> ReconnectCoreAsync(IVncSession? expected, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (expected is not null && !ReferenceEquals(_session, expected)) { return false; }
            // Connect first, so a failed attempt leaves the current (disconnected) view and its Reconnect button in place.
            IVncSession session = await _factory(_request!, _settings, _loc, linked.Token).ConfigureAwait(false);
            try { await CloseConnectionAsync().ConfigureAwait(false); }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            AttachSession(session);
            if (expected is null) { _ = Interlocked.Exchange(ref _autoAttempt, 0); }
            if (_root is not null) { await Dispatcher.UIThread.InvokeAsync(BindView); }
            return true;
        }
        finally { _ = _gate.Release(); }
    }

    private async Task CloseConnectionAsync()
    {
        IVncSession? session;
        lock (_stateGate) { session = _session; _session = null; }
        session?.Disconnected -= _disconnectedHandler;
        _disconnectedHandler = null;
        if (session is not null) { await session.DisposeAsync().ConfigureAwait(false); }
        if (_view is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                VncWorkspaceView? view = _view;
                _view = null;
                _root?.Content = null;
                if (view is not null) { await view.DisposeAsync(); }
            });
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateGate) { return new(_disposeTask ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
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
            lock (_stateGate) { _autoCancel?.Dispose(); _autoCancel = null; }
            _lifetime.Dispose();
        }
    }
}
