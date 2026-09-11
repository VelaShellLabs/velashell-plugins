using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 按权重横向瓜分宽度的面板。
/// <para>
/// 用它而不是 <c>Grid</c> 的星形列,是因为段数是数据决定的 ——
/// <c>ColumnDefinitions</c> 绑不了集合。用它而不是固定像素,是因为面板宽度
/// 是用户拖出来的,算好的像素在下一次拖动就错了。
/// </para>
/// <para>
/// 权重从哪儿读,有两条路:
/// <list type="bullet">
///   <item>子元素直接写在 XAML 里(占用比例条那种两段式)—— 用它的 <c>Tag</c> 就够了;</item>
///   <item>子元素由 <c>ItemsControl</c> 按集合生成 —— <b>必须</b>用附加属性
///     <see cref="WeightProperty" />,而且要设在**容器**上。</item>
/// </list>
/// 第二条是一个容易踩空的地方:<c>ItemsControl</c> 会把每一项包进一个
/// <c>ContentPresenter</c>,面板看到的孩子是那个 presenter,而 <c>DataTemplate</c> 里写的
/// <c>Tag</c> 在它下面一层。于是每一段的权重都读成 0、整条不占宽 ——
/// 界面上就是一条空槽,而数据其实完全正确。
/// </para>
/// </summary>
public sealed class WeightedStackPanel : Panel
{
    /// <summary>
    /// 这一段占多少(0–1 的比例,或任意同量纲的数 —— 面板只用它们的相对大小)。
    /// <para>
    /// 由 <c>ItemsControl</c> 生成子元素时,设在 <c>ItemContainerTheme</c> 里,
    /// 这样它落在面板真正看得到的那一层上。
    /// </para>
    /// </summary>
    public static readonly AttachedProperty<double> WeightProperty =
        AvaloniaProperty.RegisterAttached<WeightedStackPanel, Control, double>("Weight");

    static WeightedStackPanel() =>
        // 权重变了要重排的是**父面板**(每一段的宽度都跟着变),不是那一段自己。
        WeightProperty.Changed.AddClassHandler<Control>((child, _) =>
            (child.GetVisualParent() as WeightedStackPanel)?.InvalidateArrange());

    /// <summary>读 <see cref="WeightProperty" />。</summary>
    public static double GetWeight(Control control) => control.GetValue(WeightProperty);

    /// <summary>写 <see cref="WeightProperty" />。</summary>
    public static void SetWeight(Control control, double value) => control.SetValue(WeightProperty, value);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double total = 0;
        var last = -1;
        for (var i = 0; i < Children.Count; i++)
        {
            var weight = Weight(Children[i]);
            total += weight;
            if (weight > 0)
            {
                last = i;
            }
        }
        if (total <= 0)
        {
            return finalSize;
        }
        double x = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            // 舍入误差交给**最后一段有宽度的**吃掉,免得右边留一条一像素的缝。
            // 不能简单地给最后一个孩子:一个 0 字节的构建缓存排在末尾时,
            // 它会把整条剩余宽度都画成自己的颜色 —— 明明什么都没占。
            var width = i == last
                ? Math.Max(0, finalSize.Width - x)
                : finalSize.Width * (Weight(child) / total);
            child.Arrange(new(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }

    /// <summary>附加属性优先;没设过再退回 <c>Tag</c>(手写子元素的那两处用的是它)。</summary>
    private static double Weight(Control child) =>
        child.GetValue(WeightProperty) is var attached && attached > 0 ? attached
        : child.Tag is double weight && weight > 0 ? weight
        : child.Tag is string text && double.TryParse(text, out var parsed) && parsed > 0 ? parsed
        : 0;
}
