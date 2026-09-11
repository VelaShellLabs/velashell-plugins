using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.RemoteExec;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 连不上时的自动诊断。
/// <para>
/// 这里盯的是"分得出是哪一种"。sshd 打不开通道只回一句笼统的失败,面板要是分不出
/// "没这个文件"与"你没权限",界面就只能说"自己去终端看看" —— 而这两件事
/// 要用户做的完全不一样。
/// </para>
/// </summary>
[TestClass]
public sealed class SocketProbeTests
{
    private static readonly DockerEndpoint Remote =
        DockerEndpoint.Remote("session-1", "EQ12-FnOS", "joes@192.168.16.5");

    /// <summary>测试上下文(取消令牌用)。</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Probe_ReadsBackTheAccountAndTheGroupByName()
    {
        // 真机上的形态:socket 属于 root:docker,而登录账号只在 Users / Administrators 里。
        var exec = new StubExec("DENIED|docker|joes|Users Administrators\n");

        var result = await SocketProbe.RunAsync(exec, Remote, TestContext.CancellationToken);

        Assert.AreEqual(SocketProbeKind.PermissionDenied, result.Kind);
        // 名字要是具体的 —— 界面要把它们直接写进"账号 joes 不在 docker 组里"这句话。
        Assert.AreEqual("joes", result.Account);
        Assert.AreEqual("docker", result.Group);
        Assert.AreEqual("Users Administrators", result.Groups);
    }

    [TestMethod]
    public async Task Probe_SeparatesMissingFromDenied()
    {
        Assert.AreEqual(SocketProbeKind.Missing,
            (await SocketProbe.RunAsync(new StubExec("MISSING\n"), Remote,
                TestContext.CancellationToken)).Kind);
        Assert.AreEqual(SocketProbeKind.Ready,
            (await SocketProbe.RunAsync(new StubExec("OK\n"), Remote,
                TestContext.CancellationToken)).Kind);
    }

    [TestMethod]
    public async Task Probe_IgnoresLoginBannersAndReadsTheLastLine()
    {
        // 登录 shell 会往 stdout 上打欢迎语、fortune、rc 文件的输出 —— 结论在最后一行。
        var exec = new StubExec("Welcome to EQ12-FnOS\nLast login: Sun Aug 23\nOK\n");

        Assert.AreEqual(SocketProbeKind.Ready,
            (await SocketProbe.RunAsync(exec, Remote, TestContext.CancellationToken)).Kind);
    }

    [TestMethod]
    public async Task Probe_StaysQuietWhenItCannotTell()
    {
        // 探不出来就是探不出来。硬凑一个答案会把用户引去做一件没用的事,
        // 那比"连不上,原因不明"更糟。
        Assert.AreEqual(SocketProbeKind.Unknown,
            (await SocketProbe.RunAsync(new StubExec("sh: stat: not found\n"), Remote,
                TestContext.CancellationToken)).Kind);
        Assert.AreEqual(SocketProbeKind.Unknown,
            (await SocketProbe.RunAsync(new ThrowingExec(), Remote,
                TestContext.CancellationToken)).Kind);
    }

    [TestMethod]
    public async Task Probe_AsksAboutTheConfiguredPathNotADefault()
    {
        var exec = new StubExec("OK\n");
        var custom = DockerEndpoint.Remote("s", "n", "d", "/run/user/1000/docker.sock");

        await SocketProbe.RunAsync(exec, custom, TestContext.CancellationToken);

        Assert.Contains("/run/user/1000/docker.sock", exec.LastCommand);
    }

    private sealed class StubExec(string output) : IRemoteExecApi
    {
        public string LastCommand { get; private set; } = "";

        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(new ExecResult(output) { ExitCode = 0 });
        }

        public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
            IProgress<ExecOutput> output, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExecStreamResult(0, 0));
    }

    private sealed class ThrowingExec : IRemoteExecApi
    {
        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("会话没了");

        public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
            IProgress<ExecOutput> output, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("会话没了");
    }
}
