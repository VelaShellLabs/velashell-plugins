using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// Clipboard virtual channel (MS-RDPECLIP) over "cliprdr". Translates between the wire PDUs
/// and the <see cref="RdpClipboardEvent"/> kinds 2–9 that the existing RdpClipboardBridge /
/// RdpClipboardCodec consume, so the managed engine reuses them unchanged. Mirrors the former
/// native bridge: long format names, stream file-clip, huge-file support; one outstanding
/// format request per direction.
/// </summary>
internal sealed class CliprdrChannel(Func<byte[], CancellationToken, Task> send, Action<RdpClipboardEvent> raise)
{
    private const ushort MonitorReady = 0x0001, FormatList = 0x0002, FormatListResponse = 0x0003;
    private const ushort FormatDataRequest = 0x0004, FormatDataResponse = 0x0005, ClipCaps = 0x0007;
    private const ushort FileContentsRequest = 0x0008, FileContentsResponse = 0x0009;
    private const ushort LockClipData = 0x000A, UnlockClipData = 0x000B;
    private const uint CanLockClipData = 0x00000010;
    internal bool SupportsClipboardLock => (_serverFlags & CanLockClipData) != 0;
    private const ushort ResponseOk = 0x0001, ResponseFail = 0x0002;
    private const ushort CapstypeGeneral = 0x0001;
    private const uint CapsVersion2 = 0x00000002;
    private const uint UseLongFormatNames = 0x00000002;
    private const uint StreamFileClipEnabled = 0x00000004;
    private const uint FileclipNoFilePaths = 0x00000008;
    private const uint HugeFileSupportEnabled = 0x00000020;

    internal CliprdrChannel(Func<byte[], Task> send, Action<RdpClipboardEvent> raise) : this((pdu, _) => send(pdu), raise) { }
    private readonly Func<byte[], CancellationToken, Task> _send = send;
    private readonly Action<RdpClipboardEvent> _raise = raise;
    private uint _requestedFormat;
    private uint _serverFlags;
    private readonly Lock _offerGate = new();
    private readonly Queue<TaskCompletionSource?> _offers = new();
    private bool LongNames => (_serverFlags & UseLongFormatNames) != 0;

    internal async Task ProcessAsync(byte[] pdu, CancellationToken ct = default)
    {
        var r = new ByteReader(pdu);
        ushort msgType = r.U16LE();
        ushort msgFlags = r.U16LE();
        uint dataLen = r.U32LE();
        if (dataLen > (uint)r.Remaining) { throw new InvalidDataException("Truncated clipboard PDU payload."); }
        ReadOnlySpan<byte> data = r.Take((int)dataLen);

        switch (msgType)
        {
            case MonitorReady:
                await _send(BuildCaps(), ct).ConfigureAwait(false);
                // Complete initialization even before a workspace has attached its system clipboard.
                lock (_offerGate) { _offers.Enqueue(null); }
                await _send(Header(FormatList, 0, []), ct).ConfigureAwait(false);
                _raise(new RdpClipboardEvent(2, 0, 0, []));
                break;
            case ClipCaps:
                ReadCapabilities(data);
                break;
            case FormatList:
                string text = ParseFormatList(data, LongNames, (msgFlags & 4) != 0);
                await _send(Header(FormatListResponse, ResponseOk, []), ct).ConfigureAwait(false);
                _raise(text.Length == 0 ? new RdpClipboardEvent(9, 0, 0, []) : new RdpClipboardEvent(3, 0, 0, Encoding.UTF8.GetBytes(text)));
                break;
            case FormatListResponse:
                lock (_offerGate)
                {
                    if (_offers.TryDequeue(out TaskCompletionSource? offer) && offer is not null)
                    {
                        if ((msgFlags & ResponseOk) != 0) { _ = offer.TrySetResult(); }
                        else { _ = offer.TrySetException(new IOException("The remote clipboard rejected the format list.")); }
                    }
                }
                break;
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
            case LockClipData:
            case UnlockClipData:
                if (!SupportsClipboardLock || data.Length != 4)
                {
                    throw new InvalidDataException("Invalid clipboard lock PDU.");
                }

                _raise(new RdpClipboardEvent(msgType == LockClipData ? 11u : 12u, BitConverter.ToUInt32(data), 0, []));
                break;
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
                    if (rr.Remaining == 4 && SupportsClipboardLock)
                    {
                        payload.U32LE(rr.U32LE());
                    }

                    if (rr.Remaining != 0)
                    {
                        throw new InvalidDataException("Invalid clipboard file version.");
                    }

                    _raise(new RdpClipboardEvent(6, streamId, listIndex, payload.ToArray()));
                    break;
                }
            case FileContentsResponse:
                {
                    var rr = new ByteReader(data);
                    uint streamId = rr.U32LE();
                    bool ok = msgFlags == ResponseOk;
                    _raise(new RdpClipboardEvent(7, streamId, ok ? 1u : 0u, ok ? rr.Rest().ToArray() : []) { ResponseFlags = msgFlags });
                    break;
                }
        }
    }

    // ---- Client → server (invoked by RdpClipboardBridge via IRdpClipboardTransport) ----

    internal async Task OfferFormatsAsync(string formats, CancellationToken ct = default)
    {
        var body = new ByteWriter(64);
        bool asciiNames = !LongNames && formats.All(character => character <= 0x7F);
        foreach (string line in formats.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length < 1 || !uint.TryParse(parts[0], out uint id))
            {
                continue;
            }

            string name = parts.Length > 1 ? parts[1] : "";
            if ((_serverFlags & StreamFileClipEnabled) == 0 && name is "FileGroupDescriptorW" or "FileContents")
            {
                continue;
            }
            body.U32LE(id);
            if (LongNames)
            {
                body.Bytes(Encoding.Unicode.GetBytes(name));
                body.U16LE(0);
            }
            else
            {
                byte[] shortName = new byte[32];
                // FileGroupDescriptorW fits the 32-byte ANSI field, but not its UTF-16 variant.
                Encoding encoding = asciiNames ? Encoding.ASCII : Encoding.Unicode;
                _ = encoding.GetBytes(name.AsSpan(0, Math.Min(name.Length, asciiNames ? 31 : 15)), shortName);
                body.Bytes(shortName);
            }
        }
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_offerGate) { _offers.Enqueue(response); }
        await _send(Header(FormatList, asciiNames ? (ushort)4 : (ushort)0, body.Span), ct).ConfigureAwait(false);
        // Ctrl+V must follow the server accepting the offered formats, not merely the socket write.
        await response.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    internal Task RequestFormatAsync(uint format, CancellationToken ct = default)
    {
        _requestedFormat = format;
        var body = new ByteWriter(4);
        body.U32LE(format);
        return _send(Header(FormatDataRequest, 0, body.Span), ct);
    }

    internal Task RespondFormatAsync(byte[] data, bool success, CancellationToken ct = default) =>
        _send(Header(FormatDataResponse, success ? ResponseOk : ResponseFail, success ? data : []), ct);

    internal Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, uint? clipDataId = null, CancellationToken ct = default)
    {
        var body = new ByteWriter(28);
        body.U32LE(stream);
        body.U32LE(index);
        body.U32LE(flags);
        body.U32LE((uint)(offset & 0xFFFFFFFF));
        body.U32LE((uint)(offset >> 32));
        body.U32LE(length);
        if (clipDataId is { } id)
        {
            if (!SupportsClipboardLock)
            {
                throw new InvalidOperationException("Peer does not support clipboard locks.");
            }

            body.U32LE(id);
        }
        return _send(Header(FileContentsRequest, 0, body.Span), ct);
    }

    internal Task LockClipboardAsync(uint id, bool locked, CancellationToken ct)
    {
        if (!SupportsClipboardLock)
        {
            throw new InvalidOperationException("Peer does not support clipboard locks.");
        }

        return _send(Header(locked ? LockClipData : UnlockClipData, 0, BitConverter.GetBytes(id)), ct);
    }

    internal Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken ct = default)
    {
        var body = new ByteWriter(data.Length + 4);
        body.U32LE(stream);
        if (success)
        {
            body.Bytes(data);
        }

        return _send(Header(FileContentsResponse, success ? ResponseOk : ResponseFail, body.Span), ct);
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
        caps.U32LE(UseLongFormatNames | StreamFileClipEnabled | FileclipNoFilePaths | CanLockClipData | HugeFileSupportEnabled);
        return Header(ClipCaps, 0, caps.Span);
    }

    private void ReadCapabilities(ReadOnlySpan<byte> data)
    {
        var r = new ByteReader(data);
        int count = r.U16LE();
        _ = r.U16LE();
        for (int i = 0; i < count; i++)
        {
            ushort type = r.U16LE();
            int length = r.U16LE();
            if (length < 4) { throw new InvalidDataException("Invalid clipboard capability length."); }
            var capability = new ByteReader(r.Take(length - 4));
            if (type == CapstypeGeneral)
            {
                _ = capability.U32LE();
                _serverFlags = capability.U32LE();
            }
        }
    }

    private static string ParseFormatList(ReadOnlySpan<byte> data, bool longNames, bool asciiNames)
    {
        if (!longNames)
        {
            if (data.Length % 36 != 0) { throw new InvalidDataException("Invalid short clipboard format list."); }
            var shortNames = new StringBuilder();
            var reader = new ByteReader(data);
            while (reader.Remaining > 0)
            {
                uint id = reader.U32LE();
                ReadOnlySpan<byte> nameBytes = reader.Take(32);
                string name = (asciiNames ? Encoding.ASCII : Encoding.Unicode).GetString(nameBytes).Split('\0')[0];
                _ = shortNames.Append(id).Append('\t').Append(name.Replace("\t", "").Replace("\r", "").Replace("\n", "")).Append('\n');
            }
            return shortNames.ToString();
        }
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
                if (unit == 0)
                {
                    break;
                }

                _ = name.Append((char)unit);
            }
            string clean = name.ToString().Replace("\t", "").Replace("\r", "").Replace("\n", "");
            _ = sb.Append(id).Append('\t').Append(clean).Append('\n');
        }
        return sb.ToString();
    }
}
