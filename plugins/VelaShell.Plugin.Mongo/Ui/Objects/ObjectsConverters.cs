using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 对象列表 / 新建集合两块界面自用的转换器(共享的 <see cref="Converters" /> 里没有的那几个)。
/// </summary>
public static class ObjectsConverters
{
    /// <summary>
    /// 令牌名 → 12% 淡底画刷。详情面板头的 32px 图标方块、大图标视图的图标底 —— 颜色随对象种类变,
    /// 所以不能像 <c>{ui:Dim VelaInfo, 0.12}</c> 那样在 XAML 里写死令牌。
    /// </summary>
    public static IValueConverter DimToken { get; } =
        new FuncValueConverter<string?, IBrush>(static token => ThemeBrushes.GetDim(token ?? "VelaTextTertiary", 0.12));
}
