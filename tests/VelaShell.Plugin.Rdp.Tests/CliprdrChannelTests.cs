using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class CliprdrChannelTests
{
    [TestMethod]
    public async Task MonitorReadyCompletesHandshakeWithoutAWorkspaceSubscriber()
    {
        List<byte[]> sent = [];
        var channel = new CliprdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; }, ev =>
        {
            Assert.AreEqual(2u, ev.Kind);
            Assert.HasCount(2, sent);
        });
        await channel.ProcessAsync(Packet(1));
        Assert.AreEqual((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(sent[0]));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(sent[0].AsSpan(20));
        Assert.AreEqual(0x10u, flags & 0x10, "Versioned file requests require negotiated clipboard locks.");
        Assert.AreNotEqual(0u, flags & 0x20, "Huge-file support uses bit 0x20.");
        Assert.AreSequenceEqual(Packet(2), sent[1]);
    }

    [TestMethod]
    public async Task VersionLocksAndFileRequestsRoundTripOnlyWhenNegotiated()
    {
        List<byte[]> sent = [];
        List<RdpClipboardEvent> received = [];
        var channel = new CliprdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; }, received.Add);
        Assert.IsFalse(channel.SupportsClipboardLock);
        Assert.Throws<InvalidOperationException>(() => channel.LockClipboardAsync(42, true, default));
        await channel.ProcessAsync(Capabilities(0x16));
        Assert.IsTrue(channel.SupportsClipboardLock);
        await channel.LockClipboardAsync(42, true, default);
        Assert.AreSequenceEqual(Packet(10, 0, BitConverter.GetBytes(42u)), sent[0]);
        await channel.ProcessAsync(sent[0]);
        Assert.AreEqual(11u, received[0].Kind);
        Assert.AreEqual(42u, received[0].A);
        await channel.RequestFileAsync(7, 2, 2, 11, 8, 42);
        Assert.AreEqual(28u, BinaryPrimitives.ReadUInt32LittleEndian(sent[1].AsSpan(4)));
        await channel.ProcessAsync(sent[1]);
        Assert.HasCount(20, received[1].Data);
        Assert.AreEqual(42u, BinaryPrimitives.ReadUInt32LittleEndian(received[1].Data.AsSpan(16)));
        await channel.LockClipboardAsync(42, false, default);
        await channel.ProcessAsync(sent[2]);
        Assert.AreEqual(12u, received[2].Kind);
        Assert.AreEqual(42u, received[2].A);
    }

    [TestMethod]
    public async Task ZeroLockIdIsPresentAndDistinctFromUnversionedRequests()
    {
        List<byte[]> sent = [];
        List<RdpClipboardEvent> received = [];
        var channel = new CliprdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; }, received.Add);
        await channel.ProcessAsync(Capabilities(0x16));
        await channel.LockClipboardAsync(0, true, default);
        await channel.RequestFileAsync(1, 0, 2, 209715200, 262144, 0);
        await channel.RequestFileAsync(2, 0, 2, 209715200, 262144);
        Assert.AreEqual(28u, BinaryPrimitives.ReadUInt32LittleEndian(sent[1].AsSpan(4)));
        Assert.AreEqual(24u, BinaryPrimitives.ReadUInt32LittleEndian(sent[2].AsSpan(4)));
        await channel.ProcessAsync(sent[1]);
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(received[0].Data.AsSpan(16)));
        Assert.AreEqual(209715200UL, BinaryPrimitives.ReadUInt64LittleEndian(received[0].Data.AsSpan(4)));
    }

    [TestMethod]
    [DataRow((ushort)1, true)]
    [DataRow((ushort)2, false)]
    [DataRow((ushort)3, false)]
    public async Task FileResponsesPreserveFlagsAndMatchStreamInsteadOfTreatingFailureAsPacketLoss(ushort flags, bool success)
    {
        RdpClipboardEvent? result = null;
        var channel = new CliprdrChannel(_ => Task.CompletedTask, ev => result = ev);
        await channel.ProcessAsync(Packet(9, flags, [17, 0, 0, 0, 23]));
        Assert.AreEqual(17u, result!.A);
        Assert.AreEqual(flags, result.ResponseFlags);
        Assert.AreEqual(success ? 1u : 0u, result.B);
        Assert.HasCount(success ? 1 : 0, result.Data);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task NegotiatedFormatNamesSurviveAsynchronousAcknowledgement(bool longNames, bool ascii)
    {
        List<RdpClipboardEvent> events = [];
        var channel = new CliprdrChannel(async _ => await Task.Yield(), events.Add);
        if (longNames) { await channel.ProcessAsync(Capabilities(2)); }
        byte[] name = (ascii ? Encoding.ASCII : Encoding.Unicode).GetBytes("PNG\0");
        byte[] body = new byte[longNames ? 4 + name.Length : 36];
        BinaryPrimitives.WriteUInt32LittleEndian(body, 49170);
        name.CopyTo(body, 4);
        await channel.ProcessAsync(Packet(2, ascii ? (ushort)4 : (ushort)0, body));
        Assert.HasCount(1, events);
        Assert.AreEqual(3u, events[0].Kind);
        Assert.AreEqual("49170\tPNG\n", Encoding.UTF8.GetString(events[0].Data));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublicationWaitsForItsOwnAcknowledgement(bool accepted)
    {
        List<byte[]> sent = [];
        var channel = new CliprdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; }, _ => { });
        await channel.ProcessAsync(Capabilities(2));
        await channel.ProcessAsync(Packet(1));
        Task offer = channel.OfferFormatsAsync("13\t\n");
        Assert.IsFalse(offer.IsCompleted);
        await channel.ProcessAsync(Packet(3, 1)); // Initial empty list acknowledgement.
        Assert.IsFalse(offer.IsCompleted);
        await channel.ProcessAsync(Packet(3, accepted ? (ushort)1 : (ushort)2));
        if (accepted) { await offer; }
        else { await Assert.ThrowsAsync<IOException>(() => offer); }
        Assert.AreEqual(6u, BinaryPrimitives.ReadUInt32LittleEndian(sent[^1].AsSpan(4)));
    }

    [TestMethod]
    public async Task NoCapabilitiesUsesShortNamesAndDoesNotOfferFileStreaming()
    {
        List<byte[]> sent = [];
        var channel = new CliprdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; }, _ => { });
        Task offer = channel.OfferFormatsAsync("13\t\n49170\tPNG\n49160\tFileGroupDescriptorW\n49161\tFileContents\n");
        byte[] body = sent.Single()[8..];
        Assert.HasCount(72, body);
        Assert.AreEqual(49170u, BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(36)));
        Assert.AreEqual((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(sent[0].AsSpan(2)));
        Assert.AreEqual("PNG", Encoding.ASCII.GetString(body, 40, 32).TrimEnd('\0'));
        await channel.ProcessAsync(Packet(3, 1));
        await offer;
    }

    [TestMethod]
    public async Task CompressedVirtualChannelAcknowledgementCompletesPublication()
    {
        await using var connection = CreateConnection();
        var channel = new CliprdrChannel(_ => Task.CompletedTask, _ => { });
        Task? processing = null;
        connection.ChannelData += value => processing = channel.ProcessAsync(value.Data);
        Task offer = channel.OfferFormatsAsync("13\t\n");
        // RDP5 MPPC: four literals followed by offset=1, length=4 (the trailing zeroes).
        Deliver(connection, 8, 0x00A10013, [3, 0, 1, 0, 0xF8, 0x30]);
        Assert.IsNotNull(processing);
        await processing;
        await offer.WaitAsync(TimeSpan.FromSeconds(1));
        Task nextOffer = channel.OfferFormatsAsync("13\t\n");
        // The next compressed ACK refers to all eight bytes in the existing bulk history.
        Deliver(connection, 8, 0x00210013, [0xF9, 0x18, 0]);
        await processing;
        await nextOffer.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task DeclaredClipboardPayloadCannotBeSilentlyTruncated()
    {
        var channel = new CliprdrChannel(_ => Task.CompletedTask, _ => { });
        byte[] packet = Packet(2);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 6);
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ProcessAsync(packet));
    }

    [TestMethod]
    public async Task ClipboardChunksCarryShowProtocolIncludingContinuationChunks()
    {
        await using var connection = CreateConnection();
        using var wire = new MemoryStream();
        var transport = new VelaShell.Rdp.Wire.RdpTransport(wire);
        var mcs = new VelaShell.Rdp.Mcs.McsLayer(transport);
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("UserId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(mcs, (ushort)1001);
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("Channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(mcs, new Dictionary<string, ushort> { ["cliprdr"] = 1004 });
        typeof(RdpConnection).GetField("_mcs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(connection, mcs);
        await connection.SendVirtualChannelAsync("cliprdr", new byte[1700], default);
        wire.Position = 0;
        for (int index = 0; index < 2; index++)
        {
            var frame = await transport.ReadPduAsync(default);
            frame.Data[3] = 0x68; // Decode the client request with the symmetric indication parser.
            var message = VelaShell.Rdp.Mcs.McsLayer.ParseSendDataIndication(frame.Data)!.Value;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(message.Payload.AsSpan(4));
            Assert.AreEqual(index == 0 ? 0x11u : 0x12u, flags);
            Assert.AreEqual(1700u, BinaryPrimitives.ReadUInt32LittleEndian(message.Payload));
        }
    }

    [TestMethod]
    public async Task LargeClipboardMessagesAllowInputBetweenFragmentsWithoutInterleavingMessages()
    {
        await using var connection = CreateConnection();
        using var wire = new PausingWire();
        var transport = new VelaShell.Rdp.Wire.RdpTransport(wire);
        var mcs = new VelaShell.Rdp.Mcs.McsLayer(transport);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("UserId", flags)!.SetValue(mcs, (ushort)1001);
        typeof(VelaShell.Rdp.Mcs.McsLayer).GetProperty("Channels", flags)!.SetValue(mcs, new Dictionary<string, ushort> { ["cliprdr"] = 1004 });
        typeof(RdpConnection).GetField("_mcs", flags)!.SetValue(connection, mcs);
        typeof(RdpConnection).GetField("_transport", flags)!.SetValue(connection, transport);
        using var cancel = new CancellationTokenSource();
        Task first = connection.SendVirtualChannelAsync("cliprdr", new byte[256 * 1024], cancel.Token);
        await wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task second = connection.SendVirtualChannelAsync("cliprdr", new byte[1], default);
        Task input = connection.SendScancodeAsync(0x1e, true, default);
        cancel.Cancel();
        wire.Release.SetResult();
        await Task.WhenAll(first, second, input).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreNotEqual((byte)3, wire.Frames[1][0], "Keyboard input must precede the second clipboard fragment.");
        byte[][] clipboard = [.. wire.Frames.Where(frame => frame[0] == 3)];
        for (int index = 0; index < clipboard.Length; index++)
        {
            using var source = new MemoryStream(clipboard[index]);
            var packet = await new VelaShell.Rdp.Wire.RdpTransport(source).ReadPduAsync(default);
            packet.Data[3] = 0x68;
            byte[] payload = VelaShell.Rdp.Mcs.McsLayer.ParseSendDataIndication(packet.Data)!.Value.Payload;
            Assert.AreEqual(index == clipboard.Length - 1 ? 1u : 256u * 1024, BinaryPrimitives.ReadUInt32LittleEndian(payload));
            uint channelFlags = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
            Assert.AreEqual(index == clipboard.Length - 1 ? 0x13u : index == 0 ? 0x11u : index == clipboard.Length - 2 ? 0x12u : 0x10u, channelFlags);
        }
    }

    [TestMethod]
    public async Task ClipboardQueueSaturationFailsExplicitlyWithoutWaitingForItsReader()
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var connection = CreateConnection();
        await using var session = (RdpManagedSession)Activator.CreateInstance(typeof(RdpManagedSession), flags, null, [connection], null)!;
        var queue = System.Threading.Channels.Channel.CreateBounded<byte[]>(1);
        typeof(RdpManagedSession).GetField("_cliprdrQueue", flags)!.SetValue(session, queue);
        var failed = new TaskCompletionSource<RdpClipboardEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ClipboardEvent += value => { if (value.Kind == 10) { failed.TrySetResult(value); } };
        Deliver(connection, 8, 3, Packet(3, 1));
        Deliver(connection, 8, 3, Packet(3, 1));
        var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("queue capacity", Encoding.UTF8.GetString(failure.Data));
        RdpClipboardEvent? replayed = null;
        session.ClipboardEvent += value => replayed = value;
        Assert.AreEqual(10u, replayed!.Kind);
        await Assert.ThrowsAsync<IOException>(() => session.OfferFormatsAsync("13\t\n", default));
    }

    private sealed class PausingWire : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<byte[]> Frames = [];
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Frames.Add(buffer.ToArray());
            if (Frames.Count == 1)
            {
                Started.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FileFormatNamesAndRequestsRoundTrip(bool longNames)
    {
        List<byte[]> sent = [];
        List<RdpClipboardEvent> events = [];
        var channel = new CliprdrChannel(pdu => { sent.Add(pdu); return Task.CompletedTask; }, events.Add);
        await channel.ProcessAsync(Capabilities(longNames ? 6u : 4u));
        Task offer = channel.OfferFormatsAsync("49160\tFileGroupDescriptorW\n49161\tFileContents\n");
        await channel.ProcessAsync(sent[0]);
        Assert.AreEqual("49160\tFileGroupDescriptorW\n49161\tFileContents\n", Encoding.UTF8.GetString(events[0].Data));
        await channel.ProcessAsync(sent[1]);
        await offer;
        await channel.RequestFileAsync(17, 2, 2, 0x100000003, 1024);
        await channel.ProcessAsync(sent[^1]);
        var request = events[^1];
        Assert.AreEqual(6u, request.Kind);
        Assert.AreEqual(17u, request.A);
        Assert.AreEqual(2u, request.B);
        Assert.AreEqual(0x100000003ul, BinaryPrimitives.ReadUInt64LittleEndian(request.Data.AsSpan(4)));
        await channel.RespondFileAsync(17, [0xFF, 0x80, 42], true);
        await channel.ProcessAsync(sent[^1]);
        Assert.AreEqual(7u, events[^1].Kind);
        Assert.AreEqual(17u, events[^1].A);
        Assert.AreSequenceEqual(new byte[] { 0xFF, 0x80, 42 }, events[^1].Data);
    }

    [TestMethod]
    public async Task CompressedFragmentedFileDescriptorReachesClipboardIntact()
    {
        await using var connection = CreateConnection();
        List<RdpClipboardEvent> events = [];
        var channel = new CliprdrChannel(_ => Task.CompletedTask, events.Add);
        Task? processing = null;
        connection.ChannelData += value => processing = channel.ProcessAsync(value.Data);
        await channel.RequestFormatAsync(49160);
        byte[] descriptors = RdpClipboardCodec.EncodeFiles([new("目录\\文件.bin", false, 3)]);
        byte[] packet = Packet(5, 1, descriptors);
        Deliver(connection, (uint)packet.Length, 0x00A10011, CompressLiterals(packet[..100]));
        Assert.IsNull(processing);
        Deliver(connection, (uint)packet.Length, 0x00210012, CompressLiterals(packet[100..]));
        Assert.IsNotNull(processing);
        await processing;
        Assert.AreEqual(49160u, events.Single().B);
        Assert.AreSequenceEqual(descriptors, events[0].Data);
        Assert.AreEqual("目录\\文件.bin", RdpClipboardCodec.DecodeFiles(events[0].Data).Single().Name);
    }

    private static byte[] CompressLiterals(byte[] data)
    {
        string bits = string.Concat(data.Select(value => value < 128
            ? Convert.ToString(value, 2).PadLeft(8, '0')
            : "10" + Convert.ToString(value - 128, 2).PadLeft(7, '0')));
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        return [.. Enumerable.Range(0, bits.Length / 8).Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2))];
    }

    private static RdpConnection CreateConnection() => new(new RdpConnectOptions(
        "localhost", 3389, "", "", "", 800, 600, 32, null, "localhost",
        [new VelaShell.Rdp.Mcs.ChannelDef("cliprdr", 0xC0A00000)]));

    private static void Deliver(RdpConnection connection, uint length, uint flags, byte[] data)
    {
        byte[] packet = new byte[data.Length + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), flags);
        data.CopyTo(packet, 8);
        typeof(RdpConnection).GetMethod("ReassembleChannel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(connection, [(ushort)1004, "cliprdr", packet]);
    }

    private static byte[] Capabilities(uint flags)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), flags);
        return Packet(7, 0, body);
    }

    private static byte[] Packet(ushort type, ushort flags = 0, byte[]? body = null)
    {
        body ??= [];
        byte[] packet = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, type);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)body.Length);
        body.CopyTo(packet, 8);
        return packet;
    }
}
