using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>慢查询与当前操作视图(设计稿 15)。</summary>
public sealed partial class ProfilerTabView : UserControl
{
    /// <summary>用给定的视图模型初始化。</summary>
    internal ProfilerTabView(ProfilerTabViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    /// <summary>设计器 / headless 用的无参构造。</summary>
    public ProfilerTabView()
    {
        InitializeComponent();
        // slowms / sampleRate:回车或离开输入框即应用 —— 这两个值改一下就要打一次 profile 命令,
        // 不该每敲一个字符就发一次。
        foreach (TextBox box in new[] { SlowMsBox, SampleRateBox })
        {
            box.KeyDown += OnSettingKeyDown;
            box.LostFocus += OnSettingLostFocus;
        }
        ActualThemeVariantChanged += (_, _) => MonitorChartColors.Sync(this);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // "已运行"进度条用 MongoChart1(设计稿 15 的蓝条),同监控页一样先把图表色同步好。
        MonitorChartColors.Sync(this);
    }

    /// <summary>
    /// 样本代码块是只读展示、高度按行数给足:关掉"滚过文末"与竖向滚动条,
    /// 否则几行语句的小块也挂着一条滚动条。
    /// </summary>
    private void OnSampleEditorAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is CodeEditor editor)
        {
            editor.Editor.Options.AllowScrollBelowDocument = false;
            editor.Editor.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            // CodeEditor 在构造里把字号绑到了 VelaFontSize12,AXAML 里写的字号压不过它;
            // 设计稿的样本代码是 10px,挂进可视树之后再绑一次(它会把字号转给里面那个编辑器)。
            editor[!TemplatedControl.FontSizeProperty] = editor.GetResourceObservable("VelaFontSize10").ToBinding();
            // 左边还有一列诊断标记边栏(只读块里永远是空的),内边距就不再另加 8px,字与设计稿的 8px 缩进对齐。
            editor.Editor.Padding = new Thickness(0, 6, 8, 6);
        }
    }

    private void OnSettingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ProfilerTabViewModel vm)
        {
            vm.ApplySettingsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSettingLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfilerTabViewModel vm)
        {
            vm.ApplySettingsCommand.Execute(null);
        }
    }
}
