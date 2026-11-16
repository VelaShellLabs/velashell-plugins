using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc;

/// <summary>
/// Draws the remote framebuffer and turns local keyboard and mouse input into RFB events. Mirrors the RDP desktop: one
/// ordered input queue (a key-up can never overtake its key-down), frames copied incrementally into one bitmap, the
/// server's cursor shape, window-matching resizes after the drag settles, and display updates paused while hidden.
/// </summary>
internal sealed class VncDesktopControl : Control, IAsyncDisposable
{
    private readonly IVncSession _session;
    private readonly VncSettings _settings;
    private readonly Channel<Func<Task>> _input = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(512)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task _inputPump;
    private readonly Dictionary<PhysicalKey, (uint Keysym, uint ScanCode)> _keys = [];
    private readonly VncInput.WheelAccumulator _wheelY = new(), _wheelX = new();
    private readonly DispatcherTimer _resizeTimer;
    private readonly DispatcherTimer _moveTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private readonly ImeClient _ime;
    private readonly Dictionary<VncPointer, (Cursor Cursor, Bitmap Bitmap)> _cursors = [];
    private WriteableBitmap? _bitmap;
    private VncButtons _buttons;
    private IPointer? _pointer;
    private int _mouseX, _mouseY;
    private bool _movePending;
    private int _framePending, _pointerPending, _disposed;
    private VncPointer? _pendingPointer;
    private string? _preedit;
    private int _requestedWidth, _requestedHeight, _resizeChecks;
    private Window? _window;
    private TopLevel? _topLevel;
    private bool _attached;
    private Task? _focusPublication;

    internal Func<Task>? PublishClipboard { get; set; }
    internal Action? OperationFailed { get; set; }
    internal Action? DesktopSizeChanged { get; set; }
    internal int DesktopWidth { get; private set; }
    internal int DesktopHeight { get; private set; }
    internal bool IsPresenting { get; private set; } = true;
    internal bool ViewOnly => _settings.ViewOnly;

    /// <summary>Scale the desktop into the view (Match window and Scale to fit); false draws it 1:1 with scroll bars.</summary>
    internal bool Fit => _settings.Scaling != VncScaling.None;

    internal Rect DesktopViewport => VncInput.Viewport(Bounds.Size, DesktopWidth, DesktopHeight, Fit, RenderScaling);
    private double RenderScaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
    private static Cursor NoCursor => field ??= new(StandardCursorType.None);

    public VncDesktopControl(IVncSession session, VncSettings settings)
    {
        _session = session;
        _settings = settings;
        Focusable = !settings.ViewOnly;
        ClipToBounds = true;
        _ime = new(this);
        TextInputMethodClientRequested += (_, e) => e.Client = _ime;
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); MatchRemoteToViewport(); };
        _moveTimer.Tick += (_, _) => FlushPointerMove();
        _inputPump = ProcessInputAsync();
        session.FrameAvailable += OnFrame;
        session.PointerChanged += OnPointer;
        OnFrame();
    }

    // ---- frames ----

    private void OnFrame()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _framePending, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _ = Interlocked.Exchange(ref _framePending, 0);
            if (_disposed == 0 && IsPresenting)
            {
                PresentFrame();
            }
        }, DispatcherPriority.Input);   // not Render: a frame flood must not delay clicks and keys
    }

    /// <summary>Copies only what changed straight into the locked bitmap; a new desktop size recreates it.</summary>
    internal void PresentFrame()
    {
        bool resized = false, full = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            VncFrameCopy copy;
            if (_bitmap is null)
            {
                copy = _session.CopyFrame([], 0, 0, 0);
            }
            else
            {
                using ILockedFramebuffer buffer = _bitmap.Lock();
                Span<byte> target;
                unsafe { target = new Span<byte>(buffer.Address.ToPointer(), buffer.RowBytes * buffer.Size.Height); }
                copy = _session.CopyFrame(target, buffer.RowBytes, buffer.Size.Width, buffer.Size.Height, full);
            }

            if (copy.SizeChanged)
            {
                if (copy.Width <= 0 || copy.Height <= 0) { return; }
                _bitmap?.Dispose();
                DesktopWidth = copy.Width;
                DesktopHeight = copy.Height;
                _bitmap = new WriteableBitmap(new(copy.Width, copy.Height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                if (!Fit) { MinWidth = copy.Width / RenderScaling; MinHeight = copy.Height / RenderScaling; }
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
            ScheduleResize();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is { } bitmap)
        {
            Rect target = DesktopViewport;
            double s = RenderScaling;
            bool native = Math.Abs(target.Width * s - bitmap.PixelSize.Width) < 0.5 && Math.Abs(target.Height * s - bitmap.PixelSize.Height) < 0.5;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = native ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), target);
            }
        }

        if (!string.IsNullOrEmpty(_preedit) && this.TryFindResource("VelaTextPrimary", out object? resource) && resource is IBrush brush)
        {
            double size = this.TryFindResource("VelaFontSize14", out object? sizeResource) && sizeResource is double fontSize ? fontSize : 14;
            var text = new FormattedText(_preedit, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
            context.DrawText(text, new Point(8, Math.Max(0, Bounds.Height - text.Height - 8)));
        }
    }

    // ---- remote cursor ----

    private void OnPointer(VncPointer pointer)
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

    internal void ApplyPointer(VncPointer pointer)
    {
        if (pointer.IsHidden)
        {
            Cursor = NoCursor;
            return;
        }

        if (!_cursors.TryGetValue(pointer, out (Cursor Cursor, Bitmap Bitmap) entry))
        {
            if (_cursors.Count >= 16)
            {
                foreach ((VncPointer key, (Cursor cursor, Bitmap old)) in _cursors.ToArray())
                {
                    if (ReferenceEquals(cursor, Cursor)) { continue; }
                    cursor.Dispose(); old.Dispose();
                    _ = _cursors.Remove(key);
                }
            }

            var image = new WriteableBitmap(new PixelSize(pointer.Width, pointer.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (ILockedFramebuffer buffer = image.Lock())
            {
                for (int row = 0; row < pointer.Height; row++)
                {
                    Marshal.Copy(pointer.Bgra, row * pointer.Width * 4, buffer.Address + row * buffer.RowBytes, pointer.Width * 4);
                }
            }
            entry = (new Cursor(image, new PixelPoint(pointer.HotspotX, pointer.HotspotY)), image);
            _cursors[pointer] = entry;
        }
        Cursor = entry.Cursor;
    }

    // ---- remote resolution ----

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            ScheduleResize();
        }
    }

    private void ScheduleResize()
    {
        if (_disposed != 0 || _settings.Scaling != VncScaling.Remote) { return; }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    /// <summary>
    /// Asks the server (SetDesktopSize) for a desktop the size of the view in device pixels, so it renders 1:1 instead of
    /// scaled. A no-op when the server does not take it; Fit then scales.
    /// </summary>
    private void MatchRemoteToViewport()
    {
        if (_disposed != 0 || _settings.Scaling != VncScaling.Remote || _settings.ViewOnly)
        {
            return;
        }

        if (!_session.CanResize)
        {
            // The server's ExtendedDesktopSize answer comes with its first update, which on a slow link can land after the
            // first layout: look again a few times (about 3.5 s) before settling for scaling.
            if (++_resizeChecks <= 10) { _resizeTimer.Start(); }
            return;
        }

        double scale = RenderScaling;
        int width = (int)Math.Round(Bounds.Width * scale), height = (int)Math.Round(Bounds.Height * scale);
        if (width < 200 || height < 200) { return; }
        if (Math.Abs(width - DesktopWidth) < 8 && Math.Abs(height - DesktopHeight) < 8) { return; }
        if (width == _requestedWidth && height == _requestedHeight) { return; }
        _requestedWidth = width;
        _requestedHeight = height;
        Queue(() => _session.RequestResizeAsync(width, height));
    }

    // ---- presentation (hidden tab / minimized window) ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        _window?.Deactivated += OnWindowDeactivated;
        _window?.Activated += OnWindowActivated;
        _window?.PropertyChanged += OnWindowPropertyChanged;
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.ScalingChanged += OnScalingChanged;
        _attached = true;
        UpdatePresentation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unhook();
        ReleaseInput();
        _attached = false;
        UpdatePresentation();
        base.OnDetachedFromVisualTree(e);
    }

    private void Unhook()
    {
        _window?.Deactivated -= OnWindowDeactivated;
        _window?.Activated -= OnWindowActivated;
        _window?.PropertyChanged -= OnWindowPropertyChanged;
        _topLevel?.ScalingChanged -= OnScalingChanged;
        _window = null;
        _topLevel = null;
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) { UpdatePresentation(); }
    }

    private void OnScalingChanged(object? sender, EventArgs e) => ScheduleResize();

    private void UpdatePresentation()
    {
        bool shown = _attached && _window?.WindowState != WindowState.Minimized;
        if (shown == IsPresenting || _disposed != 0) { return; }
        IsPresenting = shown;
        Queue(async () =>
        {
            try { await _session.SetDisplayUpdatesAsync(shown); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { /* not connected */ }
        });
        if (shown) { OnFrame(); }
    }

    /// <summary>For a desktop created off screen (reconnected while its tab is in the background).</summary>
    internal void PauseUntilShown()
    {
        if (!_attached) { UpdatePresentation(); }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => ReleaseInput();

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (IsFocused && IsEffectivelyVisible && IsEffectivelyEnabled && PublishClipboard is { } publish) { PublishOnFocus(publish); }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (PublishClipboard is { } publish) { PublishOnFocus(publish); }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        ReleaseInput();
        base.OnLostFocus(e);
    }

    private void PublishOnFocus(Func<Task> publish)
    {
        if (_focusPublication is { IsCompleted: false }) { return; }
        _focusPublication = PublishAsync(publish);
    }

    private async Task PublishAsync(Func<Task> publish)
    {
        try { await publish(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (_disposed == 0) { OperationFailed?.Invoke(); } }
    }

    // ---- input queue ----

    private async Task ProcessInputAsync()
    {
        await foreach (Func<Task> action in _input.Reader.ReadAllAsync())
        {
            try { await action(); }
            catch (Exception) { if (_disposed == 0) { OperationFailed?.Invoke(); } }
        }
    }

    internal void Queue(Func<Task> action)
    {
        FlushPointerMove();
        Enqueue(action);
    }

    private void Enqueue(Func<Task> action)
    {
        if (_disposed != 0) { return; }
        if (!_input.Writer.TryWrite(action))
        {
            OperationFailed?.Invoke();
            // An overflowing queue cannot safely drop a key-up, so the session ends as a lost connection the document sees.
            if (_session is VncSession session) { session.Abort("Remote input could not keep up with local input (the network stalled)."); }
        }
    }

    // ---- keyboard ----

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_disposed != 0 || ViewOnly) { base.OnKeyDown(e); return; }
        VncInput.KeyboardPlatform platform = VncInput.CurrentPlatform;
        bool shortcut = VncInput.IsShortcut(e.KeyModifiers, e.KeySymbol, platform);
        if (e.Key is Key.ImeProcessed or Key.DeadCharProcessed || !shortcut && (VncInput.IsTextKey(e) || !string.IsNullOrEmpty(_preedit)))
        {
            base.OnKeyDown(e);   // the character arrives through OnTextInput
            return;
        }

        (uint keysym, uint scan) = VncInput.Key(e.PhysicalKey, platform, _settings.CommandAsControl);
        if (keysym == 0) { base.OnKeyDown(e); return; }
        _keys[e.PhysicalKey] = (keysym, scan);
        bool paste = keysym == 'v' && (e.KeyModifiers & (VncInput.CurrentPlatform == VncInput.KeyboardPlatform.MacOS && _settings.CommandAsControl ? KeyModifiers.Meta : KeyModifiers.Control)) != 0;
        Queue(async () =>
        {
            if (paste && PublishClipboard is { } publish)
            {
                try { await publish(); } catch (Exception) when (_disposed == 0) { OperationFailed?.Invoke(); }
            }
            await _session.KeyAsync(keysym, scan, true);
        });
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_keys.Remove(e.PhysicalKey, out (uint Keysym, uint ScanCode) key))
        {
            Queue(() => _session.KeyAsync(key.Keysym, key.ScanCode, false));
            e.Handled = true;
        }
        else
        {
            base.OnKeyUp(e);
        }

        if (VncInput.CurrentPlatform == VncInput.KeyboardPlatform.MacOS && e.PhysicalKey is PhysicalKey.MetaLeft or PhysicalKey.MetaRight)
        {
            // AppKit sends no key-up for keys released while Command is held: let go of every ordinary key still down.
            foreach ((PhysicalKey physical, (uint keysym, uint scan)) in _keys.Where(static k => !VncInput.IsModifier(k.Value.Keysym)).ToArray())
            {
                _ = _keys.Remove(physical);
                Queue(() => _session.KeyAsync(keysym, scan, false));
            }
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && !ViewOnly && _disposed == 0)
        {
            // A character typed with Option (macOS) or AltGr (Windows, reported as Ctrl+Alt) arrives while the remote still
            // holds Alt/Ctrl from that modifier's own key-down: lift them around the character and press them again.
            (uint Keysym, uint ScanCode)[] held = [.. _keys.Values.Where(static k => VncInput.IsCharacterModifier(k.Keysym))];
            string text = e.Text;
            Queue(async () =>
            {
                foreach ((uint keysym, uint scan) in held) { await _session.KeyAsync(keysym, scan, false); }
                await TypeAsync(text);
                foreach ((uint keysym, uint scan) in held) { await _session.KeyAsync(keysym, scan, true); }
            });
            _preedit = null;
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    private async Task TypeAsync(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            uint keysym = VncInput.CharacterKeysym(rune);
            await _session.KeyAsync(keysym, 0, true);
            await _session.KeyAsync(keysym, 0, false);
        }
    }

    /// <summary>Presses the keysyms in order and releases them in reverse — combinations the local OS keeps for itself.</summary>
    internal void SendChord(params (uint Keysym, uint ScanCode)[] keys)
    {
        if (ViewOnly) { return; }
        Queue(async () =>
        {
            foreach ((uint keysym, uint scan) in keys) { await _session.KeyAsync(keysym, scan, true); }
            for (int i = keys.Length - 1; i >= 0; i--) { await _session.KeyAsync(keys[i].Keysym, keys[i].ScanCode, false); }
        });
    }

    /// <summary>Types text as keystrokes (sign-in screens, consoles, servers without clipboard support).</summary>
    internal void TypeText(string text)
    {
        if (ViewOnly) { return; }
        Queue(() => TypeAsync(text));
    }

    // ---- pointer ----

    private static VncButtons Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => VncButtons.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => VncButtons.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => VncButtons.Middle,
        _ => VncButtons.None
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (ViewOnly || !Coordinates(e, false)) { return; }
        _ = Focus();
        VncButtons button = Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind);
        if (button == VncButtons.None) { return; }
        _buttons |= button;
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        SendPointer(_buttons);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (ViewOnly) { return; }
        _ = Coordinates(e, true);
        VncButtons button = e.InitialPressMouseButton switch
        {
            MouseButton.Left => VncButtons.Left,
            MouseButton.Right => VncButtons.Right,
            MouseButton.Middle => VncButtons.Middle,
            _ => VncButtons.None
        };
        if ((_buttons & button) != 0)
        {
            _buttons &= ~button;
            SendPointer(_buttons);
        }
        if (_buttons == VncButtons.None) { _pointer = null; e.Pointer.Capture(null); }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        ReleasePointer();
        base.OnPointerCaptureLost(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (ViewOnly || !Coordinates(e, _buttons != VncButtons.None)) { return; }
        // Moves are coalesced: at most one queued at a time, carrying the newest position.
        if (!_moveTimer.IsEnabled) { _moveTimer.Start(); }
        _movePending = true;
        _ime.CursorMoved();
    }

    private void FlushPointerMove()
    {
        _moveTimer.Stop();
        if (!_movePending) { return; }
        _movePending = false;
        SendPointer(_buttons, flush: false);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (ViewOnly || !Coordinates(e, false)) { return; }
        Wheel(_wheelY.Add(e.Delta.Y), VncButtons.WheelUp, VncButtons.WheelDown);
        Wheel(_wheelX.Add(e.Delta.X), VncButtons.WheelLeft, VncButtons.WheelRight);
        e.Handled = true;
    }

    /// <summary>Each notch is a press and release of the wheel "button" (RFC 6143 §7.5.5): up/left positive, down/right negative.</summary>
    private void Wheel(int notches, VncButtons positive, VncButtons negative)
    {
        VncButtons wheel = notches > 0 ? positive : negative;
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            SendPointer(_buttons | wheel);
            SendPointer(_buttons);
        }
    }

    private bool Coordinates(PointerEventArgs e, bool captured)
    {
        if (!VncInput.Map(e.GetPosition(this), DesktopViewport, DesktopWidth, DesktopHeight, captured, out int x, out int y))
        {
            return false;
        }

        _mouseX = x;
        _mouseY = y;
        return true;
    }

    private void SendPointer(VncButtons buttons, bool flush = true)
    {
        int x = _mouseX, y = _mouseY;
        if (flush) { FlushPointerMove(); }
        Enqueue(() => _session.PointerAsync(buttons, x, y));
    }

    internal void ReleaseInput()
    {
        foreach ((uint keysym, uint scan) in _keys.Values) { Queue(() => _session.KeyAsync(keysym, scan, false)); }
        _keys.Clear();
        ReleasePointer();
        _preedit = null;
    }

    private void ReleasePointer()
    {
        if (_buttons != VncButtons.None)
        {
            _buttons = VncButtons.None;
            SendPointer(VncButtons.None);
        }
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { await _inputPump; return; }
        Unhook();
        _resizeTimer.Stop();
        _moveTimer.Stop();
        _session.FrameAvailable -= OnFrame;
        _session.PointerChanged -= OnPointer;
        // Let go of anything still held on the remote before the queue closes.
        foreach ((uint keysym, uint scan) in _keys.Values) { _ = _input.Writer.TryWrite(() => _session.KeyAsync(keysym, scan, false)); }
        _keys.Clear();
        if (_buttons != VncButtons.None) { int x = _mouseX, y = _mouseY; _ = _input.Writer.TryWrite(() => _session.PointerAsync(VncButtons.None, x, y)); }
        Cursor = null;
        foreach ((Cursor cursor, Bitmap bitmap) in _cursors.Values) { cursor.Dispose(); bitmap.Dispose(); }
        _cursors.Clear();
        _ = _input.Writer.TryComplete();
        await _inputPump;
        _bitmap?.Dispose();
        _bitmap = null;
        PublishClipboard = null;
        OperationFailed = null;
    }

    private sealed class ImeClient(VncDesktopControl owner) : TextInputMethodClient
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
