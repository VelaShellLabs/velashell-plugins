using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 文件表 + 拖放区的上下分配(设计稿 06:表有几行就占几行高,拖放区吃掉剩下的全部)。
/// <para>
/// 用 Grid 的 Auto 行做不到:Auto 行给子元素的是无限高,ListBox 一旦拿到无限高就不再虚拟化,
/// 一万个文件就是一万个容器。这里给第一个子元素的高度上限是"总高 − 拖放区最小高",
/// 于是表少时贴着内容、表多时自己滚动,拖放区始终至少露出 <see cref="MinRest" />。
/// </para>
/// </summary>
public sealed class GridFsSplitPanel : Panel
{
    /// <summary>第二个子元素(拖放区)的最小高度。</summary>
    public static readonly StyledProperty<double> MinRestProperty =
        AvaloniaProperty.Register<GridFsSplitPanel, double>(nameof(MinRest), 96);

    static GridFsSplitPanel()
    {
        AffectsMeasure<GridFsSplitPanel>(MinRestProperty);
    }

    /// <summary>拖放区最小高度。</summary>
    public double MinRest
    {
        get => GetValue(MinRestProperty);
        set => SetValue(MinRestProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0)
        {
            return default;
        }
        double topMax = double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : Math.Max(0, availableSize.Height - MinRest);
        Control top = Children[0];
        top.Measure(new Size(availableSize.Width, topMax));
        double topHeight = Math.Min(top.DesiredSize.Height, topMax);
        double rest = double.IsInfinity(availableSize.Height) ? MinRest : Math.Max(0, availableSize.Height - topHeight);
        double width = top.DesiredSize.Width;
        for (int i = 1; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(availableSize.Width, rest));
            width = Math.Max(width, Children[i].DesiredSize.Width);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? topHeight + rest : availableSize.Height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }
        double topHeight = Math.Min(Children[0].DesiredSize.Height, Math.Max(0, finalSize.Height - MinRest));
        Children[0].Arrange(new Rect(0, 0, finalSize.Width, topHeight));
        for (int i = 1; i < Children.Count; i++)
        {
            Children[i].Arrange(new Rect(0, topHeight, finalSize.Width, Math.Max(0, finalSize.Height - topHeight)));
        }
        return finalSize;
    }
}

/// <summary>
/// 细进度条(底栏 180×6、上传行里 4px 高的那根):圆角轨道 + 强调色填充。
/// 不用 Fluent 的 ProgressBar —— 它的模板在宿主与 headless 下长得不一样,而这里只要两块圆角矩形。
/// </summary>
public sealed class GridFsBar : Control
{
    /// <summary>进度(0–1)。</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<GridFsBar, double>(nameof(Value));

    /// <summary>轨道画刷。</summary>
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<GridFsBar, IBrush?>(nameof(Track));

    /// <summary>填充画刷。</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<GridFsBar, IBrush?>(nameof(Fill));

    static GridFsBar()
    {
        AffectsRender<GridFsBar>(ValueProperty, TrackProperty, FillProperty);
    }

    /// <summary>进度。</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>轨道画刷。</summary>
    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>填充画刷。</summary>
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        double radius = bounds.Height / 2;
        if (Track is { } track)
        {
            context.DrawRectangle(track, null, bounds, radius, radius);
        }
        double width = bounds.Width * Math.Clamp(Value, 0, 1);
        if (Fill is { } fill && width > 0)
        {
            context.DrawRectangle(fill, null, new Rect(0, 0, Math.Max(width, bounds.Height), bounds.Height), radius, radius);
        }
    }
}
