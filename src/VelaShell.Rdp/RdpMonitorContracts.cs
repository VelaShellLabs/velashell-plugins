namespace VelaShell.Rdp;

/// <summary>The orientation value carried by an MS-RDPEDISP monitor definition.</summary>
public enum RdpMonitorOrientation
{
    /// <summary>Landscape, unrotated.</summary>
    Landscape = 0,

    /// <summary>Portrait, rotated clockwise by 90 degrees.</summary>
    Portrait = 90,

    /// <summary>Landscape, rotated by 180 degrees.</summary>
    LandscapeFlipped = 180,

    /// <summary>Portrait, rotated by 270 degrees.</summary>
    PortraitFlipped = 270
}

/// <summary>A monitor rectangle in the remote virtual desktop.</summary>
/// <param name="X">The non-negative desktop x-coordinate of the monitor's left edge.</param>
/// <param name="Y">The non-negative desktop y-coordinate of the monitor's top edge.</param>
/// <param name="Width">The monitor width in remote pixels.</param>
/// <param name="Height">The monitor height in remote pixels.</param>
/// <param name="Primary">Whether this is the one primary monitor. The primary monitor must start at (0, 0).</param>
/// <param name="PhysicalWidth">Physical monitor width in millimetres, or zero when unknown.</param>
/// <param name="PhysicalHeight">Physical monitor height in millimetres, or zero when unknown.</param>
/// <param name="Orientation">The monitor rotation.</param>
public readonly record struct RdpMonitor(
    int X,
    int Y,
    int Width,
    int Height,
    bool Primary = false,
    int PhysicalWidth = 0,
    int PhysicalHeight = 0,
    RdpMonitorOrientation Orientation = RdpMonitorOrientation.Landscape);

/// <summary>
/// A validated MS-RDPEDISP virtual monitor layout. The managed framebuffer represents the bounding desktop, so
/// coordinates are non-negative and the layout must fit within the existing 8192×8192 surface limit.
/// </summary>
public sealed class RdpMonitorLayout
{
    /// <summary>The maximum monitor count accepted by the managed graphics and display-control foundations.</summary>
    public const int MaxMonitors = 16;

    private const int MaxDesktopSide = 8192;

    /// <summary>The monitors in desktop order supplied by the caller.</summary>
    public IReadOnlyList<RdpMonitor> Monitors { get; }

    /// <summary>The width of the virtual desktop bounding rectangle.</summary>
    public int DesktopWidth { get; }

    /// <summary>The height of the virtual desktop bounding rectangle.</summary>
    public int DesktopHeight { get; }

    /// <summary>Creates a validated monitor layout.</summary>
    public RdpMonitorLayout(IEnumerable<RdpMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        RdpMonitor[] copy = [.. monitors];
        if (copy.Length is < 1 or > MaxMonitors)
        {
            throw new ArgumentException($"A monitor layout must contain 1 to {MaxMonitors} monitors.", nameof(monitors));
        }

        int primaryCount = 0;
        long rightmost = 0, bottommost = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            RdpMonitor monitor = copy[i];
            if (monitor.X < 0 || monitor.Y < 0)
            {
                throw new ArgumentException("Monitor coordinates must be non-negative for the managed virtual desktop.", nameof(monitors));
            }

            if (monitor.Width is < 1 or > MaxDesktopSide || monitor.Height is < 1 or > MaxDesktopSide)
            {
                throw new ArgumentException("Monitor dimensions must be between 1 and 8192 pixels.", nameof(monitors));
            }

            if (monitor.PhysicalWidth is < 0 or > ushort.MaxValue || monitor.PhysicalHeight is < 0 or > ushort.MaxValue)
            {
                throw new ArgumentException("Physical monitor dimensions must be between 0 and 65535 millimetres.", nameof(monitors));
            }

            if (!Enum.IsDefined(monitor.Orientation))
            {
                throw new ArgumentException("Monitor orientation is invalid.", nameof(monitors));
            }

            if (monitor.Primary)
            {
                primaryCount++;
                if (monitor.X != 0 || monitor.Y != 0)
                {
                    throw new ArgumentException("The primary monitor must start at (0, 0).", nameof(monitors));
                }
            }

            rightmost = Math.Max(rightmost, (long)monitor.X + monitor.Width);
            bottommost = Math.Max(bottommost, (long)monitor.Y + monitor.Height);
        }

        if (primaryCount != 1)
        {
            throw new ArgumentException("A monitor layout must contain exactly one primary monitor.", nameof(monitors));
        }

        if (rightmost > MaxDesktopSide || bottommost > MaxDesktopSide || rightmost * bottommost > (long)MaxDesktopSide * MaxDesktopSide)
        {
            throw new ArgumentException("The monitor layout exceeds the managed 8192×8192 virtual desktop limit.", nameof(monitors));
        }

        for (int i = 0; i < copy.Length; i++)
        {
            for (int j = i + 1; j < copy.Length; j++)
            {
                if (Overlaps(copy[i], copy[j]))
                {
                    throw new ArgumentException("Monitor rectangles must not overlap.", nameof(monitors));
                }
            }
        }

        Monitors = Array.AsReadOnly(copy);
        DesktopWidth = (int)rightmost;
        DesktopHeight = (int)bottommost;
    }

    /// <summary>Creates the single-monitor layout used by the existing resize API.</summary>
    public static RdpMonitorLayout Single(int width, int height) =>
        new([new RdpMonitor(0, 0, width, height, Primary: true)]);

    private static bool Overlaps(RdpMonitor left, RdpMonitor right) =>
        left.X < (long)right.X + right.Width && right.X < (long)left.X + left.Width
        && left.Y < (long)right.Y + right.Height && right.Y < (long)left.Y + left.Height;
}
