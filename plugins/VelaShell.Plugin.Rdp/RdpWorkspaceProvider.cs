using VelaShell.Plugin.Rdp.Engine;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Rdp;

/// <summary>Owns all documents and in-flight opens, including the no-view connection-test path.</summary>
internal sealed class RdpWorkspaceProvider : IWorkspaceProvider, IAsyncDisposable
{
    private readonly Lock _sync = new();
    private readonly IPluginContext _context;
    private readonly IRdpSessionFactory _factory;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _shutdown;
    private readonly HashSet<RdpWorkspaceDocument> _documents = [];
    private Loc _loc;
    private long _localeVersion;
    private int _opening;
    private TaskCompletionSource? _opensDrained;
    private Task? _disposeTask;
    private bool _disposed;

    public RdpWorkspaceProvider(IPluginContext context, Loc loc, IRdpSessionFactory? factory = null)
    {
        _context = context;
        _loc = loc;
        _factory = factory ?? RdpSessionFactory.Instance;
        _lifetime = new CancellationTokenSource();
        // Host cancellation runs callbacks synchronously; never run teardown on that caller.
        _shutdown = context.Shutdown.Register(() => _ = Task.Run(DisposeOnShutdownAsync));
    }

    public async Task<IWorkspaceDocument> OpenAsync(WorkspaceConnectRequest request, CancellationToken cancellationToken = default)
    {
        Loc loc;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _context.Shutdown.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
            if (_opening++ == 0) _opensDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            loc = _loc;
        }
        RdpCredentials? credentials = null;
        IRdpSession? session = null;
        RdpWorkspaceDocument? document = null;
        try
        {
            var settings = RdpSettings.From(request, loc);
            (string Username, string Domain) = settings.ParseAccount(request.Username, loc);
            RdpSettings.ValidatePassword(request.Password, loc);
            credentials = new(Username, request.Password, Domain);
            var options = new RdpConnectionOptions(request.Host, request.Port,
                request.Tunnel?.TargetHost ?? request.Host, request.Tunnel?.TargetPort ?? request.Port,
                settings.DesktopWidth, settings.DesktopHeight, TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs), settings.TrustedThumbprint)
            {
                RedirectClipboard = settings.RedirectClipboard,
                Drives = settings.Drives
            };
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _context.Shutdown.ThrowIfCancellationRequested();
            try { session = await _factory.ConnectAsync(options, credentials, linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw LocalizeFailure(ex, loc); }
            _context.Shutdown.ThrowIfCancellationRequested();
            linked.Token.ThrowIfCancellationRequested();
            if (session.Status.State != RdpSessionState.Connected)
                throw new ProtocolConnectionException(loc["Rdp_Error"]);
            // Retain only explicit endpoint/display fields, never the password-bearing request.
            string endpoint = request.Tunnel is { } tunnel
                ? $"{tunnel.TargetHost}:{tunnel.TargetPort} ↝ {tunnel.JumpDisplayName}"
                : $"{request.Host}:{request.Port}";
            string title = string.IsNullOrWhiteSpace(request.DisplayName) ? endpoint : request.DisplayName;
            document = new(_context, _factory, options, credentials, session, settings.ScaleMode, title, endpoint, loc, OnDocumentClosed, _lifetime.Token);
            credentials = null;
            session = null;
            long localeVersion;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _context.Shutdown.ThrowIfCancellationRequested();
                linked.Token.ThrowIfCancellationRequested();
                loc = _loc;
                localeVersion = _localeVersion;
                _documents.Add(document);
            }
            // Versioning prevents an older Open completion overwriting a newer locale event.
            document.UpdateLocale(loc, localeVersion);
            RdpWorkspaceDocument result = document;
            document = null;
            return result;
        }
        finally
        {
            try
            {
                if (document is not null) await document.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    credentials?.Dispose();
                    lock (_sync)
                    {
                        if (--_opening == 0) _opensDrained?.TrySetResult();
                    }
                }
            }
        }
    }

    public void UpdateLocale(Loc loc)
    {
        RdpWorkspaceDocument[] documents;
        long version;
        lock (_sync)
        {
            if (_disposed) return;
            _loc = loc;
            version = ++_localeVersion;
            documents = [.. _documents];
        }
        foreach (RdpWorkspaceDocument document in documents) document.UpdateLocale(loc, version);
    }

    internal static Exception LocalizeFailure(Exception failure, Loc loc) => failure switch
    {
        ProtocolCertificateTrustException certificate => new ProtocolCertificateTrustException(loc["Rdp_Certificate"],
            certificate.Subject, certificate.Issuer, certificate.ExpiresOn, certificate.Thumbprint, certificate.PolicyErrors),
        ProtocolAuthenticationException => new ProtocolAuthenticationException(loc["Rdp_AuthFailed"]),
        ProtocolUnsupportedException => new ProtocolUnsupportedException(loc["Rdp_Unsupported"]),
        TimeoutException => new TimeoutException(loc["Rdp_Timeout"]),
        _ => new ProtocolConnectionException(loc["Rdp_Error"])
    };

    private void OnDocumentClosed(RdpWorkspaceDocument document)
    {
        lock (_sync) _documents.Remove(document);
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            RdpWorkspaceDocument[] documents = [.. _documents];
            Task opens = _opening == 0 ? Task.CompletedTask : _opensDrained!.Task;
            _disposeTask = Task.Run(() => DisposeCoreAsync(documents, opens));
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(RdpWorkspaceDocument[] documents, Task opens)
    {
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            catch { _context.Log.Warn("An RDP cancellation subscriber failed."); }
            try { await Task.WhenAll(documents.Select(d => d.DisposeAsync().AsTask())).ConfigureAwait(false); }
            finally { await opens.ConfigureAwait(false); }
        }
        finally
        {
            _shutdown.Unregister();
            _lifetime.Dispose();
        }
    }

    private async Task DisposeOnShutdownAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { _context.Log.Warn("RDP shutdown cleanup failed."); }
    }
}
