using System.Diagnostics;
using System.Formats.Asn1;
using System.Net.Security;
using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Graphics;
using VelaShell.Rdp.Pdu;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Regression tests for the 2026-11-01 security review: a hostile server must not be able to make the client
/// allocate or loop without bound, step outside a mapped folder, or talk it down to weaker authentication.
/// </summary>
[TestClass]
public sealed class RdpHardeningTests
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileOpen = 1, FileCreate = 2;

    private static string NewMapping()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-rdp-hardening-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(root, "share"));
        return root;
    }

    // ---- Drive redirection ----

    [TestMethod]
    public void DriveReadsAreClampedInsteadOfAllocatingWhatTheServerAsks()
    {
        string root = NewMapping();
        try
        {
            string share = Path.Combine(root, "share");
            File.WriteAllBytes(Path.Combine(share, "big.bin"), new byte[3 * 1024 * 1024]);
            var file = DriveFile.Create(share, "\\big.bin", GenericRead, FileOpen, 0, 0);
            byte[] data = file.Read(0, uint.MaxValue);   // used to allocate ~4 GB and spin in ByteWriter growth
            file.Close();
            Assert.HasCount(DriveFile.MaxIo, data);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DataRow("..\\outside.txt")]
    [DataRow("\\a\\..\\..\\outside.txt")]
    [DataRow("/a/../../outside.txt")]
    [DataRow("\\.\\file.txt")]
    [DataRow("\\bad\0name")]
    public void DrivePathsCannotStepOutOfTheMapping(string path)
    {
        DriveStatusException denied = Assert.Throws<DriveStatusException>(() => DriveFile.ResolvePath("C:\\share", path));
        Assert.AreEqual(NtStatus.AccessDenied, denied.Status);
    }

    [TestMethod]
    [DataRow("\\file.txt:hidden")]
    [DataRow("\\dir::$INDEX_ALLOCATION")]
    [DataRow("\\CON")]
    [DataRow("\\nul.txt")]
    [DataRow("\\COM¹")]
    [DataRow("\\CONIN$")]
    [DataRow("\\trailing.")]
    [DataRow("\\trailing ")]
    public void WindowsAliasesAndStreamsAreRefused(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only name rules.");
        }

        _ = Assert.Throws<DriveStatusException>(() => DriveFile.ResolvePath("C:\\share", path));
    }

    [TestMethod]
    public void OrdinaryPathsResolveInsideTheMapping()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "share"));
        Assert.AreEqual(Path.Join(root, "dir", "file.txt"), DriveFile.ResolvePath(root, "\\dir\\file.txt"));
        Assert.AreEqual(Path.Join(root, "dir", "file.txt"), DriveFile.ResolvePath(root, "/dir/file.txt"));   // xrdp
        Assert.AreEqual(root, DriveFile.ResolvePath(root, "\\"));
    }

    [TestMethod]
    public void DriveRefusesToCreateOrRenameThroughALinkInsideTheMapping()
    {
        string root = NewMapping();
        try
        {
            string share = Path.Combine(root, "share"), outside = Path.Combine(root, "outside");
            _ = Directory.CreateDirectory(outside);
            if (!TryCreateDirectoryLink(Path.Combine(share, "link"), outside))
            {
                Assert.Inconclusive("Cannot create a directory link here.");
            }

            DriveStatusException created = Assert.Throws<DriveStatusException>(() => DriveFile.Create(share, "\\link\\planted.txt", GenericWrite, FileCreate, 0, 0));
            Assert.AreEqual(NtStatus.AccessDenied, created.Status);

            var file = DriveFile.Create(share, "\\inside.txt", GenericWrite, FileCreate, 0, 0);
            DriveStatusException renamed = Assert.Throws<DriveStatusException>(() => file.Rename("\\link\\moved.txt"));
            Assert.AreEqual(NtStatus.AccessDenied, renamed.Status);
            file.Close();

            Assert.IsEmpty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void DriveWritesRespectTheOpenModeAndCannotJumpFarPastTheEnd()
    {
        string root = NewMapping();
        try
        {
            string share = Path.Combine(root, "share");
            File.WriteAllText(Path.Combine(share, "a.txt"), "hello");
            var readOnly = DriveFile.Create(share, "\\a.txt", GenericRead, FileOpen, 0, 0);
            _ = readOnly.Read(0, 5);
            DriveStatusException denied = Assert.Throws<DriveStatusException>(() => readOnly.Write(0, "x"u8));
            Assert.AreEqual(NtStatus.AccessDenied, denied.Status);
            readOnly.Close();

            var writable = DriveFile.Create(share, "\\a.txt", GenericWrite, FileOpen, 0, 0);
            DriveStatusException full = Assert.Throws<DriveStatusException>(() => writable.Write((ulong)DriveFile.MaxWriteGap + 1000, "x"u8));
            Assert.AreEqual(NtStatus.DiskFull, full.Status);
            writable.Close();
            Assert.AreEqual(5, new FileInfo(Path.Combine(share, "a.txt")).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            _ = Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }
        }

        // Unprivileged Windows: a junction needs no symlink privilege.
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        _ = (mklink?.WaitForExit(10_000));
        return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
    }

    // ---- Virtual channels ----

    private static RdpConnection NewConnection() =>
        new(new RdpConnectOptions("host", 3389, "user", "pass", "", 1024, 768, 16, null, "host", []));

    private static byte[] Chunk(uint total, uint flags, int length) =>
        [.. BitConverter.GetBytes(total), .. BitConverter.GetBytes(flags), .. new byte[length]];

    [TestMethod]
    public void ChannelReassemblyCannotOutgrowTheDeclaredLength()
    {
        RdpConnection connection = NewConnection();
        var delivered = new List<ChannelPayload>();
        connection.ChannelData += delivered.Add;

        connection.ReassembleChannel(1004, "cliprdr", Chunk(10, 0, 6));        // a continuation without a start: dropped
        connection.ReassembleChannel(1004, "cliprdr", Chunk(10, 1, 6));        // FIRST, 6 of 10
        _ = Assert.Throws<RdpProtocolException>(() => connection.ReassembleChannel(1004, "cliprdr", Chunk(10, 0, 6)));   // 12 > 10
        _ = Assert.Throws<RdpProtocolException>(() => connection.ReassembleChannel(1004, "cliprdr", Chunk(RdpConnection.MaxChannelMessage + 1, 1, 4)));
        Assert.IsEmpty(delivered);

        connection.ReassembleChannel(1004, "cliprdr", Chunk(3, 1 | 2, 3));     // FIRST|LAST
        Assert.HasCount(1, delivered);
        Assert.HasCount(3, delivered[0].Data);
    }

    [TestMethod]
    public async Task DynamicChannelDataIsOnlyBufferedForTheOpenDisplayChannel()
    {
        var sent = new List<byte[]>();
        var dvc = new DrdynvcChannel((pdu, _) => { sent.Add(pdu); return Task.CompletedTask; }, CancellationToken.None);
        await dvc.ProcessAsync([0x50, 0x00, 0x03, 0x00]);
        await dvc.ProcessAsync([((0x01 << 4) | 0x00), 3, .. Encoding.ASCII.GetBytes("Microsoft::Windows::RDS::DisplayControl"), 0x00]);

        // DATA_FIRST (cmd 2, 4-byte length) declaring 2 GB: on an unknown channel and on the display channel.
        byte[] huge = [0x28, 9, 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3];
        await dvc.ProcessAsync(huge);
        huge[1] = 3;
        await dvc.ProcessAsync(huge);

        // Still healthy: the display channel takes its capabilities and a resize goes out.
        await dvc.ProcessAsync([((0x03 << 4) | 0x00), 3, 5, 0, 0, 0, 20, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        await dvc.RequestResizeAsync(1280, 720);
        Assert.AreEqual((byte)((0x03 << 4) | 0x00), sent[^1][0]);
    }

    // ---- NLA ----

    private static RdpClientOptions Options(FakeRdpServer server, RdpSecurity security) => new()
    {
        Host = "127.0.0.1",
        Port = server.Port,
        Username = "vela",
        Password = "not-sent",
        CertificatePin = FakeRdpServer.Pin,
        Security = security
    };

    private static async Task<Exception> ConnectFailureAsync(RdpClientOptions options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await using RdpManagedSession session = await RdpManagedSession.ConnectAsync(options, cts.Token);
        }
        catch (Exception ex) { return ex; }
        throw new AssertFailedException("The connection should have failed.");
    }

    [TestMethod]
    public async Task AnNlaMessageClaimingGigabytesIsRefusedBeforeAllocating()
    {
        await using var server = new FakeRdpServer(async connection =>
        {
            await connection.SelectAsync(FakeRdpConnection.Hybrid);
            await using SslStream ssl = await connection.StartTlsAsync();
            _ = await FakeRdpConnection.ReadDerAsync(ssl);
            await ssl.WriteAsync(new byte[] { 0x30, 0x84, 0x7F, 0xFF, 0xFF, 0xFF });   // SEQUENCE of ~2 GB
            await ssl.FlushAsync();
            await Task.Delay(2000);
        });

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Nla));
        Assert.Contains("too large", failure.ToString());
    }

    [TestMethod]
    public async Task APreCve2018_0886CredSspVersionIsRefusedAndNotRetriedAsTls()
    {
        await using var server = new FakeRdpServer(async connection =>
        {
            await connection.SelectAsync(FakeRdpConnection.Hybrid);
            await using SslStream ssl = await connection.StartTlsAsync();
            _ = await FakeRdpConnection.ReadDerAsync(ssl);
            await ssl.WriteAsync(TsRequest(version: 3, token: [0x4E, 0x54]));
            await ssl.FlushAsync();
            await Task.Delay(2000);
        });

        Exception failure = await ConnectFailureAsync(Options(server, RdpSecurity.Auto));
        Assert.Contains("CVE-2018-0886", failure.ToString());
        Assert.HasCount(1, server.RequestedProtocols, "An old CredSSP version must not trigger the TLS fallback.");
    }

    private static byte[] TsRequest(int version, byte[] token)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                w.WriteInteger(version);
            }
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
            using (w.PushSequence())
            using (w.PushSequence())
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                w.WriteOctetString(token);
            }
        }
        return w.Encode();
    }

    // ---- Bitmaps ----

    [TestMethod]
    public void AnOversizedBitmapRectangleIsRefusedBeforeAnyWork()
    {
        var fb = new Framebuffer(64, 64);
        var watch = Stopwatch.StartNew();
        _ = Assert.Throws<RdpProtocolException>(() => fb.ApplyRect(0, 0, 65534, 1079, 65535, 1080, 32, compressed: true, []));
        Assert.IsLessThan(1000, watch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void AnUpdateDemandingMoreThanAFewDesktopsOfPixelsIsRefused()
    {
        var fb = new Framebuffer(1024, 768);
        var w = new ByteWriter(64);
        w.U16LE(2);                                   // two rectangles
        for (int i = 0; i < 2; i++)
        {
            w.U16LE(0); w.U16LE(0); w.U16LE(1023); w.U16LE(767);
            w.U16LE(2048); w.U16LE(1500);             // 3 M pixels each: the pair is over the 4 M budget
            w.U16LE(16); w.U16LE(0x0001 | 0x0400);    // compressed, no TS_CD_HEADER
            w.U16LE(0);                               // empty stream
        }
        byte[] update = w.ToArray();
        _ = Assert.Throws<RdpProtocolException>(() =>
        {
            var r = new ByteReader(update);
            _ = BitmapUpdate.Apply(ref r, fb);
        });
    }

    [TestMethod]
    public void OnlyTheDestinationRectangleIsPaintedNotTheRowPadding()
    {
        var fb = new Framebuffer(8, 1);
        // 16 bpp, 4 px wide (padded), 1 row, but destination right = left + 1: only 2 columns are real.
        byte[] pixels = [0x00, 0xF8, 0x00, 0xF8, 0xE0, 0x07, 0xE0, 0x07];   // red, red, green, green (RGB565)
        fb.ApplyRect(2, 0, 3, 0, 4, 1, 16, compressed: false, pixels);
        uint Px(int x) => BitConverter.ToUInt32(fb.Bgra, x * 4);
        Assert.AreEqual(0xFFFF0000u, Px(2));
        Assert.AreEqual(0xFFFF0000u, Px(3));
        Assert.AreEqual(0xFF000000u, Px(4), "the padding column must not be painted");
        Assert.AreEqual(0xFF000000u, Px(5));
    }

    [TestMethod]
    public void AShortCompressedStreamPaintsBlackNotStaleMemory()
    {
        var fb = new Framebuffer(4, 2);
        byte[] white = [.. Enumerable.Repeat((byte)0xFF, 16)];
        fb.ApplyRect(0, 0, 3, 1, 4, 2, 16, compressed: false, white);
        fb.ApplyRect(0, 0, 3, 1, 4, 2, 16, compressed: true, []);   // decodes nothing
        Assert.IsTrue(fb.Bgra.Chunk(4).All(p => p is [0, 0, 0, 0xFF]));
    }

    [TestMethod]
    public void CopyToMovesOnlyWhatChanged()
    {
        var fb = new Framebuffer(16, 16);
        byte[] target = new byte[16 * 16 * 4];
        Assert.AreEqual(new RdpFrameRegion(0, 0, 16, 16), fb.CopyTo(target, 64, full: false));   // a new surface is all new
        Assert.IsTrue(fb.CopyTo(target, 64, full: false).IsEmpty);
        fb.Blit(3, 4, 2, 1, new byte[8]);
        fb.Blit(10, 6, 1, 2, new byte[8]);
        Assert.AreEqual(new RdpFrameRegion(3, 4, 8, 4), fb.CopyTo(target, 64, full: false));   // the union
        Assert.AreEqual(new RdpFrameRegion(0, 0, 16, 16), fb.CopyTo(target, 64, full: true));
    }

    // ---- Smaller items ----

    [TestMethod]
    public void TheDesktopSizeAServerCanImposeIsBounded()
    {
        var w = new ByteWriter(64);
        w.U32LE(1); w.U16LE(6); w.U16LE(0); w.Bytes("MSTSC\0"u8); w.U16LE(1); w.U16LE(0);
        w.U16LE(2); w.U16LE(4 + 18); w.U16LE(16); w.U16LE(0); w.U16LE(0); w.U16LE(0);
        w.U16LE(8192); w.U16LE(8192);   // 268 MB of BGRA: over the 8K UHD area
        w.U16LE(0); w.U16LE(0); w.U16LE(0);
        var r = new ByteReader(w.Span);
        Capabilities.DemandActive active = Capabilities.ReadDemandActive(ref r);
        Assert.AreEqual(0, active.Width);
    }

    [TestMethod]
    public void TheCleartextCookieCarriesOnlyPrintableAscii()
    {
        byte[] request = RdpTransport.BuildConnectionRequest("evil\r\nInjected: 1é", SecurityProtocol.Ssl);
        string text = Encoding.Latin1.GetString(request);
        int start = text.IndexOf("mstshash=", StringComparison.Ordinal) + 9;
        Assert.AreEqual("evilInjected: 1", text[start..text.IndexOf('\r', start)]);
    }

    [TestMethod]
    public void OptionsNeverPrintThePassword()
    {
        var options = new RdpClientOptions { Host = "h", Username = "u", Password = "s3cr3t-value" };
        Assert.DoesNotContain("s3cr3t-value", options.ToString());
        Assert.DoesNotContain("s3cr3t-value", new RdpConnectOptions("h", 1, "u", "s3cr3t-value", "", 1, 1, 16, null, "h", []).ToString());
    }

    [TestMethod]
    [DataRow("photo\u202Egpj.exe")]
    [DataRow("CONIN$")]
    [DataRow("dir\\COM¹.txt")]
    public void RemoteClipboardNamesThatDisguiseOrAliasAreRefused(string name) =>
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.ValidateFileName(name));

    [TestMethod]
    public void ALargeRemoteFileListIsCheckedInLinearTime()
    {
        RdpClipboardFile[] files = [.. Enumerable.Range(0, RdpClipboardCodec.MaxFiles).Select(i => new RdpClipboardFile($"d{i % 16}\\sub\\f{i}.txt", false, 1))];
        byte[] encoded = RdpClipboardCodec.EncodeFiles(files);
        var watch = Stopwatch.StartNew();
        Assert.HasCount(files.Length, RdpClipboardCodec.DecodeFiles(encoded));
        Assert.IsLessThan(2000, watch.ElapsedMilliseconds);
    }
}
