using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VelaShell.Rdp.Channels;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// Unix ABI facts the plugin hard-codes (flag values, structure layouts, state encodings). The pure mappings run
/// everywhere; the libc calls run on Linux and macOS, so CI's Unix jobs (x64 and arm64) exercise them.
/// </summary>
[TestClass]
public sealed class UnixAbiTests
{
    [TestMethod]
    [DataRow(0x01u, 0u, DisplayName = "SCARD_UNKNOWN")]
    [DataRow(0x02u, 1u, DisplayName = "SCARD_ABSENT")]
    [DataRow(0x04u, 2u, DisplayName = "SCARD_PRESENT")]
    [DataRow(0x0Cu, 3u, DisplayName = "PRESENT|SWALLOWED")]
    [DataRow(0x14u, 4u, DisplayName = "PRESENT|POWERED")]
    [DataRow(0x34u, 5u, DisplayName = "PRESENT|POWERED|NEGOTIABLE")]
    [DataRow(0x54u, 6u, DisplayName = "PRESENT|POWERED|SPECIFIC")]
    [DataRow(0x00050054u, 6u, DisplayName = "event counter in the high word is ignored")]
    [DataRow(0x00u, 0u, DisplayName = "nothing reported")]
    public void PcscStateBitsMapToTheWireOrdinal(uint unixState, uint wire) => Assert.AreEqual(wire, PcscSmartCardBackend.WireCardState(unixState));

    [TestMethod]
    public void LinuxNoFollowFollowsTheArchitecture()
    {
        Assert.AreEqual(0x20000, UnixClipboardFile.LinuxNoFollow(Architecture.X64));
        Assert.AreEqual(0x8000, UnixClipboardFile.LinuxNoFollow(Architecture.Arm64));
        Assert.AreEqual(0x8000, UnixClipboardFile.LinuxNoFollow(Architecture.Arm));
        Assert.AreEqual(0x8000, UnixClipboardFile.LinuxNoFollow(Architecture.Ppc64le));
        Assert.AreEqual(0x20000, UnixClipboardFile.LinuxNoFollow(Architecture.RiscV64));
    }

    [TestMethod]
    public void ClipboardFileOpensRegularFilesAndRefusesEverythingElse()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual(-1, UnixClipboardFile.Open(Path.GetTempFileName()));
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "vela-clip-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        try
        {
            string regular = Path.Combine(dir, "file.txt");
            File.WriteAllText(regular, "x");
            string link = Path.Combine(dir, "link.txt");
            _ = File.CreateSymbolicLink(link, regular);
            string fifo = Path.Combine(dir, "pipe");
            using (Process mkfifo = Process.Start("mkfifo", fifo)!) { mkfifo.WaitForExit(); }

            int fd = UnixClipboardFile.Open(regular);
            Assert.IsGreaterThanOrEqualTo(0, fd, $"A regular file must open on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}.");
            new SafeFileHandle(fd, ownsHandle: true).Dispose();

            Assert.AreEqual(-1, UnixClipboardFile.Open(link), "A final-component symlink must be refused.");
            Assert.AreEqual(-1, UnixClipboardFile.Open(dir), "A directory is not a regular file.");
            Assert.AreEqual(-1, UnixClipboardFile.Open(fifo), "A FIFO must be refused without blocking.");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
