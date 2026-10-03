using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// GridFS 文件管理(设计稿 06):路径 / 类型 / 搜索规则的纯函数、服务层对真实服务器的读写
/// (上传、下载、改名、删除、版本恢复、metadata、孤儿块、新建桶)、标签页的视图模型流程与截图。
/// 写测试只碰自己建的 <c>velashell_gridfs_*</c> 库,收尾 drop 掉;<c>shop</c> 只读。
/// </summary>
[TestClass]
public sealed class GridFsTests
{
    // ── 纯函数 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Paths_split_into_virtual_directories()
    {
        Assert.AreEqual("main.jpg", GridFsPaths.BaseName("products/SKU-7710/main.jpg"));
        Assert.AreEqual("products/SKU-7710/", GridFsPaths.DirectoryOf("products/SKU-7710/main.jpg"));
        Assert.AreEqual("", GridFsPaths.DirectoryOf("readme.md"));
        Assert.AreEqual("products/", GridFsPaths.Parent("products/SKU-7710/"));
        Assert.AreEqual("", GridFsPaths.Parent("products/"));
        Assert.AreEqual("", GridFsPaths.Parent(""));
        Assert.AreEqual("a/b/", GridFsPaths.NormalizePrefix("/a\\b"));
        Assert.AreEqual("a/b/", GridFsPaths.NormalizePrefix("a//b/"));
        Assert.AreEqual("", GridFsPaths.NormalizePrefix("  / "));
    }

    [TestMethod]
    public void Content_types_and_categories_follow_type_then_extension()
    {
        Assert.AreEqual("image/jpeg", GridFsPaths.GuessContentType("x/y/photo.JPG"));
        Assert.AreEqual("application/octet-stream", GridFsPaths.GuessContentType("blob"));
        Assert.AreEqual(GridFsCategory.Image, GridFsPaths.Categorize("noext", "image/png"));
        Assert.AreEqual(GridFsCategory.Document, GridFsPaths.Categorize("spec.bin", "application/pdf"));
        Assert.AreEqual(GridFsCategory.Video, GridFsPaths.Categorize("360-spin.mp4", null));
        Assert.AreEqual(GridFsCategory.Other, GridFsPaths.Categorize("archive.zip", "application/zip"));
        Assert.AreEqual("4 × 255K", GridFsPaths.ChunkSummary(4, 261120));
        Assert.AreEqual("JPEG", GridFsDetailsViewModel.FormatName("image/jpeg", "a.jpg"));
        Assert.AreEqual("SVG", GridFsDetailsViewModel.FormatName("image/svg+xml", "a.svg"));
    }

    [TestMethod]
    public void Search_box_reads_metadata_key_value_or_file_name()
    {
        BsonDocument byName = GridFsPaths.SearchFilter("main")!;
        Assert.AreEqual("filename", byName.GetElement(0).Name);

        BsonDocument bySku = GridFsPaths.SearchFilter("sku:SKU-7710")!;
        Assert.AreEqual("metadata.sku", bySku.GetElement(0).Name, "a bare key means a metadata field");

        BsonDocument explicitKey = GridFsPaths.SearchFilter("metadata.size: 42")!;
        Assert.AreEqual("$or", explicitKey.GetElement(0).Name, "numbers also match by equality");
        Assert.IsNull(GridFsPaths.SearchFilter("   "));
        Assert.IsNull(GridFsPaths.TypeFilter(GridFsTypeFilter.All));
        Assert.IsNotNull(GridFsPaths.TypeFilter(GridFsTypeFilter.Videos));
    }

    [TestMethod]
    public void Bucket_names_are_validated()
    {
        Assert.IsNull(GridFsPaths.ValidateBucketName("images"));
        Assert.AreEqual("Fs_BucketNameEmpty", GridFsPaths.ValidateBucketName(" "));
        Assert.AreEqual("Fs_BucketNameInvalid", GridFsPaths.ValidateBucketName("a$b"));
        Assert.AreEqual("Fs_BucketNameInvalid", GridFsPaths.ValidateBucketName("trailing."));
        Assert.AreEqual("Fs_BucketNameSystem", GridFsPaths.ValidateBucketName("system.x"));
    }

    [TestMethod]
    public void Download_paths_never_escape_the_target_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "fs-safe-" + Guid.NewGuid().ToString("N"));
        string evil = GridFsTabViewModel.SafeLocalPath(root, "../../windows/evil.dll");
        Assert.IsTrue(evil.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase), evil);
        string nested = GridFsTabViewModel.SafeLocalPath(root, "products/SKU-7710/main.jpg");
        Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "products", "SKU-7710", "main.jpg"), nested);
        Assert.AreEqual("a_b", GridFsTabViewModel.SafeSegment("a:b"));
        Assert.AreEqual("_CON.txt", GridFsTabViewModel.SafeSegment("CON.txt"));
    }

    [TestMethod]
    public void Metadata_template_fills_content_type_per_file()
    {
        var template = new BsonDocument("uploader", "ops-lin");
        BsonDocument jpg = GridFsTabViewModel.MetadataFor(template, "a/b.jpg");
        BsonDocument pdf = GridFsTabViewModel.MetadataFor(template, "c.pdf");
        Assert.AreEqual("image/jpeg", jpg["contentType"].AsString);
        Assert.AreEqual("application/pdf", pdf["contentType"].AsString);
        Assert.AreEqual("ops-lin", pdf["uploader"].AsString);
        Assert.IsFalse(template.Contains("contentType"), "the template itself is not mutated");

        var forced = new BsonDocument { { "contentType", "text/plain" } };
        Assert.AreEqual("text/plain", GridFsTabViewModel.MetadataFor(forced, "c.pdf")["contentType"].AsString);
    }

    [TestMethod]
    public void Folders_expand_recursively_with_relative_paths()
    {
        string root = Path.Combine(Path.GetTempPath(), "fs-up-" + Guid.NewGuid().ToString("N"));
        string folder = Path.Combine(root, "SKU-1");
        Directory.CreateDirectory(Path.Combine(folder, "detail"));
        File.WriteAllBytes(Path.Combine(folder, "main.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(folder, "detail", "d1.jpg"), [4, 5]);
        string loose = Path.Combine(root, "loose.txt");
        File.WriteAllText(loose, "x");
        try
        {
            IReadOnlyList<GridFsUploadItem> items = GridFsTabViewModel.ExpandPaths([folder, loose]);
            CollectionAssert.AreEquivalent(new[] { "SKU-1/main.jpg", "SKU-1/detail/d1.jpg", "loose.txt" }, items.Select(static i => i.Relative).ToArray());
            Assert.AreEqual(3L, items.Single(static i => i.Relative == "SKU-1/main.jpg").Size);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── 服务层(真实服务器) ───────────────────────────────────────────────

    [TestMethod]
    public void Service_lists_virtual_directories_versions_and_filters() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using TempBucket temp = await TempBucket.CreateAsync();
        GridFsService fs = temp.Service;
        await UploadAsync(fs, "products/SKU-1/main.jpg", 1000, new BsonDocument { { "contentType", "image/jpeg" }, { "sku", "SKU-1" } });
        await UploadAsync(fs, "products/SKU-1/main.jpg", 1100, new BsonDocument { { "contentType", "image/jpeg" }, { "sku", "SKU-1" } });
        await UploadAsync(fs, "products/SKU-1/main.jpg", 1200, new BsonDocument { { "contentType", "image/jpeg" }, { "sku", "SKU-1" } });
        await UploadAsync(fs, "products/SKU-1/spec.pdf", 3000, new BsonDocument { { "contentType", "application/pdf" }, { "sku", "SKU-1" } });
        await UploadAsync(fs, "products/SKU-2/clip.mp4", 5000, new BsonDocument("sku", "SKU-2"));
        await UploadAsync(fs, "readme.txt", 10, null);

        GridFsListing root = await fs.ListAsync("", virtualDirs: true, GridFsTypeFilter.All, null);
        Assert.AreEqual(2, root.Items.Count);
        GridFsListItem products = root.Items.Single(static i => i.IsFolder);
        Assert.AreEqual("products", products.Folder);
        Assert.AreEqual(3L, products.Files, "a folder counts distinct file names, recursively");
        Assert.AreEqual(1200L + 3000 + 5000, products.Bytes, "a folder sums the latest version of each file");

        GridFsListing sku1 = await fs.ListAsync("products/SKU-1/", virtualDirs: true, GridFsTypeFilter.All, null);
        GridFsListItem main = sku1.Items.Single(static i => i.Latest?.BaseName == "main.jpg");
        Assert.AreEqual(3, main.Versions);
        Assert.AreEqual(1200L, main.Latest!.Length, "the row shows the newest version");
        Assert.AreEqual("image/jpeg", main.Latest.ContentType);

        GridFsListing flat = await fs.ListAsync("", virtualDirs: false, GridFsTypeFilter.All, null);
        Assert.AreEqual(4, flat.Items.Count, "flat mode lists every file name once");

        GridFsListing images = await fs.ListAsync("", virtualDirs: false, GridFsTypeFilter.Images, null);
        Assert.AreEqual("products/SKU-1/main.jpg", images.Items.Single().Latest!.Filename);
        GridFsListing videos = await fs.ListAsync("", virtualDirs: false, GridFsTypeFilter.Videos, null);
        Assert.AreEqual("products/SKU-2/clip.mp4", videos.Items.Single().Latest!.Filename, "the extension decides when contentType is missing");

        GridFsListing bySku = await fs.ListAsync("", virtualDirs: true, GridFsTypeFilter.All, "sku:SKU-1");
        Assert.AreEqual(2, bySku.Items.Count, "a metadata search lists matches flat, recursively");
        GridFsListing byName = await fs.ListAsync("products/", virtualDirs: true, GridFsTypeFilter.All, "CLIP");
        Assert.AreEqual("products/SKU-2/clip.mp4", byName.Items.Single().Latest!.Filename);

        IReadOnlyList<GridFsFile> versions = await fs.VersionsAsync("products/SKU-1/main.jpg");
        Assert.AreEqual(3, versions.Count);
        Assert.IsTrue(versions[0].UploadDate >= versions[1].UploadDate, "newest first");
    });

    [TestMethod]
    public void Service_round_trips_rename_restore_delete_and_metadata() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using TempBucket temp = await TempBucket.CreateAsync();
        GridFsService fs = temp.Service;

        // 跨块(> 255 KB)的上传与下载,字节逐一比对。
        byte[] v1 = Bytes(700_000, seed: 1);
        byte[] v2 = Bytes(300_000, seed: 2);
        long reported = 0;
        using (var source = new MemoryStream(v1))
        {
            await fs.UploadAsync("docs/a.bin", source, new BsonDocument("note", "first"), new Progress<long>(b => reported = b));
        }
        await Task.Delay(5);
        using (var source = new MemoryStream(v2))
        {
            await fs.UploadAsync("docs/a.bin", source, new BsonDocument("note", "second"));
        }
        IReadOnlyList<GridFsFile> versions = await fs.VersionsAsync("docs/a.bin");
        Assert.AreEqual(2, versions.Count);
        Assert.AreEqual(3L, versions[1].ChunkCount);

        using (var target = new MemoryStream())
        {
            await fs.DownloadAsync(versions[1].Id, target);
            byte[] downloaded = target.ToArray();
            Assert.IsTrue(v1.AsSpan().SequenceEqual(downloaded), "v1 downloads byte-for-byte");
        }

        // 恢复 v1:新写一份,成为最新;旧的两份都还在。
        await fs.RestoreAsync(versions[1]);
        IReadOnlyList<GridFsFile> afterRestore = await fs.VersionsAsync("docs/a.bin");
        Assert.AreEqual(3, afterRestore.Count);
        Assert.AreEqual(v1.Length, afterRestore[0].Length);
        Assert.AreEqual("first", afterRestore[0].Metadata!["note"].AsString, "restore carries the old metadata");
        byte[] restored = await fs.ReadAllAsync(afterRestore[0].Id);
        Assert.IsTrue(v1.AsSpan().SequenceEqual(restored));

        // 改名改全部版本。
        long renamed = await fs.RenameAsync("docs/a.bin", "docs/b.bin");
        Assert.AreEqual(3L, renamed);
        bool oldNameLeft = await fs.ExistsAsync("docs/a.bin");
        Assert.IsFalse(oldNameLeft);
        // 目录改名。
        long moved = await fs.RenameFolderAsync("docs/", "archive/2026/");
        Assert.AreEqual(3L, moved);
        IReadOnlyList<GridFsFile> archived = await fs.VersionsAsync("archive/2026/b.bin");
        Assert.AreEqual(3, archived.Count);

        // metadata:改、删。
        await fs.UpdateMetadataAsync(archived[0].Id, new BsonDocument { { "contentType", "application/x-test" }, { "owner", "qa" } });
        GridFsFile? updated = await fs.GetAsync(archived[0].Id);
        Assert.AreEqual("application/x-test", updated!.ContentType);
        await fs.UpdateMetadataAsync(archived[0].Id, null);
        GridFsFile? cleared = await fs.GetAsync(archived[0].Id);
        Assert.IsNull(cleared!.Metadata);

        // 只删一个版本 vs 删全部版本。
        await fs.DeleteAsync(archived[0].Id);
        IReadOnlyList<GridFsFile> afterOne = await fs.VersionsAsync("archive/2026/b.bin");
        Assert.AreEqual(2, afterOne.Count);
        long chunksBefore = await fs.Chunks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.IsTrue(chunksBefore > 0);
        IReadOnlyList<GridFsFile> remaining = await fs.FilesUnderAsync("archive/", latestOnly: false);
        long deleted = await fs.DeleteManyAsync([.. remaining.Select(static f => f.Id)]);
        Assert.AreEqual(2L, deleted);
        long chunksAfter = await fs.Chunks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.AreEqual(0L, chunksAfter, "chunks go with their files");
        Assert.IsTrue(reported >= v1.Length, "upload progress reaches the full length");
    });

    [TestMethod]
    public void Service_finds_and_cleans_orphan_chunks_and_creates_buckets() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using TempBucket temp = await TempBucket.CreateAsync();
        GridFsService fs = temp.Service;
        using (var source = new MemoryStream(Bytes(1000, seed: 3)))
        {
            await fs.UploadAsync("kept.bin", source, null);
        }
        var ghost = ObjectId.GenerateNewId();
        await fs.Chunks.InsertManyAsync(
        [
            new BsonDocument { { "files_id", ghost }, { "n", 0 }, { "data", new BsonBinaryData(new byte[100]) } },
            new BsonDocument { { "files_id", ghost }, { "n", 1 }, { "data", new BsonBinaryData(new byte[50]) } }
        ]);

        IReadOnlyList<GridFsOrphan> orphans = await fs.FindOrphansAsync();
        Assert.AreEqual(1, orphans.Count);
        Assert.AreEqual(new BsonObjectId(ghost), orphans[0].FilesId);
        Assert.AreEqual(2L, orphans[0].Chunks);
        Assert.AreEqual(150L, orphans[0].Bytes);

        long cleaned = await fs.DeleteOrphansAsync([orphans[0].FilesId]);
        Assert.AreEqual(2L, cleaned);
        IReadOnlyList<GridFsOrphan> after = await fs.FindOrphansAsync();
        Assert.AreEqual(0, after.Count);
        long healthy = await fs.Chunks.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
        Assert.AreEqual(1L, healthy, "the healthy file keeps its chunk");

        await GridFsService.CreateBucketAsync(temp.Connection, temp.Database, "images");
        IReadOnlyList<GridFsBucketInfo> buckets = await GridFsService.ListBucketsAsync(temp.Connection, temp.Database);
        CollectionAssert.Contains(buckets.Select(static b => b.Name).ToList(), "images");
        IReadOnlyList<BsonDocument> chunkIndexes = await temp.Connection.ListIndexesAsync(temp.Database, "images.chunks");
        BsonDocument unique = chunkIndexes.Single(static i => i["name"].AsString == "files_id_1_n_1");
        Assert.IsTrue(unique.GetValue("unique", false).ToBoolean());
        IReadOnlyList<BsonDocument> fileIndexes = await temp.Connection.ListIndexesAsync(temp.Database, "images.files");
        Assert.IsTrue(fileIndexes.Any(static i => i["name"].AsString == "filename_1_uploadDate_1"));
    });

    // ── 标签页(视图模型 + 外壳) ─────────────────────────────────────────

    [TestMethod]
    public void Tab_uploads_navigates_selects_and_deletes_with_confirmation() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using TempBucket temp = await TempBucket.CreateAsync();
        // 先放一份,让桶在对象树里出现。
        using (var seed = new MemoryStream(Bytes(10, seed: 4)))
        {
            await temp.Service.UploadAsync("seed/readme.txt", seed, null);
        }
        await using Workbench bench = await Screens.OpenWorkbenchAsync(temp.Database);
        bench.Session.OpenGridFs(temp.Database, "fs");
        var tab = (GridFsTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => !tab.IsLoading && tab.Entries.Count > 0);
        Assert.AreEqual("seed", tab.Entries.Single().Name);
        StringAssert.Contains(tab.StatusText, $"{temp.Database}.fs");

        // 进入 seed/,经对话框上传两个本地文件(对话框里改 metadata)。
        await tab.NavigateAsync("seed/");
        Assert.AreEqual(GridFsEntryKind.Parent, tab.Entries[0].Kind);
        string local = Path.Combine(Path.GetTempPath(), "fs-tab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        string photo = Path.Combine(local, "photo.png");
        string doc = Path.Combine(local, "manual.pdf");
        File.WriteAllBytes(photo, Bytes(400_000, seed: 5));
        File.WriteAllBytes(doc, Bytes(1_000, seed: 6));
        try
        {
            Task upload = tab.UploadPathsAsync([photo, doc]);
            await Screens.PumpAsync(5);
            var dialog = (GridFsUploadDialogViewModel)bench.ViewModel.Dialog!;
            Assert.AreEqual("seed/", dialog.Target);
            StringAssert.Contains(dialog.MetadataText, "uploader");
            dialog.MetadataText = "{ uploader: \"qa\", batch: 7 }";
            dialog.ConfirmCommand.Execute(null);
            await upload;
            await WaitAsync(() => !tab.IsTransferring && tab.Entries.Count(static e => e.IsFile) == 3);

            GridFsEntry png = tab.Entries.Single(static e => e.Name == "photo.png");
            Assert.AreEqual("image/png", png.ContentType, "contentType is filled per file from the extension");
            Assert.AreEqual(GridFsCategory.Image, png.Category);
            GridFsFile? stored = await temp.Service.GetAsync(png.File!.Id);
            Assert.AreEqual(7, stored!.Metadata!["batch"].ToInt32());

            // 选中 → 详情(版本、字段);假字节解不出图,老实说「无法预览」。
            tab.SelectedEntry = png;
            await WaitAsync(() => tab.Details.Versions.Count == 1 && !tab.Details.IsPreviewLoading);
            Assert.AreEqual("seed/photo.png", tab.Details.FilenameText);
            Assert.IsFalse(tab.Details.HasImage);
            Assert.AreEqual(bench.ViewModel.Loc["Fs_NoPreview"], tab.Details.PreviewMessage);

            // 一张真图:预览解出来,尺寸与格式写在图下面;缩略图视图也给它出缩略图。
            string real = Path.Combine(local, "real.png");
            WritePng(real, 160, 90);
            await tab.UploadPathsAsync([real], ask: false);
            await WaitAsync(() => !tab.IsTransferring && tab.Entries.Any(static e => e.Name == "real.png"));
            tab.SelectedEntry = tab.Entries.Single(static e => e.Name == "real.png");
            await WaitAsync(() => tab.Details.HasImage);
            Assert.AreEqual("160 × 90 · PNG", tab.Details.ImageInfo);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "06-gridfs-preview");
            tab.IsGridView = true;
            await WaitAsync(() => tab.Entries.Single(static e => e.Name == "real.png").HasThumbnail);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "06-gridfs-thumbs");
            tab.IsListView = true;
            tab.SelectedEntry = tab.Entries.Single(static e => e.Name == "real.png");
            tab.DeleteCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
            ((ConfirmDialogViewModel)bench.ViewModel.Dialog!).ConfirmCommand.Execute(null);
            await WaitAsync(() => tab.Entries.All(static e => e.Name != "real.png"));

            // 外部打开:下到临时目录、交给 Launcher(这里接一个假的记下路径)。
            string? launched = null;
            tab.Launcher = path =>
            {
                launched = path;
                return Task.FromResult(true);
            };
            await tab.OpenExternalAsync(tab.Entries.Single(static e => e.Name == "manual.pdf").File!);
            await WaitAsync(() => launched is not null);
            byte[] opened = await File.ReadAllBytesAsync(launched!);
            byte[] original = await File.ReadAllBytesAsync(doc);
            Assert.IsTrue(original.AsSpan().SequenceEqual(opened), "external open hands over the exact bytes");
            Directory.Delete(Path.GetDirectoryName(launched!)!, recursive: true);

            // 目录下载:按虚拟目录在本地重建结构。
            await tab.NavigateAsync("");
            string target = Path.Combine(local, "out");
            await tab.DownloadToFolderAsync([tab.Entries.Single(static e => e.Name == "seed")], target);
            await WaitAsync(() => !tab.IsTransferring && File.Exists(Path.Combine(target, "seed", "photo.png")));
            byte[] fetched = await File.ReadAllBytesAsync(Path.Combine(target, "seed", "photo.png"));
            byte[] sent = await File.ReadAllBytesAsync(photo);
            Assert.IsTrue(sent.AsSpan().SequenceEqual(fetched), "folder download writes each file byte-for-byte");
            Assert.IsTrue(File.Exists(Path.Combine(target, "seed", "readme.txt")));

            // 上传中:底栏进度 + 表里带进度条的上传行;取消后不留半个文件。
            await tab.NavigateAsync("seed/");
            string big = Path.Combine(local, "detail-07.jpg");
            File.WriteAllBytes(big, Bytes(96 * 1024 * 1024, seed: 8));
            await tab.UploadPathsAsync([big], ask: false);
            await WaitAsync(() => tab.IsTransferring && tab.TransferProgress > 0.02, timeoutMs: 30000);
            Assert.IsTrue(tab.Entries.Any(static e => e.IsUpload), "the pending upload shows as a row in its folder");
            await Screens.PumpAsync(4);
            Screens.Capture(bench.Window, "06-gridfs-uploading");
            tab.CancelTransfersCommand.Execute(null);
            await WaitAsync(() => !tab.IsTransferring, timeoutMs: 30000);
            await WaitAsync(() => tab.Entries.All(static e => !e.IsUpload));
            bool partial = await temp.Service.ExistsAsync("seed/detail-07.jpg");
            Assert.IsFalse(partial, "a cancelled upload leaves no file");
            IReadOnlyList<GridFsOrphan> leftovers = await temp.Service.FindOrphansAsync();
            Assert.AreEqual(0, leftovers.Count, "a cancelled upload leaves no chunks behind");

            // 勾两个 → 底栏汇总(刷新过,行对象是新的)。
            png = tab.Entries.Single(static e => e.Name == "photo.png");
            tab.SelectedEntry = png;
            png.IsChecked = true;
            tab.Entries.Single(static e => e.Name == "manual.pdf").IsChecked = true;
            StringAssert.Contains(tab.SelectionSummary, "2");

            // 删除:确认框写明"全部版本",点确认后两项都没了。
            tab.DeleteCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
            var confirm = (ConfirmDialogViewModel)bench.ViewModel.Dialog!;
            Assert.AreEqual(bench.ViewModel.Loc["Fs_DeleteConfirm"], confirm.Request.ConfirmLabel);
            confirm.ConfirmCommand.Execute(null);
            await WaitAsync(() => tab.Entries.Count(static e => e.IsFile) == 1);
            Assert.AreEqual("readme.txt", tab.Entries.Single(static e => e.IsFile).Name);
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }
    });

    [TestMethod]
    public void Read_only_mode_blocks_uploads() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using TempBucket temp = await TempBucket.CreateAsync();
        using (var seed = new MemoryStream(Bytes(10, seed: 7)))
        {
            await temp.Service.UploadAsync("a.txt", seed, null);
        }
        await using Workbench bench = await Screens.OpenWorkbenchAsync(temp.Database);
        bench.Session.OpenGridFs(temp.Database, "fs");
        var tab = (GridFsTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => !tab.IsLoading && tab.Entries.Count > 0);
        bench.Session.Guard.IsReadOnly = true;

        string local = Path.Combine(Path.GetTempPath(), "fs-ro-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(local, "nope");
        try
        {
            await tab.UploadPathsAsync([local], ask: false);
            await Screens.PumpAsync(10);
            Assert.IsNull(bench.ViewModel.Dialog);
            Assert.IsFalse(tab.IsTransferring);
            long files = await temp.Service.Files.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);
            Assert.AreEqual(1L, files);
        }
        finally
        {
            File.Delete(local);
        }
    });

    // ── 截图 ───────────────────────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board06_GridFs_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        if (bench.ViewModel.VisibleNodes.FirstOrDefault(static n => n.Kind == NodeKind.Bucket && n.Name == "fs") is { } node)
        {
            bench.ViewModel.SelectedNode = node;
        }
        bench.Session.OpenGridFs(Screens.Database, "fs");
        var tab = (GridFsTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => !tab.IsLoading && tab.Entries.Count > 0);
        await tab.NavigateAsync("products/SKU-7710/");
        await WaitAsync(() => tab.Entries.Any(static e => e.Name == "main.jpg"));
        GridFsEntry main = tab.Entries.First(static e => e.Name == "main.jpg");
        main.IsChecked = true;
        if (tab.Entries.FirstOrDefault(static e => e.Name == "detail-01.jpg") is { } detail)
        {
            detail.IsChecked = true;
        }
        tab.SelectedEntry = main;
        await WaitAsync(() => tab.Details.Versions.Count > 0 && !tab.Details.IsPreviewLoading);
        await Screens.PumpAsync(40);
        WriteableBitmap? frame = Screens.Capture(bench.Window, "06-gridfs");
        Assert.IsNotNull(frame);
        Assert.IsTrue(tab.Details.Versions.Count >= 1);
        StringAssert.Contains(tab.StatusText, "products/SKU-7710/");
    });

    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board06_GridFs_dialogs_render() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        bench.Session.OpenGridFs(Screens.Database, "fs");
        var tab = (GridFsTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => !tab.IsLoading && tab.Entries.Count > 0);
        await tab.NavigateAsync("products/SKU-7710/");
        await WaitAsync(() => tab.Entries.Any(static e => e.Name == "main.jpg"));

        // 上传对话框:只看不传(shop 只读对待)。
        string local = Path.Combine(Path.GetTempPath(), "fs-dlg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(local, "detail-07.jpg"), Bytes(1000, seed: 9));
        File.WriteAllBytes(Path.Combine(local, "detail-08.jpg"), Bytes(2000, seed: 10));
        try
        {
            Task upload = tab.UploadPathsAsync([Path.Combine(local, "detail-07.jpg"), Path.Combine(local, "detail-08.jpg")]);
            await WaitAsync(() => bench.ViewModel.Dialog is GridFsUploadDialogViewModel);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "06-gridfs-upload");
            var dialog = (GridFsUploadDialogViewModel)bench.ViewModel.Dialog!;
            StringAssert.Contains(dialog.MetadataText, "image/jpeg", "same-type batches get contentType in the template");
            dialog.Close();
            await upload;
            Assert.IsFalse(tab.IsTransferring, "cancelling the dialog uploads nothing");
        }
        finally
        {
            Directory.Delete(local, recursive: true);
        }

        // 新建存储桶:非法名字就地报错,不建。
        tab.NewBucketCommand.Execute(null);
        var create = (GridFsNewBucketDialogViewModel)bench.ViewModel.Dialog!;
        create.Name = "bad$name";
        Assert.IsTrue(create.HasError);
        Assert.IsFalse(create.CreateCommand.CanExecute(null));
        create.Name = "images";
        await Screens.PumpAsync(20);
        Screens.Capture(bench.Window, "06-gridfs-newbucket");
        create.Close();

        // 删除确认框(只看):写明"全部版本",并给出"只删最新版本"的出口。
        tab.SelectedEntry = tab.Entries.First(static e => e.Name == "main.jpg");
        tab.DeleteCommand.Execute(null);
        await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
        var confirm = (ConfirmDialogViewModel)bench.ViewModel.Dialog!;
        Assert.IsNotNull(confirm.Request.AsideLabel, "a multi-version file offers deleting only the latest version");
        await Screens.PumpAsync(20);
        Screens.Capture(bench.Window, "06-gridfs-delete");
        confirm.Close();
        await Screens.PumpAsync(5);
        Assert.IsTrue(tab.Entries.Any(static e => e.Name == "main.jpg"), "cancel deletes nothing");
    });

    // ── 工具 ───────────────────────────────────────────────────────────────

    /// <summary>画一张真 PNG(左半强调色、右半深底),给预览与缩略图一个能解码的东西。</summary>
    private static void WritePng(string path, int width, int height)
    {
        using var bitmap = new WriteableBitmap(new Avalonia.PixelSize(width, height), new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (Avalonia.Platform.ILockedFramebuffer frame = bitmap.Lock())
        {
            var row = new byte[frame.RowBytes];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool left = x < width / 2;
                    row[(x * 4) + 0] = left ? (byte)0xF9 : (byte)0x36;
                    row[(x * 4) + 1] = left ? (byte)0x93 : (byte)0x2A;
                    row[(x * 4) + 2] = left ? (byte)0xBD : (byte)0x28;
                    row[(x * 4) + 3] = 0xFF;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, frame.Address + (y * frame.RowBytes), row.Length);
            }
        }
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task UploadAsync(GridFsService fs, string name, int length, BsonDocument? metadata)
    {
        using var source = new MemoryStream(Bytes(length, length));
        await fs.UploadAsync(name, source, metadata);
        // uploadDate 是毫秒精度:同名的几份隔开一点,"最新"才有确定的先后。
        await Task.Delay(3);
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
            {
                Assert.Fail("Timed out waiting for the GridFS tab.");
            }
            await Screens.PumpAsync(3);
        }
    }

    /// <summary>一个临时库里的 <c>fs</c> 桶;释放时 drop 整个临时库。</summary>
    private sealed class TempBucket : IAsyncDisposable
    {
        private TempBucket(MongoConnection connection, string database)
        {
            Connection = connection;
            Database = database;
            Service = new GridFsService(connection, new GridFsBucketInfo(database, "fs"));
        }

        public MongoConnection Connection { get; }

        public string Database { get; }

        public GridFsService Service { get; }

        public static async Task<TempBucket> CreateAsync()
        {
            MongoConnection connection = await TestServer.OpenAsync();
            string db = "velashell_gridfs_" + Guid.NewGuid().ToString("N")[..8];
            await GridFsService.CreateBucketAsync(connection, db, "fs");
            return new(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.Client.DropDatabaseAsync(Database);
            }
            finally
            {
                await Connection.DisposeAsync();
            }
        }
    }
}
