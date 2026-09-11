using System.IO.Pipes;
using System.Net.Sockets;
using VelaShell.PluginSdk.RemoteTunnel;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 打开一条到 Docker daemon 的双工字节流。
/// <para>
/// 这一层刻意只有一个方法:面板上面的所有东西 —— HTTP 客户端、事件流、exec 的
/// 多路复用帧 —— 都只要求"给我一条能读能写的流",至于它是 SSH 通道、本机 unix socket
/// 还是 Windows 命名管道,再往上就不该有人关心了。
/// </para>
/// </summary>
public interface IDockerTransport
{
    /// <summary>端点描述(给日志与错误文案用)。</summary>
    string Description { get; }

    /// <summary>打开一条新的双工流。调用方负责释放。</summary>
    Task<Stream> ConnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 经 SDK 的远程隧道能力连到远端 daemon 的 socket
/// (SSH 的 <c>direct-streamlocal@openssh.com</c> 通道)。
/// <para>
/// 相比"在本机开一个转发端口再连过去",这条路不在本机留下任何别的进程能连上的入口 ——
/// 隧道对面是一个 root 等价的 socket,这个区别不是优化而是前提。
/// </para>
/// </summary>
public sealed class TunnelTransport(IRemoteTunnelApi tunnels, string sessionId, string socketPath) : IDockerTransport
{
    /// <inheritdoc />
    public string Description { get; } = $"{socketPath} (SSH 隧道)";

    /// <inheritdoc />
    public Task<Stream> ConnectAsync(CancellationToken cancellationToken = default) =>
        tunnels.OpenUnixSocketAsync(sessionId, socketPath, null, cancellationToken);
}

/// <summary>
/// 连本机 daemon:Windows 走命名管道,其余平台走 unix 域套接字。
/// </summary>
public sealed class LocalTransport(string socketPath) : IDockerTransport
{
    /// <inheritdoc />
    public string Description { get; } = socketPath;

    /// <inheritdoc />
    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() && Description.StartsWith(@"\\.\pipe\", StringComparison.Ordinal))
        {
            var pipeName = Description[@"\\.\pipe\".Length..];
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(Description), cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
