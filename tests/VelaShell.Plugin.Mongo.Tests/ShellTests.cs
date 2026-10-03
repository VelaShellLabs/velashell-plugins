using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 查询编辑器的纯文本层:语句切分、语句解析、补全上下文、格式化。都不碰数据库 ——
/// 这一层错了,"运行当前语句"会跑错语句、补全会在错的位置给错的东西。
/// </summary>
[TestClass]
public sealed class ShellTests
{
    private const string DesignScript = """
        // 近 30 天高价值订单 · 按客户汇总
        use("shop")

        db.orders.find(
          { status: "paid", total: { $gte: 500 }, createdAt: { $gte: ISODate("2026-08-27") } },
          { orderNo: 1, "customer.name": 1, total: 1 }
        ).sort({ createdAt: -1 }).limit(100)

        db.orders.aggregate([
          { $match: { status: "paid", createdAt: { $gte: ISODate("2026-08-27") } } },
          { $group: { _id: "$customer.id", name: { $first: "$customer.name" }, orders: { $sum: 1 }, sum: { $sum: "$total" } } },
          { $sort: { sum: -1 } }
        ])
        """;

    // ── 切分 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Split_DesignScriptIntoThreeStatements()
    {
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(DesignScript);
        Assert.HasCount(3, statements);
        Assert.AreEqual("use(\"shop\")", statements[0].Text);
        Assert.StartsWith("db.orders.find(", statements[1].Text);
        Assert.EndsWith(".limit(100)", statements[1].Text);
        Assert.StartsWith("db.orders.aggregate([", statements[2].Text);
        Assert.EndsWith("])", statements[2].Text);
        Assert.AreEqual(DesignScript.IndexOf("db.orders.find", StringComparison.Ordinal), statements[1].Offset);
    }

    [TestMethod]
    public void Split_IgnoresSeparatorsInsideStringsCommentsAndRegex()
    {
        const string script = "db.a.find({ s: \"x;y\\\"z\" }); // a;b\ndb.b.find({ r: /a;b(/ }) /* ; ( */\ndb.c.find({ t: 'it''s' })";
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(script);
        Assert.HasCount(3, statements);
        Assert.AreEqual("db.a.find({ s: \"x;y\\\"z\" })", statements[0].Text);
        Assert.AreEqual("db.b.find({ r: /a;b(/ })", statements[1].Text);
    }

    [TestMethod]
    public void Split_ChainedCallsOnFollowingLinesStayTogether()
    {
        const string script = "db.orders.find({})\n  .sort({ a: 1 })\n  // 注释\n  .limit(5)\nshow dbs";
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(script);
        Assert.HasCount(2, statements);
        Assert.EndsWith(".limit(5)", statements[0].Text);
        Assert.AreEqual("show dbs", statements[1].Text);
    }

    [TestMethod]
    public void Split_SemicolonsOnOneLine()
    {
        IReadOnlyList<ShellStatement> statements = ShellScript.Split("use shop; db.a.count();db.b.find()");
        Assert.HasCount(3, statements);
        Assert.AreEqual("db.a.count()", statements[1].Text);
        Assert.AreEqual("db.b.find()", statements[2].Text);
    }

    [TestMethod]
    public void At_PicksTheStatementUnderOrBeforeTheCaret()
    {
        IReadOnlyList<ShellStatement> statements = ShellScript.Split(DesignScript);
        int inAggregate = DesignScript.IndexOf("$group", StringComparison.Ordinal);
        Assert.AreSame(statements[2], ShellScript.At(statements, inAggregate));
        int blankAfterFind = DesignScript.IndexOf(".limit(100)", StringComparison.Ordinal) + ".limit(100)".Length + 1;
        Assert.AreSame(statements[1], ShellScript.At(statements, blankAfterFind));
        Assert.AreSame(statements[0], ShellScript.At(statements, 0));
    }

    // ── 解析 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Parse_FindWithModifiers()
    {
        ShellStatement find = ShellScript.Split(DesignScript)[1];
        ShellCommand command = ShellParser.Parse(find);
        Assert.AreEqual(ShellCommandKind.Collection, command.Kind);
        Assert.AreEqual("orders", command.Collection);
        Assert.AreEqual("find", command.Method!.Name);
        BsonDocument filter = command.Method.Document(0)!;
        Assert.AreEqual("paid", filter["status"].AsString);
        Assert.AreEqual(BsonType.DateTime, filter["createdAt"]["$gte"].BsonType);
        Assert.AreEqual(1, command.Method.Document(1)!["customer.name"].AsInt32);
        Assert.AreEqual(-1, command.Modifier("sort")!.Document(0)!["createdAt"].AsInt32);
        Assert.AreEqual(100, command.Modifier("limit")!.Arg(0)!.AsInt32);
        Assert.IsNull(command.Explain);
        Assert.IsFalse(command.IsWrite);
    }

    [TestMethod]
    public void Parse_UseShowAndDatabaseCommands()
    {
        ShellCommand use = ShellParser.Parse(new ShellStatement(0, "use(\"shop\")", 0));
        Assert.AreEqual(ShellCommandKind.Use, use.Kind);
        Assert.AreEqual("shop", use.Target);
        Assert.AreEqual("logs", ShellParser.Parse(new ShellStatement(0, "use logs", 0)).Target);
        Assert.AreEqual("dbs", ShellParser.Parse(new ShellStatement(0, "show databases", 0)).Target);
        Assert.AreEqual("collections", ShellParser.Parse(new ShellStatement(0, "show tables", 0)).Target);

        ShellCommand run = ShellParser.Parse(new ShellStatement(0, "db.runCommand({ ping: 1 })", 0));
        Assert.AreEqual(ShellCommandKind.Database, run.Kind);
        Assert.AreEqual("runCommand", run.Method!.Name);
        Assert.IsFalse(run.IsWrite);
        Assert.IsTrue(ShellParser.Parse(new ShellStatement(0, "db.runCommand({ drop: \"x\" })", 0)).IsWrite);
    }

    [TestMethod]
    public void Parse_CollectionAddressingForms()
    {
        ShellCommand dotted = ShellParser.Parse(new ShellStatement(0, "db.system.profile.find().limit(5)", 0));
        Assert.AreEqual("system.profile", dotted.Collection);
        ShellCommand byName = ShellParser.Parse(new ShellStatement(0, "db.getCollection(\"weird-name\").countDocuments({})", 0));
        Assert.AreEqual("weird-name", byName.Collection);
        Assert.AreEqual("countDocuments", byName.Method!.Name);
        ShellCommand bracket = ShellParser.Parse(new ShellStatement(0, "db[\"a b\"].findOne()", 0));
        Assert.AreEqual("a b", bracket.Collection);
        ShellCommand sibling = ShellParser.Parse(new ShellStatement(0, "db.getSiblingDB(\"logs\").events.find()", 0));
        Assert.AreEqual("logs", sibling.Database);
        Assert.AreEqual("events", sibling.Collection);
    }

    [TestMethod]
    public void Parse_ExplainEitherPosition()
    {
        ShellCommand front = ShellParser.Parse(new ShellStatement(0, "db.orders.explain(\"executionStats\").find({ status: \"paid\" })", 0));
        Assert.AreEqual("executionStats", front.Explain);
        Assert.AreEqual("find", front.Method!.Name);
        ShellCommand back = ShellParser.Parse(new ShellStatement(0, "db.orders.find({}).sort({ a: 1 }).explain()", 0));
        Assert.AreEqual("queryPlanner", back.Explain);
        Assert.HasCount(1, back.Chain);
    }

    [TestMethod]
    public void Parse_WritesAreFlagged()
    {
        Assert.IsTrue(ShellParser.Parse(new ShellStatement(0, "db.a.updateMany({}, { $set: { x: 1 } })", 0)).IsWrite);
        Assert.IsTrue(ShellParser.Parse(new ShellStatement(0, "db.a.aggregate([{ $match: {} }, { $out: \"b\" }])", 0)).IsWrite);
        Assert.IsFalse(ShellParser.Parse(new ShellStatement(0, "db.a.aggregate([{ $match: {} }])", 0)).IsWrite);
    }

    [TestMethod]
    public void Parse_ErrorsPointAtTheProblem()
    {
        const string text = "db.orders.find({ customer.name: 1 })";
        ShellParseException dotted = Assert.ThrowsExactly<ShellParseException>(() => ShellParser.Parse(new ShellStatement(0, text, 0)));
        Assert.AreEqual("Shell_DottedKey", dotted.MessageKey);
        Assert.AreEqual(text.IndexOf("customer.name", StringComparison.Ordinal), dotted.Offset);

        ShellParseException method = Assert.ThrowsExactly<ShellParseException>(() => ShellParser.Parse(new ShellStatement(0, "db.orders", 0)));
        Assert.AreEqual("Query_ParseNeedsMethod", method.MessageKey);
        ShellParseException cursor = Assert.ThrowsExactly<ShellParseException>(() => ShellParser.Parse(new ShellStatement(0, "db.a.find().frobnicate()", 0)));
        Assert.AreEqual("Query_ParseUnknownCursor", cursor.MessageKey);
        ShellParseException unclosed = Assert.ThrowsExactly<ShellParseException>(() => ShellParser.Parse(new ShellStatement(0, "db.a.find({ a: 1 }", 0)));
        Assert.AreEqual("Query_ParseUnclosed", unclosed.MessageKey);
        ShellParseException js = Assert.ThrowsExactly<ShellParseException>(() => ShellParser.Parse(new ShellStatement(0, "var x = 1", 0)));
        Assert.AreEqual("Query_ParseUnsupported", js.MessageKey);
    }

    // ── 补全上下文 ──────────────────────────────────────────────────────────

    private static CompletionContext At(string textWithCaret)
    {
        int caret = textWithCaret.IndexOf('|', StringComparison.Ordinal);
        return ShellCompletion.Analyze(textWithCaret.Remove(caret, 1), caret);
    }

    [TestMethod]
    public void Completion_MemberAccess()
    {
        CompletionContext db = At("use(\"shop\")\ndb.or|");
        Assert.AreEqual(CompletionSlot.DbMember, db.Slot);
        Assert.AreEqual("or", db.Prefix);

        CompletionContext method = At("db.orders.fi|");
        Assert.AreEqual(CompletionSlot.CollectionMember, method.Slot);
        Assert.AreEqual("orders", method.Collection);

        CompletionContext byName = At("db.getCollection(\"orders\").|");
        Assert.AreEqual(CompletionSlot.CollectionMember, byName.Slot);
        Assert.AreEqual("orders", byName.Collection);

        CompletionContext cursor = At("db.orders.find({ a: 1 }).so|");
        Assert.AreEqual(CompletionSlot.CursorMember, cursor.Slot);
        Assert.AreEqual("find", cursor.Method);
    }

    [TestMethod]
    public void Completion_KeysInFilterAndOperatorObjects()
    {
        CompletionContext key = At("db.orders.find({ status: \"paid\", tot|");
        Assert.AreEqual(CompletionSlot.Key, key.Slot);
        Assert.AreEqual(ObjectRole.Filter, key.Role);
        Assert.AreEqual("tot", key.Prefix);
        Assert.AreEqual("orders", key.Collection);

        CompletionContext op = At("db.orders.find({ total: { $g| } })");
        Assert.AreEqual(CompletionSlot.Key, op.Slot);
        Assert.AreEqual(ObjectRole.Operator, op.Role);
        Assert.AreEqual("total", op.FieldKey);

        CompletionContext dotted = At("db.orders.find({ customer.na|");
        Assert.AreEqual("customer.na", dotted.Prefix, "对象里的点路径整个算前缀");

        CompletionContext sort = At("db.orders.find({}).sort({ cre|");
        Assert.AreEqual(ObjectRole.Sort, sort.Role);

        CompletionContext update = At("db.orders.updateOne({ _id: 1 }, { $s|");
        Assert.AreEqual(ObjectRole.Update, update.Role);
        CompletionContext set = At("db.orders.updateOne({ _id: 1 }, { $set: { st|");
        Assert.AreEqual(ObjectRole.UpdateFields, set.Role);
    }

    [TestMethod]
    public void Completion_PipelineStagesKnowTheUpstreamGroup()
    {
        int caret = DesignScript.IndexOf("$sort", StringComparison.Ordinal) + 3;
        CompletionContext stage = ShellCompletion.Analyze(DesignScript, caret);
        Assert.AreEqual(CompletionSlot.Key, stage.Slot);
        Assert.AreEqual(ObjectRole.Stage, stage.Role);
        Assert.AreEqual("$so", stage.Prefix);
        Assert.IsTrue(stage.InPipeline);
        Assert.HasCount(2, stage.PrecedingStages);
        PipelineShape? shape = ShellCompletion.Upstream(stage.PrecedingStages);
        Assert.IsNotNull(shape);
        Assert.AreEqual("$group", shape.Source);
        Assert.AreEqual("_id,name,orders,sum", string.Join(",", shape.Fields));

        CompletionContext inMatch = At("db.orders.aggregate([{ $match: { sta| } }])");
        Assert.AreEqual(ObjectRole.Filter, inMatch.Role);
        Assert.AreEqual("$match", inMatch.StageName);

        CompletionContext accumulator = At("db.orders.aggregate([{ $group: { _id: \"$a\", n: { $s| } } }])");
        Assert.AreEqual(ObjectRole.Accumulator, accumulator.Role);

        CompletionContext path = At("db.orders.aggregate([{ $group: { _id: \"$cust|");
        Assert.AreEqual(CompletionSlot.FieldPathString, path.Slot);
        Assert.AreEqual("$cust", path.Prefix);
    }

    [TestMethod]
    public void Completion_StringArgumentsAndStatementStart()
    {
        CompletionContext getCollection = At("db.getCollection(\"or|");
        Assert.AreEqual(CompletionSlot.StringArgument, getCollection.Slot);
        Assert.AreEqual("getCollection", getCollection.StringCall);
        Assert.AreEqual("or", getCollection.Prefix);

        CompletionContext hint = At("db.orders.find({}).hint(\"st|\")");
        Assert.AreEqual(CompletionSlot.StringArgument, hint.Slot);
        Assert.IsTrue(hint.QuoteClosed);

        Assert.AreEqual(CompletionSlot.Statement, At("db.a.find()\nus|").Slot);
        Assert.AreEqual(CompletionSlot.None, At("// db.|").Slot);
    }

    [TestMethod]
    public void Upstream_FollowsProjectionAndCount()
    {
        PipelineShape? project = ShellCompletion.Upstream(["{ $project: { orderNo: 1, total: 1 } }"]);
        Assert.AreEqual("_id,orderNo,total", string.Join(",", project!.Fields));
        PipelineShape? count = ShellCompletion.Upstream(["{ $match: {} }", "{ $count: \"n\" }"]);
        Assert.AreEqual("n", string.Join(",", count!.Fields));
        Assert.IsNull(ShellCompletion.Upstream(["{ $match: { a: 1 } }", "{ $sort: { a: 1 } }"]));
    }

    // ── 格式化 ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Format_NormalizesSpacingAndIndentation()
    {
        const string messy = "db.orders.aggregate([\n{$match:{status:\"a:b\",n:{$gte:1}}},\n      {$sort:{n:-1}}\n])";
        string formatted = ShellFormatter.Format(messy);
        Assert.AreEqual("db.orders.aggregate([\n  { $match: { status: \"a:b\", n: { $gte: 1 } } },\n  { $sort: { n: -1 } }\n])", formatted);

        const string chained = "db.orders.find({})\n.sort({a:1})\n.limit(5)";
        Assert.AreEqual("db.orders.find({})\n  .sort({ a: 1 })\n  .limit(5)", ShellFormatter.Format(chained));
    }

    [TestMethod]
    public void Format_LeavesStringsRegexAndCommentsAlone()
    {
        const string text = "db.a.find({ s: \"{x:1,  y}\", r: /a,b:c/ }) // {keep:this}";
        Assert.AreEqual(text, ShellFormatter.Format(text));
    }
}
