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
        // Downgrade protection for Auto: once a host has logged on with NLA, later connections to it require NLA.
        // The X.224 negotiation is unauthenticated, so without this a man-in-the-middle could answer "TLS only" and
        // — if the user accepted its certificate — receive the password in the Client Info. A host that never used
        // NLA (e.g. one that refuses our NTLM) keeps the TLS fallback.
        string nlaKey = NlaKey(request);
        bool pinned = settings.Security == RdpSecurity.Auto && await UsedNlaBeforeAsync(nlaKey, ct).ConfigureAwait(false);
        try
        {
            IRdpSession session = await inner(request, pinned ? settings with { Security = RdpSecurity.Nla } : settings, loc, ct).ConfigureAwait(false);
            if (session is RdpManagedSession { NlaRefusal: { } refusal })
            {
                _context.Log.Warn($"RDP {request.Host}:{request.Port}: the server refused NLA before any credentials were sent " +
                    $"({refusal.Detail}{(refusal.Auth is { } a ? "; " + a : "")}); connected with TLS security instead (security layer: Auto).");
            }
            else if (!pinned && settings.Security == RdpSecurity.Auto && session is RdpManagedSession { Security: RdpSecurity.Nla })
            {
                try { await _context.Storage.SetAsync(nlaKey, true, ct).ConfigureAwait(false); }
                catch (Exception storeError) when (storeError is not OperationCanceledException) { _context.Log.Warn("Could not record the RDP host's NLA use.", storeError); }
            }
            return session;
        }
        catch (Exception ex) when (pinned && ex.InnerException is RdpNlaUnavailableException or RdpNlaRefusedException)
        {
            _context.Log.Warn($"RDP {request.Host}:{request.Port}: this host used NLA before but now offers no NLA — refused as a possible downgrade attack.", ex);
            throw new ProtocolConnectionException(loc.Format("NlaDowngrade", request.Host), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProtocolCertificateTrustException)
        {
            _context.Log.Warn($"RDP connection to {request.Host}:{request.Port} failed: {ex.Message}", ex);
            throw;
        }
    };

    private static string NlaKey(WorkspaceConnectRequest request) => $"nla-host:{request.Host.ToLowerInvariant()}:{request.Port}";

    private async Task<bool> UsedNlaBeforeAsync(string key, CancellationToken ct)
    {
        try { return await _context.Storage.GetAsync<bool>(key, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }
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
            RdpSettings settings = await Task.Run(() => RdpSettings.From(request, Loc), linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            IRdpSession session = await _factory(request, settings, Loc, linked.Token).ConfigureAwait(false);
            RdpWorkspaceDocument? document = null;
            lock (_gate)
            {
                if (!_disposed && !linked.IsCancellationRequested)
                {
                    document = new(session, request, settings, _context, _loc, _factory, Closed, _shutdown.Token);
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
    private Task<IRdpSession> DefaultFactory(WorkspaceConnectRequest request, RdpSettings settings, Loc loc, CancellationToken ct) =>
        RdpConnector.ConnectAsync(request, settings, loc, ct, message => _context.Log.Info(message));

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
