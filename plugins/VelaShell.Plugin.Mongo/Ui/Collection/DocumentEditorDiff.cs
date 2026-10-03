using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一次保存要发给服务器的东西:<c>$set</c> / <c>$unset</c> 两张表,或者"整份替换"。
/// </summary>
internal sealed class DocumentEditorPlan
{
    /// <summary><c>$set</c>:点路径 → 新值。</summary>
    public BsonDocument Set { get; } = [];

    /// <summary><c>$unset</c>:点路径 → <c>""</c>。</summary>
    public BsonDocument Unset { get; } = [];

    /// <summary>
    /// 要整份替换(<c>replaceOne</c>):顶层已有字段的**相对顺序**变了(用户拖动排了序),
    /// 或者有字段名没法写成点路径(<c>$</c> 开头、含点号)—— 这两样 <c>$set</c> 都表达不了。
    /// </summary>
    public bool Replace { get; set; }

    /// <summary>什么也不用发。</summary>
    public bool IsEmpty => !Replace && Set.ElementCount == 0 && Unset.ElementCount == 0;

    /// <summary>涉及的字段路径(确认框里列出来)。</summary>
    public IEnumerable<string> Paths => Set.Names.Concat(Unset.Names);
}

/// <summary>
/// 文档编辑器的两种"差异":字段级的更新计划(<see cref="Plan" />,决定发什么命令)
/// 与文本级的逐行对比(<see cref="Lines" />,对比模式与预览的红绿行)。
/// </summary>
internal static class DocumentEditorDiff
{
    /// <summary>
    /// 原文档 → 当前文档的最小更新:只 <c>$set</c> 真变了的路径、<c>$unset</c> 删掉的字段。
    /// <para>
    /// 嵌套文档两边都在、键序没变时下钻到子路径(<c>customer.level</c>),而不是把整个 customer 覆盖回去 ——
    /// 别人同时改了 customer.name 的话,整块覆盖会把那次修改悄悄抹掉。等长数组同理按下标下钻;
    /// 长度变了的数组只能整体 <c>$set</c>(按下标写会和并发的 $push 错位)。
    /// </para>
    /// </summary>
    public static DocumentEditorPlan Plan(BsonDocument original, BsonDocument current)
    {
        var plan = new DocumentEditorPlan();
        if (!SameOrder(original, current, skipId: true))
        {
            plan.Replace = true;
            return plan;
        }
        DiffDocument(original, current, null, plan);
        if (plan.Replace)
        {
            plan.Set.Clear();
            plan.Unset.Clear();
        }
        return plan;
    }

    private static void DiffDocument(BsonDocument original, BsonDocument current, string? prefix, DocumentEditorPlan plan)
    {
        foreach (BsonElement gone in original)
        {
            if ((prefix is null && gone.Name == "_id") || current.Contains(gone.Name))
            {
                continue;
            }
            if (!Addressable(gone.Name))
            {
                plan.Replace = true;
                return;
            }
            plan.Unset[BsonPath.Join(prefix, gone.Name)] = "";
        }
        foreach (BsonElement element in current)
        {
            if (prefix is null && element.Name == "_id")
            {
                continue;
            }
            if (!original.TryGetValue(element.Name, out BsonValue before))
            {
                if (!Addressable(element.Name))
                {
                    plan.Replace = true;
                    return;
                }
                plan.Set[BsonPath.Join(prefix, element.Name)] = element.Value;
                continue;
            }
            if (before.Equals(element.Value))
            {
                continue;
            }
            if (!Addressable(element.Name))
            {
                plan.Replace = true;
                return;
            }
            DiffValue(before, element.Value, BsonPath.Join(prefix, element.Name), plan);
            if (plan.Replace)
            {
                return;
            }
        }
    }

    private static void DiffValue(BsonValue before, BsonValue after, string path, DocumentEditorPlan plan)
    {
        if (before is BsonDocument a && after is BsonDocument b && a.ElementCount > 0 && b.ElementCount > 0 && SameOrder(a, b, skipId: false))
        {
            DiffDocument(a, b, path, plan);
            return;
        }
        // 等长数组:改动少时按下标下钻(items.1.qty);过半的项都变了就整体 $set —— 一串 tags.0 / tags.1 既难读,
        // 也不比整体替换更"局部"。
        if (before is BsonArray x && after is BsonArray y && x.Count == y.Count && x.Count > 0
            && Enumerable.Range(0, x.Count).Count(i => !x[i].Equals(y[i])) * 2 <= x.Count)
        {
            for (int i = 0; i < x.Count; i++)
            {
                if (!x[i].Equals(y[i]))
                {
                    DiffValue(x[i], y[i], BsonPath.Join(path, i.ToString(CultureInfo.InvariantCulture)), plan);
                    if (plan.Replace)
                    {
                        return;
                    }
                }
            }
            return;
        }
        plan.Set[path] = after;
    }

    /// <summary>两边都有的键,相对顺序一致(新增的键在哪儿不管 —— <c>$set</c> 会把它追加到末尾)。</summary>
    internal static bool SameOrder(BsonDocument original, BsonDocument current, bool skipId)
    {
        List<string> before = [.. original.Names.Where(n => current.Contains(n) && !(skipId && n == "_id"))];
        List<string> after = [.. current.Names.Where(n => original.Contains(n) && !(skipId && n == "_id"))];
        return before.SequenceEqual(after, StringComparer.Ordinal);
    }

    /// <summary>能不能写进点路径:空名、<c>$</c> 开头、含点号的都不行。</summary>
    internal static bool Addressable(string name) => name.Length > 0 && name[0] != '$' && !name.Contains('.');

    /// <summary>
    /// 逐行对比(对比模式):返回左侧被删掉的行与右侧新增的行(行号 1 起)。
    /// 先剥掉两头相同的行,中间段做 LCS;中间段太大(两边行数乘积超过四百万)就整段标红 / 标绿 ——
    /// 那种规模的改动,逐行对齐也没人看得过来。
    /// </summary>
    public static (HashSet<int> Removed, HashSet<int> Added) Lines(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var removed = new HashSet<int>();
        var added = new HashSet<int>();
        int start = 0;
        while (start < left.Count && start < right.Count && left[start] == right[start])
        {
            start++;
        }
        int endLeft = left.Count;
        int endRight = right.Count;
        while (endLeft > start && endRight > start && left[endLeft - 1] == right[endRight - 1])
        {
            endLeft--;
            endRight--;
        }
        int n = endLeft - start;
        int m = endRight - start;
        if ((long)n * m > 4_000_000)
        {
            for (int i = start; i < endLeft; i++)
            {
                removed.Add(i + 1);
            }
            for (int j = start; j < endRight; j++)
            {
                added.Add(j + 1);
            }
            return (removed, added);
        }
        // lcs[i, j] = left[start+i..] 与 right[start+j..] 的最长公共子序列长度。
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = left[start + i] == right[start + j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        int p = 0;
        int q = 0;
        while (p < n && q < m)
        {
            if (left[start + p] == right[start + q])
            {
                p++;
                q++;
            }
            else if (lcs[p + 1, q] >= lcs[p, q + 1])
            {
                removed.Add(start + p + 1);
                p++;
            }
            else
            {
                added.Add(start + q + 1);
                q++;
            }
        }
        for (; p < n; p++)
        {
            removed.Add(start + p + 1);
        }
        for (; q < m; q++)
        {
            added.Add(start + q + 1);
        }
        return (removed, added);
    }
}
