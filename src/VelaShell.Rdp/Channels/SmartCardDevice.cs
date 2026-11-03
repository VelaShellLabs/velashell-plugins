using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>Opt-in forwarding of the local PC/SC resource manager through MS-RDPESC.</summary>
internal sealed class SmartCardDevice : IRdpdrDevice
{
    private const uint Base = 0x90000;
    private readonly ISmartCardBackend _backend;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Context> _contexts = [];
    private ulong _sequence = (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue) << 32;
    private bool _disposed;
    private int _generation;

    internal SmartCardDevice() : this(new PcscSmartCardBackend())
    {
        try
        {
            nint probe = _backend.EstablishContext(2);
            _backend.ReleaseContext(probe);
        }
        catch (Exception ex) when (ex is SmartCardException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Smart-card redirection requires an available local PC/SC service (Windows Smart Card service, pcscd on Linux, or macOS PCSC).", ex);
        }
    }

    internal SmartCardDevice(ISmartCardBackend backend) => _backend = backend;
    public uint DeviceType => 0x20;
    public string Name => "SCARD";
    public ushort CapabilityType => 5;
    public uint CapabilityVersion => 1;
    public byte[] DeviceData => [];

    public Task<RdpdrIoResult> ProcessAsync(RdpdrIoRequest request, CancellationToken cancellationToken)
    {
        int generation;
        lock (_gate) { generation = _generation; }
        return Task.Run(() => Process(request, cancellationToken, generation), CancellationToken.None);
    }

    private RdpdrIoResult Process(RdpdrIoRequest request, CancellationToken token, int generation)
    {
        if (request.MajorFunction != 0x0E) { return Failure(0xC00000BB); }
        uint operation = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            byte[] body = request.Body;
            if (body.Length < 32) { return Failure(0xC000000D); }
            uint outputLength = BinaryPrimitives.ReadUInt32LittleEndian(body);
            uint inputLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            operation = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
            if (inputLength != body.Length - 32 || inputLength > 1024 * 1024 || outputLength > 1024 * 1024)
            {
                return Failure(0xC000000D);
            }
            if (outputLength < (operation == Base + 0x14 ? 40 : operation is Base + 0xAC or Base + 0xB0 ? 64 : 24))
            {
                return Failure(0xC0000023);
            }
            byte[] result;
            try
            {
                result = Execute(operation, body[32..], token, generation);
            }
            catch (SmartCardException ex) { result = Error(operation, ex.Code); }
            catch (OperationCanceledException) { result = Error(operation, SmartCardException.Cancelled); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                result = Error(operation, SmartCardException.NoService);
            }
            if (result.Length > outputLength) { return Failure(0xC0000023); }
            var response = new ByteWriter(result.Length + 4);
            response.U32LE((uint)result.Length); response.Bytes(result);
            return new RdpdrIoResult(0, response.ToArray());
        }
        catch (OperationCanceledException) { return Failure(0xC0000120); }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or DecoderFallbackException)
        {
            return Failure(0xC000000D);
        }
    }

    private byte[] Execute(uint operation, byte[] input, CancellationToken token, int generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) { throw new SmartCardException(SmartCardException.Cancelled); }
        }
        if (operation == Base + 0xE0)
        {
            if (input.Length != 4) { throw new InvalidDataException(); }
            nint probe = _backend.EstablishContext(2);
            _backend.ReleaseContext(probe);
            return Error(operation, 0);
        }
        var r = new SmartCardNdr(input);
        var w = new ByteWriter(128);
        w.U32LE(0);
        if (operation == Base + 0x14)
        {
            uint scope = r.U32(); r.Complete();
            if (scope > 2) { throw new SmartCardException(SmartCardException.InvalidParameter); }
            lock (_gate)
            {
                if (_disposed || generation != _generation) { throw new SmartCardException(SmartCardException.Cancelled); }
                if (_contexts.Count >= 64) { throw new SmartCardException(SmartCardException.NoMemory); }
                nint native = _backend.EstablishContext(scope);
                ulong id = ++_sequence;
                _contexts.Add(id, new Context(native));
                SmartCardNdr.BlobHeader(w, 8, true); SmartCardNdr.Handle(w, id);
            }
            return SmartCardNdr.Encode(w);
        }
        if (!Supported(operation)) { return Error(operation, SmartCardException.Unsupported); }

        bool connect = operation is Base + 0xAC or Base + 0xB0;
        bool cardOperation = operation >= Base + 0xB4 && operation <= Base + 0xD8;
        bool wide = operation is Base + 0x24 or Base + 0x2C or Base + 0xA4 or Base + 0xB0 or Base + 0xCC;
        if (connect && r.U32() == 0) { throw new InvalidDataException("Missing reader name."); }
        var contextHeader = r.BlobHeader(16);
        var cardHeader = cardOperation ? r.BlobHeader(16) : default;
        uint arg1 = 0, arg2 = 0, arg3 = 0;
        bool lengthOnly = false, receivePciPresent = false, readersPresent = false;
        (int Length, bool Present) bufferHeader = default, pciHeader = default;
        int count = 0;
        switch (operation - Base)
        {
            case 0x20: case 0x24: lengthOnly = r.Boolean(); arg1 = r.U32(); break;
            case 0x28: case 0x2C:
                bufferHeader = r.BlobHeader(65536); lengthOnly = r.Boolean(); arg1 = r.U32(); break;
            case 0xA0: case 0xA4:
                arg1 = r.U32(); count = r.Count(11);
                readersPresent = r.U32() != 0;
                if (!readersPresent && count != 0) { throw new InvalidDataException(); }
                break;
            case 0xAC: case 0xB0: arg1 = r.U32(); arg2 = r.U32(); break;
            case 0xB4: arg1 = r.U32(); arg2 = r.U32(); arg3 = r.U32(); break;
            case 0xB8: case 0xBC: case 0xC0: arg1 = r.U32(); break;
            case 0xC4: lengthOnly = r.Boolean(); arg1 = r.U32(); break;
            case 0xC8: case 0xCC: lengthOnly = r.Boolean(); arg1 = r.U32(); _ = r.U32(); break;
            case 0xD0:
                arg1 = r.U32(); pciHeader = r.BlobHeader(1024); bufferHeader = r.BlobHeader();
                receivePciPresent = r.U32() != 0; lengthOnly = r.Boolean(); arg2 = r.U32(); break;
            case 0xD4:
                arg1 = r.U32(); bufferHeader = r.BlobHeader(); lengthOnly = r.Boolean(); arg2 = r.U32(); break;
            case 0xD8: arg1 = r.U32(); lengthOnly = r.Boolean(); arg2 = r.U32(); break;
        }
        string? reader = connect ? r.String(wide) : null;
        ulong contextId = r.Handle(contextHeader);
        ulong cardId = cardOperation ? r.Handle(cardHeader) : 0;
        byte[] extra = operation == Base + 0xD0 ? r.Blob(pciHeader) : [];
        byte[] data = r.Blob(bufferHeader);
        SmartCardPci? receivePci = null;
        if (receivePciPresent)
        {
            uint protocol = r.U32(); var header = r.BlobHeader(1024);
            receivePci = new SmartCardPci(protocol, r.Blob(header));
        }
        SmartCardReaderState[] readers = [];
        if (readersPresent)
        {
            if (r.Count(11) != count) { throw new InvalidDataException(); }
            readers = new SmartCardReaderState[count];
            for (int i = 0; i < count; i++)
            {
                if (r.U32() == 0) { throw new InvalidDataException(); }
                uint current = r.U32(), state = r.U32(); int atrLength = r.Count(36);
                byte[] atr = r.Bytes(36)[..atrLength].ToArray();
                readers[i] = new SmartCardReaderState("", current, state, atr);
            }
            for (int i = 0; i < count; i++) { readers[i] = readers[i] with { Reader = r.String(wide) }; }
        }
        r.Complete();
        Context context = Acquire(contextId);
        bool leased = true;
        int cancelVersion = Volatile.Read(ref context.CancelVersion);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, context.Stop.Token);
            CancellationToken ct = linked.Token;
            ct.ThrowIfCancellationRequested();
            using CancellationTokenRegistration registration = ct.Register(() => CancelNative(context));
            if (operation == Base + 0x18)
            {
                Close(contextId, context);
                leased = false;
                Release(context);
                return Error(operation, context.Released.Task.GetAwaiter().GetResult());
            }
            if (operation == Base + 0xA8)
            {
                Interlocked.Increment(ref context.CancelVersion);
                _backend.Cancel(context.Native);
                return SmartCardNdr.Encode(w);
            }
            if (operation is Base + 0xA0 or Base + 0xA4)
            {
                SmartCardReaderState[] states = WaitForChange(context, arg1, readers, ct, cancelVersion);
                if (states.Length != readers.Length) { throw new SmartCardException(SmartCardException.InvalidParameter); }
                w.U32LE((uint)states.Length); w.U32LE(0x20000); w.U32LE((uint)states.Length);
                foreach (SmartCardReaderState state in states)
                {
                    if (state.Atr.Length > 36) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
                    w.U32LE(state.CurrentState); w.U32LE(state.EventState); w.U32LE((uint)state.Atr.Length);
                    w.Bytes(state.Atr); w.Zeros(36 - state.Atr.Length);
                }
                return SmartCardNdr.Encode(w);
            }
            context.Operations.Wait(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                nint card = 0;
                if (cardOperation && !context.Cards.TryGetValue(cardId, out card)) { throw new SmartCardException(SmartCardException.InvalidHandle); }
                switch (operation - Base)
                {
                    case 0x1C: _backend.IsValidContext(context.Native); break;
                    case 0x20: case 0x24:
                        WriteBuffer(w, MultiString(_backend.ListReaderGroups(context.Native), wide), lengthOnly, arg1, wide ? 2 : 1); break;
                    case 0x28: case 0x2C:
                        string[]? groups = data.Length == 0 ? null : ParseMultiString(data, wide);
                        WriteBuffer(w, MultiString(_backend.ListReaders(context.Native, groups), wide), lengthOnly, arg1, wide ? 2 : 1); break;
                    case 0xAC: case 0xB0:
                        if (context.Cards.Count >= 128) { throw new SmartCardException(SmartCardException.NoMemory); }
                        (nint connected, uint protocol) = _backend.Connect(context.Native, reader!, arg1, arg2);
                        ulong newId;
                        lock (_gate) { newId = ++_sequence; }
                        context.Cards.Add(newId, connected);
                        SmartCardNdr.BlobHeader(w, 8, true); SmartCardNdr.BlobHeader(w, 8, true); w.U32LE(protocol);
                        SmartCardNdr.Handle(w, contextId); SmartCardNdr.Handle(w, newId); break;
                    case 0xB4: w.U32LE(_backend.Reconnect(card, arg1, arg2, arg3)); break;
                    case 0xB8: _backend.Disconnect(card, arg1); _ = context.Cards.Remove(cardId); break;
                    case 0xBC: _backend.BeginTransaction(card); break;
                    case 0xC0: _backend.EndTransaction(card, arg1); break;
                    case 0xC4:
                        SmartCardStatus state = _backend.Status(card);
                        w.U32LE(state.State); w.U32LE(state.Protocol); WriteBuffer(w, state.Atr, lengthOnly, arg1); break;
                    case 0xC8: case 0xCC:
                        SmartCardStatus status = _backend.Status(card);
                        byte[] names = MultiString(status.Readers, wide);
                        CheckCapacity(names.Length / (wide ? 2 : 1), lengthOnly, arg1);
                        if (status.Atr.Length > 32) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
                        bool include = !lengthOnly && arg1 != 0;
                        SmartCardNdr.BlobHeader(w, names.Length, include); w.U32LE(status.State); w.U32LE(status.Protocol);
                        w.Bytes(status.Atr); w.Zeros(32 - status.Atr.Length); w.U32LE((uint)status.Atr.Length);
                        if (include) { SmartCardNdr.Blob(w, names); }
                        break;
                    case 0xD0:
                        SmartCardTransmitResult result = _backend.Transmit(card, new SmartCardPci(arg1, extra), data, receivePci,
                            Capacity(arg2, lengthOnly), lengthOnly);
                        w.U32LE(result.Pci is null ? 0u : 0x20000u);
                        ValidateBuffer(result.Buffer, arg2, lengthOnly);
                        SmartCardNdr.BlobHeader(w, result.Buffer.Length, !lengthOnly);
                        if (result.Pci is not null)
                        {
                            if (result.Pci.Extra.Length > 1024) { throw new InvalidDataException(); }
                            w.U32LE(result.Pci.Protocol); SmartCardNdr.BlobHeader(w, result.Pci.Extra.Length, result.Pci.Extra.Length != 0);
                            if (result.Pci.Extra.Length != 0) { SmartCardNdr.Blob(w, result.Pci.Extra); }
                        }
                        if (!lengthOnly) { SmartCardNdr.Blob(w, result.Buffer.Data); }
                        break;
                    case 0xD4:
                        SmartCardBuffer control = _backend.Control(card, arg1, data, Capacity(arg2, lengthOnly), lengthOnly);
                        ValidateBuffer(control, arg2, lengthOnly);
                        SmartCardNdr.BlobHeader(w, control.Length, !lengthOnly);
                        if (!lengthOnly) { SmartCardNdr.Blob(w, control.Data); }
                        break;
                    case 0xD8: WriteBuffer(w, _backend.GetAttribute(card, arg1), lengthOnly, arg2); break;
                }
            }
            finally { context.Operations.Release(); }
            return SmartCardNdr.Encode(w);
        }
        finally { if (leased) { Release(context); } }
    }

    private SmartCardReaderState[] WaitForChange(Context context, uint timeout, SmartCardReaderState[] readers, CancellationToken token, int version)
    {
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (version != Volatile.Read(ref context.CancelVersion)) { throw new SmartCardException(SmartCardException.Cancelled); }
            uint elapsed = (uint)Math.Min(uint.MaxValue, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            uint slice = timeout == uint.MaxValue ? 250 : Math.Min(250, timeout > elapsed ? timeout - elapsed : 0);
            try { return _backend.GetStatusChange(context.Native, slice, readers); }
            catch (SmartCardException ex) when (ex.Code == SmartCardException.Timeout && (timeout == uint.MaxValue || elapsed + slice < timeout)) { }
        }
    }

    private static bool Supported(uint op) => op - Base is 0x18 or 0x1C or 0x20 or 0x24 or 0x28 or 0x2C or 0xA0 or 0xA4 or 0xA8 or
        0xAC or 0xB0 or 0xB4 or 0xB8 or 0xBC or 0xC0 or 0xC4 or 0xC8 or 0xCC or 0xD0 or 0xD4 or 0xD8;

    private static byte[] MultiString(string[] names, bool wide)
    {
        byte[] bytes = (wide ? Encoding.Unicode : Encoding.ASCII).GetBytes(string.Join('\0', names) + "\0\0");
        if (bytes.Length > 65536) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
        return bytes;
    }

    private static string[] ParseMultiString(byte[] bytes, bool wide)
    {
        string text = SmartCardNdr.Decode(bytes, wide);
        if (!text.EndsWith("\0\0", StringComparison.Ordinal)) { throw new InvalidDataException("Unterminated reader groups."); }
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int Capacity(uint capacity, bool lengthOnly)
    {
        if (lengthOnly) { return 0; }
        if (capacity > SmartCardNdr.MaxBuffer) { throw new SmartCardException(SmartCardException.InvalidParameter); }
        return (int)capacity;
    }

    private static void CheckCapacity(int required, bool lengthOnly, uint capacity)
    {
        if (!lengthOnly && capacity != 0 && capacity != uint.MaxValue && required > capacity)
        {
            throw new SmartCardException(SmartCardException.InsufficientBuffer);
        }
    }

    private static void WriteBuffer(ByteWriter w, byte[] bytes, bool lengthOnly, uint capacity, int characterSize = 1)
    {
        if (bytes.Length > 65536) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
        CheckCapacity(bytes.Length / characterSize, lengthOnly, capacity);
        bool include = !lengthOnly && capacity != 0;
        SmartCardNdr.BlobHeader(w, bytes.Length, include);
        if (include) { SmartCardNdr.Blob(w, bytes); }
    }

    private static void ValidateBuffer(SmartCardBuffer buffer, uint capacity, bool lengthOnly)
    {
        if (buffer.Length < 0 || buffer.Length > SmartCardNdr.MaxBuffer ||
            (!lengthOnly && (buffer.Length != buffer.Data.Length || buffer.Length > capacity)))
        {
            throw new SmartCardException(SmartCardException.InsufficientBuffer);
        }
    }

    private static byte[] Error(uint op, uint code)
    {
        var writer = new ByteWriter(80); writer.U32LE(code);
        int fields = (op - Base) switch
        {
            0x14 or 0x20 or 0x24 or 0x28 or 0x2C or 0x98 or 0x9C or 0xA0 or 0xA4 or 0xD4 or 0xD8 or 0xE8 or 0xEC or 0xF0 or 0xF4 or 0x104 => 2,
            0xAC or 0xB0 => 5,
            0xB4 or 0x100 or 0x108 => 1,
            0xC4 => 4,
            0xC8 or 0xCC => 13,
            0xD0 => 3,
            _ => 0
        };
        writer.Zeros(fields * 4);
        return SmartCardNdr.Encode(writer);
    }

    private static RdpdrIoResult Failure(uint status) => new(status, new byte[4]);

    private Context Acquire(ulong id)
    {
        lock (_gate)
        {
            if (!_contexts.TryGetValue(id, out Context? context) || context.Closing) { throw new SmartCardException(SmartCardException.InvalidHandle); }
            context.Users++;
            return context;
        }
    }

    private void Close(ulong id, Context context)
    {
        lock (_gate)
        {
            if (!_contexts.Remove(id)) { throw new SmartCardException(SmartCardException.InvalidHandle); }
            context.Closing = true;
        }
        context.Stop.Cancel();
        CancelNative(context);
    }

    private void Release(Context context)
    {
        bool clean;
        lock (_gate) { clean = --context.Users == 0 && context.Closing; }
        if (clean)
        {
            uint status = 0;
            try { _backend.ReleaseContext(context.Native); }
            catch (SmartCardException ex) { status = ex.Code; }
            finally
            {
                context.Stop.Dispose(); context.Operations.Dispose();
                context.Released.TrySetResult(status);
            }
        }
    }

    private void CancelNative(Context context)
    {
        try { _backend.Cancel(context.Native); }
        catch (SmartCardException) { }
    }

    public void Reset()
    {
        KeyValuePair<ulong, Context>[] contexts;
        lock (_gate)
        {
            _generation++;
            contexts = [.. _contexts];
            _contexts.Clear();
            foreach (var pair in contexts) { pair.Value.Closing = true; pair.Value.Users++; }
        }
        foreach (var pair in contexts)
        {
            pair.Value.Stop.Cancel(); CancelNative(pair.Value); Release(pair.Value);
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; }
        Reset();
    }

    private sealed class Context(nint native)
    {
        internal readonly nint Native = native;
        internal readonly Dictionary<ulong, nint> Cards = [];
        internal readonly SemaphoreSlim Operations = new(1, 1);
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource<uint> Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Users, CancelVersion;
        internal bool Closing;
    }
}
