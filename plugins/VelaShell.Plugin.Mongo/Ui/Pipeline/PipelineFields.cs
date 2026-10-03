using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>抽样里出现过的一个字段路径。</summary>
/// <param name="Path">点号路径(<c>items.sku</c>)。</param>
/// <param name="Kind">最常见的类型。</param>
/// <param name="Ratio">出现率(含它的文档 / 抽样文档数)。</param>
internal sealed record PipelineFieldSample(string Path, BsonKind Kind, double Ratio);

/// <summary>
/// 上游字段抽样与阶段编辑器的补全。
/// <para>
/// 补全里的"上游字段"不是集合的 Schema,而是**前一个阶段输出预览**里实际出现的字段:
/// <c>$group</c> 之后只剩 <c>_id / qty / revenue</c>,这时再提示 <c>orderNo</c> 只会误导。
/// 预览本来就在跑,顺手把它的输出抽一遍字段,不多打一次服务器。
/// </para>
/// </summary>
internal static class PipelineFields
{
    /// <summary>嵌套多深就不再展开(<c>a.b.c</c>)。</summary>
    private const int MaxDepth = 3;

    /// <summary>从一批文档里抽字段路径(按首次出现的顺序;数组里的子文档按同一路径展开)。</summary>
    public static IReadOnlyList<PipelineFieldSample> Sample(IReadOnlyList<BsonDocument> documents)
    {
        if (documents.Count == 0)
        {
            return [];
        }
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var kinds = new Dictionary<string, Dictionary<BsonKind, int>>(StringComparer.Ordinal);
        foreach (BsonDocument doc in documents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Visit(doc, "", 0, seen, kinds, order);
            foreach (string path in seen)
            {
                counts[path] = counts.GetValueOrDefault(path) + 1;
            }
        }
        return
        [
            .. order.Select(path => new PipelineFieldSample(
                path,
                kinds[path].OrderByDescending(static k => k.Value).First().Key,
                (double)counts.GetValueOrDefault(path) / documents.Count))
        ];
    }

    private static void Visit(
        BsonDocument doc,
        string prefix,
        int depth,
        HashSet<string> seen,
        Dictionary<string, Dictionary<BsonKind, int>> kinds,
        List<string> order)
    {
        foreach (BsonElement element in doc)
        {
            string path = prefix.Length == 0 ? element.Name : prefix + "." + element.Name;
            Record(path, element.Value, seen, kinds, order);
            if (depth + 1 >= MaxDepth)
            {
                continue;
            }
            switch (element.Value)
            {
                case BsonDocument child:
                    Visit(child, path, depth + 1, seen, kinds, order);
                    break;
                case BsonArray array:
                    foreach (BsonValue item in array.Take(20))
                    {
                        if (item is BsonDocument itemDoc)
                        {
                            Visit(itemDoc, path, depth + 1, seen, kinds, order);
                        }
                    }
                    break;
            }
        }
    }

    private static void Record(string path, BsonValue value, HashSet<string> seen, Dictionary<string, Dictionary<BsonKind, int>> kinds, List<string> order)
    {
        if (!kinds.TryGetValue(path, out Dictionary<BsonKind, int>? byKind))
        {
            byKind = [];
            kinds[path] = byKind;
            order.Add(path);
        }
        BsonKind kind = BsonKinds.Of(value);
        byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
        _ = seen.Add(path);
    }

    /// <summary>
    /// 阶段编辑器的补全:
    /// 字符串里敲 <c>"$</c> → 上游字段路径;敲 <c>$</c> → 运算符(<c>$match</c> 里先给查询运算符,其余阶段先给表达式);
    /// 键的位置敲字母 → 上游字段名。
    /// </summary>
    /// <param name="request">补全请求。</param>
    /// <param name="stageOperator">所在阶段的运算符;文本模式(整条管道)为 <see langword="null" />。</param>
    /// <param name="fields">上游字段。</param>
    /// <param name="sampleSize">抽样的输入文档数(标题里说清"这是抽样")。</param>
    /// <param name="loc">文案。</param>
    public static CompletionSet? Complete(CompletionRequest request, string? stageOperator, IReadOnlyList<PipelineFieldSample> fields, int sampleSize, Loc loc)
    {
        string text = request.Text;
        int caret = Math.Clamp(request.CaretOffset, 0, text.Length);
        int start = caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '$' or '.'))
        {
            start--;
        }
        string prefix = text[start..caret];
        bool chinese = loc.IsChinese;
        bool inString = start > 0 && text[start - 1] is '"' or '\'' && InsideString(text, start - 1);
        var items = new List<CompletionItem>();
        string? header = null;

        if (inString)
        {
            if (!prefix.StartsWith('$') && !(request.Explicit && prefix.Length == 0))
            {
                return null;
            }
            items.AddRange(fields.Select(f => FieldItem(f, "$" + f.Path, "$" + f.Path, loc)));
            header = loc.Format("Pipe_FieldsHeader", sampleSize);
        }
        else if (prefix.StartsWith('$'))
        {
            IEnumerable<VocabularyEntry> entries = stageOperator switch
            {
                null => MongoVocabulary.Stages.Concat(MongoVocabulary.QueryOperators).Concat(MongoVocabulary.Expressions),
                "$match" => MongoVocabulary.QueryOperators.Concat(MongoVocabulary.Expressions),
                _ => MongoVocabulary.Expressions.Concat(MongoVocabulary.QueryOperators)
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (VocabularyEntry entry in entries)
            {
                if (seen.Add(entry.Name))
                {
                    items.Add(MongoVocabulary.ToCompletion(entry, chinese));
                }
            }
        }
        else if (IsKeyPosition(text, start) || request.Explicit)
        {
            items.AddRange(fields.Select(f => FieldItem(f, f.Path, BsonText.FieldName(f.Path) + ": |", loc)));
            header = fields.Count > 0 ? loc.Format("Pipe_FieldsHeader", sampleSize) : null;
        }
        if (items.Count == 0)
        {
            return null;
        }
        return new CompletionSet
        {
            Items = items,
            ReplaceOffset = start,
            ReplaceLength = caret - start,
            Header = header,
            Footer = loc["Pipe_CompletionFooter"]
        };
    }

    private static CompletionItem FieldItem(PipelineFieldSample field, string label, string insert, Loc loc) => new()
    {
        Label = label,
        InsertText = insert,
        IconKey = "Mongo.variable",
        IconToken = "VelaWarning",
        Category = BsonKinds.Name(field.Kind),
        Badge = loc["Pipe_FieldBadge"],
        Description = loc.Format("Pipe_FieldDetail", field.Path, BsonKinds.Name(field.Kind), $"{field.Ratio * 100:0}%"),
        Ratio = field.Ratio
    };

    /// <summary>位置 <paramref name="quote" /> 的引号是不是一个字符串的开引号(数一数前面未闭合的引号)。</summary>
    private static bool InsideString(string text, int quote)
    {
        int i = 0;
        while (i < quote)
        {
            char c = text[i];
            if (c is '"' or '\'')
            {
                int end = PipelineText.SkipString(text, i);
                if (end > quote)
                {
                    return false;
                }
                i = end;
                continue;
            }
            i++;
        }
        return true;
    }

    /// <summary>前一个有效字符是 <c>{</c> 或 <c>,</c>(或者在行首)—— 这里该写键名。</summary>
    private static bool IsKeyPosition(string text, int start)
    {
        for (int i = start - 1; i >= 0; i--)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            return c is '{' or ',';
        }
        return false;
    }
}
