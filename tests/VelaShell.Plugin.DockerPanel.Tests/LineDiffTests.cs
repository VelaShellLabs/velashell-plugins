using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 按行比对。
/// <para>
/// 这块的价值全在"改一行只显示一行" —— 一个把整个文件都标成改过的差异视图,
/// 和没有差异视图是一样的,而且更浪费用户的时间。
/// </para>
/// </summary>
[TestClass]
public class LineDiffTests
{
    private static string Render(IReadOnlyList<DiffLine> lines) =>
        string.Join("\n", lines.Select(l => l.Marker switch
        {
            DiffMarker.Added => "+" + l.Text,
            DiffMarker.Removed => "-" + l.Text,
            DiffMarker.Changed => "~" + l.Text,
            _ => " " + l.Text
        }));

    [TestMethod]
    public void InsertingALineAtTheTopDoesNotMarkEverythingElse()
    {
        // 逐行对齐会把之后的每一行都标成改过 —— 那正是不能逐行对齐的理由。
        IReadOnlyList<DiffLine> diff = LineDiff.Compute("a\nb\nc", "new\na\nb\nc");

        Assert.AreEqual("+new\n a\n b\n c", Render(diff));
        Assert.AreEqual(1, LineDiff.CountChanged(diff));
    }

    [TestMethod]
    public void ReplacingALineShowsOneChangeNotTwo()
    {
        IReadOnlyList<DiffLine> diff = LineDiff.Compute("listen 80;", "listen 8080;");

        // 人看到的是"一行改了",不是"删一行加一行"。
        Assert.AreEqual("~listen 8080;", Render(diff));
        Assert.AreEqual(1, LineDiff.CountChanged(diff));
    }

    [TestMethod]
    public void KeepsOldAndNewLineNumbers()
    {
        IReadOnlyList<DiffLine> diff = LineDiff.Compute("a\nb", "a\nx\nb");

        DiffLine added = diff.Single(l => l.Marker == DiffMarker.Added);
        Assert.AreEqual(0, added.OldNumber);
        Assert.AreEqual(2, added.NewNumber);
        DiffLine last = diff[^1];
        // 原文第 2 行在新文里是第 3 行 —— 两侧行号都要留着,差异视图才对得上原文件。
        Assert.AreEqual(2, last.OldNumber);
        Assert.AreEqual(3, last.NewNumber);
    }

    [TestMethod]
    public void DeletionsAreMarkedAndCounted()
    {
        IReadOnlyList<DiffLine> diff = LineDiff.Compute("a\ngone\nb", "a\nb");

        Assert.AreEqual(" a\n-gone\n b", Render(diff));
        Assert.AreEqual(1, LineDiff.CountChanged(diff));
    }

    [TestMethod]
    public void IdenticalTextHasNoChanges()
    {
        IReadOnlyList<DiffLine> diff = LineDiff.Compute("a\nb\nc", "a\nb\nc");

        Assert.AreEqual(0, LineDiff.CountChanged(diff));
        Assert.HasCount(3, diff);
    }

    [TestMethod]
    public void NormalisesLineEndingsSoCrlfIsNotAWholeFileChange()
    {
        // 编辑器保存成 LF、原文件是 CRLF 时,不做归一化会把整个文件报成改过。
        IReadOnlyList<DiffLine> diff = LineDiff.Compute("a\r\nb\r\nc", "a\nb\nc");

        Assert.AreEqual(0, LineDiff.CountChanged(diff));
    }

    [TestMethod]
    public void HandlesEmptySides()
    {
        Assert.IsEmpty(LineDiff.Compute("", ""));
        Assert.AreEqual(2, LineDiff.Compute("", "a\nb").Count(l => l.Marker == DiffMarker.Added));
        Assert.AreEqual(2, LineDiff.Compute("a\nb", "").Count(l => l.Marker == DiffMarker.Removed));
    }

    [TestMethod]
    public void RealisticConfigEditShowsOnlyTheEditedRegion()
    {
        const string before = """
            upstream api_backend {
                least_conn;
                keepalive 32;
            }
            server {
                listen 80;
            }
            """;
        const string after = """
            upstream api_backend {
                least_conn;
                server api-gateway:8000 max_fails=3;
                keepalive 32;
            }
            server {
                listen 80;
            }
            """;

        IReadOnlyList<DiffLine> diff = LineDiff.Compute(before, after);

        Assert.AreEqual(1, LineDiff.CountChanged(diff));
        Assert.AreEqual(DiffMarker.Added, diff.Single(l => l.Marker != DiffMarker.None).Marker);
    }
}
