using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MongoDB.Driver;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.RemoteTunnel;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>
/// 经 SSH 跳板的一条转发:本机一个回环端口 ↔ 跳板机上开到目标的 TCP 流。
/// 驱动只认 TCP 端点,所以给它一个本地端口,每接进一条连接就在跳板机上开一条流对接。
/// </summary>
internal interface IMongoTunnel : IAsyncDisposable
{
    /// <summary>跳板的显示名(已保存的 SSH 连接名)。</summary>
    string JumpName { get; }

    /// <summary>本机回环上的转发端口。</summary>
    int LocalPort { get; }

    /// <summary>目标(跳板机看到的 host:port)。</summary>
    string Target { get; }

    /// <summary>在跳板机上敲一下目标端口(开一条流再关掉),返回耗时。不通就抛。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>毫秒。</returns>
    Task<int> CheckTargetAsync(CancellationToken cancellationToken);

    /// <summary>把请求改写成连本地端点(<see cref="WorkspaceConnectRequest.Tunnel" /> 记下真实目标)。</summary>
    /// <param name="request">原请求。</param>
    /// <returns>改写后的请求。</returns>
    WorkspaceConnectRequest Rewrite(WorkspaceConnectRequest request);
}

/// <summary>
/// 用宿主的 SSH 会话做跳板(<see cref="ISessionsApi.OpenAsync" /> + <see cref="IRemoteTunnelApi.OpenTcpAsync" />)。
/// 插件一行 SSH 代码都不写,也碰不到跳板机的凭据 —— 那条会话由宿主按用户已保存的配置去连。
/// </summary>
internal sealed class SshTunnel : IMongoTunnel
{
    private readonly IPluginContext _context;
    private readonly JumpLease _lease;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, Stream? Remote)> _pumps = new();
    private readonly string _host;
    private readonly int _port;
    private int _nextPump;
    private int _disposed;

    private SshTunnel(IPluginContext context, JumpLease lease, string host, int port)
    {
        _context = context;
        _lease = lease;
        _host = host;
        _port = port;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    /// <inheritdoc />
    public string JumpName => _lease.Name;

    /// <inheritdoc />
    public int LocalPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <inheritdoc />
    public string Target => $"{_host}:{_port}";

    /// <summary>
    /// 开一条转发:先让宿主连上那条已保存的 SSH 会话(第一次会弹宿主的确认框,理由原样给用户看),
    /// 再在本机回环上起一个监听端口。
    /// </summary>
    /// <param name="context">插件上下文。</param>
    /// <param name="savedSessionId">已保存的 SSH 连接 id。</param>
    /// <param name="host">目标主机(跳板机看到的)。</param>
    /// <param name="port">目标端口。</param>
    /// <param name="reason">给用户看的理由。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>转发。</returns>
    public static async Task<SshTunnel> OpenAsync(
        IPluginContext context,
        string savedSessionId,
        string host,
        int port,
        string reason,
        CancellationToken cancellationToken)
    {
        JumpLease lease = await JumpLease.AcquireAsync(context, savedSessionId, reason, cancellationToken).ConfigureAwait(false);
        try
        {
            return new SshTunnel(context, lease, host, port);
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>请求要连的那台(跳板机看到的地址):主机列表是主机行;连接串是它的第一台。</summary>
    /// <param name="request">请求。</param>
    /// <param name="settings">设置。</param>
    /// <returns>主机与端口。</returns>
    public static (string Host, int Port) TargetOf(WorkspaceConnectRequest request, MongoSettings settings)
    {
        if (settings.Topology == MongoTopology.Uri)
        {
            MongoServerAddress first = new MongoUrlBuilder(request.Host.Trim()).Servers.First();
            return (first.Host, first.Port);
        }
        return (request.Host.Trim(), request.Port);
    }

    /// <inheritdoc />
    public async Task<int> CheckTargetAsync(CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Stream stream = await _context.RemoteTunnel.OpenTcpAsync(_lease.SessionId, _host, _port, null, cancellationToken).ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
        return (int)watch.ElapsedMilliseconds;
    }

    /// <inheritdoc />
    public WorkspaceConnectRequest Rewrite(WorkspaceConnectRequest request) => request with
    {
        Host = IPAddress.Loopback.ToString(),
        Port = LocalPort,
        Tunnel = new WorkspaceTunnelInfo(_host, _port, JumpName)
    };

    private async Task AcceptLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            client.NoDelay = true;
            int id = Interlocked.Increment(ref _nextPump);
            _pumps[id] = (client, null);
            _ = PumpAsync(id, client);
        }
    }

    /// <summary>一条本地连接 ↔ 一条跳板机上的流,两个方向各拷各的,任一边断了两边一起收。</summary>
    private async Task PumpAsync(int id, TcpClient client)
    {
        Stream? remote = null;
        try
        {
            remote = await _context.RemoteTunnel.OpenTcpAsync(_lease.SessionId, _host, _port, null, _lifetime.Token).ConfigureAwait(false);
            _pumps[id] = (client, remote);
            NetworkStream local = client.GetStream();
            Task up = local.CopyToAsync(remote, _lifetime.Token);
            Task down = remote.CopyToAsync(local, _lifetime.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 跳板上开不出流(目标拒连、会话断了):关掉本地这一头,驱动会当成一次普通的连接失败处理。
            if (!_lifetime.IsCancellationRequested)
            {
                _context.Log.Warn($"SSH forward to {Target} via {JumpName} failed: {ex.Message}");
            }
        }
        finally
        {
            _pumps.TryRemove(id, out _);
            client.Dispose();
            if (remote is not null)
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach ((TcpClient client, Stream? remote) in _pumps.Values)
        {
            client.Dispose();
            if (remote is not null)
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }
        _pumps.Clear();
        _lifetime.Dispose();
        await _lease.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 一条跳板会话的引用计数。几个 MongoDB 连接走同一台跳板时共用一条 SSH 会话,最后一个走了才关;
    /// 而且只关**本插件开的**那条 —— 用户自己开着的终端会话宿主不让关(<see cref="ISessionsApi.CloseAsync" />),那就留着。
    /// </summary>
    private sealed class JumpLease : IAsyncDisposable
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<string, Shared> Open = new(StringComparer.Ordinal);

        private readonly IPluginContext _context;
        private readonly string _savedSessionId;
        private readonly Shared _shared;
        private int _disposed;

        private JumpLease(IPluginContext context, string savedSessionId, Shared shared)
        {
            _context = context;
            _savedSessionId = savedSessionId;
            _shared = shared;
        }

        public string SessionId => _shared.Session.SessionId;

        public string Name => _shared.Name;

        public static async Task<JumpLease> AcquireAsync(IPluginContext context, string savedSessionId, string reason, CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                if (Open.TryGetValue(savedSessionId, out Shared? shared))
                {
                    shared.Count++;
                    return new JumpLease(context, savedSessionId, shared);
                }
            }
            IReadOnlyList<SavedSessionInfo> saved = await context.Sessions.ListSavedAsync(cancellationToken).ConfigureAwait(false);
            string name = saved.FirstOrDefault(s => s.SavedSessionId == savedSessionId)?.Name ?? savedSessionId;
            SessionInfo session = await context.Sessions.OpenAsync(savedSessionId, new SessionOpenOptions(reason), cancellationToken).ConfigureAwait(false);
            lock (Gate)
            {
                // 等宿主连的这段时间里可能已经有别的连接开好了同一条:用先到的那条,自己这条交还计数。
                if (Open.TryGetValue(savedSessionId, out Shared? raced))
                {
                    raced.Count++;
                    return new JumpLease(context, savedSessionId, raced);
                }
                var fresh = new Shared(session, name) { Count = 1 };
                Open[savedSessionId] = fresh;
                return new JumpLease(context, savedSessionId, fresh);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            lock (Gate)
            {
                if (--_shared.Count > 0)
                {
                    return;
                }
                Open.Remove(_savedSessionId);
            }
            try
            {
                await _context.Sessions.CloseAsync(_shared.Session.SessionId).ConfigureAwait(false);
            }
            catch (PluginPermissionDeniedException)
            {
                // 宿主复用了用户自己开着的那条会话:不归插件关。
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _context.Log.Warn($"Closing the SSH jump session '{_shared.Name}' failed: {ex.Message}");
            }
        }

        private sealed class Shared(SessionInfo session, string name)
        {
            public SessionInfo Session { get; } = session;

            public string Name { get; } = name;

            public int Count { get; set; }
        }
    }
}
