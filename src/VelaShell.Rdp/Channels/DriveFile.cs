using System.Text;
using VelaShell.Rdp.Wire;

namespace VelaShell.Rdp.Channels;

/// <summary>
/// One open file/directory within a redirected drive (MS-RDPEFS device I/O). Maps NT file
/// semantics onto .NET file APIs and serializes the FsInformation classes the server queries.
/// Every path is confined to the mapping's base directory; traversal and reparse points are
/// rejected, matching the former native bridge.
/// </summary>
internal sealed class DriveFile
{
    // CreateDisposition
    private const uint FileSupersede = 0, FileOpen = 1, FileCreate = 2, FileOpenIf = 3, FileOverwrite = 4, FileOverwriteIf = 5;
    // CreateOptions
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileDeleteOnClose = 0x00001000;
    // FsInformationClass (query/set)
    internal const uint FileDirectoryInformation = 1, FileFullDirectoryInformation = 2, FileBothDirectoryInformation = 3;
    internal const uint FileBasicInformation = 4, FileStandardInformation = 5, FileNamesInformation = 12;
    internal const uint FileRenameInformation = 10, FileDispositionInformation = 13;
    internal const uint FileAllocationInformation = 19, FileEndOfFileInformation = 20, FileAttributeTagInformation = 35;
    // Information (create result)
    internal const byte FileSuperseded = 0, FileOpened = 1, FileOverwritten = 3;

    private readonly string _basePath;
    internal string FullPath { get; }
    internal bool IsDirectory { get; }
    private FileStream? _stream;
    private bool _deleteOnClose;
    private List<string>? _enumeration;
    private int _enumIndex;

    internal byte Information { get; private set; }

    private DriveFile(string basePath, string fullPath, bool isDirectory)
    {
        _basePath = basePath;
        FullPath = fullPath;
        IsDirectory = isDirectory;
    }

    /// <summary>Opens or creates a file/dir for an IRP_MJ_CREATE. Throws <see cref="DriveStatusException"/> on NT error.</summary>
    internal static DriveFile Create(string basePath, string rdpPath, uint desiredAccess, uint disposition,
        uint options, uint fileAttributes)
    {
        string full = ResolvePath(basePath, rdpPath);
        RejectLinks(basePath, full);
        bool wantDirectory = (options & FileDirectoryFile) != 0;
        bool exists = File.Exists(full) || Directory.Exists(full);
        bool existingDir = Directory.Exists(full);

        var file = new DriveFile(basePath, full, existingDir || (wantDirectory && !File.Exists(full)))
        {
            _deleteOnClose = (options & FileDeleteOnClose) != 0
        };

        if (file.IsDirectory)
        {
            switch (disposition)
            {
                case FileOpen:
                    if (!existingDir)
                    {
                        throw new DriveStatusException(existingDir || exists ? NtStatus.NotADirectory : NtStatus.NoSuchFile);
                    }

                    file.Information = FileOpened;
                    break;
                case FileCreate:
                    if (existingDir)
                    {
                        throw new DriveStatusException(NtStatus.ObjectNameCollision);
                    }

                    _ = Directory.CreateDirectory(full);
                    file.Information = FileSuperseded;
                    break;
                case FileOpenIf:
                    if (!existingDir)
                    {
                        _ = Directory.CreateDirectory(full);
                    }

                    file.Information = existingDir ? FileOpened : FileSuperseded;
                    break;
                default:
                    if (!existingDir)
                    {
                        _ = Directory.CreateDirectory(full);
                    }

                    file.Information = FileSuperseded;
                    break;
            }
            return file;
        }

        // Regular file.
        if (disposition == FileOpen && !exists)
        {
            throw new DriveStatusException(NtStatus.NoSuchFile);
        }

        if (disposition == FileCreate && exists)
        {
            throw new DriveStatusException(NtStatus.ObjectNameCollision);
        }

        FileMode mode = disposition switch
        {
            FileSupersede => FileMode.Create,
            FileOpen => FileMode.Open,
            FileCreate => FileMode.CreateNew,
            FileOpenIf => FileMode.OpenOrCreate,
            FileOverwrite => FileMode.Truncate,
            FileOverwriteIf => FileMode.Create,
            _ => FileMode.OpenOrCreate
        };
        bool write = (desiredAccess & 0x40000000) != 0 ||                        // GENERIC_WRITE
                     (desiredAccess & (0x0002 | 0x0004 | 0x0006 | 0x0100)) != 0 || // FILE_WRITE_DATA/APPEND/EA
                     mode is FileMode.Create or FileMode.CreateNew or FileMode.Truncate or FileMode.OpenOrCreate;
        try
        {
            file._stream = new FileStream(full, mode, write ? FileAccess.ReadWrite : FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException) { throw new DriveStatusException(NtStatus.NoSuchFile); }
        catch (DirectoryNotFoundException) { throw new DriveStatusException(NtStatus.ObjectPathNotFound); }
        catch (UnauthorizedAccessException) { throw new DriveStatusException(NtStatus.AccessDenied); }
        catch (IOException) when (disposition == FileCreate) { throw new DriveStatusException(NtStatus.ObjectNameCollision); }

        file.Information = disposition switch
        {
            FileOpenIf => exists ? FileOpened : FileSuperseded,
            FileOverwriteIf or FileOverwrite => FileOverwritten,
            _ => exists && disposition == FileOpen ? FileOpened : FileSuperseded
        };
        _ = fileAttributes;
        return file;
    }

    /// <summary>Largest read served per request. Windows asks for ≤64 KB; a short read is legal, so larger requests are clamped.</summary>
    internal const int MaxIo = 1024 * 1024;

    /// <summary>How far past end-of-file a write may start; one write at a huge offset would otherwise allocate disk up to it.</summary>
    internal const long MaxWriteGap = 256L * 1024 * 1024;

    internal byte[] Read(ulong offset, uint length)
    {
        _stream ??= OpenForContent(FileAccess.Read);
        if (offset > (ulong)_stream.Length)
        {
            return [];
        }

        _ = _stream.Seek((long)offset, SeekOrigin.Begin);
        int toRead = (int)Math.Min(Math.Min(length, (ulong)MaxIo), (ulong)_stream.Length - offset);
        byte[] buffer = new byte[toRead];
        int read = 0;
        while (read < toRead)
        {
            int next = _stream.Read(buffer, read, toRead - read);
            if (next == 0)
            {
                break;
            }

            read += next;
        }
        return read == toRead ? buffer : buffer[..read];
    }

    internal uint Write(ulong offset, ReadOnlySpan<byte> data)
    {
        _stream ??= OpenForContent(FileAccess.ReadWrite);
        if (!_stream.CanWrite)
        {
            throw new DriveStatusException(NtStatus.AccessDenied);
        }

        if (offset > (ulong)(_stream.Length + MaxWriteGap))
        {
            throw new DriveStatusException(NtStatus.DiskFull);
        }

        _ = _stream.Seek((long)offset, SeekOrigin.Begin);
        _stream.Write(data);
        _stream.Flush();
        return (uint)data.Length;
    }

    private FileStream OpenForContent(FileAccess access)
    {
        if (IsDirectory)
        {
            throw new DriveStatusException(NtStatus.FileIsADirectory);
        }

        return new FileStream(FullPath, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
    }

    internal void Close()
    {
        _stream?.Dispose();
        _stream = null;
        if (!_deleteOnClose)
        {
            return;
        }

        try
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: false);
            }
            else if (File.Exists(FullPath))
            {
                File.Delete(FullPath);
            }
        }
        catch { /* best-effort delete-on-close */ }
    }

    internal void MarkForDeletion() => _deleteOnClose = true;

    internal void Rename(string newRdpPath)
    {
        string target = ResolvePath(_basePath, newRdpPath);
        RejectLinks(_basePath, target);
        if (IsDirectory)
        {
            Directory.Move(FullPath, target);
        }
        else { _stream?.Dispose(); _stream = null; File.Move(FullPath, target, overwrite: true); }
    }

    // ---- Information serialization ----

    internal void QueryInformation(uint infoClass, ByteWriter output)
    {
        FileSystemInfo info = IsDirectory ? new DirectoryInfo(FullPath) : new FileInfo(FullPath);
        if (!info.Exists)
        {
            throw new DriveStatusException(NtStatus.NoSuchFile);
        }

        long size = info is FileInfo fi ? fi.Length : 0;
        uint attributes = (uint)info.Attributes;

        switch (infoClass)
        {
            case FileBasicInformation:
                output.U32LE(36);
                WriteTime(output, info.CreationTimeUtc);
                WriteTime(output, info.LastAccessTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                output.U32LE(attributes);
                break;
            case FileStandardInformation:
                output.U32LE(22);
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));   // AllocationSize
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));   // EndOfFile
                output.U32LE(0);                                              // NumberOfLinks
                output.U8(_deleteOnClose ? (byte)1 : (byte)0);                // DeletePending
                output.U8(IsDirectory ? (byte)1 : (byte)0);                   // Directory
                break;
            case FileAttributeTagInformation:
                output.U32LE(8);
                output.U32LE(attributes);
                output.U32LE(0);
                break;
            default:
                throw new DriveStatusException(NtStatus.Unsuccessful);
        }
    }

    internal void BeginEnumeration(string pattern)
    {
        string spec = pattern.TrimStart('\\');
        string search = spec.Length == 0 ? "*" : Path.GetFileName(spec.Replace('\\', Path.DirectorySeparatorChar));
        if (search.Length == 0)
        {
            search = "*";
        }

        var entries = new List<string> { ".", ".." };
        try
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(FullPath, search))
            {
                entries.Add(Path.GetFileName(path));
            }
        }
        catch (DirectoryNotFoundException) { throw new DriveStatusException(NtStatus.NoSuchFile); }
        _enumeration = entries;
        _enumIndex = 0;
    }

    /// <summary>Writes one directory entry; returns false (and resets) when enumeration is exhausted.</summary>
    internal bool QueryDirectory(uint infoClass, ByteWriter output)
    {
        if (_enumeration is null || _enumIndex >= _enumeration.Count)
        {
            return false;
        }

        string name = _enumeration[_enumIndex++];
        string path = Path.Combine(FullPath, name);
        FileSystemInfo info = name is "." or ".." || Directory.Exists(path) ? new DirectoryInfo(name is "." or ".." ? FullPath : path) : new FileInfo(path);
        long size = info is FileInfo f ? f.Length : 0;
        uint attributes = info.Exists ? (uint)info.Attributes : 0x10; // treat missing as dir-ish
        byte[] nameBytes = Encoding.Unicode.GetBytes(name);

        switch (infoClass)
        {
            case FileBothDirectoryInformation:
                output.U32LE((uint)(93 + nameBytes.Length));
                output.U32LE(0);                 // NextEntryOffset
                output.U32LE(0);                 // FileIndex
                WriteTime(output, info.CreationTimeUtc);
                WriteTime(output, info.LastAccessTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));   // EndOfFile
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));   // AllocationSize
                output.U32LE(attributes);
                output.U32LE((uint)nameBytes.Length);
                output.U32LE(0);                 // EaSize
                output.U8(0);                    // ShortNameLength
                output.Zeros(24);                // ShortName
                output.Bytes(nameBytes);
                break;
            case FileFullDirectoryInformation:
                output.U32LE((uint)(68 + nameBytes.Length));
                output.U32LE(0); output.U32LE(0);
                WriteTime(output, info.CreationTimeUtc);
                WriteTime(output, info.LastAccessTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));
                output.U32LE(attributes);
                output.U32LE((uint)nameBytes.Length);
                output.U32LE(0);                 // EaSize
                output.Bytes(nameBytes);
                break;
            case FileDirectoryInformation:
                output.U32LE((uint)(64 + nameBytes.Length));
                output.U32LE(0); output.U32LE(0);
                WriteTime(output, info.CreationTimeUtc);
                WriteTime(output, info.LastAccessTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                WriteTime(output, info.LastWriteTimeUtc);
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));
                output.U32LE((uint)size); output.U32LE((uint)(size >> 32));
                output.U32LE(attributes);
                output.U32LE((uint)nameBytes.Length);
                output.Bytes(nameBytes);
                break;
            case FileNamesInformation:
                output.U32LE((uint)(12 + nameBytes.Length));
                output.U32LE(0); output.U32LE(0);
                output.U32LE((uint)nameBytes.Length);
                output.Bytes(nameBytes);
                break;
            default:
                throw new DriveStatusException(NtStatus.Unsuccessful);
        }
        return true;
    }

    private static void WriteTime(ByteWriter w, DateTime utc)
    {
        long ft;
        try { ft = utc.ToFileTimeUtc(); } catch { ft = 0; }
        w.U32LE((uint)ft);
        w.U32LE((uint)(ft >> 32));
    }

    // ---- Path confinement ----

    /// <summary>
    /// Maps a server path onto the mapping. The path is rebuilt from its components — RDP uses backslashes,
    /// xrdp sends forward slashes, both are accepted — and any component that could step outside or alias
    /// something else is refused: "." / "..", NUL/control characters, and on Windows alternate data streams
    /// (':'), device names (CON, NUL, COM1 …) and trailing dots/spaces, which Windows silently strips.
    /// The prefix check stays as a second line, case-sensitive where the file system usually is.
    /// </summary>
    internal static string ResolvePath(string basePath, string rdpPath)
    {
        string root = Path.GetFullPath(basePath);
        string full = root;
        foreach (string component in rdpPath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsSafeComponent(component))
            {
                throw new DriveStatusException(NtStatus.AccessDenied);
            }

            full = Path.Join(full, component);   // Join copes with roots ("/", "C:\") that already end in a separator
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, comparison) && !full.StartsWith(prefix, comparison))
        {
            throw new DriveStatusException(NtStatus.AccessDenied);
        }

        return full;
    }

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    ];

    internal static bool IsSafeComponent(string component)
    {
        if (component is "." or ".." || component.Any(char.IsControl))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        if (component.Contains(':') || component.EndsWith('.') || component.EndsWith(' '))
        {
            return false;
        }

        string stem = component.Split('.')[0].TrimEnd(' ');
        return !DeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Refuses a path that goes through a symbolic link or junction anywhere below the mapping root (the root
    /// itself may be a link — the user chose it). Checked for existing targets AND for the parents of a path
    /// about to be created or renamed onto, so a link already inside the mapping cannot be used to create or
    /// overwrite files outside it. Only name-surrogate links count: cloud placeholders (OneDrive) are reparse
    /// points too but stay where they are.
    /// </summary>
    private static void RejectLinks(string basePath, string full)
    {
        int rootLength = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath)).Length;
        for (string? current = Path.TrimEndingDirectorySeparator(full);
             current is not null && current.Length > rootLength;
             current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new DriveStatusException(NtStatus.AccessDenied);
            }
        }
    }
}

/// <summary>NTSTATUS values used by the drive redirection device.</summary>
internal static class NtStatus
{
    internal const uint Success = 0x00000000;
    internal const uint NoMoreFiles = 0x80000006;
    internal const uint Unsuccessful = 0xC0000001;
    internal const uint NotImplemented = 0xC0000002;
    internal const uint NoSuchFile = 0xC000000F;   // FreeRDP maps ERROR_FILE_NOT_FOUND here; xrdp needs this to create
    internal const uint AccessDenied = 0xC0000022;
    internal const uint EndOfFile = 0xC0000011;
    internal const uint ObjectNameNotFound = 0xC0000034;
    internal const uint ObjectNameCollision = 0xC0000035;
    internal const uint ObjectPathNotFound = 0xC000003A;
    internal const uint NotSupported = 0xC00000BB;
    internal const uint InvalidParameter = 0xC000000D;
    internal const uint DiskFull = 0xC000007F;
    internal const uint InsufficientResources = 0xC000009A;
    internal const uint FileIsADirectory = 0xC00000BA;
    internal const uint NotADirectory = 0xC0000103;
    internal const uint DirectoryNotEmpty = 0xC0000101;
}

internal sealed class DriveStatusException(uint status) : Exception
{
    internal uint Status { get; } = status;
}
