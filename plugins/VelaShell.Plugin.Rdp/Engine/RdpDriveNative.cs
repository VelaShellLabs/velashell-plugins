using System.Runtime.InteropServices;
using System.Text;
using Devolutions.IronRdp;
using Devolutions.IronRdp.Raw;
using ClientConnector = Devolutions.IronRdp.ClientConnector;
using IronRdpError = Devolutions.IronRdp.IronRdpError;

namespace VelaShell.Plugin.Rdp.Engine;

internal sealed record RdpDriveMapping(uint DeviceId, string Name, string Root, bool ReadOnly);

/// <summary>Windows logical-volume configuration copied into the Rust-owned filesystem backend.</summary>
internal static class RdpDriveNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NewConfig();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyConfig(IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ConnectorFfiResultVoidBoxIronRdpError AddDrive(
        IntPtr config, uint id, IntPtr name, nuint nameLength, IntPtr root, nuint rootLength, [MarshalAs(UnmanagedType.U1)] bool readOnly);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ConnectorFfiResultVoidBoxIronRdpError AttachConfig(
        IntPtr connector, IntPtr name, nuint nameLength, IntPtr config);

    internal static unsafe void Attach(ClientConnector connector, IReadOnlyList<RdpDriveMapping> drives)
    {
        if (!OperatingSystem.IsWindows() || !RdpClipboardNative.IsAvailable)
            throw new PlatformNotSupportedException("Drive redirection requires the Windows RDP native extension.");
        IntPtr library = NativeLibrary.Load("DevolutionsIronRdp", typeof(ClientConnector).Assembly, null);
        try
        {
            var create = Marshal.GetDelegateForFunctionPointer<NewConfig>(NativeLibrary.GetExport(library, "WindowsRdpdrConfig_new"));
            var destroy = Marshal.GetDelegateForFunctionPointer<DestroyConfig>(NativeLibrary.GetExport(library, "WindowsRdpdrConfig_destroy"));
            var add = Marshal.GetDelegateForFunctionPointer<AddDrive>(NativeLibrary.GetExport(library, "WindowsRdpdrConfig_add_drive"));
            var attach = Marshal.GetDelegateForFunctionPointer<AttachConfig>(NativeLibrary.GetExport(library, "ClientConnector_with_static_channel_windows_rdpdr"));
            IntPtr config = create();
            if (config == IntPtr.Zero) throw new IOException("RDP drive configuration failed.");
            try
            {
                foreach (var drive in drives)
                {
                    byte[] name = Encoding.UTF8.GetBytes(drive.Name), root = Encoding.UTF8.GetBytes(drive.Root);
                    fixed (byte* n = name)
                    fixed (byte* r = root)
                        Check(add(config, drive.DeviceId, (IntPtr)n, (nuint)name.Length, (IntPtr)r, (nuint)root.Length, drive.ReadOnly));
                }
                byte[] computer = "VelaShell"u8.ToArray();
                fixed (byte* name = computer)
                    Check(attach((IntPtr)connector.AsFFI(), (IntPtr)name, (nuint)computer.Length, config));
            }
            finally { destroy(config); }
        }
        finally { NativeLibrary.Free(library); GC.KeepAlive(connector); }
    }

    private static unsafe void Check(ConnectorFfiResultVoidBoxIronRdpError result)
    {
        if (!result.isOk) throw new IronRdpException(new IronRdpError(result.Err));
    }
}
