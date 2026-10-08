using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;
using VelaShell.PluginSdk.Logging;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 聚合管道构建器(设计稿 04):阶段 ⇄ 文本的互逆、阶段体诊断、前缀预览、代码生成、写入路径与截图。
/// 写入测试只碰自己建的 <c>velashell_pipeline_*</c> 库,收尾删掉;shop 只读。
/// </summary>
[TestClass]
public sealed class PipelineTests
{
    /// <summary>设计稿的 6 个阶段(第 6 个 $lookup 停用,在文本里是一段注释)。</summary>
    internal const string DesignPipeline = """
        [
          {
            $match: {
              status: "paid",
              createdAt: { $gte: ISODate("2026-08-27") }
            }
          },
          { $unwind: { path: "$items" } },
          {
            $group: {
              _id: "$items.sku",
              qty: { $sum: "$items.qty" },
              revenue: { $sum: { $multiply: [ "$items.qty", "$items.price" ] } }
            }
          },
          { $sort: { revenue: -1 } },
          { $limit: 10 },
          // { $lookup: { from: "products", localField: "_id", foreignField: "sku", as: "product" } },
        ]
        """;

    // ── 阶段 ⇄ 文本 ─────────────────────────────────────────────────────

    [TestMethod]
    public void Text_parses_the_design_pipeline_with_the_disabled_lookup()
    {
        PipelineParse parse = PipelineText.Parse(DesignPipeline);

        Assert.IsTrue(parse.Ok, parse.ErrorKey ?? "");
        CollectionAssert.AreEqual(new[] { "$match", "$unwind", "$group", "$sort", "$limit", "$lookup" }, parse.Stages.Select(s => s.Operator).ToArray());
        CollectionAssert.AreEqual(new[] { true, true, true, true, true, false }, parse.Stages.Select(s => s.Enabled).ToArray());
        Assert.AreEqual("{\n  status: \"paid\",\n  createdAt: { $gte: ISODate(\"2026-08-27\") }\n}", parse.Stages[0].Body);
        Assert.AreEqual("{ path: \"$items\" }", parse.Stages[1].Body);
        Assert.AreEqual("10", parse.Stages[4].Body);
        Assert.AreEqual("{ from: \"products\", localField: \"_id\", foreignField: \"sku\", as: \"product\" }", parse.Stages[5].Body);
    }

    [TestMethod]
    public void Stages_to_text_and_back_round_trips_bodies_verbatim()
    {
        PipelineStageSpec[] stages =
        [
            new("$match", "{\n  status: \"paid\",\n  // 只看已付款\n  total: { $gt: NumberDecimal(\"100\") }\n}"),
            new("$unwind", "\"$items\""),
            new("$project", "{\n  sku: \"$items.sku\",\n  nested: {\n    a: 1\n  }\n}", Enabled: false),
            new("$limit", "5")
        ];

        string text = PipelineText.Format(stages);
        PipelineParse back = PipelineText.Parse(text);

        Assert.IsTrue(back.Ok, back.ErrorKey ?? "");
        CollectionAssert.AreEqual(stages, back.Stages.ToArray());
        // 再走一圈仍然一字不差 —— 两种模式之间来回切不会"被格式化"。
        Assert.AreEqual(text, PipelineText.Format(back.Stages));
    }

    [TestMethod]
    public void Text_errors_carry_a_key_and_a_position()
    {
        PipelineParse notArray = PipelineText.Parse("{ $match: {} }");
        Assert.AreEqual("Pipe_ErrNotArray", notArray.ErrorKey);
        Assert.AreEqual(0, notArray.ErrorOffset);

        PipelineParse unclosed = PipelineText.Parse("[ { $match: { a: 1 } }");
        Assert.AreEqual("Pipe_ErrUnclosed", unclosed.ErrorKey);

        const string twoOps = "[ { $match: { a: 1 }, $sort: { a: 1 } } ]";
        PipelineParse two = PipelineText.Parse(twoOps);
        Assert.AreEqual("Pipe_ErrOneOperator", two.ErrorKey);
        Assert.AreEqual(twoOps.IndexOf("$sort", StringComparison.Ordinal), two.ErrorOffset);

        const string badName = "[ { match: {} } ]";
        PipelineParse name = PipelineText.Parse(badName);
        Assert.AreEqual("Pipe_ErrStageName", name.ErrorKey);
        Assert.AreEqual("match", name.ErrorArgument);
        Assert.AreEqual(badName.IndexOf("match", StringComparison.Ordinal), name.ErrorOffset);

        const string dotted = "[\n  { $match: { a: 1 } },\n  {\n    $sort: {\n      items.qty: -1\n    }\n  }\n]";
        PipelineParse dot = PipelineText.Parse(dotted);
        Assert.AreEqual("Shell_DottedKey", dot.ErrorKey);
        Assert.AreEqual(dotted.IndexOf("items.qty", StringComparison.Ordinal), dot.ErrorOffset);
        Assert.AreEqual("items.qty".Length, dot.ErrorLength);

        PipelineParse trailing = PipelineText.Parse("[ { $limit: 1 } ] x");
        Assert.AreEqual("Pipe_ErrTrailing", trailing.ErrorKey);

        Assert.IsTrue(PipelineText.Parse("").Ok);
        Assert.IsTrue(PipelineText.Parse("// shop.orders\n[ { $limit: 1 }, ];").Ok);
    }

    [TestMethod]
    public void Body_parse_reports_syntax_errors_on_the_card()
    {
        PipelineBodyParse ok = PipelineText.ParseBody("{ revenue: -1 }");
        Assert.IsTrue(ok.Ok);
        Assert.AreEqual(new BsonDocument("revenue", -1), ok.Value);

        PipelineBodyParse unbalanced = PipelineText.ParseBody("{ a: { $gt: 1 }");
        Assert.AreEqual("Pipe_ErrUnbalanced", unbalanced.ErrorKey);
        Assert.AreEqual(0, unbalanced.Offset);

        PipelineBodyParse dotted = PipelineText.ParseBody("{ items.sku: 1 }");
        Assert.AreEqual("Shell_DottedKey", dotted.ErrorKey);
        Assert.AreEqual("\"items.sku\"", dotted.Fix);

        PipelineBodyParse garbage = PipelineText.ParseBody("{ a: }");
        Assert.AreEqual("Pipe_ErrSyntax", garbage.ErrorKey);
        Assert.IsTrue(garbage.Length > 0);

        Assert.AreEqual("Pipe_ErrEmptyBody", PipelineText.ParseBody("  ").ErrorKey);
    }

    [TestMethod]
    public void FormatBody_keeps_short_containers_inline_like_the_design()
    {
        var match = new BsonDocument
        {
            { "status", "paid" },
            { "createdAt", new BsonDocument("$gte", new BsonDateTime(new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc))) }
        };

        Assert.AreEqual("{\n  status: \"paid\",\n  createdAt: { $gte: ISODate(\"2026-08-27\") }\n}", PipelineText.FormatBody(match));
        Assert.AreEqual("{ path: \"$items\" }", PipelineText.FormatBody(new BsonDocument("path", "$items")));
        Assert.AreEqual("10", PipelineText.FormatBody(10));
        // 写出来的阶段体能原样解析回同一个值。
        Assert.AreEqual(match, PipelineText.ParseBody(PipelineText.FormatBody(match)).Value);
    }

    [TestMethod]
    public void Default_bodies_come_from_the_vocabulary()
    {
        Assert.AreEqual("{ path: \"$\" }", PipelineText.DefaultBody("$unwind"));
        Assert.AreEqual("{\n  \n}", PipelineText.DefaultBody("$match"));
        Assert.AreEqual("10", PipelineText.DefaultBody("$limit"));
        Assert.IsTrue(PipelineText.ParseBody(PipelineText.DefaultBody("$sample")).Ok);
    }

    [TestMethod]
    public void Inline_tokens_color_keys_operators_and_field_paths()
    {
        const string code = "{ revenue: { $sum: \"$items.qty\" }, at: ISODate(\"2026-08-27\"), n: -1, ok: true }";
        IReadOnlyList<PipelineToken> tokens = PipelineTokens.Tokenize(code);
        string Kind(string text) => tokens.First(t => code.Substring(t.Start, t.Length) == text).Kind.ToString();

        Assert.AreEqual(nameof(PipelineTokenKind.Key), Kind("revenue"));
        Assert.AreEqual(nameof(PipelineTokenKind.Operator), Kind("$sum"));
        Assert.AreEqual(nameof(PipelineTokenKind.FieldPath), Kind("\"$items.qty\""));
        Assert.AreEqual(nameof(PipelineTokenKind.Constructor), Kind("ISODate"));
        Assert.AreEqual(nameof(PipelineTokenKind.String), Kind("\"2026-08-27\""));
        Assert.AreEqual(nameof(PipelineTokenKind.Number), Kind("-1"));
        Assert.AreEqual(nameof(PipelineTokenKind.Bool), Kind("true"));
        // 记号首尾相接,拼回去就是原文。
        Assert.AreEqual(code, string.Concat(tokens.Select(t => code.Substring(t.Start, t.Length))));
        Assert.AreEqual("{ status: \"paid\", createdAt: { $gte: 1 } }", PipelineTokens.OneLine("{\n  status: \"paid\",\n  createdAt: { $gte: 1 }\n}"));
    }

    [TestMethod]
    public void Field_sampling_walks_nested_documents_and_arrays()
    {
        BsonDocument[] docs =
        [
            new() { { "_id", 1 }, { "items", new BsonArray { new BsonDocument { { "sku", "A" }, { "qty", 2 } } } }, { "customer", new BsonDocument("name", "x") } },
            new() { { "_id", 2 }, { "items", new BsonArray() } }
        ];

        IReadOnlyList<PipelineFieldSample> fields = PipelineFields.Sample(docs);
        string[] paths = [.. fields.Select(f => f.Path)];

        CollectionAssert.AreEqual(new[] { "_id", "items", "items.sku", "items.qty", "customer", "customer.name" }, paths);
        Assert.AreEqual(1.0, fields.Single(f => f.Path == "_id").Ratio);
        Assert.AreEqual(0.5, fields.Single(f => f.Path == "items.sku").Ratio);
        Assert.AreEqual(BsonKind.Array, fields.Single(f => f.Path == "items").Kind);
    }

    [TestMethod]
    public void Completion_offers_upstream_paths_inside_strings_and_operators_after_dollar()
    {
        var loc = new Loc("zh-CN");
        PipelineFieldSample[] upstream = [new("items.qty", BsonKind.Int32, 1), new("items.price", BsonKind.Decimal128, 1)];

        const string inString = "{ qty: { $sum: \"$it";
        CompletionSet? paths = PipelineFields.Complete(new(inString, inString.Length, false), "$group", upstream, 20, loc);
        Assert.IsNotNull(paths);
        Assert.AreEqual(inString.Length - 3, paths.ReplaceOffset);
        CollectionAssert.AreEqual(new[] { "$items.qty", "$items.price" }, paths.Items.Select(i => i.Label).ToArray());
        StringAssert.Contains(paths.Header ?? "", "20");

        const string op = "{ qty: { $su";
        CompletionSet? ops = PipelineFields.Complete(new(op, op.Length, false), "$group", upstream, 20, loc);
        Assert.IsNotNull(ops);
        Assert.IsTrue(ops.Items.Any(i => i.Label == "$sum"));
        // $group 里先给表达式(累加器),$match 里先给查询运算符。
        Assert.AreEqual("$sum", ops.Items[0].Label);
        CompletionSet? query = PipelineFields.Complete(new("{ a: { $", 8, false), "$match", upstream, 20, loc);
        Assert.AreEqual("$eq", query!.Items[0].Label);

        const string key = "{ ";
        CompletionSet? keys = PipelineFields.Complete(new(key, key.Length, true), "$sort", upstream, 20, loc);
        Assert.AreEqual("\"items.qty\": |", keys!.Items[0].InsertText);
    }

    // ── 代码生成 ────────────────────────────────────────────────────────

    private static List<BsonDocument> DesignStages()
    {
        PipelineParse parse = PipelineText.Parse(DesignPipeline);
        return [.. parse.Stages.Where(s => s.Enabled).Select(s => new BsonDocument(s.Operator, PipelineText.ParseBody(s.Body).Value!))];
    }

    [TestMethod]
    public void Mongosh_export_parses_back_to_the_same_pipeline()
    {
        List<BsonDocument> stages = DesignStages();
        string code = PipelineCode.Generate(PipelineCodeLanguage.Mongosh, "shop", "orders", stages, allowDiskUse: true);

        StringAssert.StartsWith(code, "use(\"shop\");");
        StringAssert.Contains(code, "db.orders.aggregate([");
        StringAssert.Contains(code, "ISODate(\"2026-08-27\")");
        StringAssert.Contains(code, "], { allowDiskUse: true });");
        int open = code.IndexOf('[', StringComparison.Ordinal);
        int close = code.LastIndexOf(']');
        PipelineParse back = PipelineText.Parse(code[open..(close + 1)]);
        Assert.IsTrue(back.Ok, back.ErrorKey ?? "");
        CollectionAssert.AreEqual(stages, back.Stages.Select(s => new BsonDocument(s.Operator, PipelineText.ParseBody(s.Body).Value!)).ToList());
    }

    [TestMethod]
    public void Driver_exports_write_typed_literals_per_language()
    {
        List<BsonDocument> stages = DesignStages();

        string cs = PipelineCode.Generate(PipelineCodeLanguage.CSharp, "shop", "orders", stages, allowDiskUse: true);
        StringAssert.Contains(cs, "new BsonDocument(\"$limit\", 10)");
        StringAssert.Contains(cs, "new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc)");
        StringAssert.Contains(cs, "new BsonDocument(\"$sort\", new BsonDocument(\"revenue\", -1))");
        StringAssert.Contains(cs, "new BsonArray { \"$items.qty\", \"$items.price\" }");
        StringAssert.Contains(cs, "AllowDiskUse = true");
        StringAssert.Contains(cs, "GetCollection<BsonDocument>(\"orders\")");

        string py = PipelineCode.Generate(PipelineCodeLanguage.Python, "shop", "orders", stages, allowDiskUse: true);
        StringAssert.Contains(py, "{\"$limit\": 10}");
        StringAssert.Contains(py, "datetime(2026, 8, 27, tzinfo=timezone.utc)");
        StringAssert.Contains(py, "from datetime import datetime, timezone");
        StringAssert.Contains(py, "allowDiskUse=True");

        string node = PipelineCode.Generate(PipelineCodeLanguage.Node, "shop", "orders", stages, allowDiskUse: false);
        StringAssert.Contains(node, "{ $limit: 10 }");
        StringAssert.Contains(node, "new Date(\"2026-08-27T00:00:00Z\")");
        Assert.IsFalse(node.Contains("allowDiskUse", StringComparison.Ordinal));
        StringAssert.Contains(node, "const { MongoClient } = require(\"mongodb\");");

        string java = PipelineCode.Generate(PipelineCodeLanguage.Java, "shop", "orders", stages, allowDiskUse: true);
        StringAssert.Contains(java, "new Document(\"$limit\", 10)");
        StringAssert.Contains(java, "Date.from(Instant.parse(\"2026-08-27T00:00:00Z\"))");
        StringAssert.Contains(java, "import java.time.Instant;");
        StringAssert.Contains(java, ".allowDiskUse(true)");
        StringAssert.Contains(java, ".append(\"createdAt\"");

        var typed = new BsonDocument("$match", new BsonDocument
        {
            { "_id", ObjectId.Parse("66f5c2a1b04e97d2a1b2c3d4") },
            { "total", new BsonDecimal128(Decimal128.Parse("8740.00")) },
            { "n", 5L },
            { "x", BsonNull.Value }
        });
        string typedPy = PipelineCode.Generate(PipelineCodeLanguage.Python, "shop", "orders", [typed], false);
        StringAssert.Contains(typedPy, "ObjectId(\"66f5c2a1b04e97d2a1b2c3d4\")");
        StringAssert.Contains(typedPy, "Decimal128(\"8740.00\")");
        StringAssert.Contains(typedPy, "Int64(5)");
        StringAssert.Contains(typedPy, "None");
        StringAssert.Contains(typedPy, "from bson import ObjectId, Decimal128, Int64");
        string typedCs = PipelineCode.Generate(PipelineCodeLanguage.CSharp, "shop", "orders", [typed], false);
        StringAssert.Contains(typedCs, "ObjectId.Parse(\"66f5c2a1b04e97d2a1b2c3d4\")");
        StringAssert.Contains(typedCs, "Decimal128.Parse(\"8740.00\")");
        StringAssert.Contains(typedCs, "5L");
        StringAssert.Contains(typedCs, "BsonNull.Value");
    }

    // ── 结果表 ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Disabled_lookup_becomes_a_ghost_column()
    {
        var loc = new Loc("zh-CN");
        List<(string, BsonValue?, bool)> stages =
        [
            ("$group", new BsonDocument("_id", "$items.sku"), true),
            ("$limit", 10, true),
            ("$lookup", new BsonDocument { { "from", "products" }, { "as", "product" } }, false)
        ];
        IReadOnlyList<PipelineGhost> ghosts = PipelineResults.Ghosts(stages, from => from == "products" ? "name" : null);
        Assert.AreEqual(new PipelineGhost("product.name", 3), ghosts.Single());

        BsonDocument[] docs = [new() { { "_id", "SKU-1182" }, { "qty", 312 }, { "revenue", new BsonDecimal128(Decimal128.Parse("405288.00")) } }];
        (IReadOnlyList<PipelineColumn> columns, IReadOnlyList<PipelineRow> rows) = PipelineResults.Build(docs, ghosts, loc);

        CollectionAssert.AreEqual(new[] { "_id", "qty", "revenue", "product.name" }, columns.Select(c => c.Name).ToArray());
        Assert.IsTrue(columns[1].IsNumeric);
        Assert.IsTrue(columns[3].IsGhost);
        Assert.AreEqual("\"SKU-1182\"", rows[0].Cells[0].Text);
        Assert.AreEqual("405,288.00", rows[0].Cells[2].Text);
        Assert.AreEqual("VelaShellGreen", rows[0].Cells[2].Token);
        Assert.AreEqual("(阶段 3 已停用)", rows[0].Cells[3].Text);

        // 停用阶段前面有启用的重塑阶段时不画提示列($group 之后 $addFields 的字段反正也到不了输出)。
        List<(string, BsonValue?, bool)> masked =
        [
            ("$addFields", new BsonDocument("x", 1), false),
            ("$group", new BsonDocument("_id", "$a"), true)
        ];
        Assert.AreEqual(0, PipelineResults.Ghosts(masked).Count);
    }

    [TestMethod]
    public void Mini_cards_summarise_like_the_design()
    {
        var doc = new BsonDocument
        {
            { "_id", ObjectId.Parse("66f5c2a1b04e97d2a1b2c3d4") },
            { "orderNo", "SO2609-10400" },
            { "items", new BsonArray { 1, 2, 3 } },
            { "total", new BsonDecimal128(Decimal128.Parse("1299.00")) },
            { "extra", 1 }
        };
        PipelineMiniDoc mini = PipelineResults.Mini(doc);

        Assert.AreEqual(4, mini.Fields.Count);
        Assert.AreEqual(new PipelineMiniField("_id:", "66f5c2a1b04e97d2…", "VelaShellBlue"), mini.Fields[0]);
        Assert.AreEqual(new PipelineMiniField("orderNo:", "\"SO2609-10400\"", "VelaShellCyan"), mini.Fields[1]);
        Assert.AreEqual(new PipelineMiniField("items:", "Array[3]", "VelaWarning"), mini.Fields[2]);
        Assert.AreEqual(new PipelineMiniField("total:", "1299.00", "VelaShellGreen"), mini.Fields[3]);
        Assert.AreEqual("64 ms", PipelineResults.Elapsed(TimeSpan.FromMilliseconds(64)));
        Assert.AreEqual("1.4 s", PipelineResults.Elapsed(TimeSpan.FromMilliseconds(1400)));
    }

    // ── 视图模型:阶段 ⇄ 文本同步 ─────────────────────────────────────────

    [TestMethod]
    public void View_model_keeps_cards_and_text_in_sync() => Screens.OnUi(async () =>
    {
        var workspace = new PipelineTestWorkspace();
        var vm = new PipelineTabViewModel(workspace, new CollectionInfo("shop", "orders", CollectionKind.Collection, []), null) { AutoPreview = false };

        Assert.AreEqual("管道 · orders", vm.Title);
        Assert.AreEqual("@shop", vm.Scope);
        Assert.AreEqual("pipeline:shop.orders", vm.Key);
        Assert.AreEqual("$match", vm.Stages.Single().Operator);
        Assert.IsFalse(vm.IsModified);
        StringAssert.Contains(vm.StatusText, "管道 · 未保存");

        Assert.IsTrue(vm.LoadFromText(DesignPipeline));
        Assert.AreEqual(6, vm.Stages.Count);
        Assert.IsTrue(vm.IsModified);
        Assert.AreEqual("6 个阶段 · 5 个启用", vm.StageSummary);
        Assert.AreEqual("关联集合 · 已停用", vm.Stages[5].Description);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, vm.Stages.Select(s => s.Index).ToArray());

        // 执行计划:交给查询标签 explain,停用的阶段不在里面。
        vm.ExplainCommand.Execute(null);
        (string explainDb, string? explainText, bool explainRun) = workspace.Queries.Single();
        Assert.AreEqual("shop", explainDb);
        StringAssert.StartsWith(explainText ?? "", "db.orders.aggregate([");
        StringAssert.EndsWith(explainText ?? "", "], { allowDiskUse: true }).explain(\"executionStats\")");
        Assert.IsFalse((explainText ?? "").Contains("$lookup", StringComparison.Ordinal));
        Assert.IsTrue(explainRun);

        // 导出为代码:自己的对话框,切语言即重新生成。
        vm.ExportCodeCommand.Execute(null);
        var code = (PipelineCodeDialogViewModel)workspace.Dialogs.Single();
        StringAssert.Contains(code.Code, "db.orders.aggregate([");
        code.IsPython = true;
        StringAssert.Contains(code.Code, "from pymongo import MongoClient");
        Assert.AreEqual("5 个启用阶段 · 停用的阶段不导出", code.Note);
        workspace.CloseDialog(code);

        // 卡片 → 文本:停用的阶段是注释。
        vm.IsTextMode = true;
        Assert.AreEqual(PipelineMode.Text, vm.Mode);
        StringAssert.Contains(vm.PipelineTextValue, "// { $lookup:");
        StringAssert.Contains(vm.PipelineTextValue, "{ $limit: 10 },");

        // 文本 → 卡片:改 $limit、取消注释 $lookup、删掉 $sort。
        vm.PipelineTextValue = vm.PipelineTextValue
            .Replace("{ $limit: 10 }", "{ $limit: 3 }", StringComparison.Ordinal)
            .Replace("// { $lookup:", "{ $lookup:", StringComparison.Ordinal)
            .Replace("  { $sort: { revenue: -1 } },\n", "", StringComparison.Ordinal);
        Assert.IsTrue(vm.ApplyText());
        CollectionAssert.AreEqual(new[] { "$match", "$unwind", "$group", "$limit", "$lookup" }, vm.Stages.Select(s => s.Operator).ToArray());
        Assert.AreEqual("3", vm.Stages[3].Body);
        Assert.IsTrue(vm.Stages[4].IsEnabled);

        // 解析失败:停在文本模式,卡片不动,标错。
        vm.PipelineTextValue = "[ { $match: { a: 1 }, $sort: {} } ]";
        vm.IsStagesMode = true;
        Assert.AreEqual(PipelineMode.Text, vm.Mode);
        Assert.IsTrue(vm.HasTextError);
        Assert.AreEqual(1, vm.TextDiagnostics.Count);
        Assert.AreEqual(5, vm.Stages.Count);
        Assert.IsTrue(workspace.Toasts.Any(t => t.Kind == ToastKind.Warning));

        // 修好就能回卡片。
        vm.PipelineTextValue = "[ { $match: { status: \"paid\" } }, { $limit: 5 } ]";
        vm.IsStagesMode = true;
        Assert.AreEqual(PipelineMode.Stages, vm.Mode);
        Assert.AreEqual(2, vm.Stages.Count);
        Assert.IsFalse(vm.HasTextError);

        // 卡片上的语法错误:标在卡片上,底栏计数。
        vm.Stages[0].Body = "{ status: }";
        Assert.IsTrue(vm.Stages[0].HasError);
        Assert.AreEqual("语法错误", vm.Stages[0].ErrorBadge);
        Assert.AreEqual(1, vm.ErrorCount);
        StringAssert.Contains(vm.StageSummary, "1 个有错");
        Assert.AreEqual(1, vm.Stages[0].Diagnostics.Count);

        // 换运算符:没动过的模板跟着换,写过的阶段体保留。
        PipelineStage added = vm.AddStage("$unwind");
        vm.ChangeOperator(added, "$count");
        Assert.AreEqual("\"\"", added.Body);
        vm.Stages[1].Operator = "$skip";
        Assert.AreEqual("5", vm.Stages[1].Body);

        // 拖动重排 / 复制 / 删除后序号重排。
        vm.MoveStageTo(added, 0);
        Assert.AreEqual("$count", vm.Stages[0].Operator);
        vm.DuplicateStage(vm.Stages[0]);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, vm.Stages.Select(s => s.Index).ToArray());
        vm.RemoveStage(vm.Stages[0]);
        Assert.AreEqual(3, vm.Stages.Count);
        await Task.CompletedTask;
    });

    [TestMethod]
    public void Pipeline_handed_in_becomes_cards() => Screens.OnUi(async () =>
    {
        var workspace = new PipelineTestWorkspace();
        var pipeline = new BsonArray
        {
            new BsonDocument("$match", new BsonDocument("status", "paid")),
            new BsonDocument("$limit", 10)
        };
        var vm = new PipelineTabViewModel(workspace, new CollectionInfo("shop", "v_x", CollectionKind.View, []), pipeline) { AutoPreview = false };

        CollectionAssert.AreEqual(new[] { "$match", "$limit" }, vm.Stages.Select(s => s.Operator).ToArray());
        Assert.AreEqual("{ status: \"paid\" }", vm.Stages[0].Body);
        Assert.IsTrue(vm.Stages[0].IsExpanded);
        Assert.IsFalse(vm.IsModified);
        Assert.AreEqual("Mongo.eye", vm.SourceIcon);
        Assert.AreEqual(
            "db.v_x.aggregate([\n  { $match: { status: \"paid\" } },\n  { $limit: 10 },\n], { allowDiskUse: true })",
            vm.AggregateCall());
        await Task.CompletedTask;
    });

    // ── 预览(打真实服务器)─────────────────────────────────────────────

    [TestMethod]
    public void Prefix_preview_runs_each_stage_on_the_sample() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using MongoConnection connection = await TestServer.OpenAsync();
        IMongoCollection<BsonDocument> orders = connection.Collection(Screens.Database, "orders");
        PipelineParse parse = PipelineText.Parse(DesignPipeline);
        List<(BsonDocument?, bool)> stages =
        [
            .. parse.Stages.Select(s => ((BsonDocument?)new BsonDocument(s.Operator, PipelineText.ParseBody(s.Body).Value!), s.Enabled))
        ];

        PipelinePreviewRun run = await PipelinePreview.RunAsync(orders, stages, 20, new AggregateOptions(), CancellationToken.None);

        Assert.AreEqual(20, run.Input.Count);
        Assert.IsTrue(run.Stages.Take(5).All(s => s.State == PipelineStageState.Ok), string.Join(",", run.Stages.Select(s => s.State)));
        Assert.AreEqual(PipelineStageState.Skipped, run.Stages[5].State);
        long matched = run.Stages[0].Count;
        long unwound = run.Stages[1].Count;
        long grouped = run.Stages[2].Count;
        Assert.IsTrue(matched is > 0 and <= 20, $"match {matched}");
        Assert.IsTrue(unwound >= matched, $"unwind {unwound} < match {matched}");
        Assert.IsTrue(grouped <= unwound);
        Assert.AreEqual(grouped, run.Stages[3].Count);
        Assert.AreEqual(Math.Min(10, grouped), run.Stages[4].Count);
        CollectionAssert.AreEqual(new[] { "_id", "qty", "revenue" }, run.Stages[2].Documents[0].Names.ToArray());
        // $sort 之后 revenue 降序。
        IReadOnlyList<BsonDocument> sorted = run.Stages[3].Documents;
        for (int i = 1; i < sorted.Count; i++)
        {
            Assert.IsTrue(sorted[i - 1]["revenue"].ToDecimal() >= sorted[i]["revenue"].ToDecimal());
        }
    });

    [TestMethod]
    public void Preview_pins_a_server_error_on_the_first_failing_stage() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using MongoConnection connection = await TestServer.OpenAsync();
        IMongoCollection<BsonDocument> orders = connection.Collection(Screens.Database, "orders");
        List<(BsonDocument?, bool)> stages =
        [
            (new BsonDocument("$match", new BsonDocument()), true),
            (new BsonDocument("$group", new BsonDocument { { "_id", "$status" }, { "n", new BsonDocument("$bogus", 1) } }), true),
            (new BsonDocument("$sort", new BsonDocument("n", 1)), true),
            (null, true),
            (new BsonDocument("$out", "nowhere"), true)
        ];

        PipelinePreviewRun run = await PipelinePreview.RunAsync(orders, stages, 5, new AggregateOptions(), CancellationToken.None);

        Assert.AreEqual(PipelineStageState.Ok, run.Stages[0].State);
        Assert.AreEqual(PipelineStageState.Error, run.Stages[1].State);
        StringAssert.Contains(run.Stages[1].Error ?? "", "$bogus");
        Assert.AreEqual(PipelineStageState.Blocked, run.Stages[2].State);
        Assert.AreEqual(PipelineStageState.Invalid, run.Stages[3].State);
        Assert.AreEqual(PipelineStageState.Blocked, run.Stages[4].State);

        // 写入阶段从不在预览里跑。
        PipelinePreviewRun write = await PipelinePreview.RunAsync(orders, [(new BsonDocument("$out", "nowhere"), true)], 5, new AggregateOptions(), CancellationToken.None);
        Assert.AreEqual(PipelineStageState.WriteStage, write.Stages[0].State);
    });

    // ── 写入路径(自建临时库)──────────────────────────────────────────

    [TestMethod]
    public void Run_with_out_view_creation_and_save_write_for_real() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_pipeline_{Guid.NewGuid():N}"[..30];
        await using MongoConnection connection = await TestServer.OpenAsync();
        try
        {
            await connection.Collection(db, "src").InsertManyAsync(
            [
                new BsonDocument { { "_id", 1 }, { "k", "a" }, { "v", 2 } },
                new BsonDocument { { "_id", 2 }, { "k", "b" }, { "v", 3 } },
                new BsonDocument { { "_id", 3 }, { "k", "a" }, { "v", 5 } }
            ]);
            var workspace = new PipelineTestWorkspace(connection);
            var vm = new PipelineTabViewModel(workspace, new CollectionInfo(db, "src", CollectionKind.Collection, []), null) { AutoPreview = false };
            Assert.IsTrue(vm.LoadFromText("[ { $group: { _id: \"$k\", total: { $sum: \"$v\" } } }, { $sort: { _id: 1 } } ]"));

            // 预览:直接算一次,卡片拿到计数与上游字段。
            await vm.RefreshPreviewAsync();
            Assert.AreEqual("2", vm.Stages[0].CountText);
            Assert.AreEqual(PipelineStageState.Ok, vm.Stages[1].Outcome.State);
            CollectionAssert.AreEqual(new[] { "_id", "total" }, vm.Stages[1].UpstreamFields.Select(f => f.Path).ToArray());

            // 运行:结果进输出表。
            await vm.RunAsync();
            Assert.IsTrue(vm.HasRows);
            Assert.AreEqual(2, vm.ResultRows.Count);
            Assert.AreEqual("\"a\"", vm.ResultRows[0].Cells[0].Text);
            Assert.AreEqual("7", vm.ResultRows[0].Cells[1].Text);
            StringAssert.Contains(vm.ResultSummary, "2 份文档 · 完整运行");

            // 带 $out 的管道:先过 EnsureWritable 与确认,再真写。
            vm.AddStage("$out").Body = "\"out_totals\"";
            await vm.RunAsync();
            Assert.AreEqual(1, workspace.Confirms.Count);
            Assert.IsTrue(workspace.Writable.Contains(db));
            long written = await connection.Collection(db, "out_totals").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(2, written);

            // 只读拦截:EnsureWritable 说不行就什么都不写。
            workspace.AllowWrites = false;
            vm.Stages[^1].Body = "\"blocked\"";
            await vm.RunAsync();
            List<string> names = await (await connection.Database(db).ListCollectionNamesAsync()).ToListAsync();
            Assert.IsFalse(names.Contains("blocked"));
            workspace.AllowWrites = true;

            // 建视图。
            vm.RemoveStage(vm.Stages[^1]);
            bool created = await vm.CreateViewCoreAsync("v_totals", vm.EnabledPipeline());
            Assert.IsTrue(created);
            BsonDocument? view = await connection.Collection(db, "v_totals").Find(new BsonDocument("_id", "b")).FirstOrDefaultAsync();
            Assert.AreEqual(3, view!["total"].ToInt32());

            // 保存管道:按连接存放,内容第一行写命名空间,存完不再是"未保存"。
            await vm.SavePipelineAsync("totals");
            IReadOnlyList<SavedItem> saved = await workspace.Store.LoadSavedAsync("pipeline", workspace.ConnectionKey);
            SavedItem item = saved.Single(i => i.Name == "totals");
            StringAssert.StartsWith(item.Content, $"// {db}.src\n[");
            Assert.IsTrue(PipelineText.Parse(item.Content).Ok);
            Assert.IsFalse(vm.IsModified);
            StringAssert.Contains(vm.StatusText, "管道 · totals");
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(db);
        }
    });

    // ── 截图 ────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board04_Pipeline_builder_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        if (bench.ViewModel.VisibleNodes.FirstOrDefault(n => n.Kind == NodeKind.Collection && n.Name == "orders") is { } node)
        {
            bench.ViewModel.SelectedNode = node;
        }
        bench.Session.OpenPipeline(Screens.Database, "orders");
        var tab = (PipelineTabViewModel)bench.ViewModel.ActiveTab!;
        await Screens.PumpAsync();
        Assert.IsTrue(tab.LoadFromText(DesignPipeline, markClean: true));
        for (int i = 0; i < tab.Stages.Count; i++)
        {
            tab.Stages[i].IsExpanded = i is 0 or 2;
        }
        await tab.RefreshPreviewAsync();
        await tab.RunAsync();
        await Screens.PumpAsync(80);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "04-pipeline");
        Assert.IsNotNull(frame);
        Assert.AreEqual("6 个阶段 · 5 个启用", tab.StageSummary);
        Assert.IsTrue(tab.Stages[0].HasPreviewCards);
        Assert.IsTrue(tab.HasRows);
        Assert.IsTrue(tab.ResultRows.Count is > 0 and <= 10);
        Assert.IsTrue(tab.ResultColumns.Any(c => c.IsGhost && c.Name == "product.name"));
    });
}

/// <summary>管道测试用的外壳替身:记下提示、确认与写检查,不弹任何东西。</summary>
internal sealed class PipelineTestWorkspace(MongoConnection? connection = null) : IMongoWorkspace
{
    private readonly TestPluginContext _context = new();

    /// <summary>EnsureWritable 的答案。</summary>
    public bool AllowWrites { get; set; } = true;

    /// <summary>过过写检查的库。</summary>
    public List<string> Writable { get; } = [];

    /// <summary>弹过的确认。</summary>
    public List<ConfirmRequest> Confirms { get; } = [];

    /// <summary>弹过的提示。</summary>
    public List<ToastRequest> Toasts { get; } = [];

    /// <summary>打开过的查询(库, 文本, 是否运行)。</summary>
    public List<(string Database, string? Text, bool Run)> Queries { get; } = [];

    /// <summary>挂过的对话框。</summary>
    public List<DialogViewModel> Dialogs { get; } = [];

    public Loc Loc { get; } = new("zh-CN");

    public MongoConnection Connection => connection ?? throw new InvalidOperationException("No connection in this test.");

    public MongoStore Store => field ??= new MongoStore(_context);

    public IPluginLogger Log => _context.Log;

    public string ConnectionKey => "127.0.0.1:27017";

    public string ConnectionName => "mongo-test";

    public WriteGuard Guard { get; } = new(new MongoSettings(), PrivilegeSummary.Unrestricted);

    public bool EnsureWritable(string database)
    {
        if (AllowWrites)
        {
            Writable.Add(database);
        }
        return AllowWrites;
    }

    public (string? Database, string? Collection) Scope => (null, null);

    public IReadOnlyList<string> Databases => [];

    public IReadOnlyList<CollectionInfo> CollectionsOf(string database) => [];

    public void OpenCollection(string database, string collection, string? filter = null)
    {
    }

    public void OpenQuery(string database, string? text = null, bool run = false) => Queries.Add((database, text, run));

    public void OpenPipeline(string database, string collection, BsonArray? pipeline = null)
    {
    }

    public void OpenDesign(string database, string collection, DesignPage page = DesignPage.Indexes)
    {
    }

    public void OpenGridFs(string database, string bucket)
    {
    }

    public void OpenObjects(string database, ObjectFilter filter = ObjectFilter.All)
    {
    }

    public void OpenMonitor()
    {
    }

    public void OpenProfiler(string database)
    {
    }

    public void OpenUsers(string? database, bool roles = false)
    {
    }

    public void ShowDialog(DialogViewModel dialog) => Dialogs.Add(dialog);

    public void CloseDialog(DialogViewModel dialog)
    {
        _ = Dialogs.Remove(dialog);
        dialog.OnClosed();
    }

    public Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        Confirms.Add(request);
        return Task.FromResult(true);
    }

    public void Toast(ToastRequest toast) => Toasts.Add(toast);

    public Task CopyAsync(string text) => Task.CompletedTask;

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple = false) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Task RefreshTreeAsync(string? database = null) => Task.CompletedTask;

    public void NotifyStatusChanged()
    {
    }
}
