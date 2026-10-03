using System.Text.RegularExpressions;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>光标处要补全的"那个位置"是什么。</summary>
internal enum CompletionSlot
{
    /// <summary>不补全。</summary>
    None,

    /// <summary>语句开头(<c>db</c> / <c>use</c> / <c>show</c>)。</summary>
    Statement,

    /// <summary><c>db.</c> 之后:集合名与库级方法。</summary>
    DbMember,

    /// <summary><c>db.coll.</c> 之后:集合方法。</summary>
    CollectionMember,

    /// <summary><c>).</c> 之后:游标方法。</summary>
    CursorMember,

    /// <summary>对象的键位置(<c>{ |</c> 或 <c>, |</c>)。</summary>
    Key,

    /// <summary>值位置(<c>: |</c>、数组元素、调用实参)。</summary>
    Value,

    /// <summary>字符串里的 <c>$字段路径</c>(聚合表达式)。</summary>
    FieldPathString,

    /// <summary>某个调用的字符串实参(<c>getCollection("|")</c>、<c>use("|")</c>、<c>hint("|")</c>、<c>distinct("|")</c>)。</summary>
    StringArgument
}

/// <summary>光标所在对象 / 数组的语义角色(决定给字段、运算符还是阶段)。</summary>
internal enum ObjectRole
{
    /// <summary>认不出。</summary>
    Unknown,

    /// <summary>查询条件(键:字段与逻辑运算符)。</summary>
    Filter,

    /// <summary><c>$and / $or</c> 的条件数组。</summary>
    FilterList,

    /// <summary>字段的运算符对象(<c>{ total: { | } }</c>:比较运算符)。</summary>
    Operator,

    /// <summary>更新文档(键:更新运算符)。</summary>
    Update,

    /// <summary>更新运算符的字段对象(<c>$set: { | }</c>)。</summary>
    UpdateFields,

    /// <summary>投影。</summary>
    Projection,

    /// <summary>排序。</summary>
    Sort,

    /// <summary>索引键(hint / createIndex)。</summary>
    IndexKeys,

    /// <summary>要写入的文档。</summary>
    Document,

    /// <summary>文档数组(insertMany)。</summary>
    DocumentList,

    /// <summary>聚合管道数组。</summary>
    Pipeline,

    /// <summary>管道里的一个阶段对象(键:阶段名)。</summary>
    Stage,

    /// <summary><c>$group</c> 的主体。</summary>
    Group,

    /// <summary><c>$group</c> 里某个输出字段的累加器对象。</summary>
    Accumulator,

    /// <summary>聚合表达式。</summary>
    Expression,

    /// <summary>选项对象(collation、allowDiskUse、$lookup 的参数…)。</summary>
    Options,

    /// <summary><c>$facet</c> 主体(键是名字,值是子管道)。</summary>
    Facet,

    /// <summary><c>$in / $nin / $all</c> 的值数组。</summary>
    ValueList
}

/// <summary>补全上下文:光标在哪种位置、要替换哪一段、属于哪条语句的哪个集合。</summary>
internal sealed record CompletionContext
{
    /// <summary>位置种类。</summary>
    public CompletionSlot Slot { get; init; }

    /// <summary>替换起点(整段脚本坐标)。</summary>
    public int ReplaceOffset { get; init; }

    /// <summary>替换长度(光标前已打的那半截)。</summary>
    public int ReplaceLength { get; init; }

    /// <summary>已打的那半截(不含引号)。</summary>
    public string Prefix { get; init; } = "";

    /// <summary>语句作用的集合;不明为 <see langword="null" />。</summary>
    public string? Collection { get; init; }

    /// <summary><c>db.getSiblingDB("x")</c> 指定的库;没有为 <see langword="null" />。</summary>
    public string? Database { get; init; }

    /// <summary>语句的主方法(<c>find</c> / <c>aggregate</c>)。</summary>
    public string? Method { get; init; }

    /// <summary>对象角色。</summary>
    public ObjectRole Role { get; init; }

    /// <summary>值位置所属的键 / 运算符对象所属的字段。</summary>
    public string? FieldKey { get; init; }

    /// <summary>所在的管道阶段(<c>$match</c>、<c>$sort</c>)。</summary>
    public string? StageName { get; init; }

    /// <summary>光标所在管道里,当前阶段之前那些阶段的原文。</summary>
    public IReadOnlyList<string> PrecedingStages { get; init; } = [];

    /// <summary>在管道里。</summary>
    public bool InPipeline { get; init; }

    /// <summary>字符串实参属于哪个调用(<see cref="CompletionSlot.StringArgument" />)。</summary>
    public string? StringCall { get; init; }

    /// <summary>前缀在引号里(键写成了 <c>"cust</c>)。</summary>
    public bool Quoted { get; init; }

    /// <summary>光标后面紧跟着收尾的引号(插入时不必再补)。</summary>
    public bool QuoteClosed { get; init; }
}

/// <summary>上游管道阶段的输出形状。</summary>
/// <param name="Fields">输出字段。</param>
/// <param name="Source">决定这个形状的阶段(<c>$group</c>)。</param>
internal sealed record PipelineShape(IReadOnlyList<string> Fields, string Source);

/// <summary>
/// 补全的上下文分析(纯函数,不碰数据库,可单测)。
/// <para>
/// 从语句开头扫到光标,维护一个括号栈:每个 <c>(</c> 记它属于哪个调用、第几个实参;每个 <c>{</c> 记它是
/// 哪个键的值、当前在键还是值位置;每个 <c>[</c> 记已经写完的元素。再沿栈从外到内推出语义角色:
/// <c>aggregate</c> 的第一个实参是管道 → 元素是阶段 → <c>$match</c> 的值是查询条件 → 字段的值是运算符对象……
/// 设计稿 03 那个"在 <c>$group</c> 之后的 <c>$sort</c> 里给出上游输出字段"就是这样来的。
/// </para>
/// </summary>
internal static partial class ShellCompletion
{
    private sealed class Frame
    {
        public char Open;
        public int Offset;
        public string? Call;
        public int Arg;
        public string? Key;
        public bool AfterColon;
        public string? Owner;
        public int ElementStart;
        public readonly List<(int Start, int End)> Elements = [];
    }

    /// <summary>分析光标处的补全上下文。</summary>
    public static CompletionContext Analyze(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (InsideComment(text, caret))
        {
            return new CompletionContext { Slot = CompletionSlot.None, ReplaceOffset = caret };
        }
        int statementStart = StatementStart(text, caret);
        string src = text[statementStart..caret];
        (string? collection, string? method, string? database) = Head(src);

        var stack = new List<Frame>();
        string? lastWord = null;
        string? lastString = null;
        char previous = '\0';
        int i = 0;
        while (i < src.Length)
        {
            char c = src[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (ShellScript.IsCommentStart(src, i))
            {
                int end = ShellScript.SkipComment(src, i);
                if (end >= src.Length)
                {
                    return new CompletionContext { Slot = CompletionSlot.None, ReplaceOffset = caret };
                }
                i = end;
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                int end = ShellScript.SkipString(src, i);
                bool closed = end - 1 > i && src[end - 1] == c;
                if (!closed && end >= src.Length)
                {
                    // 光标在一个还没收尾的字符串里。
                    return InString(text, caret, statementStart + i + 1, src[(i + 1)..], c, stack, collection, method, database);
                }
                lastString = closed ? src[(i + 1)..(end - 1)] : src[(i + 1)..end];
                lastWord = null;
                previous = c;
                i = end;
                continue;
            }
            if (c == '/' && ShellScript.IsRegexStart(previous))
            {
                i = ShellScript.SkipRegex(src, i);
                previous = '/';
                continue;
            }
            if (ShellScript.IsIdentifierStart(c))
            {
                int start = i;
                while (i < src.Length && ShellScript.IsIdentifierPart(src[i]))
                {
                    i++;
                }
                lastWord = src[start..i];
                lastString = null;
                previous = 'a';
                continue;
            }
            Frame? top = stack.Count > 0 ? stack[^1] : null;
            switch (c)
            {
                case '(':
                    stack.Add(new Frame { Open = '(', Offset = statementStart + i, Call = previous == 'a' ? lastWord : null });
                    break;
                case '{' or '[':
                    stack.Add(new Frame
                    {
                        Open = c,
                        Offset = statementStart + i,
                        Owner = top is { Open: '{', AfterColon: true } ? top.Key : null,
                        ElementStart = statementStart + i + 1
                    });
                    break;
                case ')' or ']' or '}':
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    break;
                case ',':
                    if (top is { Open: '(' })
                    {
                        top.Arg++;
                    }
                    else if (top is { Open: '[' })
                    {
                        top.Elements.Add((top.ElementStart, statementStart + i));
                        top.ElementStart = statementStart + i + 1;
                    }
                    else if (top is { Open: '{' })
                    {
                        top.AfterColon = false;
                        top.Key = null;
                    }
                    break;
                case ':':
                    if (top is { Open: '{' })
                    {
                        top.AfterColon = true;
                        top.Key = previous == 'a' ? lastWord : lastString;
                    }
                    break;
            }
            previous = c;
            i++;
        }

        // 光标前那半截词。
        int prefixStart = src.Length;
        while (prefixStart > 0 && ShellScript.IsIdentifierPart(src[prefixStart - 1]))
        {
            prefixStart--;
        }

        // 成员访问:db. / db.coll. / ).
        if (prefixStart > 0 && src[prefixStart - 1] == '.')
        {
            int chainEnd = prefixStart - 1;
            int chainStart = chainEnd;
            while (chainStart > 0 && (ShellScript.IsIdentifierPart(src[chainStart - 1]) || src[chainStart - 1] == '.'))
            {
                chainStart--;
            }
            string chain = src[chainStart..chainEnd];
            int before = chainStart - 1;
            while (before >= 0 && char.IsWhiteSpace(src[before]))
            {
                before--;
            }
            bool memberOfCall = chain.Length == 0 && before >= 0 && src[before] == ')';
            bool rooted = chain == "db" || chain.StartsWith("db.", StringComparison.Ordinal);
            if (rooted || memberOfCall)
            {
                var member = new CompletionContext
                {
                    ReplaceOffset = statementStart + prefixStart,
                    ReplaceLength = src.Length - prefixStart,
                    Prefix = src[prefixStart..],
                    Collection = collection,
                    Method = method,
                    Database = database
                };
                if (chain == "db")
                {
                    return member with { Slot = CompletionSlot.DbMember };
                }
                if (rooted)
                {
                    return member with { Slot = CompletionSlot.CollectionMember, Collection = chain[3..] };
                }
                // 括号前是 getCollection(…) / getSiblingDB(…) 时,点后面是集合方法 / 库成员;否则是游标方法。
                string head = src[..(before + 1)];
                if (GetCollectionTail().Match(head) is { Success: true } coll)
                {
                    return member with { Slot = CompletionSlot.CollectionMember, Collection = coll.Groups["c"].Value };
                }
                if (GetSiblingTail().Match(head) is { Success: true } sibling)
                {
                    return member with { Slot = CompletionSlot.DbMember, Database = sibling.Groups["d"].Value };
                }
                return method is null ? member with { Slot = CompletionSlot.None } : member with { Slot = CompletionSlot.CursorMember };
            }
            // 不是 db 开头的点:对象里的字段路径(customer.na),前缀连点一起替换。
            prefixStart = chainStart;
        }

        string prefix = src[prefixStart..];
        var context = new CompletionContext
        {
            ReplaceOffset = statementStart + prefixStart,
            ReplaceLength = src.Length - prefixStart,
            Prefix = prefix,
            Collection = collection,
            Method = method,
            Database = database
        };
        if (stack.Count == 0)
        {
            // 语句开头只有一个词时给 db / use / show。
            return src[..prefixStart].Trim().Length == 0 ? context with { Slot = CompletionSlot.Statement } : context with { Slot = CompletionSlot.None };
        }
        (ObjectRole role, string? stage, Frame? pipeline) = Resolve(stack);
        Frame innermost = stack[^1];
        CompletionSlot slot = innermost.Open == '{' && !innermost.AfterColon ? CompletionSlot.Key : CompletionSlot.Value;
        // 前缀前面紧挨着 "-"(sort 的 -1)之类时照样给值补全;但在数字中间不弹。
        if (prefix.Length > 0 && char.IsDigit(prefix[0]))
        {
            return context with { Slot = CompletionSlot.None };
        }
        return context with
        {
            Slot = slot,
            Role = role,
            StageName = stage,
            FieldKey = slot == CompletionSlot.Value ? innermost.Key : innermost.Owner,
            InPipeline = pipeline is not null,
            PrecedingStages = pipeline is null ? [] : [.. pipeline.Elements.Select(e => text[e.Start..e.End].Trim())]
        };
    }

    private static CompletionContext InString(string text, int caret, int contentStart, string content, char quote, List<Frame> stack,
        string? collection, string? method, string? database)
    {
        var context = new CompletionContext
        {
            ReplaceOffset = contentStart,
            ReplaceLength = caret - contentStart,
            Prefix = content,
            Collection = collection,
            Method = method,
            Database = database,
            Quoted = true,
            QuoteClosed = caret < text.Length && text[caret] == quote
        };
        Frame? top = stack.Count > 0 ? stack[^1] : null;
        if (top is { Open: '(', Arg: 0, Call: "getCollection" or "use" or "getSiblingDB" or "hint" or "dropIndex" or "distinct" or "renameCollection" })
        {
            return context with { Slot = CompletionSlot.StringArgument, StringCall = top.Call };
        }
        (ObjectRole role, string? stage, Frame? pipeline) = stack.Count > 0 ? Resolve(stack) : (ObjectRole.Unknown, null, null);
        if (content.StartsWith('$'))
        {
            return context with
            {
                Slot = CompletionSlot.FieldPathString,
                Role = role,
                StageName = stage,
                InPipeline = pipeline is not null,
                PrecedingStages = pipeline is null ? [] : [.. pipeline.Elements.Select(e => text[e.Start..e.End].Trim())]
            };
        }
        if (top is { Open: '{', AfterColon: false })
        {
            return context with
            {
                Slot = CompletionSlot.Key,
                Role = role,
                StageName = stage,
                FieldKey = top.Owner,
                InPipeline = pipeline is not null,
                PrecedingStages = pipeline is null ? [] : [.. pipeline.Elements.Select(e => text[e.Start..e.End].Trim())]
            };
        }
        return context with { Slot = CompletionSlot.None };
    }

    /// <summary>沿括号栈从外到内推出语义角色;同时找出所在的管道数组与阶段名。</summary>
    private static (ObjectRole Role, string? Stage, Frame? Pipeline) Resolve(List<Frame> stack)
    {
        ObjectRole role = ObjectRole.Unknown;
        string? stage = null;
        Frame? pipeline = null;
        Frame? parent = null;
        foreach (Frame frame in stack)
        {
            if (frame.Open == '(')
            {
                role = ForArgument(frame.Call, frame.Arg);
            }
            else
            {
                ObjectRole next = Transition(role, frame.Owner, frame.Open, parent);
                if (role == ObjectRole.Stage && frame.Owner is { } owner)
                {
                    stage = owner;
                }
                if (next == ObjectRole.Pipeline && frame.Open == '[')
                {
                    pipeline = frame;
                    stage = null;
                }
                role = next;
            }
            parent = frame;
        }
        return (role, stage, pipeline);
    }

    private static ObjectRole ForArgument(string? call, int arg) => (call, arg) switch
    {
        ("find" or "findOne" or "countDocuments" or "count" or "deleteOne" or "deleteMany" or "remove" or "findOneAndDelete"
            or "updateOne" or "updateMany" or "update" or "findOneAndUpdate" or "replaceOne" or "findOneAndReplace", 0) => ObjectRole.Filter,
        ("find" or "findOne", 1) => ObjectRole.Projection,
        ("updateOne" or "updateMany" or "update" or "findOneAndUpdate", 1) => ObjectRole.Update,
        ("replaceOne" or "findOneAndReplace", 1) => ObjectRole.Document,
        ("distinct", 1) => ObjectRole.Filter,
        ("aggregate", 0) => ObjectRole.Pipeline,
        ("insertOne", 0) => ObjectRole.Document,
        ("insertMany", 0) => ObjectRole.DocumentList,
        ("sort", 0) => ObjectRole.Sort,
        ("project" or "projection", 0) => ObjectRole.Projection,
        ("hint" or "min" or "max" or "createIndex", 0) => ObjectRole.IndexKeys,
        _ => ObjectRole.Options
    };

    private static ObjectRole Transition(ObjectRole role, string? owner, char open, Frame? parent)
    {
        bool directInCall = parent is { Open: '(' };
        if (directInCall)
        {
            // 实参本身:aggregate([ 是管道;find({ 是条件;updateOne(f, [ 是更新管道。
            return role == ObjectRole.Update && open == '[' ? ObjectRole.Pipeline
                : role == ObjectRole.Pipeline && open == '{' ? ObjectRole.Stage
                : role;
        }
        return role switch
        {
            ObjectRole.Pipeline when open == '{' => ObjectRole.Stage,
            ObjectRole.DocumentList when open == '{' => ObjectRole.Document,
            ObjectRole.FilterList when open == '{' => ObjectRole.Filter,
            ObjectRole.Stage => owner switch
            {
                "$match" => ObjectRole.Filter,
                "$sort" => ObjectRole.Sort,
                "$project" or "$addFields" or "$set" => ObjectRole.Projection,
                "$group" => ObjectRole.Group,
                "$facet" => ObjectRole.Facet,
                "$unset" => ObjectRole.Projection,
                "$replaceRoot" or "$replaceWith" or "$sortByCount" or "$redact" => ObjectRole.Expression,
                _ => ObjectRole.Options
            },
            ObjectRole.Facet when open == '[' => ObjectRole.Pipeline,
            ObjectRole.Group => owner == "_id" ? ObjectRole.Expression : ObjectRole.Accumulator,
            ObjectRole.Accumulator or ObjectRole.Expression => ObjectRole.Expression,
            ObjectRole.Projection => ObjectRole.Expression,
            ObjectRole.Filter => owner switch
            {
                "$and" or "$or" or "$nor" when open == '[' => ObjectRole.FilterList,
                "$expr" => ObjectRole.Expression,
                "$text" or "$jsonSchema" => ObjectRole.Options,
                "$elemMatch" => ObjectRole.Filter,
                { } field when !field.StartsWith('$') => open == '{' ? ObjectRole.Operator : ObjectRole.ValueList,
                _ => ObjectRole.Unknown
            },
            ObjectRole.Operator => owner switch
            {
                "$elemMatch" => ObjectRole.Filter,
                "$not" => ObjectRole.Operator,
                "$in" or "$nin" or "$all" => ObjectRole.ValueList,
                _ => ObjectRole.Unknown
            },
            ObjectRole.Update => owner is { } op && op.StartsWith('$') ? ObjectRole.UpdateFields : ObjectRole.Unknown,
            _ => ObjectRole.Unknown
        };
    }

    /// <summary>
    /// 光标所在语句的起点。只看光标前的文本:没写完的语句括号不配平,换行不会把它切开,
    /// 所以"最后一条语句"就是光标所在的那条 —— 除非它已经在光标之前结束了(那光标在一条新语句的开头)。
    /// </summary>
    private static int StatementStart(string text, int caret)
    {
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(text[..caret]);
        if (statements.Count == 0)
        {
            return caret;
        }
        ShellStatement last = statements[^1];
        if (last.End >= caret)
        {
            return last.Offset;
        }
        // 光标前只剩空白 / 注释:同一行上还算上一条语句的尾巴(db.orders.find() |),换过行或隔了分号就是新语句的开头。
        string gap = text[last.End..caret];
        return gap.Contains('\n') || gap.Contains(';') ? caret : last.Offset;
    }

    /// <summary>光标是不是落在注释里(注释里不弹补全)。</summary>
    private static bool InsideComment(string text, int caret)
    {
        int i = 0;
        char previous = '\0';
        while (i < caret)
        {
            char c = text[i];
            if (ShellScript.IsCommentStart(text, i))
            {
                int end = ShellScript.SkipComment(text, i);
                if (end > caret || (end == caret && text[i + 1] == '/'))
                {
                    return true;
                }
                i = end;
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                i = ShellScript.SkipString(text, i);
                previous = c;
                continue;
            }
            if (c == '/' && ShellScript.IsRegexStart(previous))
            {
                i = ShellScript.SkipRegex(text, i);
                previous = '/';
                continue;
            }
            if (!char.IsWhiteSpace(c))
            {
                previous = c;
            }
            i++;
        }
        return false;
    }

    /// <summary>语句头:<c>db.coll.method(</c> → (集合, 方法, 库)。</summary>
    internal static (string? Collection, string? Method, string? Database) Head(string statement)
    {
        Match match = HeadPattern().Match(statement);
        if (!match.Success)
        {
            return (null, null, null);
        }
        string? database = match.Groups["d"].Success ? match.Groups["d"].Value : null;
        string? collection = match.Groups["c"].Success && match.Groups["c"].Value.Length > 0
            ? match.Groups["c"].Value
            : match.Groups["n"].Success ? match.Groups["n"].Value : null;
        string? method = match.Groups["m"].Success ? match.Groups["m"].Value : null;
        if (collection is not null && method is null && ShellParser.DatabaseMethods.Contains(collection))
        {
            return (null, null, database);
        }
        return (collection, method, database);
    }

    /// <summary>
    /// 管道里当前阶段的"上游形状":最近一个重塑文档的阶段($group / $project / $count / $bucket…)决定输出字段;
    /// 没有重塑过返回 <see langword="null" />(上游就是集合本身,用抽样字段)。
    /// </summary>
    internal static PipelineShape? Upstream(IEnumerable<string> precedingStages)
    {
        PipelineShape? shape = null;
        foreach (string text in precedingStages)
        {
            if (!ShellJson.TryParseDocument(text, out BsonDocument stage, out _) || stage.ElementCount == 0)
            {
                continue;
            }
            BsonElement element = stage.GetElement(0);
            switch (element.Name)
            {
                case "$group" when element.Value is BsonDocument group:
                    shape = new PipelineShape([.. group.Names], "$group");
                    break;
                case "$project" when element.Value is BsonDocument project:
                {
                    bool inclusion = project.Elements.Any(static e => e.Name != "_id" && !(e.Value.IsNumeric && e.Value.ToDouble() == 0)
                                                                      && !(e.Value is BsonBoolean { Value: false }));
                    if (inclusion)
                    {
                        List<string> fields = [.. project.Elements.Where(static e => !(e.Value.IsNumeric && e.Value.ToDouble() == 0) && e.Value is not BsonBoolean { Value: false }).Select(static e => e.Name)];
                        if (!project.Contains("_id"))
                        {
                            fields.Insert(0, "_id");
                        }
                        shape = new PipelineShape(fields, "$project");
                    }
                    else if (shape is not null)
                    {
                        shape = shape with { Fields = [.. shape.Fields.Where(f => !project.Contains(f))] };
                    }
                    break;
                }
                case "$addFields" or "$set" when element.Value is BsonDocument added && shape is not null:
                    shape = new PipelineShape([.. shape.Fields, .. added.Names.Where(n => !shape.Fields.Contains(n))], shape.Source);
                    break;
                case "$count" when element.Value is BsonString name:
                    shape = new PipelineShape([name.Value], "$count");
                    break;
                case "$sortByCount":
                    shape = new PipelineShape(["_id", "count"], "$sortByCount");
                    break;
                case "$bucket" or "$bucketAuto" when element.Value is BsonDocument bucket:
                    shape = new PipelineShape(bucket.GetValue("output", null) is BsonDocument output ? ["_id", .. output.Names] : ["_id", "count"], element.Name);
                    break;
                case "$facet" when element.Value is BsonDocument facet:
                    shape = new PipelineShape([.. facet.Names], "$facet");
                    break;
                case "$lookup" when element.Value is BsonDocument lookup && shape is not null && lookup.GetValue("as", null) is BsonString alias:
                    shape = shape with { Fields = [.. shape.Fields, alias.Value] };
                    break;
                case "$replaceRoot" or "$replaceWith":
                    shape = new PipelineShape([], element.Name);
                    break;
            }
        }
        return shape;
    }

    [GeneratedRegex(@"^\s*db\s*\.\s*(?:getSiblingDB\(\s*[""'](?<d>[^""']*)[""']\s*\)\s*\.\s*)?(?:getCollection\(\s*[""'](?<c>[^""']*)[""']\s*\)|(?<n>[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*?))\s*\.\s*(?<m>[A-Za-z_$][\w$]*)\s*\(")]
    private static partial Regex HeadPattern();

    [GeneratedRegex(@"getCollection\(\s*[""'](?<c>[^""']*)[""']\s*\)\s*$")]
    private static partial Regex GetCollectionTail();

    [GeneratedRegex(@"getSiblingDB\(\s*[""'](?<d>[^""']*)[""']\s*\)\s*$")]
    private static partial Regex GetSiblingTail();
}
