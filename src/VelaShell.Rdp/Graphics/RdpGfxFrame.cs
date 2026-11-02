namespace VelaShell.Rdp.Graphics;

internal struct RdpGfxFrame
{
    internal byte[] Pixels;
    internal int Width, Height, Stride, X, Y, W, H;

    internal readonly void Validate()
    {
        if (Pixels is null || Width is < 1 or > Framebuffer.MaxBitmapSide || Height is < 1 or > Framebuffer.MaxBitmapSide
            || Stride < Width * 4 || (long)Stride * Height > Pixels.Length
            || X < 0 || Y < 0 || W < 0 || H < 0 || (long)X + W > Width || (long)Y + H > Height)
        {
            throw new RdpProtocolException("RDP graphics backend returned an invalid frame.");
        }
    }
}

/// <summary>One serialized caller copies each borrowed frame before calling the backend again.</summary>
internal interface IRdpGfxBackend : IDisposable
{
    void Open();
    void Feed(byte[] message);
    bool TryGetFrame(out RdpGfxFrame frame);
    byte[]? ReadResponse();
}
