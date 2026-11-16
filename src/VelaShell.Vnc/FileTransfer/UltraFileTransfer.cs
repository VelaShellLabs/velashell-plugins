using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Threading.Channels;
using VelaShell.Vnc.Wire;

namespace VelaShell.Vnc.FileTransfer;

/// <summary>
/// UltraVNC file transfer (RFB message type 7), as UltraVNC Server and LibVNC (x11vnc <c>-ultrafilexfer</c>) implement it.
/// Every message is a 12-byte header — type 7, content type, content parameter, a pad byte (UltraVNC's Unicode flag
/// 0x80 lands here), u32 size, u32 length — then <c>length</c> body bytes; a file header (and a file offer) carries the
/// high 32 bits of the size in 4 more bytes. The protocol has no request ids, so one operation runs at a time and the
/// replies of the running one are routed to it; anything arriving while nothing runs is a leftover and dropped.
/// </summary>
internal sealed class UltraFileTransfer : IVncFileTransfer
{
    internal const byte MessageType = 7;
    internal const byte DirContentRequest = 1, DirPacket = 2, FileTransferRequest = 3, FileHeader = 4, FilePacket = 5,
        EndOfFile = 6, AbortFileTransfer = 7, FileTransferOffer = 8, FileAcceptHeader = 9, Command = 10, CommandReturn = 11,
        FileChecksums = 12, FileTransferAccess = 14, SessionStart = 15, SessionEnd = 16, ProtocolVersion = 17;
    internal const byte RDirContent = 1, RDrivesList = 2;
    internal const byte ADirectory = 1, ADrivesList = 3, ADirCreate = 4, AFileDelete = 7, AFileRename = 8, ADirInaccessible = 18;
    internal const byte CDirCreate = 1, CFileDelete = 4, CFileRename = 5;
    internal const byte UnicodeFlag = 0x80;   // high byte of UltraVNC's 16-bit content parameter
    internal const byte ClientVersion = 2;    // the LibVNC / UltraVNC "new method" access request
    internal const int DefaultBlockSize = 8192;
    private const int MaxBody = 2 * 1024 * 1024 + 1024, MaxEntries = 100_000;
    private const uint Refused = uint.MaxValue;

    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly Action<string>? _log;
    private readonly Func<CancellationToken, Task> _ensureSupported;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<Message> _inbox = Channel.CreateBounded<Message>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly TaskCompletionSource _versionKnown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _active, _granted, _accessPending, _unsettled;
    private volatile int _serverVersion, _blockSize = DefaultBlockSize;

    internal UltraFileTransfer(Func<byte[], CancellationToken, Task> send, Action<string>? log, Func<CancellationToken, Task> ensureSupported)
    {
        _send = send;
        _log = log;
        _ensureSupported = ensureSupported;
    }

    /// <summary>How long to wait for each reply.</summary>
    internal TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long access may take before it counts as refused (UltraVNC does not answer a view-only session).</summary>
    internal TimeSpan AccessTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait after access for UltraVNC's protocol-version message (block size, Unicode names).</summary>
    internal TimeSpan VersionGrace { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the line must stay silent after a failed operation before the next one starts.</summary>
    internal TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest that settling may take (a server still streaming an aborted file).</summary>
    internal TimeSpan QuietLimit { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsGranted => _granted;

    /// <summary>An access request is waiting for its reply (a server closing now does not know file transfer).</summary>
    internal bool AccessPending => _accessPending;

    internal int BlockSize => _blockSize;
    private bool Unicode => _serverVersion >= 4;

    // ------------------------------------------------------------ receive side (called by the session's receive loop)

    /// <summary>Reads one file-transfer message after its type byte and routes it.</summary>
    internal async ValueTask ReceiveAsync(RfbReader reader, CancellationToken ct)
    {
        byte contentType = await reader.U8Async(ct).ConfigureAwait(false);
        byte param = await reader.U8Async(ct).ConfigureAwait(false);
        byte pad = await reader.U8Async(ct).ConfigureAwait(false);
        uint size = await reader.U32Async(ct).ConfigureAwait(false);
        uint length = await reader.U32Async(ct).ConfigureAwait(false);
        if (length > MaxBody)
        {
            throw new VncProtocolException($"The server sent a {length}-byte file-transfer message.");
        }

        byte[] body = await reader.BytesAsync((int)length, ct).ConfigureAwait(false);
        // A file header for a file that could be opened is followed by the high 32 bits of its size. A refusal also
        // has size -1 but echoes only the path, while an opened file's body ends in ",MM/DD/YYYY HH:MM" — which tells
        // the two apart even for a file whose low size word happens to be 0xFFFFFFFF.
        bool opened = contentType == FileHeader && (size != Refused || HasTimestamp(body));
        uint high = opened ? await reader.U32Async(ct).ConfigureAwait(false) : 0;

        if (contentType == ProtocolVersion)
        {
            _serverVersion = param;
            if (param >= 3 && size is >= 4096 and <= 1024 * 1024) { _blockSize = (int)size; }
            _ = _versionKnown.TrySetResult();
            return;
        }
        if (!_active) { return; }
        await _inbox.Writer.WriteAsync(new Message(contentType, param, pad, size, high, body, opened), ct).ConfigureAwait(false);
    }

    /// <summary>The session ended: running and later operations fail.</summary>
    internal void Close(Exception? reason) =>
        _inbox.Writer.TryComplete(reason ?? new VncFileTransferException(VncFileTransferError.Disconnected, "The VNC session ended."));

    // ------------------------------------------------------------ operations

    public Task<bool> RequestAccessAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        if (_granted) { return true; }
        await _ensureSupported(token).ConfigureAwait(false);
        _accessPending = true;
        Message reply;
        try
        {
            // The access request is an AbortFileTransfer with a non-zero version; version 0 is the legacy handshake both
            // servers refuse. The reply is FileTransferAccess with size 1 (granted) or -1.
            await SendAsync(Header(AbortFileTransfer, ClientVersion, 0, 0, []), token).ConfigureAwait(false);
            reply = await ExpectAsync(static m => m.Type is FileTransferAccess or AbortFileTransfer, AccessTimeout, token).ConfigureAwait(false);
        }
        catch (VncFileTransferException failure) when (failure.Error == VncFileTransferError.Timeout)
        {
            return false;   // UltraVNC does not answer a view-only session at all
        }
        finally { _accessPending = false; }

        _granted = reply.Type == FileTransferAccess && (int)reply.Size > 0;
        if (!_granted) { return false; }

        // UltraVNC follows up with its protocol version (block size, Unicode names); LibVNC sends none.
        try { await _versionKnown.Task.WaitAsync(VersionGrace, token).ConfigureAwait(false); }
        catch (TimeoutException) { }
        if (_serverVersion >= 3) { await SendAsync(Header(SessionStart, 0, 0, 0, []), token).ConfigureAwait(false); }
        _log?.Invoke($"VNC file transfer granted (server version {_serverVersion}, block {_blockSize} bytes{(Unicode ? ", Unicode names" : "")}).");
        return true;
    }, ct, requireAccess: false);

    public Task<IReadOnlyList<VncRemoteDrive>> ListDrivesAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        await SendAsync(Header(DirContentRequest, RDrivesList, 0, 0, []), token).ConfigureAwait(false);
        Message reply = await ExpectAsync(static m => m.Type == DirPacket, ReplyTimeout, token).ConfigureAwait(false);
        return (IReadOnlyList<VncRemoteDrive>)ParseDrives(reply.Body);
    }, ct);

    public Task<IReadOnlyList<VncRemoteEntry>> ListAsync(string directory, CancellationToken ct = default) => RunAsync(async token =>
    {
        string path = directory.EndsWith('\\') ? directory : directory + "\\";
        await SendAsync(Header(DirContentRequest, RDirContent, Unicode ? UnicodeFlag : (byte)0, 0, Name(path)), token).ConfigureAwait(false);

        // The first packet echoes the path; an empty echo (LibVNC) or "inaccessible" (UltraVNC) means it cannot be read.
        Message echo = await ExpectAsync(static m => m.Type == DirPacket, ReplyTimeout, token).ConfigureAwait(false);
        if (echo.Param == ADirInaccessible || (echo.Param == ADirectory && echo.Body.Length == 0))
        {
            throw new VncFileTransferException(VncFileTransferError.Unavailable, $"The server cannot list {path}.");
        }

        var entries = new List<VncRemoteEntry>();
        if (echo.Param != 0)
        {
            while (true)
            {
                Message packet = await ExpectAsync(static m => m.Type == DirPacket, ReplyTimeout, token).ConfigureAwait(false);
                if (packet.Param == 0) { break; }   // end of the listing
                if (ParseEntry(packet.Body, (packet.Pad & UnicodeFlag) != 0) is { } entry && entry.Name is not ("." or ".."))
                {
                    entries.Add(entry);
                }
                if (entries.Count > MaxEntries)
                {
                    throw new VncProtocolException($"The server listed more than {MaxEntries} entries in one directory.");
                }
            }
        }

        entries.Sort(static (a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return (IReadOnlyList<VncRemoteEntry>)entries;
    }, ct);

    public Task<long> DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default) => RunAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(destination);
        // size 1 asks for zlib-compressed packets (each sent compressed only when that is smaller).
        await SendAsync(Header(FileTransferRequest, 0, 0, 1, Name(remotePath)), token).ConfigureAwait(false);
        Message header = await ExpectAsync(static m => m.Type is FileHeader or AbortFileTransfer, ReplyTimeout, token).ConfigureAwait(false);
        if (header.Type != FileHeader || !header.Opened)
        {
            throw new VncFileTransferException(VncFileTransferError.Unavailable, $"The server cannot read {remotePath}.");
        }

        long expected = (long)header.High << 32 | header.Size;
        long received = 0;
        await SendAsync(Header(FileHeader, 0, 0, 0, []), token).ConfigureAwait(false);   // go ahead
        try
        {
            while (true)
            {
                Message packet = await ExpectAsync(static m => m.Type is FilePacket or EndOfFile or AbortFileTransfer, ReplyTimeout, token).ConfigureAwait(false);
                if (packet.Type == EndOfFile) { break; }
                if (packet.Type == AbortFileTransfer)
                {
                    throw new VncFileTransferException(VncFileTransferError.Failed, $"The server aborted the transfer of {remotePath}.");
                }

                byte[] data = packet.Size == 1 ? Inflate(packet.Body) : packet.Body;
                await destination.WriteAsync(data, token).ConfigureAwait(false);
                received += data.Length;
                progress?.Report(received);
            }
        }
        catch (Exception ex) when (ex is not VncFileTransferException { Error: VncFileTransferError.Disconnected })
        {
            // Cancelled, a local write failed, a timeout or a corrupt packet: stop the server sending the rest.
            await AbortQuietlyAsync().ConfigureAwait(false);
            throw;
        }

        // LibVNC's header holds only 32 bits of the size (its high word is always 0), so only a short file is an error.
        if (received < expected)
        {
            throw new VncFileTransferException(VncFileTransferError.Failed, $"The transfer of {remotePath} ended after {received} of {expected} bytes.");
        }
        return received;
    }, ct);

    public Task UploadAsync(Stream source, long length, DateTimeOffset modified, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default) => RunAsync(async token =>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        // "path,MM/DD/YYYY HH:MM" (both servers split at the last comma), then the high 32 bits of the size.
        byte[] name = Name(remotePath + "," + modified.LocalDateTime.ToString("MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture));
        byte[] offer = new RfbWriter(16 + name.Length).U8(MessageType).U8(FileTransferOffer).U8(0).U8(0)
            .U32((uint)length).U32((uint)name.Length).Bytes(name).U32((uint)(length >> 32)).ToArray();
        await SendAsync(offer, token).ConfigureAwait(false);
        // UltraVNC may send block checksums of an existing file first; they are only of use for resuming, so skipped.
        Message accept = await ExpectAsync(static m => m.Type == FileAcceptHeader, ReplyTimeout, token).ConfigureAwait(false);
        if (accept.Size == Refused)
        {
            throw new VncFileTransferException(VncFileTransferError.Unavailable, $"The server cannot create {remotePath}.");
        }

        byte[] block = new byte[_blockSize];
        long sent = 0;
        try
        {
            // Exactly the size offered: the server allocated and expects that many bytes.
            while (sent < length)
            {
                int want = (int)Math.Min(block.Length, length - sent);
                int read = await source.ReadAtLeastAsync(block.AsMemory(0, want), want, throwOnEndOfStream: false, token).ConfigureAwait(false);
                if (read < want)
                {
                    throw new IOException($"The local file ended after {sent + read} of {length} bytes.");
                }
                byte[] compressed = Deflate(block.AsSpan(0, read));
                byte[] packet = compressed.Length < read
                    ? Header(FilePacket, 0, 0, 1, compressed)
                    : Header(FilePacket, 0, 0, 0, block.AsSpan(0, read));
                await SendAsync(packet, token).ConfigureAwait(false);
                sent += read;
                progress?.Report(sent);
            }
            await SendAsync(Header(EndOfFile, 0, 0, 0, []), token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not VncFileTransferException { Error: VncFileTransferError.Disconnected })
        {
            await AbortQuietlyAsync().ConfigureAwait(false);
            throw;
        }
        return true;
    }, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct = default) => CommandAsync(CDirCreate, ADirCreate, path, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) => CommandAsync(CFileDelete, AFileDelete, path, ct);

    public Task RenameAsync(string path, string newPath, CancellationToken ct = default)
    {
        if (path.Contains('*') || newPath.Contains('*'))
        {
            throw new ArgumentException("File names for a rename cannot contain '*'.");   // the old and new names are joined by '*'
        }
        return CommandAsync(CFileRename, AFileRename, path + "*" + newPath, ct);
    }

    /// <summary>A command answered by CommandReturn with the matching parameter; size 0 is success, -1 failure.</summary>
    private Task<bool> CommandAsync(byte command, byte reply, string body, CancellationToken ct) => RunAsync(async token =>
    {
        await SendAsync(Header(Command, command, 0, 0, Name(body)), token).ConfigureAwait(false);
        Message result = await ExpectAsync(m => m.Type == CommandReturn && m.Param == reply, ReplyTimeout, token).ConfigureAwait(false);
        if (result.Size != 0)
        {
            throw new VncFileTransferException(VncFileTransferError.Failed, $"The server could not {(command == CDirCreate ? "create" : command == CFileDelete ? "delete" : "rename")} {body.Replace("*", " → ", StringComparison.Ordinal)}.");
        }
        return true;
    }, ct);

    // ------------------------------------------------------------ plumbing

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct, bool requireAccess = true)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (requireAccess && !_granted)
            {
                throw new VncFileTransferException(VncFileTransferError.Denied, "File transfer access has not been granted.");
            }
            while (_inbox.Reader.TryRead(out _)) { }   // leftovers of an earlier operation
            _active = true;
            if (_unsettled)
            {
                // The last operation failed part-way (timeout, cancel, error): the rest of its replies may still be coming,
                // and with no request ids they would pass for this operation's. Discard until the server goes quiet.
                await DrainUntilQuietAsync(ct).ConfigureAwait(false);
                _unsettled = false;
            }
            return await operation(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not VncFileTransferException { Error: VncFileTransferError.Disconnected or VncFileTransferError.Denied or VncFileTransferError.Unsupported })
        {
            _unsettled = true;
            throw;
        }
        finally
        {
            _active = false;
            while (_inbox.Reader.TryRead(out _)) { }
            _ = _gate.Release();
        }
    }

    private async Task DrainUntilQuietAsync(CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + (long)QuietLimit.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quiet.CancelAfter(QuietPeriod);
            try { _ = await _inbox.Reader.ReadAsync(quiet.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
            catch (ChannelClosedException) { return; }
        }
    }

    private async Task<Message> ExpectAsync(Func<Message, bool> match, TimeSpan timeout, CancellationToken ct)
    {
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            while (true)
            {
                Message message = await _inbox.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
                if (match(message)) { return message; }
            }
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new VncFileTransferException(VncFileTransferError.Timeout, "The server did not answer the file-transfer request.");
        }
        catch (ChannelClosedException ex)
        {
            throw ex.InnerException as VncFileTransferException
                ?? new VncFileTransferException(VncFileTransferError.Disconnected, "The VNC session ended.", ex.InnerException);
        }
    }

    private async Task AbortQuietlyAsync()
    {
        try { await _send(Header(AbortFileTransfer, 0, 0, 0, []), CancellationToken.None).ConfigureAwait(false); }
        catch { /* the session is going away anyway */ }
    }

    /// <summary>Sends one message; a session that is gone surfaces as <see cref="VncFileTransferError.Disconnected"/>.</summary>
    private async Task SendAsync(byte[] message, CancellationToken ct)
    {
        // Cancellation is honoured between messages only: a write cancelled part-way would leave half a packet on the
        // wire (and an SslStream unusable), and the server would then read garbage and drop the whole session.
        ct.ThrowIfCancellationRequested();
        try { await _send(message, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException or System.Net.Sockets.SocketException)
        {
            throw new VncFileTransferException(VncFileTransferError.Disconnected, "The VNC session ended.", ex);
        }
    }

    internal static byte[] Header(byte contentType, byte param, byte pad, uint size, ReadOnlySpan<byte> body) =>
        new RfbWriter(12 + body.Length).U8(MessageType).U8(contentType).U8(param).U8(pad).U32(size).U32((uint)body.Length).Bytes(body).ToArray();

    /// <summary>Paths go out as UTF-8: what UltraVNC expects with Unicode names and what Unix file systems use.</summary>
    private static byte[] Name(string path) => Encoding.UTF8.GetBytes(path);

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Drive list: 4 bytes per drive — letter, ':', kind, NUL ("C:l\0" is all LibVNC sends).</summary>
    internal static List<VncRemoteDrive> ParseDrives(byte[] body)
    {
        var drives = new List<VncRemoteDrive>();
        for (int i = 0; i + 2 < body.Length && body[i] != 0; i += 4)
        {
            if (body[i + 1] != ':') { throw new VncProtocolException("The server sent a malformed drive list."); }
            drives.Add(new VncRemoteDrive($"{(char)body[i]}:\\", (char)body[i + 2]));
        }
        return drives;
    }

    /// <summary>
    /// A WIN32_FIND_DATA record, little-endian: attributes (0), creation / access / write FILETIMEs (4, 12, 20), size high
    /// (28) and low (32), two reserved words, then the NUL-terminated name at 44 — ANSI bytes, or UTF-16 when the
    /// Unicode flag is set. LibVNC stores Unix seconds in the low word of the times instead of a FILETIME.
    /// </summary>
    internal static VncRemoteEntry? ParseEntry(byte[] record, bool unicode)
    {
        const int nameOffset = 44;
        if (record.Length <= nameOffset) { return null; }
        ReadOnlySpan<byte> span = record;
        uint attributes = BinaryPrimitives.ReadUInt32LittleEndian(span);
        uint timeLow = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]), timeHigh = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
        long size = (long)BinaryPrimitives.ReadUInt32LittleEndian(span[28..]) << 32 | BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
        ReadOnlySpan<byte> raw = span[nameOffset..];
        string name;
        if (unicode)
        {
            int end = 0;
            while (end + 1 < raw.Length && (raw[end] | raw[end + 1]) != 0) { end += 2; }
            name = Encoding.Unicode.GetString(raw[..end]);
        }
        else
        {
            int end = raw.IndexOf((byte)0);
            name = Text(end < 0 ? raw : raw[..end]);
        }
        if (name.Length == 0) { return null; }

        DateTimeOffset? modified = null;
        try
        {
            if (timeHigh != 0) { modified = DateTimeOffset.FromFileTime((long)timeHigh << 32 | timeLow); }
            else if (timeLow != 0) { modified = DateTimeOffset.FromUnixTimeSeconds(timeLow); }
        }
        catch (ArgumentOutOfRangeException) { }

        return new VncRemoteEntry(name, (attributes & 0x10) != 0, size, modified);
    }

    /// <summary>A packet compressed with zlib's compress(): at most a block (UltraVNC allows up to 1 MiB) when inflated.</summary>
    private static byte[] Inflate(byte[] compressed)
    {
        try
        {
            using var inflater = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int read;
            while ((read = inflater.Read(chunk)) > 0)
            {
                if (output.Length + read > 1024 * 1024)
                {
                    throw new VncProtocolException("A compressed file-transfer packet inflated to more than 1 MiB.");
                }
                output.Write(chunk, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new VncProtocolException("The server sent a corrupt compressed file-transfer packet.", ex);
        }
    }

    private static byte[] Deflate(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream(data.Length);
        using (var deflater = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflater.Write(data);
        }
        return output.ToArray();
    }

    internal readonly record struct Message(byte Type, byte Param, byte Pad, uint Size, uint High, byte[] Body, bool Opened = false);

    /// <summary>Whether a file header's body ends in the ",MM/DD/YYYY HH:MM" both servers append for an opened file.</summary>
    internal static bool HasTimestamp(byte[] body) =>
        body.Length >= 17 && MatchesStamp(body.AsSpan(body.Length - 17));

    private static bool MatchesStamp(ReadOnlySpan<byte> s)
    {
        const string shape = ",00/00/0000 00:00";   // '0' stands for any digit
        for (int i = 0; i < shape.Length; i++)
        {
            bool ok = shape[i] == '0' ? s[i] is >= (byte)'0' and <= (byte)'9' : s[i] == shape[i];
            if (!ok) { return false; }
        }
        return true;
    }
}
