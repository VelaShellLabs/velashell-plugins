using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>右侧预览栏的文本,以及每一行对应哪个字段(好把表单里的行状态映射成整行标记)。</summary>
/// <param name="Text">全文。</param>
/// <param name="LinePaths">每一行对应的字段路径(行号 - 1 为下标);大括号、注释行为 <see langword="null" />。</param>
/// <param name="RemovedLines">"已删除字段"那几行(注释形式,行号 1 起)。</param>
internal sealed record DocumentEditorPreview(string Text, IReadOnlyList<string?> LinePaths, IReadOnlyList<int> RemovedLines);

/// <summary>
/// 文档编辑器右栏的两段文本:当前文档的紧凑预览,与"将执行的命令"。
/// </summary>
internal static class DocumentEditorFormat
{
    /// <summary>预览里嵌套文档最多展开几层;更深的给 <c>{ …N }</c>。</summary>
    private const int MaxDepth = 4;

    /// <summary>
    /// 紧凑预览(设计稿 05 右栏):330px 宽放不下完整的 mongosh 文本,所以
    /// ObjectId 缩写成 <c>66f5c2a1…e3b7</c>、对象数组折成 <c>[ …4 项 ]</c>、短的标量数组写一行;
    /// 原文档里有而现在没有的字段以注释行 <c>// note: null</c> 留在原处,标成删除。
    /// 复制按钮复制的是完整文本,不是这份预览。
    /// </summary>
    public static DocumentEditorPreview Preview(BsonDocument current, BsonDocument? original, Loc loc)
    {
        var lines = new List<string>();
        var paths = new List<string?>();
        var removed = new List<int>();
        lines.Add("{");
        paths.Add(null);
        WriteFields(current, original, null, 1, loc, lines, paths, removed);
        lines.Add("}");
        paths.Add(null);
        return new(string.Join('\n', lines), paths, removed);
    }

    private static void WriteFields(BsonDocument doc, BsonDocument? original, string? prefix, int depth, Loc loc,
        List<string> lines, List<string?> paths, List<int> removed)
    {
        string indent = new(' ', depth * 2);
        List<BsonElement> gone = original?.Elements.Where(e => !doc.Contains(e.Name)).ToList() ?? [];
        int index = 0;
        foreach (BsonElement element in doc)
        {
            bool last = ++index == doc.ElementCount;
            string comma = last ? "" : ",";
            string path = BsonPath.Join(prefix, element.Name);
            string key = BsonText.FieldName(element.Name);
            if (element.Value is BsonDocument child && child.ElementCount > 0 && depth < MaxDepth)
            {
                lines.Add($"{indent}{key}: {{");
                paths.Add(path);
                BsonDocument? before = original is not null && original.TryGetValue(element.Name, out BsonValue b) ? b as BsonDocument : null;
                WriteFields(child, before, path, depth + 1, loc, lines, paths, removed);
                lines.Add(indent + "}" + comma);
                paths.Add(path);
                continue;
            }
            lines.Add($"{indent}{key}: {Short(element.Value, loc, depth)}{comma}");
            paths.Add(path);
        }
        foreach (BsonElement element in gone)
        {
            lines.Add($"{indent}// {BsonText.FieldName(element.Name)}: {Short(element.Value, loc, depth)}");
            paths.Add(null);
            removed.Add(lines.Count);
        }
    }

    /// <summary>预览里的单行值。</summary>
    internal static string Short(BsonValue value, Loc loc, int depth = 0)
    {
        switch (value.BsonType)
        {
            case BsonType.ObjectId:
                return $"ObjectId(\"{BsonText.Shorten(value.AsObjectId.ToString())}\")";
            case BsonType.String:
            {
                string text = BsonText.OneLine(value.AsString);
                return BsonText.Quote(text.Length > 60 ? text[..60] + "…" : text);
            }
            case BsonType.Document:
            {
                BsonDocument doc = value.AsBsonDocument;
                return doc.ElementCount == 0 ? "{}" : $"{{ …{doc.ElementCount} }}";
            }
            case BsonType.Array:
            {
                BsonArray array = value.AsBsonArray;
                if (array.Count == 0)
                {
                    return "[]";
                }
                if (array.Count <= 6 && array.All(static v => v.BsonType is not (BsonType.Document or BsonType.Array)))
                {
                    string inline = "[ " + string.Join(", ", array.Select(v => Short(v, loc, depth + 1))) + " ]";
                    if (inline.Length <= 60)
                    {
                        return inline;
                    }
                }
                return loc.Format("Doc_PreviewItems", array.Count);
            }
            default:
                return BsonText.Literal(value);
        }
    }

    /// <summary>
    /// 将执行的命令(mongosh 写法,可以原样粘进查询编辑器):
    /// <c>db.orders.updateOne({ _id: … }, { $set: { … }, $unset: { … } })</c>,新建时 <c>insertOne</c>,
    /// 键序变了时 <c>replaceOne</c>。
    /// </summary>
    /// <param name="collectionRef"><c>orders</c> 或 <c>getCollection("weird-name")</c>。</param>
    /// <param name="method"><c>updateOne</c> / <c>replaceOne</c> / <c>insertOne</c>。</param>
    /// <param name="filter">筛选(插入时为 null)。</param>
    /// <param name="body">更新文档(<c>{ $set: …, $unset: … }</c>)、替换文档或要插入的文档。</param>
    public static string Command(string collectionRef, string method, BsonDocument? filter, BsonDocument body)
    {
        var b = new StringBuilder();
        b.Append("db.").Append(collectionRef).Append('.').Append(method).Append("(\n");
        if (filter is not null)
        {
            b.Append("  ").Append(BsonText.Literal(filter)).Append(",\n");
        }
        if (method == "updateOne")
        {
            AppendUpdate(b, body);
        }
        else
        {
            b.Append(Indent(BsonText.Pretty(body, EjsonMode.Shell), "  "));
        }
        b.Append("\n)");
        return b.ToString();
    }

    /// <summary>
    /// 更新文档的写法照设计稿:<c>{ $set: {</c> 起头,字段缩进六格,<c>} }</c> 收尾;
    /// 多个操作符之间 <c>},</c> 换行接下一个。
    /// </summary>
    private static void AppendUpdate(StringBuilder b, BsonDocument update)
    {
        int op = 0;
        foreach (BsonElement element in update)
        {
            b.Append(op++ == 0 ? "  { " : "    ").Append(element.Name).Append(": {");
            if (element.Value is BsonDocument fields)
            {
                int i = 0;
                foreach (BsonElement field in fields)
                {
                    b.Append(i++ == 0 ? "\n" : ",\n");
                    b.Append("      ").Append(BsonText.FieldName(field.Name)).Append(": ").Append(BsonText.Literal(field.Value));
                }
            }
            b.Append(op == update.ElementCount ? "\n  } }" : "\n    },\n");
        }
        if (update.ElementCount == 0)
        {
            b.Append("  {}");
        }
    }

    private static string Indent(string text, string indent) =>
        indent + text.Replace("\n", "\n" + indent, StringComparison.Ordinal);
}
