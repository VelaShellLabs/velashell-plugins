using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 让 <c>MongoChart1..4</c> 这四支图表色在 <see cref="ThemeBrushes" /> 里取到正确的值。
/// <para>
/// <see cref="ThemeBrushes" /> 只查**应用级**资源,而这四支色写在插件自己的 <c>MongoTheme.axaml</c> 里
/// (经视图的 StyleInclude 装进来,宿主的 Application 里没有它们)—— 于是 <see cref="BarChart" />、
/// 图例色块与 <see cref="MonitorBar" /> 拿到的都是各自的兜底色(钢蓝、灰、橙红)。
/// 这里从视图自己的资源链上查出真值,写进 <see cref="ThemeBrushes" /> 那支长期有效的画刷:
/// 那支画刷对象本来就是"就地改色"的设计,所有已经用上它的地方跟着变,不用重绑。
/// </para>
/// <para>
/// 这是外壳层的缺口在本分区里的绕行(根治应在 <see cref="ThemeBrushes" /> 的解析里补上插件资源),
/// 已写进分区报告。
/// </para>
/// </summary>
internal static class MonitorChartColors
{
    private static readonly string[] Tokens = ["MongoChart1", "MongoChart2", "MongoChart3", "MongoChart4"];

    /// <summary>按 <paramref name="host" /> 当前的明暗,把四支图表色同步进共享画刷。</summary>
    /// <param name="host">已挂进可视树、资源链上有 MongoTheme 的控件。</param>
    public static void Sync(Control host)
    {
        foreach (string token in Tokens)
        {
            if (host.TryFindResource(token, host.ActualThemeVariant, out object? value) && value is ISolidColorBrush resolved
                && ThemeBrushes.Get(token, resolved) is SolidColorBrush shared && shared.Color != resolved.Color)
            {
                shared.Color = resolved.Color;
            }
        }
    }
}
