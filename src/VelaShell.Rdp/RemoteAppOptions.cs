namespace VelaShell.Rdp;

/// <summary>Explicitly starts a published RemoteApp using classic RAIL window graphics.</summary>
public sealed record RemoteAppOptions
{
    /// <summary>The server's published program alias, including its || prefix.</summary>
    public required string Program { get; init; }
    /// <summary>Space reserved within the fixed virtual desktop for the local application selector.</summary>
    public const int WorkspaceStripHeight = 32;
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Program) || !Program.StartsWith("||", StringComparison.Ordinal) || Program.Length <= 2)
        {
            throw new ArgumentException("RemoteApp requires a published program alias beginning with ||.", nameof(Program));
        }
        ValidateString(Program, 260, nameof(Program));
        ValidateString(Arguments, 8000, nameof(Arguments));
        ValidateString(WorkingDirectory, 260, nameof(WorkingDirectory));
    }

    private static void ValidateString(string value, int limit, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length > limit || value.Contains('\0'))
        {
            throw new ArgumentException($"RemoteApp {name} exceeds {limit} UTF-16 characters or contains NUL.", name);
        }
    }

    public override string ToString() => nameof(RemoteAppOptions);
}

/// <summary>A server window; coordinates and regions are in remote pixels. Regions are window-relative.</summary>
public sealed record RemoteAppWindow(uint Id)
{
    public string Title { get; init; } = "";
    public uint OwnerId { get; init; }
    public uint Style { get; init; }
    public uint ExtendedStyle { get; init; }
    public byte ShowState { get; init; } = 5;
    /// <summary>
    /// Whether the server has cloaked the window from the user (TS_RAIL_ORDER_CLOAK, MS-RDPERP 2.2.2.12.1): it
    /// exists but must not be shown — e.g. on another virtual desktop or still starting.
    /// </summary>
    public bool IsCloaked { get; init; }

    /// <summary>
    /// EnforceServerZOrder (WINDOW_ORDER_FIELD_ENFORCE_SERVER_ZORDER, MS-RDPERP 2.2.1.3.1.2.1): the window keeps its
    /// place in the server's Z-order relative to the other windows that set it. It does not affect visibility.
    /// </summary>
    public bool EnforceServerZOrder { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int ClientX { get; init; }
    public int ClientY { get; init; }
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int ClientDeltaX { get; init; }
    public int ClientDeltaY { get; init; }
    public int VisibleX { get; init; }
    public int VisibleY { get; init; }
    public IReadOnlyList<RdpFrameRegion> WindowRects { get; init; } = Array.Empty<RdpFrameRegion>();
    public IReadOnlyList<RdpFrameRegion> VisibilityRects { get; init; } = Array.Empty<RdpFrameRegion>();
    public bool Visible => !IsCloaked && ShowState is not (0 or 2) && Width > 0 && Height > 0;
}

/// <summary>Optional RAIL contract; a desktop session reports IsRemoteApp=false. Events run on the receiver thread.</summary>
public interface IRdpRemoteAppSource
{
    bool IsRemoteApp { get; }
    uint ActiveRemoteWindow => 0;
    /// <summary>Current windows in front-to-back order, including hidden/minimized windows.</summary>
    IReadOnlyList<RemoteAppWindow> RemoteWindows { get; }
    event Action? RemoteWindowsChanged;
    Task ActivateWindowAsync(uint windowId, bool active, CancellationToken ct = default);
    Task CloseWindowAsync(uint windowId, CancellationToken ct = default);
    Task MoveWindowAsync(uint windowId, int x, int y, int width, int height, CancellationToken ct = default);
    Task SetWindowStateAsync(uint windowId, bool maximized, CancellationToken ct = default);
}
