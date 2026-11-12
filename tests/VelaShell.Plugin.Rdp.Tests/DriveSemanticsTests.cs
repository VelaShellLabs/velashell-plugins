using System.Text;
using VelaShell.Rdp;
using VelaShell.Rdp.Channels;
using VelaShell.Rdp.Wire;

namespace VelaShell.Plugin.Rdp.Tests;

/// <summary>
/// NT file semantics of drive redirection (MS-RDPEFS over MS-FSCC information classes) that the server relies
/// on and that must hold identically on every client OS: directory queries, SetInformation, rename.
/// </summary>
[TestClass]
public sealed class DriveSemanticsTests
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileOpen = 1, FileCreate = 2, FileOpenIf = 3, FileDirectoryFile = 1;
    private const uint FileWriteAttributes = 0x100, MaximumAllowed = 0x02000000, GenericAll = 0x10000000;

    private static string NewShare()
    {
        string share = Path.Combine(Path.GetTempPath(), "vela-drive-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(share);
        return share;
    }

    private static void WriteFile(string share, string name, string content)
    {
        DriveFile file = DriveFile.Create(share, "\\" + name, GenericWrite, FileCreate, 0, 0);
        _ = file.Write(0, Encoding.ASCII.GetBytes(content));
        file.Close();
    }

    private static List<string> Names(DriveFile directory)
    {
        var names = new List<string>();
        while (true)
        {
            var w = new ByteWriter(256);
            if (!directory.QueryDirectory(DriveFile.FileNamesInformation, w))
            {
                return names;
            }

            ReadOnlySpan<byte> entry = w.Span;
            int nameLength = BitConverter.ToInt32(entry[12..16]);
            names.Add(Encoding.Unicode.GetString(entry.Slice(16, nameLength)));
        }
    }

    [TestMethod]
    public void QueryForAMissingNameMatchesNothingAndOmitsDotEntries()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "a.txt", "x");
            _ = Directory.CreateDirectory(Path.Combine(share, "sub"));
            DriveFile root = DriveFile.Create(share, "\\", GenericRead, FileOpen, FileDirectoryFile, 0);

            Assert.IsFalse(root.BeginEnumeration("\\missing.txt"), "A missing name must not match '.' or '..'.");
            Assert.IsEmpty(Names(root));

            Assert.IsTrue(root.BeginEnumeration("\\*.txt"));
            CollectionAssert.AreEqual(new[] { "a.txt" }, Names(root));

            Assert.IsTrue(root.BeginEnumeration("\\a.txt"));
            CollectionAssert.AreEqual(new[] { "a.txt" }, Names(root));

            Assert.IsTrue(root.BeginEnumeration("\\*"));
            CollectionAssert.AreEquivalent(new[] { ".", "..", "a.txt", "sub" }, Names(root));
            root.Close();
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void EndOfFileTruncatesSoAShorterRewriteLeavesNoStaleTail()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "doc.txt", "0123456789");
            DriveFile file = DriveFile.Create(share, "\\doc.txt", GenericWrite, FileOpen, 0, 0);
            _ = file.Write(0, "abc"u8);
            file.SetEndOfFile(3);
            file.Close();

            string content = File.ReadAllText(Path.Combine(share, "doc.txt"));
            Assert.AreEqual("abc", content);
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void AllocationBelowEndOfFileTruncatesAndAboveLeavesTheFile()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "doc.txt", "0123456789");
            DriveFile file = DriveFile.Create(share, "\\doc.txt", GenericWrite, FileOpen, 0, 0);
            file.SetAllocation(4096);
            file.SetAllocation(4);
            file.Close();

            string content = File.ReadAllText(Path.Combine(share, "doc.txt"));
            Assert.AreEqual("0123", content);
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void BasicInformationAppliesTimesAndReadOnlyAndZeroMeansUnchanged()
    {
        string share = NewShare();
        string path = Path.Combine(share, "doc.txt");
        try
        {
            WriteFile(share, "doc.txt", "x");
            var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            DriveFile file = DriveFile.Create(share, "\\doc.txt", GenericWrite, FileOpen, 0, 0);
            file.SetBasic(0, 0, stamp.ToFileTimeUtc(), (uint)FileAttributes.ReadOnly);
            DateTime accessBefore = File.GetLastAccessTimeUtc(path);
            file.SetBasic(0, -1, 0, 0);
            file.Close();

            Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(path));
            Assert.AreEqual(accessBefore, File.GetLastAccessTimeUtc(path));
            Assert.IsTrue(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); }
            Directory.Delete(share, recursive: true);
        }
    }

    [TestMethod]
    public void RenameOntoAnExistingFileNeedsReplaceIfExists()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "new.txt", "new");
            WriteFile(share, "keep.txt", "keep");
            DriveFile file = DriveFile.Create(share, "\\new.txt", GenericWrite, FileOpen, 0, 0);

            DriveStatusException refused = Assert.Throws<DriveStatusException>(() => file.Rename("\\keep.txt", replaceIfExists: false));
            Assert.AreEqual(NtStatus.ObjectNameCollision, refused.Status);
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(share, "keep.txt")));

            file.Rename("\\keep.txt", replaceIfExists: true);
            Assert.IsFalse(File.Exists(Path.Combine(share, "new.txt")));
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(share, "keep.txt")));
            file.Close();
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void RenamedHandleKeepsWorkingUnderItsNewName()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "draft.tmp", "abc");
            DriveFile file = DriveFile.Create(share, "\\draft.tmp", GenericWrite, FileOpen, 0, 0);
            file.Rename("\\final.txt", replaceIfExists: false);

            file.QueryInformation(DriveFile.FileStandardInformation, new ByteWriter(64));
            _ = file.Write(3, "def"u8);
            file.Close();

            Assert.AreEqual("abcdef", File.ReadAllText(Path.Combine(share, "final.txt")));
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void RenameNeverReplacesADirectory()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "a.txt", "a");
            _ = Directory.CreateDirectory(Path.Combine(share, "folder"));
            DriveFile file = DriveFile.Create(share, "\\a.txt", GenericWrite, FileOpen, 0, 0);

            DriveStatusException refused = Assert.Throws<DriveStatusException>(() => file.Rename("\\folder", replaceIfExists: true));
            Assert.AreEqual(NtStatus.AccessDenied, refused.Status);
            Assert.IsTrue(Directory.Exists(Path.Combine(share, "folder")));
            file.Close();
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void CaseOnlyRenameIsNotACollisionWithItself()
    {
        string share = NewShare();
        try
        {
            WriteFile(share, "readme.txt", "r");
            DriveFile file = DriveFile.Create(share, "\\readme.txt", GenericWrite, FileOpen, 0, 0);
            file.Rename("\\README.txt", replaceIfExists: false);
            file.Close();

            CollectionAssert.AreEqual(new[] { "README.txt" }, Directory.GetFiles(share).Select(Path.GetFileName).ToArray());
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    private static void WithReadOnlyFile(Action<string, string> test)
    {
        string share = NewShare();
        string path = Path.Combine(share, "ro.txt");
        File.WriteAllText(path, "ro");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try { test(share, path); }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(share, recursive: true);
        }
    }

    [TestMethod]
    public void AReadOnlyFileOpensForReadingWithoutDemandingWriteAccess() => WithReadOnlyFile((share, _) =>
    {
        // FILE_OPEN_IF and FILE_WRITE_ATTRIBUTES used to force a writable stream and so failed with access denied.
        DriveFile openIf = DriveFile.Create(share, "\\ro.txt", GenericRead, FileOpenIf, 0, 0);
        CollectionAssert.AreEqual("ro"u8.ToArray(), openIf.Read(0, 16));
        openIf.Close();

        DriveFile attributes = DriveFile.Create(share, "\\ro.txt", GenericRead | FileWriteAttributes, FileOpen, 0, 0);
        attributes.Close();

        DriveStatusException write = Assert.Throws<DriveStatusException>(() => DriveFile.Create(share, "\\ro.txt", GenericWrite, FileOpen, 0, 0));
        Assert.AreEqual(NtStatus.AccessDenied, write.Status);
    });

    [TestMethod]
    public void MaximumAllowedFallsBackToReadAndGenericAllWrites()
    {
        WithReadOnlyFile((share, _) =>
        {
            DriveFile file = DriveFile.Create(share, "\\ro.txt", MaximumAllowed, FileOpen, 0, 0);
            CollectionAssert.AreEqual("ro"u8.ToArray(), file.Read(0, 16));
            Assert.AreEqual(NtStatus.AccessDenied, Assert.Throws<DriveStatusException>(() => file.Write(0, "x"u8)).Status);
            file.Close();
        });

        string share = NewShare();
        try
        {
            WriteFile(share, "rw.txt", "rw");
            DriveFile all = DriveFile.Create(share, "\\rw.txt", GenericAll, FileOpen, 0, 0);
            _ = all.Write(0, "RW"u8);
            all.Close();
            Assert.AreEqual("RW", File.ReadAllText(Path.Combine(share, "rw.txt")));
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void DeleteIsRefusedUpFrontWhenItCannotSucceedAndCanBeWithdrawn()
    {
        WithReadOnlyFile((share, path) =>
        {
            DriveFile file = DriveFile.Create(share, "\\ro.txt", GenericRead, FileOpen, 0, 0);
            Assert.AreEqual(NtStatus.CannotDelete, Assert.Throws<DriveStatusException>(() => file.SetDeletePending(true)).Status);
            file.Close();
            Assert.IsTrue(File.Exists(path));
        });

        string share = NewShare();
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(share, "full"));
            WriteFile(share, "full\\inner.txt", "x");
            DriveFile directory = DriveFile.Create(share, "\\full", GenericRead, FileOpen, FileDirectoryFile, 0);
            Assert.AreEqual(NtStatus.DirectoryNotEmpty, Assert.Throws<DriveStatusException>(() => directory.SetDeletePending(true)).Status);
            directory.Close();

            WriteFile(share, "keep.txt", "k");
            DriveFile keep = DriveFile.Create(share, "\\keep.txt", GenericWrite, FileOpen, 0, 0);
            keep.SetDeletePending(true);
            keep.SetDeletePending(false);   // DeletePending = 0 withdraws the delete
            keep.Close();
            Assert.IsTrue(File.Exists(Path.Combine(share, "keep.txt")));

            DriveFile gone = DriveFile.Create(share, "\\keep.txt", GenericWrite, FileOpen, 0, 0);
            gone.SetDeletePending(true);
            gone.Close();
            Assert.IsFalse(File.Exists(Path.Combine(share, "keep.txt")));
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void AFifoInAMappingIsRefusedInsteadOfBlockingTheChannel()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows file systems have no FIFOs.");
        }

        string share = NewShare();
        try
        {
            string fifo = Path.Combine(share, "pipe");
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", fifo)!) { mkfifo.WaitForExit(); }

            Task<DriveStatusException> open = Task.Run(() => Assert.Throws<DriveStatusException>(
                () => DriveFile.Create(share, "\\pipe", GenericRead, FileOpen, 0, 0)));
            Assert.IsTrue(open.Wait(TimeSpan.FromSeconds(10)), "Opening a FIFO must not block.");
            Assert.AreEqual(NtStatus.AccessDenied, open.Result.Status);
        }
        finally { Directory.Delete(share, recursive: true); }
    }

    [TestMethod]
    public void VolumeInformationDescribesTheMappingsOwnVolumeAndAStableSerial()
    {
        string share = NewShare();
        try
        {
            (long total, long free) = RdpdrChannel.VolumeSpace(share);
            Assert.IsGreaterThan(0L, total);
            Assert.IsGreaterThanOrEqualTo(0L, free);
            Assert.IsLessThanOrEqualTo(total, free);

            var mapping = new DriveMapping("work", share);
            Assert.AreEqual(RdpdrChannel.VolumeSerial(mapping), RdpdrChannel.VolumeSerial(mapping));
            Assert.AreNotEqual(RdpdrChannel.VolumeSerial(mapping), RdpdrChannel.VolumeSerial(new DriveMapping("docs", share)));
            WriteFile(share, "grow.bin", new string('x', 1 << 16));
            Assert.AreEqual(RdpdrChannel.VolumeSerial(mapping), RdpdrChannel.VolumeSerial(new DriveMapping("work", share)),
                "The serial must not follow the free space.");
        }
        finally { Directory.Delete(share, recursive: true); }
    }
}
