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
    public async Task DeactivateAsync(CancellationToken cancellationToken)
    {
        _command?.Dispose();
        _command = null;
        if (_panel is { } panel)
        {
            await panel.CloseAsync().ConfigureAwait(false);
            _panel = null;
        }
        // 连接、跳板转发、各标签的游标与订阅都挂在外壳上:它一走,服务器那边就干净了。
        _viewModel?.Dispose();
        _viewModel = null;
        ThemeBrushes.Detach();
        _context = null;
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
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = viewModel.InitializeAsync());
                    return view;
                },
                context.Shutdown).ConfigureAwait(false);
            _panel.Closed += () =>
            {
                _panel = null;
                viewModel.Dispose();
                if (ReferenceEquals(_viewModel, viewModel))
                {
                    _viewModel = null;
                }
            };
        }
        catch (Exception ex)
        {
            context.Log.Error($"Opening the MongoDB workbench failed: {ex.Message}");
            viewModel.Dispose();
            _viewModel = null;
        }
    }
}
