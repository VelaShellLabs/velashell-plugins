using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>服务器监控视图(设计稿 11)。</summary>
public sealed partial class MonitorTabView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal MonitorTabView(MonitorTabViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public MonitorTabView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => MonitorChartColors.Sync(this);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MonitorChartColors.Sync(this);
    }

    /// <summary>
    /// 采样间隔的下拉在代码里现建菜单:弹出层是独立的可视树,写在 AXAML 里的菜单项
    /// 要绕回本视图的数据上下文才能改间隔;这里直接拿着视图模型改。
    /// </summary>
    private void OnIntervalClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorTabViewModel vm || sender is not Control anchor)
        {
            return;
        }
        var items = new List<MenuItem>();
        foreach (int seconds in MonitorTabViewModel.IntervalChoices)
        {
            items.Add(new MenuItem
            {
                Header = vm.Loc.Format("Mon_Every", seconds),
                Icon = seconds == vm.IntervalSeconds
                    ? new Glyph { Key = "Mongo.check", Size = 12, Brush = ThemeBrushes.Get("VelaAccent", Brushes.SteelBlue) }
                    : null,
                Command = new RelayCommand(() => vm.IntervalSeconds = seconds)
            });
        }
        new ContextMenu { ItemsSource = items }.Open(anchor);
        e.Handled = true;
    }
}
