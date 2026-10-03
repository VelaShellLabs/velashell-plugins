using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 插件激活、连接设置与异常翻译:这些决定的是"连接对话框长什么样、连不上时宿主怎么反应",
/// 本身就是产品的一部分,所以按 SDK 的测试替身逐项验。
/// </summary>
[TestClass]
public sealed class PluginTests
{
    private static TestPluginContext NewContext(string locale = "zh-Hans")
    {
        var context = new TestPluginContext { PluginId = "velashell.mongo" };
        context.HostInfo.Locale = locale;
        return context;
    }

    private static WorkspaceConnectRequest Request(Dictionary<string, string>? settings = null, string host = "10.20.3.21", int port = 27017,
        string user = "", string password = "", WorkspaceTunnelInfo? tunnel = null) =>
        new()
        {
            SessionId = "s",
            Host = host,
            Port = port,
            Username = user,
            Password = password,
            Tunnel = tunnel,
            Settings = settings ?? new Dictionary<string, string>(StringComparer.Ordinal)
        };

    /// <summary>
    /// 与 Docker 面板同一个路数:激活时注册命令面板里的一条命令并当场开文档面板;
    /// 不再注册工作台连接类型 —— 宿主的新建连接窗口与会话树里没有 MongoDB。
    /// </summary>
    [TestMethod]
    public async Task Activate_RegistersTheCommandAndOpensTheWorkbench()
    {
        using TestPluginContext context = NewContext();
        await new MongoPlugin().ActivateAsync(context, CancellationToken.None);

        PluginCommandDescriptor command = context.RecordingCommands.Registered.Single();
        Assert.AreEqual("velashell.mongo.open", command.Id);
        StringAssert.StartsWith(command.Title, "MongoDB");
        Assert.IsEmpty(context.RecordingWorkspaces.Registered, "连接由插件自己管,不进宿主的连接列表");
        FakePanel panel = context.FakeUi.LastPanel;
        Assert.AreEqual("MongoDB", panel.Options.Title);
        Assert.AreEqual(PanelDisplayMode.Document, panel.Options.DisplayMode);
    }

    /// <summary>面板已经开着时再按一次命令:把它带到眼前,不开第二个。</summary>
    [TestMethod]
    public async Task OpenCommand_ReactivatesTheOpenPanel()
    {
        using TestPluginContext context = NewContext();
        await new MongoPlugin().ActivateAsync(context, CancellationToken.None);

        await context.RecordingCommands.RunAsync("velashell.mongo.open");

        Assert.HasCount(1, context.FakeUi.Panels);
        Assert.AreEqual(1, context.FakeUi.LastPanel.ActivateCount);
    }

    /// <summary>面板关了再按命令:开一个新的。</summary>
    [TestMethod]
    public async Task OpenCommand_AfterClosingOpensANewPanel()
    {
        using TestPluginContext context = NewContext();
        await new MongoPlugin().ActivateAsync(context, CancellationToken.None);
        await context.FakeUi.LastPanel.CloseAsync();

        await context.RecordingCommands.RunAsync("velashell.mongo.open");

        Assert.HasCount(2, context.FakeUi.Panels);
        Assert.IsTrue(context.FakeUi.LastPanel.IsOpen);
    }

    /// <summary>面板标签上的图标是描边的叶子(lucide leaf)。</summary>
    [TestMethod]
    public async Task Activate_PanelIconIsTheStrokedLeaf()
    {
        using TestPluginContext context = NewContext();
        await new MongoPlugin().ActivateAsync(context, CancellationToken.None);

        PluginIcon? icon = context.FakeUi.LastPanel.Options.Icon;
        Assert.IsNotNull(icon);
        Assert.IsFalse(icon.IsFilled, "lucide 是描边字形");
        Assert.AreEqual(24d, icon.ViewBoxSize);
        Assert.AreEqual(MongoIcon.PathData, icon.PathData);
    }

    /// <summary>停用:命令撤掉、面板关掉。</summary>
    [TestMethod]
    public async Task Deactivate_RemovesTheCommandAndClosesThePanel()
    {
        using TestPluginContext context = NewContext();
        var plugin = new MongoPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);

        await plugin.DeactivateAsync(CancellationToken.None);

        Assert.IsEmpty(context.RecordingCommands.Registered);
        Assert.IsFalse(context.FakeUi.LastPanel.IsOpen);
    }

    /// <summary>清单:命令贡献 + 按命令惰性激活,不贡献工作台连接类型;用到 2.0.2 的会话面要钉 minSdkVersion。</summary>
    [TestMethod]
    public void Manifest_ContributesTheCommandNotAWorkspace()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins", "VelaShell.Plugin.Mongo", "plugin.json");
        string json = File.ReadAllText(path);
        StringAssert.Contains(json, "\"onCommand:velashell.mongo.open\"");
        StringAssert.Contains(json, "\"commands\"");
        StringAssert.Contains(json, "\"minSdkVersion\": \"2.0.2\"");
        Assert.IsFalse(json.Contains("\"workspaces\"", StringComparison.Ordinal), "不再出现在宿主的新建连接窗口里");
        Assert.IsFalse(json.Contains("onWorkspace:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Settings_ProductionDefaultsToReadOnlyAndConfirm()
    {
        MongoSettings prod = MongoSettings.From(Request(new() { ["environment"] = "production" }));
        Assert.IsTrue(prod.ReadOnly);
        Assert.IsTrue(prod.ConfirmWrites);

        MongoSettings dev = MongoSettings.From(Request());
        Assert.IsFalse(dev.ReadOnly);
        Assert.IsFalse(dev.ConfirmWrites);
        Assert.IsTrue(dev.DisableDropDatabase);
    }

    [TestMethod]
    public void Settings_ExplicitValuesWinOverTheEnvironmentDefault()
    {
        MongoSettings prod = MongoSettings.From(Request(new() { ["environment"] = "production", ["readOnly"] = "false" }));
        Assert.IsFalse(prod.ReadOnly, "用户显式关掉了就听用户的");
        Assert.IsTrue(prod.ConfirmWrites);
    }

    [TestMethod]
    public void Settings_ClampsAndSplits()
    {
        MongoSettings settings = MongoSettings.From(Request(new()
        {
            ["hosts"] = "10.20.3.22:27017, 10.20.3.23:27018;10.20.3.24",
            ["pageSize"] = "100000",
            ["sampleSize"] = "1",
            ["ejsonMode"] = "canonical"
        }));
        CollectionAssert.AreEqual(new[] { "10.20.3.22:27017", "10.20.3.23:27018", "10.20.3.24" }, settings.AdditionalHosts.ToArray());
        Assert.AreEqual(1000, settings.PageSize);
        Assert.AreEqual(50, settings.SampleSize);
        Assert.AreEqual(EjsonMode.Canonical, settings.Ejson);
    }

    [TestMethod]
    public void BuildSettings_HostListWithReplicaSet()
    {
        WorkspaceConnectRequest request = Request(new() { ["hosts"] = "10.20.3.22:27017", ["replicaSet"] = "rs0" }, user: "ops", password: "pw");
        MongoClientSettings client = MongoConnection.BuildSettings(request, MongoSettings.From(request), null);

        Assert.AreEqual(2, client.Servers.Count());
        Assert.AreEqual("rs0", client.ReplicaSetName);
        Assert.IsFalse(client.DirectConnection);
        Assert.AreEqual("ops", client.Credential.Username);
        Assert.AreEqual("admin", client.Credential.Source);
    }

    [TestMethod]
    public void BuildSettings_TunnelForcesDirectConnectionToTheLocalEndpoint()
    {
        WorkspaceConnectRequest request = Request(new() { ["hosts"] = "10.20.3.22:27017", ["replicaSet"] = "rs0" },
            host: "127.0.0.1", port: 51872, tunnel: new("10.20.3.21", 27017, "bastion-ops"));
        MongoClientSettings client = MongoConnection.BuildSettings(request, MongoSettings.From(request), null);

        Assert.AreEqual(1, client.Servers.Count(), "其余成员的地址经隧道够不着");
        Assert.AreEqual(51872, client.Servers.Single().Port);
        Assert.IsTrue(client.DirectConnection);
        Assert.IsNull(client.ReplicaSetName);
    }

    [TestMethod]
    public void BuildSettings_UriModeTakesCredentialsFromTheForm()
    {
        WorkspaceConnectRequest request = Request(new() { ["topology"] = "uri" },
            host: "mongodb://10.0.0.1:27017,10.0.0.2:27017/shop?replicaSet=rs0", user: "ops", password: "secret");
        MongoClientSettings client = MongoConnection.BuildSettings(request, MongoSettings.From(request), null);

        Assert.AreEqual("rs0", client.ReplicaSetName);
        Assert.AreEqual("ops", client.Credential.Username);
        Assert.AreEqual(2, client.Servers.Count());
    }

    [TestMethod]
    public void BuildSettings_MechanismsMapToCredentials()
    {
        WorkspaceConnectRequest scram = Request(new() { ["authMechanism"] = "SCRAM-SHA-1", ["authSource"] = "shop" }, user: "u", password: "p");
        Assert.AreEqual("SCRAM-SHA-1", MongoConnection.BuildSettings(scram, MongoSettings.From(scram), null).Credential.Mechanism);

        WorkspaceConnectRequest x509 = Request(new() { ["authMechanism"] = "MONGODB-X509", ["tls"] = "true" });
        MongoClientSettings x509Client = MongoConnection.BuildSettings(x509, MongoSettings.From(x509), new TlsTrust(""));
        Assert.AreEqual("MONGODB-X509", x509Client.Credential.Mechanism);
        Assert.IsTrue(x509Client.UseTls);

        WorkspaceConnectRequest anonymous = Request();
        Assert.IsNull(MongoConnection.BuildSettings(anonymous, MongoSettings.From(anonymous), null).Credential);
    }

    [TestMethod]
    public void RedactUri_HidesThePassword()
    {
        string redacted = MongoConnection.RedactUri("mongodb://ops:secret@10.0.0.1:27017/shop");
        Assert.IsFalse(redacted.Contains("secret", StringComparison.Ordinal));
        Assert.IsTrue(redacted.Contains("ops", StringComparison.Ordinal));
    }

    /// <summary>选服超时背后的真正原因是认证失败:失败卡片要说「认证失败」,给「编辑连接」去改口令。</summary>
    [TestMethod]
    public void Translate_AuthenticationFailureBehindATimeout()
    {
        using TestPluginContext context = NewContext();
        var connector = new MongoConnector(context, new Loc("zh-Hans"));
        WorkspaceConnectRequest request = Request();
        var auth = new MongoAuthenticationException(new MongoDB.Driver.Core.Connections.ConnectionId(
            new MongoDB.Driver.Core.Servers.ServerId(new MongoDB.Driver.Core.Clusters.ClusterId(), new System.Net.DnsEndPoint("h", 1))), "bad password");
        var failure = new MongoConnectFailedException(new TimeoutException("A timeout occurred after 10000ms selecting a server."), [auth], null);

        MongoConnectException translated = connector.Translate(failure, request, MongoSettings.From(request));

        Assert.AreEqual(ConnectFailureKind.Authentication, translated.Kind);
        StringAssert.StartsWith(translated.Message, "认证失败");
    }

    /// <summary>单纯的超时是连不上;驱动附在后面的整段集群描述剪掉。</summary>
    [TestMethod]
    public void Translate_PlainTimeoutIsANetworkFailure()
    {
        using TestPluginContext context = NewContext();
        var connector = new MongoConnector(context, new Loc("en"));
        WorkspaceConnectRequest request = Request();

        MongoConnectException translated = connector.Translate(
            new MongoConnectFailedException(new TimeoutException("A timeout occurred. Client view of cluster state is { ClusterId : 1 }"), [], null),
            request, MongoSettings.From(request));

        Assert.AreEqual(ConnectFailureKind.Network, translated.Kind);
        Assert.IsFalse(translated.Message.Contains("ClusterId", StringComparison.Ordinal), "集群描述是给驱动开发者看的,剪掉");
    }

    /// <summary>按已保存的连接连上本机 mongod。</summary>
    [TestMethod]
    public async Task Connector_ConnectsARealServer() => await RunAgainstServerAsync(async () =>
    {
        using TestPluginContext context = NewContext();
        var connector = new MongoConnector(context, new Loc("zh-Hans"));
        var url = new MongoUrl(TestServer.Uri);
        var profile = new MongoProfile { Name = "local", Host = url.Server.Host, Port = url.Server.Port };
        profile.Set(MongoSettings.KeyDirect, "true");

        MongoLink link = await connector.ConnectAsync(profile, "", CancellationToken.None);
        await using (link)
        {
            Assert.IsNull(link.Tunnel);
            Assert.IsFalse(string.IsNullOrEmpty(link.Connection.Server.Version));
        }
    });

    /// <summary>端口不通:抛 <see cref="MongoConnectException" />(连不上),不是驱动的原生异常。</summary>
    [TestMethod]
    public async Task Connector_UnreachablePortIsANetworkFailure() => await RunAgainstServerAsync(async () =>
    {
        using TestPluginContext context = NewContext();
        var connector = new MongoConnector(context, new Loc("en"));
        var profile = new MongoProfile { Name = "nowhere", Host = "127.0.0.1", Port = 1 };
        profile.Set(MongoSettings.KeyConnectTimeout, "800");
        profile.Set(MongoSettings.KeyDirect, "true");

        MongoConnectException ex = await Assert.ThrowsExactlyAsync<MongoConnectException>(() =>
            connector.ConnectAsync(profile, "", CancellationToken.None));
        Assert.AreEqual(ConnectFailureKind.Network, ex.Kind);
    });

    /// <summary>
    /// SSH 跳板:经宿主打开那条已保存的 SSH 会话(理由原样给用户看),在本机回环上起转发,
    /// 每条驱动连接都经跳板开一条 TCP 流(这里的替身把流直接接到本机 mongod 上);断开时关掉插件开的那条会话。
    /// </summary>
    [TestMethod]
    public async Task Connector_TunnelsThroughASavedSshSession() => await RunAgainstServerAsync(async () =>
    {
        using TestPluginContext context = NewContext();
        var url = new MongoUrl(TestServer.Uri);
        SavedSessionInfo bastion = context.FakeSessions.AddSaved("bastion-ops", "10.0.0.5");
        context.FakeRemoteTunnel.Handler = (_, endpoint) =>
        {
            Assert.AreEqual($"tcp:10.20.3.21:27017", endpoint, "跳板机看到的是目标的真实地址");
            var client = new System.Net.Sockets.TcpClient();
            client.Connect(url.Server.Host, url.Server.Port);
            return client.GetStream();
        };
        var connector = new MongoConnector(context, new Loc("zh-Hans"));
        var profile = new MongoProfile { Name = "via-bastion", Host = "10.20.3.21", Port = 27017 };
        profile.Set(MongoSettings.KeyJumpSession, bastion.SavedSessionId);

        MongoLink link = await connector.ConnectAsync(profile, "", CancellationToken.None);
        string sessionId;
        await using (link)
        {
            Assert.IsNotNull(link.Tunnel);
            Assert.AreEqual("bastion-ops", link.Tunnel.JumpName);
            StringAssert.Contains(context.FakeSessions.LastOpenReason, "via-bastion");
            Assert.IsFalse(string.IsNullOrEmpty(link.Connection.Server.Version));
            sessionId = context.FakeSessions.OpenedByPlugin.Single();
            Assert.IsTrue(context.FakeRemoteTunnel.Opened.Count > 0);
        }
        Assert.IsFalse(context.FakeSessions.Sessions.Any(s => s.SessionId == sessionId), "插件开的跳板会话随连接一起关掉");
    });

    /// <summary>用户在宿主的确认框里点了「不」:连接失败,种类是跳板。</summary>
    [TestMethod]
    public async Task Connector_RefusedJumpSessionIsATunnelFailure()
    {
        using TestPluginContext context = NewContext();
        SavedSessionInfo bastion = context.FakeSessions.AddSaved("bastion-ops", "10.0.0.5");
        context.FakeSessions.DenyOpen = true;
        var connector = new MongoConnector(context, new Loc("zh-Hans"));
        var profile = new MongoProfile { Name = "via-bastion", Host = "10.20.3.21", Port = 27017 };
        profile.Set(MongoSettings.KeyJumpSession, bastion.SavedSessionId);

        MongoConnectException ex = await Assert.ThrowsExactlyAsync<MongoConnectException>(() =>
            connector.ConnectAsync(profile, "", CancellationToken.None));
        Assert.AreEqual(ConnectFailureKind.Tunnel, ex.Kind);
        StringAssert.StartsWith(ex.Message, "SSH 跳板没建起来");
    }

    private static async Task RunAgainstServerAsync(Func<Task> body)
    {
        await TestServer.RequireAsync();
        await body();
    }
}
