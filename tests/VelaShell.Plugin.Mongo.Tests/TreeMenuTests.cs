using Avalonia.Controls;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 对象树的右键菜单:「集合 / 视图 / GridFS 存储桶」三个分组(以及其中的对象)上都能新建对应的东西,
/// 桶上能直接上传与删除。写路径只在自建的临时库里做,收尾 drop 掉。
/// </summary>
[TestClass]
public sealed class TreeMenuTests
{
    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Screens.PumpAsync(5);
        }
    }

    private static string[] Labels(Workbench bench, TreeNode node) =>
        [.. bench.View.TreeMenuItems(node).OfType<MenuItem>().Select(static m => m.Header as string ?? "")];

    private static async Task<TreeNode> DatabaseNodeAsync(Workbench bench, string db)
    {
        TreeNode node = bench.Session.Root.Children.First(n => n.Name == db);
        if (!node.IsLoaded)
        {
            await bench.Session.ExpandDatabaseAsync(node);
        }
        return node;
    }

    private static TreeNode Folder(TreeNode db, FolderKind kind) => db.Children.Single(n => n.Kind == NodeKind.Folder && n.Folder == kind);

    [TestMethod]
    public void Every_group_offers_creating_its_own_kind_of_object() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        Loc loc = bench.ViewModel.Loc;
        TreeNode db = await DatabaseNodeAsync(bench, Screens.Database);

        CollectionAssert.Contains(Labels(bench, Folder(db, FolderKind.Collections)), loc["Tree_NewCollection"]);
        CollectionAssert.Contains(Labels(bench, Folder(db, FolderKind.Views)), loc["Tree_NewView"]);
        CollectionAssert.Contains(Labels(bench, Folder(db, FolderKind.Buckets)), loc["Tree_NewBucket"]);
        string[] database = Labels(bench, db);
        CollectionAssert.IsSubsetOf(new[] { loc["Tree_NewCollection"], loc["Tree_NewView"], loc["Tree_NewBucket"] }, database);

        TreeNode orders = Folder(db, FolderKind.Collections).Children.First(static n => n.Name == "orders");
        CollectionAssert.IsSubsetOf(new[] { loc["Tree_NewCollection"], loc["Tree_NewView"] }, Labels(bench, orders));
        TreeNode view = Folder(db, FolderKind.Views).Children.First();
        string[] viewMenu = Labels(bench, view);
        CollectionAssert.Contains(viewMenu, loc["Tree_NewView"]);
        CollectionAssert.DoesNotContain(viewMenu, loc["Tree_NewCollection"]);

        TreeNode bucket = Folder(db, FolderKind.Buckets).Children.First();
        CollectionAssert.IsSubsetOf(new[] { loc["Tree_UploadFiles"], loc["Tree_UploadFolder"], loc["Tree_NewBucket"], loc["Tree_DropBucket"] },
            Labels(bench, bucket));
        Assert.IsFalse(bench.View.TreeMenuItems(bucket).LastOrDefault() is Separator, "no dangling separator at the end");
    });

    [TestMethod]
    public void New_view_from_a_collection_lands_on_the_view_card_with_that_source() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        TreeNode db = await DatabaseNodeAsync(bench, Screens.Database);
        TreeNode orders = Folder(db, FolderKind.Collections).Children.First(static n => n.Name == "orders");

        bench.ViewModel.NewViewNodeCommand.Execute(orders);
        await Screens.PumpAsync(20);
        var dialog = (NewCollectionDialogViewModel)bench.ViewModel.Dialog!;
        Assert.IsTrue(dialog.IsView);
        Assert.AreEqual("orders", dialog.ViewSource);
        Assert.AreEqual(bench.ViewModel.Loc["Nav_NewView"], dialog.Title);
        dialog.Close();
        await Screens.PumpAsync(5);

        bench.ViewModel.NewCollectionNodeCommand.Execute(Folder(db, FolderKind.Collections));
        await Screens.PumpAsync(20);
        var plain = (NewCollectionDialogViewModel)bench.ViewModel.Dialog!;
        Assert.IsTrue(plain.IsPlain);
        Assert.AreEqual(Screens.Database, plain.Database);
        plain.Close();
    });

    [TestMethod]
    public void A_bucket_created_from_the_tree_takes_uploads_and_can_be_dropped_from_the_tree() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string db = $"velashell_tree_{Guid.NewGuid():N}"[..26];
        var client = new MongoClient(TestServer.Uri);
        await client.GetDatabase(db).CreateCollectionAsync("seed");
        string local = Path.Combine(Path.GetTempPath(), "tree-upload-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(local, "hello gridfs");
        try
        {
            await using Workbench bench = await Screens.OpenWorkbenchAsync(db);
            TreeNode dbNode = await DatabaseNodeAsync(bench, db);
            Assert.IsEmpty(Folder(dbNode, FolderKind.Buckets).Children);

            // 「GridFS 存储桶」分组上右键 →「新建存储桶…」。
            bench.ViewModel.NewBucketNodeCommand.Execute(Folder(dbNode, FolderKind.Buckets));
            await Screens.PumpAsync(10);
            var create = (GridFsNewBucketDialogViewModel)bench.ViewModel.Dialog!;
            create.Name = "images";
            await create.CreateCommand.ExecuteAsync();
            await WaitAsync(() => bench.ViewModel.ActiveTab is GridFsTabViewModel { IsLoading: false });

            dbNode = await DatabaseNodeAsync(bench, db);
            TreeNode bucket = Folder(dbNode, FolderKind.Buckets).Children.Single();
            Assert.AreEqual("images", bucket.Name);
            var tab = (GridFsTabViewModel)bench.ViewModel.ActiveTab!;
            Assert.AreEqual("images", tab.Bucket.Name, "creating the bucket opens it");

            // 上传走标签里同一条路径(桶行的「上传文件…」也是先打开这个标签再调它)。
            Assert.IsTrue(bench.ViewModel.UploadFilesNodeCommand.CanExecute(bucket));
            await tab.UploadPathsAsync([local], ask: false);
            await WaitAsync(() => !tab.IsTransferring && tab.Entries.Count > 0);
            long files = await client.GetDatabase(db).GetCollection<BsonDocument>("images.files")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(1L, files);

            // 桶行右键 →「删除存储桶…」:.files 与 .chunks 一起删掉,标签关掉,树上没了。
            Task drop = bench.ViewModel.DropCollectionAsync(bucket);
            await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel || drop.IsCompleted);
            var confirm = (ConfirmDialogViewModel)bench.ViewModel.Dialog!;
            Assert.IsTrue(confirm.RequiresTyping, "a bucket with files asks for its name");
            confirm.Typed = confirm.Request.TypeToConfirm!;
            confirm.ConfirmCommand.Execute(null);
            await drop;
            await Screens.PumpAsync(10);

            List<string> left = await (await client.GetDatabase(db).ListCollectionNamesAsync()).ToListAsync();
            CollectionAssert.DoesNotContain(left, "images.files");
            CollectionAssert.DoesNotContain(left, "images.chunks");
            Assert.IsFalse(bench.ViewModel.Tabs.OfType<GridFsTabViewModel>().Any(), "the bucket's tab is closed");
            dbNode = await DatabaseNodeAsync(bench, db);
            Assert.IsEmpty(Folder(dbNode, FolderKind.Buckets).Children);
        }
        finally
        {
            File.Delete(local);
            await client.DropDatabaseAsync(db);
        }
    });
}
