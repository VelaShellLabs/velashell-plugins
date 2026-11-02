using VelaShell.Rdp;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpUdpTests
{
    private static (RdpUdpState A, RdpUdpState B) Pair(bool lossy = false, uint a = 100, uint b = 500)
    {
        var left = new RdpUdpState(lossy, a);
        var right = new RdpUdpState(lossy, b);
        left.Receive(right.Syn() with { Ack = a, Flags = right.Syn().Flags | RdpUdpFlags.Ack }, 0);
        right.Receive(left.Syn() with { Ack = b, Flags = left.Syn().Flags | RdpUdpFlags.Ack }, 0);
        return (left, right);
    }
    private static void Deliver(RdpUdpState sender, RdpUdpState receiver, IEnumerable<byte[]> packets, long now)
    {
        foreach (byte[] packet in packets)
        {
            foreach (byte[] ack in receiver.Receive(RdpUdpPacket.Decode(packet), now))
            { sender.Receive(RdpUdpPacket.Decode(ack), now + 1); }
        }
    }

    [TestMethod]
    public void DatagramGoldenBytesAndMalformedFlags()
    {
        byte[] data = [0,0,0,5, 0,16, 0,12, 0,1,1,0, 0,0,0,9, 0,0,0,8, 0xAB];
        RdpUdpPacket packet = RdpUdpPacket.Decode(data);
        Assert.AreEqual(8u, packet.SourceSequence);
        Assert.AreSequenceEqual(data, packet.Encode());
        Assert.ThrowsExactly<RdpProtocolException>(() => RdpUdpPacket.Decode([0,0,0,0,0,0,0,16]));
        Assert.ThrowsExactly<RdpProtocolException>(() => RdpUdpPacket.Decode([0,0,0,0,0,0,0,4,0,1,0x41,0]));
        Assert.ThrowsExactly<RdpProtocolException>(() => RdpUdpPacket.Decode([0,0,0,0,0,0,0,4,0,1,0,0]));
    }

    [TestMethod]
    public void ReliableReordersRetransmitsAndSuppressesDuplicatesAcrossSequenceWrap()
    {
        var (a, b) = Pair(a: uint.MaxValue - 1, b: uint.MaxValue - 1);
        byte[] first = a.Send([1], 1)!.Single();
        byte[] second = a.Send([2], 2)!.Single();
        Deliver(a, b, [second], 20);
        Assert.IsNull(b.Read());
        List<byte[]> retry = a.Tick(600);
        Assert.HasCount(1, retry);
        Assert.AreEqual(uint.MaxValue, RdpUdpPacket.Decode(retry[0]).SourceSequence);
        Deliver(a, b, retry, 620);
        Assert.AreSequenceEqual(new byte[] { 1 }, b.Read()!);
        Assert.AreSequenceEqual(new byte[] { 2 }, b.Read()!);
        Deliver(a, b, [first, second], 630);
        Assert.IsNull(b.Read());
        Assert.AreEqual(0, a.Outstanding);
        var repeat = b.Syn() with { Ack = uint.MaxValue - 1, Flags = RdpUdpFlags.Syn | RdpUdpFlags.Ack };
        a.Receive(repeat, 640);
        Assert.IsTrue(a.Established);
        Assert.ThrowsExactly<RdpProtocolException>(() => a.Receive(repeat with { InitialSequence = 8 }, 641));
    }

    [TestMethod]
    public void LossyFecRecoversMissingPacketBeforeOrderedDelivery()
    {
        var (a, b) = Pair(lossy: true, a: 253);
        b.RequireCompleteDelivery = true;
        List<byte[]> packets = [];
        for (byte i = 1; i <= 4; i++) { packets.AddRange(a.Send([i, 9], i)!); }
        Assert.HasCount(5, packets);
        Deliver(a, b, [packets[3], packets[0], packets[2]], 20);
        Assert.AreSequenceEqual(new byte[] { 1, 9 }, b.Read()!);
        Assert.IsNull(b.Read());
        Deliver(a, b, [packets[4]], 25);
        for (byte i = 2; i <= 4; i++) { Assert.AreSequenceEqual(new byte[] { i, 9 }, b.Read()!); }
        Assert.AreEqual(0, a.Outstanding);
    }

    [TestMethod]
    public void LossySkipsExpiredGapAndDiscardsLatePacket()
    {
        var (a, b) = Pair(lossy: true);
        byte[] first = a.Send([1], 0)!.Single();
        byte[] second = a.Send([2], 1)!.Single();
        Deliver(a, b, [second], 10);
        b.Tick(211);
        Assert.AreSequenceEqual(new byte[] { 2 }, b.Read()!);
        Deliver(a, b, [first], 212);
        Assert.IsNull(b.Read());
        Assert.AreEqual(0, a.Tick(1000).Count(p => RdpUdpPacket.Decode(p).Flags.HasFlag(RdpUdpFlags.Data)));
    }

    [TestMethod]
    public void ApplicationGapCannotSpliceLaterDvcFragmentsIntoIncompleteMessages()
    {
        var (a, b) = Pair(lossy: true);
        b.RequireCompleteDelivery = true;
        a.Send([0x20, 1, 2, 7], 0);
        Deliver(a, b, a.Send([0x30, 1, 8], 1)!, 10);
        Assert.ThrowsExactly<IOException>(() => b.Tick(211));
        Assert.IsNull(b.Read(), "Continuation after a lost DATA_FIRST must never reach the channel parser.");
    }

    [TestMethod]
    public void ReceiveWindowBoundsQueuesAndReopensAfterDrain()
    {
        var (a, b) = Pair();
        for (int i = 0; i < RdpUdpState.Capacity; i++)
        {
            List<byte[]>? packets = a.Send([(byte)i], i * 5);
            Assert.IsNotNull(packets, $"send {i}");
            Deliver(a, b, packets, i * 5 + 1);
        }
        Assert.IsNull(a.Send([9], 1300));
        for (int i = 0; i < RdpUdpState.Capacity; i++) { Assert.AreSequenceEqual(new byte[] { (byte)i }, b.Read()!); }
        foreach (byte[] ack in b.Tick(3000)) { a.Receive(RdpUdpPacket.Decode(ack), 3001); }
        Assert.IsNotNull(a.Send([9], 3002));
    }

    [TestMethod]
    public void FecSolvesTwoMissingSourcesAndRejectsInvalidIndex()
    {
        byte[][] data = [[1, 2], [3], [4, 5, 6], [7, 8]];
        var fec = new RdpUdpFec();
        fec.AddSource(10, data[0]); fec.AddSource(13, data[3]);
        for (byte index = 20; index < 22; index++)
        {
            fec.AddEquation(new(0, 0, RdpUdpFlags.Data | RdpUdpFlags.Fec)
            { SourceSequence = 10, FecRange = 3, FecIndex = index, Payload = RdpUdpFec.Encode(10, data, index) });
        }
        var recovered = fec.Recover(11, 256).ToDictionary(p => p.Sequence, p => p.Payload);
        Assert.AreSequenceEqual(data[1], recovered[11]);
        Assert.AreSequenceEqual(data[2], recovered[12]);
        Assert.ThrowsExactly<ArgumentException>(() => RdpUdpFec.Encode(10, data, 12));
    }

    [TestMethod]
    public void ReliableRetryLimitAndPeerSilenceFailDeterministically()
    {
        var (a, _) = Pair();
        a.Send([1], 0);
        for (int i = 1; i <= 4; i++) { a.Tick(i * 500); }
        Assert.ThrowsExactly<IOException>(() => a.Tick(2500));
        var (b, _) = Pair();
        Assert.ThrowsExactly<IOException>(() => b.Tick(65000));
    }
}
