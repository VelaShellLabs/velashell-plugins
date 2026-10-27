using System.Buffers.Binary;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>Exact reads avoid consuming even one TLS byte before the TLS stream owns the socket.</summary>
internal static class RdpFraming
{
    public const int MaxPduLength = 16 * 1024 * 1024;
    public static async Task<byte[]> ReadRdpAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header.AsMemory(0, 2), ct).ConfigureAwait(false);
        int length;
        int used;
        if (header[0] == 3)
        {
            if (header[1] != 0) throw new IOException("Invalid RDP TPKT header.");
            await ReadExactlyAsync(stream, header.AsMemory(2, 2), ct).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
            used = 4;
        }
        else
        {
            if ((header[0] & 3) != 0) throw new IOException("Invalid RDP fast-path header.");
            if ((header[1] & 0x80) == 0) { length = header[1]; used = 2; }
            else
            {
                await ReadExactlyAsync(stream, header.AsMemory(2, 1), ct).ConfigureAwait(false);
                length = ((header[1] & 0x7f) << 8) | header[2];
                used = 3;
            }
        }
        ValidateLength(length, used);
        var frame = new byte[length];
        header.AsSpan(0, used).CopyTo(frame);
        await ReadExactlyAsync(stream, frame.AsMemory(used), ct).ConfigureAwait(false);
        return frame;
    }

    public static async Task<byte[]> ReadCredsspAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[6];
        await ReadExactlyAsync(stream, header.AsMemory(0, 2), ct).ConfigureAwait(false);
        if (header[0] != 0x30) throw new IOException("Invalid CredSSP sequence tag.");
        int used = 2;
        int content = header[1];
        if ((content & 0x80) != 0)
        {
            int count = content & 0x7f;
            if (count is < 1 or > 4) throw new IOException("Invalid CredSSP length.");
            await ReadExactlyAsync(stream, header.AsMemory(2, count), ct).ConfigureAwait(false);
            used += count;
            uint value = 0;
            for (int i = 2; i < used; i++) value = checked((value << 8) | header[i]);
            if (value > MaxPduLength || value < 128 || header[2] == 0) throw new IOException("Invalid CredSSP length.");
            content = checked((int)value);
        }
        int length = checked(content + used);
        ValidateLength(length, used);
        var frame = new byte[length];
        header.AsSpan(0, used).CopyTo(frame);
        await ReadExactlyAsync(stream, frame.AsMemory(used), ct).ConfigureAwait(false);
        return frame;
    }

    public static async Task ReadExactlyAsync(Stream stream, Memory<byte> target, CancellationToken ct)
    {
        while (!target.IsEmpty)
        {
            int read = await stream.ReadAsync(target, ct).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The RDP peer closed the connection.");
            target = target[read..];
        }
    }
    internal static void ValidateLength(int length, int header)
    {
        if (length < header || length > MaxPduLength) throw new IOException("RDP packet length exceeds the session budget.");
    }
}
