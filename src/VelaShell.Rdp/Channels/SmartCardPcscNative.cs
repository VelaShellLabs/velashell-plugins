using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Channels;

// Separate declarations preserve Win32 LLP64, pcsc-lite LP64 and Apple fixed-width handles.
internal static unsafe partial class SmartCardPcscNative
{
    internal static partial class Windows
    {
        [LibraryImport("winscard.dll", EntryPoint = "SCardEstablishContext")]
        internal static partial uint EstablishContext(uint scope, nint reserved1, nint reserved2, nint* context);
        [LibraryImport("winscard.dll", EntryPoint = "SCardReleaseContext")]
        internal static partial uint ReleaseContext(nint context);
        [LibraryImport("winscard.dll", EntryPoint = "SCardIsValidContext")]
        internal static partial uint IsValidContext(nint context);
        [LibraryImport("winscard.dll", EntryPoint = "SCardCancel")]
        internal static partial uint Cancel(nint context);
        [LibraryImport("winscard.dll", EntryPoint = "SCardListReadersW")]
        internal static partial uint ListReaders(nint context, byte* groups, byte* readers, uint* length);
        [LibraryImport("winscard.dll", EntryPoint = "SCardListReaderGroupsW")]
        internal static partial uint ListReaderGroups(nint context, byte* groups, uint* length);
        [LibraryImport("winscard.dll", EntryPoint = "SCardGetStatusChangeW")]
        internal static partial uint GetStatusChange(nint context, uint timeout, byte* readers, uint count);
        [LibraryImport("winscard.dll", EntryPoint = "SCardConnectW")]
        internal static partial uint Connect(nint context, byte* reader, uint shareMode, uint protocols, nint* card, uint* activeProtocol);
        [LibraryImport("winscard.dll", EntryPoint = "SCardReconnect")]
        internal static partial uint Reconnect(nint card, uint shareMode, uint protocols, uint initialization, uint* activeProtocol);
        [LibraryImport("winscard.dll", EntryPoint = "SCardDisconnect")]
        internal static partial uint Disconnect(nint card, uint disposition);
        [LibraryImport("winscard.dll", EntryPoint = "SCardBeginTransaction")]
        internal static partial uint BeginTransaction(nint card);
        [LibraryImport("winscard.dll", EntryPoint = "SCardEndTransaction")]
        internal static partial uint EndTransaction(nint card, uint disposition);
        [LibraryImport("winscard.dll", EntryPoint = "SCardStatusW")]
        internal static partial uint Status(nint card, byte* readers, uint* readerLength, uint* state, uint* protocol, byte* atr, uint* atrLength);
        [LibraryImport("winscard.dll", EntryPoint = "SCardTransmit")]
        internal static partial uint Transmit(nint card, byte* sendPci, byte* send, uint sendLength, byte* receivePci, byte* receive, uint* receiveLength);
        [LibraryImport("winscard.dll", EntryPoint = "SCardControl")]
        internal static partial uint Control(nint card, uint code, byte* input, uint inputLength, byte* output, uint outputLength, uint* returned);
        [LibraryImport("winscard.dll", EntryPoint = "SCardGetAttrib")]
        internal static partial uint GetAttrib(nint card, uint attribute, byte* output, uint* length);
    }
    internal static partial class Linux
    {
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardEstablishContext")]
        internal static partial nint EstablishContext(nuint scope, nint reserved1, nint reserved2, nint* context);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardReleaseContext")]
        internal static partial nint ReleaseContext(nint context);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardIsValidContext")]
        internal static partial nint IsValidContext(nint context);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardCancel")]
        internal static partial nint Cancel(nint context);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardListReaders")]
        internal static partial nint ListReaders(nint context, byte* groups, byte* readers, nuint* length);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardListReaderGroups")]
        internal static partial nint ListReaderGroups(nint context, byte* groups, nuint* length);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardGetStatusChange")]
        internal static partial nint GetStatusChange(nint context, nuint timeout, byte* readers, nuint count);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardConnect")]
        internal static partial nint Connect(nint context, byte* reader, nuint shareMode, nuint protocols, nint* card, nuint* activeProtocol);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardReconnect")]
        internal static partial nint Reconnect(nint card, nuint shareMode, nuint protocols, nuint initialization, nuint* activeProtocol);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardDisconnect")]
        internal static partial nint Disconnect(nint card, nuint disposition);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardBeginTransaction")]
        internal static partial nint BeginTransaction(nint card);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardEndTransaction")]
        internal static partial nint EndTransaction(nint card, nuint disposition);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardStatus")]
        internal static partial nint Status(nint card, byte* readers, nuint* readerLength, nuint* state, nuint* protocol, byte* atr, nuint* atrLength);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardTransmit")]
        internal static partial nint Transmit(nint card, byte* sendPci, byte* send, nuint sendLength, byte* receivePci, byte* receive, nuint* receiveLength);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardControl")]
        internal static partial nint Control(nint card, nuint code, byte* input, nuint inputLength, byte* output, nuint outputLength, nuint* returned);
        [LibraryImport("libpcsclite.so.1", EntryPoint = "SCardGetAttrib")]
        internal static partial nint GetAttrib(nint card, nuint attribute, byte* output, nuint* length);
    }
    internal static partial class Mac
    {
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardEstablishContext")]
        internal static partial uint EstablishContext(uint scope, nint reserved1, nint reserved2, int* context);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardReleaseContext")]
        internal static partial uint ReleaseContext(int context);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardIsValidContext")]
        internal static partial uint IsValidContext(int context);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardCancel")]
        internal static partial uint Cancel(int context);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardListReaders")]
        internal static partial uint ListReaders(int context, byte* groups, byte* readers, uint* length);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardListReaderGroups")]
        internal static partial uint ListReaderGroups(int context, byte* groups, uint* length);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardGetStatusChange")]
        internal static partial uint GetStatusChange(int context, uint timeout, byte* readers, uint count);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardConnect")]
        internal static partial uint Connect(int context, byte* reader, uint shareMode, uint protocols, int* card, uint* activeProtocol);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardReconnect")]
        internal static partial uint Reconnect(int card, uint shareMode, uint protocols, uint initialization, uint* activeProtocol);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardDisconnect")]
        internal static partial uint Disconnect(int card, uint disposition);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardBeginTransaction")]
        internal static partial uint BeginTransaction(int card);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardEndTransaction")]
        internal static partial uint EndTransaction(int card, uint disposition);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardStatus")]
        internal static partial uint Status(int card, byte* readers, uint* readerLength, uint* state, uint* protocol, byte* atr, uint* atrLength);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardTransmit")]
        internal static partial uint Transmit(int card, byte* sendPci, byte* send, uint sendLength, byte* receivePci, byte* receive, uint* receiveLength);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardControl132")]
        internal static partial uint Control(int card, uint code, byte* input, uint inputLength, byte* output, uint outputLength, uint* returned);
        [LibraryImport("/System/Library/Frameworks/PCSC.framework/PCSC", EntryPoint = "SCardGetAttrib")]
        internal static partial uint GetAttrib(int card, uint attribute, byte* output, uint* length);
    }
}
