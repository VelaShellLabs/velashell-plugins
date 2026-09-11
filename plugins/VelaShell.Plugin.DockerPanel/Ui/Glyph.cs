using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 一枚描边图标。
/// <para>
/// lucide 的路径是**描边**的(没有填充),用 <c>PathIcon</c> 那种填充渲染会糊成一团黑。
/// 这个控件按 24×24 的视框等比缩放,描边宽度固定在 2(与 lucide 原始设计一致),
/// 于是 13px 与 20px 的图标看起来是同一套线宽。
/// </para>
/// <para>
/// 图标既可以直接给几何(<see cref="Data" />),也可以给资源键(<see cref="Key" />)——
/// 后者让视图模型能用字符串决定画哪一个,而不必在视图里堆一张 DataTrigger 大表。
/// </para>
/// </summary>
public sealed class Glyph : Control
{
    /// <summary>lucide 的设计视框边长。</summary>
    private const double ViewBox = 24;

    /// <summary>图标几何。</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Glyph, Geometry?>(nameof(Data));

    /// <summary>图标的资源键(如 <c>Icon.play</c> / <c>Docker.box</c>)。</summary>
    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<Glyph, string?>(nameof(Key));

    /// <summary>描边画刷。</summary>
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<Glyph, IBrush?>(nameof(Brush));

    /// <summary>边长(逻辑像素),宽高相同。</summary>
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Glyph, double>(nameof(Size), 14d);

    static Glyph()
    {
        AffectsRender<Glyph>(DataProperty, BrushProperty, SizeProperty);
        AffectsMeasure<Glyph>(SizeProperty);
        KeyProperty.Changed.AddClassHandler<Glyph>((glyph, _) => glyph.ResolveKey());
        // 图标是定尺寸的,默认的 Stretch 对它没有意义:排版会把它拉成整行高,
        // 而 Render 从原点开画 —— 于是图标贴在行顶,旁边居中的文字与它错开一截。
        // 这是"图标和文字不在一个水平中轴上"的根因,逐处补 VerticalAlignment 补不干净。
        // 枚举与 Layoutable.VerticalAlignment 属性同名,这里必须写全限定。
        VerticalAlignmentProperty.OverrideDefaultValue<Glyph>(Avalonia.Layout.VerticalAlignment.Center);
    }

    /// <summary>图标几何。</summary>
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>图标的资源键。</summary>
    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>描边画刷。</summary>
    public IBrush? Brush
    {
        get => GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    /// <summary>边长。</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 资源要等控件进了可视树才查得到 —— 在那之前 FindResource 一定落空。
        ResolveKey();
    }

    private void ResolveKey()
    {
        if (Key is not { Length: > 0 } key)
        {
            return;
        }
        if (this.TryFindResource(key, out var resource) && resource is Geometry geometry)
        {
            Data = geometry;
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Data is not { } geometry || Brush is not { } brush)
        {
            return;
        }
        var scale = Size / ViewBox;
        // 在实际分到的框里居中再画。默认对齐下这一步是零位移(框正好是 Size×Size);
        // 但调用方显式写了 Stretch、或父容器给了更大的框时,图标仍然待在正中,
        // 而不是缩在左上角。
        var transform = Matrix.CreateScale(scale, scale) *
                           Matrix.CreateTranslation((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2);
        using (context.PushTransform(transform))
        {
            // 线宽写在 24 的坐标系里,缩放之后自然与 lucide 在各尺寸下的观感一致。
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
