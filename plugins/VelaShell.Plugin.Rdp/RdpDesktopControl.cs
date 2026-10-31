using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpDesktopControl : Control, IAsyncDisposable
{
    private readonly IRdpSession _session;
    private readonly Channel<Func<Task>> _input = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(256)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _inputPump;
    private Task? _overflowShutdown;
    private readonly HashSet<uint> _keys = [];
    private readonly HashSet<uint> _buttons = [];
    private WriteableBitmap? _bitmap;
    private IPointer? _pointer;
    private uint _mouseX;
    private uint _mouseY;
    private sealed class PendingMove(uint x, uint y) { internal uint X = x; internal uint Y = y; }
    private PendingMove? _pendingMove;
    private bool _fit = true;
    private int _framePending;
    private int _disposed;
    private string? _preedit;
    private readonly ImeClient _ime;
    private readonly DispatcherTimer _resizeTimer;
    private int _requestedWidth;
    private int _requestedHeight;

    internal Func<Task>? PublishClipboard { get; set; }
    internal Action? OperationFailed { get; set; }
    /// <summary>Raised on the UI thread after a frame changes the remote desktop dimensions.</summary>
    internal Action? DesktopSizeChanged { get; set; }
    internal int DesktopWidth { get; private set; }
    internal int DesktopHeight { get; private set; }
    internal bool Fit
    {
        get => _fit;
        set
        {
            _fit = value;
            MinWidth = value ? 0 : DesktopWidth;
            MinHeight = value ? 0 : DesktopHeight;
            InvalidateVisual();
            // Fit mode tracks the viewport; re-request a matching remote resolution when it turns on.
            if (value) ScheduleResize();
        }
    }
    internal Rect DesktopViewport => RdpInput.Viewport(Bounds.Size, DesktopWidth, DesktopHeight, Fit);

    public RdpDesktopControl(IRdpSession session, int width, int height)
    {
        _session = session;
        DesktopWidth = width;
        DesktopHeight = height;
        Focusable = true;
        ClipToBounds = true;
        // Smooth the remote surface when "Fit" scales a resolution that differs from the viewport;
        // at 1:1 (the common case once the desktop matches the tab) it is a no-op.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        _ime = new(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        // Coalesce window/tab resizes: ask the server for a matching desktop only after the drag settles.
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); MatchRemoteToViewport(); };
        _inputPump = ProcessInputAsync();
        session.FrameAvailable += OnFrame;
        OnFrame();
    }

    private void OnFrame()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _framePending, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _framePending, 0);
            if (_disposed != 0) return;
            RdpFrame? frame = _session.TakeFrame();
            if (frame is null) return;
            bool resized = _bitmap is null || frame.Width != DesktopWidth || frame.Height != DesktopHeight;
            if (resized)
            {
                _bitmap?.Dispose();
                DesktopWidth = frame.Width;
                DesktopHeight = frame.Height;
                _bitmap = new WriteableBitmap(new(frame.Width, frame.Height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                if (!_fit) { MinWidth = frame.Width; MinHeight = frame.Height; }
            }
            using (ILockedFramebuffer buffer = _bitmap!.Lock())
            {
                for (int row = 0; row < frame.Height; row++)
                    Marshal.Copy(frame.Pixels, row * frame.Width * 4, buffer.Address + row * buffer.RowBytes, frame.Width * 4);
            }
            InvalidateVisual();
            if (resized) DesktopSizeChanged?.Invoke();
        }, DispatcherPriority.Render);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _fit) ScheduleResize();
    }

    private void ScheduleResize()
    {
        if (_disposed != 0) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>
    /// Asks the server (via the display-control channel) to resize the desktop to the viewport in
    /// device pixels, so Fit renders the remote 1:1 instead of scaling. A no-op when the server does
    /// not support dynamic resolution — the desktop then keeps its size and Fit scales it to fit.
    /// </summary>
    private void MatchRemoteToViewport()
    {
        if (_disposed != 0 || !_fit) return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = (int)Math.Round(Bounds.Width * scale) & ~1;   // even width, as display-control requires
        int height = (int)Math.Round(Bounds.Height * scale) & ~1;
        if (width < 200 || height < 200) return;
        // Ignore sub-8px jitter so a one-pixel layout settle does not drive a server reactivation.
        if (Math.Abs(width - _requestedWidth) < 8 && Math.Abs(height - _requestedHeight) < 8) return;
        _requestedWidth = width;
        _requestedHeight = height;
        Queue(() => _session.RequestResizeAsync(width, height));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is { } bitmap) context.DrawImage(bitmap, new Rect(bitmap.Size), DesktopViewport);
        if (!string.IsNullOrEmpty(_preedit))
        {
            if (this.TryFindResource("VelaTextPrimary", out object? resource) && resource is IBrush brush)
            {
                double size = this.TryFindResource("VelaFontSize14", out object? sizeResource) && sizeResource is double fontSize ? fontSize : 14;
                Typeface typeface = this.TryFindResource("VelaUiFont", out object? fontResource) && fontResource is FontFamily family ? new(family) : Typeface.Default;
                var text = new FormattedText(_preedit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush);
                context.DrawText(text, new Point(8, Math.Max(0, Bounds.Height - text.Height - 8)));
            }
        }
    }

    private async Task ProcessInputAsync()
    {
        await foreach (Func<Task> action in _input.Reader.ReadAllAsync())
        {
            try { await action(); }
            catch (Exception) { if (_disposed == 0) OperationFailed?.Invoke(); }
        }
    }
    internal void Queue(Func<Task> action)
    {
        if (_disposed != 0) return;
        if (!_input.Writer.TryWrite(action))
        {
            OperationFailed?.Invoke();
            // An overflowing input stream cannot safely drop a key-up. End the session instead.
            _overflowShutdown ??= _session.DisposeAsync().AsTask();
        }
    }
    internal void SendSecureAttention() => Queue(async () =>
    {
        await _session.KeyAsync(0x1d, true);
        await _session.KeyAsync(0x38, true);
        await _session.KeyAsync(0x153, true);
        await _session.KeyAsync(0x153, false);
        await _session.KeyAsync(0x38, false);
        await _session.KeyAsync(0x1d, false);
    });

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (PublishClipboard is { } publish) Queue(publish);
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        ReleaseInput();
        base.OnLostFocus(e);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseInput();
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_disposed != 0) return;
        uint code = RdpInput.ScanCode(e.PhysicalKey);
        bool shortcut = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        // Printable local text and IME commits use Unicode; navigation and shortcuts use physical scan codes.
        if (!shortcut && (!string.IsNullOrEmpty(e.KeySymbol) && !e.KeySymbol.Any(char.IsControl) || !string.IsNullOrEmpty(_preedit)))
        { base.OnKeyDown(e); return; }
        if (code == 0) { base.OnKeyDown(e); return; }
        _keys.Add(code);
        bool paste = e.PhysicalKey == PhysicalKey.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
        Queue(async () =>
        {
            if (paste && PublishClipboard is { } publish) await publish();
            await _session.KeyAsync(code, true);
        });
        e.Handled = true;
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        uint code = RdpInput.ScanCode(e.PhysicalKey);
        if (_keys.Remove(code)) { Queue(() => _session.KeyAsync(code, false)); e.Handled = true; }
        else base.OnKeyUp(e);
    }
    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            string text = e.Text;
            Queue(() => _session.UnicodeAsync(text));
            _preedit = null;
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnTextInput(e);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!Coordinates(e, false)) return;
        Focus();
        uint button = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0x1000u,
            PointerUpdateKind.RightButtonPressed => 0x2000u,
            PointerUpdateKind.MiddleButtonPressed => 0x4000u,
            _ => 0
        };
        if (button == 0) return;
        _buttons.Add(button);
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        SendMouse(button | 0x8000u);
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Coordinates(e, true);
        uint button = e.InitialPressMouseButton switch { MouseButton.Left => 0x1000u, MouseButton.Right => 0x2000u, MouseButton.Middle => 0x4000u, _ => 0 };
        if (_buttons.Remove(button)) SendMouse(button);
        if (_buttons.Count == 0) { _pointer = null; e.Pointer.Capture(null); }
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { ReleaseInput(); base.OnPointerCaptureLost(e); }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!Coordinates(e, _buttons.Count != 0)) return;
        if (_pendingMove is { } pending) { pending.X = _mouseX; pending.Y = _mouseY; }
        else
        {
            var move = new PendingMove(_mouseX, _mouseY);
            _pendingMove = move;
            Queue(async () =>
            {
                if (ReferenceEquals(_pendingMove, move)) _pendingMove = null;
                await _session.MouseAsync(0x0800, move.X, move.Y);
            });
        }
        _ime.CursorMoved();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!Coordinates(e, false)) return;
        if (e.Delta.Y != 0) SendMouse(RdpInput.Wheel(e.Delta.Y, false));
        if (e.Delta.X != 0) SendMouse(RdpInput.Wheel(e.Delta.X, true));
        e.Handled = true;
    }
    private bool Coordinates(PointerEventArgs e, bool captured)
    {
        if (!RdpInput.Map(e.GetPosition(this), DesktopViewport, DesktopWidth, DesktopHeight, captured, out uint x, out uint y)) return false;
        _mouseX = x;
        _mouseY = y;
        return true;
    }
    private void SendMouse(uint flags)
    {
        _pendingMove = null;
        uint x = _mouseX, y = _mouseY;
        Queue(() => _session.MouseAsync(flags, x, y));
    }
    internal void ReleaseInput()
    {
        foreach (uint key in _keys) { uint captured = key; Queue(() => _session.KeyAsync(captured, false)); }
        _keys.Clear();
        foreach (uint button in _buttons) SendMouse(button);
        _buttons.Clear();
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        _preedit = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed != 0) { await _inputPump; return; }
        ReleaseInput();
        _resizeTimer.Stop();
        Interlocked.Exchange(ref _disposed, 1);
        _session.FrameAvailable -= OnFrame;
        _input.Writer.TryComplete();
        await _inputPump;
        if (_overflowShutdown is { } shutdown) await shutdown;
        _bitmap?.Dispose();
        _bitmap = null;
        PublishClipboard = null;
        OperationFailed = null;
    }
    private sealed class ImeClient(RdpDesktopControl owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => new(8, Math.Max(0, owner.Bounds.Height - 28), 1, 20);
        public override TextSelection Selection { get => default; set { } }
        public override void SetPreeditText(string? text) { owner._preedit = text; owner.InvalidateVisual(); }
        public override void SetPreeditText(string? text, int? cursorPosition) => SetPreeditText(text);
        public void CursorMoved() => RaiseCursorRectangleChanged();
    }
}
