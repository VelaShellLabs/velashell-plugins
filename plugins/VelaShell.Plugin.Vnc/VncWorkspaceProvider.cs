using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

internal sealed class VncWorkspaceProvider : IWorkspaceProvider, IAsyncDisposable
{
    private readonly IPluginContext _context;
    private readonly VncSessionFactory _factory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _hostShutdown;
    private readonly Lock _gate = new();
    private readonly HashSet<VncWorkspaceDocument> _documents = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    private int _opens;
    private bool _disposed;
    private Loc _loc;

    internal VncWorkspaceProvider(IPluginContext context, Loc loc, VncSessionFactory? factory = null)
    {
        _context = context;
        _loc = loc;
        _factory = Logged(factory ?? DefaultFactory);
        _hostShutdown = context.Shutdown.Register(() => { _ = DisposeAsync().AsTask(); });
    }

    /// <summary>
    /// Logs every failed connect with the full exception: the host only shows the message. The certificate prompt is the
    /// normal first-connect flow for a TLS server, not a failure; exceptions never carry the password.
    /// </summary>
    private VncSessionFactory Logged(VncSessionFactory inner) => async (request, settings, loc, ct) =>
    {
        try
        {
            return await inner(request, settings, loc, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProtocolCertificateTrustException)
        {
            _context.Log.Warn($"VNC connection to {request.Host}:{request.Port} failed: {ex.Message}", ex);
            throw;
        }
    };

    internal Loc Loc
    {
        get { lock (_gate) { return _loc; } }
        set
        {
            VncWorkspaceDocument[] documents;
            lock (_gate)
            {
                _loc = value;
                documents = [.. _documents];
            }
            // Outside the lock: SetLocale raises StatusChanged into host code.
            foreach (VncWorkspaceDocument document in documents) { document.SetLocale(value); }
        }
    }

    public async Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _opens++; }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var settings = VncSettings.From(request, Loc);
            IVncSession session = await _factory(request, settings, Loc, linked.Token).ConfigureAwait(false);
            VncWorkspaceDocument? document = null;
            lock (_gate)
            {
                if (!_disposed && !linked.IsCancellationRequested)
                {
                    document = new(session, request, settings, _context, _loc, _factory, Closed, _shutdown.Token);
                    _ = _documents.Add(document);
                }
            }
            if (document is not null) { return document; }
            await session.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(linked.Token);
        }
        finally { lock (_gate) { if (--_opens == 0 && _disposed) { _ = _drained.TrySetResult(); } } }
    }

    private Task<IVncSession> DefaultFactory(WorkspaceConnectRequest request, VncSettings settings, Loc loc, CancellationToken ct) =>
        VncConnector.ConnectAsync(request, settings, loc, ct, message => _context.Log.Info(message), _context.Network);

    private void Closed(VncWorkspaceDocument document)
    {
        lock (_gate) { _ = _documents.Remove(document); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_disposeTask ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            if (_opens == 0) { _ = _drained.TrySetResult(); }
        }
        _shutdown.Cancel();
        await _drained.Task.ConfigureAwait(false);
        VncWorkspaceDocument[] documents;
        lock (_gate) { documents = [.. _documents]; }
        await Task.WhenAll(documents.Select(static document => document.DisposeAsync().AsTask())).ConfigureAwait(false);
        _hostShutdown.Dispose();
        _shutdown.Dispose();
    }
}
