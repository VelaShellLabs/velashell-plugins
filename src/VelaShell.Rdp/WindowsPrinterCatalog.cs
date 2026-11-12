using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VelaShell.Rdp.Channels;

namespace VelaShell.Rdp;

/// <summary>Read-only discovery of installed Windows queues suitable for legacy RAW redirection.</summary>
public static class WindowsPrinterCatalog
{
    /// <summary>Queries queue metadata only. Does not open a document or send print data. Run off the UI thread.</summary>
    public static IReadOnlyList<PrinterMapping> GetPrinters()
    {
        if (!OperatingSystem.IsWindows()) { return []; }
        string? defaultName = PrinterNative.GetDefaultName();
        return [.. PrinterNative.Enumerate().Where(queue => queue.IsSupported)
            .Select(queue => new PrinterMapping(queue.Name, queue.Driver,
                string.Equals(queue.Name, defaultName, StringComparison.OrdinalIgnoreCase), queue.IsNetwork))];
    }
}

internal sealed class WindowsPrinterBackend : IPrinterBackend
{
    public unsafe IPrinterJob Start(PrinterMapping printer)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Legacy printer redirection requires Windows."); }
        // A server never chooses an OpenPrinter target. Resolve only an existing locally installed selection.
        PrinterNative.QueueInfo? selected = PrinterNative.Enumerate().FirstOrDefault(queue =>
            queue.IsSupported && string.Equals(queue.Name, printer.PrinterName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(queue.Driver, printer.DriverName, StringComparison.Ordinal)) ?? throw new IOException("The selected printer queue or driver is no longer available.");
        if (!PrinterNative.OpenPrinterW(selected.Name, out PrinterNative.PrinterHandle handle, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            handle?.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            PrinterNative.QueueInfo current = PrinterNative.GetQueue(handle);
            if (!current.IsSupported || !string.Equals(current.Driver, selected.Driver, StringComparison.Ordinal))
            {
                throw new IOException("The selected printer configuration changed.");
            }
            fixed (char* documentName = "VelaShell remote print job", dataType = "RAW")
            {
                var document = new PrinterNative.DocumentInfo { DocumentName = documentName, DataType = dataType };
                if (PrinterNative.StartDocPrinterW(handle, 1, ref document) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            return new WindowsPrinterJob(handle);
        }
        catch
        {
            _ = PrinterNative.AbortPrinter(handle);
            handle.Dispose();
            throw;
        }
    }

    private sealed class WindowsPrinterJob(PrinterNative.PrinterHandle handle) : IPrinterJob
    {
        private bool _active = true;
        public unsafe int Write(ReadOnlySpan<byte> data)
        {
            if (!_active) { throw new IOException("Printer job is closed."); }
            fixed (byte* pointer = data)
            {
                if (!PrinterNative.WritePrinter(handle, (IntPtr)pointer, (uint)data.Length, out uint count))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                return checked((int)count);
            }
        }
        public void Complete()
        {
            if (!_active) { throw new IOException("Printer job is closed."); }
            if (!PrinterNative.EndDocPrinter(handle)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            _active = false;
        }
        public void Abort()
        {
            if (!_active) { return; }
            _active = false;
            if (!PrinterNative.AbortPrinter(handle)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        }
        public void Dispose()
        {
            try { Abort(); }
            finally { handle.Dispose(); }
        }
    }
}

internal static partial class PrinterNative
{
    private const int MaxBuffer = 16 * 1024 * 1024;
    internal sealed record QueueInfo(string Name, string Driver, string Ports, uint Attributes)
    {
        internal bool IsNetwork => (Attributes & 0x10) != 0;
        internal bool IsSupported => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Driver)
            && Name.Length <= 1024 && Driver.Length <= 1024 && !Name.Any(char.IsControl) && !Driver.Any(char.IsControl)
            && (Attributes & 0x8000) == 0 && !string.IsNullOrWhiteSpace(Ports)
            && !Ports.Split(',').Any(static port => port.Trim().Equals("FILE:", StringComparison.OrdinalIgnoreCase)
                || port.Trim().Equals("PORTPROMPT:", StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<QueueInfo> Enumerate()
    {
        bool success = EnumPrintersW(6, null, 2, IntPtr.Zero, 0, out uint needed, out _);
        if (!success && Marshal.GetLastPInvokeError() != 122) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        if (needed == 0) { return []; }
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ValidateSize(needed);
            uint capacity = needed;
            IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
            try
            {
                if (!EnumPrintersW(6, null, 2, buffer, capacity, out needed, out uint count))
                {
                    if (Marshal.GetLastPInvokeError() == 122) { continue; }
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                int size = Marshal.SizeOf<PrinterInfo>();
                if (count > 4096 || (ulong)count * (uint)size > capacity) { throw new IOException("Invalid printer enumeration size."); }
                var queues = new List<QueueInfo>((int)count);
                for (int index = 0; index < count; index++)
                {
                    queues.Add(ReadQueue(IntPtr.Add(buffer, index * size)));
                }
                return queues;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Printer configuration changed during enumeration.");
    }

    internal static QueueInfo GetQueue(PrinterHandle handle)
    {
        _ = GetPrinterW(handle, 2, IntPtr.Zero, 0, out uint needed);
        if (Marshal.GetLastPInvokeError() != 122) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        ValidateSize(needed);
        IntPtr buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!GetPrinterW(handle, 2, buffer, needed, out _)) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            return ReadQueue(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static unsafe string? GetDefaultName()
    {
        uint length = 0;
        _ = GetDefaultPrinterW(null, ref length);
        if (length is 0 or > 1025) { return null; }
        Span<char> name = stackalloc char[(int)length];
        name.Clear();
        fixed (char* pointer = name)
        {
            if (!GetDefaultPrinterW(pointer, ref length)) { return null; }
        }
        int terminator = name.IndexOf('\0');
        return new string(terminator < 0 ? name : name[..terminator]);
    }

    private static void ValidateSize(uint size)
    {
        if (size < Marshal.SizeOf<PrinterInfo>() || size > MaxBuffer) { throw new IOException("Printer metadata exceeds the supported size."); }
    }
    private static QueueInfo ReadQueue(IntPtr pointer)
    {
        PrinterInfo info = Marshal.PtrToStructure<PrinterInfo>(pointer);
        return new(Marshal.PtrToStringUni(info.PrinterName) ?? "", Marshal.PtrToStringUni(info.DriverName) ?? "",
            Marshal.PtrToStringUni(info.PortName) ?? "", info.Attributes);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo
    {
        internal IntPtr ServerName, PrinterName, ShareName, PortName, DriverName, Comment, Location;
        internal IntPtr DevMode, SeparatorFile, PrintProcessor, DataType, Parameters, SecurityDescriptor;
        internal uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, Jobs, AveragePpm;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DocumentInfo
    {
        internal char* DocumentName;
        internal char* OutputFile;
        internal char* DataType;
    }
    internal sealed class PrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public PrinterHandle() : base(true) { }
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }

    [LibraryImport("winspool.drv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrintersW(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);
    [LibraryImport("winspool.drv", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenPrinterW(string name, out PrinterHandle handle, IntPtr defaults);
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPrinterW(PrinterHandle handle, uint level, IntPtr buffer, uint size, out uint needed);
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetDefaultPrinterW(char* buffer, ref uint characters);
    [LibraryImport("winspool.drv", SetLastError = true)]
    internal static partial uint StartDocPrinterW(PrinterHandle handle, uint level, ref DocumentInfo info);
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WritePrinter(PrinterHandle handle, IntPtr buffer, uint length, out uint written);
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndDocPrinter(PrinterHandle handle);
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AbortPrinter(PrinterHandle handle);
    [LibraryImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(IntPtr handle);
}
