using System.Collections.Concurrent;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

internal sealed class RdpUiFakeSession : IRdpSession, IRdpClipboardSession
{
    public bool ClipboardEnabled { get; set; }
    public bool ClipboardFocused { get; private set; }
    public event Action<string>? RemoteTextAvailable;
    public Func<string?, CancellationToken, Task<bool>>? ClipboardHandler { get; set; }
    public void SetClipboardFocus(bool focused) => ClipboardFocused = focused;
    public Task<bool> SynchronizeClipboardAsync(string? text, CancellationToken cancellationToken)
    {
        Inputs.Enqueue(new(RdpInputKind.ClipboardText, Text: text));
        return ClipboardHandler?.Invoke(text, cancellationToken) ?? Task.FromResult(true);
    }
    public void NotifyRemoteText(string text) => RemoteTextAvailable?.Invoke(text);
    private byte[] _frame = [];
    public RdpSessionStatus Status { get; private set; } = new(RdpSessionState.Connected);
    public RdpFrameInfo FrameInfo { get; private set; }
    public RdpPointer Pointer { get; private set; } = new(RdpPointerKind.Default);
    public event Action? FrameAvailable;
    public event Action<RdpSessionStatus>? StatusChanged;
    public event Action<RdpPointer>? PointerChanged;
    public ConcurrentQueue<RdpInput> Inputs { get; } = new();
    public Func<RdpInput, CancellationToken, ValueTask>? InputHandler { get; set; }
    public bool Disposed { get; private set; }
    public int CopyCount { get; private set; }
    public int CopyAttempts { get; private set; }
    public bool CopyUnavailable { get; set; }
    public bool FrameConsumer { get; private set; }
    public Action? BeforeCopy { get; set; }
    public void SetFrameConsumer(bool enabled) => FrameConsumer = enabled;
    public Action? CaptureFrameCallback() => FrameAvailable;
    public Action<RdpSessionStatus>? CaptureStatusCallback() => StatusChanged;
    public void SetFrame(int width = 4, int height = 2, int padding = 0, byte value = 20)
    {
        int stride = width * 4 + padding;
        _frame = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * stride + x * 4;
            _frame[offset] = value;
            _frame[offset + 1] = (byte)y;
            _frame[offset + 2] = (byte)x;
            _frame[offset + 3] = 255;
        }
        FrameInfo = new(width, height, stride, FrameInfo.Sequence + 1);
        FrameAvailable?.Invoke();
    }
    public void NotifyFrame() => FrameAvailable?.Invoke();
    public void SetStatus(RdpSessionState state)
    {
        Status = new(state);
        StatusChanged?.Invoke(Status);
    }
    public void SetPointer(RdpPointer pointer) { Pointer = pointer; PointerChanged?.Invoke(pointer); }
    public bool TryCopyFrame(IntPtr destination, int width, int height, int stride, int capacity, out RdpFrameInfo frame)
    {
        BeforeCopy?.Invoke();
        frame = FrameInfo;
        CopyAttempts++;
        if (CopyUnavailable || frame.Width != width || frame.Height != height || destination == IntPtr.Zero ||
            stride < width * 4 || (long)stride * height > capacity) return false;
        Ui.RdpDesktopSurface.CopyRows(_frame, frame, destination, stride);
        CopyCount++;
        return true;
    }
    public ValueTask SendInputAsync(RdpInput input, CancellationToken cancellationToken = default)
    {
        Inputs.Enqueue(input);
        return InputHandler?.Invoke(input, cancellationToken) ?? ValueTask.CompletedTask;
    }
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(RdpSessionState.Disconnected);
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() { Disposed = true; SetStatus(RdpSessionState.Closed); return ValueTask.CompletedTask; }
}
