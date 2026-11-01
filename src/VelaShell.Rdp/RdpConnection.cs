using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    IReadOnlyList<ChannelDef> Channels, RdpSecurity Security = RdpSecurity.Auto,
    RdpPerformanceFlags Performance = RdpPerformanceFlags.DisableWallpaper | RdpPerformanceFlags.DisableFullWindowDrag | RdpPerformanceFlags.DisableMenuAnimations,
    bool AudioPlayback = false, int ScaleFactor = 100, bool DrawingOrders = true, TimeSpan KeepAlive = default)
{
    /// <summary>Never prints the password (the compiler-generated record ToString would).</summary>
    public override string ToString() => $"{nameof(RdpConnectOptions)} {{ Host = {Host}, Port = {Port}, Username = {Username}, Security = {Security} }}";
}

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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _channelWriteGates = new();
    private readonly CancellationTokenSource _sendStop = new();
    private readonly Dictionary<ushort, string> _channelsById = [];
    private readonly Dictionary<ushort, (MemoryStream Buffer, uint Total)> _vcReassembly = [];
    /// <summary>Largest complete virtual-channel message accepted (cliprdr formats/file lists, rdpdr I/O).</summary>
    internal const uint MaxChannelMessage = 32 * 1024 * 1024;
    private const int ChannelChunkSize = 1600;
    private const uint ChannelFlagFirst = 0x01, ChannelFlagLast = 0x02;

    private readonly MemoryStream _fragment = new();
    private int _fragmentCode;
    private const int MaxFragment = 16 * 1024 * 1024;
    private Graphics.Mppc? _mppc;                    // server→client bulk decompression (shared history)

    // Replaced only on the pump thread (reactivation); readers take the reference once and use that surface.
    private volatile Framebuffer _framebuffer = new(1, 1);
    internal Framebuffer Framebuffer { get => _framebuffer; private set => _framebuffer = value; }
    internal event Action? FrameUpdated;
    internal event Action<ChannelPayload>? ChannelData;
    internal event Action<uint>? ErrorInfoReceived;

    /// <summary>The server's Save Session Info infoType (0/1 = a user logged on; 2 = plain notify; 3 = extended).</summary>
    internal event Action<uint>? LogonInfoReceived;

    internal RdpConnection(RdpConnectOptions options)
    {
        _options = options;
        _width = options.Width;
        _height = options.Height;
    }

    /// <summary>The connection step in progress; reported when an attempt fails.</summary>
    internal RdpConnectStage Stage { get; private set; }

    /// <summary>The TLS version negotiated with the server, once the handshake has completed.</summary>
    internal System.Security.Authentication.SslProtocols? TlsProtocol { get; private set; }

    /// <summary>The security layer the server selected (<see cref="RdpSecurity.Nla"/> or <see cref="RdpSecurity.Tls"/>).</summary>
    internal RdpSecurity Security { get; private set; }

    private TlsRecordTap? _tlsTap;
    private TlsCipherSuite? _tlsCipher;

    /// <summary>
    /// For failure reports after the handshake: the cipher suite and the framing of the records the server
    /// sent since (e.g. "TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384; server sent: Alert 26 B").
    /// </summary>
    internal string? TlsTrace => _tlsCipher is { } cipher && _tlsTap?.DescribeSinceHandshake() is { } records
        ? $"{cipher}; server sent: {records}"
        : null;

    internal async Task ConnectAsync(CancellationToken ct)
    {
        Stage = RdpConnectStage.Tcp;
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);
        ConfigureKeepAlive(_tcp.Client, _options.KeepAlive);
        _transport = new RdpTransport(_tcp.GetStream());

        // X.224 negotiation: offer TLS and NLA and let the server pick (NLA-only offers both too, as the spec
        // asks for PROTOCOL_SSL alongside PROTOCOL_HYBRID, and then insists on NLA), or TLS only.
        Stage = RdpConnectStage.Negotiation;
        SecurityProtocol requested = _options.Security == RdpSecurity.Tls
            ? SecurityProtocol.Ssl
            : SecurityProtocol.Ssl | SecurityProtocol.Hybrid;
        await _transport.SendTpktAsync(RdpTransport.BuildConnectionRequest(_options.Username, requested), ct).ConfigureAwait(false);
        (SecurityProtocol selected, _) = await _transport.ReadConnectionConfirmAsync(ct).ConfigureAwait(false);
        bool nla = selected.HasFlag(SecurityProtocol.Hybrid) || selected.HasFlag(SecurityProtocol.HybridEx);
        if (!nla && _options.Security == RdpSecurity.Nla)
        {
            throw new RdpNlaUnavailableException();
        }
        Security = nla ? RdpSecurity.Nla : RdpSecurity.Tls;

        if (nla)
        {
            SslStream ssl = await StartTlsAsync(ct).ConfigureAwait(false);
            Stage = RdpConnectStage.Nla;
            await CredSsp.AuthenticateAsync(ssl, _options.CertificateHost, _options.Domain, _options.Username, _options.Password, ct).ConfigureAwait(false);
        }
        else if (selected.HasFlag(SecurityProtocol.Ssl))
        {
            _ = await StartTlsAsync(ct).ConfigureAwait(false);
        }
        else
        {
            throw new RdpProtocolException("The server offered only legacy RDP security, which is disabled.");
        }

        // MCS + GCC.
        Stage = RdpConnectStage.Mcs;
        byte[] gcc = GccData.BuildClientData(_options.Width, _options.Height, _options.Bpp,
            (uint)selected, "VelaShell", 0x409, _options.Channels, _options.ScaleFactor);
        _mcs = new McsLayer(_transport);
        await _mcs.ConnectAsync(gcc, _options.Channels, ct).ConfigureAwait(false);
        _ioChannel = _mcs.Server.IoChannelId;
        foreach ((string name, ushort id) in _mcs.Channels)
        {
            _channelsById[id] = name;
        }

        // Client Info on the I/O channel.
        Stage = RdpConnectStage.Login;
        byte[] clientInfo = ClientInfo.Build(_options.Domain, _options.Username, _options.Password, autoLogon: true,
            (uint)_options.Performance, _options.AudioPlayback);
        try { await SendOnIoAsync(clientInfo, ct).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(clientInfo); }   // carries the password in the clear

        // Licensing then capability exchange.
        Stage = RdpConnectStage.Licensing;
        byte[] demandActive = await ReceiveLicensingThenDemandActiveAsync(ct).ConfigureAwait(false);
        Stage = RdpConnectStage.Capabilities;
        await HandleDemandActiveAsync(demandActive, ct).ConfigureAwait(false);
        _tlsTap?.Stop();
    }

    /// <summary>Unanswered keep-alive probes, <see cref="KeepAliveProbeInterval"/> apart, before the connection is declared dead.</summary>
    internal const int KeepAliveProbes = 3;

    internal static readonly TimeSpan KeepAliveProbeInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// TCP keep-alive after <paramref name="idle"/> of silence. A session whose display updates are paused, or whose
    /// desktop simply does not change, can be silent for hours, and NAT gateways, firewalls and cloud load balancers
    /// drop idle connections after minutes — without telling either end, so the next click went into a dead socket.
    /// The probes keep such middleboxes' state alive, and a dead path is noticed within about
    /// idle + <see cref="KeepAliveProbes"/> × <see cref="KeepAliveProbeInterval"/> instead of only when the user returns.
    /// Best effort: an option the platform lacks is skipped.
    /// </summary>
    internal static void ConfigureKeepAlive(Socket socket, TimeSpan idle)
    {
        if (idle <= TimeSpan.Zero)
        {
            return;
        }

        try { socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException) { return; }

        TrySet(SocketOptionName.TcpKeepAliveTime, (int)Math.Clamp(Math.Ceiling(idle.TotalSeconds), 1, int.MaxValue));
        TrySet(SocketOptionName.TcpKeepAliveInterval, (int)KeepAliveProbeInterval.TotalSeconds);
        TrySet(SocketOptionName.TcpKeepAliveRetryCount, KeepAliveProbes);

        void TrySet(SocketOptionName name, int value)
        {
            try { socket.SetSocketOption(SocketOptionLevel.Tcp, name, value); }
            catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException) { /* platform default */ }
        }
    }

    private async Task<SslStream> StartTlsAsync(CancellationToken ct)
    {
        Stage = RdpConnectStage.Tls;
        _tlsTap = new TlsRecordTap(_transport.Stream);
        SslStream ssl = await RdpTls.AuthenticateAsync(_tlsTap, _options.CertificateHost, _options.CertificatePin, ct).ConfigureAwait(false);
        _tlsTap.MarkHandshakeComplete();
        TlsProtocol = ssl.SslProtocol;
        _tlsCipher = ssl.NegotiatedCipherSuite;
        _transport.UpgradeStream(ssl);
        return ssl;
    }

    private async Task<byte[]> ReceiveLicensingThenDemandActiveAsync(CancellationToken ct)
    {
        while (true)
        {
            byte[] payload = await ReadIoPayloadAsync(ct).ConfigureAwait(false);
            if (LooksLikeShareControl(payload))
            {
                return payload;   // server skipped or finished licensing
            }

            if (Licensing.Process(payload))
            {
                return await ReadIoPayloadAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleDemandActiveAsync(byte[] payload, CancellationToken ct)
    {
        var r = new ByteReader(payload);
        ShareControl.ControlHeader header = ShareControl.ReadControlHeader(ref r);
        if (header.Type != ShareControlType.DemandActive)
        {
            throw new RdpProtocolException("Expected Demand Active PDU.");
        }

        Capabilities.DemandActive active = Capabilities.ReadDemandActive(ref r);
        _shareId = active.ShareId;
        ServerSupportsSuppressOutput = active.SuppressOutput;
        ServerSupportsRefreshRect = active.RefreshRect;
        // Adopt the resolution the server actually settled on (initial negotiation, or the result of
        // a display-control resize); keep the requested size only when the caps omit a bitmap set.
        if (active.Width > 0 && active.Height > 0) { _width = active.Width; _height = active.Height; }

        ushort user = _mcs.UserId;
        await SendOnIoAsync(Capabilities.BuildConfirmActive(_shareId, user, _width, _height, _options.Bpp, _channelsById.Count > 0, _options.DrawingOrders), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.Synchronize(_shareId, user), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.ControlCooperate(_shareId, user), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.RequestControl(_shareId, user), ct).ConfigureAwait(false);
        await SendOnIoAsync(ControlPdus.FontList(_shareId, user), ct).ConfigureAwait(false);

        // The server paints a full-screen bitmap update right after the font list, which raises
        // FrameUpdated; resize the backing store here so that update lands in the new dimensions.
        StartSurface(active.Bpp is 0 ? _options.Bpp : active.Bpp);
    }

    // ---- Active-phase pump ----

    /// <summary>Reads and processes one PDU. Returns false when the server disconnects.</summary>
    internal async Task<bool> PumpAsync(CancellationToken ct)
    {
        RdpFramePdu pdu;
        try { pdu = await _transport.ReadPduAsync(ct).ConfigureAwait(false); }
        // A failing read is the network, not the protocol: reset, keep-alive timeout, a socket closed after a stalled
        // send. Marked so callers can reconnect; malformed data still surfaces as RdpProtocolException.
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
            && ex is not RdpConnectionLostException && !ct.IsCancellationRequested)
        {
            throw new RdpConnectionLostException(ex);
        }

        if (pdu.FastPath)
        {
            ProcessFastPathOutput(pdu.Data);
            return true;
        }
        McsData? data = McsLayer.ParseSendDataIndication(pdu.Data);
        if (data is not { } indication)
        {
            return false;     // disconnect ultimatum
        }

        if (indication.ChannelId == _ioChannel)
        {
            await ProcessIoAsync(indication.Payload, ct).ConfigureAwait(false);
        }
        else if (_channelsById.TryGetValue(indication.ChannelId, out string? name))
        {
            ReassembleChannel(indication.ChannelId, name, indication.Payload);
        }

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

    internal void ProcessDataPdu(ShareDataType type, ref ByteReader r)
    {
        switch (type)
        {
            case ShareDataType.Update:
                ushort updateType = r.U16LE();
                if (updateType == 0x0001)
                {
                    _ = BitmapUpdate.Apply(ref r, Framebuffer);   // decodes outside the surface lock

                    FrameUpdated?.Invoke();
                }
                else if (updateType == 0x0000 && _orders is not null)   // UPDATETYPE_ORDERS
                {
                    r.Skip(2);                                    // pad2OctetsA
                    ushort numberOrders = r.U16LE();
                    r.Skip(2);                                    // pad2OctetsB
                    _orders.Process(r.Rest(), numberOrders);
                    OrderUpdates++;
                    FrameUpdated?.Invoke();
                }
                break;
            case ShareDataType.Pointer:
                {
                    RdpPointer? pointer;
                    try { pointer = _pointers.SlowPath(r.Rest()); }
                    catch (RdpProtocolException) { pointer = null; }
                    EmitPointer(pointer);
                }
                break;
            case ShareDataType.SetErrorInfo:
                uint errorInfo = r.U32LE();
                if (errorInfo != 0)
                {
                    ErrorInfoReceived?.Invoke(errorInfo);
                }

                break;
            case ShareDataType.SaveSessionInfo:
                // The server announces the logon result (MS-RDPBCGR 2.2.10.1). A logon notification means the
                // credentials were accepted and a session started — so a logoff right after it is the server
                // terminating an established session (policy), not a credentials or protocol failure.
                LogonInfoReceived?.Invoke(r.U32LE());
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
            if ((compression & 0x02) != 0)
            {
                compressionFlags = r.U8(); // FASTPATH_OUTPUT_COMPRESSION_USED
            }

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
                    ResetFragment();
                    _fragment.Write(updateData);
                    break;
                case 3: // NEXT
                    if (_fragment.Length + updateData.Length <= MaxFragment)
                    {
                        _fragment.Write(updateData);
                    }

                    break;
                case 1: // LAST
                    if (_fragment.Length + updateData.Length <= MaxFragment)
                    {
                        _fragment.Write(updateData);
                        painted |= ApplyFastPathUpdate(_fragmentCode, _fragment.GetBuffer().AsSpan(0, (int)_fragment.Length));
                    }
                    ResetFragment();
                    break;
            }
        }
        if (painted)
        {
            FrameUpdated?.Invoke();
        }
    }

    /// <summary>Empties the fast-path fragment buffer; a large update (up to 16 MB) does not keep its peak capacity alive.</summary>
    private void ResetFragment()
    {
        _fragment.SetLength(0);
        if (_fragment.Capacity > 1024 * 1024)
        {
            _fragment.Capacity = 64 * 1024;
        }
    }

    /// <summary>Applies MPPC bulk decompression when the flags mark the payload compressed; otherwise passthrough.</summary>
    private ReadOnlySpan<byte> Decompress(ReadOnlySpan<byte> data, byte compressionFlags)
    {
        if ((compressionFlags & (Graphics.Mppc.PacketCompressed | Graphics.Mppc.PacketAtFront | Graphics.Mppc.PacketFlushed)) == 0)
        {
            return data;
        }

        _mppc ??= new Graphics.Mppc((compressionFlags & Graphics.Mppc.CompressionTypeMask) == 1 ? 1 : 0);
        ReadOnlySpan<byte> result = _mppc.Decompress(data, compressionFlags, out bool passthrough);
        // Valid until the next Decompress call; the caller consumes it before then.
        return passthrough ? data : result;
    }

    internal bool ApplyFastPathUpdate(int code, ReadOnlySpan<byte> updateData)
    {
        if (code is >= PointerDecoder.FastPathNull and <= PointerDecoder.FastPathNew)
        {
            RdpPointer? pointer;
            try { pointer = _pointers.FastPath(code, updateData); }
            catch (RdpProtocolException) { pointer = null; }   // cosmetic: a malformed cursor never costs the session
            EmitPointer(pointer);
            return false;
        }

        if (code == 0x0)   // FASTPATH_UPDATETYPE_ORDERS
        {
            if (_orders is null)
            {
                return false;   // not negotiated
            }

            var orders = new ByteReader(updateData);
            ushort numberOrders = orders.U16LE();
            _orders.Process(orders.Rest(), numberOrders);
            OrderUpdates++;
            return true;
        }

        if (code != 0x1)
        {
            return false; // FASTPATH_UPDATETYPE_BITMAP; palette (8 bpp) and surface commands are not negotiated
        }

        var rr = new ByteReader(updateData);
        _ = rr.U16LE(); // updateType (UPDATETYPE_BITMAP)
        _ = BitmapUpdate.Apply(ref rr, Framebuffer);   // decodes outside the surface lock

        return true;
    }

    // Drawing orders (when negotiated): one decoder per connection, so the bitmap cache survives a reactivation (a
    // resize) like FreeRDP's; its primary-order state is reset then, and it draws on whatever surface is current.
    private OrderDecoder? _orders;

    /// <summary>Orders updates processed (diagnostics and interop tests).</summary>
    internal int OrderUpdates { get; private set; }

    /// <summary>Drawing operations by kind: "fill=… dst=… pat=… scr=… mem=…".</summary>
    internal string OrderStats => $"fill={_orderSurface.Counts[0]} dst={_orderSurface.Counts[1]} pat={_orderSurface.Counts[2]} scr={_orderSurface.Counts[3]} mem={_orderSurface.Counts[4]}";

    private int _ordersBpp;
    private readonly OrderSurface _orderSurface = new();

    /// <summary>A new surface at the activated size, and the drawing-order decoder pointed at it.</summary>
    internal void StartSurface(int sessionBpp)
    {
        Framebuffer = new Framebuffer(_width, _height);
        _orderSurface.Current = Framebuffer;
        if (!_options.DrawingOrders)
        {
            return;
        }

        if (_orders is null || _ordersBpp != sessionBpp)
        {
            _orders = new OrderDecoder(sessionBpp, _orderSurface, Capabilities.BitmapCacheCells);
            _ordersBpp = sessionBpp;
        }
        else
        {
            _orders.ResetPrimaryState();
        }
    }

    /// <summary>Forwards drawing orders to the framebuffer of the current activation (it is replaced on a resize).</summary>
    private sealed class OrderSurface : IOrderTarget
    {
        internal Framebuffer Current { get; set; } = new(1, 1);

        /// <summary>Operations drawn per kind: fill, DstBlt, PatBlt, ScrBlt, MemBlt (diagnostics and interop tests).</summary>
        internal readonly int[] Counts = new int[5];

        public int Width => Current.Width;

        public int Height => Current.Height;

        public void FillRect(OrderRect rect, uint bgra, OrderRect? clip) { Counts[0]++; Current.FillRect(rect, bgra, clip); }

        public void DstRop(OrderRect rect, byte rop3, OrderRect? clip) { Counts[1]++; Current.DstRop(rect, rop3, clip); }

        public void PatBlt(OrderRect rect, byte rop3, OrderBrush brush, OrderRect? clip) { Counts[2]++; Current.PatBlt(rect, rop3, brush, clip); }

        public void ScreenCopy(int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip) { Counts[3]++; Current.ScreenCopy(srcX, srcY, dest, rop3, clip); }

        public void CachedBlit(CachedBitmap bitmap, int srcX, int srcY, OrderRect dest, byte rop3, OrderRect? clip)
        {
            Counts[4]++;
            Current.CachedBlit(bitmap, srcX, srcY, dest, rop3, clip);
        }
    }

    /// <summary>The server's mouse pointer changed (decoded and cached by <see cref="PointerDecoder"/>).</summary>
    internal event Action<RdpPointer>? PointerChanged;

    // Matches the pointer cache sizes advertised in the Pointer capability set (colour 20, pointer 21).
    private readonly PointerDecoder _pointers = new(21);

    private void EmitPointer(RdpPointer? pointer)
    {
        if (pointer is not null)
        {
            PointerChanged?.Invoke(pointer);
        }
    }

    private static bool LooksLikeShareControl(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 6)
        {
            return false;
        }

        int pduType = payload[2] | (payload[3] << 8);
        int type = pduType & 0x0F;
        return (pduType & 0x0010) != 0 && type is 0x1 or 0x3 or 0x6 or 0x7 or 0xA;
    }

    // ---- Input ----

    internal Task SendScancodeAsync(uint scanCode, bool down, CancellationToken ct) =>
        SendRawAsync(FastPathInput.Scancode(scanCode, down), ct);

    internal async Task SendUnicodeTextAsync(string text, CancellationToken ct)
    {
        foreach (byte[] pdu in FastPathInput.UnicodeText(text))
        {
            await SendRawAsync(pdu, ct).ConfigureAwait(false);
        }
    }

    internal Task SendUnicodeAsync(ushort code, bool down, CancellationToken ct) =>
        SendRawAsync(FastPathInput.Unicode(code, down), ct);

    internal Task SendMouseAsync(uint flags, uint x, uint y, CancellationToken ct) =>
        SendRawAsync(FastPathInput.Mouse(flags, x, y), ct);

    /// <summary>Whether the server (General capability set) accepts TS_SUPPRESS_OUTPUT_PDU / TS_REFRESH_RECT_PDU.</summary>
    internal bool ServerSupportsSuppressOutput { get; private set; }

    internal bool ServerSupportsRefreshRect { get; private set; }

    /// <summary>
    /// TS_SUPPRESS_OUTPUT_PDU (MS-RDPBCGR 2.2.11.3.1): with <paramref name="allow"/> false the server stops sending
    /// display updates (the view is hidden or minimized), with true it resumes for the whole desktop.
    /// </summary>
    internal Task SendSuppressOutputAsync(bool allow, CancellationToken ct)
    {
        var body = new ByteWriter(12);
        body.U8(allow ? (byte)1 : (byte)0);
        body.Zeros(3);
        if (allow)
        {
            WriteDesktopRect(body);
        }

        return SendOnIoAsync(ShareControl.BuildDataPdu(_shareId, ShareDataType.SuppressOutput, _mcs.UserId, body.Span), ct);
    }

    /// <summary>TS_REFRESH_RECT_PDU (MS-RDPBCGR 2.2.11.2.1) for the whole desktop: asks the server to repaint it.</summary>
    internal Task SendRefreshRectAsync(CancellationToken ct)
    {
        var body = new ByteWriter(12);
        body.U8(1);              // numberOfAreas
        body.Zeros(3);
        WriteDesktopRect(body);
        return SendOnIoAsync(ShareControl.BuildDataPdu(_shareId, ShareDataType.RefreshRect, _mcs.UserId, body.Span), ct);
    }

    private void WriteDesktopRect(ByteWriter body)
    {
        Framebuffer fb = Framebuffer;   // TS_RECTANGLE16, inclusive bounds
        body.U16LE(0);
        body.U16LE(0);
        body.U16LE((ushort)Math.Max(0, fb.Width - 1));
        body.U16LE((ushort)Math.Max(0, fb.Height - 1));
    }

    internal ushort ChannelId(string name) => _mcs.Channels.TryGetValue(name, out ushort id) ? id : (ushort)0;

    /// <summary>Sends a complete virtual-channel PDU, chunked with CHANNEL_PDU_HEADER (MS-RDPBCGR 2.2.6.1).</summary>
    internal async Task SendVirtualChannelAsync(string name, byte[] data, CancellationToken ct)
    {
        ushort channelId = ChannelId(name);
        if (channelId == 0)
        {
            throw new RdpProtocolException($"Virtual channel '{name}' was not joined.");
        }

        SemaphoreSlim channelGate = _channelWriteGates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _sendStop.Token);
        await channelGate.WaitAsync(waiting.Token).ConfigureAwait(false);
        // A partially sent channel message cannot be abandoned. Finish its fragments, or close
        // the connection on a stalled write; cancellation can prevent the next message.
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(_sendStop.Token);
        sending.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            waiting.Token.ThrowIfCancellationRequested();
            int offset = 0;
            bool first = true;
            do
            {
                int chunk = Math.Min(ChannelChunkSize, data.Length - offset);
                uint flags = (first ? ChannelFlagFirst : 0u) | (offset + chunk >= data.Length ? ChannelFlagLast : 0u);
                var w = new ByteWriter(chunk + 8);
                w.U32LE((uint)data.Length);     // total length of the reassembled PDU
                // CHANNEL_OPTION_SHOW_PROTOCOL requires the header to reach the server's channel consumer.
                bool showProtocol = _options.Channels.Any(channel => channel.Name == name && (channel.Options & 0x00200000) != 0);
                w.U32LE(flags | (showProtocol ? 0x10u : 0u));
                w.Bytes(data.AsSpan(offset, chunk));
                await Guarded(() => _mcs.SendOnChannelAsync(channelId, w.ToArray(), sending.Token), sending.Token).ConfigureAwait(false);
                offset += chunk;
                first = false;
                if (offset < data.Length) { await Task.Yield(); }
            } while (offset < data.Length);
        }
        catch (Exception ex) when (sending.IsCancellationRequested || ex is IOException)
        {
            _sendStop.Cancel();
            _tcp?.Close();
            throw;
        }
        finally { _ = channelGate.Release(); }
    }

    internal void ReassembleChannel(ushort channelId, string name, byte[] payload)
    {
        var r = new ByteReader(payload);
        uint totalLength = r.U32LE();
        uint flags = r.U32LE();
        // CHANNEL_PACKET_* occupies bits 16–23 and shares the bulk history with graphics.
        ReadOnlySpan<byte> chunk = Decompress(r.Rest(), (byte)(flags >> 16));
        // The total is fixed by the FIRST chunk and the bytes actually buffered may never exceed it: checking only
        // the declared length let a server stream chunks without FIRST/LAST into an ever-growing buffer.
        if ((flags & ChannelFlagFirst) != 0)
        {
            if (totalLength > MaxChannelMessage)
            {
                throw new RdpProtocolException("Virtual channel PDU too large.");
            }

            _vcReassembly[channelId] = (new MemoryStream((int)Math.Min(totalLength, 64 * 1024)), totalLength);
        }
        else if (!_vcReassembly.ContainsKey(channelId))
        {
            return;   // a continuation whose start never came: nothing to attach it to
        }

        (MemoryStream buffer, uint total) = _vcReassembly[channelId];
        if (buffer.Length + chunk.Length > total)
        {
            _ = _vcReassembly.Remove(channelId);
            throw new RdpProtocolException("Virtual channel data exceeds its declared length.");
        }

        buffer.Write(chunk);
        if ((flags & ChannelFlagLast) != 0)
        {
            _ = _vcReassembly.Remove(channelId);
            if (buffer.Length == total)
            {
                ChannelData?.Invoke(new ChannelPayload(name, buffer.ToArray()));
            }
        }
    }

    /// <summary>Takes a full copy of the current desktop surface (allocates W×H×4 — prefer <see cref="CopyFrame"/>).</summary>
    internal (byte[] Bgra, int Width, int Height) SnapshotFrame()
    {
        Framebuffer fb = Framebuffer;
        return (fb.Snapshot(), fb.Width, fb.Height);
    }

    /// <summary>
    /// Copies what changed since the last copy into a caller surface of <paramref name="width"/>×<paramref name="height"/>.
    /// When the desktop is no longer that size nothing is copied and the result carries the new size instead.
    /// </summary>
    internal RdpFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full)
    {
        Framebuffer fb = Framebuffer;
        if (fb.Width != width || fb.Height != height)
        {
            return new RdpFrameCopy(fb.Width, fb.Height, default, SizeChanged: true);
        }

        return new RdpFrameCopy(fb.Width, fb.Height, fb.CopyTo(destination, stride, full), SizeChanged: false);
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
        finally { _ = _writeGate.Release(); }
    }

    private async Task<byte[]> ReadIoPayloadAsync(CancellationToken ct)
    {
        while (true)
        {
            RdpFramePdu pdu = await _transport.ReadPduAsync(ct).ConfigureAwait(false);
            if (pdu.FastPath)
            {
                continue; // no fast-path output before the active phase
            }

            McsData? data = McsLayer.ParseSendDataIndication(pdu.Data);
            if (data is not { } indication)
            {
                throw new RdpProtocolException("Server disconnected during connection.");
            }

            if (indication.ChannelId == _ioChannel)
            {
                return indication.Payload;
            }

            // RDPDR can start its handshake before licensing / Demand Active completes.
            // Dropping that first Server Announce leaves the server waiting forever for our reply.
            if (_channelsById.TryGetValue(indication.ChannelId, out string? name))
            {
                ReassembleChannel(indication.ChannelId, name, indication.Payload);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sendStop.Cancel();
        try { _tcp?.Close(); } catch { /* ignore */ }
        _tcp?.Dispose();
        _writeGate.Dispose();
        await Task.CompletedTask;
    }
}
