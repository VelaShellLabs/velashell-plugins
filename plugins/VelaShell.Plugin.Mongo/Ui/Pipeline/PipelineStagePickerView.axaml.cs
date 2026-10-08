using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>阶段搜索弹层的视图:搜索框里 ↑↓ 选、↵ 确认、Esc 关;单击列表项即选中。</summary>
public sealed partial class PipelineStagePickerView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal PipelineStagePickerView(PipelineStagePickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public PipelineStagePickerView()
    {
        InitializeComponent();
    }

    /// <summary>Esc:请宿主(弹层)关掉自己。</summary>
    public event EventHandler? CloseRequested;

    private PipelineStagePickerViewModel? ViewModel => DataContext as PipelineStagePickerViewModel;

    /// <summary>打开弹层后把焦点给搜索框。</summary>
    public void FocusSearch()
    {
        _ = Search.Focus();
        Search.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                vm.Move(1);
                Reveal();
                e.Handled = true;
                break;
            case Key.Up:
                vm.Move(-1);
                Reveal();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((e.Source as Control)?.DataContext is PipelineStageChoice choice)
        {
            ViewModel?.Accept(choice);
        }
    }

    private void Reveal()
    {
        if (ViewModel?.Selected is { } selected)
        {
            List.ScrollIntoView(selected);
        }
    }
}
