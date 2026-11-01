namespace VelaShell.Rdp;

public sealed record RdpClipboardEvent(uint Kind, uint A, uint B, byte[] Data)
{
    public ushort? ResponseFlags { get; init; }
}

public interface IRdpClipboardTransport
{
    bool SupportsClipboardLock => false;
    Task LockClipboardAsync(uint id, bool locked, CancellationToken cancellationToken) => Task.FromException(new NotSupportedException("Clipboard version locking is unavailable."));
    Task RequestFileVersionAsync(uint stream, uint index, uint flags, ulong offset, uint length, uint? clipDataId, CancellationToken cancellationToken) =>
        clipDataId is null ? RequestFileAsync(stream, index, flags, offset, length, cancellationToken) : Task.FromException(new NotSupportedException("Clipboard version locking is unavailable."));
    event Action<RdpClipboardEvent>? ClipboardEvent;
    Task OfferFormatsAsync(string formats, CancellationToken cancellationToken);
    Task RequestFormatAsync(uint format, CancellationToken cancellationToken);
    Task RespondFormatAsync(byte[] data, bool success, CancellationToken cancellationToken);
    Task RequestFileAsync(uint stream, uint index, uint flags, ulong offset, uint length, CancellationToken cancellationToken);
    Task RespondFileAsync(uint stream, byte[] data, bool success, CancellationToken cancellationToken);
}
