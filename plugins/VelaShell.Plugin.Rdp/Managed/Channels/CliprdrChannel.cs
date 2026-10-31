using System.Text;
using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Channels;

/// <summary>
/// Clipboard virtual channel (MS-RDPECLIP) over "cliprdr". Translates between the wire PDUs
/// and the <see cref="RdpClipboardEvent"/> kinds 2–9 that the existing RdpClipboardBridge /
/// RdpClipboardCodec consume, so the managed engine reuses them unchanged. Mirrors the former
/// native bridge: long format names, stream file-clip, huge-file support; one outstanding
/// format request per direction.
/// </summary>
internal sealed class CliprdrChannel(Func<byte[], Task> send, Action<RdpClipboardEvent> raise)
{
    private const ushort MonitorReady = 0x0001, FormatList = 0x0002, FormatListResponse = 0x0003;
    private const ushort FormatDataRequest = 0x0004, FormatDataResponse = 0x0005, ClipCaps = 0x0007;
    private const ushort FileContentsRequest = 0x0008, FileContentsResponse = 0x0009;
    private const ushort ResponseOk = 0x0001, ResponseFail = 0x0002;
    private const ushort CapstypeGeneral = 0x0001;
    private const uint CapsVersion2 = 0x00000002;
    private const uint UseLongFormatNames = 0x00000002;
    private const uint StreamFileClipEnabled = 0x00000004;
    private const uint FileclipNoFilePaths = 0x00000008;
    private const uint HugeFileSupportEnabled = 0x00000010;

    private readonly Func<byte[], Task> _send = send;
    private readonly Action<RdpClipboardEvent> _raise = raise;
    private uint _requestedFormat;

    internal async Task ProcessAsync(byte[] pdu)
    {
        var r = new ByteReader(pdu);
        ushort msgType = r.U16LE();
        ushort msgFlags = r.U16LE();
        uint dataLen = r.U32LE();
        ReadOnlySpan<byte> data = r.Take((int)Math.Min(dataLen, (uint)r.Remaining));

        switch (msgType)
        {
            case MonitorReady:
                await _send(BuildCaps()).ConfigureAwait(false);
                _raise(new RdpClipboardEvent(2, 0, 0, []));
                break;
            case ClipCaps:
                break; // server capabilities; nothing required
            case FormatList:
                await _send(Header(FormatListResponse, ResponseOk, [])).ConfigureAwait(false);
                string text = ParseFormatList(data);
                _raise(text.Length == 0 ? new RdpClipboardEvent(9, 0, 0, []) : new RdpClipboardEvent(3, 0, 0, Encoding.UTF8.GetBytes(text)));
                break;
            case FormatListResponse:
                break; // our offered list was acknowledged
            case FormatDataRequest:
            {
                uint formatId = data.Length >= 4 ? BitConverter.ToUInt32(data[..4]) : 0;
                _raise(new RdpClipboardEvent(4, formatId, 0, []));
                break;
            }
            case FormatDataResponse:
            {
                bool ok = (msgFlags & ResponseOk) != 0;
                _raise(new RdpClipboardEvent(5, ok ? 1u : 0u, _requestedFormat, ok ? data.ToArray() : []));
                break;
            }
            case FileContentsRequest:
            {
                var rr = new ByteReader(data);
                uint streamId = rr.U32LE();
                uint listIndex = rr.U32LE();
                uint dwFlags = rr.U32LE();
                uint posLow = rr.U32LE();
                uint posHigh = rr.U32LE();
                uint cbRequested = rr.U32LE();
                var payload = new ByteWriter(16);
                payload.U32LE(dwFlags);
                payload.U32LE(posLow);
                payload.U32LE(posHigh);
                payload.U32LE(cbRequested);
                _raise(new RdpClipboardEvent(6, streamId, listIndex, payload.ToArray()));
                break;
            }
            case FileContentsResponse:
            {
                var rr = new ByteReader(data);
                uint streamId = rr.U32LE();
                bool ok = (msgFlags & ResponseOk) != 0;
                _raise(new RdpClipboardEvent(7, streamId, ok ? 1u : 0u, ok ? rr.Rest().ToArray() : []));
                break;
            }
        }
    }

    // ---- Client → server (invoked by RdpClipboardBridge via IRdpClipboardTransport) ----

    internal Task OfferFormatsAsync(string formats)
    {
        var body = new ByteWriter(64);
        foreach (string line in formats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length < 1 || !uint.TryParse(parts[0], out uint id)) continue;
            string name = parts.Length > 1 ? parts[1] : "";
            body.U32LE(id);
            body.Bytes(Encoding.Unicode.GetBytes(name));
            body.U16LE(0); // UTF-16 null terminator
        }
        return _send(Header(FormatList, 0, body.Span));
    }

    internal Task RequestFormatAsync(uint format)
    {
        _requestedFormat = format;
        var body = new ByteWriter(4);
        body.U32LE(format);
        return _send(Header(FormatDataRequest, 0, body.Span));
    }

    internal Task RespondFormatAsync(byte[] data, bool success) =>
        _send(Header(FormatDataResponse, success ? ResponseOk : ResponseFail, success ? data : []));

    internal Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length)
    {
        var body = new ByteWriter(24);
        body.U32LE(stream);
        body.U32LE(index);
        body.U32LE(flags);
        body.U32LE((uint)(offset & 0xFFFFFFFF));
        body.U32LE((uint)(offset >> 32));
        body.U32LE(length);
        return _send(Header(FileContentsRequest, 0, body.Span));
    }

    internal Task RespondFileAsync(uint stream, byte[] data, bool success)
    {
        var body = new ByteWriter(data.Length + 4);
        body.U32LE(stream);
        if (success) body.Bytes(data);
        return _send(Header(FileContentsResponse, success ? ResponseOk : ResponseFail, body.Span));
    }

    // ---- Encoding helpers ----

    private static byte[] Header(ushort msgType, ushort msgFlags, ReadOnlySpan<byte> data)
    {
        var w = new ByteWriter(data.Length + 8);
        w.U16LE(msgType);
        w.U16LE(msgFlags);
        w.U32LE((uint)data.Length);
        w.Bytes(data);
        return w.ToArray();
    }

    private static byte[] BuildCaps()
    {
        var caps = new ByteWriter(16);
        caps.U16LE(1);           // cCapabilitiesSets
        caps.U16LE(0);           // pad2octets
        caps.U16LE(CapstypeGeneral);
        caps.U16LE(12);          // lengthCapability
        caps.U32LE(CapsVersion2);
        caps.U32LE(UseLongFormatNames | StreamFileClipEnabled | FileclipNoFilePaths | HugeFileSupportEnabled);
        return Header(ClipCaps, 0, caps.Span);
    }

    private static string ParseFormatList(ReadOnlySpan<byte> data)
    {
        // Long format names: repeated [formatId(4 LE)][formatName UTF-16, NUL-terminated].
        var sb = new StringBuilder();
        var r = new ByteReader(data);
        while (r.Remaining >= 6)
        {
            uint id = r.U32LE();
            var name = new StringBuilder();
            while (r.Remaining >= 2)
            {
                ushort unit = r.U16LE();
                if (unit == 0) break;
                name.Append((char)unit);
            }
            string clean = name.ToString().Replace("\t", "").Replace("\r", "").Replace("\n", "");
            sb.Append(id).Append('\t').Append(clean).Append('\n');
        }
        return sb.ToString();
    }
}
