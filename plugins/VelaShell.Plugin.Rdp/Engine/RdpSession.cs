using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Devolutions.IronRdp;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Rdp.Engine;

internal sealed class RdpSession : IRdpSession
{
    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly ActiveStage? _active;
    private DecodedImage _image;
    private readonly InputDatabase _input;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopToken;
    private readonly Lock _stopGate = new();
    private bool _stopDisposed;
    private bool _stopping;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly FramePublication _publication = new();
    private readonly Channel<RdpInput> _inputs = Channel.CreateBounded<RdpInput>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<bool> _notifications = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly RdpFrameBuffer _frames;
    private int _notificationChanges;
    private RdpSessionStatus _status = new(RdpSessionState.Connected);
    private RdpPointer _pointer = new(RdpPointerKind.Default);
    internal (int X, int Y) ServerPointerPosition { get; private set; }
    private readonly HashSet<ushort> _keys = [];
    private readonly HashSet<RdpMouseButton> _buttons = [];
    private readonly Task _pump;
    private readonly Task _notifier;
    private readonly Task _cleanup;
    internal Task CleanupCompletion => _cleanup;
    private Task? _dispose;
    private readonly Lock _lifecycle = new();
    public RdpSessionStatus Status => Volatile.Read(ref _status);
    public RdpFrameInfo FrameInfo => _frames.Info;
    public RdpPointer Pointer => Volatile.Read(ref _pointer);
    public event System.Action? FrameAvailable;
    public event System.Action<RdpSessionStatus>? StatusChanged;
    public event System.Action<RdpPointer>? PointerChanged;

    public RdpSession(TcpClient tcp, SslStream stream, ActiveStage active, DecodedImage image, int width, int height)
        : this(tcp, stream, active, image, width, height, Task.CompletedTask) { }

    // Offline lifetime tests delay the real pump before its first (deliberately unauthenticated) read.
    // No protocol operation is legal without an ActiveStage; production always supplies one.
    internal RdpSession(SslStream stream, DecodedImage image, int width, int height, Task start)
        : this(new TcpClient(), stream, null, image, width, height, start) { }

    private RdpSession(TcpClient tcp, SslStream stream, ActiveStage? active, DecodedImage image, int width, int height, Task start)
    {
        _tcp = tcp; _stream = stream; _active = active; _image = image;
        _stopToken = _stop.Token;
        _frames = new(width, height, RequestRefresh);
        _input = InputDatabase.New();
        _notifier = Task.Run(NotifyAsync);
        _pump = Task.Run(async () => { await start.ConfigureAwait(false); await PumpAsync().ConfigureAwait(false); });
        _cleanup = Task.Run(CleanupAsync);
        ObserveFailure(_cleanup);
        ObserveFailure(_notifier);
    }
    public bool TryCopyFrame(IntPtr destination, int width, int height, int stride, int capacity, out RdpFrameInfo frame)
        => _frames.TryCopy(destination, width, height, stride, capacity, out frame);
    public void SetFrameConsumer(bool enabled)
    {
        _publication.SetConsumer(enabled);
        RequestRefresh();
    }
    private void RequestRefresh() => _wake.Writer.TryWrite(true);
    public async ValueTask SendInputAsync(RdpInput input, CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (_stopGate)
        {
            if (_stopping || Status.State != RdpSessionState.Connected) return;
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopToken);
        }
        using (linked)
        {
            try { await _inputs.Writer.WriteAsync(input, linked.Token).ConfigureAwait(false); }
            catch (ChannelClosedException) { }
            catch (OperationCanceledException) when (_stopToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
        }
    }
    private void Stop()
    {
        lock (_stopGate)
        {
            _stopping = true;
            if (!_stopDisposed) _stop.Cancel();
        }
        _tcp.Dispose(); // Wakes the unique pending read even if the peer ignores TLS shutdown.
        _inputs.Writer.TryComplete();
        _wake.Writer.TryComplete();
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Stop();
        await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        // Run no cancellation callbacks or disposal code while holding the lifecycle lock.
        lock (_lifecycle) return new(_dispose ??= Task.Run(DisposeCoreAsync));
    }
    private async Task DisposeCoreAsync()
    {
        try
        {
            Stop();
            try { await _cleanup.ConfigureAwait(false); }
            finally { await _notifier.ConfigureAwait(false); }
        }
        finally { Volatile.Write(ref _status, new(RdpSessionState.Closed)); }
    }
    private async Task CleanupAsync()
    {
        try { await _pump.ConfigureAwait(false); }
        finally
        {
            Stop();
            _notifications.Writer.TryComplete();
            // External subscribers may block indefinitely. They never own the decoder or frame cleanup.
            try { await _stream.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { _tcp.Dispose(); }
                finally
                {
                    try { _input.Dispose(); }
                    finally
                    {
                        try { _image.Dispose(); }
                        finally
                        {
                            try { _active?.Dispose(); }
                            finally
                            {
                                try { await _frames.DisposeAsync().ConfigureAwait(false); }
                                finally { lock (_stopGate) { _stopDisposed = true; _stop.Dispose(); } }
                            }
                        }
                    }
                }
            }
        }
    }
    private static void ObserveFailure(Task task)
        => _ = task.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private async Task PumpAsync()
    {
        Task<byte[]>? read = null;
        Task<bool>? ready = null;
        Task<bool>? wake = null;
        bool preferNetwork = true;
        try
        {
            using var pdu = IronRdpPdu.New();
            while (!_stop.IsCancellationRequested)
            {
                read ??= RdpFraming.ReadRdpAsync(_stream, _stopToken);
                ready ??= _inputs.Reader.WaitToReadAsync(_stopToken).AsTask();
                wake ??= _wake.Reader.WaitToReadAsync(_stopToken).AsTask();
                if (!read.IsCompleted && !ready.IsCompleted && !wake.IsCompleted)
                    await Task.WhenAny(read, ready, wake).ConfigureAwait(false);
                if (wake.IsCompleted)
                {
                    bool available = await wake.ConfigureAwait(false); wake = null;
                    _wake.Reader.TryRead(out _);
                    if (!available) break;
                }
                bool network = SelectNetwork(read.IsCompleted, ready.IsCompleted, ref preferNetwork);
                if (network)
                {
                    byte[] bytes = await read.ConfigureAwait(false); read = null;
                    OutputBatch batch;
                    try
                    {
                        using var info = pdu.FindSize(bytes) ?? throw new IOException("Incomplete RDP packet.");
                        if (info.GetLength() != (nuint)bytes.Length) throw new IOException("Invalid RDP packet size.");
                        using var action = info.GetAction();
                        using var outputs = _active!.Process(_image, action, bytes);
                        batch = Consume(outputs);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    await WriteBatchAsync(batch).ConfigureAwait(false);
                    if (batch.Terminate) break;
                    if (batch.Reactivate) await ReactivateAsync().ConfigureAwait(false);
                }
                else if (ready.IsCompleted)
                {
                    bool available = await ready.ConfigureAwait(false); ready = null;
                    if (!available) break;
                    // A finite batch prevents a flooded input queue from starving network reads.
                    for (int count = 0; count < 32 && _inputs.Reader.TryRead(out var command); count++)
                    {
                        foreach (var input in Expand(command))
                        {
                            if (input.Kind == RdpInputKind.KeyDown && _keys.Contains(input.ScanCode))
                            {
                                await RdpSessionFactory.WritePacketAsync(_stream, RdpRepeatInput.EncodePress(input.ScanCode), _stopToken).ConfigureAwait(false);
                                continue;
                            }
                            OutputBatch batch;
                            using (var operation = CreateOperation(input))
                            using (var events = _input.Apply(operation))
                            using (var outputs = _active!.ProcessFastpathInput(_image, events)) batch = Consume(outputs);
                            // Native input processing cannot consume incoming protocol activation; fail closed
                            // if this invariant changes instead of creating a second stream reader.
                            if (batch.Reactivate || batch.Terminate) throw new IOException("Unexpected input-side RDP lifecycle transition.");
                            await WriteBatchAsync(batch).ConfigureAwait(false);
                        }
                    }
                }
                // Native output iterators are already disposed; one complete snapshot per finite turn.
                PublishFrame();
            }
            SetStatus(new(RdpSessionState.Disconnected));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { SetStatus(new(RdpSessionState.Disconnected)); }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { SetStatus(new(RdpSessionState.Disconnected)); }
        catch (IronRdpException ex)
        {
            Exception safe = RdpSessionFactory.SanitizeNative(ex);
            SetStatus(new(RdpSessionState.Faulted, safe.Message));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolConnectionException or InvalidOperationException or OverflowException)
        { SetStatus(new(RdpSessionState.Faulted, "The RDP session ended because the connection or protocol failed.")); }
        catch (Exception)
        {
            SetStatus(new(RdpSessionState.Faulted, "The RDP session ended because the connection or protocol failed."));
            throw; // Cleanup observes the original failure; explicit Dispose can report it.
        }
        finally
        {
            Stop();
            // Observe the one outstanding read before any native/session disposal.
            if (read is not null) { try { byte[] leftover = await read.ConfigureAwait(false); CryptographicOperations.ZeroMemory(leftover); } catch (Exception) { /* The terminal pump owns this read's failure. */ } }
            if (ready is not null) { try { await ready.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            if (wake is not null) { try { await wake.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            _keys.Clear(); _buttons.Clear();
        }
    }
    // Selection is independent of WhenAny's parameter ordering. With both queues ready each gets
    // a turn within one network PDU / 32 input commands; neither owns a second stream reader.
    internal static bool SelectNetwork(bool networkReady, bool inputReady, ref bool preferNetwork)
    {
        if (!networkReady) return false;
        if (!inputReady) { preferNetwork = false; return true; }
        bool network = preferNetwork;
        preferNetwork = !preferNetwork;
        return network;
    }
    internal sealed class FramePublication
    {
        private int _consumer;
        private long _dirty = 1;
        private long _published;
        public void SetConsumer(bool enabled)
        {
            if (Interlocked.Exchange(ref _consumer, enabled ? 1 : 0) == 0 && enabled) MarkDirty();
        }
        public void MarkDirty() => Interlocked.Increment(ref _dirty);
        public bool TryPublish(RdpFrameBuffer frames, System.Action<byte[]> fill)
        {
            long dirty = Volatile.Read(ref _dirty);
            if (Volatile.Read(ref _consumer) == 0 || dirty == _published) return false;
            if (!frames.Publish(fill)) return false; // Lease return wakes the pump; keep the final dirty image.
            _published = dirty; // A concurrent attach increments dirty and is not erased here.
            return true;
        }
    }
    private sealed record OutputBatch(List<byte[]> Writes, bool Reactivate, bool Terminate);
    private OutputBatch Consume(ActiveStageOutputIterator outputs)
    {
        var writes = new List<byte[]>();
        bool reactivate = false, terminate = false;
        int total = 0;
        while (!outputs.IsEmpty())
        {
            using var item = outputs.Next() ?? throw new IOException("Invalid native RDP output iterator.");
            switch (item.GetEnumType())
            {
                case ActiveStageOutputType.ResponseFrame:
                    using (var slice = item.GetResponseFrame())
                    {
                        int length = checked((int)slice.GetSize());
                        RdpFraming.ValidateLength(length, 0);
                        total = checked(total + length);
                        if (total > RdpFraming.MaxPduLength) throw new IOException("RDP response budget exceeded.");
                        var bytes = new byte[length]; slice.Fill(bytes); writes.Add(bytes);
                    }
                    break;
                case ActiveStageOutputType.GraphicsUpdate: _publication.MarkDirty(); break;
                case ActiveStageOutputType.PointerDefault: SetPointer(new(RdpPointerKind.Default)); break;
                case ActiveStageOutputType.PointerHidden: SetPointer(new(RdpPointerKind.Hidden)); break;
                case ActiveStageOutputType.PointerPosition:
                    var position = item.GetPointerPosition();
                    ServerPointerPosition = (position.X, position.Y);
                    // No cross-platform OS pointer warp is offered; keep the latest shape reference stable.
                    break;
                case ActiveStageOutputType.PointerBitmap:
                    using (var pointer = item.GetPointerBitmap())
                    {
                        int width = pointer.GetWidth(), height = pointer.GetHeight();
                        if (width == 0 && height == 0) { SetPointer(new(RdpPointerKind.Hidden)); break; }
                        if (width is < 1 or > 384 || height is < 1 or > 384 || pointer.GetHotspotX() >= width || pointer.GetHotspotY() >= height)
                            throw new IOException("RDP pointer size or hotspot exceeds budget.");
                        int length = checked(width * height * 4);
                        using var data = pointer.GetData();
                        if (data.GetSize() != (nuint)length) throw new IOException("Invalid RDP pointer byte count.");
                        var rgba = new byte[length]; data.Fill(rgba);
                        SetPointer(new(RdpPointerKind.Bitmap, width, height, pointer.GetHotspotX(), pointer.GetHotspotY(), rgba));
                    }
                    break;
                case ActiveStageOutputType.DeactivateAll: reactivate = true; break;
                case ActiveStageOutputType.Terminate: terminate = true; break;
                case ActiveStageOutputType.WindowingOrders: throw new ProtocolConnectionException("RemoteApp windowing is not supported by this desktop session.");
                case ActiveStageOutputType.AutoReconnectFailed: throw new ProtocolConnectionException("Automatic server reconnection was rejected.");
                // Autodetect responses are emitted separately as ResponseFrame. Do not open auxiliary transports,
                // follow redirects, persist reconnect cookies, or enable unsolicited redirections.
                case ActiveStageOutputType.MultitransportRequest:
                case ActiveStageOutputType.AutoDetect:
                case ActiveStageOutputType.SaveSessionInfo:
                case ActiveStageOutputType.AutoReconnectCookie:
                case ActiveStageOutputType.MonitorLayout: break;
                default: throw new IOException("Unknown RDP output.");
            }
        }
        return new(writes, reactivate, terminate);
    }
    private async Task WriteBatchAsync(OutputBatch batch)
    {
        try { foreach (var bytes in batch.Writes) await RdpSessionFactory.WritePacketAsync(_stream, bytes, _stopToken).ConfigureAwait(false); }
        finally { foreach (var bytes in batch.Writes) CryptographicOperations.ZeroMemory(bytes); }
    }
    private void PublishFrame()
    {
        if (_publication.TryPublish(_frames, target =>
        {
            using var data = _image.GetData();
            if (data.GetSize() != (nuint)target.Length) throw new IOException("Invalid decoded image byte count.");
            data.Fill(target);
        })) _notifications.Writer.TryWrite(true);
    }
    private async Task ReactivateAsync()
    {
        using var activation = _active!.CreateConnectionActivation();
        using var output = WriteBuf.New();
        for (int step = 0; step < 256; step++)
        {
            int width = 0, height = 0;
            using (var state = activation.GetState())
            {
                if (state.GetType() == ConnectionActivationStateType.Finalized)
                {
                    using var final = state.GetFinalized();
                    using (var size = final.GetDesktopSize()) { width = size.GetWidth(); height = size.GetHeight(); }
                    RdpLimits.DesktopBytes(width, height);
                    _active.Reactivate(activation.GetIoChannelId(), activation.GetUserChannelId(), final.GetShareId(),
                        final.GetEnableServerPointer(), final.GetPointerSoftwareRendering(), final.GetStaticChannelChunkSize(), final.GetWindowSupportLevel());
                }
            }
            if (width != 0)
            {
                if (width != FrameInfo.Width || height != FrameInfo.Height)
                {
                    RdpLimits.DesktopBytes(width, height);
                    var replacement = DecodedImage.New(PixelFormat.RgbA32, checked((ushort)width), checked((ushort)height));
                    _image.Dispose(); _image = replacement;
                    _frames.Resize(width, height);
                }
                _publication.MarkDirty();
                return;
            }
            await RdpSessionFactory.StepAsync(activation, output, _stream, _stopToken).ConfigureAwait(false);
        }
        throw new IOException("RDP reactivation step budget exceeded.");
    }
    private IEnumerable<RdpInput> Expand(RdpInput input)
    {
        if (input.Kind == RdpInputKind.ReleaseAll)
        {
            foreach (var key in _keys.ToArray()) yield return new(RdpInputKind.KeyUp, key);
            foreach (var button in _buttons.ToArray()) yield return new(RdpInputKind.ButtonUp, Button: button);
        }
        else if (input.Kind == RdpInputKind.CtrlAltDelete)
        {
            // Release the currently tracked state first, then a complete secure-attention chord.
            foreach (var key in _keys.ToArray()) yield return new(RdpInputKind.KeyUp, key);
            yield return new(RdpInputKind.KeyDown, 0x1d); yield return new(RdpInputKind.KeyDown, 0x38); yield return new(RdpInputKind.KeyDown, 0x153);
            yield return new(RdpInputKind.KeyUp, 0x153); yield return new(RdpInputKind.KeyUp, 0x38); yield return new(RdpInputKind.KeyUp, 0x1d);
        }
        else
        {
            if (input.Kind is RdpInputKind.ButtonDown or RdpInputKind.ButtonUp or RdpInputKind.Wheel)
                yield return new(RdpInputKind.PointerMove, X: input.X, Y: input.Y);
            yield return input;
        }
    }
    private Operation CreateOperation(RdpInput input)
    {
        switch (input.Kind)
        {
            case RdpInputKind.KeyDown:
            case RdpInputKind.KeyUp:
                if (input.ScanCode == 0 || input.ScanCode > 0x1ff) throw new IOException("Invalid RDP scan code.");
                using (var scan = Scancode.FromU8((input.ScanCode & 0x100) != 0, (byte)input.ScanCode))
                {
                    if (input.Kind == RdpInputKind.KeyDown) { _keys.Add(input.ScanCode); return scan.AsOperationKeyPressed(); }
                    _keys.Remove(input.ScanCode); return scan.AsOperationKeyReleased();
                }
            case RdpInputKind.PointerMove:
                var frame = FrameInfo;
                using (var position = MousePosition.New(checked((ushort)Math.Clamp(input.X, 0, frame.Width - 1)), checked((ushort)Math.Clamp(input.Y, 0, frame.Height - 1)))) return position.AsMoveOperation();
            case RdpInputKind.ButtonDown:
            case RdpInputKind.ButtonUp:
                using (var button = MouseButton.New(input.Button switch
                {
                    RdpMouseButton.Left => MouseButtonType.Left, RdpMouseButton.Middle => MouseButtonType.Middle,
                    RdpMouseButton.Right => MouseButtonType.Right, RdpMouseButton.X1 => MouseButtonType.X1,
                    RdpMouseButton.X2 => MouseButtonType.X2, _ => throw new IOException("Invalid RDP mouse button.")
                }))
                {
                    if (input.Kind == RdpInputKind.ButtonDown) { _buttons.Add(input.Button); return button.AsOperationMouseButtonPressed(); }
                    _buttons.Remove(input.Button); return button.AsOperationMouseButtonReleased();
                }
            case RdpInputKind.Wheel:
                using (var wheel = WheelRotations.New(!input.Horizontal, input.WheelDelta)) return wheel.AsOperation();
            default: throw new IOException("Invalid RDP input command.");
        }
    }
    private void SetPointer(RdpPointer pointer) { Volatile.Write(ref _pointer, pointer); Interlocked.Exchange(ref _notificationChanges, 1); _notifications.Writer.TryWrite(true); }
    private void SetStatus(RdpSessionStatus status) { Volatile.Write(ref _status, status); Interlocked.Exchange(ref _notificationChanges, 1); _notifications.Writer.TryWrite(true); }
    internal static void NotifySafely(System.Action? handlers)
    {
        if (handlers is null) return;
        foreach (System.Action handler in handlers.GetInvocationList())
        { try { handler(); } catch (Exception) { /* One subscriber cannot suppress another. */ } }
    }
    internal static void NotifySafely<T>(System.Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (System.Action<T> handler in handlers.GetInvocationList())
        { try { handler(value); } catch (Exception) { /* One subscriber cannot suppress another. */ } }
    }
    private async Task NotifyAsync()
    {
        await foreach (bool unused in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            NotifySafely(FrameAvailable);
            if (Interlocked.Exchange(ref _notificationChanges, 0) != 0)
            {
                NotifySafely(StatusChanged, Status);
                NotifySafely(PointerChanged, Pointer);
            }
        }
    }
}
