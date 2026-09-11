using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 转换器取色的唯一出口:按令牌名给一支**长期有效**的画刷,宿主换肤时就地改色。
///
/// <para>
/// <b>要解决的问题。</b>转换器是绑定求值时才跑的,而绑定只在**它自己的源值**变化时重算。
/// 主题不是绑定源 —— 所以换肤时转换器根本不会被再调一次。
/// 从前这里直接把宿主资源里的画刷对象返回出去,而宿主换主题是**整格替换**
/// (<c>Application.Resources.ThemeDictionaries</c> 里换一个新的字典,里面是一批新造的
/// <c>SolidColorBrush</c>),于是上一次返回出去的那个对象还在界面上挂着、颜色还是旧主题的。
/// 表现是:切完主题,列表里的状态点、CPU 数字、危险按钮的描边全停在上一套配色上,
/// 直到那一行的数据碰巧变了才跟上来。
/// </para>
///
/// <para>
/// <b>为什么不是"换肤时让绑定重算"。</b>那需要把主题做成绑定源(每个转换器后面挂一个
/// 通知属性),十几处调用点全要改,而且每次换肤都要惊动整棵树重新求值。
/// 这里反过来:**画刷对象本身不换**,只改它的 <c>Color</c>。
/// <c>SolidColorBrush.Color</c> 是 styled property,改它会自动让所有用到这支画刷的地方重绘 ——
/// 绑定一次都不用重算,调用点一行都不用改。
/// </para>
///
/// <para>
/// <b>为什么要 Attach/Detach。</b>订阅 <c>Application.Current</c> 的事件,是从**宿主**的对象
/// 指向**插件**静态方法的一条引用。插件停用后 ALC 要能回收,这条引用就必须撤掉 ——
/// 不撤的话整个插件程序集被钉在内存里,而且下次加载会有两份。
/// 生命周期挂在插件的激活/停用上,见 <c>DockerPanelPlugin</c>。
/// </para>
///
/// <para>
/// 本面板是 <c>inProcess</c> 的(清单里钉死),所以直接认宿主应用的资源变更就够了 ——
/// 它覆盖全部三种换肤:换具名主题、"跟随系统"下系统明暗翻转、用户改强调色。
/// 隔离模式的插件拿不到宿主的 <c>Application</c>,那边要认 <c>ctx.Theme.Changed</c>。
/// </para>
/// </summary>
internal static class ThemeBrushes
{
    /// <summary>令牌名 → 那支长期有效的画刷。只在 UI 线程上读写(转换器与资源事件都在 UI 线程)。</summary>
    private static readonly Dictionary<string, SolidColorBrush> Tracked = new(StringComparer.Ordinal);

    private static EventHandler<ResourcesChangedEventArgs>? _hook;

    /// <summary>开始跟随宿主换肤。插件激活时调用一次;重复调用无副作用。</summary>
    public static void Attach()
    {
        if (_hook is not null || Application.Current is not { } app)
        {
            return;
        }
        _hook = (_, _) => Refresh();
        app.ResourcesChanged += _hook;
    }

    /// <summary>停止跟随并清空缓存。插件停用时调用 —— 不调就是把插件程序集钉在内存里。</summary>
    public static void Detach()
    {
        if (_hook is { } hook && Application.Current is { } app)
        {
            app.ResourcesChanged -= hook;
        }
        _hook = null;
        Tracked.Clear();
    }

    /// <summary>
    /// 取令牌对应的画刷。同一个令牌**永远返回同一个实例** —— 这是整套机制成立的前提:
    /// 早先绑上去的那些地方,靠的就是手里这个实例会被就地改色。
    /// </summary>
    /// <param name="token">宿主令牌名,如 <c>VelaAccent</c>。</param>
    /// <param name="fallback">令牌解析不到时的兜底色(宿主资源还没就位,或令牌名写错了)。</param>
    public static IBrush Get(string token, IBrush fallback)
    {
        if (Tracked.TryGetValue(token, out SolidColorBrush? tracked))
        {
            return tracked;
        }
        // 解析不到就用兜底色起头,但**照样登记**:资源可能只是还没挂上(控件尚未进可视树),
        // 下一次 Refresh 会把它补正。不登记的话它就永远是兜底灰了。
        Color initial = Resolve(token) ?? (fallback as ISolidColorBrush)?.Color ?? Colors.Gray;
        var brush = new SolidColorBrush(initial);
        Tracked[token] = brush;
        return brush;
    }

    /// <summary>宿主资源变了:把每一支跟踪中的画刷重新解析一遍。</summary>
    private static void Refresh()
    {
        // 资源变更可能来自任意线程的写入;改 styled property 必须回 UI 线程。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }
        foreach ((string token, SolidColorBrush brush) in Tracked)
        {
            if (Resolve(token) is { } color && brush.Color != color)
            {
                brush.Color = color;
            }
        }
    }

    /// <summary>
    /// 按**当前主题变体**解析一个令牌。
    /// <para>
    /// 必须带上 <c>ActualThemeVariant</c>:宿主的令牌分两族 —— <c>VelaError</c> /
    /// <c>VelaWarning</c> / <c>VelaShell*</c> 写在整份主题文件里,而 <c>VelaAccent</c> /
    /// <c>VelaStatusConnected</c> / <c>VelaGauge*</c> / <c>VelaText*</c> 写在
    /// <c>ThemeDictionaries</c> 的 Dark / Light 分支下。不带主题的那个重载按
    /// <c>ThemeVariant.Default</c> 查,后一族一个都查不到,于是**恰好半套颜色**静默回落成灰。
    /// </para>
    /// </summary>
    private static Color? Resolve(string token) =>
        Application.Current is { } app
        && app.TryFindResource(token, app.ActualThemeVariant, out object? value)
        && value is ISolidColorBrush brush
            ? brush.Color
            : null;
}
