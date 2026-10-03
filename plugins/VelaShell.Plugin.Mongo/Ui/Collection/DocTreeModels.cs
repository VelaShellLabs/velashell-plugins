using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 树视图(设计稿 13)的一行:文档行(<c>(2) 66f5c2a1…</c>)或字段行(键 / 值 / 类型)。
/// 树被摊平成一张列表交给虚拟化的 ListBox —— 展开一份 12 个字段的文档就是插入 12 行,而不是嵌套控件树。
/// </summary>
internal sealed class DocTreeRow : ObservableObject
{

    /// <summary>构造。</summary>
    /// <param name="row">所属网格行(同一份文档)。</param>
    /// <param name="path">字段路径;文档行为空串。</param>
    /// <param name="key">键名显示(数组元素是 <c>[0]</c>)。</param>
    /// <param name="value">值;文档行是整份文档。</param>
    /// <param name="depth">缩进层级(文档行 0)。</param>
    /// <param name="change">与暂存修改的关系。</param>
    /// <param name="original">被改字段的原值(<c>原值 2</c>);没改为 <see langword="null" />。</param>
    /// <param name="loc">文案表。</param>
    public DocTreeRow(CollectionRow row, string path, string key, BsonValue? value, int depth, ChangeRelation change, BsonValue? original, Loc loc)
    {
        Row = row;
        Path = path;
        Key = key;
        Value = value;
        Depth = depth;
        Change = change;
        Kind = path.Length == 0 ? BsonKind.Object : BsonKinds.Of(value);
        ValueText = path.Length == 0 ? DocumentSummary(row, loc) : ValueOf(value, loc);
        OriginalText = change == ChangeRelation.Exact && original is not null
            ? loc.Format("Cw_TreeOriginal", BsonText.Inline(original, loc, shortenIds: true))
            : change == ChangeRelation.Exact ? loc["Cw_TreeAdded"] : "";
    }

    /// <summary>所属网格行。</summary>
    public CollectionRow Row { get; }

    /// <summary>字段路径;文档行为空串。</summary>
    public string Path { get; }

    /// <summary>是不是文档行。</summary>
    public bool IsDocument => Path.Length == 0;

    /// <summary>键名显示。</summary>
    public string Key { get; }

    /// <summary>文档行的序号(<c>(2)</c>)。</summary>
    public string NumberText => IsDocument ? $"({Row.NumberText})" : "";

    /// <summary>值。</summary>
    public BsonValue? Value { get; }

    /// <summary>类型。</summary>
    public BsonKind Kind { get; }

    /// <summary>缩进层级。</summary>
    public int Depth { get; }

    /// <summary>键列左内边距(设计稿:文档行 10,每层 +18)。</summary>
    public double Indent => 10 + Depth * 18;

    /// <summary>与暂存修改的关系。</summary>
    public ChangeRelation Change { get; }

    /// <summary>这一格本身被改了(橙底 + 左橙条)。</summary>
    public bool IsModified => Change is ChangeRelation.Exact or ChangeRelation.InsideChange;

    /// <summary>值列文字。</summary>
    public string ValueText { get; }

    /// <summary>值列后面的小字(<c>原值 2</c> / <c>新增</c>)。</summary>
    public string OriginalText { get; }

    /// <summary>有没有那行小字。</summary>
    public bool HasOriginal => OriginalText.Length > 0;

    /// <summary>值列颜色:容器 / 文档行退成三级灰,标量按类型。</summary>
    public string ValueToken => IsModified ? "VelaWarning"
        : IsDocument || Kind is BsonKind.Object or BsonKind.Array ? "VelaTextTertiary"
        : BsonKinds.ColorToken(Kind);

    /// <summary>类型名(文档行写 <c>Document</c>)。</summary>
    public string TypeName => IsDocument ? "Document" : BsonKinds.Name(Kind);

    /// <summary>类型色块令牌。</summary>
    public string TypeToken => IsDocument ? "VelaWarning" : BsonKinds.ColorToken(Kind);

    /// <summary>能展开。</summary>
    public bool IsExpandable => IsDocument || Kind is BsonKind.Object or BsonKind.Array && ChildCount > 0;

    /// <summary>子项个数。</summary>
    public int ChildCount => Value switch
    {
        BsonDocument doc => doc.ElementCount,
        BsonArray array => array.Count,
        _ => 0
    };

    /// <summary>展开着。</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(Chevron));
            }
        }
    }

    /// <summary>展开箭头。</summary>
    public string Chevron => IsExpanded ? "Mongo.chevron-down" : "Mongo.chevron-right";

    /// <summary>值列的内联编辑器。</summary>
    public InlineValueEditor? Editor
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(IsEditing));
            }
        }
    }

    /// <summary>编辑中。</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>父路径(数组元素的父是数组)。</summary>
    public string ParentPath => Path.LastIndexOf('.') is var dot and > 0 ? Path[..dot] : "";

    /// <summary>是不是数组里的一个元素。</summary>
    public bool IsArrayElement { get; init; }

    /// <summary>
    /// 「按此值筛选」生成的条件:数组元素下的字段按 MongoDB 的语义写成去掉下标的路径
    /// (<c>items.0.sku</c> → <c>items.sku</c>,匹配"任意一项的 sku 等于它")—— 这才是用户想要的那种筛选。
    /// </summary>
    public string FilterPath => string.Join('.', Path.Split('.').Where(static s => !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _)));

    /// <summary>值的单行文字:字符串带引号、Decimal 写构造器(树里要看得出类型)。</summary>
    internal static string ValueOf(BsonValue? value, Loc loc) => BsonKinds.Of(value) switch
    {
        BsonKind.Object => loc.Format("Bson_FieldsSummary", value!.AsBsonDocument.ElementCount),
        BsonKind.Array => loc.Format("Bson_ItemsSummary", value!.AsBsonArray.Count),
        BsonKind.Decimal128 => $"NumberDecimal(\"{value!.AsDecimal128}\")",
        BsonKind.Int64 => $"NumberLong({value!.AsInt64.ToString(CultureInfo.InvariantCulture)})",
        BsonKind.Date => BsonText.FormatDate(value!),
        _ => BsonText.Inline(value, loc)
    };

    /// <summary>文档行的摘要:<c>{ 12 个字段 } · SO2609-10403 · 张伟</c>。</summary>
    private static string DocumentSummary(CollectionRow row, Loc loc)
    {
        BsonDocument doc = row.Document;
        string head = loc.Format("Bson_FieldsSummary", doc.ElementCount);
        string summary = DeepSummary(doc);
        return summary.Length > 0 ? $"{head} · {summary}" : head;
    }

    /// <summary>头两个"像名字"的字符串(顶层或一层嵌套里):单号、客户名比 ObjectId 有辨识度。</summary>
    private static string DeepSummary(BsonDocument doc)
    {
        var parts = new List<string>(2);
        foreach (BsonElement element in doc)
        {
            if (element.Name == "_id")
            {
                continue;
            }
            if (element.Value is BsonString s && s.Value.Length is > 0 and <= 40)
            {
                parts.Add(s.Value);
            }
            else if (element.Value is BsonDocument nested
                     && nested.Elements.FirstOrDefault(static e => e.Value is BsonString { Value.Length: > 0 and <= 40 }) is { Value: BsonString inner })
            {
                parts.Add(inner.Value);
            }
            if (parts.Count == 2)
            {
                break;
            }
        }
        return BsonText.OneLine(string.Join(" · ", parts));
    }
}

/// <summary>把网格行摊平成树视图的行(按展开状态)。</summary>
internal static class DocTreeBuilder
{
    /// <summary>展开状态的键:<c>行号:路径</c>(文档行路径为空)。</summary>
    public static string ExpansionKey(CollectionRow row, string path) => $"{row.Number}{(row.Insert is null ? "" : "*" + row.Insert.Key)}:{path}";

    /// <summary>生成全部可见行。</summary>
    public static List<DocTreeRow> Build(IEnumerable<CollectionRow> rows, IReadOnlySet<string> expanded, StagingArea staging, Loc loc)
    {
        var list = new List<DocTreeRow>();
        foreach (CollectionRow row in rows)
        {
            AppendDocument(list, row, expanded, staging, loc);
        }
        return list;
    }

    /// <summary>一份文档(及其展开的字段)的行。</summary>
    public static void AppendDocument(List<DocTreeRow> list, CollectionRow row, IReadOnlySet<string> expanded, StagingArea staging, Loc loc)
    {
        string key = row.Document.GetValue("_id", BsonNull.Value) is { IsBsonNull: false } id ? BsonText.Cell(id) : loc["Cw_AutoId"];
        var docRow = new DocTreeRow(row, "", key, row.Document, 0, ChangeRelation.None, null, loc)
        {
            IsExpanded = expanded.Contains(ExpansionKey(row, ""))
        };
        list.Add(docRow);
        if (docRow.IsExpanded)
        {
            AppendChildren(list, row, row.Document, "", 1, expanded, row.Insert is null ? staging.EditOf(row.Id) : null, loc);
        }
    }

    /// <summary>一个容器的子行(递归展开)。</summary>
    public static void AppendChildren(List<DocTreeRow> list, CollectionRow row, BsonValue container, string parent, int depth,
        IReadOnlySet<string> expanded, StagedEdit? edit, Loc loc)
    {
        IEnumerable<(string Name, string Key, BsonValue Value, bool IsElement)> children = container switch
        {
            BsonDocument doc => doc.Elements.Select(static e => (e.Name, e.Name, e.Value, false)),
            BsonArray array => array.Select(static (v, i) => (i.ToString(CultureInfo.InvariantCulture), $"[{i}]", v, true)),
            _ => []
        };
        foreach ((string name, string key, BsonValue value, bool isElement) in children)
        {
            string path = BsonPath.Join(parent.Length == 0 ? null : parent, name);
            ChangeRelation relation = edit?.RelationOf(path) ?? (row.IsAdded ? ChangeRelation.None : ChangeRelation.None);
            BsonValue? original = relation == ChangeRelation.Exact ? edit!.Find(path)?.Original : null;
            var child = new DocTreeRow(row, path, key, value, depth, relation, original, loc)
            {
                IsArrayElement = isElement,
                IsExpanded = expanded.Contains(ExpansionKey(row, path))
            };
            list.Add(child);
            if (child.IsExpanded && child.IsExpandable)
            {
                AppendChildren(list, row, value, path, depth + 1, expanded, edit, loc);
            }
        }
    }
}
