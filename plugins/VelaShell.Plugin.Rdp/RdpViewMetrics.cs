namespace VelaShell.Plugin.Rdp;

/// <summary>
/// The last desktop view's size in device pixels and its display scale, recorded by the view (on the UI thread)
/// and read when the next session connects: it is not shown yet, but its tab will almost always be the same size,
/// so connecting at that size and scale avoids a full repaint at the configured size followed by an immediate
/// resize and a second full repaint.
/// </summary>
internal static class RdpViewMetrics
{
    private static int _width, _height, _scale;

    internal static void Record(int width, int height, int scalePercent)
    {
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        Volatile.Write(ref _scale, scalePercent);
    }

    /// <summary>The last view's (width, height, scale %), or null before any view has been laid out.</summary>
    internal static (int Width, int Height, int Scale)? Last
    {
        get
        {
            int width = Volatile.Read(ref _width), height = Volatile.Read(ref _height), scale = Volatile.Read(ref _scale);
            return width >= 200 && height >= 200 ? (width, height, Math.Max(100, scale)) : null;
        }
    }

    internal static void ResetForTest() => Record(0, 0, 0);
}
