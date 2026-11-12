using VelaShell.Rdp.Security;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// The TLS record framing seen under SslStream — what tells "the server aborted after our first NLA message"
/// apart from a local decryption problem when SChannel only says SEC_E_INTERNAL_ERROR.
/// </summary>
[TestClass]
public sealed class TlsRecordTapTests
{
    private static byte[] Record(byte type, int length) => [type, 3, 3, (byte)(length >> 8), (byte)length, .. new byte[length]];

    /// <summary>Reads <paramref name="handshake" /> through the tap, marks it complete, then reads the rest in small chunks.</summary>
    private static async Task<TlsRecordTap> FeedAsync(byte[] handshake, byte[] afterHandshake, int chunk)
    {
        var tap = new TlsRecordTap(new MemoryStream([.. handshake, .. afterHandshake]));
        byte[] buffer = new byte[chunk];
        for (int remaining = handshake.Length; remaining > 0;)
        {
            remaining -= await tap.ReadAsync(buffer.AsMemory(0, Math.Min(chunk, remaining)));
        }
        tap.MarkHandshakeComplete();
        while (await tap.ReadAsync(buffer) > 0)
        {
        }
        return tap;
    }

    [TestMethod]
    public async Task RecordsAfterTheHandshakeAreReportedWithTypeAndLength()
    {
        Assert.IsNull(new TlsRecordTap(new MemoryStream()).DescribeSinceHandshake(), "nothing to report before the handshake");
        byte[] handshake = [.. Record(22, 100), .. Record(20, 1), .. Record(22, 40)];
        TlsRecordTap tap = await FeedAsync(handshake, Record(21, 26), chunk: 3);
        Assert.AreEqual("Alert 26 B", tap.DescribeSinceHandshake());
    }

    [TestMethod]
    public async Task AnUnencryptedAlertIsDecoded()
    {
        TlsRecordTap tap = await FeedAsync(Record(22, 10), [0x15, 3, 3, 0, 2, 2, 80], chunk: 1);
        Assert.AreEqual("Alert fatal internal_error(80)", tap.DescribeSinceHandshake());
    }

    [TestMethod]
    public async Task OnlyTheLatestRecordsAreKept()
    {
        byte[] eight = [.. Enumerable.Range(0, 8).SelectMany(static _ => Record(23, 10))];
        TlsRecordTap tap = await FeedAsync([], [.. eight, .. Record(21, 26)], chunk: 7);
        string described = tap.DescribeSinceHandshake()!;
        Assert.StartsWith("… 3 more, ", described);
        Assert.EndsWith("ApplicationData 10 B, Alert 26 B", described);
    }

    [TestMethod]
    public async Task NonTlsBytesAndSilenceAreReportedAsSuch()
    {
        TlsRecordTap garbage = await FeedAsync([], "HTTP/1.1 400"u8.ToArray(), chunk: 64);
        Assert.AreEqual("unparseable TLS framing", garbage.DescribeSinceHandshake());
        TlsRecordTap silent = await FeedAsync(Record(22, 10), [], chunk: 64);
        Assert.AreEqual("nothing", silent.DescribeSinceHandshake());
    }

    [TestMethod]
    public async Task StoppedTapsIgnoreLaterRecords()
    {
        var tap = new TlsRecordTap(new MemoryStream([.. Record(23, 5), .. Record(21, 26)]));
        tap.MarkHandshakeComplete();
        byte[] buffer = new byte[10];
        _ = await tap.ReadAsync(buffer);
        tap.Stop();
        while (await tap.ReadAsync(buffer) > 0)
        {
        }
        Assert.AreEqual("ApplicationData 5 B", tap.DescribeSinceHandshake());
    }
}
