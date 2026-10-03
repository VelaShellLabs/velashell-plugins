using Avalonia.Controls;
using Avalonia.VisualTree;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;
using VelaShell.PluginSdk.Sessions;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 插件自己管的连接(与 Docker 面板同一个路数):对象树根上的已保存连接、设计稿 10 的新建 / 编辑对话框、
/// 设计稿 22 的连接中 / 连接失败占位标签、几条连接同时开着时各管各的标签,以及数据区右侧面板的收起。
/// </summary>
[TestClass]
public sealed class ConnectionManagementTests
{
    private static readonly Loc Zh = new("zh-CN");

    /// <summary>空工作台:不连任何东西,只有外壳与视图。</summary>
    private static async Task<(MongoWorkspaceViewModel Shell, MongoWorkspaceView View, Window Window)> OpenEmptyAsync(TestPluginContext context)
    {
        var shell = new MongoWorkspaceViewModel(Zh, context, new MongoStore(context));
        var view = new MongoWorkspaceView(shell);
        var window = new Window { Width = 1440, Height = 836, Content = view };
        window.Show();
        await shell.InitializeAsync();
        await Screens.PumpAsync(5);
        return (shell, view, window);
    }

    private static MongoProfile LocalProfile(string name, Dictionary<string, string>? settings = null)
    {
        var url = new MongoUrl(TestServer.Uri);
        var profile = new MongoProfile { Name = name, Host = url.Server.Host, Port = url.Server.Port };
        profile.Set(MongoSettings.KeyDirect, "true");
        foreach ((string key, string value) in settings ?? [])
        {
            profile.Set(key, value);
        }
        return profile;
    }

    // ── 外壳:空状态、已保存的连接、新建 ─────────────────────────────────────

    /// <summary>一条连接都没有:内容区给「新建连接」;工具栏最左是「连接」,按下去是设计稿 10 的对话框。</summary>
    [TestMethod]
    public void Empty_workbench_offers_a_new_connection() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext();
        (MongoWorkspaceViewModel shell, MongoWorkspaceView view, Window window) = await OpenEmptyAsync(context);
        try
        {
            Assert.IsTrue(shell.HasNoConnections);
            Assert.IsFalse(shell.HasSession, "工具栏右侧的只读开关与服务器徽章没有对象可讲");
            Button connect = view.GetVisualDescendants().OfType<Button>().Single(static b => b.Name == "ConnectButton");
            Assert.IsTrue(connect.IsEffectivelyVisible);
            Assert.IsTrue(view.GetVisualDescendants().OfType<Button>().Single(static b => b.Name == "EmptyNewConnection").IsEffectivelyVisible);
            _ = Screens.Capture(window, "00-empty-workbench");

            connect.Command!.Execute(null);
            await Screens.PumpAsync(5);

            var dialog = (ConnectionDialogViewModel)shell.Dialog!;
            Assert.IsTrue(dialog.IsNew);
            Assert.AreEqual("新建 MongoDB 连接", dialog.Title);
            Assert.AreEqual(1020d, dialog.Width);
            Assert.AreEqual("mongo", dialog.Name, "名字先给一个能用的,用户改不改都能存");
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>已保存的连接一打开就列在树根上(灰点,不自动连);分了组的挂在组名那一节下面。</summary>
    [TestMethod]
    public void Saved_connections_are_tree_roots_and_are_not_auto_connected() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext();
        var store = new MongoProfileStore(context);
        await store.SaveAsync(new MongoProfile { Name = "orders-prod", Host = "10.20.3.21", Group = "生产" }, "pw");
        await store.SaveAsync(new MongoProfile { Name = "local-dev", Host = "127.0.0.1" }, null);
        (MongoWorkspaceViewModel shell, _, Window window) = await OpenEmptyAsync(context);
        try
        {
            string[] rows = [.. shell.VisibleNodes.Select(static n => $"{n.Kind}:{n.Name}")];
            CollectionAssert.AreEqual(new[] { "Connection:local-dev", "Group:生产", "Connection:orders-prod" }, rows);
            Assert.IsTrue(shell.Connections.All(static c => c is { State: ConnectionState.Disconnected, Session: null }));
            Assert.IsTrue(shell.VisibleNodes.Where(static n => n.Kind == NodeKind.Connection).All(static n => n.DotClass == "off"));
            Assert.IsFalse(shell.HasTabs);

            TreeNode group = shell.VisibleNodes.Single(static n => n.Kind == NodeKind.Group);
            await shell.ToggleAsync(group);
            Assert.IsFalse(shell.VisibleNodes.Any(static n => n.Name == "orders-prod"), "收起分组藏起它的连接");
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>口令进宿主的加密密钥库,不进插件存储;删连接时一起删。</summary>
    [TestMethod]
    public async Task Profile_store_keeps_the_password_out_of_plugin_storage()
    {
        using var context = new TestPluginContext();
        var store = new MongoProfileStore(context);
        var profile = new MongoProfile { Name = "a", Host = "h", Username = "ops" };

        await store.SaveAsync(profile, "s3cret-pw");

        IReadOnlyList<MongoProfile> loaded = await store.LoadAsync();
        Assert.AreEqual("a", loaded.Single().Name);
        Assert.AreEqual("s3cret-pw", await store.GetPasswordAsync(profile.Id));
        Assert.IsFalse(context.FakeSecrets.Values.Keys.Any(static k => !k.StartsWith("password:", StringComparison.Ordinal)));
        foreach (string key in await context.Storage.GetKeysAsync())
        {
            System.Text.Json.JsonElement raw = await context.Storage.GetAsync<System.Text.Json.JsonElement>(key);
            Assert.IsFalse(raw.GetRawText().Contains("s3cret", StringComparison.Ordinal), "口令绝不能落进插件存储");
        }
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(loaded).Contains("s3cret", StringComparison.Ordinal));

        await store.SaveAsync(profile, null);
        Assert.AreEqual("s3cret-pw", await store.GetPasswordAsync(profile.Id), "null 表示不动已存的口令");

        await store.DeleteAsync(profile.Id);
        Assert.IsEmpty(await store.LoadAsync());
        Assert.AreEqual("", await store.GetPasswordAsync(profile.Id));
    }

    // ── 对话框(设计稿 10)─────────────────────────────────────────────────

    /// <summary>设计稿 10 那一条:副本集三台、ops_reader、默认库 shop、SCRAM-SHA-256、primaryPreferred;生产 → 默认只读 + 写前确认。</summary>
    [TestMethod]
    public void Dialog_maps_the_form_to_a_profile_and_previews_it() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext();
        SavedSessionInfo bastion = context.FakeSessions.AddSaved("bastion-ops", "10.0.0.5");
        (MongoWorkspaceViewModel shell, _, Window window) = await OpenEmptyAsync(context);
        try
        {
            shell.NewConnection();
            var dialog = (ConnectionDialogViewModel)shell.Dialog!;
            await Screens.PumpAsync(5);
            dialog.Name = "mongo-inner-01";
            dialog.IsProduction = true;
            dialog.Hosts[0].Host = "10.20.3.21";
            dialog.AddHostCommand.Execute(null);
            dialog.Hosts[1].Host = "10.20.3.22";
            dialog.AddHostCommand.Execute(null);
            dialog.Hosts[2].Host = "10.20.3.23";
            dialog.ReplicaSet = "rs0";
            dialog.Database = "shop";
            dialog.ReadPreference = "primaryPreferred";
            dialog.SelectedMechanism = dialog.MechanismChoices.Single(static c => c.Value == "SCRAM-SHA-256");
            dialog.Username = "ops_reader";
            dialog.Password = "pw";
            dialog.SshEnabled = true;
            await Screens.PumpAsync(5);

            Assert.AreEqual(
                "mongodb://ops_reader:****@10.20.3.21:27017,10.20.3.22:27017,10.20.3.23:27017/shop?replicaSet=rs0&authSource=admin&authMechanism=SCRAM-SHA-256&readPreference=primaryPreferred",
                dialog.PreviewText);
            Assert.IsTrue(dialog.PreviewSpans.Any(static s => s is { IsSecret: true, Text: ":****@" }));
            Assert.IsFalse(dialog.PreviewText.Contains("pw@", StringComparison.Ordinal), "口令不进连接串");
            Assert.IsTrue(dialog.ReadOnly && dialog.ConfirmWrites, "生产连接默认只读 + 写前确认");

            MongoProfile profile = dialog.ToProfile();
            MongoSettings settings = profile.Parsed;
            Assert.AreEqual("10.20.3.21", profile.Host);
            CollectionAssert.AreEqual(new[] { "10.20.3.22:27017", "10.20.3.23:27017" }, settings.AdditionalHosts.ToArray());
            Assert.AreEqual(MongoEnvironment.Production, settings.Environment);
            Assert.AreEqual(bastion.SavedSessionId, settings.JumpSessionId);
            Assert.IsTrue(settings.ReadOnly);
            Assert.IsFalse(profile.Settings.Values.Any(static v => v.Contains("pw", StringComparison.Ordinal)), "口令不进设置");
            _ = Screens.Capture(window, "10-connection-dialog");

            dialog.IsSrvTopology = true;
            Assert.IsFalse(dialog.CanUseSsh is false && dialog.SshEnabled is false, "开着的跳板开关仍然能关");
            Assert.AreEqual(Zh["Conn_SshNoSrv"], dialog.SshHint);
            Assert.AreEqual("", dialog.ToProfile().Parsed.JumpSessionId, "SRV 不走跳板");
            Assert.IsTrue(dialog.TlsEnabled, "SRV 默认 TLS");
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>名字必填、不能重名;主机必填。错误写在页脚,不弹框。</summary>
    [TestMethod]
    public void Dialog_validates_before_saving() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext();
        await new MongoProfileStore(context).SaveAsync(new MongoProfile { Name = "taken", Host = "h" }, null);
        (MongoWorkspaceViewModel shell, _, Window window) = await OpenEmptyAsync(context);
        try
        {
            shell.NewConnection();
            var dialog = (ConnectionDialogViewModel)shell.Dialog!;
            dialog.Name = "";
            await dialog.SaveCommand.ExecuteAsync();
            Assert.AreEqual(Zh["Conn_NeedName"], dialog.Error);

            dialog.Name = "TAKEN";
            await dialog.SaveCommand.ExecuteAsync();
            StringAssert.Contains(dialog.Error, "TAKEN");

            dialog.Name = "fresh";
            await dialog.SaveCommand.ExecuteAsync();
            Assert.AreEqual(Zh["Conn_NeedHost"], dialog.Error);
            Assert.AreSame(dialog, shell.Dialog, "没存成就不关");
            Assert.HasCount(1, shell.Connections);
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>测试连接:逐步报进度,成员表里有本机这台;改了参数结果就作废。</summary>
    [TestMethod]
    public void Dialog_test_walks_the_steps_and_clears_on_change() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        using var context = new TestPluginContext();
        (MongoWorkspaceViewModel shell, _, Window window) = await OpenEmptyAsync(context);
        try
        {
            var dialog = new ConnectionDialogViewModel(shell, LocalProfile("local"), isNew: true);
            shell.ShowDialog(dialog);
            await dialog.TestCommand.ExecuteAsync();
            await Screens.PumpAsync(5);

            Assert.IsTrue(dialog.TestPassed, string.Join("; ", dialog.Steps.Select(static s => $"{s.Key}={s.State}:{s.Detail}")));
            CollectionAssert.AreEqual(new[] { "tcp", "auth", "hello", "privileges" }, dialog.Steps.Select(static s => s.Key).ToArray());
            Assert.IsTrue(dialog.HasMembers);
            StringAssert.StartsWith(dialog.TestSummary, "连接成功");
            _ = Screens.Capture(window, "10-connection-dialog-tested");

            dialog.Database = "other";
            Assert.IsEmpty(dialog.Steps, "参数一改,上一次的测试结果不再代表它");
            Assert.IsFalse(dialog.TestPassed);
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    /// <summary>「保存并连接」:存进存储、挂到树上、连上、开默认库的对象列表;口令进密钥库。</summary>
    [TestMethod]
    public void Save_and_connect_attaches_a_session() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        using var context = new TestPluginContext();
        (MongoWorkspaceViewModel shell, _, Window window) = await OpenEmptyAsync(context);
        try
        {
            var dialog = new ConnectionDialogViewModel(shell, LocalProfile("local", new() { [MongoSettings.KeyDatabase] = "shop" }), isNew: true);
            shell.ShowDialog(dialog);
            dialog.Username = "";
            await dialog.SaveAndConnectCommand.ExecuteAsync();
            await Screens.PumpAsync(20);

            Assert.IsNull(shell.Dialog);
            ConnectionEntry entry = shell.Connections.Single();
            Assert.AreEqual(ConnectionState.Connected, entry.State);
            Assert.IsNotNull(entry.Session);
            Assert.AreSame(entry.Session, shell.CurrentSession);
            Assert.AreEqual("ok", entry.Root.DotClass);
            _ = Assert.IsInstanceOfType<ObjectsTabViewModel>(shell.ActiveTab);
            Assert.AreEqual("local", shell.StatusConnection);
            Assert.AreEqual("local", (await new MongoProfileStore(context).LoadAsync()).Single().Name);
            Assert.IsNotNull((await new MongoProfileStore(context).LoadAsync()).Single().LastConnectedAt, "记下最近连过,下次排在上面");
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    // ── 连接中 / 连接失败(设计稿 22)──────────────────────────────────────

    /// <summary>连不上:原地变成「无法连接」卡片(不弹模态框),给编辑连接 / 关闭标签页 / 重新连接;树上红点。</summary>
    [TestMethod]
    public void Failed_connection_shows_the_failed_card() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext();
        var profile = new MongoProfile { Name = "mongo-inner-01", Host = "127.0.0.1", Port = 1 };
        profile.Set(MongoSettings.KeyConnectTimeout, "800");
        profile.Set(MongoSettings.KeyDirect, "true");
        await new MongoProfileStore(context).SaveAsync(profile, null);
        (MongoWorkspaceViewModel shell, MongoWorkspaceView view, Window window) = await OpenEmptyAsync(context);
        try
        {
            ConnectionEntry entry = shell.Connections.Single();
            await shell.ConnectAsync(entry);
            await Screens.PumpAsync(10);

            Assert.AreEqual(ConnectionState.Failed, entry.State);
            Assert.AreEqual("err", entry.Root.DotClass);
            var card = (ConnectionStateTabViewModel)shell.ActiveTab!;
            Assert.IsTrue(card.IsFailed);
            Assert.AreEqual("无法连接 mongo-inner-01", card.FailedTitle);
            StringAssert.Contains(card.ErrorMessage, "127.0.0.1:1");
            Assert.IsTrue(view.GetVisualDescendants().OfType<TextBlock>().Any(static t => t.Text == "无法连接 mongo-inner-01" && t.IsEffectivelyVisible));
            _ = Screens.Capture(window, "22-connection-failed");

            card.EditCommand.Execute(null);
            _ = Assert.IsInstanceOfType<ConnectionDialogViewModel>(shell.Dialog);
            shell.CloseDialog(shell.Dialog!);

            await card.CloseCommand.ExecuteAsync();
            Assert.IsFalse(shell.HasTabs);
            Assert.AreEqual(ConnectionState.Failed, entry.State, "关掉卡片不改连接的状态,树上照旧是红点");
        }
        finally
        {
            window.Close();
            shell.Dispose();
        }
    });

    // ── 几条连接同时开着 ──────────────────────────────────────────────────

    /// <summary>两条连接各开同一个集合:两个标签,不串;当前连接跟着活动标签走;断开一条只关它自己的标签。</summary>
    [TestMethod]
    public void Two_connections_keep_their_tabs_apart() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        MongoWorkspaceViewModel shell = bench.ViewModel;
        MongoSession first = bench.Session;
        MongoConnection secondConnection = await TestServer.OpenAsync(new Dictionary<string, string> { ["database"] = Screens.Database });
        MongoSession second = await shell.AttachAsync(new MongoProfile { Name = "mongo-copy", Host = secondConnection.Endpoint }, secondConnection);
        await Screens.PumpAsync();

        Assert.AreEqual(2, shell.VisibleNodes.Count(static n => n.Kind == NodeKind.Connection));
        first.OpenCollection(Screens.Database, "orders");
        WorkspaceTab firstTab = shell.ActiveTab!;
        second.OpenCollection(Screens.Database, "orders");
        WorkspaceTab secondTab = shell.ActiveTab!;
        Assert.AreNotSame(firstTab, secondTab, "同名集合在两条连接里是两个标签");
        Assert.AreSame(second, shell.CurrentSession);
        Assert.AreEqual("mongo-copy", shell.StatusConnection);

        shell.ActiveTab = firstTab;
        Assert.AreSame(first, shell.CurrentSession);
        Assert.AreEqual("mongo-inner-01", shell.StatusConnection);

        Assert.IsTrue(await shell.DisconnectAsync(second.Entry));
        Assert.IsFalse(shell.Tabs.Any(t => ReferenceEquals(t.Owner, second)), "断开只关它自己的标签");
        Assert.IsTrue(shell.Tabs.Contains(firstTab));
        Assert.AreEqual(ConnectionState.Disconnected, second.Entry.State);
        Assert.IsFalse(shell.VisibleNodes.Any(n => n.Owner == second.Entry && n.Kind == NodeKind.Database), "断开后它下面的库收起来了");
    });

    // ── 数据区右侧面板的收起(Navicat 右下角那颗按钮)──────────────────────

    /// <summary>底栏右下角的按钮收起 / 展开右侧的文档检查器;收起时那一列宽度归零,展开回到原来的宽度。</summary>
    [TestMethod]
    public void Bottom_right_button_collapses_the_side_panel() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenCollection(Screens.Database, "orders");
        await Screens.PumpAsync();
        var tab = (CollectionTabViewModel)bench.ViewModel.ActiveTab!;
        GridPaneView pane = bench.View.GetVisualDescendants().OfType<GridPaneView>().First(static p => p.IsEffectivelyVisible);
        Grid split = pane.GetVisualDescendants().OfType<Grid>().First(g => SidePanel.GetCollapsed(g) == !tab.IsSidePanelVisible && g.ColumnDefinitions.Count == 3);
        DocInspectorView inspector = pane.GetVisualDescendants().OfType<DocInspectorView>().Single();
        Assert.IsTrue(inspector.IsEffectivelyVisible);
        double before = split.ColumnDefinitions[2].ActualWidth;
        Assert.IsGreaterThan(0d, before);

        Button toggle = bench.View.GetVisualDescendants().OfType<Button>().Single(static b => b.Name == "SidePanelToggle" && b.IsEffectivelyVisible);
        toggle.Command!.Execute(null);
        await Screens.PumpAsync(5);
        Assert.IsFalse(tab.IsSidePanelVisible);
        Assert.AreEqual(0d, split.ColumnDefinitions[2].ActualWidth);
        Assert.IsFalse(inspector.IsEffectivelyVisible);
        _ = Screens.Capture(bench.Window, "01-collection-side-panel-hidden");

        toggle.Command.Execute(null);
        await Screens.PumpAsync(5);
        Assert.IsTrue(inspector.IsEffectivelyVisible);
        Assert.AreEqual(before, split.ColumnDefinitions[2].ActualWidth, 1);
    });
}
