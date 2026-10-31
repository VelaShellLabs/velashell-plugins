namespace VelaShell.Plugin.Rdp;

/// <summary>A decoded desktop frame handed to the UI: tightly packed BGRA32, alpha 255.</summary>
internal sealed record RdpFrame(int Width, int Height, byte[] Pixels);

/// <summary>
/// The engine contract the desktop view, input queue and clipboard bridge depend on.
/// Implemented by the pure-managed <see cref="Managed.RdpManagedSession"/>. No wire, SDK or
/// platform types cross this boundary.
/// </summary>
internal interface IRdpSession : IRdpClipboardTransport, IAsyncDisposable
{
    event Action? FrameAvailable;
    event Action<Exception?>? Disconnected;
    bool IsConnected { get; }
    RdpFrame? TakeFrame();
    Task KeyAsync(uint code, bool down, CancellationToken ct = default);
    Task UnicodeAsync(string text, CancellationToken ct = default);
    Task MouseAsync(uint flags, uint x, uint y, CancellationToken ct = default);

    /// <summary>
    /// Requests that the remote desktop resize to <paramref name="width"/>×<paramref name="height"/>
    /// device pixels so it matches the workspace tab. A no-op when the server does not offer the
    /// display-control channel; the desktop then keeps its negotiated size and is scaled to fit.
    /// </summary>
    Task RequestResizeAsync(int width, int height, CancellationToken ct = default);
}
