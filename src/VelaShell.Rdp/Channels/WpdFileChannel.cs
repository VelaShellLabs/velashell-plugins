using System.Buffers.Binary;
using System.ComponentModel;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPEPNP 2.2.2: one FileRedirectorChannel instance owns one local handle.</summary>
internal sealed class WpdFileChannel(
    IWpdDeviceBackend backend, Func<bool> authenticated, Func<uint, bool> allowed,
    Func<byte[], Task> send, Func<Task> close, Action? released = null,
    Func<uint, Task>? deviceRemoved = null) : IDynamicChannelHandler
{
    private sealed record Pending(CancellationTokenSource Cancellation, TaskCompletionSource Completion);
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, Pending> _pending = [];
    private IWpdDeviceHandle? _handle;
    private bool _createSeen, _closed;
    private Task? _closing;
    internal uint? DeviceId { get; private set; }

    public Task OnOpenAsync() => Task.CompletedTask;
    public async Task OnMessageAsync(byte[] message)
    {
        try
        {
            lock (_gate) { if (_closed) return; }
            if (message.Length < 8 || message.Length > 2 * WpdPolicy.MaxTransfer + 32) throw new InvalidDataException();
            uint id = U32(message, 0) & 0xffffff;
            uint function = U32(message, 4);
            if (function == 6)
            {
                Require(message.Length == 12);
                uint target = U32(message, 8) >> 8;
                lock (_gate)
                {
                    if (!_createSeen || _handle is null) throw new InvalidDataException();
                    if (_pending.TryGetValue(target, out Pending? pending)) pending.Cancellation.Cancel();
                }
                return;
            }
            lock (_gate) { if (_pending.ContainsKey(id)) throw new InvalidDataException(); }
            if (function == 5)
            {
                Require(message.Length == 10);
                byte[] reply = new byte[6];
                BinaryPrimitives.WriteUInt32LittleEndian(reply, id);
                reply[4] = 4;
                await send(reply).ConfigureAwait(false);
                return;
            }
            if (function == 4)
            {
                Require(message.Length == 28);
                int result = 0;
                lock (_gate)
                {
                    if (_closed || _createSeen) throw new InvalidDataException();
                    _createSeen = true;
                    uint deviceId = U32(message, 8);
                    try
                    {
                        if (!authenticated() || !allowed(deviceId)) throw new Win32Exception(5);
                        var options = new WpdOpenOptions(U32(message, 12), U32(message, 16), U32(message, 20), U32(message, 24));
                        WpdPolicy.ValidateOpen(options);
                        DeviceId = deviceId;
                        _handle = backend.Open(deviceId, options);
                    }
                    catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
                    { result = Error(ex); }
                }
                await send(Reply(id, function, new(result, 0, []))).ConfigureAwait(false);
                return;
            }
            if (function > 2) throw new InvalidDataException();
            WpdIoRequest request = ParseRequest(message, function);
            Pending? operation = null;
            IWpdDeviceHandle? handle;
            int error = 0;
            lock (_gate)
            {
                if (_closed) return;
                if (_pending.ContainsKey(id)) throw new InvalidDataException();
                handle = _handle;
                if (handle is null || !authenticated() || DeviceId is not { } device || !allowed(device)) error = 6;
                else if (function == 2 && !WpdPolicy.IsAllowedIoctl(request.ControlCode)) error = 5;
                else if (_pending.Count >= WpdPolicy.MaxPending) error = 170;
                else
                {
                    operation = new(new CancellationTokenSource(), new(TaskCreationOptions.RunContinuationsAsynchronously));
                    _pending.Add(id, operation);
                }
            }
            if (operation is null) await send(Reply(id, function, WpdIoResult.Error(error))).ConfigureAwait(false);
            else _ = RunAsync(id, request, handle!, operation);
        }
        catch (InvalidDataException) { await CloseAsync().ConfigureAwait(false); }
    }

    private async Task RunAsync(uint id, WpdIoRequest request, IWpdDeviceHandle handle, Pending pending)
    {
        bool failed = false, removed = false;
        try
        {
            WpdIoResult result;
            try { result = await handle.ExecuteAsync(request, pending.Cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException or ObjectDisposedException)
            { result = new(Error(ex), 0, []); }
            if (result.Count < 0 || result.Count > request.Count ||
                (request.Function != 1 && result.Data.Length != result.Count)) result = WpdIoResult.Error(13);
            removed = result.HResult == WpdPolicy.HResult(1167);
            if (result.HResult < 0) result = new(result.HResult, 0, []);
            bool closed;
            lock (_gate) closed = _closed;
            if (!closed) await send(Reply(id, request.Function, result)).ConfigureAwait(false);
        }
        catch { failed = true; }
        finally
        {
            lock (_gate) { _pending.Remove(id); pending.Cancellation.Dispose(); }
            pending.Completion.TrySetResult();
        }
        if (removed && deviceRemoved is not null && DeviceId is { } deviceId)
            await deviceRemoved(deviceId).ConfigureAwait(false);
        if (failed) await CloseAsync().ConfigureAwait(false);
    }

    internal async Task CloseAsync()
    {
        try { await close().ConfigureAwait(false); }
        finally { await OnCloseAsync().ConfigureAwait(false); }
    }

    public Task OnCloseAsync()
    {
        lock (_gate)
        {
            if (_closing is not null) return _closing;
            _closed = true;
            Pending[] pending = _pending.Values.ToArray();
            foreach (Pending operation in pending) operation.Cancellation.Cancel();
            _closing = FinishCloseAsync(pending, _handle);
            return _closing;
        }
    }

    private async Task FinishCloseAsync(Pending[] pending, IWpdDeviceHandle? handle)
    {
        try
        {
            await Task.WhenAll(pending.Select(p => p.Completion.Task)).ConfigureAwait(false);
            if (handle is not null) await handle.DisposeAsync().ConfigureAwait(false);
        }
        finally { released?.Invoke(); }
    }

    private static WpdIoRequest ParseRequest(byte[] message, uint function)
    {
        Require(message.Length >= 20);
        if (function is 0 or 1)
        {
            int count = Count(message, 8);
            Require(message.Length == (function == 0 ? 20 : 21 + count));
            ulong offset = ((ulong)U32(message, 12) << 32) | U32(message, 16);
            return new(function, count, offset, 0, function == 1 ? message.AsSpan(20, count).ToArray() : [], []);
        }
        int input = Count(message, 12), output = Count(message, 16);
        Require(message.Length == 21 + input || message.Length == 21 + input + output);
        return new(function, output, 0, U32(message, 8), message.AsSpan(20, input).ToArray(),
            message.Length == 21 + input + output ? message.AsSpan(20 + input, output).ToArray() : []);
    }

    private static byte[] Reply(uint id, uint function, WpdIoResult result)
    {
        byte[] bytes = new byte[function == 4 ? 8 : function == 1 ? 12 : 13 + result.Data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, id);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), result.HResult);
        if (function != 4) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)result.Count);
        if (function is 0 or 2) result.Data.CopyTo(bytes, 12);
        return bytes;
    }
    private static int Error(Exception ex) => ex switch
    {
        Win32Exception win32 => WpdPolicy.HResult(win32.NativeErrorCode),
        OperationCanceledException => WpdPolicy.HResult(995),
        UnauthorizedAccessException => WpdPolicy.HResult(5),
        ObjectDisposedException => WpdPolicy.HResult(6),
        _ => ex.HResult < 0 ? ex.HResult : WpdPolicy.HResult(31)
    };
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static int Count(byte[] bytes, int offset)
    {
        uint value = U32(bytes, offset);
        Require(value <= WpdPolicy.MaxTransfer);
        return (int)value;
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException("Invalid WPD request."); }
}
