using System.Text;
using VelaShell.Plugin.Rdp.Managed.Wire;

namespace VelaShell.Plugin.Rdp.Managed.Mcs;

/// <summary>A static virtual channel request (MS-RDPBCGR 2.2.1.3.4.1).</summary>
internal readonly record struct ChannelDef(string Name, uint Options);

/// <summary>Parsed GCC server data: the I/O channel and the joined virtual-channel ids.</summary>
internal sealed class GccServerData
{
    internal ushort IoChannelId { get; init; }
    internal IReadOnlyList<ushort> ChannelIds { get; init; } = [];
    internal uint EncryptionMethod { get; init; }
    internal uint EncryptionLevel { get; init; }
}

/// <summary>
/// Builds the client GCC Conference Create Request (CS_CORE / CS_SECURITY / CS_NET /
/// CS_CLUSTER) and parses the server's Conference Create Response (SC_CORE / SC_SECURITY
/// / SC_NET). Only the TLS/NLA external-security path is supported, so SC_SECURITY carries
/// no server certificate or random and encryption method/level are zero.
/// </summary>
internal static class GccData
{
    private static readonly byte[] GccConnectOid = [0, 0, 20, 124, 0, 1];
    private const ushort CsCore = 0xC001, CsSecurity = 0xC002, CsNet = 0xC003, CsCluster = 0xC004;
    private const ushort ScCore = 0x0C01, ScSecurity = 0x0C02, ScNet = 0x0C03;

    internal static byte[] BuildClientData(int width, int height, int bpp, uint serverSelectedProtocol,
        string clientName, uint keyboardLayout, IReadOnlyList<ChannelDef> channels)
    {
        var core = BuildCore(width, height, bpp, serverSelectedProtocol, clientName, keyboardLayout);
        var security = BuildSecurity();
        var net = BuildNet(channels);
        var cluster = BuildCluster(serverSelectedProtocol);

        var blocks = new ByteWriter(core.Length + security.Length + net.Length + cluster.Length);
        blocks.Bytes(core);
        blocks.Bytes(security);
        blocks.Bytes(net);
        blocks.Bytes(cluster);
        return WrapConferenceCreateRequest(blocks.Span);
    }

    private static byte[] BuildCore(int width, int height, int bpp, uint serverSelectedProtocol,
        string clientName, uint keyboardLayout)
    {
        var w = new ByteWriter(256);
        int headerAt = w.Length;
        w.U16LE(CsCore);
        int lenAt = w.Reserve16();
        w.U32LE(0x00080004);                       // version RDP 5.0+
        w.U16LE((ushort)width);
        w.U16LE((ushort)height);
        w.U16LE(0xCA01);                            // colorDepth RNS_UD_COLOR_8BPP (legacy)
        w.U16LE(0xAA03);                            // SASSequence RNS_UD_SAS_DEL
        w.U32LE(keyboardLayout);
        w.U32LE(2600);                              // clientBuild
        w.Unicode(clientName, 32);                  // clientName (16 UTF-16 units)
        w.U32LE(4);                                 // keyboardType IBM enhanced
        w.U32LE(0);                                 // keyboardSubType
        w.U32LE(12);                                // keyboardFunctionKey
        w.Zeros(64);                                // imeFileName
        w.U16LE(0xCA01);                            // postBeta2ColorDepth
        w.U16LE(1);                                 // clientProductId
        w.U32LE(0);                                 // serialNumber
        w.U16LE(HighColorDepth(bpp));               // highColorDepth
        w.U16LE(0x000F);                            // supportedColorDepths (32/24/16/15)
        // earlyCapabilityFlags: ERRINFO PDU + optional 32bpp session
        ushort early = 0x0001;                      // RNS_UD_CS_SUPPORT_ERRINFO_PDU
        if (bpp == 32) early |= 0x0002;             // RNS_UD_CS_WANT_32BPP_SESSION
        w.U16LE(early);
        w.Zeros(64);                                // clientDigProductId
        w.U8(0);                                    // connectionType
        w.U8(0);                                    // pad1octet
        w.U32LE(serverSelectedProtocol);            // serverSelectedProtocol (echo)
        w.PatchU16LE(lenAt, (ushort)(w.Length - headerAt));
        return w.ToArray();
    }

    private static byte[] BuildSecurity()
    {
        var w = new ByteWriter(16);
        w.U16LE(CsSecurity);
        w.U16LE(12);                 // length
        w.U32LE(0);                  // encryptionMethods (none; TLS/NLA external)
        w.U32LE(0);                  // extEncryptionMethods
        return w.ToArray();
    }

    private static byte[] BuildNet(IReadOnlyList<ChannelDef> channels)
    {
        var w = new ByteWriter(16 + channels.Count * 12);
        int headerAt = w.Length;
        w.U16LE(CsNet);
        int lenAt = w.Reserve16();
        w.U32LE((uint)channels.Count);
        Span<byte> name = stackalloc byte[8];
        foreach (ChannelDef channel in channels)
        {
            name.Clear();
            Encoding.ASCII.GetBytes(channel.Name.AsSpan(0, Math.Min(channel.Name.Length, 8)), name);
            w.Bytes(name);
            w.U32LE(channel.Options);
        }
        w.PatchU16LE(lenAt, (ushort)(w.Length - headerAt));
        return w.ToArray();
    }

    private static byte[] BuildCluster(uint serverSelectedProtocol)
    {
        var w = new ByteWriter(16);
        w.U16LE(CsCluster);
        w.U16LE(12);
        // REDIRECTION_SUPPORTED | redirectionVersion 4 (<<2)
        w.U32LE(0x0000000D);
        w.U32LE(0);                  // RedirectedSessionID
        _ = serverSelectedProtocol;
        return w.ToArray();
    }

    private static ushort HighColorDepth(int bpp) => bpp switch
    {
        32 => 0x0018, // negotiate 24 in core; 32bpp session requested via earlyCapabilityFlags
        24 => 0x0018,
        16 => 0x0010,
        15 => 0x000F,
        _ => 0x0010
    };

    /// <summary>Wraps the client data blocks in the GCC Conference Create Request (PER). Mirrors FreeRDP gcc_write_*.</summary>
    private static byte[] WrapConferenceCreateRequest(ReadOnlySpan<byte> userData)
    {
        var w = new ByteWriter(userData.Length + 32);
        // ConnectData: Key::object (choice 0) + T.124 object identifier.
        Per.WriteChoice(w, 0);
        Per.WriteObjectIdentifier(w, GccConnectOid);
        // ConnectData::connectPDU (OCTET STRING) length = GCC PDU that follows.
        int gccLength = 12 + Per.DeterminantSize(userData.Length) + userData.Length;
        Per.WriteLength(w, gccLength);
        // ConnectGCCPDU: conferenceCreateRequest (choice 0), userData present (selection 0x08).
        Per.WriteChoice(w, 0);
        Per.WriteSelection(w, 0x08);
        // ConferenceCreateRequest::conferenceName::numeric "1", then alignment padding.
        Per.WriteNumericString(w, "1", 1);
        Per.WritePadding(w, 1);
        // UserData: one set, value present + h221NonStandard (choice 0xC0), key "Duca".
        Per.WriteNumberOfSets(w, 1);
        Per.WriteChoice(w, 0xC0);
        Per.WriteOctetString(w, "Duca"u8, 4);
        // userData::value (OCTET STRING) = the concatenated client data blocks.
        Per.WriteOctetString(w, userData, 0);
        return w.ToArray();
    }

    internal static GccServerData ParseServerData(ReadOnlySpan<byte> data, int requestedChannelCount)
    {
        var r = new ByteReader(data);
        // ConnectData: choice + T.124 OID + connectPDU length.
        Per.ReadChoice(ref r);
        Per.ReadObjectIdentifier(ref r, GccConnectOid);
        Per.ReadLength(ref r);
        // ConnectGCCPDU: conferenceCreateResponse.
        Per.ReadChoice(ref r);
        Per.ReadInteger16(ref r, 1001);                 // nodeID (UserID)
        Per.ReadInteger(ref r);                         // tag (INTEGER)
        Per.ReadEnumerated(ref r, 16);                  // result (ENUMERATED, MCS_Result)
        Per.ReadNumberOfSets(ref r);                    // number of user data sets
        r.U8();                                         // UserData value present + h221NonStandard (0xC0)
        Per.ReadOctetString(ref r, 4);                  // h221 key "McDn"
        Per.ReadLength(ref r);                           // server data block length

        ushort ioChannel = 0;
        var channelIds = new List<ushort>();
        uint encMethod = 0, encLevel = 0;
        while (r.Remaining >= 4)
        {
            int blockStart = r.Position;
            ushort type = r.U16LE();
            ushort length = r.U16LE();
            if (length < 4) throw new RdpProtocolException("Invalid GCC server block length.");
            int bodyLen = length - 4;
            ByteReader body = new(r.Peek(Math.Min(bodyLen, r.Remaining)));
            switch (type)
            {
                case ScCore:
                    break; // version / clientRequestedProtocols / earlyCapabilityFlags — not needed
                case ScSecurity:
                    encMethod = body.U32LE();
                    encLevel = body.U32LE();
                    break;
                case ScNet:
                    ioChannel = body.U16LE();
                    ushort count = body.U16LE();
                    for (int i = 0; i < count; i++) channelIds.Add(body.U16LE());
                    break;
            }
            r.Skip(bodyLen);
            if (r.Position <= blockStart) throw new RdpProtocolException("GCC server block made no progress.");
        }
        if (ioChannel == 0) throw new RdpProtocolException("Server GCC response missing I/O channel.");
        _ = requestedChannelCount;
        return new GccServerData
        {
            IoChannelId = ioChannel,
            ChannelIds = channelIds,
            EncryptionMethod = encMethod,
            EncryptionLevel = encLevel
        };
    }
}
