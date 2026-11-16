using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VelaShell.Vnc.Wire;

namespace VelaShell.Vnc;

/// <summary>
/// The extended clipboard (pseudo-encoding 0xC0A1E5CE, rfbproto "Extended Clipboard"): UTF-8 text instead of
/// ServerCutText's Latin-1, announced with Notify and pulled with Request/Provide so nothing large moves until it is
/// pasted. Only the text format is used. Text travels with CRLF line endings and a terminating NUL.
/// </summary>
internal sealed class ExtendedClipboard
{
    internal const uint Text = 1;
    internal const uint Caps = 1u << 24, Request = 1u << 25, Peek = 1u << 26, Notify = 1u << 27, Provide = 1u << 28;
    internal const int MaxText = 20 * 1024 * 1024;

    /// <summary>The server sent its capabilities, so the extended messages may be used.</summary>
    internal bool Enabled { get; private set; }
    internal uint ServerActions { get; private set; }
    internal int ServerMaxText { get; private set; } = MaxText;

    /// <summary>What we last offered: sent when the server asks for it.</summary>
    internal string? LocalText { get; set; }

    /// <summary>Messages to send in reply and text received (null when none).</summary>
    internal (List<byte[]> Replies, string? Received) Handle(ReadOnlySpan<byte> payload, bool receiveText)
    {
        if (payload.Length < 4)
        {
            throw new VncProtocolException("An extended clipboard message is too short.");
        }

        uint flags = BinaryPrimitives.ReadUInt32BigEndian(payload);
        ReadOnlySpan<byte> body = payload[4..];
        var replies = new List<byte[]>();
        if ((flags & Caps) != 0)
        {
            Enabled = true;
            ServerActions = flags & 0xFF000000;
            int offset = 0;
            for (int format = 0; format < 16; format++)
            {
                if ((flags & (1u << format)) == 0) { continue; }
                if (body.Length < offset + 4) { break; }   // sizes are optional in practice; be lenient
                int size = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32BigEndian(body[offset..]));
                if (format == 0) { ServerMaxText = size; }
                offset += 4;
            }
            replies.Add(Message(new RfbWriter(12).U32(Caps | Request | Peek | Notify | Provide | Text).U32(MaxText)));
            return (replies, null);
        }

        if ((flags & Request) != 0)
        {
            if ((flags & Text) != 0 && LocalText is { } text)
            {
                replies.Add(ProvideMessage(text));
            }
        }
        else if ((flags & Peek) != 0)
        {
            replies.Add(NotifyMessage(LocalText is not null));
        }
        else if ((flags & Notify) != 0)
        {
            if ((flags & Text) != 0 && receiveText)
            {
                replies.Add(Message(new RfbWriter(4).U32(Request | Text)));
            }
        }
        else if ((flags & Provide) != 0 && receiveText)
        {
            return (replies, ReadProvide(flags, body));
        }

        return (replies, null);
    }

    internal static byte[] NotifyMessage(bool hasText) => Message(new RfbWriter(4).U32(Notify | (hasText ? Text : 0)));

    internal byte[] ProvideMessage(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));
        int length = Math.Min(utf8.Length, Math.Max(0, Math.Min(ServerMaxText, MaxText) - 1));
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)length + 1);
            z.Write(size);
            z.Write(utf8, 0, length);
            z.WriteByte(0);
        }
        return Message(new RfbWriter((int)compressed.Length + 8).U32(Provide | Text).Bytes(compressed.ToArray()));
    }

    private static string? ReadProvide(uint flags, ReadOnlySpan<byte> compressed)
    {
        using var z = new ZLibStream(new MemoryStream(compressed.ToArray()), CompressionMode.Decompress);
        Span<byte> size = stackalloc byte[4];
        try
        {
            for (int format = 0; format < 16; format++)
            {
                if ((flags & (1u << format)) == 0) { continue; }
                z.ReadExactly(size);
                uint length = BinaryPrimitives.ReadUInt32BigEndian(size);
                if (length > MaxText) { throw new VncProtocolException("The server's clipboard text is too large."); }
                byte[] data = new byte[length];
                z.ReadExactly(data);
                if (format == 0)
                {
                    int end = Array.IndexOf(data, (byte)0);
                    return Encoding.UTF8.GetString(data, 0, end < 0 ? data.Length : end).ReplaceLineEndings();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            throw new VncProtocolException("The server's clipboard data is corrupt.", ex);
        }
        return null;
    }

    /// <summary>A ClientCutText carrying an extended payload: the length is sent negated.</summary>
    private static byte[] Message(RfbWriter payload) =>
        new RfbWriter(payload.Length + 8).U8(6).Zeros(3).S32(-payload.Length).Bytes(payload.Memory.Span).ToArray();
}
