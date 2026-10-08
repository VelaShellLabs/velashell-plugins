using System.Text;

namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>
/// 查询编辑器的「格式化」(Alt+Shift+F):统一花括号、冒号、逗号两侧的空格,按括号层级重排缩进,
/// 链式调用的续行多缩一级。
/// <para>
/// 刻意**不重排换行**:用户把一个长条件拆成几行是有意的,格式化器把它压回一行或按自己的规则拆开,
/// 都是在替用户做决定。这里只做没有争议的那部分 —— 空格与缩进。字符串、正则、注释原样保留;
/// 含模板字符串(反引号,可以跨行)的脚本整段不动,宁可不格式化也不改坏字符串内容。
/// </para>
/// </summary>
public static class ShellFormatter
{
    private const string Unit = "  ";

    /// <summary>格式化一段脚本。</summary>
    public static string Format(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Contains('`'))
        {
            return text;
        }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder(text.Length + 32);
        var levels = new Stack<int>();
        bool inBlockComment = false;
        int blankRun = 0;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (inBlockComment)
            {
                _ = output.Append(new string(' ', levels.Count * Unit.Length)).Append(line).Append('\n');
                if (line.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }
                continue;
            }
            if (line.Length == 0)
            {
                if (++blankRun <= 1)
                {
                    _ = output.Append('\n');
                }
                continue;
            }
            blankRun = 0;
            string spaced = Space(line, out bool opensBlockComment, out IReadOnlyList<char> brackets, out int leadingClosers);

            // 本行的缩进:先扣掉行首那几个右括号,再看是不是链式续行。
            int[] snapshot = [.. levels];
            var temp = new Stack<int>(snapshot.Reverse());
            for (int k = 0; k < leadingClosers && temp.Count > 0; k++)
            {
                int top = temp.Pop() - 1;
                if (top > 0)
                {
                    temp.Push(top);
                }
            }
            int indent = temp.Count + (spaced.StartsWith('.') ? 1 : 0);
            _ = output.Append(string.Concat(Enumerable.Repeat(Unit, indent))).Append(spaced).Append('\n');

            // 再按本行的括号更新层级:一行里新开的括号(不论几个)只算一级缩进。
            int pending = 0;
            foreach (char b in brackets)
            {
                if (b is '(' or '[' or '{')
                {
                    pending++;
                }
                else if (pending > 0)
                {
                    pending--;
                }
                else if (levels.Count > 0)
                {
                    int top = levels.Pop() - 1;
                    if (top > 0)
                    {
                        levels.Push(top);
                    }
                }
            }
            if (pending > 0)
            {
                levels.Push(pending);
            }
            inBlockComment = opensBlockComment;
        }
        string result = output.ToString().TrimEnd('\n');
        return text.EndsWith('\n') ? result + "\n" : result;
    }

    /// <summary>
    /// 一行内的空格规范化(字符串 / 正则 / 注释之外):<c>{a:1}</c> → <c>{ a: 1 }</c>,逗号后一个空格,
    /// 连续空白压成一个。顺带收集本行的括号序列与行首右括号个数,供缩进计算。
    /// </summary>
    private static string Space(string line, out bool opensBlockComment, out IReadOnlyList<char> brackets, out int leadingClosers)
    {
        var b = new StringBuilder(line.Length + 8);
        var found = new List<char>();
        opensBlockComment = false;
        leadingClosers = 0;
        bool sawContent = false;
        char previous = '\0';
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                int end = ShellScript.SkipString(line, i);
                _ = b.Append(line, i, end - i);
                i = end;
                previous = c;
                sawContent = true;
                continue;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                TrimSpace(b);
                if (b.Length > 0)
                {
                    _ = b.Append(' ');
                }
                _ = b.Append(line, i, line.Length - i);
                break;
            }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int end = close < 0 ? line.Length : close + 2;
                _ = b.Append(line, i, end - i);
                opensBlockComment = close < 0;
                i = end;
                continue;
            }
            if (c == '/' && ShellScript.IsRegexStart(previous))
            {
                int end = ShellScript.SkipRegex(line, i);
                _ = b.Append(line, i, end - i);
                i = end;
                previous = '/';
                sawContent = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (b.Length > 0 && b[^1] != ' ')
                {
                    _ = b.Append(' ');
                }
                i++;
                continue;
            }
            switch (c)
            {
                case '{':
                    _ = b.Append('{');
                    // { 后一个空格(空对象 {} 除外)。
                    if (NextSignificant(line, i + 1) is var n && n != '}' && n != '\0')
                    {
                        _ = b.Append(' ');
                    }
                    break;
                case '}':
                    TrimSpace(b);
                    if (b.Length > 0 && b[^1] != '{' && b[^1] is not ('(' or '['))
                    {
                        _ = b.Append(' ');
                    }
                    _ = b.Append('}');
                    break;
                case ':':
                    TrimSpace(b);
                    _ = b.Append(':');
                    if (i + 1 < line.Length)
                    {
                        _ = b.Append(' ');
                    }
                    break;
                case ',':
                    TrimSpace(b);
                    _ = b.Append(',');
                    if (i + 1 < line.Length && NextSignificant(line, i + 1) is not (']' or ')' or '\0'))
                    {
                        _ = b.Append(' ');
                    }
                    break;
                case ')' or ']':
                    TrimSpace(b);
                    _ = b.Append(c);
                    break;
                case '(' or '[':
                    _ = b.Append(c);
                    break;
                default:
                    _ = b.Append(c);
                    break;
            }
            if (c is '(' or '[' or '{' or ')' or ']' or '}')
            {
                found.Add(c);
                if (!sawContent && c is ')' or ']' or '}')
                {
                    leadingClosers++;
                }
            }
            if (c is not (')' or ']' or '}'))
            {
                sawContent = true;
            }
            previous = c;
            i++;
            // 跳过本来就跟在 { : , 后面的空白(上面已经补了规范的一个)。
            if (c is '{' or ':' or ',')
            {
                while (i < line.Length && line[i] == ' ')
                {
                    i++;
                }
            }
        }
        brackets = found;
        return b.ToString().TrimEnd();
    }

    private static char NextSignificant(string line, int i)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }
        return i < line.Length ? line[i] : '\0';
    }

    private static void TrimSpace(StringBuilder b)
    {
        while (b.Length > 0 && b[^1] == ' ')
        {
            b.Length--;
        }
    }
}
