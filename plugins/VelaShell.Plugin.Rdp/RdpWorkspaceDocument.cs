using VelaShell.Plugin.Rdp.Engine;
using VelaShell.Plugin.Rdp.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

/// <summary>Serializes replacement and teardown; UI creation is not part of connection lifetime.</summary>
internal sealed class RdpWorkspaceDocument : IWorkspaceDocument
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly IPluginContext _context;
    private readonly IRdpSessionFactory _factory;
    private readonly RdpConnectionOptions _options;
    private readonly RdpCredentials _credentials;
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource _reconnectEpoch = new();
    private readonly Action<RdpWorkspaceDocument> _closed;
    private IRdpSession? _session;
    private Action<RdpSessionStatus>? _sessionStatusHandler;
    private Loc _loc;
    private RdpSessionState _state = RdpSessionState.Connected;
    private string? _errorKey;
    private long _generation;
    private long _localeVersion = -1;
    private bool _disposed;
    private bool _viewCreated;
    private Task? _disposeTask;

    internal RdpWorkspaceDocument(IPluginContext context, IRdpSessionFactory factory, RdpConnectionOptions options,
        RdpCredentials credentials, IRdpSession session, RdpScaleMode scaleMode, string title, string endpoint,
        Loc loc, Action<RdpWorkspaceDocument> closed, CancellationToken lifetime)
    {
        _context = context;
        _factory = factory;
        _options = options;
        _credentials = credentials;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _loc = loc;
        _closed = closed;
        ScaleMode = scaleMode;
        Title = title;
        Endpoint = endpoint;
        lock (_sync)
        {
            Attach(session);
            Status = Describe();
        }
    }

    public IRdpSession? Session { get { lock (_sync) return _session; } }
    public Loc Loc { get { lock (_sync) return _loc; } }
    public string Title { get; }
    public string Endpoint { get; }
    public RdpScaleMode ScaleMode { get; }
    public WorkspaceStatus Status { get { lock (_sync) return field; }

        private set;
    }
    public event EventHandler<WorkspaceStatus>? StatusChanged;
    public event Action? SessionChanged;
    public event Action? LocaleChanged;

    // Open/Dispose-only callers never need to JIT this separate UI method.
    public object CreateView()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_viewCreated) throw new InvalidOperationException("RDP view is created only once.");
            _viewCreated = true;
        }
        return new RdpWorkspaceView(this);
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken lifetime;
        CancellationToken reconnectEpoch;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = _lifetime.Token;
            reconnectEpoch = _reconnectEpoch.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime, reconnectEpoch);
        _context.Shutdown.ThrowIfCancellationRequested();
        await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            ThrowIfClosed();
            SetState(RdpSessionState.Connecting);
            await RemoveSessionAsync().ConfigureAwait(false);
            IRdpSession? next = null;
            try
            {
                next = await _factory.ConnectAsync(_options, _credentials, linked.Token).ConfigureAwait(false);
                _context.Shutdown.ThrowIfCancellationRequested();
                linked.Token.ThrowIfCancellationRequested();
                if (next.Status.State != RdpSessionState.Connected) throw new ProtocolConnectionException(Loc["Rdp_Error"]);
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    Attach(next);
                    next = null;
                }
                Notify(SessionChanged);
                Publish();
            }
            finally
            {
                if (next is not null) await next.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            SetState(RdpSessionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            SetState(RdpSessionState.Faulted, ErrorKey(ex));
            throw RdpWorkspaceProvider.LocalizeFailure(ex, Loc);
        }
        finally { _operation.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationToken lifetime;
        CancellationTokenSource oldEpoch;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lifetime = _lifetime.Token;
            oldEpoch = _reconnectEpoch;
            _reconnectEpoch = new();
        }
        // An explicit disconnect interrupts active/queued reconnects before waiting for their gate.
        try { await oldEpoch.CancelAsync().ConfigureAwait(false); }
        catch { _context.Log.Warn("An RDP cancellation subscriber failed."); }
        finally { oldEpoch.Dispose(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfClosed();
            await RemoveSessionAsync().ConfigureAwait(false);
            SetState(RdpSessionState.Disconnected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(RdpSessionState.Faulted, ErrorKey(ex));
            throw RdpWorkspaceProvider.LocalizeFailure(ex, Loc);
        }
        finally { _operation.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            // Cancellation callbacks and close callbacks must not run under the document monitor.
            _disposeTask = Task.Run(DisposeCoreAsync);
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch { _context.Log.Warn("An RDP cancellation subscriber failed."); }
        await _operation.WaitAsync().ConfigureAwait(false);
        try { await RemoveSessionAsync().ConfigureAwait(false); }
        finally
        {
            _credentials.Dispose();
            _lifetime.Dispose();
            _reconnectEpoch.Dispose();
            SetState(RdpSessionState.Closed);
            _closed(this);
            lock (_sync)
            {
                StatusChanged = null;
                SessionChanged = null;
                LocaleChanged = null;
            }
            _operation.Release();
        }
    }

    internal void UpdateLocale(Loc loc, long version = 0)
    {
        lock (_sync)
        {
            if (_disposed || version < _localeVersion) return;
            _localeVersion = version;
            _loc = loc;
            Status = Describe();
        }
        Publish();
        Notify(LocaleChanged);
    }

    private void ThrowIfClosed()
    {
        _context.Shutdown.ThrowIfCancellationRequested();
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    // Called only while no other operation can replace the session.
    private void Attach(IRdpSession session)
    {
        _session = session;
        long generation = ++_generation;
        _sessionStatusHandler = status =>
        {
            lock (_sync)
            {
                if (_disposed || generation != _generation || !ReferenceEquals(_session, session)) return;
                _state = status.State;
                // Never display a native/third-party exception string in status text.
                _errorKey = status.State == RdpSessionState.Faulted ? "Rdp_Error" : null;
                Status = Describe();
            }
            Publish();
        };
        session.StatusChanged += _sessionStatusHandler;
        // Subscribe then read: a disconnect between factory return and subscription is not lost.
        RdpSessionStatus current = session.Status;
        _state = current.State;
        _errorKey = current.State == RdpSessionState.Faulted ? "Rdp_Error" : null;
        Status = Describe();
    }

    private async Task RemoveSessionAsync()
    {
        IRdpSession? old;
        Action<RdpSessionStatus>? handler;
        lock (_sync)
        {
            old = _session;
            _session = null;
            ++_generation;
            handler = _sessionStatusHandler;
            _sessionStatusHandler = null;
        }
        if (old is not null && handler is not null) old.StatusChanged -= handler;
        Notify(SessionChanged);
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
    }

    private void SetState(RdpSessionState state, string? errorKey = null)
    {
        lock (_sync)
        {
            if (_disposed && state != RdpSessionState.Closed) return;
            _state = state;
            _errorKey = errorKey;
            Status = Describe();
        }
        Publish();
    }

    private WorkspaceStatus Describe() => new(_state switch
    {
        RdpSessionState.Connected => ProtocolSessionState.Connected,
        RdpSessionState.Closed => ProtocolSessionState.Closed,
        _ => ProtocolSessionState.Faulted
    }, _loc[_errorKey ?? (_state switch
    {
        RdpSessionState.Connected => "Rdp_Connected",
        RdpSessionState.Connecting => "Rdp_Connecting",
        RdpSessionState.Disconnected => "Rdp_Disconnected",
        RdpSessionState.Closed => "Rdp_Closed",
        _ => "Rdp_Faulted"
    })]);

    private void Publish()
    {
        if (StatusChanged is not { } handlers) return;
        foreach (EventHandler<WorkspaceStatus> handler in handlers.GetInvocationList().Cast<EventHandler<WorkspaceStatus>>())
        {
            try { handler(this, Status); }
            catch { _context.Log.Warn("An RDP status subscriber failed."); }
        }
    }

    private void Notify(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch { _context.Log.Warn("An RDP document subscriber failed."); }
        }
    }

    private static string ErrorKey(Exception ex) => ex switch
    {
        ProtocolCertificateTrustException => "Rdp_CertificateReconnect",
        ProtocolAuthenticationException => "Rdp_AuthFailed",
        ProtocolUnsupportedException => "Rdp_Unsupported",
        TimeoutException => "Rdp_Timeout",
        _ => "Rdp_Error"
    };
}
