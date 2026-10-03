using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 导出:各格式写出器(JSON / Shell / BSON 转储)的往返、导出执行器对真实服务器的流式导出
/// (CSV / Excel / JSON / mongodump 兼容转储,能被驱动与 mongorestore 读回)、向导流程与设计稿 19 截图。
/// 写服务器的测试只碰自己建的 <c>velashell_transfer_*</c> 库,收尾 drop 掉;<c>shop</c> 只读。
/// </summary>
[TestClass]
public sealed class ExportTests
{
    private static readonly BsonDocument Tricky = new()
    {
        { "_id", ObjectId.Parse("66f5c2a1b04e1c3a5d7e9f01") },
        { "total", new BsonDecimal128(Decimal128.Parse("1299.00")) },
        { "big", new BsonInt64(9_007_199_254_740_993) },
        { "ratio", 0.5 },
        { "createdAt", new BsonDateTime(new DateTime(2026, 9, 27, 7, 0, 0, 123, DateTimeKind.Utc)) },
        { "uuid", new BsonBinaryData(Guid.Parse("7f1c2a3b-4d5e-6f70-8192-a3b4c5d6e7f8"), GuidRepresentation.Standard) },
        { "items", new BsonArray { new BsonDocument { { "sku", "SKU-1" }, { "qty", 2 } }, BsonNull.Value } },
        { "nested", new BsonDocument("deep", new BsonDocument("x", "引号\"与{花括号}")) }
    };

    // ── 写出器 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Json_lines_round_trip_through_the_driver_parser()
    {
        foreach (EjsonMode mode in new[] { EjsonMode.Relaxed, EjsonMode.Canonical, EjsonMode.Shell })
        {
            string text = WriteText(output => new JsonExportWriter(output, new JsonOptions { Lines = true, Mode = mode }), Tricky, Tricky);
            string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.HasCount(2, lines, mode.ToString());
            Assert.AreEqual(Tricky, BsonDocument.Parse(lines[0]), $"{mode} must parse back to the same document");
        }
    }

    [TestMethod]
    public void Json_array_is_a_valid_array_even_when_empty()
    {
        string text = WriteText(output => new JsonExportWriter(output, new JsonOptions { Lines = false }), Tricky, Tricky);
        BsonArray array = MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(text);
        Assert.HasCount(2, array);
        Assert.AreEqual(Tricky, array[1]);

        string empty = WriteText(output => new JsonExportWriter(output, new JsonOptions { Lines = false }));
        Assert.HasCount(0, MongoDB.Bson.Serialization.BsonSerializer.Deserialize<BsonArray>(empty));
    }

    [TestMethod]
    public void Shell_script_batches_insert_many_statements()
    {
        string text = WriteText(output => new ShellExportWriter(output, "shop", new ShellOptions { BatchSize = 2 }), Tricky, Tricky, Tricky);
        Assert.AreEqual(2, CountOf(text, ".insertMany(["), "3 documents in batches of 2 = 2 statements");
        Assert.AreEqual(2, CountOf(text, "]);"));
        StringAssert.Contains(text, "db.getSiblingDB(\"shop\").getCollection(\"orders\")");
        StringAssert.Contains(text, "NumberDecimal(\"1299.00\")");
        StringAssert.Contains(text, "ObjectId(\"66f5c2a1b04e1c3a5d7e9f01\")");
        StringAssert.Contains(text, "ISODate(\"2026-09-27T07:00:00.123Z\")");
    }

    [TestMethod]
    public void Bson_dump_files_read_back_document_by_document()
    {
        string dir = TempDir();
        try
        {
            foreach (bool gzip in new[] { false, true })
            {
                string path = BsonDump.DataPath(dir, "shop", "orders", gzip);
                using (Stream output = BsonDump.OpenWrite(path, gzip))
                {
                    BsonDump.Write(output, Tricky);
                    BsonDump.Write(output, new BsonDocument("_id", 2));
                }
                using Stream input = BsonDump.OpenRead(path);
                Assert.AreEqual(Tricky, BsonDump.Read(input));
                Assert.AreEqual(new BsonDocument("_id", 2), BsonDump.Read(input));
                Assert.IsNull(BsonDump.Read(input));
                Assert.AreEqual(2, BsonDump.Count(path));
            }
            Assert.IsTrue(File.Exists(Path.Combine(dir, "shop", "orders.bson")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "shop", "orders.bson.gz")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Truncated_bson_is_reported_not_silently_dropped()
    {
        byte[] bytes = Tricky.ToBson();
        using var stream = new MemoryStream(bytes[..^3]);
        Assert.ThrowsExactly<InvalidDataException>(() => BsonDump.Read(stream));
    }

    [TestMethod]
    public void Dump_metadata_matches_the_mongodump_shape()
    {
        var info = new CollectionInfo("shop", "orders", CollectionKind.Collection,
            new BsonDocument("validator", new BsonDocument("$jsonSchema", new BsonDocument("required", new BsonArray { "orderNo" }))));
        BsonDocument[] indexes =
        [
            new() { { "v", 2 }, { "key", new BsonDocument("_id", 1) }, { "name", "_id_" }, { "ns", "shop.orders" } },
            new() { { "v", 2 }, { "key", new BsonDocument("orderNo", 1) }, { "name", "orderNo_1" }, { "unique", true } }
        ];
        var uuid = Guid.Parse("8f3b0c1a-0000-4000-8000-00000000abcd");
        BsonDocument metadata = BsonDocument.Parse(BsonDump.MetadataJson(BsonDump.Metadata(info, indexes, uuid)));
        CollectionAssert.AreEqual(new[] { "options", "indexes", "uuid", "collectionName", "type" }, metadata.Names.ToArray());
        Assert.AreEqual("orders", metadata["collectionName"].AsString);
        Assert.AreEqual("collection", metadata["type"].AsString);
        Assert.AreEqual(uuid.ToString("N"), metadata["uuid"].AsString, "uuid is plain hex like mongodump writes it");
        Assert.IsFalse(metadata["indexes"][0].AsBsonDocument.Contains("ns"), "ns is stripped from index specs");
        Assert.IsTrue(metadata["indexes"][1]["unique"].AsBoolean);
        StringAssert.Contains(BsonDump.MetadataJson(metadata), "\"$numberInt\"", "canonical EJSON like mongodump");
    }

    // ── 导出执行器(真实服务器) ───────────────────────────────────────────

    [TestMethod]
    public async Task Runner_streams_csv_excel_json_and_dump_from_a_real_collection()
    {
        await TestServer.RequireAsync();
        await using MongoConnection connection = await TestServer.OpenAsync();
        string db = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        string dir = TempDir();
        try
        {
            IMongoCollection<BsonDocument> orders = connection.Collection(db, "orders");
            await orders.InsertManyAsync(Enumerable.Range(1, 2500).Select(i => new BsonDocument
            {
                { "orderNo", $"SO-{i:00000}" },
                { "customer", new BsonDocument { { "name", $"客户{i % 7}" }, { "level", i % 3 == 0 ? "VIP" : "普通" } } },
                { "total", new BsonDecimal128(Decimal128.Parse($"{i}.5")) },
                { "status", i % 2 == 0 ? "paid" : "pending" }
            }));
            await orders.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("orderNo", 1), new CreateIndexOptions { Unique = true }));
            IReadOnlyList<CollectionInfo> collections = await connection.ListCollectionsAsync(db);
            CollectionInfo info = collections.Single(static c => c.Name == "orders");
            var filter = new BsonDocument("status", "paid");
            ExportColumn[] columns =
            [
                new("orderNo", "订单号", CellConversion.None), new("customer.name", "客户", CellConversion.None),
                new("total", "金额", CellConversion.Fixed2)
            ];

            // CSV:筛选生效、表头是改过的列名、流式写出 1,250 行
            string csv = Path.Combine(dir, "orders.csv");
            var progress = new List<ExportProgress>();
            ExportResult csvResult = await ExportRunner.RunAsync(connection, new ExportJob
            {
                Database = db, Sources = [new ExportSource(info, columns)], Format = ExportFormat.Csv, Target = csv,
                Filter = filter, Sort = new BsonDocument("orderNo", 1), EstimatedTotal = 1250
            }, new SyncProgress<ExportProgress>(progress.Add), CancellationToken.None);
            Assert.AreEqual(1250, csvResult.Documents);
            Assert.IsFalse(File.Exists(csv + ".part"), "the temp file is renamed when the export completes");
            string[] lines = await File.ReadAllLinesAsync(csv, Encoding.UTF8);
            Assert.AreEqual("订单号,客户,金额", lines[0].TrimStart('﻿'));
            Assert.AreEqual("SO-00002,客户2,2.50", lines[1]);
            Assert.HasCount(1251, lines);
            Assert.AreEqual(1250, progress[^1].Documents);

            // Excel:能被 ZipArchive 读回,行数 = 表头 + 1,250
            string xlsx = Path.Combine(dir, "orders.xlsx");
            await ExportRunner.RunAsync(connection, new ExportJob
            {
                Database = db, Sources = [new ExportSource(info, columns)], Format = ExportFormat.Excel, Target = xlsx, Filter = filter
            }, null, CancellationToken.None);
            using (ZipArchive zip = ZipFile.OpenRead(xlsx))
            {
                using Stream sheet = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
                var xml = System.Xml.Linq.XDocument.Load(sheet);
                Assert.AreEqual(1251, xml.Descendants(System.Xml.Linq.XName.Get("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")).Count());
            }

            // JSON(Canonical):每行都能被驱动读回成原文档
            string json = Path.Combine(dir, "orders.jsonl");
            await ExportRunner.RunAsync(connection, new ExportJob
            {
                Database = db, Sources = [new ExportSource(info)], Format = ExportFormat.Json, Target = json,
                Json = new JsonOptions { Mode = EjsonMode.Canonical }
            }, null, CancellationToken.None);
            string[] jsonLines = await File.ReadAllLinesAsync(json);
            Assert.HasCount(2500, jsonLines);
            BsonDocument first = BsonDocument.Parse(jsonLines[0]);
            BsonDocument stored = await orders.Find(new BsonDocument("_id", first["_id"])).FirstAsync();
            Assert.AreEqual(stored, first);

            // BSON 转储:数据文件被驱动读回,元数据带上唯一索引
            string dump = Path.Combine(dir, "dump");
            ExportResult dumpResult = await ExportRunner.RunAsync(connection, new ExportJob
            {
                Database = db, Sources = [new ExportSource(info)], Format = ExportFormat.BsonDump, Target = dump, TargetIsFolder = true,
                Dump = new DumpOptions { Gzip = true }
            }, null, CancellationToken.None);
            Assert.HasCount(2, dumpResult.Files);
            Assert.AreEqual(2500, BsonDump.Count(BsonDump.DataPath(dump, db, "orders", gzip: true)));
            await using (Stream metadataStream = BsonDump.OpenRead(BsonDump.MetadataPath(dump, db, "orders", gzip: true)))
            using (var reader = new StreamReader(metadataStream))
            {
                BsonDocument metadata = BsonDocument.Parse(await reader.ReadToEndAsync());
                Assert.IsTrue(metadata["indexes"].AsBsonArray.Any(static i => i["name"] == "orderNo_1" && i["unique"].ToBoolean()));
                Assert.AreEqual(32, metadata["uuid"].AsString.Length);
            }

            // mongorestore 能直接恢复(装了工具才验;没装就只验到上面为止)
            if (FindTool("mongorestore") is { } mongorestore)
            {
                string restored = db[..^2] + "rr";
                var start = new ProcessStartInfo(mongorestore,
                    $"--uri=\"{TestServer.Uri}\" --gzip --nsFrom=\"{db}.*\" --nsTo=\"{restored}.*\" --dir=\"{dump}\"")
                {
                    RedirectStandardError = true
                };
                using Process process = Process.Start(start)!;
                string log = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                try
                {
                    Assert.AreEqual(0, process.ExitCode, log);
                    long count = await connection.Collection(restored, "orders").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
                    Assert.AreEqual(2500, count);
                    IReadOnlyList<BsonDocument> restoredIndexes = await connection.ListIndexesAsync(restored, "orders");
                    Assert.IsTrue(restoredIndexes.Any(static i => i["name"] == "orderNo_1"), "mongorestore rebuilt the indexes from our metadata");
                }
                finally
                {
                    await connection.Client.DropDatabaseAsync(restored);
                }
            }
        }
        finally
        {
            await connection.Client.DropDatabaseAsync(db);
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public async Task Cancelling_an_export_leaves_no_partial_file()
    {
        await TestServer.RequireAsync();
        await using MongoConnection connection = await TestServer.OpenAsync();
        string dir = TempDir();
        try
        {
            IReadOnlyList<CollectionInfo> collections = await connection.ListCollectionsAsync("shop");
            CollectionInfo orders = collections.Single(static c => c.Name == "orders");
            string target = Path.Combine(dir, "orders.json");
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => ExportRunner.RunAsync(connection, new ExportJob
            {
                Database = "shop", Sources = [new ExportSource(orders)], Format = ExportFormat.Json, Target = target
            }, null, cts.Token));
            Assert.IsFalse(File.Exists(target));
            Assert.IsFalse(File.Exists(target + ".part"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── 向导 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Wizard_builds_the_field_table_preview_and_estimate_from_shop_orders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var query = new FindRequest
        {
            Database = "shop",
            Collection = "orders",
            Filter = new BsonDocument { { "status", "paid" }, { "total", new BsonDocument("$gte", 500) } }
        };
        var vm = new ExportWizardViewModel(bench.Session, "shop", "orders", query, null);
        await WaitAsync(() => !vm.Loading && vm.Fields.Count > 0);
        Assert.IsTrue(vm.IsCsv, "single collection defaults to CSV");
        Assert.IsTrue(vm.UseQuery);
        Assert.IsTrue(vm.Fields.Any(static f => f.Path == "customer.name"), "nested documents are flattened for CSV");
        Assert.IsFalse(vm.Fields.Single(static f => f.Path == "items").Include, "arrays are off by default for CSV");
        Assert.IsTrue(vm.PreviewLines[0].IsHeader);
        Assert.IsTrue(vm.PreviewLines.Count >= 2);
        StringAssert.StartsWith(vm.EstimateDocs, "≈ ");

        vm.Format = ExportFormat.Json;
        Assert.IsFalse(vm.Fields.Any(static f => f.Path == "customer.name"), "JSON keeps top-level fields only");
        BsonDocument.Parse(vm.PreviewLines[0].Text); // 预览是合法的 EJSON

        vm.Format = ExportFormat.BsonDump;
        Assert.IsFalse(vm.ShowFieldTable);
        Assert.IsTrue(vm.TargetIsFolder);
        StringAssert.EndsWith(vm.PreviewLines[0].Text, "orders.bson");
    });

    [TestMethod]
    public void Wizard_exports_end_to_end_into_a_file() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        string dir = TempDir();
        try
        {
            var vm = new ExportWizardViewModel(bench.Session, "shop", "customers", null, ExportFormat.Excel);
            bench.ViewModel.ShowDialog(vm);
            await WaitAsync(() => !vm.Loading && vm.Fields.Count > 0);
            vm.NextCommand.Execute(null);
            await WaitAsync(() => vm.IsStep1);
            vm.NextCommand.Execute(null);
            await WaitAsync(() => vm.IsStep2);
            vm.TargetPath = Path.Combine(dir, "customers.xlsx");
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "19-export-target");
            vm.NextCommand.Execute(null);
            await WaitAsync(() => vm.IsFinished, 20_000);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "19-export-done");
            Assert.IsTrue(vm.ResultOk, vm.ResultText);
            Assert.IsTrue(File.Exists(vm.TargetPath));
            using ZipArchive zip = ZipFile.OpenRead(vm.TargetPath);
            Assert.IsNotNull(zip.GetEntry("xl/workbook.xml"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [TestMethod]
    public void Saved_profiles_restore_format_options_and_field_settings() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var first = new ExportWizardViewModel(bench.Session, "shop", "orders", null, null);
        await WaitAsync(() => !first.Loading && first.Fields.Count > 0);
        first.Delimiter = first.Delimiters.Single(static d => (char)d.Value == ';');
        ExportFieldRow status = first.Fields.Single(static f => f.Path == "status");
        status.Header = "状态";
        first.Fields.Single(static f => f.Path == "orderNo").Include = false;
        first.MoveField(first.Fields.IndexOf(status), 0);
        first.SaveProfileCommand.Execute(null);
        await WaitAsync(() => first.Profiles.Count > 0);

        var second = new ExportWizardViewModel(bench.Session, "shop", "orders", null, ExportFormat.Json);
        await WaitAsync(() => !second.Loading && second.Fields.Count > 0 && second.Profiles.Count > 0);
        second.Profile = second.Profiles[0];
        Assert.IsTrue(second.IsCsv, "the profile switches the format back to CSV");
        Assert.AreEqual(';', (char)second.Delimiter.Value);
        Assert.AreEqual("status", second.Fields[0].Path, "column order is restored");
        Assert.AreEqual("状态", second.Fields[0].Header);
        Assert.IsFalse(second.Fields.Single(static f => f.Path == "orderNo").Include);
        StringAssert.StartsWith(second.PreviewLines[0].Text, "状态;");
    });

    [TestMethod]
    public void Dump_button_dumps_the_whole_database_in_mongodump_layout() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        string dir = TempDir();
        try
        {
            // 外壳的「转储」按钮:集合为 null、预选 BSON 转储 → 整库多选、目标是目录。
            var vm = new ExportWizardViewModel(bench.Session, "shop", null, null, ExportFormat.BsonDump);
            bench.ViewModel.ShowDialog(vm);
            await WaitAsync(() => !vm.Loading && vm.Sources.Count > 0);
            Assert.IsTrue(vm.IsMulti);
            Assert.IsTrue(vm.IsDump);
            Assert.IsTrue(vm.Sources.All(static s => s.IsChecked), "a dump takes the whole database by default");
            Assert.IsFalse(vm.Sources.Any(static s => s.Name.StartsWith("system.", StringComparison.Ordinal)));
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "19-export-dump");
            vm.NextCommand.Execute(null);
            await WaitAsync(() => vm.IsStep1);
            vm.NextCommand.Execute(null);
            await WaitAsync(() => vm.IsStep2);
            Assert.IsTrue(vm.TargetIsFolder);
            vm.TargetPath = dir;
            vm.NextCommand.Execute(null);
            await WaitAsync(() => vm.IsFinished, 30_000);
            Assert.IsTrue(vm.ResultOk, vm.ResultText);
            string shop = Path.Combine(dir, "shop");
            Assert.IsTrue(File.Exists(Path.Combine(shop, "orders.bson")));
            Assert.IsTrue(File.Exists(Path.Combine(shop, "orders.metadata.json")));
            Assert.IsTrue(File.Exists(Path.Combine(shop, "fs.chunks.bson")), "GridFS buckets are plain collections in a dump");
            Assert.IsFalse(File.Exists(Path.Combine(shop, "v_order_summary.bson")), "views have no data file");
            Assert.IsTrue(File.Exists(Path.Combine(shop, "v_order_summary.metadata.json")));
            long orders = await bench.Connection.Collection("shop", "orders").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(orders, BsonDump.Count(Path.Combine(shop, "orders.bson")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board19_export_wizard_screenshot() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var query = new FindRequest
        {
            Database = "shop",
            Collection = "orders",
            Filter = new BsonDocument { { "status", "paid" }, { "total", new BsonDocument("$gte", 500) } }
        };
        var vm = new ExportWizardViewModel(bench.Session, "shop", "orders", query, null);
        bench.ViewModel.ShowDialog(vm);
        await WaitAsync(() => !vm.Loading && vm.Fields.Count > 0);
        // 与设计稿同一组列名(用户在字段表里改过的样子)
        var headers = new Dictionary<string, string>
        {
            ["_id"] = "id", ["orderNo"] = "订单号", ["customer.name"] = "客户", ["customer.level"] = "等级",
            ["total"] = "金额", ["status"] = "状态", ["createdAt"] = "下单时间"
        };
        foreach (ExportFieldRow row in vm.Fields)
        {
            if (headers.TryGetValue(row.Path, out string? header))
            {
                row.Header = header;
                row.Include = true;
            }
            else
            {
                row.Include = false;
            }
        }
        string[] order = ["_id", "orderNo", "customer.name", "customer.level", "total", "status", "createdAt", "items", "note"];
        for (int target = 0; target < order.Length; target++)
        {
            int from = vm.Fields.ToList().FindIndex(f => f.Path == order[target]);
            if (from >= 0 && from != target && target < vm.Fields.Count)
            {
                vm.MoveField(from, target);
            }
        }
        await Screens.PumpAsync(20);
        Screens.Capture(bench.Window, "19-export-source");
        vm.NextCommand.Execute(null);
        await WaitAsync(() => vm.IsStep1);
        await Screens.PumpAsync(40);
        WriteableBitmap? frame = Screens.Capture(bench.Window, "19-export");
        Assert.IsNotNull(frame);
        Assert.AreEqual(7, vm.IncludedCount);
    });

    // ── 工具 ─────────────────────────────────────────────────────────────

    internal static async Task WaitAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("Timed out waiting for the wizard.");
            }
            await Screens.PumpAsync(5);
        }
    }

    internal static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "velashell-transfer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string? FindTool(string name)
    {
        string exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static string WriteText(Func<Stream, ExportWriter> create, params BsonDocument[] documents)
    {
        var memory = new MemoryStream();
        using (ExportWriter writer = create(memory))
        {
            writer.Begin("orders", []);
            foreach (BsonDocument document in documents)
            {
                writer.Write(document);
            }
            writer.End();
            writer.Complete();
        }
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>同步回调的进度(<see cref="Progress{T}" /> 会投递到同步上下文,单测里要的是立刻记下)。</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
