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
