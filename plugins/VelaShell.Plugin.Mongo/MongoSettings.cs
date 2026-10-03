using System.Globalization;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo;

/// <summary>服务器从哪里来。决定连接对话框的形态(主机行要不要端口、能不能走 SSH 跳板)。</summary>
public enum MongoTopology
{
    /// <summary>主机列表:主机 + 端口 + 其余成员。</summary>
    Hosts,

    /// <summary>SRV 记录:<c>mongodb+srv://</c>,端口与成员由 DNS 给出。</summary>
    Srv,

    /// <summary>整条连接字符串:写在"主机"那一栏里。</summary>
    Uri
}

/// <summary>
/// 环境标记。它**不是**装饰:只读默认值、写前确认、标签配色全由它派生 ——
/// "我现在在动线上"这件事必须无法被忽略。
/// </summary>
public enum MongoEnvironment
{
    /// <summary>开发。</summary>
    Development,

    /// <summary>测试。</summary>
    Testing,

    /// <summary>生产:默认只读,写前二次确认。</summary>
    Production
}

/// <summary>扩展 JSON 的三种写法(JSON 视图、复制、导出共用)。</summary>
public enum EjsonMode
{
    /// <summary>mongosh 写法:<c>ObjectId("…")</c>、<c>ISODate("…")</c>。</summary>
    Shell,

    /// <summary>Relaxed EJSON:数字与日期尽量用原生 JSON 表示。</summary>
    Relaxed,

    /// <summary>Canonical EJSON:每个值都带类型包装,往返无损。</summary>
    Canonical
}

/// <summary>
/// 一条 MongoDB 连接的强类型设置。已保存的连接(<see cref="Core.MongoProfile" />)以字符串键值存设置,
/// 这里是唯一的解析处(<see cref="From" />);连接对话框按同一批键读写,两边靠这些常量对齐。
/// </summary>
internal sealed record MongoSettings
{
    // 字段键:落进用户配置,**发布后不可更名**。
    internal const string KeyTopology = "topology";
    internal const string KeyHosts = "hosts";
    internal const string KeyReplicaSet = "replicaSet";
    internal const string KeyDirect = "directConnection";
    internal const string KeyDatabase = "database";
    internal const string KeyReadPreference = "readPreference";
    internal const string KeyAuthMechanism = "authMechanism";
    internal const string KeyAuthSource = "authSource";
    internal const string KeyTls = "tls";
    internal const string KeyTlsCaFile = "tlsCaFile";
    internal const string KeyTlsCertFile = "tlsCertificateKeyFile";
    internal const string KeyEnvironment = "environment";
    internal const string KeyReadOnly = "readOnly";
    internal const string KeyConfirmWrites = "confirmWrites";
    internal const string KeyDisableDropDatabase = "disableDropDatabase";
    internal const string KeyAppName = "appName";
    internal const string KeyConnectTimeout = "connectTimeout";
    internal const string KeyMaxTimeMs = "maxTimeMS";
    internal const string KeyPageSize = "pageSize";
    internal const string KeySampleSize = "sampleSize";
    internal const string KeyEjson = "ejsonMode";
    internal const string KeyShowSystemDatabases = "showSystemDatabases";
    internal const string KeyJumpSession = "jumpSession";
    internal const string KeyTrustedThumbprint = "trustedThumbprint";

    /// <summary>指纹回写字段的键(宿主在用户确认信任证书后写进这里)。</summary>
    public const string TrustedThumbprintKey = KeyTrustedThumbprint;

    /// <summary>服务器从哪里来。</summary>
    public MongoTopology Topology { get; init; } = MongoTopology.Hosts;

    /// <summary>其余成员(<c>host:port</c>,已拆好)。</summary>
    public IReadOnlyList<string> AdditionalHosts { get; init; } = [];

    /// <summary>副本集名称;空 = 不指定(由驱动发现)。</summary>
    public string ReplicaSet { get; init; } = "";

    /// <summary>直连,不做副本集发现。</summary>
    public bool DirectConnection { get; init; }

    /// <summary>默认数据库(对象树里默认展开它、查询编辑器默认 <c>use</c> 它)。</summary>
    public string Database { get; init; } = "";

    /// <summary>读偏好(<c>primary</c> / <c>secondaryPreferred</c> / …)。</summary>
    public string ReadPreference { get; init; } = "primary";

    /// <summary>认证机制;<c>DEFAULT</c> = 由驱动与服务器协商。</summary>
    public string AuthMechanism { get; init; } = "DEFAULT";

    /// <summary>认证库。</summary>
    public string AuthSource { get; init; } = "admin";

    /// <summary>是否使用 TLS。</summary>
    public bool UseTls { get; init; }

    /// <summary>自备 CA 证书(PEM)路径;空 = 系统信任链。</summary>
    public string TlsCaFile { get; init; } = "";

    /// <summary>客户端证书(PFX / PEM)路径;X.509 认证必填。</summary>
    public string TlsCertificateFile { get; init; } = "";

    /// <summary>环境标记。</summary>
    public MongoEnvironment Environment { get; init; } = MongoEnvironment.Development;

    /// <summary>以只读模式打开(生产默认开)。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>写操作前二次确认(生产默认开)。</summary>
    public bool ConfirmWrites { get; init; }

    /// <summary>不提供 dropDatabase。</summary>
    public bool DisableDropDatabase { get; init; } = true;

    /// <summary>上报给服务器的应用名。</summary>
    public string AppName { get; init; } = "velashell";

    /// <summary>连接 / 选服超时(毫秒)。</summary>
    public int ConnectTimeoutMs { get; init; } = 10_000;

    /// <summary>查询默认的 <c>maxTimeMS</c>。</summary>
    public int MaxTimeMs { get; init; } = 30_000;

    /// <summary>网格每页行数。</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Schema 分析与字段补全的抽样数。</summary>
    public int SampleSize { get; init; } = 1000;

    /// <summary>扩展 JSON 的默认写法。</summary>
    public EjsonMode Ejson { get; init; } = EjsonMode.Relaxed;

    /// <summary>对象树里列出 admin / config / local(默认不列;对象树表头的眼睛按钮可临时切换)。</summary>
    public bool ShowSystemDatabases { get; init; }

    /// <summary>经哪条 SSH 配置抵达(插件不用它做事,只为在界面上如实显示来路)。</summary>
    public string JumpSessionId { get; init; } = "";

    /// <summary>用户已确认信任的服务器证书指纹;为空表示还没信任过。</summary>
    public string TrustedThumbprint { get; init; } = "";

    /// <summary>从宿主递来的连接请求解析。缺失/不可解析的一律回落到声明的默认值。</summary>
    /// <param name="request">连接请求。</param>
    /// <returns>强类型设置。</returns>
    public static MongoSettings From(WorkspaceConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        MongoTopology topology = request.GetString(KeyTopology, "hosts") switch
        {
            "srv" => MongoTopology.Srv,
            "uri" => MongoTopology.Uri,
            _ => MongoTopology.Hosts
        };
        MongoEnvironment environment = request.GetString(KeyEnvironment, "development") switch
        {
            "production" => MongoEnvironment.Production,
            "testing" => MongoEnvironment.Testing,
            _ => MongoEnvironment.Development
        };
        bool production = environment == MongoEnvironment.Production;
        return new()
        {
            Topology = topology,
            AdditionalHosts = SplitHosts(request.GetString(KeyHosts)),
            ReplicaSet = request.GetString(KeyReplicaSet).Trim(),
            DirectConnection = request.GetBoolean(KeyDirect),
            Database = request.GetString(KeyDatabase).Trim(),
            ReadPreference = request.GetString(KeyReadPreference, "primary"),
            AuthMechanism = request.GetString(KeyAuthMechanism, "DEFAULT"),
            AuthSource = request.GetString(KeyAuthSource, "admin").Trim(),
            UseTls = request.GetBoolean(KeyTls, topology == MongoTopology.Srv),
            TlsCaFile = request.GetString(KeyTlsCaFile).Trim(),
            TlsCertificateFile = request.GetString(KeyTlsCertFile).Trim(),
            Environment = environment,
            // 只读与写前确认的默认值随环境走,但用户显式设过就听用户的 ——
            // 所以要看"键在不在",不能只看解析出来的布尔值(分不清"关掉了"与"没配过")。
            ReadOnly = ExplicitBool(request, KeyReadOnly) ?? production,
            ConfirmWrites = ExplicitBool(request, KeyConfirmWrites) ?? production,
            DisableDropDatabase = request.GetBoolean(KeyDisableDropDatabase, true),
            AppName = request.GetString(KeyAppName, "velashell"),
            ConnectTimeoutMs = Math.Clamp(request.GetInt32(KeyConnectTimeout, 10_000), 500, 120_000),
            MaxTimeMs = Math.Clamp(request.GetInt32(KeyMaxTimeMs, 30_000), 0, 3_600_000),
            PageSize = Math.Clamp(request.GetInt32(KeyPageSize, 50), 10, 1000),
            SampleSize = Math.Clamp(request.GetInt32(KeySampleSize, 1000), 50, 100_000),
            Ejson = request.GetString(KeyEjson, "relaxed") switch
            {
                "shell" => EjsonMode.Shell,
                "canonical" => EjsonMode.Canonical,
                _ => EjsonMode.Relaxed
            },
            ShowSystemDatabases = request.GetBoolean(KeyShowSystemDatabases),
            JumpSessionId = request.GetString(KeyJumpSession),
            TrustedThumbprint = request.GetString(KeyTrustedThumbprint)
        };
    }

    private static bool? ExplicitBool(WorkspaceConnectRequest request, string key) =>
        request.Settings.TryGetValue(key, out string? raw) && bool.TryParse(raw, out bool parsed) ? parsed : null;

    /// <summary>把"其余成员"一栏拆成 <c>host:port</c> 列表(逗号、分号、空白都算分隔)。</summary>
    internal static IReadOnlyList<string> SplitHosts(string raw) =>
    [
        .. raw.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ];

    /// <summary>给日志与界面的一行摘要(不含任何凭据)。</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Topology} rs={ReplicaSet} direct={DirectConnection} tls={UseTls} env={Environment} ro={ReadOnly}");
}
