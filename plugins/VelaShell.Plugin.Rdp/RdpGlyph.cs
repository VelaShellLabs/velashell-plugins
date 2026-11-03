using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// A stroked Lucide icon drawn the way the host's <c>LucideIcon</c> draws it: the 24×24 view box is
/// scaled to the control's size and the 2-unit pen scales with it, so a 10px glyph here has the same
/// line weight as a 10px glyph anywhere else in the app. A <c>Path</c> with <c>Stretch</c> would
/// instead blow the glyph's own bounds up to the full size and keep a fixed pen — visibly bigger and
/// bolder than the host's icons.
/// </summary>
internal sealed class RdpGlyph : Control
{
    private const double ViewBox = 24;

    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<RdpGlyph, Geometry?>(nameof(Data));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<RdpGlyph, IBrush?>(nameof(Foreground));

    static RdpGlyph()
    {
        AffectsRender<RdpGlyph>(DataProperty, ForegroundProperty);
    }

    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (Data is not { } geometry || Foreground is not { } brush || w <= 0 || h <= 0) { return; }
        double scale = Math.Min(w, h) / ViewBox;
        var offset = Matrix.CreateTranslation((w - ViewBox * scale) / 2, (h - ViewBox * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * offset))
        {
            context.DrawGeometry(null, new Pen(brush, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }
}
