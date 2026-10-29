using System.Buffers.Binary;
using VelaShell.Plugin.Rdp.Engine;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpClipboardProtocolTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("hello\r\nworld")]
    [DataRow("中文 日本語 한글 😀")]
    public void UnicodeTextRoundTrips(string text)
    {
        byte[] packet = RdpClipboardProtocol.CreateTextResponse(text);
        var message = RdpClipboardProtocol.Decode(packet);
        Assert.AreEqual(text, RdpClipboardProtocol.ReadTextResponse(message));
    }

    [TestMethod]
    public void ErrorResponseIsNotAnEmptyClipboard()
    {
        var message = RdpClipboardProtocol.Decode(RdpClipboardProtocol.CreateTextResponse(null));
        Assert.IsNull(RdpClipboardProtocol.ReadTextResponse(message));
    }

    [TestMethod]
    public void CapabilitiesAdvertiseLongNamesWithoutFileTransfer()
    {
        byte[] packet = RdpClipboardProtocol.CreateCapabilities();
        Assert.AreEqual(24, packet.Length);
        Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(20)));
        Assert.IsTrue(RdpClipboardProtocol.ReadLongFormatNames(RdpClipboardProtocol.Decode(packet)));
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void FormatListRoundTrips(bool longNames, bool hasText)
    {
        byte[] packet = RdpClipboardProtocol.CreateFormatList(longNames, hasText);
        Assert.AreEqual(hasText, RdpClipboardProtocol.OffersUnicodeText(RdpClipboardProtocol.Decode(packet), longNames));
    }

    [TestMethod]
    public void RequestUsesUnicodeTextFormat()
    {
        var message = RdpClipboardProtocol.Decode(RdpClipboardProtocol.CreateTextRequest());
        Assert.AreEqual(13u, RdpClipboardProtocol.ReadRequestedFormat(message));
    }

    [TestMethod]
    public void ExactMaximumTextIsAcceptedAndLargerTextRejected()
    {
        string maximum = new('a', RdpClipboardProtocol.MaximumTextBytes / 2 - 1);
        byte[] packet = RdpClipboardProtocol.CreateTextResponse(maximum);
        Assert.AreEqual(RdpClipboardProtocol.MaximumTextBytes + 8, packet.Length);
        Assert.AreEqual(maximum, RdpClipboardProtocol.ReadTextResponse(RdpClipboardProtocol.Decode(packet)));
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.CreateTextResponse(maximum + "a"));
    }

    [TestMethod]
    public void EmbeddedNulAndUnpairedSurrogateAreRejected()
    {
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.CreateTextResponse("a\0b"));
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.CreateTextResponse("\ud800"));
    }

    [TestMethod]
    public void MalformedTextNeverReachesClipboard()
    {
        byte[][] invalid = [[], [0], [65, 0], [65, 0, 0], [0, 216, 0, 0], [0, 0, 65, 0, 0, 0]];
        foreach (byte[] payload in invalid)
        {
            byte[] packet = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatDataResponse, 1, payload);
            Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadTextResponse(RdpClipboardProtocol.Decode(packet)));
        }
    }

    [TestMethod]
    public void HeaderLengthMustExactlyMatchPacket()
    {
        byte[] packet = RdpClipboardProtocol.CreateTextRequest();
        for (int i = 0; i < packet.Length; i++)
        {
            byte[] truncated = packet[..i];
            Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.Decode(truncated));
        }
        byte[] trailing = [.. packet, 0];
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.Decode(trailing));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), uint.MaxValue);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.Decode(packet));
    }

    [TestMethod]
    public void TruncatedFormatNamesAndExcessiveFormatsAreRejected()
    {
        byte[] unterminated = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatList, 0, [13, 0, 0, 0, 65, 0]);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.OffersUnicodeText(RdpClipboardProtocol.Decode(unterminated), true));
        byte[] shortName = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatList, 0, new byte[35]);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.OffersUnicodeText(RdpClipboardProtocol.Decode(shortName), false));
        byte[] tooMany = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatList, 0, new byte[1025 * 6]);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.OffersUnicodeText(RdpClipboardProtocol.Decode(tooMany), true));
    }

    [TestMethod]
    public void UnknownCapabilityIsSkippedButDuplicateGeneralIsRejected()
    {
        byte[] packet = RdpClipboardProtocol.CreateCapabilities();
        byte[] payload = packet[8..];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 2);
        byte[] unknown = [.. payload, 255, 0, 4, 0];
        Assert.IsTrue(RdpClipboardProtocol.ReadLongFormatNames(RdpClipboardProtocol.Decode(
            RdpClipboardProtocol.Encode(RdpClipboardProtocol.Capabilities, 0, unknown))));
        byte[] duplicate = [.. payload, .. payload[4..]];
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadLongFormatNames(RdpClipboardProtocol.Decode(
            RdpClipboardProtocol.Encode(RdpClipboardProtocol.Capabilities, 0, duplicate))));
    }

    [TestMethod]
    public void CapabilityLengthCannotEscapePayloadOrLoopForever()
    {
        foreach (ushort length in new ushort[] { 0, 3, 11, 13, ushort.MaxValue })
        {
            byte[] packet = RdpClipboardProtocol.CreateCapabilities();
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), length);
            Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadLongFormatNames(RdpClipboardProtocol.Decode(packet)));
        }
    }

    [TestMethod]
    public void TextEncodingAllocatesOnlyOnePayloadSizedBuffer()
    {
        string text = new('a', 128 * 1024);
        _ = RdpClipboardProtocol.CreateTextResponse(text);
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] packet = RdpClipboardProtocol.CreateTextResponse(text);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated <= packet.Length + 256, $"Allocated {allocated} bytes for a {packet.Length}-byte packet.");
        Assert.AreEqual(text, RdpClipboardProtocol.ReadTextResponse(RdpClipboardProtocol.Decode(packet)));
    }

    [TestMethod]
    public void DirectMessageParsingStillEnforcesControlPayloadBudget()
    {
        byte[] oversized = new byte[RdpClipboardProtocol.MaximumFormatListBytes + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(oversized, RdpClipboardProtocol.UnicodeText);
        // One long name terminated at the end would otherwise bypass the format-count limit.
        oversized.AsSpan(4).Fill(65);
        oversized[^2] = 0;
        oversized[^1] = 0;
        var formats = new RdpClipboardProtocol.Message(RdpClipboardProtocol.FormatList, 0, oversized);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.OffersUnicodeText(formats, true));
        var capabilities = new RdpClipboardProtocol.Message(RdpClipboardProtocol.Capabilities, 0, oversized);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadLongFormatNames(capabilities));
    }

    [TestMethod]
    public void InvalidFlagsAndErrorPayloadAreRejected()
    {
        byte[] flags = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatDataResponse, 3, [0, 0]);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadTextResponse(RdpClipboardProtocol.Decode(flags)));
        byte[] errorPayload = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatDataResponse, 2, [0, 0]);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadTextResponse(RdpClipboardProtocol.Decode(errorPayload)));
        byte[] request = RdpClipboardProtocol.Encode(RdpClipboardProtocol.FormatDataRequest, 0, new byte[5]);
        Assert.ThrowsExactly<IOException>(() => RdpClipboardProtocol.ReadRequestedFormat(RdpClipboardProtocol.Decode(request)));
    }
}
