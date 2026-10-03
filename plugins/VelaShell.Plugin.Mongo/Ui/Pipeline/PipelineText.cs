using System.Globalization;
using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>一个阶段的文本形态:运算符、阶段体原文、是否启用。</summary>
/// <param name="Operator">运算符(<c>$match</c>)。</param>
/// <param name="Body">阶段体原文(mongosh 写法,保留用户的排版)。</param>
/// <param name="Enabled">是否启用。</param>
internal sealed record PipelineStageSpec(string Operator, string Body, bool Enabled = true);

/// <summary>整条管道文本的解析结果。</summary>
/// <param name="Stages">阶段;失败时为空。</param>
/// <param name="ErrorKey">失败原因的文案键;成功为 <see langword="null" />。</param>
/// <param name="ErrorArgument">文案参数。</param>
/// <param name="ErrorOffset">出错位置(相对于整段文本)。</param>
/// <param name="ErrorLength">出错区间长度。</param>
internal sealed record PipelineParse(
    IReadOnlyList<PipelineStageSpec> Stages,
    string? ErrorKey = null,
    string ErrorArgument = "",
    int ErrorOffset = 0,
    int ErrorLength = 1)
{
    /// <summary>成功。</summary>
    public bool Ok => ErrorKey is null;
}

/// <summary>阶段体的解析结果(卡片上的错误标记与编辑器波浪线都从它来)。</summary>
/// <param name="Value">解析出的值;失败为 <see langword="null" />。</param>
/// <param name="ErrorKey">失败原因的文案键。</param>
/// <param name="ErrorArgument">文案参数。</param>
/// <param name="Offset">出错位置(相对于阶段体)。</param>
/// <param name="Length">出错区间长度。</param>
/// <param name="Fix">一键修复的替换文本。</param>
internal sealed record PipelineBodyParse(BsonValue? Value, string? ErrorKey = null, string ErrorArgument = "", int Offset = 0, int Length = 0, string? Fix = null)
{
    /// <summary>成功。</summary>
    public bool Ok => Value is not null;
}

/// <summary>
/// 管道的「文本」形态:阶段卡片 ⇄ 一整段 mongosh 数组文本。
/// <para>
/// 文本模式与卡片模式是**同一份数据的两种编辑方式**,所以这里的格式化与解析必须互逆:
/// 卡片 → 文本 → 卡片要回到原样(包括用户在阶段体里的换行与缩进)。为此阶段体按**原文**搬运,
/// 而不是解析成 BSON 再写回 —— 写回会把 <c>ISODate("2026-08-27")</c> 变成带毫秒的全称,
/// 把用户精心排好的一行拆成五行,用户在两种模式之间切一次就"被格式化"一次。
/// </para>
/// <para>
/// 停用的阶段在文本里写成**整段注释**(<c>// { $lookup: … },</c>):这是 Compass 导出时的同一个约定,
/// 粘到 mongosh 里也能直接跑(注释被忽略,正好就是"停用")。解析时只把"在数组顶层、以 <c>{</c> 起头、
/// 括号能配平"的那种注释块认成停用阶段;其余注释照常当注释丢掉。
/// </para>
/// </summary>
internal static class PipelineText
{
    /// <summary>行宽:超过它的容器才折行(与 mongosh 的默认输出宽度相近)。</summary>
    private const int Width = 72;

    // ── 卡片 → 文本 ─────────────────────────────────────────────────────────

    /// <summary>把阶段写成一整段数组文本(停用的阶段整段注释掉)。</summary>
    public static string Format(IEnumerable<PipelineStageSpec> stages)
    {
        var b = new StringBuilder("[\n");
        bool first = true;
        foreach (PipelineStageSpec stage in stages)
        {
            if (!first)
            {
                b.Append('\n');
            }
            first = false;
            string text = FormatStage(stage.Operator, stage.Body) + ",";
            foreach (string line in text.Split('\n'))
            {
                b.Append("  ");
                if (!stage.Enabled)
                {
                    b.Append("// ");
                }
                b.Append(line).Append('\n');
            }
            b.Length--;
        }
        if (!first)
        {
            b.Append('\n');
        }
        b.Append(']');
        return b.ToString();
    }

    /// <summary>
    /// 一个阶段:单行阶段体写成 <c>{ $limit: 10 }</c>;多行的把运算符单列一行,阶段体整体缩进两格。
    /// </summary>
    public static string FormatStage(string op, string body)
    {
        string[] lines = Normalize(body).Trim().Split('\n');
        if (lines.Length == 1)
        {
            return $"{{ {op}: {lines[0]} }}";
        }
        var b = new StringBuilder("{\n  ").Append(op).Append(": ").Append(lines[0]);
        for (int i = 1; i < lines.Length; i++)
        {
            b.Append('\n');
            if (lines[i].Length > 0)
            {
                b.Append("  ").Append(lines[i]);
            }
        }
        return b.Append("\n}").ToString();
    }

    // ── 文本 → 卡片 ─────────────────────────────────────────────────────────

    /// <summary>解析一整段数组文本。空白文本视为空管道。</summary>
    public static PipelineParse Parse(string? text)
    {
        text = Normalize(text ?? "");
        int i = SkipTrivia(text, 0);
        if (i >= text.Length)
        {
            return new([]);
        }
        if (text[i] != '[')
        {
            return Fail("Pipe_ErrNotArray", "", i, 1);
        }
        i++;
        var stages = new List<PipelineStageSpec>();
        while (true)
        {
            // 元素之间的空白、逗号与注释;顶层的注释块可能是一个停用的阶段。
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == ',')
                {
                    i++;
                    continue;
                }
                if (c == '/' && At(text, i + 1) == '/')
                {
                    if (TryReadDisabled(text, i, out PipelineStageSpec? disabled, out int next))
                    {
                        stages.Add(disabled);
                        i = next;
                        continue;
                    }
                    i = LineEnd(text, i);
                    continue;
                }
                if (c == '/' && At(text, i + 1) == '*')
                {
                    i = BlockCommentEnd(text, i);
                    continue;
                }
                break;
            }
            if (i >= text.Length)
            {
                return Fail("Pipe_ErrUnclosed", "", Math.Max(0, text.Length - 1), 1);
            }
            if (text[i] == ']')
            {
                i++;
                break;
            }
            int start = i;
            int end = ElementEnd(text, i);
            PipelineParse? error = ParseElement(text[start..end], start, out PipelineStageSpec? stage);
            if (error is not null)
            {
                return error;
            }
            stages.Add(stage!);
            i = end;
        }
        i = SkipTrivia(text, i);
        if (i < text.Length && text[i] == ';')
        {
            i = SkipTrivia(text, i + 1);
        }
        if (i < text.Length)
        {
            return Fail("Pipe_ErrTrailing", "", i, text.Length - i);
        }
        return new(stages);
    }

    /// <summary>解析一个数组元素 <c>{ $op: body }</c>;出错返回带位置的失败结果。</summary>
    private static PipelineParse? ParseElement(string element, int baseOffset, out PipelineStageSpec? stage)
    {
        stage = null;
        string trimmed = element.TrimEnd();
        int i = SkipTrivia(trimmed, 0);
        if (i >= trimmed.Length || trimmed[i] != '{')
        {
            return Fail("Pipe_ErrStageShape", "", baseOffset + i, Math.Max(1, Math.Min(trimmed.Length - i, 12)));
        }
        int close = Matching(trimmed, i);
        if (close < 0)
        {
            return Fail("Pipe_ErrUnbalanced", "", baseOffset + i, 1);
        }
        if (SkipTrivia(trimmed, close + 1) < trimmed.Length)
        {
            return Fail("Pipe_ErrStageShape", "", baseOffset + close + 1, trimmed.Length - close - 1);
        }
        int k = SkipTrivia(trimmed, i + 1);
        if (k >= close)
        {
            return Fail("Pipe_ErrEmptyStage", "", baseOffset + i, close - i + 1);
        }
        int keyStart = k;
        string key;
        if (trimmed[k] is '"' or '\'')
        {
            int end = SkipString(trimmed, k);
            key = trimmed[(k + 1)..Math.Max(k + 1, end - 1)];
            k = end;
        }
        else
        {
            while (k < close && (char.IsLetterOrDigit(trimmed[k]) || trimmed[k] is '_' or '$'))
            {
                k++;
            }
            key = trimmed[keyStart..k];
        }
        if (key.Length == 0)
        {
            return Fail("Pipe_ErrStageShape", "", baseOffset + keyStart, 1);
        }
        if (!key.StartsWith('$'))
        {
            return Fail("Pipe_ErrStageName", key, baseOffset + keyStart, k - keyStart);
        }
        k = SkipTrivia(trimmed, k);
        if (k >= close || trimmed[k] != ':')
        {
            return Fail("Pipe_ErrExpectedColon", key, baseOffset + Math.Min(k, close), 1);
        }
        int bodyStart = k + 1;
        int bodyEnd = close;
        int comma = TopLevelComma(trimmed, bodyStart, close);
        if (comma >= 0)
        {
            int rest = SkipTrivia(trimmed, comma + 1);
            if (rest < close)
            {
                return Fail("Pipe_ErrOneOperator", key, baseOffset + rest, close - rest);
            }
            bodyEnd = comma;
        }
        string raw = trimmed[bodyStart..bodyEnd];
        int lead = 0;
        while (lead < raw.Length && char.IsWhiteSpace(raw[lead]))
        {
            lead++;
        }
        string body = Dedent(raw.Trim());
        if (body.Length == 0)
        {
            return Fail("Pipe_ErrEmptyBody", key, baseOffset + bodyStart, 1);
        }
        PipelineBodyParse parsed = ParseBody(body);
        if (!parsed.Ok)
        {
            int offset = baseOffset + bodyStart + lead + MapDedented(raw.Trim(), parsed.Offset);
            return Fail(parsed.ErrorKey!, parsed.ErrorArgument, offset, Math.Max(1, parsed.Length));
        }
        stage = new PipelineStageSpec(key, body);
        return null;
    }

    /// <summary>
    /// 读一个停用阶段的注释块:从 <paramref name="start" /> 的 <c>//</c> 起,
    /// 连续的注释行去掉 <c>//</c> 前缀后能配平成一个 <c>{ $op: … }</c>,就是一个停用阶段。
    /// 配不平或解析不出阶段,就当普通注释(返回 <see langword="false" />)。
    /// </summary>
    private static bool TryReadDisabled(string text, int start, out PipelineStageSpec stage, out int next)
    {
        stage = null!;
        next = start;
        var content = new StringBuilder();
        int depth = 0;
        bool opened = false;
        int i = start;
        while (true)
        {
            int lineEnd = LineEnd(text, i);
            // i 指着 "//"。
            int from = i + 2;
            if (from < lineEnd && text[from] == ' ')
            {
                from++;
            }
            string line = text[from..lineEnd];
            if (!opened)
            {
                string head = line.TrimStart();
                if (!head.StartsWith('{'))
                {
                    return false;
                }
            }
            content.Append(line).Append('\n');
            depth += BraceDelta(line, ref opened);
            if (opened && depth <= 0)
            {
                next = lineEnd;
                break;
            }
            // 下一行必须仍是注释行。
            int j = lineEnd + 1;
            while (j < text.Length && text[j] is ' ' or '\t')
            {
                j++;
            }
            if (j + 1 >= text.Length || text[j] != '/' || text[j + 1] != '/')
            {
                return false;
            }
            i = j;
        }
        string element = content.ToString().Trim();
        if (element.EndsWith(','))
        {
            element = element[..^1].TrimEnd();
        }
        if (ParseElement(element, 0, out PipelineStageSpec? parsed) is not null || parsed is null)
        {
            return false;
        }
        stage = parsed with { Enabled = false };
        return true;
    }

    /// <summary>一行代码里花括号的净增减(跳过字符串)。</summary>
    private static int BraceDelta(string line, ref bool opened)
    {
        int delta = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c is '"' or '\'')
            {
                i = SkipString(line, i) - 1;
                continue;
            }
            if (c is '{' or '[')
            {
                delta++;
                opened = true;
            }
            else if (c is '}' or ']')
            {
                delta--;
            }
        }
        return delta;
    }

    // ── 阶段体 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 解析一个阶段体(<c>{ … }</c>、<c>10</c>、<c>"$items"</c>、<c>[ "a" ]</c> 都可以 —— 各阶段的形态不同)。
    /// 先跑 shell 诊断(含点号的裸键这类 mongosh 自己也会报的错),再交给解析器。
    /// </summary>
    public static PipelineBodyParse ParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new(null, "Pipe_ErrEmptyBody", "", 0, 1);
        }
        IReadOnlyList<ShellDiagnostic> diagnostics = ShellJson.Diagnose(body);
        if (diagnostics.FirstOrDefault() is { } first)
        {
            return new(null, first.MessageKey, first.Argument, first.Offset, Math.Max(1, first.Length), first.Fix);
        }
        int unbalanced = FindUnbalanced(body);
        if (unbalanced >= 0)
        {
            return new(null, "Pipe_ErrUnbalanced", "", unbalanced, 1);
        }
        try
        {
            return new(ShellJson.ParseValue(body));
        }
        catch (ShellJsonException ex)
        {
            (int offset, int length) = ex.Offset >= 0 && ex.Offset < body.Length
                ? (ex.Offset, 1)
                : GuessErrorSpan(body);
            return new(null, "Pipe_ErrSyntax", ex.Message, offset, length);
        }
    }

    /// <summary>
    /// 驱动的 JSON 读取器报错时不给位置。退一步:标第一个非空行 —— 至少让波浪线落在阶段体上,
    /// 而不是什么都不标(那会让人以为错的是别处)。
    /// </summary>
    private static (int Offset, int Length) GuessErrorSpan(string body)
    {
        int start = 0;
        while (start < body.Length && char.IsWhiteSpace(body[start]))
        {
            start++;
        }
        int end = body.IndexOf('\n', start);
        if (end < 0)
        {
            end = body.Length;
        }
        return (start, Math.Max(1, end - start));
    }

    /// <summary>第一个配不上的括号位置;都配上了返回 -1。</summary>
    internal static int FindUnbalanced(string text)
    {
        var stack = new Stack<(char Open, int At)>();
        int result = -1;
        Walk(text, 0, text.Length, (i, c) =>
        {
            switch (c)
            {
                case '{' or '[' or '(':
                    stack.Push((c, i));
                    break;
                case '}' or ']' or ')':
                    char expected = c switch { '}' => '{', ']' => '[', _ => '(' };
                    if (stack.Count == 0 || stack.Peek().Open != expected)
                    {
                        result = i;
                        return false;
                    }
                    stack.Pop();
                    break;
            }
            return true;
        });
        if (result >= 0)
        {
            return result;
        }
        return stack.Count > 0 ? stack.Peek().At : -1;
    }

    /// <summary>
    /// 去掉多行阶段体第二行起的公共缩进(第一行紧跟在冒号后面,它自己的缩进没有意义)。
    /// 文本模式里阶段体整体缩进了四格,这一步把它还原成卡片里的样子。
    /// </summary>
    public static string Dedent(string body)
    {
        string[] lines = Normalize(body).Split('\n');
        if (lines.Length <= 1)
        {
            return body.Trim();
        }
        int min = int.MaxValue;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }
            min = Math.Min(min, Indentation(lines[i]));
        }
        if (min == int.MaxValue)
        {
            min = 0;
        }
        var b = new StringBuilder(lines[0].Trim());
        for (int i = 1; i < lines.Length; i++)
        {
            b.Append('\n');
            string line = lines[i];
            b.Append(line.Trim().Length == 0 ? "" : line[Math.Min(min, Indentation(line))..].TrimEnd());
        }
        return b.ToString().TrimEnd();
    }

    /// <summary>去缩进后的偏移 → 去缩进前的偏移(诊断位置要落回原文)。</summary>
    private static int MapDedented(string raw, int dedentedOffset)
    {
        string dedented = Dedent(raw);
        if (dedentedOffset <= 0)
        {
            return 0;
        }
        // 按行列对齐:找到偏移在去缩进文本里的行与列,再到原文的同一行加回被去掉的缩进。
        int line = 0;
        int column = 0;
        for (int i = 0; i < Math.Min(dedentedOffset, dedented.Length); i++)
        {
            if (dedented[i] == '\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }
        string[] rawLines = Normalize(raw).Split('\n');
        string[] newLines = dedented.Split('\n');
        int offset = 0;
        for (int i = 0; i < line && i < rawLines.Length; i++)
        {
            offset += rawLines[i].Length + 1;
        }
        // 第一行没有被去缩进(原文已 Trim);其余行被去掉的是两边缩进之差。
        int removed = line > 0 && line < rawLines.Length && line < newLines.Length
            ? Indentation(rawLines[line]) - Indentation(newLines[line])
            : 0;
        return Math.Min(raw.Length, offset + Math.Max(0, removed) + column);
    }

    /// <summary>新阶段的默认阶段体:取词汇表的插入模板(<c>$unwind: { path: "$|" }</c>)冒号后那段。</summary>
    public static string DefaultBody(string op)
    {
        // 模板里光标落在值的位置(<c>$limit: |</c>)的,去掉光标就成了空阶段体;给一个能直接跑的值。
        string? fallback = op switch
        {
            "$limit" => "10",
            "$skip" => "0",
            "$sample" => "{ size: 10 }",
            _ => null
        };
        if (fallback is not null)
        {
            return fallback;
        }
        Shell.VocabularyEntry? entry = Shell.MongoVocabulary.Stages.FirstOrDefault(s => s.Name == op);
        string insert = entry?.InsertText ?? op + ": { | }";
        int colon = insert.IndexOf(": ", StringComparison.Ordinal);
        string body = (colon >= 0 ? insert[(colon + 2)..] : "{ }").Replace("|", "", StringComparison.Ordinal);
        return body.Trim() switch
        {
            "{  }" or "{ }" or "{}" => "{\n  \n}",
            "" => "{ }",
            var other => other
        };
    }

    // ── BSON → 阶段体文本 ────────────────────────────────────────────────────

    /// <summary>
    /// 把一个已解析的值写成阶段体文本(从外部带进来的管道用):顶层文档两个字段以上逐行写,
    /// 内层容器能放进一行就放一行 —— 与设计稿里 <c>createdAt: { $gte: ISODate("2026-08-27") }</c> 的写法一致。
    /// </summary>
    public static string FormatBody(BsonValue value) => Write(value, 0, topLevel: true);

    private static string Write(BsonValue value, int depth, bool topLevel = false)
    {
        string flat = Flat(value);
        bool fits = flat.Length + depth * 2 <= Width;
        switch (value)
        {
            case BsonDocument doc when doc.ElementCount > 0 && (!fits || (topLevel && doc.ElementCount > 1)):
            {
                var b = new StringBuilder("{");
                int i = 0;
                foreach (BsonElement element in doc)
                {
                    b.Append(i++ == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2)
                        .Append(BsonText.FieldName(element.Name)).Append(": ")
                        .Append(Write(element.Value, depth + 1));
                }
                return b.Append('\n').Append(' ', depth * 2).Append('}').ToString();
            }
            case BsonArray array when array.Count > 0 && !fits:
            {
                var b = new StringBuilder("[");
                for (int i = 0; i < array.Count; i++)
                {
                    b.Append(i == 0 ? "\n" : ",\n").Append(' ', (depth + 1) * 2).Append(Write(array[i], depth + 1));
                }
                return b.Append('\n').Append(' ', depth * 2).Append(']').ToString();
            }
            default:
                return flat;
        }
    }

    /// <summary>单行字面量。日期恰在 UTC 零点时写成 <c>ISODate("2026-08-27")</c>(mongosh 接受,而且好读得多)。</summary>
    internal static string Flat(BsonValue value) => value switch
    {
        BsonDocument { ElementCount: 0 } => "{}",
        BsonDocument doc => "{ " + string.Join(", ", doc.Select(e => BsonText.FieldName(e.Name) + ": " + Flat(e.Value))) + " }",
        BsonArray { Count: 0 } => "[]",
        BsonArray array => "[ " + string.Join(", ", array.Select(Flat)) + " ]",
        BsonDateTime date when IsMidnight(date) =>
            $"ISODate(\"{date.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\")",
        _ => BsonText.Literal(value)
    };

    private static bool IsMidnight(BsonDateTime date)
    {
        long ms = date.MillisecondsSinceEpoch;
        return ms % 86_400_000 == 0 && date.IsValidDateTime;
    }

    // ── 扫描器 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 <paramref name="start" /> 往后走到 <paramref name="end" />,跳过字符串、注释与正则字面量,
    /// 对每个"代码字符"回调;回调返回 <see langword="false" /> 即停。返回停下的位置。
    /// </summary>
    internal static int Walk(string text, int start, int end, Func<int, char, bool> onCode)
    {
        char previous = '\0';
        int i = start;
        while (i < end)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipString(text, i);
                previous = '"';
                continue;
            }
            if (c == '/' && At(text, i + 1) == '/')
            {
                i = LineEnd(text, i);
                continue;
            }
            if (c == '/' && At(text, i + 1) == '*')
            {
                i = BlockCommentEnd(text, i);
                continue;
            }
            if (c == '/' && previous is ':' or ',' or '[' or '(' or '\0')
            {
                i = SkipRegex(text, i);
                previous = '/';
                continue;
            }
            if (!onCode(i, c))
            {
                return i;
            }
            if (!char.IsWhiteSpace(c))
            {
                previous = c;
            }
            i++;
        }
        return Math.Min(i, end);
    }

    /// <summary>一个数组元素在哪里结束:顶层的 <c>,</c> 或 <c>]</c>(返回它的位置)。</summary>
    private static int ElementEnd(string text, int start)
    {
        int depth = 0;
        return Walk(text, start, text.Length, (_, c) =>
        {
            switch (c)
            {
                case '{' or '[' or '(':
                    depth++;
                    return true;
                case ']' when depth == 0:
                    return false;
                case ',' when depth == 0:
                    return false;
                case '}' or ']' or ')':
                    depth--;
                    return depth >= 0;
                default:
                    return true;
            }
        });
    }

    /// <summary>与 <paramref name="open" /> 处的括号配对的那个;配不上返回 -1。</summary>
    private static int Matching(string text, int open)
    {
        int depth = 0;
        int found = -1;
        Walk(text, open, text.Length, (i, c) =>
        {
            if (c is '{' or '[' or '(')
            {
                depth++;
            }
            else if (c is '}' or ']' or ')')
            {
                depth--;
                if (depth == 0)
                {
                    found = i;
                    return false;
                }
            }
            return true;
        });
        return found;
    }

    /// <summary>区间里第一个顶层逗号;没有返回 -1。</summary>
    private static int TopLevelComma(string text, int start, int end)
    {
        int depth = 0;
        int found = -1;
        Walk(text, start, end, (i, c) =>
        {
            if (c is '{' or '[' or '(')
            {
                depth++;
            }
            else if (c is '}' or ']' or ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                found = i;
                return false;
            }
            return true;
        });
        return found;
    }

    /// <summary>跳过空白与注释。</summary>
    internal static int SkipTrivia(string text, int i)
    {
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/' && At(text, i + 1) == '/')
            {
                i = LineEnd(text, i);
            }
            else if (c == '/' && At(text, i + 1) == '*')
            {
                i = BlockCommentEnd(text, i);
            }
            else
            {
                break;
            }
        }
        return i;
    }

    /// <summary>字符串字面量结束后的位置(处理转义;没收尾就到文本末尾)。</summary>
    internal static int SkipString(string text, int start)
    {
        char quote = text[start];
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (text[i] == quote)
            {
                return i + 1;
            }
            if (text[i] == '\n')
            {
                return i;
            }
            i++;
        }
        return text.Length;
    }

    private static int SkipRegex(string text, int start)
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

    private static int LineEnd(string text, int i)
    {
        int end = text.IndexOf('\n', i);
        return end < 0 ? text.Length : end;
    }

    private static int BlockCommentEnd(string text, int i)
    {
        int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 ? text.Length : close + 2;
    }

    private static char At(string text, int i) => i < text.Length ? text[i] : '\0';

    private static int Indentation(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] is ' ' or '\t')
        {
            n++;
        }
        return n;
    }

    /// <summary>统一换行符(Windows 剪贴板粘进来的是 CRLF)。</summary>
    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static PipelineParse Fail(string key, string argument, int offset, int length) =>
        new([], key, argument, Math.Max(0, offset), Math.Max(1, length));
}
