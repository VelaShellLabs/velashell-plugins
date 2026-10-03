using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Ui;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Mongo;

/// <summary>
/// MongoDB 插件入口,与 Docker 面板同一个路数:注册一条命令(Ctrl+P 里的「MongoDB: 打开 MongoDB 工作台」),
/// 按下它开一个文档面板。连接由插件自己管 —— 已保存的连接列在工作台对象树的根上,新建 / 编辑是设计稿 10 的对话框,
/// 口令进宿主的加密密钥库,SSH 跳板经宿主的 SSH 会话转发;宿主的会话树与新建连接窗口里都没有 MongoDB。
/// <para>
/// 惰性激活:清单里声明了 <c>onCommand:velashell.mongo.open</c>,用户在命令面板里点了它才装载本程序集与驱动 ——
/// 不用 MongoDB 的用户一个字节都不该为它装载。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class MongoPlugin : IVelaPlugin
{
    internal const string OpenCommandId = "velashell.mongo.open";

    private IPluginContext? _context;
    private IDisposable? _command;
    private IPluginPanel? _panel;
    private MongoWorkspaceViewModel? _viewModel;

    /// <inheritdoc />
    public async Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        // 转换器取色跟随宿主换肤(见 ThemeBrushes):订阅挂在宿主的 Application 上,停用时必须摘。
        ThemeBrushes.Attach();
        _command = context.Commands.Register(new(OpenCommandId, "MongoDB: 打开 MongoDB 工作台", "MongoDB", _ => OpenPanelAsync()));
        // 激活即打开:用户是按了命令面板里那一条才走到这里的,再让他找一次入口没有道理。
        await OpenPanelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 宿主退出时,UI 线程正同步等着全部插件停用(容器拆除的那一下 <c>Wait</c>),停用本身又只给 2 秒。
    /// 所以这里**不能把停用押在 UI 线程上**:关面板要排到 UI 线程,只等一小会儿;连接与跳板转发先放
    /// (不碰界面,哪个线程都行);界面那一半在 UI 线程上就地拆,不在就投递过去(退出时投递不到也无妨,窗口随进程一起走)。
    /// 运行中停用插件时 UI 线程是空闲的,每一步都立刻完成,与原来一样。
    /// </remarks>
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _command?.Dispose();
        _command = null;
        MongoWorkspaceViewModel? viewModel = _viewModel;
        _viewModel = null;
        Task released = viewModel?.ReleaseConnectionsAsync() ?? Task.CompletedTask;
        if (_panel is { } panel)
        {
            _panel = null;
            await WithinAsync(panel.CloseAsync(), PanelCloseBudget, cancellationToken).ConfigureAwait(false);
        }
        await WithinAsync(released, ReleaseBudget, cancellationToken).ConfigureAwait(false);
        if (viewModel is not null)
        {
            OnUiThread(viewModel.Dispose);
        }
        ThemeBrushes.Detach();
        _context = null;
    }

    /// <summary>停用时关面板最多等多久(宿主给整个停用 2 秒)。</summary>
    private static readonly TimeSpan PanelCloseBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>停用时等连接与跳板转发断开最多等多久。</summary>
    private static readonly TimeSpan ReleaseBudget = TimeSpan.FromMilliseconds(1000);

    /// <summary>等一件收尾的事,超时或取消就不等了(它照样在后台做完);它自己的失败也不外溢。</summary>
    private static async Task WithinAsync(Task work, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            await work.WaitAsync(budget, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // 停用路径:任何一步的失败都不该让其余收尾半途而废。
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>界面对象只能在 UI 线程上碰:在就地做,不在就投递过去(调度器已关时投递不到,随它)。</summary>
    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        try
        {
            Dispatcher.UIThread.Post(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task OpenPanelAsync()
    {
        if (_context is not { } context)
        {
            return;
        }
        // 已经开着就把它带到眼前 —— 再开一个重复的不对,什么都不做又像是按钮坏了。
        if (_panel is { IsOpen: true } existing)
        {
            await existing.ActivateAsync().ConfigureAwait(false);
            return;
        }
        var loc = new Loc(context.Host.Locale);
        var viewModel = new MongoWorkspaceViewModel(loc, context, new MongoStore(context));
        _viewModel = viewModel;
        try
        {
            _panel = await context.Ui.ShowPanelAsync(
                new() { Title = "MongoDB", Icon = MongoIcon.Tab, DisplayMode = PanelDisplayMode.Document },
                () =>
                {
                    var view = new MongoWorkspaceView(viewModel);
                    // 读已保存的连接在控件挂上之后再跑:构造期做 I/O 会让标签页在出现前先卡住一拍。
                    Dispatcher.UIThread.Post(() => _ = viewModel.InitializeAsync());
                    return view;
                },
                context.Shutdown).ConfigureAwait(false);
            IPluginPanel panel = _panel;
            // Closed 不保证在 UI 线程上:宿主退出时调度器已经不收活,它就在线程池上直接触发。
            // 连接先放(哪个线程都行),界面那一半交给 UI 线程。
            panel.Closed += () =>
            {
                if (ReferenceEquals(_panel, panel))
                {
                    _panel = null;
                }
                _ = viewModel.ReleaseConnectionsAsync();
                OnUiThread(viewModel.Dispose);
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    _viewModel = null;
                }
            };
        }
        catch (Exception ex)
        {
            context.Log.Error($"Opening the MongoDB workbench failed: {ex.Message}");
            OnUiThread(viewModel.Dispose);
            _viewModel = null;
        }
    }
}
