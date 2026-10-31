using System.Buffers.Binary;
using System.Text;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeText([1]));
        Assert.Throws<DecoderFallbackException>(() => RdpClipboardCodec.DecodeText([0, 0xD8]));
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.EncodeText("a\0b"));
    }

    [TestMethod]
    public void FileDescriptorsRoundTripDirectoriesAnd64BitLengths()
    {
        RdpClipboardFile[] files = [new("目录", true, 0), new("目录\\文件.txt", false, 0x100000005)];
        byte[] data = RdpClipboardCodec.EncodeFiles(files);
        var decoded = RdpClipboardCodec.DecodeFiles(data);
        CollectionAssert.AreEqual(files, decoded.ToArray());
        Assert.AreEqual(4 + 592 * 2, data.Length);
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(data[..^1]));
        BinaryPrimitives.WriteUInt32LittleEndian(data, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(data));
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
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(duplicates));
        byte[] conflict = RdpClipboardCodec.EncodeFiles([new("a", false, 0), new("a\\b", false, 0)]);
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeFiles(conflict));
    }

    [TestMethod]
    public void DibRoundTripHandlesPaddingAndVerticalOrder()
    {
        byte[] pixels = [1, 2, 3, 255, 4, 5, 6, 255];
        byte[] encoded = RdpClipboardCodec.EncodeDib(new(1, 2, pixels));
        RdpClipboardImage decoded = RdpClipboardCodec.DecodeDib(encoded);
        Assert.AreEqual(1, decoded.Width);
        Assert.AreEqual(2, decoded.Height);
        CollectionAssert.AreEqual(pixels, decoded.Bgra);
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeDib(encoded[..^1]));
        BinaryPrimitives.WriteInt32LittleEndian(encoded.AsSpan(4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeDib(encoded));
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
        CollectionAssert.AreEqual(new byte[] { 9, 8, 7, 255 }, RdpClipboardCodec.DecodeDib(data).Bgra);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 3);
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.DecodeDib(data));
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.ValidateImageSize(8192, 8192));
        Assert.Throws<InvalidDataException>(() => RdpClipboardCodec.ValidatePng(new byte[33]));
    }

    [TestMethod]
    public async Task LocalSnapshotUsesChunksAndRejectsInvalidRanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "vela-rdp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "child"));
        byte[] bytes = Enumerable.Range(0, RdpClipboardFiles.ChunkSize + 11).Select(i => (byte)i).ToArray();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "child", "data.bin"), bytes);
            using var snapshot = RdpClipboardFiles.Open([root], default);
            int index = snapshot.Entries.FindIndex(e => !e.IsDirectory);
            byte[] first = await snapshot.ReadAsync((uint)index, 0, RdpClipboardFiles.ChunkSize, default);
            byte[] last = await snapshot.ReadAsync((uint)index, (ulong)first.Length, 100, default);
            CollectionAssert.AreEqual(bytes, first.Concat(last).ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ReadAsync((uint)index, (ulong)bytes.Length + 1, 1, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ReadAsync(0, 0, 1, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => snapshot.ReadAsync((uint)index, 0, RdpClipboardFiles.ChunkSize + 1, default));
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
        Assert.AreEqual(offers, transport.Offers.Count);
        await window.Clipboard.SetTextAsync("changed");
        await bridge.PublishLocalAsync();
        Assert.AreEqual(offers + 1, transport.Offers.Count);
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
        using (ILockedFramebuffer buffer = bitmap.Lock()) Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
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
        StringAssert.Contains(transport.Offers[0], "PNG");
        transport.Emit(4, RdpClipboardCodec.Dib);
        await Until(() => transport.Responses.Count > 0);
        CollectionAssert.AreEqual(pixels, RdpClipboardCodec.DecodeDib(transport.Responses[0].Data).Bgra);
        using var png = new MemoryStream();
        bitmap.Save(png, PngBitmapEncoderOptions.Default);
        transport.RemotePayload = usePng ? png.ToArray() : RdpClipboardCodec.EncodeDib(new(2, 1, pixels));
        await window.Clipboard.ClearAsync();
        transport.Emit(3, data: Encoding.UTF8.GetBytes(usePng ? "49170\tPNG\n" : "8\t\n"));
        await UntilAsync(async () =>
        {
            using Bitmap? received = await window.Clipboard.TryGetBitmapAsync();
            if (received is null) return false;
            using var copy = new WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer buffer = copy.Lock();
            received.CopyPixels(buffer);
            byte[] actual = new byte[8];
            Marshal.Copy(buffer.Address, actual, 0, actual.Length);
            CollectionAssert.AreEqual(pixels, actual);
            return true;
        });
        int offers = transport.Offers.Count;
        await bridge.PublishLocalAsync();
        Assert.AreEqual(offers, transport.Offers.Count);
        await window.Clipboard.ClearAsync();
        window.Close();
    });

    [TestMethod]
    public void RemoteFilesAreDownloadedInChunksOnlyOnExplicitReceive() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport
        {
            RemoteFiles = [new("folder", true, 0), new("folder\\data.bin", false, RdpClipboardFiles.ChunkSize + 7)],
            FileData = Enumerable.Range(0, RdpClipboardFiles.ChunkSize + 7).Select(i => (byte)i).ToArray()
        };
        await using var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await bridge.PublishLocalAsync();
        Assert.AreEqual(0, transport.FileRequests.Count);
        await bridge.ReceiveRemoteAsync();
        Assert.AreEqual(2, transport.FileRequests.Count);
        string file = Directory.GetFiles(Path.Combine(context.DataDirectory, "rdp-clipboard"), "data.bin", SearchOption.AllDirectories).Single();
        byte[] downloaded = await File.ReadAllBytesAsync(file);
        CollectionAssert.AreEqual(transport.FileData, downloaded);
        var items = await window.Clipboard.TryGetFilesAsync();
        Assert.HasCount(1, items!);
        int offers = transport.Offers.Count;
        await bridge.PublishLocalAsync();
        Assert.AreEqual(offers, transport.Offers.Count);
        window.Close();
    });

    [TestMethod]
    public void RemoteClipboardChangeAbortsDownloadAndRemovesPartialFiles() => Run(async () =>
    {
        using var context = new TestPluginContext();
        var owner = new Border();
        var window = new Window { Content = owner };
        window.Show();
        await window.Clipboard!.ClearAsync();
        var transport = new ClipboardTransport { RemoteFiles = [new("data", false, 10)], FileData = new byte[10], ChangeDuringFileRead = true };
        await using var bridge = new RdpClipboardBridge(transport, context, owner, true);
        transport.Emit(2);
        await Until(() => transport.Offers.Count > 0);
        transport.Emit(3, data: Encoding.UTF8.GetBytes("49160\tFileGroupDescriptorW\n"));
        await Assert.ThrowsAsync<IOException>(() => bridge.ReceiveRemoteAsync());
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.Combine(context.DataDirectory, "rdp-clipboard")));
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
        StringAssert.Contains(transport.Offers[0], "FileGroupDescriptorW");
        transport.Emit(4, RdpClipboardCodec.FileGroup);
        await Until(() => transport.Responses.Count > 0);
        var entries = RdpClipboardCodec.DecodeFiles(transport.Responses[0].Data);
        Assert.AreEqual("local.bin", entries[0].Name);
        byte[] request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 1);
        transport.Emit(6, 1, 0, request);
        await Until(() => transport.FileResponses.Count > 0);
        Assert.AreEqual(5L, BinaryPrimitives.ReadInt64LittleEndian(transport.FileResponses[0].Data));
        request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, 2);
        BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 2);
        transport.Emit(6, 2, 0, request);
        transport.Emit(6, 3, 99, request);
        await Until(() => transport.FileResponses.Count == 3);
        CollectionAssert.AreEqual(new byte[] { 3, 4 }, transport.FileResponses[1].Data);
        Assert.IsFalse(transport.FileResponses[2].Success);
        await bridge.DisposeAsync();
        await window.Clipboard.ClearAsync();
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

    private static async Task Until(Func<bool> predicate) => await UntilAsync(() => Task.FromResult(predicate()));
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }
    private static void Run(Func<Task> action)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RdpHeadlessApp).Assembly);
        session.Dispatch(async () => { await action(); return true; }, default).GetAwaiter().GetResult();
    }

    private sealed class ClipboardTransport : IRdpClipboardTransport
    {
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
        internal void Emit(uint kind, uint a = 0, uint b = 0, byte[]? data = null) => ClipboardEvent?.Invoke(new(kind, a, b, data ?? []));
        public Task OfferFormatsAsync(string formats, CancellationToken cancellationToken) { Offers.Add(formats); return Task.CompletedTask; }
        public Task RequestFormatAsync(uint format, CancellationToken cancellationToken)
        {
            FormatRequests++;
            if (!DelayResponses) Emit(5, 1, format, RemotePayload ?? (format == 13 ? RdpClipboardCodec.EncodeText(RemoteText) : RdpClipboardCodec.EncodeFiles(RemoteFiles)));
            return Task.CompletedTask;
        }
        public Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken)
        { Responses.Add((data, success)); return Task.CompletedTask; }
        public Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken)
        {
            FileRequests.Add((index, offset, length));
            if (ChangeDuringFileRead) Emit(9);
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
