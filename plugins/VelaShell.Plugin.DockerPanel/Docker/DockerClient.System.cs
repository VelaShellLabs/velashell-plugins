namespace VelaShell.Plugin.DockerPanel.Docker;

public sealed partial class DockerClient
{
    /// <summary>daemon 版本。</summary>
    public Task<SystemVersion> VersionAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<SystemVersion>("/version", cancellationToken);

    /// <summary>daemon 概况。</summary>
    public Task<SystemInfo> InfoAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<SystemInfo>("/info", cancellationToken);

    /// <summary><c>/version</c> 的原始 JSON。</summary>
    public async Task<string> VersionRawAsync(CancellationToken cancellationToken = default) =>
        DockerJson.Prettify(await GetStringAsync("/version", cancellationToken).ConfigureAwait(false));

    /// <summary><c>/info</c> 的原始 JSON。</summary>
    public async Task<string> InfoRawAsync(CancellationToken cancellationToken = default) =>
        DockerJson.Prettify(await GetStringAsync("/info", cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 磁盘用量(<c>docker system df -v</c>)。
    /// <para>
    /// 这条请求在镜像多的机器上要几秒 —— daemon 得把每个卷都 du 一遍。
    /// 界面因此不把它挂在自动刷新上,只在进"系统"页与手动重算时发。
    /// </para>
    /// </summary>
    public Task<DiskUsage> DiskUsageAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<DiskUsage>("/system/df", cancellationToken);

    /// <summary>清理已停止的容器。</summary>
    public Task<PruneReport> PruneContainersAsync(CancellationToken cancellationToken = default) =>
        PostJsonAsync<PruneReport>("/containers/prune", null, cancellationToken);

    /// <summary>清理构建缓存。</summary>
    public Task<PruneReport> PruneBuildCacheAsync(bool all = false, CancellationToken cancellationToken = default) =>
        PostJsonAsync<PruneReport>("/build/prune" + Query(("all", all ? "1" : "0")), null, cancellationToken);
}
