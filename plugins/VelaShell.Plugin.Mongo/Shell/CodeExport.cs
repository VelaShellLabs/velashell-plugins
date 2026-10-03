using System.Globalization;
using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>导出为代码的目标语言。</summary>
public enum CodeTarget
{
    /// <summary>mongosh(规范化后的原语句)。</summary>
    Mongosh,

    /// <summary>C#(MongoDB.Driver)。</summary>
    CSharp,

    /// <summary>Python(PyMongo)。</summary>
    Python,

    /// <summary>Node.js(官方 mongodb 驱动)。</summary>
    NodeJs,

    /// <summary>Java(mongodb-driver-sync)。</summary>
    Java
}

/// <summary>
/// 一条语句 → 各语言驱动的等价代码(设计稿 03「导出为代码」/ code lens「复制为 C#」)。
/// <para>
/// 字面量逐个类型翻成目标语言的构造写法 —— <c>ObjectId</c>、<c>ISODate</c>、<c>NumberDecimal</c>、正则在每种驱动里
/// 都有自己的类型,一份"差不多的 JSON 字符串"贴过去,日期会变成字符串、金额会丢精度。
/// 认不出的操作明确写一行注释,而不是生成一段看起来对、跑起来错的代码。
/// </para>
/// </summary>
public static class CodeExport
{
    /// <summary>生成代码。</summary>
    /// <param name="command">语句。</param>
    /// <param name="database">语句作用的库(会话当前库)。</param>
    /// <param name="target">目标语言。</param>
    /// <param name="connectionString">示例里用的连接串(不含凭据)。</param>
    public static string Generate(ShellCommand command, string database, CodeTarget target, string connectionString = "mongodb://localhost:27017")
    {
        Op op = Describe(command, command.Database ?? database);
        return target switch
        {
            CodeTarget.Mongosh => Mongosh(command, op),
            CodeTarget.CSharp => new CSharpWriter(op, connectionString).Write(),
            CodeTarget.Python => new PythonWriter(op, connectionString).Write(),
            CodeTarget.NodeJs => new NodeWriter(op, connectionString).Write(),
            _ => new JavaWriter(op, connectionString).Write()
        };
    }

    /// <summary>各语言的显示名。</summary>
    public static string Name(CodeTarget target) => target switch
    {
        CodeTarget.Mongosh => "mongosh",
        CodeTarget.CSharp => "C#",
        CodeTarget.Python => "Python",
        CodeTarget.NodeJs => "Node.js",
        _ => "Java"
    };

    // ── 语句 → 中间形态 ─────────────────────────────────────────────────────

    /// <summary>与语言无关的操作描述。</summary>
    private sealed class Op
    {
        public required string Name { get; init; }
        public required string Database { get; init; }
        public string? Collection { get; init; }
        public BsonDocument? Filter { get; init; }
        public BsonDocument? Projection { get; init; }
        public BsonDocument? Sort { get; init; }
        public int Skip { get; init; }
        public int Limit { get; init; }
        public BsonValue? Hint { get; init; }
        public int MaxTimeMs { get; init; }
        public BsonArray? Pipeline { get; init; }
        public BsonValue? Document { get; init; }
        public BsonValue? Update { get; init; }
        public string? Field { get; init; }
        public bool Upsert { get; init; }
        public bool ReturnNew { get; init; }
        public bool Count { get; init; }
        public BsonValue? Index { get; init; }
        public BsonDocument? Command { get; init; }
    }

    private static Op Describe(ShellCommand command, string database)
    {
        ShellCall? method = command.Method;
        if (command.Kind == ShellCommandKind.Database && method is { Name: "runCommand" or "adminCommand" })
        {
            return new Op
            {
                Name = "runCommand",
                Database = method.Name == "adminCommand" ? "admin" : database,
                Command = method.Arg(0) switch
                {
                    BsonDocument d => d,
                    BsonString s => new BsonDocument(s.Value, 1),
                    _ => []
                }
            };
        }
        if (command.Kind != ShellCommandKind.Collection || method is null)
        {
            return new Op { Name = "unsupported", Database = database };
        }
        BsonDocument settings2 = method.Document(2) ?? [];
        BsonDocument settings1 = method.Document(1) ?? [];
        switch (method.Name)
        {
            case "find" or "findOne":
            {
                Core.FindRequest request = ShellExecutor.BuildFindRequest(command, database, 0);
                return new Op
                {
                    Name = method.Name,
                    Database = database,
                    Collection = command.Collection,
                    Filter = request.Filter,
                    Projection = request.Projection,
                    Sort = request.Sort,
                    Skip = request.Skip,
                    Limit = method.Name == "findOne" ? 0 : request.Limit,
                    Hint = request.Hint,
                    MaxTimeMs = request.MaxTimeMs,
                    Count = command.Modifier("count") is not null
                };
            }
            case "aggregate":
                return new Op
                {
                    Name = "aggregate",
                    Database = database,
                    Collection = command.Collection,
                    Pipeline = method.Arg(0) as BsonArray ?? (method.Arg(0) is BsonDocument single ? [single] : [])
                };
            case "countDocuments" or "deleteOne" or "deleteMany" or "findOneAndDelete":
                return new Op { Name = method.Name, Database = database, Collection = command.Collection, Filter = method.Document(0) ?? [] };
            case "estimatedDocumentCount" or "getIndexes" or "drop" or "stats":
                return new Op { Name = method.Name, Database = database, Collection = command.Collection };
            case "distinct":
                return new Op
                {
                    Name = "distinct",
                    Database = database,
                    Collection = command.Collection,
                    Field = method.Arg(0)?.ToString(),
                    Filter = method.Document(1) ?? []
                };
            case "insertOne" or "insertMany":
                return new Op { Name = method.Name, Database = database, Collection = command.Collection, Document = method.Arg(0) };
            case "updateOne" or "updateMany" or "replaceOne" or "findOneAndUpdate" or "findOneAndReplace":
                return new Op
                {
                    Name = method.Name,
                    Database = database,
                    Collection = command.Collection,
                    Filter = method.Document(0) ?? [],
                    Update = method.Arg(1),
                    Upsert = settings2.GetValue("upsert", false).ToBoolean(),
                    ReturnNew = settings2.GetValue("returnNewDocument", false).ToBoolean() || settings2.GetValue("returnDocument", "").ToString() == "after"
                };
            case "createIndex":
                return new Op { Name = "createIndex", Database = database, Collection = command.Collection, Document = method.Arg(0), Command = settings1 };
            case "dropIndex":
                return new Op { Name = "dropIndex", Database = database, Collection = command.Collection, Index = method.Arg(0) };
            default:
                return new Op { Name = "unsupported", Database = database, Collection = command.Collection };
        }
    }

    // ── mongosh ─────────────────────────────────────────────────────────────

    private static string Mongosh(ShellCommand command, Op op)
    {
        var b = new StringBuilder();
        b.Append("use(").Append(BsonText.Quote(op.Database)).Append(");\n\n");
        switch (command.Kind)
        {
            case ShellCommandKind.Use:
                return $"use({BsonText.Quote(command.Target ?? op.Database)});";
            case ShellCommandKind.Show:
                return $"show {command.Target}";
        }
        b.Append("db");
        if (command.Collection is { } collection)
        {
            b.Append(System.Text.RegularExpressions.Regex.IsMatch(collection, "^[A-Za-z_$][A-Za-z0-9_$]*$")
                ? "." + collection
                : $".getCollection({BsonText.Quote(collection)})");
        }
        if (command.Explain is { } verbosity)
        {
            b.Append(".explain(").Append(BsonText.Quote(verbosity)).Append(')');
        }
        if (command.Method is { } method)
        {
            AppendCall(b, method, multiline: method.Arguments.Count > 0 && method.Arguments.Sum(static a => a.Text.Length) > 60);
        }
        foreach (ShellCall call in command.Chain)
        {
            AppendCall(b, call, multiline: false);
        }
        b.Append(';');
        return b.ToString();
    }

    private static void AppendCall(StringBuilder b, ShellCall call, bool multiline)
    {
        b.Append('.').Append(call.Name).Append('(');
        if (multiline)
        {
            b.Append('\n');
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                b.Append("  ").Append(Indent(BsonText.Pretty(call.Arguments[i].Value), "  "));
                b.Append(i < call.Arguments.Count - 1 ? ",\n" : "\n");
            }
        }
        else
        {
            b.Append(string.Join(", ", call.Arguments.Select(static a => BsonText.Literal(a.Value))));
        }
        b.Append(')');
    }

    private static string Indent(string text, string indent) => text.Replace("\n", "\n" + indent, StringComparison.Ordinal);

    // ── 公共写出器 ──────────────────────────────────────────────────────────

    private abstract class Writer(Op op, string connectionString)
    {
        protected Op Op { get; } = op;

        protected string ConnectionString { get; } = connectionString;

        protected readonly StringBuilder Body = new();

        protected readonly SortedSet<string> Imports = new(StringComparer.Ordinal);

        public abstract string Write();

        protected static string Escape(string text)
        {
            var b = new StringBuilder(text.Length + 2);
            b.Append('"');
            foreach (char c in text)
            {
                b.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => c < ' ' ? $"\\u{(int)c:x4}" : c.ToString()
                });
            }
            b.Append('"');
            return b.ToString();
        }

        protected static string IsoString(BsonValue value) => BsonText.IsoDate(value);

        protected static DateTime Utc(BsonValue value) =>
            DateTimeOffset.FromUnixTimeMilliseconds(value.AsBsonDateTime.MillisecondsSinceEpoch).UtcDateTime;

        protected static string Number(double d) => d.ToString("R", CultureInfo.InvariantCulture) is var s && (s.Contains('.') || s.Contains('E')) ? s : s + ".0";

        protected static string Pad(int level) => new(' ', level * 4);
    }

    // ── C# ──────────────────────────────────────────────────────────────────

    private sealed class CSharpWriter(Op op, string connectionString) : Writer(op, connectionString)
    {
        public override string Write()
        {
            Imports.Add("MongoDB.Bson");
            Imports.Add("MongoDB.Driver");
            var head = new StringBuilder();
            head.Append("var client = new MongoClient(").Append(Escape(ConnectionString)).Append(");\n");
            head.Append("var database = client.GetDatabase(").Append(Escape(Op.Database)).Append(");\n");
            if (Op.Collection is { } c)
            {
                head.Append("var collection = database.GetCollection<BsonDocument>(").Append(Escape(c)).Append(");\n");
            }
            head.Append('\n');
            Statement();
            var output = new StringBuilder();
            foreach (string import in Imports)
            {
                output.Append("using ").Append(import).Append(";\n");
            }
            return output.Append('\n').Append(head).Append(Body).ToString().TrimEnd() + "\n";
        }

        private void Var(string name, BsonValue value) => Body.Append("var ").Append(name).Append(" = ").Append(Lit(value, 0)).Append(";\n");

        private void Statement()
        {
            switch (Op.Name)
            {
                case "find" or "findOne":
                {
                    Var("filter", Op.Filter ?? []);
                    if (Op.Count)
                    {
                        Body.Append("\nvar count = await collection.CountDocumentsAsync(filter);\n");
                        return;
                    }
                    if (Op.Projection is { } p)
                    {
                        Var("projection", p);
                    }
                    if (Op.Sort is { } s)
                    {
                        Var("sort", s);
                    }
                    Body.Append('\n');
                    var options = new List<string>();
                    if (Op.Hint is { } hint)
                    {
                        options.Add("Hint = " + Lit(hint, 1));
                    }
                    if (Op.MaxTimeMs > 0)
                    {
                        options.Add($"MaxTime = TimeSpan.FromMilliseconds({Op.MaxTimeMs})");
                    }
                    Body.Append(Op.Name == "findOne" ? "var document = await collection.Find(filter" : "var documents = await collection.Find(filter");
                    if (options.Count > 0)
                    {
                        Body.Append(", new FindOptions { ").Append(string.Join(", ", options)).Append(" }");
                    }
                    Body.Append(')');
                    if (Op.Projection is not null)
                    {
                        Body.Append("\n    .Project(projection)");
                    }
                    if (Op.Sort is not null)
                    {
                        Body.Append("\n    .Sort(sort)");
                    }
                    if (Op.Skip > 0)
                    {
                        Body.Append("\n    .Skip(").Append(Op.Skip).Append(')');
                    }
                    if (Op.Limit > 0)
                    {
                        Body.Append("\n    .Limit(").Append(Op.Limit).Append(')');
                    }
                    Body.Append(Op.Name == "findOne" ? "\n    .FirstOrDefaultAsync();\n" : "\n    .ToListAsync();\n");
                    return;
                }
                case "aggregate":
                    Body.Append("var pipeline = new[]\n{\n");
                    for (int i = 0; i < Op.Pipeline!.Count; i++)
                    {
                        Body.Append(Pad(1)).Append(Lit(Op.Pipeline[i], 1)).Append(i < Op.Pipeline.Count - 1 ? ",\n" : "\n");
                    }
                    Body.Append("};\n\nvar documents = await collection.Aggregate<BsonDocument>(pipeline).ToListAsync();\n");
                    return;
                case "countDocuments":
                    Var("filter", Op.Filter!);
                    Body.Append("\nvar count = await collection.CountDocumentsAsync(filter);\n");
                    return;
                case "estimatedDocumentCount":
                    Body.Append("var count = await collection.EstimatedDocumentCountAsync();\n");
                    return;
                case "distinct":
                    Var("filter", Op.Filter!);
                    Body.Append("\nvar values = await (await collection.DistinctAsync<BsonValue>(").Append(Escape(Op.Field ?? "")).Append(", filter)).ToListAsync();\n");
                    return;
                case "insertOne":
                    Var("document", Op.Document ?? new BsonDocument());
                    Body.Append("\nawait collection.InsertOneAsync(document);\n");
                    return;
                case "insertMany":
                    Body.Append("var documents = new[]\n{\n");
                    BsonArray docs = Op.Document as BsonArray ?? [];
                    for (int i = 0; i < docs.Count; i++)
                    {
                        Body.Append(Pad(1)).Append(Lit(docs[i], 1)).Append(i < docs.Count - 1 ? ",\n" : "\n");
                    }
                    Body.Append("};\n\nawait collection.InsertManyAsync(documents);\n");
                    return;
                case "updateOne" or "updateMany" or "findOneAndUpdate":
                {
                    Var("filter", Op.Filter!);
                    if (Op.Update is BsonArray stages)
                    {
                        Body.Append("var update = Builders<BsonDocument>.Update.Pipeline(new[]\n{\n");
                        for (int i = 0; i < stages.Count; i++)
                        {
                            Body.Append(Pad(1)).Append(Lit(stages[i], 1)).Append(i < stages.Count - 1 ? ",\n" : "\n");
                        }
                        Body.Append("});\n");
                    }
                    else
                    {
                        Var("update", Op.Update ?? new BsonDocument());
                    }
                    Body.Append('\n');
                    if (Op.Name == "findOneAndUpdate")
                    {
                        Body.Append("var document = await collection.FindOneAndUpdateAsync<BsonDocument>(filter, update, new FindOneAndUpdateOptions<BsonDocument>\n{\n")
                            .Append(Pad(1)).Append("ReturnDocument = ReturnDocument.").Append(Op.ReturnNew ? "After" : "Before").Append(",\n")
                            .Append(Pad(1)).Append("IsUpsert = ").Append(Op.Upsert ? "true" : "false").Append("\n});\n");
                        return;
                    }
                    Body.Append("var result = await collection.").Append(Op.Name == "updateOne" ? "UpdateOneAsync" : "UpdateManyAsync").Append("(filter, update");
                    if (Op.Upsert)
                    {
                        Body.Append(", new UpdateOptions { IsUpsert = true }");
                    }
                    Body.Append(");\n");
                    return;
                }
                case "replaceOne" or "findOneAndReplace":
                    Var("filter", Op.Filter!);
                    Var("replacement", Op.Update ?? new BsonDocument());
                    Body.Append('\n').Append(Op.Name == "replaceOne"
                        ? $"var result = await collection.ReplaceOneAsync(filter, replacement{(Op.Upsert ? ", new ReplaceOptions { IsUpsert = true }" : "")});\n"
                        : "var document = await collection.FindOneAndReplaceAsync(filter, replacement);\n");
                    return;
                case "deleteOne" or "deleteMany" or "findOneAndDelete":
                    Var("filter", Op.Filter!);
                    Body.Append('\n').Append(Op.Name switch
                    {
                        "deleteOne" => "var result = await collection.DeleteOneAsync(filter);\n",
                        "deleteMany" => "var result = await collection.DeleteManyAsync(filter);\n",
                        _ => "var document = await collection.FindOneAndDeleteAsync(filter);\n"
                    });
                    return;
                case "createIndex":
                    Var("keys", Op.Document ?? new BsonDocument());
                    Body.Append("\nvar name = await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(keys");
                    if (Op.Command is { ElementCount: > 0 } indexOptions)
                    {
                        Body.Append(", new CreateIndexOptions { ").Append(string.Join(", ", indexOptions.Elements.Select(static e => e.Name switch
                        {
                            "unique" => $"Unique = {(e.Value.ToBoolean() ? "true" : "false")}",
                            "sparse" => $"Sparse = {(e.Value.ToBoolean() ? "true" : "false")}",
                            "name" => $"Name = {Escape(e.Value.ToString() ?? "")}",
                            "expireAfterSeconds" => $"ExpireAfter = TimeSpan.FromSeconds({e.Value})",
                            _ => $"/* {e.Name} */"
                        }))).Append(" }");
                    }
                    Body.Append("));\n");
                    return;
                case "dropIndex":
                    Body.Append("await collection.Indexes.DropOneAsync(").Append(Escape(Op.Index?.ToString() ?? "")).Append(");\n");
                    return;
                case "getIndexes":
                    Body.Append("var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();\n");
                    return;
                case "drop":
                    Body.Append("await database.DropCollectionAsync(").Append(Escape(Op.Collection ?? "")).Append(");\n");
                    return;
                case "stats":
                    Body.Append("var stats = await database.RunCommandAsync<BsonDocument>(new BsonDocument(\"collStats\", ")
                        .Append(Escape(Op.Collection ?? "")).Append("));\n");
                    return;
                case "runCommand":
                    Var("command", Op.Command ?? []);
                    Body.Append("\nvar reply = await database.RunCommandAsync<BsonDocument>(command);\n");
                    return;
                default:
                    Body.Append("// This statement has no driver equivalent here.\n");
                    return;
            }
        }

        private string Lit(BsonValue value, int level)
        {
            switch (value.BsonType)
            {
                case BsonType.Document:
                {
                    BsonDocument doc = value.AsBsonDocument;
                    if (doc.ElementCount == 0)
                    {
                        return "new BsonDocument()";
                    }
                    if (doc.ElementCount == 1 && !IsContainer(doc[0]))
                    {
                        return $"new BsonDocument({Escape(doc.GetElement(0).Name)}, {Lit(doc[0], level)})";
                    }
                    var b = new StringBuilder("new BsonDocument\n").Append(Pad(level)).Append("{\n");
                    for (int i = 0; i < doc.ElementCount; i++)
                    {
                        BsonElement e = doc.GetElement(i);
                        b.Append(Pad(level + 1)).Append("{ ").Append(Escape(e.Name)).Append(", ").Append(Lit(e.Value, level + 1)).Append(" }")
                            .Append(i < doc.ElementCount - 1 ? ",\n" : "\n");
                    }
                    return b.Append(Pad(level)).Append('}').ToString();
                }
                case BsonType.Array:
                {
                    BsonArray array = value.AsBsonArray;
                    if (array.Count == 0)
                    {
                        return "new BsonArray()";
                    }
                    if (!array.Any(IsContainer))
                    {
                        return "new BsonArray { " + string.Join(", ", array.Select(v => Lit(v, level))) + " }";
                    }
                    var b = new StringBuilder("new BsonArray\n").Append(Pad(level)).Append("{\n");
                    for (int i = 0; i < array.Count; i++)
                    {
                        b.Append(Pad(level + 1)).Append(Lit(array[i], level + 1)).Append(i < array.Count - 1 ? ",\n" : "\n");
                    }
                    return b.Append(Pad(level)).Append('}').ToString();
                }
                case BsonType.String:
                    return Escape(value.AsString);
                case BsonType.Int32:
                    return value.AsInt32.ToString(CultureInfo.InvariantCulture);
                case BsonType.Int64:
                    return value.AsInt64.ToString(CultureInfo.InvariantCulture) + "L";
                case BsonType.Double:
                    return Number(value.AsDouble);
                case BsonType.Decimal128:
                    return $"Decimal128.Parse({Escape(value.AsDecimal128.ToString())})";
                case BsonType.Boolean:
                    return value.AsBoolean ? "true" : "false";
                case BsonType.Null:
                    return "BsonNull.Value";
                case BsonType.ObjectId:
                    return $"new ObjectId({Escape(value.AsObjectId.ToString())})";
                case BsonType.DateTime:
                {
                    DateTime at = Utc(value);
                    return at.Millisecond == 0
                        ? $"new DateTime({at.Year}, {at.Month}, {at.Day}, {at.Hour}, {at.Minute}, {at.Second}, DateTimeKind.Utc)"
                        : $"new DateTime({at.Year}, {at.Month}, {at.Day}, {at.Hour}, {at.Minute}, {at.Second}, {at.Millisecond}, DateTimeKind.Utc)";
                }
                case BsonType.RegularExpression:
                    return $"new BsonRegularExpression({Escape(value.AsBsonRegularExpression.Pattern)}, {Escape(value.AsBsonRegularExpression.Options)})";
                case BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard:
                    return $"new BsonBinaryData(Guid.Parse({Escape(value.AsBsonBinaryData.ToGuid().ToString())}), GuidRepresentation.Standard)";
                case BsonType.Timestamp:
                    return $"new BsonTimestamp({value.AsBsonTimestamp.Timestamp}, {value.AsBsonTimestamp.Increment})";
                default:
                    return $"BsonDocument.Parse({Escape("{\"v\": " + value.ToJson() + "}")})[\"v\"]";
            }
        }
    }

    // ── Python ──────────────────────────────────────────────────────────────

    private sealed class PythonWriter(Op op, string connectionString) : Writer(op, connectionString)
    {
        public override string Write()
        {
            Imports.Add("from pymongo import MongoClient");
            Statement();
            var head = new StringBuilder();
            foreach (string import in Imports)
            {
                head.Append(import).Append('\n');
            }
            head.Append("\nclient = MongoClient(").Append(Escape(ConnectionString)).Append(")\n");
            head.Append("db = client[").Append(Escape(Op.Database)).Append("]\n");
            if (Op.Collection is { } c)
            {
                head.Append("collection = db[").Append(Escape(c)).Append("]\n");
            }
            return head.Append('\n').Append(Body).ToString().TrimEnd() + "\n";
        }

        private void Var(string name, BsonValue value) => Body.Append(name).Append(" = ").Append(Lit(value, 0)).Append('\n');

        private void Statement()
        {
            switch (Op.Name)
            {
                case "find" or "findOne":
                {
                    Var("filter", Op.Filter ?? []);
                    if (Op.Count)
                    {
                        Body.Append("\ncount = collection.count_documents(filter)\n");
                        return;
                    }
                    if (Op.Projection is { } p)
                    {
                        Var("projection", p);
                    }
                    Body.Append('\n');
                    string args = Op.Projection is null ? "filter" : "filter, projection";
                    if (Op.Name == "findOne")
                    {
                        Body.Append("document = collection.find_one(").Append(args).Append(")\n");
                        return;
                    }
                    Body.Append("cursor = collection.find(").Append(args).Append(')');
                    if (Op.Sort is { } s)
                    {
                        Body.Append(".sort([").Append(string.Join(", ", s.Elements.Select(e => $"({Escape(e.Name)}, {Lit(e.Value, 0)})"))).Append("])");
                    }
                    if (Op.Skip > 0)
                    {
                        Body.Append(".skip(").Append(Op.Skip).Append(')');
                    }
                    if (Op.Limit > 0)
                    {
                        Body.Append(".limit(").Append(Op.Limit).Append(')');
                    }
                    if (Op.Hint is { } hint)
                    {
                        Body.Append(".hint(").Append(hint is BsonDocument h
                            ? "[" + string.Join(", ", h.Elements.Select(e => $"({Escape(e.Name)}, {Lit(e.Value, 0)})")) + "]"
                            : Lit(hint, 0)).Append(')');
                    }
                    if (Op.MaxTimeMs > 0)
                    {
                        Body.Append(".max_time_ms(").Append(Op.MaxTimeMs).Append(')');
                    }
                    Body.Append("\ndocuments = list(cursor)\n");
                    return;
                }
                case "aggregate":
                    Var("pipeline", Op.Pipeline!);
                    Body.Append("\ndocuments = list(collection.aggregate(pipeline))\n");
                    return;
                case "countDocuments":
                    Var("filter", Op.Filter!);
                    Body.Append("\ncount = collection.count_documents(filter)\n");
                    return;
                case "estimatedDocumentCount":
                    Body.Append("count = collection.estimated_document_count()\n");
                    return;
                case "distinct":
                    Var("filter", Op.Filter!);
                    Body.Append("\nvalues = collection.distinct(").Append(Escape(Op.Field ?? "")).Append(", filter)\n");
                    return;
                case "insertOne":
                    Var("document", Op.Document ?? new BsonDocument());
                    Body.Append("\nresult = collection.insert_one(document)\n");
                    return;
                case "insertMany":
                    Var("documents", Op.Document ?? new BsonArray());
                    Body.Append("\nresult = collection.insert_many(documents)\n");
                    return;
                case "updateOne" or "updateMany" or "replaceOne":
                    Var("filter", Op.Filter!);
                    Var(Op.Name == "replaceOne" ? "replacement" : "update", Op.Update ?? new BsonDocument());
                    Body.Append("\nresult = collection.").Append(Op.Name switch { "updateOne" => "update_one", "updateMany" => "update_many", _ => "replace_one" })
                        .Append("(filter, ").Append(Op.Name == "replaceOne" ? "replacement" : "update").Append(Op.Upsert ? ", upsert=True" : "").Append(")\n");
                    return;
                case "findOneAndUpdate" or "findOneAndReplace":
                    Imports.Add("from pymongo import ReturnDocument");
                    Var("filter", Op.Filter!);
                    Var("update", Op.Update ?? new BsonDocument());
                    Body.Append("\ndocument = collection.").Append(Op.Name == "findOneAndUpdate" ? "find_one_and_update" : "find_one_and_replace")
                        .Append("(filter, update, return_document=ReturnDocument.").Append(Op.ReturnNew ? "AFTER" : "BEFORE")
                        .Append(Op.Upsert ? ", upsert=True" : "").Append(")\n");
                    return;
                case "deleteOne" or "deleteMany" or "findOneAndDelete":
                    Var("filter", Op.Filter!);
                    Body.Append("\n").Append(Op.Name switch
                    {
                        "deleteOne" => "result = collection.delete_one(filter)\n",
                        "deleteMany" => "result = collection.delete_many(filter)\n",
                        _ => "document = collection.find_one_and_delete(filter)\n"
                    });
                    return;
                case "createIndex":
                    Body.Append("name = collection.create_index([")
                        .Append(string.Join(", ", (Op.Document as BsonDocument ?? []).Elements.Select(e => $"({Escape(e.Name)}, {Lit(e.Value, 0)})")))
                        .Append(']');
                    foreach (BsonElement e in Op.Command ?? [])
                    {
                        Body.Append(", ").Append(e.Name switch { "expireAfterSeconds" => "expireAfterSeconds", _ => e.Name }).Append('=').Append(Lit(e.Value, 0));
                    }
                    Body.Append(")\n");
                    return;
                case "dropIndex":
                    Body.Append("collection.drop_index(").Append(Escape(Op.Index?.ToString() ?? "")).Append(")\n");
                    return;
                case "getIndexes":
                    Body.Append("indexes = list(collection.list_indexes())\n");
                    return;
                case "drop":
                    Body.Append("collection.drop()\n");
                    return;
                case "stats":
                    Body.Append("stats = db.command(\"collStats\", ").Append(Escape(Op.Collection ?? "")).Append(")\n");
                    return;
                case "runCommand":
                    Var("command", Op.Command ?? []);
                    Body.Append("\nreply = db.command(command)\n");
                    return;
                default:
                    Body.Append("# This statement has no driver equivalent here.\n");
                    return;
            }
        }

        private string Lit(BsonValue value, int level)
        {
            switch (value.BsonType)
            {
                case BsonType.Document:
                {
                    BsonDocument doc = value.AsBsonDocument;
                    if (doc.ElementCount == 0)
                    {
                        return "{}";
                    }
                    string inline = "{" + string.Join(", ", doc.Elements.Select(e => $"{Escape(e.Name)}: {Lit(e.Value, level + 1)}")) + "}";
                    if (inline.Length <= 72 && !inline.Contains('\n'))
                    {
                        return inline;
                    }
                    var b = new StringBuilder("{\n");
                    for (int i = 0; i < doc.ElementCount; i++)
                    {
                        BsonElement e = doc.GetElement(i);
                        b.Append(Pad(level + 1)).Append(Escape(e.Name)).Append(": ").Append(Lit(e.Value, level + 1)).Append(i < doc.ElementCount - 1 ? ",\n" : "\n");
                    }
                    return b.Append(Pad(level)).Append('}').ToString();
                }
                case BsonType.Array:
                {
                    BsonArray array = value.AsBsonArray;
                    string inline = "[" + string.Join(", ", array.Select(v => Lit(v, level + 1))) + "]";
                    if (inline.Length <= 72 && !inline.Contains('\n'))
                    {
                        return inline;
                    }
                    var b = new StringBuilder("[\n");
                    for (int i = 0; i < array.Count; i++)
                    {
                        b.Append(Pad(level + 1)).Append(Lit(array[i], level + 1)).Append(i < array.Count - 1 ? ",\n" : "\n");
                    }
                    return b.Append(Pad(level)).Append(']').ToString();
                }
                case BsonType.String:
                    return Escape(value.AsString);
                case BsonType.Int32 or BsonType.Int64:
                    return value.ToInt64().ToString(CultureInfo.InvariantCulture);
                case BsonType.Double:
                    return Number(value.AsDouble);
                case BsonType.Decimal128:
                    Imports.Add("from bson.decimal128 import Decimal128");
                    return $"Decimal128({Escape(value.AsDecimal128.ToString())})";
                case BsonType.Boolean:
                    return value.AsBoolean ? "True" : "False";
                case BsonType.Null:
                    return "None";
                case BsonType.ObjectId:
                    Imports.Add("from bson import ObjectId");
                    return $"ObjectId({Escape(value.AsObjectId.ToString())})";
                case BsonType.DateTime:
                {
                    Imports.Add("from datetime import datetime, timezone");
                    DateTime at = Utc(value);
                    string time = at.TimeOfDay == TimeSpan.Zero ? "" : $", {at.Hour}, {at.Minute}, {at.Second}" + (at.Millisecond > 0 ? $", {at.Millisecond * 1000}" : "");
                    return $"datetime({at.Year}, {at.Month}, {at.Day}{time}, tzinfo=timezone.utc)";
                }
                case BsonType.RegularExpression:
                    Imports.Add("from bson.regex import Regex");
                    return $"Regex({Escape(value.AsBsonRegularExpression.Pattern)}, {Escape(value.AsBsonRegularExpression.Options)})";
                case BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard:
                    Imports.Add("import uuid");
                    return $"uuid.UUID({Escape(value.AsBsonBinaryData.ToGuid().ToString())})";
                default:
                    return Escape(value.ToString() ?? "");
            }
        }
    }

    // ── Node.js ─────────────────────────────────────────────────────────────

    private sealed class NodeWriter(Op op, string connectionString) : Writer(op, connectionString)
    {
        public override string Write()
        {
            Imports.Add("MongoClient");
            Statement();
            var head = new StringBuilder("const { ").Append(string.Join(", ", Imports)).Append(" } = require(\"mongodb\");\n\n");
            head.Append("const client = new MongoClient(").Append(Escape(ConnectionString)).Append(");\n");
            head.Append("const db = client.db(").Append(Escape(Op.Database)).Append(");\n");
            if (Op.Collection is { } c)
            {
                head.Append("const collection = db.collection(").Append(Escape(c)).Append(");\n");
            }
            return head.Append('\n').Append(Body).ToString().TrimEnd() + "\n";
        }

        private void Var(string name, BsonValue value) => Body.Append("const ").Append(name).Append(" = ").Append(Lit(value, 0)).Append(";\n");

        private void Statement()
        {
            switch (Op.Name)
            {
                case "find" or "findOne":
                {
                    Var("filter", Op.Filter ?? []);
                    if (Op.Count)
                    {
                        Body.Append("\nconst count = await collection.countDocuments(filter);\n");
                        return;
                    }
                    var options = new List<string>();
                    if (Op.Projection is { } p)
                    {
                        options.Add("projection: " + Lit(p, 1));
                    }
                    if (Op.Hint is { } hint)
                    {
                        options.Add("hint: " + Lit(hint, 1));
                    }
                    if (Op.MaxTimeMs > 0)
                    {
                        options.Add($"maxTimeMS: {Op.MaxTimeMs}");
                    }
                    string optionText = options.Count == 0 ? "" : ", { " + string.Join(", ", options) + " }";
                    Body.Append('\n');
                    if (Op.Name == "findOne")
                    {
                        Body.Append("const document = await collection.findOne(filter").Append(optionText).Append(");\n");
                        return;
                    }
                    Body.Append("const documents = await collection\n    .find(filter").Append(optionText).Append(')');
                    if (Op.Sort is { } s)
                    {
                        Body.Append("\n    .sort(").Append(Lit(s, 1)).Append(')');
                    }
                    if (Op.Skip > 0)
                    {
                        Body.Append("\n    .skip(").Append(Op.Skip).Append(')');
                    }
                    if (Op.Limit > 0)
                    {
                        Body.Append("\n    .limit(").Append(Op.Limit).Append(')');
                    }
                    Body.Append("\n    .toArray();\n");
                    return;
                }
                case "aggregate":
                    Var("pipeline", Op.Pipeline!);
                    Body.Append("\nconst documents = await collection.aggregate(pipeline).toArray();\n");
                    return;
                case "countDocuments":
                    Var("filter", Op.Filter!);
                    Body.Append("\nconst count = await collection.countDocuments(filter);\n");
                    return;
                case "estimatedDocumentCount":
                    Body.Append("const count = await collection.estimatedDocumentCount();\n");
                    return;
                case "distinct":
                    Var("filter", Op.Filter!);
                    Body.Append("\nconst values = await collection.distinct(").Append(Escape(Op.Field ?? "")).Append(", filter);\n");
                    return;
                case "insertOne":
                    Var("document", Op.Document ?? new BsonDocument());
                    Body.Append("\nconst result = await collection.insertOne(document);\n");
                    return;
                case "insertMany":
                    Var("documents", Op.Document ?? new BsonArray());
                    Body.Append("\nconst result = await collection.insertMany(documents);\n");
                    return;
                case "updateOne" or "updateMany" or "replaceOne" or "findOneAndUpdate" or "findOneAndReplace":
                {
                    Var("filter", Op.Filter!);
                    string second = Op.Name is "replaceOne" or "findOneAndReplace" ? "replacement" : "update";
                    Var(second, Op.Update ?? new BsonDocument());
                    var options = new List<string>();
                    if (Op.Upsert)
                    {
                        options.Add("upsert: true");
                    }
                    if (Op.Name.StartsWith("findOneAnd", StringComparison.Ordinal))
                    {
                        options.Add($"returnDocument: \"{(Op.ReturnNew ? "after" : "before")}\"");
                    }
                    Body.Append('\n').Append(Op.Name.StartsWith("findOneAnd", StringComparison.Ordinal) ? "const document" : "const result")
                        .Append(" = await collection.").Append(Op.Name).Append("(filter, ").Append(second)
                        .Append(options.Count > 0 ? ", { " + string.Join(", ", options) + " }" : "").Append(");\n");
                    return;
                }
                case "deleteOne" or "deleteMany" or "findOneAndDelete":
                    Var("filter", Op.Filter!);
                    Body.Append('\n').Append(Op.Name == "findOneAndDelete" ? "const document" : "const result")
                        .Append(" = await collection.").Append(Op.Name).Append("(filter);\n");
                    return;
                case "createIndex":
                    Body.Append("const name = await collection.createIndex(").Append(Lit(Op.Document ?? new BsonDocument(), 0));
                    if (Op.Command is { ElementCount: > 0 } options2)
                    {
                        Body.Append(", ").Append(Lit(options2, 0));
                    }
                    Body.Append(");\n");
                    return;
                case "dropIndex":
                    Body.Append("await collection.dropIndex(").Append(Escape(Op.Index?.ToString() ?? "")).Append(");\n");
                    return;
                case "getIndexes":
                    Body.Append("const indexes = await collection.indexes();\n");
                    return;
                case "drop":
                    Body.Append("await collection.drop();\n");
                    return;
                case "stats":
                    Body.Append("const stats = await db.command({ collStats: ").Append(Escape(Op.Collection ?? "")).Append(" });\n");
                    return;
                case "runCommand":
                    Var("command", Op.Command ?? []);
                    Body.Append("\nconst reply = await db.command(command);\n");
                    return;
                default:
                    Body.Append("// This statement has no driver equivalent here.\n");
                    return;
            }
        }

        private string Lit(BsonValue value, int level)
        {
            switch (value.BsonType)
            {
                case BsonType.Document:
                {
                    BsonDocument doc = value.AsBsonDocument;
                    if (doc.ElementCount == 0)
                    {
                        return "{}";
                    }
                    string inline = "{ " + string.Join(", ", doc.Elements.Select(e => $"{Key(e.Name)}: {Lit(e.Value, level + 1)}")) + " }";
                    if (inline.Length <= 72 && !inline.Contains('\n'))
                    {
                        return inline;
                    }
                    var b = new StringBuilder("{\n");
                    for (int i = 0; i < doc.ElementCount; i++)
                    {
                        BsonElement e = doc.GetElement(i);
                        b.Append(Pad(level + 1)).Append(Key(e.Name)).Append(": ").Append(Lit(e.Value, level + 1)).Append(i < doc.ElementCount - 1 ? ",\n" : "\n");
                    }
                    return b.Append(Pad(level)).Append('}').ToString();
                }
                case BsonType.Array:
                {
                    BsonArray array = value.AsBsonArray;
                    string inline = "[" + string.Join(", ", array.Select(v => Lit(v, level + 1))) + "]";
                    if (inline.Length <= 72 && !inline.Contains('\n'))
                    {
                        return inline;
                    }
                    var b = new StringBuilder("[\n");
                    for (int i = 0; i < array.Count; i++)
                    {
                        b.Append(Pad(level + 1)).Append(Lit(array[i], level + 1)).Append(i < array.Count - 1 ? ",\n" : "\n");
                    }
                    return b.Append(Pad(level)).Append(']').ToString();
                }
                case BsonType.String:
                    return Escape(value.AsString);
                case BsonType.Int32:
                    return value.AsInt32.ToString(CultureInfo.InvariantCulture);
                case BsonType.Int64:
                    Imports.Add("Long");
                    return $"Long.fromString({Escape(value.AsInt64.ToString(CultureInfo.InvariantCulture))})";
                case BsonType.Double:
                    return value.AsDouble.ToString("R", CultureInfo.InvariantCulture);
                case BsonType.Decimal128:
                    Imports.Add("Decimal128");
                    return $"Decimal128.fromString({Escape(value.AsDecimal128.ToString())})";
                case BsonType.Boolean:
                    return value.AsBoolean ? "true" : "false";
                case BsonType.Null:
                    return "null";
                case BsonType.ObjectId:
                    Imports.Add("ObjectId");
                    return $"new ObjectId({Escape(value.AsObjectId.ToString())})";
                case BsonType.DateTime:
                    return $"new Date({Escape(IsoString(value))})";
                case BsonType.RegularExpression:
                    return $"/{value.AsBsonRegularExpression.Pattern.Replace("/", "\\/", StringComparison.Ordinal)}/{value.AsBsonRegularExpression.Options}";
                case BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard:
                    Imports.Add("UUID");
                    return $"new UUID({Escape(value.AsBsonBinaryData.ToGuid().ToString())})";
                default:
                    return Escape(value.ToString() ?? "");
            }
        }

        private static string Key(string name) =>
            System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_$][A-Za-z0-9_$]*$") ? name : Escape(name);
    }

    // ── Java ────────────────────────────────────────────────────────────────

    private sealed class JavaWriter(Op op, string connectionString) : Writer(op, connectionString)
    {
        public override string Write()
        {
            Imports.Add("com.mongodb.client.MongoClient");
            Imports.Add("com.mongodb.client.MongoClients");
            Imports.Add("com.mongodb.client.MongoDatabase");
            Imports.Add("org.bson.Document");
            if (Op.Collection is not null)
            {
                Imports.Add("com.mongodb.client.MongoCollection");
            }
            Statement();
            var head = new StringBuilder();
            foreach (string import in Imports)
            {
                head.Append("import ").Append(import).Append(";\n");
            }
            head.Append("\nMongoClient client = MongoClients.create(").Append(Escape(ConnectionString)).Append(");\n");
            head.Append("MongoDatabase database = client.getDatabase(").Append(Escape(Op.Database)).Append(");\n");
            if (Op.Collection is { } c)
            {
                head.Append("MongoCollection<Document> collection = database.getCollection(").Append(Escape(c)).Append(");\n");
            }
            return head.Append('\n').Append(Body).ToString().TrimEnd() + "\n";
        }

        private void Var(string name, BsonValue value) => Body.Append("Document ").Append(name).Append(" = ").Append(Lit(value, 0)).Append(";\n");

        private void Statement()
        {
            switch (Op.Name)
            {
                case "find" or "findOne":
                {
                    Var("filter", Op.Filter ?? []);
                    if (Op.Count)
                    {
                        Body.Append("\nlong count = collection.countDocuments(filter);\n");
                        return;
                    }
                    if (Op.Projection is { } p)
                    {
                        Var("projection", p);
                    }
                    if (Op.Sort is { } s)
                    {
                        Var("sort", s);
                    }
                    Body.Append('\n');
                    if (Op.Name == "findOne")
                    {
                        Body.Append("Document document = collection.find(filter)").Append(Op.Projection is null ? "" : ".projection(projection)").Append(".first();\n");
                        return;
                    }
                    Imports.Add("java.util.ArrayList");
                    Imports.Add("java.util.List");
                    Body.Append("List<Document> documents = collection.find(filter)");
                    if (Op.Projection is not null)
                    {
                        Body.Append("\n    .projection(projection)");
                    }
                    if (Op.Sort is not null)
                    {
                        Body.Append("\n    .sort(sort)");
                    }
                    if (Op.Skip > 0)
                    {
                        Body.Append("\n    .skip(").Append(Op.Skip).Append(')');
                    }
                    if (Op.Limit > 0)
                    {
                        Body.Append("\n    .limit(").Append(Op.Limit).Append(')');
                    }
                    if (Op.Hint is { } hint)
                    {
                        Body.Append(hint is BsonString ? "\n    .hintString(" : "\n    .hint(").Append(Lit(hint, 1)).Append(')');
                    }
                    if (Op.MaxTimeMs > 0)
                    {
                        Imports.Add("java.util.concurrent.TimeUnit");
                        Body.Append("\n    .maxTime(").Append(Op.MaxTimeMs).Append(", TimeUnit.MILLISECONDS)");
                    }
                    Body.Append("\n    .into(new ArrayList<>());\n");
                    return;
                }
                case "aggregate":
                    Imports.Add("java.util.ArrayList");
                    Imports.Add("java.util.Arrays");
                    Imports.Add("java.util.List");
                    Body.Append("List<Document> pipeline = Arrays.asList(\n");
                    for (int i = 0; i < Op.Pipeline!.Count; i++)
                    {
                        Body.Append(Pad(1)).Append(Lit(Op.Pipeline[i], 1)).Append(i < Op.Pipeline.Count - 1 ? ",\n" : "\n");
                    }
                    Body.Append(");\n\nList<Document> documents = collection.aggregate(pipeline).into(new ArrayList<>());\n");
                    return;
                case "countDocuments":
                    Var("filter", Op.Filter!);
                    Body.Append("\nlong count = collection.countDocuments(filter);\n");
                    return;
                case "estimatedDocumentCount":
                    Body.Append("long count = collection.estimatedDocumentCount();\n");
                    return;
                case "distinct":
                    Imports.Add("java.util.ArrayList");
                    Imports.Add("java.util.List");
                    Imports.Add("org.bson.BsonValue");
                    Var("filter", Op.Filter!);
                    Body.Append("\nList<BsonValue> values = collection.distinct(").Append(Escape(Op.Field ?? "")).Append(", BsonValue.class).filter(filter).into(new ArrayList<>());\n");
                    return;
                case "insertOne":
                    Var("document", Op.Document ?? new BsonDocument());
                    Body.Append("\ncollection.insertOne(document);\n");
                    return;
                case "insertMany":
                {
                    Imports.Add("java.util.Arrays");
                    Imports.Add("java.util.List");
                    BsonArray docs = Op.Document as BsonArray ?? [];
                    Body.Append("List<Document> documents = Arrays.asList(\n");
                    for (int i = 0; i < docs.Count; i++)
                    {
                        Body.Append(Pad(1)).Append(Lit(docs[i], 1)).Append(i < docs.Count - 1 ? ",\n" : "\n");
                    }
                    Body.Append(");\n\ncollection.insertMany(documents);\n");
                    return;
                }
                case "updateOne" or "updateMany" or "replaceOne" or "findOneAndUpdate" or "findOneAndReplace":
                {
                    Var("filter", Op.Filter!);
                    string second = Op.Name is "replaceOne" or "findOneAndReplace" ? "replacement" : "update";
                    Var(second, Op.Update ?? new BsonDocument());
                    Body.Append('\n');
                    if (Op.Name.StartsWith("findOneAnd", StringComparison.Ordinal))
                    {
                        string optionsType = Op.Name == "findOneAndUpdate" ? "FindOneAndUpdateOptions" : "FindOneAndReplaceOptions";
                        Imports.Add("com.mongodb.client.model." + optionsType);
                        Imports.Add("com.mongodb.client.model.ReturnDocument");
                        Body.Append("Document document = collection.").Append(Op.Name).Append("(filter, ").Append(second).Append(", new ").Append(optionsType)
                            .Append("()\n    .returnDocument(ReturnDocument.").Append(Op.ReturnNew ? "AFTER" : "BEFORE").Append(")\n    .upsert(")
                            .Append(Op.Upsert ? "true" : "false").Append("));\n");
                        return;
                    }
                    string optionsName = Op.Name == "replaceOne" ? "ReplaceOptions" : "UpdateOptions";
                    if (Op.Upsert)
                    {
                        Imports.Add("com.mongodb.client.model." + optionsName);
                    }
                    Body.Append(Op.Name == "replaceOne" ? "UpdateResult" : "UpdateResult").Append(" result = collection.").Append(Op.Name).Append("(filter, ").Append(second)
                        .Append(Op.Upsert ? $", new {optionsName}().upsert(true)" : "").Append(");\n");
                    Imports.Add("com.mongodb.client.result.UpdateResult");
                    return;
                }
                case "deleteOne" or "deleteMany":
                    Imports.Add("com.mongodb.client.result.DeleteResult");
                    Var("filter", Op.Filter!);
                    Body.Append("\nDeleteResult result = collection.").Append(Op.Name).Append("(filter);\n");
                    return;
                case "findOneAndDelete":
                    Var("filter", Op.Filter!);
                    Body.Append("\nDocument document = collection.findOneAndDelete(filter);\n");
                    return;
                case "createIndex":
                    Var("keys", Op.Document ?? new BsonDocument());
                    Body.Append("\nString name = collection.createIndex(keys");
                    if (Op.Command is { ElementCount: > 0 } options)
                    {
                        Imports.Add("com.mongodb.client.model.IndexOptions");
                        Body.Append(", new IndexOptions()");
                        foreach (BsonElement e in options)
                        {
                            Body.Append(e.Name switch
                            {
                                "unique" => $".unique({(e.Value.ToBoolean() ? "true" : "false")})",
                                "sparse" => $".sparse({(e.Value.ToBoolean() ? "true" : "false")})",
                                "name" => $".name({Escape(e.Value.ToString() ?? "")})",
                                _ => $" /* {e.Name} */"
                            });
                        }
                    }
                    Body.Append(");\n");
                    return;
                case "dropIndex":
                    Body.Append("collection.dropIndex(").Append(Escape(Op.Index?.ToString() ?? "")).Append(");\n");
                    return;
                case "getIndexes":
                    Imports.Add("java.util.ArrayList");
                    Imports.Add("java.util.List");
                    Body.Append("List<Document> indexes = collection.listIndexes().into(new ArrayList<>());\n");
                    return;
                case "drop":
                    Body.Append("collection.drop();\n");
                    return;
                case "stats":
                    Body.Append("Document stats = database.runCommand(new Document(\"collStats\", ").Append(Escape(Op.Collection ?? "")).Append("));\n");
                    return;
                case "runCommand":
                    Var("command", Op.Command ?? []);
                    Body.Append("\nDocument reply = database.runCommand(command);\n");
                    return;
                default:
                    Body.Append("// This statement has no driver equivalent here.\n");
                    return;
            }
        }

        private string Lit(BsonValue value, int level)
        {
            switch (value.BsonType)
            {
                case BsonType.Document:
                {
                    BsonDocument doc = value.AsBsonDocument;
                    if (doc.ElementCount == 0)
                    {
                        return "new Document()";
                    }
                    var b = new StringBuilder("new Document(").Append(Escape(doc.GetElement(0).Name)).Append(", ").Append(Lit(doc[0], level + 1)).Append(')');
                    for (int i = 1; i < doc.ElementCount; i++)
                    {
                        BsonElement e = doc.GetElement(i);
                        b.Append('\n').Append(Pad(level + 1)).Append(".append(").Append(Escape(e.Name)).Append(", ").Append(Lit(e.Value, level + 1)).Append(')');
                    }
                    return b.ToString();
                }
                case BsonType.Array:
                    Imports.Add("java.util.Arrays");
                    return "Arrays.asList(" + string.Join(", ", value.AsBsonArray.Select(v => Lit(v, level + 1))) + ")";
                case BsonType.String:
                    return Escape(value.AsString);
                case BsonType.Int32:
                    return value.AsInt32.ToString(CultureInfo.InvariantCulture);
                case BsonType.Int64:
                    return value.AsInt64.ToString(CultureInfo.InvariantCulture) + "L";
                case BsonType.Double:
                    return Number(value.AsDouble);
                case BsonType.Decimal128:
                    Imports.Add("java.math.BigDecimal");
                    Imports.Add("org.bson.types.Decimal128");
                    return $"new Decimal128(new BigDecimal({Escape(value.AsDecimal128.ToString())}))";
                case BsonType.Boolean:
                    return value.AsBoolean ? "true" : "false";
                case BsonType.Null:
                    return "null";
                case BsonType.ObjectId:
                    Imports.Add("org.bson.types.ObjectId");
                    return $"new ObjectId({Escape(value.AsObjectId.ToString())})";
                case BsonType.DateTime:
                    Imports.Add("java.time.Instant");
                    Imports.Add("java.util.Date");
                    return $"Date.from(Instant.parse({Escape(IsoString(value))}))";
                case BsonType.RegularExpression:
                    Imports.Add("org.bson.BsonRegularExpression");
                    return $"new BsonRegularExpression({Escape(value.AsBsonRegularExpression.Pattern)}, {Escape(value.AsBsonRegularExpression.Options)})";
                case BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard:
                    Imports.Add("java.util.UUID");
                    return $"UUID.fromString({Escape(value.AsBsonBinaryData.ToGuid().ToString())})";
                default:
                    return Escape(value.ToString() ?? "");
            }
        }
    }

    private static bool IsContainer(BsonValue value) => value.BsonType is BsonType.Document or BsonType.Array;
}
