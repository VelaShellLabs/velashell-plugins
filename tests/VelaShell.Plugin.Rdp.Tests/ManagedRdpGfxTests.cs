using System.Buffers.Binary;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class ManagedRdpGfxTests
{
    [TestMethod]
    public void CapabilityNegotiationOffersAvcAndUnknownVersionsDoNotActivateGraphics()
    {
        using var gfx = new ManagedRdpGfx(2, 1);
        gfx.Open();
        byte[] caps = gfx.ReadResponse()!;
        Assert.AreEqual((ushort)0x12, BinaryPrimitives.ReadUInt16LittleEndian(caps));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(8));
        int offset = 10;
        for (int i = 0; i < count; i++)
        {
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(offset));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(offset + 4));
            Assert.AreEqual(version == 0xa0100 ? 16 : 4, size);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(offset + 8));
            Assert.AreEqual(0u, flags & 0x20);
            if (version == 0x80105) { Assert.AreEqual(0x10u, flags & 0x10); }
            offset += 8 + size;
        }
        Assert.AreEqual(caps.Length, offset);
        gfx.Feed(Packet(Confirm()));
        Assert.IsFalse(gfx.TryGetFrame(out _));
        Assert.IsNull(gfx.ReadResponse(), "Without persistent bitmaps there must be no cache-import request.");
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => gfx.Feed(Packet(Confirm())));
        using var unsupported = new ManagedRdpGfx(2, 1); unsupported.Open();
        unsupported.Feed(Packet(Confirm(0xdeadbeef)));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => unsupported.Feed(Packet(Create(1, 2, 1))));
        using var v101 = new ManagedRdpGfx(2, 1); v101.Open(); _ = v101.ReadResponse();
        v101.Feed(Packet(Command(19, w => { w.Write(0xa0100u); w.Write(16u); w.Write(new byte[16]); }),
            Create(1, 2, 1), Map(1), Start(1), Fill(1, 3, 0, 0, 2, 1), End(1)));
        Assert.IsTrue(v101.TryGetFrame(out RdpGfxFrame frame));
        Assert.AreEqual((byte)3, frame.Pixels[0]);
    }

    [TestMethod]
    public void MultipleFramesShareOutputButMustBeCopiedBeforeAcknowledgement()
    {
        using ManagedRdpGfx gfx = Ready(3, 2);
        gfx.Feed(Packet(Create(1, 3, 2), Map(1), Start(7), Fill(1, 0x00030201, 1, 1, 3, 2), End(7),
            Start(8), Fill(1, 0xff060504, 0, 0, 1, 1), End(8)));
        _ = Assert.ThrowsExactly<RdpProtocolException>(gfx.ReadResponse);
        Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame first));
        byte[] snapshot = [.. first.Pixels];
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 255 }, snapshot.AsSpan(16, 4).ToArray());
        Assert.AreEqual((byte)255, snapshot[3]);
        _ = Assert.ThrowsExactly<RdpProtocolException>(gfx.ReadResponse);
        Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame second));
        Assert.AreSame(first.Pixels, second.Pixels);
        Assert.AreEqual(new RdpFrameRegion(0, 0, 1, 1), new(second.X, second.Y, second.W, second.H));
        Assert.AreSequenceEqual(new byte[] { 4, 5, 6, 255 }, second.Pixels.AsSpan(0, 4).ToArray());
        Assert.AreEqual((byte)0, snapshot[0]);
        Assert.IsFalse(gfx.TryGetFrame(out _));
        AssertAck(gfx.ReadResponse()!, 7, 1); AssertAck(gfx.ReadResponse()!, 8, 2);
        Assert.IsNull(gfx.ReadResponse());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DynamicChannelCopiesEveryManagedFrameBeforeSendingAcknowledgements(bool compressed, bool throwingLog)
    {
        using var stop = new CancellationTokenSource();
        var logs = new List<string>();
        var painted = new List<byte>();
        var acknowledgements = new List<uint>();
        var commands = new List<ushort>();
        var dvc = new DrdynvcChannel((packet, _) =>
        {
            if ((packet[0] >> 4) == 3) { commands.Add(BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2))); }
            if ((packet[0] >> 4) == 3 && BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 13)
            {
                Assert.AreSequenceEqual(new byte[] { 1, 2 }, painted);
                acknowledgements.Add(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(14)));
            }
            return Task.CompletedTask;
        }, stop.Token);
        await using var channel = new RdpGfxChannel(new ManagedRdpGfx(2, 1, message =>
        {
            logs.Add(message);
            if (throwingLog) { throw new InvalidOperationException("diagnostic callback failed"); }
        }), frame => painted.Add(frame.Pixels[0]),
            ex => Assert.Fail(ex.ToString()), stop.Token);
        channel.Register(dvc);
        await dvc.ProcessAsync([0x10, 1, .. Encoding.ASCII.GetBytes(RdpGfxChannel.ChannelName), 0]);
        byte[] update = Packet(Confirm(), Create(1, 2, 1), Map(1), Start(10), Fill(1, 1, 0, 0, 2, 1), End(10),
            Start(11), Fill(1, 2, 0, 0, 2, 1), End(11));
        if (compressed)
        {
            int split = update.Length / 2;
            await dvc.ProcessAsync([0x60, 1, checked((byte)update.Length), 0xe0, 4, .. update.AsSpan(0, split)]);
            await dvc.ProcessAsync([0x70, 1, 0xe0, 4, .. update.AsSpan(split)]);
        }
        else { await dvc.ProcessAsync([0x30, 1, .. update]); }
        Assert.AreSequenceEqual(new ushort[] { 0x12, 13, 13 }, commands,
            "Only capabilities and frame ACKs may be sent; there are no persistent bitmaps to import.");
        Assert.AreSequenceEqual(new uint[] { 10, 11 }, acknowledgements);
        Assert.Contains(message => message.Contains("graphics DVC opened", StringComparison.Ordinal), logs);
        Assert.Contains(message => message.Contains("server confirmed graphics capabilities", StringComparison.Ordinal), logs);
        Assert.ContainsSingle(message => message.Contains("first graphics frame decoded", StringComparison.Ordinal), logs);
        stop.Cancel(); await dvc.CloseAsync();
    }

    [TestMethod]
    public void CompressedGraphicsMessageDecodesAvcIdrAndPredictedFrameBeforeAcknowledgement()
    {
        using ManagedRdpGfx gfx = Ready(16, 16);
        var framebuffer = new Framebuffer(16, 16);
        byte[] Avc(string name) => Command(1, w =>
        {
            byte[] data = GfxAvcTests.Block(GfxAvcTests.Fixture(name));
            w.Write((ushort)1); w.Write((ushort)11); w.Write((byte)0x20);
            Rect(w, 0, 0, 1, 1); w.Write((uint)data.Length); w.Write(data);
        });
        byte[] pdus = [.. Create(1, 16, 16), .. Map(1), .. Start(31), .. Avc("idr"), .. End(31),
            .. Start(32), .. Avc("predicted"), .. End(32)];
        byte[] compressed = new byte[pdus.Length + 7];
        compressed[0] = 0xe0; compressed[1] = 0x24;
        // Distance zero encodes a 15-bit unencoded-run length followed by byte alignment.
        BinaryPrimitives.WriteUInt32BigEndian(compressed.AsSpan(2), 0x88000000u | ((uint)pdus.Length << 7));
        pdus.CopyTo(compressed, 6);
        gfx.Feed(compressed);
        _ = Assert.ThrowsExactly<RdpProtocolException>(gfx.ReadResponse);
        foreach (byte gray in new byte[] { 96, 160 })
        {
            Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame frame));
            Assert.AreEqual(new RdpFrameRegion(0, 0, 16, 16), new(frame.X, frame.Y, frame.W, frame.H));
            framebuffer.Blit(frame);
            byte[] pixels = framebuffer.Snapshot();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.AreSequenceEqual(new byte[] { gray, gray, gray, 255 }, pixels.AsSpan(i, 4).ToArray());
            }
        }
        Assert.IsFalse(gfx.TryGetFrame(out _));
        AssertAck(gfx.ReadResponse()!, 31, 1); AssertAck(gfx.ReadResponse()!, 32, 2);
        Assert.IsNull(gfx.ReadResponse());
    }

    [TestMethod]
    public void FrameSpansMessagesAndPartialDrawingIsNotPresented()
    {
        using ManagedRdpGfx gfx = Ready(2, 1);
        gfx.Feed(Packet(Create(1, 2, 1), Map(1), Start(1), Fill(1, 0xff000009, 0, 0, 2, 1)));
        Assert.IsFalse(gfx.TryGetFrame(out _)); Assert.IsNull(gfx.ReadResponse());
        gfx.Feed(Packet(End(1)));
        Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame frame));
        Assert.AreEqual((byte)9, frame.Pixels[0]);
        Assert.IsFalse(gfx.TryGetFrame(out _)); AssertAck(gfx.ReadResponse()!, 1, 1);
    }

    [TestMethod]
    public void OverlapCopiesUseOriginalSourceAndCacheSurvivesSurfaceDeletion()
    {
        using ManagedRdpGfx gfx = Ready(4, 1);
        gfx.Feed(Packet(Create(1, 4, 1), Map(1), Start(1), Raw(1, 4, 1, [1, 2, 3, 4]),
            Command(6, w => { w.Write((ushort)1); w.Write(99UL); w.Write((ushort)1); Rect(w, 0, 0, 2, 1); }),
            Command(5, w => { w.Write((ushort)1); w.Write((ushort)1); Rect(w, 0, 0, 3, 1); w.Write((ushort)1); Point(w, 1, 0); }), End(1)));
        Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame frame));
        Assert.AreSequenceEqual(new byte[] { 1, 1, 2, 3 }, Blue(frame)); Drain(gfx);
        gfx.Feed(Packet(Command(10, w => w.Write((ushort)1)), Create(2, 4, 1), Map(2), Start(2),
            Command(7, w => { w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)2); Point(w, 0, 0); Point(w, 2, 0); }), End(2)));
        Assert.IsTrue(gfx.TryGetFrame(out frame));
        Assert.AreSequenceEqual(new byte[] { 1, 2, 1, 2 }, Blue(frame)); Drain(gfx);
        gfx.Feed(Packet(Command(8, w => w.Write((ushort)1))));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => gfx.Feed(Packet(Start(3),
            Command(7, w => { w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)1); Point(w, 0, 0); }))));
    }

    [TestMethod]
    public void ScaledMappingClipsToDesktopAndResizePreservesOffscreenPixels()
    {
        using ManagedRdpGfx gfx = Ready(4, 2);
        gfx.Feed(Packet(Create(1, 2, 1), Command(23, static w =>
        {
            w.Write((ushort)1); w.Write((ushort)0); w.Write(1u); w.Write(0u); w.Write(4u); w.Write(2u);
        }), Start(1), Raw(1, 2, 1, [3, 7]), End(1)));
        Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame frame));
        Assert.AreSequenceEqual(new byte[] { 0, 3, 3, 7, 0, 3, 3, 7 }, Blue(frame)); Drain(gfx);
        gfx.Feed(Packet(Reset(5, 2), Start(2), End(2)));
        Assert.IsTrue(gfx.TryGetFrame(out RdpGfxFrame resized)); Assert.AreEqual(5, resized.Width);
        Assert.IsTrue(gfx.TryGetFrame(out frame));
        Assert.AreSequenceEqual(new byte[] { 0, 3, 3, 7, 7, 0, 3, 3, 7, 7 }, Blue(frame)); Drain(gfx);
    }

    [TestMethod]
    public void InvalidLengthsStateReferencesAndBoundsFailAsProtocolErrors()
    {
        byte[][] invalid =
        [
            [1, 0], [9, 0, 0, 0, 7, 0, 0, 0], [9, 0, 0, 0, 0xff, 0xff, 0xff, 0x7f], [9, 0, 1, 0, 8, 0, 0, 0],
            Command(9, w => { w.Write((ushort)1); w.Write((ushort)65535); w.Write((ushort)65535); w.Write((byte)0x20); }),
            Command(9, w => { w.Write((ushort)1); w.Write((ushort)2); w.Write((ushort)1); w.Write((byte)0x22); }),
            [.. Create(1, 2, 1), .. Create(1, 2, 1)],
            Fill(1, 1, 0, 0, 1, 1), End(99), [.. Start(1), .. Start(2)],
            [.. Start(1), .. End(2)], Command(10, w => w.Write((ushort)123)),
            [.. Create(1, 2, 1), .. Command(3, w => { w.Write((ushort)1); w.Write(123u); })],
            [.. Create(1, 2, 1), .. Start(1), .. Fill(1, 1, 0, 0, 3, 1)],
            [.. Create(1, 2, 1), .. Command(4, w => { w.Write((ushort)1); w.Write(0u); w.Write((ushort)65535); })],
            Command(14, w => { w.Write(2u); w.Write(1u); w.Write(17u); w.Write(new byte[320]); }),
            Command(0x7777, _ => { }), Command(17, w => w.Write((ushort)0)), Command(17, w => w.Write((ushort)1))
        ];
        foreach (byte[] pdu in invalid)
        {
            using ManagedRdpGfx gfx = Ready(2, 1);
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => gfx.Feed(Packet(pdu)), Convert.ToHexString(pdu.AsSpan(0, Math.Min(20, pdu.Length))));
        }
        using var premature = new ManagedRdpGfx(2, 1); premature.Open();
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => premature.Feed(Packet(Create(1, 2, 1))));
    }

    [TestMethod]
    public void CacheBudgetAndNegotiatedCodecRestrictionsAreEnforcedBeforeDecode()
    {
        using ManagedRdpGfx gfx = Ready(2, 1);
        gfx.Feed(Packet(Create(1, 2048, 2048), Start(1), Command(6, w =>
        {
            w.Write((ushort)1); w.Write(1UL); w.Write((ushort)1); Rect(w, 0, 0, 2048, 2048);
        })));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => gfx.Feed(Packet(Command(6, w =>
        {
            w.Write((ushort)1); w.Write(2UL); w.Write((ushort)2); Rect(w, 0, 0, 1, 1);
        }))));
        using ManagedRdpGfx disabled = Ready(2, 1, flags: 0x22);
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => disabled.Feed(Packet(Create(1, 2, 1), Start(1),
            Command(1, w => { w.Write((ushort)1); w.Write((ushort)11); w.Write((byte)0x20); Rect(w, 0, 0, 2, 1); w.Write(0u); }))));
    }

    [TestMethod]
    public void DisposalIsIdempotentAndStopsFurtherUse()
    {
        ManagedRdpGfx gfx = Ready(2, 1); gfx.Dispose(); gfx.Dispose();
        _ = Assert.ThrowsExactly<ObjectDisposedException>(gfx.Open);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => gfx.Feed(Packet(Start(1))));
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => gfx.TryGetFrame(out _));
        _ = Assert.ThrowsExactly<ObjectDisposedException>(gfx.ReadResponse);
    }

    private static ManagedRdpGfx Ready(int width, int height, uint flags = 2)
    {
        var gfx = new ManagedRdpGfx(width, height); gfx.Open(); _ = gfx.ReadResponse();
        gfx.Feed(Packet(Confirm(flags: flags))); Drain(gfx); return gfx;
    }
    private static void Drain(ManagedRdpGfx gfx)
    {
        while (gfx.TryGetFrame(out _)) { }
        while (gfx.ReadResponse() is not null) { }
    }
    private static byte[] Blue(RdpGfxFrame frame) => [.. Enumerable.Range(0, frame.Width * frame.Height).Select(i => frame.Pixels[i * 4])];
    private static void AssertAck(byte[] ack, uint id, uint total)
    {
        Assert.AreEqual((ushort)13, BinaryPrimitives.ReadUInt16LittleEndian(ack));
        Assert.AreEqual(id, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(12)));
        Assert.AreEqual(total, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(16)));
    }
    private static byte[] Packet(params byte[][] pdus) => [0xe0, 4, .. pdus.SelectMany(static p => p)];
    private static byte[] Command(ushort id, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(id); writer.Write((ushort)0); writer.Write(0u); write(writer);
        byte[] pdu = stream.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(pdu.AsSpan(4), (uint)pdu.Length); return pdu;
    }
    private static byte[] Confirm(uint version = 0xa0701, uint flags = 2) => Command(19, w => { w.Write(version); w.Write(4u); w.Write(flags); });
    private static byte[] Create(ushort id, ushort width, ushort height) => Command(9, w => { w.Write(id); w.Write(width); w.Write(height); w.Write((byte)0x20); });
    private static byte[] Map(ushort id) => Command(15, w => { w.Write(id); w.Write((ushort)0); w.Write(0u); w.Write(0u); });
    private static byte[] Start(uint id) => Command(11, w => { w.Write(0u); w.Write(id); });
    private static byte[] End(uint id) => Command(12, w => w.Write(id));
    private static byte[] Fill(ushort id, uint color, ushort left, ushort top, ushort right, ushort bottom) => Command(4, w =>
    { w.Write(id); w.Write(color); w.Write((ushort)1); Rect(w, left, top, right, bottom); });
    private static void Rect(BinaryWriter w, ushort left, ushort top, ushort right, ushort bottom)
    { w.Write(left); w.Write(top); w.Write(right); w.Write(bottom); }
    private static void Point(BinaryWriter w, ushort x, ushort y) { w.Write(x); w.Write(y); }
    private static byte[] Raw(ushort id, ushort width, ushort height, byte[] blue) => Command(1, w =>
    {
        w.Write(id); w.Write((ushort)0); w.Write((byte)0x20); Rect(w, 0, 0, width, height); w.Write((uint)blue.Length * 4);
        foreach (byte b in blue) { w.Write(b); w.Write((byte)0); w.Write((byte)0); w.Write((byte)0); }
    });
    private static byte[] Reset(uint width, uint height) => Command(14, w =>
    { w.Write(width); w.Write(height); w.Write(0u); w.Write(new byte[320]); });
}
