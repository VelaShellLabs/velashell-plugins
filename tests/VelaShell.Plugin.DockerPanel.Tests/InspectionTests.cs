using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui;
using VelaShell.Plugin.DockerPanel.Ui.Pages;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 面板"看进去"的那几处翻译:容器里的进程表、镜像的构建历史、可回收空间。
/// 这三处的共同点是 daemon 给的东西**形状不固定** —— 列由远端的 ps 决定、
/// 指令带着构建器的脚手架、df 的口径要和 prune 对得上。
/// 这里钉的就是这些翻译。
/// </summary>
[TestClass]
public class InspectionTests
{
    // ── docker top ────────────────────────────────────────────────

    [TestMethod]
    public void ProcessTable_ReadsGnuPsMinusEfByColumnName()
    {
        var top = new ContainerTopResult
        {
            Titles = ["UID", "PID", "PPID", "C", "STIME", "TTY", "TIME", "CMD"],
            Processes = [["root", "1234", "1200", "0", "10:21", "?", "00:00:03", "nginx: master process"]]
        };

        var rows = ProcessTable.Normalize(top);

        Assert.HasCount(1, rows);
        Assert.AreEqual("1234", rows[0].Pid);
        Assert.AreEqual("root", rows[0].User);
        Assert.AreEqual("00:00:03", rows[0].Cpu);
        Assert.AreEqual("nginx: master process", rows[0].Command);
    }

    [TestMethod]
    public void ProcessTable_ReadsBusyboxLayoutWhereColumnsAreInADifferentOrder()
    {
        // Alpine 的 busybox ps 给的是另一套列。按下标取会把 TIME 显示成用户名 ——
        // 这正是这一层存在的理由。
        var top = new ContainerTopResult
        {
            Titles = ["PID", "USER", "TIME", "COMMAND"],
            Processes = [["1", "nobody", "0:00", "/bin/sh -c server"]]
        };

        var rows = ProcessTable.Normalize(top);

        Assert.AreEqual("1", rows[0].Pid);
        Assert.AreEqual("nobody", rows[0].User);
        Assert.AreEqual("0:00", rows[0].Cpu);
        Assert.AreEqual("/bin/sh -c server", rows[0].Command);
    }

    [TestMethod]
    public void ProcessTable_FallsBackToTheLastColumnWhenTheCommandColumnHasAnUnknownName()
    {
        var top = new ContainerTopResult
        {
            Titles = ["PID", "USER", "TIME", "WHATEVER"],
            Processes = [["7", "root", "0:01", "/usr/bin/redis-server"]]
        };

        // 命令永远排在末尾 —— 只有它能带空格,ps 没别的地方放它。
        Assert.AreEqual("/usr/bin/redis-server", ProcessTable.Normalize(top)[0].Command);
    }

    [TestMethod]
    public void ProcessTable_SurvivesRowsShorterThanTheTitleRow()
    {
        var top = new ContainerTopResult
        {
            Titles = ["UID", "PID", "PPID", "C", "STIME", "TTY", "TIME", "CMD"],
            Processes = [["root", "1"]]
        };

        var rows = ProcessTable.Normalize(top);

        // 缺列不能抛:一个越界异常会把整张表换成一句报错,而前两列本来是读得到的。
        Assert.AreEqual("1", rows[0].Pid);
        Assert.AreEqual("root", rows[0].User);
        Assert.AreEqual("", rows[0].Cpu);
    }

    [TestMethod]
    public void ProcessTable_EmptyResultIsEmptyNotAnError() =>
        Assert.IsEmpty(ProcessTable.Normalize(null));

    [TestMethod]
    public void ProcessTable_NamesTheCpuColumnAfterWhatTheRemotePsActuallyGave()
    {
        Assert.AreEqual("TIME", ProcessTable.CpuColumnTitle(new() { Titles = ["PID", "USER", "TIME", "CMD"] }));
        Assert.AreEqual("%CPU", ProcessTable.CpuColumnTitle(new() { Titles = ["USER", "PID", "%CPU", "COMMAND"] }));
        Assert.AreEqual("CPU", ProcessTable.CpuColumnTitle(new() { Titles = ["PID", "CMD"] }));
    }

    // ── 镜像层历史 ────────────────────────────────────────────────

    [TestMethod]
    public void CleanInstruction_StripsTheBuilderScaffoldingOffMetadataLayers()
    {
        Assert.AreEqual("ENV PATH=/usr/local/bin",
            ImageDetailViewModel.CleanInstruction("/bin/sh -c #(nop)  ENV PATH=/usr/local/bin"));
        Assert.AreEqual("EXPOSE 80",
            ImageDetailViewModel.CleanInstruction("/bin/sh -c #(nop) EXPOSE 80"));
    }

    [TestMethod]
    public void CleanInstruction_PutsRunBackOnRealCommandLayers()
    {
        // 剥掉 /bin/sh -c 之后只剩一句裸命令,夹在一排 COPY / ENV 中间读不出它是什么。
        Assert.AreEqual("RUN apt-get update && apt-get install -y curl",
            ImageDetailViewModel.CleanInstruction("/bin/sh -c apt-get update && apt-get install -y curl"));
    }

    [TestMethod]
    public void CleanInstruction_LeavesBuildKitStyleEntriesAlone()
    {
        Assert.AreEqual("RUN /bin/sh -c make build # buildkit",
            ImageDetailViewModel.CleanInstruction("RUN /bin/sh -c make build # buildkit"));
        Assert.AreEqual("COPY . /app # buildkit",
            ImageDetailViewModel.CleanInstruction("COPY . /app # buildkit"));
    }

    [TestMethod]
    public void CleanInstruction_SaysSomethingWhenTheresNothingToSay() =>
        Assert.AreEqual("(无记录)", ImageDetailViewModel.CleanInstruction(null));

    // ── system df ─────────────────────────────────────────────────

    [TestMethod]
    public void Reclaimable_CountsEveryUnusedImageNotJustTheDanglingOnes()
    {
        var usage = new DiskUsage
        {
            Images =
            [
                new() { Id = "sha256:a", Size = 100, Containers = 1, RepoTags = ["app:1"] },
                new() { Id = "sha256:b", Size = 250, Containers = 0, RepoTags = ["old:1"] },
                new() { Id = "sha256:c", Size = 50, Containers = 0, RepoTags = ["<none>:<none>"] }
            ]
        };

        var reclaim = DiskMath.Reclaimable(usage);

        // 只算悬空的那 50 会把可回收量报少五倍 —— prune -a 会把 old:1 也删掉。
        Assert.AreEqual(300, reclaim.Images);
        Assert.AreEqual(2, reclaim.UnusedImages);
    }

    [TestMethod]
    public void Reclaimable_CountsOnlyVolumesNothingRefersTo()
    {
        var usage = new DiskUsage
        {
            Volumes =
            [
                new() { Name = "used", UsageData = new() { Size = 900, RefCount = 2 } },
                new() { Name = "orphan", UsageData = new() { Size = 400, RefCount = 0 } }
            ]
        };

        var reclaim = DiskMath.Reclaimable(usage);

        Assert.AreEqual(400, reclaim.Volumes);
        Assert.AreEqual(1, reclaim.UnusedVolumes);
    }

    [TestMethod]
    public void Reclaimable_SkipsBuildCacheThatIsStillInUse()
    {
        var usage = new DiskUsage
        {
            BuildCache =
            [
                new() { ID = "x", Size = 1000, InUse = true },
                new() { ID = "y", Size = 700, InUse = false }
            ]
        };

        Assert.AreEqual(700, DiskMath.Reclaimable(usage).BuildCache);
    }

    [TestMethod]
    public void Reclaimable_TotalIsTheSumOfThreeAndSurvivesAnEmptyReport()
    {
        var usage = new DiskUsage
        {
            Images = [new() { Id = "sha256:b", Size = 250, Containers = 0 }],
            Volumes = [new() { Name = "orphan", UsageData = new() { Size = 400, RefCount = 0 } }],
            BuildCache = [new() { ID = "y", Size = 700, InUse = false }]
        };

        Assert.AreEqual(1350, DiskMath.Reclaimable(usage).Total);
        // daemon 在全新机器上把这三段都给 null,不能因此抛。
        Assert.AreEqual(0, DiskMath.Reclaimable(new DiskUsage()).Total);
        Assert.AreEqual(0, DiskMath.Reclaimable(null).Total);
    }

    [TestMethod]
    public void Reclaimable_SaysNothingToCleanWhenThereIsNothing() =>
        Assert.AreEqual("没有可回收的空间", DiskMath.Reclaimable(new DiskUsage()).Describe());

    // ── 时间口径 ──────────────────────────────────────────────────

    [TestMethod]
    public void AgoFromIso_HandlesTheRfc3339FormThatImageInspectUses()
    {
        var text = Humanize.AgoFromIso(DateTimeOffset.UtcNow.AddHours(-3).ToString("O"));

        Assert.EndsWith("前", text);
        // 列表接口给 unix 秒、inspect 给 RFC3339,两条路要落到同一句话上。
        Assert.AreEqual("—", Humanize.AgoFromIso("not a timestamp"));
        Assert.AreEqual("—", Humanize.AgoFromIso(null));
    }
}
