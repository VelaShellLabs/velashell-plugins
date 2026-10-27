using System.Collections.Concurrent;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

internal sealed class RdpUiFakeSession : IRdpSession
{
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
    public byte[]? LastCopyBuffer { get; private set; }
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
    public bool TryCopyFrame(Memory<byte> destination, out RdpFrameInfo frame)
    {
        frame = FrameInfo;
        CopyAttempts++;
        if (CopyUnavailable || destination.Length < _frame.Length) return false;
        _frame.CopyTo(destination);
        CopyCount++;
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)destination, out var segment))
            LastCopyBuffer = segment.Array;
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
