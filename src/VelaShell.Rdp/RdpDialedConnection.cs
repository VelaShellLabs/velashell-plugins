using System.Net;

namespace VelaShell.Rdp;

/// <summary>
/// A connection opened by <see cref="RdpClientOptions.Dialer"/> for the main RDP transport.
/// </summary>
/// <param name="Stream">The connected duplex stream; the RDP connection owns and disposes it.</param>
/// <param name="Tunneled">
/// True when the stream goes through a proxy or tunnel that carries this TCP stream only. UDP side channels are then
/// disabled, since they would bypass it (or reach the proxy's address instead of the server).
/// </param>
/// <param name="RemoteEndPoint">The server's address when connected directly; null when tunneled.</param>
public sealed record RdpDialedConnection(Stream Stream, bool Tunneled, IPEndPoint? RemoteEndPoint);
