namespace VelaShell.Rdp.Wire;

/// <summary>Security protocols from the X.224 negotiation (MS-RDPBCGR 2.2.1.1.1).</summary>
[Flags]
internal enum SecurityProtocol : uint
{
    Rdp = 0x00000000,
    Ssl = 0x00000001,
    Hybrid = 0x00000002,
    RdsTls = 0x00000004,
    HybridEx = 0x00000008
}

/// <summary>A single received PDU: either a slow-path TPKT/X.224 TPDU or a fast-path update.</summary>
internal readonly record struct RdpFramePdu(bool FastPath, byte FastPathHeader, byte[] Data);

/// <summary>
/// Frames RDP over a byte stream. Handles both the slow path (TPKT + X.224, used for
/// the whole connection sequence and for virtual-channel data) and the fast path
/// (compact output/input updates during the active phase, MS-RDPBCGR 2.2.9.1.2).
/// The underlying stream is swapped from raw TCP to <c>SslStream</c> after negotiation.
/// </summary>
internal sealed class RdpTransport(Stream stream)
{
    internal Stream Stream { get; private set; } = stream;
    internal void UpgradeStream(Stream secure) => Stream = secure;

    // ---- Slow path (TPKT) ----

    /// <summary>Wraps an X.224 TPDU in a TPKT header and sends it.</summary>
    internal async Task SendTpktAsync(ReadOnlyMemory<byte> x224Tpdu, CancellationToken ct)
    {
        int total = x224Tpdu.Length + 4;
        byte[] frame = new byte[total];
        frame[0] = 3;            // TPKT version
        frame[1] = 0;            // reserved
        frame[2] = (byte)(total >> 8);
        frame[3] = (byte)(total & 0xFF);
        x224Tpdu.Span.CopyTo(frame.AsSpan(4));
        await Stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await Stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends an MCS/data payload as a slow-path X.224 Data TPDU (LI=2, DT=0xF0, EOT=0x80).
    /// </summary>
    internal Task SendDataAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] tpdu = new byte[payload.Length + 3];
        tpdu[0] = 2;      // LI
        tpdu[1] = 0xF0;   // DT DATA
        tpdu[2] = 0x80;   // EOT
        payload.Span.CopyTo(tpdu.AsSpan(3));
        return SendTpktAsync(tpdu, ct);
    }

    // ---- Reading ----

    internal async Task<RdpFramePdu> ReadPduAsync(CancellationToken ct)
    {
        byte[] head = await ReadExactlyAsync(1, ct).ConfigureAwait(false);
        if (head[0] == 3)
        {
            // TPKT: version(1)=3, reserved(1), length(2 BE)
            byte[] rest = await ReadExactlyAsync(3, ct).ConfigureAwait(false);
            int length = (rest[1] << 8) | rest[2];
            if (length is < 4 or > 0x10_0000)
            {
                throw new RdpProtocolException("Invalid TPKT length.");
            }

            byte[] payload = await ReadExactlyAsync(length - 4, ct).ConfigureAwait(false);
            return new RdpFramePdu(false, 0, payload);
        }
        // Fast-path output: byte0 header, then 1- or 2-byte length (total PDU length incl. header).
        byte header = head[0];
        byte len1 = (await ReadExactlyAsync(1, ct).ConfigureAwait(false))[0];
        int consumed = 2;
        int total;
        if ((len1 & 0x80) != 0)
        {
            byte len2 = (await ReadExactlyAsync(1, ct).ConfigureAwait(false))[0];
            total = ((len1 & 0x7F) << 8) | len2;
            consumed = 3;
        }
        else
        {
            total = len1;
        }
        if (total < consumed || total > 0x10_0000)
        {
            throw new RdpProtocolException("Invalid fast-path length.");
        }

        byte[] body = await ReadExactlyAsync(total - consumed, ct).ConfigureAwait(false);
        return new RdpFramePdu(true, header, body);
    }

    /// <summary>Reads the X.224 Connection Confirm and returns the negotiated protocol.</summary>
    internal async Task<(SecurityProtocol Protocol, byte NegFlags)> ReadConnectionConfirmAsync(CancellationToken ct)
    {
        RdpFramePdu pdu = await ReadPduAsync(ct).ConfigureAwait(false);
        if (pdu.FastPath)
        {
            throw new RdpProtocolException("Expected X.224 Connection Confirm.");
        }

        var r = new ByteReader(pdu.Data);
        byte li = r.U8();
        if (li < 6)
        {
            throw new RdpProtocolException("Short X.224 Connection Confirm.");
        }

        byte code = r.U8();
        if ((code & 0xF0) != 0xD0)
        {
            throw new RdpProtocolException("Expected X.224 CC TPDU.");
        }

        r.Skip(5); // DST-REF(2), SRC-REF(2), class(1)
        int variable = li - 6;
        if (variable <= 0)
        {
            return (SecurityProtocol.Rdp, 0);
        }

        byte type = r.U8();
        byte flags = r.U8();
        ushort negLen = r.U16LE();
        uint data = r.U32LE();
        if (type == 0x03)
        {
            throw new RdpNegotiationFailureException(data);
        }

        if (type != 0x02)
        {
            throw new RdpProtocolException("Unexpected X.224 negotiation type.");
        }

        _ = negLen;
        return ((SecurityProtocol)data, flags);
    }

    /// <summary>Writes an already-framed PDU (e.g. a fast-path input PDU) directly to the stream.</summary>
    internal async Task SendRawAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        await Stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await Stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int next = await Stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (next == 0)
            {
                throw new RdpProtocolException("The RDP server closed the connection.");
            }

            read += next;
        }
        return buffer;
    }

    // ---- X.224 connection request ----

    /// <summary>Builds the X.224 Connection Request carrying the RDP negotiation request.</summary>
    internal static byte[] BuildConnectionRequest(string? cookieUser, SecurityProtocol requested)
    {
        var w = new ByteWriter(64);
        // Optional routing cookie, terminated with CR LF.
        if (!string.IsNullOrEmpty(cookieUser))
        {
            foreach (char c in $"Cookie: mstshash={cookieUser}")
            {
                w.U8((byte)c);
            }

            w.U8(0x0D);
            w.U8(0x0A);
        }
        // RDP_NEG_REQ
        w.U8(0x01);              // type = TYPE_RDP_NEG_REQ
        w.U8(0x00);              // flags
        w.U16LE(0x0008);         // length
        w.U32LE((uint)requested);

        int variable = w.Length;
        var tpdu = new ByteWriter(variable + 8);
        tpdu.U8((byte)(6 + variable)); // LI
        tpdu.U8(0xE0);                 // CR CDT
        tpdu.U16BE(0);                 // DST-REF
        tpdu.U16BE(0);                 // SRC-REF
        tpdu.U8(0);                    // class option
        tpdu.Bytes(w.Span);
        return tpdu.ToArray();
    }
}

/// <summary>The X.224 negotiation returned RDP_NEG_FAILURE (MS-RDPBCGR 2.2.1.2.1).</summary>
internal sealed class RdpNegotiationFailureException(uint code)
    : Exception($"RDP security negotiation failed (0x{code:X8}).")
{
    internal uint Code { get; } = code;
}
