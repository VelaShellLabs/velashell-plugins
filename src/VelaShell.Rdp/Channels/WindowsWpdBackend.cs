using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VelaShell.Rdp.Channels;

/// <summary>Enumerates WPD interface metadata without opening devices or reading their contents.</summary>
public static class RdpWpdDevices
{
    public static IReadOnlyList<RdpWpdDevice> Enumerate() => OperatingSystem.IsWindows() ? EnumerateWindows() : [];

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<RdpWpdDevice> EnumerateWindows() =>
        [.. WindowsWpdBackend.EnumerateInterfaces().Select(d => new RdpWpdDevice(d.Path, d.Name))];
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsWpdBackend : IWpdDeviceBackend, IWpdDeviceMonitor
{
    private readonly WpdDeviceMonitor _monitor;
    public IReadOnlyList<WpdDeviceDescription> Devices => _monitor.Devices;
    internal WindowsWpdBackend(IEnumerable<string> selectedIds, Action<string> log)
    {
        _monitor = new(selectedIds, EnumerateInterfaces, OpenDevice, new WindowsWpdNotifications(), log);
    }

    public IWpdDeviceHandle Open(uint deviceId, WpdOpenOptions options) => _monitor.Open(deviceId, options);
    public void Start(Func<uint, Task> removed, Func<WpdDeviceDescription, Task> added) => _monitor.Start(removed, added);
    public ValueTask DisposeAsync() => _monitor.DisposeAsync();

    private static Handle OpenDevice(WpdLocalDevice device, WpdOpenOptions options)
    {
        WpdPolicy.ValidateOpen(options);
        SafeFileHandle handle = Native.CreateFileW(device.Path, options.Access, options.Share, 0, 3,
            options.Flags | 0x40000000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new Handle(handle);
    }

    internal static IReadOnlyList<WpdLocalDevice> EnumerateInterfaces()
    {
        Guid guid = WpdPolicy.InterfaceClass;
        nint set = Native.SetupDiGetClassDevsW(ref guid, null, 0, 0x12);
        if (set == -1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            List<WpdLocalDevice> devices = [];
            for (uint index = 0; index < 1024; index++)
            {
                var item = new Native.InterfaceData { Size = (uint)Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, 0, ref guid, index, ref item))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == 259)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }
                var info = new Native.DeviceInfo { Size = (uint)Marshal.SizeOf<Native.DeviceInfo>() };
                _ = Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, 0, 0, out uint required, ref info);
                if (required is < 6 or > 65536)
                {
                    continue;
                }

                nint buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref item, buffer, required, out _, ref info))
                    {
                        continue;
                    }

                    string? path = Marshal.PtrToStringUni(buffer + 4);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }

                    byte[] name = Property(set, ref info, 12);
                    if (name.Length == 0)
                    {
                        name = Property(set, ref info, 0);
                    }

                    string display = Encoding.Unicode.GetString(name).TrimEnd('\0');
                    devices.Add(new(path, string.IsNullOrWhiteSpace(display) ? "WPD" : display,
                        Property(set, ref info, 1), Property(set, ref info, 2), StableIdentity(set, ref info)));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return devices;
        }
        finally { _ = Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static string? StableIdentity(nint set, ref Native.DeviceInfo info)
    {
        byte[] capabilities = new byte[4];
        if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref info, 15, out uint type, capabilities, 4, out uint size) ||
            type != 4 || size != 4 || (BitConverter.ToUInt32(capabilities) & 0x10) == 0)
        {
            return null;
        }

        var instance = new StringBuilder(200);
        return Native.SetupDiGetDeviceInstanceIdW(set, ref info, instance, 200, out _) && instance.Length != 0
            ? instance.ToString() : null;
    }

    private static byte[] Property(nint set, ref Native.DeviceInfo info, uint property)
    {
        byte[] data = new byte[16384];
        if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out uint type, data, (uint)data.Length, out uint size) ||
            size > data.Length || (size & 1) != 0 || type is not (1 or 7))
        {
            return [];
        }

        return data.AsSpan(0, (int)size).ToArray();
    }

    private sealed class Handle(SafeFileHandle handle) : IWpdDeviceHandle
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _closed = new();
        private readonly HashSet<Task<WpdIoResult>> _operations = [];
        private bool _disposed;
        public Task<WpdIoResult> ExecuteAsync(WpdIoRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (request.Count < 0 || request.Count > WpdPolicy.MaxTransfer || request.Input.Length > WpdPolicy.MaxTransfer ||
                    request.Output.Length > WpdPolicy.MaxTransfer || request.Function > 2 ||
                    (request.Function == 2 && !WpdPolicy.IsAllowedIoctl(request.ControlCode)))
                {
                    return Task.FromResult(WpdIoResult.Error(5));
                }

                var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
                Task<WpdIoResult> operation = Task.Run(() => Execute(request, linked.Token));
                _ = _operations.Add(operation);
                _ = operation.ContinueWith(t => { lock (_gate) { _ = _operations.Remove(t); } linked.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return operation;
            }
        }

        private WpdIoResult Execute(WpdIoRequest request, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return WpdIoResult.Error(995);
            }

            using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
            int outputLength = request.Function == 1 ? 0 : request.Count;
            nint input = 0, output = 0, overlapped = 0;
            try
            {
                input = Marshal.AllocHGlobal(Math.Max(1, request.Input.Length));
                output = Marshal.AllocHGlobal(Math.Max(1, outputLength));
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
                if (request.Input.Length != 0)
                {
                    Marshal.Copy(request.Input, 0, input, request.Input.Length);
                }

                if (outputLength != 0)
                {
                    Marshal.Copy(new byte[outputLength], 0, output, outputLength);
                }

                if (request.Output.Length != 0)
                {
                    Marshal.Copy(request.Output, 0, output, Math.Min(request.Output.Length, outputLength));
                }

                Marshal.StructureToPtr(new Native.Overlapped
                {
                    Offset = (uint)request.Offset,
                    OffsetHigh = (uint)(request.Offset >> 32),
                    Event = completed.SafeWaitHandle.DangerousGetHandle()
                }, overlapped, false);
                bool started = request.Function switch
                {
                    0 => Native.ReadFile(handle, output, (uint)outputLength, 0, overlapped),
                    1 => Native.WriteFile(handle, input, (uint)request.Input.Length, 0, overlapped),
                    _ => Native.DeviceIoControl(handle, request.ControlCode, input, (uint)request.Input.Length,
                        output, (uint)outputLength, 0, overlapped)
                };
                int error = started ? 0 : Marshal.GetLastPInvokeError();
                if (error is not (0 or 997))
                {
                    return WpdIoResult.Error(error);
                }
                // Register after issuance so cancellation cannot race ahead of the native request.
                // The registration is disposed before the OVERLAPPED/buffers are freed.
                using CancellationTokenRegistration cancel = token.Register(() => Native.CancelIoEx(handle, overlapped));
                if (!Native.GetOverlappedResult(handle, overlapped, out uint count, true))
                {
                    return WpdIoResult.Error(Marshal.GetLastPInvokeError());
                }

                int bound = request.Function == 1 ? request.Input.Length : outputLength;
                if (count > bound)
                {
                    return WpdIoResult.Error(13);
                }

                byte[] data = request.Function == 1 ? [] : new byte[count];
                if (data.Length != 0)
                {
                    Marshal.Copy(output, data, 0, data.Length);
                }

                return new(0, (int)count, data);
            }
            finally
            {
                Marshal.FreeHGlobal(overlapped);
                Marshal.FreeHGlobal(output);
                Marshal.FreeHGlobal(input);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task[] pending;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                pending = [.. _operations];
            }
            _closed.Cancel();
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            finally { handle.Dispose(); _closed.Dispose(); }
        }
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct InterfaceData { internal uint Size; internal Guid ClassGuid; internal uint Flags; internal nuint Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfo { internal uint Size; internal Guid ClassGuid; internal uint DevInst; internal nuint Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Overlapped { internal nuint Internal, InternalHigh; internal uint Offset, OffsetHigh; internal nint Event; }
        [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent, uint flags);
        [LibraryImport("setupapi.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetupDiEnumDeviceInterfaces(nint set, nint info, ref Guid guid, uint index, ref InterfaceData data);
        [LibraryImport("setupapi.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetupDiGetDeviceInterfaceDetailW(nint set, ref InterfaceData data, nint detail, uint size, out uint required, ref DeviceInfo info);
        [LibraryImport("setupapi.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo info, uint property, out uint type, [Out] byte[] data, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref DeviceInfo info, StringBuilder instanceId, uint size, out uint required);
        [LibraryImport("setupapi.dll")][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetupDiDestroyDeviceInfoList(nint set);
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ReadFile(SafeFileHandle handle, nint buffer, uint size, nint read, nint overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WriteFile(SafeFileHandle handle, nint buffer, uint size, nint written, nint overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DeviceIoControl(SafeFileHandle handle, uint code, nint input, uint inputSize, nint output, uint outputSize, nint returned, nint overlapped);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetOverlappedResult(SafeFileHandle handle, nint overlapped, out uint count, [MarshalAs(UnmanagedType.Bool)] bool wait);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CancelIoEx(SafeFileHandle handle, nint overlapped);
    }
}
