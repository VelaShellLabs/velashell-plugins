using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>
/// 由抽样生成 <c>$jsonSchema</c> 验证规则(设计稿 08 右栏),以及验证规则的"按宽度折行"排版(08 预览、16 编辑器)。
/// <para>
/// 生成策略**偏宽松**:规则一旦应用,写不进去的是业务;宁可漏掉一条约束,也不要因为抽样恰好没抽到
/// 某个合法取值就把它拦在门外。所以 —— 只有出现率 100% 的字段进 <c>required</c>;
/// <c>enum</c> 只给取值少、且每个取值都反复出现过的字符串(只出现一次的取值很可能是抽样没抽全的长尾);
/// <c>minimum: 0</c> 只给全部非负的数值;数组只约束"非空"。
/// </para>
/// </summary>
internal static class JsonSchemaGenerator
{
    /// <summary>生成 enum 的取值个数上限。</summary>
    public const int EnumMax = 10;

    /// <summary>
    /// 由 Schema 报告生成整条 validator(<c>{ $jsonSchema: { … } }</c>)。<c>_id</c> 不进规则(服务器保证它在)。
    /// </summary>
    public static BsonDocument Generate(AnalyzedSchema report)
    {
        ArgumentNullException.ThrowIfNull(report);
        BsonDocument schema = ObjectSchema(report, parent: null, parentPresent: report.Sampled);
        return new BsonDocument("$jsonSchema", schema);
    }

    /// <summary>一层对象的 schema(顶层或嵌套文档)。</summary>
    private static BsonDocument ObjectSchema(AnalyzedSchema report, string? parent, int parentPresent)
    {
        var schema = new BsonDocument("bsonType", "object");
        var required = new BsonArray();
        var properties = new BsonDocument();
        foreach (AnalyzedField field in report.ChildrenOf(parent))
        {
            if (field.InArray || (parent is null && field.Path == "_id"))
            {
                continue;
            }
            if (parentPresent > 0 && field.Present >= parentPresent)
            {
                _ = required.Add(field.Name);
            }
            properties[field.Name] = PropertySchema(report, field);
        }
        if (required.Count > 0)
        {
            schema["required"] = required;
        }
        if (properties.ElementCount > 0)
        {
            schema["properties"] = properties;
        }
        return schema;
    }

    /// <summary>一个字段的 schema。</summary>
    private static BsonDocument PropertySchema(AnalyzedSchema report, AnalyzedField field)
    {
        IReadOnlyList<BsonKind> kinds = field.PresentKinds;
        if (kinds.Count == 1 && kinds[0] == BsonKind.String && IsStableEnum(field))
        {
            // 设计稿 08:enum 已经限定了取值,bsonType 写了也是多余。
            return new BsonDocument("enum", new BsonArray(field.Values
                .OrderByDescending(static v => v.Value)
                .ThenBy(static v => v.Key.AsString, StringComparer.Ordinal)
                .Select(static v => v.Key)));
        }
        var schema = new BsonDocument();
        BsonValue type = BsonTypeOf(kinds);
        if (!type.IsBsonNull)
        {
            schema["bsonType"] = type;
        }
        if (kinds.Count == 1)
        {
            BsonKind kind = kinds[0];
            if (BsonKinds.IsNumeric(kind) && field.Numbers.Count > 0 && field.Numbers.All(static n => n >= 0))
            {
                schema["minimum"] = 0;
            }
            else if (kind == BsonKind.Array && field.ArrayLengths.Count > 0 && !field.ArrayLengths.ContainsKey(0))
            {
                schema["minItems"] = 1;
            }
            else if (kind == BsonKind.Object)
            {
                BsonDocument nested = ObjectSchema(report, field.Path, field.Present);
                foreach (string key in new[] { "required", "properties" })
                {
                    if (nested.TryGetValue(key, out BsonValue value))
                    {
                        schema[key] = value;
                    }
                }
            }
        }
        return schema;
    }

    /// <summary>取值少且稳定:每个取值至少出现两次(且不少于 1%),不同取值不超过 <see cref="EnumMax" />。</summary>
    internal static bool IsStableEnum(AnalyzedField field)
    {
        if (!SchemaAnalyzer.IsLowCardinality(field) || field.DistinctCount > EnumMax || field.ScalarCount < 20)
        {
            return false;
        }
        int floor = Math.Max(2, (int)Math.Ceiling(field.ScalarCount * 0.01));
        return field.Values.Values.All(c => c >= floor);
    }

    /// <summary>BSON 类型 → <c>$jsonSchema</c> 的 <c>bsonType</c> 别名。</summary>
    public static string? BsonTypeName(BsonKind kind) => kind switch
    {
        BsonKind.String => "string",
        BsonKind.Int32 => "int",
        BsonKind.Int64 => "long",
        BsonKind.Double => "double",
        BsonKind.Decimal128 => "decimal",
        BsonKind.Boolean => "bool",
        BsonKind.Date => "date",
        BsonKind.ObjectId => "objectId",
        BsonKind.Object => "object",
        BsonKind.Array => "array",
        BsonKind.Null => "null",
        BsonKind.Binary or BsonKind.Uuid => "binData",
        BsonKind.Regex => "regex",
        BsonKind.Timestamp => "timestamp",
        _ => null
    };

    /// <summary>
    /// 一组类型 → <c>bsonType</c> 的值:一种就是字符串,多种是数组;
    /// 多种数值类型并成 <c>"number"</c>(Int32 与 Double 混用是 JavaScript 驱动写数字的常态,不是脏数据)。
    /// </summary>
    internal static BsonValue BsonTypeOf(IReadOnlyList<BsonKind> kinds)
    {
        var names = new List<string>();
        bool numbers = kinds.Count(BsonKinds.IsNumeric) > 1;
        foreach (BsonKind kind in kinds)
        {
            string? name = numbers && BsonKinds.IsNumeric(kind) ? "number" : BsonTypeName(kind);
            if (name is not null && !names.Contains(name))
            {
                names.Add(name);
            }
        }
        return names.Count switch
        {
            0 => BsonNull.Value,
            1 => names[0],
            _ => new BsonArray(names)
        };
    }

    /// <summary>
    /// 把 validator 排成"能一行写下的就写一行,写不下才展开"的 mongosh 文本(设计稿 16 编辑器里的样子)。
    /// </summary>
    /// <param name="value">validator(或其中任意一段)。</param>
    /// <param name="width">单行的最大宽度(含缩进)。</param>
    /// <param name="compactRoot">
    /// 根是只有一个键的文档时,把 <c>{ $jsonSchema: {</c> 并在第一行(设计稿 08 预览的写法)。
    /// </param>
    public static string Format(BsonValue value, int width = 88, bool compactRoot = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder();
        if (compactRoot && value is BsonDocument { ElementCount: 1 } root && root.GetElement(0).Value is BsonDocument { ElementCount: > 0 } inner)
        {
            _ = builder.Append("{ ").Append(BsonText.FieldName(root.GetElement(0).Name)).Append(": ");
            WriteExpanded(builder, inner, 0, width);
            _ = builder.Append(" }");
            return builder.ToString();
        }
        Write(builder, value, 0, width);
        return builder.ToString();
    }

    private static void Write(StringBuilder b, BsonValue value, int depth, int width, int used = 0)
    {
        string inline = BsonText.Literal(value);
        if (value is not (BsonDocument or BsonArray) || (depth * 2) + used + inline.Length <= width)
        {
            _ = b.Append(inline);
            return;
        }
        WriteExpanded(b, value, depth, width);
    }

    private static void WriteExpanded(StringBuilder b, BsonValue value, int depth, int width)
    {
        string pad = new(' ', (depth + 1) * 2);
        if (value is BsonDocument doc)
        {
            _ = b.Append('{');
            int i = 0;
            foreach (BsonElement element in doc)
            {
                _ = b.Append(i++ == 0 ? "\n" : ",\n").Append(pad);
                string key = BsonText.FieldName(element.Name) + ": ";
                _ = b.Append(key);
                Write(b, element.Value, depth + 1, width, key.Length);
            }
            _ = b.Append('\n').Append(' ', depth * 2).Append('}');
            return;
        }
        BsonArray array = value.AsBsonArray;
        _ = b.Append('[');
        if (array.All(static v => v is not (BsonDocument or BsonArray)))
        {
            // 全是标量(required、enum 的取值表):按宽度流式排,一行放得下几个放几个 ——
            // 一个字段名占一行,十个字段的 required 就要拉出十几行。
            int column = width;
            for (int i = 0; i < array.Count; i++)
            {
                string item = BsonText.Literal(array[i]) + (i < array.Count - 1 ? "," : "");
                if (column + 1 + item.Length > width && column > pad.Length)
                {
                    _ = b.Append('\n').Append(pad);
                    column = pad.Length;
                }
                else if (i > 0)
                {
                    _ = b.Append(' ');
                    column++;
                }
                _ = b.Append(item);
                column += item.Length;
            }
            _ = b.Append('\n').Append(' ', depth * 2).Append(']');
            return;
        }
        for (int i = 0; i < array.Count; i++)
        {
            _ = b.Append(i == 0 ? "\n" : ",\n").Append(pad);
            Write(b, array[i], depth + 1, width);
        }
        _ = b.Append('\n').Append(' ', depth * 2).Append(']');
    }
}
