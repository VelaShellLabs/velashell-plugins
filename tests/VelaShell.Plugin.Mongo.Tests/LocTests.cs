using System.Text.RegularExpressions;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 文案表的完整性:重复键(静态构造即抛,整个插件不可用)、空文案、以及两种语言的占位符对不上
/// (英文 <c>{1}</c> 中文忘了写,运行时 <c>string.Format</c> 不会抛,但中文界面会少一截信息;
/// 反过来多写一个 <c>{2}</c> 则是 <c>FormatException</c>)。二十来块界面各管一张表,这里统一守。
/// </summary>
[TestClass]
public sealed partial class LocTests
{
    [TestMethod]
    public void Tables_HaveNoDuplicateKeys() => Assert.IsGreaterThan(100, Loc.AllKeys.Count);

    [TestMethod]
    public void EveryKey_HasTextInBothLanguages()
    {
        var zh = new Loc("zh-Hans");
        var en = new Loc("en");
        foreach (string key in Loc.AllKeys)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(zh[key]), $"中文缺:{key}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(en[key]), $"英文缺:{key}");
        }
    }

    [TestMethod]
    public void Placeholders_MatchAcrossLanguages()
    {
        var zh = new Loc("zh-Hans");
        var en = new Loc("en");
        var mismatches = new List<string>();
        foreach (string key in Loc.AllKeys)
        {
            string[] a = [.. Placeholders(en[key])];
            string[] b = [.. Placeholders(zh[key])];
            if (!a.SequenceEqual(b))
            {
                mismatches.Add($"{key}: en {{{string.Join(",", a)}}} / zh {{{string.Join(",", b)}}}");
            }
        }
        Assert.AreEqual(0, mismatches.Count, string.Join(Environment.NewLine, mismatches));
    }

    [TestMethod]
    public void FormattedTexts_DoNotThrow()
    {
        // 只验带占位符的文案:那些才会走 Format;不带占位符的只经索引器取,字面花括号无害。
        object[] args = [.. Enumerable.Range(0, 10).Select(static i => (object)i)];
        var failures = new List<string>();
        foreach (Loc loc in new[] { new Loc("zh-Hans"), new Loc("en") })
        {
            foreach (string key in Loc.AllKeys.Where(k => Placeholders(loc[k]).Any()))
            {
                try
                {
                    _ = loc.Format(key, args);
                }
                catch (FormatException ex)
                {
                    failures.Add($"{key}: {ex.Message}");
                }
            }
        }
        Assert.AreEqual(0, failures.Count, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void UnknownKeys_FallBackToTheKey() => Assert.AreEqual("No_Such_Key", new Loc("en")["No_Such_Key"]);

    private static IEnumerable<string> Placeholders(string text) =>
        PlaceholderPattern().Matches(text.Replace("{{", "", StringComparison.Ordinal).Replace("}}", "", StringComparison.Ordinal))
            .Select(static m => m.Groups[1].Value)
            .Distinct()
            .Order(StringComparer.Ordinal);

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
