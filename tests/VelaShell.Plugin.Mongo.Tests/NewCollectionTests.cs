using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 新建集合(设计稿 17):名称校验、命令拼装与预览,以及在自建的 <c>velashell_newcoll_*</c> 库里真建
/// 普通 / 时序 / 固定 / 聚簇 / 视图五种集合、复制索引;截图那一张只填表不建。
/// </summary>
[TestClass]
public sealed class NewCollectionTests
{
    /// <summary>集合名的硬性规则:空、首尾空白、<c>$</c>、<c>system.</c>、点号收尾、超长、重名。</summary>
    [TestMethod]
    public void Name_validation_follows_server_rules()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "orders" };
        Assert.AreEqual(CollectionNameState.Empty, NewCollectionDialogViewModel.ValidateName("", "shop", existing, out _));
        Assert.AreEqual(CollectionNameState.Available, NewCollectionDialogViewModel.ValidateName("device_metrics", "shop", existing, out _));
        Assert.AreEqual(CollectionNameState.Exists, NewCollectionDialogViewModel.ValidateName("orders", "shop", existing, out _));
        Assert.AreEqual(CollectionNameState.Invalid, NewCollectionDialogViewModel.ValidateName("a$b", "shop", existing, out string? dollar));
        Assert.AreEqual("NewColl_NameDollar", dollar);
        Assert.AreEqual(CollectionNameState.Invalid, NewCollectionDialogViewModel.ValidateName("system.x", "shop", existing, out _));
        Assert.AreEqual(CollectionNameState.Invalid, NewCollectionDialogViewModel.ValidateName(" padded", "shop", existing, out _));
        Assert.AreEqual(CollectionNameState.Invalid, NewCollectionDialogViewModel.ValidateName("trailing.", "shop", existing, out _));
        Assert.AreEqual(CollectionNameState.Invalid, NewCollectionDialogViewModel.ValidateName(new string('x', 260), "shop", existing, out string? tooLong));
        Assert.AreEqual("NewColl_NameTooLong", tooLong);
        Assert.AreEqual(CollectionNameState.Available, NewCollectionDialogViewModel.ValidateName("logs.2026", "shop", existing, out _));
    }

    /// <summary>时序选项拼成 <c>create</c> 命令,预览与之同形(90 天 = 7,776,000 秒,写成 Int32)。</summary>
    [TestMethod]
    public void Timeseries_command_and_preview_agree() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var vm = new NewCollectionDialogViewModel(bench.Session, "shop")
        {
            Name = "device_metrics",
            Kind = NewCollectionKind.TimeSeries,
            TimeField = "ts",
            MetaField = "device",
            Granularity = "minutes",
            ExpireValue = "90"
        };

        BsonDocument command = vm.BuildCreateCommand();

        Assert.AreEqual("device_metrics", command["create"].AsString);
        Assert.AreEqual("ts", command["timeseries"]["timeField"].AsString);
        Assert.AreEqual("device", command["timeseries"]["metaField"].AsString);
        Assert.AreEqual("minutes", command["timeseries"]["granularity"].AsString);
        Assert.AreEqual(BsonType.Int32, command["expireAfterSeconds"].BsonType);
        Assert.AreEqual(7_776_000, command["expireAfterSeconds"].AsInt32);
        Assert.AreEqual("= 7,776,000 s", vm.ExpireSecondsText);
        StringAssert.StartsWith(vm.Preview, "db.createCollection(\n  \"device_metrics\",");
        StringAssert.Contains(vm.Preview, "expireAfterSeconds: 7776000");
        Assert.IsTrue(vm.CanCreate, vm.Error);
        Assert.AreEqual(CollectionNameState.Available, vm.NameState);
    });

    /// <summary>选项错误会拦住创建并显示在底栏。</summary>
    [TestMethod]
    public void Option_errors_block_creation() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var vm = new NewCollectionDialogViewModel(bench.Session, "shop") { Name = "x_new", Kind = NewCollectionKind.TimeSeries, TimeField = "" };
        Assert.IsFalse(vm.CanCreate);
        Assert.IsTrue(vm.HasError);
        vm.TimeField = "ts";
        vm.MetaField = "ts";
        Assert.IsFalse(vm.CanCreate);
        vm.MetaField = "";
        Assert.IsTrue(vm.CanCreate, vm.Error);

        vm.Kind = NewCollectionKind.View;
        vm.PipelineText = "[ { $match: { a: 1 } ";
        Assert.IsFalse(vm.CanCreate);
        vm.PipelineText = "[ { $match: { a: 1 } } ]";
        Assert.IsTrue(vm.CanCreate, vm.Error);

        await Screens.PumpAsync(30);
        vm.Name = "orders";
        Assert.AreEqual(CollectionNameState.Exists, vm.NameState, "the existing list is loaded from the server, not only from the tree");
        Assert.IsFalse(vm.CanCreate);
    });

    /// <summary>五种形态在服务器上真建出来,选项落到 <c>listCollections</c> 里;复制索引逐个建到新集合上。</summary>
    [TestMethod]
    public void Creates_every_kind_on_the_server() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_newcoll_{Guid.NewGuid():N}"[..28];
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        IMongoDatabase database = bench.Connection.Database(db);
        try
        {
            IMongoCollection<BsonDocument> source = database.GetCollection<BsonDocument>("source");
            await source.InsertOneAsync(new BsonDocument { { "sku", "a" }, { "at", DateTime.UtcNow } });
            _ = await source.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("sku", 1), new CreateIndexOptions { Name = "sku_1", Unique = true }));

            await CreateAsync(bench, db, vm => { vm.Name = "plain"; vm.CopyIndexes = true; vm.CopyIndexesFrom = "source"; });
            await CreateAsync(bench, db, vm => { vm.Name = "metrics"; vm.Kind = NewCollectionKind.TimeSeries; vm.TimeField = "ts"; vm.MetaField = "device"; vm.Granularity = "hours"; vm.ExpireValue = "2"; vm.ExpireUnit = vm.ExpireUnits[2]; });
            await CreateAsync(bench, db, vm => { vm.Name = "ring"; vm.Kind = NewCollectionKind.Capped; vm.CappedSize = "1"; vm.CappedMax = "100"; });
            await CreateAsync(bench, db, vm => { vm.Name = "clustered"; vm.Kind = NewCollectionKind.Clustered; vm.UseCollation = true; vm.CollationLocale = "zh"; });
            await CreateAsync(bench, db, vm => { vm.Name = "v_source"; vm.Kind = NewCollectionKind.View; vm.ViewSource = "source"; vm.PipelineText = "[ { $project: { sku: 1 } } ]"; });

            List<BsonDocument> infos = await (await database.ListCollectionsAsync()).ToListAsync();
            BsonDocument Options(string name) => infos.Single(i => i["name"].AsString == name).GetValue("options", new BsonDocument()).AsBsonDocument;

            Assert.AreEqual("hours", Options("metrics")["timeseries"]["granularity"].AsString);
            Assert.AreEqual(7200L, Options("metrics")["expireAfterSeconds"].ToInt64());
            Assert.IsTrue(Options("ring")["capped"].AsBoolean);
            Assert.AreEqual(100L, Options("ring")["max"].ToInt64());
            Assert.IsTrue(Options("clustered").Contains("clusteredIndex"));
            Assert.AreEqual("zh", Options("clustered")["collation"]["locale"].AsString);
            Assert.AreEqual("source", Options("v_source")["viewOn"].AsString);

            List<BsonDocument> copied = await (await database.GetCollection<BsonDocument>("plain").Indexes.ListAsync()).ToListAsync();
            BsonDocument sku = copied.Single(static i => i["name"].AsString == "sku_1");
            Assert.IsTrue(sku["unique"].ToBoolean());
        }
        finally
        {
            await bench.Connection.Client.DropDatabaseAsync(db);
        }
    });

    /// <summary>只读模式下点「创建集合」什么也不发。</summary>
    [TestMethod]
    public void Read_only_mode_blocks_creation() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_newcoll_{Guid.NewGuid():N}"[..28];
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.Guard.IsReadOnly = true;
        var vm = new NewCollectionDialogViewModel(bench.Session, db) { Name = "never" };
        await vm.CreateAsync();
        List<string> names = await (await bench.Connection.Database(db).ListCollectionNamesAsync()).ToListAsync();
        Assert.AreEqual(0, names.Count);
    });

    /// <summary>设计稿 17:时序集合填好(device_metrics),不真建。</summary>
    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board17_new_collection_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        var vm = new NewCollectionDialogViewModel(bench.Session, "shop")
        {
            Name = "device_metrics",
            Kind = NewCollectionKind.TimeSeries,
            TimeField = "ts",
            MetaField = "device",
            Granularity = "minutes",
            ExpireValue = "90"
        };
        bench.ViewModel.ShowDialog(vm);
        await Screens.PumpAsync(60);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "17-newcollection");

        Assert.IsNotNull(frame);
        Assert.IsTrue(vm.CanCreate, vm.Error);
    });

    private static async Task CreateAsync(Workbench bench, string db, Action<NewCollectionDialogViewModel> fill)
    {
        var vm = new NewCollectionDialogViewModel(bench.Session, db);
        await Screens.PumpAsync(10);
        fill(vm);
        await Screens.PumpAsync(10);
        Assert.IsTrue(vm.CanCreate, vm.Error);
        await vm.CreateAsync();
        Assert.IsFalse(vm.HasError, vm.Error);
    }
}
