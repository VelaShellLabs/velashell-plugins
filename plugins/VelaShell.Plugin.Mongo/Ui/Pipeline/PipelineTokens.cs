using System.Text;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>mongosh 代码里一个记号的种类(决定它的颜色)。</summary>
internal enum PipelineTokenKind
{
    /// <summary>括号、冒号、逗号、空白。</summary>
    Punct,

    /// <summary>键名(<c>status:</c>)。</summary>
    Key,

    /// <summary>运算符(<c>$gte</c>、<c>$sum</c>)。</summary>
    Operator,

    /// <summary>字符串。</summary>
    String,

    /// <summary>字段路径字符串(<c>"$items.qty"</c>)。</summary>
    FieldPath,

    /// <summary>数字。</summary>
    Number,

    /// <summary>类型构造器(<c>ISODate</c>、<c>NumberDecimal</c>)。</summary>
    Constructor,

    /// <summary><c>ObjectId</c>。</summary>
    ObjectId,

    /// <summary><c>true</c> / <c>false</c>。</summary>
    Bool,

    /// <summary><c>null</c> / <c>undefined</c>。</summary>
    Null,

    /// <summary>注释。</summary>
    Comment,

    /// <summary>其余标识符。</summary>
    Identifier
}

/// <summary>一个记号。</summary>
/// <param name="Start">起点。</param>
/// <param name="Length">长度。</param>
/// <param name="Kind">种类。</param>
internal readonly record struct PipelineToken(int Start, int Length, PipelineTokenKind Kind);

/// <summary>
/// 一个轻量的 mongosh 记号切分器:折叠态卡片里那一行内联阶段体要按 token 着色,
/// 而那里放不下一个 AvaloniaEdit(几十张卡片各挂一个编辑器,滚动时会明显卡)。
/// 配色与 <c>Syntax/Mongosh.xshd</c> 逐条对应(键名用主文字色,与设计稿的阶段体一致)。
/// </summary>
internal static class PipelineTokens
{
    private static readonly HashSet<string> Constructors =
    [
        with(StringComparer.Ordinal),
        "ISODate", "Date", "NumberDecimal", "NumberLong", "NumberInt", "NumberDouble", "Decimal128", "Long", "Int32",
        "Double", "BinData", "UUID", "Timestamp", "MinKey", "MaxKey", "RegExp", "HexData", "MD5", "new"
    ];

    /// <summary>切分。</summary>
    public static IReadOnlyList<PipelineToken> Tokenize(string text)
    {
        var tokens = new List<PipelineToken>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            int start = i;
            if (c is '"' or '\'')
            {
                i = PipelineText.SkipString(text, i);
                bool path = i - start > 2 && text[start + 1] == '$';
                tokens.Add(new(start, i - start, path ? PipelineTokenKind.FieldPath : PipelineTokenKind.String));
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                int end = text[i + 1] == '/'
                    ? (text.IndexOf('\n', i) is var nl && nl >= 0 ? nl : text.Length)
                    : (text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close && close >= 0 ? close + 2 : text.Length);
                tokens.Add(new(start, end - start, PipelineTokenKind.Comment));
                i = end;
                continue;
            }
            if (char.IsDigit(c) || (c is '-' or '.' && i + 1 < text.Length && char.IsDigit(text[i + 1]) && !PreviousIsWord(text, i)))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '_'))
                {
                    i++;
                }
                tokens.Add(new(start, i - start, PipelineTokenKind.Number));
                continue;
            }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
                {
                    i++;
                }
                string word = text[start..i];
                tokens.Add(new(start, i - start, Classify(word, IsFollowedByColon(text, i))));
                continue;
            }
            i++;
            // 连续的标点与空白合成一个记号,少建几个 Run。
            while (i < text.Length && !IsTokenStart(text, i))
            {
                i++;
            }
            tokens.Add(new(start, i - start, PipelineTokenKind.Punct));
        }
        return tokens;
    }

    /// <summary>记号种类 → 宿主颜色令牌。</summary>
    public static string ColorToken(PipelineTokenKind kind) => kind switch
    {
        PipelineTokenKind.Key or PipelineTokenKind.Identifier => "VelaTextPrimary",
        PipelineTokenKind.Operator => "VelaShellMagenta",
        PipelineTokenKind.String => "VelaShellCyan",
        PipelineTokenKind.FieldPath => "VelaWarning",
        PipelineTokenKind.Number => "VelaShellGreen",
        PipelineTokenKind.Constructor => "VelaInfo",
        PipelineTokenKind.ObjectId => "VelaShellBlue",
        PipelineTokenKind.Bool => "VelaShellYellow",
        PipelineTokenKind.Null or PipelineTokenKind.Comment => "VelaTextTertiary",
        _ => "VelaTextSecondary"
    };

    /// <summary>
    /// 把多行代码压成一行(折叠态卡片):注释去掉,字符串外的换行与连续空白收成一个空格。
    /// </summary>
    public static string OneLine(string code)
    {
        var b = new StringBuilder(code.Length);
        bool space = false;
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (c is '"' or '\'')
            {
                int end = PipelineText.SkipString(code, i);
                if (space && b.Length > 0)
                {
                    _ = b.Append(' ');
                }
                space = false;
                _ = b.Append(code, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                int nl = code.IndexOf('\n', i);
                i = nl < 0 ? code.Length : nl;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                space = true;
                i++;
                continue;
            }
            if (space && b.Length > 0)
            {
                _ = b.Append(' ');
            }
            space = false;
            _ = b.Append(c);
            i++;
        }
        return b.ToString();
    }

    private static PipelineTokenKind Classify(string word, bool key)
    {
        if (word.StartsWith('$'))
        {
            return PipelineTokenKind.Operator;
        }
        if (key)
        {
            return PipelineTokenKind.Key;
        }
        return word switch
        {
            "true" or "false" => PipelineTokenKind.Bool,
            "null" or "undefined" => PipelineTokenKind.Null,
            "ObjectId" => PipelineTokenKind.ObjectId,
            _ when Constructors.Contains(word) => PipelineTokenKind.Constructor,
            _ => PipelineTokenKind.Identifier
        };
    }

    private static bool IsFollowedByColon(string text, int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }
        return i < text.Length && text[i] == ':';
    }

    private static bool PreviousIsWord(string text, int i) => i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] is '_' or '$');

    private static bool IsTokenStart(string text, int i)
    {
        char c = text[i];
        return c is '"' or '\'' or '_' or '$' || char.IsLetterOrDigit(c)
               || (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
               || (c is '-' && i + 1 < text.Length && char.IsDigit(text[i + 1]));
    }
}
