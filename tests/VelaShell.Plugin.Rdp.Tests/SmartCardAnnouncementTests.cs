using System.Buffers.Binary;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class SmartCardAnnouncementTests
{
    [TestMethod]
    [DataRow(12)]
    [DataRow(5)]
    public async Task SmartCardsAreAvailableBeforeLogonWithStableIdsAcrossAnnouncements(int version)
    {
        List<byte[]> sent = [];
        var channel = new RdpdrChannel(p => { sent.Add(p); return Task.CompletedTask; },
            [new DriveMapping("work", Directory.GetCurrentDirectory())],
            additionalDevices: [new Device(4, 2), new Device(0x20, 5), new Device(1, 3)]);
        for (int round = 0; round < 2; round++)
        {
            sent.Clear();
            byte[] announce = new byte[12];
            Pdu(0x496E).CopyTo(announce, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(announce.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(announce.AsSpan(6), (ushort)version);
            BinaryPrimitives.WriteUInt32LittleEndian(announce.AsSpan(8), 42);
            await channel.ProcessAsync(announce);
            await channel.ProcessAsync(Pdu(0x5350));
            byte[] capability = sent.Single(p => Id(p) == 0x4350);
            Assert.AreEqual(1u, Read(capability, 48));
            await channel.ProcessAsync(Pdu(0x4343));
            await channel.ProcessAsync(Pdu(0x4343));
            byte[] early = sent.Single(p => Id(p) == 0x4441);
            if (version >= 12)
            {
                Assert.AreSequenceEqual(new (uint, uint)[] { (0x20, 3) }, Devices(early));
                await channel.ProcessAsync(Pdu(0x554C));
                await channel.ProcessAsync(Pdu(0x554C));
                Assert.AreEqual(2, sent.Count(p => Id(p) == 0x4441));
                byte[] late = sent.Last(p => Id(p) == 0x4441);
                Assert.AreSequenceEqual(new (uint, uint)[] { (8, 1), (4, 2), (1, 4) }, Devices(late));
            }
            else
            {
                Assert.AreSequenceEqual(new (uint, uint)[] { (8, 1), (4, 2), (0x20, 3), (1, 4) }, Devices(early));
            }
        }
    }

    private static uint Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ushort Id(byte[] bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
    private static byte[] Pdu(ushort id)
    {
        byte[] data = [0x72, 0x44, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), id); return data;
    }
    private static (uint, uint)[] Devices(byte[] bytes)
    {
        List<(uint, uint)> devices = [];
        for (int offset = 8; offset < bytes.Length;)
        {
            devices.Add((Read(bytes, offset), Read(bytes, offset + 4)));
            offset += 20 + checked((int)Read(bytes, offset + 16));
        }
        Assert.AreEqual(Read(bytes, 4), (uint)devices.Count);
        return [.. devices];
    }
    private sealed class Device(uint type, ushort capability) : IRdpdrDevice
    {
        public uint DeviceType => type;
        public string Name => "TEST";
        public ushort CapabilityType => capability;
        public uint CapabilityVersion => 1;
        public byte[] DeviceData => [];
        public Task<RdpdrIoResult> ProcessAsync(RdpdrIoRequest request, CancellationToken cancellationToken) => Task.FromResult(new RdpdrIoResult(0, []));
        public void Reset() { }
        public void Dispose() { }
    }
}
