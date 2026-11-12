using System.Buffers.Binary;
using VelaShell.Rdp;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class ZgfxTests
{
    [TestMethod]
    public void NormativeLiteralAndOverlappingMatchExample()
    {
        var decoder = new ZgfxDecoder();
        byte[] decoded = decoder.Decode(Compressed("0 01001001 10001 00001 110 001"));
        Assert.AreSequenceEqual(Enumerable.Repeat((byte)0x49, 10).ToArray(), decoded);
        Assert.AreSequenceEqual(new byte[] { 0x49, 0x49, 0x49 }, decoder.Decode(Compressed("10001 00001 0")));
    }

    [TestMethod]
    public void AllShortLiteralTokensAndUnencodedRuns()
    {
        var decoder = new ZgfxDecoder();
        string[] tokens = ["11000", "11001", "110100", "110101", "110110", "1101110", "1101111", "1110000", "1110001",
            "1110010", "1110011", "1110100", "1110101", "1110110", "1110111", "1111000", "1111001", "1111010",
            "1111011", "1111100", "1111101", "11111100", "11111101", "11111110", "11111111"];
        byte[] values = [0, 1, 2, 3, 255, 4, 5, 6, 7, 8, 9, 10, 11, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f, 0x40, 0x80, 0x0c, 0x38, 0x39, 0x66];
        Assert.AreSequenceEqual(values, decoder.Decode(Compressed(string.Concat(tokens))));
        string raw = "1000100000" + Convert.ToString(3, 2).PadLeft(15, '0');
        raw = raw.PadRight((raw.Length + 7) / 8 * 8, '0') + "00010001 00100010 00110011";
        Assert.AreSequenceEqual(new byte[] { 0x11, 0x22, 0x33, 0x11, 0x22, 0x33 },
            decoder.Decode(Compressed(raw + "10001 00011 0")));
    }

    [TestMethod]
    public void TheGenericLiteralMayEncodeBytesThatAlsoHaveShortTokens()
    {
        // 0x00 and 0xFF have short tokens; an encoder may still send them as "0" + 8 bits.
        Assert.AreSequenceEqual(new byte[] { 0x00, 0xFF, 0x41 }, new ZgfxDecoder().Decode(Compressed("0 00000000 0 11111111 0 01000001")));
    }

    [TestMethod]
    public void MultipartConcatenatesSegmentsAndUsesTheirSharedHistory()
    {
        var decoder = new ZgfxDecoder();
        byte[] packet = Multipart(6, [4, 0x49, 0x4a, 0x4b], Compressed("10001 00011 0")[1..]);
        Assert.AreSequenceEqual(new byte[] { 0x49, 0x4a, 0x4b, 0x49, 0x4a, 0x4b }, decoder.Decode(packet));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => new ZgfxDecoder().Decode(Compressed("10001 00001 0")));
    }

    [TestMethod]
    public void ChannelOutputBudgetAppliesToRawCompressedAndMultipartMessages()
    {
        byte[][] packets = [[0xe0, 4, 0x49, 0x4a, 0x4b], Compressed("0 01001001 10001 00001 110 001"),
            Multipart(6, [4, 0x49, 0x4a, 0x4b], Compressed("10001 00011 0")[1..])];
        foreach (byte[] packet in packets)
        {
            byte[] expected = new ZgfxDecoder().Decode(packet);
            Assert.AreSequenceEqual(expected, new ZgfxDecoder().Decode(packet, expected.Length));
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => new ZgfxDecoder().Decode(packet, expected.Length - 1));
        }
    }

    [TestMethod]
    public void CircularHistoryWrapsAndSupportsMaximumDistance()
    {
        var decoder = new ZgfxDecoder();
        int total = 2_500_007;
        int written = 0;
        while (written < total)
        {
            int count = Math.Min(65535, total - written);
            byte[] packet = new byte[count + 2]; packet[0] = 0xe0; packet[1] = 4;
            for (int i = 0; i < count; i++) { packet[i + 2] = (byte)((written + i) % 251); }
            _ = decoder.Decode(packet); written += count;
        }
        string distance = "10111101" + Convert.ToString(2_500_000 - 2_414_240, 2).PadLeft(21, '0');
        Assert.AreSequenceEqual(new byte[] { 7, 8, 9 }, decoder.Decode(Compressed(distance + "0")));
        Assert.AreSequenceEqual(new byte[] { 9, 9, 9 }, decoder.Decode(Compressed("10001 00001 0")));
    }

    [TestMethod]
    public void CompressedExpansionStopsExactlyAtTheSegmentLimit()
    {
        string prefix = "0 01001001 10001 00001 " + new string('1', 14) + "0";
        var decoder = new ZgfxDecoder();
        byte[] maximum = decoder.Decode(Compressed(prefix + Convert.ToString(32766, 2).PadLeft(15, '0')));
        Assert.AreEqual(65535, maximum.Length);
        Assert.IsTrue(maximum.All(b => b == 0x49));
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => new ZgfxDecoder().Decode(Compressed(prefix + new string('1', 15))));
    }

    [TestMethod]
    public void MalformedHeadersLengthsTokensAndExpansionAreRejected()
    {
        byte[][] malformed =
        [
            [], [0xe0], [0xe2, 4], [0xe0, 3, 1], [0xe0, 0x24], [0xe0, 0x24, 8],
            [0xe0, 0x24, 7], Compressed("10000"), Compressed("0"),
            Compressed("1000100000" + new string('1', 15)),
            Multipart(2, [4, 1]), Multipart(0, [4, 1]),
            [0xe1, 1, 0, 0xff, 0xff, 0xff, 0x7f, 1, 0, 0, 0, 4],
            [0xe1, 1, 0, 1, 0, 0, 0, 10, 0, 0, 0, 4],
            [.. Multipart(1, [4, 1]), 0]
        ];
        foreach (byte[] packet in malformed)
        {
            _ = Assert.ThrowsExactly<RdpProtocolException>(() => new ZgfxDecoder().Decode(packet), Convert.ToHexString(packet));
        }
        var decoder = new ZgfxDecoder();
        _ = decoder.Decode([0xe0, 4, 0x49]);
        string tooLong = "10001 00001 " + new string('1', 15) + "0";
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Decode(Compressed(tooLong)));
        string distanceTooFar = "10111101" + Convert.ToString(2_500_001 - 2_414_240, 2).PadLeft(21, '0') + "0";
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Decode(Compressed(distanceTooFar)));
        byte[] rawOverflow = new byte[65538]; rawOverflow[0] = 0xe0; rawOverflow[1] = 4;
        _ = Assert.ThrowsExactly<RdpProtocolException>(() => decoder.Decode(rawOverflow));
    }

    internal static byte[] Compressed(string bits)
    {
        bits = bits.Replace(" ", "", StringComparison.Ordinal);
        int bytes = (bits.Length + 7) / 8;
        byte[] packet = new byte[bytes + 3]; packet[0] = 0xe0; packet[1] = 0x24;
        for (int i = 0; i < bits.Length; i++) { if (bits[i] == '1') { packet[2 + i / 8] |= (byte)(1 << (7 - i % 8)); } }
        packet[^1] = (byte)(bytes * 8 - bits.Length);
        return packet;
    }

    private static byte[] Multipart(uint outputSize, params byte[][] segments)
    {
        byte[] packet = new byte[7 + segments.Sum(s => 4 + s.Length)]; packet[0] = 0xe1;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(1), (ushort)segments.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), outputSize);
        int offset = 7;
        foreach (byte[] segment in segments)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset), (uint)segment.Length);
            segment.CopyTo(packet, offset + 4); offset += 4 + segment.Length;
        }
        return packet;
    }
}
