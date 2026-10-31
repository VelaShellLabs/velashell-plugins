using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Pdu;

/// <summary>PDUTYPE values in the Share Control Header (MS-RDPBCGR 2.2.8.1.1.1.1).</summary>
internal enum ShareControlType : byte
{
    DemandActive = 0x1,
    ConfirmActive = 0x3,
    Deactivate = 0x6,
    Data = 0x7,
    ServerRedirect = 0xA
}

/// <summary>PDUTYPE2 values in the Share Data Header (MS-RDPBCGR 2.2.8.1.1.1.2).</summary>
internal enum ShareDataType : byte
{
    Update = 2,
    Control = 20,
    Pointer = 27,
    Input = 28,
    Synchronize = 31,
    RefreshRect = 33,
    SuppressOutput = 35,
    Shutdown = 36,
    FontList = 39,
    FontMap = 40,
    SetErrorInfo = 47
}

/// <summary>Reads and writes the Share Control / Share Data headers common to most PDUs.</summary>
internal static class ShareControl
{
    internal const ushort ProtocolVersion = 0x0010;

    internal const int ControlHeaderLength = 6;
    internal const int DataHeaderLength = 12;

    internal static void WriteControlHeader(ByteWriter w, int totalLength, ShareControlType type, ushort source)
    {
        w.U16LE((ushort)totalLength);
        w.U16LE((ushort)((byte)type | ProtocolVersion));
        w.U16LE(source);
    }

    internal readonly record struct ControlHeader(int TotalLength, ShareControlType Type, ushort Source);

    internal static ControlHeader ReadControlHeader(ref ByteReader r)
    {
        int total = r.U16LE();
        ushort pduType = r.U16LE();
        ushort source = r.U16LE();
        return new ControlHeader(total, (ShareControlType)(pduType & 0x0F), source);
    }

    /// <summary>Assembles a complete non-data share control PDU (e.g. Confirm Active) around a body.</summary>
    internal static byte[] BuildControlPdu(ShareControlType type, ushort source, ReadOnlySpan<byte> body)
    {
        int total = ControlHeaderLength + body.Length;
        var w = new ByteWriter(total);
        WriteControlHeader(w, total, type, source);
        w.Bytes(body);
        return w.ToArray();
    }

    /// <summary>Assembles a complete Data PDU: share control header + share data header + body.</summary>
    internal static byte[] BuildDataPdu(uint shareId, ShareDataType type, ushort source, ReadOnlySpan<byte> body)
    {
        int total = ControlHeaderLength + DataHeaderLength + body.Length;
        var w = new ByteWriter(total);
        WriteControlHeader(w, total, ShareControlType.Data, source);
        w.U32LE(shareId);
        w.U8(0);                      // pad1
        w.U8(1);                      // streamId = STREAM_LOW
        w.U16LE((ushort)total);       // uncompressedLength: full PDU incl. both headers (MS-RDPBCGR 2.2.8.1.1.1.2)
        w.U8((byte)type);             // pduType2
        w.U8(0);                      // compressedType
        w.U16LE(0);                   // compressedLength
        w.Bytes(body);
        return w.ToArray();
    }

    internal readonly record struct DataHeader(uint ShareId, ShareDataType Type, int UncompressedLength, byte CompressedType);

    internal static DataHeader ReadDataHeader(ref ByteReader r)
    {
        uint shareId = r.U32LE();
        _ = r.U8();                       // pad1
        _ = r.U8();                       // streamId
        ushort uncompressed = r.U16LE();
        byte type = r.U8();
        byte compressedType = r.U8();
        _ = r.U16LE();                    // compressedLength
        return new DataHeader(shareId, (ShareDataType)type, uncompressed, compressedType);
    }
}
