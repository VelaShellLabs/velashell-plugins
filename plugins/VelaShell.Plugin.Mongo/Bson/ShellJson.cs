using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace VelaShell.Plugin.Mongo.Bson;

/// <summary>一条语法诊断(编辑器下划线 + 行尾提示)。</summary>
/// <param name="Offset">起点(相对于被解析文本)。</param>
/// <param name="Length">长度。</param>
/// <param name="MessageKey">文案键。</param>
/// <param name="Argument">文案参数。</param>
/// <param name="Fix">一键修复后的替换文本;没有为 <see langword="null" />。</param>
public sealed record ShellDiagnostic(int Offset, int Length, string MessageKey, string Argument = "", string? Fix = null);

/// <summary>shell 字面量解析失败。</summary>
/// <param name="message">原因。</param>
/// <param name="offset">大致位置(相对于被解析文本);未知为 -1。</param>
public sealed class ShellJsonException(string message, int offset) : FormatException(message)
{
    /// <summary>大致位置。</summary>
    public int Offset { get; } = offset;
}

/// <summary>
/// mongosh 写法的字面量 → BSON。
/// <para>
/// 用户在筛选栏、查询编辑器、管道阶段、文档编辑器里敲的都是 mongosh 的写法:键名不加引号、
/// <c>ObjectId("…")</c>、<c>ISODate("…")</c>、<c>NumberDecimal("…")</c>、正则字面量、单引号字符串,
/// 偶尔还有行尾逗号和注释。驱动的 <see cref="JsonReader" /> 能吃下大部分构造器写法,
/// 这里补上它吃不下的那几样(注释、行尾逗号、<c>$</c> 开头的裸键),并把"含点号的裸键"
/// 认成**错误**而不是悄悄加引号 —— mongosh 自己就会在那里报错,设计稿 03 也把它画成了一条诊断。
/// </para>
/// </summary>
public static class ShellJson
{
    /// <summary>解析一份文档;空白文本视为空文档 <c>{}</c>。</summary>
    /// <exception cref="ShellJsonException">语法错误。</exception>
    public static BsonDocument ParseDocument(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }
        BsonValue value = ParseValue(text);
        return value as BsonDocument ?? throw new ShellJsonException("Expected a document { … }", 0);
    }

    /// <summary>解析一个数组(管道)。</summary>
    /// <exception cref="ShellJsonException">语法错误。</exception>
    public static BsonArray ParseArray(string text)
    {
        BsonValue value = ParseValue(text);
        return value as BsonArray ?? throw new ShellJsonException("Expected an array [ … ]", 0);
    }

    /// <summary>解析任意值。</summary>
    /// <exception cref="ShellJsonException">语法错误。</exception>
    public static BsonValue ParseValue(string text)
    {
        string normalized = Normalize(text, out IReadOnlyList<ShellDiagnostic> diagnostics);
        if (diagnostics.FirstOrDefault() is { } first)
        {
            throw new ShellJsonException(first.MessageKey + (first.Argument.Length > 0 ? ": " + first.Argument : ""), first.Offset);
        }
        try
        {
            using var reader = new JsonReader(normalized);
            var context = BsonDeserializationContext.CreateRoot(reader);
            BsonValue value = BsonValueSerializer.Instance.Deserialize(context);
            if (reader.State != BsonReaderState.Done && !reader.IsAtEndOfFile())
            {
                throw new ShellJsonException("Unexpected trailing text", normalized.Length);
            }
            return value;
        }
        catch (ShellJsonException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or BsonSerializationException or EndOfStreamException or InvalidOperationException)
        {
            throw new ShellJsonException(ex.Message, -1);
        }
    }

    /// <summary>尝试解析一份文档(界面实时校验用,不抛)。</summary>
    public static bool TryParseDocument(string? text, out BsonDocument document, out string? error)
    {
        try
        {
            document = ParseDocument(text);
            error = null;
            return true;
        }
        catch (ShellJsonException ex)
        {
            document = [];
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 只做诊断,不解析(编辑器边打字边标红用)。
    /// </summary>
    public static IReadOnlyList<ShellDiagnostic> Diagnose(string text)
    {
        _ = Normalize(text, out IReadOnlyList<ShellDiagnostic> diagnostics);
        return diagnostics;
    }

    /// <summary>
    /// 规范化:去注释、去行尾逗号、给 <c>$gte</c> 这类裸键补引号;含点号的裸键记一条诊断。
    /// 字符串、正则字面量原样跳过。
    /// </summary>
    internal static string Normalize(string text, out IReadOnlyList<ShellDiagnostic> diagnostics)
    {
        var output = new StringBuilder(text.Length + 16);
        var found = new List<ShellDiagnostic>();
        // 每层括号是不是对象(只有对象里的"标识符 + 冒号"才是键)。
        var stack = new Stack<char>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            // 字符串:原样拷贝到收尾引号(处理转义)。
            if (c is '"' or '\'')
            {
                int end = SkipString(text, i);
                _ = output.Append(text, i, end - i);
                i = end;
                continue;
            }
            // 注释。
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 2;
                continue;
            }
            // 正则字面量:前一个有效字符是 : , [ ( 时 / 起头的是正则,不是除号。
            if (c == '/' && PreviousSignificant(output) is ':' or ',' or '[' or '(' or '\0')
            {
                int end = SkipRegex(text, i);
                _ = output.Append(text, i, end - i);
                i = end;
                continue;
            }
            if (c is '{' or '[' or '(')
            {
                stack.Push(c);
                _ = output.Append(c);
                i++;
                continue;
            }
            if (c is '}' or ']' or ')')
            {
                // 行尾逗号:`{ a: 1, }` → `{ a: 1 }`。
                TrimTrailingComma(output);
                if (stack.Count > 0)
                {
                    _ = stack.Pop();
                }
                _ = output.Append(c);
                i++;
                continue;
            }
            if (IsIdentifierStart(c) && stack.Count > 0 && stack.Peek() == '{')
            {
                int start = i;
                int end = i;
                while (end < text.Length && (IsIdentifierPart(text[end]) || text[end] == '.'))
                {
                    end++;
                }
                int after = end;
                while (after < text.Length && char.IsWhiteSpace(text[after]))
                {
                    after++;
                }
                bool isKey = after < text.Length && text[after] == ':' && LastSignificantIsKeyPosition(output);
                string word = text[start..end];
                if (isKey)
                {
                    if (word.Contains('.'))
                    {
                        found.Add(new(start, end - start, "Shell_DottedKey", word, "\"" + word + "\""));
                    }
                    _ = output.Append('"').Append(word).Append('"');
                    i = end;
                    continue;
                }
                _ = output.Append(word);
                i = end;
                continue;
            }
            _ = output.Append(c);
            i++;
        }
        diagnostics = found;
        return output.ToString();
    }

    private static bool LastSignificantIsKeyPosition(StringBuilder output) => PreviousSignificant(output) is '{' or ',';

    private static char PreviousSignificant(StringBuilder output)
    {
        for (int k = output.Length - 1; k >= 0; k--)
        {
            if (!char.IsWhiteSpace(output[k]))
            {
                return output[k];
            }
        }
        return '\0';
    }

    private static void TrimTrailingComma(StringBuilder output)
    {
        for (int k = output.Length - 1; k >= 0; k--)
        {
            if (char.IsWhiteSpace(output[k]))
            {
                continue;
            }
            if (output[k] == ',')
            {
                _ = output.Remove(k, 1);
            }
            return;
        }
    }

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

    /// <summary>标识符首字符(含 <c>$</c>,MongoDB 的操作符都以它起头)。</summary>
    internal static bool IsIdentifierStart(char c) => char.IsLetter(c) || c is '_' or '$';

    /// <summary>标识符后续字符。</summary>
    internal static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
