using System.Security.Cryptography;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Pdu;

/// <summary>The server cookie from TS_LOGON_INFO_EXTENDED, retained only in memory for the next connection.</summary>
internal sealed class AutoReconnectCookie
{
    internal const int PacketLength = 28;
    private readonly byte[] _arcRandomBits;

    private AutoReconnectCookie(uint version, uint logonId, ReadOnlySpan<byte> arcRandomBits)
    {
        if (arcRandomBits.Length != 16)
        {
            throw new ArgumentException("The auto-reconnect random value must be 16 bytes.", nameof(arcRandomBits));
        }

        Version = version;
        LogonId = logonId;
        _arcRandomBits = arcRandomBits.ToArray();
    }

    internal uint Version { get; }
    internal uint LogonId { get; }

    internal static AutoReconnectCookie Parse(ref ByteReader r)
    {
        if (r.Remaining < PacketLength)
        {
            throw new RdpProtocolException("The server auto-reconnect cookie is truncated.");
        }

        uint length = r.U32LE();
        uint version = r.U32LE();
        uint logonId = r.U32LE();
        ReadOnlySpan<byte> random = r.Take(16);
        if (length != PacketLength)
        {
            throw new RdpProtocolException("The server auto-reconnect cookie has an invalid length.");
        }

        if (version != 1)
        {
            throw new RdpProtocolException("The server auto-reconnect cookie has an unsupported version.");
        }

        return new AutoReconnectCookie(version, logonId, random);
    }

    /// <summary>Creates the 28-byte client cookie. Enhanced security uses the specified all-zero client random.</summary>
    internal byte[] BuildClientPacket()
    {
        Span<byte> clientRandom = stackalloc byte[32];
        byte[] verifier = HMACMD5.HashData(_arcRandomBits, clientRandom);
        try
        {
            var w = new ByteWriter(PacketLength);
            w.U32LE(PacketLength);
            w.U32LE(Version);
            w.U32LE(LogonId);
            w.Bytes(verifier);
            CryptographicOperations.ZeroMemory(verifier);
            return w.ToArray();
        }
        catch
        {
            CryptographicOperations.ZeroMemory(verifier);
            throw;
        }
    }
}
