using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 4px 的比例细条(设计稿 14 阶段卡片底部的耗时条):<c>VelaBgActive</c> 轨 + 令牌色填充,两端圆角。
/// 卡片宽度随阶段数变化,用 Border 的固定宽度画不出比例 —— 这里按实际宽度现算。
/// </summary>
public sealed class RatioBar : Control
{
    /// <summary>比例(0–1)。</summary>
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<RatioBar, double>(nameof(Ratio));

    /// <summary>填充色令牌。</summary>
    public static readonly StyledProperty<string?> TokenProperty =
        AvaloniaProperty.Register<RatioBar, string?>(nameof(Token), "MongoChart1");

    static RatioBar()
    {
        AffectsRender<RatioBar>(RatioProperty, TokenProperty);
        HeightProperty.OverrideDefaultValue<RatioBar>(4);
    }

    /// <summary>比例。</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    /// <summary>填充色令牌。</summary>
    public string? Token
    {
        get => GetValue(TokenProperty);
        set => SetValue(TokenProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double h = Bounds.Height;
        double radius = h / 2;
        context.DrawRectangle(ThemeBrushes.Get("VelaBgActive", Brushes.Gray), null, new Rect(0, 0, Bounds.Width, h), radius, radius);
        double w = Math.Clamp(Ratio, 0, 1) * Bounds.Width;
        if (w > 0)
        {
            context.DrawRectangle(ThemeBrushes.Get(Token ?? "MongoChart1", Brushes.SteelBlue), null, new Rect(0, 0, Math.Max(w, h), h), radius, radius);
        }
    }
}

/// <summary>
/// 阶段流的排版(设计稿 14):卡片与箭头交替,卡片等宽分掉剩余宽度、箭头固定 40,全部垂直居中。
/// 阶段多到每张卡不足 <see cref="MinCardWidth" /> 时按最小宽度排开(外面套横向滚动)。
/// <para>
/// 放在横向可滚的 <see cref="ScrollViewer" /> 里时,测量拿到的可用宽度是无穷大 —— 那样卡片就没法"等分剩余宽度"。
/// 这里改用滚动视口的宽度(减去 <see cref="ReservedWidth" />)来分,视口变了再重新测量。
/// </para>
/// </summary>
public sealed class ExplainFlowPanel : Panel
{
    /// <summary>卡片最小宽度。</summary>
    public const double MinCardWidth = 150;

    /// <summary>箭头宽度。</summary>
    public const double ArrowWidth = 40;

    /// <summary>视口里要让出来的宽度(外层的左右边距)。</summary>
    public static readonly StyledProperty<double> ReservedWidthProperty =
        AvaloniaProperty.Register<ExplainFlowPanel, double>(nameof(ReservedWidth));

    private ScrollViewer? _scroller;

    /// <summary>视口里要让出来的宽度。</summary>
    public double ReservedWidth
    {
        get => GetValue(ReservedWidthProperty);
        set => SetValue(ReservedWidthProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null)
        {
            _scroller.PropertyChanged += OnScrollerChanged;
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroller is not null)
        {
            _scroller.PropertyChanged -= OnScrollerChanged;
        }
        _scroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty)
        {
            InvalidateMeasure();
        }
    }

    private double CardWidth(double available)
    {
        int cards = (Children.Count + 1) / 2;
        int arrows = Children.Count / 2;
        if (cards == 0)
        {
            return 0;
        }
        if (double.IsInfinity(available))
        {
            double viewport = _scroller?.Viewport.Width ?? 0;
            available = viewport > 0 ? viewport - ReservedWidth : cards * 220 + arrows * ArrowWidth;
        }
        return Math.Max(MinCardWidth, (available - arrows * ArrowWidth) / cards);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        double card = CardWidth(availableSize.Width);
        double height = 0;
        double width = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            double w = i % 2 == 0 ? card : ArrowWidth;
            Children[i].Measure(new Size(w, double.PositiveInfinity));
            height = Math.Max(height, Children[i].DesiredSize.Height);
            width += w;
        }
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        double card = CardWidth(finalSize.Width);
        double x = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            double w = i % 2 == 0 ? card : ArrowWidth;
            Control child = Children[i];
            double h = child.DesiredSize.Height;
            child.Arrange(new Rect(x, (finalSize.Height - h) / 2, w, h));
            x += w;
        }
        return finalSize;
    }
}
