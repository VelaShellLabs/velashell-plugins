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
    /// Tells the server whether the desktop is being shown. A hidden or minimized view passes
    /// <see langword="false"/>: the server stops sending display updates (no bandwidth, no decoding) when it supports
    /// Suppress Output. <see langword="true"/> resumes them and asks for a full repaint. A no-op otherwise.
    /// </summary>
    Task SetDisplayUpdatesAsync(bool visible, CancellationToken ct = default) => Task.CompletedTask;
}
