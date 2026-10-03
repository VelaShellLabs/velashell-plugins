using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// UI 测试的公共基建:headless 会话、在 UI 线程上跑异步测试体、泵消息、开一个工作台窗口、截图。
/// </summary>
internal static class Screens
{
    private static HeadlessUnitTestSession? _session;

    /// <summary>截图输出目录(<c>MONGO_SCREENSHOT_DIR</c>);没设就不落盘。</summary>
    public static string? OutputDirectory => Environment.GetEnvironmentVariable("MONGO_SCREENSHOT_DIR") is { Length: > 0 } dir ? dir : null;

    /// <summary>截图用的库(<c>MONGO_SCREENSHOT_DB</c>,默认 <c>shop</c> —— 与设计稿的数据同形)。</summary>
    public static string Database => Environment.GetEnvironmentVariable("MONGO_SCREENSHOT_DB") is { Length: > 0 } db ? db : "shop";

    /// <summary>
    /// 在 headless UI 线程上跑一段**异步**测试体。lambda 必须带返回值 ——
    /// <see cref="HeadlessUnitTestSession" /> 没有 <c>Func&lt;Task&gt;</c> 重载,写成无返回值会拿到一个
    /// 从未被等待的 <c>Task&lt;Task&gt;</c>:测试体跑到第一个 await 就"通过"了(仓库里记过这一条)。
    /// </summary>
    public static void OnUi(Func<Task> body)
    {
        _session ??= HeadlessUnitTestSession.GetOrStartForAssembly(typeof(Screens).Assembly);
        _ = _session.Dispatch(async () =>
        {
            await body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>泵一会儿消息(让异步加载、绑定与布局跑完)。</summary>
    public static async Task PumpAsync(int rounds = 60)
    {
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>开一个工作台(连接 + 视图模型 + 视图 + 1440×812 的窗口)。</summary>
    public static async Task<Workbench> OpenWorkbenchAsync(string? database = null, string locale = "zh-CN", IDictionary<string, string>? settings = null)
    {
        var all = new Dictionary<string, string>(settings ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["database"] = database ?? Database
        };
        MongoConnection connection = await TestServer.OpenAsync(all);
        var context = new TestPluginContext();
        var viewModel = new MongoWorkspaceViewModel(new Loc(locale), context, new MongoStore(context));
        var view = new MongoWorkspaceView(viewModel);
        // 设计稿的文档区:56px 工具栏 + 756px 主体 + 24px 状态条 = 836。
        var window = new Window { Width = 1440, Height = 836, Content = view };
        window.Show();
        await viewModel.InitializeAsync();
        // 连接由插件自己管:这里把测试服务器当成一条已保存的连接挂到对象树根上(不经存储、不建跳板)。
        var profile = new MongoProfile { Name = "mongo-inner-01", Host = connection.Endpoint, Settings = new(all, StringComparer.Ordinal) };
        MongoSession session = await viewModel.AttachAsync(profile, connection);
        await PumpAsync();
        return new(window, view, viewModel, session, connection, context);
    }

    /// <summary>截一张图(设了输出目录才落盘),返回位图。</summary>
    public static WriteableBitmap? Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        if (frame is not null && OutputDirectory is { } dir)
        {
            _ = Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
        }
        return frame;
    }
}

/// <summary>一个开着的工作台。</summary>
internal sealed record Workbench(
    Window Window,
    MongoWorkspaceView View,
    MongoWorkspaceViewModel ViewModel,
    MongoSession Session,
    MongoConnection Connection,
    TestPluginContext Context) : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Window.Close();
        ViewModel.Dispose();
        await Connection.DisposeAsync();
        Context.Dispose();
    }
}
