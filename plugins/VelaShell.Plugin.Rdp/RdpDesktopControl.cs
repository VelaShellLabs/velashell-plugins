using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
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
    private Task? _focusPublication;
    private readonly HashSet<uint> _keys = [];
    private readonly HashSet<uint> _buttons = [];
    private WriteableBitmap? _bitmap;
    private IPointer? _pointer;
    private uint _mouseX;
    private uint _mouseY;
    private sealed class PendingMove(uint x, uint y) { internal uint X = x; internal uint Y = y; }
    private PendingMove? _pendingMove;
    private PendingMove? _deferredMove;
    private readonly DispatcherTimer _moveTimer = new();
    private long _lastMove;
    private int _framePending;
    private int _disposed;
    private string? _preedit;
    private readonly ImeClient _ime;
    private readonly DispatcherTimer _resizeTimer;
    private int _requestedWidth;
    private int _requestedHeight;
    private int _requestedScale = 100;
    private Window? _window;
    private TopLevel? _topLevel;
    private bool _attached;

    internal Func<Task>? PublishClipboard { get; set; }
    internal Action? OperationFailed { get; set; }
    /// <summary>Raised on the UI thread after a frame changes the remote desktop dimensions.</summary>
    internal Action? DesktopSizeChanged { get; set; }
    internal int DesktopWidth { get; private set; }
    internal int DesktopHeight { get; private set; }
    internal bool Fit
    {
        get;
        set
        {
            field = value;
            MinWidth = value ? 0 : DesktopWidth;
            MinHeight = value ? 0 : DesktopHeight;
            InvalidateVisual();
            // Fit mode tracks the viewport; re-request a matching remote resolution when it turns on.
            if (value)
            {
                ScheduleResize();
            }
        }
    } = true;
    internal Rect DesktopViewport => RdpInput.Viewport(Bounds.Size, DesktopWidth, DesktopHeight, Fit, RenderScaling);

    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    public RdpDesktopControl(IRdpSession session, int width, int height)
    {
        _session = session;
        DesktopWidth = width;
        DesktopHeight = height;
        Focusable = true;
        ClipToBounds = true;
        _ime = new(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        // Coalesce window/tab resizes: ask the server for a matching desktop only after the drag settles.
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); MatchRemoteToViewport(); };
        _moveTimer.Tick += (_, _) => FlushPointerMove();
        _inputPump = ProcessInputAsync();
        session.FrameAvailable += OnFrame;
        if (session is IRdpPointerSource pointers)
        {
            pointers.PointerChanged += OnPointer;
        }
        OnFrame();
    }

    private void OnFrame()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _framePending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _ = Interlocked.Exchange(ref _framePending, 0);
            if (_disposed != 0 || !IsPresenting)
            {
                return;   // hidden: changes keep accumulating until the view is shown again
            }

            PresentFrame();
        }, DispatcherPriority.Input);   // not Render: that outranks input, so a frame flood could delay clicks and keys
    }

    // ---- Remote pointer ----

    private RdpPointer? _pendingPointer;
    private int _pointerPending;
    private readonly Dictionary<RdpPointer, (Cursor Cursor, Bitmap Bitmap)> _cursors = [];

    private static Cursor NoCursor => field ??= new(StandardCursorType.None);   // created on first use, after platform init

    /// <summary>
    /// The server's pointer shape (I-beam over text, resize arrows, hidden while typing) replaces the local arrow over
    /// the desktop. Raised on the receive thread: the newest shape wins and is applied once on the UI thread.
    /// </summary>
    private void OnPointer(RdpPointer pointer)
    {
        Volatile.Write(ref _pendingPointer, pointer);
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _pointerPending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _ = Interlocked.Exchange(ref _pointerPending, 0);
            if (_disposed == 0 && Volatile.Read(ref _pendingPointer) is { } latest)
            {
                ApplyPointer(latest);
            }
        });
    }

    internal void ApplyPointer(RdpPointer pointer)
    {
        Cursor = pointer.Kind switch
        {
            RdpPointerKind.Hidden => NoCursor,
            RdpPointerKind.Image => CursorFor(pointer),
            _ => null   // the platform default arrow
        };
    }

    private Cursor? CursorFor(RdpPointer pointer)
    {
        if (_cursors.TryGetValue(pointer, out (Cursor Cursor, Bitmap Bitmap) cached))
        {
            return cached.Cursor;
        }

        if (_cursors.Count >= 32)
        {
            // Pointers are cached by the server too (21 slots): an evicted shape is rebuilt if it ever returns.
            foreach ((Cursor cursor, Bitmap bitmap) in _cursors.Values) { if (!ReferenceEquals(cursor, Cursor)) { cursor.Dispose(); bitmap.Dispose(); } }
            _cursors.Clear();
        }

        var image = new WriteableBitmap(new PixelSize(pointer.Width, pointer.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = image.Lock())
        {
            for (int row = 0; row < pointer.Height; row++)
            {
                Marshal.Copy(pointer.Bgra, row * pointer.Width * 4, buffer.Address + row * buffer.RowBytes, pointer.Width * 4);
            }
        }

        var created = new Cursor(image, new PixelPoint(pointer.HotspotX, pointer.HotspotY));
        _cursors[pointer] = (created, image);
        return created;
    }

    /// <summary>
    /// Copies only what changed straight into the locked bitmap — no per-frame W×H×4 allocation and no second full
    /// copy (a blinking caret used to cost two 8 MB copies at 1080p). A new desktop size recreates the bitmap and
    /// takes one full copy.
    /// </summary>
    private unsafe void PresentFrame()
    {
        bool resized = false, full = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            RdpFrameCopy copy;
            if (_bitmap is null)
            {
                copy = _session.CopyFrame([], 0, 0, 0);   // no surface yet: only learn the size
            }
            else
            {
                using ILockedFramebuffer buffer = _bitmap.Lock();
                var target = new Span<byte>((void*)buffer.Address, buffer.RowBytes * buffer.Size.Height);
                copy = _session.CopyFrame(target, buffer.RowBytes, buffer.Size.Width, buffer.Size.Height, full);
            }

            if (copy.SizeChanged)
            {
                if (copy.Width <= 0 || copy.Height <= 0)
                {
                    return;
                }

                _bitmap?.Dispose();
                DesktopWidth = copy.Width;
                DesktopHeight = copy.Height;
                _bitmap = new WriteableBitmap(new(copy.Width, copy.Height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                if (!Fit) { MinWidth = copy.Width; MinHeight = copy.Height; }
                resized = full = true;
                continue;
            }

            if (!copy.Changed.IsEmpty || resized)
            {
                InvalidateVisual();
            }

            break;
        }

        if (resized)
        {
            DesktopSizeChanged?.Invoke();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && Fit)
        {
            ScheduleResize();
        }
    }

    private void ScheduleResize()
    {
        if (_disposed != 0)
        {
            return;
        }

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
        if (_disposed != 0 || !Fit)
        {
            return;
        }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int width = (int)Math.Round(Bounds.Width * scale) & ~1;   // even width, as display-control requires
        int height = (int)Math.Round(Bounds.Height * scale);   // MS-RDPEDISP only requires an even width
        if (width < 200 || height < 200)
        {
            return;
        }
        int percent = (int)Math.Round(scale * 100);
        RdpViewMetrics.Record(width, height, percent);
        // Ignore sub-8px jitter so a one-pixel layout settle does not drive a server reactivation (the few pixels
        // of difference are drawn 1:1 with a thin margin, not resampled); a DPI change always goes through.
        if (Math.Abs(width - _requestedWidth) < 8 && Math.Abs(height - _requestedHeight) < 8 && percent == _requestedScale)
        {
            return;
        }

        _requestedWidth = width;
        _requestedHeight = height;
        _requestedScale = percent;
        Queue(() => _session.RequestResizeAsync(width, height, percent));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is { } bitmap)
        {
            // 1:1 (the normal case): copy pixels exactly, no filtering at all. Real scaling: high-quality filter.
            Rect target = DesktopViewport;
            double s = RenderScaling;
            bool native = Math.Abs(target.Width * s - bitmap.PixelSize.Width) < 0.5 && Math.Abs(target.Height * s - bitmap.PixelSize.Height) < 0.5;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = native ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            }
        }

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
            catch (Exception)
            {
                if (_disposed == 0)
                {
                    OperationFailed?.Invoke();
                }
            }
        }
    }
    internal void Queue(Func<Task> action)
    {
        FlushPointerMove();
        _pendingMove = null;
        EnqueueInput(action);
    }

    private void EnqueueInput(Func<Task> action)
    {
        if (_disposed != 0)
        {
            return;
        }

        if (!_input.Writer.TryWrite(action))
        {
            OperationFailed?.Invoke();
            // An overflowing input stream cannot safely drop a key-up. End the session instead.
            _overflowShutdown ??= _session.DisposeAsync().AsTask();
        }
    }
    internal void SendSecureAttention() => SendChord(0x1d, 0x38, 0x153);

    /// <summary>
    /// Presses the scan codes in order and releases them in reverse — a key combination the local OS would
    /// otherwise keep for itself (Win+L, Ctrl+Shift+Esc, Alt+Tab …), sent through the same ordered input queue.
    /// </summary>
    internal void SendChord(params uint[] codes) => Queue(async () =>
    {
        foreach (uint code in codes) { await _session.KeyAsync(code, true); }
        for (int i = codes.Length - 1; i >= 0; i--) { await _session.KeyAsync(codes[i], false); }
    });

    /// <summary>
    /// Types text as keystrokes: runs of characters go as Unicode input, line breaks as Enter and tabs as Tab, so
    /// it also works where clipboard sharing does not (the sign-in screen, UAC prompts, consoles). The caller passes
    /// text from <see cref="NormalizeTypedText"/>.
    /// </summary>
    internal void TypeText(string text) => Queue(async () =>
    {
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            uint key = i < text.Length ? text[i] switch { '\n' => 0x1cu, '\t' => 0x0fu, _ => 0u } : 0u;
            if (i < text.Length && key == 0) { continue; }
            if (i > start) { await _session.UnicodeAsync(text[start..i]); }
            if (key != 0)
            {
                await _session.KeyAsync(key, true);
                await _session.KeyAsync(key, false);
            }
            start = i + 1;
        }
    });

    /// <summary>Line breaks become '\n'; control characters other than '\n' and '\t' are dropped.</summary>
    internal static string NormalizeTypedText(string text) =>
        string.Concat(text.ReplaceLineEndings("\n").Where(c => !char.IsControl(c) || c is '\n' or '\t'));

    private void PublishOnFocus(Func<Task> publish)
    {
        if (_focusPublication is { IsCompleted: false }) { return; }
        _focusPublication = PublishOnFocusAsync(publish);
    }

    private async Task PublishOnFocusAsync(Func<Task> publish)
    {
        try { await publish(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_disposed == 0) { OperationFailed?.Invoke(); } }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (PublishClipboard is { } publish)
        {
            PublishOnFocus(publish);
        }
    }
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        ReleaseInput();
        base.OnLostFocus(e);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        _window?.Activated += OnWindowActivated;
        _window?.PropertyChanged += OnWindowPropertyChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.ScalingChanged += OnScalingChanged;
        _attached = true;
        UpdatePresentation();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdatePresentation();
        }
    }

    /// <summary>Moving to a monitor with another DPI changes the device-pixel size without changing Bounds.</summary>
    private void OnScalingChanged(object? sender, EventArgs e) => ScheduleResize();

    /// <summary>
    /// The host detaches an inactive tab's view (and a minimized window shows nothing): stop presenting and ask
    /// the server to pause display updates, so a background session costs no bandwidth, decoding or copying.
    /// Changes keep accumulating in the session's dirty region; coming back resumes updates, asks for a repaint
    /// and presents whatever is pending.
    /// </summary>
    private void UpdatePresentation()
    {
        bool shown = _attached && _window?.WindowState != WindowState.Minimized;
        if (shown == IsPresenting || _disposed != 0)
        {
            return;
        }

        IsPresenting = shown;
        Queue(async () =>
        {
            try { await _session.SetDisplayUpdatesAsync(shown); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { /* not connected */ }
        });
        if (shown)
        {
            OnFrame();
        }
    }

    internal bool IsPresenting { get; private set; } = true;

    /// <summary>
    /// For a desktop created off screen (a reconnect while the tab is in the background): pause the server's display
    /// updates now instead of presenting into a view nobody sees; being attached later resumes them with a repaint.
    /// </summary>
    internal void PauseUntilShown()
    {
        if (!_attached)
        {
            UpdatePresentation();
        }
    }
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        // Returning from another application can retain keyboard focus on this desktop.
        if (IsFocused && IsEffectivelyVisible && IsEffectivelyEnabled && PublishClipboard is { } publish)
        {
            PublishOnFocus(publish);
        }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _window?.Activated -= OnWindowActivated;
        _window?.PropertyChanged -= OnWindowPropertyChanged;
        _topLevel?.ScalingChanged -= OnScalingChanged;
        _window = null; _topLevel = null; ReleaseInput();
        _attached = false;
        UpdatePresentation();
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_disposed != 0)
        {
            return;
        }

        uint code = RdpInput.ScanCode(e.PhysicalKey);
        bool shortcut = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
        // IME-owned keys can arrive before preedit, including candidate-selection shortcuts.
        // Printable local text and IME commits use Unicode; navigation and shortcuts use physical scan codes.
        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed
            || !shortcut && (RdpInput.IsTextKey(e) || !string.IsNullOrEmpty(_preedit)))
        { base.OnKeyDown(e); return; }
        if (code == 0) { base.OnKeyDown(e); return; }
        _ = _keys.Add(code);
        bool paste = e.PhysicalKey == PhysicalKey.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && (e.KeyModifiers & (KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
        Queue(async () =>
        {
            if (paste && PublishClipboard is { } publish)
            {
                await publish();
            }

            await _session.KeyAsync(code, true);
        });
        e.Handled = true;
    }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        uint code = RdpInput.ScanCode(e.PhysicalKey);
        if (_keys.Remove(code)) { Queue(() => _session.KeyAsync(code, false)); e.Handled = true; }
        else
        {
            base.OnKeyUp(e);
        }
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
        if (!Coordinates(e, false))
        {
            return;
        }

        _ = Focus();
        uint button = e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => 0x1000u,
            PointerUpdateKind.RightButtonPressed => 0x2000u,
            PointerUpdateKind.MiddleButtonPressed => 0x4000u,
            _ => 0
        };
        if (button == 0)
        {
            return;
        }

        _ = _buttons.Add(button);
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        SendMouse(button | 0x8000u);
        e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _ = Coordinates(e, true);
        uint button = e.InitialPressMouseButton switch { MouseButton.Left => 0x1000u, MouseButton.Right => 0x2000u, MouseButton.Middle => 0x4000u, _ => 0 };
        if (_buttons.Remove(button))
        {
            SendMouse(button);
        }

        if (_buttons.Count == 0) { _pointer = null; e.Pointer.Capture(null); }
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { ReleaseInput(); base.OnPointerCaptureLost(e); }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (!Coordinates(e, _buttons.Count != 0))
        {
            return;
        }

        MovePointer(_mouseX, _mouseY);
        _ime.CursorMoved();
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!Coordinates(e, false))
        {
            return;
        }

        if (e.Delta.Y != 0)
        {
            SendMouse(RdpInput.Wheel(e.Delta.Y, false));
        }

        if (e.Delta.X != 0)
        {
            SendMouse(RdpInput.Wheel(e.Delta.X, true));
        }

        e.Handled = true;
    }
    private bool Coordinates(PointerEventArgs e, bool captured)
    {
        if (!RdpInput.Map(e.GetPosition(this), DesktopViewport, DesktopWidth, DesktopHeight, captured, out uint x, out uint y))
        {
            return false;
        }

        _mouseX = x;
        _mouseY = y;
        return true;
    }
    internal void MovePointer(uint x, uint y)
    {
        int interval = (_session as IRdpNetworkSource)?.NetworkStatus.PointerIntervalMilliseconds ?? 0;
        long remaining = interval - (Environment.TickCount64 - _lastMove);
        if (remaining > 0 && _pendingMove is null)
        {
            _deferredMove = new(x, y);
            if (!_moveTimer.IsEnabled)
            {
                _moveTimer.Interval = TimeSpan.FromMilliseconds(remaining);
                _moveTimer.Start();
            }
            return;
        }
        _deferredMove = new(x, y);
        FlushPointerMove();
    }

    private void FlushPointerMove()
    {
        _moveTimer.Stop();
        if (_deferredMove is not { } move) { return; }
        _deferredMove = null;
        _lastMove = Environment.TickCount64;
        if (_pendingMove is { } pending) { pending.X = move.X; pending.Y = move.Y; return; }
        _pendingMove = move;
        EnqueueInput(async () =>
        {
            if (ReferenceEquals(_pendingMove, move)) { _pendingMove = null; }
            await _session.MouseAsync(0x0800, move.X, move.Y);
        });
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
        foreach (uint button in _buttons)
        {
            SendMouse(button);
        }

        _buttons.Clear();
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        _preedit = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed != 0) { await _inputPump; return; }

        _window?.Activated -= OnWindowActivated;
        _window?.PropertyChanged -= OnWindowPropertyChanged;
        _topLevel?.ScalingChanged -= OnScalingChanged;
        _window = null; _topLevel = null; ReleaseInput();
        _resizeTimer.Stop();
        _moveTimer.Stop();
        _deferredMove = null;
        _ = Interlocked.Exchange(ref _disposed, 1);
        _session.FrameAvailable -= OnFrame;
        if (_session is IRdpPointerSource pointers)
        {
            pointers.PointerChanged -= OnPointer;
        }

        foreach ((Cursor cursor, Bitmap bitmap) in _cursors.Values) { cursor.Dispose(); bitmap.Dispose(); }
        _cursors.Clear();
        _ = _input.Writer.TryComplete();
        await _inputPump;
        if (_overflowShutdown is { } shutdown)
        {
            await shutdown;
        }

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
