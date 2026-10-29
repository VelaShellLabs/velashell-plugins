using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Ui;

/// <summary>Owns UI pixels only. The session owns every protocol/native object.</summary>
internal sealed class RdpDesktopSurface : Control
{
    private const int MaximumFrameBytes = 128 * 1024 * 1024;
    private IRdpSession? _session;
    private RdpInputQueue? _input;
    private RdpClipboardBinding? _clipboardBinding;
    internal void QueueClipboardFocus(bool focused) => _input?.Enqueue(new(RdpInputKind.ClipboardFocus, X: focused ? 1 : 0));
    private readonly HashSet<ushort> _keys = [];
    private readonly HashSet<RdpMouseButton> _buttons = [];
    private IPointer? _captured;
    private TopLevel? _topLevel;
    private Window? _window;
    private Cursor? _ownedCursor;
    private WriteableBitmap? _cursorBitmap;
    private volatile bool _attached;
    private bool _subscribed;
    private int _dirty;
    private int _pending;
    private long _bindingGeneration;
    private RdpPointer? _pointer;

    public RdpDesktopSurface()
    {
        Focusable = true;
        ClipToBounds = true;
        // Tunnel on this control handles ordinary keys, not host/OS reserved bindings.
        AddHandler(KeyDownEvent, KeyPressed, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, KeyReleased, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => ReleaseInput();
    }

    public event Action? Updated;
    public event Action? InputFailed;
    public RdpFrameInfo Frame { get; private set; }
    public RdpSessionStatus Status { get; private set; } = new(RdpSessionState.Disconnected);
    internal WriteableBitmap? Bitmap { get; private set; }
    internal int HeldKeyCount => _keys.Count;
    internal int HeldButtonCount => _buttons.Count;
    internal bool HasPendingUpdate => Volatile.Read(ref _pending) != 0;
    internal Task InputDrained => _input?.Drained ?? Task.CompletedTask;
    internal double DpiScale => _topLevel?.RenderScaling ?? 1;
    public RdpScaleMode ScaleMode
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            UpdateExtent();
            InvalidateVisual();
        }
    }
    internal RdpViewport Viewport => RdpViewport.Create(Bounds.Size, Frame.Width, Frame.Height, ScaleMode, DpiScale);

    public void SetSession(IRdpSession? session)
    {
        if (ReferenceEquals(_session, session)) return;
        Unsubscribe();
        ReleaseInput();
        _clipboardBinding?.Dispose();
        _clipboardBinding = session is null ? null : new(this, session);
        _session = session;
        long generation = ++_bindingGeneration;
        CanInput = false;
        _input = session is null ? null : new RdpInputQueue(session, () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_attached || generation != _bindingGeneration) return;
                CanInput = true;
                ReleaseInput();
                InputFailed?.Invoke();
            });
        }, _clipboardBinding is { } binding ? binding.PrepareAsync : null);
        if (_attached)
        {
            _clipboardBinding?.Attach();
            if (_window?.IsActive != false) _clipboardBinding?.Resume();
        }
        if (session is not null) ClearBitmap();
        _pointer = null;
        ResetCursor();
        Status = session?.Status ?? new(RdpSessionState.Disconnected);
        if (_attached) Subscribe();
        ScheduleUpdate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _clipboardBinding?.Attach();
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += ScalingChanged;
        _window = _topLevel as Window;
        if (_window is not null)
        {
            _window.Deactivated += WindowDeactivated;
            _window.Activated += WindowActivated;
        }
        if (_window?.IsActive != false) _clipboardBinding?.Resume();
        Subscribe();
        ScheduleUpdate(); // Initial frame/status/pointer do not depend on receiving a notification.
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        QueueClipboardFocus(false);
        _clipboardBinding?.Detach();
        Unsubscribe();
        if (_topLevel is not null) _topLevel.ScalingChanged -= ScalingChanged;
        if (_window is not null)
        {
            _window.Deactivated -= WindowDeactivated;
            _window.Activated -= WindowActivated;
        }
        _window = null;
        _topLevel = null;
        ReleaseInput();
        ClearBitmap();
        ResetCursor();
        _pointer = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Subscribe()
    {
        if (_session is null || _subscribed) return;
        _subscribed = true;
        _session.FrameAvailable += ScheduleUpdate;
        _session.StatusChanged += StatusNotification;
        _session.PointerChanged += PointerNotification;
        _session.SetFrameConsumer(true);
    }
    private void Unsubscribe()
    {
        if (_session is null || !_subscribed) return;
        _session.SetFrameConsumer(false);
        _subscribed = false;
        _session.FrameAvailable -= ScheduleUpdate;
        _session.StatusChanged -= StatusNotification;
        _session.PointerChanged -= PointerNotification;
    }
    private void StatusNotification(RdpSessionStatus _) => ScheduleUpdate();
    private void PointerNotification(RdpPointer _) => ScheduleUpdate();
    private void ScalingChanged(object? sender, EventArgs e) { UpdateExtent(); InvalidateVisual(); }
    private void WindowActivated(object? sender, EventArgs e) => _clipboardBinding?.Resume();
    private void WindowDeactivated(object? sender, EventArgs e)
    {
        _clipboardBinding?.Suspend();
        ReleaseInput();
    }

    private void ScheduleUpdate()
    {
        Interlocked.Exchange(ref _dirty, 1);
        if (!_attached || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(ApplyUpdate, DispatcherPriority.Render);
    }

    private void ApplyUpdate()
    {
        Interlocked.Exchange(ref _dirty, 0);
        try
        {
            if (!_attached || _session is not { } session) return;
            RdpSessionStatus previousStatus = Status;
            int previousWidth = Frame.Width, previousHeight = Frame.Height;
            Status = session.Status;
            if (Status.State != RdpSessionState.Connected) ReleaseInput();
            else CopyLatestFrame(session); // Keep the last image; a closing session may already have cleared its managed pixels.
            if (!ReferenceEquals(_pointer, session.Pointer))
            {
                _pointer = session.Pointer;
                ApplyPointer(_pointer);
            }
            if (Status != previousStatus || Frame.Width != previousWidth || Frame.Height != previousHeight)
                Updated?.Invoke();
        }
        catch (Exception)
        {
            // A malformed snapshot or unavailable platform bitmap is visible, not an unhandled dispatcher fault.
            CanInput = true;
            ReleaseInput();
            InputFailed?.Invoke();
        }
        finally
        {
            Interlocked.Exchange(ref _pending, 0);
            if (_attached && Volatile.Read(ref _dirty) != 0) ScheduleUpdate();
        }
    }

    private void CopyLatestFrame(IRdpSession session)
    {
        RdpFrameInfo advertised = session.FrameInfo;
        if (advertised.Width <= 0 || advertised.Height <= 0) return;
        if (Bitmap is not null && Frame.Sequence == advertised.Sequence && Frame.Width == advertised.Width &&
            Frame.Height == advertised.Height) return;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ValidateFrame(advertised);
            WriteableBitmap? candidate = null;
            try
            {
                if (Bitmap is null || Bitmap.PixelSize != new PixelSize(advertised.Width, advertised.Height))
                    candidate = new WriteableBitmap(new PixelSize(advertised.Width, advertised.Height), new Vector(96, 96),
                        PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                WriteableBitmap bitmap = candidate ?? Bitmap!;
                RdpFrameInfo copied;
                bool success;
                using (ILockedFramebuffer target = bitmap.Lock())
                    success = session.TryCopyFrame(target.Address, advertised.Width, advertised.Height,
                        target.RowBytes, checked(target.RowBytes * advertised.Height), out copied);
                if (!success)
                {
                    // Retry once only for a changed size. Busy/closed metadata waits for a real notification.
                    if (session.Status.State != RdpSessionState.Connected || copied == advertised || copied.Width <= 0 || copied.Height <= 0) return;
                    advertised = copied;
                    continue;
                }
                ValidateFrame(copied);
                if (candidate is not null)
                {
                    WriteableBitmap? previous = Bitmap;
                    Bitmap = candidate;
                    candidate = null;
                    previous?.Dispose();
                }
                bool resized = Frame.Width != copied.Width || Frame.Height != copied.Height;
                Frame = copied;
                if (resized) UpdateExtent();
                InvalidateVisual();
                return;
            }
            finally { candidate?.Dispose(); }
        }
        // Further resize activity arrives through FrameAvailable. Do not self-schedule a permanently unavailable frame.
    }

    private static void ValidateFrame(RdpFrameInfo frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < checked(frame.Width * 4) ||
            frame.ByteLength > MaximumFrameBytes) throw new InvalidDataException("Invalid managed RDP frame size.");
    }

    internal static void CopyRows(byte[] pixels, RdpFrameInfo frame, IntPtr address, int rowBytes)
    {
        ValidateFrame(frame);
        int packedRow = checked(frame.Width * 4);
        if (pixels.Length < frame.ByteLength || rowBytes < packedRow) throw new InvalidDataException("Invalid RDP row stride.");
        for (int y = 0; y < frame.Height; y++)
            Marshal.Copy(pixels, checked(y * frame.Stride), IntPtr.Add(address, checked(y * rowBytes)), packedRow);
    }

    private void UpdateExtent()
    {
        double width = ScaleMode == RdpScaleMode.Actual && Frame.Width > 0 ? Frame.Width / DpiScale : double.NaN;
        double height = ScaleMode == RdpScaleMode.Actual && Frame.Height > 0 ? Frame.Height / DpiScale : double.NaN;
        if (Width.Equals(width) && Height.Equals(height)) return;
        Width = width;
        Height = height;
    }

    private void ClearBitmap()
    {
        Bitmap?.Dispose();
        Bitmap = null;
        Frame = default;
        UpdateExtent();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // A token-derived brush both paints the letterbox and makes it hit-testable.
        if (this.TryFindResource("VelaBgTerminal", out object? background) && background is IBrush brush)
            context.DrawRectangle(brush, null, new Rect(Bounds.Size));
        else context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Bitmap is not null) context.DrawImage(Bitmap, new Rect(Bitmap.Size), Viewport.Desktop);
    }

    private bool CanInput { get => _attached && !field && Status.State == RdpSessionState.Connected; set; }
    private bool Send(RdpInput input)
    {
        if (_input?.Enqueue(input) == true) return true;
        _keys.Clear(); _buttons.Clear();
        return false;
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (!CanInput) return;
        ushort scan = RdpInputMapper.ScanCode(e.PhysicalKey);
        if (scan == 0) { InputFailed?.Invoke(); return; }
        bool paste = scan == 0x2f && (e.KeyModifiers & KeyModifiers.Control) != 0;
        if (Send(new(RdpInputKind.KeyDown, scan, SynchronizeClipboard: paste))) _keys.Add(scan); // Repeats are sent, only release tracking is deduplicated.
        e.Handled = true;
    }
    private void KeyReleased(object? sender, KeyEventArgs e)
    {
        ushort scan = RdpInputMapper.ScanCode(e.PhysicalKey);
        if (scan == 0 || !_keys.Remove(scan)) return;
        Send(new(RdpInputKind.KeyUp, scan));
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        // Releasing our final mouse button intentionally drops capture, not the keyboard chord.
        if (_captured is not null) ReleaseInput();
        base.OnPointerCaptureLost(e);
    }
    internal void ReleaseInput()
    {
        _keys.Clear(); _buttons.Clear();
        _input?.Enqueue(new(RdpInputKind.ReleaseAll));
        IPointer? captured = _captured;
        _captured = null;
        if (captured?.Captured == this) captured.Capture(null);
    }
    public void SendCtrlAltDelete()
    {
        if (CanInput) { ReleaseInput(); Send(new(RdpInputKind.CtrlAltDelete)); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanInput) return;
        Focus();
        if (!Viewport.TryMap(e.GetPosition(this), false, out PixelPoint point)) return;
        RdpMouseButton? button = Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button is null) return;
        if (Send(new(RdpInputKind.ButtonDown, X: point.X, Y: point.Y, Button: button.Value)))
        {
            _buttons.Add(button.Value);
            _captured = e.Pointer;
            e.Pointer.Capture(this);
        }
        e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (CanInput && Viewport.TryMap(e.GetPosition(this), _buttons.Count > 0, out PixelPoint point))
            Send(new(RdpInputKind.PointerMove, X: point.X, Y: point.Y));
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        RdpMouseButton? button = Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button is null || !_buttons.Remove(button.Value)) return;
        if (Viewport.TryMap(e.GetPosition(this), true, out PixelPoint point))
            Send(new(RdpInputKind.ButtonUp, X: point.X, Y: point.Y, Button: button.Value));
        if (_buttons.Count == 0)
        {
            _captured = null;
            e.Pointer.Capture(null);
        }
        e.Handled = true;
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!CanInput || !Viewport.TryMap(e.GetPosition(this), false, out PixelPoint point)) return;
        Wheel(e.Delta.Y, false, point);
        Wheel(e.Delta.X, true, point);
        e.Handled = true; // Remote wheel must not also scroll the local 1:1 viewport.
    }
    private void Wheel(double delta, bool horizontal, PixelPoint point)
    {
        if (delta == 0) return;
        short amount = (short)Math.Clamp(Math.Round(delta * 120), short.MinValue, short.MaxValue);
        if (amount != 0) Send(new(RdpInputKind.Wheel, X: point.X, Y: point.Y, WheelDelta: amount, Horizontal: horizontal));
    }
    private static RdpMouseButton? Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RdpMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RdpMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RdpMouseButton.Middle,
        PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton1Released => RdpMouseButton.X1,
        PointerUpdateKind.XButton2Pressed or PointerUpdateKind.XButton2Released => RdpMouseButton.X2,
        _ => null
    };

    private void ResetCursor()
    {
        Cursor = null; // Inherit the platform default; no per-surface default cursor handle.
        _ownedCursor?.Dispose(); _ownedCursor = null;
        _cursorBitmap?.Dispose(); _cursorBitmap = null;
    }
    private void ApplyPointer(RdpPointer pointer)
    {
        if (pointer.Kind == RdpPointerKind.Position) return; // No public cross-platform OS pointer-warp API.
        ResetCursor();
        if (pointer.Kind == RdpPointerKind.Hidden)
        {
            _ownedCursor = new Cursor(StandardCursorType.None);
            Cursor = _ownedCursor;
            return;
        }
        if (pointer.Kind != RdpPointerKind.Bitmap) return;
        if (pointer.Width <= 0 || pointer.Height <= 0 || pointer.Width > 384 || pointer.Height > 384 ||
            pointer.HotspotX < 0 || pointer.HotspotX >= pointer.Width || pointer.HotspotY < 0 || pointer.HotspotY >= pointer.Height ||
            pointer.Rgba is not { } rgba || rgba.Length != checked(pointer.Width * pointer.Height * 4)) return;
        _cursorBitmap = new(new PixelSize(pointer.Width, pointer.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer target = _cursorBitmap.Lock())
            CopyRows(rgba, new(pointer.Width, pointer.Height, pointer.Width * 4, 0), target.Address, target.RowBytes);
        _ownedCursor = new Cursor(_cursorBitmap, new PixelPoint(pointer.HotspotX, pointer.HotspotY));
        Cursor = _ownedCursor;
    }
}
