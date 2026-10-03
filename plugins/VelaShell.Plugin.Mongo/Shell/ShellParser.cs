using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>一个调用实参:原文、在语句里的位置、解析出的值。</summary>
/// <param name="Text">原文。</param>
/// <param name="Offset">起点(相对语句)。</param>
/// <param name="Value">值。</param>
public sealed record ShellArgument(string Text, int Offset, BsonValue Value);

/// <summary>链上的一节:<c>.find(…)</c>、<c>.sort(…)</c>。</summary>
/// <param name="Name">方法名。</param>
/// <param name="Offset">方法名的起点(相对语句)。</param>
/// <param name="Arguments">实参。</param>
public sealed record ShellCall(string Name, int Offset, IReadOnlyList<ShellArgument> Arguments)
{
    /// <summary>第 <paramref name="index" /> 个实参的值;没有为 <see langword="null" />。</summary>
    public BsonValue? Arg(int index) => index < Arguments.Count ? Arguments[index].Value : null;

    /// <summary>第 <paramref name="index" /> 个实参作为文档;没有或不是文档为 <see langword="null" />。</summary>
    public BsonDocument? Document(int index) => Arg(index) as BsonDocument;
}

/// <summary>语句的种类。</summary>
public enum ShellCommandKind
{
    /// <summary><c>use shop</c> / <c>use("shop")</c>。</summary>
    Use,

    /// <summary><c>show dbs</c> / <c>show collections</c>。</summary>
    Show,

    /// <summary>库级方法:<c>db.runCommand(…)</c>、<c>db.getCollectionNames()</c>。</summary>
    Database,

    /// <summary>集合方法:<c>db.orders.find(…)</c>。</summary>
    Collection
}

/// <summary>解析好的一条语句。</summary>
public sealed class ShellCommand
{
    /// <summary>来源语句。</summary>
    public required ShellStatement Statement { get; init; }

    /// <summary>种类。</summary>
    public required ShellCommandKind Kind { get; init; }

    /// <summary><c>use</c> 的库名 / <c>show</c> 的对象(<c>dbs</c>、<c>collections</c>)。</summary>
    public string? Target { get; init; }

    /// <summary><c>db.getSiblingDB("x")</c> 指定的库;没有为 <see langword="null" />(用会话当前库)。</summary>
    public string? Database { get; init; }

    /// <summary>集合名(<see cref="ShellCommandKind.Collection" />)。</summary>
    public string? Collection { get; init; }

    /// <summary>主方法(<c>find</c>、<c>aggregate</c>、<c>runCommand</c>…);<c>use</c> / <c>show</c> 为 <see langword="null" />。</summary>
    public ShellCall? Method { get; init; }

    /// <summary>主方法之后的游标修饰(<c>sort</c>、<c>limit</c>…),按书写顺序。</summary>
    public IReadOnlyList<ShellCall> Chain { get; init; } = [];

    /// <summary><c>.explain("…")</c> / <c>db.coll.explain("…").find()</c> 的 verbosity;不是 explain 为 <see langword="null" />。</summary>
    public string? Explain { get; init; }

    /// <summary>给人看的操作名(结果页签上的 <c>find</c> / <c>aggregate</c> / <c>use</c>)。</summary>
    public string Operation => Kind switch
    {
        ShellCommandKind.Use => "use",
        ShellCommandKind.Show => "show " + Target,
        _ => Method?.Name ?? ""
    };

    /// <summary>链上某个修饰;没有为 <see langword="null" />(同名写了两次以后一次为准,与驱动的游标一致)。</summary>
    public ShellCall? Modifier(string name) => Chain.LastOrDefault(c => c.Name == name);

    /// <summary>这条语句会不会写库(只读模式据此拦截)。</summary>
    public bool IsWrite => Kind switch
    {
        ShellCommandKind.Collection => Method?.Name is { } name && (ShellParser.CollectionWrites.Contains(name)
            || name == "aggregate" && Method.Arg(0) is BsonArray pipeline && pipeline.Any(static s => s is BsonDocument d && (d.Contains("$out") || d.Contains("$merge")))),
        ShellCommandKind.Database => Method?.Name is "createCollection" or "dropDatabase" or "createView"
            || Method?.Name is "runCommand" or "adminCommand" && Method.Arg(0) is BsonDocument command && command.ElementCount > 0
               && ShellParser.WriteCommands.Contains(command.GetElement(0).Name),
        _ => false
    };
}

/// <summary>解析失败(编辑器里一条诊断)。</summary>
/// <param name="messageKey">文案键。</param>
/// <param name="argument">文案参数。</param>
/// <param name="offset">起点(相对语句)。</param>
/// <param name="length">长度。</param>
public sealed class ShellParseException(string messageKey, string argument, int offset, int length)
    : FormatException($"{messageKey}: {argument}")
{
    /// <summary>文案键。</summary>
    public string MessageKey { get; } = messageKey;

    /// <summary>文案参数。</summary>
    public string Argument { get; } = argument;

    /// <summary>起点(相对语句)。</summary>
    public int Offset { get; } = offset;

    /// <summary>长度。</summary>
    public int Length { get; } = Math.Max(1, length);
}

/// <summary>
/// 一条 mongosh 语句 → <see cref="ShellCommand" />。
/// <para>
/// 不是 JavaScript 解释器:只认查询编辑器真正会写的那几种形状 —— <c>use</c> / <c>show</c>、
/// <c>db.coll.method(…).modifier(…)</c>、<c>db.getCollection("x")</c>、<c>db.getSiblingDB("x")</c>、
/// <c>db.runCommand(…)</c>。实参一律按 mongosh 字面量(<see cref="ShellJson" />)解析。
/// 认不出的写法给一条**定位到字符**的诊断,而不是把整段丢给服务器换回一句看不懂的错误。
/// </para>
/// </summary>
public static class ShellParser
{
    /// <summary>会写库的集合方法。</summary>
    public static IReadOnlySet<string> CollectionWrites { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "insertOne", "insertMany", "insert", "updateOne", "updateMany", "update", "replaceOne", "deleteOne", "deleteMany", "remove",
        "findOneAndUpdate", "findOneAndReplace", "findOneAndDelete", "bulkWrite", "createIndex", "createIndexes", "dropIndex",
        "dropIndexes", "drop", "renameCollection"
    };

    /// <summary>经 <c>runCommand</c> 发出时算写操作的命令名。</summary>
    public static IReadOnlySet<string> WriteCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "findAndModify", "drop", "dropDatabase", "create", "createIndexes", "dropIndexes",
        "renameCollection", "collMod", "createUser", "dropUser", "updateUser", "createRole", "dropRole", "updateRole",
        "grantRolesToUser", "revokeRolesFromUser", "convertToCapped", "cloneCollectionAsCapped", "compact", "reIndex",
        "applyOps", "shutdown", "killOp", "setParameter", "fsync"
    };

    /// <summary>库级方法(<c>db.xxx()</c> 里不是集合名的那些)。</summary>
    public static IReadOnlySet<string> DatabaseMethods { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "getCollectionNames", "getCollectionInfos", "stats", "runCommand", "adminCommand", "createCollection", "createView",
        "dropDatabase", "getName", "version", "serverStatus", "hostInfo", "currentOp", "listCommands", "getProfilingStatus",
        "getUsers", "getRoles", "aggregate"
    };

    /// <summary>链上允许出现的游标方法。</summary>
    public static IReadOnlySet<string> CursorMethods { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "sort", "skip", "limit", "project", "projection", "hint", "maxTimeMS", "collation", "comment", "batchSize",
        "count", "size", "itcount", "toArray", "pretty", "explain", "allowDiskUse", "readPref", "noCursorTimeout", "max", "min",
        "returnKey", "showRecordId", "readConcern"
    };

    /// <summary>解析。</summary>
    /// <exception cref="ShellParseException">认不出的写法或字面量语法错误。</exception>
    public static ShellCommand Parse(ShellStatement statement)
    {
        string text = statement.Text;
        int i = ShellScript.SkipTrivia(text, 0);
        (string word, int wordStart, int wordEnd) = ReadIdentifier(text, i);
        if (word.Length == 0)
        {
            throw new ShellParseException("Query_ParseUnexpected", Snippet(text, i), i, 1);
        }
        return word switch
        {
            "use" => ParseUse(statement, wordEnd),
            "show" => ParseShow(statement, wordEnd),
            "db" => ParseDb(statement, wordEnd),
            _ => throw new ShellParseException("Query_ParseUnsupported", word, wordStart, word.Length)
        };
    }

    /// <summary>尝试解析(诊断用,不抛)。</summary>
    public static bool TryParse(ShellStatement statement, out ShellCommand? command, out ShellParseException? error)
    {
        try
        {
            command = Parse(statement);
            error = null;
            return true;
        }
        catch (ShellParseException ex)
        {
            command = null;
            error = ex;
            return false;
        }
    }

    private static ShellCommand ParseUse(ShellStatement statement, int i)
    {
        string text = statement.Text;
        i = ShellScript.SkipTrivia(text, i);
        string? name;
        if (i < text.Length && text[i] == '(')
        {
            IReadOnlyList<ShellArgument> args = ParseArguments(text, i, out int close);
            name = args.Count > 0 && args[0].Value.IsString ? args[0].Value.AsString : null;
            EnsureEnd(text, close + 1);
        }
        else
        {
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ';')
            {
                i++;
            }
            name = text[start..i].Trim('"', '\'');
            EnsureEnd(text, i);
        }
        if (string.IsNullOrEmpty(name))
        {
            throw new ShellParseException("Query_ParseNeedsDatabase", "", 0, text.Length);
        }
        return new ShellCommand { Statement = statement, Kind = ShellCommandKind.Use, Target = name };
    }

    private static ShellCommand ParseShow(ShellStatement statement, int i)
    {
        string text = statement.Text;
        i = ShellScript.SkipTrivia(text, i);
        (string what, int start, int end) = ReadIdentifier(text, i);
        string normalized = what switch
        {
            "dbs" or "databases" => "dbs",
            "collections" or "tables" => "collections",
            _ => throw new ShellParseException("Query_ParseShowTarget", what, start, Math.Max(1, what.Length))
        };
        EnsureEnd(text, end);
        return new ShellCommand { Statement = statement, Kind = ShellCommandKind.Show, Target = normalized };
    }

    /// <summary>链上的一节(解析中间态):名字 + 是否带调用括号。</summary>
    private sealed record Segment(string Name, int Offset, IReadOnlyList<ShellArgument>? Arguments);

    private static ShellCommand ParseDb(ShellStatement statement, int i)
    {
        string text = statement.Text;
        var segments = new List<Segment>();
        while (true)
        {
            i = ShellScript.SkipTrivia(text, i);
            if (i >= text.Length)
            {
                break;
            }
            char c = text[i];
            if (c == '.')
            {
                int nameStart = ShellScript.SkipTrivia(text, i + 1);
                (string name, int start, int end) = ReadIdentifier(text, nameStart);
                if (name.Length == 0)
                {
                    throw new ShellParseException("Query_ParseExpectedName", Snippet(text, nameStart), nameStart, 1);
                }
                segments.Add(new Segment(name, start, null));
                i = end;
            }
            else if (c == '[')
            {
                int close = ShellScript.MatchBracket(text, i);
                if (close < 0)
                {
                    throw new ShellParseException("Query_ParseUnclosed", "[", i, 1);
                }
                string inner = text[(i + 1)..close].Trim();
                if (inner.Length < 2 || inner[0] is not ('"' or '\'') || inner[^1] != inner[0])
                {
                    throw new ShellParseException("Query_ParseExpectedName", inner, i + 1, Math.Max(1, close - i - 1));
                }
                segments.Add(new Segment(inner[1..^1], i + 1, null));
                i = close + 1;
            }
            else if (c == '(')
            {
                if (segments.Count == 0 || segments[^1].Arguments is not null)
                {
                    throw new ShellParseException("Query_ParseUnexpected", "(", i, 1);
                }
                IReadOnlyList<ShellArgument> args = ParseArguments(text, i, out int close);
                segments[^1] = segments[^1] with { Arguments = args };
                i = close + 1;
            }
            else
            {
                throw new ShellParseException("Query_ParseUnexpected", Snippet(text, i), i, 1);
            }
        }
        if (segments.Count == 0)
        {
            throw new ShellParseException("Query_ParseNeedsMethod", "db", 0, 2);
        }

        int index = 0;
        string? database = null;
        if (segments[0] is { Name: "getSiblingDB", Arguments: { } siblingArgs })
        {
            database = siblingArgs.Count > 0 && siblingArgs[0].Value.IsString
                ? siblingArgs[0].Value.AsString
                : throw new ShellParseException("Query_ParseNeedsDatabase", "", segments[0].Offset, "getSiblingDB".Length);
            index = 1;
        }
        if (index >= segments.Count)
        {
            throw new ShellParseException("Query_ParseNeedsMethod", "db", 0, text.Length);
        }

        string? collection = null;
        if (segments[index] is { Name: "getCollection", Arguments: { } collArgs })
        {
            collection = collArgs.Count > 0 && collArgs[0].Value.IsString
                ? collArgs[0].Value.AsString
                : throw new ShellParseException("Query_ParseNeedsCollection", "", segments[index].Offset, "getCollection".Length);
            index++;
        }
        else if (segments[index].Arguments is not null && DatabaseMethods.Contains(segments[index].Name))
        {
            return Finish(statement, ShellCommandKind.Database, database, null, segments, index);
        }
        else
        {
            // db.system.profile.find():点分的若干节合起来才是集合名,第一个带括号的那节才是方法。
            int first = index;
            while (index < segments.Count && segments[index].Arguments is null)
            {
                index++;
            }
            if (index == first)
            {
                throw new ShellParseException("Query_ParseUnknownDbMethod", segments[first].Name, segments[first].Offset, segments[first].Name.Length);
            }
            collection = string.Join('.', segments.Skip(first).Take(index - first).Select(static s => s.Name));
        }
        if (index >= segments.Count)
        {
            Segment last = segments[^1];
            throw new ShellParseException("Query_ParseNeedsMethod", collection ?? "db", last.Offset, last.Name.Length);
        }
        return Finish(statement, ShellCommandKind.Collection, database, collection, segments, index);
    }

    private static ShellCommand Finish(ShellStatement statement, ShellCommandKind kind, string? database, string? collection,
        List<Segment> segments, int index)
    {
        string? explain = null;
        Segment method = segments[index];
        if (method.Arguments is null)
        {
            throw new ShellParseException("Query_ParseNeedsCall", method.Name, method.Offset, method.Name.Length);
        }
        // db.coll.explain("executionStats").find(…):explain 写在前面时,后一节才是真正的方法。
        if (kind == ShellCommandKind.Collection && method.Name == "explain")
        {
            explain = Verbosity(method.Arguments);
            index++;
            if (index >= segments.Count || segments[index].Arguments is null)
            {
                throw new ShellParseException("Query_ParseNeedsMethod", "explain", method.Offset, method.Name.Length);
            }
            method = segments[index];
        }
        var chain = new List<ShellCall>();
        foreach (Segment segment in segments.Skip(index + 1))
        {
            if (segment.Arguments is null)
            {
                throw new ShellParseException("Query_ParseNeedsCall", segment.Name, segment.Offset, segment.Name.Length);
            }
            if (kind == ShellCommandKind.Collection && !CursorMethods.Contains(segment.Name))
            {
                throw new ShellParseException("Query_ParseUnknownCursor", segment.Name, segment.Offset, segment.Name.Length);
            }
            if (segment.Name == "explain")
            {
                explain = Verbosity(segment.Arguments);
                continue;
            }
            chain.Add(new ShellCall(segment.Name, segment.Offset, segment.Arguments));
        }
        return new ShellCommand
        {
            Statement = statement,
            Kind = kind,
            Database = database,
            Collection = collection,
            Method = new ShellCall(method.Name, method.Offset, method.Arguments!),
            Chain = chain,
            Explain = explain
        };
    }

    /// <summary><c>explain()</c> 的 verbosity:不写为 queryPlanner(与 mongosh 一致);<c>true</c> 视为 allPlansExecution。</summary>
    private static string Verbosity(IReadOnlyList<ShellArgument> args) => args.Count == 0
        ? "queryPlanner"
        : args[0].Value switch
        {
            BsonString s => s.Value,
            BsonBoolean { Value: true } => "allPlansExecution",
            _ => "queryPlanner"
        };

    /// <summary>
    /// 解析从 <paramref name="open" />(左圆括号)起的实参列表。<paramref name="close" /> 返回右括号位置。
    /// </summary>
    private static IReadOnlyList<ShellArgument> ParseArguments(string text, int open, out int close)
    {
        close = ShellScript.MatchBracket(text, open);
        if (close < 0)
        {
            throw new ShellParseException("Query_ParseUnclosed", "(", open, 1);
        }
        var args = new List<ShellArgument>();
        foreach ((int start, int end) in ShellScript.SplitTopLevel(text, open + 1, close))
        {
            string raw = text[start..end];
            args.Add(new ShellArgument(raw, start, ParseLiteral(raw, start)));
        }
        return args;
    }

    /// <summary>一个实参 → BSON;失败时把 <see cref="ShellJson" /> 的位置换算到语句坐标。</summary>
    private static BsonValue ParseLiteral(string raw, int offset)
    {
        string trimmed = raw.Trim();
        // 函数实参(forEach(function…)、箭头函数)不是查询编辑器支持的东西 —— 明说,而不是报一句 JSON 语法错误。
        if (trimmed.StartsWith("function", StringComparison.Ordinal)
            || (trimmed.Length > 0 && trimmed[0] is not ('{' or '[' or '"' or '\'') && trimmed.Contains("=>", StringComparison.Ordinal)))
        {
            throw new ShellParseException("Query_ParseFunction", "", offset, raw.Length);
        }
        try
        {
            return ShellJson.ParseValue(raw);
        }
        catch (ShellJsonException ex)
        {
            int at = ex.Offset >= 0 && ex.Offset < raw.Length ? ex.Offset : 0;
            int length = ex.Offset >= 0 && ex.Offset < raw.Length ? 1 : raw.Length;
            if (ex.Message.StartsWith("Shell_DottedKey", StringComparison.Ordinal))
            {
                string key = ex.Message.Length > "Shell_DottedKey: ".Length ? ex.Message["Shell_DottedKey: ".Length..] : "";
                throw new ShellParseException("Shell_DottedKey", key, offset + at, Math.Max(1, key.Length));
            }
            throw new ShellParseException("Query_ParseLiteral", ex.Message, offset + at, length);
        }
    }

    private static void EnsureEnd(string text, int i)
    {
        i = ShellScript.SkipTrivia(text, i);
        if (i < text.Length)
        {
            throw new ShellParseException("Query_ParseUnexpected", Snippet(text, i), i, text.Length - i);
        }
    }

    private static (string Word, int Start, int End) ReadIdentifier(string text, int i)
    {
        int start = i;
        if (i >= text.Length || !ShellScript.IsIdentifierStart(text[i]))
        {
            return ("", start, start);
        }
        while (i < text.Length && ShellScript.IsIdentifierPart(text[i]))
        {
            i++;
        }
        return (text[start..i], start, i);
    }

    private static string Snippet(string text, int i) =>
        i >= text.Length ? "" : text.Substring(i, Math.Min(12, text.Length - i)).Split('\n')[0];
}
