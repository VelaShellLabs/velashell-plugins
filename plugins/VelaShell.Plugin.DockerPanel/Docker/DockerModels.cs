using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// Docker Engine API 的 DTO。
/// <para>
/// 只声明面板真正读的字段。daemon 的响应比这里宽得多,而反序列化器对多出来的字段
/// 一律忽略 —— 把整个 schema 抄下来只会让每次 Docker 升级都变成一次维护事件。
/// 需要看"全部"的地方(详情抽屉的 inspect 页)直接呈现原始 JSON,不经过 DTO。
/// </para>
/// </summary>
public static class DockerJson
{
    /// <summary>全局共用的序列化设置。</summary>
    /// <remarks>
    /// 大小写不敏感是必需的:Docker 的字段大多是 PascalCase(<c>Id</c>、<c>Names</c>),
    /// 但统计与事件那几条流混着 snake_case(<c>cpu_stats</c>、<c>timeNano</c>)。
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly JsonSerializerOptions HumanJson = new() { WriteIndented = true };

    /// <summary>把一段 JSON 反序列化成 <typeparamref name="T" />;失败返回 <see langword="null" />。</summary>
    /// <remarks>
    /// 先看一眼首字符再解析。这条路上最常见的输入根本不是 JSON —— 反代挡下来的 HTML、
    /// socket 权限不足时的一行纯文本、空响应体,都会走到这里。为这些"抛出再吞掉"
    /// 一次异常,除了在调试器里刷一屏首次异常之外没有任何收益。
    /// </remarks>
    public static T? TryDeserialize<T>(string json) where T : class
    {
        var body = json.AsSpan().Trim();
        if (body.IsEmpty || (body[0] != '{' && body[0] != '['))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把 JSON 重新缩进成人能读的样子;不是合法 JSON 就原样返回。</summary>
    public static string Prettify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, HumanJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}

/// <summary>daemon 的错误响应体。</summary>
public sealed record DockerErrorBody
{
    /// <summary>人话形式的失败原因。</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

// ─────────────────────────── 容器 ───────────────────────────

/// <summary>端口映射(列表接口给的形态)。</summary>
public sealed record DockerPort
{
    /// <summary>宿主侧监听地址;未发布时为空。</summary>
    public string? IP { get; init; }

    /// <summary>容器内端口。</summary>
    public int PrivatePort { get; init; }

    /// <summary>宿主侧端口;未发布时为 0。</summary>
    public int PublicPort { get; init; }

    /// <summary>tcp / udp / sctp。</summary>
    public string? Type { get; init; }
}

/// <summary>挂载点(列表与 inspect 共用)。</summary>
public sealed record DockerMount
{
    /// <summary>bind / volume / tmpfs。</summary>
    public string? Type { get; init; }

    /// <summary>命名卷的名字;bind 挂载为空。</summary>
    public string? Name { get; init; }

    /// <summary>宿主侧路径(bind)或卷的挂载点(volume)。</summary>
    public string? Source { get; init; }

    /// <summary>容器内路径。</summary>
    public string? Destination { get; init; }

    /// <summary>驱动。</summary>
    public string? Driver { get; init; }

    /// <summary>是否可写。</summary>
    public bool RW { get; init; }
}

/// <summary>容器列表项(<c>GET /containers/json</c>)。</summary>
public sealed record ContainerSummary
{
    /// <summary>完整 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>名字,带前导斜杠(daemon 的形态)。</summary>
    public string[]? Names { get; init; }

    /// <summary>镜像引用。</summary>
    public string? Image { get; init; }

    /// <summary>镜像 id。</summary>
    public string? ImageID { get; init; }

    /// <summary>入口命令。</summary>
    public string? Command { get; init; }

    /// <summary>创建时间(unix 秒)。</summary>
    public long Created { get; init; }

    /// <summary>created / running / paused / restarting / removing / exited / dead。</summary>
    public string? State { get; init; }

    /// <summary>人话状态,如 <c>Up 3 days (healthy)</c>。</summary>
    public string? Status { get; init; }

    /// <summary>端口映射。</summary>
    public DockerPort[]? Ports { get; init; }

    /// <summary>标签。</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>挂载。</summary>
    public DockerMount[]? Mounts { get; init; }

    /// <summary>
    /// 可写层占用的字节数。
    /// <para>
    /// <c>/system/df</c> 永远给这个值,而 <c>/containers/json</c> 只在带
    /// <c>size=1</c> 时才算(那要对每个容器做一次 diff,贵得多)—— 所以列表页拿到的是 0,
    /// 系统页拿到的是真值。判空要看是不是 <c>0</c>,不要当成"容器没占空间"。
    /// </para>
    /// </summary>
    public long SizeRw { get; init; }

    /// <summary>包含镜像层在内的总占用;同样只有 <c>/system/df</c> 与 <c>size=1</c> 才给。</summary>
    public long SizeRootFs { get; init; }

    /// <summary>去掉前导斜杠的第一个名字。</summary>
    public string Name => Names is { Length: > 0 } n ? n[0].TrimStart('/') : Id[..Math.Min(12, Id.Length)];

    /// <summary>compose 项目名(来自标准标签)。</summary>
    public string? ComposeProject => Labels?.GetValueOrDefault("com.docker.compose.project");

    /// <summary>compose 服务名。</summary>
    public string? ComposeService => Labels?.GetValueOrDefault("com.docker.compose.service");
}

/// <summary>健康检查状态。</summary>
public sealed record ContainerHealth
{
    /// <summary>starting / healthy / unhealthy / none。</summary>
    public string? Status { get; init; }

    /// <summary>连续失败次数。</summary>
    public int FailingStreak { get; init; }
}

/// <summary>容器状态(inspect)。</summary>
public sealed record ContainerState
{
    /// <summary>状态字符串。</summary>
    public string? Status { get; init; }

    /// <summary>是否在跑。</summary>
    public bool Running { get; init; }

    /// <summary>是否暂停。</summary>
    public bool Paused { get; init; }

    /// <summary>是否正在重启。</summary>
    public bool Restarting { get; init; }

    /// <summary>是否被 OOM 杀掉。</summary>
    public bool OOMKilled { get; init; }

    /// <summary>进程号。</summary>
    public int Pid { get; init; }

    /// <summary>退出码。</summary>
    public int ExitCode { get; init; }

    /// <summary>daemon 记下的错误。</summary>
    public string? Error { get; init; }

    /// <summary>启动时间。</summary>
    public string? StartedAt { get; init; }

    /// <summary>结束时间。</summary>
    public string? FinishedAt { get; init; }

    /// <summary>健康检查。</summary>
    public ContainerHealth? Health { get; init; }
}

/// <summary>重启策略。</summary>
public sealed record RestartPolicy
{
    /// <summary>no / on-failure / always / unless-stopped。</summary>
    public string? Name { get; init; }

    /// <summary>on-failure 的最大重试次数。</summary>
    public int MaximumRetryCount { get; init; }
}

/// <summary>宿主配置(inspect,只取面板用到的)。</summary>
public sealed record ContainerHostConfig
{
    /// <summary>重启策略。</summary>
    public RestartPolicy? RestartPolicy { get; init; }

    /// <summary>网络模式。</summary>
    public string? NetworkMode { get; init; }

    /// <summary>是否特权。</summary>
    public bool Privileged { get; init; }

    /// <summary>内存上限(字节),0 表示不限。</summary>
    public long Memory { get; init; }

    /// <summary>CPU 配额。</summary>
    public long NanoCpus { get; init; }

    /// <summary>退出即删除(<c>--rm</c>)。</summary>
    public bool AutoRemove { get; init; }

    /// <summary>
    /// 端口映射声明,键是 <c>80/tcp</c>,值是绑定列表。
    /// <para>
    /// 用它而不是 <c>ContainerSummary.Ports</c> 重建 <c>docker run</c>:后者是**运行态**,
    /// 容器停了就是空的,而"这个停掉的容器当初是怎么起的"恰恰是最需要复制那条命令的时候。
    /// </para>
    /// </summary>
    public Dictionary<string, PortBinding[]?>? PortBindings { get; init; }

    /// <summary>绑定挂载,<c>源:目标[:模式]</c> 形态。</summary>
    public string[]? Binds { get; init; }
}

/// <summary>一条端口绑定。</summary>
public sealed record PortBinding
{
    /// <summary>宿主 IP;留空是全部接口。</summary>
    public string? HostIp { get; init; }

    /// <summary>宿主端口。</summary>
    public string? HostPort { get; init; }
}

/// <summary>容器配置(inspect,只取面板用到的)。</summary>
public sealed record ContainerConfig
{
    /// <summary>主机名。</summary>
    public string? Hostname { get; init; }

    /// <summary>镜像引用。</summary>
    public string? Image { get; init; }

    /// <summary>环境变量,<c>KEY=VALUE</c> 形态。</summary>
    public string[]? Env { get; init; }

    /// <summary>命令。</summary>
    public string[]? Cmd { get; init; }

    /// <summary>入口点。</summary>
    public string[]? Entrypoint { get; init; }

    /// <summary>工作目录。</summary>
    public string? WorkingDir { get; init; }

    /// <summary>用户。</summary>
    public string? User { get; init; }

    /// <summary>标签。</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>
    /// 声明暴露的端口,键是 <c>80/tcp</c> 形态,值恒为空对象 ——
    /// Docker 用它当集合使,面板只读键。
    /// </summary>
    public Dictionary<string, object>? ExposedPorts { get; init; }
}

/// <summary><c>POST /commit</c> 的响应。</summary>
public sealed record CommitResponse
{
    /// <summary>新镜像的 id。</summary>
    public string Id { get; init; } = "";
}

/// <summary>镜像的层构成。</summary>
public sealed record ImageRootFs
{
    /// <summary>类型,基本恒为 <c>layers</c>。</summary>
    public string? Type { get; init; }

    /// <summary>各层的 diff id。</summary>
    public string[]? Layers { get; init; }
}

/// <summary>一个镜像的完整信息(<c>docker image inspect</c>)。</summary>
public sealed record ImageInspect
{
    /// <summary>镜像 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>标签。</summary>
    public string[]? RepoTags { get; init; }

    /// <summary>摘要引用。</summary>
    public string[]? RepoDigests { get; init; }

    /// <summary>作者留下的注释。</summary>
    public string? Comment { get; init; }

    /// <summary>创建时间,RFC3339 字符串(不是 unix 秒 —— 与列表接口不一样)。</summary>
    public string? Created { get; init; }

    /// <summary>构建它的 Docker 版本。</summary>
    public string? DockerVersion { get; init; }

    /// <summary>作者。</summary>
    public string? Author { get; init; }

    /// <summary>架构(<c>amd64</c> / <c>arm64</c>…)。</summary>
    public string? Architecture { get; init; }

    /// <summary>架构变体(<c>v7</c>…)。</summary>
    public string? Variant { get; init; }

    /// <summary>操作系统。</summary>
    public string? Os { get; init; }

    /// <summary>大小。</summary>
    public long Size { get; init; }

    /// <summary>运行时默认配置。</summary>
    public ContainerConfig? Config { get; init; }

    /// <summary>层构成。</summary>
    public ImageRootFs? RootFS { get; init; }
}

/// <summary>容器接入的一个网络。</summary>
public sealed record EndpointSettings
{
    /// <summary>网络 id。</summary>
    public string? NetworkID { get; init; }

    /// <summary>容器在该网络中的 IPv4。</summary>
    public string? IPAddress { get; init; }

    /// <summary>网关。</summary>
    public string? Gateway { get; init; }

    /// <summary>MAC 地址。</summary>
    public string? MacAddress { get; init; }

    /// <summary>网络别名。</summary>
    public string[]? Aliases { get; init; }
}

/// <summary>网络设置(inspect)。</summary>
public sealed record ContainerNetworkSettings
{
    /// <summary>按网络名索引的接入信息。</summary>
    public Dictionary<string, EndpointSettings>? Networks { get; init; }
}

/// <summary>容器 inspect。</summary>
public sealed record ContainerInspect
{
    /// <summary>完整 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>名字,带前导斜杠。</summary>
    public string? Name { get; init; }

    /// <summary>创建时间(ISO8601)。</summary>
    public string? Created { get; init; }

    /// <summary>镜像 id。</summary>
    public string? Image { get; init; }

    /// <summary>平台,如 <c>linux</c>。</summary>
    public string? Platform { get; init; }

    /// <summary>重启次数。</summary>
    public int RestartCount { get; init; }

    /// <summary>状态。</summary>
    public ContainerState? State { get; init; }

    /// <summary>宿主配置。</summary>
    public ContainerHostConfig? HostConfig { get; init; }

    /// <summary>容器配置。</summary>
    public ContainerConfig? Config { get; init; }

    /// <summary>挂载。</summary>
    public DockerMount[]? Mounts { get; init; }

    /// <summary>网络设置。</summary>
    public ContainerNetworkSettings? NetworkSettings { get; init; }

    /// <summary>去掉前导斜杠的名字。</summary>
    public string DisplayName => Name?.TrimStart('/') ?? Id[..Math.Min(12, Id.Length)];
}

/// <summary><c>docker top</c> 的结果。</summary>
public sealed record ContainerTopResult
{
    /// <summary>列标题。</summary>
    public string[]? Titles { get; init; }

    /// <summary>每行一个进程,列与 <see cref="Titles" /> 对应。</summary>
    public string[][]? Processes { get; init; }
}

/// <summary>容器可写层相对镜像的一处变更(<c>docker diff</c>)。</summary>
public sealed record FilesystemChange
{
    /// <summary>路径。</summary>
    public string Path { get; init; } = "";

    /// <summary>0=修改 1=新增 2=删除。</summary>
    public int Kind { get; init; }

    /// <summary>单字母标记,与 <c>docker diff</c> 的输出一致。</summary>
    public string Marker => Kind switch { 1 => "A", 2 => "D", _ => "C" };
}

/// <summary>创建容器的响应。</summary>
public sealed record CreateContainerResponse
{
    /// <summary>新容器 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>daemon 的警告(端口冲突提醒之类)。</summary>
    public string[]? Warnings { get; init; }
}

// ─────────────────────────── 镜像 ───────────────────────────

/// <summary>镜像列表项。</summary>
public sealed record ImageSummary
{
    /// <summary>镜像 id,带 <c>sha256:</c> 前缀。</summary>
    public string Id { get; init; } = "";

    /// <summary>标签,<c>repo:tag</c> 形态;悬空镜像为 <c>&lt;none&gt;:&lt;none&gt;</c>。</summary>
    public string[]? RepoTags { get; init; }

    /// <summary>摘要引用。</summary>
    public string[]? RepoDigests { get; init; }

    /// <summary>创建时间(unix 秒)。</summary>
    public long Created { get; init; }

    /// <summary>大小(字节)。</summary>
    public long Size { get; init; }

    /// <summary>与其它镜像共享的层大小。</summary>
    public long SharedSize { get; init; }

    /// <summary>使用它的容器数;<c>-1</c> 表示 daemon 没算。</summary>
    public int Containers { get; init; } = -1;

    /// <summary>标签。</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>短 id(去掉 <c>sha256:</c>,取前 12 位)。</summary>
    public string ShortId => Id.StartsWith("sha256:", StringComparison.Ordinal)
        ? Id[7..Math.Min(19, Id.Length)]
        : Id[..Math.Min(12, Id.Length)];

    /// <summary>没有任何标签 —— 即悬空镜像。</summary>
    public bool IsDangling =>
        RepoTags is null or { Length: 0 } || RepoTags.All(t => t == "<none>:<none>");
}

/// <summary>镜像构建历史的一层。</summary>
public sealed record ImageHistoryEntry
{
    /// <summary>层 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>创建时间(unix 秒)。</summary>
    public long Created { get; init; }

    /// <summary>产生这一层的指令。</summary>
    public string? CreatedBy { get; init; }

    /// <summary>大小。</summary>
    public long Size { get; init; }

    /// <summary>注释。</summary>
    public string? Comment { get; init; }

    /// <summary>标签。</summary>
    public string[]? Tags { get; init; }
}

// ─────────────────────────── 卷 / 网络 ───────────────────────────

/// <summary>卷的用量(仅 <c>/system/df</c> 会填)。</summary>
public sealed record VolumeUsageData
{
    /// <summary>占用字节;<c>-1</c> 表示未统计。</summary>
    public long Size { get; init; } = -1;

    /// <summary>被多少容器引用。</summary>
    public int RefCount { get; init; }
}

/// <summary>卷。</summary>
public sealed record VolumeSummary
{
    /// <summary>卷名。</summary>
    public string Name { get; init; } = "";

    /// <summary>驱动。</summary>
    public string? Driver { get; init; }

    /// <summary>宿主上的挂载点。</summary>
    public string? Mountpoint { get; init; }

    /// <summary>创建时间(ISO8601)。</summary>
    public string? CreatedAt { get; init; }

    /// <summary>作用域。</summary>
    public string? Scope { get; init; }

    /// <summary>标签。</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>驱动选项。</summary>
    public Dictionary<string, string>? Options { get; init; }

    /// <summary>用量。</summary>
    public VolumeUsageData? UsageData { get; init; }
}

/// <summary>卷列表的响应外壳。</summary>
public sealed record VolumeListResponse
{
    /// <summary>卷。</summary>
    public VolumeSummary[]? Volumes { get; init; }

    /// <summary>daemon 的警告。</summary>
    public string[]? Warnings { get; init; }
}

/// <summary>IPAM 的一段配置。</summary>
public sealed record IpamConfig
{
    /// <summary>子网。</summary>
    public string? Subnet { get; init; }

    /// <summary>网关。</summary>
    public string? Gateway { get; init; }

    /// <summary>可分配范围。</summary>
    public string? IPRange { get; init; }
}

/// <summary>网络的 IPAM。</summary>
public sealed record IpamSettings
{
    /// <summary>驱动。</summary>
    public string? Driver { get; init; }

    /// <summary>配置段。</summary>
    public IpamConfig[]? Config { get; init; }
}

/// <summary>接在某网络上的一个容器。</summary>
public sealed record NetworkContainer
{
    /// <summary>容器名。</summary>
    public string? Name { get; init; }

    /// <summary>IPv4 地址(带掩码)。</summary>
    public string? IPv4Address { get; init; }

    /// <summary>MAC。</summary>
    public string? MacAddress { get; init; }
}

/// <summary>网络。</summary>
public sealed record NetworkSummary
{
    /// <summary>网络 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>网络名。</summary>
    public string Name { get; init; } = "";

    /// <summary>创建时间。</summary>
    public string? Created { get; init; }

    /// <summary>作用域。</summary>
    public string? Scope { get; init; }

    /// <summary>驱动。</summary>
    public string? Driver { get; init; }

    /// <summary>是否启用 IPv6。</summary>
    public bool EnableIPv6 { get; init; }

    /// <summary>是否内部网络(不通外网)。</summary>
    public bool Internal { get; init; }

    /// <summary>是否允许事后接入。</summary>
    public bool Attachable { get; init; }

    /// <summary>IPAM。</summary>
    public IpamSettings? IPAM { get; init; }

    /// <summary>已接入的容器,按容器 id 索引。</summary>
    public Dictionary<string, NetworkContainer>? Containers { get; init; }

    /// <summary>标签。</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>选项。</summary>
    public Dictionary<string, string>? Options { get; init; }

    /// <summary>
    /// Docker 内置的三个网络删不掉,界面据此把删除按钮直接置灰 ——
    /// 而不是让用户去撞一条 daemon 的错误。
    /// </summary>
    public bool IsPredefined => Name is "bridge" or "host" or "none";

    /// <summary>第一段子网(界面上只显示一段)。</summary>
    public string? FirstSubnet => IPAM?.Config is { Length: > 0 } c ? c[0].Subnet : null;

    /// <summary>第一段网关。</summary>
    public string? FirstGateway => IPAM?.Config is { Length: > 0 } c ? c[0].Gateway : null;
}

// ─────────────────────────── 系统 ───────────────────────────

/// <summary><c>GET /version</c>。</summary>
public sealed record SystemVersion
{
    /// <summary>Engine 版本。</summary>
    public string? Version { get; init; }

    /// <summary>当前 API 版本。</summary>
    public string? ApiVersion { get; init; }

    /// <summary>支持的最低 API 版本。</summary>
    public string? MinAPIVersion { get; init; }

    /// <summary>提交号。</summary>
    public string? GitCommit { get; init; }

    /// <summary>Go 版本。</summary>
    public string? GoVersion { get; init; }

    /// <summary>操作系统。</summary>
    public string? Os { get; init; }

    /// <summary>架构。</summary>
    public string? Arch { get; init; }

    /// <summary>内核版本。</summary>
    public string? KernelVersion { get; init; }
}

/// <summary>Swarm 状态(只取节点状态)。</summary>
public sealed record SwarmInfo
{
    /// <summary>inactive / pending / active / error / locked。</summary>
    public string? LocalNodeState { get; init; }
}

/// <summary><c>GET /info</c>。</summary>
public sealed record SystemInfo
{
    /// <summary>daemon 唯一 id。</summary>
    public string? ID { get; init; }

    /// <summary>daemon 主机名。</summary>
    public string? Name { get; init; }

    /// <summary>容器总数。</summary>
    public int Containers { get; init; }

    /// <summary>运行中容器数。</summary>
    public int ContainersRunning { get; init; }

    /// <summary>暂停容器数。</summary>
    public int ContainersPaused { get; init; }

    /// <summary>停止容器数。</summary>
    public int ContainersStopped { get; init; }

    /// <summary>镜像数。</summary>
    public int Images { get; init; }

    /// <summary>存储驱动。</summary>
    public string? Driver { get; init; }

    /// <summary>日志驱动。</summary>
    public string? LoggingDriver { get; init; }

    /// <summary>cgroup 版本。</summary>
    public string? CgroupVersion { get; init; }

    /// <summary>cgroup 驱动。</summary>
    public string? CgroupDriver { get; init; }

    /// <summary>内存总量(字节)。</summary>
    public long MemTotal { get; init; }

    /// <summary>CPU 核数。</summary>
    public int NCPU { get; init; }

    /// <summary>操作系统。</summary>
    public string? OperatingSystem { get; init; }

    /// <summary>内核版本。</summary>
    public string? KernelVersion { get; init; }

    /// <summary>Engine 版本。</summary>
    public string? ServerVersion { get; init; }

    /// <summary>架构。</summary>
    public string? Architecture { get; init; }

    /// <summary>Docker 根目录。</summary>
    public string? DockerRootDir { get; init; }

    /// <summary>Swarm。</summary>
    public SwarmInfo? Swarm { get; init; }
}

/// <summary>构建缓存的一条记录。</summary>
public sealed record BuildCacheRecord
{
    /// <summary>id。</summary>
    public string? ID { get; init; }

    /// <summary>是否可回收。</summary>
    public bool InUse { get; init; }

    /// <summary>占用字节。</summary>
    public long Size { get; init; }
}

/// <summary><c>GET /system/df</c>。</summary>
public sealed record DiskUsage
{
    /// <summary>镜像层总占用。</summary>
    public long LayersSize { get; init; }

    /// <summary>镜像。</summary>
    public ImageSummary[]? Images { get; init; }

    /// <summary>容器。</summary>
    public ContainerSummary[]? Containers { get; init; }

    /// <summary>卷。</summary>
    public VolumeSummary[]? Volumes { get; init; }

    /// <summary>构建缓存。</summary>
    public BuildCacheRecord[]? BuildCache { get; init; }
}

/// <summary>各种 prune 的统一响应形态(字段按类型取其一)。</summary>
public sealed record PruneReport
{
    /// <summary>被删掉的容器 id。</summary>
    public string[]? ContainersDeleted { get; init; }

    /// <summary>被删掉的卷名。</summary>
    public string[]? VolumesDeleted { get; init; }

    /// <summary>被删掉的网络名。</summary>
    public string[]? NetworksDeleted { get; init; }

    /// <summary>被删掉的镜像。</summary>
    public DeletedImage[]? ImagesDeleted { get; init; }

    /// <summary>被删掉的构建缓存。</summary>
    public BuildCacheRecord[]? CachesDeleted { get; init; }

    /// <summary>回收的字节数。</summary>
    public long SpaceReclaimed { get; init; }

    /// <summary>被删项目的条数(各类之和)。</summary>
    public int DeletedCount =>
        (ContainersDeleted?.Length ?? 0) + (VolumesDeleted?.Length ?? 0) +
        (NetworksDeleted?.Length ?? 0) + (ImagesDeleted?.Length ?? 0) + (CachesDeleted?.Length ?? 0);
}

/// <summary>被删掉/取消标签的镜像。</summary>
public sealed record DeletedImage
{
    /// <summary>被删掉的镜像 id。</summary>
    public string? Deleted { get; init; }

    /// <summary>被取消的标签。</summary>
    public string? Untagged { get; init; }
}

// ─────────────────────────── 事件 / 统计 ───────────────────────────

/// <summary>事件的主体。</summary>
public sealed record EventActor
{
    /// <summary>对象 id。</summary>
    public string? ID { get; init; }

    /// <summary>属性(容器名、镜像、compose 标签都在这里)。</summary>
    public Dictionary<string, string>? Attributes { get; init; }
}

/// <summary><c>GET /events</c> 推来的一条事件。</summary>
public sealed record DockerEvent
{
    /// <summary>container / image / volume / network / daemon…</summary>
    [JsonPropertyName("Type")]
    public string? Type { get; init; }

    /// <summary>start / die / pull / health_status: healthy …</summary>
    [JsonPropertyName("Action")]
    public string? Action { get; init; }

    /// <summary>主体。</summary>
    [JsonPropertyName("Actor")]
    public EventActor? Actor { get; init; }

    /// <summary>秒级时间戳。</summary>
    [JsonPropertyName("time")]
    public long Time { get; init; }

    /// <summary>纳秒级时间戳。</summary>
    [JsonPropertyName("timeNano")]
    public long TimeNano { get; init; }

    /// <summary>对象的显示名:容器名 / 镜像名 / 卷名。</summary>
    public string DisplayName =>
        Actor?.Attributes?.GetValueOrDefault("name")
        ?? Actor?.ID?[..Math.Min(12, Actor.ID.Length)]
        ?? "";

    /// <summary>事件发生时间(本地)。</summary>
    public DateTimeOffset At => TimeNano > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(TimeNano / 1_000_000).ToLocalTime()
        : DateTimeOffset.FromUnixTimeSeconds(Time).ToLocalTime();
}

/// <summary>CPU 用量的一次采样。</summary>
public sealed record CpuUsage
{
    /// <summary>容器累计使用的纳秒。</summary>
    [JsonPropertyName("total_usage")]
    public ulong TotalUsage { get; init; }

    /// <summary>按核拆分的累计使用。</summary>
    [JsonPropertyName("percpu_usage")]
    public ulong[]? PerCpuUsage { get; init; }
}

/// <summary>CPU 统计。</summary>
public sealed record CpuStats
{
    /// <summary>容器 CPU 用量。</summary>
    [JsonPropertyName("cpu_usage")]
    public CpuUsage? CpuUsage { get; init; }

    /// <summary>整机累计 CPU 纳秒。</summary>
    [JsonPropertyName("system_cpu_usage")]
    public ulong SystemCpuUsage { get; init; }

    /// <summary>在线核数(cgroup v2 给这个)。</summary>
    [JsonPropertyName("online_cpus")]
    public int OnlineCpus { get; init; }
}

/// <summary>内存统计。</summary>
public sealed record MemoryStats
{
    /// <summary>当前用量(含缓存)。</summary>
    [JsonPropertyName("usage")]
    public ulong Usage { get; init; }

    /// <summary>上限。</summary>
    [JsonPropertyName("limit")]
    public ulong Limit { get; init; }

    /// <summary>明细,其中 <c>cache</c> / <c>inactive_file</c> 要从用量里扣掉才是"真占用"。</summary>
    [JsonPropertyName("stats")]
    public Dictionary<string, ulong>? Stats { get; init; }
}

/// <summary>一张网卡的收发计数。</summary>
public sealed record NetworkStats
{
    /// <summary>收到的字节。</summary>
    [JsonPropertyName("rx_bytes")]
    public ulong RxBytes { get; init; }

    /// <summary>发出的字节。</summary>
    [JsonPropertyName("tx_bytes")]
    public ulong TxBytes { get; init; }
}

/// <summary><c>GET /containers/{id}/stats</c> 推来的一帧。</summary>
public sealed record ContainerStats
{
    /// <summary>容器 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>采样时刻。</summary>
    [JsonPropertyName("read")]
    public DateTimeOffset Read { get; init; }

    /// <summary>本次 CPU 采样。</summary>
    [JsonPropertyName("cpu_stats")]
    public CpuStats? CpuStats { get; init; }

    /// <summary>上一次 CPU 采样(算差值用)。</summary>
    [JsonPropertyName("precpu_stats")]
    public CpuStats? PreCpuStats { get; init; }

    /// <summary>内存。</summary>
    [JsonPropertyName("memory_stats")]
    public MemoryStats? MemoryStats { get; init; }

    /// <summary>按网卡名索引的收发计数。</summary>
    [JsonPropertyName("networks")]
    public Dictionary<string, NetworkStats>? Networks { get; init; }

    /// <summary>
    /// CPU 占用百分比(与 <c>docker stats</c> 同一口径:两次采样的差值之比 × 核数)。
    /// 采样不全或分母为 0 时返回 0,而不是一个假的尖峰。
    /// </summary>
    public double CpuPercent
    {
        get
        {
            if (CpuStats?.CpuUsage is null || PreCpuStats?.CpuUsage is null)
            {
                return 0;
            }
            var cpuDelta = (double)CpuStats.CpuUsage.TotalUsage - PreCpuStats.CpuUsage.TotalUsage;
            var systemDelta = (double)CpuStats.SystemCpuUsage - PreCpuStats.SystemCpuUsage;
            if (cpuDelta <= 0 || systemDelta <= 0)
            {
                return 0;
            }
            var cpus = CpuStats.OnlineCpus > 0
                ? CpuStats.OnlineCpus
                : CpuStats.CpuUsage.PerCpuUsage?.Length ?? 1;
            return cpuDelta / systemDelta * cpus * 100.0;
        }
    }

    /// <summary>
    /// 真实内存占用:总用量减去页缓存。
    /// <para>
    /// 不扣缓存的话,一个只是读过大文件的容器会显示成"内存快满了" ——
    /// <c>docker stats</c> 自己也是这么扣的。cgroup v2 的字段叫 <c>inactive_file</c>,
    /// v1 叫 <c>cache</c>;两个都试,取到哪个算哪个。
    /// </para>
    /// </summary>
    public ulong MemoryUsed
    {
        get
        {
            if (MemoryStats is null)
            {
                return 0;
            }
            ulong cache = 0;
            if (MemoryStats.Stats is { } stats)
            {
                if (stats.TryGetValue("inactive_file", out var inactive))
                {
                    cache = inactive;
                }
                else if (stats.TryGetValue("cache", out var c))
                {
                    cache = c;
                }
            }
            return MemoryStats.Usage > cache ? MemoryStats.Usage - cache : MemoryStats.Usage;
        }
    }

    /// <summary>内存上限;0 表示不限。</summary>
    public ulong MemoryLimit => MemoryStats?.Limit ?? 0;
}

// ─────────────────────────── exec ───────────────────────────

/// <summary>创建 exec 的响应。</summary>
public sealed record ExecCreateResponse
{
    /// <summary>exec 实例 id。</summary>
    public string Id { get; init; } = "";
}

/// <summary>exec 的检查结果(拿退出码)。</summary>
public sealed record ExecInspectResponse
{
    /// <summary>是否还在跑。</summary>
    public bool Running { get; init; }

    /// <summary>退出码。</summary>
    public int ExitCode { get; init; }

    /// <summary>进程号。</summary>
    public int Pid { get; init; }
}

// ─────────────────────────── 拉取进度 ───────────────────────────

/// <summary>拉取/推送进度的一帧(NDJSON)。</summary>
public sealed record PullProgressFrame
{
    /// <summary>层 id;总览行没有 id。</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Downloading / Extracting / Pull complete / Already exists …</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>字节进度。</summary>
    [JsonPropertyName("progressDetail")]
    public PullProgressDetail? ProgressDetail { get; init; }

    /// <summary>错误(有它就意味着这次拉取失败了)。</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>拉取进度的字节明细。</summary>
public sealed record PullProgressDetail
{
    /// <summary>已完成字节。</summary>
    [JsonPropertyName("current")]
    public long Current { get; init; }

    /// <summary>总字节;未知时为 0。</summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }
}
