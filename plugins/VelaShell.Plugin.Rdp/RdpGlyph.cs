using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// A stroked Lucide icon drawn the way the host's <c>LucideIcon</c> draws it: the 24×24 view box is
/// scaled to the control's size and the 2-unit pen scales with it, so a 10px glyph here has the same
/// line weight as a 10px glyph anywhere else in the app. A <c>Path</c> with <c>Stretch</c> would
/// instead blow the glyph's own bounds up to the full size and keep a fixed pen — visibly bigger and
/// bolder than the host's icons.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately touches none of Avalonia's process-wide state: no <c>AvaloniaProperty.Register</c>, no
/// <c>AddOwner</c>, no <c>AffectsRender</c> or class handler. Avalonia is shared with the host, and its global
/// property registry keeps every such registration — and through the owner type the plugin's whole collectible
/// load context — alive after the plugin is unloaded: each deactivation left the plugin's assemblies (and, on
/// Windows, a cached PDB handle that blocks updating the plugin) in memory until VelaShell restarted.
/// </para>
/// <para>
/// So the colour is not a styled property. The glyph is told two host tokens (<see cref="RestToken"/>,
/// <see cref="HoverToken"/>), watches its own button's <c>:pointerover</c> pseudo-class through an instance event, and resolves
/// the brush when it renders — which also follows theme and accent changes.
/// </para>
/// </remarks>
internal sealed class RdpGlyph : Control
{
    private const double ViewBox = 24;
    private Button? _button;

    public RdpGlyph()
    {
        ResourcesChanged += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The 24×24 Lucide geometry to stroke.</summary>
    public Geometry? Data
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Host brush token for the glyph at rest (e.g. <c>VelaTextPrimary</c>).</summary>
    public string? RestToken
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>Host brush token while the pointer is over the enclosing button; falls back to <see cref="RestToken"/>.</summary>
    public string? HoverToken
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    /// <summary>The brush the glyph draws with right now (null when no token resolves).</summary>
    internal IBrush? Foreground
    {
        get
        {
            string? token = _button?.Classes.Contains(":pointerover") == true ? HoverToken ?? RestToken : RestToken;
            return token is not null && this.TryFindResource(token, ActualThemeVariant, out object? value) && value is IBrush brush
                ? brush
                : null;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _button = this.FindAncestorOfType<Button>();
        _button?.Classes.CollectionChanged += OnButtonClassesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _button?.Classes.CollectionChanged -= OnButtonClassesChanged;
        _button = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The button's <c>:pointerover</c> pseudo-class — the same state the old hover style selected on.</summary>
    private void OnButtonClassesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => InvalidateVisual();

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
