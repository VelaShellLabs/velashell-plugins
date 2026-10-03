using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 管道构建器的视图。代码里只做三件 AXAML 做不好的事:
/// 阶段搜索弹层(两处入口共用一张,挂到点的那个控件下面)、抽样数菜单、拖动手柄重排。
/// </summary>
public sealed partial class PipelineTabView : UserControl
{
    private readonly PipelineTabViewModel? _viewModel;
    private readonly PipelineStagePickerViewModel? _picker;
    private readonly PipelineStagePickerView? _pickerView;
    private PipelineStage? _dragging;
    private Border? _draggingCard;

    /// <summary>用给定的视图模型初始化。</summary>
    internal PipelineTabView(PipelineTabViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        _picker = new PipelineStagePickerViewModel(viewModel.Loc);
        _pickerView = new PipelineStagePickerView(_picker);
        _pickerView.CloseRequested += (_, _) => PickerPopup.IsOpen = false;
        PickerPopup.Child = _pickerView;

        // 拖动手柄:按下时把指针捕获到列表本身(而不是手柄)—— 重排时卡片的容器会被拆了重建,
        // 捕获在手柄上就会在第一次换位时丢掉。
        StageList.AddHandler(PointerPressedEvent, OnStagePointerPressed, RoutingStrategies.Tunnel);
        StageList.AddHandler(PointerMovedEvent, OnStagePointerMoved, RoutingStrategies.Tunnel);
        StageList.AddHandler(PointerReleasedEvent, OnStagePointerReleased, RoutingStrategies.Tunnel);
        StageList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble);
        ResultScroller.SizeChanged += (_, _) => FitResultTable();
        ColumnFit.OnGripDoubleClick(ResultHeader, column =>
        {
            if (column is PipelineColumn c)
            {
                FitResultColumn(c);
            }
        });
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public PipelineTabView()
    {
        InitializeComponent();
    }

    // ── 阶段搜索弹层 ───────────────────────────────────────────────────────

    /// <summary>在 <paramref name="anchor" /> 下面打开阶段列表,选中后回调。</summary>
    private void OpenPicker(Control anchor, string? current, Action<string> picked)
    {
        if (_picker is null || _pickerView is null)
        {
            return;
        }
        _picker.Reset(current);
        _picker.Picked = op =>
        {
            PickerPopup.IsOpen = false;
            picked(op);
        };
        PickerPopup.PlacementTarget = anchor;
        PickerPopup.IsOpen = true;
        Dispatcher.UIThread.Post(_pickerView.FocusSearch, DispatcherPriority.Input);
    }

    private void OnAddStageClick(object? sender, RoutedEventArgs e) => OpenAddPicker(AddStageTool);

    private void OnAddStageBoxReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            OpenAddPicker(AddStageBox);
        }
    }

    private void OpenAddPicker(Control anchor)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        OpenPicker(anchor, null, op =>
        {
            PipelineStage stage = vm.AddStage(op);
            Dispatcher.UIThread.Post(() => Reveal(stage), DispatcherPriority.Background);
        });
    }

    private void OnOperatorClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm || sender is not Control { DataContext: PipelineStage stage } anchor)
        {
            return;
        }
        OpenPicker(anchor, stage.Operator, op => vm.ChangeOperator(stage, op));
    }

    /// <summary>新加的卡片滚进视野(加在末尾时它在可视区下方)。</summary>
    private void Reveal(PipelineStage stage)
    {
        if (StageList.ContainerFromItem(stage) is Control container)
        {
            container.BringIntoView();
        }
    }

    /// <summary>
    /// 阶段编辑器按行数定高、整段摆出,所以关掉它自己的竖向滚动:
    /// 否则滚轮停在编辑器上时被它吃掉,外面那一列卡片滚不动。
    /// </summary>
    private void OnStageEditorLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not CodeEditor editor || editor.Tag is "pipeline-hooked")
        {
            return;
        }
        editor.Tag = "pipeline-hooked";
        editor.Editor.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        // 诊断行尾写着「Alt+↵ 修复」(含点号的裸键加引号):在这里兑现它。
        editor.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key == Key.Enter && args.KeyModifiers == KeyModifiers.Alt
                && editor.DataContext is PipelineStage stage
                && stage.Diagnostics.FirstOrDefault(static d => d.FixText is not null) is { FixText: { } fix } diagnostic)
            {
                editor.Replace(diagnostic.Offset, diagnostic.Length, fix);
                args.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    // ── 抽样数 ─────────────────────────────────────────────────────────────

    private void OnSampleClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }
        var items = new List<MenuItem>();
        foreach (int size in PipelineTabViewModel.SampleChoices)
        {
            items.Add(new MenuItem
            {
                Header = vm.Loc.Format("Pipe_Sample", size),
                Icon = size == vm.SampleSize
                    ? new Glyph { Key = "Mongo.check", Size = 12, Brush = ThemeBrushes.Get("VelaAccent", Avalonia.Media.Brushes.Gray) }
                    : null,
                Command = new RelayCommand(() => vm.SampleSize = size)
            });
        }
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Open(SampleButton);
    }

    // ── 拖动重排 ───────────────────────────────────────────────────────────

    private void OnStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source || !e.GetCurrentPoint(StageList).Properties.IsLeftButtonPressed)
        {
            return;
        }
        // 只认手柄:从按下的元素往上找,先碰到卡片外框就说明按的不是手柄。
        Border? grip = null;
        for (Visual? v = source; v is not null && !ReferenceEquals(v, StageList); v = v.GetVisualParent())
        {
            if (v is Border border && border.Classes.Contains("grip"))
            {
                grip = border;
                break;
            }
            if (v is Border card && card.Classes.Contains("stage"))
            {
                return;
            }
        }
        if (grip?.DataContext is not PipelineStage stage)
        {
            return;
        }
        _dragging = stage;
        _draggingCard = grip.GetVisualAncestors().OfType<Border>().FirstOrDefault(static b => b.Classes.Contains("stage"));
        _draggingCard?.Classes.Add("dragging");
        e.Pointer.Capture(StageList);
        e.Handled = true;
    }

    private void OnStagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is not { } stage || _viewModel is not { } vm)
        {
            return;
        }
        double y = e.GetPosition(StageList).Y;
        int target = vm.Stages.Count - 1;
        for (int i = 0; i < vm.Stages.Count; i++)
        {
            if (StageList.ContainerFromIndex(i) is not Control container)
            {
                continue;
            }
            Rect bounds = container.Bounds;
            if (y < bounds.Y + bounds.Height / 2)
            {
                target = i;
                break;
            }
        }
        int from = vm.Stages.IndexOf(stage);
        // 往下拖时目标是"越过了谁的中线":落在它后面,索引要减掉自己腾出来的那一格。
        if (target > from)
        {
            target = Math.Max(from, target - 1);
            if (StageList.ContainerFromIndex(vm.Stages.Count - 1) is Control last && y >= last.Bounds.Y + last.Bounds.Height / 2)
            {
                target = vm.Stages.Count - 1;
            }
        }
        if (target != from)
        {
            vm.MoveStageTo(stage, target);
            RestyleDragged();
        }
    }

    private void OnStagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging is null)
        {
            return;
        }
        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        _ = (_draggingCard?.Classes.Remove("dragging"));
        _draggingCard = null;
        _dragging = null;
    }

    /// <summary>换位后卡片的容器可能是新的:把高亮挪到新容器上。</summary>
    private void RestyleDragged()
    {
        _ = (_draggingCard?.Classes.Remove("dragging"));
        _draggingCard = null;
        if (_dragging is { } stage && StageList.ContainerFromItem(stage) is Control container
            && container.GetVisualDescendants().OfType<Border>().FirstOrDefault(static b => b.Classes.Contains("stage")) is { } card)
        {
            card.Classes.Add("dragging");
            _draggingCard = card;
        }
    }

    // ── 输出表 ─────────────────────────────────────────────────────────────

    /// <summary>表比视口窄时撑满视口(行的底线一直画到右边),比视口宽时横向滚动。</summary>
    private void FitResultTable() => ResultTable.MinWidth = Math.Max(0, ResultScroller.Bounds.Width);

    /// <summary>拖列头分隔线改列宽。</summary>
    private void OnResultResizeDelta(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if ((sender as Control)?.DataContext is PipelineColumn column)
        {
            column.Width += e.Vector.X;
        }
    }

    /// <summary>双击列头分隔线:按列头与已实现的行里这一列的内容自适应。</summary>
    private void FitResultColumn(PipelineColumn column)
    {
        double width = ColumnFit.Widest(ResultTable.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("rcell")
                        && (ReferenceEquals(b.DataContext, column) || b.DataContext is PipelineCell cell && ReferenceEquals(cell.Column, column))));
        if (width > 0)
        {
            column.Width = Math.Ceiling(width) + 2;
        }
    }
}
