using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 查询结果的只读网格(设计稿 03 下半):26px 类型化列头(字段名 + 灰色类型)、28px 行、
/// 行号列、BSON 类型着色、数值右对齐,点列头在客户端排序;列头分隔线可拖宽、双击按内容自适应。
/// 双击对象 / 数组单元格钻进去(Navicat 式),顶上多一条面包屑,Backspace / 点面包屑回去。
/// <para>
/// 自己画而不是 DataGrid:结果的列是运行时才知道的(每次查询都不一样),一千行乘十几列的单元格
/// 做成控件树太重;这里只画视口里那几十行。放在 <see cref="ScrollViewer" /> 里用,列头钉在视口顶上。
/// </para>
/// </summary>
public sealed class ResultGrid : Control
{
    private const double HeaderHeight = 26;
    private const double CrumbHeight = 28;
    private const double RowHeight = 28;
    private const double NumberWidth = 44;
    private const double CellPadding = 8;
    private const double GripHalf = 3;

    /// <summary>数据源(<c>QueryResultSet</c>)。</summary>
    public static readonly StyledProperty<object?> SourceProperty =
        AvaloniaProperty.Register<ResultGrid, object?>(nameof(Source));

    /// <summary>一层:顶层是查询结果本身,往下每钻一层压一个。</summary>
    private readonly List<Level> _levels = [];
    private readonly List<(Rect Bounds, int Level)> _crumbHits = [];
    private Rect _backHit;
    private int _hover = -1;
    private string? _queryOrderColumn;
    private bool _queryOrderDescending;
    private ScrollViewer? _scroller;
    private int _resizing = -1;
    private double _resizeStartX;
    private double _resizeStartWidth;

    static ResultGrid()
    {
        AffectsMeasure<ResultGrid>(SourceProperty);
        AffectsRender<ResultGrid>(SourceProperty);
        FocusableProperty.OverrideDefaultValue<ResultGrid>(true);
    }

    /// <summary>数据源。</summary>
    public object? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>选中行(当前这一层里的行下标,已按排序换算);没有为 -1。</summary>
    public int SelectedIndex => Current is { } level && level.Selected >= 0 && level.Selected < level.Order.Length ? level.Order[level.Selected] : -1;

    /// <summary>钻进去了几层(0 = 看的是查询结果本身)。</summary>
    public int Depth => Math.Max(0, _levels.Count - 1);

    /// <summary>当前这一层从文档根算起的路径(顶层为空串)。</summary>
    public string CurrentPath => Current?.Path ?? "";

    /// <summary>当前这一层的列名(测试与无障碍用)。</summary>
    internal IReadOnlyList<string> ColumnNames => Current?.Columns.Select(static c => c.Name).ToList() ?? [];

    /// <summary>当前这一层第 <paramref name="column" /> 列的宽度。</summary>
    internal double WidthOf(int column)
    {
        if (Current is not { } level)
        {
            return 0;
        }
        EnsureWidths(level);
        return level.Widths[column];
    }

    /// <summary>右键 / 双击 / Ctrl+C 某个单元格(冒泡,视图据此弹菜单或复制)。</summary>
    public static readonly RoutedEvent<ResultCellEventArgs> CellActivatedEvent =
        RoutedEvent.Register<ResultGrid, ResultCellEventArgs>(nameof(CellActivated), Avalonia.Interactivity.RoutingStrategies.Bubble);

    /// <summary>右键 / 双击 / Ctrl+C 某个单元格。</summary>
    public event EventHandler<ResultCellEventArgs>? CellActivated
    {
        add => AddHandler(CellActivatedEvent, value);
        remove => RemoveHandler(CellActivatedEvent, value);
    }

    private QueryResultSet? Set => Source as QueryResultSet;

    private Level? Current => _levels.Count > 0 ? _levels[^1] : null;

    private double TopBand => (_levels.Count > 1 ? CrumbHeight : 0) + HeaderHeight;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _hover = -1;
            _levels.Clear();
            if (Set is { } set)
            {
                _levels.Add(new Level("", "", -1, set.Documents, set.Columns, null));
            }
            (_queryOrderColumn, _queryOrderDescending) = QueryOrder(Set);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }
        _scroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    // ── 钻入 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 钻进当前层第 <paramref name="row" /> 行(排序后的显示位置)的 <paramref name="column" /> 列;
    /// 不是对象 / 数组返回 false。
    /// </summary>
    internal bool DrillInto(int row, int column)
    {
        if (Current is not { } level || Set is not { } set || row < 0 || row >= level.Order.Length || column < 0 || column >= level.Columns.Count)
        {
            return false;
        }
        int index = level.Order[row];
        ResultColumn target = level.Columns[column];
        BsonValue? value = level.ValueAt(index, target);
        if (value is not (BsonDocument or BsonArray))
        {
            return false;
        }
        int rootIndex = level.RootIndex >= 0 ? level.RootIndex : index;
        string path = level.PathOf(index, target);
        string label = target.IsElementValue ? $"[{index.ToString(CultureInfo.InvariantCulture)}]" : level.Path.Length == 0 || !level.IsArray
            ? target.Name
            : $"[{index.ToString(CultureInfo.InvariantCulture)}].{target.Name}";
        level.ScrollOffset = _scroller?.Offset ?? default;
        _levels.Add(Level.Of(set.Loc, label, path, rootIndex, value));
        _hover = -1;
        InvalidateMeasure();
        InvalidateVisual();
        if (_scroller is not null)
        {
            _scroller.Offset = default;
        }
        return true;
    }

    /// <summary>回上一层;已在顶层返回 false。</summary>
    internal bool DrillBack() => DrillTo(_levels.Count - 2);

    /// <summary>回到第 <paramref name="level" /> 层(0 = 查询结果本身)。</summary>
    internal bool DrillTo(int level)
    {
        if (level < 0 || level >= _levels.Count - 1)
        {
            return false;
        }
        _levels.RemoveRange(level + 1, _levels.Count - level - 1);
        _hover = -1;
        InvalidateMeasure();
        InvalidateVisual();
        Vector restore = _levels[^1].ScrollOffset;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_scroller is not null)
            {
                _scroller.Offset = restore;
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
        return true;
    }

    // ── 度量 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Current is not { } level)
        {
            return default;
        }
        EnsureWidths(level);
        double width = NumberWidth + level.Widths.Sum();
        double height = TopBand + level.Documents.Count * RowHeight;
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Max(width, availableSize.Width), height);
    }

    private void EnsureWidths(Level level)
    {
        if (level.Widths.Count == level.Columns.Count)
        {
            return;
        }
        level.Widths.Clear();
        for (int c = 0; c < level.Columns.Count; c++)
        {
            level.Widths.Add(Math.Clamp(ContentWidth(level, c, sample: 200, maxChars: 60), 72, 360));
        }
    }

    /// <summary>一列按内容要多宽:列头 + 前 <paramref name="sample" /> 行里最宽的那格(每格最多量 <paramref name="maxChars" /> 个字)。</summary>
    private double ContentWidth(Level level, int column, int sample, int maxChars)
    {
        Typeface mono = Mono();
        Typeface ui = Ui();
        ResultColumn col = level.Columns[column];
        double header = Text(col.Name, mono, 11, null, FontWeight.Medium).Width + 6 + Text(col.TypeName, mono, 9, null).Width + 17;
        double content = 0;
        int count = Math.Min(level.Documents.Count, sample);
        for (int i = 0; i < count; i++)
        {
            BsonValue? value = level.ValueAt(i, col);
            string text = BsonText.Cell(value);
            double w = Text(text.Length > maxChars ? text[..maxChars] : text, value?.IsString == true ? ui : mono, 11, null).Width;
            content = Math.Max(content, w);
        }
        return Math.Max(header, content) + CellPadding * 2 + 4;
    }

    /// <summary>双击列头分隔线:按这一列的内容(最多量前 5000 行)重设列宽。</summary>
    internal void AutoFit(int column)
    {
        if (Current is not { } level || column < 0 || column >= level.Columns.Count)
        {
            return;
        }
        EnsureWidths(level);
        level.Widths[column] = Math.Clamp(Math.Ceiling(ContentWidth(level, column, sample: 5000, maxChars: 200)), 40, 1200);
        InvalidateMeasure();
        InvalidateVisual();
    }

    // ── 绘制 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Current is not { } level || Set is not { } set)
        {
            return;
        }
        EnsureWidths(level);
        Vector offset = _scroller?.Offset ?? default;
        Size viewport = _scroller?.Viewport ?? Bounds.Size;
        double width = Bounds.Width;
        double band = TopBand;
        IBrush border = Brush("VelaBorderPrimary");
        var pen = new Pen(border, 1);
        Typeface mono = Mono();
        Typeface ui = Ui();
        Typeface italic = new(mono.FontFamily, FontStyle.Italic);

        context.FillRectangle(Brush("VelaBgTerminal"), new Rect(offset.X, offset.Y, viewport.Width, viewport.Height));

        int first = Math.Max(0, (int)(offset.Y / RowHeight) - 1);
        int last = Math.Min(level.Order.Length - 1, (int)((offset.Y + viewport.Height) / RowHeight) + 1);
        double right = offset.X + viewport.Width;
        for (int r = first; r <= last; r++)
        {
            double y = band + r * RowHeight;
            if (r == level.Selected)
            {
                context.FillRectangle(Brush("VelaBgActive"), new Rect(0, y, width, RowHeight));
            }
            else if (r == _hover)
            {
                context.FillRectangle(Brush("VelaBgHover"), new Rect(0, y, width, RowHeight));
            }
            int index = level.Order[r];
            // 行号列:顶层是 1 起的行号;子表里是数组下标(与路径 items.2 同一个数)。
            string numberText = level.IsArray ? index.ToString(CultureInfo.InvariantCulture)
                : level.Path.Length > 0 ? "" : (r + 1).ToString(CultureInfo.InvariantCulture);
            FormattedText number = Text(numberText, mono, 10, Brush("VelaTextMuted"));
            context.DrawText(number, new Point(NumberWidth - CellPadding - number.Width, y + (RowHeight - number.Height) / 2));
            double x = NumberWidth;
            for (int c = 0; c < level.Columns.Count; c++)
            {
                double w = level.Widths[c];
                if (x > right)
                {
                    break;
                }
                if (x + w >= offset.X)
                {
                    BsonValue? value = level.ValueAt(index, level.Columns[c]);
                    BsonKind kind = BsonKinds.Of(value);
                    string text = BsonText.Cell(value);
                    (Typeface face, IBrush brush) = kind switch
                    {
                        BsonKind.String => (ui, Brush("VelaTextPrimary")),
                        BsonKind.Null => (italic, Brush("VelaTextTertiary")),
                        BsonKind.Missing => (mono, Brush("VelaTextMuted")),
                        _ => (mono, Brush(BsonKinds.ColorToken(kind)))
                    };
                    FormattedText cell = Text(text, face, 11, brush, maxWidth: w - CellPadding * 2);
                    double tx = BsonKinds.IsNumeric(kind) ? x + w - CellPadding - cell.Width : x + CellPadding;
                    context.DrawText(cell, new Point(tx, y + (RowHeight - cell.Height) / 2));
                }
                x += w;
                context.DrawLine(pen, new Point(x - 0.5, y), new Point(x - 0.5, y + RowHeight));
            }
            context.DrawLine(pen, new Point(NumberWidth - 0.5, y), new Point(NumberWidth - 0.5, y + RowHeight));
            context.DrawLine(pen, new Point(0, y + RowHeight - 0.5), new Point(width, y + RowHeight - 0.5));
        }

        // 列头钉在视口顶上(最后画,盖住滚上去的行)。
        double top = offset.Y + band - HeaderHeight;
        context.FillRectangle(Brush("VelaBgSurface"), new Rect(0, top, width, HeaderHeight));
        context.DrawLine(pen, new Point(0, top + HeaderHeight - 0.5), new Point(width, top + HeaderHeight - 0.5));
        context.DrawLine(pen, new Point(NumberWidth - 0.5, top), new Point(NumberWidth - 0.5, top + HeaderHeight));
        double hx = NumberWidth;
        for (int c = 0; c < level.Columns.Count; c++)
        {
            ResultColumn column = level.Columns[c];
            double w = level.Widths[c];
            FormattedText name = Text(column.Name, mono, 11, Brush(column.IsElementValue ? "VelaTextTertiary" : "VelaTextSecondary"), FontWeight.Medium,
                w - CellPadding * 2);
            context.DrawText(name, new Point(hx + CellPadding, top + (HeaderHeight - name.Height) / 2));
            double after = hx + CellPadding + name.Width + 6;
            FormattedText type = Text(column.TypeName, mono, 9, Brush("VelaTextMuted"), maxWidth: Math.Max(1, hx + w - after - 14));
            context.DrawText(type, new Point(after, top + (HeaderHeight - type.Height) / 2 + 1));
            bool? descending = c == level.SortColumn ? level.SortDescending
                : level.SortColumn < 0 && _levels.Count == 1 && column.Name == _queryOrderColumn ? _queryOrderDescending : null;
            if (descending is { } desc)
            {
                DrawIcon(context, desc ? "Mongo.arrow-down" : "Mongo.arrow-up", Math.Min(after + type.Width + 6, hx + w - 15),
                    top + (HeaderHeight - 11) / 2, 11, "VelaAccent");
            }
            hx += w;
            context.DrawLine(pen, new Point(hx - 0.5, top), new Point(hx - 0.5, top + HeaderHeight));
        }

        if (_levels.Count > 1)
        {
            DrawCrumbs(context, set.Loc, offset, viewport, pen);
        }
    }

    /// <summary>面包屑:‹ #3 › items › [2]                        数组 · 4 项(钉在列头上面,跟着水平滚动留在视口左缘)。</summary>
    private void DrawCrumbs(DrawingContext context, Loc loc, Vector offset, Size viewport, Pen pen)
    {
        _crumbHits.Clear();
        double top = offset.Y;
        double left = offset.X;
        context.FillRectangle(Brush("VelaBgSidebar"), new Rect(left, top, viewport.Width, CrumbHeight));
        context.DrawLine(pen, new Point(left, top + CrumbHeight - 0.5), new Point(left + viewport.Width, top + CrumbHeight - 0.5));
        Typeface mono = Mono();
        _backHit = new Rect(left + 4, top + 3, 22, 22);
        DrawIcon(context, "Mongo.chevron-left", _backHit.X + 4, top + 7, 13, "VelaTextSecondary");
        double x = _backHit.Right + 6;
        for (int i = 0; i < _levels.Count; i++)
        {
            bool isLast = i == _levels.Count - 1;
            string label = i == 0 ? $"#{(_levels[1].RootIndex + 1).ToString(CultureInfo.InvariantCulture)}" : _levels[i].Label;
            FormattedText text = Text(label, mono, 11, Brush(isLast ? "VelaTextPrimary" : "VelaFileDirName"), FontWeight.Medium);
            context.DrawText(text, new Point(x, top + (CrumbHeight - text.Height) / 2));
            if (!isLast)
            {
                _crumbHits.Add((new Rect(x - 4, top + 3, text.Width + 8, 22), i));
            }
            x += text.Width + 6;
            if (!isLast)
            {
                DrawIcon(context, "Mongo.chevron-right", x, top + (CrumbHeight - 11) / 2, 11, "VelaTextMuted");
                x += 17;
            }
        }
        string summary = _levels[^1].Summary(loc);
        FormattedText tail = Text(summary, mono, 10, Brush("VelaTextTertiary"));
        double tailX = left + viewport.Width - 10 - tail.Width;
        if (tailX > x + 12)
        {
            context.DrawText(tail, new Point(tailX, top + (CrumbHeight - tail.Height) / 2));
        }
    }

    private void DrawIcon(DrawingContext context, string key, double x, double y, double size, string token)
    {
        if (this.TryFindResource(key, out object? icon) && icon is Geometry geometry)
        {
            using (context.PushTransform(Matrix.CreateScale(size / 24d, size / 24d) * Matrix.CreateTranslation(x, y)))
            {
                context.DrawGeometry(null, new Pen(Brush(token), 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            }
        }
    }

    // ── 交互 ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (_resizing >= 0 && Current is { } level)
        {
            level.Widths[_resizing] = Math.Clamp(_resizeStartWidth + point.X - _resizeStartX, 40, 2000);
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }
        Cursor = GripAt(point) >= 0 ? new Cursor(StandardCursorType.SizeWestEast)
            : InCrumbs(point) && (_backHit.Contains(point) || _crumbHits.Any(h => h.Bounds.Contains(point))) ? new Cursor(StandardCursorType.Hand)
            : null;
        int row = RowAt(point);
        if (row != _hover)
        {
            _hover = row;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != -1)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Current is not { } level || Set is null)
        {
            return;
        }
        Point point = e.GetPosition(this);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (InCrumbs(point))
        {
            if (properties.IsLeftButtonPressed)
            {
                if (_backHit.Contains(point))
                {
                    DrillBack();
                }
                else if (_crumbHits.FirstOrDefault(h => h.Bounds.Contains(point)) is { Bounds.Width: > 0 } hit)
                {
                    DrillTo(hit.Level);
                }
            }
            e.Handled = true;
            return;
        }
        double top = (_scroller?.Offset.Y ?? 0) + TopBand - HeaderHeight;
        if (point.Y >= top && point.Y < top + HeaderHeight)
        {
            if (!properties.IsLeftButtonPressed)
            {
                return;
            }
            if (GripAt(point) is var grip and >= 0)
            {
                e.Handled = true;
                if (e.ClickCount == 2)
                {
                    _resizing = -1;
                    AutoFit(grip);
                    return;
                }
                EnsureWidths(level);
                _resizing = grip;
                _resizeStartX = point.X;
                _resizeStartWidth = level.Widths[grip];
                e.Pointer.Capture(this);
                return;
            }
            if (ColumnAt(point.X) is { } column)
            {
                Sort(level, column);
            }
            return;
        }
        int row = RowAt(point);
        if (row < 0)
        {
            return;
        }
        level.Selected = row;
        Focus();
        InvalidateVisual();
        int? col = ColumnAt(point.X);
        if (properties.IsRightButtonPressed)
        {
            RaiseEvent(Activation(level, row, col, activated: false));
            e.Handled = true;
        }
        else if (e.ClickCount == 2)
        {
            // 双击对象 / 数组:钻进去;其余照旧(复制值)。
            if (col is { } c && DrillInto(row, c))
            {
                e.Handled = true;
                return;
            }
            RaiseEvent(Activation(level, row, col, activated: true));
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_resizing >= 0)
        {
            _resizing = -1;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _resizing = -1;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Current is not { } level)
        {
            return;
        }
        if (_levels.Count > 1 && (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None || e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt))
        {
            DrillBack();
            e.Handled = true;
            return;
        }
        if (level.Order.Length == 0)
        {
            return;
        }
        int next = e.Key switch
        {
            Key.Down => Math.Min(level.Order.Length - 1, level.Selected + 1),
            Key.Up => Math.Max(0, level.Selected - 1),
            Key.Home => 0,
            Key.End => level.Order.Length - 1,
            _ => -2
        };
        if (next == -2)
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control) && level.Selected >= 0)
            {
                RaiseEvent(Activation(level, level.Selected, null, activated: true));
                e.Handled = true;
            }
            return;
        }
        level.Selected = next;
        if (_scroller is { } scroller)
        {
            double band = TopBand;
            double y = band + next * RowHeight;
            if (y - band < scroller.Offset.Y + RowHeight)
            {
                scroller.Offset = scroller.Offset.WithY(Math.Max(0, y - band - RowHeight));
            }
            else if (y + RowHeight > scroller.Offset.Y + scroller.Viewport.Height)
            {
                scroller.Offset = scroller.Offset.WithY(y + RowHeight - scroller.Viewport.Height);
            }
        }
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    /// 事件参数里带上真实的值、所属文档与绝对路径:钻进去之后,"第几行第几列"对外部已经没有意义。
    /// </summary>
    private ResultCellEventArgs Activation(Level level, int row, int? column, bool activated)
    {
        int index = level.Order[row];
        int rootIndex = level.RootIndex >= 0 ? level.RootIndex : index;
        BsonDocument root = Set!.Documents[rootIndex];
        ResultColumn? target = column is { } c ? level.Columns[c] : null;
        string? path = target is null ? null : level.PathOf(index, target);
        BsonValue? value = target is null ? null : level.ValueAt(index, target);
        return new ResultCellEventArgs(CellActivatedEvent, rootIndex, path, activated)
        {
            Document = root,
            Value = value,
            GridRow = row,
            GridColumn = column ?? -1
        };
    }

    private void Sort(Level level, int column)
    {
        level.SortDescending = level.SortColumn == column && !level.SortDescending;
        level.SortColumn = column;
        ResultColumn target = level.Columns[column];
        level.Order =
        [
            .. (level.SortDescending
                ? level.Order.OrderByDescending(i => level.ValueAt(i, target) ?? BsonMinKey.Value)
                : level.Order.OrderBy(i => level.ValueAt(i, target) ?? BsonMinKey.Value))
        ];
        level.Selected = -1;
        InvalidateVisual();
    }

    private bool InCrumbs(Point point) => _levels.Count > 1 && point.Y >= (_scroller?.Offset.Y ?? 0) && point.Y < (_scroller?.Offset.Y ?? 0) + CrumbHeight;

    private int RowAt(Point point)
    {
        double top = _scroller?.Offset.Y ?? 0;
        if (Current is not { } level || point.Y < top + TopBand)
        {
            return -1;
        }
        int row = (int)((point.Y - TopBand) / RowHeight);
        return row >= 0 && row < level.Order.Length ? row : -1;
    }

    private int? ColumnAt(double x)
    {
        if (Current is not { } level)
        {
            return null;
        }
        double left = NumberWidth;
        for (int c = 0; c < level.Widths.Count; c++)
        {
            if (x >= left && x < left + level.Widths[c])
            {
                return c;
            }
            left += level.Widths[c];
        }
        return null;
    }

    /// <summary>指针落在哪一列的右分隔线上(只认列头那一条带);不在任何分隔线上为 -1。</summary>
    private int GripAt(Point point)
    {
        if (Current is not { } level)
        {
            return -1;
        }
        double top = (_scroller?.Offset.Y ?? 0) + TopBand - HeaderHeight;
        if (point.Y < top || point.Y >= top + HeaderHeight)
        {
            return -1;
        }
        double edge = NumberWidth;
        for (int c = 0; c < level.Widths.Count; c++)
        {
            edge += level.Widths[c];
            if (Math.Abs(point.X - edge) <= GripHalf)
            {
                return c;
            }
        }
        return -1;
    }

    /// <summary>查询本身的排序(find 的 sort、管道里最后一个 $sort):列头上画个箭头,不重排数据。</summary>
    private static (string? Column, bool Descending) QueryOrder(QueryResultSet? set)
    {
        BsonDocument? sort = set?.Result.Query?.Sort;
        if (sort is null && set?.Result.Pipeline is { } pipeline)
        {
            sort = pipeline.OfType<BsonDocument>().LastOrDefault(static s => s.Contains("$sort"))?["$sort"] as BsonDocument;
        }
        if (sort is not { ElementCount: > 0 })
        {
            return (null, false);
        }
        BsonElement first = sort.GetElement(0);
        return (first.Name, first.Value.IsNumeric && first.Value.ToDouble() < 0);
    }

    // ── 资源 ────────────────────────────────────────────────────────────────

    private static IBrush Brush(string token) => ThemeBrushes.Get(token, Brushes.Gray);

    private Typeface Mono() => new(this.TryFindResource("VelaUiMonoFont", out object? f) && f is FontFamily family ? family : FontFamily.Default);

    private Typeface Ui() => new(this.TryFindResource("VelaUiFont", out object? f) && f is FontFamily family ? family : FontFamily.Default);

    private double FontSizeOf(double design) =>
        this.TryFindResource($"VelaFontSize{design.ToString(CultureInfo.InvariantCulture)}", out object? size) && size is double d ? d : design;

    private FormattedText Text(string text, Typeface face, double size, IBrush? brush, FontWeight weight = FontWeight.Normal, double maxWidth = double.NaN)
    {
        var typeface = weight == FontWeight.Normal ? face : new Typeface(face.FontFamily, face.Style, weight);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSizeOf(size), brush ?? Brushes.Gray);
        if (!double.IsNaN(maxWidth) && maxWidth > 0)
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.MaxLineCount = 1;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
        }
        return formatted;
    }

    /// <summary>
    /// 网格的一层。顶层的行就是查询结果的文档;往下一层的"行"是某个数组的元素(或某个对象本身),
    /// 标量元素放进一列「值」里 —— 只读网格不必像集合网格那样给每格算暂存身份,按值摊平就够了。
    /// </summary>
    private sealed class Level
    {
        public Level(string label, string path, int rootIndex, IReadOnlyList<BsonDocument> documents, IReadOnlyList<ResultColumn> columns, BsonArray? array)
        {
            Label = label;
            Path = path;
            RootIndex = rootIndex;
            Documents = documents;
            Columns = columns;
            Array = array;
            Order = [.. Enumerable.Range(0, documents.Count)];
        }

        /// <summary>面包屑上的字。</summary>
        public string Label { get; }

        /// <summary>容器从文档根算起的路径;顶层为空串。</summary>
        public string Path { get; }

        /// <summary>所属文档在查询结果里的下标;顶层为 -1(每行各是各的文档)。</summary>
        public int RootIndex { get; }

        /// <summary>行。数组层里标量元素包成 <c>{ "值": x }</c>,文档元素原样。</summary>
        public IReadOnlyList<BsonDocument> Documents { get; }

        /// <summary>列。</summary>
        public IReadOnlyList<ResultColumn> Columns { get; }

        /// <summary>数组层的原数组;对象层与顶层为 <see langword="null" />。</summary>
        public BsonArray? Array { get; }

        /// <summary>是数组层。</summary>
        public bool IsArray => Array is not null;

        public List<double> Widths { get; } = [];

        public int[] Order { get; set; }

        public int Selected { get; set; } = -1;

        public int SortColumn { get; set; } = -1;

        public bool SortDescending { get; set; }

        public Vector ScrollOffset { get; set; }

        /// <summary>第 <paramref name="index" /> 行在 <paramref name="column" /> 列的值。</summary>
        public BsonValue? ValueAt(int index, ResultColumn column) =>
            column.IsElementValue ? Array![index] is BsonDocument ? null : Array[index] : Documents[index].GetValue(column.Name, null);

        /// <summary>第 <paramref name="index" /> 行在 <paramref name="column" /> 列的值从文档根算起的路径。</summary>
        public string PathOf(int index, ResultColumn column)
        {
            string row = IsArray ? Join(Path, index.ToString(CultureInfo.InvariantCulture)) : Path;
            return column.IsElementValue ? row : Join(row, column.Name);
        }

        public string Summary(Loc loc) =>
            loc.Format(IsArray ? "Cw_DrillArray" : "Cw_DrillObject", (IsArray ? Documents.Count : Columns.Count).ToString("N0", CultureInfo.InvariantCulture));

        public static Level Of(Loc loc, string label, string path, int rootIndex, BsonValue container)
        {
            if (container is BsonArray array)
            {
                var documents = new List<BsonDocument>(array.Count);
                bool scalars = array.Count == 0;
                var scalarKinds = new Dictionary<BsonKind, int>();
                foreach (BsonValue element in array)
                {
                    if (element is BsonDocument document)
                    {
                        documents.Add(document);
                    }
                    else
                    {
                        scalars = true;
                        BsonKind kind = BsonKinds.Of(element);
                        scalarKinds[kind] = scalarKinds.GetValueOrDefault(kind) + 1;
                        documents.Add([]);
                    }
                }
                List<ResultColumn> columns = QueryResultSet.BuildColumns(documents);
                if (scalars)
                {
                    columns.Insert(0, new ResultColumn(loc["Cw_DrillValue"], CollectionTabViewModel.Dominant(scalarKinds)) { IsElementValue = true });
                }
                return new Level(label, path, rootIndex, documents, columns, array);
            }
            var only = (BsonDocument)container;
            return new Level(label, path, rootIndex, [only], QueryResultSet.BuildColumns([only]), null);
        }

        private static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}.{name}";
    }
}

/// <summary>网格单元格被激活(右键 = 菜单,双击 / Ctrl+C = 复制)。</summary>
/// <param name="routedEvent">路由事件。</param>
/// <param name="documentIndex">所属文档在查询结果里的下标。</param>
/// <param name="column">值从文档根算起的路径(顶层就是列名);行号列为 <see langword="null" />。</param>
/// <param name="activated">双击或 Ctrl+C(否则是右键)。</param>
public sealed class ResultCellEventArgs(RoutedEvent routedEvent, int documentIndex, string? column, bool activated)
    : Avalonia.Interactivity.RoutedEventArgs(routedEvent)
{
    /// <summary>所属文档在查询结果里的下标。</summary>
    public int DocumentIndex { get; } = documentIndex;

    /// <summary>值从文档根算起的路径(顶层就是列名;钻进去后是 <c>items.2.sku</c>)。</summary>
    public string? Column { get; } = column;

    /// <summary>双击或 Ctrl+C。</summary>
    public bool Activated { get; } = activated;

    /// <summary>所属文档。</summary>
    public BsonDocument? Document { get; init; }

    /// <summary>单元格的值;行号列或字段缺失为 <see langword="null" />。</summary>
    public BsonValue? Value { get; init; }

    /// <summary>网格当前这一层里的显示行(给「展开查看」回调 <see cref="ResultGrid.DrillInto" /> 用)。</summary>
    public int GridRow { get; init; } = -1;

    /// <summary>网格当前这一层里的列下标;行号列为 -1。</summary>
    public int GridColumn { get; init; } = -1;
}
