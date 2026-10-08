using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 网格钻入的一层(Navicat 的「双击对象 / 数组单元格进入」):一份文档里某个对象或数组字段,摊成一张子表。
/// <list type="bullet">
/// <item>数组:一个元素一行,行号是下标;元素是文档时列是它们字段的并集,有标量元素时多一列「值」。</item>
/// <item>对象:就一行,列是它的字段。</item>
/// </list>
/// 子表的行与所属文档共用暂存身份(见 <see cref="CollectionRow" /> 的子表构造),单元格路径从文档根算起。
/// </summary>
internal sealed class GridDrill
{
    private GridDrill(CollectionRow root, string path, bool isArray, int count)
    {
        Root = root;
        Path = path;
        IsArray = isArray;
        Count = count;
    }

    /// <summary>所属文档的那一行(顶层行)。</summary>
    public CollectionRow Root { get; }

    /// <summary>容器从文档根算起的路径(<c>items</c>、<c>items.2.attrs</c>)。</summary>
    public string Path { get; }

    /// <summary>是数组(否则是对象)。</summary>
    public bool IsArray { get; }

    /// <summary>元素数 / 字段数。</summary>
    public int Count { get; }

    /// <summary>这一层的列。</summary>
    public IReadOnlyList<CollectionColumn> Columns { get; private set; } = [];

    /// <summary>
    /// 在 <paramref name="root" /> 的当前版本(原像 + 暂存修改)里取 <paramref name="path" />,是对象或数组就建一层;
    /// 不是容器(字段被删了、改成了标量)返回 <see langword="null" />。
    /// </summary>
    /// <param name="owner">集合工作台。</param>
    /// <param name="root">文档那一行。</param>
    /// <param name="path">容器路径。</param>
    /// <param name="previous">同一路径上一次的列(沿用用户拖过的宽度)。</param>
    /// <param name="rows">子表的行。</param>
    public static GridDrill? Create(CollectionTabViewModel owner, CollectionRow root, string path,
        IReadOnlyList<CollectionColumn>? previous, out List<CollectionRow> rows)
    {
        rows = [];
        switch (BsonPath.Get(root.Document, path))
        {
            case BsonArray array:
                {
                    var drill = new GridDrill(root, path, isArray: true, array.Count)
                    {
                        Columns = ArrayColumns(owner.Loc, array, previous)
                    };
                    for (int i = 0; i < array.Count; i++)
                    {
                        rows.Add(new CollectionRow(owner, root, drill, i));
                    }
                    return drill;
                }
            case BsonDocument document:
                {
                    var drill = new GridDrill(root, path, isArray: false, document.ElementCount)
                    {
                        Columns = DocumentColumns([document], previous)
                    };
                    rows.Add(new CollectionRow(owner, root, drill, -1));
                    return drill;
                }
            default:
                return null;
        }
    }

    /// <summary>
    /// 筛选用的路径:去掉数组下标(<c>items.2.sku</c> → <c>items.sku</c>)。
    /// 带下标的筛选只匹配"第 3 个元素恰好是它"的文档,几乎从来不是想要的。
    /// </summary>
    public static string FilterPath(string path) =>
        string.Join('.', path.Split('.').Where(static s => s.Length == 0 || !s.All(char.IsAsciiDigit)));

    /// <summary>面包屑一段的文字:数组下标写成 <c>[2]</c>。</summary>
    public static string SegmentLabel(string segment) =>
        segment.Length > 0 && segment.All(char.IsAsciiDigit) ? $"[{segment}]" : segment;

    private static List<CollectionColumn> ArrayColumns(Loc loc, BsonArray array, IReadOnlyList<CollectionColumn>? previous)
    {
        var scalarKinds = new Dictionary<BsonKind, int>();
        int scalarLength = 0;
        var documents = new List<BsonDocument>();
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is BsonDocument document)
            {
                documents.Add(document);
                continue;
            }
            BsonKind kind = BsonKinds.Of(array[i]);
            scalarKinds[kind] = scalarKinds.GetValueOrDefault(kind) + 1;
            if (i < 200)
            {
                scalarLength = Math.Max(scalarLength, Math.Min(40, BsonText.Cell(array[i]).Length));
            }
        }
        var columns = new List<CollectionColumn>();
        // 有标量元素(或者是空数组 —— 给一个列头,别让子表只剩一片空白)才有「值」这一列。
        if (scalarKinds.Count > 0 || array.Count == 0)
        {
            string name = loc["Cw_DrillValue"];
            BsonKind kind = CollectionTabViewModel.Dominant(scalarKinds);
            double width = Width(previous, name, isElementValue: true)
                           ?? CollectionTabViewModel.EstimateWidth(name, kind, scalarLength);
            columns.Add(new CollectionColumn(name, kind, width, isElementValue: true));
        }
        columns.AddRange(DocumentColumns(documents, previous));
        return columns;
    }

    private static List<CollectionColumn> DocumentColumns(IReadOnlyList<BsonDocument> documents, IReadOnlyList<CollectionColumn>? previous)
    {
        var order = new List<string>();
        var kinds = new Dictionary<string, Dictionary<BsonKind, int>>(StringComparer.Ordinal);
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < documents.Count; i++)
        {
            foreach (BsonElement element in documents[i])
            {
                if (!kinds.TryGetValue(element.Name, out Dictionary<BsonKind, int>? counts))
                {
                    counts = [];
                    kinds[element.Name] = counts;
                    order.Add(element.Name);
                }
                BsonKind kind = BsonKinds.Of(element.Value);
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                if (i < 200 && lengths.GetValueOrDefault(element.Name) < 40)
                {
                    lengths[element.Name] = Math.Max(lengths.GetValueOrDefault(element.Name), BsonText.Cell(element.Value).Length);
                }
            }
        }
        if (order.Remove("_id"))
        {
            order.Insert(0, "_id");
        }
        return
        [
            .. order.Select(name =>
            {
                BsonKind kind = CollectionTabViewModel.Dominant(kinds[name]);
                double width = Width(previous, name, isElementValue: false)
                               ?? CollectionTabViewModel.EstimateWidth(name, kind, lengths.GetValueOrDefault(name));
                return new CollectionColumn(name, kind, width);
            })
        ];
    }

    private static double? Width(IReadOnlyList<CollectionColumn>? previous, string name, bool isElementValue) =>
        previous?.FirstOrDefault(c => c.IsElementValue == isElementValue && c.Name == name)?.Width;

    /// <summary>右侧那行小字:<c>数组 · 4 项</c> / <c>对象 · 3 个字段</c>。</summary>
    public string Summary(Loc loc) =>
        loc.Format(IsArray ? "Cw_DrillArray" : "Cw_DrillObject", Count.ToString("N0", CultureInfo.InvariantCulture));
}

/// <summary>面包屑的一段。</summary>
/// <param name="Label">文字(<c>文档 #3</c>、<c>items</c>、<c>[2]</c>)。</param>
/// <param name="Path">点它回到的容器路径;文档那一段为空串(= 退出钻入)。</param>
/// <param name="IsLast">最后一段(当前所在,不可点)。</param>
internal sealed record DrillCrumb(string Label, string Path, bool IsLast)
{
    /// <summary>最后一段之前都画一个「›」。</summary>
    public bool HasSeparator => !IsLast;
}
