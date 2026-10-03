using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Compression;
using MongoDB.Driver.Core.Configuration;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>
/// 一条 MongoDB 连接:驱动的 <see cref="MongoClient" /> 加上界面要的那几样现成信息
/// (服务器身份、当前用户的权限、往返延迟)。
/// <para>
/// 驱动自己是连接池 + 后台心跳,一个 <see cref="MongoClient" /> 就够整个工作台用 ——
/// 网格、查询编辑器、监控页共享它,而不是每个标签页各开一条。
/// </para>
/// <para>
/// <b>驱动的异常不得越过插件边界</b>:这里只抛驱动原生异常,出口(<see cref="MongoConnector" />
/// 与各视图模型)负责翻成一句人话。
/// </para>
/// </summary>
internal sealed partial class MongoConnection : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer? _heartbeat;
    private int _disposed;

    private MongoConnection(
        MongoClient client,
        MongoSettings settings,
        string endpoint,
        TlsTrust? trust,
        bool startHeartbeat)
    {
        Client = client;
        Settings = settings;
        Endpoint = endpoint;
        Trust = trust;
        if (startHeartbeat)
        {
            // 每 10 秒 ping 一次:工具栏的延迟数字与宿主标签上的状态圆点都靠它。
            // 驱动自己的心跳不对外报延迟,也不在失败时通知谁 —— 这条是给人看的。
            _heartbeat = new(_ => _ = BeatAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>驱动客户端。</summary>
    public MongoClient Client { get; }

    /// <summary>连接设置。</summary>
    public MongoSettings Settings { get; }

    /// <summary>给人看的端点(走隧道时是真实目标 + 来路,不是 127.0.0.1)。</summary>
    public string Endpoint { get; }

    /// <summary>TLS 证书记录器(翻译证书失败时要它手里那张证书)。</summary>
    public TlsTrust? Trust { get; }

    /// <summary>服务器身份(连上时探一次,<see cref="RefreshServerAsync" /> 可重探)。</summary>
    public ServerInfo Server { get; private set; } = new("", null, "STANDALONE", "", [], true, "wiredTiger", 0);

    /// <summary>当前用户的权限摘要。</summary>
    public PrivilegeSummary Privileges { get; private set; } = PrivilegeSummary.Unrestricted;

    /// <summary>最近一次 ping 的往返(毫秒);未知为 <see langword="null" />。</summary>
    public int? LatencyMs { get; private set; }

    /// <summary>连通性变化(心跳失败 / 恢复)。在线程池线程上触发。</summary>
    public event Action<bool>? Availability;

    /// <summary>延迟更新(每次心跳)。在线程池线程上触发。</summary>
    public event Action<int>? LatencyChanged;

    private bool _available = true;

    /// <summary>
    /// 连上并探一次身份。**返回前必须完成首次往返** —— 宿主要把失败原因呈现在连接流程里,
    /// 而驱动的 <c>new MongoClient</c> 本身不连任何东西。
    /// </summary>
    /// <param name="request">宿主的连接请求。</param>
    /// <param name="settings">解析好的设置。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已连接的连接。</returns>
    public static async Task<MongoConnection> ConnectAsync(
        WorkspaceConnectRequest request,
        MongoSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TlsTrust? trust = settings.UseTls || settings.Topology != MongoTopology.Hosts ? new(settings.TrustedThumbprint) : null;
        MongoClientSettings clientSettings = BuildSettings(request, settings, trust);
        var client = new MongoClient(clientSettings);
        string endpoint = DescribeEndpoint(request, settings);
        var connection = new MongoConnection(client, settings, endpoint, trust, startHeartbeat: true);
        try
        {
            await connection.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 选服超时的消息只说"超时了",真正的原因(认证失败、TLS 被拒、端口不通)躺在各成员的
            // 心跳异常里 —— 释放连接之前把它们捞出来,否则出口只能报一句无用的"连接超时"。
            List<Exception> causes = [.. connection.HeartbeatFailures()];
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new MongoConnectFailedException(ex, causes, trust);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 用一条现成的连接串直接开(数据传输的目标端、单测)。不起心跳。
    /// </summary>
    /// <param name="connectionString">连接串。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已连接的连接。</returns>
    public static async Task<MongoConnection> ConnectAsync(string connectionString, CancellationToken cancellationToken)
    {
        MongoClientSettings clientSettings = MongoClientSettings.FromConnectionString(connectionString);
        clientSettings.ServerSelectionTimeout = TimeSpan.FromSeconds(10);
        clientSettings.ApplicationName ??= "velashell";
        var url = new MongoUrl(connectionString);
        var connection = new MongoConnection(
            new MongoClient(clientSettings),
            new MongoSettings { Database = url.DatabaseName ?? "" },
            string.Join(",", url.Servers.Select(static s => $"{s.Host}:{s.Port}")),
            null,
            startHeartbeat: false);
        try
        {
            await connection.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>按宿主请求 + 插件设置拼驱动设置。</summary>
    internal static MongoClientSettings BuildSettings(WorkspaceConnectRequest request, MongoSettings settings, TlsTrust? trust)
    {
        MongoClientSettings client;
        switch (settings.Topology)
        {
            case MongoTopology.Uri:
            {
                // URI 写在"主机"那一栏里。凭据可以在 URI 里,也可以填在用户名/密码两栏 ——
                // 后者优先:那两栏的密码是宿主加密落盘的,URI 里的密码是明文。
                var url = new MongoUrlBuilder(request.Host.Trim());
                if (request.Tunnel is not null)
                {
                    // 走隧道时宿主把主机改写成了本地端点 —— 但 URI 模式下"主机"一栏是整条串,
                    // 宿主改写的是它解析出来的第一台。这里按宿主给的端点直连那一台。
                    url.Server = new(request.Host, request.Port);
                    url.DirectConnection = true;
                }
                if (!string.IsNullOrEmpty(request.Username))
                {
                    url.Username = request.Username;
                    url.Password = request.Password;
                }
                client = MongoClientSettings.FromUrl(url.ToMongoUrl());
                break;
            }
            case MongoTopology.Srv:
            {
                var url = new MongoUrlBuilder
                {
                    Scheme = ConnectionStringScheme.MongoDBPlusSrv,
                    Server = new(request.Host.Trim())
                };
                client = MongoClientSettings.FromUrl(url.ToMongoUrl());
                ApplyCredential(client, request, settings);
                if (settings.ReplicaSet.Length > 0)
                {
                    client.ReplicaSetName = settings.ReplicaSet;
                }
                break;
            }
            default:
            {
                client = new MongoClientSettings();
                var servers = new List<MongoServerAddress> { new(request.Host.Trim(), request.Port) };
                bool tunneled = request.Tunnel is not null;
                if (!tunneled)
                {
                    foreach (string extra in settings.AdditionalHosts)
                    {
                        servers.Add(ParseAddress(extra));
                    }
                }
                client.Servers = servers;
                // 经隧道:本地转发只通到那一台,驱动按副本集配置去连其余成员的真实地址 ——
                // 那些地址从本机根本够不着,结果是选服超时。所以隧道下强制直连。
                bool direct = tunneled || (settings.DirectConnection && servers.Count == 1);
                client.DirectConnection = direct;
                if (!direct && settings.ReplicaSet.Length > 0)
                {
                    client.ReplicaSetName = settings.ReplicaSet;
                }
                ApplyCredential(client, request, settings);
                break;
            }
        }

        client.ApplicationName = settings.AppName;
        client.ConnectTimeout = TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs);
        client.ServerSelectionTimeout = TimeSpan.FromMilliseconds(settings.ConnectTimeoutMs);
        client.ReadPreference = ParseReadPreference(settings.ReadPreference);
        // 压缩:服务器不支持的会在握手时被协商掉,写上没有坏处 —— 远端大结果集上能省一大半带宽。
        client.Compressors = [new(CompressorType.ZStandard), new(CompressorType.Snappy), new(CompressorType.Zlib)];

        bool tls = settings.UseTls || client.UseTls;
        if (tls)
        {
            client.UseTls = true;
            var ssl = client.SslSettings?.Clone() ?? new SslSettings();
            ssl.CheckCertificateRevocation = false;
            X509Certificate2Collection? caCertificates = LoadCaCertificates(settings.TlsCaFile);
            ssl.ServerCertificateValidationCallback = (_, certificate, chain, errors) =>
                trust?.Validate(certificate, chain, errors, caCertificates) ?? errors == SslPolicyErrors.None;
            if (LoadClientCertificate(settings.TlsCertificateFile) is { } clientCertificate)
            {
                ssl.ClientCertificates = [clientCertificate];
            }
            client.SslSettings = ssl;
        }
        return client;
    }

    private static void ApplyCredential(MongoClientSettings client, WorkspaceConnectRequest request, MongoSettings settings)
    {
        string mechanism = settings.AuthMechanism.ToUpperInvariant();
        if (mechanism == "MONGODB-X509")
        {
            // X.509 的身份就是客户端证书;用户名可空(服务器从证书主题里取)。
            client.Credential = MongoCredential.CreateMongoX509Credential(
                string.IsNullOrEmpty(request.Username) ? null : request.Username);
            return;
        }
        if (string.IsNullOrEmpty(request.Username))
        {
            return;
        }
        string source = settings.AuthSource.Length > 0
            ? settings.AuthSource
            : mechanism == "PLAIN" ? "$external" : "admin";
        MongoIdentity identity = mechanism == "PLAIN"
            ? new MongoExternalIdentity(source, request.Username)
            : new MongoInternalIdentity(source, request.Username);
        client.Credential = mechanism switch
        {
            "SCRAM-SHA-256" or "SCRAM-SHA-1" or "PLAIN" =>
                new MongoCredential(mechanism, identity, new PasswordEvidence(request.Password)),
            _ => MongoCredential.CreateCredential(source, request.Username, request.Password)
        };
    }

    internal static MongoServerAddress ParseAddress(string text)
    {
        int colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out int port)
            ? new(text[..colon], port)
            : new(text, 27017);
    }

    /// <summary>读偏好字面量 → 驱动对象;认不出的回落 primary。</summary>
    internal static ReadPreference ParseReadPreference(string text) => text switch
    {
        "primaryPreferred" => ReadPreference.PrimaryPreferred,
        "secondary" => ReadPreference.Secondary,
        "secondaryPreferred" => ReadPreference.SecondaryPreferred,
        "nearest" => ReadPreference.Nearest,
        _ => ReadPreference.Primary
    };

    private static X509Certificate2Collection? LoadCaCertificates(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        var collection = new X509Certificate2Collection();
        collection.ImportFromPemFile(path);
        return collection;
    }

    private static X509Certificate2? LoadClientCertificate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        return Path.GetExtension(path).ToLowerInvariant() is ".pem" or ".crt"
            ? X509Certificate2.CreateFromPemFile(path)
            : X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    private static string DescribeEndpoint(WorkspaceConnectRequest request, MongoSettings settings)
    {
        if (request.Tunnel is { } tunnel)
        {
            return $"{tunnel.TargetHost}:{tunnel.TargetPort}  ↝ {tunnel.JumpDisplayName}";
        }
        return settings.Topology switch
        {
            MongoTopology.Srv => request.Host.Trim(),
            MongoTopology.Uri => RedactUri(request.Host.Trim()),
            _ => $"{request.Host.Trim()}:{request.Port}"
        };
    }

    /// <summary>把 URI 里的密码换成星号(任何要显示或记日志的地方都只拿这个形态)。</summary>
    internal static string RedactUri(string uri)
    {
        try
        {
            var builder = new MongoUrlBuilder(uri);
            if (!string.IsNullOrEmpty(builder.Password))
            {
                builder.Password = "****";
            }
            return builder.ToString();
        }
        catch (Exception)
        {
            return MaskUnparsed(uri);
        }
    }

    /// <summary>
    /// 驱动解析不了的(多半是还没敲完的)URI 按字符串自己遮。权限段(<c>://</c> 之后、
    /// 第一个 <c>/</c> 或 <c>?</c> 之前)里:有 <c>@</c> 就把 <c>user:</c> 之后到 <c>@</c> 的部分遮掉;
    /// 还没敲到 <c>@</c> 时,冒号后面不是纯数字(不像端口)的也遮 —— 那多半是正在敲的口令。
    /// </summary>
    internal static string MaskUnparsed(string uri)
    {
        int scheme = uri.IndexOf("://", StringComparison.Ordinal);
        int start = scheme < 0 ? 0 : scheme + 3;
        int end = uri.IndexOfAny(['/', '?'], start);
        if (end < 0)
        {
            end = uri.Length;
        }
        string authority = uri[start..end];
        int at = authority.LastIndexOf('@');
        string masked;
        if (at >= 0)
        {
            string userInfo = authority[..at];
            int colon = userInfo.IndexOf(':', StringComparison.Ordinal);
            masked = (colon < 0 ? userInfo : userInfo[..colon] + ":****") + authority[at..];
        }
        else
        {
            masked = string.Join(',', authority.Split(',').Select(static part =>
            {
                int colon = part.LastIndexOf(':');
                return colon < 0 || part.StartsWith('[') || part[(colon + 1)..].All(char.IsAsciiDigit)
                    ? part
                    : part[..colon] + ":****";
            }));
        }
        return uri[..start] + masked + uri[end..];
    }

    /// <summary>
    /// 连接后的身份探测:<c>hello</c>(拓扑与身份)、<c>buildInfo</c>(版本)、
    /// <c>connectionStatus</c>(权限)。<c>ping</c> 顺带量一次延迟。
    /// </summary>
    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        IMongoDatabase admin = Client.GetDatabase("admin");
        var watch = Stopwatch.StartNew();
        BsonDocument hello = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        LatencyMs = (int)Math.Max(1, watch.ElapsedMilliseconds);
        BsonDocument? build = null;
        try
        {
            build = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoCommandException)
        {
            // Atlas 的低权限用户可能连 buildInfo 都拿不到 —— 版本空着,不该因此连不上。
        }
        Server = ParseHello(hello, build);
        Privileges = await ProbePrivilegesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>重探服务器身份(重连之后,或主从切换后)。</summary>
    public Task RefreshServerAsync(CancellationToken cancellationToken = default) => ProbeAsync(cancellationToken);

    internal static ServerInfo ParseHello(BsonDocument hello, BsonDocument? build)
    {
        string version = build?.GetValue("version", "").AsString ?? "";
        string? setName = hello.TryGetValue("setName", out BsonValue set) ? set.AsString : null;
        bool primary = hello.GetValue("isWritablePrimary", hello.GetValue("ismaster", false)).ToBoolean();
        bool secondary = hello.GetValue("secondary", false).ToBoolean();
        bool arbiter = hello.GetValue("arbiterOnly", false).ToBoolean();
        bool mongos = hello.TryGetValue("msg", out BsonValue msg) && msg.IsString && msg.AsString == "isdbgrid";
        string role = mongos ? "MONGOS"
            : setName is null ? "STANDALONE"
            : primary ? "PRIMARY"
            : secondary ? "SECONDARY"
            : arbiter ? "ARBITER"
            : "OTHER";
        string me = hello.TryGetValue("me", out BsonValue meValue) && meValue.IsString ? meValue.AsString : "";
        IReadOnlyList<string> members = hello.TryGetValue("hosts", out BsonValue hosts) && hosts.IsBsonArray
            ? [.. hosts.AsBsonArray.Select(static h => h.AsString)]
            : [];
        string engine = build?.TryGetValue("storageEngines", out BsonValue engines) == true && engines.IsBsonArray
                        && engines.AsBsonArray.Any(static e => e.AsString == "wiredTiger")
            ? "wiredTiger"
            : "wiredTiger";
        int wire = hello.GetValue("maxWireVersion", 0).ToInt32();
        return new(version, setName, role, me, members, primary || mongos || setName is null, engine, wire);
    }

    /// <summary>
    /// 读当前用户的角色与写权限。探测失败(老版本、没开认证)按"全放行"处理 ——
    /// 这里的结论只用来置灰按钮,判错的代价远小于把一个能写的用户锁死。
    /// </summary>
    private async Task<PrivilegeSummary> ProbePrivilegesAsync(CancellationToken cancellationToken)
    {
        try
        {
            BsonDocument status = await Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument { { "connectionStatus", 1 }, { "showPrivileges", true } },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            BsonDocument auth = status.GetValue("authInfo", new BsonDocument()).AsBsonDocument;
            BsonArray users = auth.GetValue("authenticatedUsers", new BsonArray()).AsBsonArray;
            if (users.Count == 0)
            {
                // 没认证:要么服务器没开认证(全放行),要么匿名(服务器会拒,这里也不拦)。
                return PrivilegeSummary.Unrestricted;
            }
            string user = string.Join(", ", users.Select(static u => $"{u["user"].AsString}@{u["db"].AsString}"));
            List<RoleGrant> roles =
            [
                .. auth.GetValue("authenticatedUserRoles", new BsonArray()).AsBsonArray
                    .Select(static r => new RoleGrant(r["role"].AsString, r["db"].AsString))
            ];
            var writable = new HashSet<string>(StringComparer.Ordinal);
            foreach (BsonValue privilege in auth.GetValue("authenticatedUserPrivileges", new BsonArray()).AsBsonArray)
            {
                BsonDocument resource = privilege["resource"].AsBsonDocument;
                bool writes = privilege["actions"].AsBsonArray
                    .Any(static a => a.AsString is "insert" or "update" or "remove" or "createCollection" or "dropCollection" or "createIndex");
                if (!writes)
                {
                    continue;
                }
                if (resource.GetValue("anyResource", false).ToBoolean()
                    || (resource.TryGetValue("db", out BsonValue db) && db.IsString && db.AsString.Length == 0))
                {
                    writable.Add("*");
                }
                else if (resource.TryGetValue("db", out BsonValue named) && named.IsString)
                {
                    writable.Add(named.AsString);
                }
            }
            return new(user, roles, writable);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return PrivilegeSummary.Unrestricted;
        }
    }

    /// <summary>ping 一次并返回往返毫秒。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>往返毫秒。</returns>
    public async Task<int> PingAsync(CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        await Client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        int ms = (int)Math.Max(1, watch.ElapsedMilliseconds);
        LatencyMs = ms;
        return ms;
    }

    private async Task BeatAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            int ms = await PingAsync(timeout.Token).ConfigureAwait(false);
            LatencyChanged?.Invoke(ms);
            SetAvailable(true);
        }
        catch (Exception) when (Volatile.Read(ref _disposed) == 0)
        {
            LatencyMs = null;
            SetAvailable(false);
        }
        catch (Exception)
        {
            // 已释放:心跳与释放赛跑时的那一次失败不报。
        }
    }

    private void SetAvailable(bool available)
    {
        if (_available == available)
        {
            return;
        }
        _available = available;
        Availability?.Invoke(available);
    }

    /// <summary>当前集群描述里各成员的心跳错误(诊断"为什么选服超时")。</summary>
    internal IEnumerable<Exception> HeartbeatFailures() =>
        Client.Cluster.Description.Servers
            .Select(static s => s.HeartbeatException)
            .Where(static e => e is not null)
            .Select(static e => e!);

    /// <summary>取一个库。</summary>
    public IMongoDatabase Database(string name) => Client.GetDatabase(name);

    /// <summary>取一个集合(一律 <see cref="BsonDocument" />:界面处理的是任意形状的文档)。</summary>
    public IMongoCollection<BsonDocument> Collection(string database, string collection) =>
        Client.GetDatabase(database).GetCollection<BsonDocument>(collection);

    /// <summary>在某个库上跑一条命令。</summary>
    public Task<BsonDocument> RunCommandAsync(string database, BsonDocument command, CancellationToken cancellationToken = default) =>
        Client.GetDatabase(database).RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken);

    /// <summary>
    /// 按 <c>comment</c> 标记找到正在跑的操作并 <c>killOp</c>。
    /// 长查询「取消」按钮的落点:客户端取消令牌只能放弃等待,服务器那边的查询照跑不误。
    /// </summary>
    /// <param name="comment">发起操作时打的标记。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>杀掉的操作数。</returns>
    public async Task<int> KillByCommentAsync(string comment, CancellationToken cancellationToken = default)
    {
        BsonDocument current = await RunCommandAsync("admin", new BsonDocument
        {
            { "currentOp", true },
            { "$or", new BsonArray
                {
                    new BsonDocument("command.comment", comment),
                    new BsonDocument("originatingCommand.comment", comment)
                }
            }
        }, cancellationToken).ConfigureAwait(false);
        int killed = 0;
        foreach (BsonValue op in current.GetValue("inprog", new BsonArray()).AsBsonArray)
        {
            if (op.AsBsonDocument.TryGetValue("opid", out BsonValue opid))
            {
                await RunCommandAsync("admin", new BsonDocument { { "killOp", 1 }, { "op", opid } }, cancellationToken)
                    .ConfigureAwait(false);
                killed++;
            }
        }
        return killed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_heartbeat is not null)
        {
            await _heartbeat.DisposeAsync().ConfigureAwait(false);
        }
        _lifetime.Dispose();
        Client.Dispose();
    }
}

/// <summary>
/// 首次连接失败:原始异常 + 各成员心跳里记下的真正原因。只在插件内部流转,
/// 出口(<see cref="MongoConnector" />)把它翻成一句人话(<see cref="MongoConnectException" />)。
/// </summary>
/// <param name="primary">首次往返抛出的异常(多半是选服超时)。</param>
/// <param name="causes">各成员心跳异常。</param>
/// <param name="trust">这次连接用的证书记录器(TLS 被拒时手里有那张证书)。</param>
internal sealed class MongoConnectFailedException(Exception primary, IReadOnlyList<Exception> causes, TlsTrust? trust)
    : Exception(primary.Message, primary)
{
    /// <summary>各成员心跳异常。</summary>
    public IReadOnlyList<Exception> Causes { get; } = causes;

    /// <summary>证书记录器。</summary>
    public TlsTrust? Trust { get; } = trust;
}
