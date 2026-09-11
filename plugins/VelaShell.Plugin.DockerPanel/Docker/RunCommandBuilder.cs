using System.Text;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// 从一个已存在的容器反推出等价的 <c>docker run</c> 命令。
/// <para>
/// 用途是"这个容器当初是怎么起的" —— 手起的容器没有 compose 文件,
/// 而三个月后要在另一台机器上重建它时,inspect 的那一大坨 JSON 没人愿意读。
/// </para>
/// <para>
/// <b>它是近似的,而且必须说清楚是近似的。</b> Engine 不保存原始命令行,只保存生效后的配置,
/// 所以推回去必然有偏差:镜像自带的 <c>ENV</c> 与用户 <c>-e</c> 传的混在一起(这里靠减去镜像的
/// 环境变量还原)、<c>--network</c> 之外的网络别名、能力增删、日志驱动等等都不在面板取的那几个字段里。
/// 因此产物末尾会附一句提醒,而不是假装它可以照抄执行。
/// </para>
/// </summary>
public static class RunCommandBuilder
{
    /// <summary>拼出命令。</summary>
    /// <param name="inspect">容器 inspect。</param>
    /// <param name="imageEnv">镜像自带的环境变量(用来把用户加的那些筛出来);拿不到时传 null。</param>
    public static string Build(ContainerInspect inspect, IReadOnlyCollection<string>? imageEnv)
    {
        var sb = new StringBuilder("docker run");
        var config = inspect.Config;
        var host = inspect.HostConfig;

        // -d 是面板起容器的默认形态,也是绝大多数长期容器的形态。
        sb.Append(" -d");
        if (host?.AutoRemove == true)
        {
            sb.Append(" --rm");
        }
        if (inspect.Name is { Length: > 0 } name)
        {
            sb.Append(" --name ").Append(Sh.Quote(name.TrimStart('/')));
        }

        foreach ((var port, var bindings) in Ordered(host?.PortBindings))
        {
            // tcp 是 -p 的默认协议,写出来只是噪音;udp / sctp 必须留着。
            var spec = port.EndsWith("/tcp", StringComparison.Ordinal) ? port[..^4] : port;
            foreach (var binding in bindings ?? [])
            {
                var hostPart = string.IsNullOrEmpty(binding.HostIp)
                    ? binding.HostPort ?? ""
                    : $"{binding.HostIp}:{binding.HostPort}";
                sb.Append(" -p ").Append(Sh.Quote(hostPart.Length > 0 ? $"{hostPart}:{spec}" : spec));
            }
        }

        foreach (var bind in host?.Binds ?? [])
        {
            sb.Append(" -v ").Append(Sh.Quote(bind));
        }
        // Binds 只覆盖 -v 起的那些;命名卷经 Mounts 出现,别漏掉。
        foreach (var mount in inspect.Mounts ?? [])
        {
            if (mount.Type != "volume" || mount.Name is not { Length: > 0 } volume)
            {
                continue;
            }
            var spec = $"{volume}:{mount.Destination}{(mount.RW ? "" : ":ro")}";
            sb.Append(" -v ").Append(Sh.Quote(spec));
        }

        foreach (var entry in UserEnv(config?.Env, imageEnv))
        {
            sb.Append(" -e ").Append(Sh.Quote(entry));
        }

        if (host?.NetworkMode is { Length: > 0 } network && network != "default" && !network.StartsWith("container:", StringComparison.Ordinal))
        {
            sb.Append(" --network ").Append(Sh.Quote(network));
        }
        if (host?.RestartPolicy?.Name is { Length: > 0 } policy && policy != "no")
        {
            sb.Append(" --restart ")
              .Append(policy == "on-failure" && host.RestartPolicy.MaximumRetryCount > 0
                  ? $"on-failure:{host.RestartPolicy.MaximumRetryCount}"
                  : policy);
        }
        if (config?.User is { Length: > 0 } user)
        {
            sb.Append(" -u ").Append(Sh.Quote(user));
        }
        if (config?.WorkingDir is { Length: > 0 } workdir)
        {
            sb.Append(" -w ").Append(Sh.Quote(workdir));
        }
        if (host?.Privileged == true)
        {
            sb.Append(" --privileged");
        }
        if (host?.Memory > 0)
        {
            sb.Append(" -m ").Append(host.Memory);
        }

        sb.Append(' ').Append(Sh.Quote(config?.Image ?? inspect.Image ?? "<image>"));

        // Cmd 只有在覆盖了镜像默认值时才该出现,但面板拿不到镜像的默认 Cmd 来比对,
        // 所以照原样附上 —— 多写一次等价的命令,好过漏掉一个真正被覆盖过的。
        foreach (var arg in config?.Cmd ?? [])
        {
            sb.Append(' ').Append(Sh.Quote(arg));
        }
        return sb.ToString();
    }

    /// <summary>随命令一起复制的那句提醒。</summary>
    public const string Caveat =
        "# 由面板从 inspect 反推,是近似值:Engine 不保存原始命令行。\n" +
        "# 网络别名、能力增删、日志驱动、健康检查等未包含在内,执行前请核对。";

    /// <summary>
    /// 把镜像自带的环境变量减掉,只留用户真正传过的。
    /// <para>
    /// 不减的话,一条 <c>docker run</c> 会拖着镜像里那十几条 <c>PATH</c>、<c>LANG</c>、
    /// <c>NGINX_VERSION</c> —— 它们不是用户写的,抄进新命令里既噪音又可能过时。
    /// </para>
    /// </summary>
    internal static IEnumerable<string> UserEnv(string[]? containerEnv, IReadOnlyCollection<string>? imageEnv)
    {
        if (containerEnv is null)
        {
            yield break;
        }
        HashSet<string> fromImage = imageEnv is null ? [] : [.. imageEnv];
        foreach (var entry in containerEnv)
        {
            if (!fromImage.Contains(entry))
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, PortBinding[]?>> Ordered(
        Dictionary<string, PortBinding[]?>? bindings) =>
        bindings is null ? [] : bindings.OrderBy(p => p.Key, StringComparer.Ordinal);
}
