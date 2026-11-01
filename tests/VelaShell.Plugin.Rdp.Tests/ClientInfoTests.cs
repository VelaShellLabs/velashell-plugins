using System.Buffers.Binary;
using VelaShell.Rdp.Pdu;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class ClientInfoTests
{
    // Walks the variable-length TS_INFO_PACKET to the two fields these tests check: the flags word
    // (fixed offset 8) and performanceFlags (after the credential strings and the extended info).
    private static (uint Flags, uint Performance) Parse(byte[] p)
    {
        int o = 8;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o, 4));
        o += 4;
        int cbDomain = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2;
        int cbUser = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2;
        int cbPass = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2;
        int cbShell = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2;
        int cbWork = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2;
        // Each cb* excludes the null terminator, but the string bytes that follow include it.
        o += cbDomain + 2 + cbUser + 2 + cbPass + 2 + cbShell + 2 + cbWork + 2;
        o += 2;                                                             // clientAddressFamily
        int cbAddr = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2 + cbAddr;   // cbClientAddress includes the null
        int cbDir = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(o)); o += 2 + cbDir;
        o += 172;                                                           // clientTimeZone
        o += 4;                                                             // clientSessionId
        return (flags, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(o, 4)));
    }

    [TestMethod]
    public void AudioPlaybackTogglesTheNoAudioFlag()
    {
        (uint on, _) = Parse(ClientInfo.Build("", "u", "p", autoLogon: true, 0, audioPlayback: true));
        (uint off, _) = Parse(ClientInfo.Build("", "u", "p", autoLogon: true, 0, audioPlayback: false));
        Assert.AreEqual(0u, on & 0x00080000u, "Audio on must not set INFO_NOAUDIOPLAYBACK.");
        Assert.AreNotEqual(0u, off & 0x00080000u, "Audio off must set INFO_NOAUDIOPLAYBACK.");
    }

    [TestMethod]
    public void PerformanceFlagsArePassedThrough()
    {
        const uint perf = 0x01 | 0x04 | 0x80;   // wallpaper + animations disabled, font smoothing enabled
        (_, uint written) = Parse(ClientInfo.Build("dom", "user", "secret", autoLogon: true, perf, audioPlayback: false));
        Assert.AreEqual(perf, written);
    }
}
