using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Graphics;

namespace VelaShell.Rdp;

public sealed partial class RdpManagedSession
{
    private RdpGfxChannel? _graphics;
    private Action<string>? _graphicsLog;

    internal static IRdpGfxBackend? PrepareGraphics(RdpClientOptions options,
        Func<int, int, IRdpGfxBackend>? factory = null)
    {
        if (!options.EnableGraphicsPipeline || options.RemoteApp is not null)
        {
            GraphicsDiagnostic(options.GraphicsLog, "classic graphics selected; managed graphics pipeline disabled");
            return null;
        }
        int width = options.MonitorLayout?.DesktopWidth ?? options.Width;
        int height = options.MonitorLayout?.DesktopHeight ?? options.Height;
        IRdpGfxBackend backend = (factory ?? ((w, h) => new ManagedRdpGfx(w, h,
            message => GraphicsDiagnostic(options.GraphicsLog, message))))(width, height);
        GraphicsDiagnostic(options.GraphicsLog, "managed graphics pipeline ready; offering RDPGFX with AVC420/AVC444/AVC444v2");
        return backend;
    }

    /// <summary>
    /// Ends the session as if the connection had been lost (for example local input could not be delivered because
    /// the network stalled): the connection stops and <see cref="Disconnected" /> is raised with an
    /// <see cref="RdpConnectionLostException" /> carrying <paramref name="reason" />, so the caller's normal handling —
    /// status, overlay, automatic reconnect to the waiting Windows session — runs. Disposing instead ends a session
    /// silently, because a disposed session raises no event. The caller still disposes the session afterwards.
    /// </summary>
    public void AbortConnection(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _ = Interlocked.CompareExchange(ref _dynamicChannelFailure, new RdpConnectionLostException(reason), null);
        _stop.Cancel();
    }

    private void FailGraphics(Exception exception)
    {
        var failure = new IOException("RDP graphics pipeline failed; reconnect the session.", exception);
        if (Interlocked.CompareExchange(ref _dynamicChannelFailure, failure, null) is null)
        {
            GraphicsDiagnostic(_graphicsLog, $"graphics pipeline failed; disconnecting: {exception.Message}");
        }
        _stop.Cancel();
    }

    private static void GraphicsDiagnostic(Action<string>? log, string message)
    {
        try { log?.Invoke($"rdpgfx: {message}"); }
        catch { /* Diagnostics must not affect protocol or resource ownership. */ }
    }
}
