using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk.RemoteExec;
using VelaShell.PluginSdk.RemoteFs;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// compose 的执行通道。
/// <para>
/// 这里盯的是**下发到远端 shell 的那一整行**。参数在插件里是按 argv 传的,
/// 拼行只发生在一处;拼错了(比如漏掉开头的 <c>docker</c>、或者带空格的项目名没引起来),
/// 界面上看不出来 —— 命令是在别人的机器上失败的。
/// </para>
/// </summary>
[TestClass]
public sealed class ComposeHostTests
{
    // ProjectDirectory 是从 compose 文件路径推出来的,不能单独给。
    private static ComposeProject Project(string name, string directory) =>
        new(name, "running(2)", directory + "/compose.yaml");

    [TestMethod]
    public async Task RemoteHost_PrependsDockerAndQuotesOnlyWhatNeedsIt()
    {
        var exec = new RecordingExec();
        var cli = new ComposeCli(new RemoteComposeHost(exec, RemoteFs, "session-1"));

        await cli.ConfigAsync(Project("shop", "/srv/app"), TestContext.CancellationToken);

        // 开头的 docker 是远端这条通道自己补的 —— 本机那条由进程名承担,argv 里没有它。
        Assert.AreEqual("docker compose -p shop -f /srv/app/compose.yaml --project-directory /srv/app config",
            exec.LastCommand);
    }

    [TestMethod]
    public async Task RemoteHost_QuotesProjectNamesWithSpaces()
    {
        var exec = new RecordingExec();
        var cli = new ComposeCli(new RemoteComposeHost(exec, RemoteFs, "session-1"));

        await cli.ConfigAsync(Project("my shop", "/srv/my app"), TestContext.CancellationToken);

        Assert.Contains("-p 'my shop'", exec.LastCommand);
        Assert.Contains("-f '/srv/my app/compose.yaml'", exec.LastCommand);
        Assert.Contains("--project-directory '/srv/my app'", exec.LastCommand);
    }

    [TestMethod]
    public async Task RemoteHost_ProbesComposeV2()
    {
        var exec = new RecordingExec();
        var cli = new ComposeCli(new RemoteComposeHost(exec, RemoteFs, "session-1"));

        Assert.IsTrue(await cli.IsAvailableAsync(TestContext.CancellationToken));
        Assert.AreEqual("docker compose version --short", exec.LastCommand);
    }

    [TestMethod]
    public void LocalHost_IsFlaggedAsLocalSoTheHintCanSayWhereToLook()
    {
        Assert.IsTrue(new ComposeCli(new LocalComposeHost()).IsLocal);
        Assert.IsFalse(new ComposeCli(new RemoteComposeHost(new RecordingExec(), RemoteFs, "s")).IsLocal);
    }

    /// <summary>
    /// 这几个用例只走命令那条路,文件通道压根不参与 ——
    /// 与其塞一个二十来个成员的空壳,不如把"没用到"直接写出来。
    /// </summary>
    private static IRemoteFsApi RemoteFs => null!;

    /// <summary>测试上下文(取消令牌用)。</summary>
    public TestContext TestContext { get; set; } = null!;

    private sealed class RecordingExec : IRemoteExecApi
    {
        public string LastCommand { get; private set; } = "";

        public Task<ExecResult> RunAsync(string sessionId, string command, ExecOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(new ExecResult("") { ExitCode = 0 });
        }

        public Task<ExecStreamResult> StreamAsync(string sessionId, string command, ExecStreamOptions? options,
            IProgress<ExecOutput> output, CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            return Task.FromResult(new ExecStreamResult(0, 0));
        }
    }
}

/// <summary>
/// 本机那条通道的实地校验。
/// <para>
/// 这台机器上没有 <c>docker</c> 时整类跳过 —— 它验的是"起进程、收两条流、拿退出码"
/// 这条真实路径,不是可以拿假对象糊过去的东西。
/// </para>
/// </summary>
[TestClass]
public sealed class LocalComposeHostSmokeTests
{
    private static readonly LocalComposeHost Host = new();

    /// <summary>测试上下文(取消令牌用)。</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LocalHost_RunsDockerComposeAndBringsBackStdout()
    {
        ExecResult result;
        try
        {
            result = await Host.RunAsync(["compose", "version", "--short"], TimeSpan.FromSeconds(30),
                TestContext.CancellationToken);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"这台机器上起不了 docker:{ex.Message}");
            return;
        }
        if (result.ExitCode != 0 && LooksLikeNoDaemon(result.Error))
        {
            Assert.Inconclusive($"这台机器上 docker 守护进程没起:{result.Error}");
            return;
        }
        Assert.AreEqual(0, result.ExitCode, result.Error);
        // compose v2 的版本号形如 2.29.7 / 5.4.0 —— 只断言"有内容且以数字打头"。
        var version = result.Output.Trim();
        Assert.IsTrue(version.Length > 0 && char.IsAsciiDigit(version[0]), $"版本号不像话:{version}");
    }

    [TestMethod]
    public async Task LocalHost_StreamsBothPipesLineByLine()
    {
        List<ExecOutput> lines = [];
        int exit;
        try
        {
            exit = await Host.StreamAsync(["compose", "ls", "--all", "--format", "json"],
                new CollectingProgress(lines), TestContext.CancellationToken);
        }
        catch (Exception ex)
        {
            Assert.Inconclusive($"这台机器上起不了 docker:{ex.Message}");
            return;
        }
        var output = string.Join('\n', lines.Select(l => l.Line));
        if (exit != 0 && LooksLikeNoDaemon(output))
        {
            Assert.Inconclusive($"这台机器上 docker 守护进程没起:{output}");
            return;
        }
        Assert.AreEqual(0, exit, output);
        // --format json 至少给一个 JSON 数组,哪怕是空的。
        var joined = string.Concat(lines.Where(l => l.Stream == ExecStream.StandardOutput).Select(l => l.Line));
        Assert.StartsWith("[", joined.TrimStart());
    }

    /// <summary>
    /// "docker 在,但守护进程没起"。
    /// <para>
    /// 与"这台机器压根没装 docker"是两回事,而上面那两处 try/catch 只拦得住后者 ——
    /// 没装时进程起不来会抛异常,装了但守护进程没起时 <c>docker compose</c> 正常退出,
    /// 只是**退出码非零 + stderr 写着连不上**。于是这两条用例在 CI runner
    /// (装了 docker、守护进程不一定起着)与没开 Docker Desktop 的开发机上都会判失败,
    /// 而它们本该是 Inconclusive —— 那不是被测代码的问题。
    /// </para>
    /// </summary>
    private static bool LooksLikeNoDaemon(string? text) =>
        !string.IsNullOrEmpty(text)
        && (text.Contains("docker API", StringComparison.OrdinalIgnoreCase)
            || text.Contains("daemon", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pipe/docker", StringComparison.OrdinalIgnoreCase));

    private sealed class CollectingProgress(List<ExecOutput> sink) : IProgress<ExecOutput>
    {
        public void Report(ExecOutput value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
