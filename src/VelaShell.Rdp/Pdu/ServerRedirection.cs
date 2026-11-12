using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Pdu;

internal static class RedirectionFlags
{
    internal const uint TargetNetAddress = 0x00000001;
    internal const uint LoadBalanceInfo = 0x00000002;
    internal const uint Username = 0x00000004;
    internal const uint Domain = 0x00000008;
    internal const uint Password = 0x00000010;
    internal const uint NoRedirect = 0x00000080;
    internal const uint TargetFqdn = 0x00000100;
    internal const uint TargetNetBiosName = 0x00000200;
    internal const uint TargetNetAddresses = 0x00000800;
    internal const uint ClientTsvUrl = 0x00001000;
    internal const uint ServerTsvCapable = 0x00002000;
    internal const uint PasswordIsPkEncrypted = 0x00004000;
    internal const uint RedirectionGuid = 0x00008000;
    internal const uint TargetCertificate = 0x00010000;
}

internal readonly record struct ServerRedirection(
    uint SessionId,
    uint Flags,
    string? TargetNetAddress,
    string? TargetFqdn,
    string? TargetNetBiosName,
    IReadOnlyList<string> TargetNetAddresses,
    int LoadBalanceInfoLength,
    int TsvUrlLength,
    int RedirectionGuidLength,
    bool HasPasswordOrCookie,
    bool HasTargetCertificate,
    byte[]? LoadBalanceInfo = null);

internal static class ServerRedirectionParser
{
    private const ushort SecurityRedirectionPacket = 0x0400;
    private const int HeaderLength = 12;
    private const int MaxFieldLength = 512 * 1024;

    internal static ServerRedirection Parse(ref ByteReader r)
    {
        r.Skip(2); // pad2Octets
        ushort securityFlags = r.U16LE();
        ushort packetLength = r.U16LE();
        if (securityFlags != SecurityRedirectionPacket)
        {
            throw new RdpProtocolException("The server redirection PDU has invalid security flags.");
        }

        if (packetLength < HeaderLength)
        {
            throw new RdpProtocolException("The server redirection PDU has an invalid length.");
        }

        ReadOnlySpan<byte> encoded = r.Take(packetLength - 4);
        var body = new ByteReader(encoded);
        uint sessionId = body.U32LE();
        uint flags = body.U32LE();
        string? targetNetAddress = null;
        string? targetFqdn = null;
        string? targetNetBiosName = null;
        var targetNetAddresses = new List<string>();
        int loadBalanceInfoLength = 0;
        int tsvUrlLength = 0;
        int redirectionGuidLength = 0;
        bool hasPasswordOrCookie = false;
        bool hasTargetCertificate = false;

        if ((flags & RedirectionFlags.TargetNetAddress) != 0) { targetNetAddress = ReadUnicodeField(ref body, 80); }
        byte[]? loadBalanceInfo = null;
        if ((flags & RedirectionFlags.LoadBalanceInfo) != 0)
        {
            loadBalanceInfo = ReadOpaqueField(ref body);
            loadBalanceInfoLength = loadBalanceInfo.Length;
        }
        if ((flags & RedirectionFlags.Username) != 0) { _ = ReadUnicodeField(ref body, 512); }
        if ((flags & RedirectionFlags.Domain) != 0) { _ = ReadUnicodeField(ref body, 52); }
        if ((flags & RedirectionFlags.Password) != 0)
        {
            hasPasswordOrCookie = true;
            _ = ReadOpaqueFieldLength(ref body);
        }
        if ((flags & RedirectionFlags.TargetFqdn) != 0) { targetFqdn = ReadUnicodeField(ref body, 512); }
        if ((flags & RedirectionFlags.TargetNetBiosName) != 0) { targetNetBiosName = ReadUnicodeField(ref body, 32); }
        if ((flags & RedirectionFlags.ClientTsvUrl) != 0) { tsvUrlLength = ReadOpaqueFieldLength(ref body); }
        if ((flags & RedirectionFlags.RedirectionGuid) != 0) { redirectionGuidLength = ReadOpaqueFieldLength(ref body); }
        if ((flags & RedirectionFlags.TargetCertificate) != 0)
        {
            hasTargetCertificate = true;
            _ = ReadOpaqueFieldLength(ref body);
        }
        if ((flags & RedirectionFlags.TargetNetAddresses) != 0)
        {
            int fieldLength = checked((int)ReadUInt32(ref body));
            if (fieldLength < 4 || fieldLength > MaxFieldLength || fieldLength > body.Remaining)
            {
                throw new RdpProtocolException("The server redirection target-address field is invalid.");
            }

            var addresses = new ByteReader(body.Take(fieldLength));
            uint count = addresses.U32LE();
            if (count is 0 or > 256)
            {
                throw new RdpProtocolException("The server redirection target-address count is invalid.");
            }

            for (uint i = 0; i < count; i++)
            {
                targetNetAddresses.Add(ReadUnicodeField(ref addresses, 80));
            }
        }

        return new ServerRedirection(sessionId, flags, targetNetAddress, targetFqdn, targetNetBiosName,
            targetNetAddresses, loadBalanceInfoLength, tsvUrlLength, redirectionGuidLength,
            hasPasswordOrCookie, hasTargetCertificate, loadBalanceInfo);
    }

    /// <summary>
    /// The routing token the client sends back to the broker. It must fit in an X.224 Connection Request (one-byte
    /// length indicator: 254 - 6 header bytes - 8 RDP_NEG_REQ bytes - CR LF = 238), so a longer one is refused here.
    /// </summary>
    private static byte[] ReadOpaqueField(ref ByteReader r)
    {
        int length = checked((int)ReadUInt32(ref r));
        if (length < 0 || length > 238 || length > r.Remaining)
        {
            throw new RdpProtocolException("The server redirection load-balance field is invalid.");
        }

        return r.Take(length).ToArray();
    }

    private static string ReadUnicodeField(ref ByteReader r, int maxBytes)
    {
        int length = checked((int)ReadUInt32(ref r));
        if (length < 2 || length > maxBytes || (length & 1) != 0)
        {
            throw new RdpProtocolException("The server redirection Unicode field is invalid.");
        }

        ReadOnlySpan<byte> bytes = r.Take(length);
        if (bytes[^1] != 0 || bytes[^2] != 0)
        {
            throw new RdpProtocolException("The server redirection Unicode field is not null terminated.");
        }

        string value = Encoding.Unicode.GetString(bytes[..^2]);
        if (value.Any(char.IsControl))
        {
            throw new RdpProtocolException("The server redirection Unicode field contains control characters.");
        }

        return value;
    }

    private static int ReadOpaqueFieldLength(ref ByteReader r)
    {
        int length = checked((int)ReadUInt32(ref r));
        if (length < 0 || length > MaxFieldLength || length > r.Remaining)
        {
            throw new RdpProtocolException("The server redirection data field is invalid.");
        }

        r.Skip(length);
        return length;
    }

    private static uint ReadUInt32(ref ByteReader r) => r.U32LE();
}
