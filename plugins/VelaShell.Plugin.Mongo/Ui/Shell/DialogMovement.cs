using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>拖动工作台内的模态卡片,不移动宿主窗口,也不触发内容重新布局。</summary>
internal sealed class DialogMovement
{
    private readonly Panel _area;
    private readonly Border _frame;
    private readonly Control _grip;
    // 二级确认框有自己的位置;返回被遮住的向导时恢复它原来的位置。
    private readonly ConditionalWeakTable<DialogViewModel, TranslateTransform> _positions = new();
    private IPointer? _pointer;
    private Point _last;
    private Rect _startBounds;

    public DialogMovement(Panel area, Border frame, Control grip, Control resizeGrip)
    {
        (_area, _frame, _grip) = (area, frame, grip);
        frame.DataContextChanged += (_, _) =>
        {
            EndDrag();
            frame.RenderTransform = frame.DataContext is DialogViewModel vm ? _positions.GetOrCreateValue(vm) : null;
        };
        frame.SizeChanged += (_, _) => Move(default);
        area.SizeChanged += (_, _) => Move(default);
        foreach (Control handle in new[] { grip, resizeGrip })
        {
            handle.PointerPressed += OnPressed;
            handle.PointerMoved += OnMoved;
            handle.PointerReleased += (_, e) =>
            {
                if (e.Pointer == _pointer && !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed)
                {
                    EndDrag();
                    e.Handled = true;
                }
            };
            handle.PointerCaptureLost += (_, _) => _pointer = null;
            handle.DetachedFromVisualTree += (_, _) => EndDrag();
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control handle || _pointer is not null || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed
            || _frame.TranslatePoint(default, _area) is not { } origin)
        {
            return;
        }
        _last = e.GetPosition(_area);
        _startBounds = new Rect(origin, _frame.Bounds.Size);
        _pointer = e.Pointer;
        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer != _pointer)
        {
            return;
        }
        Point next = e.GetPosition(_area);
        Vector delta = next - _last;
        if (sender != _grip && _frame.DataContext is DocumentEditorDialogViewModel editor && _frame.RenderTransform is TranslateTransform offset)
        {
            double maxWidth = Math.Max(0, _area.Bounds.Width - _startBounds.X);
            double maxHeight = Math.Max(0, _area.Bounds.Height - _startBounds.Y);
            double width = Math.Clamp(_startBounds.Width + delta.X, Math.Min(860, maxWidth), maxWidth);
            double height = Math.Clamp(_startBounds.Height + delta.Y, Math.Min(400, maxHeight), maxHeight);
            editor.ResizeTo(width, height);
            // 卡片的布局是居中的:补偿中心的移动,让拖动期间左上角停在原处。
            offset.X = _startBounds.X - (_area.Bounds.Width - width) / 2;
            offset.Y = _startBounds.Y - (_area.Bounds.Height - height) / 2;
        }
        else
        {
            Move(delta);
            _last = next;
        }
        e.Handled = true;
    }

    private void Move(Vector delta)
    {
        if (_frame.RenderTransform is not TranslateTransform offset)
        {
            return;
        }
        double x = Math.Max(0, (_area.Bounds.Width - _frame.Bounds.Width) / 2);
        double y = Math.Max(0, (_area.Bounds.Height - _frame.Bounds.Height) / 2);
        offset.X = Math.Clamp(offset.X + delta.X, -x, x);
        offset.Y = Math.Clamp(offset.Y + delta.Y, -y, y);
    }

    private void EndDrag()
    {
        _pointer?.Capture(null);
        _pointer = null;
    }
}
