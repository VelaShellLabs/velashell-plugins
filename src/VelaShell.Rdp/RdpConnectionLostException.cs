namespace VelaShell.Rdp;

/// <summary>
/// The transport under an established session failed: the network path dropped (a NAT gateway or firewall expired
/// the connection, Wi-Fi changed, the computer slept), keep-alive probes went unanswered, a send stalled, or the
/// server closed TCP without saying why. The remote Windows session normally survives this in the disconnected
/// state, so connecting again is expected to resume it — unlike <see cref="RdpDisconnectException"/>, where the
/// server ended the connection on purpose (signed out, taken over, idle limit …).
/// </summary>
public sealed class RdpConnectionLostException : IOException
{
    internal RdpConnectionLostException(string message) : base(message) { }

    internal RdpConnectionLostException(Exception inner)
        : base($"The connection to the RDP server was lost: {inner.Message}", inner) { }
}
