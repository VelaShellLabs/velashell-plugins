using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

namespace VelaShell.Rdp.Channels;

internal interface IPrinterBackend
{
    IPrinterJob Start(PrinterMapping printer);
}

internal interface IPrinterJob : IDisposable
{
    int Write(ReadOnlySpan<byte> data);
    void Complete();
    void Abort();
}

/// <summary>MS-RDPEPC legacy RAW printing. No XPS, driver installation, cache persistence, or IOCTL forwarding.</summary>
internal sealed class PrinterDevice : IRdpdrDevice, IAsyncDisposable
{
    internal const int MaxWriteLength = 1024 * 1024;
    internal const long MaxJobLength = 64L * 1024 * 1024;
    internal const int MaxOpenJobs = 4;
    private const uint InvalidParameter = 0xC000000D, InvalidHandle = 0xC0000008;
    private const uint NotSupported = 0xC00000BB, Cancelled = 0xC0000120, IoError = 0xC0000185;
    private readonly Lock _gate = new();
    private readonly Lock _lifecycleGate = new();
    private Task _cleanup = Task.CompletedTask;
    private Task _requests = Task.CompletedTask;
    private readonly PrinterMapping _mapping;
    private readonly IPrinterBackend _backend;
    private readonly Dictionary<uint, Job> _jobs = [];
    private readonly Queue<uint> _closedIds = new();
    private uint _nextId = 1;
    private int _epoch;
    private int _pending;
    private bool _disposed;

    internal PrinterDevice(PrinterMapping mapping, int number, IPrinterBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        if (number is < 1 or > 9999) { throw new ArgumentOutOfRangeException(nameof(number)); }
        _mapping = mapping;
        _backend = backend ?? new WindowsPrinterBackend();
        Name = "PRN" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public uint DeviceType => 4;
    public string Name { get; }
    public ushort CapabilityType => 2;
    public uint CapabilityVersion => 1;
    public byte[] DeviceData
    {
        get
        {
            byte[] driver = Encoding.Unicode.GetBytes(_mapping.DriverName + '\0');
            byte[] printer = Encoding.Unicode.GetBytes(_mapping.PrinterName + '\0');
            byte[] data = new byte[26 + driver.Length + printer.Length];
            Put(data, 0, (_mapping.IsDefault ? 2u : 0) | (_mapping.IsNetwork ? 4u : 0));
            Put(data, 8, 2); // Empty, null-terminated PnPName; CodePage and CachedFieldsLen stay zero.
            Put(data, 12, (uint)driver.Length);
            Put(data, 16, (uint)printer.Length);
            driver.CopyTo(data, 26);
            printer.CopyTo(data, 26 + driver.Length);
            return data;
        }
    }

    public Task<RdpdrIoResult> ProcessAsync(RdpdrIoRequest request, CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_disposed) { return Task.FromResult(Result(request, Cancelled)); }
            int epoch = _epoch;
            Task cleanup = _cleanup;
            Task previous = _requests;
            if (Interlocked.Increment(ref _pending) > 64)
            {
                _ = Interlocked.Decrement(ref _pending);
                return Task.FromResult(Result(request, 0xC000009A));
            }
            // WRITE offsets are ignored; preserve arrival order even when channel requests overlap.
            Task<RdpdrIoResult> pending = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    await cleanup.ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (epoch != Volatile.Read(ref _epoch)) { return Result(request, Cancelled); }
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return Execute(request, cancellationToken, epoch);
                        }
                        catch (Exception exception) when (exception is IOException or Win32Exception or OperationCanceledException or NotSupportedException or UnauthorizedAccessException)
                        {
                            if (request.MajorFunction is 2 or 4) { Abort(request.FileId); }
                            uint status = exception switch
                            {
                                OperationCanceledException => Cancelled,
                                UnauthorizedAccessException => 0xC0000022,
                                NotSupportedException => NotSupported,
                                Win32Exception { NativeErrorCode: 5 } => 0xC0000022,
                                _ => IoError
                            };
                            return Result(request, status);
                        }
                    }
                }
                finally { _ = Interlocked.Decrement(ref _pending); }
            });
            _requests = pending;
            return pending;
        }
    }

    private RdpdrIoResult Execute(RdpdrIoRequest request, CancellationToken cancellationToken, int epoch)
    {
        byte[] body = request.Body;
        if (request.MajorFunction == 0)
        {
            // MS-RDPEPC 2.2.2.7: all fields except PathLength are unused and ignored.
            if (body.Length != 32 || Get(body, 28) != 0) { return Result(request, InvalidParameter); }
            if (_jobs.Count >= MaxOpenJobs) { return Result(request, 0xC000009A); }
            IPrinterJob native = _backend.Start(_mapping);
            try
            {
                CheckCancellation(cancellationToken, epoch);
                uint id = _closedIds.Count > 0 ? _closedIds.Dequeue() : _nextId++;
                _jobs.Add(id, new Job(native));
                return Result(request, 0, id);
            }
            catch
            {
                Cleanup(native);
                throw;
            }
        }
        if (request.MajorFunction is not (2 or 4)) { return Result(request, NotSupported); }
        if (!_jobs.TryGetValue(request.FileId, out Job? job)) { return Result(request, InvalidHandle); }
        if (request.MajorFunction == 2)
        {
            if (body.Length != 32) { Abort(request.FileId); return Result(request, InvalidParameter); }
            CheckCancellation(cancellationToken, epoch);
            job.Native.Complete();
            _ = _jobs.Remove(request.FileId);
            job.Native.Dispose();
            _closedIds.Enqueue(request.FileId);
            return Result(request, 0);
        }
        if (body.Length < 32 || Get(body, 0) > MaxWriteLength || Get(body, 0) != body.Length - 32)
        {
            Abort(request.FileId);
            return Result(request, InvalidParameter);
        }
        int length = (int)Get(body, 0);
        if (length > MaxJobLength - job.Length)
        {
            Abort(request.FileId);
            return Result(request, 0xC000007F);
        }
        int written = 0;
        while (written < length)
        {
            CheckCancellation(cancellationToken, epoch);
            // Offset is reserved and ignored (MS-RDPEPC 2.2.2.9). Complete short native writes.
            int count = job.Native.Write(body.AsSpan(32 + written, length - written));
            if (count <= 0 || count > length - written) { throw new IOException("Invalid spooler write count."); }
            written += count;
        }
        CheckCancellation(cancellationToken, epoch);
        job.Length += written;
        return Result(request, 0, (uint)written);
    }

    private void CheckCancellation(CancellationToken token, int epoch)
    {
        token.ThrowIfCancellationRequested();
        if (epoch != Volatile.Read(ref _epoch)) { throw new OperationCanceledException(); }
    }

    private static RdpdrIoResult Result(RdpdrIoRequest request, uint status, uint value = 0)
    {
        byte[] body = new byte[request.MajorFunction == 4 ? 5 : 4];
        if (request.MajorFunction is 0 or 4) { Put(body, 0, value); }
        return new(status, body);
    }

    private void Abort(uint id)
    {
        if (_jobs.Remove(id, out Job? job)) { Cleanup(job.Native); }
    }

    private static void Cleanup(IPrinterJob job)
    {
        try { job.Abort(); }
        catch (Exception exception) when (exception is IOException or Win32Exception) { }
        finally
        {
            try { job.Dispose(); }
            catch (Exception exception) when (exception is IOException or Win32Exception) { }
        }
    }

    public void Reset()
    {
        lock (_lifecycleGate)
        {
            _ = Interlocked.Increment(ref _epoch);
            if (!_cleanup.IsCompleted) { return; }
            Task previous = _cleanup;
            // Winspool calls can block in a driver. Never hold the channel/UI thread waiting for them.
            // New requests await this barrier; old requests observe the changed epoch before further writes.
            _cleanup = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (Job job in _jobs.Values) { Cleanup(job.Native); }
                    _jobs.Clear();
                    _closedIds.Clear();
                }
            });
        }
    }

    internal Task DrainAsync()
    {
        lock (_lifecycleGate) { return _cleanup; }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) { return; }
            _disposed = true;
            Reset();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await DrainAsync().ConfigureAwait(false);
    }

    private static uint Get(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private sealed class Job(IPrinterJob native)
    {
        internal IPrinterJob Native { get; } = native;
        internal long Length { get; set; }
    }
}
