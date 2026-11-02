using VelaShell.Rdp.Graphics;

namespace VelaShell.Rdp;

internal sealed partial class RdpConnection
{
    private readonly Lock _surfaceGate = new();
    private bool _graphicsActive;

    internal void ApplyGraphicsFrame(RdpGfxFrame frame)
    {
        frame.Validate();
        lock (_surfaceGate)
        {
            _graphicsActive = true;
            Framebuffer surface = Framebuffer;
            if (surface.Width != frame.Width || surface.Height != frame.Height)
            {
                surface = new Framebuffer(frame.Width, frame.Height);
                // A new UI surface needs all pixels, even if this update dirtied only a small region.
                frame.X = frame.Y = 0;
                frame.W = frame.Width;
                frame.H = frame.Height;
                surface.Blit(frame);
                Framebuffer = surface;
                _orderSurface.Current = surface;
                _width = frame.Width;
                _height = frame.Height;
            }
            else { surface.Blit(frame); }
        }
        FrameUpdated?.Invoke();
    }
}
