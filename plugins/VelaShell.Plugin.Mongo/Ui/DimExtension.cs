using Avalonia.Markup.Xaml;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// <c>{ui:Dim VelaWarning, 0.12}</c>:宿主令牌按固定透明度淡化后的画刷(见 <see cref="ThemeBrushes.GetDim" />)。
/// 设计稿的已修改单元格、TTL 徽章、警告条都是"语义色 + 12% 淡底"这一种写法。
/// </summary>
public sealed class DimExtension : MarkupExtension
{
    /// <summary>无参构造(XAML 用)。</summary>
    public DimExtension()
    {
    }

    /// <summary>构造。</summary>
    /// <param name="token">宿主令牌名。</param>
    /// <param name="opacity">不透明度。</param>
    public DimExtension(string token, double opacity)
    {
        Token = token;
        Opacity = opacity;
    }

    /// <summary>宿主令牌名。</summary>
    public string Token { get; set; } = "VelaAccent";

    /// <summary>不透明度(默认 0.12,与设计稿的 *Dim 一致)。</summary>
    public double Opacity { get; set; } = 0.12;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider) => ThemeBrushes.GetDim(Token, Opacity);
}
