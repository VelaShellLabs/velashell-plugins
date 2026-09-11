namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>一行在差异里的角色。</summary>
public enum DiffMarker
{
    /// <summary>没变。</summary>
    None,

    /// <summary>新增。</summary>
    Added,

    /// <summary>删除。</summary>
    Removed,

    /// <summary>改过(同一位置上的替换)。</summary>
    Changed
}

/// <summary>差异里的一行。</summary>
/// <param name="Marker">角色。</param>
/// <param name="OldNumber">在原文里的行号;新增行为 0。</param>
/// <param name="NewNumber">在新文里的行号;删除行为 0。</param>
/// <param name="Text">行内容。</param>
public readonly record struct DiffLine(DiffMarker Marker, int OldNumber, int NewNumber, string Text);

/// <summary>
/// 按行比对两段文本。
/// <para>
/// 用最长公共子序列,不是逐行对齐:在一个配置文件顶部插一行,逐行对齐会把
/// **之后的每一行**都标成改过,那样的差异视图等于没有。
/// </para>
/// <para>
/// 面板要比的是配置文件(几十到几千行),LCS 的 O(n·m) 在这个量级上无所谓;
/// 真碰上超大文件,上层的 2 MB 在线编辑上限先一步挡住了。
/// </para>
/// </summary>
public static class LineDiff
{
    /// <summary>相邻的一删一增合并成"改过";超过这个规模就不合并了。</summary>
    private const int MaxPairedChange = 200;

    /// <summary>比出差异。</summary>
    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText)
    {
        var a = Split(oldText);
        var b = Split(newText);
        var lcs = BuildLcs(a, b);

        List<DiffLine> result = [];
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                result.Add(new(DiffMarker.None, i + 1, j + 1, a[i]));
                i++;
                j++;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1])
            {
                result.Add(new(DiffMarker.Removed, i + 1, 0, a[i]));
                i++;
            }
            else
            {
                result.Add(new(DiffMarker.Added, 0, j + 1, b[j]));
                j++;
            }
        }
        while (i < a.Length)
        {
            result.Add(new(DiffMarker.Removed, i + 1, 0, a[i]));
            i++;
        }
        while (j < b.Length)
        {
            result.Add(new(DiffMarker.Added, 0, j + 1, b[j]));
            j++;
        }
        return Pair(result);
    }

    /// <summary>新文里有多少行与原文不同(底部那句"已修改 N 行")。</summary>
    public static int CountChanged(IReadOnlyList<DiffLine> lines) =>
        lines.Count(l => l.Marker != DiffMarker.None);

    /// <summary>
    /// 把"紧挨着的一删一增"合并成一条"改过"。
    /// <para>
    /// 改一行的值(<c>listen 80;</c> → <c>listen 8080;</c>)在 LCS 里天然是一删一增,
    /// 但人看到的是**一行改了**。不合并的话,一个改了三处的文件会显示成六行差异。
    /// </para>
    /// </summary>
    private static List<DiffLine> Pair(List<DiffLine> lines)
    {
        List<DiffLine> paired = [with(lines.Count)];
        for (var k = 0; k < lines.Count; k++)
        {
            var current = lines[k];
            if (current.Marker == DiffMarker.Removed
                && k + 1 < lines.Count
                && lines[k + 1].Marker == DiffMarker.Added
                && lines.Count <= MaxPairedChange * 4)
            {
                var next = lines[k + 1];
                paired.Add(new(DiffMarker.Changed, current.OldNumber, next.NewNumber, next.Text));
                k++;
                continue;
            }
            paired.Add(current);
        }
        return paired;
    }

    private static string[] Split(string text) =>
        text.Length == 0 ? [] : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static int[,] BuildLcs(string[] a, string[] b)
    {
        // lcs[i, j] = a[i..] 与 b[j..] 的最长公共子序列长度。倒着填,回溯时正着走。
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        return lcs;
    }
}
