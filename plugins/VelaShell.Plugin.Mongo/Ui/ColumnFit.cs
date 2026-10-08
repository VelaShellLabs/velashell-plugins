using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 列宽"双击分隔线按内容自适应"的公共零件:量控件不受限时的自然宽度、把列头拖把上的双击接出来。
/// </summary>
internal static class ColumnFit
{
    /// <summary>控件不受宽度限制时想要多宽;量完让它下一轮按真实约束重排。</summary>
    public static double Natural(Control control)
    {
        control.Measure(Size.Infinity);
        double width = control.DesiredSize.Width;
        control.InvalidateMeasure();
        return width;
    }

    /// <summary>
    /// 一组格子(<see cref="Border" />,内容是它的 <see cref="Decorator.Child" />)里最宽的那格要多宽:
    /// 内容自然宽度 + 内边距 + 边框。只量已经实现的格子 —— 虚拟化列表里看不见的行本来就没有控件。
    /// </summary>
    public static double Widest(IEnumerable<Border> cells)
    {
        double width = 0;
        foreach (Border cell in cells)
        {
            if (cell.Child is { } child)
            {
                double content = child is Panel panel
                    ? panel.Children.Where(static c => c.IsVisible && c is not Thumb).Select(Natural).DefaultIfEmpty(0).Max()
                    : Natural(child);
                width = Math.Max(width, content + cell.Padding.Left + cell.Padding.Right + cell.BorderThickness.Left + cell.BorderThickness.Right);
            }
        }
        return width;
    }

    /// <summary>
    /// 在 <paramref name="host" /> 里的任何一个列头拖把(<see cref="Thumb" />)上双击时回调,参数是拖把的数据上下文(那一列)。
    /// <see cref="Thumb" /> 自己会吃掉按下事件,所以挂在隧道阶段先看点击次数。
    /// </summary>
    public static void OnGripDoubleClick(Control host, Action<object?> fit) =>
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.ClickCount == 2 && e.Source is Visual source && source.FindAncestorOfType<Thumb>(includeSelf: true) is { } grip)
            {
                e.Handled = true;
                fit(grip.DataContext);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
}
