using System.Net.Sockets;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Input;
using VelaShell.Rdp.Mcs;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Security;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp;

internal sealed record RdpConnectOptions(
    string Host, int Port, string Username, string Password, string Domain,
    int Width, int Height, int Bpp, byte[]? CertificatePin, string CertificateHost,
    IReadOnlyList<ChannelDef> Channels);

/// <summary>Raised when a virtual channel delivers data to the managed session.</summary>
internal readonly record struct ChannelPayload(string ChannelName, byte[] Data);

/// <summary>
/// Drives the full RDP connection sequence and the active-phase pump using only managed
/// code: X.224 negotiation, TLS (or CredSSP when a server requires NLA), MCS/GCC, the client
/// info, licensing, capability exchange and finalization, then bitmap output and input.
/// </summary>
internal sealed class RdpConnection : IAsyncDisposable
{
    private readonly RdpConnectOptions _options;
    private TcpClient? _tcp;
    private RdpTransport _transport = null!;
    private McsLayer _mcs = null!;
    private ushort _ioChannel;
    private uint _shareId;
    private int _width;
    private int _height;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<ushort, string> _channelsById = [];
    private readonly Dictionary<ushort, MemoryStream> _vcReassembly = [];
    private readonly object _frameLock = new();
    private const int ChannelChunkSize = 1600;
    private const uint ChannelFlagFirst = 0x01, ChannelFlagLast = 0x02;

    private readonly MemoryStream _fragment = new();
    private int _fragmentCode;
    private const int MaxFragment = 16 * 1024 * 1024;
    private Graphics.Mppc? _mppc;                    // server→client bulk decompression (shared history)

    internal Framebuffer Framebuffer { get; private set; } = new(1, 1);
    internal event Action? FrameUpdated;
    internal event Action<ChannelPayload>? ChannelData;
    internal event Action<uint>? ErrorInfoReceived;

    internal RdpConnection(RdpConnectOptions options)
    {
        _options = options;
        _width = options.Width;
        _height = options.Height;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
        _transport = new RdpTransport(_tcp.GetStream());

        // X.224 negotiation: offer TLS and NLA; let the server pick.
        SecurityProtocol requested = SecurityProtocol.Ssl | SecurityProtocol.Hybrid;
        await _transport.SendTpktAsync(RdpTransport.BuildConnectionRequest(_options.Username, requested), ct).ConfigureAwait(false);
        (SecurityProtocol selected, _) = await _transport.ReadConnectionConfirmAsync(ct).ConfigureAwait(false);

        if (selected.HasFlag(SecurityProtocol.Hybrid) || selected.HasFlag(SecurityProtocol.HybridEx))
        {
            var ssl = await RdpTls.AuthenticateAsync(_transport.Stream, _options.CertificateHost, _options.CertificatePin, ct).ConfigureAwait(false);
            _transport.UpgradeStream(ssl);
            await CredSsp.AuthenticateAsync(ssl, _options.CertificateHost, _options.Domain, _options.Username, _options.Password, ct).ConfigureAwait(false);
        }
        else if (selected.HasFlag(SecurityProtocol.Ssl))
        {
            var ssl = await RdpTls.AuthenticateAsync(_transport.Stream, _options.CertificateHost, _options.CertificatePin, ct).ConfigureAwait(false);
            _transport.UpgradeStream(ssl);
        }
        else
        {
            throw new RdpProtocolException("The server offered only legacy RDP security, which is disabled.");
        }

        // MCS + GCC.
        byte[] gcc = GccData.BuildClientData(_options.Width, _options.Height, _options.Bpp,
            (uint)selected, "VelaShell", 0x409, _options.Channels);
        _mcs = new McsLayer(_transport);
        await _mcs.ConnectAsync(gcc, _options.Channels, ct).ConfigureAwait(false);
        _ioChannel = _mcs.Server.IoChannelId;
        foreach ((string name, ushort id) in _mcs.Channels) _channelsById[id] = name;

        // Client Info on the I/O channel.
        await SendOnIoAsync(ClientInfo.Build(_options.Domain, _options.Username, _options.Password, autoLogon: true), ct).ConfigureAwait(false);

        // Licensing then capability exchange.
        byte[] demandActive = await ReceiveLicensingThenDemandActiveAsync(ct).ConfigureAwait(false);
        await HandleDemandActiveAsync(demandActive, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> ReceiveLicensingThenDemandActiveAsync(CancellationToken ct)
    {
        while (true)
        {
            byte[] payload = await ReadIoPayloadAsync(ct).ConfigureAwait(false);
            if (LooksLikeShareControl(payload)) return payload;   // server skipped or finished licensing
            if (Licensing.Process(payload)) return await ReadIoPayloadAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task HandleDemandActiveAsync(byte[] payload, CancellationToken ct)
    {
        var r = new ByteReader(payload);
        ShareControl.ControlHeader header = ShareControl.ReadControlHeader(ref r);
        if (header.Type != ShareControlType.DemandActive) throw new RdpProtocolException("Expected Demand Active PDU.");
        Capabilities.DemandActive active = Capabilities.ReadDemandActive(ref r);
        _shareId = active.ShareId;
        // Adopt the resolution the server actually settled on (initial negotiation, or the result of
        // a display-control resize); keep the requested size only when the caps omit a bitmap set.
        if (active.Width > 0 && active.Height > 0) { _width = active.Width; _height = active.Height; }

        ushort user = _mcs.UserId;
        await SendOnIoAsync(Capabilities.BuildConfirmActive(_shareId, user, _width, _height, _options.Bpp, _channelsById.Count > 0), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.Synchronize(_shareId, user), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.ControlCooperate(_shareId, user), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.RequestControl(_shareId, user), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.FontList(_shareId, user), ct).ConfigureAwait(false);

        // The server paints a full-screen bitmap update right after the font list, which raises
        // FrameUpdated; resize the backing store here so that update lands in the new dimensions.
        lock (_frameLock) Framebuffer = new Framebuffer(_width, _height);
    }

    // ---- Active-phase pump ----

    /// <summary>Reads and processes one PDU. Returns false when the server disconnects.</summary>
    internal async Task<bool> PumpAsync(CancellationToken ct)
    {
        RdpFramePdu pdu = await _transport.ReadPduAsync(ct).ConfigureAwait(false);
        if (pdu.FastPath)
        {
            ProcessFastPathOutput(pdu.Data);
            return true;
        }
        McsData? data = McsLayer.ParseSendDataIndication(pdu.Data);
        if (data is not { } indication) return false;     // disconnect ultimatum
        if (indication.ChannelId == _ioChannel) await ProcessIoAsync(indication.Payload, ct).ConfigureAwait(false);
        else if (_channelsById.TryGetValue(indication.ChannelId, out string? name))
            ReassembleChannel(indication.ChannelId, name, indication.Payload);
        return true;
    }

    private async Task ProcessIoAsync(byte[] payload, CancellationToken ct)
    {
        var r = new ByteReader(payload);
        ShareControl.ControlHeader header = ShareControl.ReadControlHeader(ref r);
        switch (header.Type)
        {
            case ShareControlType.Data:
                ShareControl.DataHeader dh = ShareControl.ReadDataHeader(ref r);
                ReadOnlySpan<byte> body = Decompress(r.Rest(), dh.CompressedType);
                var dr = new ByteReader(body);
                ProcessDataPdu(dh.Type, ref dr);
                break;
            case ShareControlType.DemandActive:
                // Deactivation-reactivation: re-confirm against the new share and adopt its resolution.
                await HandleDemandActiveAsync(payload, ct).ConfigureAwait(false);
                break;
            case ShareControlType.Deactivate:
                break; // await the following Demand Active
        }
    }

    private void ProcessDataPdu(ShareDataType type, ref ByteReader r)
    {
        switch (type)
        {
            case ShareDataType.Update:
                ushort updateType = r.U16LE();
                if (updateType == 0x0001)
                {
                    lock (_frameLock) BitmapUpdate.Apply(ref r, Framebuffer);
                    FrameUpdated?.Invoke();
                }
                break;
            case ShareDataType.SetErrorInfo:
                uint errorInfo = r.U32LE();
                if (errorInfo != 0) ErrorInfoReceived?.Invoke(errorInfo);
                break;
        }
    }

    private void ProcessFastPathOutput(ReadOnlySpan<byte> data)
    {
        var r = new ByteReader(data);
        bool painted = false;
        while (r.Remaining > 0)
        {
            byte updateHeader = r.U8();
            int code = updateHeader & 0x0F;
            int fragmentation = (updateHeader >> 4) & 0x03;
            int compression = (updateHeader >> 6) & 0x03;
            byte compressionFlags = 0;
            if ((compression & 0x02) != 0) compressionFlags = r.U8(); // FASTPATH_OUTPUT_COMPRESSION_USED
            int size = r.U16LE();
            ReadOnlySpan<byte> updateData = r.Take(size);
            updateData = Decompress(updateData, compressionFlags);

            // Reassemble fragmented updates (SINGLE=0, LAST=1, FIRST=2, NEXT=3). Large bitmap
            // regions — common on weak links or big screens — arrive split across several PDUs;
            // dropping a fragment would leave holes in the frame.
            switch (fragmentation)
            {
                case 0: // SINGLE
                    painted |= ApplyFastPathUpdate(code, updateData);
                    break;
                case 2: // FIRST
                    _fragmentCode = code;
                    _fragment.SetLength(0);
                    _fragment.Write(updateData);
                    break;
                case 3: // NEXT
                    if (_fragment.Length + updateData.Length <= MaxFragment) _fragment.Write(updateData);
                    break;
                case 1: // LAST
                    if (_fragment.Length + updateData.Length <= MaxFragment)
                    {
                        _fragment.Write(updateData);
                        painted |= ApplyFastPathUpdate(_fragmentCode, _fragment.GetBuffer().AsSpan(0, (int)_fragment.Length));
                    }
                    _fragment.SetLength(0);
                    break;
            }
        }
        if (painted) FrameUpdated?.Invoke();
    }

    /// <summary>Applies MPPC bulk decompression when the flags mark the payload compressed; otherwise passthrough.</summary>
    private ReadOnlySpan<byte> Decompress(ReadOnlySpan<byte> data, byte compressionFlags)
    {
        if ((compressionFlags & Graphics.Mppc.PacketCompressed) == 0) return data;
        _mppc ??= new Graphics.Mppc((compressionFlags & Graphics.Mppc.CompressionTypeMask) == 1 ? 1 : 0);
        ReadOnlySpan<byte> result = _mppc.Decompress(data, compressionFlags, out bool passthrough);
        // Valid until the next Decompress call; the caller consumes it before then.
        return passthrough ? data : result;
    }

    private bool ApplyFastPathUpdate(int code, ReadOnlySpan<byte> updateData)
    {
        if (code != 0x1) return false; // FASTPATH_UPDATETYPE_BITMAP only; others (orders/palette/pointer) ignored
        var rr = new ByteReader(updateData);
        rr.U16LE(); // updateType (UPDATETYPE_BITMAP)
        lock (_frameLock) BitmapUpdate.Apply(ref rr, Framebuffer);
        return true;
    }

    private static bool LooksLikeShareControl(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6) return false;
        int pduType = payload[2] | (payload[3] << 8);
        int type = pduType & 0x0F;
        return (pduType & 0x0010) != 0 && type is 0x1 or 0x3 or 0x6 or 0x7 or 0xA;
    }

    // ---- Input ----

    internal Task SendScancodeAsync(uint scanCode, bool down, CancellationToken ct) =>
        SendRawAsync(FastPathInput.Scancode(scanCode, down), ct);

    internal Task SendUnicodeAsync(ushort code, bool down, CancellationToken ct) =>
        SendRawAsync(FastPathInput.Unicode(code, down), ct);

    internal Task SendMouseAsync(uint flags, uint x, uint y, CancellationToken ct) =>
        SendRawAsync(FastPathInput.Mouse(flags, x, y), ct);

    internal ushort ChannelId(string name) => _mcs.Channels.TryGetValue(name, out ushort id) ? id : (ushort)0;

    /// <summary>Sends a complete virtual-channel PDU, chunked with CHANNEL_PDU_HEADER (MS-RDPBCGR 2.2.6.1).</summary>
    internal Task SendVirtualChannelAsync(string name, byte[] data, CancellationToken ct)
    {
        ushort channelId = ChannelId(name);
        if (channelId == 0) throw new RdpProtocolException($"Virtual channel '{name}' was not joined.");
        return Guarded(async () =>
        {
            int offset = 0;
            bool first = true;
            do
            {
                int chunk = Math.Min(ChannelChunkSize, data.Length - offset);
                uint flags = (first ? ChannelFlagFirst : 0u) | (offset + chunk >= data.Length ? ChannelFlagLast : 0u);
                var w = new ByteWriter(chunk + 8);
                w.U32LE((uint)data.Length);     // total length of the reassembled PDU
                w.U32LE(flags);
                w.Bytes(data.AsSpan(offset, chunk));
                await _mcs.SendOnChannelAsync(channelId, w.ToArray(), ct).ConfigureAwait(false);
                offset += chunk;
                first = false;
            } while (offset < data.Length);
        }, ct);
    }

    private void ReassembleChannel(ushort channelId, string name, byte[] payload)
    {
        var r = new ByteReader(payload);
        uint totalLength = r.U32LE();
        uint flags = r.U32LE();
        ReadOnlySpan<byte> chunk = r.Rest();
        if ((flags & ChannelFlagFirst) != 0 || !_vcReassembly.TryGetValue(channelId, out MemoryStream? buffer))
        {
            buffer = new MemoryStream();
            _vcReassembly[channelId] = buffer;
        }
        if (totalLength > 32 * 1024 * 1024) throw new RdpProtocolException("Virtual channel PDU too large.");
        buffer.Write(chunk);
        if ((flags & ChannelFlagLast) != 0)
        {
            byte[] complete = buffer.ToArray();
            _vcReassembly.Remove(channelId);
            ChannelData?.Invoke(new ChannelPayload(name, complete));
        }
    }

    /// <summary>Takes a consistent copy of the current desktop surface for presentation.</summary>
    internal (byte[] Bgra, int Width, int Height) SnapshotFrame()
    {
        lock (_frameLock)
        {
            Framebuffer fb = Framebuffer;
            return ((byte[])fb.Bgra.Clone(), fb.Width, fb.Height);
        }
    }

    // ---- Framing helpers ----

    private Task SendOnIoAsync(byte[] sharePdu, CancellationToken ct) =>
        Guarded(() => _mcs.SendOnChannelAsync(_ioChannel, sharePdu, ct), ct);

    private Task SendRawAsync(byte[] frame, CancellationToken ct) =>
        Guarded(() => _transport.SendRawAsync(frame, ct), ct);

    private async Task Guarded(Func<Task> action, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task<byte[]> ReadIoPayloadAsync(CancellationToken ct)
    {
        while (true)
        {
            RdpFramePdu pdu = await _transport.ReadPduAsync(ct).ConfigureAwait(false);
            if (pdu.FastPath) continue; // no fast-path output before the active phase
            McsData? data = McsLayer.ParseSendDataIndication(pdu.Data);
            if (data is not { } indication) throw new RdpProtocolException("Server disconnected during connection.");
            if (indication.ChannelId == _ioChannel) return indication.Payload;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _tcp?.Close(); } catch { /* ignore */ }
        _tcp?.Dispose();
        _writeGate.Dispose();
        await Task.CompletedTask;
    }
}
