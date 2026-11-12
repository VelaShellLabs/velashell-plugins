using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RecoveryProtocolTests
{
    [TestMethod]
    public void ServerCookieIsValidatedAndClientVerifierIsSerialized()
    {
        byte[] server = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(0, 4), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(8, 4), 0x12345678);
        for (int i = 0; i < 16; i++) { server[12 + i] = (byte)(i + 1); }

        var reader = new ByteReader(server);
        var cookie = AutoReconnectCookie.Parse(ref reader);
        byte[] packet = cookie.BuildClientPacket();

        Assert.HasCount(28, packet);
        Assert.AreEqual(28u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0, 4)));
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4, 4)));
        Assert.AreEqual(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4)));
        byte[] expected = HMACMD5.HashData(server.AsSpan(12, 16), new byte[32]);
        Assert.AreSequenceEqual(expected, packet[12..]);
    }

    [TestMethod]
    public void InvalidServerCookieLengthIsRejected()
    {
        byte[] server = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(0, 4), 27);
        BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(4, 4), 1);
        Assert.ThrowsExactly<RdpProtocolException>(() => ParseCookie(server));
    }

    private static AutoReconnectCookie ParseCookie(byte[] packet)
    {
        var reader = new ByteReader(packet);
        return AutoReconnectCookie.Parse(ref reader);
    }

    [TestMethod]
    public void ExtendedSaveSessionInfoStoresAutoReconnectCookie()
    {
        var options = new RdpConnectOptions("host", 3389, "user", "password", "domain", 800, 600, 32,
            null, "host", [], Security: RdpSecurity.Tls);
        var connection = new RdpConnection(options);
        var w = new ByteWriter(640);
        w.U32LE(3);                  // INFO_TYPE_LOGON_EXTENDED_INF
        w.U16LE(38);                 // 2-byte length: header + cbFieldData + 28-byte cookie
        w.U32LE(1);                  // LOGON_EX_AUTORECONNECTCOOKIE
        w.U32LE(28);
        w.U32LE(28);
        w.U32LE(1);
        w.U32LE(0xCAFEBABE);
        w.Zeros(16);
        w.Zeros(570);

        var reader = new ByteReader(w.ToArray());
        connection.ProcessDataPdu(ShareDataType.SaveSessionInfo, ref reader);

        Assert.IsNotNull(connection.AutoReconnectCookie);
        Assert.AreEqual(0xCAFEBABEu, connection.AutoReconnectCookie!.LogonId);
    }

    [TestMethod]
    public void ServerRedirectionParsesSafeMetadataWithoutOpaqueSecrets()
    {
        const uint flags = RedirectionFlags.TargetNetAddress | RedirectionFlags.Password | RedirectionFlags.TargetFqdn;
        var w = new ByteWriter(256);
        w.U16LE(0);                 // pad2Octets
        w.U16LE(0x0400);            // SEC_REDIRECTION_PKT
        w.U16LE(0);                 // patched packet length
        w.U32LE(0x44);
        w.U32LE(flags);
        WriteUnicodeField(w, "10.0.0.5");
        w.U32LE(4); w.Zeros(4);     // opaque password/cookie
        WriteUnicodeField(w, "rdp.example.test");
        int packetLength = w.Length - 2;
        w.PatchU16LE(4, (ushort)packetLength);

        var reader = new ByteReader(w.ToArray());
        ServerRedirection redirection = ServerRedirectionParser.Parse(ref reader);

        Assert.AreEqual(0x44u, redirection.SessionId);
        Assert.AreEqual("10.0.0.5", redirection.TargetNetAddress);
        Assert.AreEqual("rdp.example.test", redirection.TargetFqdn);
        Assert.IsTrue(redirection.HasPasswordOrCookie);
        Assert.AreEqual(0, reader.Remaining);
    }

    private static void WriteUnicodeField(ByteWriter w, string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value + "\0");
        w.U32LE((uint)bytes.Length);
        w.Bytes(bytes);
    }
}
