namespace VelaShell.Vnc;

/// <summary>A rectangle of the desktop in pixels (top-left origin).</summary>
public readonly record struct VncRegion(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// The result of <see cref="IVncSession.CopyFrame"/>: the desktop size, the region copied (empty when nothing changed),
/// and whether the desktop no longer matches the caller's surface — then nothing was copied and
/// <see cref="Width"/>/<see cref="Height"/> are the new size.
/// </summary>
public readonly record struct VncFrameCopy(int Width, int Height, VncRegion Changed, bool SizeChanged);

/// <summary>Mouse buttons in RFB PointerEvent button-mask order (RFC 6143 §7.5.5).</summary>
[Flags]
public enum VncButtons : byte
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
    WheelUp = 8,
    WheelDown = 16,
    WheelLeft = 32,
    WheelRight = 64
}

/// <summary>
/// The remote cursor (Cursor pseudo-encoding): a top-down BGRA32 image with straight alpha and its hot spot. An empty
/// image means the server hides the cursor.
/// </summary>
public sealed class VncPointer(int width, int height, int hotspotX, int hotspotY, byte[] bgra)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int HotspotX { get; } = hotspotX;
    public int HotspotY { get; } = hotspotY;
    public byte[] Bgra { get; } = bgra;
    public bool IsHidden => Width == 0 || Height == 0;
}

/// <summary>What the session negotiated, for the status bar and logs.</summary>
public sealed record VncSessionInfo(string ProtocolVersion, string Security, string DesktopName, bool Tls);

/// <summary>
/// The engine contract the desktop view, input queue and clipboard depend on, implemented by <see cref="VncSession"/>.
/// Events are raised on the session's receive thread; marshal to the UI thread before touching UI objects.
/// </summary>
public interface IVncSession : IAsyncDisposable
{
    /// <summary>A framebuffer update finished; call <see cref="CopyFrame"/>.</summary>
    event Action? FrameAvailable;

    /// <summary>
    /// The session ended without being disposed: <see cref="VncConnectionLostException"/> when the network dropped,
    /// another exception for a protocol failure, or null for a clean close by the server.
    /// </summary>
    event Action<Exception?>? Disconnected;

    event Action<VncPointer>? PointerChanged;

    /// <summary>The server's clipboard text changed (ServerCutText or an extended-clipboard Provide).</summary>
    event Action<string>? ClipboardReceived;

    /// <summary>The server's desktop name changed (DesktopName pseudo-encoding).</summary>
    event Action? InfoChanged;

    bool IsConnected { get; }
    VncSessionInfo Info { get; }

    /// <summary>The name of the encoding of the last rectangle (for the status bar), e.g. "Tight".</summary>
    string LastEncoding { get; }

    /// <summary>The server takes SetDesktopSize (it answered the ExtendedDesktopSize pseudo-encoding).</summary>
    bool CanResize { get; }

    /// <summary>The server takes QEMU extended key events, so physical scan codes reach it layout-independently.</summary>
    bool SupportsScanCodes { get; }

    /// <summary>The server confirmed the QEMU Audio extension and its audio is being played.</summary>
    bool AudioActive { get; }

    /// <summary>
    /// Audio playback stopped (the output device failed); the desktop stays connected. Raised on a background thread; a
    /// failure that happened before subscribing is replayed to the new handler.
    /// </summary>
    event Action<Exception>? AudioFailed;

    /// <summary>UltraVNC file transfer over this connection, or null when <see cref="VncClientOptions.FileTransfer"/> is off.</summary>
    IVncFileTransfer? FileTransfer { get; }

    /// <summary>
    /// Copies what changed since the last copy into a caller-owned, top-down BGRA32 surface of
    /// <paramref name="width"/>×<paramref name="height"/> pixels with a row pitch of <paramref name="stride"/> bytes.
    /// <paramref name="full"/> copies everything. When the desktop is no longer that size nothing is copied and
    /// <see cref="VncFrameCopy.SizeChanged"/> is set.
    /// </summary>
    VncFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full = false);

    /// <summary>
    /// A key press or release. <paramref name="keysym"/> is the X11 keysym; <paramref name="scanCode"/> the XT scan code
    /// of the physical key (0x1xx for E0-prefixed keys), or 0 when the key has none (typed text). The scan code is
    /// only sent when <see cref="SupportsScanCodes"/>.
    /// </summary>
    Task KeyAsync(uint keysym, uint scanCode, bool down, CancellationToken ct = default);

    /// <summary>Pointer position and the buttons held, in framebuffer pixels.</summary>
    Task PointerAsync(VncButtons buttons, int x, int y, CancellationToken ct = default);

    /// <summary>Offers local clipboard text to the server.</summary>
    Task SendClipboardAsync(string text, CancellationToken ct = default);

    /// <summary>Asks the server to resize its desktop (SetDesktopSize); a no-op unless <see cref="CanResize"/>.</summary>
    Task RequestResizeAsync(int width, int height, CancellationToken ct = default);

    /// <summary>
    /// Whether the desktop is shown. While hidden no further updates are requested (no bandwidth, no decoding); showing
    /// it again requests the changes accumulated meanwhile.
    /// </summary>
    Task SetDisplayUpdatesAsync(bool visible, CancellationToken ct = default);
}

/// <summary>An entry of a remote directory listing. <see cref="Modified"/> is null when the server gave no time.</summary>
public sealed record VncRemoteEntry(string Name, bool IsDirectory, long Size, DateTimeOffset? Modified);

/// <summary>A remote drive root such as <c>C:\</c> (LibVNC servers map <c>C:\</c> to <c>/</c>). <see cref="Kind"/> is
/// 'l' fixed, 'f' removable, 'c' CD-ROM or 'n' network.</summary>
public sealed record VncRemoteDrive(string Path, char Kind);

/// <summary>
/// UltraVNC file transfer (RFB message type 7) on the session's connection. Paths are Windows-style (<c>C:\dir\file</c>).
/// One operation runs at a time; the others wait. Failures are <see cref="VncFileTransferException"/>; cancelling a
/// transfer aborts it on the server.
/// </summary>
public interface IVncFileTransfer
{
    /// <summary>Access was granted by <see cref="RequestAccessAsync"/>.</summary>
    bool IsGranted { get; }

    /// <summary>
    /// Asks the server for file-transfer permission (once per connection). Returns false when the server refuses.
    /// Missing compatible capabilities fail locally with <see cref="VncFileTransferError.Unsupported"/>; no transfer
    /// message is sent. Explicit legacy opt-in can still cause an incompatible server to close the desktop connection.
    /// </summary>
    Task<bool> RequestAccessAsync(CancellationToken ct = default);

    Task<IReadOnlyList<VncRemoteDrive>> ListDrivesAsync(CancellationToken ct = default);

    /// <summary>Lists a directory, folders first; "." and ".." are left out.</summary>
    Task<IReadOnlyList<VncRemoteEntry>> ListAsync(string directory, CancellationToken ct = default);

    /// <summary>Copies a remote file into <paramref name="destination"/>; returns the bytes received.</summary>
    Task<long> DownloadAsync(string remotePath, Stream destination, IProgress<long>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Creates (or truncates) <paramref name="remotePath"/> from <paramref name="source"/>. An UltraVNC server resumes onto
    /// an existing file of that name, so delete it first to replace it.
    /// </summary>
    Task UploadAsync(Stream source, long length, DateTimeOffset modified, string remotePath, IProgress<long>? progress = null, CancellationToken ct = default);

    Task CreateDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>Deletes a file or a directory (UltraVNC deletes a directory with its contents; LibVNC only an empty one).</summary>
    Task DeleteAsync(string path, CancellationToken ct = default);

    Task RenameAsync(string path, string newPath, CancellationToken ct = default);
}
