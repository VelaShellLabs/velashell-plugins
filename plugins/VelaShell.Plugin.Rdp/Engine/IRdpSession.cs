using System.Diagnostics.CodeAnalysis;

namespace VelaShell.Plugin.Rdp.Engine;

/// <summary>The transport endpoint is distinct from the authenticated server identity.</summary>
internal sealed record RdpConnectionOptions(string TransportHost, int TransportPort, string TargetHost,
    int TargetPort, int DesktopWidth, int DesktopHeight, TimeSpan ConnectTimeout, string TrustedSha256)
{
    public bool RedirectClipboard { get; init; }
    public IReadOnlyList<RdpDriveMapping> Drives { get; init; } = Array.Empty<RdpDriveMapping>();
}

/// <summary>Session-only credential references; never persist or format this object.</summary>
internal sealed class RdpCredentials(string username, string password, string domain) : IDisposable
{
    private char[]? _password = password.ToCharArray();

    [AllowNull]
    public string Username { get => field ?? throw new ObjectDisposedException(nameof(RdpCredentials)); private set; } = username;
    // Only create an immutable copy at the native call boundary. That copy cannot be reliably erased.
    public string GetPasswordForNative() => new(_password ?? throw new ObjectDisposedException(nameof(RdpCredentials)));

    [AllowNull]
    public string Domain { get => field ?? throw new ObjectDisposedException(nameof(RdpCredentials)); private set; } = domain;
    public void Dispose()
    {
        char[]? secret = Interlocked.Exchange(ref _password, null);
        if (secret is not null) Array.Clear(secret);
        Username = null;
        Domain = null;
    }
    public override string ToString() => nameof(RdpCredentials);
}

internal enum RdpSessionState { Connecting, Connected, Disconnected, Faulted, Closed }
internal sealed record RdpSessionStatus(RdpSessionState State, string? Error = null);
/// <summary>Pixels are top-down straight RGBA32, stride Width*4. Sequence increases per complete update.</summary>
internal readonly record struct RdpFrameInfo(int Width, int Height, int Stride, long Sequence)
{
    public int ByteLength => checked(Stride * Height);
}
internal enum RdpPointerKind { Default, Hidden, Bitmap, Position }
internal sealed record RdpPointer(RdpPointerKind Kind, int Width = 0, int Height = 0, int HotspotX = 0,
    int HotspotY = 0, byte[]? Rgba = null, int X = 0, int Y = 0);
internal enum RdpInputKind { KeyDown, KeyUp, PointerMove, ButtonDown, ButtonUp, Wheel, ReleaseAll, CtrlAltDelete, ClipboardText, ClipboardFocus }
internal enum RdpMouseButton { Left, Middle, Right, X1, X2 }
/// <summary>ScanCode is a physical set-1 scan code, with extended bit encoded as 0x100.</summary>
internal readonly record struct RdpInput(RdpInputKind Kind, ushort ScanCode = 0, int X = 0, int Y = 0,
    RdpMouseButton Button = RdpMouseButton.Left, short WheelDelta = 0, bool Horizontal = false, string? Text = null, bool SynchronizeClipboard = false,
    TaskCompletionSource<bool>? ClipboardCompletion = null, long ClipboardEpoch = 0);

/// <summary>No UI, SDK or native types cross the desktop/session boundary.</summary>
internal interface IRdpSession : IAsyncDisposable
{
    RdpSessionStatus Status { get; }
    RdpFrameInfo FrameInfo { get; }
    RdpPointer Pointer { get; }
    // Notifications are coalesced on a separate worker, never on the network/native pump.
    event Action? FrameAvailable;
    event Action<RdpSessionStatus>? StatusChanged;
    event Action<RdpPointer>? PointerChanged;
    // The caller owns a locked pixel target for this synchronous copy; a size mismatch never writes it.
    bool TryCopyFrame(IntPtr destination, int width, int height, int stride, int capacity, out RdpFrameInfo frame);
    void SetFrameConsumer(bool enabled);
    ValueTask SendInputAsync(RdpInput input, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

internal interface IRdpSessionFactory
{
    // Success means full desktop activation; it never depends on CreateView being called.
    Task<IRdpSession> ConnectAsync(RdpConnectionOptions options, RdpCredentials credentials,
        CancellationToken cancellationToken = default);
}
