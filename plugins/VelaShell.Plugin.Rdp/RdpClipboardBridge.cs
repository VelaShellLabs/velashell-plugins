using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VelaShell.PluginSdk;
using VelaShell.Rdp;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpClipboardBridge : IAsyncDisposable
{
    private readonly IRdpClipboardTransport _transport;
    private readonly IPluginContext _context;
    private readonly Control _owner;
    private readonly bool _enabled;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly SemaphoreSlim _localGate = new(1, 1);
    private readonly object _requestGate = new();
    private readonly Channel<RdpClipboardEvent> _events = Channel.CreateBounded<RdpClipboardEvent>(128);
    private readonly Channel<RdpClipboardEvent> _requests = Channel.CreateBounded<RdpClipboardEvent>(128);
    private readonly Task _requestWorker;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _fileRequests = new();
    private readonly Task _worker;
    private TaskCompletionSource<byte[]>? _formatResponse;
    private uint _requestedFormat;
    private uint _stream;
    private int _generation;
    private int _disposed;
    private bool _ready;
    private Dictionary<uint, string> _remoteFormats = [];
    private Dictionary<uint, byte[]> _localData = [];
    private RdpClipboardFiles? _localFiles;
    private string? _lastRemoteFingerprint;
    private string? _lastPublishedFingerprint;

    internal RdpClipboardBridge(IRdpClipboardTransport transport, IPluginContext context, Control owner, bool enabled)
    {
        _transport = transport;
        _context = context;
        _owner = owner;
        _enabled = enabled;
        _worker = RunAsync();
        _requestWorker = ServeRequestsAsync();
        if (enabled) _transport.ClipboardEvent += OnEvent;
    }

    private void OnEvent(RdpClipboardEvent value)
    {
        if (_stop.IsCancellationRequested) return;
        if (value.Kind == 5)
        {
            lock (_requestGate)
            {
                if (_formatResponse is { } response && value.B == _requestedFormat)
                {
                    _formatResponse = null;
                    if (value.A != 0) response.TrySetResult(value.Data);
                    else response.TrySetException(new IOException("The remote clipboard rejected the format request."));
                }
            }
            return;
        }
        if (value.Kind == 7)
        {
            if (_fileRequests.TryRemove(value.A, out var response))
            {
                if (value.B != 0) response.TrySetResult(value.Data);
                else response.TrySetException(new IOException("The remote clipboard rejected the file request."));
            }
            return;
        }
        if (value.Kind is 3 or 9)
        {
            Dictionary<uint, string> formats = [];
            if (value.Kind == 3)
            {
                foreach (string line in Encoding.UTF8.GetString(value.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split('\t', 2);
                    if (parts.Length == 2 && uint.TryParse(parts[0], out uint id)) formats[id] = parts[1];
                }
            }
            lock (_requestGate)
            {
                _remoteFormats = formats;
                Interlocked.Increment(ref _generation);
            }
        }
        ChannelWriter<RdpClipboardEvent> writer = value.Kind is 4 or 6 ? _requests.Writer : _events.Writer;
        if (!writer.TryWrite(value))
        {
            _context.Log.Warn("RDP clipboard queue overflow; clipboard sharing stopped for this connection.");
            _stop.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (RdpClipboardEvent value in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (value.Kind)
                    {
                        case 2:
                            _ready = true;
                            await PublishLocalAsync(_stop.Token).ConfigureAwait(false);
                            break;
                        case 3:
                            await ReceiveAsync(false, _stop.Token).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _context.Log.Warn("RDP clipboard operation failed.", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeRequestsAsync()
    {
        try
        {
            await foreach (RdpClipboardEvent request in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (request.Kind == 4) await RespondFormatAsync(request.A).ConfigureAwait(false);
                    else await RespondFileAsync(request).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _context.Log.Warn("RDP clipboard response failed.", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task PublishLocalAsync(CancellationToken ct = default)
    {
        if (!_enabled || !_ready || _stop.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = linked.Token;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        RdpClipboardFiles? files = null;
        try
        {
            var snapshot = await OnUiAsync(async () =>
            {
                IClipboard? clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
                Dictionary<uint, byte[]> data = [];
                string[] paths = [];
                if (clipboard is null) return (Data: data, Paths: paths);
                var items = await clipboard.TryGetFilesAsync();
                if (items is { Length: > 0 })
                {
                    paths = items.Select(item => item.TryGetLocalPath()
                        ?? throw new InvalidDataException("Only local clipboard files can be shared through RDP.")).ToArray();
                    return (Data: data, Paths: paths);
                }
                string? text = await clipboard.TryGetTextAsync();
                if (text is not null) data[RdpClipboardCodec.UnicodeText] = RdpClipboardCodec.EncodeText(text);
                using Bitmap? bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap is not null)
                {
                    RdpClipboardCodec.ValidateImageSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
                    using var png = new MemoryStream();
                    bitmap.Save(png, PngBitmapEncoderOptions.Default);
                    byte[] pngBytes = png.ToArray();
                    RdpClipboardCodec.ValidatePng(pngBytes);
                    data[RdpClipboardCodec.Png] = pngBytes;
                    using var copy = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                    using ILockedFramebuffer pixels = copy.Lock();
                    bitmap.CopyPixels(pixels);
                    byte[] bgra = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
                    for (int y = 0; y < bitmap.PixelSize.Height; y++)
                        Marshal.Copy(pixels.Address + y * pixels.RowBytes, bgra, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
                    data[RdpClipboardCodec.Dib] = RdpClipboardCodec.EncodeDib(new(bitmap.PixelSize.Width, bitmap.PixelSize.Height, bgra));
                }
                return (Data: data, Paths: paths);
            }).ConfigureAwait(false);
            string fingerprint = Fingerprint(snapshot.Data, snapshot.Paths);
            if (fingerprint == _lastRemoteFingerprint || fingerprint == _lastPublishedFingerprint) return;
            if (snapshot.Paths.Length > 0)
            {
                files = await Task.Run(() => RdpClipboardFiles.Open(snapshot.Paths, ct), ct).ConfigureAwait(false);
                snapshot.Data[RdpClipboardCodec.FileGroup] = RdpClipboardCodec.EncodeFiles(files.Entries);
            }
            await _localGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _localFiles?.Dispose();
                _localFiles = files;
                files = null;
                _localData = snapshot.Data;
                string formats = string.Concat(_localData.Keys.Select(id => $"{id}\t{FormatName(id)}\n"));
                if (_localFiles is not null) formats += $"{RdpClipboardCodec.FileContents}\tFileContents\n";
                await _transport.OfferFormatsAsync(formats, ct).ConfigureAwait(false);
                _lastPublishedFingerprint = fingerprint;
            }
            finally { _localGate.Release(); }
        }
        finally { files?.Dispose(); _operations.Release(); }
    }

    public Task ReceiveRemoteAsync(CancellationToken ct = default) => ReceiveAsync(true, ct);

    private async Task ReceiveAsync(bool includeFiles, CancellationToken ct)
    {
        if (!_enabled || !_ready || _stop.IsCancellationRequested) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        ct = linked.Token;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            int generation;
            Dictionary<uint, string> formats;
            lock (_requestGate) { generation = _generation; formats = _remoteFormats; }
            uint fileFormat = formats.FirstOrDefault(p => p.Value == "FileGroupDescriptorW").Key;
            if (fileFormat != 0)
            {
                if (includeFiles)
                {
                    byte[] descriptors = await RequestFormatAsync(fileFormat, ct).ConfigureAwait(false);
                    CheckGeneration(generation);
                    await ReceiveFilesAsync(RdpClipboardCodec.DecodeFiles(descriptors), generation, ct).ConfigureAwait(false);
                }
                return;
            }
            uint pngFormat = formats.FirstOrDefault(p => p.Value == "PNG").Key;
            uint imageFormat = pngFormat != 0 ? pngFormat : formats.ContainsKey(RdpClipboardCodec.Dib)
                ? RdpClipboardCodec.Dib : formats.ContainsKey(RdpClipboardCodec.DibV5) ? RdpClipboardCodec.DibV5 : 0;
            string? text = null;
            byte[]? image = null;
            if (formats.ContainsKey(RdpClipboardCodec.UnicodeText))
                text = RdpClipboardCodec.DecodeText(await RequestFormatAsync(RdpClipboardCodec.UnicodeText, ct).ConfigureAwait(false));
            CheckGeneration(generation);
            if (imageFormat != 0) image = await RequestFormatAsync(imageFormat, ct).ConfigureAwait(false);
            CheckGeneration(generation);
            if (text is null && image is null) return;
            RdpClipboardImage? dib = null;
            if (image is not null)
            {
                if (pngFormat != 0) RdpClipboardCodec.ValidatePng(image);
                else dib = RdpClipboardCodec.DecodeDib(image);
            }
            await OnUiAsync(async () =>
            {
                CheckGeneration(generation);
                IClipboard? clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
                if (clipboard is null) return false;
                Bitmap? bitmap = null;
                try
                {
                    var item = new DataTransferItem();
                    Dictionary<uint, byte[]> data = [];
                    if (text is not null)
                    {
                        item.SetText(text);
                        data[RdpClipboardCodec.UnicodeText] = RdpClipboardCodec.EncodeText(text);
                    }
                    if (image is not null)
                    {
                        if (dib is not null)
                        {
                            var writable = new WriteableBitmap(new PixelSize(dib.Width, dib.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                            bitmap = writable;
                            using ILockedFramebuffer pixels = writable.Lock();
                            for (int y = 0; y < dib.Height; y++)
                                Marshal.Copy(dib.Bgra, y * dib.Width * 4, pixels.Address + y * pixels.RowBytes, dib.Width * 4);
                        }
                        else bitmap = new Bitmap(new MemoryStream(image, false));
                        using var encoded = new MemoryStream();
                        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
                        byte[] clipboardPng = encoded.ToArray();
                        // Each delayed clipboard read owns its bitmap; never expose a disposed shared instance.
                        item.Set(DataFormat.Bitmap, () => new Bitmap(new MemoryStream(clipboardPng, false)));
                    }
                    var transfer = new DataTransfer();
                    transfer.Add(item);
                    await clipboard.SetDataAsync(transfer);
                    // Read the platform's normalized representation to suppress clipboard echo.
                    _lastRemoteFingerprint = await CurrentFingerprintAsync(clipboard);
                    return true;
                }
                finally { bitmap?.Dispose(); }
            }).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task ReceiveFilesAsync(IReadOnlyList<RdpClipboardFile> files, int generation, CancellationToken ct)
    {
        if (files.Sum(f => f.Length) > RdpClipboardFiles.MaxTransferBytes)
            throw new InvalidDataException("Clipboard files exceed the 1 GiB transfer limit.");
        string cache = Path.Combine(_context.DataDirectory, "rdp-clipboard");
        Directory.CreateDirectory(cache);
        RdpClipboardFiles.RejectLinks(cache);
        long cachedBytes = Directory.EnumerateFiles(cache, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }).Sum(path => new FileInfo(path).Length);
        if (cachedBytes + files.Sum(f => f.Length) > 4 * RdpClipboardFiles.MaxTransferBytes)
            throw new IOException("RDP clipboard cache is full (4 GiB). Remove old received files before receiving more.");
        string root = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool complete = false;
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                CheckGeneration(generation);
                RdpClipboardFile file = files[i];
                string path = Path.Combine(root, file.Name.Replace('\\', Path.DirectorySeparatorChar));
                if (file.IsDirectory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    RdpClipboardFiles.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long offset = 0;
                while (offset < file.Length)
                {
                    uint length = (uint)Math.Min(RdpClipboardFiles.ChunkSize, file.Length - offset);
                    byte[] bytes = await RequestFileAsync((uint)i, (ulong)offset, length, ct).ConfigureAwait(false);
                    CheckGeneration(generation);
                    if (bytes.Length == 0 || bytes.Length > length) throw new InvalidDataException("Invalid clipboard file block.");
                    await output.WriteAsync(bytes, ct).ConfigureAwait(false);
                    offset += bytes.Length;
                }
            }
            string[] paths = Directory.GetFileSystemEntries(root);
            await OnUiAsync(async () =>
            {
                CheckGeneration(generation);
                TopLevel? top = TopLevel.GetTopLevel(_owner);
                if (top?.Clipboard is null) throw new IOException("System clipboard is unavailable.");
                var transfer = new DataTransfer();
                foreach (string path in paths)
                {
                    IStorageItem? item = Directory.Exists(path)
                        ? await top.StorageProvider.TryGetFolderFromPathAsync(path)
                        : await top.StorageProvider.TryGetFileFromPathAsync(path);
                    if (item is null) throw new IOException("Could not publish a received clipboard file.");
                    transfer.Add(DataTransferItem.CreateFile(item));
                }
                await top.Clipboard.SetDataAsync(transfer);
                _lastRemoteFingerprint = Fingerprint([], paths);
                return true;
            }).ConfigureAwait(false);
            complete = true;
        }
        finally
        {
            if (!complete) Directory.Delete(root, true);
        }
    }

    private async Task<byte[]> RequestFormatAsync(uint format, CancellationToken ct)
    {
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_requestGate)
        {
            // Format responses carry no protocol token. A timed-out request must drain before another request.
            if (_formatResponse is not null) throw new IOException("The previous clipboard format request is still pending.");
            _requestedFormat = format;
            _formatResponse = response;
        }
        try { await _transport.RequestFormatAsync(format, ct).ConfigureAwait(false); }
        catch
        {
            lock (_requestGate) if (ReferenceEquals(_formatResponse, response)) _formatResponse = null;
            throw;
        }
        return await response.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
    }

    private async Task<byte[]> RequestFileAsync(uint index, ulong offset, uint length, CancellationToken ct)
    {
        uint stream = unchecked(++_stream);
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_fileRequests.TryAdd(stream, response)) throw new IOException("Clipboard stream collision.");
        try
        {
            await _transport.RequestFileAsync(stream, index, 2, offset, length, ct).ConfigureAwait(false);
            return await response.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        finally { _fileRequests.TryRemove(stream, out _); }
    }

    private async Task RespondFormatAsync(uint format)
    {
        await _localGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            bool found = _localData.TryGetValue(format, out byte[]? data);
            await _transport.RespondFormatAsync(data ?? [], found, _stop.Token).ConfigureAwait(false);
        }
        finally { _localGate.Release(); }
    }

    private async Task RespondFileAsync(RdpClipboardEvent request)
    {
        byte[] data = [];
        bool success = false;
        await _localGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            if (request.Data.Length != 16 || _localFiles is null || request.B >= _localFiles.Entries.Count)
                throw new InvalidDataException("Invalid clipboard file request.");
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(request.Data);
            if (flags == 1 && !_localFiles.Entries[(int)request.B].IsDirectory)
            {
                data = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(data, _localFiles.Entries[(int)request.B].Length);
            }
            else if (flags == 2)
                data = await _localFiles.ReadAsync(request.B, BinaryPrimitives.ReadUInt64LittleEndian(request.Data.AsSpan(4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(request.Data.AsSpan(12)), _stop.Token).ConfigureAwait(false);
            else throw new InvalidDataException("Invalid clipboard file request flags.");
            success = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException)
        { _context.Log.Warn("RDP clipboard file request rejected.", ex); }
        finally { _localGate.Release(); }
        await _transport.RespondFileAsync(request.A, data, success, _stop.Token).ConfigureAwait(false);
    }

    private void CheckGeneration(int generation)
    {
        if (generation != Volatile.Read(ref _generation)) throw new IOException("The remote clipboard changed during transfer.");
        _stop.Token.ThrowIfCancellationRequested();
    }

    private static string FormatName(uint id) => id switch
    {
        RdpClipboardCodec.Png => "PNG",
        RdpClipboardCodec.FileGroup => "FileGroupDescriptorW",
        _ => ""
    };

    private static string Fingerprint(Dictionary<uint, byte[]> data, string[] paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in data.OrderBy(p => p.Key))
        {
            hash.AppendData(BitConverter.GetBytes(pair.Key));
            hash.AppendData(pair.Value);
        }
        foreach (string path in paths.Order(StringComparer.Ordinal)) hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> CurrentFingerprintAsync(IClipboard clipboard)
    {
        var files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 }) return Fingerprint([], files.Select(f => f.TryGetLocalPath() ?? "").ToArray());
        Dictionary<uint, byte[]> data = [];
        string? text = await clipboard.TryGetTextAsync();
        if (text is not null) data[RdpClipboardCodec.UnicodeText] = RdpClipboardCodec.EncodeText(text);
        using Bitmap? bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            RdpClipboardCodec.ValidateImageSize(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            data[RdpClipboardCodec.Png] = stream.ToArray();
            using var copy = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer pixels = copy.Lock();
            bitmap.CopyPixels(pixels);
            byte[] bgra = new byte[bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4];
            for (int y = 0; y < bitmap.PixelSize.Height; y++)
                Marshal.Copy(pixels.Address + y * pixels.RowBytes, bgra, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
            data[RdpClipboardCodec.Dib] = RdpClipboardCodec.EncodeDib(new(bitmap.PixelSize.Width, bitmap.PixelSize.Height, bgra));
        }
        return Fingerprint(data, []);
    }

    private static async Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return await action().ConfigureAwait(false);
        return await Dispatcher.UIThread.InvokeAsync(action);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _transport.ClipboardEvent -= OnEvent;
        _stop.Cancel();
        _events.Writer.TryComplete();
        _requests.Writer.TryComplete();
        lock (_requestGate) _formatResponse?.TrySetCanceled();
        foreach (var pending in _fileRequests.Values) pending.TrySetCanceled();
        await Task.WhenAll(_worker, _requestWorker).ConfigureAwait(false);
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await _localGate.WaitAsync().ConfigureAwait(false);
            try { _localFiles?.Dispose(); _localFiles = null; _localData.Clear(); }
            finally { _localGate.Release(); }
        }
        finally { _operations.Release(); }
    }
}
