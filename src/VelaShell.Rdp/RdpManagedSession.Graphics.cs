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
        IRdpGfxBackend backend = (factory ?? ((width, height) => new ManagedRdpGfx(width, height,
            message => GraphicsDiagnostic(options.GraphicsLog, message))))(options.Width, options.Height);
        GraphicsDiagnostic(options.GraphicsLog, "managed graphics pipeline ready; offering RDPGFX with AVC420/AVC444/AVC444v2");
        return backend;
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
