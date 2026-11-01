namespace VelaShell.Rdp.Channels;

/// <summary>A locally enumerated WPD interface. Selection is explicit and empty by default.</summary>
public sealed record RdpWpdDevice(string Id, string Name);

internal sealed record WpdDeviceDescription(uint DeviceId, string Name, byte[] HardwareIds, byte[] CompatibleIds);
internal sealed record WpdOpenOptions(uint Access, uint Share, uint Disposition, uint Flags);
internal sealed record WpdIoRequest(uint Function, int Count, ulong Offset, uint ControlCode, byte[] Input, byte[] Output);
internal sealed record WpdIoResult(int HResult, int Count, byte[] Data)
{
    internal static WpdIoResult Error(int error) => new(WpdPolicy.HResult(error), 0, []);
}

internal interface IWpdDeviceBackend
{
    IReadOnlyList<WpdDeviceDescription> Devices { get; }
    IWpdDeviceHandle Open(uint deviceId, WpdOpenOptions options);
}

internal interface IWpdDeviceHandle : IAsyncDisposable
{
    Task<WpdIoResult> ExecuteAsync(WpdIoRequest request, CancellationToken cancellationToken);
}

internal static class WpdPolicy
{
    internal const int MaxTransfer = 1024 * 1024;
    internal const int MaxPending = 4;
    internal static readonly Guid InterfaceClass = new("6ac27878-a6fa-4155-ba85-f98f491d4f33");
    // Windows SDK PortableDevice.h: FILE_DEVICE_WPD=0x40, function 0x42, METHOD_BUFFERED.
    internal const uint ReadIoctl = 0x0040_4108;
    internal const uint ReadWriteIoctl = 0x0040_C108;
    internal static bool IsAllowedIoctl(uint code) => code is ReadIoctl or ReadWriteIoctl;
    internal static int HResult(int error) => error <= 0 ? error : unchecked((int)(0x80070000u | ((uint)error & 0xffff)));
    internal static void ValidateOpen(WpdOpenOptions options)
    {
        if (options.Access is not (0x80000000 or 0xC0000000) || (options.Share & ~3u) != 0 ||
            options.Disposition != 3 || (options.Flags & ~(0x40000000u | 0x80u)) != 0)
        {
            throw new System.ComponentModel.Win32Exception(5);
        }
    }
}
