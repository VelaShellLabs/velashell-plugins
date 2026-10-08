using System.Diagnostics;
using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>补全弹层视图。点一项 = 接受;文档链接用系统浏览器打开。</summary>
public sealed partial class CompletionPopupView : UserControl
{
    /// <summary>用户点了一项。</summary>
    public event Action<CompletionItem>? ItemClicked;

    /// <summary>构造。</summary>
    public CompletionPopupView()
    {
        InitializeComponent();
        List.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is CompletionItem item)
            {
                ItemClicked?.Invoke(item);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        DocsLink.Click += (_, _) =>
        {
            if ((DataContext as CompletionSession)?.Selected?.DocsUrl is { Length: > 0 } url)
            {
                try
                {
                    _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                    // 打不开浏览器不是补全的事。
                }
            }
        };
    }

    /// <summary>把选中项滚进视野。</summary>
    public void Reveal(CompletionItem? item)
    {
        if (item is not null)
        {
            List.ScrollIntoView(item);
        }
    }
}
