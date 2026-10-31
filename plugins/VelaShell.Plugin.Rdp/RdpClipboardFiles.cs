using Microsoft.Win32.SafeHandles;

namespace VelaShell.Plugin.Rdp;

internal sealed class RdpClipboardFiles : IDisposable
{
    internal const long MaxTransferBytes = 1024L * 1024 * 1024;
    internal const int ChunkSize = 256 * 1024;
    private readonly List<SafeFileHandle?> _handles = [];
    internal List<RdpClipboardFile> Entries { get; } = [];
    private long _total;

    internal static RdpClipboardFiles Open(IEnumerable<string> paths, CancellationToken ct)
    {
        var snapshot = new RdpClipboardFiles();
        try
        {
            HashSet<string> names = [with(StringComparer.OrdinalIgnoreCase)];
            foreach (string path in paths)
            {
                string full = Path.GetFullPath(path);
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
                if (!names.Add(name))
                {
                    throw new InvalidDataException("Clipboard root file names must be unique.");
                }

                RejectLinks(full);
                snapshot.Add(full, name, ct);
            }
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private void Add(string path, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RdpClipboardCodec.ValidateFileName(name);
        if (Entries.Count >= RdpClipboardCodec.MaxFiles)
        {
            throw new InvalidDataException("Too many clipboard files.");
        }

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new InvalidDataException("Links and special files cannot be copied through RDP.");
        }

        bool directory = (attributes & FileAttributes.Directory) != 0;
        if (directory)
        {
            Entries.Add(new(name, true, 0));
            _handles.Add(null);
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                Add(child, name + "\\" + Path.GetFileName(child), ct);
            }
        }
        else
        {
            SafeFileHandle handle;
            if (OperatingSystem.IsWindows())
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);
            }
            else
            {
                int descriptor = UnixClipboardFile.Open(path);
                if (descriptor < 0)
                {
                    throw new IOException("Could not open a regular clipboard file without following links.");
                }

                handle = new SafeFileHandle(descriptor, ownsHandle: true);
            }
            try
            {
                RejectLinks(path);
                long length = RandomAccess.GetLength(handle);
                _total = checked(_total + length);
                if (_total > MaxTransferBytes)
                {
                    throw new InvalidDataException("Clipboard files exceed the 1 GiB transfer limit.");
                }

                Entries.Add(new(name, false, length));
                _handles.Add(handle);
            }
            catch { handle.Dispose(); throw; }
        }
    }

    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Clipboard paths cannot contain symbolic links.");
            }
        }
    }

    internal async Task<byte[]> ReadAsync(uint index, ulong offset, uint length, CancellationToken ct)
    {
        if (index >= Entries.Count || Entries[(int)index].IsDirectory || length > ChunkSize
            || offset > (ulong)Entries[(int)index].Length)
        {
            throw new InvalidDataException("Invalid clipboard file range.");
        }

        SafeFileHandle handle = _handles[(int)index] ?? throw new InvalidDataException("Not a regular file.");
        int count = (int)Math.Min(length, (ulong)Entries[(int)index].Length - offset);
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int next = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), checked((long)offset + read), ct).ConfigureAwait(false);
            if (next == 0)
            {
                throw new EndOfStreamException("The local clipboard file changed during transfer.");
            }

            read += next;
        }
        return buffer;
    }

    public void Dispose()
    {
        foreach (SafeFileHandle? handle in _handles)
        {
            handle?.Dispose();
        }

        _handles.Clear();
    }
}
