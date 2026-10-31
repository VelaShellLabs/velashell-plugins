namespace VelaShell.Rdp;

/// <summary>A redirected local folder, exposed to the remote desktop as a drive device under \\tsclient.</summary>
/// <param name="Name">The share name (1–8 ASCII letters, digits or underscores).</param>
/// <param name="LocalPath">The absolute local path to share with read/write access.</param>
public readonly record struct DriveMapping(string Name, string LocalPath);
