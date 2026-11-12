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
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private CancellationTokenSource? _receiveCancellation;
    private CancellationTokenSource? _uploadCancellation;
    private RdpClipboardTransfer _download = new();
    private RdpClipboardTransfer _upload = new();
    private RdpClipboardTransfer _localProgress = new();
    private readonly Task _maintenance;
    private readonly IRdpVirtualFileClipboard? _virtualClipboard;
    private RdpVirtualFiles? _remoteFiles;
    private readonly ConcurrentDictionary<RdpVirtualFiles, byte> _remoteSources = new();
    private readonly ConcurrentBag<Task> _unlocks = [];
    private uint _clipDataId;
    internal RdpClipboardProgress? DownloadProgress => _download.Snapshot;
    internal RdpClipboardProgress? UploadProgress => _upload.Snapshot;
    private readonly SemaphoreSlim _localGate = new(1, 1);
    private readonly Lock _requestGate = new();
    private readonly Channel<RdpClipboardEvent> _events = Channel.CreateBounded<RdpClipboardEvent>(128);
    private readonly Channel<RdpClipboardEvent> _requests = Channel.CreateBounded<RdpClipboardEvent>(128);
    private readonly Task _requestWorker;
    private sealed record FileRequest(int Generation, uint Index, ulong Offset, uint Length, uint? Version, int Attempt, TaskCompletionSource<byte[]> Response);
    private readonly ConcurrentDictionary<uint, FileRequest> _fileRequests = new();
    private readonly TimeSpan _fileResponseTimeout;
    private readonly TimeSpan _fileRetryDelay;
    private readonly Task _worker;
    private TaskCompletionSource<byte[]>? _formatResponse;
    private uint _requestedFormat;
    private TaskCompletionSource? _formatDrained;
    private readonly TimeSpan _formatResponseTimeout;
    private readonly CancellationToken _stopToken;
    private bool _stopping;
    private Task? _disposeTask;
    private long _stream;
    private int _generation;
    private int _receivedFileGeneration = -1;
    private volatile bool _ready;
    private Dictionary<uint, string> _remoteFormats = [];
    private Dictionary<uint, byte[]> _localData = [];
    private RdpClipboardFiles? _localFiles;
    private uint? _localFilesSequence;
    private sealed record LocalVersion(RdpClipboardFiles Files, CancellationTokenSource Cancellation, RdpClipboardTransfer Progress);
    private readonly ConcurrentDictionary<uint, LocalVersion> _localVersions = new();
    private string? _lastRemoteFingerprint;
    private string? _lastPublishedFingerprint;

    internal RdpClipboardBridge(IRdpClipboardTransport transport, IPluginContext context, Control owner, bool enabled,
        Func<IRdpVirtualFileClipboard?>? virtualClipboardFactory = null, TimeSpan? fileResponseTimeout = null, TimeSpan? fileRetryDelay = null,
        TimeSpan? formatResponseTimeout = null)
    {
        _stopToken = _stop.Token;
        _formatResponseTimeout = formatResponseTimeout ?? TimeSpan.FromSeconds(30);
        _fileResponseTimeout = fileResponseTimeout ?? TimeSpan.FromSeconds(30);
        _fileRetryDelay = fileRetryDelay ?? TimeSpan.FromMilliseconds(500);
        _virtualClipboard = !enabled ? null : virtualClipboardFactory is not null ? virtualClipboardFactory()
            : OperatingSystem.IsWindows() ? new WindowsVirtualFileClipboard() : null;
        _transport = transport;
        _context = context;
        _owner = owner;
        _enabled = enabled;
        _worker = Task.Run(RunAsync);
        _requestWorker = Task.Run(ServeRequestsAsync);
        _maintenance = Task.Run(MaintainAsync);
        if (enabled)
        {
            _transport.ClipboardEvent += OnEvent;
        }
    }

    private void OnEvent(RdpClipboardEvent value)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        if (value.Kind == 10)
        {
            FailClipboard(new IOException(Encoding.UTF8.GetString(value.Data)));
            return;
        }
        if (value.Kind == 5)
        {
            lock (_requestGate)
            {
                if (_formatResponse is { } response && value.B == _requestedFormat)
                {
                    _formatResponse = null;
                    _ = (_formatDrained?.TrySetResult());
                    _formatDrained = null;
                    if (value.A != 0)
                    {
                        _ = response.TrySetResult(value.Data);
                    }
                    else
                    {
                        _ = response.TrySetException(new IOException("The remote clipboard rejected the format request."));
                    }
                }
            }
            return;
        }
        if (value.Kind == 7)
        {
            ushort flags = value.ResponseFlags ?? (value.B != 0 ? (ushort)1 : (ushort)2);
            if (_fileRequests.TryRemove(value.A, out FileRequest? request))
            {
                string detail = FileRequestDetail(value.A, request) + $" responseFlags=0x{flags:X4} responseLength={value.Data.Length}";
                _context.Log.Debug("RDP clipboard file response: " + detail);
                if (flags == 1)
                {
                    _ = request.Response.TrySetResult(value.Data);
                }
                else
                {
                    _context.Log.Warn("RDP clipboard file request rejected: " + detail);
                    _ = request.Response.TrySetException(flags == 2
                        ? new IOException("The remote clipboard rejected the file request (CB_RESPONSE_FAIL; no retry classification). " + detail)
                        : new InvalidDataException("Invalid clipboard file response flags. " + detail));
                }
            }
            else
            {
                _context.Log.Debug($"RDP clipboard ignored late/unmatched file response: streamId={value.A} responseFlags=0x{flags:X4} responseLength={value.Data.Length}");
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
                    if (parts.Length == 2 && uint.TryParse(parts[0], out uint id))
                    {
                        formats[id] = parts[1];
                    }
                }
            }
            lock (_requestGate)
            {
                if (_stopping)
                {
                    return;
                }

                _receiveCancellation?.Cancel();
                _remoteFiles?.Retire();
                _remoteFormats = formats;
                value = value with { A = unchecked((uint)Interlocked.Increment(ref _generation)) };
            }
        }
        ChannelWriter<RdpClipboardEvent> writer = value.Kind is 4 or 6 or 11 or 12 ? _requests.Writer : _events.Writer;
        if (!writer.TryWrite(value))
        {
            FailClipboard(new IOException("RDP clipboard queue overflow; clipboard sharing stopped for this connection."));
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (RdpClipboardEvent value in _events.Reader.ReadAllAsync(_stopToken).ConfigureAwait(false))
            {
                try
                {
                    switch (value.Kind)
                    {
                        case 2:
                            _ready = true;
                            await PublishLocalAsync(_stopToken).ConfigureAwait(false);
                            break;
                        case 3:
                            if (value.A != unchecked((uint)Volatile.Read(ref _generation)))
                            {
                                break;
                            }

                            await ReceiveAsync(true, _stopToken).ConfigureAwait(false);
                            break;
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _context.Log.Warn("RDP clipboard operation failed.", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ServeRequestsAsync()
    {
        try
        {
            await foreach (RdpClipboardEvent request in _requests.Reader.ReadAllAsync(_stopToken).ConfigureAwait(false))
            {
                try
                {
                    if (request.Kind is 11 or 12)
                    {
                        await HandleLocalLockAsync(request.A, request.Kind == 11).ConfigureAwait(false);
                    }
                    else if (request.Kind == 4)
                    {
                        await RespondFormatAsync(request.A).ConfigureAwait(false);
                    }
                    else
                    {
                        await RespondFileAsync(request).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _context.Log.Warn("RDP clipboard response failed.", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public Task PublishLocalAsync(CancellationToken ct = default) => Task.Run(() => PublishCoreAsync(ct), ct);

    private async Task PublishCoreAsync(CancellationToken ct)
    {
        if (!_enabled || !_ready || _stop.IsCancellationRequested)
        {
            return;
        }

        if (_virtualClipboard?.IsCurrent == true || OperatingSystem.IsWindows() && _virtualClipboard is WindowsVirtualFileClipboard { OwnsClipboard: true }) { return; }
        _remoteFiles?.Retire();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopToken);
        ct = linked.Token;
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        RdpClipboardFiles? files = null;
        CancellationTokenSource? preparing = null;
        try
        {
            (Dictionary<uint, byte[]> Data, string[] Paths) = await OnUiAsync(async () =>
            {
                IClipboard? clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
                Dictionary<uint, byte[]> data = [];
                string[] paths = [];
                if (clipboard is null)
                {
                    return (Data: data, Paths: paths);
                }

                IStorageItem[]? items = await clipboard.TryGetFilesAsync();
                if (items is { Length: > 0 })
                {
                    paths = [.. items.Select(item => item.TryGetLocalPath()
                        ?? throw new InvalidDataException("Only local clipboard files can be shared through RDP."))];
                    return (Data: data, Paths: paths);
                }
                string? text = await clipboard.TryGetTextAsync();
                if (text is not null)
                {
                    data[RdpClipboardCodec.UnicodeText] = RdpClipboardCodec.EncodeText(text);
                }

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
                    {
                        Marshal.Copy(pixels.Address + y * pixels.RowBytes, bgra, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
                    }

                    data[RdpClipboardCodec.Dib] = RdpClipboardCodec.EncodeDib(new(bitmap.PixelSize.Width, bitmap.PixelSize.Height, bgra));
                }
                return (Data: data, Paths: paths);
            }).ConfigureAwait(false);
            uint? localSequence = OperatingSystem.IsWindows() ? WindowsVirtualFileClipboard.Native.GetClipboardSequenceNumber() : null;
            string fingerprint = Fingerprint(Data, Paths);
            if (fingerprint == _lastRemoteFingerprint || fingerprint == _lastPublishedFingerprint)
            {
                return;
            }

            await _localGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RetireLocalFiles();
                _localData = [];
                lock (_requestGate)
                {
                    _receiveCancellation?.Cancel();
                    _uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopToken);
                    preparing = CancellationTokenSource.CreateLinkedTokenSource(ct, _uploadCancellation.Token);
                }
                _upload = _localProgress = new();
                if (Paths.Length > 0) { _upload.Start(0, "Preparing"); }
            }
            finally { _ = _localGate.Release(); }
            ct = preparing.Token;
            if (Paths.Length > 0)
            {
                files = await Task.Run(() => RdpClipboardFiles.Open(Paths, ct), ct).ConfigureAwait(false);
                Data[RdpClipboardCodec.FileGroup] = RdpClipboardCodec.EncodeFiles(files.Entries);
            }
            string formats;
            await _localGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (files is not null && localSequence is { } sequence && OperatingSystem.IsWindows()
                    && WindowsVirtualFileClipboard.Native.GetClipboardSequenceNumber() != sequence)
                {
                    throw new OperationCanceledException("Local clipboard changed while preparing file descriptions.", ct);
                }

                if (files is not null) { _upload.Start(files.Entries.Sum(f => f.Length), "Ready"); }
                _localFiles = files;
                _localFilesSequence = files is null ? null : localSequence;
                files = null;
                _localData = Data;
                formats = string.Concat(_localData.Keys.Select(id => $"{id}\t{FormatName(id)}\n"));
                if (_localFiles is not null)
                {
                    formats += $"{RdpClipboardCodec.FileContents}\tFileContents\n";
                }
            }
            finally { _ = _localGate.Release(); }
            await _transport.OfferFormatsAsync(formats, ct).ConfigureAwait(false);
            _lastPublishedFingerprint = fingerprint;
            _lastRemoteFingerprint = null;
        }
        catch (OperationCanceledException)
        {
            _upload.Finish("Cancelled");
            lock (_requestGate) { _uploadCancellation?.Cancel(); }
            throw;
        }
        catch (Exception ex)
        {
            _upload.Finish(ex is TimeoutException ? "TimedOut" : "Failed", ex.Message);
            lock (_requestGate) { _uploadCancellation?.Cancel(); }
            throw;
        }
        finally { preparing?.Dispose(); files?.Dispose(); _ = _operations.Release(); }
    }

    public Task ReceiveRemoteAsync(CancellationToken ct = default) => Task.Run(() => ReceiveAsync(true, ct), ct);

    private async Task ReceiveAsync(bool includeFiles, CancellationToken ct)
    {
        if (!_enabled || !_ready || _stop.IsCancellationRequested)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopToken);
        ct = linked.Token;
        await _receiveGate.WaitAsync(ct).ConfigureAwait(false);
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            int generation;
            Dictionary<uint, string> formats;
            lock (_requestGate)
            {
                ct.ThrowIfCancellationRequested();
                _stopToken.ThrowIfCancellationRequested();
                generation = _generation; formats = _remoteFormats;
                _receiveCancellation = receiving;
            }
            ct = receiving.Token;
            uint? localSequence = _virtualClipboard is WindowsVirtualFileClipboard && OperatingSystem.IsWindows()
                ? WindowsVirtualFileClipboard.Native.GetClipboardSequenceNumber() : null;
            uint fileFormat = formats.FirstOrDefault(p => p.Value == "FileGroupDescriptorW").Key;
            string? localFingerprint = _virtualClipboard is WindowsVirtualFileClipboard && fileFormat != 0 ? null : await OnUiAsync(async () =>
            {
                IClipboard? clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
                return clipboard is null ? null : await CurrentFingerprintAsync(clipboard);
            }).ConfigureAwait(false);
            if (fileFormat != 0)
            {
                if (includeFiles && generation != _receivedFileGeneration && _virtualClipboard is null)
                {
                    // No delayed file provider on this platform: say so without first locking the remote clipboard
                    // and fetching every descriptor only to discard them.
                    _download.Finish("Unsupported", force: true);
                    _receivedFileGeneration = generation;
                }
                else if (includeFiles && generation != _receivedFileGeneration)
                {
                    await WaitForFormatDrainAsync(ct).ConfigureAwait(false);
                    uint? version = null;
                    bool handedOff = false;
                    TaskCompletionSource? unlocked = null;
                    int released = 0;
                    void ReleaseVersion()
                    {
                        if (Interlocked.Exchange(ref released, 1) == 0 && unlocked is { } completion && version is { } id)
                        {
                            _ = ReleaseAsync(id, completion);
                        }
                    }
                    async Task ReleaseAsync(uint id, TaskCompletionSource completion)
                    {
                        try { await UnlockAsync(id).ConfigureAwait(false); }
                        finally { _ = completion.TrySetResult(); }
                    }
                    try
                    {
                        Task lockSend = Task.CompletedTask;
                        lock (_requestGate)
                        {
                            CheckReceive(generation, ct);
                            version = _transport.SupportsClipboardLock ? unchecked(++_clipDataId) : null;
                            _context.Log.Debug($"RDP clipboard descriptors: operation={generation} lockCapability={_transport.SupportsClipboardLock} clipDataId={version?.ToString() ?? "absent"}");
                            if (version is { } id)
                            {
                                // Track the entire lease before sending: disposal must also await a concurrent release callback.
                                unlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                                _unlocks.Add(unlocked.Task);
                                lockSend = _transport.LockClipboardAsync(id, true, ct);
                            }
                        }
                        await lockSend.ConfigureAwait(false);
                        if (version is { } lockedId)
                        {
                            _context.Log.Debug($"RDP clipboard LOCK sent: operation={generation} clipDataId={lockedId} (protocol has no lock ACK)");
                        }

                        CheckReceive(generation, ct);
                        byte[] descriptors = await RequestFormatAsync(fileFormat, ct).ConfigureAwait(false);
                        CheckReceive(generation, ct);
                        await ReceiveFilesAsync(RdpClipboardCodec.DecodeFiles(descriptors), generation, localFingerprint, localSequence, version, ReleaseVersion, ct).ConfigureAwait(false);
                        handedOff = true;
                    }
                    finally
                    {
                        if (!handedOff)
                        {
                            ReleaseVersion();
                        }
                    }
                }
                return;
            }
            uint pngFormat = formats.FirstOrDefault(p => p.Value == "PNG").Key;
            uint imageFormat = pngFormat != 0 ? pngFormat : formats.ContainsKey(RdpClipboardCodec.Dib)
                ? RdpClipboardCodec.Dib : formats.ContainsKey(RdpClipboardCodec.DibV5) ? RdpClipboardCodec.DibV5 : 0;
            string? text = null;
            byte[]? image = null;
            if (formats.ContainsKey(RdpClipboardCodec.UnicodeText))
            {
                text = RdpClipboardCodec.DecodeText(await RequestFormatAsync(RdpClipboardCodec.UnicodeText, ct).ConfigureAwait(false));
            }

            CheckReceive(generation, ct);
            if (imageFormat != 0)
            {
                image = await RequestFormatAsync(imageFormat, ct).ConfigureAwait(false);
            }

            CheckReceive(generation, ct);
            if (text is null && image is null)
            {
                return;
            }

            RdpClipboardImage? dib = null;
            if (image is not null)
            {
                if (pngFormat != 0)
                {
                    RdpClipboardCodec.ValidatePng(image);
                }
                else
                {
                    dib = RdpClipboardCodec.DecodeDib(image);
                }
            }
            _ = await OnUiAsync(async () =>
            {
                CheckReceive(generation, ct);
                IClipboard? clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
                if (clipboard is null)
                {
                    return false;
                }

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
                            {
                                Marshal.Copy(dib.Bgra, y * dib.Width * 4, pixels.Address + y * pixels.RowBytes, dib.Width * 4);
                            }
                        }
                        else
                        {
                            bitmap = new Bitmap(new MemoryStream(image, false));
                        }

                        using var encoded = new MemoryStream();
                        bitmap.Save(encoded, PngBitmapEncoderOptions.Default);
                        byte[] clipboardPng = encoded.ToArray();
                        // Each delayed clipboard read owns its bitmap; never expose a disposed shared instance.
                        item.Set(DataFormat.Bitmap, () => new Bitmap(new MemoryStream(clipboardPng, false)));
                    }
                    var transfer = new DataTransfer();
                    transfer.Add(item);
                    await CheckLocalClipboardAsync(clipboard, localFingerprint);
                    Task publication;
                    lock (_requestGate)
                    {
                        CheckReceive(generation, ct);
                        publication = clipboard.SetDataAsync(transfer);
                    }
                    await publication;
                    // Read the platform's normalized representation to suppress clipboard echo.
                    _lastRemoteFingerprint = await CurrentFingerprintAsync(clipboard);
                    _lastPublishedFingerprint = null;
                    return true;
                }
                finally { bitmap?.Dispose(); }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_download.Snapshot?.Active != true)
            {
                _download.Finish("Cancelled");
            }

            throw;
        }
        catch (Exception ex)
        {
            _download = new();
            _download.Finish(ex is TimeoutException ? "TimedOut" : "Failed", ex.Message, force: true);
            throw;
        }
        finally
        {
            lock (_requestGate) { if (ReferenceEquals(_receiveCancellation, receiving)) { _receiveCancellation = null; } }
            _ = _receiveGate.Release();
        }
    }

    private static async Task CheckLocalClipboardAsync(IClipboard clipboard, string? expected)
    {
        if (await CurrentFingerprintAsync(clipboard) != expected)
        {
            throw new IOException("The local clipboard changed while receiving remote clipboard content.");
        }
    }

    private async Task ReceiveFilesAsync(IReadOnlyList<RdpClipboardFile> files, int generation, string? localFingerprint, uint? localSequence, uint? version, Action releaseVersion, CancellationToken ct)
    {
        CheckReceive(generation, ct);
        if (_virtualClipboard is null)
        {
            _download.Finish("Unsupported", force: true);
            _receivedFileGeneration = generation;
            releaseVersion();
            return;
        }
        var progress = new RdpClipboardTransfer();
        RdpVirtualFiles? source = null;
        source = new RdpVirtualFiles(files, async (index, offset, length, token) =>
        {
            if (version is null && generation != Volatile.Read(ref _generation))
            {
                throw new IOException("The peer did not negotiate clipboard version locks and changed its file source.");
            }

            _download = progress;
            byte[] bytes = await RequestFileAsync(index, offset, length, version, generation, progress, token).ConfigureAwait(false);
            _download = progress;
            token.ThrowIfCancellationRequested();
            if (version is null)
            {
                CheckGeneration(generation);
            }

            return bytes;
        }, progress, _stopToken, () =>
        {
            if (source is not null)
            {
                _ = _remoteSources.TryRemove(source, out _);
            }

            releaseVersion();
        });
        try
        {
            if (_virtualClipboard is not WindowsVirtualFileClipboard)
            {
                _ = await OnUiAsync(async () =>
            {
                IClipboard clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard ?? throw new IOException("System clipboard is unavailable.");
                await CheckLocalClipboardAsync(clipboard, localFingerprint);
                CheckReceive(generation, ct);
                return true;
            }).ConfigureAwait(false);
            }

            Task publication;
            lock (_requestGate)
            {
                CheckReceive(generation, ct);
                _ = _remoteSources.TryAdd(source, 0);
                _remoteFiles?.Retire();
                _remoteFiles = source;
                _download = progress;
                progress.Start(files.Sum(f => f.Length), "Ready");
                publication = _virtualClipboard.PublishAsync(source, localSequence, ct);
            }
            await publication.ConfigureAwait(false);
            _receivedFileGeneration = generation;
            _lastPublishedFingerprint = null;
        }
        catch { source.Dispose(); throw; }
    }

    private async Task WaitForFormatDrainAsync(CancellationToken ct)
    {
        Task? drain;
        lock (_requestGate)
        {
            ct.ThrowIfCancellationRequested();
            _stopToken.ThrowIfCancellationRequested();
            drain = _formatDrained?.Task;
        }
        if (drain is null)
        {
            return;
        }

        try { await drain.WaitAsync(_formatResponseTimeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            _stopToken.ThrowIfCancellationRequested();
            FailClipboard(new IOException("The cancelled clipboard format response did not drain; reconnect to restore clipboard sharing."));
            throw new OperationCanceledException(_stopToken);
        }
    }

    private async Task<byte[]> RequestFormatAsync(uint format, CancellationToken ct)
    {
        await WaitForFormatDrainAsync(ct).ConfigureAwait(false);
        var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Task send;
            lock (_requestGate)
            {
                ct.ThrowIfCancellationRequested();
                _stopToken.ThrowIfCancellationRequested();
                // No wire request ID: retain this slot even if cancellation interrupts the send.
                _requestedFormat = format;
                _formatResponse = response;
                _formatDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                send = _transport.RequestFormatAsync(format, ct);
            }
            await send.ConfigureAwait(false);
            return await response.Task.WaitAsync(_formatResponseTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            // Finish the caller, but only the actual response may free the protocol slot.
            _ = response.TrySetCanceled();
            _ = response.Task.Exception;
            throw;
        }
    }

    private string FileRequestDetail(uint stream, FileRequest request) =>
        $"operation={request.Generation} streamId={stream} lindex={request.Index} offset={request.Offset} requestLength={request.Length} attempt={request.Attempt} lockCapability={_transport.SupportsClipboardLock} clipDataId={request.Version?.ToString() ?? "absent"}";

    private async Task<byte[]> RequestFileAsync(uint index, ulong offset, uint length, uint? version, int generation, RdpClipboardTransfer progress, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            _stopToken.ThrowIfCancellationRequested();
            if (version is null)
            {
                CheckGeneration(generation);
            }

            long next = Interlocked.Increment(ref _stream);
            // Never reuse an ID in this connection: an arbitrarily late response must stay unmatched.
            if (next > uint.MaxValue)
            {
                throw new IOException("Clipboard request identifiers exhausted; reconnect before another transfer.");
            }

            uint stream = (uint)next;
            var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new FileRequest(generation, index, offset, length, version, attempt, response);
            if (!_fileRequests.TryAdd(stream, request))
            {
                throw new IOException("Clipboard stream collision.");
            }

            bool retry = false;
            try
            {
                _context.Log.Debug("RDP clipboard file request: " + FileRequestDetail(stream, request));
                // A failed send has ambiguous delivery/connection state; only a completed send's response wait is retried.
                await _transport.RequestFileVersionAsync(stream, index, 2, offset, length, version, ct).ConfigureAwait(false);
                try { return await response.Task.WaitAsync(_fileResponseTimeout, ct).ConfigureAwait(false); }
                catch (TimeoutException) when (!ct.IsCancellationRequested && !_stop.IsCancellationRequested)
                {
                    _context.Log.Warn("RDP clipboard response timeout: " + FileRequestDetail(stream, request) + $" timeoutMs={_fileResponseTimeout.TotalMilliseconds} retry={attempt < 3}");
                    if (attempt >= 3)
                    {
                        throw;
                    }

                    retry = true;
                }
            }
            finally { _ = _fileRequests.TryRemove(stream, out _); }
            if (retry)
            {
                if (version is null)
                {
                    CheckGeneration(generation);
                }

                progress.Retry();
                await Task.Delay(_fileRetryDelay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task RespondFormatAsync(uint format)
    {
        await _localGate.WaitAsync(_stopToken).ConfigureAwait(false);
        try
        {
            bool found = _localData.TryGetValue(format, out byte[]? data);
            await _transport.RespondFormatAsync(data ?? [], found, _stopToken).ConfigureAwait(false);
            if (found && format == RdpClipboardCodec.FileGroup && _localProgress.Snapshot?.Total == 0)
            {
                _localProgress.Sent(0, 0, 0, "");
            }
        }
        finally { _ = _localGate.Release(); }
    }

    private async Task RespondFileAsync(RdpClipboardEvent request)
    {
        bool success = false;
        CancellationTokenSource? requestCancellation = null;
        RdpClipboardTransfer? requestProgress = null;
        await _localGate.WaitAsync(_stopToken).ConfigureAwait(false);
        try
        {
            if (request.Data.Length is not (16 or 20))
            {
                throw new InvalidDataException("Invalid clipboard file request.");
            }

            LocalVersion? version = null;
            if (request.Data.Length == 20 && !_localVersions.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(request.Data.AsSpan(16)), out version))
            {
                throw new InvalidDataException("Unknown clipboard version.");
            }

            RdpClipboardFiles? source = version?.Files ?? _localFiles;
            requestCancellation = version?.Cancellation ?? _uploadCancellation;
            CancellationToken ct = requestCancellation?.Token ?? _stopToken;
            RdpClipboardTransfer progress = requestProgress = version?.Progress ?? _localProgress;
            ct.ThrowIfCancellationRequested();
            if (version is null && LocalFileOwnershipChanged())
            {
                throw new OperationCanceledException("Local clipboard ownership changed.", ct);
            }

            if (source is null || request.B >= source.Entries.Count)
            {
                throw new InvalidDataException("Invalid clipboard file request.");
            }

            RdpClipboardFile file = source.Entries[(int)request.B];
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(request.Data);
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(request.Data.AsSpan(4));
            byte[] data;
            if (flags == 1 && !file.IsDirectory)
            {
                data = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(data, file.Length);
            }
            else if (flags == 2)
            {
                data = await source.ReadAsync(request.B, offset,
                    BinaryPrimitives.ReadUInt32LittleEndian(request.Data.AsSpan(12)), ct).ConfigureAwait(false);
            }
            else { throw new InvalidDataException("Invalid clipboard file request flags."); }
            ct.ThrowIfCancellationRequested();
            await _transport.RespondFileAsync(request.A, data, true, ct).ConfigureAwait(false);
            success = true;
            _upload = progress;
            progress.Touch();
            if (flags == 2) { progress.Sent(request.B, checked((long)offset), data.Length, file.Name); }
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { requestProgress?.Finish("Cancelled"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or UnauthorizedAccessException or TimeoutException)
        {
            requestProgress?.Finish(ex is TimeoutException ? "TimedOut" : "Failed", ex.Message);
            lock (_requestGate) { requestCancellation?.Cancel(); }
            _context.Log.Warn("RDP clipboard file request rejected.", ex);
        }
        finally { _ = _localGate.Release(); }
        if (!success) { await _transport.RespondFileAsync(request.A, [], false, _stopToken).ConfigureAwait(false); }
    }

    internal void CancelTransfer(bool upload)
    {
        lock (_requestGate)
        {
            if (upload) { _uploadCancellation?.Cancel(); foreach (LocalVersion version in _localVersions.Values) { version.Cancellation.Cancel(); } _upload.Finish("Cancelled"); }
            else { _receiveCancellation?.Cancel(); foreach (RdpVirtualFiles source in _remoteSources.Keys) { source.Dispose(); } _download.Finish("Cancelled"); }
        }
    }

    internal void StopTransfers()
    {
        lock (_requestGate)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;
            _stop.Cancel();
        }
        CancelTransfer(false);
        CancelTransfer(true);
    }

    private void FailClipboard(Exception error)
    {
        _context.Log.Warn("RDP clipboard sharing stopped for this connection.", error);
        StopTransfers();
        _download.Finish("Failed", error.Message, force: true);
        _upload.Finish("Failed", error.Message, force: true);
    }

    /// <summary>An upload that stops making progress for this long is ended as timed out.</summary>
    internal static readonly TimeSpan LocalTransferStallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A local file source that is prepared or delivered but unlocked is released after this long without a request,
    /// so its file handles do not keep the local files in use; the next paste through this window prepares it again.
    /// </summary>
    internal static readonly TimeSpan LocalSourceIdleRelease = TimeSpan.FromSeconds(60);

    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopToken).ConfigureAwait(false))
            {
                if (_receivedFileGeneration == Volatile.Read(ref _generation) && _virtualClipboard?.IsCurrent == false)
                {
                    _remoteFiles?.Retire();
                }

                if (_localProgress.IsStalled(LocalTransferStallTimeout, LocalSourceIdleRelease) && !IsLocalLocked(_localFiles))
                {
                    _localProgress.Finish("TimedOut");
                    lock (_requestGate) { _uploadCancellation?.Cancel(); }
                }
                await _localGate.WaitAsync(_stopToken).ConfigureAwait(false);
                try
                {
                    ReleaseCancelledLocalVersions();
                    bool cancelled;
                    lock (_requestGate)
                    {
                        if (LocalFileOwnershipChanged() && !IsLocalLocked(_localFiles)) { _uploadCancellation?.Cancel(); _upload.Finish("Cancelled"); }
                        cancelled = _uploadCancellation?.IsCancellationRequested == true;
                    }
                    if (cancelled)
                    {
                        RetireLocalFiles();
                        _ = _localData.Remove(RdpClipboardCodec.FileGroup);
                        _lastPublishedFingerprint = null;
                    }
                }
                finally { _ = _localGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            await _localGate.WaitAsync().ConfigureAwait(false);
            try { DisposeLocalVersions(); RetireLocalFiles(); _localData.Clear(); }
            finally { _ = _localGate.Release(); }
        }
    }

    private bool IsLocalLocked(RdpClipboardFiles? files) => files is not null && _localVersions.Values.Any(version => ReferenceEquals(version.Files, files));
    private void RetireLocalFiles()
    {
        lock (_requestGate)
        {
            if (!IsLocalLocked(_localFiles))
            {
                _localFiles?.Dispose();
                _uploadCancellation?.Cancel(); _uploadCancellation?.Dispose();
            }
            _localFiles = null; _uploadCancellation = null; _localFilesSequence = null;
        }
    }
    private void ReleaseCancelledLocalVersions()
    {
        lock (_requestGate)
        {
            foreach (LocalVersion? version in _localVersions.Values.Distinct().Where(v => v.Cancellation.IsCancellationRequested))
            {
                foreach (KeyValuePair<uint, LocalVersion> entry in _localVersions.Where(p => ReferenceEquals(p.Value.Files, version.Files)))
                {
                    _ = _localVersions.TryRemove(entry.Key, out _);
                }

                if (!ReferenceEquals(version.Files, _localFiles)) { version.Files.Dispose(); version.Cancellation.Dispose(); }
            }
        }
    }
    private void DisposeLocalVersions()
    {
        lock (_requestGate)
        {
            foreach (LocalVersion? version in _localVersions.Values.Distinct())
            {
                version.Cancellation.Cancel(); version.Files.Dispose();
                if (!ReferenceEquals(version.Cancellation, _uploadCancellation))
                {
                    version.Cancellation.Dispose();
                }
            }
            _localVersions.Clear();
        }
    }
    private async Task HandleLocalLockAsync(uint id, bool locked)
    {
        await _localGate.WaitAsync(_stopToken).ConfigureAwait(false);
        try
        {
            lock (_requestGate)
            {
                if (locked)
                {
                    if (!_transport.SupportsClipboardLock || _localVersions.ContainsKey(id) || _localVersions.Count >= 8)
                    {
                        throw new InvalidDataException("Invalid or excessive clipboard version locks.");
                    }

                    if (_localFiles is not null && _uploadCancellation is not null)
                    {
                        _ = _localVersions.TryAdd(id, new(_localFiles, _uploadCancellation, _localProgress));
                    }
                }
                else if (_localVersions.TryRemove(id, out LocalVersion? version) && !ReferenceEquals(version.Files, _localFiles) && !IsLocalLocked(version.Files))
                {
                    version.Files.Dispose(); version.Cancellation.Cancel(); version.Cancellation.Dispose();
                }
            }
        }
        finally { _ = _localGate.Release(); }
    }

    private async Task UnlockAsync(uint id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            _context.Log.Debug($"RDP clipboard UNLOCK: clipDataId={id}");
            await _transport.LockClipboardAsync(id, false, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested)
            {
                _context.Log.Warn("Could not release the remote clipboard version.", ex);
            }
        }
    }

    private bool LocalFileOwnershipChanged() => _localFiles is not null && _localFilesSequence is { } sequence
        && OperatingSystem.IsWindows() && WindowsVirtualFileClipboard.Native.GetClipboardSequenceNumber() != sequence;

    private void CheckReceive(int generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckGeneration(generation);
    }

    private void CheckGeneration(int generation)
    {
        _stopToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _generation))
        {
            throw new IOException("The remote clipboard changed during transfer.");
        }

        _stopToken.ThrowIfCancellationRequested();
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
        foreach (KeyValuePair<uint, byte[]> pair in data.OrderBy(p => p.Key))
        {
            hash.AppendData(BitConverter.GetBytes(pair.Key));
            hash.AppendData(pair.Value);
        }
        foreach (string path in paths.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0"));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> CurrentFingerprintAsync(IClipboard clipboard)
    {
        IStorageItem[]? files = await clipboard.TryGetFilesAsync();
        if (files is { Length: > 0 })
        {
            return Fingerprint([], [.. files.Select(f => f.TryGetLocalPath() ?? "")]);
        }

        Dictionary<uint, byte[]> data = [];
        string? text = await clipboard.TryGetTextAsync();
        if (text is not null)
        {
            data[RdpClipboardCodec.UnicodeText] = RdpClipboardCodec.EncodeText(text);
        }

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
            {
                Marshal.Copy(pixels.Address + y * pixels.RowBytes, bgra, y * bitmap.PixelSize.Width * 4, bitmap.PixelSize.Width * 4);
            }

            data[RdpClipboardCodec.Dib] = RdpClipboardCodec.EncodeDib(new(bitmap.PixelSize.Width, bitmap.PixelSize.Height, bgra));
        }
        return Fingerprint(data, []);
    }

    private static async Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (Dispatcher.UIThread.CheckAccess())
                {
                    return await action().ConfigureAwait(false);
                }

                return await Dispatcher.UIThread.InvokeAsync(action);
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800401D0) && attempt < 7)
            { await Task.Delay(25 * (attempt + 1)).ConfigureAwait(false); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_requestGate) { return new(_disposeTask ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        _transport.ClipboardEvent -= OnEvent;
        StopTransfers();
        _ = _events.Writer.TryComplete();
        _ = _requests.Writer.TryComplete();
        lock (_requestGate)
        {
            _ = (_formatResponse?.TrySetCanceled());
        }

        foreach (FileRequest pending in _fileRequests.Values)
        {
            _ = pending.Response.TrySetCanceled();
        }

        await Task.WhenAll(_worker, _requestWorker, _maintenance).ConfigureAwait(false);
        await _receiveGate.WaitAsync().ConfigureAwait(false);
        _ = _receiveGate.Release();
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await _localGate.WaitAsync().ConfigureAwait(false);
            try { DisposeLocalVersions(); RetireLocalFiles(); _localData.Clear(); }
            finally { _ = _localGate.Release(); }
        }
        finally { _ = _operations.Release(); }
        lock (_requestGate)
        {
            _uploadCancellation?.Dispose();
            _uploadCancellation = null;
        }
        if (_virtualClipboard is not null) { await _virtualClipboard.DisposeAsync().ConfigureAwait(false); }
        await Task.WhenAll([.. _unlocks]).ConfigureAwait(false);
        _stop.Dispose();
    }
}
