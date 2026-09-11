namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>面板这一次要管哪台机器上的 Docker。</summary>
public enum DockerEndpointKind
{
    /// <summary>宿主所在这台机器上的 Docker(unix socket 或 Windows 命名管道)。</summary>
    Local,

    /// <summary>某条已连接 SSH 会话对面那台机器上的 Docker(经隧道直连它的 socket)。</summary>
    Remote
}

/// <summary>
/// 一个 Docker 端点的完整描述。**不可变**,且是面板里一切请求的第一参数 ——
/// 同时开着生产与测试两台机器时,"这条请求发给谁"必须是一个显式的值,
/// 而不是某个"当前会话"的隐式全局状态。
/// </summary>
/// <param name="Kind">本机还是远端。</param>
/// <param name="SessionId">
/// 远端时为 SSH 会话 id;本机时为 <see cref="LocalSessionId" />。
/// </param>
/// <param name="SocketPath">
/// daemon 的 socket 路径。远端默认 <c>/var/run/docker.sock</c>;
/// 本机在 Windows 上是命名管道 <c>//./pipe/docker_engine</c>,其余平台同远端。
/// </param>
/// <param name="DisplayName">界面上显示的名字(主机名 / “本机”)。</param>
/// <param name="Detail">显示名下面那行小字(user@host、管道路径)。</param>
public sealed record DockerEndpoint(
    DockerEndpointKind Kind,
    string SessionId,
    string SocketPath,
    string DisplayName,
    string Detail)
{
    /// <summary>本机端点用的伪会话 id。</summary>
    public const string LocalSessionId = "@local";

    /// <summary>Linux / macOS 上 daemon 的默认 socket 路径。</summary>
    public const string DefaultUnixSocket = "/var/run/docker.sock";

    /// <summary>Windows 上 daemon 的默认命名管道。</summary>
    public const string DefaultWindowsPipe = @"\\.\pipe\docker_engine";

    /// <summary>本机端点(按当前操作系统选默认 socket)。</summary>
    public static DockerEndpoint Local(string displayName) =>
        new(DockerEndpointKind.Local,
            LocalSessionId,
            OperatingSystem.IsWindows() ? DefaultWindowsPipe : DefaultUnixSocket,
            displayName,
            OperatingSystem.IsWindows() ? DefaultWindowsPipe : DefaultUnixSocket);

    /// <summary>远端端点。</summary>
    public static DockerEndpoint Remote(string sessionId, string displayName, string detail, string? socketPath = null) =>
        new(DockerEndpointKind.Remote, sessionId, string.IsNullOrWhiteSpace(socketPath) ? DefaultUnixSocket : socketPath!,
            displayName, detail);

    /// <summary>
    /// 端点的稳定标识:用于按端点缓存连接池、记忆每主机设置。
    /// </summary>
    public string Key => $"{Kind}|{SessionId}|{SocketPath}";

}
