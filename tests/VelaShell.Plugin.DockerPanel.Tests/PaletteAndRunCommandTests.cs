using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 命令面板的筛选/排序,以及从 inspect 反推 <c>docker run</c>。
/// <para>
/// 后者是近似的,所以这里钉的不是"字节级还原",而是那几条**会让人踩坑**的取舍:
/// 镜像自带的环境变量要减掉、停掉的容器也要能推出端口、命名卷不能漏。
/// </para>
/// </summary>
[TestClass]
public class PaletteAndRunCommandTests
{
    // ── 命令面板 ──────────────────────────────────────────────────

    private static CommandPalette Palette(params PaletteEntry[] entries) => new(() => entries);

    private static PaletteEntry Entry(string group, string title, string detail = "") =>
        new(group, title, detail, "Icon.info", RowTone.Idle, false, () => Task.CompletedTask);

    [TestMethod]
    public void OpenCollectsFreshEntriesEveryTime()
    {
        var collected = 0;
        var palette = new CommandPalette(() =>
        {
            collected++;
            return [Entry("容器", "nginx")];
        });

        palette.Open("prod-sg-01");
        palette.CloseCommand.Execute(null);
        palette.Open("prod-sg-01");

        // 不缓存:容器列表随时在变,缓存会让用户对着一个已经不存在的容器按回车。
        Assert.AreEqual(2, collected);
        Assert.AreEqual("prod-sg-01", palette.HostName);
    }

    [TestMethod]
    public void FirstItemOfEachGroupCarriesTheGroupHeader()
    {
        var palette = Palette(
            Entry("动作", "重启 nginx-proxy"),
            Entry("动作", "重启 api-gateway"),
            Entry("容器", "redis-cache"));
        palette.Open("h");

        Assert.IsTrue(palette.Items[0].ShowGroup);
        Assert.AreEqual(2, palette.Items[0].GroupCount);
        Assert.IsFalse(palette.Items[1].ShowGroup);
        Assert.IsTrue(palette.Items[2].ShowGroup);
        Assert.AreEqual(1, palette.Items[2].GroupCount);
    }

    [TestMethod]
    public void TitleMatchesOutrankDetailMatches()
    {
        var palette = Palette(
            Entry("镜像 / 卷", "pg-data", "卷 · 名字里没有那个词"),
            Entry("动作", "restart nginx", "运行中"),
            Entry("容器", "some-container", "restart policy: always"));
        palette.Open("h");

        palette.Query = "restart";

        // 标题前缀命中排最前;只在小字里命中的排最后。
        Assert.AreEqual("restart nginx", palette.Items[0].Title);
        Assert.AreEqual("some-container", palette.Items[^1].Title);
    }

    [TestMethod]
    public void ArrowKeysWrapAround()
    {
        var palette = Palette(Entry("g", "a"), Entry("g", "b"), Entry("g", "c"));
        palette.Open("h");

        Assert.IsTrue(palette.Items[0].Active);
        palette.Move(-1);
        // 到顶再往上回到最后一条,比停在那里不动更符合预期。
        Assert.IsTrue(palette.Items[2].Active);
        palette.Move(1);
        Assert.IsTrue(palette.Items[0].Active);
    }

    [TestMethod]
    public void TabCompletesWithoutTheEllipsis()
    {
        var palette = Palette(Entry("面板命令", "清理未使用的镜像…"));
        palette.Open("h");

        palette.Complete();

        // 省略号代表"还会再问你一次",补进搜索框只会让下一次匹配失败。
        Assert.AreEqual("清理未使用的镜像", palette.Query);
    }

    [TestMethod]
    public void EscapeClosesWithoutRunningAnything()
    {
        var ran = false;
        var palette = new CommandPalette(() =>
        [
            new("g", "t", "", "Icon.info", RowTone.Idle, false, () =>
            {
                ran = true;
                return Task.CompletedTask;
            })
        ]);
        palette.Open("h");

        palette.CloseCommand.Execute(null);

        Assert.IsFalse(palette.IsOpen);
        Assert.IsFalse(ran);
    }

    [TestMethod]
    public void EmptyResultIsReportedSoTheViewCanSaySomething()
    {
        var palette = Palette(Entry("容器", "nginx"));
        palette.Open("h");

        palette.Query = "没有这个东西";

        // 一片空白不如一句"没有匹配的命令"。
        Assert.IsTrue(palette.IsEmpty);
        Assert.HasCount(0, palette.Items);
    }

    // ── docker run 反推 ───────────────────────────────────────────

    [TestMethod]
    public void SubtractsTheImageOwnEnvironment()
    {
        string[] container = ["PATH=/usr/local/sbin:/usr/bin", "NGINX_VERSION=1.27", "APP_MODE=prod"];
        string[] image = ["PATH=/usr/local/sbin:/usr/bin", "NGINX_VERSION=1.27"];

        string[] user = [.. RunCommandBuilder.UserEnv(container, image)];

        // 不减的话命令会拖着镜像里那十几条 PATH / LANG —— 它们不是用户写的。
        Assert.AreSequenceEqual(["APP_MODE=prod"], user);
    }

    [TestMethod]
    public void KeepsEverythingWhenTheImageIsGone()
    {
        string[] container = ["A=1", "B=2"];

        // 镜像被删了但容器还在跑很常见 —— 那就不减,多几条环境变量而已。
        Assert.AreSequenceEqual(container, [.. RunCommandBuilder.UserEnv(container, null)]);
    }

    [TestMethod]
    public void BuildsFromDeclaredConfigSoStoppedContainersStillWork()
    {
        var inspect = new ContainerInspect
        {
            Id = "abc",
            Name = "/nginx-proxy",
            Config = new()
            {
                Image = "nginx:1.27-alpine",
                Env = ["APP_MODE=prod"],
                WorkingDir = "/etc/nginx"
            },
            HostConfig = new()
            {
                RestartPolicy = new() { Name = "unless-stopped" },
                NetworkMode = "web-stack_default",
                PortBindings = new()
                {
                    ["80/tcp"] = [new() { HostPort = "8080" }],
                    ["443/tcp"] = [new() { HostIp = "127.0.0.1", HostPort = "8443" }]
                },
                Binds = ["/srv/nginx/conf.d:/etc/nginx/conf.d:ro"]
            },
            Mounts = [new() { Type = "volume", Name = "nginx-cache", Destination = "/var/cache/nginx", RW = true }]
        };

        var command = RunCommandBuilder.Build(inspect, null);

        Assert.Contains("--name 'nginx-proxy'", command);
        // 端口取自 PortBindings 而不是运行态的 Ports:容器停了 Ports 就是空的,
        // 而"这个停掉的容器当初怎么起的"恰恰是最需要这条命令的时候。
        // /tcp 是 -p 的默认协议,不写出来。
        Assert.Contains("-p '8080:80'", command);
        Assert.Contains("-p '127.0.0.1:8443:443'", command);
        Assert.Contains("-v '/srv/nginx/conf.d:/etc/nginx/conf.d:ro'", command);
        // 命名卷走 Mounts,Binds 里没有 —— 漏掉它重建出来的容器会丢数据。
        Assert.Contains("-v 'nginx-cache:/var/cache/nginx'", command);
        Assert.Contains("-e 'APP_MODE=prod'", command);
        Assert.Contains("--restart unless-stopped", command);
        Assert.Contains("--network 'web-stack_default'", command);
        Assert.Contains("-w '/etc/nginx'", command);
        Assert.EndsWith("'nginx:1.27-alpine'", command);
    }

    [TestMethod]
    public void KeepsNonDefaultProtocols()
    {
        var inspect = new ContainerInspect
        {
            Config = new() { Image = "coredns/coredns:1.11" },
            HostConfig = new()
            {
                PortBindings = new() { ["53/udp"] = [new() { HostPort = "53" }] }
            }
        };

        // udp 不写就变成了 tcp,那是一个跑不起来的 DNS。
        Assert.Contains("-p '53:53/udp'", RunCommandBuilder.Build(inspect, null));
    }

    [TestMethod]
    public void SkipsDefaultsThatWouldBeNoise()
    {
        var inspect = new ContainerInspect
        {
            Config = new() { Image = "redis:7.4" },
            HostConfig = new() { RestartPolicy = new() { Name = "no" }, NetworkMode = "default" }
        };

        var command = RunCommandBuilder.Build(inspect, null);

        // --restart no 与 --network default 就是不写时的行为,写出来只是噪音。
        Assert.IsFalse(command.Contains("--restart", StringComparison.Ordinal));
        Assert.IsFalse(command.Contains("--network", StringComparison.Ordinal));
        Assert.AreEqual("docker run -d 'redis:7.4'", command);
    }

    [TestMethod]
    public void CarriesACaveatBecauseTheReconstructionIsApproximate()
    {
        // Engine 不保存原始命令行,只保存生效后的配置 —— 产物必须说明这一点,
        // 而不是假装它可以照抄执行。
        Assert.Contains("近似", RunCommandBuilder.Caveat);
    }
}
