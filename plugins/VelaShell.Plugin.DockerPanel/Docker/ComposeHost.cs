using System.Diagnostics;
using System.Text;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// <c>docker compose</c> 跑在哪儿。
/// <para>
/// compose 与面板其余部分不同:容器、镜像、卷、网络全都是 Engine 的 HTTP API,
/// 而 compose <b>只有 CLI</b>(daemon 上没有 compose 端点,它是客户端侧把 yml 展开成
/// 一串 create/start 调用的)。所以这一块必须有一条"执行命令"的通道。
/// </para>
/// <para>
/// 远端那条通道是 SSH(宿主的 <see cref="IRemoteExecApi" />);本机那条是**本地进程**。
/// 早先本机端点直接被判成"不支持 compose",那句话其实是在描述面板自己缺一条通道,
/// 却被写成了"compose 是远端的东西" —— 本机装着 Docker Desktop 的用户当然不认。
/// </para>
/// <para>
/// 参数一律按 <b>argv</b> 传,不拼成一整行:本机走 <see cref="ProcessStartInfo.ArgumentList" />,
/// 根本不经过 shell,Windows 上也就没有引用规则可踩;远端才按 POSIX 规则引用一次。
/// </para>
/// </summary>
public interface IComposeHost
{
    /// <summary>这条通道的说明(诊断与文案用)。</summary>
    string Description { get; }

    /// <summary>跑一条命令并等它结束。退出码非 0 不抛异常 —— 那是一种正常结果。</summary>
    Task<ExecResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>跑一条命令,输出边跑边回调。返回退出码。</summary>
    Task<int> StreamAsync(IReadOnlyList<string> arguments, IProgress<ExecOutput> output,
        CancellationToken cancellationToken = default);

    /// <summary>读一个文件(compose.yaml / .env)。</summary>
    Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>覆盖写一个文件。</summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default);
}

/// <summary>远端:命令走 SSH 的 exec 通道,文件走 SFTP。</summary>
public sealed class RemoteComposeHost(IRemoteExecApi exec, IRemoteFsApi remoteFs, string sessionId) : IComposeHost
{
    /// <inheritdoc />
    public string Description => "经 SSH 会话执行";

    /// <inheritdoc />
    public Task<ExecResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        exec.RunAsync(sessionId, Join(arguments), new ExecOptions { Timeout = timeout }, cancellationToken);

    /// <inheritdoc />
    public async Task<int> StreamAsync(IReadOnlyList<string> arguments, IProgress<ExecOutput> output,
        CancellationToken cancellationToken = default)
    {
        ExecStreamResult result = await exec
            .StreamAsync(sessionId, Join(arguments), new ExecStreamOptions { IncludeStandardError = true },
                output, cancellationToken)
            .ConfigureAwait(false);
        return result.ExitCode;
    }

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await remoteFs.ReadAllBytesAsync(sessionId, path, 4 << 20, cancellationToken)
                                     .ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
        remoteFs.WriteAllBytesAsync(sessionId, path, Encoding.UTF8.GetBytes(content), cancellationToken);

    /// <summary>
    /// 拼成远端 shell 的一行。
    /// <para>
    /// argv 里不含 <c>docker</c> —— 本机那头由 <see cref="ProcessStartInfo.FileName" /> 承担,
    /// 远端这头就在这里补上。引用也只在这一处发生:远端那头是一个 shell,本机那头不是。
    /// </para>
    /// </summary>
    private static string Join(IReadOnlyList<string> arguments) =>
        "docker " + string.Join(' ', arguments.Select(a => a.Length > 0 && a.All(IsBare) ? a : Sh.Quote(a)));

    private static bool IsBare(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or ':';
}

/// <summary>
/// 本机:命令是一个本地进程,文件就是本地文件。
/// <para>
/// 本机端点本来就已经绕开 SDK 直接开命名管道 / unix 套接字连 daemon
/// (见 <see cref="LocalTransport" />)—— 本地起一个 <c>docker</c> 进程与那件事同一性质,
/// 不是新开的口子。
/// </para>
/// </summary>
public sealed class LocalComposeHost : IComposeHost
{
    /// <inheritdoc />
    public string Description => "在本机执行 docker compose";

    /// <inheritdoc />
    public async Task<ExecResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        int exit;
        try
        {
            exit = await PumpAsync(arguments,
                line => stdout.AppendLine(line),
                line => stderr.AppendLine(line),
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"本机命令超时({timeout.TotalSeconds:0} 秒):docker {string.Join(' ', arguments)}");
        }
        return new(stdout.ToString())
        {
            Error = stderr.ToString(),
            ExitCode = exit
        };
    }

    /// <inheritdoc />
    public Task<int> StreamAsync(IReadOnlyList<string> arguments, IProgress<ExecOutput> output,
        CancellationToken cancellationToken = default) =>
        PumpAsync(arguments,
            line => output.Report(new(ExecStream.StandardOutput, line)),
            line => output.Report(new(ExecStream.StandardError, line)),
            cancellationToken);

    /// <inheritdoc />
    public async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
        await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
        File.WriteAllTextAsync(path, content, cancellationToken);

    /// <summary>
    /// 起一个 <c>docker</c> 进程,把两条输出逐行喂出去。
    /// <para>
    /// 不经 shell(<c>UseShellExecute = false</c> + <c>ArgumentList</c>):参数按数组交给操作系统,
    /// 带空格的路径、带引号的项目名都不需要额外转义,Windows 与 POSIX 的行为也就一致了。
    /// </para>
    /// <para>
    /// 取消时先 <c>Kill(entireProcessTree)</c>:<c>compose up</c> 会拉起子进程,
    /// 只杀父进程会留下一串没人管的孤儿。
    /// </para>
    /// </summary>
    private static async Task<int> PumpAsync(IReadOnlyList<string> arguments, Action<string> onOut,
        Action<string> onError, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onOut(line);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } line)
            {
                onError(line);
            }
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("起不了 docker 进程");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        return process.ExitCode;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程可能刚好自己退了 —— 那正是我们想要的结果,不必再说什么。
        }
    }
}
