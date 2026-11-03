using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using static VelaShell.Rdp.Channels.SmartCardPcscNative;

namespace VelaShell.Rdp.Channels;

/// <summary>System PC/SC only; no native library or reader driver is bundled.</summary>
internal sealed unsafe class PcscSmartCardBackend : ISmartCardBackend
{
    private static bool Win => OperatingSystem.IsWindows();
    private static bool Lin => OperatingSystem.IsLinux();
    private static bool MacOs => OperatingSystem.IsMacOS();
    private static int WordSize => Lin ? IntPtr.Size : 4;
    private static Encoding TextEncoding => Win ? Encoding.Unicode : Encoding.UTF8;

    internal PcscSmartCardBackend()
    {
        if (!Win && !Lin && !MacOs) { throw new PlatformNotSupportedException("System PC/SC is unavailable on this platform."); }
    }

    private static void Check(nint result)
    {
        uint code = unchecked((uint)result);
        if (!Win && code == 0x8010001F) { code = SmartCardException.Unsupported; }
        if (code != 0) { throw new SmartCardException(code); }
    }

    public nint EstablishContext(uint scope)
    {
        nint context = 0;
        if (Win) { Check((nint)Windows.EstablishContext(scope, 0, 0, &context)); }
        else if (Lin) { Check(Linux.EstablishContext(scope, 0, 0, &context)); }
        else { int handle = 0; Check((nint)Mac.EstablishContext(scope, 0, 0, &handle)); context = handle; }
        return context;
    }

    public void ReleaseContext(nint context) => Check(Win ? (nint)Windows.ReleaseContext(context) : Lin ? Linux.ReleaseContext(context) : (nint)Mac.ReleaseContext((int)context));
    public void IsValidContext(nint context) => Check(Win ? (nint)Windows.IsValidContext(context) : Lin ? Linux.IsValidContext(context) : (nint)Mac.IsValidContext((int)context));
    public void Cancel(nint context) => Check(Win ? (nint)Windows.Cancel(context) : Lin ? Linux.Cancel(context) : (nint)Mac.Cancel((int)context));
    public void Disconnect(nint card, uint disposition) => Check(Win ? (nint)Windows.Disconnect(card, disposition) : Lin ? Linux.Disconnect(card, disposition) : (nint)Mac.Disconnect((int)card, disposition));
    public void BeginTransaction(nint card) => Check(Win ? (nint)Windows.BeginTransaction(card) : Lin ? Linux.BeginTransaction(card) : (nint)Mac.BeginTransaction((int)card));
    public void EndTransaction(nint card, uint disposition) => Check(Win ? (nint)Windows.EndTransaction(card, disposition) : Lin ? Linux.EndTransaction(card, disposition) : (nint)Mac.EndTransaction((int)card, disposition));

    public string[] ListReaders(nint context, string[]? groups) => List(context, groups, false);
    public string[] ListReaderGroups(nint context) => List(context, null, true);

    private static string[] List(nint context, string[]? groups, bool listGroups)
    {
        byte[] output = new byte[65536];
        byte[] input = groups is null ? [] : TextEncoding.GetBytes(string.Join('\0', groups) + "\0\0");
        uint length = (uint)(Win ? output.Length / 2 : output.Length);
        nuint longLength = length;
        fixed (byte* result = output, names = input)
        {
            if (Win) { Check((nint)(listGroups ? Windows.ListReaderGroups(context, result, &length) : Windows.ListReaders(context, names, result, &length))); }
            else if (Lin) { Check(listGroups ? Linux.ListReaderGroups(context, result, &longLength) : Linux.ListReaders(context, names, result, &longLength)); length = Bounded(longLength, (uint)output.Length); }
            else { Check((nint)(listGroups ? Mac.ListReaderGroups((int)context, result, &length) : Mac.ListReaders((int)context, names, result, &length))); }
        }
        int bytes = checked((int)length * (Win ? 2 : 1));
        if (bytes > output.Length) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
        return TextEncoding.GetString(output, 0, bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    public (nint Card, uint Protocol) Connect(nint context, string reader, uint shareMode, uint protocols)
    {
        byte[] name = TextEncoding.GetBytes(reader + '\0');
        nint card = 0;
        uint protocol = 0;
        fixed (byte* text = name)
        {
            if (Win) { Check((nint)Windows.Connect(context, text, shareMode, protocols, &card, &protocol)); }
            else if (Lin)
            {
                nuint nativeProtocol = 0;
                Check(Linux.Connect(context, text, shareMode, NativeProtocol(protocols), &card, &nativeProtocol));
                protocol = WireProtocol((uint)nativeProtocol);
            }
            else
            {
                int nativeCard = 0;
                Check((nint)Mac.Connect((int)context, text, shareMode, NativeProtocol(protocols), &nativeCard, &protocol));
                card = nativeCard; protocol = WireProtocol(protocol);
            }
        }
        return (card, protocol);
    }

    public uint Reconnect(nint card, uint shareMode, uint protocols, uint initialization)
    {
        uint protocol = 0;
        if (Win) { Check((nint)Windows.Reconnect(card, shareMode, protocols, initialization, &protocol)); }
        else if (Lin)
        {
            nuint nativeProtocol = 0;
            Check(Linux.Reconnect(card, shareMode, NativeProtocol(protocols), initialization, &nativeProtocol));
            protocol = WireProtocol((uint)nativeProtocol);
        }
        else { Check((nint)Mac.Reconnect((int)card, shareMode, NativeProtocol(protocols), initialization, &protocol)); protocol = WireProtocol(protocol); }
        return protocol;
    }

    public SmartCardReaderState[] GetStatusChange(nint context, uint timeout, SmartCardReaderState[] readers)
    {
        if (readers.Length > 11) { throw new SmartCardException(SmartCardException.InvalidParameter); }
        int atrSize = Win ? 36 : 33;
        int currentOffset = IntPtr.Size * 2;
        int eventOffset = currentOffset + WordSize;
        int lengthOffset = eventOffset + WordSize;
        int atrOffset = lengthOffset + WordSize;
        int stride = MacOs ? atrOffset + atrSize : (atrOffset + atrSize + IntPtr.Size - 1) & -IntPtr.Size;
        byte[] states = new byte[checked(stride * readers.Length)];
        nint[] names = new nint[readers.Length];
        try
        {
            fixed (byte* ptr = states)
            {
                for (int i = 0; i < readers.Length; i++)
                {
                    byte[] name = TextEncoding.GetBytes(readers[i].Reader + '\0');
                    names[i] = Marshal.AllocHGlobal(name.Length);
                    Marshal.Copy(name, 0, names[i], name.Length);
                    Marshal.WriteIntPtr((nint)(ptr + i * stride), names[i]);
                    WriteWord(states.AsSpan(i * stride + currentOffset), readers[i].CurrentState);
                }
                if (Win) { Check((nint)Windows.GetStatusChange(context, timeout, ptr, (uint)readers.Length)); }
                else if (Lin) { Check(Linux.GetStatusChange(context, timeout, ptr, (nuint)readers.Length)); }
                else { Check((nint)Mac.GetStatusChange((int)context, timeout, ptr, (uint)readers.Length)); }
            }
            var result = new SmartCardReaderState[readers.Length];
            for (int i = 0; i < readers.Length; i++)
            {
                ReadOnlySpan<byte> state = states.AsSpan(i * stride, stride);
                int length = checked((int)ReadWord(state[lengthOffset..]));
                if (length > atrSize) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
                result[i] = readers[i] with { EventState = (uint)ReadWord(state[eventOffset..]), Atr = state.Slice(atrOffset, length).ToArray() };
            }
            return result;
        }
        finally { foreach (nint name in names) { if (name != 0) { Marshal.FreeHGlobal(name); } } }
    }

    public SmartCardStatus Status(nint card)
    {
        byte[] names = new byte[65536], atr = new byte[36];
        uint length = (uint)(Win ? names.Length / 2 : names.Length), state = 0, protocol = 0, atrLength = (uint)atr.Length;
        fixed (byte* readers = names, atrPtr = atr)
        {
            if (Win) { Check((nint)Windows.Status(card, readers, &length, &state, &protocol, atrPtr, &atrLength)); }
            else if (Lin)
            {
                nuint ln = length, st = 0, pr = 0, al = atrLength;
                Check(Linux.Status(card, readers, &ln, &st, &pr, atrPtr, &al));
                length = Bounded(ln, (uint)names.Length); atrLength = Bounded(al, (uint)atr.Length);
                state = (uint)st; protocol = (uint)pr;
            }
            else { Check((nint)Mac.Status((int)card, readers, &length, &state, &protocol, atrPtr, &atrLength)); }
        }
        int bytes = checked((int)length * (Win ? 2 : 1));
        if (bytes > names.Length || atrLength > atr.Length) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
        if (!Win)
        {
            state = state switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, 32 => 5, 64 => 6, _ => 0 };
            protocol = WireProtocol(protocol);
        }
        return new SmartCardStatus(TextEncoding.GetString(names, 0, bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries), state, protocol, atr[..(int)atrLength]);
    }

    public SmartCardTransmitResult Transmit(nint card, SmartCardPci sendPci, byte[] send, SmartCardPci? receivePci, int capacity, bool lengthOnly)
    {
        ValidateCapacity(capacity);
        if (send.Length > SmartCardNdr.MaxBuffer) { throw new SmartCardException(SmartCardException.InvalidParameter); }
        byte[] sendHeader = Pci(sendPci), receiveHeader = receivePci is null ? [] : Pci(receivePci);
        byte[] output = new byte[capacity];
        uint length = (uint)capacity;
        nuint longLength = length;
        fixed (byte* sp = sendHeader, rp = receiveHeader, input = send, result = output)
        {
            byte* buffer = lengthOnly ? null : result;
            if (Win) { Check((nint)Windows.Transmit(card, sp, input, (uint)send.Length, rp, buffer, &length)); }
            else if (Lin) { Check(Linux.Transmit(card, sp, input, (nuint)send.Length, rp, buffer, &longLength)); length = Bounded(longLength, SmartCardNdr.MaxBuffer); }
            else { Check((nint)Mac.Transmit((int)card, sp, input, (uint)send.Length, rp, buffer, &length)); }
        }
        SmartCardPci? pci = null;
        if (receivePci is not null)
        {
            int nativeLength = checked((int)ReadWord(receiveHeader.AsSpan(WordSize)));
            if (nativeLength < WordSize * 2 || nativeLength > receiveHeader.Length) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
            uint protocol = (uint)ReadWord(receiveHeader);
            pci = new SmartCardPci(Win ? protocol : WireProtocol(protocol), receiveHeader[(WordSize * 2)..nativeLength]);
        }
        return new SmartCardTransmitResult(pci, Result(output, length, lengthOnly));
    }

    public SmartCardBuffer Control(nint card, uint code, byte[] input, int capacity, bool lengthOnly)
    {
        ValidateCapacity(capacity);
        if (input.Length > SmartCardNdr.MaxBuffer) { throw new SmartCardException(SmartCardException.InvalidParameter); }
        byte[] output = new byte[capacity];
        uint length = 0;
        nuint longLength = 0;
        uint nativeCode = Win ? code : ToPcscControl(code);
        fixed (byte* data = input, result = output)
        {
            byte* buffer = lengthOnly ? null : result;
            if (Win) { Check((nint)Windows.Control(card, nativeCode, data, (uint)input.Length, buffer, (uint)capacity, &length)); }
            else if (Lin) { Check(Linux.Control(card, nativeCode, data, (nuint)input.Length, buffer, (nuint)capacity, &longLength)); length = Bounded(longLength, SmartCardNdr.MaxBuffer); }
            else { Check((nint)Mac.Control((int)card, nativeCode, data, (uint)input.Length, buffer, (uint)capacity, &length)); }
        }
        SmartCardBuffer response = Result(output, length, lengthOnly);
        // PC/SC Part 10 feature discovery embeds big-endian platform-specific IOCTLs.
        if (!Win && nativeCode == 0x42000D48 && !lengthOnly)
        {
            for (int offset = 0; offset + 6 <= response.Data.Length; offset += 6)
            {
                if (response.Data[offset + 1] != 4) { break; }
                Span<byte> value = response.Data.AsSpan(offset + 2, 4);
                uint feature = BinaryPrimitives.ReadUInt32BigEndian(value);
                if (feature >= 0x42000000 && feature <= 0x42000FFF)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(value, 0x00310000 | ((feature - 0x42000000) << 2));
                }
            }
        }
        return response;
    }

    public byte[] GetAttribute(nint card, uint attribute)
    {
        byte[] output = new byte[65536];
        uint length = (uint)output.Length;
        nuint longLength = length;
        fixed (byte* result = output)
        {
            if (Win) { Check((nint)Windows.GetAttrib(card, attribute, result, &length)); }
            else if (Lin) { Check(Linux.GetAttrib(card, attribute, result, &longLength)); length = Bounded(longLength, (uint)output.Length); }
            else { Check((nint)Mac.GetAttrib((int)card, attribute, result, &length)); }
        }
        return Result(output, length, false).Data;
    }

    private static byte[] Pci(SmartCardPci pci)
    {
        if (pci.Extra.Length > 1024) { throw new SmartCardException(SmartCardException.InvalidParameter); }
        byte[] bytes = new byte[WordSize * 2 + pci.Extra.Length];
        WriteWord(bytes, Win ? pci.Protocol : NativeProtocol(pci.Protocol));
        WriteWord(bytes.AsSpan(WordSize), (uint)bytes.Length);
        pci.Extra.CopyTo(bytes, WordSize * 2);
        return bytes;
    }

    private static uint NativeProtocol(uint value) => (value & ~0x10000u) | ((value & 0x10000) != 0 ? 4u : 0);
    private static uint WireProtocol(uint value) => (value & ~4u) | ((value & 4) != 0 ? 0x10000u : 0);
    private static uint ToPcscControl(uint code) => (code & 0xFFFF0003) == 0x00310000 ? 0x42000000 + ((code >> 2) & 0xFFF) : code;
    private static uint Bounded(nuint value, uint max) => value <= max ? (uint)value : throw new SmartCardException(SmartCardException.InsufficientBuffer);
    private static void ValidateCapacity(int capacity)
    {
        if (capacity < 0 || capacity > SmartCardNdr.MaxBuffer) { throw new SmartCardException(SmartCardException.InvalidParameter); }
    }

    private static SmartCardBuffer Result(byte[] output, uint length, bool lengthOnly)
    {
        if (length > SmartCardNdr.MaxBuffer || (!lengthOnly && length > output.Length)) { throw new SmartCardException(SmartCardException.InsufficientBuffer); }
        return new SmartCardBuffer(lengthOnly ? [] : output[..(int)length], (int)length);
    }

    private static void WriteWord(Span<byte> target, uint value)
    {
        if (WordSize == 8) { BinaryPrimitives.WriteUInt64LittleEndian(target, value); }
        else { BinaryPrimitives.WriteUInt32LittleEndian(target, value); }
    }

    private static ulong ReadWord(ReadOnlySpan<byte> source) => WordSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(source) : BinaryPrimitives.ReadUInt32LittleEndian(source);
}
