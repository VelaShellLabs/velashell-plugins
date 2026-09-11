using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Globalization;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 列头上的列宽拖拽。四张列表(容器 / 镜像 / 卷 / 网络)共用这一份。
/// <para>
/// 做法与宿主的文件浏览器一致:每列右边一条 <see cref="ListColumns.TrackWidth" /> 宽的轨道,
/// 轨道的 <c>Tag</c> 说明它改的是哪一列;按下时**把当前全部列宽快照下来**,
/// 位移一律按"当前指针 − 按下时指针"算,而不是一帧一帧累加 ——
/// 累加会在列宽被夹住(碰到上下限)之后跑偏,指针和界线就再也对不上了。
/// </para>
/// <para>
/// 指针捕获在**视图**上而不是那条 6px 的窄轨道上:拖出轨道之外仍然收得到移动。
/// 移动与抬起因此挂在视图根上(隧道式),而不是挂在轨道自己身上。
/// </para>
/// </summary>
public sealed class ColumnResizer
{
    private readonly Control _host;
    private readonly Grid _header;
    private readonly double _chrome;
    private readonly Dictionary<string, double> _startWidths = [];
    private string? _active;
    private double _startX;

    /// <summary>接上一张列表的列头。</summary>
    /// <param name="host">视图根(指针捕获在它身上)。</param>
    /// <param name="headerName">列头那张 Grid 的名字。</param>
    /// <param name="chrome">列宽之外那些**不可拖**的固定占位之和(勾选框、状态色条、行尾动作…)。</param>
    public ColumnResizer(Control host, string headerName, double chrome)
    {
        _host = host;
        _header = host.GetControl<Grid>(headerName);
        _chrome = chrome;
        // 轨道在 XAML 里只写 Classes 与 Tag,事件在这里挂 —— 四个页面就不必各抄一遍转发方法。
        foreach (var grip in _header.Children.OfType<Border>().Where(b => b.Tag is string))
        {
            grip.PointerPressed += OnPressed;
            grip.PointerReleased += OnReleased;
        }
        host.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
    }

    private ListColumns? Columns => (_host.DataContext as PageViewModel)?.ColumnLayout;

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string key } grip || Columns is not { } columns)
        {
            return;
        }
        // 双击 = 按内容自适应,与宿主文件浏览器一致。
        if (e.ClickCount >= 2)
        {
            AutoFit(columns, key, grip);
            e.Handled = true;
            return;
        }
        _active = key;
        _startX = e.GetPosition(_host).X;
        _startWidths.Clear();
        foreach (var other in columns.Keys)
        {
            _startWidths[other] = columns.Get(other);
        }
        e.Pointer.Capture(_host);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_active is not { } key || Columns is not { } columns ||
            !_startWidths.TryGetValue(key, out var start))
        {
            return;
        }
        var delta = e.GetPosition(_host).X - _startX;
        columns.Set(key, Math.Clamp(start + delta, columns.Min(key), Max(columns, key)));
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_active is null)
        {
            return;
        }
        _active = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// 一列最宽能到哪:剩下那些列、每条轨道与行首行尾的固定占位都得留出来,
    /// 否则一拖就把行尾那几颗动作按钮挤出可视区。
    /// </summary>
    private double Max(ListColumns columns, string key)
    {
        var others = columns.Keys.Where(k => k != key).Sum(columns.Get);
        var available = _header.Bounds.Width > 0 ? _header.Bounds.Width : _host.Bounds.Width;
        return Math.Max(columns.Min(key),
            available - others - (columns.Keys.Count * ListColumns.TrackWidth) - _chrome);
    }

    /// <summary>
    /// 双击轨道:把这一列收放到"正好装得下当前这些行"。
    /// <para>
    /// 量的是当前**筛出来的**那些行,不是全部 —— 用户看得见的就是这些,
    /// 为了一行被筛掉的超长文本把列撑开,反而看不成。
    /// </para>
    /// </summary>
    private void AutoFit(ListColumns columns, string key, Border grip)
    {
        if (_host.DataContext is not PageViewModel page)
        {
            return;
        }
        var size = Resource(key is "name" or "repo" ? "VelaFontSize12" : "VelaFontSize11") as double? ?? 11;
        Typeface mono = new(Resource("VelaUiMonoFont") as FontFamily ?? FontFamily.Default);
        Typeface ui = new(FontFamily.Default);
        // 表头那几个字也要装得下。文字直接从列头里读,免得把同一串标题在代码里再写一遍。
        var widest = Measure(HeaderText(grip), ui, size);
        foreach (var text in page.ColumnTexts(key))
        {
            widest = Math.Max(widest, Measure(text, key is "name" or "repo" ? ui : mono, size));
        }
        columns.Set(key, Math.Clamp(widest + columns.Padding(key),
            columns.Min(key), Math.Min(columns.MaxAutoFit(key), Max(columns, key))));
    }

    private string HeaderText(Border grip) =>
        _header.Children.OfType<TextBlock>()
            .FirstOrDefault(t => Grid.GetColumn(t) == Grid.GetColumn(grip) - 1)?.Text ?? "";

    private static double Measure(string text, Typeface typeface, double size) =>
        text.Length == 0
            ? 0
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, size, Brushes.Black).Width;

    private object? Resource(string key) =>
        _host.TryFindResource(key, (_host as StyledElement)?.ActualThemeVariant, out var value) ? value : null;
}
