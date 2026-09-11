using System.Net;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// daemon 如实返回的一条失败。
/// <para>
/// Docker 把人话写在响应体的 <c>{"message":"…"}</c> 里,而状态码只给出粗分类。
/// 界面上要显示的是那句人话("port is already allocated"、"container is paused"),
/// 不是 "HTTP 409" —— 后者对用户没有任何可操作性。
/// </para>
/// </summary>
public sealed class DockerApiException(HttpStatusCode statusCode, string message, string? requestPath = null)
    : Exception(message)
{
    /// <summary>daemon 返回的状态码。</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>发起的请求路径(诊断用,不进主文案)。</summary>
    public string? RequestPath { get; } = requestPath;

    /// <summary>目标不存在(404)。批量操作里把它单独归类:多半是别处已经删掉了。</summary>
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;

    /// <summary>状态冲突(409):容器已在运行、卷仍被占用、网络还连着容器。</summary>
    public bool IsConflict => StatusCode == HttpStatusCode.Conflict;
}

/// <summary>
/// 连不上 daemon(隧道没建起来、socket 不存在、权限不足)。
/// <para>
/// 与 <see cref="DockerApiException" /> 分开是因为**出路完全不同**:后者是"这条操作不行",
/// 前者是"这台机器还没准备好" —— 界面要给的是"把账号加进 docker 组"这类指引,
/// 而不是重试按钮。
/// </para>
/// </summary>
public sealed class DockerUnreachableException(string message, DockerUnreachableReason reason, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>连不上的分类,决定界面给哪一条出路。</summary>
    public DockerUnreachableReason Reason { get; } = reason;
}

/// <summary>连不上 daemon 的分类。</summary>
public enum DockerUnreachableReason
{
    /// <summary>说不清,原样把底层异常的话报上去。</summary>
    Unknown,

    /// <summary>会话没了或没连上。</summary>
    SessionUnavailable,

    /// <summary>socket 文件不存在 —— 多半是远端压根没装 Docker,或 daemon 没在跑。</summary>
    SocketMissing,

    /// <summary>socket 在,但当前账号没有读写权限(不在 docker 组)。</summary>
    PermissionDenied,

    /// <summary>宿主不支持隧道(隔离进程模式)。</summary>
    TunnelUnsupported
}
