using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>图表里的一组数据。</summary>
/// <param name="Name">名字(图例、悬停提示里的那一行)。</param>
/// <param name="BrushToken">颜色令牌(<c>MongoChart1</c>、<c>VelaShellGreen</c>…)。</param>
/// <param name="Values">每根柱子的值。</param>
public sealed record ChartSeries(string Name, string BrushToken, IReadOnlyList<double> Values);

/// <summary>
/// 柱状图:多组数据堆叠成一根柱(设计稿 11 的操作 / 秒、连接数;08 的 Schema 直方图)。
/// <para>
/// 自绘而不是引一个图表库:用到的只有"等宽柱 + 堆叠 + 几条网格线 + 悬停提示"这一种形态,
/// 而任何图表库都会随插件目录分发一大串程序集,配色也得另外接一遍宿主令牌。
/// </para>
/// </summary>
public sealed class BarChart : Control
{
    /// <summary>数据。</summary>
    public static readonly StyledProperty<IReadOnlyList<ChartSeries>?> SeriesProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<ChartSeries>?>(nameof(Series));

    /// <summary>横轴标签(与柱一一对应;只画首、中、尾三个,其余进悬停提示)。</summary>
    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<string>?>(nameof(Labels));

    /// <summary>纵轴上限;0 = 按数据自动取整。</summary>
    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<BarChart, double>(nameof(MaxValue));

    /// <summary>是否画坐标轴(网格线、刻度、横轴标签)。直方图那种小图关掉。</summary>
    public static readonly StyledProperty<bool> ShowAxesProperty =
        AvaloniaProperty.Register<BarChart, bool>(nameof(ShowAxes), true);

    /// <summary>柱间距占柱宽的比例。</summary>
    public static readonly StyledProperty<double> GapRatioProperty =
        AvaloniaProperty.Register<BarChart, double>(nameof(GapRatio), 0.25);

    /// <summary>悬停提示(堆叠图的那张小卡片)。</summary>
    public static readonly StyledProperty<bool> ShowTooltipProperty =
        AvaloniaProperty.Register<BarChart, bool>(nameof(ShowTooltip), true);

    /// <summary>横轴画几个标签(均匀取,含首尾;设计稿 11 是 5 个)。</summary>
    public static readonly StyledProperty<int> XLabelCountProperty =
        AvaloniaProperty.Register<BarChart, int>(nameof(XLabelCount), 5);

    /// <summary>
    /// 至少按这么多根柱分格(0 = 有几根分几格)。时间序列刚开始采样时只有寥寥几根:不设它,每根柱都被拉得和整张图一样宽;
    /// 设成满窗口的根数,柱子从一开始就是最终的宽度,靠右排(最新的贴着「现在」),左边空着等数据填进来。
    /// </summary>
    public static readonly StyledProperty<int> MinSlotsProperty =
        AvaloniaProperty.Register<BarChart, int>(nameof(MinSlots));

    private int _hover = -1;

    static BarChart()
    {
        AffectsRender<BarChart>(SeriesProperty, LabelsProperty, MaxValueProperty, ShowAxesProperty, GapRatioProperty, MinSlotsProperty);
    }

    /// <summary>数据。</summary>
    public IReadOnlyList<ChartSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>横轴标签。</summary>
    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <summary>纵轴上限。</summary>
    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    /// <summary>是否画坐标轴。</summary>
    public bool ShowAxes
    {
        get => GetValue(ShowAxesProperty);
        set => SetValue(ShowAxesProperty, value);
    }

    /// <summary>柱间距比例。</summary>
    public double GapRatio
    {
        get => GetValue(GapRatioProperty);
        set => SetValue(GapRatioProperty, value);
    }

    /// <summary>至少分几格。</summary>
    public int MinSlots
    {
        get => GetValue(MinSlotsProperty);
        set => SetValue(MinSlotsProperty, value);
    }

    /// <summary>横轴标签个数。</summary>
    public int XLabelCount
    {
        get => GetValue(XLabelCountProperty);
        set => SetValue(XLabelCountProperty, value);
    }

    /// <summary>悬停提示。</summary>
    public bool ShowTooltip
    {
        get => GetValue(ShowTooltipProperty);
        set => SetValue(ShowTooltipProperty, value);
    }

    private const double AxisLeft = 30;
    private const double AxisBottom = 16;

    private Rect Plot => ShowAxes
        ? new Rect(AxisLeft, 6, Math.Max(0, Bounds.Width - AxisLeft - 4), Math.Max(0, Bounds.Height - AxisBottom - 6))
        : new Rect(0, 0, Bounds.Width, Bounds.Height);

    private int Count => Series is { Count: > 0 } s ? s.Max(static x => x.Values.Count) : 0;

    /// <summary>分几格(柱数与 <see cref="MinSlots" /> 取大)。</summary>
    private int Slots => Math.Max(Count, Math.Max(0, MinSlots));

    /// <summary>第一根柱前面空着的格数(柱靠右排)。</summary>
    private int Offset => Slots - Count;

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int count = Count;
        Rect plot = Plot;
        double x = e.GetPosition(this).X;
        int index = count == 0 || plot.Width <= 0 ? -1 : (int)Math.Floor((x - plot.X) / (plot.Width / Slots)) - Offset;
        index = index < 0 || index >= count ? -1 : index;
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        // 透明底:让整块区域都接得住指针(悬停提示要)。
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        IReadOnlyList<ChartSeries> series = Series ?? [];
        int count = Count;
        if (count == 0)
        {
            return;
        }
        double[] totals = new double[count];
        for (int i = 0; i < count; i++)
        {
            totals[i] = series.Sum(s => i < s.Values.Count ? Math.Max(0, s.Values[i]) : 0);
        }
        (double max, double step) = MaxValue > 0 ? (MaxValue, NiceStep(MaxValue)) : NiceScale(totals.Max());
        Rect plot = Plot;
        IBrush muted = ThemeBrushes.Get("VelaTextMuted", Brushes.Gray);
        var grid = new Pen(ThemeBrushes.Get("VelaBorderPrimary", Brushes.DimGray), 1);
        var typeface = new Typeface(this.TryFindResource("VelaUiMonoFont", out object? f) && f is FontFamily ff ? ff : FontFamily.Default);

        if (ShowAxes)
        {
            int steps = Math.Clamp((int)Math.Round(max / step), 1, 8);
            for (int g = 0; g <= steps; g++)
            {
                double value = Math.Min(max, step * g);
                double y = plot.Bottom - (plot.Height * value / max);
                context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
                var label = new FormattedText(Short(value), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, muted);
                context.DrawText(label, new Point(plot.Left - label.Width - 6, y - (label.Height / 2)));
            }
        }

        double slot = plot.Width / Slots;
        // 柱靠右排:origin 是第 0 根柱所在格的左边。
        double origin = plot.Left + (slot * Offset);
        double barWidth = Math.Max(1, slot * (1 - Math.Clamp(GapRatio, 0, 0.9)));
        for (int i = 0; i < count; i++)
        {
            double x = origin + (slot * i) + ((slot - barWidth) / 2);
            double bottom = plot.Bottom;
            int top = -1;
            for (int k = series.Count - 1; k >= 0; k--)
            {
                if (i < series[k].Values.Count && series[k].Values[i] > 0)
                {
                    top = k;
                    break;
                }
            }
            for (int k = 0; k < series.Count; k++)
            {
                ChartSeries s = series[k];
                double v = i < s.Values.Count ? Math.Max(0, s.Values[i]) : 0;
                double h = max <= 0 ? 0 : plot.Height * Math.Min(v, max) / max;
                if (h <= 0)
                {
                    continue;
                }
                IBrush fill = ThemeBrushes.Get(s.BrushToken, Brushes.SteelBlue);
                // 只有最上面那一段圆顶(1px):整根柱子是一个形状,中间的接缝不该有圆角。
                if (k == top && h > 2)
                {
                    context.FillRectangle(fill, new Rect(x, bottom - h, barWidth, h), 1);
                    context.FillRectangle(fill, new Rect(x, bottom - Math.Min(h, 2), barWidth, Math.Min(h, 2)));
                }
                else
                {
                    context.FillRectangle(fill, new Rect(x, bottom - h, barWidth, h));
                }
                bottom -= h;
            }
            if (i == _hover)
            {
                context.FillRectangle(ThemeBrushes.GetDim("VelaTextPrimary", 0.08), new Rect(origin + (slot * i), plot.Top, slot, plot.Height));
            }
        }

        if (ShowAxes && Labels is { Count: > 0 } labels)
        {
            int n = Math.Clamp(XLabelCount, 2, labels.Count);
            var placed = new List<(FormattedText Text, double X)>();
            foreach (int i in Enumerable.Range(0, n).Select(k => (int)Math.Round((double)k * (labels.Count - 1) / (n - 1))).Distinct())
            {
                var text = new FormattedText(labels[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 9, muted);
                double cx = origin + (slot * i) + (slot / 2) - (text.Width / 2);
                placed.Add((text, Math.Clamp(cx, plot.Left, Math.Max(plot.Left, plot.Right - text.Width))));
            }
            // 图窄时标签会叠在一起(最后那个还带着「现在」):首尾一定画,中间的挨不下就跳过。
            const double labelGap = 8;
            double right = double.NegativeInfinity;
            double lastLeft = placed.Count > 1 ? placed[^1].X : double.PositiveInfinity;
            for (int k = 0; k < placed.Count; k++)
            {
                (FormattedText text, double x) = placed[k];
                bool last = k == placed.Count - 1;
                if (!last && k > 0 && (x < right + labelGap || x + text.Width > lastLeft - labelGap))
                {
                    continue;
                }
                context.DrawText(text, new Point(x, plot.Bottom + 3));
                right = x + text.Width;
            }
        }

        if (ShowTooltip && _hover >= 0)
        {
            DrawTooltip(context, typeface, series, plot, slot, origin);
        }
    }

    private void DrawTooltip(DrawingContext context, Typeface typeface, IReadOnlyList<ChartSeries> series, Rect plot, double slot, double origin)
    {
        IBrush primary = ThemeBrushes.Get("VelaTextPrimary", Brushes.White);
        IBrush secondary = ThemeBrushes.Get("VelaTextSecondary", Brushes.LightGray);
        var lines = new List<(string Name, string Value, IBrush Brush)>();
        foreach (ChartSeries s in series)
        {
            double v = _hover < s.Values.Count ? s.Values[_hover] : 0;
            lines.Add((s.Name, v.ToString("#,0.##", CultureInfo.InvariantCulture), ThemeBrushes.Get(s.BrushToken, Brushes.SteelBlue)));
        }
        string title = Labels is { } labels && _hover < labels.Count ? labels[_hover] : "";
        const double row = 15;
        double width = 150;
        double height = 10 + (title.Length > 0 ? row : 0) + (lines.Count * row);
        double x = origin + (slot * _hover) + slot + 6;
        if (x + width > Bounds.Width)
        {
            x = origin + (slot * _hover) - width - 6;
        }
        double y = plot.Top + 4;
        var box = new Rect(Math.Max(0, x), y, width, height);
        context.DrawRectangle(ThemeBrushes.Get("VelaBgSurface", Brushes.Black),
            new Pen(ThemeBrushes.Get("VelaBorderSecondary", Brushes.Gray), 1), box, 4, 4);
        double ty = box.Y + 5;
        if (title.Length > 0)
        {
            context.DrawText(new FormattedText(title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, primary), new Point(box.X + 8, ty));
            ty += row;
        }
        foreach ((string name, string value, IBrush brush) in lines)
        {
            context.FillRectangle(brush, new Rect(box.X + 8, ty + 4, 7, 7), 1);
            context.DrawText(new FormattedText(name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, secondary), new Point(box.X + 20, ty));
            var v = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, primary);
            context.DrawText(v, new Point(box.Right - v.Width - 8, ty));
            ty += row;
        }
    }

    /// <summary>纵轴刻度:取 1 / 2 / 2.5 / 5 × 10ⁿ 的步长,使刻度数在 3–5 之间,上限抬到步长的整数倍。</summary>
    internal static (double Max, double Step) NiceScale(double value)
    {
        if (value <= 0)
        {
            return (1, 1d / 3);
        }
        double step = NiceStep(value);
        return (Math.Ceiling(value / step) * step, step);
    }

    /// <summary>给定上限,挑一个让刻度落在整数上的步长。</summary>
    internal static double NiceStep(double max)
    {
        double raw = max / 3;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (double m in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (m * magnitude >= raw)
            {
                return m * magnitude;
            }
        }
        return 10 * magnitude;
    }

    /// <summary>把最大值抬到一个好读的整数(1.5k、200、20)。</summary>
    internal static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double step in new[] { 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
        {
            if (step * magnitude >= value)
            {
                return step * magnitude;
            }
        }
        return 10 * magnitude;
    }

    /// <summary>刻度文字:<c>1.5k</c>、<c>500</c>。</summary>
    internal static string Short(double value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0.#", CultureInfo.InvariantCulture)
    };
}
