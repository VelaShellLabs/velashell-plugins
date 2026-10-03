using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一根分段比例条:轨道 + 按比例首尾相接的若干段(设计稿 08 的"类型与出现率"、取值比例条,
/// 16 的通过 / 不通过,统计页的索引大小)。
/// <para>
/// 自绘而不是在 XAML 里拼 Rectangle:段宽取决于控件自己的实际宽度,而这几处的宽度都随列宽伸缩;
/// 在视图模型里预先算像素宽就得知道布局结果,反过来了。颜色走 <see cref="ThemeBrushes" />,
/// 那里给的是长期有效、换肤时就地改色的画刷。
/// </para>
/// </summary>
public sealed class DesignBar : Control
{
    /// <summary>各段(比例之和不必为 1;剩下的露出轨道)。</summary>
    public static readonly StyledProperty<IEnumerable<object>?> SegmentsProperty =
        AvaloniaProperty.Register<DesignBar, IEnumerable<object>?>(nameof(Segments));

    /// <summary>轨道颜色令牌;空 = 不画轨道。</summary>
    public static readonly StyledProperty<string?> TrackTokenProperty =
        AvaloniaProperty.Register<DesignBar, string?>(nameof(TrackToken), "VelaBgActive");

    /// <summary>段与段之间的空隙(像素)。</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<DesignBar, double>(nameof(Gap));

    /// <summary>圆角。</summary>
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<DesignBar, double>(nameof(Radius), 2);

    static DesignBar()
    {
        AffectsRender<DesignBar>(SegmentsProperty, TrackTokenProperty, GapProperty, RadiusProperty);
    }

    /// <summary>各段。</summary>
    public IEnumerable<object>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>轨道颜色令牌。</summary>
    public string? TrackToken
    {
        get => GetValue(TrackTokenProperty);
        set => SetValue(TrackTokenProperty, value);
    }

    /// <summary>段间空隙。</summary>
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
        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }
        double radius = Math.Min(Radius, bounds.Height / 2);
        if (TrackToken is { Length: > 0 } track)
        {
            context.DrawRectangle(ThemeBrushes.Get(track, Brushes.DimGray), null, bounds, radius, radius);
        }
        var pieces = (Segments ?? []).OfType<BarPiece>().Where(static p => p.Ratio > 0).ToList();
        if (pieces.Count == 0)
        {
            return;
        }
        double gaps = Gap * (pieces.Count - 1);
        double usable = Math.Max(0, bounds.Width - gaps);
        double x = 0;
        // 分段条整体是一个圆角形状:先按圆角裁一次,段与段之间就是直边 —— 与设计稿的类型条一致。
        using (context.PushClip(new RoundedRect(bounds, radius)))
        {
            foreach (BarPiece piece in pieces)
            {
                double width = Math.Max(1, usable * Math.Clamp(piece.Ratio, 0, 1));
                width = Math.Min(width, bounds.Width - x);
                if (width <= 0)
                {
                    break;
                }
                context.FillRectangle(ThemeBrushes.Get(piece.Token, Brushes.SteelBlue), new Rect(x, 0, width, bounds.Height));
                x += width + Gap;
            }
        }
    }
}
