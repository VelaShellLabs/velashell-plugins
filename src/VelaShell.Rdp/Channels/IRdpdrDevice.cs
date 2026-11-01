namespace VelaShell.Rdp.Channels;

/// <summary>A non-filesystem RDPDR device. Requests may overlap; Reset cancels open instances.</summary>
internal interface IRdpdrDevice : IDisposable
{
    uint DeviceType { get; }
    string Name { get; }
    ushort CapabilityType { get; }
    uint CapabilityVersion { get; }
    byte[] DeviceData { get; }
    Task<RdpdrIoResult> ProcessAsync(RdpdrIoRequest request, CancellationToken cancellationToken);
    void Reset();
}

internal sealed record RdpdrIoRequest(uint FileId, uint MajorFunction, uint MinorFunction, byte[] Body);
internal sealed record RdpdrIoResult(uint Status, byte[] Body);

internal interface IRedirectedPort : IDisposable
{
    Task<byte[]> ReadAsync(int length, CancellationToken cancellationToken);
    Task<int> WriteAsync(byte[] data, CancellationToken cancellationToken);
    Task<byte[]> ControlAsync(PortControl control, byte[] input, int outputLength, CancellationToken cancellationToken);
}
