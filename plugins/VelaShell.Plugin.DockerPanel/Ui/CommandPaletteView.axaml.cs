using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 命令面板的视图。
/// <para>
/// 代码后置只做键盘:上下移动、回车执行、Tab 补全、Esc 关闭。
/// 这四个键必须在**输入框**上处理 —— 用户打开面板之后手就没离开过键盘,
/// 让他为了选一条命令去摸鼠标,这个功能就白做了。
/// </para>
/// </summary>
public sealed partial class CommandPaletteView : UserControl
{
    /// <summary>建视图。</summary>
    public CommandPaletteView()
    {
        AvaloniaXamlLoader.Load(this);
        // 打开即聚焦到输入框:面板是被 Ctrl+K 唤出来的,那一刻用户已经在打字了。
        AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("QueryBox")?.Focus());
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is not CommandPalette palette)
        {
            base.OnKeyDown(e);
            return;
        }
        switch (e.Key)
        {
            case Key.Down:
                palette.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                palette.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                palette.RunSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab:
                palette.Complete();
                e.Handled = true;
                break;
            case Key.Escape:
                palette.CloseCommand.Execute(null);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }
}
