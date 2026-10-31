using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpWorkspaceProvider : IWorkspaceProvider, IAsyncDisposable
{
    private readonly IPluginContext _context;
    private readonly RdpSessionFactory _factory;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _hostShutdown;
    private readonly Lock _gate = new();
    private readonly HashSet<RdpWorkspaceDocument> _documents = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposeTask;
    private int _opens;
    private bool _disposed;
    private Loc _loc;

    internal RdpWorkspaceProvider(IPluginContext context, Loc loc, RdpSessionFactory? factory = null)
    {
        _context = context;
        _loc = loc;
        _factory = Logged(factory ?? DefaultFactory);
        _hostShutdown = context.Shutdown.Register(() => { _ = DisposeAsync().AsTask(); });
    }

    /// <summary>
    /// Logs every failed connect/reconnect with the full exception. The host only shows the message, so
    /// without this a TLS/NLA failure leaves nothing to diagnose from (the inner SChannel error and the
    /// stack live only on the exception). The certificate-trust prompt is the normal first-connect flow,
    /// not a failure; exceptions never carry the password.
    /// </summary>
    private RdpSessionFactory Logged(RdpSessionFactory inner) => async (request, settings, loc, ct) =>
    {
        try
        {
            IRdpSession session = await inner(request, settings, loc, ct).ConfigureAwait(false);
            if (session is RdpManagedSession { NlaRefusal: { } refusal })
            {
                _context.Log.Warn($"RDP {request.Host}:{request.Port}: the server refused NLA before any credentials were sent " +
                    $"({refusal.Detail}); connected with TLS security instead (security layer: Auto).");
            }
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProtocolCertificateTrustException)
        {
            _context.Log.Warn($"RDP connection to {request.Host}:{request.Port} failed: {ex.Message}", ex);
            throw;
        }
    };
    internal Loc Loc
    {
        get
        {
            lock (_gate)
            {
                return _loc;
            }
        }
        set
        {
            lock (_gate)
            {
                _loc = value;
                foreach (RdpWorkspaceDocument document in _documents)
                {
                    document.SetLocale(value);
                }
            }
        }
    }
    public async Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _opens++; }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var settings = RdpSettings.From(request, Loc);
            IRdpSession session = await _factory(request, settings, Loc, linked.Token).ConfigureAwait(false);
            RdpWorkspaceDocument? document = null;
            lock (_gate)
            {
                if (!_disposed && !linked.IsCancellationRequested)
                {
                    document = new(session, request, settings, _context, _loc, _factory, _shutdown.Token, Closed);
                    _ = _documents.Add(document);
                }
            }
            if (document is not null)
            {
                return document;
            }

            await session.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(linked.Token);
        }
        finally { lock (_gate) { if (--_opens == 0 && _disposed) { _ = _drained.TrySetResult(); } } }
    }
    private static Task<IRdpSession> DefaultFactory(WorkspaceConnectRequest request, RdpSettings settings, Loc loc, CancellationToken ct) =>
        RdpConnector.ConnectAsync(request, settings, loc, ct);

    private void Closed(RdpWorkspaceDocument document)
    {
        lock (_gate)
        {
            _ = _documents.Remove(document);
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _shutdown.Cancel();
        if (_opens == 0)
        {
            _ = _drained.TrySetResult();
        }

        await _drained.Task.ConfigureAwait(false);
        RdpWorkspaceDocument[] documents;
        lock (_gate)
        {
            documents = [.. _documents];
        }

        await Task.WhenAll(documents.Select(document => document.DisposeAsync().AsTask())).ConfigureAwait(false);
        _hostShutdown.Dispose();
        _shutdown.Dispose();
    }
}
