using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Specialized;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 让一份滚动内容跟着集合的新条目往下走。
/// <para>
/// 规矩只有一条:<b>用户自己滚上去看东西的时候不许抢</b>。所以"跟不跟"不是一个设置,
/// 而是从用户最近一次滚动推出来的 —— 停在底部就继续跟,往上翻了就停住,
/// 再滚回底部又接着跟。日志窗口都是这个约定,只是通常没人把它写下来。
/// </para>
/// <para>
/// 判断"是不是用户滚的"看 <see cref="ScrollChangedEventArgs" />:内容变长会带来
/// <c>ExtentDelta</c>,而用户拖滚动条只有 <c>OffsetDelta</c>。把两者分开,
/// 才不会把"新日志把视口顶上去"误当成"用户翻上去了"。
/// </para>
/// <para>
/// 挂在哪:可以是 <see cref="ScrollViewer" /> 自己,也可以是**里面**有一个滚动条的控件
/// (虚拟化的列表就是这种 —— 滚动条在它自己的模板里,外面套不得)。
/// </para>
/// </summary>
public static class AutoScroll
{
    private static readonly Dictionary<Control, Watcher> Watchers = [];

    /// <summary>要跟的集合。绑上去就开始跟。</summary>
    public static readonly AttachedProperty<INotifyCollectionChanged?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Control, INotifyCollectionChanged?>("Source", typeof(AutoScroll));

    static AutoScroll() => SourceProperty.Changed.AddClassHandler<Control>(OnSourceChanged);

    /// <summary>读 <see cref="SourceProperty" />。</summary>
    public static INotifyCollectionChanged? GetSource(Control control) => control.GetValue(SourceProperty);

    /// <summary>写 <see cref="SourceProperty" />。</summary>
    public static void SetSource(Control control, INotifyCollectionChanged? value) =>
        control.SetValue(SourceProperty, value);

    private static void OnSourceChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        if (Watchers.Remove(control, out var previous))
        {
            previous.Detach();
        }
        if (e.NewValue is INotifyCollectionChanged source)
        {
            Watchers[control] = new(control, source);
        }
    }

    /// <summary>一个控件上的一次"跟随"。状态是每个控件各自的,所以做成实例而不是静态表。</summary>
    private sealed class Watcher
    {
        private readonly Control _control;
        private readonly INotifyCollectionChanged _source;
        private ScrollViewer? _scroll;
        // 一接上就是"在底部":此时还没有内容,用户也还没表达过任何意图。
        private bool _sticky = true;

        internal Watcher(Control control, INotifyCollectionChanged source)
        {
            _control = control;
            _source = source;
            source.CollectionChanged += OnCollectionChanged;
            control.AttachedToVisualTree += OnAttached;
            Bind();
        }

        internal void Detach()
        {
            _source.CollectionChanged -= OnCollectionChanged;
            _control.AttachedToVisualTree -= OnAttached;
            if (_scroll is { } scroll)
            {
                scroll.ScrollChanged -= OnScrollChanged;
                _scroll = null;
            }
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Bind();

        /// <summary>
        /// 找到那个真正在滚的 ScrollViewer。
        /// <para>
        /// 虚拟化列表的滚动条长在它自己的模板里,模板套上之前找不着 —— 所以这里
        /// <b>每次要用的时候再找</b>,而不是找不到就排一个任务下次再找:
        /// 那个控件可能压根不会显示(收起来的页签),自我重排的任务就成了一个永不结束的循环。
        /// </para>
        /// </summary>
        private ScrollViewer? Bind()
        {
            if (_scroll is not null)
            {
                return _scroll;
            }
            _scroll = _control as ScrollViewer ?? _control.FindDescendantOfType<ScrollViewer>();
            if (_scroll is { } found)
            {
                found.ScrollChanged += OnScrollChanged;
            }
            return _scroll;
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // 清屏(Reset)也要回到底,否则视口会停在一个已经不存在的位置上。
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset && _sticky)
            {
                ScrollLater();
            }
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            if (_scroll is not { } scroll)
            {
                return;
            }
            // 内容变长带来的滚动不算用户意图 —— 只有 Offset 单独动了才是他自己滚的。
            if (e.ExtentDelta.Y != 0 || e.OffsetDelta.Y == 0)
            {
                return;
            }
            // 留一行的余量:滚动条很少停在恰好 0 的位置上。
            _sticky = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y <= 24;
        }

        /// <summary>
        /// 排到布局之后再滚:集合刚变,新的那几行还没测量,这一刻的 Extent 还是旧的。
        /// </summary>
        private void ScrollLater() =>
            Dispatcher.UIThread.Post(() => Bind()?.ScrollToEnd(), DispatcherPriority.Background);
    }
}
