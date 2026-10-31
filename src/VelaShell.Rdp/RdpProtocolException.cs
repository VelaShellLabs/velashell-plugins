namespace VelaShell.Rdp;

/// <summary>
/// Raised when a received PDU is malformed or violates the negotiated protocol state.
/// </summary>
public sealed class RdpProtocolException : Exception
{
    internal RdpProtocolException(string message) : base(message) { }
    internal RdpProtocolException(string message, Exception inner) : base(message, inner) { }
}
