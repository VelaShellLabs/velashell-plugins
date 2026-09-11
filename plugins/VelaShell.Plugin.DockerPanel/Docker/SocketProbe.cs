using System.Diagnostics;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>探出来的结论。</summary>
public enum SocketProbeKind
{
    /// <summary>没探成(命令跑不了、超时、看不懂的输出)。</summary>
    Unknown,

    /// <summary>socket 在,而且当前账号读写得动 —— 那么连不上是别的原因。</summary>
    Ready,

    /// <summary>那个位置上没有 socket。</summary>
    Missing,

    /// <summary>socket 在,但当前账号不在它的属组里。</summary>
    PermissionDenied
}

/// <summary>
/// 探测结果。名字都是**具体**的,界面要拿它们直接写进句子里。
/// </summary>
/// <param name="Kind">结论。</param>
/// <param name="Account">当前账号名(如 <c>joes</c>)。</param>
/// <param name="Group">socket 的属组(如 <c>docker</c>)。</param>
/// <param name="Groups">当前账号所在的组,空格分隔。</param>
public sealed record SocketProbeResult(
    SocketProbeKind Kind,
    string Account = "",
    string Group = "",
    string Groups = "");

/// <summary>
/// 连不上的时候,替用户去问一句"到底是哪种连不上"。
/// <para>
/// 为什么必须由面板来问:sshd 打不开到一个 unix socket 的通道时,只回一句笼统的失败,
/// <b>不区分"文件不存在"和"你没权限"</b>。而这两件事要做的完全不是一回事 ——
/// 一个是去装 Docker / 改路径,一个是去加组。分不出来,界面就只能说一句
/// "连不上,自己去终端看看",然后把 <c>ls -l</c> 和 <c>id</c> 两串输出丢给用户自己交叉比对。
/// </para>
/// <para>
/// 探测走的是 SSH 的执行通道(远端)或本地 shell(本机),只读、不改任何东西:
/// <c>-S</c> 看在不在,<c>-r</c>/<c>-w</c> 看碰不碰得到,再把属组与账号所在的组带回来 ——
/// 界面据此写出"账号 joes 不在 docker 组"这样一句人话,而不是让用户去读权限位。
/// </para>
/// </summary>
public static class SocketProbe
{
    private static readonly SocketProbeResult Unknown = new(SocketProbeKind.Unknown);

    /// <summary>探一次。任何异常都归到 <see cref="SocketProbeKind.Unknown" /> —— 这是诊断,不该自己再抛。</summary>
    public static async Task<SocketProbeResult> RunAsync(IRemoteExecApi exec, DockerEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var script = Script(endpoint.SocketPath);
            var output = endpoint.Kind == DockerEndpointKind.Remote
                ? (await exec.RunAsync(endpoint.SessionId, script,
                    new ExecOptions { Timeout = TimeSpan.FromSeconds(10) }, cancellationToken)
                    .ConfigureAwait(false)).Output
                : await LocalAsync(script, cancellationToken).ConfigureAwait(false);
            return Parse(output);
        }
        catch (Exception)
        {
            // 探测失败不该盖过原本那条错误 —— 界面照旧显示笼统的那一屏。
            return Unknown;
        }
    }

    /// <summary>
    /// 一行 POSIX sh。刻意不用 <c>ls -l</c> 之类需要人来读的输出:
    /// <c>-r</c>/<c>-w</c> 问的正是"这个账号碰不碰得到",答案是确定的。
    /// </summary>
    private static string Script(string path) =>
        $"s={Sh.Quote(path)}; if [ -S \"$s\" ]; then if [ -r \"$s\" ] && [ -w \"$s\" ]; " +
        "then echo OK; else echo \"DENIED|$(stat -c %G \"$s\" 2>/dev/null)|$(id -un)|$(id -nG)\"; fi; " +
        "else echo MISSING; fi";

    /// <summary>
    /// 本机端点上自己起一个 shell。
    /// <para>
    /// Windows 上没有 <c>/bin/sh</c>,而命名管道也没有"属组"这回事 —— 那里探不出东西来,
    /// 直接放弃比硬凑一个答案好。
    /// </para>
    /// </summary>
    private static async Task<string> LocalAsync(string script, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return "";
        }
        using var process = new Process
        {
            StartInfo = new("/bin/sh")
            {
                ArgumentList = { "-c", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static SocketProbeResult Parse(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim())
                            .LastOrDefault(l => l.Length > 0) ?? "";
        if (line == "OK")
        {
            return new(SocketProbeKind.Ready);
        }
        if (line == "MISSING")
        {
            return new(SocketProbeKind.Missing);
        }
        if (!line.StartsWith("DENIED|", StringComparison.Ordinal))
        {
            return Unknown;
        }
        // DENIED|属组|账号|账号所在的组
        var parts = line.Split('|');
        return new(SocketProbeKind.PermissionDenied,
            parts.Length > 2 ? parts[2].Trim() : "",
            parts.Length > 1 ? parts[1].Trim() : "",
            parts.Length > 3 ? parts[3].Trim() : "");
    }
}
