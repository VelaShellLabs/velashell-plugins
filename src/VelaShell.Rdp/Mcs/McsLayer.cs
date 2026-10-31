using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Mcs;

/// <summary>A received MCS Send Data Indication: the source channel and its SVC/share payload.</summary>
internal readonly record struct McsData(ushort ChannelId, byte[] Payload);

/// <summary>
/// T.125 MCS over the RDP connection: Connect-Initial/Response (BER envelope around the
/// GCC data), Erect Domain, Attach User and Channel Join, then Send Data Request/Indication
/// framing. Follows FreeRDP's mcs.c value-for-value so it interoperates with real servers.
/// </summary>
internal sealed class McsLayer(RdpTransport transport)
{
    // DomainMCSPDU choice indices (shifted left by 2 on the wire).
    private const byte ErectDomainRequest = 1;
    private const byte DisconnectUltimatum = 8;
    private const byte AttachUserRequest = 10;
    private const byte AttachUserConfirm = 11;
    private const byte ChannelJoinRequest = 14;
    private const byte ChannelJoinConfirm = 15;
    private const byte SendDataRequest = 25;
    private const byte SendDataIndication = 26;

    private readonly RdpTransport _transport = transport;
    internal ushort UserId { get; private set; }
    internal GccServerData Server { get; private set; } = new();
    internal IReadOnlyDictionary<string, ushort> Channels { get; private set; } = new Dictionary<string, ushort>();

    internal async Task ConnectAsync(byte[] gccClientData, IReadOnlyList<ChannelDef> channels, CancellationToken ct)
    {
        // Connect-Initial → Connect-Response (BER). All MCS PDUs travel in X.224 Data TPDUs.
        await _transport.SendDataAsync(BuildConnectInitial(gccClientData), ct).ConfigureAwait(false);
        RdpFramePdu confirm = await _transport.ReadPduAsync(ct).ConfigureAwait(false);
        byte[] serverGcc = ParseConnectResponse(StripX224Data(confirm));
        Server = GccData.ParseServerData(serverGcc, channels.Count);

        // Map requested channel names to the ids the server assigned, in request order.
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < channels.Count && i < Server.ChannelIds.Count; i++)
            map[channels[i].Name] = Server.ChannelIds[i];
        Channels = map;

        // Erect Domain, Attach User.
        await SendMcsAsync(BuildErectDomain(), ct).ConfigureAwait(false);
        await SendMcsAsync([AttachUserRequest << 2], ct).ConfigureAwait(false);
        UserId = await ReadAttachUserConfirmAsync(ct).ConfigureAwait(false);

        // Join the user channel, the I/O channel and every virtual channel.
        await JoinChannelAsync(UserId, ct).ConfigureAwait(false);
        await JoinChannelAsync(Server.IoChannelId, ct).ConfigureAwait(false);
        foreach (ushort id in Server.ChannelIds) await JoinChannelAsync(id, ct).ConfigureAwait(false);
    }

    // ---- Send Data ----

    internal Task SendOnChannelAsync(ushort channelId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var w = new ByteWriter(payload.Length + 8);
        w.U8(SendDataRequest << 2);
        Per.WriteInteger16(w, UserId, 1001);   // initiator
        Per.WriteInteger16(w, channelId, 0);   // channelId
        w.U8(0x70);                            // dataPriority(high) + segmentation(begin|end)
        Per.WriteLength(w, payload.Length);
        w.Bytes(payload.Span);
        return SendMcsAsync(w.ToArray(), ct);
    }

    /// <summary>Parses a slow-path PDU as an MCS Send Data Indication, or null if it is a disconnect.</summary>
    internal static McsData? ParseSendDataIndication(ReadOnlySpan<byte> x224Tpdu)
    {
        ReadOnlySpan<byte> mcs = StripX224(x224Tpdu);
        var r = new ByteReader(mcs);
        byte choice = (byte)(r.U8() >> 2);
        if (choice == DisconnectUltimatum) return null;
        if (choice != SendDataIndication)
            throw new RdpProtocolException($"Unexpected MCS PDU choice {choice}.");
        ushort initiator = Per.ReadInteger16(ref r, 1001);
        ushort channelId = Per.ReadInteger16(ref r, 0);
        r.U8();                                 // dataPriority + segmentation
        int length = Per.ReadLength(ref r);
        _ = initiator;
        ReadOnlySpan<byte> payload = r.Take(length);
        return new McsData(channelId, payload.ToArray());
    }

    // ---- Connect-Initial / Response (BER) ----

    private static byte[] BuildConnectInitial(byte[] gccClientData)
    {
        var content = new ByteWriter(gccClientData.Length + 128);
        Ber.WriteOctetString(content, [0x01]);           // callingDomainSelector
        Ber.WriteOctetString(content, [0x01]);           // calledDomainSelector
        Ber.WriteBoolean(content, true);                 // upwardFlag
        WriteDomainParameters(content, [34, 2, 0, 1, 0, 1, 0xFFFF, 2]);   // target
        WriteDomainParameters(content, [1, 1, 1, 1, 0, 1, 0x420, 2]);     // minimum
        WriteDomainParameters(content, [0xFFFF, 0xFFFF, 0xFFFF, 1, 0, 1, 0xFFFF, 2]); // maximum
        Ber.WriteOctetString(content, gccClientData);    // userData (GCC CreateConferenceRequest)

        var w = new ByteWriter(content.Length + 8);
        Ber.WriteApplicationTag(w, 101, content.Length); // Connect-Initial
        w.Bytes(content.Span);
        return w.ToArray();
    }

    private static void WriteDomainParameters(ByteWriter w, uint[] values)
    {
        var inner = new ByteWriter(32);
        foreach (uint value in values) Ber.WriteInteger(inner, value);
        Ber.WriteSequenceTag(w, inner.Length);
        w.Bytes(inner.Span);
    }

    private static byte[] ParseConnectResponse(ReadOnlySpan<byte> mcsPayload)
    {
        var r = new ByteReader(mcsPayload);
        Ber.ReadApplicationTag(ref r, 102);              // Connect-Response
        if (Ber.ReadEnumerated(ref r) != 0)
            throw new RdpProtocolException("MCS Connect-Response rejected the connection.");
        Ber.SkipInteger(ref r);                          // calledConnectId
        int domainLen = Ber.ReadSequenceTag(ref r);      // domainParameters
        r.Skip(domainLen);
        return Ber.ReadOctetString(ref r).ToArray();     // userData = GCC Conference Create Response
    }

    // ---- Erect Domain / Attach / Join ----

    private static byte[] BuildErectDomain()
    {
        var w = new ByteWriter(8);
        w.U8(ErectDomainRequest << 2);
        Per.WriteInteger(w, 0);   // subHeight
        Per.WriteInteger(w, 0);   // subInterval
        return w.ToArray();
    }

    private async Task<ushort> ReadAttachUserConfirmAsync(CancellationToken ct)
    {
        RdpFramePdu pdu = await _transport.ReadPduAsync(ct).ConfigureAwait(false);
        var r = new ByteReader(StripX224Data(pdu));
        byte first = r.U8();
        if ((byte)(first >> 2) != AttachUserConfirm) throw new RdpProtocolException("Expected MCS Attach User Confirm.");
        if (Per.ReadEnumerated(ref r, 16) != 0) throw new RdpProtocolException("MCS Attach User rejected.");
        return (first & 0x02) != 0 ? Per.ReadInteger16(ref r, 1001) : (ushort)0;
    }

    private async Task JoinChannelAsync(ushort channelId, CancellationToken ct)
    {
        var w = new ByteWriter(8);
        w.U8(ChannelJoinRequest << 2);
        Per.WriteInteger16(w, UserId, 1001);
        Per.WriteInteger16(w, channelId, 0);
        await SendMcsAsync(w.ToArray(), ct).ConfigureAwait(false);

        RdpFramePdu pdu = await _transport.ReadPduAsync(ct).ConfigureAwait(false);
        var r = new ByteReader(StripX224Data(pdu));
        byte first = r.U8();
        if ((byte)(first >> 2) != ChannelJoinConfirm) throw new RdpProtocolException("Expected MCS Channel Join Confirm.");
        if (Per.ReadEnumerated(ref r, 16) != 0) throw new RdpProtocolException($"MCS channel join {channelId} rejected.");
        Per.ReadInteger16(ref r, 1001);               // initiator
        ushort requested = Per.ReadInteger16(ref r, 0);
        if (requested != channelId) throw new RdpProtocolException("MCS channel join mismatch.");
    }

    // ---- Framing helpers ----

    private Task SendMcsAsync(byte[] mcs, CancellationToken ct) => _transport.SendDataAsync(mcs, ct);

    private static ReadOnlySpan<byte> StripX224Data(RdpFramePdu pdu)
    {
        if (pdu.FastPath) throw new RdpProtocolException("Expected a slow-path MCS PDU.");
        return StripX224(pdu.Data);
    }

    /// <summary>Strips the X.224 Data TPDU header (LI, DT, EOT) from a TPKT payload.</summary>
    private static ReadOnlySpan<byte> StripX224(ReadOnlySpan<byte> x224Tpdu)
    {
        if (x224Tpdu.Length < 3) throw new RdpProtocolException("Short X.224 Data TPDU.");
        byte li = x224Tpdu[0];
        return x224Tpdu[(li + 1)..];
    }
}
