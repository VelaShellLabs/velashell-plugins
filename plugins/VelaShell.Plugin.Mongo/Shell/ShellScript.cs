namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>
/// 查询编辑器里的一条语句:<paramref name="Offset" /> 是它在整段脚本里的起点(第一个有效字符,
/// 前导空白与注释不算),<paramref name="Text" /> 是去掉首尾空白与行尾注释后的原文。
/// </summary>
/// <param name="Offset">起点(相对整段脚本)。</param>
/// <param name="Text">语句原文(内部的注释保留,解析器自己会跳)。</param>
/// <param name="Index">第几条(0 起)。</param>
public sealed record ShellStatement(int Offset, string Text, int Index)
{
    /// <summary>长度。</summary>
    public int Length => Text.Length;

    /// <summary>终点(不含)。</summary>
    public int End => Offset + Text.Length;

    /// <summary>某个脚本内偏移是否落在这条语句里(含两端:光标停在语句末尾也算)。</summary>
    public bool Contains(int offset) => offset >= Offset && offset <= End;
}

/// <summary>
/// 把一段 mongosh 脚本切成语句,外加查询编辑器各处共用的词法小工具(跳字符串、注释、正则字面量)。
/// <para>
/// 切分规则照 mongosh 的直觉:分号一定断句;括号配平之后的换行**通常**断句 —— 除非下一行以
/// <c>.</c> 起头(链式调用写成多行:<c>db.orders.find({})\n  .sort(…)</c>),或这一行以运算符 / 逗号 /
/// 左括号收尾(还没写完)。字符串、模板字符串、注释、正则字面量里的括号和分号一概不算数 ——
/// 这一条是"按行号跑当前语句"能不能用的前提:一个 <c>"a;b"</c> 就能把朴素的按分号切分切坏。
/// </para>
/// </summary>
public static class ShellScript
{
    /// <summary>切分;只有注释与空白的片段不成语句。</summary>
    public static IReadOnlyList<ShellStatement> Split(string? text)
    {
        var statements = new List<ShellStatement>();
        if (string.IsNullOrEmpty(text))
        {
            return statements;
        }
        int depth = 0;
        int start = -1;
        int lastSignificantEnd = -1;
        char lastSignificant = '\0';
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                if (depth == 0 && start >= 0 && EndsAtNewline(text, i, lastSignificant))
                {
                    Emit(text, start, lastSignificantEnd, statements);
                    start = -1;
                }
                i++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (IsCommentStart(text, i))
            {
                i = SkipComment(text, i);
                continue;
            }
            if (start < 0)
            {
                start = i;
            }
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(text, i);
                lastSignificant = c;
                lastSignificantEnd = i;
                continue;
            }
            if (c == '/' && IsRegexStart(lastSignificant))
            {
                i = SkipRegex(text, i);
                lastSignificant = '/';
                lastSignificantEnd = i;
                continue;
            }
            if (c == ';' && depth == 0)
            {
                Emit(text, start, lastSignificantEnd, statements);
                start = -1;
                lastSignificant = ';';
                i++;
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            lastSignificant = c;
            lastSignificantEnd = i + 1;
            i++;
        }
        if (start >= 0)
        {
            Emit(text, start, lastSignificantEnd, statements);
        }
        return statements;
    }

    /// <summary>
    /// 光标所在的语句:落在某条语句里就是它;落在两条之间(空行、行尾空白)时取**前一条** ——
    /// 写完一条语句、光标停在下面的空行上按 Ctrl+↵,用户想跑的就是上面那条。
    /// </summary>
    public static ShellStatement? At(IReadOnlyList<ShellStatement> statements, int offset)
    {
        ShellStatement? before = null;
        foreach (ShellStatement statement in statements)
        {
            if (statement.Contains(offset))
            {
                return statement;
            }
            if (statement.End <= offset)
            {
                before = statement;
            }
        }
        return before ?? statements.FirstOrDefault();
    }

    /// <summary>与某个区间有交集的语句(运行选区用)。</summary>
    public static IReadOnlyList<ShellStatement> Overlapping(IReadOnlyList<ShellStatement> statements, int offset, int length) =>
        [.. statements.Where(s => s.Offset < offset + length && s.End > offset)];

    private static void Emit(string text, int start, int end, List<ShellStatement> statements)
    {
        if (start < 0 || end <= start)
        {
            return;
        }
        statements.Add(new ShellStatement(start, text[start..end], statements.Count));
    }

    /// <summary>
    /// 括号已配平的换行断不断句:这一行以"还没写完"的符号收尾,或下一个有效字符是 <c>.</c>(链式调用的续行)时不断。
    /// </summary>
    private static bool EndsAtNewline(string text, int newline, char lastSignificant)
    {
        if (lastSignificant is '.' or ',' or '(' or '[' or '{' or ':' or '=' or '+' or '-' or '*' or '&' or '|' or '?' or '!' or '<' or '>')
        {
            return false;
        }
        int next = SkipTrivia(text, newline);
        return next >= text.Length || text[next] != '.';
    }

    /// <summary>跳过空白与注释,返回下一个有效字符的位置(到头为文本长度)。</summary>
    public static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (IsCommentStart(text, i))
            {
                i = SkipComment(text, i);
            }
            else
            {
                break;
            }
        }
        return i;
    }

    /// <summary><c>//</c> 或 <c>/*</c>。</summary>
    public static bool IsCommentStart(string text, int i) =>
        text[i] == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*';

    /// <summary>跳过一段注释(行注释停在换行符上,不吃掉它 —— 换行对断句有意义)。</summary>
    public static int SkipComment(string text, int i)
    {
        if (text[i + 1] == '/')
        {
            int newline = text.IndexOf('\n', i);
            return newline < 0 ? text.Length : newline;
        }
        int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }

    /// <summary>跳过一个字符串字面量(<c>"…"</c>、<c>'…'</c>、<c>`…`</c>,处理转义);没收尾就到文本末尾。</summary>
    public static int SkipString(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == quote)
            {
                return i + 1;
            }
            // 普通字符串不跨行:没收尾的引号到行末为止,免得一个手误把后面整段都吞成字符串。
            if (c == '\n' && quote != '`')
            {
                return i;
            }
            i++;
        }
        return text.Length;
    }

    /// <summary>
    /// 前一个有效字符决定 <c>/</c> 是正则的开头还是除号:跟在运算符、逗号、左括号、冒号后面(或在开头)的是正则。
    /// </summary>
    public static bool IsRegexStart(char previousSignificant) =>
        previousSignificant is '\0' or '(' or ',' or '[' or '{' or ':' or '=' or ';' or '!' or '&' or '|' or '?' or '+' or '-' or '*';

    /// <summary>跳过一个正则字面量(含字符类与标志位)。</summary>
    public static int SkipRegex(string text, int start)
    {
        int i = start + 1;
        bool inClass = false;
        while (i < text.Length && text[i] != '\n')
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '[')
            {
                inClass = true;
            }
            else if (c == ']')
            {
                inClass = false;
            }
            else if (c == '/' && !inClass)
            {
                i++;
                while (i < text.Length && char.IsLetter(text[i]))
                {
                    i++;
                }
                return i;
            }
            i++;
        }
        return i;
    }

    /// <summary>
    /// 从 <paramref name="open" />(一个左括号)找到与它配对的右括号;字符串、注释、正则里的不算。
    /// 找不到返回 -1。
    /// </summary>
    public static int MatchBracket(string text, int open)
    {
        int depth = 0;
        char previous = '\0';
        int i = open;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (IsCommentStart(text, i))
            {
                i = SkipComment(text, i);
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(text, i);
                previous = c;
                continue;
            }
            if (c == '/' && IsRegexStart(previous))
            {
                i = SkipRegex(text, i);
                previous = '/';
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
            previous = c;
            i++;
        }
        return -1;
    }

    /// <summary>
    /// 在 <c>[start, end)</c> 里按顶层逗号切分(参数列表、管道数组的元素)。返回每段的起点与终点(已去首尾空白与注释)。
    /// </summary>
    public static IReadOnlyList<(int Start, int End)> SplitTopLevel(string text, int start, int end)
    {
        var parts = new List<(int, int)>();
        int depth = 0;
        int partStart = start;
        char previous = '\0';
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (IsCommentStart(text, i))
            {
                i = SkipComment(text, i);
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                i = SkipString(text, i);
                previous = c;
                continue;
            }
            if (c == '/' && IsRegexStart(previous))
            {
                i = SkipRegex(text, i);
                previous = '/';
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddPart(text, partStart, i, parts);
                partStart = i + 1;
            }
            previous = c;
            i++;
        }
        AddPart(text, partStart, end, parts);
        return parts;
    }

    private static void AddPart(string text, int start, int end, List<(int, int)> parts)
    {
        int s = SkipTrivia(text, start);
        int e = end;
        while (e > s && char.IsWhiteSpace(text[e - 1]))
        {
            e--;
        }
        if (s < e)
        {
            parts.Add((s, Math.Min(e, end)));
        }
    }

    /// <summary>标识符首字符(含 <c>$</c>)。</summary>
    public static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$';

    /// <summary>标识符后续字符。</summary>
    public static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
