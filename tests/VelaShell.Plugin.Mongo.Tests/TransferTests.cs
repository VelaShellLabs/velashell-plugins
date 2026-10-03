using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 数据传输:建集合命令的选项清洗、暂停闸、集合 / 视图 / GridFS 桶在真实服务器上的复制
/// (覆盖、追加、跳过、索引与验证规则),向导的整条流程与设计稿 21 截图。
/// 只在自己建的 <c>velashell_transfer_*</c> 库之间复制(读 <c>shop</c> 的那个测试只读不写),收尾 drop 掉。
/// </summary>
[TestClass]
public sealed class TransferTests
{
    [TestMethod]
    public void Create_command_carries_options_and_drops_what_the_server_would_reject()
    {
        BsonDocument options = BsonDocument.Parse("""
            {
              "capped": true, "size": 4096,
              "validator": { "$jsonSchema": { "required": ["a"] } }, "validationLevel": "moderate",
              "timeseries": { "timeField": "ts", "granularity": "seconds", "bucketMaxSpanSeconds": 3600 },
              "clusteredIndex": { "key": { "_id": 1 }, "unique": true, "v": 2 }
            }
            """);
        BsonDocument with = CollectionCopier.CreateCommand("c", options, new XferOptions());
        Assert.AreEqual("c", with["create"].AsString);
        Assert.IsTrue(with["capped"].AsBoolean);
        Assert.IsTrue(with.Contains("validator"));
        Assert.IsFalse(with["timeseries"].AsBsonDocument.Contains("bucketMaxSpanSeconds"), "granularity and bucketMaxSpanSeconds cannot be combined");
        Assert.IsFalse(with["clusteredIndex"].AsBsonDocument.Contains("v"));

        BsonDocument without = CollectionCopier.CreateCommand("c", options, new XferOptions { Validation = false });
        Assert.IsFalse(without.Contains("validator"));
        Assert.IsFalse(without.Contains("validationLevel"));
    }

    [TestMethod]
    public async Task Pause_gate_blocks_until_resumed()
    {
        var gate = new XferPauseGate();
        await gate.WaitAsync(CancellationToken.None); // 没暂停时直接过
        gate.Pause();
        Assert.IsTrue(gate.IsPaused);
        Task waiting = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.IsFalse(waiting.IsCompleted);
        gate.Resume();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(gate.IsPaused);

        gate.Pause();
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAsync<OperationCanceledException>(() => gate.WaitAsync(cts.Token));
    }

    [TestMethod]
    public async Task Copier_moves_collections_views_and_buckets_between_databases()
    {
        await TestServer.RequireAsync();
        await using MongoConnection source = await TestServer.OpenAsync();
        await using MongoConnection target = await MongoConnection.ConnectAsync(TestServer.Uri, CancellationToken.None);
        string from = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        string to = from[..^2] + "to";
        var loc = new Loc("zh-CN");
        var log = new List<(string Text, XferTone Tone)>();
        try
        {
            // 源:带 validator 与唯一索引的集合、一个视图、一个 GridFS 桶
            await source.RunCommandAsync(from, new BsonDocument
            {
                { "create", "items" },
                { "validator", BsonDocument.Parse("{ $jsonSchema: { required: ['sku'] } }") }
            });
            IMongoCollection<BsonDocument> items = source.Collection(from, "items");
            await items.InsertManyAsync(Enumerable.Range(1, 2345).Select(static i => new BsonDocument { { "sku", $"SKU-{i}" }, { "qty", i } }));
            await items.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("sku", 1), new CreateIndexOptions { Unique = true }));
            await source.RunCommandAsync(from, BsonDocument.Parse("{ create: 'v_items', viewOn: 'items', pipeline: [{ $match: { qty: { $gt: 10 } } }] }"));
            var bucket = new GridFSBucket(source.Database(from), new GridFSBucketOptions { BucketName = "fs", ChunkSizeBytes = 1024 });
            await bucket.UploadFromBytesAsync("a.bin", new byte[5000]);

            IReadOnlyList<CollectionInfo> infos = await source.ListCollectionsAsync(from);
            CollectionInfo itemsInfo = infos.Single(static c => c.Name == "items");
            CollectionInfo viewInfo = infos.Single(static c => c.Name == "v_items");
            var options = new XferOptions { BatchSize = 100, Concurrency = 3 };
            long reported = 0;

            XferOutcome copied = await CollectionCopier.CopyAsync(source, from, target, to, new XferItem("items", XferObjectKind.Collection, itemsInfo, XferAction.Create),
                options, new XferPauseGate(), n => Interlocked.Add(ref reported, n), (t, tone) => log.Add((t, tone)), loc, CancellationToken.None);
            Assert.AreEqual(2345, copied.Copied);
            Assert.AreEqual(2345, reported);
            Assert.AreEqual(1, copied.Indexes);
            CollectionInfo targetItems = (await target.ListCollectionsAsync(to)).Single(static c => c.Name == "items");
            Assert.IsNotNull(targetItems.Validator, "validation rules travel with the collection");
            Assert.IsTrue((await target.ListIndexesAsync(to, "items")).Any(static i => i["name"] == "sku_1" && i["unique"].ToBoolean()));

            await CollectionCopier.CopyAsync(source, from, target, to, new XferItem("v_items", XferObjectKind.View, viewInfo, XferAction.Create),
                options, new XferPauseGate(), _ => { }, (t, tone) => log.Add((t, tone)), loc, CancellationToken.None);
            long viewCount = await target.Collection(to, "v_items").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(2335, viewCount, "the view definition was recreated on the target");

            XferOutcome fs = await CollectionCopier.CopyAsync(source, from, target, to, new XferItem("fs", XferObjectKind.Bucket, null, XferAction.Create),
                options, new XferPauseGate(), _ => { }, (t, tone) => log.Add((t, tone)), loc, CancellationToken.None);
            Assert.AreEqual(6, fs.Copied, "5 chunks + 1 file document");
            byte[] downloaded = await new GridFSBucket(target.Database(to)).DownloadAsBytesByNameAsync("a.bin");
            Assert.HasCount(5000, downloaded);

            // 追加:撞唯一索引的被拒,但不中断;不保留 _id 时全部重新生成
            XferOutcome appended = await CollectionCopier.CopyAsync(source, from, target, to, new XferItem("items", XferObjectKind.Collection, itemsInfo, XferAction.Append),
                options with { KeepId = false }, new XferPauseGate(), _ => { }, (t, tone) => log.Add((t, tone)), loc, CancellationToken.None);
            Assert.AreEqual(2345, appended.Failed, "every sku collides with the unique index");
            Assert.AreEqual(0, appended.Copied);

            // 覆盖:drop + 重建,数量回到源的样子
            await target.Collection(to, "items").InsertOneAsync(new BsonDocument { { "sku", "extra" } });
            XferOutcome overwritten = await CollectionCopier.CopyAsync(source, from, target, to, new XferItem("items", XferObjectKind.Collection, itemsInfo, XferAction.Overwrite),
                options, new XferPauseGate(), _ => { }, (t, tone) => log.Add((t, tone)), loc, CancellationToken.None);
            Assert.AreEqual(2345, overwritten.Copied);
            long after = await target.Collection(to, "items").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(2345, after);

            // 新建:目标已有同名时拒绝(向导会默认改成跳过)
            await Assert.ThrowsAsync<InvalidOperationException>(() => CollectionCopier.CopyAsync(source, from, target, to,
                new XferItem("items", XferObjectKind.Collection, itemsInfo, XferAction.Create), options, new XferPauseGate(), _ => { },
                (t, tone) => log.Add((t, tone)), loc, CancellationToken.None));
            XferOutcome skipped = await CollectionCopier.CopyAsync(source, from, target, to, new XferItem("items", XferObjectKind.Collection, itemsInfo, XferAction.Skip),
                options, new XferPauseGate(), _ => { }, (t, tone) => log.Add((t, tone)), loc, CancellationToken.None);
            Assert.AreEqual(0, skipped.Copied);
        }
        finally
        {
            await source.Client.DropDatabaseAsync(from);
            await source.Client.DropDatabaseAsync(to);
        }
    }

    [TestMethod]
    public void Wizard_defaults_to_skip_for_existing_names_and_runs_to_completion() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        await using MongoConnection second = await TestServer.OpenAsync(new Dictionary<string, string> { ["environment"] = "testing" });
        string to = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        try
        {
            await second.Collection(to, "coupons").InsertOneAsync(new BsonDocument("code", "keep-me"));
            var vm = new TransferWizardViewModel(bench.Session, "shop");
            bench.ViewModel.ShowDialog(vm);
            vm.UseTarget(new TransferTarget("mongo-test-02", second, owned: false));
            vm.TargetDatabase = to;
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep1 && vm.Objects.Count > 0 && !vm.IsBusy, 20_000);

            TransferObjectRow coupons = vm.Objects.Single(static o => o.Name == "coupons");
            Assert.AreEqual(XferAction.Skip, coupons.ActionValue);
            Assert.IsTrue(coupons.HasNote);
            Assert.AreEqual(XferAction.Create, vm.Objects.Single(static o => o.Name == "orders").ActionValue);
            Assert.IsTrue(vm.Objects.Any(static o => o.Kind == XferObjectKind.Bucket && o.Name == "fs"));
            Assert.IsFalse(vm.Objects.Any(static o => o.Name.StartsWith("system.", StringComparison.Ordinal) || o.Name == "fs.files"));

            // 只传几个小的,其余不勾
            string[] wanted = ["customers", "products", "inventory", "coupons", "v_order_summary"];
            foreach (TransferObjectRow row in vm.Objects)
            {
                row.IsChecked = wanted.Contains(row.Name);
            }
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep2);
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsFinished, 30_000);
            Assert.IsTrue(vm.ResultOk, vm.ResultText);

            long shopCustomers = await bench.Connection.Collection("shop", "customers").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            long copiedCustomers = await second.Collection(to, "customers").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(shopCustomers, copiedCustomers);
            long coupons2 = await second.Collection(to, "coupons").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(1, coupons2, "the existing collection was skipped, not touched");
            Assert.AreEqual(TransferObjectState.Skipped, coupons.State);
            Assert.IsTrue(vm.Log.Any(static l => l.Tone == XferTone.Ok));
        }
        finally
        {
            await second.Client.DropDatabaseAsync(to);
        }
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board21_transfer_screenshot() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync(settings: new Dictionary<string, string> { ["environment"] = "production" });
        await using MongoConnection second = await TestServer.OpenAsync(new Dictionary<string, string> { ["environment"] = "testing" });
        string to = $"velashell_transfer_{Guid.NewGuid():N}"[..28];
        try
        {
            await second.Collection(to, "coupons").InsertOneAsync(new BsonDocument("code", "existing"));
            var vm = new TransferWizardViewModel(bench.Session, "shop");
            bench.ViewModel.ShowDialog(vm);
            vm.UseTarget(new TransferTarget("mongo-test-02", second, owned: false));
            vm.TargetDatabase = to;
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "21-transfer-target");
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep1 && vm.Objects.Count > 0 && !vm.IsBusy, 20_000);
            string[] wanted = ["customers", "products", "inventory", "orders", "reviews", "coupons"];
            foreach (TransferObjectRow row in vm.Objects)
            {
                row.IsChecked = wanted.Contains(row.Name) || row.Kind == XferObjectKind.Bucket && row.Name == "fs";
            }
            TransferObjectRow inventory = vm.Objects.Single(static o => o.Name == "inventory");
            inventory.Action = inventory.Actions.Single(static a => (XferAction)a.Value == XferAction.Append);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "21-transfer-objects");
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsStep2);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "21-transfer-options");
            vm.NextCommand.Execute(null);
            await ExportTests.WaitAsync(() => vm.IsFinished, 60_000);
            await Screens.PumpAsync(40);
            WriteableBitmap? frame = Screens.Capture(bench.Window, "21-transfer");
            Assert.IsNotNull(frame);
            Assert.IsTrue(vm.ResultOk, vm.ResultText);
        }
        finally
        {
            await second.Client.DropDatabaseAsync(to);
        }
    });
}
