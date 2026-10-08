namespace VelaShell.Plugin.DockerPanel.Docker;

public sealed partial class DockerClient
{
    /// <summary>列容器。</summary>
    /// <param name="all">是否含已停止的。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<ContainerSummary[]> ListContainersAsync(bool all = true, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ContainerSummary[]>("/containers/json" + Query(("all", all ? "1" : "0")), cancellationToken);

    /// <summary>容器 inspect(结构化)。</summary>
    public Task<ContainerInspect> InspectContainerAsync(string id, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ContainerInspect>($"/containers/{Uri.EscapeDataString(id)}/json", cancellationToken);

    /// <summary>容器 inspect 的**原始 JSON**,直接呈现给界面。</summary>
    /// <remarks>
    /// 详情页要的是"全部",而把整个 schema 抄成 DTO 只会让每次 Docker 升级
    /// 都悄悄少显示几个字段。原文没有这个问题。
    /// </remarks>
    public async Task<string> InspectContainerRawAsync(string id, CancellationToken cancellationToken = default) =>
        DockerJson.Prettify(await GetStringAsync($"/containers/{Uri.EscapeDataString(id)}/json", cancellationToken)
            .ConfigureAwait(false));

    /// <summary>启动容器。</summary>
    public Task StartContainerAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/start", null, cancellationToken);

    /// <summary>停止容器(先 SIGTERM,超时后 SIGKILL)。</summary>
    /// <param name="id">容器 id 或名字。</param>
    /// <param name="timeoutSeconds">等待优雅退出的秒数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task StopContainerAsync(string id, int timeoutSeconds = 10, CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/stop" + Query(("t", timeoutSeconds.ToString())), null, cancellationToken);

    /// <summary>重启容器。</summary>
    public Task RestartContainerAsync(string id, int timeoutSeconds = 10, CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/restart" + Query(("t", timeoutSeconds.ToString())), null, cancellationToken);

    /// <summary>暂停容器(SIGSTOP,进程还在)。</summary>
    public Task PauseContainerAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/pause", null, cancellationToken);

    /// <summary>恢复容器。</summary>
    public Task UnpauseContainerAsync(string id, CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/unpause", null, cancellationToken);

    /// <summary>
    /// 强杀容器。默认 <c>SIGKILL</c> —— 不给进程刷缓冲、关连接的机会,
    /// 所以界面上它单独走一道确认。
    /// </summary>
    public Task KillContainerAsync(string id, string signal = "SIGKILL", CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/kill" + Query(("signal", signal)), null, cancellationToken);

    /// <summary>删除容器。</summary>
    /// <param name="id">容器 id 或名字。</param>
    /// <param name="force">运行中也删(等价于先 kill)。</param>
    /// <param name="removeVolumes">连同匿名卷一起删 —— 这一项会丢数据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task RemoveContainerAsync(string id, bool force = false, bool removeVolumes = false,
        CancellationToken cancellationToken = default) =>
        DeleteAsync($"/containers/{Uri.EscapeDataString(id)}" +
                    Query(("force", force ? "1" : "0"), ("v", removeVolumes ? "1" : "0")), cancellationToken);

    /// <summary>重命名容器(不重启)。</summary>
    public Task RenameContainerAsync(string id, string newName, CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/rename" + Query(("name", newName)), null, cancellationToken);

    /// <summary>改重启策略(立即生效,不重启容器)。</summary>
    public Task UpdateRestartPolicyAsync(string id, string policy, int maximumRetryCount = 0,
        CancellationToken cancellationToken = default) =>
        PostAsync($"/containers/{Uri.EscapeDataString(id)}/update",
            new { RestartPolicy = new { Name = policy, MaximumRetryCount = maximumRetryCount } }, cancellationToken);

    /// <summary>容器内进程表(<c>docker top</c>)。</summary>
    public Task<ContainerTopResult> TopAsync(string id, string psArgs = "-ef", CancellationToken cancellationToken = default) =>
        GetJsonAsync<ContainerTopResult>($"/containers/{Uri.EscapeDataString(id)}/top" + Query(("ps_args", psArgs)), cancellationToken);

    /// <summary>可写层相对镜像的变更(<c>docker diff</c>)。</summary>
    public async Task<FilesystemChange[]> ChangesAsync(string id, CancellationToken cancellationToken = default)
    {
        // 没有任何变更时 daemon 返回 JSON null,不是空数组。
        string body = await GetStringAsync($"/containers/{Uri.EscapeDataString(id)}/changes", cancellationToken).ConfigureAwait(false);
        return DockerJson.TryDeserialize<FilesystemChange[]>(body) ?? [];
    }

    /// <summary>创建容器。</summary>
    /// <param name="name">容器名;留空由 daemon 生成。</param>
    /// <param name="config">创建配置(见 <see cref="ContainerCreateRequest" />)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<CreateContainerResponse> CreateContainerAsync(string? name, ContainerCreateRequest config,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<CreateContainerResponse>(
            "/containers/create" + (string.IsNullOrWhiteSpace(name) ? "" : Query(("name", name))),
            config, cancellationToken);

    /// <summary>
    /// 把容器当前的可写层固化成一个镜像(<c>docker commit</c>)。
    /// </summary>
    /// <param name="id">容器 id。</param>
    /// <param name="repository">仓库名;留空则产生一个只有 id 的悬空镜像。</param>
    /// <param name="tag">标签。</param>
    /// <param name="comment">提交说明,会进镜像的 <c>Comment</c>。</param>
    /// <param name="author">作者。</param>
    /// <param name="pause">
    /// 提交期间是否暂停容器。默认<b>暂停</b> —— 与 docker CLI 一致:
    /// 一个正在写文件的进程被拍下快照,得到的可能是一个写到一半的数据库。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<CommitResponse> CommitContainerAsync(string id, string? repository, string? tag,
        string? comment, string? author, bool pause = true, CancellationToken cancellationToken = default) =>
        PostJsonAsync<CommitResponse>("/commit" + Query(
            ("container", id),
            ("repo", string.IsNullOrWhiteSpace(repository) ? null : repository),
            ("tag", string.IsNullOrWhiteSpace(tag) ? null : tag),
            ("comment", string.IsNullOrWhiteSpace(comment) ? null : comment),
            ("author", string.IsNullOrWhiteSpace(author) ? null : author),
            ("pause", pause ? "1" : "0")), null, cancellationToken);

    /// <summary>
    /// 等容器结束并拿退出码。用于"跑一次性容器"的场景。
    /// </summary>
    public Task<WaitResponse> WaitContainerAsync(string id, CancellationToken cancellationToken = default) =>
        PostJsonAsync<WaitResponse>($"/containers/{Uri.EscapeDataString(id)}/wait", null, cancellationToken);
}

/// <summary>等待容器结束的响应。</summary>
public sealed record WaitResponse
{
    /// <summary>退出码。</summary>
    public int StatusCode { get; init; }
}

/// <summary>
/// <c>POST /containers/create</c> 的请求体。
/// <para>
/// 字段名必须与 Docker 的 schema 一致(PascalCase),所以这里不套记录的惯例改名。
/// </para>
/// </summary>
public sealed record ContainerCreateRequest
{
    /// <summary>镜像引用。</summary>
    public required string Image { get; init; }

    /// <summary>主机名。</summary>
    public string? Hostname { get; init; }

    /// <summary>覆盖入口命令。</summary>
    public string[]? Cmd { get; init; }

    /// <summary>覆盖 entrypoint。</summary>
    public string[]? Entrypoint { get; init; }

    /// <summary>环境变量,<c>KEY=VALUE</c>。</summary>
    public string[]? Env { get; init; }

    /// <summary>工作目录。</summary>
    public string? WorkingDir { get; init; }

    /// <summary>用户。</summary>
    public string? User { get; init; }

    /// <summary>标签。</summary>
    public Dictionary<string, string>? Labels { get; init; }

    /// <summary>要暴露的容器端口,键形如 <c>80/tcp</c>,值是空对象。</summary>
    public Dictionary<string, object>? ExposedPorts { get; init; }

    /// <summary>是否分配 TTY。</summary>
    public bool Tty { get; init; }

    /// <summary>是否保持 stdin 打开。</summary>
    public bool OpenStdin { get; init; }

    /// <summary>宿主配置。</summary>
    public HostConfigRequest? HostConfig { get; init; }

    /// <summary>网络配置。</summary>
    public NetworkingConfigRequest? NetworkingConfig { get; init; }
}

/// <summary>创建容器时的宿主配置。</summary>
public sealed record HostConfigRequest
{
    /// <summary>端口映射:键 <c>80/tcp</c>,值是一组宿主绑定。</summary>
    public Dictionary<string, PortBindingRequest[]>? PortBindings { get; init; }

    /// <summary>挂载,<c>源:目标[:ro]</c> 形态。</summary>
    public string[]? Binds { get; init; }

    /// <summary>重启策略。</summary>
    public RestartPolicyRequest? RestartPolicy { get; init; }

    /// <summary>退出即删除。</summary>
    public bool AutoRemove { get; init; }

    /// <summary>特权模式。</summary>
    public bool Privileged { get; init; }

    /// <summary>网络模式(接入单个网络时用它)。</summary>
    public string? NetworkMode { get; init; }

    /// <summary>额外的 Linux capability。</summary>
    public string[]? CapAdd { get; init; }

    /// <summary>内存上限(字节)。</summary>
    public long? Memory { get; init; }
}

/// <summary>一条宿主端口绑定。</summary>
public sealed record PortBindingRequest
{
    /// <summary>宿主监听地址;留空表示所有地址。</summary>
    public string? HostIp { get; init; }

    /// <summary>宿主端口(字符串,Docker 的 schema 就是字符串)。</summary>
    public string? HostPort { get; init; }
}

/// <summary>重启策略(请求侧)。</summary>
public sealed record RestartPolicyRequest
{
    /// <summary>策略名。</summary>
    public string? Name { get; init; }

    /// <summary>on-failure 的最大重试次数。</summary>
    public int MaximumRetryCount { get; init; }
}

/// <summary>创建容器时的网络配置。</summary>
public sealed record NetworkingConfigRequest
{
    /// <summary>按网络名索引的接入设置。</summary>
    public Dictionary<string, EndpointConfigRequest>? EndpointsConfig { get; init; }
}

/// <summary>接入某网络的设置。</summary>
public sealed record EndpointConfigRequest
{
    /// <summary>网络别名。</summary>
    public string[]? Aliases { get; init; }
}
