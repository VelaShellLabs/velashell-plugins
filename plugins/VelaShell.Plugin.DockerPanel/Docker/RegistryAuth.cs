using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>某个仓库的登录状态,用于在拉取表单上如实显示。</summary>
/// <param name="Registry">仓库地址。</param>
/// <param name="State">状态。</param>
/// <param name="Detail">补充说明(凭据来源、为什么用不了)。</param>
public readonly record struct RegistryAuthStatus(string Registry, RegistryAuthState State, string Detail);

/// <summary>仓库登录状态。</summary>
public enum RegistryAuthState
{
    /// <summary>公开仓库,不需要凭据。</summary>
    NotRequired,

    /// <summary>在远端的 config.json 里找到了可用凭据。</summary>
    Available,

    /// <summary>config.json 里有这个仓库,但凭据交给了 credential helper —— 面板取不到。</summary>
    HelperOnly,

    /// <summary>没有找到凭据。私有仓库会因此拉取失败。</summary>
    Missing
}

/// <summary>
/// 仓库凭据。
/// <para>
/// <b>这里有一个 CLI 版没有的坑。</b> <c>docker pull</c> 的凭据是 **CLI** 从
/// <c>~/.docker/config.json</c> 读出来、放进 <c>X-Registry-Auth</c> 头再发给 daemon 的;
/// daemon **自己不读那个文件**。所以改走 HTTP API 之后,私有仓库的拉取必须由面板
/// 自己去读那份 config —— 否则表现为"命令行能拉、面板拉不动",而且报的是
/// 一句没头没脑的 401。
/// </para>
/// <para>
/// 凭据只在**发起那一次请求时**读取,不落盘、不进内存缓存、不写日志。
/// 用 credential helper(<c>credsStore</c> / <c>credHelpers</c>)保存的凭据取不到 ——
/// 那需要在远端跑一个 helper 可执行文件,面板不做这件事,而是在界面上说清楚。
/// </para>
/// </summary>
public sealed class RegistryAuthProvider(IRemoteFsApi remoteFs, DockerEndpoint endpoint)
{
    /// <summary>Docker Hub 在 config.json 里的键。</summary>
    public const string DockerHub = "https://index.docker.io/v1/";

    /// <summary>从镜像引用里解出仓库地址。</summary>
    /// <remarks>
    /// 规则与 Docker 自己一致:第一段里带 <c>.</c> 或 <c>:</c> 或等于 <c>localhost</c>
    /// 才算仓库主机,否则整串都是 Docker Hub 上的仓库名。
    /// </remarks>
    public static string ResolveRegistry(string imageReference)
    {
        var reference = imageReference.Trim();
        var slash = reference.IndexOf('/');
        if (slash <= 0)
        {
            return DockerHub;
        }
        var first = reference[..slash];
        var isHost = first.Contains('.', StringComparison.Ordinal)
                      || first.Contains(':', StringComparison.Ordinal)
                      || first == "localhost";
        return isHost ? first : DockerHub;
    }

    /// <summary>查这个镜像所属仓库的登录状态(给表单显示用,不返回凭据本身)。</summary>
    public async Task<RegistryAuthStatus> GetStatusAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        var registry = ResolveRegistry(imageReference);
        var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            return new(registry, registry == DockerHub ? RegistryAuthState.NotRequired : RegistryAuthState.Missing,
                "远端没有 ~/.docker/config.json —— 公开仓库不受影响。");
        }
        if (config.CredHelpers?.ContainsKey(registry) == true ||
            (!string.IsNullOrEmpty(config.CredsStore) && config.Auths?.ContainsKey(registry) == true &&
             string.IsNullOrEmpty(config.Auths[registry].Auth)))
        {
            return new(registry, RegistryAuthState.HelperOnly,
                "凭据由 credential helper 保管,面板读不到。私有镜像请在终端里 docker pull。");
        }
        if (config.Auths?.TryGetValue(registry, out var entry) == true && !string.IsNullOrEmpty(entry.Auth))
        {
            return new(registry, RegistryAuthState.Available, "凭据来自远端 ~/.docker/config.json");
        }
        return new(registry, registry == DockerHub ? RegistryAuthState.NotRequired : RegistryAuthState.Missing,
            registry == DockerHub ? "公开仓库,不需要凭据。" : "config.json 里没有这个仓库的凭据。");
    }

    /// <summary>
    /// 造 <c>X-Registry-Auth</c> 头的值;取不到凭据返回 <see langword="null" />
    /// (公开镜像照样能拉)。
    /// </summary>
    public async Task<string?> GetAuthHeaderAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        var registry = ResolveRegistry(imageReference);
        var config = await ReadConfigAsync(cancellationToken).ConfigureAwait(false);
        if (config?.Auths is null || !config.Auths.TryGetValue(registry, out var entry))
        {
            return null;
        }
        var username = entry.Username;
        var password = entry.Password;
        if (!string.IsNullOrEmpty(entry.Auth))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Auth));
                var colon = decoded.IndexOf(':');
                if (colon > 0)
                {
                    username = decoded[..colon];
                    password = decoded[(colon + 1)..];
                }
            }
            catch (FormatException)
            {
                return null;
            }
        }
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(entry.IdentityToken))
        {
            return null;
        }
        var json = JsonSerializer.Serialize(new
        {
            username,
            password,
            serveraddress = registry,
            identitytoken = string.IsNullOrEmpty(entry.IdentityToken) ? null : entry.IdentityToken
        }, DockerJson.Options);
        // Docker 要的是 base64url(RFC 4648 §5)—— 用标准 base64 的话,
        // 带 + 或 / 的凭据会在头里被截断,表现为一个莫名其妙的 401。
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                      .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private async Task<DockerConfigFile?> ReadConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (endpoint.Kind == DockerEndpointKind.Local)
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker", "config.json");
                return File.Exists(path)
                    ? DockerJson.TryDeserialize<DockerConfigFile>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
                    : null;
            }
            var home = await remoteFs.GetWorkingDirectoryAsync(endpoint.SessionId, cancellationToken).ConfigureAwait(false);
            var remotePath = $"{home.TrimEnd('/')}/.docker/config.json";
            if (!await remoteFs.ExistsAsync(endpoint.SessionId, remotePath, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var bytes = await remoteFs.ReadAllBytesAsync(endpoint.SessionId, remotePath, 1 << 20, cancellationToken)
                                         .ConfigureAwait(false);
            return DockerJson.TryDeserialize<DockerConfigFile>(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 读不到 config 不是错误 —— 公开镜像本来就不需要它。
            return null;
        }
    }
}

/// <summary><c>~/.docker/config.json</c> 里我们关心的那几项。</summary>
internal sealed record DockerConfigFile
{
    [JsonPropertyName("auths")]
    public Dictionary<string, DockerAuthEntry>? Auths { get; init; }

    [JsonPropertyName("credsStore")]
    public string? CredsStore { get; init; }

    [JsonPropertyName("credHelpers")]
    public Dictionary<string, string>? CredHelpers { get; init; }
}

/// <summary>config.json 里一个仓库的凭据项。</summary>
internal sealed record DockerAuthEntry
{
    [JsonPropertyName("auth")]
    public string? Auth { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [JsonPropertyName("identitytoken")]
    public string? IdentityToken { get; init; }
}
