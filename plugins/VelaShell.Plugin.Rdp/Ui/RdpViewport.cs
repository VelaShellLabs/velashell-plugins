using Avalonia;

namespace VelaShell.Plugin.Rdp.Ui;

/// <summary>All input starts in DIPs; Actual maps one server pixel to one physical display pixel.</summary>
internal readonly record struct RdpViewport(Rect Desktop, int PixelWidth, int PixelHeight)
{
    public static RdpViewport Create(Size viewport, int width, int height, RdpScaleMode mode, double dpiScale = 1,
        Vector scroll = default)
    {
        if (width <= 0 || height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return default;
        double scale = mode == RdpScaleMode.Fit
            ? Math.Min(viewport.Width / width, viewport.Height / height)
            : 1 / Math.Max(0.1, dpiScale);
        double w = width * scale, h = height * scale;
        return new(new Rect(mode == RdpScaleMode.Fit ? (viewport.Width - w) / 2 : -scroll.X,
            mode == RdpScaleMode.Fit ? (viewport.Height - h) / 2 : -scroll.Y, w, h), width, height);
    }

    public bool TryMap(Point point, bool clamp, out PixelPoint pixel)
    {
        pixel = default;
        if (Desktop.Width <= 0 || Desktop.Height <= 0 || (!clamp && !Desktop.Contains(point))) return false;
        pixel = new(Math.Clamp((int)Math.Floor((point.X - Desktop.X) * PixelWidth / Desktop.Width), 0, PixelWidth - 1),
            Math.Clamp((int)Math.Floor((point.Y - Desktop.Y) * PixelHeight / Desktop.Height), 0, PixelHeight - 1));
        return true;
    }
}
