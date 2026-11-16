using VelaShell.Vnc;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>An in-memory <see cref="IVncSession"/> for workspace and view tests: records input, serves a fixed frame.</summary>
internal sealed class FakeVncSession : IVncSession
{
    private readonly Lock _gate = new();
    private byte[] _frame;
    private int _width, _height;
    private bool _dirty = true;

    internal FakeVncSession(int width = 4, int height = 3)
    {
        _width = width;
        _height = height;
        _frame = new byte[width * height * 4];
    }

    public event Action? FrameAvailable;
    public event Action<Exception?>? Disconnected;
    public event Action<VncPointer>? PointerChanged;
    public event Action<string>? ClipboardReceived;
    public event Action? InfoChanged;

    public bool IsConnected { get; private set; } = true;
    public VncSessionInfo Info { get; set; } = new("3.8", "VNC", "fake", false);
    public string LastEncoding { get; set; } = "Tight";
    public bool CanResize { get; set; }
    public bool SupportsScanCodes { get; set; }
    internal int DisposeCount { get; private set; }
    internal List<(uint Keysym, uint ScanCode, bool Down)> Keys { get; } = [];
    internal List<(VncButtons Buttons, int X, int Y)> Pointer { get; } = [];
    internal List<string> SentClipboard { get; } = [];
    internal List<(int Width, int Height)> Resizes { get; } = [];
    internal List<bool> DisplayUpdates { get; } = [];

    internal void Emit(int width, int height, byte[] bgra)
    {
        lock (_gate) { (_width, _height, _frame, _dirty) = (width, height, bgra, true); }
        FrameAvailable?.Invoke();
    }

    internal void Disconnect(Exception? failure)
    {
        IsConnected = false;
        Disconnected?.Invoke(failure);
    }

    internal void RaiseClipboard(string text) => ClipboardReceived?.Invoke(text);
    internal void RaisePointer(VncPointer pointer) => PointerChanged?.Invoke(pointer);
    internal void RaiseInfo() => InfoChanged?.Invoke();

    public VncFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full = false)
    {
        lock (_gate)
        {
            if (width != _width || height != _height) { return new(_width, _height, default, true); }
            if (!_dirty && !full) { return new(_width, _height, default, false); }
            for (int row = 0; row < height; row++) { _frame.AsSpan(row * width * 4, width * 4).CopyTo(destination[(row * stride)..]); }
            _dirty = false;
            return new(_width, _height, new(0, 0, width, height), false);
        }
    }

    public Task KeyAsync(uint keysym, uint scanCode, bool down, CancellationToken ct = default)
    {
        lock (_gate) { Keys.Add((keysym, scanCode, down)); }
        return Task.CompletedTask;
    }

    public Task PointerAsync(VncButtons buttons, int x, int y, CancellationToken ct = default)
    {
        lock (_gate) { Pointer.Add((buttons, x, y)); }
        return Task.CompletedTask;
    }

    public Task SendClipboardAsync(string text, CancellationToken ct = default)
    {
        lock (_gate) { SentClipboard.Add(text); }
        return Task.CompletedTask;
    }

    public Task RequestResizeAsync(int width, int height, CancellationToken ct = default)
    {
        lock (_gate) { Resizes.Add((width, height)); }
        return Task.CompletedTask;
    }

    public Task SetDisplayUpdatesAsync(bool visible, CancellationToken ct = default)
    {
        lock (_gate) { DisplayUpdates.Add(visible); }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
