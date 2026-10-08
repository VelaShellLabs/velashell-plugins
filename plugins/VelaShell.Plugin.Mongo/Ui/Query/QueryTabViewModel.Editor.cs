using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

internal sealed partial class QueryTabViewModel
{
    private readonly DispatcherTimer _diagnosticTimer;
    private readonly Dictionary<string, Task<IReadOnlyList<CollectionInfo>>> _collections = [with(StringComparer.Ordinal)];
    private readonly Dictionary<(string, string), Task<SampledSchema?>> _schemas = [];
    private readonly Dictionary<(string, string), Task<IReadOnlyList<string>>> _indexNames = [];

    /// <summary>当前脚本切出的语句(code lens 按它摆)。</summary>
    public IReadOnlyList<ShellStatement> Statements { get; private set; } = [];

    /// <summary>诊断(波浪线、行号旁红点、行尾提示)。</summary>
    public IReadOnlyList<EditorDiagnostic> Diagnostics
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(ProblemCount));
                RaisePropertyChanged(nameof(HasProblems));
                RaisePropertyChanged(nameof(ProblemText));
            }
        }
    } = [];

    /// <summary>问题数。</summary>
    public int ProblemCount => Diagnostics.Count;

    /// <summary>有问题。</summary>
    public bool HasProblems => Diagnostics.Count > 0;

    /// <summary>状态条右侧的 <c>1 个问题</c>。</summary>
    public string ProblemText => Loc.Format("Query_Problems", Diagnostics.Count);

    /// <summary>补全来源(绑给 <c>CodeEditor.CompletionProvider</c>)。</summary>
    public Func<CompletionRequest, Task<CompletionSet?>> CompletionProvider => field ??= ProvideCompletionAsync;

    /// <summary>光标换了语句时也要重算诊断(光标所在的那条不报"还没写完"的解析错误)。</summary>
    internal void OnCaretSettled()
    {
        _diagnosticTimer.Stop();
        _diagnosticTimer.Start();
    }

    /// <summary>重切语句、重算诊断。</summary>
    private void Analyze()
    {
        Statements = ShellScript.Split(_text);
        var diagnostics = new List<EditorDiagnostic>();
        foreach (ShellStatement statement in Statements)
        {
            IReadOnlyList<ShellDiagnostic> found = ShellJson.Diagnose(statement.Text);
            foreach (ShellDiagnostic d in found)
            {
                diagnostics.Add(new EditorDiagnostic(statement.Offset + d.Offset, d.Length, Loc.Format(d.MessageKey, d.Argument),
                    DiagnosticSeverity.Error, d.Fix, d.Fix is null ? null : Loc["Query_FixHint"]));
            }
            // 解析错误只报光标不在的语句:正在打的那条还没写完,每敲一个字都标红只会添乱。
            if (found.Count == 0 && !statement.Contains(CaretOffset)
                && !ShellParser.TryParse(statement, out _, out ShellParseException? error) && error is not null)
            {
                diagnostics.Add(new EditorDiagnostic(statement.Offset + Math.Min(error.Offset, Math.Max(0, statement.Length - 1)),
                    Math.Min(error.Length, Math.Max(1, statement.Length - error.Offset)), Describe(error)));
            }
        }
        Diagnostics = diagnostics;
        RaisePropertyChanged(nameof(Statements));
        _ = CheckCollectionsAsync(Statements, diagnostics);
    }

    /// <summary>Alt+↵:应用光标所在行的快捷修复(没有就应用全文唯一的那一个)。</summary>
    internal bool ApplyQuickFix()
    {
        List<EditorDiagnostic> fixable = [.. Diagnostics.Where(static d => d.FixText is not null)];
        if (fixable.Count == 0)
        {
            return false;
        }
        int lineStart = _text.LastIndexOf('\n', Math.Clamp(CaretOffset - 1, 0, Math.Max(0, _text.Length - 1))) + 1;
        int lineEnd = _text.IndexOf('\n', Math.Min(CaretOffset, _text.Length));
        lineEnd = lineEnd < 0 ? _text.Length : lineEnd;
        EditorDiagnostic? target = fixable.FirstOrDefault(d => d.Offset >= lineStart && d.Offset <= lineEnd)
                                   ?? (fixable.Count == 1 ? fixable[0] : null);
        if (target is null)
        {
            return false;
        }
        ReplaceText(target.Offset, target.Length, target.FixText!, literal: true);
        return true;
    }

    /// <summary>格式化全文。</summary>
    private void Format()
    {
        string formatted = ShellFormatter.Format(_text);
        if (formatted != _text)
        {
            ReplaceText(0, _text.Length, formatted, literal: true);
        }
    }

    /// <summary>改文本:有编辑器就走编辑器(进撤销栈、光标不乱跳),没有就直接改属性。</summary>
    private void ReplaceText(int offset, int length, string text, bool literal = false)
    {
        if (Editor is { } editor)
        {
            editor.Replace(offset, length, text, literal);
            return;
        }
        int caret = literal ? -1 : text.IndexOf('|', StringComparison.Ordinal);
        string clean = caret >= 0 ? text.Remove(caret, 1) : text;
        Text = _text.Remove(offset, Math.Min(length, _text.Length - offset)).Insert(offset, clean);
    }

    // ── 补全 ────────────────────────────────────────────────────────────────

    /// <summary>补全(设计稿 03 的双栏弹层)。</summary>
    internal async Task<CompletionSet?> ProvideCompletionAsync(CompletionRequest request)
    {
        CompletionContext context = ShellCompletion.Analyze(request.Text, request.CaretOffset);
        if (context.Slot == CompletionSlot.None)
        {
            return null;
        }
        string database = context.Database ?? DatabaseAt(request.Text, request.CaretOffset);
        bool zh = Loc.IsChinese;
        var items = new List<CompletionItem>();
        switch (context.Slot)
        {
            case CompletionSlot.Statement:
                items.AddRange(Keywords());
                break;
            case CompletionSlot.DbMember:
                foreach (CollectionInfo info in await CollectionsAsync(database).ConfigureAwait(true))
                {
                    items.Add(new CompletionItem
                    {
                        Label = info.Name,
                        InsertText = System.Text.RegularExpressions.Regex.IsMatch(info.Name, "^[A-Za-z_$][A-Za-z0-9_$]*$") ? info.Name : $"getCollection({BsonText.Quote(info.Name)})",
                        IconKey = info.Kind == CollectionKind.View ? "Mongo.eye" : "Mongo.table-2",
                        IconToken = "VelaInfo",
                        Category = info.Kind == CollectionKind.View ? Loc["Query_CatView"] : Loc["Query_CatCollection"]
                    });
                }
                items.AddRange(DatabaseMethodItems());
                break;
            case CompletionSlot.CollectionMember:
                items.AddRange(MongoVocabulary.Methods.Select(e => MongoVocabulary.ToCompletion(e, zh)));
                break;
            case CompletionSlot.CursorMember:
                items.AddRange(MongoVocabulary.CursorMethods
                    .Where(e => context.Method != "aggregate" || e.Name is "toArray" or "explain" or "comment" or "maxTimeMS" or "hint")
                    .Select(e => MongoVocabulary.ToCompletion(e, zh)));
                break;
            case CompletionSlot.StringArgument:
                items.AddRange(await StringArgumentItemsAsync(context, database).ConfigureAwait(true));
                break;
            case CompletionSlot.FieldPathString:
                foreach ((string path, string category, string token) in await FieldPathsAsync(context, database).ConfigureAwait(true))
                {
                    items.Add(new CompletionItem
                    {
                        Label = "$" + path,
                        InsertText = "$" + path + (context.QuoteClosed ? "" : "\""),
                        IconKey = "Mongo.variable",
                        IconToken = token,
                        Category = category
                    });
                }
                break;
            case CompletionSlot.Key:
                items.AddRange(await KeyItemsAsync(context, database).ConfigureAwait(true));
                break;
            case CompletionSlot.Value:
                items.AddRange(await ValueItemsAsync(context, database).ConfigureAwait(true));
                break;
        }
        return items.Count == 0
            ? null
            : new CompletionSet
            {
                Items = items,
                ReplaceOffset = context.ReplaceOffset,
                ReplaceLength = context.ReplaceLength,
                Footer = Loc["Query_CompletionFooter"]
            };
    }

    private IEnumerable<CompletionItem> Keywords() =>
    [
        Keyword("db", "db.", Loc["Query_KwDb"]),
        Keyword("use", "use(\"|\")", Loc["Query_KwUse"]),
        Keyword("show dbs", "show dbs", Loc["Query_KwShowDbs"]),
        Keyword("show collections", "show collections", Loc["Query_KwShowCollections"])
    ];

    private CompletionItem Keyword(string label, string insert, string description) => new()
    {
        Label = label,
        InsertText = insert,
        IconKey = "Mongo.square-terminal",
        IconToken = "VelaAccent",
        Category = Loc["Query_CatKeyword"],
        Description = description
    };

    private IEnumerable<CompletionItem> DatabaseMethodItems()
    {
        (string Name, string Insert, string Key)[] methods =
        [
            ("getCollection", "getCollection(\"|\")", "Query_DbGetCollection"),
            ("getCollectionNames", "getCollectionNames()", "Query_DbGetCollectionNames"),
            ("getSiblingDB", "getSiblingDB(\"|\")", "Query_DbGetSiblingDb"),
            ("runCommand", "runCommand({ | })", "Query_DbRunCommand"),
            ("adminCommand", "adminCommand({ | })", "Query_DbAdminCommand"),
            ("stats", "stats()", "Query_DbStats"),
            ("createCollection", "createCollection(\"|\")", "Query_DbCreateCollection"),
            ("aggregate", "aggregate([\n  { | }\n])", "Query_DbAggregate")
        ];
        return methods.Select(m => new CompletionItem
        {
            Label = m.Name,
            InsertText = m.Insert,
            IconKey = "Mongo.parentheses",
            IconToken = "VelaShellYellow",
            Category = Loc["Query_CatDbMethod"],
            Badge = Loc["Query_CatDbMethod"],
            Description = Loc[m.Key],
            Syntax = "db." + m.Name + "(…)"
        });
    }

    private async Task<IEnumerable<CompletionItem>> StringArgumentItemsAsync(CompletionContext context, string database)
    {
        string close = context.QuoteClosed ? "" : "\"";
        switch (context.StringCall)
        {
            case "getCollection":
                return (await CollectionsAsync(database).ConfigureAwait(true)).Select(c => new CompletionItem
                {
                    Label = c.Name,
                    InsertText = c.Name + close,
                    IconKey = "Mongo.table-2",
                    IconToken = "VelaInfo",
                    Category = Loc["Query_CatCollection"]
                });
            case "use" or "getSiblingDB":
                return Databases.Select(d => new CompletionItem
                {
                    Label = d,
                    InsertText = d + close,
                    IconKey = "Mongo.database",
                    IconToken = "VelaWarning",
                    Category = Loc["Query_CatDatabase"]
                });
            case "hint" or "dropIndex" when context.Collection is { } collection:
                return (await IndexNamesAsync(context.Database ?? database, collection).ConfigureAwait(true)).Select(n => new CompletionItem
                {
                    Label = n,
                    InsertText = n + close,
                    IconKey = "Mongo.key-round",
                    IconToken = "VelaStatusConnected",
                    Category = Loc["Query_CatIndex"]
                });
            case "distinct" when context.Collection is { } collection:
                SampledSchema? schema = await SchemaAsync(database, collection).ConfigureAwait(true);
                return schema is null ? [] : schema.Fields.Select(f => new CompletionItem
                {
                    Label = f.Path,
                    InsertText = f.Path + close,
                    IconKey = "Mongo.variable",
                    IconToken = f.ColorToken,
                    Category = Loc["Query_FieldSampled"],
                    Badge = f.TypeText,
                    Description = Loc.Format("Query_FieldInfo", f.TypeText, f.RatioText)
                });
            default:
                return [];
        }
    }

    /// <summary>键位置:按对象角色给字段、运算符、阶段名或选项名。</summary>
    private async Task<IEnumerable<CompletionItem>> KeyItemsAsync(CompletionContext context, string database)
    {
        bool zh = Loc.IsChinese;
        bool dollar = context.Prefix.StartsWith('$');
        var items = new List<CompletionItem>();
        switch (context.Role)
        {
            case ObjectRole.Stage:
                {
                    (PipelineShape? shape, _) = await UpstreamAsync(context, database).ConfigureAwait(true);
                    string? chipsTitle = shape is null ? null : Loc.Format("Query_UpstreamFields", shape.Source);
                    IReadOnlyList<string> chips = shape?.Fields ?? [];
                    foreach (VocabularyEntry entry in MongoVocabulary.Stages)
                    {
                        items.Add(WithChips(MongoVocabulary.ToCompletion(entry, zh), chipsTitle, chips));
                    }
                    items.AddRange(StageSnippets(chipsTitle, chips));
                    if (!dollar && shape is not null)
                    {
                        items.AddRange(await UpstreamItemsAsync(context, database, keyInsert: true).ConfigureAwait(true));
                    }
                    break;
                }
            case ObjectRole.Filter:
                if (!dollar)
                {
                    items.AddRange(await FieldItemsAsync(context, database).ConfigureAwait(true));
                }
                items.AddRange(MongoVocabulary.QueryOperators
                    .Where(static e => e.Name is "$and" or "$or" or "$nor" or "$expr" or "$text" or "$jsonSchema")
                    .Select(e => MongoVocabulary.ToCompletion(e, zh)));
                break;
            case ObjectRole.Operator:
                items.AddRange(MongoVocabulary.QueryOperators
                    .Where(static e => e.Name is not ("$and" or "$or" or "$nor" or "$expr" or "$text" or "$jsonSchema"))
                    .Select(e => MongoVocabulary.ToCompletion(e, zh)));
                break;
            case ObjectRole.Update:
                items.AddRange(MongoVocabulary.UpdateOperators.Select(e => MongoVocabulary.ToCompletion(e, zh)));
                break;
            case ObjectRole.UpdateFields or ObjectRole.Sort or ObjectRole.IndexKeys or ObjectRole.Document:
                items.AddRange(await FieldItemsAsync(context, database).ConfigureAwait(true));
                break;
            case ObjectRole.Projection:
                if (dollar)
                {
                    items.AddRange(MongoVocabulary.Expressions.Select(e => MongoVocabulary.ToCompletion(e, zh)));
                }
                else
                {
                    items.AddRange(await FieldItemsAsync(context, database).ConfigureAwait(true));
                }
                break;
            case ObjectRole.Group:
                items.Add(new CompletionItem
                {
                    Label = "_id",
                    InsertText = "_id: \"$|\"",
                    IconKey = "Mongo.variable",
                    IconToken = "VelaShellBlue",
                    Category = Loc["Query_CatGroupKey"],
                    Description = Loc["Query_GroupIdHint"]
                });
                break;
            case ObjectRole.Accumulator or ObjectRole.Expression:
                IEnumerable<VocabularyEntry> expressions = context.Role == ObjectRole.Accumulator
                    ? MongoVocabulary.Expressions.OrderBy(static e => e.Name is "$sum" or "$avg" or "$min" or "$max" or "$first" or "$last" or "$push" or "$addToSet" or "$count" ? 0 : 1)
                    : MongoVocabulary.Expressions;
                items.AddRange(expressions.Select(e => MongoVocabulary.ToCompletion(e, zh)));
                break;
            case ObjectRole.Options:
                items.AddRange(OptionItems(context));
                break;
        }
        return items;
    }

    /// <summary>值位置:常量与片段;管道数组里直接给带花括号的阶段。</summary>
    private async Task<IEnumerable<CompletionItem>> ValueItemsAsync(CompletionContext context, string database)
    {
        bool zh = Loc.IsChinese;
        switch (context.Role)
        {
            case ObjectRole.Pipeline:
                {
                    (PipelineShape? shape, _) = await UpstreamAsync(context, database).ConfigureAwait(true);
                    string? chipsTitle = shape is null ? null : Loc.Format("Query_UpstreamFields", shape.Source);
                    return MongoVocabulary.Stages.Select(e =>
                    {
                        CompletionItem item = MongoVocabulary.ToCompletion(e, zh);
                        return WithChips(Clone(item, "{ " + (item.InsertText ?? item.Label) + " }"), chipsTitle, shape?.Fields ?? []);
                    });
                }
            case ObjectRole.Sort:
                return [Constant("-1", Loc["Query_ValDesc"]), Constant("1", Loc["Query_ValAsc"])];
            case ObjectRole.IndexKeys:
                return [Constant("1", Loc["Query_ValAsc"]), Constant("-1", Loc["Query_ValDesc"]), Constant("\"text\"", "text"), Constant("\"2dsphere\"", "2dsphere"), Constant("\"hashed\"", "hashed")];
            case ObjectRole.Projection when !context.Prefix.StartsWith('$'):
                return [Constant("1", Loc["Query_ValInclude"]), Constant("0", Loc["Query_ValExclude"]),
                    .. (await FieldPathsAsync(context, database).ConfigureAwait(true)).Select(p => Constant($"\"${p.Path}\"", p.Category))];
            case ObjectRole.Group or ObjectRole.Accumulator or ObjectRole.Expression or ObjectRole.Projection:
                {
                    var items = new List<CompletionItem>();
                    foreach ((string path, string category, string token) in await FieldPathsAsync(context, database).ConfigureAwait(true))
                    {
                        items.Add(new CompletionItem { Label = $"\"${path}\"", IconKey = "Mongo.variable", IconToken = token, Category = category });
                    }
                    if (context.Role != ObjectRole.Group || context.FieldKey != "_id")
                    {
                        items.AddRange(MongoVocabulary.Expressions.Select(e =>
                        {
                            CompletionItem item = MongoVocabulary.ToCompletion(e, zh);
                            return Clone(item, "{ " + (item.InsertText ?? item.Label) + " }");
                        }));
                    }
                    items.Add(Constant("null", "null"));
                    return items;
                }
            case ObjectRole.Filter or ObjectRole.Operator or ObjectRole.ValueList or ObjectRole.UpdateFields or ObjectRole.Document:
                return ValueSnippets(context.Role is ObjectRole.Filter);
            default:
                return context.Prefix.Length > 0 ? [Constant("true", "Boolean"), Constant("false", "Boolean"), Constant("null", "Null")] : [];
        }
    }

    private IEnumerable<CompletionItem> ValueSnippets(bool operators)
    {
        var items = new List<CompletionItem>();
        if (operators)
        {
            items.Add(Snippet("{ $gte: … }", "{ $gte: | }", Loc["Query_ValRange"]));
            items.Add(Snippet("{ $in: [ … ] }", "{ $in: [ | ] }", Loc["Query_ValIn"]));
            items.Add(Snippet("{ $exists: true }", "{ $exists: true }|", Loc["Query_ValExists"]));
            items.Add(Snippet("{ $regex: /…/i }", "{ $regex: /|/i }", Loc["Query_ValRegex"]));
        }
        items.Add(Snippet("ObjectId(\"…\")", "ObjectId(\"|\")", "ObjectId"));
        items.Add(Snippet("ISODate(\"…\")", $"ISODate(\"{DateTime.UtcNow:yyyy-MM-dd}|\")", "Date"));
        items.Add(Snippet("NumberDecimal(\"…\")", "NumberDecimal(\"|\")", "Decimal128"));
        items.Add(Constant("true", "Boolean"));
        items.Add(Constant("false", "Boolean"));
        items.Add(Constant("null", "Null"));
        return items;
    }

    private IEnumerable<CompletionItem> StageSnippets(string? chipsTitle, IReadOnlyList<string> chips) =>
    [
        WithChips(Snippet(Loc["Query_SnipSortDesc"], "$sort: { |: -1 }", Loc["Query_SnipSortDescHint"], "{ $sort: { <字段>: -1 } }"), chipsTitle, chips),
        WithChips(Snippet(Loc["Query_SnipMatchEq"], "$match: { |: \"\" }", Loc["Query_SnipMatchEqHint"], "{ $match: { <字段>: <值> } }"), chipsTitle, chips),
        WithChips(Snippet(Loc["Query_SnipGroupCount"], "$group: { _id: \"$|\", count: { $sum: 1 } }", Loc["Query_SnipGroupCountHint"],
            "{ $group: { _id: \"$<字段>\", count: { $sum: 1 } } }"), chipsTitle, chips)
    ];

    private IEnumerable<CompletionItem> OptionItems(CompletionContext context)
    {
        string[] names = context.StageName switch
        {
            "$lookup" => ["from", "localField", "foreignField", "as", "let", "pipeline"],
            "$unwind" => ["path", "includeArrayIndex", "preserveNullAndEmptyArrays"],
            "$sample" => ["size"],
            "$bucket" => ["groupBy", "boundaries", "default", "output"],
            "$bucketAuto" => ["groupBy", "buckets", "output", "granularity"],
            "$merge" => ["into", "on", "whenMatched", "whenNotMatched"],
            "$unionWith" => ["coll", "pipeline"],
            "$graphLookup" => ["from", "startWith", "connectFromField", "connectToField", "as", "maxDepth", "depthField"],
            null when context.Method is "aggregate" => ["allowDiskUse", "maxTimeMS", "hint", "collation", "comment", "batchSize", "let"],
            null when context.Method is "updateOne" or "updateMany" or "replaceOne" => ["upsert", "arrayFilters", "hint", "collation"],
            null when context.Method is "findOneAndUpdate" or "findOneAndReplace" => ["returnNewDocument", "upsert", "projection", "sort"],
            null when context.Method is "createIndex" => ["name", "unique", "sparse", "expireAfterSeconds", "partialFilterExpression", "collation"],
            null when context.Method is "insertMany" or "bulkWrite" => ["ordered"],
            _ => []
        };
        return names.Select(n => new CompletionItem
        {
            Label = n,
            InsertText = n + ": |",
            IconKey = "Mongo.settings-2",
            IconToken = "VelaTextTertiary",
            Category = Loc["Query_CatOption"]
        });
    }

    private CompletionItem Snippet(string label, string insert, string description, string? syntax = null) => new()
    {
        Label = label,
        InsertText = insert,
        IconKey = "Mongo.square-code",
        IconToken = "VelaWarning",
        Category = Loc["Query_CatSnippet"],
        Badge = Loc["Query_CatSnippet"],
        Description = description,
        Syntax = syntax
    };

    private CompletionItem Constant(string label, string category) => new()
    {
        Label = label,
        IconKey = "Mongo.variable",
        IconToken = "VelaTextTertiary",
        Category = category
    };

    private static CompletionItem Clone(CompletionItem item, string insert) => new()
    {
        Label = item.Label,
        InsertText = insert,
        IconKey = item.IconKey,
        IconToken = item.IconToken,
        Category = item.Category,
        Badge = item.Badge,
        Description = item.Description,
        Syntax = item.Syntax,
        DocsUrl = item.DocsUrl,
        ChipsTitle = item.ChipsTitle,
        Chips = item.Chips
    };

    private static CompletionItem WithChips(CompletionItem item, string? title, IReadOnlyList<string> chips) => title is null || chips.Count == 0
        ? item
        : new CompletionItem
        {
            Label = item.Label,
            InsertText = item.InsertText,
            IconKey = item.IconKey,
            IconToken = item.IconToken,
            Category = item.Category,
            Badge = item.Badge,
            Description = item.Description,
            Syntax = item.Syntax,
            DocsUrl = item.DocsUrl,
            ChipsTitle = title,
            Chips = chips
        };

    /// <summary>字段键补全:管道里经过重塑的,给上游输出字段;否则给集合抽样字段。</summary>
    private async Task<IEnumerable<CompletionItem>> FieldItemsAsync(CompletionContext context, string database)
    {
        if (context.InPipeline)
        {
            (PipelineShape? shape, _) = await UpstreamAsync(context, database).ConfigureAwait(true);
            if (shape is not null)
            {
                return await UpstreamItemsAsync(context, database, keyInsert: true).ConfigureAwait(true);
            }
        }
        if (context.Collection is not { } collection || await SchemaAsync(database, collection).ConfigureAwait(true) is not { } schema)
        {
            return [];
        }
        return schema.Fields.Select(f => new CompletionItem
        {
            Label = f.Path,
            InsertText = KeyInsert(f.Path, context),
            IconKey = "Mongo.variable",
            IconToken = f.ColorToken,
            Category = Loc["Query_FieldSampled"],
            Badge = f.TypeText,
            Description = Loc.Format("Query_FieldInfo", f.TypeText, f.RatioText),
            Syntax = Loc.Format("Query_FieldSampleOf", collection, BsonText.Grouped(schema.Sampled))
        });
    }

    private async Task<IEnumerable<CompletionItem>> UpstreamItemsAsync(CompletionContext context, string database, bool keyInsert)
    {
        (PipelineShape? shape, IReadOnlyDictionary<string, BsonKind> types) = await UpstreamAsync(context, database).ConfigureAwait(true);
        if (shape is null)
        {
            return [];
        }
        return shape.Fields.Select(f =>
        {
            BsonKind kind = types.GetValueOrDefault(f, BsonKind.Missing);
            return new CompletionItem
            {
                Label = f,
                InsertText = keyInsert ? KeyInsert(f, context) : f,
                IconKey = "Mongo.variable",
                IconToken = kind == BsonKind.Missing ? "VelaWarning" : BsonKinds.ColorToken(kind),
                Category = kind == BsonKind.Missing ? Loc["Query_UpstreamField"] : $"{Loc["Query_UpstreamField"]} · {Short(kind)}",
                Description = Loc.Format("Query_UpstreamFieldHint", shape.Source)
            };
        });
    }

    private static string Short(BsonKind kind) => kind == BsonKind.Decimal128 ? "Decimal" : BsonKinds.Name(kind);

    /// <summary><c>$字段路径</c> 的候选:管道里经过重塑的取上游字段,否则取抽样字段。</summary>
    private async Task<IReadOnlyList<(string Path, string Category, string Token)>> FieldPathsAsync(CompletionContext context, string database)
    {
        if (context.InPipeline)
        {
            (PipelineShape? shape, IReadOnlyDictionary<string, BsonKind> types) = await UpstreamAsync(context, database).ConfigureAwait(true);
            if (shape is not null)
            {
                return [.. shape.Fields.Select(f => (f, Loc["Query_UpstreamField"], types.TryGetValue(f, out BsonKind k) ? BsonKinds.ColorToken(k) : "VelaWarning"))];
            }
        }
        if (context.Collection is not { } collection || await SchemaAsync(database, collection).ConfigureAwait(true) is not { } schema)
        {
            return [];
        }
        return [.. schema.Fields.Select(f => (f.Path, Loc["Query_FieldSampled"], f.ColorToken))];
    }

    private static string KeyInsert(string path, CompletionContext context) => context.Quoted
        ? path + (context.QuoteClosed ? "" : "\": |")
        : BsonText.FieldName(path) + ": |";

    /// <summary>
    /// 当前阶段的上游形状,外加能推断出的字段类型($group 的 <c>$sum: "$total"</c> → total 的抽样类型;<c>$sum: 1</c> → Int32)。
    /// </summary>
    private async Task<(PipelineShape? Shape, IReadOnlyDictionary<string, BsonKind> Types)> UpstreamAsync(CompletionContext context, string database)
    {
        PipelineShape? shape = ShellCompletion.Upstream(context.PrecedingStages);
        var types = new Dictionary<string, BsonKind>(StringComparer.Ordinal);
        if (shape is null)
        {
            return (null, types);
        }
        SampledSchema? schema = context.Collection is { } collection ? await SchemaAsync(database, collection).ConfigureAwait(true) : null;
        var group = context.PrecedingStages
            .Select(static t => ShellJson.TryParseDocument(t, out BsonDocument d, out _) ? d : null)
            .LastOrDefault(static d => d?.Contains("$group") == true)?["$group"] as BsonDocument;
        if (shape.Source == "$group" && group is not null)
        {
            foreach (BsonElement element in group)
            {
                types[element.Name] = element.Name == "_id"
                    ? Infer(element.Value, schema)
                    : element.Value is BsonDocument { ElementCount: > 0 } accumulator ? Infer(accumulator.GetElement(0), schema) : BsonKind.Missing;
            }
        }
        return (shape, types);
    }

    private static BsonKind Infer(BsonElement accumulator, SampledSchema? schema) => accumulator.Name switch
    {
        "$avg" => BsonKind.Double,
        "$push" or "$addToSet" => BsonKind.Array,
        "$count" => BsonKind.Int32,
        "$sum" when accumulator.Value.IsNumeric => BsonKinds.Of(accumulator.Value),
        _ => Infer(accumulator.Value, schema)
    };

    private static BsonKind Infer(BsonValue value, SampledSchema? schema) => value switch
    {
        BsonString { Value: var path } when path.StartsWith('$') => schema?.Find(path[1..])?.Kind ?? BsonKind.Missing,
        BsonDocument => BsonKind.Object,
        _ => BsonKinds.Of(value)
    };

    // ── 元数据缓存 ──────────────────────────────────────────────────────────

    /// <summary>某个库的集合(先用对象树已知的,没展开过就自己列一次;缓存)。</summary>
    internal Task<IReadOnlyList<CollectionInfo>> CollectionsAsync(string database)
    {
        if (_collections.TryGetValue(database, out Task<IReadOnlyList<CollectionInfo>>? cached))
        {
            return cached;
        }
        IReadOnlyList<CollectionInfo> known = Workspace.CollectionsOf(database);
        Task<IReadOnlyList<CollectionInfo>> task = known.Count > 0 ? Task.FromResult(known) : ListCollectionsAsync(database);
        _collections[database] = task;
        return task;
    }

    private async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(string database)
    {
        try
        {
            return await Workspace.Connection.ListCollectionsAsync(database).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            _ = _collections.Remove(database);
            return [];
        }
    }

    /// <summary>对一个集合抽样(<c>$sample</c>,大小取连接设置;缓存到标签关闭)。</summary>
    internal Task<SampledSchema?> SchemaAsync(string database, string collection)
    {
        if (_schemas.TryGetValue((database, collection), out Task<SampledSchema?>? cached))
        {
            return cached;
        }
        Task<SampledSchema?> task = SampleAsync(database, collection);
        _schemas[(database, collection)] = task;
        return task;
    }

    private async Task<SampledSchema?> SampleAsync(string database, string collection)
    {
        try
        {
            int size = Math.Clamp(Workspace.Connection.Settings.SampleSize, 10, 10_000);
            IMongoCollection<BsonDocument> target = Workspace.Connection.Collection(database, collection);
            List<BsonDocument> documents = await Task.Run(async () =>
            {
                using IAsyncCursor<BsonDocument> cursor = await target.AggregateAsync<BsonDocument>(
                    new BsonDocument[] { new("$sample", new BsonDocument("size", size)) },
                    new AggregateOptions { MaxTime = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
                return await cursor.ToListAsync().ConfigureAwait(false);
            }).ConfigureAwait(true);
            return SampledSchema.Build(database, collection, documents);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            _ = _schemas.Remove((database, collection));
            Workspace.Log.Info($"Sampling {database}.{collection} for completion failed: {ex.Message}");
            return null;
        }
    }

    private Task<IReadOnlyList<string>> IndexNamesAsync(string database, string collection)
    {
        if (_indexNames.TryGetValue((database, collection), out Task<IReadOnlyList<string>>? cached))
        {
            return cached;
        }
        Task<IReadOnlyList<string>> task = ListIndexNamesAsync(database, collection);
        _indexNames[(database, collection)] = task;
        return task;
    }

    private async Task<IReadOnlyList<string>> ListIndexNamesAsync(string database, string collection)
    {
        try
        {
            IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(database, collection).ConfigureAwait(true);
            return [.. indexes.Select(static i => i.GetValue("name", "").ToString() ?? "").Where(static n => n.Length > 0)];
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            _ = _indexNames.Remove((database, collection));
            return [];
        }
    }

    /// <summary>某段文本某个位置上生效的库(补全请求带的是编辑器里那一刻的文本)。</summary>
    private string DatabaseAt(string text, int offset)
    {
        string database = _database;
        foreach (ShellStatement statement in ShellScript.Split(text))
        {
            if (statement.Offset >= offset)
            {
                break;
            }
            if (statement.Text.StartsWith("use", StringComparison.Ordinal) && ShellParser.TryParse(statement, out ShellCommand? command, out _)
                && command is { Kind: ShellCommandKind.Use, Target: { } target })
            {
                database = target;
            }
        }
        return database;
    }
}
