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
    private readonly ActiveStage _active;
    private DecodedImage _image;
    private readonly InputDatabase _input;
    private readonly CancellationTokenSource _stop = new();
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
    private Task? _dispose;
    private readonly Lock _lifecycle = new();
    public RdpSessionStatus Status => Volatile.Read(ref _status);
    public RdpFrameInfo FrameInfo => _frames.Info;
    public RdpPointer Pointer => Volatile.Read(ref _pointer);
    public event System.Action? FrameAvailable;
    public event System.Action<RdpSessionStatus>? StatusChanged;
    public event System.Action<RdpPointer>? PointerChanged;

    public RdpSession(TcpClient tcp, SslStream stream, ActiveStage active, DecodedImage image, int width, int height)
    {
        _tcp = tcp; _stream = stream; _active = active; _image = image;
        _frames = new(width, height, () => _inputs.Writer.TryWrite(new(RdpInputKind.PointerMove, X: -1)));
        PublishFrame();
        _input = InputDatabase.New();
        _notifier = Task.Run(NotifyAsync);
        _pump = Task.Run(PumpAsync);
    }
    public bool TryCopyFrame(Memory<byte> destination, out RdpFrameInfo frame) => _frames.TryCopy(destination, out frame);
    public async ValueTask SendInputAsync(RdpInput input, CancellationToken cancellationToken = default)
    {
        if (Status.State != RdpSessionState.Connected) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try { await _inputs.Writer.WriteAsync(input, linked.Token).ConfigureAwait(false); }
        catch (ChannelClosedException) { }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
    }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (Status.State == RdpSessionState.Closed) return;
        try { _stop.Cancel(); } catch (ObjectDisposedException) { return; }
        _tcp.Dispose(); // Wakes a pending read even if the peer ignores TLS shutdown.
        await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new(_dispose ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally
        {
            _notifications.Writer.TryComplete();
            try { await _notifier.ConfigureAwait(false); }
            finally
            {
                try { await _stream.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    _tcp.Dispose();
                    // The native pump has exited: no native state can still be in use.
                    try { _input.Dispose(); }
                    finally { try { _image.Dispose(); } finally { _active.Dispose(); } }
                    try { await _frames.DisposeAsync().ConfigureAwait(false); }
                    finally { _stop.Dispose(); _status = new(RdpSessionState.Closed); }
                }
            }
        }
    }
    private async Task PumpAsync()
    {
        Task<byte[]>? read = null;
        Task<bool>? ready = null;
        try
        {
            using var pdu = IronRdpPdu.New();
            while (!_stop.IsCancellationRequested)
            {
                read ??= RdpFraming.ReadRdpAsync(_stream, _stop.Token);
                ready ??= _inputs.Reader.WaitToReadAsync(_stop.Token).AsTask();
                Task winner = await Task.WhenAny(read, ready).ConfigureAwait(false);
                if (winner == read)
                {
                    byte[] bytes = await read.ConfigureAwait(false); read = null;
                    OutputBatch batch;
                    try
                    {
                        using var info = pdu.FindSize(bytes) ?? throw new IOException("Incomplete RDP packet.");
                        if (info.GetLength() != (nuint)bytes.Length) throw new IOException("Invalid RDP packet size.");
                        using var action = info.GetAction();
                        using var outputs = _active.Process(_image, action, bytes);
                        batch = Consume(outputs);
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    await WriteBatchAsync(batch).ConfigureAwait(false);
                    if (batch.Terminate) break;
                    if (batch.Reactivate) await ReactivateAsync().ConfigureAwait(false);
                }
                else
                {
                    bool available = await ready.ConfigureAwait(false); ready = null;
                    if (!available) break;
                    // A finite batch prevents a flooded input queue from starving network reads.
                    for (int count = 0; count < 32 && _inputs.Reader.TryRead(out var command); count++)
                    {
                        if (command.Kind == RdpInputKind.PointerMove && command.X == -1) { PublishFrame(); continue; }
                        foreach (var input in Expand(command))
                        {
                            if (input.Kind == RdpInputKind.KeyDown && _keys.Contains(input.ScanCode))
                            {
                                await RdpSessionFactory.WritePacketAsync(_stream, RdpRepeatInput.EncodePress(input.ScanCode), _stop.Token).ConfigureAwait(false);
                                continue;
                            }
                            OutputBatch batch;
                            using (var operation = CreateOperation(input))
                            using (var events = _input.Apply(operation))
                            using (var outputs = _active.ProcessFastpathInput(_image, events)) batch = Consume(outputs);
                            // Native input processing cannot consume incoming protocol activation; fail closed
                            // if this invariant changes instead of creating a second stream reader.
                            if (batch.Reactivate || batch.Terminate) throw new IOException("Unexpected input-side RDP lifecycle transition.");
                            await WriteBatchAsync(batch).ConfigureAwait(false);
                        }
                    }
                    if (_frames.Pending) PublishFrame();
                }
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
        finally
        {
            _stop.Cancel(); _tcp.Dispose(); _inputs.Writer.TryComplete();
            // Observe the one outstanding read before any native/session disposal.
            if (read is not null) { try { byte[] leftover = await read.ConfigureAwait(false); CryptographicOperations.ZeroMemory(leftover); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { } }
            if (ready is not null) { try { await ready.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            _keys.Clear(); _buttons.Clear();
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
                case ActiveStageOutputType.GraphicsUpdate: PublishFrame(); break;
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
        try { foreach (var bytes in batch.Writes) await RdpSessionFactory.WritePacketAsync(_stream, bytes, _stop.Token).ConfigureAwait(false); }
        finally { foreach (var bytes in batch.Writes) CryptographicOperations.ZeroMemory(bytes); }
    }
    private void PublishFrame()
    {
        if (_frames.Publish(target =>
        {
            using var data = _image.GetData();
            if (data.GetSize() != (nuint)target.Length) throw new IOException("Invalid decoded image byte count.");
            data.Fill(target);
        })) _notifications.Writer.TryWrite(true);
    }
    private async Task ReactivateAsync()
    {
        using var activation = _active.CreateConnectionActivation();
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
                PublishFrame();
                return;
            }
            await RdpSessionFactory.StepAsync(activation, output, _stream, _stop.Token).ConfigureAwait(false);
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
