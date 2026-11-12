using System.Runtime.InteropServices;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// The file type of a path on Unix, without following a final symbolic link. Drive redirection opens only regular
/// files: opening a FIFO blocks (and the rdpdr worker serves every drive request in turn, so one FIFO would stall
/// all drive I/O), and device files under a mapped "/" must not be read or written by the server.
/// <para>
/// .NET exposes no file type beyond directory/file, so this calls libc. The layouts are ABI facts chosen explicitly:
/// Linux uses <c>statx</c>, whose layout is the same on every architecture (glibc 2.28+, kernel 4.11+); macOS uses the
/// 64-bit-inode <c>lstat</c>, exported as <c>lstat$INODE64</c> on x86-64 and as plain <c>lstat</c> on arm64.
/// </para>
/// </summary>
internal static partial class UnixFileType
{
    private const uint SIfmt = 0xF000, SIfreg = 0x8000;
    private const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x0001;
    private const int StatxModeOffset = 28, MacModeOffset = 4, BufferSize = 256;

    /// <summary>
    /// <see langword="true" /> when <paramref name="path" /> is a regular file (not a link, FIFO, socket or device).
    /// Always <see langword="true" /> on Windows, which has none of those in a file system path.
    /// </summary>
    internal static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        byte[] buffer = new byte[BufferSize];
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                int rc = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LstatMacX64(path, buffer) : LstatMac(path, buffer);
                return rc == 0 && (BitConverter.ToUInt16(buffer, MacModeOffset) & SIfmt) == SIfreg;
            }

            return Statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType, buffer) == 0
                && (BitConverter.ToUInt16(buffer, StatxModeOffset) & SIfmt) == SIfreg;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // An unexpected libc: refuse rather than risk a blocking open.
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int dirfd, string path, int flags, uint mask, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatMac(string path, [Out] byte[] buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatMacX64(string path, [Out] byte[] buffer);
}
