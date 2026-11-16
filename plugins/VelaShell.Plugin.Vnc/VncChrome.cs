using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;

namespace VelaShell.Plugin.Vnc;

/// <summary>Small styling helpers shared by the VNC view's self-drawn controls.</summary>
internal static class VncChrome
{
    /// <summary>A style that binds <paramref name="property"/> to the host token <paramref name="token"/>.</summary>
    internal static Style TokenStyle(Func<Selector?, Selector> selector, AvaloniaProperty property, string token) =>
        new(selector) { Setters = { new Setter(property, new DynamicResourceExtension(token)) } };

    /// <summary>
    /// The host's icon-button behaviour on a transparent button holding an <see cref="VncGlyph"/>: the glyph in
    /// <paramref name="rest"/>, <paramref name="hoverFill"/> (default <c>VelaBgHover</c>) behind it and the glyph in <paramref name="hover"/> while the
    /// pointer is over it. The fill is a style; the glyph colour is a pair of tokens the glyph resolves itself (it
    /// deliberately has no styled property — see <see cref="VncGlyph"/>). Call after setting the button's content.
    /// </summary>
    internal static void AddIconButtonStyles(Button button, string rest, string hover, string hoverFill = "VelaBgHover")
    {
        foreach (VncGlyph glyph in Glyphs(button.Content))
        {
            glyph.RestToken = rest;
            glyph.HoverToken = hover;
        }
        foreach (string state in new[] { ":pointerover", ":pressed" })
        {
            button.Styles.Add(TokenStyle(x => x.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, hoverFill));
        }
    }

    /// <summary>The glyphs in a button's content: the content itself, or the children of a panel holding several.</summary>
    private static IEnumerable<VncGlyph> Glyphs(object? content) => content switch
    {
        VncGlyph glyph => [glyph],
        Panel panel => panel.Children.SelectMany(child => Glyphs(child)),
        _ => []
    };
}
