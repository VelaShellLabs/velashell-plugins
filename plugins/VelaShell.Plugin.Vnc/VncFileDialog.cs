using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VelaShell.Plugin.Vnc;

/// <summary>Positions the file dialog independently of the remote desktop's layout.</summary>
internal sealed class VncFileDialog : Panel
{
    internal const double DefaultWidth = 800;
    internal const double DefaultHeight = 600;
    private const double MinimumWidth = 400;
    private const double MinimumHeight = 280;
    private const double Inset = 16;
    private const double GripSize = 8;

    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private readonly VncFilePanel _panel;
    private readonly List<(Control Control, Edge Edge)> _grips = [];
    private Rect? _placement;
    private Size _viewportSize;
    private Rect _dragBounds;
    private Point _dragStart;
    private Edge _dragEdge;
    private IPointer? _pointer;

    internal VncFileDialog(VncFilePanel panel)
    {
        _panel = panel;
        Children.Add(panel);
        RegisterDrag(panel.TitleBar, Edge.None);
        AddGrip(Edge.Left, StandardCursorType.SizeWestEast);
        AddGrip(Edge.Right, StandardCursorType.SizeWestEast);
        AddGrip(Edge.Top, StandardCursorType.SizeNorthSouth);
        AddGrip(Edge.Bottom, StandardCursorType.SizeNorthSouth);
        AddGrip(Edge.Left | Edge.Top, StandardCursorType.TopLeftCorner);
        AddGrip(Edge.Right | Edge.Top, StandardCursorType.TopRightCorner);
        AddGrip(Edge.Left | Edge.Bottom, StandardCursorType.BottomLeftCorner);
        AddGrip(Edge.Right | Edge.Bottom, StandardCursorType.BottomRightCorner);
    }

    private static Rect Viewport(Size size) => new(
        Math.Min(Inset, size.Width / 2), Math.Min(Inset, size.Height / 2),
        Math.Max(0, size.Width - Inset * 2), Math.Max(0, size.Height - Inset * 2));

    private Rect Constrain(Size size)
    {
        Rect viewport = Viewport(size);
        Rect requested = _placement ?? new Rect(
            (size.Width - DefaultWidth) / 2, (size.Height - DefaultHeight) / 2, DefaultWidth, DefaultHeight);
        double width = Math.Clamp(requested.Width, Math.Min(MinimumWidth, viewport.Width), viewport.Width);
        double height = Math.Clamp(requested.Height, Math.Min(MinimumHeight, viewport.Height), viewport.Height);
        return new Rect(
            Math.Clamp(requested.X, viewport.Left, viewport.Right - width),
            Math.Clamp(requested.Y, viewport.Top, viewport.Bottom - height), width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _panel.Measure(Constrain(availableSize).Size);
        foreach ((Control control, _) in _grips) { control.Measure(new Size(GripSize, GripSize)); }
        // Like Canvas, an overlay must not contribute its content's desired size to the desktop.
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewportSize) { CancelDrag(); _viewportSize = finalSize; }
        Rect placement = Constrain(finalSize);
        _placement = placement;
        _panel.Arrange(placement);
        foreach ((Control control, Edge edge) in _grips)
        {
            bool horizontal = (edge & (Edge.Left | Edge.Right)) != 0;
            bool vertical = (edge & (Edge.Top | Edge.Bottom)) != 0;
            double x = edge.HasFlag(Edge.Left) ? placement.Left - GripSize / 2
                : edge.HasFlag(Edge.Right) ? placement.Right - GripSize / 2 : placement.Left + GripSize / 2;
            double y = edge.HasFlag(Edge.Top) ? placement.Top - GripSize / 2
                : edge.HasFlag(Edge.Bottom) ? placement.Bottom - GripSize / 2 : placement.Top + GripSize / 2;
            control.Arrange(new Rect(x, y, horizontal ? GripSize : Math.Max(0, placement.Width - GripSize),
                vertical ? GripSize : Math.Max(0, placement.Height - GripSize)));
        }
        return finalSize;
    }

    private void AddGrip(Edge edge, StandardCursorType cursor)
    {
        var grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(cursor) };
        _grips.Add((grip, edge));
        Children.Add(grip);
        RegisterDrag(grip, edge);
    }

    private void RegisterDrag(Control control, Edge edge)
    {
        control.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _pointer is not null) { return; }
            _dragStart = e.GetPosition(this);
            _dragBounds = _panel.Bounds;
            _dragEdge = edge;
            _pointer = e.Pointer;
            e.Pointer.Capture(control);
            e.Handled = true;
        };
        control.PointerMoved += OnDrag;
        control.PointerReleased += (_, e) =>
        {
            if (_pointer != e.Pointer || e.InitialPressMouseButton != MouseButton.Left) { return; }
            CancelDrag();
            e.Handled = true;
        };
        control.PointerCaptureLost += (_, _) => _pointer = null;
    }

    private void OnDrag(object? sender, PointerEventArgs e)
    {
        if (_pointer != e.Pointer) { return; }
        Vector delta = e.GetPosition(this) - _dragStart;
        Rect viewport = Viewport(Bounds.Size);
        if (_dragEdge == Edge.None)
        {
            _placement = new Rect(_dragBounds.Position + delta, _dragBounds.Size);
        }
        else
        {
            double minWidth = Math.Min(MinimumWidth, viewport.Width);
            double minHeight = Math.Min(MinimumHeight, viewport.Height);
            double left = _dragEdge.HasFlag(Edge.Left) ? Math.Clamp(_dragBounds.Left + delta.X, viewport.Left, _dragBounds.Right - minWidth) : _dragBounds.Left;
            double top = _dragEdge.HasFlag(Edge.Top) ? Math.Clamp(_dragBounds.Top + delta.Y, viewport.Top, _dragBounds.Bottom - minHeight) : _dragBounds.Top;
            double right = _dragEdge.HasFlag(Edge.Right) ? Math.Clamp(_dragBounds.Right + delta.X, _dragBounds.Left + minWidth, viewport.Right) : _dragBounds.Right;
            double bottom = _dragEdge.HasFlag(Edge.Bottom) ? Math.Clamp(_dragBounds.Bottom + delta.Y, _dragBounds.Top + minHeight, viewport.Bottom) : _dragBounds.Bottom;
            _placement = new Rect(new Point(left, top), new Point(right, bottom));
        }
        InvalidateMeasure();
        e.Handled = true;
    }

    internal void CancelDrag()
    {
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
    }
}
