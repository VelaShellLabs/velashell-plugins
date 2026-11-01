using System.Buffers.Binary;
using System.ComponentModel;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>MS-RDPESP port requests; the remote never supplies a local device path.</summary>
internal sealed class PortDevice(PortMapping mapping, Func<PortMapping, IRedirectedPort>? open = null,
    TimeSpan? operationTimeout = null) : IRdpdrDevice
{
    internal const int MaxTransfer = 256 * 1024;
    private readonly Lock _sync = new();
    private readonly Func<PortMapping, IRedirectedPort> _open = open ?? WindowsRedirectedPort.Open;
    private readonly TimeSpan _timeout = operationTimeout ?? TimeSpan.FromSeconds(30);
    private IRedirectedPort? _port;
    private uint _fileId;
    private bool _disposed;

    public uint DeviceType => mapping.IsSerial ? 1u : 2u;
    public string Name => mapping.Name;
    public ushort CapabilityType => 3;
    public uint CapabilityVersion => 1;
    public byte[] DeviceData => [];

    public async Task<RdpdrIoResult> ProcessAsync(RdpdrIoRequest request, CancellationToken cancellationToken)
    {
        uint major = request.MajorFunction;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.MinorFunction != 0) { throw new DriveStatusException(NtStatus.NotSupported); }
            if (major == 0)
            {
                ValidateCreate(request.Body);
                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_port is not null) { throw new DriveStatusException(0xC0000043); } // sharing violation
                    IRedirectedPort port = _open(mapping);
                    _fileId = _fileId == uint.MaxValue ? 1 : _fileId + 1;
                    _port = port;
                    var created = new ByteWriter(5);
                    created.U32LE(_fileId);
                    created.U8(1); // FILE_OPENED
                    return new(NtStatus.Success, created.ToArray());
                }
            }

            IRedirectedPort instance;
            lock (_sync)
            {
                if (_disposed || _port is null || request.FileId != _fileId) { throw new DriveStatusException(0xC0000008); }
                instance = _port;
                if (major == 2)
                {
                    if (request.Body.Length != 32) { throw new DriveStatusException(NtStatus.InvalidParameter); }
                    _port = null;
                    instance.Dispose();
                    return new(NtStatus.Success, new byte[4]);
                }
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Reads, writes and WAIT_ON_MASK legitimately wait for the line (or flow control) as long as the
            // remote application's COMMTIMEOUTS allow, exactly as on a local port; a purge, SetCommMask, close,
            // reset or disconnect ends them. Only configuration controls, which never wait for data, get a deadline.
            if (!WaitsForLine(major, request.Body)) { deadline.CancelAfter(_timeout); }
            try
            {
                switch (major)
                {
                    case 3:
                        int length = ValidateTransfer(request.Body, false);
                        byte[] data = await instance.ReadAsync(length, deadline.Token).ConfigureAwait(false);
                        if (data.Length > length) { throw new IOException("Port returned too much data."); }
                        return LengthResponse(data);
                    case 4:
                        int writeLength = ValidateTransfer(request.Body, true);
                        int written = await instance.WriteAsync(request.Body.AsSpan(32, writeLength).ToArray(), deadline.Token).ConfigureAwait(false);
                        if (written < 0 || written > writeLength) { throw new IOException("Invalid port write count."); }
                        var result = new ByteWriter(5);
                        result.U32LE((uint)written);
                        result.U8(0);
                        return new(NtStatus.Success, result.ToArray());
                    case 14:
                        (PortControl control, byte[] input, int capacity) = ParseControl(request.Body);
                        byte[] output = await instance.ControlAsync(control, input, capacity, deadline.Token).ConfigureAwait(false);
                        if (output.Length != capacity) { throw new IOException("Invalid port control response length."); }
                        return LengthResponse(output);
                    default:
                        throw new DriveStatusException(NtStatus.NotSupported);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                return Failure(major, 0xC00000B5); // STATUS_IO_TIMEOUT
            }
        }
        catch (DriveStatusException ex) { return Failure(major, ex.Status); }
        catch (OperationCanceledException) { return Failure(major, 0xC0000120); }
        catch (ObjectDisposedException) { return Failure(major, 0xC0000120); }
        catch (Win32Exception ex) { return Failure(major, NativeStatus(ex.NativeErrorCode)); }
        catch (UnauthorizedAccessException) { return Failure(major, NtStatus.AccessDenied); }
        catch (PlatformNotSupportedException) { return Failure(major, NtStatus.NotSupported); }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or RdpProtocolException) { return Failure(major, NtStatus.InvalidParameter); }
        catch (IOException) { return Failure(major, NtStatus.Unsuccessful); }
    }

    private static bool WaitsForLine(uint major, byte[] body) => major is 3 or 4
        || (major == 14 && body.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)) == (uint)PortControl.WaitOnMask);

    private static void ValidateCreate(byte[] body)
    {
        if (body.Length != 32 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(28)) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)) != 1
            || (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(24)) & 1) != 0)
        {
            throw new DriveStatusException(NtStatus.InvalidParameter);
        }
    }

    private static int ValidateTransfer(byte[] body, bool write)
    {
        if (body.Length < 32) { throw new DriveStatusException(NtStatus.InvalidParameter); }
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(body);
        if (length > MaxTransfer || BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(4)) != 0
            || body.Length != 32 + (write ? length : 0)) { throw new DriveStatusException(NtStatus.InvalidParameter); }
        return (int)length;
    }

    private (PortControl, byte[], int) ParseControl(byte[] body)
    {
        if (body.Length < 32) { throw new DriveStatusException(NtStatus.InvalidParameter); }
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(body);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
        uint code = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        if (length > MaxTransfer || body.Length != 32 + length) { throw new DriveStatusException(NtStatus.InvalidParameter); }
        byte[] input = body.AsSpan(32).ToArray();
        (PortControl control, int outputLength) = PortControls.Validate(mapping.IsSerial, code, input, capacity);
        return (control, input, outputLength);
    }

    private static RdpdrIoResult LengthResponse(byte[] data)
    {
        var body = new ByteWriter(data.Length + 4);
        body.U32LE((uint)data.Length);
        body.Bytes(data);
        return new(NtStatus.Success, body.ToArray());
    }

    internal static RdpdrIoResult Failure(uint major, uint status) => new(status, new byte[major is 0 or 4 ? 5 : 4]);

    internal static uint NativeStatus(int error) => error switch
    {
        2 or 3 => NtStatus.NoSuchFile,
        5 => NtStatus.AccessDenied,
        6 => 0xC0000008,
        32 => 0xC0000043,
        50 or 1 => NtStatus.NotSupported,
        87 => NtStatus.InvalidParameter,
        121 or 1460 => 0xC00000B5,
        995 => 0xC0000120,
        1167 => 0xC000009D,
        _ => NtStatus.Unsuccessful
    };

    public void Reset()
    {
        lock (_sync)
        {
            IRedirectedPort? port = _port;
            _port = null;
            port?.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; Reset(); }
    }
}
