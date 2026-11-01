using System.Buffers.Binary;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpdrChannelTests
{
    private const ushort CtypCore = 0x4472;
    private const ushort ServerAnnounce = 0x496E, ClientIdConfirm = 0x4343, UserLoggedOn = 0x554C;
    private const ushort DeviceListAnnounce = 0x4441;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DriveRedirectionRegistersCompanionSoundChannel(bool shareDrive)
    {
        var options = new RdpClientOptions
        {
            Host = "localhost",
            Clipboard = true,
            DynamicResolution = true,
            AudioPlayback = false,
            Drives = shareDrive ? [new DriveMapping("work", Path.GetTempPath())] : []
        };

        string[] names = RdpManagedSession.BuildChannels(options).Select(channel => channel.Name).ToArray();
        string[] expected = shareDrive
            ? ["cliprdr", "rdpdr", "rdpsnd", "drdynvc"]
            : ["cliprdr", "drdynvc"];
        Assert.AreSequenceEqual(expected, names);
    }

    private static byte[] ServerAnnouncePdu(ushort minor)
    {
        byte[] p = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0), CtypCore);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), ServerAnnounce);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(4), 1);          // version major
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), minor);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 1);          // clientId
        return p;
    }

    private static byte[] Pdu(ushort packetId)
    {
        byte[] p = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(0), CtypCore);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), packetId);
        return p;
    }

    private static bool IsDeviceListAnnounce(byte[] frame) =>
        frame.Length >= 4 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)) == DeviceListAnnounce;

    private static (RdpdrChannel Channel, List<byte[]> Sent) Make()
    {
        var sent = new List<byte[]>();
        var channel = new RdpdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; },
            [new DriveMapping("work", Path.GetTempPath())]);
        return (channel, sent);
    }

    [TestMethod]
    [DataRow("C", "4300000000000000", "43000000")]
    [DataRow("work", "776F726B00000000", "77006F0072006B000000")]
    [DataRow("abcdefgh", "6162636465666768", "610062006300640065006600670068000000")]
    public async Task DeviceListAnnounceUsesUnicodeDeviceDataAndAsciiDosName(
        string name, string dosNameHex, string deviceDataHex)
    {
        var sent = new List<byte[]>();
        var channel = new RdpdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; },
            [new DriveMapping(name, Directory.GetCurrentDirectory())]);
        await channel.ProcessAsync(ServerAnnouncePdu(0x000C));
        await channel.ProcessAsync(Pdu(ClientIdConfirm));
        await channel.ProcessAsync(Pdu(UserLoggedOn));

        byte[] frame = sent.Single(IsDeviceListAnnounce);
        byte[] expectedDosName = Convert.FromHexString(dosNameHex);
        byte[] expectedDeviceData = Convert.FromHexString(deviceDataHex);
        Assert.AreEqual(CtypCore, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.AreEqual(8u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.AreSequenceEqual(expectedDosName.AsSpan(), frame.AsSpan(16, 8));
        Assert.AreEqual((uint)expectedDeviceData.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(24)));
        Assert.HasCount(28 + expectedDeviceData.Length, frame);
        Assert.AreSequenceEqual(expectedDeviceData.AsSpan(), frame.AsSpan(28));
    }

    [TestMethod]
    public async Task ModernServerGetsTheDriveOnlyAfterUserLoggedOn()
    {
        (RdpdrChannel channel, List<byte[]> sent) = Make();
        await channel.ProcessAsync(ServerAnnouncePdu(0x000C));
        await channel.ProcessAsync(Pdu(ClientIdConfirm));
        Assert.DoesNotContain(IsDeviceListAnnounce, sent, "A real Windows host ignores a drive announced before the user logs on.");
        await channel.ProcessAsync(Pdu(UserLoggedOn));
        Assert.ContainsSingle(IsDeviceListAnnounce, sent);
    }

    [TestMethod]
    public async Task LegacyServerGetsTheDriveOnClientIdConfirm()
    {
        (RdpdrChannel channel, List<byte[]> sent) = Make();
        await channel.ProcessAsync(ServerAnnouncePdu(0x0005));   // never sends PAKID_CORE_USER_LOGGEDON
        await channel.ProcessAsync(Pdu(ClientIdConfirm));
        Assert.ContainsSingle(IsDeviceListAnnounce, sent);
    }

    [TestMethod]
    [DataRow(0x000C)]
    [DataRow(0x0005)]
    public async Task ServerReannounceStartsANewDeviceAnnouncementRound(int minor)
    {
        (RdpdrChannel channel, List<byte[]> sent) = Make();
        for (int round = 0; round < 2; round++)
        {
            sent.Clear();
            await channel.ProcessAsync(ServerAnnouncePdu((ushort)minor));
            await channel.ProcessAsync(Pdu(0x5350)); // server capabilities
            await channel.ProcessAsync(Pdu(ClientIdConfirm));
            if (minor >= 0x000C)
            {
                Assert.DoesNotContain(IsDeviceListAnnounce, sent);
                await channel.ProcessAsync(Pdu(UserLoggedOn));
                await channel.ProcessAsync(Pdu(UserLoggedOn));
            }
            else
            {
                await channel.ProcessAsync(Pdu(ClientIdConfirm));
            }

            byte[] announcement = sent.Single(IsDeviceListAnnounce);
            Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(announcement.AsSpan(4)));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DriveHandshakeBeforeDemandActiveIsDelivered(bool fragmented)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        await using var connection = new RdpConnection(new RdpConnectOptions(
            "localhost", 3389, "", "", "", 800, 600, 16, null, "localhost", []));
        using var wire = new MemoryStream();
        var transport = new VelaShell.Rdp.Wire.RdpTransport(wire);
        typeof(RdpConnection).GetField("_transport", flags)!.SetValue(connection, transport);
        typeof(RdpConnection).GetField("_ioChannel", flags)!.SetValue(connection, (ushort)1003);
        var channels = (Dictionary<ushort, string>)typeof(RdpConnection).GetField("_channelsById", flags)!.GetValue(connection)!;
        channels[1004] = "rdpdr";
        var received = new List<byte[]>();
        connection.ChannelData += payload => received.Add(payload.Data);

        byte[] announce = ServerAnnouncePdu(0x000C);
        async Task WriteFrame(ushort channel, byte[] payload)
        {
            byte[] frame = [2, 0xF0, 0x80, 0x68, 0, 0, (byte)(channel >> 8), (byte)channel, 0x70, (byte)payload.Length, .. payload];
            await transport.SendTpktAsync(frame, default);
        }
        async Task WriteChunk(byte[] chunk, uint chunkFlags)
        {
            byte[] payload = new byte[8 + chunk.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)announce.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), chunkFlags);
            chunk.CopyTo(payload, 8);
            await WriteFrame(1004, payload);
        }
        if (fragmented)
        {
            await WriteChunk(announce[..6], 1);
            await WriteChunk(announce[6..], 2);
        }
        else
        {
            await WriteChunk(announce, 3);
        }
        byte[] ioPayload = [1, 2, 3, 4];
        await WriteFrame(1003, ioPayload);
        wire.Position = 0;

        var reading = (Task<byte[]>)typeof(RdpConnection).GetMethod("ReadIoPayloadAsync", flags)!
            .Invoke(connection, [CancellationToken.None])!;
        byte[] actual = await reading;
        Assert.AreSequenceEqual(ioPayload, actual);
        Assert.HasCount(1, received, "The early RDPDR Server Announce must not be discarded while waiting for licensing/Demand Active.");
        Assert.AreSequenceEqual(announce, received[0]);
    }

    [TestMethod]
    public async Task TheDriveIsAnnouncedOnlyOnce()
    {
        (RdpdrChannel channel, List<byte[]> sent) = Make();
        await channel.ProcessAsync(ServerAnnouncePdu(0x000C));
        await channel.ProcessAsync(Pdu(ClientIdConfirm));
        await channel.ProcessAsync(Pdu(UserLoggedOn));
        await channel.ProcessAsync(Pdu(UserLoggedOn));
        Assert.ContainsSingle(IsDeviceListAnnounce, sent);
    }
}
