namespace VelaShell.Rdp;

/// <summary>Display-scale values as MS-RDPBCGR (CS_CORE) and MS-RDPEDISP (monitor layout) define them.</summary>
internal static class RdpScale
{
    /// <summary>desktopScaleFactor: a percentage, 100..500.</summary>
    internal static int Clamp(int percent) => Math.Clamp(percent, 100, 500);

    /// <summary>deviceScaleFactor: only 100, 140 or 180 are allowed; pick the nearest bucket.</summary>
    internal static int Device(int percent) => Clamp(percent) switch
    {
        < 120 => 100,
        < 160 => 140,
        _ => 180
    };
}
