namespace VelaShell.Plugin.DockerPanel.Docker;

public sealed partial class DockerClient
{
    // ─────────────────────────── 卷 ───────────────────────────

    /// <summary>列卷。</summary>
    public async Task<VolumeSummary[]> ListVolumesAsync(CancellationToken cancellationToken = default)
    {
        VolumeListResponse response = await GetJsonAsync<VolumeListResponse>("/volumes", cancellationToken).ConfigureAwait(false);
        return response.Volumes ?? [];
    }

    /// <summary>卷 inspect 的原始 JSON。</summary>
    public async Task<string> InspectVolumeRawAsync(string name, CancellationToken cancellationToken = default) =>
        DockerJson.Prettify(await GetStringAsync($"/volumes/{Uri.EscapeDataString(name)}", cancellationToken)
            .ConfigureAwait(false));

    /// <summary>新建卷。</summary>
    /// <param name="name">卷名(创建后不可改)。</param>
    /// <param name="driver">驱动;留空用 <c>local</c>。</param>
    /// <param name="driverOpts">驱动选项(NFS 之类)。</param>
    /// <param name="labels">标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<VolumeSummary> CreateVolumeAsync(string name, string? driver,
        Dictionary<string, string>? driverOpts, Dictionary<string, string>? labels,
        CancellationToken cancellationToken = default) =>
        PostJsonAsync<VolumeSummary>("/volumes/create", new
        {
            Name = name,
            Driver = string.IsNullOrWhiteSpace(driver) ? "local" : driver,
            DriverOpts = driverOpts,
            Labels = labels
        }, cancellationToken);

    /// <summary>
    /// 删除卷。<b>会永久丢失卷里的数据</b>,界面上必须走"手打确认串"那一档闸门。
    /// </summary>
    public Task RemoveVolumeAsync(string name, bool force = false, CancellationToken cancellationToken = default) =>
        DeleteAsync($"/volumes/{Uri.EscapeDataString(name)}" + Query(("force", force ? "1" : "0")), cancellationToken);

    /// <summary>清理未被任何容器使用的卷。同样会丢数据。</summary>
    public Task<PruneReport> PruneVolumesAsync(CancellationToken cancellationToken = default) =>
        PostJsonAsync<PruneReport>("/volumes/prune", null, cancellationToken);

    // ─────────────────────────── 网络 ───────────────────────────

    /// <summary>列网络。</summary>
    public Task<NetworkSummary[]> ListNetworksAsync(CancellationToken cancellationToken = default) =>
        GetJsonAsync<NetworkSummary[]>("/networks", cancellationToken);

    /// <summary>网络 inspect(结构化,含已接入容器)。</summary>
    public Task<NetworkSummary> InspectNetworkAsync(string id, CancellationToken cancellationToken = default) =>
        GetJsonAsync<NetworkSummary>($"/networks/{Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>网络 inspect 的原始 JSON。</summary>
    public async Task<string> InspectNetworkRawAsync(string id, CancellationToken cancellationToken = default) =>
        DockerJson.Prettify(await GetStringAsync($"/networks/{Uri.EscapeDataString(id)}", cancellationToken)
            .ConfigureAwait(false));

    /// <summary>新建网络。</summary>
    public Task<CreateNetworkResponse> CreateNetworkAsync(string name, string driver, string? subnet, string? gateway,
        bool @internal, bool attachable, bool enableIPv6, CancellationToken cancellationToken = default)
    {
        object? ipam = string.IsNullOrWhiteSpace(subnet)
            ? null
            : new { Config = new[] { new { Subnet = subnet, Gateway = string.IsNullOrWhiteSpace(gateway) ? null : gateway } } };
        return PostJsonAsync<CreateNetworkResponse>("/networks/create", new
        {
            Name = name,
            Driver = driver,
            Internal = @internal,
            Attachable = attachable,
            EnableIPv6 = enableIPv6,
            IPAM = ipam
        }, cancellationToken);
    }

    /// <summary>删除网络。内置的 bridge/host/none 删不掉,界面应提前置灰。</summary>
    public Task RemoveNetworkAsync(string id, CancellationToken cancellationToken = default) =>
        DeleteAsync($"/networks/{Uri.EscapeDataString(id)}", cancellationToken);

    /// <summary>把容器接入网络(立即生效,不重启容器)。</summary>
    public Task ConnectNetworkAsync(string networkId, string container, string[]? aliases = null,
        CancellationToken cancellationToken = default) =>
        PostAsync($"/networks/{Uri.EscapeDataString(networkId)}/connect", new
        {
            Container = container,
            EndpointConfig = aliases is { Length: > 0 } ? new { Aliases = aliases } : null
        }, cancellationToken);

    /// <summary>把容器从网络上摘掉。</summary>
    public Task DisconnectNetworkAsync(string networkId, string container, bool force = false,
        CancellationToken cancellationToken = default) =>
        PostAsync($"/networks/{Uri.EscapeDataString(networkId)}/disconnect",
            new { Container = container, Force = force }, cancellationToken);

    /// <summary>清理未使用的网络。</summary>
    public Task<PruneReport> PruneNetworksAsync(CancellationToken cancellationToken = default) =>
        PostJsonAsync<PruneReport>("/networks/prune", null, cancellationToken);
}

/// <summary>新建网络的响应。</summary>
public sealed record CreateNetworkResponse
{
    /// <summary>新网络 id。</summary>
    public string Id { get; init; } = "";

    /// <summary>daemon 的警告。</summary>
    public string? Warning { get; init; }
}
