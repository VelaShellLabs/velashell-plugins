using Avalonia.Headless;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui;
using VelaShell.Plugin.DockerPanel.Ui.Pages;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 面板逻辑:批量判定、进度聚合、行合并、确认闸门、表单校验。
/// <para>
/// 这一层没有界面,但界面上最要紧的那几句话("成功 8、失败 2 —— 谁失败了、为什么")
/// 全是它算出来的。
/// </para>
/// </summary>
[TestClass]
public class PanelLogicTests
{
    // 确认闸门那几条必须在一条真的在泵的 UI 线程上跑,理由见 DockerPanelHeadlessApp。
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void InitSession(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PanelLogicTests).Assembly);

    /// <summary>
    /// 在 headless UI 线程上跑一段测试体。lambda 必须带返回值 ——
    /// <see cref="HeadlessUnitTestSession" /> 没有 <c>Func&lt;Task&gt;</c> 重载,
    /// 写成无返回值会拿到一个从未被等待的 <c>Task&lt;Task&gt;</c>:测试体跑到第一个 await 就
    /// “通过”了,后面的断言失败全部丢失(Redis 面板测试记的是同一条)。
    /// </summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    // ── 批量 ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Batch_JudgesEachTargetSeparatelyAndKeepsGoing()
    {
        var result = await BatchRunner.RunAsync(
            [("a", "worker-1"), ("b", "postgres-main"), ("c", "worker-2")],
            (target, _) => target == "b"
                ? throw new DockerApiException(System.Net.HttpStatusCode.Conflict,
                    "conflict: container is being used by api-gateway")
                : Task.CompletedTask, cancellationToken: TestContext.CancellationToken);

        // 一个目标失败不能把后面的目标一起拖下水 —— 界面要报"成功 2、失败 1",
        // 而不是把整批说成"操作失败"。
        Assert.AreEqual(2, result.SucceededCount);
        Assert.AreEqual(1, result.FailedCount);
        Assert.AreEqual("postgres-main", result.Failures.Single().Target);
        Assert.Contains("api-gateway", result.Failures.Single().Failure!);
    }

    [TestMethod]
    public async Task Batch_StopsAfterTheConnectionDies()
    {
        var attempts = 0;
        var result = await BatchRunner.RunAsync(
            [("a", "one"), ("b", "two"), ("c", "three")],
            (_, _) =>
            {
                attempts++;
                throw new DockerUnreachableException("连接断了", DockerUnreachableReason.SessionUnavailable);
            }, cancellationToken: TestContext.CancellationToken);

        // 连接没了就没必要继续戳后面的目标 —— 它们只会拿到同一条错误。
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(3, result.FailedCount);
        Assert.Contains("没有执行", result.Outcomes[2].Failure!);
    }

    [TestMethod]
    public async Task Batch_ReportsProgressForEveryTarget()
    {
        List<(int Done, int Total)> progress = [];
        await BatchRunner.RunAsync(
            [("a", "one"), ("b", "two")],
            (_, _) => Task.CompletedTask,
            (done, total, _) => progress.Add((done, total)), TestContext.CancellationToken);

        Assert.AreSequenceEqual([(0, 2), (1, 2), (2, 2)], progress);
    }

    // ── 拉取进度聚合 ──────────────────────────────────────────

    [TestMethod]
    public void PullAggregator_TracksBytesAcrossLayers()
    {
        var aggregator = new PullAggregator();
        aggregator.Accept(new() { Id = "a", Status = "Downloading", ProgressDetail = new() { Current = 50, Total = 100 } });
        aggregator.Accept(new() { Id = "b", Status = "Downloading", ProgressDetail = new() { Current = 25, Total = 100 } });

        Assert.AreEqual(2, aggregator.LayerCount);
        Assert.AreEqual(75, aggregator.CurrentBytes);
        Assert.AreEqual(200, aggregator.TotalBytes);
        Assert.AreEqual(0.375, aggregator.Progress, 0.001);
    }

    [TestMethod]
    public void PullAggregator_DoesNotRegressWhenALayerCompletes()
    {
        var aggregator = new PullAggregator();
        aggregator.Accept(new() { Id = "a", Status = "Downloading", ProgressDetail = new() { Current = 80, Total = 100 } });
        // "Pull complete" 之后不再有字节明细;沿用上次的 total 并补满,
        // 否则进度条会在最后一刻往回跳。
        aggregator.Accept(new() { Id = "a", Status = "Pull complete" });

        Assert.AreEqual(100, aggregator.CurrentBytes);
        Assert.AreEqual(1, aggregator.Progress, 0.001);
    }

    [TestMethod]
    public void PullAggregator_FallsBackToLayerCountWhenEverythingIsCached()
    {
        var aggregator = new PullAggregator();
        aggregator.Accept(new() { Id = "a", Status = "Already exists" });
        aggregator.Accept(new() { Id = "b", Status = "Already exists" });

        // 一次全命中缓存的拉取一个字节都不报;按字节算会显示成 0% 然后直接跳到完成。
        Assert.AreEqual(2, aggregator.ReusedLayers);
        Assert.AreEqual(1, aggregator.Progress, 0.001);
    }

    [TestMethod]
    public void PullAggregator_IgnoresFramesWithoutALayerId()
    {
        var aggregator = new PullAggregator();
        aggregator.Accept(new() { Status = "Pulling from library/nginx" });
        aggregator.Accept(new() { Status = "Digest: sha256:abc" });

        Assert.AreEqual(0, aggregator.LayerCount);
    }

    // ── 行合并 ────────────────────────────────────────────────

    private sealed class Item(string id) : ObservableObject
    {
        public string Id { get; } = id;
        public int Version { get; set; }
    }

    [TestMethod]
    public void Merge_KeepsExistingInstancesSoSelectionSurvivesARefresh()
    {
        var collection = new KeyedCollection<Item>(i => i.Id);
        var a = new Item("a");
        var b = new Item("b");
        collection.Merge([a, b], (_, _) => { });
        var keptA = collection[0];

        collection.Merge([new Item("a"), new Item("b")], (current, incoming) => current.Version++);

        // 简单地 Clear + AddRange 会把选中态、滚动位置与展开状态全部清掉,
        // 而这个面板每秒都可能因为一条事件而刷新。
        Assert.AreSame(keptA, collection[0]);
        Assert.AreEqual(1, collection[0].Version);
    }

    [TestMethod]
    public void Merge_AddsRemovesAndReorders()
    {
        var collection = new KeyedCollection<Item>(i => i.Id);
        collection.Merge([new Item("a"), new Item("b"), new Item("c")], (_, _) => { });

        collection.Merge([new Item("c"), new Item("a"), new Item("d")], (_, _) => { });

        Assert.AreSequenceEqual(["c", "a", "d"], [.. collection.Select(i => i.Id)]);
    }

    // ── 确认闸门 ──────────────────────────────────────────────

    private static ConfirmRequest DataLossRequest() => new()
    {
        Title = "删除卷 pg-data?",
        HostName = "prod-sg-01",
        ConfirmLabel = "永久删除卷",
        Tier = ConfirmTier.DataLoss,
        ConfirmWord = "delete"
    };

    [TestMethod]
    public void Gate_DataLossTierStaysLockedUntilTheWordIsTypedExactly() => OnUi(() =>
    {
        var gate = new ConfirmGate();
        _ = gate.AskAsync(DataLossRequest());

        gate.TypedWord = "delet";
        Assert.IsFalse(gate.CanConfirm);
        Assert.Contains("还差 1", gate.RemainingHint);

        gate.TypedWord = "DELETE";
        // 大小写不同就是不同 —— 这道闸门的全部意义就是"必须精确地打对"。
        Assert.IsFalse(gate.CanConfirm);

        gate.TypedWord = "delete";
        Assert.IsTrue(gate.CanConfirm);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Gate_DestructiveTierNeedsNoTypedWord() => OnUi(() =>
    {
        var gate = new ConfirmGate();
        _ = gate.AskAsync(new()
        {
            Title = "删除 2 个容器?",
            HostName = "prod-sg-01",
            ConfirmLabel = "删除"
        });

        Assert.IsTrue(gate.CanConfirm);
        Assert.IsFalse(gate.IsDataLoss);
        return Task.CompletedTask;
    });

    [TestMethod]
    public void Gate_ConfirmAndCancelResolveTheWaiter() => OnUi(async () =>
    {
        var gate = new ConfirmGate();
        var pending = gate.AskAsync(DataLossRequest());
        gate.TypedWord = "delete";
        gate.ConfirmCommand.Execute(null);
        Assert.IsTrue(await pending);

        var second = gate.AskAsync(DataLossRequest());
        gate.CancelCommand.Execute(null);
        Assert.IsFalse(await second);
    });

    [TestMethod]
    public void Gate_RefusesASecondRequestWhileOneIsOpen() => OnUi(async () =>
    {
        var gate = new ConfirmGate();
        var first = gate.AskAsync(DataLossRequest());

        // 两层确认框叠在一起,用户不可能说清自己在确认哪一个。
        Assert.IsFalse(await gate.AskAsync(DataLossRequest()));
        gate.CancelCommand.Execute(null);
        Assert.IsFalse(await first);
    });

    // ── 表单校验 ──────────────────────────────────────────────

    [TestMethod]
    public void RenameForm_RejectsIllegalNamesAndNoOpRenames()
    {
        var form = new RenameContainerForm("nginx-proxy", "web-stack");
        Assert.IsFalse(form.Validate(), "改成同一个名字不该放行。");

        SetText(form, "新名称", "-bad-start");
        Assert.IsFalse(form.Validate());

        SetText(form, "新名称", "nginx-proxy-2");
        Assert.IsTrue(form.Validate());
        Assert.Contains("web-stack", form.ComposeWarning);
    }

    [TestMethod]
    public void RunContainerForm_RejectsTheRmPlusRestartPolicyCombination()
    {
        var form = new RunContainerForm("nginx:1.27-alpine", "", ["bridge"]);
        SetToggle(form, "退出即删除  --rm", true);

        // Docker 自己会拒掉这个组合,但等它拒不如现在就说清楚。
        Assert.IsFalse(form.Validate());
        Assert.Contains("互斥", form.FormError!);
    }

    [TestMethod]
    public void RunContainerForm_ValidatesPortsAndMountTargets()
    {
        var form = new RunContainerForm("nginx:1.27-alpine", "", ["bridge"]);
        form.Ports.Rows.Add(new("99999", "80"));
        Assert.IsFalse(form.Validate());

        form.Ports.Rows.Clear();
        form.Volumes.Rows.Add(new("/srv/data", "relative/path"));
        Assert.IsFalse(form.Validate());

        form.Volumes.Rows.Clear();
        form.Ports.Rows.Add(new("8080", "80"));
        form.Volumes.Rows.Add(new("/srv/data", "/data"));
        Assert.IsTrue(form.Validate());
    }

    [TestMethod]
    public void RunContainerForm_BuildsACreateRequestThatMatchesTheForm()
    {
        var form = new RunContainerForm("nginx:1.27-alpine", "", ["web-stack_default"]);
        form.Ports.Rows.Add(new("8081", "80"));
        form.Volumes.Rows.Add(new("/srv/conf", "/etc/nginx/conf.d"));
        form.Env.Rows.Add(new("KEY", "value"));

        var request = form.ToRequest();

        Assert.AreEqual("nginx:1.27-alpine", request.Image);
        Assert.AreEqual("8081", request.HostConfig!.PortBindings!["80/tcp"][0].HostPort);
        Assert.AreSequenceEqual(["/srv/conf:/etc/nginx/conf.d"], [.. request.HostConfig.Binds!]);
        Assert.AreSequenceEqual(["KEY=value"], [.. request.Env!]);
        Assert.Contains("-p 8081:80", form.CommandNote);
    }

    [TestMethod]
    public void SplitArguments_RespectsQuotes()
    {
        Assert.AreSequenceEqual(["nginx", "-g", "daemon off;"], RunContainerForm.SplitArguments("nginx -g 'daemon off;'"));
    }

    [TestMethod]
    public void CreateNetworkForm_RequiresASubnetWhenAGatewayIsGiven()
    {
        var form = new CreateNetworkForm(swarmActive: false);
        SetText(form, "名称", "edge-dmz");
        SetText(form, "网关", "172.28.0.1");

        // 只给网关不给子网,Docker 不知道它属于哪一段。
        Assert.IsFalse(form.Validate());

        SetText(form, "子网", "172.28.0.0/16");
        Assert.IsTrue(form.Validate());
    }

    [TestMethod]
    public void CreateNetworkForm_DisablesOverlayWhenSwarmIsInactive()
    {
        var form = new CreateNetworkForm(swarmActive: false);
        var driver = form.Fields.OfType<ChoiceField>().Single(f => f.Label == "驱动");
        var overlay = driver.Options.Single(o => o.Value == "overlay");

        // 直接置灰而不是让用户去撞一条 daemon 的错误。
        Assert.IsFalse(overlay.Enabled);
        Assert.Contains("swarm", overlay.DisabledReason);
    }

    [TestMethod]
    public void ConnectNetworkForm_RefusesOneAliasForManyContainers()
    {
        var form = new ConnectNetworkForm("web-stack_default",
        [
            ("a", "one", "运行中", true, ""),
            ("b", "two", "运行中", true, "")
        ]);
        foreach (var item in form.Containers.Items)
        {
            item.Selected = true;
        }
        SetText(form, "网络别名", "db");

        // 同一个别名指向多个容器,DNS 解析结果就成了随机的。
        Assert.IsFalse(form.Validate());
    }

    [TestMethod]
    public void OpenComposeForm_DerivesTheProjectNameFromTheDirectory()
    {
        var form = new OpenComposeForm(isLocal: false);
        SetText(form, "compose 文件路径", "/srv/stacks/web-stack/compose.yaml");

        Assert.IsTrue(form.Validate());
        Assert.AreEqual("web-stack", form.ProjectName);
    }

    [TestMethod]
    public void OpenComposeForm_TakesWindowsPathsOnLocalEndpoints()
    {
        // 本机端点上 compose ls 报回来的就是这种路径 —— 拦掉它等于本机端点用不了这个入口。
        var form = new OpenComposeForm(isLocal: true);
        SetText(form, "compose 文件路径", @"D:\stacks\web-stack\compose.yaml");

        Assert.IsTrue(form.Validate());
        Assert.AreEqual("web-stack", form.ProjectName);
    }

    [TestMethod]
    public void OpenComposeForm_RejectsRelativePaths()
    {
        var form = new OpenComposeForm(isLocal: false);
        SetText(form, "compose 文件路径", "stacks/web/compose.yaml");

        // 相对路径会以登录目录为基准 —— 一个安静地打开错项目的 bug。
        Assert.IsFalse(form.Validate());
    }

    private static void SetText(PanelForm form, string label, string value) =>
        form.Fields.OfType<TextField>().Single(f => f.Label == label).Value = value;

    private static void SetToggle(PanelForm form, string label, bool value) =>
        form.Fields.OfType<ToggleField>().Single(f => f.Label == label).Value = value;

    public TestContext TestContext { get; set; }
}
