using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Logging;
using Avalonia.Threading;
using StackExchange.Redis;
using VelaShell.Plugin.Redis.Ui;
using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Workspaces;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 面板的 headless 装载与交互:AXAML 真装载一次(样式选择器、模板、
/// <c>Loc[...]</c> 索引器绑定这些编译期看不出的问题在此暴露),并验证
/// "扫描 → 键树 → 选中 → 详情"这条主链路真的接上了。
/// <para>
/// 需要本机有 <c>127.0.0.1:6379</c>;没有则报 Inconclusive 跳过(与集成测试同一口径)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RedisPanelUiTests
{
    private const string Host = "127.0.0.1";
    private const int Port = 6379;
    private const int Database = 9;

    private static HeadlessUnitTestSession _session = null!;
    private static string _prefix = "";
    private static bool _serverAvailable;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RedisPanelUiTests).Assembly);
        _prefix = $"velashell-ui-{Guid.NewGuid():N}";
        try
        {
            using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
                new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
            IDatabase db = mux.GetDatabase(Database);
            await db.StringSetAsync($"{_prefix}:user:1:name", "张三");
            await db.HashSetAsync($"{_prefix}:user:1:profile", [new HashEntry("name", "张三")]);
            // 一批"只有末段不同"的键:这正是键列表要折起来的那种噪音(默认阈值 8)。
            for (int i = 0; i < 10; i++)
            {
                await db.StringSetAsync($"{_prefix}:order:2026:{i:0000}", "paid", TimeSpan.FromMinutes(30));
            }
            await mux.CloseAsync();
            _serverAvailable = true;
        }
        catch (Exception)
        {
            _serverAvailable = false;
        }
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (!_serverAvailable)
        {
            return;
        }
        using ConnectionMultiplexer mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions { EndPoints = { { Host, Port } }, AllowAdmin = true, AbortOnConnectFail = true });
        IDatabase db = mux.GetDatabase(Database);
        IServer server = mux.GetServer(Host, Port);
        // 只删自己造的键(连清理脚本也不用 KEYS)。
        await foreach (RedisKey key in server.KeysAsync(Database, $"{_prefix}*", pageSize: 100))
        {
            await db.KeyDeleteAsync(key);
        }
        await mux.CloseAsync();
    }

    private static RedisSettings Settings(string deployment = "standalone") =>
        RedisSettings.From(new WorkspaceConnectRequest
        {
            SessionId = "ui",
            Host = Host,
            Port = Port,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database"] = Database.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["environment"] = "production",
                // 声明的键名是 mode(见 RedisSettings.KeyMode),不是 deployment。
                ["mode"] = deployment
            }
        });

    /// <summary>
    /// 在 headless UI 线程上跑一段**异步**测试体。lambda 必须带返回值 ——
    /// <see cref="HeadlessUnitTestSession" /> 没有 <c>Func&lt;Task&gt;</c> 重载,
    /// 写成无返回值会拿到一个从未被等待的 <c>Task&lt;Task&gt;</c>:测试体跑到第一个 await 就
    /// "通过"了,后面的断言失败全部丢失(仓库 README 与 AI 插件测试都记过这一条)。
    /// </summary>
    private static void OnUi(Func<Task> body) =>
        _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static async Task PumpAsync(int rounds = 60)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(5);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<(Window Window, RedisWorkspaceView View, RedisWorkspaceViewModel ViewModel, RedisConnection Connection)>
        ShowAsync(string filter = "", string deployment = "standalone")
    {
        RedisConnection connection = await RedisConnection.ConnectAsync(Host, Port, "", "", Settings(deployment));
        using var context = new TestPluginContext();
        var viewModel = new RedisWorkspaceViewModel(
            connection, "prod-cache", $"{Host}:{Port}", new Loc("zh-Hans"), new PluginLoggerFacade(context.Log))
        {
            Filter = filter
        };
        var view = new RedisWorkspaceView(viewModel);
        var window = new Window { Width = 1200, Height = 700, Content = view };
        window.Show();
        await PumpAsync(10);
        await viewModel.InitializeAsync();
        await PumpAsync();
        return (window, view, viewModel, connection);
    }

    /// <summary>
    /// 键列表在真机上的样子:噪音折起来、其余平铺、每行带齐类型/TTL/规模,点一下就地展开。
    /// <para>
    /// 这是左栏从树改成列表的验收点。原先这一屏是一棵树:看一个键要点三层,
    /// 行上只有本层片段,TTL 与规模压根没地方放。
    /// </para>
    /// </summary>
    [TestMethod]
    public void KeyList_FoldsTheNoisyPrefix_ShowsMetadata_AndExpandsInPlace()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync(_prefix);
            try
            {
                Assert.IsTrue(viewModel.IsScanComplete);
                Assert.AreEqual(12, viewModel.MatchedCount);

                // 10 个订单折成一行;2 个 user 键低于阈值,照旧平铺。
                CollectionAssert.AreEqual(
                    new[] { $"{_prefix}:order:2026:*", $"{_prefix}:user:1:name", $"{_prefix}:user:1:profile" },
                    viewModel.Rows.Select(row => row.Display).ToArray());

                RedisKeyRow group = viewModel.Rows.Single(row => row.IsGroup);
                Assert.AreEqual(10, group.Count);
                Assert.IsTrue(group.IsCollapsed, "折叠态应显示向右的箭头。");
                Assert.IsFalse(group.IsExpandedGroup);

                // 每行带齐元数据 —— 缩进省下来的宽度就是给这两列的。
                RedisKeyRow hash = Row(viewModel, $"{_prefix}:user:1:profile");
                Assert.AreEqual("hash", hash.TypeName);
                Assert.AreEqual("—", hash.TtlText, "没有过期时间的键给一个破折号,不是空白。");
                Assert.AreEqual("1 项", hash.SizeText);
                Assert.AreEqual("6 字节", Row(viewModel, $"{_prefix}:user:1:name").SizeText);

                // 点一下就地展开:成员缩进一级铺在原位,不是跳进另一层视图。
                viewModel.ToggleGroup(group);
                await PumpAsync(10);

                Assert.IsTrue(group.IsExpandedGroup, "展开态应显示向下的箭头。");
                Assert.IsFalse(group.IsCollapsed);
                Assert.HasCount(13, viewModel.Rows, "10 个成员就地铺开。");
                RedisKeyRow member = Row(viewModel, $"{_prefix}:order:2026:0000");
                Assert.AreEqual(1, member.Depth);
                Assert.AreEqual("string", member.TypeName);
                // 种下去是 30 分钟,读到的是 29:5x —— 断言前缀,别跟秒数较劲。
                Assert.StartsWith("29:", member.TtlText, "带 TTL 的键要显示倒计时。");
                Assert.IsFalse(member.IsExpiringSoon, "还有半小时,不该标成快过期。");

                // 再点一下收起来 —— 走 Tapped 而不是 SelectionChanged 才有的第二下。
                viewModel.ToggleGroup(group);
                await PumpAsync(10);
                Assert.HasCount(3, viewModel.Rows);

                Assert.IsNotNull(view.GetControl<ListBox>("KeyList"));
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>按**完整键名**取列表行 —— 列表世界里"找一个键"就这么直接。</summary>
    private static RedisKeyRow Row(RedisWorkspaceViewModel viewModel, string display) =>
        viewModel.Rows.FirstOrDefault(row => row.Key?.Display == display)
        ?? throw new AssertFailedException(
            $"列表里没有 {display};当前行:{string.Join(" | ", viewModel.Rows.Select(row => row.Display))}");

    private static void RequireServer()
    {
        if (!_serverAvailable)
        {
            Assert.Inconclusive($"没有可用的 Redis({Host}:{Port}),跳过面板 UI 测试。");
        }
    }

    /// <summary>
    /// 装载、选中、清空选中,全程不许有一条绑定报错。
    /// <para>
    /// 这一条守的是"日志要干净"。Avalonia 的绑定**不因父级不可见而停止求值**:详情头整块由
    /// <c>HasSelection</c> 控制显隐,可只要 AXAML 里写的是 <c>{Binding Selected.Key.Display}</c>,
    /// 没选中键时它就会在 <c>Selected</c> 这一环上断掉,每次清空选中都往日志里灌一条
    /// "Value is null"。那不是崩溃,所以没人会为它开单子;它只是让日志变得没法读 ——
    /// <b>而一份没法读的日志,等于没有日志</b>。摊平成视图模型属性即可,这里把闸门焊死。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Panel_LogsNoBindingErrors_WhenSelectionComesAndGoes()
    {
        RequireServer();
        var sink = new BindingLogSink();
        ILogSink? previous = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            OnUi(async () =>
            {
                // 装载时就没有选中:详情头此刻整块不可见,绑定却照样在求值。
                (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                    await ShowAsync(_prefix);
                try
                {
                    Assert.IsFalse(viewModel.HasSelection);
                    Assert.IsEmpty(viewModel.SelectedKeyText);
                    Assert.IsEmpty(viewModel.SelectedTypeText);

                    viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:profile");
                    await PumpAsync();

                    Assert.AreEqual($"{_prefix}:user:1:profile", viewModel.SelectedKeyText);
                    Assert.AreEqual("hash", viewModel.SelectedTypeText);

                    // 再清空:回到"没选中"才是原先那条 Value is null 的现场。
                    viewModel.SelectedRow = null;
                    await PumpAsync();

                    Assert.IsFalse(viewModel.HasSelection);
                    Assert.IsEmpty(viewModel.SelectedKeyText);
                    Assert.IsEmpty(viewModel.SelectedTypeText);
                }
                finally
                {
                    window.Close();
                    await connection.DisposeAsync();
                }
            });
        }
        finally
        {
            Logger.Sink = previous;
        }

        Assert.IsEmpty(sink.Errors, $"面板不该有绑定报错,实际:{string.Join(" | ", sink.Errors)}");
    }

    /// <summary>
    /// 自动刷新绝不吃掉没保存的编辑 —— 字符串草稿这一路。
    /// <para>
    /// 这是一条**丢数据**的回归:自动刷新每 5 秒重读选中的键,而重读会把编辑区重置回
    /// 服务端的现值。于是"改了一半、还没按保存"的内容会被服务器的旧值悄悄盖掉,
    /// 而且盖得毫无痕迹 —— 用户只会以为自己没输进去。
    /// </para>
    /// </summary>
    [TestMethod]
    public void AutoRefreshTick_KeepsTheUnsavedStringDraft()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync(_prefix);
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:name");
                await PumpAsync();
                Assert.IsTrue(viewModel.IsStringSelected);
                Assert.AreEqual("张三", viewModel.StringDraft);

                viewModel.StringDraft = "改到一半还没保存";
                Assert.IsTrue(viewModel.IsStringDirty);
                Assert.IsTrue(viewModel.HasUnsavedEdits);

                await viewModel.AutoRefreshTickAsync();
                await PumpAsync();

                Assert.AreEqual("改到一半还没保存", viewModel.StringDraft, "自动刷新把没保存的编辑盖掉了。");
                Assert.IsTrue(viewModel.IsStringDirty, "草稿还在,脏标记就该还在 —— 否则保存按钮会消失。");
                // 服务端的现值没被动过:让开的是覆盖那一步,不是把界面冻在过去。
                Assert.AreEqual("张三", viewModel.StringValue);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// TTL / 重命名 / 新增行这些手打的框同样算未保存 —— 它们被清空一样是丢输入,
    /// 只是丢得更不容易被发现(用户往往刚把一个长键名粘进去)。
    /// </summary>
    [TestMethod]
    public void AutoRefreshTick_KeepsTheOtherDrafts_AndSaysWhyItIsHolding()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync(_prefix);
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:profile");
                await PumpAsync();
                Assert.IsFalse(viewModel.HasUnsavedEdits, "刚选中,什么都没动过。");

                viewModel.RenameDraft = $"{_prefix}:user:1:profile:v2";
                viewModel.TtlDraft = "30m";
                viewModel.NewLabel = "nickname";
                viewModel.NewValue = "老张";
                Assert.IsTrue(viewModel.HasUnsavedEdits);

                // 开着自动刷新时,界面要说得出它为什么不动 —— 否则看着就像刷新坏了。
                viewModel.ToggleAutoRefreshCommand.Execute(null);
                await PumpAsync(10);
                Assert.IsTrue(viewModel.IsAutoRefreshOn);
                Assert.IsTrue(viewModel.IsAutoRefreshPaused);
                Assert.IsNotEmpty(viewModel.AutoRefreshPausedNotice);

                await viewModel.AutoRefreshTickAsync();
                await PumpAsync();

                Assert.AreEqual($"{_prefix}:user:1:profile:v2", viewModel.RenameDraft);
                Assert.AreEqual("30m", viewModel.TtlDraft);
                Assert.AreEqual("nickname", viewModel.NewLabel);
                Assert.AreEqual("老张", viewModel.NewValue);

                // 清空之后自动刷新恢复,说明也跟着消失。
                viewModel.RenameDraft = string.Empty;
                viewModel.TtlDraft = string.Empty;
                viewModel.NewLabel = string.Empty;
                viewModel.NewValue = string.Empty;
                Assert.IsFalse(viewModel.HasUnsavedEdits);
                Assert.IsFalse(viewModel.IsAutoRefreshPaused);
                Assert.IsEmpty(viewModel.AutoRefreshPausedNotice);

                await viewModel.AutoRefreshTickAsync();
                await PumpAsync();
                Assert.IsTrue(viewModel.HasSelection, "没有未保存的编辑时,自动刷新照旧重读。");
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// 面板的几何要对得上设计稿:下拉框与同行的输入框同高、TTL 框放得下、
    /// 值编辑区铺满整行、抽屉按设计稿的高度开、拖拽条抓得住。
    /// <para>
    /// 这几条全是"看一眼就知道不对、但没有断言就会一路漂回去"的量。设计稿在
    /// <c>VelaShell.Plugin.Redis.pen</c>,数值取自那里的对应节点。
    /// </para>
    /// </summary>
    [TestMethod]
    public void Panel_Geometry_MatchesTheDesign()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync(_prefix);
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:name");
                await PumpAsync();

                // 下拉框:Fluent 默认 32px,压到与 TextBox.field 同一档的 24px。
                foreach (string name in new[] { "DatabaseBox", "TypeFilterBox" })
                {
                    ComboBox box = view.GetControl<ComboBox>(name);
                    Assert.AreEqual(24d, box.Bounds.Height, $"{name} 应与同行的输入框同高。");
                }

                // TTL 输入框 180、重命名 300(设计稿 HtzFe / yampr 两行的输入宽度)。
                Assert.AreEqual(180d, view.GetControl<TextBox>("TtlBox").Bounds.Width);
                Assert.AreEqual(300d, view.GetControl<TextBox>("RenameBox").Bounds.Width);
                // 占位符必须放得进 180px:格式清单属于右边的回显位,不是占位符。
                Assert.IsFalse(view.GetControl<TextBox>("TtlBox").Watermark!.Contains("2h30m", StringComparison.Ordinal),
                    "格式清单塞进占位符就会被截断,应放在 TtlPreview 那一格。");
                Assert.Contains("2h30m", viewModel.TtlPreview, "TTL 框空着时,右边那一格要给出格式说明。");

                // 右栏各行的高度直接照设计稿量(节点名见 .pen 的对应帧)。
                foreach ((string name, double expected) in new (string, double)[]
                         {
                             ("KeyHeaderRow", 36),      // 键头 QTgjn
                             ("KeyMetaRow", 26),        // 元信息 v5yO5
                             ("KeyActionRow", 62),      // 键级动作条 w22A0
                             ("DecodeToolbar", 36),     // 值工具条 GhQ8J
                             ("KeyFooterRow", 30)       // 底部条 xc9er
                         })
                {
                    Assert.AreEqual(expected, view.GetControl<Border>(name).Bounds.Height,
                        $"{name} 与设计稿的高度对不上。");
                }
                // 左栏 420 + 分隔条 4(设计稿 Qf4Wh / pVk7j)。
                Assert.AreEqual(4d, view.GetControl<Border>("ColumnSplitLine").Bounds.Width);

                // 值编辑区铺满整行 —— 断言的是"和所在那一行一样大",不是某个像素数:
                // 原先 ScrollViewer 给内容无限高度,TextBox 只按内容量到 MinHeight=120 就不长了,
                // 于是一个短值在大片空白里浮着一个小框。窗口多大,这一格就该多大。
                TextBox value = view.GetControl<TextBox>("StringValueBox");
                var valueRow = (Control)value.Parent!;
                Assert.AreEqual(valueRow.Bounds.Height, value.Bounds.Height, "值编辑区应铺满内容行的高度。");
                Assert.AreEqual(valueRow.Bounds.Width, value.Bounds.Width, "值编辑区应铺满内容行的宽度。");
                Assert.IsGreaterThan(200d, value.Bounds.Height, "内容行本身也不该塌掉。");

                // 抽屉:默认高度 + 拖拽条抓得住。收起时那一行整个塌成 0 ——
                // 一条拖不出东西来的拖拽条只会让人以为界面卡了,所以先展开再量。
                Thumb resizer = view.GetControl<Thumb>("DrawerResizer");
                Assert.AreEqual(0d, resizer.Bounds.Height, "抽屉收起时拖拽条不该占位置。");

                viewModel.ToggleDrawerCommand.Execute(null);
                await PumpAsync(20);

                Assert.IsTrue(viewModel.IsDrawerOpen);
                Assert.AreEqual(300d, viewModel.DrawerHeight);
                Assert.IsGreaterThanOrEqualTo(7d, resizer.Bounds.Height, "抓取区太窄就等于拖不动。");
                Assert.IsGreaterThan(0d, resizer.Bounds.Width);

                // 拖上去 120px:抽屉长高,主体让位。
                viewModel.ResizeDrawer(-120, window.Height);
                Assert.AreEqual(420d, viewModel.DrawerHeight);
                // 往下拖到底也不会塌掉:最小高度兜住。
                viewModel.ResizeDrawer(9999, window.Height);
                Assert.AreEqual(120d, viewModel.DrawerHeight);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>只收绑定域的告警与报错 —— 其余日志与本用例无关,收进来只会挡住要看的那几条。</summary>
    private sealed class BindingLogSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add(messageTemplate);
            }
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
            params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add($"{messageTemplate} [{string.Join(", ", propertyValues)}]");
            }
        }
    }

    [TestMethod]
    public void Panel_Loads_WithoutHostThemeTokens()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, _, RedisConnection connection) = await ShowAsync();
            try
            {
                // AXAML 真装载了:样式、模板、Loc[...] 索引器绑定全部就位。
                Assert.IsNotNull(view.GetControl<ListBox>("KeyList"));
                Assert.IsNotNull(view.GetControl<TextBox>("FilterBox"));
                Assert.IsNotNull(view.GetControl<TextBlock>("MatchEcho"));
                Assert.IsNotNull(view.GetControl<TextBlock>("ScanStatus"));
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void MatchEcho_ShowsTheCommandThatWillActuallyBeSent()
    {
        // 这一行小字是"过滤条语义看得见"那条设计决定的落地处,值得钉住。
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, _, RedisConnection connection) = await ShowAsync($"{_prefix}:user");
            try
            {
                string echo = view.GetControl<TextBlock>("MatchEcho").Text ?? "";
                Assert.Contains("SCAN 0 MATCH", echo);
                Assert.Contains($"{_prefix}:user*", echo);
                Assert.Contains("COUNT 500", echo);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void Scan_BuildsTheKeyTreeAndReportsAnHonestStatus()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user");
            try
            {
                Assert.IsTrue(viewModel.IsScanComplete, "游标应已归零。");
                Assert.AreEqual(2, viewModel.MatchedCount);
                // 扁平列表:一行一个**完整键名**,两个键都在眼前,不必逐层点开。
                CollectionAssert.AreEquivalent(
                    new[] { $"{_prefix}:user:1:name", $"{_prefix}:user:1:profile" },
                    viewModel.Rows.Select(row => row.Display).ToArray());
                Assert.DoesNotContain(row => row.IsGroup, viewModel.Rows, "两个键远低于折叠阈值。");
                Assert.AreEqual("string", viewModel.Rows.Single(row => row.Display.EndsWith(":name", StringComparison.Ordinal)).TypeName);
                Assert.AreEqual("hash", viewModel.Rows.Single(row => row.Display.EndsWith(":profile", StringComparison.Ordinal)).TypeName);

                // 面包屑 = 这批键的公共前缀,用户据此知道自己在哪一层。
                CollectionAssert.AreEqual(
                    new[] { _prefix, "user", "1" },
                    viewModel.Breadcrumb.Select(segment => segment.Label).ToArray());

                // **只有游标归零才敢说"全部"** —— 状态条的措辞是这条纪律的出口。
                string status = view.GetControl<TextBlock>("ScanStatus").Text ?? "";
                Assert.Contains("游标已归零", status);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    /// <summary>
    /// 选了「集群」但连的是单机:扫描仍必须真的扫得动,并且要**明说**形态不符。
    /// <para>
    /// 这个测试来自真机上的一次翻车。集群路径原先走 <c>IServer.ExecuteAsync("SCAN", …)</c>,
    /// 那条路不带库号,服务器直接回 <c>A target database is required for SCAN</c> ——
    /// 用户看到的是一棵空键树加一句红字。改用 <c>IServer.KeysAsync(database, …)</c> 后
    /// 这条路显式携带库号,单机上也走得通,所以本机没有集群也能把这个回归钉住。
    /// </para>
    /// <para>
    /// 顺带验证形态不符的提示:配错形态最难受的表现正是"什么都没有,也没人告诉你为什么"。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ClusterDeploymentAgainstStandalone_StillScans_AndSaysTheModeDoesNotMatch()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user", deployment: "cluster");
            try
            {
                // 1)扫描没有炸在"没有目标库"上 —— 状态条里不该出现那句服务器错误。
                Assert.IsFalse(
                    (viewModel.StatusMessage ?? "").Contains("target database", StringComparison.OrdinalIgnoreCase),
                    $"集群路径又丢了库号:{viewModel.StatusMessage}");
                Assert.IsTrue(viewModel.IsScanComplete, "逐节点扫描应已走到最后一个节点的游标归零。");

                // 2)形态不符要说出来(服务器自报 standalone,配置里选的是集群),而且这句话
                //    必须**活过一次扫描** —— 它写在常驻的提示条上,不是被扫描清空的状态行。
                Assert.AreEqual("standalone", connection.Info.Mode);
                Assert.AreEqual(
                    new Loc("zh-Hans")["Redis_ModeMismatchStandalone"],
                    viewModel.DeploymentWarning,
                    "形态不符时提示条要给出那句话。");
                Assert.IsTrue(viewModel.HasDeploymentWarning);
                Assert.IsTrue(view.GetControl<Border>("DeploymentWarningBar").IsVisible,
                    "提示条应当在界面上真的可见。");
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void SelectingAHashKey_LoadsItsFieldsIntoTheDetailPane()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user");
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:profile");
                await PumpAsync();

                Assert.IsTrue(viewModel.HasSelection);
                Assert.AreEqual("hash", viewModel.Selected!.Type);
                Assert.IsTrue(viewModel.IsCollectionSelected);
                Assert.IsFalse(viewModel.IsStringSelected);
                Assert.AreEqual("字段", viewModel.LabelColumnHeader);
                Assert.AreEqual("张三", viewModel.Elements.Single(e => e.Label == "name").Value);
                Assert.AreEqual("永不过期", viewModel.SelectedTtlText);
                Assert.IsTrue(view.GetControl<ListBox>("ElementList").IsVisible);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void SelectingAStringKey_ShowsTheValueEditor()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, RedisWorkspaceView view, RedisWorkspaceViewModel viewModel, RedisConnection connection) =
                await ShowAsync($"{_prefix}:user");
            try
            {
                viewModel.SelectedRow = Row(viewModel, $"{_prefix}:user:1:name");
                await PumpAsync();

                Assert.IsTrue(viewModel.IsStringSelected);
                Assert.AreEqual("张三", viewModel.StringValue);
                Assert.AreEqual(string.Empty, viewModel.TruncationNotice, "没超上限就不该出现截断提示。");
                TextBox box = view.GetControl<TextBox>("StringValueBox");
                Assert.IsTrue(box.IsVisible);
                Assert.IsTrue(box.IsReadOnly, "M1 的值编辑器是只读的:写入随类型编辑器一起做。");
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void ProductionProfile_TurnsOnReadOnlyAndShowsTheBadges()
    {
        // 护栏的第一档在界面上必须看得见:生产标记 + 只读徽章。
        RequireServer();
        OnUi(async () =>
        {
            (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) = await ShowAsync();
            try
            {
                Assert.IsTrue(viewModel.IsProduction);
                Assert.IsTrue(viewModel.IsReadOnly);
                Assert.AreEqual("生产", viewModel.EnvironmentLabel);
                Assert.AreEqual("只读", viewModel.ReadOnlyLabel);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }

    [TestMethod]
    public void DatabaseDropdown_ListsEveryDatabaseWithItsKeyCount()
    {
        RequireServer();
        OnUi(async () =>
        {
            (Window window, _, RedisWorkspaceViewModel viewModel, RedisConnection connection) = await ShowAsync();
            try
            {
                Assert.IsTrue(viewModel.SupportsDatabases);
                Assert.IsGreaterThanOrEqualTo(10, viewModel.Databases.Count);
                Assert.AreEqual(Database, viewModel.SelectedDatabase!.Index);
                // 键数直接进下拉文本,省掉"逐个库点进去看有没有东西"的盲测。
                Assert.Contains("db9", viewModel.Databases[Database].Display);
            }
            finally
            {
                window.Close();
                await connection.DisposeAsync();
            }
        });
    }
}
