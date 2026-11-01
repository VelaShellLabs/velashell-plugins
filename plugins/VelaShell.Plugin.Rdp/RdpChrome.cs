using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;

namespace VelaShell.Plugin.Rdp;

/// <summary>Small styling helpers shared by the RDP view's self-drawn controls.</summary>
internal static class RdpChrome
{
    /// <summary>A style that binds <paramref name="property"/> to the host token <paramref name="token"/>.</summary>
    internal static Style TokenStyle(Func<Selector?, Selector> selector, AvaloniaProperty property, string token) =>
        new(selector) { Setters = { new Setter(property, new DynamicResourceExtension(token)) } };

    /// <summary>
    /// The host's icon-button behaviour on a transparent button holding an <see cref="RdpGlyph"/>: the glyph in
    /// <paramref name="rest"/>, <c>VelaBgHover</c> behind it and the glyph in <paramref name="hover"/> while the
    /// pointer is over or pressed. Styles rather than local values, so the hover state can override the rest state.
    /// </summary>
    internal static void AddIconButtonStyles(Button button, string rest, string hover)
    {
        button.Styles.Add(TokenStyle(x => x.OfType<RdpGlyph>(), RdpGlyph.ForegroundProperty, rest));
        button.Styles.Add(TokenStyle(x => x.OfType<Button>().Class(":pointerover").Descendant().OfType<RdpGlyph>(), RdpGlyph.ForegroundProperty, hover));
        foreach (string state in new[] { ":pointerover", ":pressed" })
        {
            button.Styles.Add(TokenStyle(x => x.OfType<Button>().Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
                ContentPresenter.BackgroundProperty, "VelaBgHover"));
        }
    }
}
