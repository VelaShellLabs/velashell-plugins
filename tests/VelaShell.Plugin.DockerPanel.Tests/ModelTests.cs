using System.Text.Json;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// DTO 与显示口径。这里钉的是"面板显示的数字要和用户在 docker 命令行里看到的对得上"。
/// </summary>
[TestClass]
public class ModelTests
{
    [TestMethod]
    public void Stats_CpuPercentMatchesDockerStatsFormula()
    {
        var sample = JsonSerializer.Deserialize<ContainerStats>("""
            {
              "cpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 20000, "online_cpus": 4 },
              "precpu_stats": { "cpu_usage": { "total_usage": 1000 }, "system_cpu_usage": 10000, "online_cpus": 4 }
            }
            """, DockerJson.Options);

        Assert.IsNotNull(sample);
        // (1000 / 10000) * 4 * 100 = 40%
        Assert.AreEqual(40, sample.CpuPercent, 0.001);
    }

    [TestMethod]
    public void Stats_FirstFrameHasNoPreviousSampleAndReportsZero()
    {
        var sample = JsonSerializer.Deserialize<ContainerStats>("""
            { "cpu_stats": { "cpu_usage": { "total_usage": 2000 }, "system_cpu_usage": 20000 } }
            """, DockerJson.Options);

        // 第一帧的 precpu 是空的 —— 编一个数出来就是一个假的尖峰。
        Assert.AreEqual(0, sample!.CpuPercent);
    }

    [TestMethod]
    public void Stats_MemoryExcludesPageCache()
    {
        var sample = JsonSerializer.Deserialize<ContainerStats>("""
            { "memory_stats": { "usage": 1000, "limit": 4000, "stats": { "inactive_file": 400 } } }
            """, DockerJson.Options);

        // 不扣缓存的话,一个只是读过大文件的容器会显示成"内存快满了"。
        Assert.AreEqual(600UL, sample!.MemoryUsed);
    }

    [TestMethod]
    public void Stats_FallsBackToCgroupV1CacheField()
    {
        var sample = JsonSerializer.Deserialize<ContainerStats>("""
            { "memory_stats": { "usage": 1000, "limit": 4000, "stats": { "cache": 250 } } }
            """, DockerJson.Options);

        Assert.AreEqual(750UL, sample!.MemoryUsed);
    }

    [TestMethod]
    public void ContainerSummary_StripsTheLeadingSlashFromNames()
    {
        var summary = JsonSerializer.Deserialize<ContainerSummary>("""
            { "Id": "abc123", "Names": ["/nginx-proxy"], "State": "running" }
            """, DockerJson.Options);

        Assert.AreEqual("nginx-proxy", summary!.Name);
    }

    [TestMethod]
    public void ContainerSummary_ReadsComposeLabels()
    {
        var summary = JsonSerializer.Deserialize<ContainerSummary>("""
            {
              "Id": "abc",
              "Labels": { "com.docker.compose.project": "web-stack", "com.docker.compose.service": "proxy" }
            }
            """, DockerJson.Options);

        Assert.AreEqual("web-stack", summary!.ComposeProject);
        Assert.AreEqual("proxy", summary.ComposeService);
    }

    [TestMethod]
    public void ImageSummary_DetectsDanglingImages()
    {
        var dangling = JsonSerializer.Deserialize<ImageSummary>("""
            { "Id": "sha256:aaa", "RepoTags": ["<none>:<none>"] }
            """, DockerJson.Options);
        var tagged = JsonSerializer.Deserialize<ImageSummary>("""
            { "Id": "sha256:bbb", "RepoTags": ["nginx:1.27-alpine"] }
            """, DockerJson.Options);

        Assert.IsTrue(dangling!.IsDangling);
        Assert.IsFalse(tagged!.IsDangling);
        Assert.AreEqual("aaa", dangling.ShortId);
    }

    [TestMethod]
    public void NetworkSummary_KnowsThePredefinedNetworksCannotBeDeleted()
    {
        foreach (var name in new[] { "bridge", "host", "none" })
        {
            Assert.IsTrue(new NetworkSummary { Name = name }.IsPredefined, name);
        }
        Assert.IsFalse(new NetworkSummary { Name = "web-stack_default" }.IsPredefined);
    }

    [TestMethod]
    public void DockerJson_ToleratesNumbersEncodedAsStrings()
    {
        // compose 与几条统计流会把数字写成字符串;严格模式下这会整条反序列化失败。
        var port = JsonSerializer.Deserialize<DockerPort>("""
            { "PrivatePort": "80", "PublicPort": "8080", "Type": "tcp" }
            """, DockerJson.Options);

        Assert.AreEqual(80, port!.PrivatePort);
        Assert.AreEqual(8080, port.PublicPort);
    }

    [TestMethod]
    public void Ports_DeduplicatesTheIPv4AndIPv6BindingsOfOnePort()
    {
        var text = Humanize.Ports([
            new() { IP = "0.0.0.0", PrivatePort = 80, PublicPort = 8080, Type = "tcp" },
            new() { IP = "::", PrivatePort = 80, PublicPort = 8080, Type = "tcp" }
        ]);

        // daemon 会给两条,但界面上它们是同一件事。
        Assert.AreEqual("8080→80", text);
    }

    [TestMethod]
    public void Ports_ShowsUnpublishedPortsSeparately()
    {
        var text = Humanize.Ports([new() { PrivatePort = 8000, PublicPort = 0, Type = "tcp" }]);
        Assert.AreEqual("8000/tcp", text);
    }

    [TestMethod]
    public void Bytes_UsesTheSameDecimalUnitsAsDocker()
    {
        // 面板显示的 "214 MB" 要和用户在 docker images 里看到的对得上。
        Assert.AreEqual("999 B", Humanize.Bytes(999));
        Assert.AreEqual("1 KB", Humanize.Bytes(1000));
        Assert.AreEqual("214 MB", Humanize.Bytes(214_000_000));
        Assert.AreEqual("—", Humanize.Bytes(-1));
    }

    [TestMethod]
    public void Duration_KeepsTwoUnitsAtMost()
    {
        Assert.AreEqual("3d 4h", Humanize.Duration(TimeSpan.FromHours(76)));
        Assert.AreEqual("6h 12m", Humanize.Duration(TimeSpan.FromMinutes(372)));
        Assert.AreEqual("48s", Humanize.Duration(TimeSpan.FromSeconds(48)));
    }

    [TestMethod]
    public void ShortId_HandlesBothPrefixedAndBareIds()
    {
        Assert.AreEqual("9c4f2e1b7a11", Humanize.ShortId("sha256:9c4f2e1b7a1122334455"));
        Assert.AreEqual("a3f2c81b9d4e", Humanize.ShortId("a3f2c81b9d4e5566"));
    }

    [TestMethod]
    public void RegistryAuth_ResolvesTheRegistryTheSameWayDockerDoes()
    {
        Assert.AreEqual(RegistryAuthProvider.DockerHub, RegistryAuthProvider.ResolveRegistry("nginx"));
        Assert.AreEqual(RegistryAuthProvider.DockerHub, RegistryAuthProvider.ResolveRegistry("library/nginx"));
        Assert.AreEqual("ghcr.io", RegistryAuthProvider.ResolveRegistry("ghcr.io/acme/api"));
        Assert.AreEqual("registry:5000", RegistryAuthProvider.ResolveRegistry("registry:5000/acme/api"));
        Assert.AreEqual("localhost", RegistryAuthProvider.ResolveRegistry("localhost/acme/api"));
    }

    [TestMethod]
    public void ComposeProject_DerivesTheProjectDirectoryFromTheComposeFile()
    {
        var project = new ComposeProject("web-stack", "running(3)", "/srv/stacks/web-stack/compose.yaml");

        Assert.AreEqual("/srv/stacks/web-stack", project.ProjectDirectory);
        Assert.AreEqual(3, project.RunningCount);
    }

    [TestMethod]
    public void DockerEndpoint_LocalPicksThePlatformDefaultSocket()
    {
        var local = DockerEndpoint.Local("本机 Docker");

        Assert.AreEqual(DockerEndpointKind.Local, local.Kind);
        Assert.AreEqual(OperatingSystem.IsWindows() ? DockerEndpoint.DefaultWindowsPipe : DockerEndpoint.DefaultUnixSocket,
            local.SocketPath);
    }

    [TestMethod]
    public void DockerEndpoint_RemoteDefaultsToTheStandardSocket()
    {
        var remote = DockerEndpoint.Remote("session-1", "prod-sg-01", "deploy@10.24.8.11");

        Assert.AreEqual(DockerEndpoint.DefaultUnixSocket, remote.SocketPath);
    }

    [TestMethod]
    public void ContainerRow_ClassifiesStateIntoTheRightTone()
    {
        Assert.AreEqual(RowTone.Ok, Row("running", "Up 3 days").Tone);
        Assert.AreEqual(RowTone.Danger, Row("running", "Up 3 days (unhealthy)").Tone);
        Assert.AreEqual(RowTone.Warn, Row("paused", "Up 2 days (Paused)").Tone);
        Assert.AreEqual(RowTone.Idle, Row("exited", "Exited (0) 5 hours ago").Tone);
        Assert.AreEqual(RowTone.Danger, Row("exited", "Exited (1) 2 hours ago").Tone);
        Assert.AreEqual(RowTone.Busy, Row("restarting", "Restarting (1) 3 seconds ago").Tone);
    }

    [TestMethod]
    public void ContainerRow_ReadsUptimeOutOfTheDaemonStatusText()
    {
        Assert.AreEqual("3 days", Row("running", "Up 3 days (healthy)").Uptime);
        Assert.AreEqual("退出 (1) 2 hours ago", Row("exited", "Exited (1) 2 hours ago").Uptime);
    }

    private static ContainerRow Row(string state, string status) =>
        new(new() { Id = "abc123def456", Names = ["/x"], State = state, Status = status });
}
