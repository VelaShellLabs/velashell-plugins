using System.Globalization;
using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「导出为代码」的目标语言。</summary>
internal enum PipelineCodeLanguage
{
    /// <summary>mongosh。</summary>
    Mongosh,

    /// <summary>C#(MongoDB.Driver)。</summary>
    CSharp,

    /// <summary>Python(PyMongo)。</summary>
    Python,

    /// <summary>Node.js(mongodb 驱动)。</summary>
    Node,

    /// <summary>Java(mongodb-driver-sync)。</summary>
    Java
}

/// <summary>
/// 把管道写成各语言驱动的代码(设计稿 04 工具栏「导出为代码」)。
/// <para>
/// 每种语言自己写一遍值的字面量,而不是统一吐一段 JSON 字符串再 <c>Parse</c>:
/// 那样导出的代码能跑,但 <c>ISODate</c>、<c>NumberDecimal</c> 这些类型信息全压在字符串里,
/// 用户要改一个日期还得去扩展 JSON 里找 <c>$date</c>。按语言写出 <c>new DateTime(…)</c>、
/// <c>Decimal128("…")</c>,导出来的就是能直接维护的代码。
/// </para>
/// </summary>
internal static class PipelineCode
{
    /// <summary>超过这个宽度的容器折行。</summary>
    private const int Width = 80;

    /// <summary>生成代码。</summary>
    /// <param name="language">语言。</param>
    /// <param name="database">库名。</param>
    /// <param name="collection">集合名。</param>
    /// <param name="stages">启用的阶段。</param>
    /// <param name="allowDiskUse">是否带 allowDiskUse。</param>
    public static string Generate(PipelineCodeLanguage language, string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse) =>
        language switch
        {
            PipelineCodeLanguage.CSharp => CSharp(database, collection, stages, allowDiskUse),
            PipelineCodeLanguage.Python => Python(database, collection, stages, allowDiskUse),
            PipelineCodeLanguage.Node => Node(database, collection, stages, allowDiskUse),
            PipelineCodeLanguage.Java => Java(database, collection, stages, allowDiskUse),
            _ => Mongosh(database, collection, stages, allowDiskUse)
        };

    // ── mongosh ─────────────────────────────────────────────────────────────

    private static string Mongosh(string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse)
    {
        var b = new StringBuilder();
        b.Append("use(").Append(BsonText.Quote(database)).Append(");\n\n");
        b.Append("db.").Append(MongoWorkspaceViewModel.ShellCollectionRef(collection)).Append(".aggregate(");
        b.Append(List(stages.Select(s => Js(s, 1, shell: true)), "[", "]", 0, "  "));
        if (allowDiskUse)
        {
            b.Append(", { allowDiskUse: true }");
        }
        return b.Append(");\n").ToString();
    }

    // ── Node.js ─────────────────────────────────────────────────────────────

    private static string Node(string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse)
    {
        string body = List(stages.Select(s => Js(s, 2, shell: false)), "[", "]", 1, "  ");
        var imports = new List<string> { "MongoClient" };
        foreach ((string token, string name) in new[]
                 {
                     ("new ObjectId(", "ObjectId"), ("Decimal128.", "Decimal128"), ("Long.", "Long"),
                     ("new UUID(", "UUID"), ("Binary.", "Binary"), ("new Timestamp(", "Timestamp"), ("new MinKey(", "MinKey"), ("new MaxKey(", "MaxKey")
                 })
        {
            if (body.Contains(token, StringComparison.Ordinal))
            {
                imports.Add(name);
            }
        }
        var b = new StringBuilder();
        b.Append("const { ").Append(string.Join(", ", imports)).Append(" } = require(\"mongodb\");\n\n");
        b.Append("const client = new MongoClient(process.env.MONGODB_URI ?? \"mongodb://localhost:27017\");\n\n");
        b.Append("async function run() {\n");
        b.Append("  const pipeline = ").Append(body).Append(";\n");
        b.Append("  const cursor = client.db(").Append(BsonText.Quote(database)).Append(").collection(").Append(BsonText.Quote(collection))
            .Append(").aggregate(pipeline").Append(allowDiskUse ? ", { allowDiskUse: true }" : "").Append(");\n");
        b.Append("  for await (const doc of cursor) {\n    console.log(doc);\n  }\n}\n\n");
        b.Append("run().finally(() => client.close());\n");
        return b.ToString();
    }

    /// <summary>JavaScript 字面量(mongosh 与 Node 只差在类型构造器的写法)。</summary>
    private static string Js(BsonValue value, int depth, bool shell)
    {
        switch (value)
        {
            case BsonDocument doc:
            {
                var fields = doc.Select(e => BsonText.FieldName(e.Name) + ": " + Js(e.Value, depth + 1, shell)).ToList();
                return Container(fields, "{ ", " }", "{}", depth, "  ");
            }
            case BsonArray array:
            {
                var items = array.Select(v => Js(v, depth + 1, shell)).ToList();
                return Container(items, "[ ", " ]", "[]", depth, "  ");
            }
        }
        if (shell)
        {
            return PipelineText.Flat(value);
        }
        return value.BsonType switch
        {
            BsonType.ObjectId => $"new ObjectId(\"{value.AsObjectId}\")",
            BsonType.DateTime => $"new Date(\"{BsonText.IsoDate(value)}\")",
            BsonType.Decimal128 => $"Decimal128.fromString(\"{value.AsDecimal128}\")",
            BsonType.Int64 => $"Long.fromString(\"{value.AsInt64.ToString(CultureInfo.InvariantCulture)}\")",
            BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard =>
                $"new UUID(\"{value.AsBsonBinaryData.ToGuid()}\")",
            BsonType.Binary => $"Binary.createFromBase64(\"{Convert.ToBase64String(value.AsBsonBinaryData.Bytes)}\", {(int)value.AsBsonBinaryData.SubType})",
            BsonType.Timestamp => $"new Timestamp({{ t: {value.AsBsonTimestamp.Timestamp}, i: {value.AsBsonTimestamp.Increment} }})",
            BsonType.MinKey => "new MinKey()",
            BsonType.MaxKey => "new MaxKey()",
            _ => BsonText.Literal(value)
        };
    }

    // ── Python ──────────────────────────────────────────────────────────────

    private static string Python(string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse)
    {
        string body = List(stages.Select(s => Py(s, 1)), "[", "]", 0, "    ");
        var bsonImports = new List<string>();
        foreach ((string token, string name) in new[]
                 {
                     ("ObjectId(", "ObjectId"), ("Decimal128(", "Decimal128"), ("Int64(", "Int64"), ("Regex(", "Regex"),
                     ("Binary(", "Binary"), ("Timestamp(", "Timestamp"), ("MinKey(", "MinKey"), ("MaxKey(", "MaxKey")
                 })
        {
            if (body.Contains(token, StringComparison.Ordinal))
            {
                bsonImports.Add(name);
            }
        }
        var b = new StringBuilder("import os\n");
        if (body.Contains("datetime(", StringComparison.Ordinal))
        {
            b.Append("from datetime import datetime, timezone\n");
        }
        if (body.Contains("uuid.UUID(", StringComparison.Ordinal))
        {
            b.Append("import uuid\n");
        }
        if (body.Contains("base64.", StringComparison.Ordinal))
        {
            b.Append("import base64\n");
        }
        b.Append('\n');
        if (bsonImports.Count > 0)
        {
            b.Append("from bson import ").Append(string.Join(", ", bsonImports)).Append('\n');
        }
        b.Append("from pymongo import MongoClient\n\n");
        b.Append("client = MongoClient(os.environ.get(\"MONGODB_URI\", \"mongodb://localhost:27017\"))\n");
        b.Append("collection = client[").Append(BsonText.Quote(database)).Append("][").Append(BsonText.Quote(collection)).Append("]\n\n");
        b.Append("pipeline = ").Append(body).Append("\n\n");
        b.Append("for doc in collection.aggregate(pipeline").Append(allowDiskUse ? ", allowDiskUse=True" : "").Append("):\n    print(doc)\n");
        return b.ToString();
    }

    private static string Py(BsonValue value, int depth)
    {
        switch (value)
        {
            case BsonDocument doc:
            {
                var fields = doc.Select(e => BsonText.Quote(e.Name) + ": " + Py(e.Value, depth + 1)).ToList();
                return Container(fields, "{", "}", "{}", depth, "    ");
            }
            case BsonArray array:
            {
                var items = array.Select(v => Py(v, depth + 1)).ToList();
                return Container(items, "[", "]", "[]", depth, "    ");
            }
        }
        return value.BsonType switch
        {
            BsonType.String => BsonText.Quote(value.AsString),
            BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
            BsonType.Int64 => $"Int64({value.AsInt64.ToString(CultureInfo.InvariantCulture)})",
            BsonType.Double => PyDouble(value.AsDouble),
            BsonType.Decimal128 => $"Decimal128(\"{value.AsDecimal128}\")",
            BsonType.Boolean => value.AsBoolean ? "True" : "False",
            BsonType.Null or BsonType.Undefined => "None",
            BsonType.ObjectId => $"ObjectId(\"{value.AsObjectId}\")",
            BsonType.DateTime => PyDate(value.AsBsonDateTime),
            BsonType.RegularExpression => $"Regex({BsonText.Quote(value.AsBsonRegularExpression.Pattern)}, {BsonText.Quote(value.AsBsonRegularExpression.Options)})",
            BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard =>
                $"Binary.from_uuid(uuid.UUID(\"{value.AsBsonBinaryData.ToGuid()}\"))",
            BsonType.Binary => $"Binary(base64.b64decode(\"{Convert.ToBase64String(value.AsBsonBinaryData.Bytes)}\"), {(int)value.AsBsonBinaryData.SubType})",
            BsonType.Timestamp => $"Timestamp({value.AsBsonTimestamp.Timestamp}, {value.AsBsonTimestamp.Increment})",
            BsonType.MinKey => "MinKey()",
            BsonType.MaxKey => "MaxKey()",
            _ => BsonText.Quote(value.ToString() ?? "")
        };
    }

    private static string PyDouble(double value) =>
        double.IsNaN(value) ? "float(\"nan\")"
        : double.IsPositiveInfinity(value) ? "float(\"inf\")"
        : double.IsNegativeInfinity(value) ? "float(\"-inf\")"
        : BsonText.FormatDouble(value);

    private static string PyDate(BsonDateTime date)
    {
        if (!date.IsValidDateTime)
        {
            return $"datetime.fromtimestamp({date.MillisecondsSinceEpoch / 1000.0}, timezone.utc)";
        }
        DateTime d = date.ToUniversalTime();
        var parts = new List<int> { d.Year, d.Month, d.Day };
        if (d.TimeOfDay != TimeSpan.Zero)
        {
            parts.AddRange([d.Hour, d.Minute, d.Second]);
            if (d.Millisecond != 0)
            {
                parts.Add(d.Millisecond * 1000);
            }
        }
        return $"datetime({string.Join(", ", parts)}, tzinfo=timezone.utc)";
    }

    // ── C# ──────────────────────────────────────────────────────────────────

    private static string CSharp(string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse)
    {
        var b = new StringBuilder();
        b.Append("using MongoDB.Bson;\nusing MongoDB.Driver;\n\n");
        b.Append("var client = new MongoClient(Environment.GetEnvironmentVariable(\"MONGODB_URI\") ?? \"mongodb://localhost:27017\");\n");
        b.Append("var collection = client.GetDatabase(").Append(CsString(database)).Append(").GetCollection<BsonDocument>(")
            .Append(CsString(collection)).Append(");\n\n");
        b.Append("BsonDocument[] pipeline =\n[\n");
        for (int i = 0; i < stages.Count; i++)
        {
            b.Append("    ").Append(Cs(stages[i], 1)).Append(i < stages.Count - 1 ? ",\n" : "\n");
        }
        b.Append("];\n\n");
        b.Append("var options = new AggregateOptions { AllowDiskUse = ").Append(allowDiskUse ? "true" : "false").Append(" };\n");
        b.Append("foreach (BsonDocument doc in collection.Aggregate(PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline), options).ToEnumerable())\n");
        b.Append("{\n    Console.WriteLine(doc);\n}\n");
        return b.ToString();
    }

    private static string Cs(BsonValue value, int depth)
    {
        string pad = new(' ', depth * 4);
        string inner = new(' ', (depth + 1) * 4);
        switch (value)
        {
            case BsonDocument { ElementCount: 0 }:
                return "new BsonDocument()";
            case BsonDocument { ElementCount: 1 } single:
                return $"new BsonDocument({CsString(single.GetElement(0).Name)}, {Cs(single.GetElement(0).Value, depth)})";
            case BsonDocument doc:
            {
                var flat = doc.Select(e => $"{{ {CsString(e.Name)}, {Cs(e.Value, depth + 1)} }}").ToList();
                string oneLine = "new BsonDocument { " + string.Join(", ", flat) + " }";
                if (oneLine.Length + depth * 4 <= Width && !oneLine.Contains('\n'))
                {
                    return oneLine;
                }
                return "new BsonDocument\n" + pad + "{\n" + string.Join(",\n", flat.Select(f => inner + f)) + "\n" + pad + "}";
            }
            case BsonArray { Count: 0 }:
                return "new BsonArray()";
            case BsonArray array:
            {
                var items = array.Select(v => Cs(v, depth + 1)).ToList();
                string oneLine = "new BsonArray { " + string.Join(", ", items) + " }";
                if (oneLine.Length + depth * 4 <= Width && !oneLine.Contains('\n'))
                {
                    return oneLine;
                }
                return "new BsonArray\n" + pad + "{\n" + string.Join(",\n", items.Select(f => inner + f)) + "\n" + pad + "}";
            }
        }
        return value.BsonType switch
        {
            BsonType.String => CsString(value.AsString),
            BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
            BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture) + "L",
            BsonType.Double => CsDouble(value.AsDouble),
            BsonType.Decimal128 => $"Decimal128.Parse(\"{value.AsDecimal128}\")",
            BsonType.Boolean => value.AsBoolean ? "true" : "false",
            BsonType.Null => "BsonNull.Value",
            BsonType.Undefined => "BsonUndefined.Value",
            BsonType.ObjectId => $"ObjectId.Parse(\"{value.AsObjectId}\")",
            BsonType.DateTime => CsDate(value.AsBsonDateTime),
            BsonType.RegularExpression => $"new BsonRegularExpression({CsString(value.AsBsonRegularExpression.Pattern)}, {CsString(value.AsBsonRegularExpression.Options)})",
            BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard =>
                $"new BsonBinaryData(Guid.Parse(\"{value.AsBsonBinaryData.ToGuid()}\"), GuidRepresentation.Standard)",
            BsonType.Binary => $"new BsonBinaryData(Convert.FromBase64String(\"{Convert.ToBase64String(value.AsBsonBinaryData.Bytes)}\"), (BsonBinarySubType){(int)value.AsBsonBinaryData.SubType})",
            BsonType.Timestamp => $"new BsonTimestamp({value.AsBsonTimestamp.Timestamp}, {value.AsBsonTimestamp.Increment})",
            BsonType.MinKey => "BsonMinKey.Value",
            BsonType.MaxKey => "BsonMaxKey.Value",
            _ => CsString(value.ToString() ?? "")
        };
    }

    private static string CsDouble(double value) =>
        double.IsNaN(value) ? "double.NaN"
        : double.IsPositiveInfinity(value) ? "double.PositiveInfinity"
        : double.IsNegativeInfinity(value) ? "double.NegativeInfinity"
        : BsonText.FormatDouble(value);

    private static string CsDate(BsonDateTime date)
    {
        if (!date.IsValidDateTime)
        {
            return $"new BsonDateTime({date.MillisecondsSinceEpoch}L)";
        }
        DateTime d = date.ToUniversalTime();
        return d.Millisecond == 0
            ? $"new DateTime({d.Year}, {d.Month}, {d.Day}, {d.Hour}, {d.Minute}, {d.Second}, DateTimeKind.Utc)"
            : $"new DateTime({d.Year}, {d.Month}, {d.Day}, {d.Hour}, {d.Minute}, {d.Second}, {d.Millisecond}, DateTimeKind.Utc)";
    }

    /// <summary>C# 字符串字面量(与 JSON 的转义规则在这些字符上一致)。</summary>
    private static string CsString(string text) => BsonText.Quote(text);

    // ── Java ────────────────────────────────────────────────────────────────

    private static string Java(string database, string collection, IReadOnlyList<BsonDocument> stages, bool allowDiskUse)
    {
        var body = new StringBuilder();
        for (int i = 0; i < stages.Count; i++)
        {
            body.Append("                ").Append(Jv(stages[i], 4)).Append(i < stages.Count - 1 ? ",\n" : "\n");
        }
        string text = body.ToString();
        var imports = new SortedSet<string>(StringComparer.Ordinal)
        {
            "com.mongodb.client.MongoClient", "com.mongodb.client.MongoClients", "com.mongodb.client.MongoCollection",
            "org.bson.Document", "java.util.Arrays", "java.util.List"
        };
        foreach ((string token, string import) in new[]
                 {
                     ("new ObjectId(", "org.bson.types.ObjectId"), ("new Decimal128(", "org.bson.types.Decimal128"),
                     ("new BigDecimal(", "java.math.BigDecimal"), ("Date.from(", "java.util.Date"), ("Instant.", "java.time.Instant"),
                     ("new BsonRegularExpression(", "org.bson.BsonRegularExpression"), ("new Binary(", "org.bson.types.Binary"),
                     ("Base64.", "java.util.Base64"), ("UUID.", "java.util.UUID"), ("new BsonTimestamp(", "org.bson.BsonTimestamp"),
                     ("new MinKey(", "org.bson.types.MinKey"), ("new MaxKey(", "org.bson.types.MaxKey")
                 })
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                imports.Add(import);
            }
        }
        var b = new StringBuilder();
        foreach (string import in imports)
        {
            b.Append("import ").Append(import).Append(";\n");
        }
        b.Append("\npublic class Pipeline {\n");
        b.Append("    public static void main(String[] args) {\n");
        b.Append("        String uri = System.getenv().getOrDefault(\"MONGODB_URI\", \"mongodb://localhost:27017\");\n");
        b.Append("        try (MongoClient client = MongoClients.create(uri)) {\n");
        b.Append("            MongoCollection<Document> collection = client.getDatabase(").Append(BsonText.Quote(database))
            .Append(").getCollection(").Append(BsonText.Quote(collection)).Append(");\n\n");
        b.Append("            List<Document> pipeline = Arrays.asList(\n");
        b.Append(text);
        b.Append("            );\n\n");
        b.Append("            for (Document doc : collection.aggregate(pipeline)").Append(allowDiskUse ? ".allowDiskUse(true)" : "").Append(") {\n");
        b.Append("                System.out.println(doc.toJson());\n            }\n        }\n    }\n}\n");
        return b.ToString();
    }

    private static string Jv(BsonValue value, int depth)
    {
        string inner = new(' ', (depth + 1) * 4);
        switch (value)
        {
            case BsonDocument { ElementCount: 0 }:
                return "new Document()";
            case BsonDocument doc:
            {
                var parts = doc.Select((e, i) => (i == 0 ? "new Document(" : ".append(") + BsonText.Quote(e.Name) + ", " + Jv(e.Value, depth + 1) + ")").ToList();
                string oneLine = string.Concat(parts);
                if (oneLine.Length + depth * 4 <= Width && !oneLine.Contains('\n'))
                {
                    return oneLine;
                }
                return parts[0] + string.Concat(parts.Skip(1).Select(p => "\n" + inner + p));
            }
            case BsonArray { Count: 0 }:
                return "Arrays.asList()";
            case BsonArray array:
            {
                var items = array.Select(v => Jv(v, depth + 1)).ToList();
                string oneLine = "Arrays.asList(" + string.Join(", ", items) + ")";
                if (oneLine.Length + depth * 4 <= Width && !oneLine.Contains('\n'))
                {
                    return oneLine;
                }
                return "Arrays.asList(\n" + string.Join(",\n", items.Select(i => inner + i)) + ")";
            }
        }
        return value.BsonType switch
        {
            BsonType.String => BsonText.Quote(value.AsString),
            BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
            BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture) + "L",
            BsonType.Double => JavaDouble(value.AsDouble),
            BsonType.Decimal128 => $"new Decimal128(new BigDecimal(\"{value.AsDecimal128}\"))",
            BsonType.Boolean => value.AsBoolean ? "true" : "false",
            BsonType.Null or BsonType.Undefined => "null",
            BsonType.ObjectId => $"new ObjectId(\"{value.AsObjectId}\")",
            BsonType.DateTime => $"Date.from(Instant.parse(\"{BsonText.IsoDate(value)}\"))",
            BsonType.RegularExpression => $"new BsonRegularExpression({BsonText.Quote(value.AsBsonRegularExpression.Pattern)}, {BsonText.Quote(value.AsBsonRegularExpression.Options)})",
            BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard =>
                $"UUID.fromString(\"{value.AsBsonBinaryData.ToGuid()}\")",
            BsonType.Binary => $"new Binary((byte) {(int)value.AsBsonBinaryData.SubType}, Base64.getDecoder().decode(\"{Convert.ToBase64String(value.AsBsonBinaryData.Bytes)}\"))",
            BsonType.Timestamp => $"new BsonTimestamp({value.AsBsonTimestamp.Timestamp}, {value.AsBsonTimestamp.Increment})",
            BsonType.MinKey => "new MinKey()",
            BsonType.MaxKey => "new MaxKey()",
            _ => BsonText.Quote(value.ToString() ?? "")
        };
    }

    private static string JavaDouble(double value) =>
        double.IsNaN(value) ? "Double.NaN"
        : double.IsPositiveInfinity(value) ? "Double.POSITIVE_INFINITY"
        : double.IsNegativeInfinity(value) ? "Double.NEGATIVE_INFINITY"
        : BsonText.FormatDouble(value);

    // ── 公共排版 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 容器:放得进一行就一行,否则每项一行。子项已按 <paramref name="depth" /> + 1 写好
    /// (子项自己的折行与缩进都对),这里只决定本层怎么摆。
    /// </summary>
    private static string Container(List<string> items, string open, string close, string empty, int depth, string unit)
    {
        if (items.Count == 0)
        {
            return empty;
        }
        string flat = open + string.Join(", ", items) + close;
        if (flat.Length + depth * unit.Length <= Width && !flat.Contains('\n'))
        {
            return flat;
        }
        string pad = string.Concat(Enumerable.Repeat(unit, depth));
        string inner = pad + unit;
        return open.TrimEnd() + "\n" + string.Join(",\n", items.Select(i => inner + i)) + "\n" + pad + close.TrimStart();
    }

    /// <summary>顶层的阶段数组:每个阶段一行。</summary>
    private static string List(IEnumerable<string> items, string open, string close, int depth, string unit)
    {
        string pad = string.Concat(Enumerable.Repeat(unit, depth));
        string inner = pad + unit;
        var list = items.ToList();
        if (list.Count == 0)
        {
            return open + close;
        }
        return open + "\n" + string.Join(",\n", list.Select(i => inner + i)) + "\n" + pad + close;
    }
}
