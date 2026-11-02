using System.Buffers.Binary;

namespace VelaShell.Rdp.Wire;

[Flags]
internal enum RdpUdpFlags : ushort
{
    Syn = 1, Ack = 4, Data = 8, Fec = 16, Congestion = 32, CongestionReset = 64,
    AckOfAcks = 256, Lossy = 512, DelayedAck = 1024, Correlation = 2048, SynEx = 4096
}

/// <summary>MS-RDPEUDP v1 uses network byte order, unlike the enclosed RDP PDUs.</summary>
internal sealed record RdpUdpPacket(uint Ack, ushort Window, RdpUdpFlags Flags)
{
    internal uint InitialSequence { get; init; }
    internal ushort UpMtu { get; init; }
    internal ushort DownMtu { get; init; }
    internal byte[] AckVector { get; init; } = [];
    internal uint AckOfAcks { get; init; }
    internal uint CodedSequence { get; init; }
    internal uint SourceSequence { get; init; }
    internal byte FecRange { get; init; }
    internal byte FecIndex { get; init; }
    internal byte[] Payload { get; init; } = [];

    internal static int Distance(uint value, uint reference) => unchecked((int)(value - reference));

    internal byte[] Encode()
    {
        bool syn = Flags.HasFlag(RdpUdpFlags.Syn);
        int ackLength = Flags.HasFlag(RdpUdpFlags.Ack) && !syn ? (2 + AckVector.Length + 3) & ~3 : 0;
        int length = syn ? Math.Min(UpMtu, DownMtu) : 8 + ackLength
            + (Flags.HasFlag(RdpUdpFlags.AckOfAcks) ? 4 : 0)
            + (Flags.HasFlag(RdpUdpFlags.Data) ? (Flags.HasFlag(RdpUdpFlags.Fec) ? 12 : 8) + Payload.Length : 0);
        byte[] bytes = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, Ack);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), Window);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)Flags);
        if (syn)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), InitialSequence);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), UpMtu);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(14), DownMtu);
            return bytes;
        }
        int offset = 8;
        if (ackLength != 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), checked((ushort)AckVector.Length));
            AckVector.CopyTo(bytes, offset + 2);
            offset += ackLength;
        }
        if (Flags.HasFlag(RdpUdpFlags.AckOfAcks))
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), AckOfAcks);
            offset += 4;
        }
        if (Flags.HasFlag(RdpUdpFlags.Data))
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), CodedSequence);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), SourceSequence);
            offset += 8;
            if (Flags.HasFlag(RdpUdpFlags.Fec))
            {
                bytes[offset] = FecRange;
                bytes[offset + 1] = FecIndex;
                offset += 4;
            }
            Payload.CopyTo(bytes, offset);
        }
        return bytes;
    }

    internal static RdpUdpPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 8 or > 1232) { throw new RdpProtocolException("Invalid RDP-UDP datagram length."); }
        var r = new ByteReader(bytes);
        uint ack = r.U32BE();
        ushort window = r.U16BE();
        var flags = (RdpUdpFlags)r.U16BE();
        var packet = new RdpUdpPacket(ack, window, flags);
        if ((flags & ~(RdpUdpFlags)0x1F7D) != 0
            || (flags.HasFlag(RdpUdpFlags.Fec) && !flags.HasFlag(RdpUdpFlags.Data))
            || (flags.HasFlag(RdpUdpFlags.Syn) && (flags & (RdpUdpFlags.Data | RdpUdpFlags.Fec | RdpUdpFlags.AckOfAcks)) != 0)
            || (!flags.HasFlag(RdpUdpFlags.Syn) && (flags & (RdpUdpFlags.Lossy | RdpUdpFlags.SynEx | RdpUdpFlags.Correlation)) != 0))
        { throw new RdpProtocolException("Invalid RDP-UDP flags."); }
        if (flags.HasFlag(RdpUdpFlags.Syn))
        {
            uint initial = r.U32BE();
            ushort up = r.U16BE(), down = r.U16BE();
            if (up is < 1132 or > 1232 || down is < 1132 or > 1232 || bytes.Length < Math.Min(up, down))
            {
                throw new RdpProtocolException("Invalid RDP-UDP MTU.");
            }
            if (flags.HasFlag(RdpUdpFlags.Correlation)) { r.Skip(32); }
            if (flags.HasFlag(RdpUdpFlags.SynEx))
            {
                ushort ex = r.U16BE(), version = r.U16BE();
                if ((ex & 1) != 0 && version != 1) { throw new RdpProtocolException("Unrequested RDP-UDP version."); }
            }
            return packet with { InitialSequence = initial, UpMtu = up, DownMtu = down };
        }
        if (flags.HasFlag(RdpUdpFlags.Ack))
        {
            ushort count = r.U16BE();
            if (count > 2048) { throw new RdpProtocolException("RDP-UDP ACK vector too large."); }
            byte[] vector = r.Take(count).ToArray();
            foreach (byte element in vector)
            {
                if ((element & 63) == 0 || (element >> 6) is 1 or 2) { throw new RdpProtocolException("Invalid RDP-UDP ACK run."); }
            }
            r.Skip((4 - ((2 + count) & 3)) & 3);
            packet = packet with { AckVector = vector };
        }
        if (flags.HasFlag(RdpUdpFlags.AckOfAcks)) { packet = packet with { AckOfAcks = r.U32BE() }; }
        if (flags.HasFlag(RdpUdpFlags.Data))
        {
            uint coded = r.U32BE(), source = r.U32BE();
            byte range = 0, index = 0;
            if (flags.HasFlag(RdpUdpFlags.Fec)) { range = r.U8(); index = r.U8(); r.Skip(2); }
            packet = packet with { CodedSequence = coded, SourceSequence = source, FecRange = range, FecIndex = index, Payload = r.Rest().ToArray() };
        }
        else if (r.Remaining != 0) { throw new RdpProtocolException("Trailing RDP-UDP control data."); }
        return packet;
    }
}
