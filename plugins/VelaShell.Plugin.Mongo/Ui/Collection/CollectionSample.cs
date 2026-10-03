using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>抽样里看到的一个字段。</summary>
/// <param name="Path">路径(数组不展开下标:<c>items.sku</c>)。</param>
/// <param name="Kind">主导类型。</param>
/// <param name="Count">出现在多少份抽样文档里。</param>
internal sealed record CollectionSampleField(string Path, BsonKind Kind, int Count);

/// <summary>
/// 补全用的抽样:<c>$sample</c> 一批文档,在内存里算字段表与"某字段的取值分布"。
/// <para>
/// 值分布在客户端从同一批样本里数,而不是每问一个字段就跑一次 <c>$group</c>:
/// 补全是边打字边问的,一次往返的延迟就足以让弹层慢半拍;样本已经在手里,数一遍是微秒级。
/// </para>
/// </summary>
internal sealed class CollectionSample
{
    private readonly IReadOnlyList<BsonDocument> _documents;
    private readonly Dictionary<string, List<(string Label, string Insert, BsonKind Kind, double Ratio)>> _valueCache = [with(StringComparer.Ordinal)];

    private CollectionSample(IReadOnlyList<BsonDocument> documents)
    {
        _documents = documents;
        var counts = new Dictionary<string, Dictionary<BsonKind, int>>(StringComparer.Ordinal);
        var presence = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (BsonDocument doc in documents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string path, BsonValue value) in BsonPath.Walk(doc, maxDepth: 4))
            {
                if (!counts.TryGetValue(path, out Dictionary<BsonKind, int>? kinds))
                {
                    kinds = [];
                    counts[path] = kinds;
                    order.Add(path);
                }
                BsonKind kind = BsonKinds.Of(value);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                if (seen.Add(path))
                {
                    presence[path] = presence.GetValueOrDefault(path) + 1;
                }
            }
        }
        Fields = [.. order.Select(p => new CollectionSampleField(p, counts[p].MaxBy(static k => k.Value).Key, presence.GetValueOrDefault(p)))];
    }

    /// <summary>空样本(还没抽到 / 抽样失败)。</summary>
    public static CollectionSample Empty { get; } = new([]);

    /// <summary>样本大小。</summary>
    public int Size => _documents.Count;

    /// <summary>字段表(按首次出现的顺序)。</summary>
    public IReadOnlyList<CollectionSampleField> Fields { get; }

    /// <summary>抽一批。视图与时序集合同样支持 <c>$sample</c>。</summary>
    public static async Task<CollectionSample> LoadAsync(MongoConnection connection, string database, string collection, int size, CancellationToken cancellationToken)
    {
        BsonDocument[] pipeline = new[] { new BsonDocument("$sample", new BsonDocument("size", Math.Clamp(size, 50, 5000))) };
        using IAsyncCursor<BsonDocument> cursor = await connection.Collection(database, collection)
            .AggregateAsync<BsonDocument>(pipeline, new AggregateOptions { MaxTime = TimeSpan.FromSeconds(10) }, cancellationToken)
            .ConfigureAwait(false);
        List<BsonDocument> documents = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(documents);
    }

    /// <summary>某个字段的主导类型;样本里没有为 <see cref="BsonKind.Missing" />。</summary>
    public BsonKind KindOf(string path) => Fields.FirstOrDefault(f => f.Path == NormalizePath(path))?.Kind ?? BsonKind.Missing;

    /// <summary>某个对象下的直接子字段名(JSON 编辑器里键位置的补全)。</summary>
    public IEnumerable<CollectionSampleField> ChildrenOf(string parent)
    {
        string normalized = NormalizePath(parent);
        string prefix = normalized.Length == 0 ? "" : normalized + ".";
        return Fields.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && f.Path.IndexOf('.', prefix.Length) < 0);
    }

    /// <summary>
    /// 某个字段的取值分布(前 8 个):字面量、插入文本、类型、占比(出现该值的文档 / 样本大小)。
    /// 数组里的值摊开来数(<c>items.sku</c> 数的是每一项的 sku)。
    /// </summary>
    public IReadOnlyList<(string Label, string Insert, BsonKind Kind, double Ratio)> ValuesOf(string path)
    {
        string normalized = NormalizePath(path);
        if (_valueCache.TryGetValue(normalized, out List<(string, string, BsonKind, double)>? cached))
        {
            return cached;
        }
        var counts = new Dictionary<string, (BsonValue Value, int Count)>(StringComparer.Ordinal);
        string[] segments = normalized.Split('.');
        foreach (BsonDocument doc in _documents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (BsonValue value in Collect(doc, segments, 0))
            {
                if (value is BsonDocument or BsonArray)
                {
                    continue;
                }
                string literal = BsonText.Literal(value);
                if (literal.Length > 60 || !seen.Add(literal))
                {
                    continue;
                }
                counts[literal] = counts.TryGetValue(literal, out (BsonValue Value, int Count) entry) ? (entry.Value, entry.Count + 1) : (value, 1);
            }
        }
        List<(string, string, BsonKind, double)> list =
        [
            .. counts.OrderByDescending(static p => p.Value.Count).Take(8)
                .Select(p => (p.Key, p.Key, BsonKinds.Of(p.Value.Value), _documents.Count == 0 ? 0 : (double)p.Value.Count / _documents.Count))
        ];
        // 每个值都只出现一次(单号、ObjectId)的字段没有"分布"可言,给出来只会是一列噪音。
        if (list.Count > 1 && counts.Values.Max(static v => v.Count) <= 1)
        {
            list.Clear();
        }
        _valueCache[normalized] = list;
        return list;
    }

    /// <summary>某列里取值不多(≤ 12 种)的字符串列的全部取值:网格内联编辑的下拉候选(<c>status</c> 这种枚举列)。</summary>
    public IReadOnlyList<string> EnumValuesOf(string path)
    {
        IReadOnlyList<(string Label, string Insert, BsonKind Kind, double Ratio)> values = ValuesOf(path);
        if (values.Count == 0 || values.Count >= 8 || values.Any(static v => v.Kind != BsonKind.String))
        {
            return [];
        }
        double covered = values.Sum(static v => v.Ratio);
        return covered < 0.6 ? [] : [.. values.Select(static v => ShellJson.ParseValue(v.Label).AsString)];
    }

    /// <summary>去掉数组下标(<c>items.0.sku</c> → <c>items.sku</c>),与抽样字段表的写法一致。</summary>
    internal static string NormalizePath(string path) =>
        string.Join('.', path.Split('.').Where(static s => s.Length > 0 && !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _)));

    private static IEnumerable<BsonValue> Collect(BsonValue current, string[] segments, int index)
    {
        if (current is BsonArray array)
        {
            foreach (BsonValue item in array)
            {
                foreach (BsonValue nested in Collect(item, segments, index))
                {
                    yield return nested;
                }
            }
            yield break;
        }
        if (index == segments.Length)
        {
            yield return current;
            yield break;
        }
        if (current is BsonDocument doc && doc.TryGetValue(segments[index], out BsonValue next))
        {
            foreach (BsonValue nested in Collect(next, segments, index + 1))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 补全:键位置给字段名(筛选栏里给完整点路径,<c>$</c> 开头给查询运算符),值位置给取值分布。
    /// </summary>
    /// <param name="loc">文案表。</param>
    /// <param name="text">全文。</param>
    /// <param name="caret">光标。</param>
    /// <param name="filterMode">筛选栏(键给完整点路径与查询运算符)还是文档编辑(键只给当前对象的子字段)。</param>
    public CompletionSet? Complete(Loc loc, string text, int caret, bool filterMode)
    {
        CaretPathScanner.Result at = CaretPathScanner.Scan(text, caret);
        string prefix = text[at.TokenStart..Math.Max(at.TokenStart, caret)];
        if (at.InValue)
        {
            string fieldPath = at.Path;
            if (filterMode)
            {
                // 筛选里 { total: { $gte: | } }:值属于外层那个字段,而不是 $gte。
                string[] parts = fieldPath.Split('.');
                fieldPath = string.Join('.', parts.Where(static p => !p.StartsWith('$')));
            }
            IReadOnlyList<(string Label, string Insert, BsonKind Kind, double Ratio)> values = ValuesOf(fieldPath);
            if (values.Count == 0)
            {
                return null;
            }
            return new CompletionSet
            {
                Items = [.. values.Select(v => new CompletionItem
                {
                    Label = v.Label,
                    InsertText = v.Insert,
                    IconKey = "Mongo.type",
                    IconToken = BsonKinds.ColorToken(v.Kind),
                    Ratio = v.Ratio,
                    Category = loc.Format("Cw_SuggestRatio", (v.Ratio * 100).ToString("0", CultureInfo.InvariantCulture))
                })],
                ReplaceOffset = at.TokenStart,
                ReplaceLength = Math.Max(0, caret - at.TokenStart),
                Header = loc.Format("Cw_SuggestHeader", NormalizePath(fieldPath), BsonText.Grouped(Size)),
                Footer = loc["Cw_SuggestFooter"]
            };
        }

        var items = new List<CompletionItem>();
        if (filterMode && prefix.StartsWith('$'))
        {
            items.AddRange(MongoVocabulary.QueryOperators.Select(e => MongoVocabulary.ToCompletion(e, loc.IsChinese)));
        }
        else
        {
            IEnumerable<CollectionSampleField> fields = filterMode && at.ParentPath.Length == 0 ? Fields : ChildrenOf(at.ParentPath);
            foreach (CollectionSampleField field in fields.Take(80))
            {
                string name = filterMode ? field.Path : field.Path[(field.Path.LastIndexOf('.') + 1)..];
                bool needsQuotes = !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_$][A-Za-z0-9_$]*$");
                items.Add(new CompletionItem
                {
                    Label = name,
                    InsertText = (needsQuotes ? BsonText.Quote(name) : name) + ": |",
                    IconKey = "Mongo.variable",
                    IconToken = "VelaWarning",
                    Category = loc.Format("Cw_SuggestField", BsonKinds.Name(field.Kind)),
                    Description = loc.Format("Cw_SuggestFieldDetail", BsonText.Grouped(field.Count), BsonText.Grouped(Size))
                });
            }
            if (filterMode && prefix.Length == 0)
            {
                items.AddRange(MongoVocabulary.QueryOperators.Take(6).Select(e => MongoVocabulary.ToCompletion(e, loc.IsChinese)));
            }
        }
        if (items.Count == 0)
        {
            return null;
        }
        return new CompletionSet
        {
            Items = items,
            ReplaceOffset = at.TokenStart,
            ReplaceLength = Math.Max(0, caret - at.TokenStart)
        };
    }
}
