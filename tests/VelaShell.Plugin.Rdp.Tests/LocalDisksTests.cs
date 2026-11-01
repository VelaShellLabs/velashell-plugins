namespace VelaShell.Plugin.Rdp.Tests;

[TestClass]
public sealed class LocalDisksTests
{
    [TestMethod]
    public void UnixListOffersRootHomeThenMountsWithUniqueShareNames()
    {
        IReadOnlyList<LocalDisk> disks = LocalDisks.FromUnix(
            ["/mnt/data", "/media/joe/My USB", "/media/joe/My-USB", "/mnt/data"], "/home/joe");
        Assert.AreSequenceEqual(new[] { "/", "~", "/media/joe/My USB", "/media/joe/My-USB", "/mnt/data" }, [.. disks.Select(disk => disk.Id)]);
        Assert.AreSequenceEqual(new[] { "root", "home", "MyUSB", "MyUSB2", "data" }, [.. disks.Select(disk => disk.ShareName)]);
        Assert.AreEqual(LocalDiskKind.Home, disks[1].Kind);
        Assert.AreEqual("/home/joe", disks[1].Path);
    }

    [TestMethod]
    public void HomeIsOmittedWhenItIsTheRoot() => Assert.HasCount(1, LocalDisks.FromUnix([], "/"));

    [TestMethod]
    public void ShareNamesAreAsciiTruncatedAndDeduplicated()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.AreEqual("AVeryLon", LocalDisks.ShareName("AVeryLongLabel", taken));
        Assert.AreEqual("AVeryLo2", LocalDisks.ShareName("A-Very-Long-Label", taken));
        Assert.AreEqual("disk", LocalDisks.ShareName("中文盘", taken));
        Assert.AreEqual("disk2", LocalDisks.ShareName("", taken));
    }

    [TestMethod]
    public void EnumerationOfThisMachineYieldsUniqueValidShareNames()
    {
        IReadOnlyList<LocalDisk> disks = LocalDisks.Enumerate();
        Assert.IsLessThanOrEqualTo(LocalDisks.MaxDisks, disks.Count);
        Assert.HasCount(disks.Count, disks.Select(disk => disk.Id).Distinct(StringComparer.Ordinal));
        Assert.HasCount(disks.Count, disks.Select(disk => disk.ShareName).Distinct(StringComparer.OrdinalIgnoreCase));
        foreach (LocalDisk disk in disks)
        {
            Assert.IsTrue(disk.ShareName.Length is >= 1 and <= 8 && disk.ShareName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
                disk.ShareName);
        }
    }
}
