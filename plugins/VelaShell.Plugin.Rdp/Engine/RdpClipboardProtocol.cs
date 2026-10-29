using System.Buffers.Binary;
using System.Text;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>MS-RDPECLIP text messages, independent of native backend and OS clipboard ownership.</summary>
internal static class RdpClipboardProtocol
{
    internal const int MaximumTextBytes = 2 * 1024 * 1024;
    internal const int MaximumFormatListBytes = 64 * 1024;
    internal const uint UnicodeText = 13;
    internal const ushort MonitorReady = 1, FormatList = 2, FormatListResponse = 3,
        FormatDataRequest = 4, FormatDataResponse = 5, Capabilities = 7;
    internal const ushort ResponseOk = 1, ResponseFail = 2, AsciiNames = 4;
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    internal readonly record struct Message(ushort Type, ushort Flags, ReadOnlyMemory<byte> Payload);

    // The enclosing static-channel transport must reassemble before calling this decoder.
    internal static Message Decode(ReadOnlyMemory<byte> packet)
    {
        if (packet.Length < 8) throw Invalid();
        ReadOnlySpan<byte> bytes = packet.Span;
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        int maximum = type == FormatDataResponse ? MaximumTextBytes : MaximumFormatListBytes;
        if (length > maximum || length != packet.Length - 8) throw Invalid();
        return new(type, flags, packet[8..]);
    }

    internal static byte[] Encode(ushort type, ushort flags, ReadOnlySpan<byte> payload)
    {
        int maximum = type == FormatDataResponse ? MaximumTextBytes : MaximumFormatListBytes;
        if (payload.Length > maximum) throw Invalid();
        byte[] packet = new byte[checked(8 + payload.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, type);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), checked((uint)payload.Length));
        payload.CopyTo(packet.AsSpan(8));
        return packet;
    }

    internal static byte[] CreateCapabilities()
    {
        // One GENERAL capability, version 2. Only long names: no file transfer or lock support.
        byte[] payload = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 2);
        return Encode(Capabilities, 0, payload);
    }

    internal static bool ReadLongFormatNames(Message message)
    {
        Require(message, Capabilities, 0);
        ReadOnlySpan<byte> payload = message.Payload.Span;
        if (payload.Length < 4 || payload.Length > MaximumFormatListBytes) throw Invalid();
        int count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        int offset = 4;
        bool found = false, longNames = false;
        for (int i = 0; i < count; i++)
        {
            if (payload.Length - offset < 4) throw Invalid();
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]);
            if (length < 4 || length > payload.Length - offset) throw Invalid();
            if (type == 1)
            {
                if (found || length != 12) throw Invalid();
                found = true;
                uint version = BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 4)..]);
                if (version is not (1 or 2)) throw Invalid();
                longNames = (BinaryPrimitives.ReadUInt32LittleEndian(payload[(offset + 8)..]) & 2) != 0;
            }
            offset += length;
        }
        if (offset != payload.Length) throw Invalid();
        return longNames;
    }

    internal static byte[] CreateFormatList(bool longNames, bool hasText)
    {
        if (!hasText) return Encode(FormatList, 0, []);
        byte[] payload = new byte[longNames ? 6 : 36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, UnicodeText);
        return Encode(FormatList, 0, payload);
    }

    internal static bool OffersUnicodeText(Message message, bool longNames)
    {
        if (message.Type != FormatList || (message.Flags & ~AsciiNames) != 0) throw Invalid();
        ReadOnlySpan<byte> payload = message.Payload.Span;
        if (payload.Length > MaximumFormatListBytes) throw Invalid();
        bool found = false;
        int offset = 0;
        for (int count = 0; offset < payload.Length; count++)
        {
            if (count >= 1024 || payload.Length - offset < 4) throw Invalid();
            found |= BinaryPrimitives.ReadUInt32LittleEndian(payload[offset..]) == UnicodeText;
            offset += 4;
            if (!longNames)
            {
                if (payload.Length - offset < 32) throw Invalid();
                offset += 32;
                continue;
            }
            // Long format names are UTF-16 regardless of CB_ASCII_NAMES.
            bool terminated = false;
            while (payload.Length - offset >= 2)
            {
                ushort value = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
                offset += 2;
                if (value == 0) { terminated = true; break; }
            }
            if (!terminated) throw Invalid();
        }
        return found;
    }

    internal static byte[] CreateTextRequest()
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, UnicodeText);
        return Encode(FormatDataRequest, 0, payload);
    }

    internal static uint ReadRequestedFormat(Message message)
    {
        Require(message, FormatDataRequest, 0);
        if (message.Payload.Length != 4) throw Invalid();
        return BinaryPrimitives.ReadUInt32LittleEndian(message.Payload.Span);
    }

    internal static byte[] CreateTextResponse(string? text)
    {
        if (text is null) return Encode(FormatDataResponse, ResponseFail, []);
        // Embedded NUL would silently truncate the content on Windows.
        if (text.Length > MaximumTextBytes / 2 - 1 || text.Contains('\0')) throw Invalid();
        int byteCount;
        try { byteCount = Utf16.GetByteCount(text); }
        catch (EncoderFallbackException) { throw Invalid(); }
        // Encode directly into the wire packet: no second full-size text buffer or copy.
        byte[] packet = new byte[checked(8 + byteCount + 2)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, FormatDataResponse);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), ResponseOk);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), checked((uint)byteCount + 2));
        try
        {
            Utf16.GetBytes(text, packet.AsSpan(8, byteCount));
            return packet;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(packet);
            throw;
        }
    }

    internal static string? ReadTextResponse(Message message)
    {
        if (message.Type != FormatDataResponse) throw Invalid();
        if (message.Flags == ResponseFail)
        {
            if (!message.Payload.IsEmpty) throw Invalid();
            return null;
        }
        Require(message, FormatDataResponse, ResponseOk);
        ReadOnlySpan<byte> payload = message.Payload.Span;
        if (payload.Length < 2 || payload.Length > MaximumTextBytes || payload.Length % 2 != 0
            || payload[^2] != 0 || payload[^1] != 0) throw Invalid();
        payload = payload[..^2];
        for (int i = 0; i < payload.Length; i += 2)
            if (payload[i] == 0 && payload[i + 1] == 0) throw Invalid();
        try { return Utf16.GetString(payload); }
        catch (DecoderFallbackException) { throw Invalid(); }
    }

    private static void Require(Message message, ushort type, ushort flags)
    {
        if (message.Type != type || message.Flags != flags) throw Invalid();
    }

    private static IOException Invalid() => new("Invalid or oversized RDP clipboard message.");
}
