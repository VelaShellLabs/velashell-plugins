using System.Buffers.Binary;

namespace VelaShell.Rdp.Graphics;

internal ref struct RfxReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _data = data;
    internal readonly int Remaining => _data.Length;
    internal byte Byte() => Take(1)[0];
    internal ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    internal uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    internal int Length32()
    {
        uint value = UInt32();
        RfxGuard.Require(value <= int.MaxValue, "RemoteFX length exceeds the supported range.");
        return (int)value;
    }
    internal ReadOnlySpan<byte> Take(int length)
    {
        RfxGuard.Require(length >= 0 && length <= _data.Length, "Truncated RemoteFX stream.");
        ReadOnlySpan<byte> result = _data[..length];
        _data = _data[length..];
        return result;
    }
    internal RfxReader Block(out ushort type)
    {
        type = UInt16();
        int length = Length32();
        RfxGuard.Require(length >= 6, "Invalid RemoteFX block length.");
        return new RfxReader(Take(length - 6));
    }
    internal readonly void End() => RfxGuard.Require(_data.IsEmpty, "Unexpected RemoteFX trailing data.");
}

internal ref struct RfxBits(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;
    internal readonly int Remaining => _data.Length * 8 - _position;
    internal int Read(int count)
    {
        RfxGuard.Require(count is >= 0 and <= 24 && count <= Remaining, "Truncated RemoteFX entropy stream.");
        int result = 0;
        for (int i = 0; i < count; i++, _position++)
            result = (result << 1) | ((_data[_position >> 3] >> (7 - (_position & 7))) & 1);
        return result;
    }
    internal readonly void Finish(bool allowExtraByte = false)
    {
        int padding = (8 - (_position & 7)) & 7;
        RfxGuard.Require(Remaining <= padding + (allowExtraByte ? 8 : 0), "Excess RemoteFX entropy data.");
    }
}

internal readonly record struct RfxRect(int X, int Y, int Width, int Height)
{
    internal static RfxRect Read(ref RfxReader reader, int width, int height)
    {
        var result = new RfxRect(reader.UInt16(), reader.UInt16(), reader.UInt16(), reader.UInt16());
        RfxGuard.Require(result.Width > 0 && result.Height > 0 && result.X + result.Width <= width && result.Y + result.Height <= height,
            "RemoteFX region is outside the destination.");
        return result;
    }
}

internal static class RfxGuard
{
    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new RdpProtocolException(message);
    }
    internal static void Destination(int width, int height, Span<byte> destination, int stride)
    {
        Require(width is > 0 and <= ushort.MaxValue && height is > 0 and <= ushort.MaxValue && stride >= (long)width * 4 &&
            (long)(height - 1) * stride + (long)width * 4 <= destination.Length, "Invalid RemoteFX destination dimensions.");
    }
    internal static void Tile(int x, int y, int width, int height) =>
        Require(x >= 0 && y >= 0 && x * 64L < width && y * 64L < height, "RemoteFX tile is outside the destination.");
}
