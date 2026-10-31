using System.Globalization;

namespace VelaShell.Plugin.Rdp;

/// <summary>What a local disk entry is; decides its label on the drive-mapping page.</summary>
internal enum LocalDiskKind
{
    Fixed,
    Removable,
    Network,
    Optical,
    Root,
    Home,
    Mount
}

/// <summary>
/// A local disk the drive-mapping page offers as a checkbox. <see cref="Id" /> is the stable part of the
/// setting key (<c>drive:&lt;Id&gt;</c>); <see cref="ShareName" /> is what the remote desktop sees under
/// <c>\\tsclient</c> (1–8 ASCII letters, digits or underscores).
/// </summary>
internal sealed record LocalDisk(string Id, string ShareName, string Path, LocalDiskKind Kind);

/// <summary>
/// Lists local disks for the drive-mapping page, mstsc-style, <b>without touching any media</b>: on
/// Windows only drive letters and drive types are read (no volume query), so a disconnected network
/// drive or an empty optical drive cannot stall plugin activation or the disk watcher. On Linux and
/// macOS the root, the home folder and removable-media mounts are offered.
/// </summary>
internal static class LocalDisks
{
    /// <summary>RDPDR redirection is capped at 32 devices per session (see <see cref="RdpSettings" />).</summary>
    internal const int MaxDisks = 32;

    private static readonly string[] MountRoots = ["/media/", "/run/media/", "/mnt/", "/Volumes/"];

    internal static IReadOnlyList<LocalDisk> Enumerate() =>
        OperatingSystem.IsWindows() ? EnumerateWindows() : EnumerateUnix();

    private static List<LocalDisk> EnumerateWindows()
    {
        var disks = new List<LocalDisk>();
        foreach (DriveInfo drive in SafeDrives())
        {
            string name = drive.Name;
            if (name.Length < 2 || !char.IsAsciiLetter(name[0]) || name[1] != ':')
            {
                continue;
            }

            // DriveType comes from GetDriveType: it never reads the volume or contacts a server.
            LocalDiskKind? kind = drive.DriveType switch
            {
                DriveType.Fixed or DriveType.Ram or DriveType.Unknown => LocalDiskKind.Fixed,
                DriveType.Removable => LocalDiskKind.Removable,
                DriveType.Network => LocalDiskKind.Network,
                DriveType.CDRom => LocalDiskKind.Optical,
                _ => null
            };
            if (kind is null || disks.Count >= MaxDisks)
            {
                continue;
            }

            string letter = char.ToUpperInvariant(name[0]).ToString();
            disks.Add(new(letter, letter, letter + @":\", kind.Value));
        }
        return disks;
    }

    private static IReadOnlyList<LocalDisk> EnumerateUnix()
    {
        var mounts = new List<string>();
        foreach (DriveInfo drive in SafeDrives())
        {
            string root = drive.Name.TrimEnd('/');
            if (IsMediaMount(root) && !IsLink(root))
            {
                mounts.Add(root);
            }
        }
        return FromUnix(mounts, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Builds the Linux/macOS list: root, home, then the given mounts in path order. Split from
    /// enumeration so share-name derivation is testable on any OS.
    /// </summary>
    internal static IReadOnlyList<LocalDisk> FromUnix(IEnumerable<string> mounts, string? home)
    {
        var disks = new List<LocalDisk> { new("/", "root", "/", LocalDiskKind.Root) };
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "root" };
        if (!string.IsNullOrEmpty(home) && home != "/")
        {
            disks.Add(new("~", ShareName("home", taken), home, LocalDiskKind.Home));
        }

        foreach (string mount in mounts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (disks.Count >= MaxDisks)
            {
                break;
            }

            disks.Add(new(mount, ShareName(System.IO.Path.GetFileName(mount), taken), mount, LocalDiskKind.Mount));
        }
        return disks;
    }

    /// <summary>
    /// Derives an RDPDR share name (1–8 ASCII letters, digits or underscores, unique within
    /// <paramref name="taken" />) from a mount's label, adding a numeric suffix on collision.
    /// </summary>
    internal static string ShareName(string label, ISet<string> taken)
    {
        string stem = string.Concat(label.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        if (stem.Length == 0)
        {
            stem = "disk";
        }

        if (stem.Length > 8)
        {
            stem = stem[..8];
        }

        string name = stem;
        for (int n = 2; !taken.Add(name); n++)
        {
            string suffix = n.ToString(CultureInfo.InvariantCulture);
            name = stem[..Math.Min(stem.Length, 8 - suffix.Length)] + suffix;
        }
        return name;
    }

    private static bool IsMediaMount(string root)
    {
        foreach (string prefix in MountRoots)
        {
            if (root.Length > prefix.Length && root.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // macOS mirrors the boot volume as /Volumes/<name> → "/"; offering it again would duplicate the root.
    private static bool IsLink(string path)
    {
        try
        {
            return new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static DriveInfo[] SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
