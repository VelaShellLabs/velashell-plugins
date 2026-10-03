using System.Text;
using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 导入:值换算(千分位、歧义日期、整数小数、布尔、ObjectId)、validator 规则(required / enum)、
/// 文件识别与读者(CSV 编码与分隔符、JSON 数组 / JSONL / 美化后首尾相接、.bson)、
/// 真实服务器上的插入 / upsert / 错误报告,以及设计稿 20 的 dry-run 截图。
/// 写服务器的测试只碰自己建的 <c>velashell_transfer_*</c> 库,收尾 drop 掉。
/// </summary>
[TestClass]
public sealed class ImportTests
{
    // ── 值换算 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Numbers_with_grouping_or_quotes_import_with_a_warning()
    {
        ConvertOutcome plain = ImportValues.FromText("1299.00", BsonKind.Decimal128, null);
        Assert.IsNull(plain.Issue);
        Assert.AreEqual(new BsonDecimal128(Decimal128.Parse("1299.00")), plain.Value);

        ConvertOutcome grouped = ImportValues.FromText("\"1,580.00\"", BsonKind.Decimal128, null);
        Assert.AreEqual(ImportIssueKind.NumberFromText, grouped.Issue);
        Assert.IsFalse(grouped.IsError);
        Assert.AreEqual(new BsonDecimal128(Decimal128.Parse("1580.00")), grouped.Value);

        ConvertOutcome currency = ImportValues.FromText("¥ 86.50", BsonKind.Double, null);
        Assert.AreEqual(ImportIssueKind.NumberFromText, currency.Issue);
        Assert.AreEqual(86.5, currency.Value!.AsDouble);

        ConvertOutcome bad = ImportValues.FromText("abc", BsonKind.Decimal128, null);
        Assert.AreEqual(ImportIssueKind.BadNumber, bad.Issue);
        Assert.IsTrue(bad.IsError);
    }

    [TestMethod]
    public void Integers_accept_whole_decimals_but_never_truncate_fractions()
    {
        Assert.AreEqual(new BsonInt32(12), ImportValues.FromText("12.00", BsonKind.Int32, null).Value);
        Assert.IsTrue(ImportValues.FromText("12.5", BsonKind.Int32, null).IsError, "12.5 is not silently truncated");
        Assert.IsTrue(ImportValues.FromText("3000000000", BsonKind.Int32, null).IsError, "overflow is an error");
        Assert.AreEqual(new BsonInt64(3_000_000_000), ImportValues.FromText("3000000000", BsonKind.Int64, null).Value);
    }

    [TestMethod]
    public void Dates_only_accept_unambiguous_forms_unless_a_format_is_given()
    {
        ConvertOutcome iso = ImportValues.FromText("2026-09-01T10:12:08Z", BsonKind.Date, null);
        Assert.AreEqual(new BsonDateTime(new DateTime(2026, 9, 1, 10, 12, 8, DateTimeKind.Utc)), iso.Value);

        ConvertOutcome local = ImportValues.FromText("2026-09-01 10:12:08", BsonKind.Date, null);
        Assert.AreEqual(new DateTime(2026, 9, 1, 10, 12, 8, DateTimeKind.Local).ToUniversalTime(), local.Value!.ToUniversalTime());

        ConvertOutcome ambiguous = ImportValues.FromText("09/01/26 11:31", BsonKind.Date, null);
        Assert.AreEqual(ImportIssueKind.BadDate, ambiguous.Issue);
        Assert.AreEqual("MM/dd/yy", ambiguous.Detail);
        Assert.AreEqual("MM/dd/yy HH:mm", ImportValues.GuessDateFormat("09/01/26 11:31", includeTime: true));
        Assert.AreEqual("dd/MM/yyyy", ImportValues.GuessDateFormat("25/12/2026", includeTime: true));

        ConvertOutcome withFormat = ImportValues.FromText("09/01/26 11:31", BsonKind.Date, "MM/dd/yy HH:mm");
        Assert.IsNull(withFormat.Issue);
        Assert.AreEqual(new DateTime(2026, 9, 1, 11, 31, 0, DateTimeKind.Local).ToUniversalTime(), withFormat.Value!.ToUniversalTime());
    }

    [TestMethod]
    public void Other_types_convert_or_fail_clearly()
    {
        Assert.AreEqual(BsonBoolean.True, ImportValues.FromText("Yes", BsonKind.Boolean, null).Value);
        Assert.AreEqual(BsonBoolean.False, ImportValues.FromText("否", BsonKind.Boolean, null).Value);
        Assert.IsTrue(ImportValues.FromText("maybe", BsonKind.Boolean, null).IsError);
        Assert.AreEqual(ObjectId.Parse("66f5c2a1b04e1c3a5d7e9f01"),
            ImportValues.FromText("ObjectId(\"66f5c2a1b04e1c3a5d7e9f01\")", BsonKind.ObjectId, null).Value!.AsObjectId);
        Assert.AreEqual(new BsonArray { "a", 1 }, ImportValues.FromText("[\"a\", 1]", BsonKind.Array, null).Value);
        Assert.IsTrue(ImportValues.FromText("{oops", BsonKind.Object, null).IsError);
        Assert.AreEqual(new BsonString("  keep spaces "), ImportValues.FromText("  keep spaces ", BsonKind.String, null).Value);

        ConvertOutcome fromJsonString = ImportValues.FromValue(new BsonString("42"), BsonKind.Int32, null);
        Assert.AreEqual(new BsonInt32(42), fromJsonString.Value);
        Assert.AreEqual(ImportIssueKind.NumberFromText, fromJsonString.Issue, "a JSON string turned into a number is worth a warning");
        Assert.AreEqual(new BsonInt64(7), ImportValues.FromValue(new BsonInt32(7), BsonKind.Int64, null).Value);
        Assert.IsNull(ImportValues.FromValue(new BsonInt32(7), BsonKind.Int32, null).Issue);
    }

    // ── 规则与换算器 ───────────────────────────────────────────────────────

    private static readonly BsonDocument Validator = BsonDocument.Parse("""
        { "$jsonSchema": {
            "bsonType": "object",
            "required": ["orderNo"],
            "properties": {
              "orderNo": { "bsonType": "string" },
              "total": { "bsonType": "decimal" },
              "status": { "enum": ["paid", "shipped", "pending"] },
              "customer": { "bsonType": "object", "properties": { "level": { "enum": ["VIP", "SVIP", "普通"] } } }
            } } }
        """);

    [TestMethod]
    public void Rules_come_from_the_json_schema_validator()
    {
        ImportRules rules = ImportRules.FromValidator(Validator, "error");
        CollectionAssert.AreEqual(new[] { "orderNo" }, rules.Required.ToArray());
        Assert.IsTrue(rules.Enums.ContainsKey("status"));
        Assert.IsTrue(rules.Enums.ContainsKey("customer.level"), "nested enums are found by path");
        Assert.AreEqual(BsonKind.Decimal128, SchemaSampler.KindFromSchema(rules.Properties["total"]));
        Assert.IsTrue(rules.ValidationErrors);
        Assert.IsFalse(ImportRules.FromValidator(Validator, "warn").ValidationErrors);
    }

    [TestMethod]
    public void Converter_maps_cells_to_nested_paths_and_reports_issues_per_cell()
    {
        ImportColumn[] columns =
        [
            new() { Source = "No", Index = 0, Target = "orderNo", Kind = BsonKind.String },
            new() { Source = "Amount", Index = 1, Target = "total", Kind = BsonKind.Decimal128 },
            new() { Source = "Status", Index = 2, Target = "status", Kind = BsonKind.String },
            new() { Source = "Level", Index = 3, Target = "customer.level", Kind = BsonKind.String },
            new() { Source = "Ignored", Index = 4, Target = "x", Include = false }
        ];
        var converter = new ImportConverter(columns, ImportRules.FromValidator(Validator, "error"), new Loc("zh-CN"));

        ConvertedRow ok = converter.Convert(new ImportRecord(2, ["SO-1", "12.50", "paid", "VIP", "zzz"], null));
        Assert.AreEqual(ImportRowStatus.Ok, ok.Status);
        Assert.AreEqual(BsonDocument.Parse("{ orderNo: 'SO-1', total: NumberDecimal('12.50'), status: 'paid', customer: { level: 'VIP' } }"), ok.Document);

        ConvertedRow warn = converter.Convert(new ImportRecord(3, ["SO-2", "\"1,580.00\"", "Paid", "vip", ""], null));
        Assert.AreEqual(ImportRowStatus.Warning, warn.Status);
        Assert.AreEqual("paid", warn.Document["status"].AsString, "enum casing normalized to the validator's spelling");
        Assert.AreEqual("VIP", warn.Document["customer"]["level"].AsString);
        Assert.AreEqual(3, warn.Issues.Count, "number from text + two enum case fixes");

        ConvertedRow missing = converter.Convert(new ImportRecord(4, ["", "219.00", "paid", "普通", ""], null));
        Assert.AreEqual(ImportRowStatus.Error, missing.Status);
        ImportIssue required = missing.Issues.Single(static i => i.Kind == ImportIssueKind.MissingRequired);
        Assert.AreEqual("orderNo", required.Field);
        Assert.AreEqual(0, required.Column, "the empty orderNo cell is highlighted");

        ConvertedRow bogus = converter.Convert(new ImportRecord(5, ["SO-3", "1", "lost"], null));
        Assert.AreEqual(ImportIssueKind.EnumMismatch, bogus.Issues.Single().Kind);

        IReadOnlyList<ImportIssueGroup> groups = ImportDryRun.Group([ok, warn, missing, bogus]);
        Assert.IsTrue(groups[0].IsError, "errors are listed first");
        Assert.AreEqual(4, groups.Single(static g => g.Kind == ImportIssueKind.MissingRequired).FirstLine);
    }

    [TestMethod]
    public void Upsert_and_replace_require_the_match_key_and_build_the_right_models()
    {
        ImportColumn[] columns = [new() { Source = "orderNo", Index = 0, Target = "orderNo", Kind = BsonKind.String }, new() { Source = "n", Index = 1, Target = "n", Kind = BsonKind.Int32 }];
        var rules = new ImportRules { Mode = ImportWriteMode.Upsert, MatchKey = "orderNo" };
        var converter = new ImportConverter(columns, rules, new Loc("en"));
        Assert.AreEqual(ImportIssueKind.MissingRequired, converter.Convert(new ImportRecord(2, ["", "1"], null)).Issues.Single().Kind);

        var doc = new BsonDocument { { "_id", 7 }, { "orderNo", "SO-1" }, { "n", 1 } };
        var upsert = (UpdateOneModel<BsonDocument>)ImportRunner.Model(doc, rules);
        Assert.IsTrue(upsert.IsUpsert);
        var update = (BsonDocumentUpdateDefinition<BsonDocument>)upsert.Update;
        Assert.AreEqual(BsonDocument.Parse("{ $set: { n: 1 }, $setOnInsert: { _id: 7 } }"), update.Document);

        var replace = (ReplaceOneModel<BsonDocument>)ImportRunner.Model(doc, rules with { Mode = ImportWriteMode.Replace });
        Assert.IsFalse(replace.Replacement.Contains("_id"), "a matched document keeps its own _id");
        Assert.IsInstanceOfType<InsertOneModel<BsonDocument>>(ImportRunner.Model(doc, new ImportRules()));
    }

    // ── 文件与读者 ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Sniffing_detects_format_encoding_and_delimiter()
    {
        string dir = ExportTests.TempDir();
        try
        {
            string gbk = Path.Combine(dir, "gbk.txt");
            File.WriteAllBytes(gbk, TextEncodings.Gbk.GetBytes("名称;等级\r\n陈立;普通\r\n"));
            ImportSource source = ImportSource.Sniff(gbk);
            Assert.AreEqual(ImportFormat.Csv, source.Format);
            Assert.AreEqual(TextEncodingKind.Gbk, source.Encoding);
            Assert.AreEqual(';', source.Delimiter);
            using (IImportReader reader = source.Open())
            {
                CollectionAssert.AreEqual(new[] { "名称", "等级" }, reader.Header.ToArray());
                Assert.IsTrue(reader.TryRead(out ImportRecord record));
                CollectionAssert.AreEqual(new[] { "陈立", "普通" }, record.Cells);
                Assert.AreEqual(2, record.Line);
            }
            Assert.AreEqual(1, source.CountRecords(CancellationToken.None));

            string json = Path.Combine(dir, "data.txt");
            File.WriteAllText(json, "[{\"a\":1}]", new UTF8Encoding(true));
            ImportSource jsonSource = ImportSource.Sniff(json);
            Assert.AreEqual(ImportFormat.Json, jsonSource.Format);
            Assert.AreEqual(TextEncodingKind.Utf8Bom, jsonSource.Encoding);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Json_scanner_reads_arrays_lines_and_pretty_concatenated_documents()
    {
        const string text = """
            [
              { "a": 1, "s": "brace } inside", "n": { "deep": [1, 2] } },
              {"a": 2, "s": "escaped \" quote {"}
            ]
            {"a": 3}
            {
              "a": 4,
              "id": ObjectId("66f5c2a1b04e1c3a5d7e9f01")
            }
            """;
        var scanner = new JsonDocumentScanner(new StringReader(text));
        var docs = new List<BsonDocument>();
        var lines = new List<long>();
        while (scanner.Next(out string json, out long line))
        {
            docs.Add(BsonDocument.Parse(json));
            lines.Add(line);
        }
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, docs.Select(static d => d["a"].AsInt32).ToArray());
        Assert.AreEqual("brace } inside", docs[0]["s"].AsString);
        Assert.AreEqual("escaped \" quote {", docs[1]["s"].AsString);
        Assert.AreEqual(ObjectId.Parse("66f5c2a1b04e1c3a5d7e9f01"), docs[3]["id"].AsObjectId);
        CollectionAssert.AreEqual(new long[] { 2, 3, 5, 6 }, lines);
        Assert.IsFalse(new JsonDocumentScanner(new StringReader("[]")).Skip());
    }

    [TestMethod]
    public void Bad_json_documents_become_error_rows_instead_of_aborting()
    {
        string dir = ExportTests.TempDir();
        try
        {
            string path = Path.Combine(dir, "rows.jsonl");
            File.WriteAllText(path, "{\"a\": 1}\n{\"a\": tru}\n{\"a\": 3}\n");
            using IImportReader reader = ImportSource.Sniff(path).Open();
            var records = new List<ImportRecord>();
            while (reader.TryRead(out ImportRecord record))
            {
                records.Add(record);
            }
            Assert.HasCount(3, records);
            Assert.IsNotNull(records[1].Error);
            Assert.AreEqual(3, records[2].Document!["a"].AsInt32);

            var converter = new ImportConverter([], new ImportRules(), new Loc("en"));
            Assert.AreEqual(ImportIssueKind.Parse, converter.Convert(records[1]).Issues.Single().Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Guessing_types_keeps_leading_zeros_as_strings()
    {
        Assert.AreEqual(BsonKind.Int32, ImportWizardViewModel.GuessKind(["1", "-2", "300"]));
        Assert.AreEqual(BsonKind.String, ImportWizardViewModel.GuessKind(["00123", "00456"]));
        Assert.AreEqual(BsonKind.Double, ImportWizardViewModel.GuessKind(["1.5", "2"]));
        Assert.AreEqual(BsonKind.Date, ImportWizardViewModel.GuessKind(["2026-09-01 10:12:08"]));
        Assert.AreEqual(BsonKind.Boolean, ImportWizardViewModel.GuessKind(["true", "FALSE"]));
        Assert.AreEqual(BsonKind.String, ImportWizardViewModel.GuessKind(["SO2609-20001"]));
    }

    // ── 真实服务器 ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Runner_inserts_upserts_and_writes_an_error_report()
    {
        await TestServer.RequireAsync();
        await using MongoConnection connection = await TestServer.OpenAsync();
        string db = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        string dir = ExportTests.TempDir();
        try
        {
            await CreateOrdersAsync(connection, db);
            string csv = WriteSampleCsv(dir, 300);
            ImportSource source = ImportSource.Sniff(csv);
            ImportColumn[] columns =
            [
                new() { Source = "orderNo", Index = 0, Target = "orderNo", Kind = BsonKind.String },
                new() { Source = "total", Index = 1, Target = "total", Kind = BsonKind.Decimal128 },
                new() { Source = "status", Index = 2, Target = "status", Kind = BsonKind.String },
                new() { Source = "createdAt", Index = 3, Target = "createdAt", Kind = BsonKind.Date },
                new() { Source = "level", Index = 4, Target = "level", Kind = BsonKind.String }
            ];
            CollectionInfo info = (await connection.ListCollectionsAsync(db)).Single(static c => c.Name == "orders");
            ImportRules rules = ImportRules.FromValidator(info.Validator, info.ValidationAction);

            ImportResult inserted = await ImportRunner.RunAsync(connection, new ImportJob
            {
                Database = db, Collection = "orders", Source = source, Columns = columns, Rules = rules,
                BatchSize = 50, ReportDirectory = dir, EstimatedTotal = source.CountRecords(CancellationToken.None)
            }, new Loc("zh-CN"), null, CancellationToken.None);
            // 300 行:2 行坏(缺 orderNo、日期歧义),3 行与预置的文档撞唯一索引
            Assert.AreEqual(300, inserted.Rows);
            Assert.AreEqual(295, inserted.Inserted);
            Assert.AreEqual(5, inserted.Skipped);
            Assert.IsNotNull(inserted.ErrorReport);
            string[] report = await File.ReadAllLinesAsync(inserted.ErrorReport);
            Assert.HasCount(5, report);
            BsonDocument firstError = BsonDocument.Parse(report[0]);
            Assert.IsTrue(firstError.Contains("line"));
            Assert.IsTrue(firstError["row"].AsBsonDocument.Contains("orderNo"), "the original row is kept so it can be fixed and re-imported");
            Assert.IsTrue(report.Any(static l => l.Contains("E11000", StringComparison.Ordinal)), "duplicate keys come back from the server");

            // 再按 orderNo upsert 一遍:没有新增,全部命中,备份文件里是被覆盖前的文档
            ImportResult upserted = await ImportRunner.RunAsync(connection, new ImportJob
            {
                Database = db, Collection = "orders", Source = source, Columns = columns,
                Rules = rules with { Mode = ImportWriteMode.Upsert, MatchKey = "orderNo" },
                BatchSize = 100, Backup = true, ReportDirectory = dir
            }, new Loc("zh-CN"), null, CancellationToken.None);
            Assert.AreEqual(0, upserted.Inserted);
            Assert.AreEqual(298, upserted.Updated);
            Assert.IsNotNull(upserted.BackupFile);
            Assert.HasCount(298, await File.ReadAllLinesAsync(upserted.BackupFile));
            long count = await connection.Collection(db, "orders").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(298, count);
            BsonDocument row4 = await connection.Collection(db, "orders").Find(new BsonDocument("orderNo", "SO2609-20003")).FirstAsync();
            Assert.AreEqual(new BsonDecimal128(Decimal128.Parse("1580.00")), row4["total"]);
            Assert.AreEqual("paid", row4["status"].AsString);
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(db);
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Wizard_dry_run_counts_rows_issues_and_estimates() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        string dir = ExportTests.TempDir();
        await using Workbench bench = await Screens.OpenWorkbenchAsync(db);
        try
        {
            await CreateOrdersAsync(bench.Connection, db);
            var vm = new ImportWizardViewModel(bench.Session, db, "orders");
            bench.ViewModel.ShowDialog(vm);
            await Screens.PumpAsync(30);
            vm.FilePath = WriteSampleCsv(dir, 1500);
            await ExportTests.WaitAsync(() => vm.Mappings.Count == 5 && !vm.Counting);
            Assert.AreEqual(BsonKind.Decimal128, (BsonKind)vm.Mappings.Single(static m => m.Source == "total").Kind.Value, "type comes from the validator");
            Assert.AreEqual(BsonKind.Date, (BsonKind)vm.Mappings.Single(static m => m.Source == "createdAt").Kind.Value);
            StringAssert.Contains(vm.FileInfo, "1,500");
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep1);
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep2 && !vm.DryRunning && vm.PreviewRows.Count > 0);
            Assert.AreEqual("1,000", vm.AllCountText);
            Assert.IsTrue(vm.Issues.Any(static i => i.IsError && i.Title.Contains("orderNo", StringComparison.Ordinal)));
            Assert.IsTrue(vm.ShowDateFix);
            vm.ModeUpsert = true;
            await ExportTests.WaitAsync(() => !vm.DryRunning && vm.EstUpdate != "—");
            Assert.IsTrue(vm.MatchKeyUnique, "orderNo has a unique index");
            Assert.AreNotEqual("≈ 0", vm.EstUpdate, "the seeded orders are upsert hits");
        }
        finally
        {
            await bench.Connection.Client.DropDatabaseAsync(db);
            Directory.Delete(dir, recursive: true);
        }
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board20_import_preview_screenshot() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        string dir = ExportTests.TempDir();
        await using Workbench bench = await Screens.OpenWorkbenchAsync(db);
        try
        {
            await CreateOrdersAsync(bench.Connection, db);
            var vm = new ImportWizardViewModel(bench.Session, db, "orders");
            bench.ViewModel.ShowDialog(vm);
            await Screens.PumpAsync(30);
            vm.FilePath = WriteSampleCsv(dir, 1500, name: "orders_2026-09.csv");
            await ExportTests.WaitAsync(() => vm.Mappings.Count == 5 && !vm.Counting);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "20-import-file");
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep1);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "20-import-mapping");
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep2 && !vm.DryRunning && vm.PreviewRows.Count > 0);
            vm.ModeUpsert = true;
            await ExportTests.WaitAsync(() => !vm.DryRunning);
            await Screens.PumpAsync(40);
            WriteableBitmap? frame = Screens.Capture(bench.Window, "20-import");
            Assert.IsNotNull(frame);
        }
        finally
        {
            await bench.Connection.Client.DropDatabaseAsync(db);
            Directory.Delete(dir, recursive: true);
        }
    });

    // ── 工具 ─────────────────────────────────────────────────────────────

    /// <summary>建一个带 validator 与唯一索引的 orders,预置三份会被 upsert 命中的文档。</summary>
    internal static async Task CreateOrdersAsync(MongoConnection connection, string db)
    {
        await connection.RunCommandAsync(db, new BsonDocument
        {
            { "create", "orders" },
            { "validator", Validator }
        });
        IMongoCollection<BsonDocument> orders = connection.Collection(db, "orders");
        await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("orderNo", 1), new CreateIndexOptions { Unique = true }));
        await orders.InsertManyAsync(
        [
            new BsonDocument { { "orderNo", "SO2609-20001" }, { "total", new BsonDecimal128(Decimal128.Parse("1.00")) }, { "status", "pending" } },
            new BsonDocument { { "orderNo", "SO2609-20003" }, { "total", new BsonDecimal128(Decimal128.Parse("2.00")) }, { "status", "pending" } },
            new BsonDocument { { "orderNo", "SO2609-20004" }, { "total", new BsonDecimal128(Decimal128.Parse("3.00")) }, { "status", "pending" } }
        ]);
    }

    /// <summary>
    /// 与设计稿 20 同形的 CSV:前 9 行照抄设计稿(含千分位、缺 orderNo、歧义日期、大小写不对的枚举),
    /// 其余是干净的行。
    /// </summary>
    internal static string WriteSampleCsv(string dir, int rows, string name = "orders.csv")
    {
        var text = new StringBuilder("orderNo,total,status,createdAt,level\r\n");
        string[] head =
        [
            "SO2609-20001,1299.00,paid,2026-09-01 10:12:08,VIP",
            "SO2609-20002,86.50,paid,2026-09-01 10:14:51,普通",
            "SO2609-20003,\"1,580.00\",paid,2026-09-01 10:20:13,SVIP",
            "SO2609-20004,640.00,shipped,2026-09-01 11:02:44,VIP",
            ",219.00,paid,2026-09-01 11:05:10,普通",
            "SO2609-20006,3120.00,paid,2026-09-01 11:30:02,VIP",
            "SO2609-20007,75.00,paid,09/01/26 11:31,普通",
            "SO2609-20008,412.00,Paid,2026-09-01 11:47:39,VIP",
            "SO2609-20009,980.00,pending,2026-09-01 12:01:15,普通"
        ];
        for (int i = 0; i < rows; i++)
        {
            text.Append(i < head.Length
                ? head[i]
                : $"SO2609-{21000 + i},{100 + i % 900}.00,{(i % 3 == 0 ? "shipped" : "paid")},2026-09-0{1 + i % 9} 1{i % 10}:{i % 60:00}:00,{(i % 4 == 0 ? "VIP" : "普通")}");
            text.Append("\r\n");
        }
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }
}
