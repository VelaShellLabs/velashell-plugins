using Avalonia.Controls;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

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
    private readonly object _disposeGate = new();
    private WorkspaceConnectRequest? _request;
    private IRdpSession? _session;
    private Action<Exception?>? _disconnectedHandler;
    private ContentControl? _root;
    private RdpWorkspaceView? _view;
    private Task? _disposeTask;
    private Loc _loc;
    private WorkspaceStatus _status = new(ProtocolSessionState.Connected);
    private int _disposed;

    internal RdpWorkspaceDocument(IRdpSession session, WorkspaceConnectRequest request, RdpSettings settings,
        IPluginContext context, Loc loc, RdpSessionFactory factory, CancellationToken lifetime, Action<RdpWorkspaceDocument> closed)
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
    public WorkspaceStatus Status { get { lock (_disposeGate) return _status; } }
    public event EventHandler<WorkspaceStatus>? StatusChanged;
    public object CreateView()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_root is not null) return _root;
        _root = new ContentControl();
        BindView();
        return _root;
    }
    private void BindView()
    {
        if (_root is null || _session is null || _disposed != 0) return;
        _view = new(_session, _context, _settings, _loc);
        _view.SetConnected(Status.State == ProtocolSessionState.Connected);
        _root.Content = _view;
    }
    internal void SetLocale(Loc loc)
    {
        lock (_disposeGate)
        {
            _loc = loc;
            if (_status.State == ProtocolSessionState.Faulted) SetStatus(_status with { Message = loc["Disconnected"] });
        }
        if (_root is not null) Dispatcher.UIThread.Post(() => { if (_disposed == 0) _view?.ApplyLocale(loc); });
    }
    private void AttachSession(IRdpSession session)
    {
        lock (_disposeGate)
        {
            _session = session;
            _disconnectedHandler = failure => OnDisconnected(session, failure);
            session.Disconnected += _disconnectedHandler;
            SetStatus(new(session.IsConnected ? ProtocolSessionState.Connected : ProtocolSessionState.Faulted), session);
        }
    }
    private void OnDisconnected(IRdpSession session, Exception? failure)
    {
        if (_disposed != 0) return;
        SetStatus(new(ProtocolSessionState.Faulted, _loc["Disconnected"]), session);
        if (_root is not null) Dispatcher.UIThread.Post(() =>
        {
            if (_disposed == 0 && ReferenceEquals(_session, session)) _view?.SetConnected(false);
        });
    }
    private void SetStatus(WorkspaceStatus status, IRdpSession? source = null)
    {
        lock (_disposeGate)
        {
            if (source is not null && !ReferenceEquals(_session, source)) return;
            _status = status;
        }
        try { StatusChanged?.Invoke(this, status); }
        catch { /* Host observers must not prevent connection cleanup. */ }
    }
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            await CloseConnectionAsync().ConfigureAwait(false);
            SetStatus(new(ProtocolSessionState.Faulted, _loc["Disconnected"]));
            IRdpSession session = await _factory(_request!, _settings, _loc, linked.Token).ConfigureAwait(false);
            AttachSession(session);
            if (_root is not null) await Dispatcher.UIThread.InvokeAsync(BindView);
        }
        finally { _gate.Release(); }
    }
    private async Task CloseConnectionAsync()
    {
        IRdpSession? session;
        lock (_disposeGate) { session = _session; _session = null; }
        if (session is not null) session.Disconnected -= _disconnectedHandler;
        _disconnectedHandler = null;
        // Abort the transport before draining clipboard/input tasks that may be awaiting a server reply.
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        if (_view is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                RdpWorkspaceView? view = _view;
                _view = null;
                if (_root is not null) _root.Content = null;
                if (view is not null) await view.DisposeAsync();
            });
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseConnectionAsync().ConfigureAwait(false); }
        finally
        {
            _request = null;
            SetStatus(new(ProtocolSessionState.Closed, _loc["Closed"]));
            StatusChanged = null;
            _closed(this);
            _gate.Release();
            _lifetime.Dispose();
        }
    }
}
