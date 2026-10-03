using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 导出向导的视图。代码里只做一件 AXAML 做不了的事:字段表按住左侧的拖动柄上下拖,调整列顺序
/// (设计稿 19「拖动调整列顺序」)。每拖过一行高(28px)挪一格,松手即定。
/// </summary>
public sealed partial class ExportWizardView : UserControl
{
    private const double RowHeight = 28;

    private ExportFieldRow? _dragRow;
    private double _dragOrigin;
    private int _dragStartIndex;

    /// <summary>用给定的视图模型初始化。</summary>
    internal ExportWizardView(ExportWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Wire();
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ExportWizardView()
    {
        InitializeComponent();
        Wire();
    }

    private ExportWizardViewModel? ViewModel => DataContext as ExportWizardViewModel;

    private void Wire()
    {
        FieldList.AddHandler(PointerPressedEvent, OnFieldPressed, RoutingStrategies.Tunnel);
        FieldList.AddHandler(PointerMovedEvent, OnFieldMoved, RoutingStrategies.Tunnel);
        FieldList.AddHandler(PointerReleasedEvent, OnFieldReleased, RoutingStrategies.Tunnel);
        FieldList.AddHandler(PointerCaptureLostEvent, (_, _) => _dragRow = null, RoutingStrategies.Bubble);
    }

    private void OnFieldPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || !e.GetCurrentPoint(FieldList).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Border? grip = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(static b => b.Classes.Contains("grip"));
        if (grip?.DataContext is not ExportFieldRow row)
        {
            return;
        }
        _dragRow = row;
        _dragOrigin = e.GetPosition(FieldList).Y;
        _dragStartIndex = vm.Fields.IndexOf(row);
        e.Pointer.Capture(FieldList);
        e.Handled = true;
    }

    private void OnFieldMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is not { } row || ViewModel is not { } vm)
        {
            return;
        }
        int steps = (int)Math.Round((e.GetPosition(FieldList).Y - _dragOrigin) / RowHeight);
        int target = Math.Clamp(_dragStartIndex + steps, 0, vm.Fields.Count - 1);
        int current = vm.Fields.IndexOf(row);
        if (current >= 0 && current != target)
        {
            vm.MoveField(current, target);
        }
        e.Handled = true;
    }

    private void OnFieldReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null)
        {
            return;
        }
        _dragRow = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
