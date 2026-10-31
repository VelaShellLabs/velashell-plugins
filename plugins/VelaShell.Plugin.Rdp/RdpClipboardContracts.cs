namespace VelaShell.Plugin.Rdp;

internal sealed record RdpClipboardEvent(uint Kind, uint A, uint B, byte[] Data);

internal interface IRdpClipboardTransport
{
    event Action<RdpClipboardEvent>? ClipboardEvent;
    Task OfferFormatsAsync(string formats, CancellationToken cancellationToken);
    Task RequestFormatAsync(uint format, CancellationToken cancellationToken);
    Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken);
    Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken);
    Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken);
}
