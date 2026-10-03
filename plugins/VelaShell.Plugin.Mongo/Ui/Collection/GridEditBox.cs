using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 内联编辑用的文本框:一出现就拿焦点并全选。
/// <para>
/// 网格、树、检查器的编辑器都是"按需出现"的(进入编辑才挂上),它们出现那一刻用户的手已经在键盘上了 ——
/// 还得再点一下才能打字,是内联编辑最常见的毛病。挂到可视树上之后再派发一次聚焦,
/// 是因为挂上的那一拍布局还没跑完,立刻 Focus 会被虚拟化面板随后的重排抢走。
/// </para>
/// </summary>
public sealed class GridEditBox : TextBox
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEffectivelyVisible)
            {
                Focus();
                SelectAll();
            }
        }, DispatcherPriority.Input);
    }
}
