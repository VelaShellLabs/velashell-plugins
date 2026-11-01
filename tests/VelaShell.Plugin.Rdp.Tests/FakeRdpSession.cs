using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

internal sealed class FakeRdpSession : IRdpSession
{
    public event Action? FrameAvailable;
    public event Action<Exception?>? Disconnected;
    public event Action<RdpClipboardEvent>? ClipboardEvent;
    public bool IsConnected { get; private set; } = true;
    internal int DisposeCount;
    internal readonly List<(uint Code, bool Down)> Keys = [];
    /// <summary>Keys ("1d+" / "1d-") and Unicode runs ("'text'") in the order they were sent.</summary>
    internal readonly List<string> Input = [];
    internal readonly List<(uint Flags, uint X, uint Y)> Mouse = [];
    internal readonly List<(int Width, int Height)> Resizes = [];
    internal readonly List<bool> DisplayUpdates = [];
    internal readonly List<int> ResizeScales = [];
    private RdpFrame? _frame;
    private RdpFrame? _latest;
    private bool _pending;
    public RdpFrame? TakeFrame() => Interlocked.Exchange(ref _frame, null);
    internal void Emit(RdpFrame frame) { _frame = frame; _latest = frame; _pending = true; FrameAvailable?.Invoke(); }
    public RdpFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full = false)
    {
        if (_latest is not { } frame) { return new(width, height, default, false); }
        if (frame.Width != width || frame.Height != height) { return new(frame.Width, frame.Height, default, true); }
        if (!_pending && !full) { return new(width, height, default, false); }
        for (int row = 0; row < height; row++) { frame.Pixels.AsSpan(row * width * 4, width * 4).CopyTo(destination.Slice(row * stride)); }
        _pending = false;
        return new(width, height, new(0, 0, width, height), false);
    }
    internal void Disconnect() { IsConnected = false; Disconnected?.Invoke(null); }
    internal void EmitClipboard(RdpClipboardEvent data) => ClipboardEvent?.Invoke(data);
    public Task KeyAsync(uint code, bool down, CancellationToken ct = default) { Keys.Add((code, down)); Input.Add($"{code:x}{(down ? "+" : "-")}"); return Task.CompletedTask; }
    public Task UnicodeAsync(string text, CancellationToken ct = default) { Input.Add($"'{text}'"); return Task.CompletedTask; }
    public Task MouseAsync(uint flags, uint x, uint y, CancellationToken ct = default) { Mouse.Add((flags, x, y)); return Task.CompletedTask; }
    public Task RequestResizeAsync(int width, int height, CancellationToken ct = default) { Resizes.Add((width, height)); return Task.CompletedTask; }
    public Task RequestResizeAsync(int width, int height, int scalePercent, CancellationToken ct = default) { Resizes.Add((width, height)); ResizeScales.Add(scalePercent); return Task.CompletedTask; }
    public Task SetDisplayUpdatesAsync(bool visible, CancellationToken ct = default) { DisplayUpdates.Add(visible); return Task.CompletedTask; }
    public Task OfferFormatsAsync(string formats, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RequestFormatAsync(uint format, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() { DisposeCount++; IsConnected = false; return ValueTask.CompletedTask; }
}
