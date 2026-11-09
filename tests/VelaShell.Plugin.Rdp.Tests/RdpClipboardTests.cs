using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using VelaShell.PluginSdk.Testing;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class RdpClipboardTests
{
    [TestMethod]
    public void UnicodeRoundTripAndMalformedPayloads()
    {
        byte[] text = RdpClipboardCodec.EncodeText("中文🙂\nnext");
        Assert.AreEqual("中文🙂\r\nnext", RdpClipboardCodec.DecodeText(text));
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeText([1]));
        _ = Assert.Throws<DecoderFallbackException>(() => RdpClipboardCodec.DecodeText([0, 0xD8]));
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.EncodeText("a\0b"));
    }

    [TestMethod]
    public void FileDescriptorsRoundTripDirectoriesAnd64BitLengths()
    {
        RdpClipboardFile[] files = [new("目录", true, 0), new("目录\\文件.txt", false, 0x100000005)];
        byte[] data = RdpClipboardCodec.EncodeFiles(files);
        IReadOnlyList<RdpClipboardFile> decoded = RdpClipboardCodec.DecodeFiles(data);
        Assert.AreSequenceEqual(files, [.. decoded]);
        Assert.HasCount(4 + 592 * 2, data);
        // Every descriptor asks the consuming Shell for its own copy-progress dialog (FD_SHOWPROGRESSUI),
        // so a large paste shows Windows' progress instead of a seemingly hung Explorer.
        Assert.AreEqual(0x4004u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        Assert.AreEqual(0x4044u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 592)));
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(data[..^1]));
        BinaryPrimitives.WriteUInt32LittleEndian(data, uint.MaxValue);
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(data));
    }

    [TestMethod]
    [DataRow("..\\escape")]
    [DataRow("C:\\secret")]
    [DataRow("\\server\\share")]
    [DataRow("a/b")]
    [DataRow("file:stream")]
    [DataRow("NUL.txt")]
    [DataRow("a\\CON")]
    [DataRow("a.\\x")]
    [DataRow("a\\\\x")]
    [DataRow("a\0b")]
    public void RejectsUnsafeRemoteNames(string name) => Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.ValidateFileName(name));

    [TestMethod]
    public void RejectsCaseCollisionsAndFileAsParent()
    {
        byte[] duplicates = RdpClipboardCodec.EncodeFiles([new("File", false, 0), new("file", false, 0)]);
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(duplicates));
        byte[] conflict = RdpClipboardCodec.EncodeFiles([new("a", false, 0), new("a\\b", false, 0)]);
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(conflict));
    }

    [TestMethod]
    public void DibRoundTripHandlesPaddingAndVerticalOrder()
    {
        byte[] pixels = [1, 2, 3, 255, 4, 5, 6, 255];
        byte[] encoded = RdpClipboardCodec.EncodeDib(new(1, 2, pixels));
        RdpClipboardImage decoded = RdpClipboardCodec.DecodeDib(encoded);
        Assert.AreEqual(1, decoded.Width);
        Assert.AreEqual(2, decoded.Height);
        Assert.AreSequenceEqual(pixels, decoded.Bgra);
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeDib(encoded[..^1]));
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(4), int.MaxValue);
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeDib(encoded));
    }

    [TestMethod]
    public void DibSupportsTopDown32BitAndRejectsUnknownMasks()
    {
        byte[] data = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 32);
        data[40] = 9; data[41] = 8; data[42] = 7;
        Assert.AreSequenceEqual(new byte[] { 9, 8, 7, 255 }, RdpClipboardCodec.DecodeDib(data).Bgra);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 3);
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeDib(data));
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.ValidateImageSize(8192, 8192));
        _ = Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.ValidatePng(new byte[33]));
    }

    [TestMethod]
    public async Task LocalSnapshotUsesChunksAndRejectsInvalidRanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-rdp-test-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.Combine(root, "child"));
        byte[] bytes = [.. Enumerable.Range(0, RdpClipboardFiles.ChunkSize + 11).Select(i => (byte)i)];
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "child", "data.bin"), bytes);
            using var snapshot = RdpClipboardFiles.Open([root], default);
            int index = snapshot.Entries.FindIndex(e => !e.IsDirectory);
            byte[] first = await snapshot.ReadAsync((uint)index, 0, RdpClipboardFiles.ChunkSize, default);
            byte[] last = await snapshot.ReadAsync((uint)index, (ulong)first.Length, 100, default);
            Assert.AreSequenceEqual(bytes, [.. first, .. last]);
            _ = await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ReadAsync((uint)index, (ulong)bytes.Length + 1, 1, default));
            _ = await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ReadAsync(0, 0, 1, default));
            _ = await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ReadAsync((uint)index, 0, RdpClipboardFiles.ChunkSize + 1, default));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TextClipboardIsBidirectionalAndSuppressesEcho() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        await window.Clipboard!.SetTextAsync("local 中文");
        var transport = new ClipboardTransport();
        await using var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(4, RdpClipboardCodec.UnicodeText);
        await Until(() => transport.Responses.Count > 0);
        Assert.AreEqual("local 中文", RdpClipboardCodec.DecodeText(transport.Responses[0].Data));
        transport.RemoteText = "remote 🙂";
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await UntilAsync(async () => await window.Clipboard.TryGetTextAsync() == transport.RemoteText);
        int offers = transport.Offers.Count;
        await bridge.PublishLocalAsync();
        Assert.HasCount(offers, transport.Offers);
        await window.Clipboard.SetTextAsync("local 中文");
        await bridge.PublishLocalAsync();
        Assert.HasCount(offers + 1, transport.Offers);
        await window.Clipboard.SetTextAsync(transport.RemoteText);
        await bridge.PublishLocalAsync();
        Assert.HasCount(offers + 2, transport.Offers);
        window.Close();
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ImageClipboardExchangesActualPixelsInBothDirections(bool usePng) => Run(async () =>
    {
        using var context = new TestPluginContext();
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        using var bitmap = new WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        byte[] pixels = [1, 2, 3, 255, 4, 5, 6, 255];
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        }

        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        using var sourcePng = new MemoryStream();
        bitmap.Save(sourcePng, PngBitmapEncoderOptions.Default);
        byte[] encodedImage = sourcePng.ToArray();
        item.Set(DataFormat.Bitmap, () => new Bitmap(new MemoryStream(encodedImage, false)));
        transfer.Add(item);
        await window.Clipboard!.SetDataAsync(transfer);
        var transport = new ClipboardTransport();
        await using var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        Assert.Contains("PNG", transport.Offers[0]);
        transport.Emit(4, RdpClipboardCodec.Dib);
        await Until(() => transport.Responses.Count > 0);
        Assert.AreSequenceEqual(pixels, RdpClipboardCodec.DecodeDib(transport.Responses[0].Data).Bgra);
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        transport.RemotePayload = usePng ? png.ToArray() : RdpClipboardCodec.EncodeDib(new(2, 1, pixels));
        await window.Clipboard.ClearAsync();
        transport.Emit(3, data: Encoding.UTF8.GetBytes(usePng ? "49170\tPNG\n" : "8\t\n"));
        await UntilAsync(async () =>
        {
            using Bitmap? received = await window.Clipboard.TryGetBitmapAsync();
            if (received is null)
            {
                return false;
            }

            using var copy = new WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer buffer = copy.Lock();
            received.CopyPixels(buffer);
            byte[] actual = new byte[8];
            Marshal.Copy(buffer.Address, actual, 0, actual.Length);
            Assert.AreSequenceEqual(pixels, actual);
            return true;
        });
        int offers = transport.Offers.Count;
        await bridge.PublishLocalAsync();
        Assert.HasCount(offers, transport.Offers);
        await window.Clipboard.ClearAsync();
        window.Close();
    });

    [TestMethod]
    public void RemoteCopyPublishesOnlyDescriptorsAndShellRequestsContent() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport
        {
            RemoteFiles = [new("folder", true, 0), new("folder\\data.bin", false, RdpClipboardFiles.ChunkSize + 7)],
            FileData = [.. Enumerable.Range(0, RdpClipboardFiles.ChunkSize + 7).Select(i => (byte)i)]
        };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        Assert.IsEmpty(transport.FileRequests);
        Assert.IsFalse(Directory.Exists(Path.Combine(context.DataDirectory, "rdp-clipboard")));
        int offers = transport.Offers.Count;
        await bridge.PublishLocalAsync();
        Assert.HasCount(offers, transport.Offers);
        byte[] first = await clipboard.Source!.ReadAsync(1, 0, RdpClipboardFiles.ChunkSize);
        byte[] last = await clipboard.Source.ReadAsync(1, first.Length, 7);
        Assert.AreSequenceEqual(transport.FileData, [.. first, .. last]);
        Assert.HasCount(2, transport.FileRequests);
        Assert.IsFalse(Directory.Exists(Path.Combine(context.DataDirectory, "rdp-clipboard")));
        clipboard.LoseOwnership();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => clipboard.Source.ReadAsync(1, 0, 1));
        window.Close();
    });

    [TestMethod]
    public void RemoteClipboardChangeFailsClearlyWithoutVersionLockSupport() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport { RemoteFiles = [new("data", false, 10)], FileData = new byte[10], ChangeDuringFileRead = true };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        _ = await Assert.ThrowsAsync<IOException>(() => clipboard.Source!.ReadAsync(0, 0, 10));
        Assert.IsFalse(Directory.Exists(Path.Combine(context.DataDirectory, "rdp-clipboard")));
        window.Close();
    });

    [TestMethod]
    public void ActiveLockedStreamSurvivesBothClipboardReplacementsAndUnlocksAtRelease() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport { SupportsClipboardLock = true, RemoteFiles = [new("data", false, 4)], FileData = [1, 2, 3, 4], ChangeDuringFileRead = true, HoldAfterFirstBlock = true };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        Assert.HasCount(1, transport.Locks);
        Assert.IsEmpty(transport.FileRequests);
        using var stream = new VirtualFileStream(clipboard.Source!, 0);
        byte[] first = new byte[2], last = new byte[2];
        stream.Read(first, 2, 0);
        var pending = Task.Run(() => stream.Read(last, 2, 0));
        await Until(() => transport.HeldStream != 0);
        clipboard.LoseOwnership();
        await window.Clipboard.SetTextAsync("new local copy");
        await bridge.PublishLocalAsync();
        Assert.IsFalse(pending.IsCompleted);
        transport.Emit(7, transport.HeldStream, 1, [3, 4]);
        await pending;
        Assert.AreSequenceEqual(new byte[] { 1, 2 }, first);
        Assert.AreSequenceEqual(new byte[] { 3, 4 }, last);
        Assert.IsFalse(clipboard.Source!.Cancelled);
        Assert.HasCount(1, transport.Locks);
        Assert.IsTrue(transport.RequestedVersions.All(id => id == transport.Locks[0].Id));
        stream.Dispose();
        await Until(() => transport.Locks.Count == 2);
        Assert.AreEqual((transport.Locks[0].Id, false), transport.Locks[1]);
        Assert.IsTrue(clipboard.Source.Cancelled);
        Assert.IsFalse(Directory.Exists(Path.Combine(context.DataDirectory, "rdp-clipboard")));
        window.Close();
    });

    [TestMethod]
    public void DescriptorPublicationDoesNotOverwriteANewerLocalCopy() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("before");
        var transport = new ClipboardTransport { DelayResponses = true, RemoteFiles = [new("data.bin", false, 3)], FileData = [1, 2, 3] };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => transport.FormatRequests > 0);
        await window.Clipboard.SetTextAsync("new local copy");
        transport.Emit(5, 1, 49160, RdpClipboardCodec.EncodeFiles(transport.RemoteFiles));
        await Until(() => bridge.DownloadProgress?.State == "Failed");
        Assert.IsNull(clipboard.Source);
        Assert.IsEmpty(transport.FileRequests);
        Assert.AreEqual("new local copy", await window.Clipboard.TryGetTextAsync());
        window.Close();
    });

    [TestMethod]
    public void UnsupportedPlatformReportsRestrictionWithoutDownloadingFiles() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var transport = new ClipboardTransport { RemoteFiles = [new("data", false, 10)], FileData = new byte[10] };
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => null);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => bridge.DownloadProgress?.State == "Unsupported");
        Assert.IsEmpty(transport.FileRequests);
        Assert.AreEqual("local", await window.Clipboard.TryGetTextAsync());
        Assert.IsFalse(Directory.Exists(Path.Combine(context.DataDirectory, "rdp-clipboard")));
        window.Close();
    });

    [TestMethod]
    public void LocalFileClipboardAnswersSizeAndRangesAndRejectsUnknownFiles() => Run(async () =>
    {
        using var context = new TestPluginContext();
        string file = Path.Combine(context.DataDirectory, "local.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3, 4, 5]);
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        IStorageFile? storageFile = await window.StorageProvider.TryGetFileFromPathAsync(file);
        Assert.IsNotNull(storageFile);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(storageFile));
        await window.Clipboard!.SetDataAsync(transfer);
        var transport = new ClipboardTransport();
        await using var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        Assert.Contains("FileGroupDescriptorW", transport.Offers[0]);
        transport.Emit(4, RdpClipboardCodec.FileGroup);
        await Until(() => transport.Responses.Count > 0);
        IReadOnlyList<RdpClipboardFile> entries = RdpClipboardCodec.DecodeFiles(transport.Responses[0].Data);
        Assert.AreEqual("local.bin", entries[0].Name);
        Assert.IsEmpty(transport.FileResponses);
        Assert.AreEqual(0L, bridge.UploadProgress!.Bytes);
        Assert.AreEqual("Ready", bridge.UploadProgress.State);
        byte[] request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1);
        transport.Emit(6, 1, 0, request);
        await Until(() => transport.FileResponses.Count > 0);
        Assert.AreEqual(5L, BinaryPrimitives.ReadInt64LittleEndian(transport.FileResponses[0].Data));
        Assert.AreEqual(0L, bridge.UploadProgress!.Bytes);
        Assert.AreEqual("Ready", bridge.UploadProgress.State);
        request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 2);
        transport.Emit(6, 2, 0, request);
        transport.Emit(6, 3, 99, request);
        await Until(() => transport.FileResponses.Count == 3);
        Assert.AreSequenceEqual(new byte[] { 3, 4 }, transport.FileResponses[1].Data);
        Assert.IsFalse(transport.FileResponses[2].Success);
        Assert.AreEqual(2L, bridge.UploadProgress!.Bytes);
        Assert.AreEqual(5L, bridge.UploadProgress.Total);
        Assert.AreEqual("Failed", bridge.UploadProgress.State);
        await bridge.DisposeAsync();
        await window.Clipboard.ClearAsync();
        window.Close();
    });

    [TestMethod]
    public void EmptyUploadFinishesWhenDescriptorsAreDeliveredWithoutWaitingForAContentRequest() => Run(async () =>
    {
        using var context = new TestPluginContext();
        string file = Path.Combine(context.DataDirectory, "empty.bin");
        await File.WriteAllBytesAsync(file, []);
        var window = new Window { Content = new Border() };
        window.Show();
        IStorageFile? item = await window.StorageProvider.TryGetFileFromPathAsync(file);
        Assert.IsNotNull(item);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(item));
        await window.Clipboard!.SetDataAsync(transfer);
        var transport = new ClipboardTransport();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        Assert.AreEqual("Ready", bridge.UploadProgress!.State);
        transport.Emit(4, RdpClipboardCodec.FileGroup);
        await Until(() => bridge.UploadProgress?.State == "Sent");
        Assert.AreEqual(0L, bridge.UploadProgress.Total);
        Assert.IsEmpty(transport.FileResponses);
        await bridge.DisposeAsync();
        Assert.AreEqual("Sent", bridge.UploadProgress.State);
        await window.Clipboard.ClearAsync();
        window.Close();
    });

    [TestMethod]
    public void LockedLocalVersionSurvivesNewCopyUntilPeerUnlocks() => Run(async () =>
    {
        using var context = new TestPluginContext();
        string file = Path.Combine(context.DataDirectory, "original.bin");
        await File.WriteAllBytesAsync(file, [4, 5, 6]);
        var window = new Window { Content = new Border() };
        window.Show();
        IStorageFile? item = await window.StorageProvider.TryGetFileFromPathAsync(file);
        Assert.IsNotNull(item);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(item));
        await window.Clipboard!.SetDataAsync(transfer);
        var transport = new ClipboardTransport { SupportsClipboardLock = true };
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(11, 42);
        transport.Emit(4, RdpClipboardCodec.FileGroup);
        await Until(() => transport.Responses.Count > 0);
        Assert.IsEmpty(transport.FileResponses);
        Assert.AreEqual(0L, bridge.UploadProgress!.Bytes);
        await window.Clipboard.SetTextAsync("replacement");
        await bridge.PublishLocalAsync();
        byte[] request = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(16), 42);
        transport.Emit(6, 1, 0, request);
        await Until(() => transport.FileResponses.Count == 1);
        Assert.IsTrue(transport.FileResponses[0].Success);
        Assert.AreSequenceEqual(new byte[] { 4, 5, 6 }, transport.FileResponses[0].Data);
        transport.Emit(12, 42);
        transport.Emit(6, 2, 0, request);
        await Until(() => transport.FileResponses.Count == 2);
        Assert.IsFalse(transport.FileResponses[1].Success);
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.IsFalse(Directory.Exists(Path.Combine(context.DataDirectory, "rdp-clipboard")));
        window.Close();
    });

    [TestMethod]
    public void PendingInboundTransferDoesNotBlockServingLocalClipboard() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var transport = new ClipboardTransport { DelayResponses = true };
        await using var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await Until(() => transport.FormatRequests > 0);
        transport.Emit(4, 13);
        await Until(() => transport.Responses.Count > 0);
        Assert.AreEqual("local", RdpClipboardCodec.DecodeText(transport.Responses[0].Data));
        window.Close();
    });

    [TestMethod]
    public void DisabledClipboardNeverSubscribesOrPublishes() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var transport = new ClipboardTransport();
        await using var bridge = new RdpClipboardBridge(transport, context, new Border(), false);
        transport.Emit(2);
        await bridge.PublishLocalAsync();
        await bridge.ReceiveRemoteAsync();
        Assert.AreEqual(0, transport.SubscriberCount);
        Assert.IsEmpty(transport.Offers);
    });

    [TestMethod]
    public void DisposalCancelsOutstandingFormatRequest() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        var transport = new ClipboardTransport { DelayResponses = true };
        var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await Until(() => transport.FormatRequests > 0);
        await bridge.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(0, transport.SubscriberCount);
        window.Close();
    });

    [TestMethod]
    public void SupersededFormatDrainsBeforeLatestDescriptorsAndRepeatedReceiveDoesNotRestartFiles() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var transport = new ClipboardTransport { DelayResponses = true, SupportsClipboardLock = true, FileData = [7] };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await Until(() => transport.FormatRequests == 1);
        for (int i = 0; i < 8; i++)
        {
            transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        }

        Task receive = bridge.ReceiveRemoteAsync();
        await Task.Delay(50);
        Assert.AreEqual(1, transport.FormatRequests);
        Assert.IsEmpty(transport.Locks);
        transport.Emit(5, 1, 13, RdpClipboardCodec.EncodeText("obsolete"));
        await Until(() => transport.FormatRequests == 2);
        Assert.IsNull(clipboard.Source);
        Assert.AreEqual("local", await window.Clipboard.TryGetTextAsync());
        transport.Emit(5, 1, 49160, RdpClipboardCodec.EncodeFiles([new("latest", false, 1)]));
        await receive.WaitAsync(TimeSpan.FromSeconds(2));
        await Until(() => clipboard.Source is not null);
        byte[] bytes = await clipboard.Source!.ReadAsync(0, 0, 1);
        Assert.AreSequenceEqual(new byte[] { 7 }, bytes);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => bridge.ReceiveRemoteAsync()));
        Assert.AreEqual(2, transport.FormatRequests);
        Assert.HasCount(1, transport.Locks);
        Assert.HasCount(1, transport.FileRequests);
        await bridge.DisposeAsync();
        Assert.HasCount(2, transport.Locks);
        Assert.DoesNotContain(e => e.Exception is not null, ((CollectingLogger)context.Log).Entries);
        window.Close();
    });

    [TestMethod]
    public void ShutdownDrainsWorkersAndAwaitsExactlyOneUnlockAcrossConcurrentDisposals() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var unlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ClipboardTransport
        {
            DelayResponses = true,
            SupportsClipboardLock = true,
            LockHook = (_, locked) => locked ? Task.CompletedTask : unlock.Task
        };
        var clipboard = new VirtualClipboard();
        var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => transport.FormatRequests == 1);
        for (int i = 0; i < 8; i++)
        {
            transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        }

        bridge.StopTransfers();
        Task first = bridge.DisposeAsync().AsTask();
        Task second = bridge.DisposeAsync().AsTask();
        await Until(() => transport.Locks.Count == 2);
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        transport.Emit(5, 1, 49160, RdpClipboardCodec.EncodeFiles([new("late", false, 1)]));
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await bridge.ReceiveRemoteAsync();
        unlock.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        bridge.StopTransfers();
        await bridge.DisposeAsync();
        Assert.IsNull(clipboard.Source);
        Assert.AreEqual(1, transport.FormatRequests);
        Assert.HasCount(2, transport.Locks);
        Assert.AreEqual((transport.Locks[0].Id, false), transport.Locks[1]);
        Assert.DoesNotContain(e => e.Exception is not null, ((CollectingLogger)context.Log).Entries);
        window.Close();
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InterruptedFormatRequestKeepsItsSlotUntilTheLateResponse(bool timeout) => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var transport = new ClipboardTransport
        {
            DelayResponses = true,
            FormatRequestHook = timeout ? null : _ => Task.FromCanceled(new CancellationToken(true))
        };
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true,
            formatResponseTimeout: TimeSpan.FromMilliseconds(300));
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await Until(() => transport.FormatRequests == 1);
        if (timeout)
        {
            await Until(() => ((CollectingLogger)context.Log).Entries.Any(e => e.Exception is TimeoutException));
        }

        transport.FormatRequestHook = null;
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await Task.Delay(50);
        Assert.AreEqual(1, transport.FormatRequests);
        transport.Emit(5, 1, 13, RdpClipboardCodec.EncodeText("old"));
        await Until(() => transport.FormatRequests == 2);
        Assert.AreEqual("local", await window.Clipboard.TryGetTextAsync());
        transport.Emit(5, 1, 13, RdpClipboardCodec.EncodeText("new"));
        await UntilAsync(async () => await window.Clipboard.TryGetTextAsync() == "new");
        Assert.AreEqual(timeout ? 1 : 0, ((CollectingLogger)context.Log).Entries.Count(e => e.Exception is not null));
        window.Close();
    });

    [TestMethod]
    public void UndrainedFormatTimeoutStopsSharingWithoutReusingTheResponseSlot() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var transport = new ClipboardTransport { DelayResponses = true, SupportsClipboardLock = true };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard,
            formatResponseTimeout: TimeSpan.FromMilliseconds(80));
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("13\t\n"));
        await Until(() => bridge.DownloadProgress?.State == "TimedOut");
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => ((CollectingLogger)context.Log).Entries.Any(e => e.Message.Contains("sharing stopped")));
        transport.Emit(5, 1, 13, RdpClipboardCodec.EncodeText("late"));
        await bridge.ReceiveRemoteAsync();
        await bridge.DisposeAsync();
        Assert.AreEqual(1, transport.FormatRequests);
        Assert.IsEmpty(transport.Locks);
        Assert.IsNull(clipboard.Source);
        Assert.Contains(e => e.Exception is TimeoutException, ((CollectingLogger)context.Log).Entries);
        window.Close();
    });

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void OnDemandReadCanBeCancelledWithoutBlockingClipboardPublication(int cancellation) => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.SetTextAsync("local");
        var transport = new ClipboardTransport
        {
            RemoteFiles = [new("large.bin", false, 3L * RdpClipboardFiles.ChunkSize)],
            FileData = new byte[3 * RdpClipboardFiles.ChunkSize],
            HoldAfterFirstBlock = true,
            SupportsClipboardLock = true
        };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        _ = await clipboard.Source!.ReadAsync(0, 0, RdpClipboardFiles.ChunkSize);
        Task<byte[]> pending = clipboard.Source.ReadAsync(0, RdpClipboardFiles.ChunkSize, RdpClipboardFiles.ChunkSize);
        await Until(() => transport.FileRequests.Count == 2);
        Assert.AreEqual(RdpClipboardFiles.ChunkSize, bridge.DownloadProgress!.Bytes);
        await bridge.PublishLocalAsync().WaitAsync(TimeSpan.FromSeconds(1));
        if (cancellation == 0)
        {
            bridge.CancelTransfer(false);
        }
        else if (cancellation == 1)
        {
            bridge.StopTransfers();
        }
        else
        {
            await bridge.DisposeAsync();
        }

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await Until(() => transport.Locks.Count == 2);
        Assert.AreEqual((transport.Locks[0].Id, false), transport.Locks[1]);
        Assert.AreEqual("Cancelled", bridge.DownloadProgress.State);
        transport.Emit(7, transport.HeldStream, 1, new byte[RdpClipboardFiles.ChunkSize]);
        Assert.HasCount(2, transport.FileRequests);
        window.Close();
    });

    [TestMethod]
    public void TimedOutBlockRetriesSameVersionRangeWithNewIdsAndIgnoresLateResponses() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        const long offset = 200L * 1024 * 1024 + 2;
        var transport = new ClipboardTransport { SupportsClipboardLock = true, RemoteFiles = [new("folder", true, 0), new("folder\\data", false, offset + 6)] };
        var clipboard = new VirtualClipboard();
        uint first = 0, second = 0;
        var retrySent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.FileRequestHook = (stream, _, _, _) =>
        {
            if (first == 0)
            {
                first = stream;
            }
            else { second = stream; _ = retrySent.TrySetResult(); }
        };
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard,
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30));
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        Task<byte[]> pending = clipboard.Source!.ReadAsync(1, offset, 4);
        await retrySent.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreNotEqual(first, second);
        Assert.AreEqual("Retrying", bridge.DownloadProgress!.State);
        Assert.IsTrue(bridge.DownloadProgress.Active);
        Assert.AreEqual(0L, bridge.DownloadProgress.Bytes);
        transport.Emit(7, first, 1, [9, 9, 9, 9]);
        Assert.IsFalse(pending.IsCompleted);
        await bridge.PublishLocalAsync().WaitAsync(TimeSpan.FromSeconds(1));
        transport.Emit(7, second, 1, [2, 3, 4, 5]);
        byte[] bytes = await pending;
        Assert.AreSequenceEqual(new byte[] { 2, 3, 4, 5 }, bytes);
        Assert.AreEqual(4L, bridge.DownloadProgress.Bytes);
        Assert.HasCount(2, transport.FileRequests);
        Assert.IsTrue(transport.FileRequests.All(r => r == (1u, offset, 4u)));
        Assert.IsTrue(transport.RequestedVersions.All(v => v == transport.Locks[0].Id));
        transport.Emit(7, first, 0);
        Assert.AreEqual(4L, bridge.DownloadProgress.Bytes);
        window.Close();
    });

    [TestMethod]
    public void FailedLockSendReleasesPossibleRemoteLockWithoutRequestingDescriptors() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport { SupportsClipboardLock = true, FailLock = true };
        var clipboard = new VirtualClipboard();
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => transport.Locks.Count == 2);
        Assert.AreEqual((transport.Locks[0].Id, false), transport.Locks[1]);
        Assert.AreEqual(0, transport.FormatRequests);
        Assert.IsNull(clipboard.Source);
        window.Close();
    });

    [TestMethod]
    [DataRow("exhausted", 3)]
    [DataRow("rejected", 1)]
    [DataRow("cancelled", 1)]
    [DataRow("disconnected", 1)]
    [DataRow("sourceChanged", 1)]
    public void BlockRetryStopsAtItsBoundOrOnTerminalConditions(string outcome, int expectedRequests) => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport { SupportsClipboardLock = outcome != "sourceChanged", RemoteFiles = [new("data", false, 4)] };
        var clipboard = new VirtualClipboard();
        transport.FileRequestHook = (stream, _, _, _) => { if (outcome == "rejected") { transport.Emit(7, stream, 0); } };
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard,
            TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(80));
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        using IDisposable lease = clipboard.Source!.Acquire();
        Task<byte[]> pending = clipboard.Source.ReadAsync(0, 0, 4);
        if (outcome is "cancelled" or "disconnected" or "sourceChanged")
        {
            await Until(() => bridge.DownloadProgress?.State == "Retrying");
            if (outcome == "cancelled")
            {
                bridge.CancelTransfer(false);
            }
            else if (outcome == "disconnected")
            {
                bridge.StopTransfers();
            }
            else
            {
                transport.Emit(9);
            }
        }
        if (outcome == "exhausted")
        {
            _ = await Assert.ThrowsAsync<TimeoutException>(() => pending);
        }
        else if (outcome is "cancelled" or "disconnected")
        {
            _ = await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            _ = await Assert.ThrowsAsync<IOException>(() => pending);
        }

        Assert.HasCount(expectedRequests, transport.FileRequests);
        Assert.AreEqual(0L, bridge.DownloadProgress!.Bytes);
        Assert.AreEqual(outcome == "exhausted" ? "TimedOut" : outcome is "cancelled" or "disconnected" ? "Cancelled" : "Failed", bridge.DownloadProgress.State);
        window.Close();
    });

    [TestMethod]
    public void ConcurrentFileReadsMatchOutOfOrderResponsesByRequestId() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var window = new Window { Content = new Border() };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport { SupportsClipboardLock = true, RemoteFiles = [new("data", false, 8)] };
        var clipboard = new VirtualClipboard();
        var streams = new List<uint>();
        transport.FileRequestHook = (stream, _, _, _) => streams.Add(stream);
        await using var bridge = new RdpClipboardBridge(transport, context, (Control)window.Content, true, () => clipboard);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Until(() => clipboard.Source is not null);
        Task<byte[]> first = clipboard.Source!.ReadAsync(0, 0, 4);
        Task<byte[]> second = clipboard.Source.ReadAsync(0, 4, 4);
        transport.Emit(7, streams[1], 1, [5, 6, 7, 8]);
        transport.Emit(7, streams[0], 1, [1, 2, 3, 4]);
        byte[] secondBytes = await second, firstBytes = await first;
        Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4 }, firstBytes);
        Assert.AreSequenceEqual(new byte[] { 5, 6, 7, 8 }, secondBytes);
        Assert.AreEqual(8L, bridge.DownloadProgress!.Bytes);
        window.Close();
    });

    [TestMethod]
    public void UploadProgressCountsUniqueRangesInsteadOfRetriesOrHighWaterMark()
    {
        var transfer = new RdpClipboardTransfer();
        transfer.Start(12);
        transfer.Sent(0, 8, 4, "data");
        Assert.AreEqual(4L, transfer.Snapshot!.Bytes);
        transfer.Sent(0, 8, 4, "data");
        Assert.AreEqual(4L, transfer.Snapshot.Bytes);
        transfer.Sent(0, 0, 6, "data");
        Assert.AreEqual("Transferring", transfer.Snapshot.State);
        transfer.Sent(0, 4, 6, "data");
        Assert.AreEqual(12L, transfer.Snapshot.Bytes);
        Assert.AreEqual("Sent", transfer.Snapshot.State);
    }

    [TestMethod]
    public void TransferProgressReportsAverageSpeedAndRemainingTimeAfterSampling()
    {
        var transfer = new RdpClipboardTransfer();
        transfer.Start(1000);
        Thread.Sleep(300);
        transfer.Update(500, "data");
        RdpClipboardProgress progress = transfer.Snapshot!;
        Assert.IsNotNull(progress.SpeedBytesPerSecond);
        Assert.IsGreaterThan(0, progress.SpeedBytesPerSecond!.Value);
        Assert.IsNotNull(progress.Remaining);
        Assert.IsGreaterThan(TimeSpan.Zero, progress.Remaining!.Value);

        transfer.Update(1000, "data");
        progress = transfer.Snapshot!;
        Assert.IsNotNull(progress.SpeedBytesPerSecond);
        Assert.IsNull(progress.Remaining);
    }

    [TestMethod]
    public void TransferProgressHidesMetricsBeforeSamplingAndForUnknownTotals()
    {
        var transfer = new RdpClipboardTransfer();
        transfer.Start(1000);
        transfer.Update(100, "data");
        Assert.IsNull(transfer.Snapshot!.SpeedBytesPerSecond);
        Assert.IsNull(transfer.Snapshot.Remaining);

        transfer.Start(0);
        Thread.Sleep(300);
        transfer.Update(100, "data");
        Assert.IsNull(transfer.Snapshot!.SpeedBytesPerSecond);
        Assert.IsNull(transfer.Snapshot.Remaining);
    }

    private static async Task Until(Func<bool> predicate) => await UntilAsync(() => Task.FromResult(predicate()));
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
    private static void Run(Func<Task> action)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RdpHeadlessApp).Assembly);
        _ = session.Dispatch(async () => { await action(); return true; }, default).GetAwaiter().GetResult();
    }

    private sealed class VirtualClipboard : IRdpVirtualFileClipboard
    {
        internal RdpVirtualFiles? Source { get; private set; }
        public bool IsCurrent { get => field && Source is { Cancelled: false }; private set; }
        public Task PublishAsync(RdpVirtualFiles source, uint? expectedSequence, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Source?.Retire(); Source = source; IsCurrent = true; return Task.CompletedTask; }
        internal void LoseOwnership() { IsCurrent = false; Source?.Retire(); }
        public ValueTask DisposeAsync() { Source?.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class ClipboardTransport : IRdpClipboardTransport
    {
        public bool SupportsClipboardLock { get; set; }
        internal List<(uint Id, bool Locked)> Locks { get; } = [];
        internal List<uint?> RequestedVersions { get; } = [];
        internal bool FailLock;
        internal Func<uint, bool, Task>? LockHook;
        internal Func<uint, Task>? FormatRequestHook;
        public Task LockClipboardAsync(uint id, bool locked, CancellationToken cancellationToken)
        {
            Locks.Add((id, locked));
            return locked && FailLock ? Task.FromException(new IOException("Lock send failed after writing.")) : LockHook?.Invoke(id, locked) ?? Task.CompletedTask;
        }
        public Task RequestFileVersionAsync(uint stream, uint index, uint flags, ulong offset, uint length, uint? clipDataId, CancellationToken cancellationToken)
        { RequestedVersions.Add(clipDataId); return RequestFileAsync(stream, index, flags, offset, length, cancellationToken); }
        public event Action<RdpClipboardEvent>? ClipboardEvent;
        internal int SubscriberCount => ClipboardEvent?.GetInvocationList().Length ?? 0;
        internal List<string> Offers { get; } = [];
        internal List<(byte[] Data, bool Success)> Responses { get; } = [];
        internal List<(uint Stream, byte[] Data, bool Success)> FileResponses { get; } = [];
        internal List<(uint Index, ulong Offset, uint Length)> FileRequests { get; } = [];
        internal int FormatRequests;
        internal string RemoteText = "";
        internal IReadOnlyList<RdpClipboardFile> RemoteFiles = [];
        internal byte[] FileData = [];
        internal byte[]? RemotePayload;
        internal bool ChangeDuringFileRead;
        internal bool DelayResponses;
        internal bool HoldAfterFirstBlock;
        internal Action<uint, uint, ulong, uint>? FileRequestHook;
        internal uint HeldStream;
        internal void Emit(uint kind, uint a = 0, uint b = 0, byte[]? data = null) => ClipboardEvent?.Invoke(new(kind, a, b, data ?? []));
        public Task OfferFormatsAsync(string formats, CancellationToken cancellationToken) { Offers.Add(formats); return Task.CompletedTask; }
        public Task RequestFormatAsync(uint format, CancellationToken cancellationToken)
        {
            FormatRequests++;
            if (FormatRequestHook is { } hook)
            {
                return hook(format);
            }

            if (!DelayResponses)
            {
                Emit(5, 1, format, RemotePayload ?? (format == 13 ? RdpClipboardCodec.EncodeText(RemoteText) : RdpClipboardCodec.EncodeFiles(RemoteFiles)));
            }

            return Task.CompletedTask;
        }
        public Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken)
        { Responses.Add((data, success)); return Task.CompletedTask; }
        public Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken)
        {
            FileRequests.Add((index, offset, length));
            if (FileRequestHook is { } hook) { hook(stream, index, offset, length); return Task.CompletedTask; }
            if (HoldAfterFirstBlock && offset > 0) { HeldStream = stream; return Task.CompletedTask; }
            if (ChangeDuringFileRead)
            {
                Emit(9);
            }

            Emit(7, stream, 1, FileData.AsSpan((int)offset, (int)length).ToArray());
            return Task.CompletedTask;
        }
        public Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken)
        {
            FileResponses.Add((stream, data, success));
            return Task.CompletedTask;
        }
    }
}
