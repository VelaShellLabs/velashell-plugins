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

    public DialogMovement(Panel area, Border frame, Control grip)
    {
        (_area, _frame, _grip) = (area, frame, grip);
        frame.DataContextChanged += (_, _) =>
        {
            EndDrag();
            frame.RenderTransform = frame.DataContext is DialogViewModel vm ? _positions.GetOrCreateValue(vm) : null;
        };
        frame.SizeChanged += (_, _) => Move(default);
        area.SizeChanged += (_, _) => Move(default);
        grip.PointerPressed += OnPressed;
        grip.PointerMoved += OnMoved;
        grip.PointerReleased += (_, e) =>
        {
            if (e.Pointer == _pointer && !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
            {
                EndDrag();
                e.Handled = true;
            }
        };
        grip.PointerCaptureLost += (_, _) => _pointer = null;
        grip.DetachedFromVisualTree += (_, _) => EndDrag();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pointer is not null || !e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _last = e.GetPosition(_area);
        _pointer = e.Pointer;
        e.Pointer.Capture(_grip);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer != _pointer)
        {
            return;
        }
        Point next = e.GetPosition(_area);
        Move(next - _last);
        _last = next;
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
        if (_pointer?.Captured == _grip)
        {
            _pointer.Capture(null);
        }
        _pointer = null;
    }
}
