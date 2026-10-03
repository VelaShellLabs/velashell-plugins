using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>抽样出的一个字段(设计稿 03 右侧「字段」页的一行)。</summary>
/// <param name="Path">完整路径(<c>customer.name</c>)。</param>
/// <param name="Name">本层名字(<c>name</c>)。</param>
/// <param name="Depth">嵌套深度(顶层 0)。</param>
/// <param name="Kinds">出现过的类型,按出现次数降序。</param>
/// <param name="Ratio">出现率(含这个字段的文档 / 抽样文档)。</param>
internal sealed record SampledField(string Path, string Name, int Depth, IReadOnlyList<BsonKind> Kinds, double Ratio)
{
    /// <summary>主类型。</summary>
    public BsonKind Kind => Kinds.Count > 0 ? Kinds[0] : BsonKind.Missing;

    /// <summary>类型文字(<c>String · Null</c>)。</summary>
    public string TypeText => string.Join(" · ", Kinds.Take(2).Select(static k => BsonKinds.Name(k)));

    /// <summary>出现率文字(<c>97%</c>)。</summary>
    public string RatioText => (Ratio * 100).ToString(Ratio is > 0.99 and < 1 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%";

    /// <summary>不是每份文档都有(出现率用警告色)。</summary>
    public bool IsPartial => Ratio < 0.9995;

    /// <summary>类型色令牌。</summary>
    public string ColorToken => BsonKinds.ColorToken(Kind);

    /// <summary>左缩进(嵌套字段多缩 12)。</summary>
    public double Indent => 12 + Depth * 12;
}

/// <summary>
/// 对一个集合抽样得到的字段表(补全的「字段 · 抽样」、右侧字段树都用它)。
/// <para>
/// 只看形状不看值:每份文档里每条路径计一次(数组里的子文档展开成 <c>items.sku</c>),
/// 出现率 = 含这条路径的文档数 / 抽样数。顺序按首次出现,子字段紧跟父字段,<c>_id</c> 永远在最前 ——
/// 与 Compass 的 Schema 页、设计稿的字段页一致。
/// </para>
/// </summary>
internal sealed class SampledSchema
{
    /// <summary>最深看几层(更深的嵌套在补全里没有意义,还拖慢抽样)。</summary>
    public const int MaxDepth = 3;

    /// <summary>库。</summary>
    public required string Database { get; init; }

    /// <summary>集合。</summary>
    public required string Collection { get; init; }

    /// <summary>抽样文档数。</summary>
    public int Sampled { get; init; }

    /// <summary>字段(父字段在前,子字段紧随)。</summary>
    public IReadOnlyList<SampledField> Fields { get; init; } = [];

    /// <summary>按路径查一个字段。</summary>
    public SampledField? Find(string path) => Fields.FirstOrDefault(f => f.Path == path);

    private sealed class Node(string path, string name, int depth)
    {
        public string Path { get; } = path;
        public string Name { get; } = name;
        public int Depth { get; } = depth;
        public int Documents;
        public readonly Dictionary<BsonKind, int> Kinds = [];
        public readonly List<Node> Children = [];
    }

    /// <summary>从一批样本建字段表。</summary>
    public static SampledSchema Build(string database, string collection, IReadOnlyList<BsonDocument> documents)
    {
        var roots = new List<Node>();
        var index = new Dictionary<string, Node>(StringComparer.Ordinal);
        foreach (BsonDocument document in documents)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Walk(document, null, 0, roots, index, seen);
        }
        var fields = new List<SampledField>();
        int total = Math.Max(1, documents.Count);
        // _id 永远第一。
        IEnumerable<Node> ordered = roots.Where(static n => n.Name == "_id").Concat(roots.Where(static n => n.Name != "_id"));
        foreach (Node node in ordered)
        {
            Flatten(node, total, fields);
        }
        return new SampledSchema { Database = database, Collection = collection, Sampled = documents.Count, Fields = fields };
    }

    private static void Walk(BsonDocument document, Node? parent, int depth, List<Node> roots, Dictionary<string, Node> index, HashSet<string> seen)
    {
        foreach (BsonElement element in document)
        {
            string path = parent is null ? element.Name : $"{parent.Path}.{element.Name}";
            if (!index.TryGetValue(path, out Node? node))
            {
                node = new Node(path, element.Name, depth);
                index[path] = node;
                (parent?.Children ?? roots).Add(node);
            }
            BsonKind kind = BsonKinds.Of(element.Value);
            if (seen.Add(path))
            {
                node.Documents++;
            }
            node.Kinds[kind] = node.Kinds.GetValueOrDefault(kind) + 1;
            if (depth + 1 >= MaxDepth)
            {
                continue;
            }
            switch (element.Value)
            {
                case BsonDocument child:
                    Walk(child, node, depth + 1, roots, index, seen);
                    break;
                case BsonArray array:
                    // 数组里的子文档:按 items.sku 展开(与 MongoDB 的点路径语义一致)。
                    foreach (BsonDocument item in array.OfType<BsonDocument>().Take(20))
                    {
                        Walk(item, node, depth + 1, roots, index, seen);
                    }
                    break;
            }
        }
    }

    private static void Flatten(Node node, int total, List<SampledField> output)
    {
        output.Add(new SampledField(
            node.Path,
            node.Name,
            node.Depth,
            [.. node.Kinds.OrderByDescending(static p => p.Value).ThenBy(static p => p.Key == BsonKind.Null ? 1 : 0).Select(static p => p.Key)],
            Math.Min(1, (double)node.Documents / total)));
        foreach (Node child in node.Children)
        {
            Flatten(child, total, output);
        }
    }
}
