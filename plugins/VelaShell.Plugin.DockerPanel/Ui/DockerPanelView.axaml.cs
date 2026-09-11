using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 面板视图。
/// <para>
/// 代码后置只做一件事:把几个"参数是数据对象、目标是普通委托"的点击接回视图模型 ——
/// 这些回调(恢复动作、toast 动作、取消某个任务)天生就是 <c>Action</c>,
/// 为它们各造一个 <c>ICommand</c> 只是为了讨好 XAML,不值得。
/// </para>
/// </summary>
public sealed partial class DockerPanelView : UserControl
{
    /// <summary>建视图并挂上视图模型。</summary>
    public DockerPanelView(DockerPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        // 文件选择器要一个顶层窗口,而只有视图知道自己被画在哪个窗口里。
        FilePicker.Attach(() => TopLevel.GetTopLevel(this));
        // 弹层一开就把焦点收进面板:键盘事件是顺着焦点那条路由走的,
        // 焦点若还留在面板外面(或者压根没有),Esc 根本到不了这里。
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DockerPanelViewModel.HasForm) or nameof(DockerPanelViewModel.HasDialog))
            {
                TakeFocusForOverlay();
            }
        };
        viewModel.Confirm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConfirmGate.IsOpen))
            {
                TakeFocusForOverlay();
            }
        };
        viewModel.Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CommandPalette.IsOpen))
            {
                TakeFocusForOverlay();
            }
        };
    }

    /// <summary>焦点已经在面板里就别动它(命令面板自己会去聚焦输入框);不在就收回来。</summary>
    private void TakeFocusForOverlay()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused &&
            focused.FindAncestorOfType<DockerPanelView>(true) == this)
        {
            return;
        }
        Focus();
    }

    /// <summary>无参构造只为设计器与 XAML 装载器;运行时走带上下文的那个。</summary>
    public DockerPanelView() => InitializeComponent();

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        Focusable = true;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Esc 挂在**顶层**上,不挂在面板上。
        // 键盘事件是顺着焦点那条路由走的:焦点若不在面板里(用户刚点过遮罩、
        // 或者面板压根还没拿到过焦点),挂在面板上的处理器根本不在路由上 ——
        // 这正是"所有二级窗口按 Esc 都没反应"的原因。顶层在任何一条路由的最上游。
        // Tunnel 优先:冒泡的话,焦点所在的 TextBox 会先把 Esc 吃掉(清输入、收候选);
        // 两种策略都注册是为了兼容,处理过的那一次会置 Handled,不会关掉两层。
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.AddHandler(KeyDownEvent, OnEscape, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.RemoveHandler(KeyDownEvent, OnEscape);
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Esc:关掉最上面那一层弹层。</summary>
    private void OnEscape(object? sender, KeyEventArgs e)
    {
        if (!e.Handled && e.Key == Key.Escape &&
            DataContext is DockerPanelViewModel viewModel && viewModel.CloseTopOverlay())
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 点遮罩 = 关掉这一层。
    /// <para>
    /// 只有命令面板这么做。表单、闸门、对话框里用户是**输了东西**的,
    /// 手滑点到旁边就把内容丢掉,比多按一次 Esc 糟得多;它们留 Esc 与「取消」两条路。
    /// </para>
    /// </summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is DockerPanelViewModel { Palette.IsOpen: true } viewModel)
        {
            viewModel.Palette.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 面板级快捷键。
    /// <para>
    /// 只在**面板拿到焦点**时生效(<c>OnKeyDown</c> 走的是面板自己的可视树),
    /// 不注册全局热键 —— 宿主自己也有 <c>Ctrl+K</c>,抢它的键会让用户在别处按不出宿主的命令面板。
    /// </para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not DockerPanelViewModel viewModel || !viewModel.IsReady)
        {
            base.OnKeyDown(e);
            return;
        }
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.K)
        {
            viewModel.OpenPaletteCommand.Execute(null);
            e.Handled = true;
            return;
        }
        // 行级快捷键落在当前选中的那一行上;没有选中就什么都不做,
        // 而不是猜一个 —— 对着错的容器执行"停止"比没反应糟得多。
        if (ctrl && viewModel.Containers.Detail is { } detail)
        {
            switch (e.Key)
            {
                case Key.R:
                    detail.RestartCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemPeriod:
                    detail.StopCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }
        base.OnKeyDown(e);
    }

    private void OnRecoveryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: RecoveryAction action })
        {
            action.Invoke();
        }
    }

    private void OnToastAction(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: ToastAction action })
        {
            action.Invoke();
        }
    }

    private void OnDismissToast(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: Toast toast } && DataContext is DockerPanelViewModel viewModel)
        {
            viewModel.Feedback.Dismiss(toast);
        }
    }

    private void OnCancelTask(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: PanelTask task })
        {
            task.Cancel();
        }
    }
}
