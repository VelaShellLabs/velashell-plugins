using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一根细横条:一段或两段按比例填充(设计稿 11 的复制延迟条、存储 Top 的 数据 + 索引 堆叠条;
/// 15 的"已运行"进度条)。
/// <para>
/// 不用 <c>ProgressBar</c>:它只有一段,而且换色得改模板;两段堆叠在 Grid 里用星号列做的话,
/// 列宽要绑到 <c>ColumnDefinition</c> 上 —— 它不在逻辑树里,拿不到数据上下文。自绘十几行就够。
/// </para>
/// </summary>
public sealed class MonitorBar : Control
{
    /// <summary>第一段比例(0–1)。</summary>
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<MonitorBar, double>(nameof(Ratio));

    /// <summary>第二段比例(0–1,接在第一段之后;0 = 没有第二段)。</summary>
    public static readonly StyledProperty<double> Ratio2Property =
        AvaloniaProperty.Register<MonitorBar, double>(nameof(Ratio2));

    /// <summary>第一段颜色令牌。</summary>
    public static readonly StyledProperty<string> TokenProperty =
        AvaloniaProperty.Register<MonitorBar, string>(nameof(Token), "VelaAccent");

    /// <summary>第二段颜色令牌。</summary>
    public static readonly StyledProperty<string> Token2Property =
        AvaloniaProperty.Register<MonitorBar, string>(nameof(Token2), "MongoChart2");

    /// <summary>底轨颜色令牌;空 = 不画底轨。</summary>
    public static readonly StyledProperty<string?> TrackTokenProperty =
        AvaloniaProperty.Register<MonitorBar, string?>(nameof(TrackToken), "VelaBgActive");

    /// <summary>两段之间的缝(像素)。</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<MonitorBar, double>(nameof(Gap));

    /// <summary>圆角。</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<MonitorBar, double>(nameof(Radius), 2);

    static MonitorBar()
    {
        AffectsRender<MonitorBar>(RatioProperty, Ratio2Property, TokenProperty, Token2Property, TrackTokenProperty, GapProperty, RadiusProperty);
    }

    /// <summary>第一段比例。</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    /// <summary>第二段比例。</summary>
    public double Ratio2
    {
        get => GetValue(Ratio2Property);
        set => SetValue(Ratio2Property, value);
    }

    /// <summary>第一段颜色令牌。</summary>
    public string Token
    {
        get => GetValue(TokenProperty);
        set => SetValue(TokenProperty, value);
    }

    /// <summary>第二段颜色令牌。</summary>
    public string Token2
    {
        get => GetValue(Token2Property);
        set => SetValue(Token2Property, value);
    }

    /// <summary>底轨颜色令牌。</summary>
    public string? TrackToken
    {
        get => GetValue(TrackTokenProperty);
        set => SetValue(TrackTokenProperty, value);
    }

    /// <summary>两段之间的缝。</summary>
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>圆角。</summary>
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        double r = Math.Min(Radius, height / 2);
        if (TrackToken is { Length: > 0 } track)
        {
            context.DrawRectangle(ThemeBrushes.Get(track, Brushes.DimGray), null, new Rect(0, 0, width, height), r, r);
        }
        double first = width * Math.Clamp(Ratio, 0, 1);
        if (first > 0)
        {
            // 比例很小也至少画 2px:"有,但很少"与"没有"在这里是两回事(存储 Top 的索引段)。
            first = Math.Max(first, 2);
            context.DrawRectangle(ThemeBrushes.Get(Token, Brushes.SteelBlue), null, new Rect(0, 0, first, height), r, r);
        }
        double second = width * Math.Clamp(Ratio2, 0, 1);
        if (second > 0)
        {
            second = Math.Max(second, 2);
            double x = first > 0 ? first + Gap : 0;
            context.DrawRectangle(ThemeBrushes.Get(Token2, Brushes.OrangeRed), null,
                new Rect(x, 0, Math.Min(second, Math.Max(0, width - x)), height), r, r);
        }
    }
}
