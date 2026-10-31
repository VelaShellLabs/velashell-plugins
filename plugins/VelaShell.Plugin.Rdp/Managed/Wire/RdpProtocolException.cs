namespace VelaShell.Plugin.Rdp.Managed.Wire;

/// <summary>
/// Raised when a received PDU is malformed or violates the negotiated protocol state.
/// The session worker translates it into the host's <c>ProtocolConnectionException</c>.
/// </summary>
internal sealed class RdpProtocolException : Exception
{
    internal RdpProtocolException(string message) : base(message) { }
    internal RdpProtocolException(string message, Exception inner) : base(message, inner) { }
}
