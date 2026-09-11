using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// Docker Engine API 客户端。一个实例对应一个端点(一台机器上的一个 daemon)。
/// <para>
/// 传输是可换的:<see cref="IDockerTransport" /> 给出一条双工流,
/// <see cref="SocketsHttpHandler.ConnectCallback" /> 把它接进 <see cref="HttpClient" /> ——
/// 于是"经 SSH 隧道连远端 socket"与"连本机命名管道"在这一层之上完全一样。
/// </para>
/// <para>
/// <b>不带 API 版本前缀。</b> Docker 对未带版本的路径按 daemon 当前版本处理,
/// 而钉一个版本号意味着每次 Docker 升级都要跟着改一次,还会在旧 daemon 上直接 400。
/// 面板改为把 daemon 报的 <c>ApiVersion</c> 显示在状态栏,让用户知道自己在跟谁说话。
/// </para>
/// </summary>
public sealed partial class DockerClient : IAsyncDisposable
{
    /// <summary>一次性请求的默认时限。流式请求不受它约束。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly SocketsHttpHandler _handler;
    private readonly HttpClient _http;
    private bool _disposed;

    /// <summary>按端点建一个客户端。</summary>
    public DockerClient(DockerEndpoint endpoint, IDockerTransport transport)
    {
        Endpoint = endpoint;
        Transport = transport;
        _handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) => await transport.ConnectAsync(ct).ConfigureAwait(false),
            // 连接池要留出余量:隧道配额是每插件 16 条,而事件流、每条跟随中的日志与统计
            // 各自长期占一条。池子开太大,真正要紧的那几条流就开不出来了。
            MaxConnectionsPerServer = 6,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // 隧道自己会随 SSH 会话一起断,不需要再叠一层生存期轮转。
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        };
        _http = new HttpClient(_handler, disposeHandler: false)
        {
            // 主机名是给 Host 头凑数的:对面是一个 socket,不看它。
            BaseAddress = new Uri("http://docker/"),
            // 总时限交给每次调用的取消令牌 —— HttpClient.Timeout 会把流式请求一并掐掉。
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>这个客户端连的是哪个端点。</summary>
    public DockerEndpoint Endpoint { get; }

    /// <summary>底层传输(exec 之类要绕开 <see cref="HttpClient" /> 的地方直接用它)。</summary>
    public IDockerTransport Transport { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _http.Dispose();
        _handler.Dispose();
        return ValueTask.CompletedTask;
    }

    // ─────────────────────────── 请求原语 ───────────────────────────

    /// <summary>拼一条查询串;值为 null 的项跳过。</summary>
    internal static string Query(params (string Key, string? Value)[] items)
    {
        var sb = new StringBuilder();
        foreach ((var key, var value) in items)
        {
            if (value is null)
            {
                continue;
            }
            sb.Append(sb.Length == 0 ? '?' : '&')
              .Append(Uri.EscapeDataString(key))
              .Append('=')
              .Append(Uri.EscapeDataString(value));
        }
        return sb.ToString();
    }

    /// <summary>把一组 filters 编码成 Docker 要的那种 JSON 查询参数。</summary>
    internal static string? Filters(params (string Key, string Value)[] items)
    {
        if (items.Length == 0)
        {
            return null;
        }
        var map = new Dictionary<string, string[]>();
        foreach (var group in items.GroupBy(i => i.Key))
        {
            map[group.Key] = [.. group.Select(g => g.Value)];
        }
        return JsonSerializer.Serialize(map, DockerJson.Options);
    }

    /// <summary>GET 一段 JSON 并反序列化。</summary>
    internal async Task<T> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GET 一段原始文本(inspect 的完整 JSON 直接给界面看)。</summary>
    internal async Task<string> GetStringAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POST,不关心响应体。</summary>
    internal async Task PostAsync(string path, object? body = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, JsonBody(body), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POST 并反序列化响应。</summary>
    internal async Task<T> PostJsonAsync<T>(string path, object? body = null, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, path, JsonBody(body), cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DELETE,不关心响应体。</summary>
    internal async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>DELETE 并反序列化响应。</summary>
    internal async Task<T> DeleteJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, null, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发一条**流式**请求:响应头一到就返回,响应体留着慢慢读。
    /// 调用方负责释放返回的 <see cref="HttpResponseMessage" />。
    /// </summary>
    internal async Task<HttpResponseMessage> OpenStreamAsync(HttpMethod method, string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unreachable(ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }
        return response;
    }

    private static JsonContent? JsonBody(object? body) => body is null ? null : JsonContent.Create(body, options: DockerJson.Options);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(DefaultTimeout);
        using var request = new HttpRequestMessage(method, path) { Content = content };
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerUnreachableException(
                $"请求 {path} 超过 {DefaultTimeout.TotalSeconds:0} 秒没有响应。", DockerUnreachableReason.Unknown);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Unreachable(ex);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, path, cancellationToken).ConfigureAwait(false);
        var value = await response.Content.ReadFromJsonAsync<T>(DockerJson.Options, cancellationToken).ConfigureAwait(false);
        return value ?? throw new DockerApiException(response.StatusCode, "daemon 返回了空响应体。", path);
    }

    /// <summary>
    /// 把非 2xx 翻成带 daemon 原话的异常。
    /// <para>
    /// 状态码只给粗分类,真正能指导用户下一步的是响应体里的 <c>message</c> ——
    /// "port is already allocated" 有可操作性,"HTTP 409" 没有。
    /// </para>
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 连错误响应体都读不回来时,至少还有状态码可报。
        }
        var message = DockerJson.TryDeserialize<DockerErrorBody>(body)?.Message
                         ?? (body.Length > 0 ? body.Trim() : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        throw new DockerApiException(response.StatusCode, message, path);
    }

    /// <summary>
    /// 把"连不上"翻成一句能指导下一步的话。
    /// <para>
    /// 这里刻意按**出路**分类而不是按异常类型:socket 不存在要去装 Docker,
    /// 权限不足要去加 docker 组 —— 给一个统一的"连接失败"等于把用户扔在原地。
    /// </para>
    /// </summary>
    private DockerUnreachableException Unreachable(Exception ex)
    {
        var text = Flatten(ex);
        // 权限先判:它是明说的,而下面那组"通道开不起来"是笼统的 ——
        // 一条同时带着两者的消息,按"没权限"处理才对得上用户要做的事。
        var reason =
            text.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access is denied", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("EACCES", StringComparison.OrdinalIgnoreCase)
                ? DockerUnreachableReason.PermissionDenied
                // sshd 开不起到 socket 的通道时只回一句笼统的失败,不区分"不存在"与"没权限"。
                // 归到"找不到 socket"这一档:它给的出路(去终端 ls 一下、换条路径)对两种情形都成立。
                // 注意 SDK 报的是 ConnectFailed —— 中间没有空格,只匹配 "connect failed" 会漏掉。
                : text.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("connect failed", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("connectfailed", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("open failed", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("ENOENT", StringComparison.OrdinalIgnoreCase)
                    ? DockerUnreachableReason.SocketMissing
                    : ex is NotSupportedException
                        ? DockerUnreachableReason.TunnelUnsupported
                        : text.Contains("session", StringComparison.OrdinalIgnoreCase) &&
                          text.Contains("not found", StringComparison.OrdinalIgnoreCase)
                            ? DockerUnreachableReason.SessionUnavailable
                            : DockerUnreachableReason.Unknown;
        var message = reason switch
        {
            DockerUnreachableReason.SocketMissing =>
                $"连不上 {Transport.Description}:打不开到它的通道。远端可能没装 Docker、daemon 没在跑,或者这条路径不对。",
            DockerUnreachableReason.PermissionDenied =>
                $"没有 {Transport.Description} 的读写权限。把账号加进 docker 组,或改用一个有权限的账号。",
            DockerUnreachableReason.TunnelUnsupported =>
                "当前宿主不支持远程隧道(插件被装载为隔离进程)。Docker 面板需要 hostMode = inProcess。",
            DockerUnreachableReason.SessionUnavailable =>
                "这条 SSH 会话已经断开了。重新连上之后再打开面板。",
            _ => $"连不上 {Transport.Description}:{text}"
        };
        return new(message, reason, ex);
    }

    private static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" → ");
            }
            sb.Append(current.Message);
        }
        return sb.ToString();
    }

    // ─────────────────────────── 探测 ───────────────────────────

    /// <summary>
    /// 探一下这个端点通不通。通了返回 daemon 的版本信息;不通抛
    /// <see cref="DockerUnreachableException" />(带分类与出路)。
    /// </summary>
    public async Task<SystemVersion> PingAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetJsonAsync<SystemVersion>("/version", cancellationToken).ConfigureAwait(false);
        return version;
    }
}
