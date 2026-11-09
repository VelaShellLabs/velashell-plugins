namespace VelaShell.Rdp.Channels;

internal static class SmartCardAttributes
{
    internal const uint SuppressT1IfsRequest = 0x7FFF0007;
}

internal interface ISmartCardBackend
{
    nint EstablishContext(uint scope);
    void ReleaseContext(nint context);
    void IsValidContext(nint context);
    string[] ListReaders(nint context, string[]? groups);
    string[] ListReaderGroups(nint context);
    SmartCardReaderState[] GetStatusChange(nint context, uint timeout, SmartCardReaderState[] readers);
    void Cancel(nint context);
    (nint Card, uint Protocol) Connect(nint context, string reader, uint shareMode, uint protocols);
    uint Reconnect(nint card, uint shareMode, uint protocols, uint initialization);
    void Disconnect(nint card, uint disposition);
    void BeginTransaction(nint card);
    void EndTransaction(nint card, uint disposition);
    SmartCardStatus Status(nint card);
    SmartCardTransmitResult Transmit(nint card, SmartCardPci sendPci, byte[] send, SmartCardPci? receivePci, int capacity, bool lengthOnly);
    SmartCardBuffer Control(nint card, uint code, byte[] input, int capacity, bool lengthOnly);
    byte[] GetAttribute(nint card, uint attribute);
    void SetAttribute(nint card, uint attribute, byte[] value);
}

internal sealed record SmartCardReaderState(string Reader, uint CurrentState, uint EventState, byte[] Atr);
internal sealed record SmartCardStatus(string[] Readers, uint State, uint Protocol, byte[] Atr);
internal sealed record SmartCardPci(uint Protocol, byte[] Extra);
internal sealed record SmartCardBuffer(byte[] Data, int Length);
internal sealed record SmartCardTransmitResult(SmartCardPci? Pci, SmartCardBuffer Buffer);

internal sealed class SmartCardException(uint code) : Exception($"PC/SC failed (0x{code:X8}).")
{
    internal uint Code { get; } = code;
    internal const uint Cancelled = 0x80100002, InvalidHandle = 0x80100003, InvalidParameter = 0x80100004;
    internal const uint NoMemory = 0x80100006, InsufficientBuffer = 0x80100008, Timeout = 0x8010000A;
    internal const uint NoService = 0x8010001D, Unsupported = 0x80100022;
}
