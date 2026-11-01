using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Rdp.Channels;

[SupportedOSPlatform("windows")]
internal sealed class WindowsWpdNotifications : IWpdNotifications
{
    public IDisposable Register(Action<WpdDeviceChange> changed)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) throw new PlatformNotSupportedException("CM_Register_Notification requires Windows 8 or later.");
        return new Registration(changed);
    }
    private sealed class Registration : IDisposable
    {
        private nint _handle;
        private GCHandle _root;
        private int _closed;
        internal Registration(Action<WpdDeviceChange> changed)
        {
            Callback callback = (_, _, action, data, size) =>
            {
                if (Volatile.Read(ref _closed) != 0 || action > 1 || size < 26 || size > 65536 || Marshal.ReadInt32(data) != 0) return 0;
                try
                {
                    if (Marshal.PtrToStructure<Guid>(data + 8) != WpdPolicy.InterfaceClass) return 0;
                    string path = Marshal.PtrToStringUni(data + 24, (int)(size - 24) / 2) ?? "";
                    int end = path.IndexOf('\0');
                    if (end > 0) changed(new(path[..end], action == 1));
                }
                catch { /* Exceptions must never cross the native callback boundary. */ }
                return 0;
            };
            _root = GCHandle.Alloc(callback);
            var filter = new Filter { Size = 416, ClassGuid = WpdPolicy.InterfaceClass };
            try
            {
                uint result = CM_Register_Notification(ref filter, 0, callback, out _handle);
                if (result != 0) throw new Win32Exception($"CM_Register_Notification failed: CONFIGRET 0x{result:X8}.");
            }
            catch { _root.Free(); throw; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            uint result = CM_Unregister_Notification(_handle);
            if (result != 0) throw new Win32Exception($"CM_Unregister_Notification failed: CONFIGRET 0x{result:X8}; callback retained for safety.");
            _root.Free();
        }
    }
    // cfgmgr32.h: four DWORDs followed by a union whose largest member is WCHAR InstanceId[200].
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct Filter
    {
        [FieldOffset(0)] internal uint Size;
        [FieldOffset(16)] internal Guid ClassGuid;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint Callback(nint notification, nint context, uint action, nint eventData, uint eventDataSize);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Register_Notification(ref Filter filter, nint context, Callback callback, out nint notification);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Unregister_Notification(nint notification);
}
