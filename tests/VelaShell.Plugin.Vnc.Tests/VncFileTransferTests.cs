using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VelaShell.Vnc;
using VelaShell.Vnc.FileTransfer;
using static VelaShell.Plugin.Vnc.Tests.ServerConnection;

namespace VelaShell.Plugin.Vnc.Tests;

/// <summary>UltraVNC file transfer (message type 7) against a scripted server speaking the UltraVNC / LibVNC side.</summary>
[TestClass]
public sealed class VncFileTransferTests
{
    internal readonly record struct Ft(byte Type, byte Param, byte Pad, uint Size, byte[] Body, uint High);

    private static FakeVncServer Server(Func<ServerConnection, Task> body, bool advertise = true, bool supported = true) => new(async (c, index) =>
    {
        await c.VersionAsync();
        await c.OfferAsync(1);
        await c.SecurityResultAsync();
        var (_, encodings) = await c.InitAsync();
        Assert.Contains(VncSession.SupportedMessagesEncoding, encodings);
        if (advertise)
        {
            byte[] messages = new byte[64];
            if (supported) { messages[0] = 0x80; messages[32] = 0x80; }
            await c.WriteAsync(Update(Rect(0, 0, 64, 0, VncSession.SupportedMessagesEncoding, messages)));
        }
        await body(c);
        await c.HoldAsync();
    });

    /// <summary>Connects with file transfer on; LibVNC sends no protocol version, so the grace for it is shortened.</summary>
    private static async Task<VncSession> ConnectAsync(FakeVncServer server, bool waitForVersion = false)
    {
        VncSession session = await VncHandshakeTests.ConnectAsync(VncHandshakeTests.Options(server.Port, fileTransfer: true));
        if (!waitForVersion) { session.Files!.VersionGrace = TimeSpan.FromMilliseconds(10); }
        return session;
    }

    internal static byte[] Message(byte type, byte param, uint size, byte[] body, byte pad = 0) =>
        [7, type, param, pad, .. U32(size), .. U32((uint)body.Length), .. body];

    /// <summary>The next file-transfer message from the client (update requests skipped); an offer's high size word too.</summary>
    internal static async Task<Ft> ReadAsync(ServerConnection c)
    {
        while (true)
        {
            byte type = await c.U8Async();
            if (type == 3) { _ = await c.ReadAsync(9); continue; }
            if (type != 7) { throw new InvalidDataException($"Unexpected client message {type}"); }
            byte[] header = await c.ReadAsync(11);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(3)), length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(7));
            byte[] body = await c.ReadAsync((int)length);
            uint high = header[0] == UltraFileTransfer.FileTransferOffer ? await c.U32Async() : 0;
            return new Ft(header[0], header[1], header[2], size, body, high);
        }
    }

    /// <summary>Answers the access request with "granted" (LibVNC's reply: FileTransferAccess, size 1).</summary>
    private static async Task GrantAsync(ServerConnection c)
    {
        Ft request = await ReadAsync(c);
        Assert.AreEqual(UltraFileTransfer.AbortFileTransfer, request.Type, "access is requested with AbortFileTransfer");
        Assert.AreEqual((byte)2, request.Param, "with a non-zero version (the legacy version 0 is refused by both servers)");
        Assert.IsEmpty(request.Body);
        await c.WriteAsync(Message(UltraFileTransfer.FileTransferAccess, 0, 1, []));
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 200 && !condition(); i++) { await Task.Delay(25); }
        Assert.IsTrue(condition(), what);
    }

    internal static byte[] FindData(string name, bool directory, long size, uint timeLow, uint timeHigh = 0, bool unicode = false)
    {
        byte[] encoded = unicode ? [.. Encoding.Unicode.GetBytes(name), 0, 0] : [.. Encoding.UTF8.GetBytes(name), 0, 0];
        byte[] record = new byte[44 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, directory ? 0x10u : 0x80u);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), timeLow);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), timeHigh);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)(size >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(32), (uint)size);
        encoded.CopyTo(record, 44);
        return record;
    }

    private static byte[] Compress(byte[] data)
    {
        var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) { zlib.Write(data); }
        return output.ToArray();
    }

    [TestMethod]
    public async Task LibVncDriveListAndDirectoryListing()
    {
        Ft drives = default, listing = default;
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            drives = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADrivesList, 0, "C:l\0\0"u8.ToArray()));
            listing = await ReadAsync(c);
            await c.WriteAsync(
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, "C:\\home\\"u8.ToArray()),   // path echo
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, FindData("zeta.txt", false, 5, 1_700_000_000)),
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, FindData("..", true, 0, 0)),
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, FindData("Alpha", true, 0, 1_700_000_000)),
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, FindData("big.iso", false, 5L << 32 | 7, 0)),
                Message(UltraFileTransfer.DirPacket, 0, 0, []));                                                     // end
        });
        await using VncSession session = await ConnectAsync(server);
        IVncFileTransfer files = session.FileTransfer!;
        Assert.IsTrue(await files.RequestAccessAsync());
        IReadOnlyList<VncRemoteDrive> drivesListed = await files.ListDrivesAsync();
        IReadOnlyList<VncRemoteEntry> entries = await files.ListAsync("C:\\home");

        Assert.AreEqual(UltraFileTransfer.DirContentRequest, drives.Type);
        Assert.AreEqual(UltraFileTransfer.RDrivesList, drives.Param);
        Assert.IsEmpty(drives.Body, "LibVNC reads no body for a drive-list request");
        Assert.AreEqual(new VncRemoteDrive("C:\\", 'l'), drivesListed.Single());

        Assert.AreEqual(UltraFileTransfer.RDirContent, listing.Param);
        Assert.AreEqual((byte)0, listing.Pad, "ANSI names until the server says it is version 4");
        Assert.AreEqual("C:\\home\\", Encoding.UTF8.GetString(listing.Body), "a folder is requested with a trailing backslash");
        Assert.AreSequenceEqual(new[] { "Alpha", "big.iso", "zeta.txt" }, entries.Select(static e => e.Name).ToArray(), "folders first, \"..\" left out");
        Assert.IsTrue(entries[0].IsDirectory);
        Assert.AreEqual(5L << 32 | 7, entries[1].Size);
        Assert.IsNull(entries[1].Modified);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), entries[2].Modified, "LibVNC sends Unix seconds in the low word");
    }

    [TestMethod]
    public async Task UltraVncVersionEnablesUnicodeNamesAndItsBlockSize()
    {
        Ft listing = default;
        var fileTime = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        long ticks = fileTime.ToFileTime();
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.ProtocolVersion, 4, 32768, []));
            Ft start = await ReadAsync(c);
            Assert.AreEqual(UltraFileTransfer.SessionStart, start.Type, "a version 3+ server is told a session starts");
            listing = await ReadAsync(c);
            await c.WriteAsync(
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, "D:\\共享\\"u8.ToArray(), pad: 0x80),
                Message(UltraFileTransfer.DirPacket, 2, 0, FindData("报告.docx", false, 1234, (uint)ticks, (uint)(ticks >> 32), unicode: true), pad: 0x80),
                Message(UltraFileTransfer.DirPacket, 0, 0, []));
        });
        await using VncSession session = await ConnectAsync(server, waitForVersion: true);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        IReadOnlyList<VncRemoteEntry> entries = await session.FileTransfer.ListAsync("D:\\共享\\");
        Assert.AreEqual((byte)0x80, listing.Pad, "the Unicode flag (high byte of UltraVNC's 16-bit parameter)");
        Assert.AreEqual("D:\\共享\\", Encoding.UTF8.GetString(listing.Body), "UTF-8 paths");
        Assert.AreEqual(new VncRemoteEntry("报告.docx", false, 1234, fileTime), entries.Single());
        Assert.AreEqual(32768, session.Files!.BlockSize);
    }

    [TestMethod]
    public async Task ARefusedAccessIsFalseAndOperationsAreDenied()
    {
        await using FakeVncServer server = Server(async c =>
        {
            _ = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FileTransferAccess, 0, uint.MaxValue, []));
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsFalse(await session.FileTransfer!.RequestAccessAsync());
        VncFileTransferException denied = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => session.FileTransfer.ListDrivesAsync());
        Assert.AreEqual(VncFileTransferError.Denied, denied.Error);
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    public async Task AServerWithoutFileTransferThatClosesIsReportedAsSuch()
    {
        await using FakeVncServer server = Server(async c =>
        {
            _ = await ReadAsync(c);   // a server that advertised support can still reject access by closing
            c.Client.Close();
        });
        await using VncSession session = await ConnectAsync(server);
        var ended = new TaskCompletionSource<Exception?>();
        session.Disconnected += e => ended.TrySetResult(e);
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => session.FileTransfer!.RequestAccessAsync());
        Assert.AreEqual(VncFileTransferError.Disconnected, error.Error);
        Exception? failure = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<VncFileTransferUnsupportedException>(failure, "not a lost network connection: no automatic reconnect");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task UnconfirmedFileTransferDoesNotSendType7OrDisconnect(bool advertise, bool legacyOverride)
    {
        var keyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeVncServer server = Server(async c =>
        {
            // ExpectAsync rejects any message type 7. A subsequent key proves the desktop connection still works.
            byte[] key = await c.ExpectAsync(4);
            Assert.AreEqual((byte)1, key[0]);
            keyReceived.TrySetResult();
        }, advertise: advertise, supported: false);
        await using VncSession session = await VncHandshakeTests.ConnectAsync(new VncClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            FileTransfer = true,
            AllowUnadvertisedFileTransfer = legacyOverride
        });
        session.FileTransferCapabilityTimeout = TimeSpan.FromMilliseconds(100);
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(
            () => session.FileTransfer!.RequestAccessAsync());
        Assert.AreEqual(VncFileTransferError.Unsupported, error.Error);
        Assert.IsFalse(session.Files!.AccessPending);
        Assert.IsTrue(session.IsConnected);
        await session.KeyAsync('a', 0, true);
        await keyReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    [DataRow(128, 0)]
    [DataRow(0, 128)]
    public async Task BothMessageDirectionsAreRequiredEvenWithLegacyOverride(int clientMask, int serverMask)
    {
        var keyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeVncServer server = Server(async c =>
        {
            byte[] messages = new byte[64];
            messages[0] = (byte)clientMask;
            messages[32] = (byte)serverMask;
            await c.WriteAsync(Update(Rect(0, 0, 64, 0, VncSession.SupportedMessagesEncoding, messages),
                Rect(0, 0, 0, 0, -258)));
            _ = await c.ExpectAsync(4);
            keyReceived.TrySetResult();
        }, advertise: false);
        await using VncSession session = await VncHandshakeTests.ConnectAsync(new VncClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            FileTransfer = true,
            AllowUnadvertisedFileTransfer = true
        });
        await Until(() => session.SupportsScanCodes, "the capability bitmap was processed before the marker rectangle");
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(
            () => session.FileTransfer!.RequestAccessAsync());
        Assert.AreEqual(VncFileTransferError.Unsupported, error.Error);
        await session.KeyAsync('a', 0, true);
        await keyReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    public async Task ANewNegativeCapabilityBlocksFurtherFileMessages()
    {
        var revoke = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            await revoke.Task;
            await c.WriteAsync(Update(Rect(0, 0, 64, 0, VncSession.SupportedMessagesEncoding, new byte[64]),
                Rect(0, 0, 0, 0, -307, Str("files disabled"))));
            _ = await c.ExpectAsync(4);
            keyReceived.TrySetResult();
        });
        await using VncSession session = await ConnectAsync(server);
        bool granted = await session.FileTransfer!.RequestAccessAsync();
        Assert.IsTrue(granted);
        revoke.TrySetResult();
        await Until(() => session.Info.DesktopName == "files disabled", "updated capabilities processed");
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(
            () => session.FileTransfer.ListDrivesAsync());
        Assert.AreEqual(VncFileTransferError.Unsupported, error.Error);
        await session.KeyAsync('a', 0, true);
        await keyReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(session.IsConnected);
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    public async Task ExplicitLegacyOverrideAllowsAnUnadvertisedCompatibleServer()
    {
        await using FakeVncServer server = Server(GrantAsync, advertise: false);
        await using VncSession session = await VncHandshakeTests.ConnectAsync(new VncClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            FileTransfer = true,
            AllowUnadvertisedFileTransfer = true
        });
        session.FileTransferCapabilityTimeout = TimeSpan.FromMilliseconds(100);
        session.Files!.VersionGrace = TimeSpan.FromMilliseconds(10);
        bool granted = await session.FileTransfer!.RequestAccessAsync();
        Assert.IsTrue(granted);
        Assert.IsTrue(session.IsConnected);
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    public async Task CancellingCapabilityWaitSendsNoFileTransferMessage()
    {
        var keyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using FakeVncServer server = Server(async c =>
        {
            _ = await c.ExpectAsync(4);
            keyReceived.TrySetResult();
        }, advertise: false);
        await using VncSession session = await ConnectAsync(server);
        using var cancel = new CancellationTokenSource();
        Task<bool> access = session.FileTransfer!.RequestAccessAsync(cancel.Token);
        cancel.Cancel();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => access);
        Assert.IsFalse(session.Files!.AccessPending);
        await session.KeyAsync('a', 0, true);
        await keyReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsEmpty(server.Failures);
    }

    [TestMethod]
    public async Task DownloadTakesCompressedAndRawPackets()
    {
        byte[] first = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible text ", 400)));
        byte[] second = [.. Enumerable.Range(0, 3000).Select(static i => (byte)(i * 31))];
        Ft request = default, ack = default;
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            request = await ReadAsync(c);
            uint total = (uint)(first.Length + second.Length);
            // The header: low size, "path,date", then (only for an opened file) the high 32 bits.
            await c.WriteAsync(Message(UltraFileTransfer.FileHeader, 0, total, "C:\\f.bin,01/02/2024 10:00"u8.ToArray()), U32(0));
            ack = await ReadAsync(c);
            await c.WriteAsync(
                Message(UltraFileTransfer.FilePacket, 0, 1, Compress(first)),
                Message(UltraFileTransfer.FilePacket, 0, 0, second),
                Message(UltraFileTransfer.EndOfFile, 0, 0, []));
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        var target = new MemoryStream();
        var reported = new List<long>();
        long received = await session.FileTransfer.DownloadAsync("C:\\f.bin", target, new SyncProgress(reported.Add));

        Assert.AreEqual(UltraFileTransfer.FileTransferRequest, request.Type);
        Assert.AreEqual(1u, request.Size, "compressed packets are welcome");
        Assert.AreEqual("C:\\f.bin", Encoding.UTF8.GetString(request.Body));
        Assert.AreEqual(UltraFileTransfer.FileHeader, ack.Type, "the client acknowledges the header to start the transfer");
        Assert.AreNotEqual(uint.MaxValue, ack.Size);
        Assert.IsEmpty(ack.Body);
        byte[] expected = [.. first, .. second];
        byte[] downloaded = target.ToArray();
        Assert.AreSequenceEqual(expected, downloaded);
        Assert.AreEqual(expected.Length, received);
        Assert.AreEqual((long)expected.Length, reported[^1]);
    }

    [TestMethod]
    public async Task AFileTheServerCannotOpenIsUnavailableAndTheSessionGoesOn()
    {
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            _ = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FileHeader, 0, uint.MaxValue, "C:\\missing"u8.ToArray()));   // no high word
            _ = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADrivesList, 0, "C:l\0"u8.ToArray()));
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => session.FileTransfer.DownloadAsync("C:\\missing", new MemoryStream()));
        Assert.AreEqual(VncFileTransferError.Unavailable, error.Error);
        Assert.HasCount(1, await session.FileTransfer.ListDrivesAsync(), "the stream stayed in step");
    }

    [TestMethod]
    public async Task UploadOffersSendsBlocksAndEndsTheFile()
    {
        byte[] content = [.. Enumerable.Range(0, 20000).Select(static i => (byte)(i % 13 == 0 ? i : 'a'))];
        Ft offer = default;
        var packets = new List<Ft>();
        Ft end = default;
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            offer = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FileAcceptHeader, 0, 0, offer.Body));
            while (true)
            {
                Ft next = await ReadAsync(c);
                if (next.Type != UltraFileTransfer.FilePacket) { end = next; break; }
                packets.Add(next);
            }
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        var modified = new DateTimeOffset(2024, 3, 4, 5, 6, 0, TimeSpan.Zero);
        var reported = new List<long>();
        await session.FileTransfer.UploadAsync(new MemoryStream(content), content.Length, modified, "C:\\up\\data.bin", new SyncProgress(reported.Add));
        await Until(() => end.Type != 0, "the end of the file");

        Assert.AreEqual(UltraFileTransfer.FileTransferOffer, offer.Type);
        Assert.AreEqual((uint)content.Length, offer.Size);
        Assert.AreEqual(0u, offer.High, "the high 32 bits follow the name");
        Assert.AreEqual("C:\\up\\data.bin," + modified.LocalDateTime.ToString("MM/dd/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8.GetString(offer.Body));
        Assert.AreEqual(UltraFileTransfer.EndOfFile, end.Type);
        Assert.HasCount(3, packets, "8 KiB blocks (LibVNC inflates each packet into an 8192-byte buffer)");
        var uploaded = new MemoryStream();
        foreach (Ft packet in packets)
        {
            if (packet.Size == 1)
            {
                using var zlib = new ZLibStream(new MemoryStream(packet.Body), CompressionMode.Decompress);
                var block = new MemoryStream();
                zlib.CopyTo(block);
                Assert.IsLessThanOrEqualTo(8192L, block.Length);
                uploaded.Write(block.ToArray());
            }
            else { uploaded.Write(packet.Body); }
        }
        Assert.IsTrue(packets.Any(static p => p.Size == 1), "compressible blocks go compressed");
        byte[] received = uploaded.ToArray();
        Assert.AreSequenceEqual(content, received);
        Assert.AreEqual((long)content.Length, reported[^1]);
    }

    [TestMethod]
    public async Task ARefusedUploadIsUnavailable()
    {
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            Ft offer = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FileChecksums, 0, 0, [1, 2, 3]), Message(UltraFileTransfer.FileAcceptHeader, 0, uint.MaxValue, offer.Body));
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(
            () => session.FileTransfer.UploadAsync(new MemoryStream([1]), 1, DateTimeOffset.Now, "C:\\ro\\x"));
        Assert.AreEqual(VncFileTransferError.Unavailable, error.Error);
    }

    [TestMethod]
    public async Task CancellingADownloadAbortsItOnTheServer()
    {
        Ft abort = default;
        var started = new TaskCompletionSource();
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            _ = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FileHeader, 0, 100000, "C:\\slow,01/01/2024 00:00"u8.ToArray()), U32(0));
            _ = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FilePacket, 0, 0, new byte[100]));
            started.SetResult();
            abort = await ReadAsync(c);
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        using var cancel = new CancellationTokenSource();
        Task download = session.FileTransfer.DownloadAsync("C:\\slow", new MemoryStream(), null, cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        cancel.Cancel();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => download);
        await Until(() => abort.Type != 0, "the abort");
        Assert.AreEqual(UltraFileTransfer.AbortFileTransfer, abort.Type);
        Assert.AreEqual((byte)0, abort.Param, "a plain abort, not an access request");
    }

    [TestMethod]
    public async Task CommandsCreateDeleteAndRename()
    {
        var commands = new List<Ft>();
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            foreach ((byte reply, uint result) in new[] { (UltraFileTransfer.ADirCreate, 0u), (UltraFileTransfer.AFileDelete, 0u), (UltraFileTransfer.AFileRename, 0u), (UltraFileTransfer.AFileDelete, uint.MaxValue) })
            {
                Ft command = await ReadAsync(c);
                commands.Add(command);
                await c.WriteAsync(Message(UltraFileTransfer.CommandReturn, reply, result, command.Body));
            }
        });
        await using VncSession session = await ConnectAsync(server);
        IVncFileTransfer files = session.FileTransfer!;
        Assert.IsTrue(await files.RequestAccessAsync());
        await files.CreateDirectoryAsync("C:\\new");
        await files.DeleteAsync("C:\\old.txt");
        await files.RenameAsync("C:\\a.txt", "C:\\b.txt");
        VncFileTransferException failed = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => files.DeleteAsync("C:\\locked"));
        Assert.AreEqual(VncFileTransferError.Failed, failed.Error);

        Assert.AreSequenceEqual(new[] { UltraFileTransfer.CDirCreate, UltraFileTransfer.CFileDelete, UltraFileTransfer.CFileRename, UltraFileTransfer.CFileDelete }, commands.Select(static c => c.Param).ToArray());
        Assert.IsTrue(commands.All(static c => c.Type == UltraFileTransfer.Command));
        Assert.AreEqual("C:\\a.txt*C:\\b.txt", Encoding.UTF8.GetString(commands[2].Body), "old and new names joined by '*'");
        _ = Assert.ThrowsExactly<ArgumentException>(() => files.RenameAsync("C:\\a*", "C:\\b"));
    }

    [TestMethod]
    public async Task AnUnansweredRequestTimesOut()
    {
        await using FakeVncServer server = Server(GrantAsync);
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        session.Files!.ReplyTimeout = TimeSpan.FromMilliseconds(200);
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => session.FileTransfer.ListDrivesAsync());
        Assert.AreEqual(VncFileTransferError.Timeout, error.Error);
    }

    [TestMethod]
    public async Task AHeaderWhoseLowSizeWordIsAllOnesStillReadsItsHighWord()
    {
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            _ = await ReadAsync(c);
            // A file of 0x1_FFFFFFFF bytes: size -1 like a refusal, but with the timestamp and the high word of an opened file.
            await c.WriteAsync(Message(UltraFileTransfer.FileHeader, 0, uint.MaxValue, "C:\\huge.img,01/02/2024 10:00"u8.ToArray()), U32(1));
            _ = await ReadAsync(c);   // the go-ahead
            await c.WriteAsync(Message(UltraFileTransfer.EndOfFile, 0, 0, []));
            _ = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADrivesList, 0, "C:l\0"u8.ToArray()));
        });
        await using VncSession session = await ConnectAsync(server);
        session.Files!.QuietPeriod = TimeSpan.FromMilliseconds(50);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        VncFileTransferException error = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => session.FileTransfer.DownloadAsync("C:\\huge.img", new MemoryStream()));
        Assert.AreEqual(VncFileTransferError.Failed, error.Error);
        StringAssert.Contains(error.Message, "8589934591", "the 33-bit size was understood");
        Assert.HasCount(1, await session.FileTransfer.ListDrivesAsync(), "the high word was consumed: the stream stayed in step");
        Assert.IsTrue(UltraFileTransfer.HasTimestamp("C:\\a,b\\x,12/31/2023 23:59"u8.ToArray()));
        Assert.IsFalse(UltraFileTransfer.HasTimestamp("C:\\missing,file"u8.ToArray()));
    }

    [TestMethod]
    public async Task AnUnansweredAccessRequestCountsAsRefused()
    {
        await using FakeVncServer server = Server(async c => _ = await ReadAsync(c));
        await using VncSession session = await ConnectAsync(server);
        session.Files!.AccessTimeout = TimeSpan.FromMilliseconds(200);
        Assert.IsFalse(await session.FileTransfer!.RequestAccessAsync(), "UltraVNC does not answer a view-only session");
        Assert.IsTrue(session.IsConnected);
    }

    [TestMethod]
    public async Task LateRepliesOfATimedOutListingDoNotReachTheNextOperation()
    {
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            _ = await ReadAsync(c);
            await Task.Delay(400);   // past the client's 200 ms timeout
            await c.WriteAsync(
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, "C:\\slow\\"u8.ToArray()),
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, FindData("stale.txt", false, 1, 0)),
                Message(UltraFileTransfer.DirPacket, 0, 0, []));
            _ = await ReadAsync(c);
            await c.WriteAsync(
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, "C:\\fresh\\"u8.ToArray()),
                Message(UltraFileTransfer.DirPacket, UltraFileTransfer.ADirectory, 0, FindData("fresh.txt", false, 1, 0)),
                Message(UltraFileTransfer.DirPacket, 0, 0, []));
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        session.Files!.ReplyTimeout = TimeSpan.FromMilliseconds(200);
        VncFileTransferException timeout = await Assert.ThrowsExactlyAsync<VncFileTransferException>(() => session.FileTransfer.ListAsync("C:\\slow\\"));
        Assert.AreEqual(VncFileTransferError.Timeout, timeout.Error);
        session.Files.ReplyTimeout = TimeSpan.FromSeconds(10);
        IReadOnlyList<VncRemoteEntry> entries = await session.FileTransfer.ListAsync("C:\\fresh\\");
        Assert.AreEqual("fresh.txt", entries.Single().Name);
    }

    [TestMethod]
    public async Task ALocalFileShorterThanOfferedIsAbortedNotPadded()
    {
        Ft after = default;
        await using FakeVncServer server = Server(async c =>
        {
            await GrantAsync(c);
            Ft offer = await ReadAsync(c);
            await c.WriteAsync(Message(UltraFileTransfer.FileAcceptHeader, 0, 0, offer.Body));
            after = await ReadAsync(c);
        });
        await using VncSession session = await ConnectAsync(server);
        Assert.IsTrue(await session.FileTransfer!.RequestAccessAsync());
        _ = await Assert.ThrowsExactlyAsync<IOException>(() => session.FileTransfer.UploadAsync(new MemoryStream([1, 2, 3, 4]), 10, DateTimeOffset.Now, "C:\\x"));
        await Until(() => after.Type != 0, "the next message");
        Assert.AreEqual(UltraFileTransfer.AbortFileTransfer, after.Type, "nothing was sent for the short file, and it was aborted");
    }

    [TestMethod]
    public void FindDataRecordsAndDriveListsParse()
    {
        Assert.IsNull(UltraFileTransfer.ParseEntry(new byte[44], unicode: false), "too short for a name");
        VncRemoteEntry? latin = UltraFileTransfer.ParseEntry([.. FindData("x", false, 1, 0)[..44], .. "caf"u8.ToArray(), (byte)0xE9], unicode: false);
        Assert.AreEqual("café", latin?.Name, "a name that is not UTF-8 is read as Latin-1, without a terminator at the end of the record");
        _ = Assert.ThrowsExactly<VncProtocolException>(() => UltraFileTransfer.ParseDrives("C-l\0"u8.ToArray()));
        Assert.HasCount(2, UltraFileTransfer.ParseDrives("C:l\0D:c\0"u8.ToArray()));
        Assert.IsEmpty(UltraFileTransfer.ParseDrives([]));
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to the thread pool and race the assertions).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
