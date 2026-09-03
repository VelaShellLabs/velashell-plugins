using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 连接失败的出口纪律:握手不通时 <see cref="RedisWorkspaceProvider.OpenAsync" /> 必须
/// <b>抛出</b> SDK 的 <see cref="ProtocolConnectionException" />,而不是交回一个"连着但其实没连上"的文档。
/// <para>
/// 宿主正是靠这一抛把失败呈现在连接流程里(提示框 + 不开标签页)。悄悄返回文档的话,
/// 用户得到的是一个空键树和一串各自超时的操作 —— 那是最难排查的一种坏。
/// </para>
/// </summary>
[TestClass]
public sealed class RedisWorkspaceProviderTests
{
    /// <summary>
    /// 一个几乎肯定没人监听的本地端口。用高位端口而不是 6379:开发机上 6379 常常是通的,
    /// 那样这条用例会在有 Redis 的机器上悄悄失去意义。
    /// </summary>
    private const int DeadPort = 63799;

    private static WorkspaceConnectRequest DeadEndpoint() =>
        new()
        {
            SessionId = "connect-failure",
            Host = "127.0.0.1",
            Port = DeadPort,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // 用例只关心"失败长什么样",没必要陪库把默认的 5 秒重试等满。
                ["connectTimeout"] = "500"
            }
        };

    [TestMethod]
    public async Task OpenAsync_WhenNothingIsListening_ThrowsAProtocolConnectionException()
    {
        using var context = new TestPluginContext { PluginId = "velashell.redis" };
        var provider = new RedisWorkspaceProvider(context, new Loc("zh-Hans"));

        ProtocolConnectionException failure = await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(
            () => provider.OpenAsync(DeadEndpoint(), CancellationToken.None));

        // 端点要在消息里:一个用户同时开着好几条 Redis 会话时,"连不上"必须说清是哪一条。
        Assert.Contains($"127.0.0.1:{DeadPort}", failure.Message);
    }

    /// <summary>
    /// 库在连不上时会附一段写给开发者的建议("…use abortConnect=false…")。它不能进提示框:
    /// 用户没有连接字符串可改,而那恰恰是本插件刻意不采纳的做法。
    /// </summary>
    [TestMethod]
    public async Task OpenAsync_StripsTheLibrarysAbortConnectAdvice()
    {
        using var context = new TestPluginContext { PluginId = "velashell.redis" };
        var provider = new RedisWorkspaceProvider(context, new Loc("zh-Hans"));

        ProtocolConnectionException failure = await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(
            () => provider.OpenAsync(DeadEndpoint(), CancellationToken.None));

        Assert.DoesNotContain("abortConnect", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AbortOnConnectFail", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
