namespace VelaShell.Rdp;

/// <summary>A decoded desktop frame handed to the UI: tightly packed BGRA32, alpha 255.</summary>
public sealed record RdpFrame(int Width, int Height, byte[] Pixels);

/// <summary>A rectangle of the desktop in pixels (top-left origin).</summary>
public readonly record struct RdpFrameRegion(int X, int Y, int Width, int Height)
{
    /// <summary>True when the region covers no pixels.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// The result of <see cref="IRdpSession.CopyFrame"/>: the desktop size, the region that was copied (empty when
/// nothing changed), and whether the desktop no longer matches the caller's surface — then nothing was copied and
/// <see cref="Width"/>/<see cref="Height"/> are the new size.
/// </summary>
public readonly record struct RdpFrameCopy(int Width, int Height, RdpFrameRegion Changed, bool SizeChanged);

/// <summary>
/// The engine contract a desktop view, input queue and clipboard bridge depend on, implemented by
/// <see cref="RdpManagedSession"/>. No wire or platform-UI types cross this boundary.
/// </summary>
public interface IRdpSession : IRdpClipboardTransport, IAsyncDisposable
{
    event Action? FrameAvailable;

    /// <summary>
    /// The session ended without being disposed. The cause is <see cref="RdpConnectionLostException"/> when the network
    /// dropped (connecting again normally resumes the remote session), <see cref="RdpDisconnectException"/> when the
    /// server ended it and said why, another exception for a protocol or channel failure, or null for a clean end.
    /// </summary>
    event Action<Exception?>? Disconnected;
    bool IsConnected { get; }

    /// <summary>A full copy of the desktop, or null when nothing changed. Allocates W×H×4 bytes per call — prefer <see cref="CopyFrame"/>.</summary>
    RdpFrame? TakeFrame();

    /// <summary>
    /// Copies the part of the desktop that changed since the last copy into a caller-owned, top-down BGRA32 surface of
    /// <paramref name="width"/>×<paramref name="height"/> pixels with a row pitch of <paramref name="stride"/> bytes
    /// (e.g. a locked UI bitmap) — no allocation, and only the changed rows move. <paramref name="full"/> copies
    /// everything (after creating a new surface). When the desktop is no longer that size nothing is copied and
    /// <see cref="RdpFrameCopy.SizeChanged"/> is set: recreate the surface at the returned size and call again with
    /// <paramref name="full"/> = true.
    /// </summary>
    RdpFrameCopy CopyFrame(Span<byte> destination, int stride, int width, int height, bool full = false);
    Task KeyAsync(uint code, bool down, CancellationToken ct = default);
    Task UnicodeAsync(string text, CancellationToken ct = default);
    Task MouseAsync(uint flags, uint x, uint y, CancellationToken ct = default);

    /// <summary>
    /// Requests that the remote desktop resize to <paramref name="width"/>×<paramref name="height"/>
    /// device pixels so it matches the workspace tab. A no-op when the server does not offer the
    /// display-control channel; the desktop then keeps its negotiated size and is scaled to fit.
    /// </summary>
    Task RequestResizeAsync(int width, int height, CancellationToken ct = default);

    /// <summary>
    /// <see cref="RequestResizeAsync(int, int, CancellationToken)"/> with the local display scale in percent (100 at
    /// 96 dpi, 150 on a 150% display), so the remote renders its UI at the local text size. Same pixel count.
    /// </summary>
    Task RequestResizeAsync(int width, int height, int scalePercent, CancellationToken ct = default) => RequestResizeAsync(width, height, ct);

    /// <summary>
    /// Requests a validated virtual monitor layout through MS-RDPEDISP. Sessions without display-control support
    /// may ignore this request. The scale is applied to every monitor because the existing framebuffer and session
    /// contract use one desktop scale for the virtual desktop.
    /// </summary>
    Task RequestMonitorLayoutAsync(RdpMonitorLayout layout, int scalePercent = 100, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Tells the server whether the desktop is being shown. A hidden or minimized view passes
    /// <see langword="false"/>: the server stops sending display updates (no bandwidth, no decoding) when it supports
    /// Suppress Output. <see langword="true"/> resumes them and asks for a full repaint. A no-op otherwise.
    /// </summary>
    Task SetDisplayUpdatesAsync(bool visible, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>What the remote mouse pointer should look like.</summary>
public enum RdpPointerKind
{
    /// <summary>The platform's default arrow.</summary>
    Default,

    /// <summary>No pointer (e.g. while typing in some applications, or full-screen video).</summary>
    Hidden,

    /// <summary>A custom image: <see cref="RdpPointer.Bgra"/>.</summary>
    Image
}

/// <summary>
/// A remote mouse pointer: its kind and, for <see cref="RdpPointerKind.Image"/>, a top-down BGRA32 image with straight
/// (non-premultiplied) alpha and its hot spot. Instances are reused when the server re-selects a cached pointer, so a
/// view can cache the platform cursor it builds per instance.
/// </summary>
public sealed class RdpPointer
{
    internal RdpPointer(RdpPointerKind kind, int width = 0, int height = 0, int hotspotX = 0, int hotspotY = 0, byte[]? bgra = null)
    {
        Kind = kind;
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        Bgra = bgra ?? [];
    }

    /// <summary>The platform default arrow.</summary>
    public static RdpPointer Default { get; } = new(RdpPointerKind.Default);

    /// <summary>No visible pointer.</summary>
    public static RdpPointer Hidden { get; } = new(RdpPointerKind.Hidden);

    public RdpPointerKind Kind { get; }
    public int Width { get; }
    public int Height { get; }
    public int HotspotX { get; }
    public int HotspotY { get; }

    /// <summary>Top-down BGRA32, straight alpha, Width × Height × 4 bytes (empty unless <see cref="RdpPointerKind.Image"/>).</summary>
    public byte[] Bgra { get; }
}

/// <summary>
/// Implemented by sessions that forward the server's pointer shape (I-beam over text, resize arrows, hidden while
/// typing…). Raised on the session's receive thread; marshal to the UI thread before touching UI objects.
/// </summary>
public interface IRdpPointerSource
{
    event Action<RdpPointer>? PointerChanged;
}
