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
    internal readonly List<(uint Flags, uint X, uint Y)> Mouse = [];
    internal readonly List<(int Width, int Height)> Resizes = [];
    private RdpFrame? _frame;
    public RdpFrame? TakeFrame() => Interlocked.Exchange(ref _frame, null);
    internal void Emit(RdpFrame frame) { _frame = frame; FrameAvailable?.Invoke(); }
    internal void Disconnect() { IsConnected = false; Disconnected?.Invoke(null); }
    internal void EmitClipboard(RdpClipboardEvent data) => ClipboardEvent?.Invoke(data);
    public Task KeyAsync(uint code, bool down, CancellationToken ct = default) { Keys.Add((code, down)); return Task.CompletedTask; }
    public Task UnicodeAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task MouseAsync(uint flags, uint x, uint y, CancellationToken ct = default) { Mouse.Add((flags, x, y)); return Task.CompletedTask; }
    public Task RequestResizeAsync(int width, int height, CancellationToken ct = default) { Resizes.Add((width, height)); return Task.CompletedTask; }
    public Task OfferFormatsAsync(string formats, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RequestFormatAsync(uint format, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() { DisposeCount++; IsConnected = false; return ValueTask.CompletedTask; }
}
