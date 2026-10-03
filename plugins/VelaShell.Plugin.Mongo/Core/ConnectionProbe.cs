using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>
/// 连接对话框右侧栏的内容(设计稿 10 的 Side):连接串预览与逐步的连接测试。
/// 只产出结构化数据(<see cref="ConnectionPreview" /> / <see cref="ProbeReport" />),
/// 怎么画归 <c>ConnectionDialogView</c> —— 这里不碰界面,也不联网(预览)。
/// </summary>
internal static class ConnectionProbe
{
    // ── 连接串预览 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 按表单草稿拼出脱敏的连接串。**不联网、不抛异常**(对话框在用户每次改动后调):
    /// 草稿里不放口令(对话框传空),有用户名就写成 <c>user:****@</c>;URI 形态解析不了时原样脱敏显示。
    /// </summary>
    /// <param name="draft">表单草稿。</param>
    /// <param name="loc">文案表。</param>
    /// <returns>预览;主机还空着时为 <see langword="null" />。</returns>
    public static ConnectionPreview? Preview(WorkspaceConnectRequest draft, Loc loc)
    {
        if (string.IsNullOrWhiteSpace(draft.Host))
        {
            return null;
        }
        var settings = MongoSettings.From(draft);
        List<PreviewSpan> spans;
        try
        {
            spans = Spans(draft, settings);
        }
        catch (Exception ex) when (ex is MongoConfigurationException or ArgumentException or FormatException or UriFormatException)
        {
            // 用户还没把 URI 填完:不画半截解析结果,就把填了的(脱敏后)原样摆出来。
            spans = [new(MongoConnection.RedactUri(draft.Host.Trim()), PreviewRole.Plain)];
        }
        return new ConnectionPreview { Title = loc["Conn_Uri"], Spans = spans, Note = loc["Conn_UriNote"] };
    }

    /// <summary>
    /// 表单草稿 → 连接串的各段。**只做字符串解析,不经驱动的设置构造** ——
    /// 驱动在构造 <c>mongodb+srv</c> 的设置时可能当场去查 DNS,而预览是用户每敲一个字就调一次的。
    /// </summary>
    private static List<PreviewSpan> Spans(WorkspaceConnectRequest draft, MongoSettings settings)
    {
        Target target = TargetOf(draft, settings);
        var spans = new List<PreviewSpan> { new(target.Srv ? "mongodb+srv://" : "mongodb://", PreviewRole.Scheme) };
        if (target.User is { Length: > 0 } user)
        {
            spans.Add(new(Uri.EscapeDataString(user), PreviewRole.User));
            spans.Add(new(":****@", PreviewRole.Secret));
        }
        for (int i = 0; i < target.Hosts.Count; i++)
        {
            spans.Add(new(target.Hosts[i] + (i < target.Hosts.Count - 1 ? "," : ""), PreviewRole.Host));
        }
        if (target.Database.Length > 0)
        {
            spans.Add(new("/" + target.Database, PreviewRole.Path));
        }
        for (int i = 0; i < target.Options.Count; i++)
        {
            string lead = (i == 0 ? (target.Database.Length > 0 ? "?" : "/?") : "&") + target.Options[i].Key + "=";
            spans.Add(new(lead, PreviewRole.Key));
            spans.Add(new(Uri.EscapeDataString(target.Options[i].Value), PreviewRole.Value));
        }
        return spans;
    }

    /// <summary>预览与 TCP 一步共用的目标描述(纯字符串解析)。</summary>
    private sealed record Target(
        bool Srv,
        string? User,
        IReadOnlyList<string> Hosts,
        string Database,
        IReadOnlyList<(string Key, string Value)> Options);

    private static Target TargetOf(WorkspaceConnectRequest request, MongoSettings settings)
    {
        var options = new List<(string Key, string Value)>();
        if (settings.Topology == MongoTopology.Uri)
        {
            // URI 写在"主机"那一栏里;用户名两栏填了就以它为准(与打开会话时同一口径)。
            var url = new MongoUrlBuilder(request.Host.Trim());
            bool srv = url.Scheme == ConnectionStringScheme.MongoDBPlusSrv;
            string? user = request.Username is { Length: > 0 } typed ? typed : url.Username;
            List<string> hosts = srv
                ? [.. url.Servers.Select(static s => s.Host)]
                : [.. url.Servers.Select(static s => $"{s.Host}:{s.Port.ToString(CultureInfo.InvariantCulture)}")];
            if (url.ReplicaSetName is { Length: > 0 } uriSet)
            {
                options.Add(("replicaSet", uriSet));
            }
            if (user is { Length: > 0 })
            {
                options.Add(("authSource", url.AuthenticationSource is { Length: > 0 } source ? source : settings.AuthSource));
            }
            if (url.AuthenticationMechanism is { Length: > 0 } uriMechanism)
            {
                options.Add(("authMechanism", uriMechanism));
            }
            if (url.ReadPreference is { } preference && preference.ReadPreferenceMode != ReadPreferenceMode.Primary)
            {
                options.Add(("readPreference", ReadPreferenceName(preference.ReadPreferenceMode)));
            }
            if (url.UseTls && !srv)
            {
                options.Add(("tls", "true"));
            }
            if (url.DirectConnection == true)
            {
                options.Add(("directConnection", "true"));
            }
            return new(srv, user, hosts, settings.Database.Length > 0 ? settings.Database : url.DatabaseName ?? "", options);
        }

        bool isSrv = settings.Topology == MongoTopology.Srv;
        List<string> list = isSrv
            ? [request.Host.Trim()]
            : [Address(request.Host.Trim(), request.Port), .. settings.AdditionalHosts.Select(static h => FormatAddress(MongoConnection.ParseAddress(h)))];
        if (settings.ReplicaSet.Length > 0)
        {
            options.Add(("replicaSet", settings.ReplicaSet));
        }
        string? name = request.Username is { Length: > 0 } u ? u : null;
        if (name is not null)
        {
            options.Add(("authSource", settings.AuthSource));
            if (settings.AuthMechanism is not ("DEFAULT" or ""))
            {
                options.Add(("authMechanism", settings.AuthMechanism));
            }
        }
        if (settings.ReadPreference is { Length: > 0 } readPreference && readPreference != "primary")
        {
            options.Add(("readPreference", readPreference));
        }
        if (settings.UseTls && !isSrv)
        {
            options.Add(("tls", "true"));
        }
        if (settings.DirectConnection && !isSrv)
        {
            options.Add(("directConnection", "true"));
        }
        if (settings.AppName is { Length: > 0 } app && app != "velashell")
        {
            options.Add(("appName", app));
        }
        return new(isSrv, name, list, settings.Database, options);
    }

    private static string Address(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? string.Create(CultureInfo.InvariantCulture, $"[{host}]:{port}")
            : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

    private static string FormatAddress(MongoServerAddress address) => Address(address.Host, address.Port);

    /// <summary><c>host:port</c> / <c>[v6]:port</c> → 端点(没写端口按 27017)。</summary>
    private static EndPoint ToEndPoint(string address)
    {
        string host;
        int port = 27017;
        if (address.StartsWith('[') && address.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            host = address[1..close];
            if (address.Length > close + 2 && int.TryParse(address[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out int v6Port))
            {
                port = v6Port;
            }
        }
        else
        {
            MongoServerAddress parsed = MongoConnection.ParseAddress(address);
            (host, port) = (parsed.Host, parsed.Port);
        }
        return IPAddress.TryParse(host, out IPAddress? ip) ? new IPEndPoint(ip, port) : new DnsEndPoint(host, port);
    }

    /// <summary>读偏好枚举 → 连接串里的写法。</summary>
    internal static string ReadPreferenceName(ReadPreferenceMode mode) => mode switch
    {
        ReadPreferenceMode.PrimaryPreferred => "primaryPreferred",
        ReadPreferenceMode.Secondary => "secondary",
        ReadPreferenceMode.SecondaryPreferred => "secondaryPreferred",
        ReadPreferenceMode.Nearest => "nearest",
        _ => "primary"
    };

    // ── 连接测试 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 按步骤测一遍:SSH 隧道 → TCP → 认证 → hello → 权限,最后列出副本集成员。
    /// 前一步断了,后面的报「未执行」。**连不上不抛**(失败是某一步的状态),只有取消会抛。测完把连接与隧道都关掉。
    /// </summary>
    /// <param name="request">连接请求(主机写的是真实地址;走跳板时由 <paramref name="openTunnel" /> 换成本地端点)。</param>
    /// <param name="loc">文案表。</param>
    /// <param name="translate">驱动异常 → 一句人话(认证失败、证书不受信任)。</param>
    /// <param name="openTunnel">配了 SSH 跳板时建隧道;没配为 <see langword="null" />。</param>
    /// <param name="progress">逐步进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>报告。</returns>
    public static async Task<ProbeReport> ProbeAsync(
        WorkspaceConnectRequest request,
        Loc loc,
        Func<Exception, MongoSettings, string> translate,
        Func<CancellationToken, Task<IMongoTunnel>>? openTunnel,
        IProgress<ProbeStep>? progress,
        CancellationToken cancellationToken)
    {
        var settings = MongoSettings.From(request);
        var run = new Run(loc, progress);
        string mechanism = settings.AuthMechanism is "DEFAULT" or "" ? "SCRAM" : settings.AuthMechanism;
        if (openTunnel is not null)
        {
            run.Declare("ssh", loc["Conn_StepSsh"]);
        }
        run.Declare("tcp", loc["Conn_StepTcp"]);
        run.Declare("auth", request.Username.Length > 0 ? loc.Format("Conn_StepAuth", mechanism) : loc["Conn_StepAuthNone"]);
        run.Declare("hello", "hello");
        run.Declare("privileges", loc["Conn_StepPrivileges"]);

        IMongoTunnel? tunnel = null;
        try
        {
            if (openTunnel is not null)
            {
                run.Start("ssh");
                var watch = Stopwatch.StartNew();
                try
                {
                    tunnel = await openTunnel(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    run.Finish("ssh", ProbeState.Failed, MongoConnector.Describe(ex));
                    return run.Report(null);
                }
                run.Finish("ssh", ProbeState.Passed,
                    loc.Format("Conn_SshDetail", tunnel.JumpName, tunnel.LocalPort, (int)watch.ElapsedMilliseconds), (int)watch.ElapsedMilliseconds);
                request = tunnel.Rewrite(request);
            }

            if (!await TcpStepAsync(run, request, settings, tunnel, loc, cancellationToken).ConfigureAwait(false))
            {
                return run.Report(null);
            }

            run.Start("auth");
            MongoConnection connection;
            try
            {
                connection = await MongoConnection.ConnectAsync(request, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Finish("auth", ProbeState.Failed, translate(ex, settings));
                return run.Report(null);
            }
            await using (connection.ConfigureAwait(false))
            {
                if (!await AuthStepAsync(run, connection, loc, cancellationToken).ConfigureAwait(false))
                {
                    return run.Report(null);
                }
                int? latency = await HelloStepAsync(run, connection, cancellationToken).ConfigureAwait(false);
                if (latency is null)
                {
                    return run.Report(null);
                }
                await PrivilegeStepAsync(run, connection, loc, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ProbeEndpoint> members = await MembersAsync(connection, loc, cancellationToken).ConfigureAwait(false);
                return run.Report(latency) with { EndpointsTitle = loc["Conn_Members"], Endpoints = members };
            }
        }
        finally
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> TcpStepAsync(
        Run run,
        WorkspaceConnectRequest request,
        MongoSettings settings,
        IMongoTunnel? tunnel,
        Loc loc,
        CancellationToken cancellationToken)
    {
        run.Start("tcp");
        if (tunnel is not null)
        {
            // 走跳板:目标的真实地址从本机够不着,在跳板机上敲它(经隧道开一条流)。
            try
            {
                int ms = await tunnel.CheckTargetAsync(cancellationToken).ConfigureAwait(false);
                run.Finish("tcp", ProbeState.Passed, loc.Format("Conn_TcpViaTunnel", tunnel.Target, ms), ms);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Finish("tcp", ProbeState.Failed, MongoConnector.Describe(ex));
                return false;
            }
        }
        List<EndPoint> targets;
        try
        {
            Target target = TargetOf(request, settings);
            if (target.Srv)
            {
                // SRV 的成员由驱动按 DNS 解析,这里没有现成的地址可敲;交给后面的认证一步去证明连得上。
                run.Finish("tcp", ProbeState.Skipped, loc["Conn_TcpSrv"]);
                return true;
            }
            targets = [.. target.Hosts.Select(ToEndPoint)];
        }
        catch (Exception ex) when (ex is MongoConfigurationException or ArgumentException or FormatException)
        {
            run.Finish("tcp", ProbeState.Failed, ex.Message);
            return false;
        }
        (bool Ok, int Ms)[] results = await Task.WhenAll(targets.Select(t => TcpAsync(t, cancellationToken))).ConfigureAwait(false);
        int reachable = results.Count(static r => r.Ok);
        int fastest = results.Where(static r => r.Ok).Select(static r => r.Ms).DefaultIfEmpty(0).Min();
        run.Finish("tcp",
            reachable == results.Length ? ProbeState.Passed : reachable > 0 ? ProbeState.Warning : ProbeState.Failed,
            loc.Format("Conn_TcpDetail", reachable, results.Length, fastest),
            reachable > 0 ? fastest : null);
        return reachable > 0;
    }

    /// <summary>一次 TCP 握手(3 秒超时)。量的是"端口通不通",与驱动的连接池无关。</summary>
    private static async Task<(bool Ok, int Ms)> TcpAsync(EndPoint endpoint, CancellationToken cancellationToken)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            return (true, (int)Math.Max(1, watch.ElapsedMilliseconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return (false, 0);
        }
    }

    private static async Task<bool> AuthStepAsync(Run run, MongoConnection connection, Loc loc, CancellationToken cancellationToken)
    {
        try
        {
            BsonDocument status = await connection.RunCommandAsync("admin", new BsonDocument("connectionStatus", 1), cancellationToken).ConfigureAwait(false);
            BsonArray users = status.GetValue("authInfo", new BsonDocument()).AsBsonDocument
                .GetValue("authenticatedUsers", new BsonArray()).AsBsonArray;
            run.Finish("auth", ProbeState.Passed, users.Count == 0
                ? loc["Conn_Anonymous"]
                : string.Join(", ", users.Select(static u => $"{u["user"].AsString}@{u["db"].AsString}")));
            return true;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            run.Finish("auth", ProbeState.Failed, MongoConnector.Describe(ex));
            return false;
        }
    }

    private static async Task<int?> HelloStepAsync(Run run, MongoConnection connection, CancellationToken cancellationToken)
    {
        run.Start("hello");
        try
        {
            var watch = Stopwatch.StartNew();
            BsonDocument hello = await connection.RunCommandAsync("admin", new BsonDocument("hello", 1), cancellationToken).ConfigureAwait(false);
            int ms = (int)Math.Max(1, watch.ElapsedMilliseconds);
            BsonDocument? build = null;
            try
            {
                build = await connection.RunCommandAsync("admin", new BsonDocument("buildInfo", 1), cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException)
            {
                // 低权限用户拿不到 buildInfo:版本空着,hello 本身是通的。
            }
            ServerInfo info = MongoConnection.ParseHello(hello, build);
            string head = info.SetName is { Length: > 0 } set ? $"{set} · {info.Role}" : info.Role;
            run.Finish("hello", ProbeState.Passed,
                $"{head} {info.Me}".TrimEnd() + (info.Version.Length > 0 ? $" · {info.Version}" : ""), ms);
            return ms;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            run.Finish("hello", ProbeState.Failed, MongoConnector.Describe(ex));
            return null;
        }
    }

    /// <summary>权限检查:账号能写哪些库(只读账号报警告色 —— 连得上,但写入功能会置灰)。</summary>
    private static async Task PrivilegeStepAsync(Run run, MongoConnection connection, Loc loc, CancellationToken cancellationToken)
    {
        run.Start("privileges");
        try
        {
            await connection.RefreshServerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            run.Finish("privileges", ProbeState.Warning, MongoConnector.Describe(ex));
            return;
        }
        PrivilegeSummary privileges = connection.Privileges;
        string roles = string.Join(loc["Conn_ListSeparator"], privileges.Roles.Select(static r => r.ToString()));
        if (privileges.User.Length == 0)
        {
            run.Finish("privileges", ProbeState.Passed, loc["Conn_PrivUnrestricted"]);
        }
        else if (!privileges.CanWriteAnything)
        {
            run.Finish("privileges", ProbeState.Warning, loc.Format("Conn_PrivReadOnly", roles.Length > 0 ? roles : "—"));
        }
        else
        {
            string writable = privileges.WritableDatabases.Contains("*")
                ? loc["Conn_PrivAllDatabases"]
                : string.Join(", ", privileges.WritableDatabases.Order(StringComparer.Ordinal));
            run.Finish("privileges", ProbeState.Passed, loc.Format("Conn_PrivWritable", roles, writable));
        }
    }

    /// <summary>
    /// 成员与复制延迟:优先 <c>replSetGetStatus</c>(要 clusterMonitor),拿不到就退回 <c>hello</c> 的成员表
    /// (只有地址与谁是主,没有延迟)。单机与 mongos 只有自己一行。
    /// </summary>
    internal static async Task<IReadOnlyList<ProbeEndpoint>> MembersAsync(MongoConnection connection, Loc loc, CancellationToken cancellationToken)
    {
        ServerInfo server = connection.Server;
        int? ping = connection.LatencyMs;
        string Ping(int? ms) => loc.Format("Conn_Ping", ms is { } value ? $"{value} ms" : "—");
        if (server.SetName is null)
        {
            return [new(server.Me.Length > 0 ? server.Me : connection.Endpoint, server.Role, Tone.Success, Ping(ping))];
        }
        var rows = new List<ProbeEndpoint>();
        try
        {
            BsonDocument status = await connection.RunCommandAsync("admin", new BsonDocument("replSetGetStatus", 1), cancellationToken).ConfigureAwait(false);
            List<BsonDocument> members = [.. status.GetValue("members", new BsonArray()).AsBsonArray.Select(static m => m.AsBsonDocument)];
            DateTime? primaryOptime = members
                .Where(static m => m.GetValue("stateStr", "").AsString == "PRIMARY")
                .Select(static m => m.TryGetValue("optimeDate", out BsonValue d) && d.IsValidDateTime ? d.ToUniversalTime() : (DateTime?)null)
                .FirstOrDefault();
            foreach (BsonDocument member in members)
            {
                string state = member.GetValue("stateStr", "").AsString;
                bool healthy = member.GetValue("health", 1).ToDouble() > 0;
                string detail;
                if (state == "PRIMARY")
                {
                    int? memberPing = member.GetValue("self", false).ToBoolean() ? ping
                        : member.TryGetValue("pingMs", out BsonValue pm) && pm.IsNumeric ? pm.ToInt32() : null;
                    detail = Ping(memberPing);
                }
                else if (primaryOptime is { } primary && member.TryGetValue("optimeDate", out BsonValue optime) && optime.IsValidDateTime)
                {
                    double seconds = Math.Max(0, (primary - optime.ToUniversalTime()).TotalSeconds);
                    detail = loc.Format("Conn_ReplLag", seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
                }
                else
                {
                    detail = loc.Format("Conn_ReplLag", "—");
                }
                rows.Add(new(member.GetValue("name", "").AsString, state, ToneOf(state, healthy), detail));
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            BsonDocument hello = await connection.RunCommandAsync("admin", new BsonDocument("hello", 1), cancellationToken).ConfigureAwait(false);
            string primary = hello.GetValue("primary", "").AsString;
            foreach (string host in Names(hello, "hosts").Concat(Names(hello, "passives")))
            {
                string role = host == primary ? "PRIMARY" : "SECONDARY";
                rows.Add(new(host, role, ToneOf(role, healthy: true), host == primary ? Ping(ping) : null));
            }
            foreach (string host in Names(hello, "arbiters"))
            {
                rows.Add(new(host, "ARBITER", Tone.Neutral));
            }
        }
        return rows;

        static IEnumerable<string> Names(BsonDocument doc, string field) =>
            doc.TryGetValue(field, out BsonValue list) && list.IsBsonArray
                ? list.AsBsonArray.Where(static v => v.IsString).Select(static v => v.AsString)
                : [];
    }

    private static Tone ToneOf(string state, bool healthy) => !healthy
        ? Tone.Danger
        : state switch
        {
            "PRIMARY" or "STANDALONE" or "MONGOS" => Tone.Success,
            "SECONDARY" => Tone.Info,
            "RECOVERING" or "STARTUP2" or "ROLLBACK" => Tone.Warning,
            _ => Tone.Neutral
        };

    /// <summary>一次测试的步骤簿:声明、开始、结束都即时报进度;报告里未执行的步骤一律「未执行」。</summary>
    private sealed class Run(Loc text, IProgress<ProbeStep>? progress)
    {
        private readonly List<ProbeStep> _steps = [];

        public void Declare(string key, string title)
        {
            var step = new ProbeStep(key, title, ProbeState.Pending);
            _steps.Add(step);
            progress?.Report(step);
        }

        public void Start(string key) => Set(key, ProbeState.Running, null, null);

        /// <summary>结束一步;返回这一步算不算通过(警告也算)。</summary>
        public bool Finish(string key, ProbeState state, string? detail, int? ms = null)
        {
            Set(key, state, detail, ms);
            return state is ProbeState.Passed or ProbeState.Warning or ProbeState.Skipped;
        }

        public ProbeReport Report(int? latency)
        {
            for (int i = 0; i < _steps.Count; i++)
            {
                if (_steps[i].State is ProbeState.Pending or ProbeState.Running)
                {
                    _steps[i] = _steps[i] with { State = ProbeState.Skipped, Detail = text["Conn_Skipped"] };
                    progress?.Report(_steps[i]);
                }
            }
            ProbeStep? failed = _steps.FirstOrDefault(static s => s.State == ProbeState.Failed);
            return new ProbeReport
            {
                Succeeded = failed is null,
                Summary = failed is null ? text.Format("Conn_TestOk", latency ?? 0) : text.Format("Conn_TestFailed", failed.Title),
                Steps = [.. _steps]
            };
        }

        private void Set(string key, ProbeState state, string? detail, int? ms)
        {
            int index = _steps.FindIndex(s => s.Key == key);
            if (index < 0)
            {
                return;
            }
            _steps[index] = _steps[index] with { State = state, Detail = detail ?? _steps[index].Detail, ElapsedMs = ms };
            progress?.Report(_steps[index]);
        }
    }
}
