using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VelaShell.Rdp.Channels;

/// <summary>Exclusive overlapped Windows port I/O. No unvalidated server IOCTL reaches DeviceIoControl.</summary>
internal sealed partial class WindowsRedirectedPort : IRedirectedPort
{
    private readonly SafeFileHandle _handle;
    private readonly bool _serial;
    private readonly Lock _sync = new();
    private bool _disposed;
    private static int _outstandingNativeOperations;

    private WindowsRedirectedPort(SafeFileHandle handle, bool serial) { _handle = handle; _serial = serial; }

    internal static IRedirectedPort Open(PortMapping mapping)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("COM/LPT redirection requires Windows."); }
        // PortMapping owns the grammar; never concatenate a remote path into this namespace.
        SafeFileHandle handle = Native.CreateFileW(@"\\.\" + mapping.Name, 0xC0000000, 0, 0, 3, 0x40000000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if (Native.GetFileType(handle) != 2)
        {
            handle.Dispose();
            throw new Win32Exception(50, "The selected name is not a character device.");
        }
        return new WindowsRedirectedPort(handle, mapping.IsSerial);
    }

    public async Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken)
    {
        if (length is < 0 or > PortDevice.MaxTransfer) { throw new ArgumentOutOfRangeException(nameof(length)); }
        byte[] output = new byte[length];
        int count = await ExecuteAsync(0, [], output, cancellationToken).ConfigureAwait(false);
        return output.AsSpan(0, count).ToArray();
    }

    public Task<int> WriteAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (data.Length > PortDevice.MaxTransfer) { throw new ArgumentOutOfRangeException(nameof(data)); }
        return ExecuteAsync(1, data, [], cancellationToken);
    }

    public async Task<byte[]> ControlAsync(PortControl control, byte[] input, int outputLength, CancellationToken cancellationToken)
    {
        // Repeat validation at the OS boundary, including calls made without the protocol adapter.
        (_, int size) = PortControls.Validate(_serial, (uint)control, input, checked((uint)outputLength));
        // SERIAL_STATUS has two trailing BOOLEANs and native alignment padding; the wire has only 18 bytes.
        int nativeSize = control == PortControl.GetCommStatus ? 20 : size;
        byte[] output = new byte[nativeSize];
        int count = await ExecuteAsync((uint)control, input, output, cancellationToken).ConfigureAwait(false);
        if (count < size || count > nativeSize) { throw new IOException("Invalid native port control length."); }
        return output.AsSpan(0, size).ToArray();
    }

    private Task<int> ExecuteAsync(uint operation, byte[] input, byte[] output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Interlocked.Increment(ref _outstandingNativeOperations) > 64)
            {
                _ = Interlocked.Decrement(ref _outstandingNativeOperations);
                throw new DriveStatusException(NtStatus.InsufficientResources);
            }
            Task<int> native = new Operation(_handle, operation, input, output).RunAsync(cancellationToken);
            _ = native.ContinueWith(completed =>
            {
                _ = completed.Exception; // Observe late cancellation/error after the caller's deadline.
                _ = Interlocked.Decrement(ref _outstandingNativeOperations);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Return cancellation promptly but retain kernel-owned memory until actual completion.
            return native.WaitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            _ = Native.CancelIoEx(_handle.DangerousGetHandle(), 0);
            _handle.Dispose();
        }
    }

    private sealed class Operation(SafeFileHandle handle, uint operation, byte[] input, byte[] output)
    {
        private readonly EventWaitHandle _completed = new(false, EventResetMode.ManualReset);
        private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GCHandle _inputPin, _outputPin;
        private nint _overlapped;
        private nint _rawHandle;
        private bool _handleHeld;

        internal async Task<int> RunAsync(CancellationToken token)
        {
            RegisteredWaitHandle? wait = null;
            try
            {
                int error = Start();
                if (error is not 0 and not 997) { throw new Win32Exception(error); }
                wait = ThreadPool.RegisterWaitForSingleObject(_completed, (_, _) => _signal.TrySetResult(), null, Timeout.Infinite, true);
                using (token.Register(() => { _ = Native.CancelIoEx(_rawHandle, _overlapped); }))
                {
                    if (error == 0) { _ = _signal.TrySetResult(); }
                    await _signal.Task.ConfigureAwait(false);
                    if (!Native.GetOverlappedResult(_rawHandle, _overlapped, out uint count, false))
                    {
                        int completionError = Marshal.GetLastWin32Error();
                        if (completionError == 995) { throw new OperationCanceledException(token); }
                        throw new Win32Exception(completionError);
                    }
                    if (count > Math.Max(input.Length, output.Length)) { throw new IOException("Invalid native transfer length."); }
                    return checked((int)count);
                }
            }
            finally
            {
                _ = wait?.Unregister(null);
                Free();
                _completed.Dispose();
            }
        }

        private unsafe int Start()
        {
            if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
            handle.DangerousAddRef(ref _handleHeld);
            _rawHandle = handle.DangerousGetHandle();
            _overlapped = (nint)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            ((NativeOverlapped*)_overlapped)->EventHandle = _completed.SafeWaitHandle.DangerousGetHandle();
            _inputPin = GCHandle.Alloc(input, GCHandleType.Pinned);
            _outputPin = GCHandle.Alloc(output, GCHandleType.Pinned);
            bool success = operation switch
            {
                0 => Native.ReadFile(_rawHandle, _outputPin.AddrOfPinnedObject(), (uint)output.Length, 0, _overlapped),
                1 => Native.WriteFile(_rawHandle, _inputPin.AddrOfPinnedObject(), (uint)input.Length, 0, _overlapped),
                _ => Native.DeviceIoControl(_rawHandle, operation, _inputPin.AddrOfPinnedObject(), (uint)input.Length,
                    _outputPin.AddrOfPinnedObject(), (uint)output.Length, 0, _overlapped)
            };
            return success ? 0 : Marshal.GetLastWin32Error();
        }

        private unsafe void Free()
        {
            if (_inputPin.IsAllocated) { _inputPin.Free(); }
            if (_outputPin.IsAllocated) { _outputPin.Free(); }
            NativeMemory.Free((void*)_overlapped);
            if (_handleHeld) { handle.DangerousRelease(); }
        }
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        internal static partial uint GetFileType(SafeFileHandle handle);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ReadFile(nint handle, nint buffer, uint length, nint count, nint overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WriteFile(nint handle, nint buffer, uint length, nint count, nint overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(nint handle, uint code, nint input, uint inputLength, nint output, uint outputLength, nint count, nint overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetOverlappedResult(nint handle, nint overlapped, out uint count, [MarshalAs(UnmanagedType.Bool)] bool wait);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CancelIoEx(nint handle, nint overlapped);
    }
}
