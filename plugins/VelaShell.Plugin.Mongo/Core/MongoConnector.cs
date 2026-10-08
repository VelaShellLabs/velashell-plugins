using System.Security.Authentication;
using MongoDB.Driver;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>连接失败的种类:失败卡片按它决定给哪些按钮(认证失败 → 编辑连接;证书 → 信任并重连)。</summary>
internal enum ConnectFailureKind
{
    /// <summary>连不上(网络、超时、拒连)。</summary>
    Network,

    /// <summary>认证失败。</summary>
    Authentication,

    /// <summary>服务器证书不受信任。</summary>
    Certificate,

    /// <summary>连接串 / 配置写错了。</summary>
    Configuration,

    /// <summary>SSH 跳板没建起来。</summary>
    Tunnel
}

/// <summary>服务器证书的样子(信任一次就记住它的指纹)。</summary>
/// <param name="Subject">主题。</param>
/// <param name="Issuer">签发者。</param>
/// <param name="NotAfter">到期。</param>
/// <param name="Thumbprint">SHA-1 指纹。</param>
/// <param name="PolicyErrors">校验失败的原因。</param>
internal sealed record CertificateFacts(string Subject, string Issuer, DateTime NotAfter, string Thumbprint, string PolicyErrors);

/// <summary>连接失败,消息已是人话。</summary>
internal sealed class MongoConnectException(string message, ConnectFailureKind kind, Exception? inner = null, CertificateFacts? certificate = null)
    : Exception(message, inner)
{
    /// <summary>种类。</summary>
    public ConnectFailureKind Kind { get; } = kind;

    /// <summary>证书(<see cref="ConnectFailureKind.Certificate" /> 且拿到了证书本体时)。</summary>
    public CertificateFacts? Certificate { get; } = certificate;
}

/// <summary>连上的一条:驱动连接 + (走跳板时)那条转发,两者同生共死。</summary>
/// <param name="Connection">连接。</param>
/// <param name="Tunnel">转发;直连为 <see langword="null" />。</param>
internal sealed record MongoLink(MongoConnection Connection, IMongoTunnel? Tunnel) : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);
        if (Tunnel is not null)
        {
            await Tunnel.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 按已保存的连接去连、去测。连接由插件自己管(不经宿主的会话),所以 SSH 跳板也由这里经宿主的
/// SSH 会话转发(<see cref="SshTunnel" />),失败也由这里翻译成人话交给工作台的失败卡片。
/// </summary>
/// <param name="context">插件上下文。</param>
/// <param name="loc">文案表。</param>
internal sealed class MongoConnector(IPluginContext context, Loc loc)
{
    /// <summary>文案表。</summary>
    public Loc Loc { get; } = loc;

    /// <summary>连上(走跳板时先建转发)。失败抛 <see cref="MongoConnectException" />。</summary>
    /// <param name="profile">连接。</param>
    /// <param name="password">口令。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连上的一条。</returns>
    public async Task<MongoLink> ConnectAsync(MongoProfile profile, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        WorkspaceConnectRequest request = profile.ToRequest(password);
        var settings = MongoSettings.From(request);
        IMongoTunnel? tunnel = null;
        if (UsesTunnel(settings))
        {
            try
            {
                tunnel = await OpenTunnelAsync(profile, request, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MongoConnectException(Loc.Format("Conn_TunnelFailed", Describe(ex)), ConnectFailureKind.Tunnel, ex);
            }
            request = tunnel.Rewrite(request);
        }
        try
        {
            MongoConnection connection = await MongoConnection.ConnectAsync(request, settings, cancellationToken).ConfigureAwait(false);
            context.Log.Info($"Connected to {connection.Endpoint} (MongoDB {connection.Server.Version}, {connection.Server.Badge}, {settings}).");
            return new MongoLink(connection, tunnel);
        }
        catch (Exception ex)
        {
            if (tunnel is not null)
            {
                await tunnel.DisposeAsync().ConfigureAwait(false);
            }
            if (ex is OperationCanceledException)
            {
                throw;
            }
            throw Translate(ex, profile.ToRequest(""), settings);
        }
    }

    /// <summary>测试连接(设计稿 10 的右侧栏):逐步报进度,失败是某一步的状态,只有取消会抛。</summary>
    /// <param name="profile">对话框里的草稿。</param>
    /// <param name="password">口令。</param>
    /// <param name="progress">逐步进度。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>报告。</returns>
    public Task<ProbeReport> ProbeAsync(MongoProfile profile, string password, IProgress<ProbeStep>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        WorkspaceConnectRequest request = profile.ToRequest(password);
        var settings = MongoSettings.From(request);
        Func<CancellationToken, Task<IMongoTunnel>>? openTunnel = UsesTunnel(settings)
            ? ct => OpenTunnelAsync(profile, request, settings, ct)
            : null;
        return ConnectionProbe.ProbeAsync(request, Loc, (ex, s) => Translate(ex, profile.ToRequest(""), s).Message, openTunnel, progress, cancellationToken);
    }

    /// <summary>SRV 没有跳板可走:它靠 DNS 解析出一组成员,一条本地转发只通得到其中一台。</summary>
    /// <param name="settings">设置。</param>
    /// <returns>要不要建转发。</returns>
    public static bool UsesTunnel(MongoSettings settings) =>
        settings.JumpSessionId.Length > 0 && settings.Topology != MongoTopology.Srv;

    private async Task<IMongoTunnel> OpenTunnelAsync(MongoProfile profile, WorkspaceConnectRequest request, MongoSettings settings, CancellationToken cancellationToken)
    {
        (string host, int port) = SshTunnel.TargetOf(request, settings);
        string name = profile.Name.Length > 0 ? profile.Name : $"{host}:{port}";
        return await SshTunnel.OpenAsync(context, settings.JumpSessionId, host, port,
            Loc.Format("Conn_SshReason", name, $"{host}:{port}"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>驱动异常 → 一句人话 + 种类。认证失败与证书不受信任必须单独认出来:前者该去改口令,后者可以当场信任。</summary>
    /// <param name="ex">异常。</param>
    /// <param name="request">请求(不含口令,只用来描述端点)。</param>
    /// <param name="settings">设置。</param>
    /// <returns>翻译后的异常。</returns>
    internal MongoConnectException Translate(Exception ex, WorkspaceConnectRequest request, MongoSettings settings)
    {
        if (ex is MongoConnectException already)
        {
            return already;
        }
        IReadOnlyList<Exception> causes = ex is MongoConnectFailedException failed ? failed.Causes : [];
        Exception primary = ex is MongoConnectFailedException ? ex.InnerException ?? ex : ex;
        string endpoint = settings.Topology switch
        {
            MongoTopology.Uri => MongoConnection.RedactUri(request.Host),
            MongoTopology.Srv => request.Host.Trim(),
            _ => $"{request.Host.Trim()}:{request.Port}"
        };

        if (Find<MongoAuthenticationException>(primary, causes) is { } auth)
        {
            return new(Loc.Format("Mongo_AuthFailed", Describe(auth)), ConnectFailureKind.Authentication, auth);
        }
        if (Find<AuthenticationException>(primary, causes) is { } tls)
        {
            // 拿不到证书本体时不能假装拿到了 —— 那会让「信任此证书」信任一张空证书。
            TlsTrust? trust = (ex as MongoConnectFailedException)?.Trust;
            CertificateFacts? facts = trust?.SeenCertificate is { } certificate
                ? new(certificate.Subject, certificate.Issuer, certificate.NotAfter, certificate.Thumbprint, trust.PolicyErrors.ToString())
                : null;
            return new(Loc.Format("Mongo_ConnectFailed", endpoint, Describe(tls)), ConnectFailureKind.Certificate, tls, facts);
        }
        if (Find<MongoConfigurationException>(primary, causes) is { } config)
        {
            return new(Loc.Format("Mongo_BadUri", Describe(config)), ConnectFailureKind.Configuration, config);
        }
        if (primary is MongoCommandException { Code: 13 } command)
        {
            // Unauthorized:连上了,但连 hello 之后的探测都不让做 —— 对用户来说就是凭据不对。
            return new(Loc.Format("Mongo_AuthFailed", Describe(command)), ConnectFailureKind.Authentication, command);
        }
        Exception reason = causes.FirstOrDefault() ?? primary;
        return primary switch
        {
            ArgumentException or FormatException => new(Loc.Format("Mongo_BadUri", primary.Message), ConnectFailureKind.Configuration, primary),
            _ => new(Loc.Format("Mongo_ConnectFailed", endpoint, Describe(Root(reason))), ConnectFailureKind.Network, primary)
        };
    }

    private static T? Find<T>(Exception primary, IReadOnlyList<Exception> causes) where T : Exception
    {
        foreach (Exception start in causes.Prepend(primary))
        {
            for (Exception? current = start; current is not null; current = current.InnerException)
            {
                if (current is T match)
                {
                    return match;
                }
            }
        }
        return null;
    }

    private static Exception Root(Exception ex)
    {
        Exception current = ex;
        while (current.InnerException is { } inner && current is MongoConnectionException or MongoConnectFailedException)
        {
            current = inner;
        }
        return current;
    }

    /// <summary>异常的一行说明:去掉驱动附在后面的整段集群状态,截到 400 字。</summary>
    /// <param name="ex">异常。</param>
    /// <returns>说明。</returns>
    internal static string Describe(Exception ex)
    {
        string message = ex.Message.Trim();
        int cluster = message.IndexOf("Client view of cluster state", StringComparison.OrdinalIgnoreCase);
        if (cluster > 0)
        {
            message = message[..cluster].TrimEnd(' ', '.', ',');
        }
        return message.Length > 400 ? message[..400] + "…" : message;
    }
}
