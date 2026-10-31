using System.Runtime.InteropServices;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// Safely opens a regular clipboard file on Unix for local→remote file transfer, via the OS
/// libc (no bundled native code). Ports the former native bridge: O_NONBLOCK|O_NOFOLLOW so a
/// FIFO cannot block the open and a final-component symlink is refused, then fstat to confirm a
/// regular file. Returns an owned fd, or -1 on any error or on Windows (which uses the .NET API).
/// </summary>
internal static partial class UnixClipboardFile
{
    // O_RDONLY=0. Linux: O_NONBLOCK=0x800, O_NOFOLLOW=0x20000, O_CLOEXEC=0x80000.
    private const int LinuxFlags = 0x800 | 0x20000 | 0x80000;
    // macOS: O_NONBLOCK=0x0004, O_NOFOLLOW=0x0100, O_CLOEXEC=0x1000000.
    private const int MacFlags = 0x0004 | 0x0100 | 0x1000000;
    private const uint SIfmt = 0xF000, SIfreg = 0x8000;

    internal static int Open(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return -1;
        }

        if (OperatingSystem.IsWindows())
        {
            return -1;
        }

        int fd = open(path, OperatingSystem.IsMacOS() ? MacFlags : LinuxFlags);
        if (fd < 0)
        {
            return -1;
        }

        byte[] stat = new byte[256];
        int rc = OperatingSystem.IsMacOS() ? fstatMac(fd, stat) : fstatLinux(fd, stat);
        // st_mode: Linux offset 24 (u32); macOS offset 4 (u16).
        uint mode = OperatingSystem.IsMacOS()
            ? BitConverter.ToUInt16(stat, 4)
            : BitConverter.ToUInt32(stat, 24);
        if (rc != 0 || (mode & SIfmt) != SIfreg)
        {
            _ = close(fd);
            return -1;
        }
        return fd;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int fstatLinux(int fd, [Out] byte[] buffer);

    // macOS exposes the 64-bit-inode variant under this symbol.
    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int fstatMac(int fd, [Out] byte[] buffer);
}
