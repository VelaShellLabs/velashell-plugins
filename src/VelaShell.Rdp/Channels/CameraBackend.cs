using System.Buffers.Binary;

namespace VelaShell.Rdp.Channels;

public sealed record RdpCameraDevice(string Id, string Name);

internal sealed record CameraMediaType(byte Format, uint Width, uint Height, uint FrameRateNumerator,
    uint FrameRateDenominator, uint PixelAspectNumerator, uint PixelAspectDenominator, byte Flags)
{
    internal byte[] Encode()
    {
        byte[] bytes = new byte[26]; bytes[0] = Format;
        uint[] values = [Width, Height, FrameRateNumerator, FrameRateDenominator, PixelAspectNumerator, PixelAspectDenominator];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1 + i * 4), values[i]);
        bytes[25] = Flags; return bytes;
    }
    internal static CameraMediaType Decode(ReadOnlySpan<byte> bytes) => new(bytes[0],
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[5..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[13..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[17..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[21..]), bytes[25]);
}

internal sealed class CameraDeviceRemovedException() : IOException("Selected camera is no longer available.");

internal interface ICameraBackend : IDisposable
{
    IReadOnlyList<CameraMediaType> MediaTypes { get; }
    CameraMediaType CurrentMediaType { get; }
    void Activate();
    void Start(CameraMediaType mediaType);
    Task<byte[]> ReadSampleAsync(CancellationToken ct);
    void Stop();
    void Deactivate();
}
