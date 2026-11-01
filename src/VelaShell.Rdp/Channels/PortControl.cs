using System.Buffers.Binary;

namespace VelaShell.Rdp.Channels;

// MS-RDPESP 2.2.2.6. Only these operations can reach the native driver; unknown IOCTLs never do.
internal enum PortControl : uint
{
    SetBaud = 0x1B0004, SetQueue = 0x1B0008, SetLine = 0x1B000C,
    BreakOn = 0x1B0010, BreakOff = 0x1B0014, ImmediateChar = 0x1B0018,
    SetTimeouts = 0x1B001C, GetTimeouts = 0x1B0020,
    SetDtr = 0x1B0024, ClearDtr = 0x1B0028, SetRts = 0x1B0030, ClearRts = 0x1B0034,
    SetXoff = 0x1B0038, SetXon = 0x1B003C, GetWaitMask = 0x1B0040,
    SetWaitMask = 0x1B0044, WaitOnMask = 0x1B0048, Purge = 0x1B004C,
    GetBaud = 0x1B0050, GetLine = 0x1B0054, SetChars = 0x1B0058, GetChars = 0x1B005C,
    GetHandflow = 0x1B0060, SetHandflow = 0x1B0064, GetModemStatus = 0x1B0068,
    GetCommStatus = 0x1B006C, GetProperties = 0x1B0074, GetDtrRts = 0x1B0078,
    ParallelQueryInformation = 0x160004, ParallelSetInformation = 0x160008
}

internal static class PortControls
{
    internal static (PortControl Control, int OutputLength) Validate(bool serial, uint code, byte[] input, uint outputCapacity)
    {
        PortControl control = (PortControl)code;
        (int inputLength, int outputLength) = control switch
        {
            PortControl.SetBaud or PortControl.SetWaitMask or PortControl.Purge => (4, 0),
            PortControl.SetQueue => (8, 0),
            PortControl.SetLine => (3, 0),
            PortControl.SetTimeouts => (20, 0),
            PortControl.SetChars => (6, 0),
            PortControl.SetHandflow => (16, 0),
            PortControl.ImmediateChar or PortControl.ParallelSetInformation => (1, 0),
            PortControl.GetBaud or PortControl.GetWaitMask or PortControl.WaitOnMask
                or PortControl.GetModemStatus or PortControl.GetDtrRts => (0, 4),
            PortControl.GetLine => (0, 3),
            PortControl.GetTimeouts => (0, 20),
            PortControl.GetChars => (0, 6),
            PortControl.GetHandflow => (0, 16),
            PortControl.GetCommStatus => (0, 18),
            PortControl.GetProperties => (0, 64),
            PortControl.ParallelQueryInformation => (0, 1),
            PortControl.BreakOn or PortControl.BreakOff or PortControl.SetDtr or PortControl.ClearDtr
                or PortControl.SetRts or PortControl.ClearRts or PortControl.SetXoff or PortControl.SetXon => (0, 0),
            _ => throw new DriveStatusException(NtStatus.NotSupported)
        };
        bool parallel = control is PortControl.ParallelQueryInformation or PortControl.ParallelSetInformation;
        if (serial == parallel) { throw new DriveStatusException(NtStatus.NotSupported); }
        if (input.Length != inputLength || outputCapacity < outputLength || outputCapacity > PortDevice.MaxTransfer)
        {
            throw new DriveStatusException(NtStatus.InvalidParameter);
        }
        uint U32(int offset = 0) => BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(offset));
        bool valid = control switch
        {
            PortControl.SetBaud => U32() is > 0 and <= 4_000_000,
            PortControl.SetQueue => U32() is > 0 and <= PortDevice.MaxTransfer && U32(4) is > 0 and <= PortDevice.MaxTransfer,
            PortControl.SetLine => input[0] <= 2 && input[1] <= 4 && input[2] is >= 5 and <= 8
                && (input[0] != 1 || input[2] == 5) && (input[0] != 2 || input[2] != 5),
            PortControl.SetWaitMask => (U32() & ~0x1FFFu) == 0,
            PortControl.Purge => U32() is > 0 and <= 15,
            PortControl.SetHandflow => (U32() & ~0x8000007Bu) == 0 && (U32() & 3) != 3
                && (U32(4) & ~0x800000DFu) == 0
                && U32(8) <= PortDevice.MaxTransfer && U32(12) <= PortDevice.MaxTransfer,
            // PARALLEL_INIT is the only writable status bit; status-query bits are not commands.
            PortControl.ParallelSetInformation => input[0] == 0x10,
            _ => true
        };
        if (!valid) { throw new DriveStatusException(NtStatus.InvalidParameter); }
        return (control, outputLength);
    }
}
