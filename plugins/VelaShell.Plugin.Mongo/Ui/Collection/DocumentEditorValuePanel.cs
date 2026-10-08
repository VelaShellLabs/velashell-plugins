using Avalonia;
using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 字符串值那一格的布局:没有行尾提示时输入框撑满;有提示(<c>原值 "paid" ↺ 还原</c>、违规说明)时
/// 输入框收到"内容宽度,至少 160",提示紧跟其后 —— 与设计稿 status 那一行一致。
/// <para>
/// 换布局而不换控件:状态是在第一次按键之后才从"未改"变成"已修改"的,
/// 若用两套控件切换,正在输入的那个框会被销毁重建、焦点丢掉。
/// </para>
/// </summary>
public sealed class DocumentEditorValuePanel : Panel
{
    private const double Spacing = 6;
    private const double CompactWidth = 160;
    private double _editorWidth;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        Control? editor = Children.Count > 0 ? Children[0] : null;
        Control? hint = Children.Count > 1 ? Children[1] : null;
        double height = availableSize.Height;
        double hintWidth = 0;
        if (hint is { IsVisible: true })
        {
            hint.Measure(new Size(double.PositiveInfinity, height));
            hintWidth = hint.DesiredSize.Width;
        }
        // 提示最多占一半:原值很长时它自己截断,不能把输入框挤没。
        if (!double.IsInfinity(availableSize.Width))
        {
            hintWidth = Math.Min(hintWidth, availableSize.Width * 0.5);
        }
        double room = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - (hintWidth > 0 ? hintWidth + Spacing : 0));
        if (editor is null)
        {
            _editorWidth = 0;
        }
        else if (hintWidth > 0)
        {
            // 先按内容量一次,再收进 [160, 剩余宽度]。
            editor.Measure(new Size(double.PositiveInfinity, height));
            double wanted = Math.Max(CompactWidth, editor.DesiredSize.Width + 2);
            _editorWidth = double.IsInfinity(room) ? wanted : Math.Min(wanted, room);
        }
        else
        {
            _editorWidth = double.IsInfinity(room) ? CompactWidth : room;
        }
        editor?.Measure(new Size(_editorWidth, height));
        double desiredHeight = Math.Max(editor?.DesiredSize.Height ?? 0, hint?.DesiredSize.Height ?? 0);
        double desiredWidth = double.IsInfinity(availableSize.Width)
            ? _editorWidth + (hintWidth > 0 ? hintWidth + Spacing : 0)
            : availableSize.Width;
        return new Size(desiredWidth, desiredHeight);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        Control? editor = Children.Count > 0 ? Children[0] : null;
        Control? hint = Children.Count > 1 ? Children[1] : null;
        double editorWidth = Math.Min(_editorWidth, finalSize.Width);
        editor?.Arrange(new Rect(0, 0, editorWidth, finalSize.Height));
        if (hint is { IsVisible: true })
        {
            double x = Math.Min(editorWidth + Spacing, finalSize.Width);
            hint.Arrange(new Rect(x, 0, Math.Max(0, Math.Min(hint.DesiredSize.Width, finalSize.Width - x)), finalSize.Height));
        }
        return finalSize;
    }
}
