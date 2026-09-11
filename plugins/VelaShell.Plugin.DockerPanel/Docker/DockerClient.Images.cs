using System.Text.Json;

namespace VelaShell.Plugin.DockerPanel.Docker;

public sealed partial class DockerClient
{
    /// <summary>列镜像。</summary>
    /// <param name="all">是否含中间层镜像。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<ImageSummary[]> ListImagesAsync(bool all = false, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ImageSummary[]>("/images/json" + Query(("all", all ? "1" : "0")), cancellationToken);

    /// <summary>镜像 inspect 的原始 JSON。</summary>
    public async Task<string> InspectImageRawAsync(string id, CancellationToken cancellationToken = default) =>
        DockerJson.Prettify(await GetStringAsync($"/images/{Uri.EscapeDataString(id)}/json", cancellationToken)
            .ConfigureAwait(false));

    /// <summary>镜像详情(<c>docker image inspect</c>)。</summary>
    public Task<ImageInspect> InspectImageAsync(string id, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ImageInspect>($"/images/{Uri.EscapeDataString(id)}/json", cancellationToken);

    /// <summary>镜像的构建历史。</summary>
    public Task<ImageHistoryEntry[]> ImageHistoryAsync(string id, CancellationToken cancellationToken = default) =>
        GetJsonAsync<ImageHistoryEntry[]>($"/images/{Uri.EscapeDataString(id)}/history", cancellationToken);

    /// <summary>给镜像打一个新标签(不复制镜像,只加一个引用)。</summary>
    public Task TagImageAsync(string id, string repository, string? tag, CancellationToken cancellationToken = default) =>
        PostAsync($"/images/{Uri.EscapeDataString(id)}/tag" + Query(("repo", repository), ("tag", tag)), null, cancellationToken);

    /// <summary>删除镜像。</summary>
    /// <param name="id">镜像 id 或 <c>repo:tag</c>。</param>
    /// <param name="force">有容器在用也删。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<DeletedImage[]> RemoveImageAsync(string id, bool force = false, CancellationToken cancellationToken = default) =>
        DeleteJsonAsync<DeletedImage[]>($"/images/{Uri.EscapeDataString(id)}" + Query(("force", force ? "1" : "0")), cancellationToken);

    /// <summary>
    /// 拉取镜像,逐帧回调进度。
    /// <para>
    /// daemon 用 NDJSON 边拉边推,每层一条("Downloading" 带字节数、"Already exists"、
    /// "Pull complete"),最后一条要么是摘要要么是 <c>error</c>。
    /// <b>HTTP 200 不代表拉取成功</b> —— 失败也是 200,错误写在流末尾的那一帧里。
    /// </para>
    /// </summary>
    /// <param name="fromImage">镜像引用,不含标签。</param>
    /// <param name="tag">标签;<paramref name="allTags" /> 为真时忽略。</param>
    /// <param name="platform">平台,如 <c>linux/amd64</c>;留空用 daemon 默认。</param>
    /// <param name="allTags">拉取全部标签。</param>
    /// <param name="registryAuth">X-Registry-Auth 头的值(base64 JSON);公开仓库传 null。</param>
    /// <param name="progress">进度接收器。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="DockerApiException">流末尾报了错误。</exception>
    public async Task PullImageAsync(string fromImage, string? tag, string? platform, bool allTags,
        string? registryAuth, IProgress<PullProgressFrame> progress, CancellationToken cancellationToken = default)
    {
        var path = "/images/create" + Query(
            ("fromImage", fromImage),
            ("tag", allTags ? null : string.IsNullOrWhiteSpace(tag) ? "latest" : tag),
            ("platform", string.IsNullOrWhiteSpace(platform) ? null : platform));
        await StreamNdjsonAsync(HttpMethod.Post, path, registryAuth, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>推送镜像,逐帧回调进度。语义与拉取同构(200 也可能是失败)。</summary>
    public async Task PushImageAsync(string name, string? tag, string? registryAuth,
        IProgress<PullProgressFrame> progress, CancellationToken cancellationToken = default)
    {
        var path = $"/images/{Uri.EscapeDataString(name)}/push" + Query(("tag", tag));
        await StreamNdjsonAsync(HttpMethod.Post, path, registryAuth, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清理镜像。</summary>
    /// <param name="danglingOnly">
    /// 只清悬空镜像(<c>dangling=true</c>);为假时清理**全部未被容器使用的镜像**,
    /// 那会连带删掉有标签但暂时没人用的镜像,重新拉要花时间与带宽。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    public Task<PruneReport> PruneImagesAsync(bool danglingOnly, CancellationToken cancellationToken = default) =>
        PostJsonAsync<PruneReport>("/images/prune" + Query(("filters", Filters(("dangling", danglingOnly ? "true" : "false")))),
            null, cancellationToken);

    /// <summary>
    /// 把一个或多个镜像导出成 tar 流(<c>docker save</c>)。
    /// <para>
    /// 交出来的是一条<b>还开着</b>的流,调用方负责边读边写盘并释放它 ——
    /// 镜像动辄上 GB,先读进内存再落盘就是把宿主换成一台会 OOM 的机器。
    /// </para>
    /// </summary>
    /// <param name="names">镜像引用,可以是 <c>repo:tag</c> 也可以是 id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<Stream> SaveImagesAsync(IReadOnlyList<string> names, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = "/images/get" + Query([.. names.Select(n => ("names", (string?)n))]);
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                  .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unreachable(ex);
        }
        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 从一条 tar 流导入镜像(<c>docker load</c>),逐帧回调进度。
    /// </summary>
    /// <remarks>
    /// 请求体直接挂在调用方给的流上,不缓冲 —— 与导出同一个理由。
    /// 与拉取同构:HTTP 200 不等于成功,错误在流末尾那一帧里。
    /// </remarks>
    public async Task LoadImagesAsync(Stream tar, IProgress<PullProgressFrame> progress,
        CancellationToken cancellationToken = default)
    {
        using var content = new StreamContent(tar);
        content.Headers.ContentType = new("application/x-tar");
        await StreamNdjsonAsync(HttpMethod.Post, "/images/load" + Query(("quiet", "0")), null, progress,
            cancellationToken, content).ConfigureAwait(false);
    }

    /// <summary>
    /// 读一条 NDJSON 进度流,逐帧回调,并把流末尾的 <c>error</c> 翻成异常。
    /// </summary>
    private async Task StreamNdjsonAsync(HttpMethod method, string path, string? registryAuth,
        IProgress<PullProgressFrame> progress, CancellationToken cancellationToken, HttpContent? content = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = new HttpRequestMessage(method, path);
        if (content is not null)
        {
            request.Content = content;
        }
        if (!string.IsNullOrEmpty(registryAuth))
        {
            request.Headers.TryAddWithoutValidation("X-Registry-Auth", registryAuth);
        }
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                                  .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unreachable(ex);
        }
        using (response)
        {
            await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(body);
            string? failure = null;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                PullProgressFrame? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<PullProgressFrame>(line, DockerJson.Options);
                }
                catch (JsonException)
                {
                    // 进度流里混进过非 JSON 的一行不该让整次拉取失败。
                    continue;
                }
                if (frame is null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(frame.Error))
                {
                    failure = frame.Error;
                }
                progress.Report(frame);
            }
            if (failure is not null)
            {
                // 拉取失败时 HTTP 仍然是 200 —— 错误只在流末尾那一帧里。
                // 不在这里翻成异常的话,界面会显示"拉取完成"然后列表里什么都没多出来。
                throw new DockerApiException(System.Net.HttpStatusCode.OK, failure, path);
            }
        }
    }
}
