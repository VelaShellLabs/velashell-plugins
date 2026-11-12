using System.Runtime.InteropServices;

namespace VelaShell.Plugin.Rdp;

/// <summary>
/// Safely opens a regular clipboard file on Unix for local→remote file transfer, via the OS
/// libc (no bundled native code). Ports the former native bridge: O_NONBLOCK|O_NOFOLLOW so a
/// FIFO cannot block the open and a final-component symlink is refused, then a handle-based
/// stat to confirm a regular file. Returns an owned fd, or -1 on any error or on Windows (which
/// uses the .NET API).
/// <para>
/// The flag values and stat layouts are ABI facts that differ by OS and CPU, so each is chosen
/// explicitly: Linux uses <c>statx</c>, whose layout is the same on every architecture (glibc
/// 2.28+, kernel 4.11+), instead of <c>struct stat</c>, whose <c>st_mode</c> offset differs between
/// x86-64 (24) and arm64 (16); macOS uses the 64-bit-inode <c>fstat</c>, exported as
/// <c>fstat$INODE64</c> on x86-64 and as plain <c>fstat</c> on arm64.
/// </para>
/// </summary>
internal static partial class UnixClipboardFile
{
    // Linux: O_RDONLY=0, O_NONBLOCK=0x800, O_CLOEXEC=0x80000 on every architecture; O_NOFOLLOW is 0x8000 on
    // arm, arm64 and ppc64le and 0x20000 elsewhere (x86, x86-64, riscv64, loongarch64, s390x).
    private const int LinuxCommonFlags = 0x800 | 0x80000;
    // macOS: O_NONBLOCK=0x0004, O_NOFOLLOW=0x0100, O_CLOEXEC=0x1000000.
    private const int MacFlags = 0x0004 | 0x0100 | 0x1000000;
    private const uint SIfmt = 0xF000, SIfreg = 0x8000;

    // statx(fd, "", AT_EMPTY_PATH, STATX_TYPE, &buf): stx_mode is a u16 at offset 28 of struct statx.
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x0001;
    private const int StatxModeOffset = 28;
    private const int StatxSize = 256;

    // macOS struct stat (64-bit inode): st_dev i32 @0, st_mode u16 @4.
    private const int MacModeOffset = 4;
    private const int MacStatSize = 256;

    internal static int LinuxNoFollow(Architecture architecture) => architecture switch
    {
        Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => 0x8000,
        _ => 0x20000
    };

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

        bool mac = OperatingSystem.IsMacOS();
        int flags = mac ? MacFlags : LinuxCommonFlags | LinuxNoFollow(RuntimeInformation.ProcessArchitecture);
        int fd = open(path, flags);
        if (fd < 0)
        {
            return -1;
        }

        if (!IsRegularFile(fd, mac))
        {
            _ = close(fd);
            return -1;
        }
        return fd;
    }

    private static bool IsRegularFile(int fd, bool mac)
    {
        try
        {
            if (mac)
            {
                byte[] stat = new byte[MacStatSize];
                int rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstatMacX64(fd, stat) : fstatMac(fd, stat);
                return rc == 0 && (BitConverter.ToUInt16(stat, MacModeOffset) & SIfmt) == SIfreg;
            }

            byte[] statx = new byte[StatxSize];
            return statxLinux(fd, "", AtEmptyPath, StatxType, statx) == 0
                && (BitConverter.ToUInt16(statx, StatxModeOffset) & SIfmt) == SIfreg;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // An unexpected libc: refuse the file rather than guess its type.
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int statxLinux(int dirfd, string path, int flags, uint mask, [Out] byte[] buffer);

    // arm64 macOS has only the 64-bit-inode stat, under the plain name.
    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static partial int fstatMac(int fd, [Out] byte[] buffer);

    // x86-64 macOS keeps the legacy 32-bit-inode layout under "fstat"; the 64-bit-inode one has this suffix.
    [LibraryImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static partial int fstatMacX64(int fd, [Out] byte[] buffer);
}
