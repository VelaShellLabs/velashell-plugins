using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 代码里现建的右键菜单共用的一项:图标 + 文字 + 命令。
/// </summary>
/// <remarks>
/// 普通项**不碰** <see cref="Avalonia.Controls.Primitives.TemplatedControl.Foreground" />:哪怕写一个 <see langword="null" />,
/// 本地值也会压过宿主 <c>ContextMenu MenuItem</c> 样式给的前景色,文字就成了透明的,
/// 只有指针悬停时 Fluent 模板改了内部 presenter 的颜色才露出来。
/// 危险项直接设本地前景色:弹出层是独立的顶层,本插件的样式类够不着它。
/// </remarks>
internal static class MenuKit
{
    /// <summary>一项绑命令的菜单。</summary>
    /// <param name="label">文字。</param>
    /// <param name="icon">图标键。</param>
    /// <param name="command">命令。</param>
    /// <param name="parameter">命令参数。</param>
    /// <param name="danger">危险操作(红字红图标)。</param>
    public static MenuItem Command(string label, string icon, ICommand command, object? parameter = null, bool danger = false)
    {
        var item = new MenuItem
        {
            Header = label,
            Icon = new Glyph { Key = icon, Size = 13, Brush = ThemeBrushes.Get(danger ? "VelaError" : "VelaTextTertiary", Brushes.Gray) },
            Command = command,
            CommandParameter = parameter
        };
        if (danger)
        {
            item.Foreground = ThemeBrushes.Get("VelaError", Brushes.Red);
        }
        return item;
    }

    /// <summary>在 <paramref name="items" /> 末尾补一条分隔线(开头与连续两条都跳过)。</summary>
    public static void Separator(List<Control> items)
    {
        if (items.Count > 0 && items[^1] is not Avalonia.Controls.Separator)
        {
            items.Add(new Avalonia.Controls.Separator());
        }
    }
}
