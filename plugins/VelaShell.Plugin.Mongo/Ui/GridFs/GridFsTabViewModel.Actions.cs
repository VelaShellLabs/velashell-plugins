using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>待上传的一个本地文件。</summary>
/// <param name="LocalPath">本地路径。</param>
/// <param name="Relative">相对上传目标目录的名字(上传文件夹时带上文件夹自己的名字与子目录)。</param>
/// <param name="Size">字节数。</param>
internal sealed record GridFsUploadItem(string LocalPath, string Relative, long Size);

/// <summary>GridFS 标签页的写操作与传输队列。</summary>
internal sealed partial class GridFsTabViewModel
{
    /// <summary>缩略图只读这么大以内的图片(整块进内存,一屏几十张)。</summary>
    private const long ThumbnailLimit = 4L * 1024 * 1024;

    private readonly Queue<GridFsTransfer> _queue = new();
    private readonly List<GridFsEntry> _pendingRows = [];
    private readonly DispatcherTimer _transferTicker;
    private readonly Stopwatch _transferClock = new();
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _thumbCts;
    private GridFsTransfer? _current;
    private string? _reselectPath;
    private string _lastTransfer = "";
    private double _transferProgress;
    private string _transferStats = "";

    /// <summary>上传文件(多选)。</summary>
    public AsyncCommand UploadFilesCommand { get; private set; } = null!;

    /// <summary>上传文件夹(递归,保留相对路径)。</summary>
    public AsyncCommand UploadFolderCommand { get; private set; } = null!;

    /// <summary>下载勾选 / 选中的项。</summary>
    public AsyncCommand DownloadCommand { get; private set; } = null!;

    /// <summary>重命名。</summary>
    public AsyncCommand RenameCommand { get; private set; } = null!;

    /// <summary>删除(每个文件的全部版本)。</summary>
    public AsyncCommand DeleteCommand { get; private set; } = null!;

    /// <summary>新建存储桶。</summary>
    public RelayCommand NewBucketCommand { get; private set; } = null!;

    /// <summary>检查孤儿块。</summary>
    public AsyncCommand CheckOrphansCommand { get; private set; } = null!;

    /// <summary>取消全部传输。</summary>
    public RelayCommand CancelTransfersCommand { get; private set; } = null!;

    /// <summary>
    /// 交给系统打开一个本地文件(视图在构造时接上 —— 只有 TopLevel 拿得到 Launcher)。
    /// 返回是否打开成功;没接上为 <see langword="null" />(headless 测试)。
    /// </summary>
    internal Func<string, Task<bool>>? Launcher { get; set; }

    private void InitializeActions()
    {
        UploadFilesCommand = new(UploadFilesAsync);
        UploadFolderCommand = new(UploadFolderAsync);
        DownloadCommand = new(DownloadTargetsAsync, () => HasTargets);
        // 改名可用条件放宽到"有对象":勾了多个时点它给一句"只选一个",比一个灰着不说为什么的按钮好懂。
        RenameCommand = new(RenameAsync, () => HasTargets);
        DeleteCommand = new(DeleteTargetsAsync, () => HasTargets);
        NewBucketCommand = new(NewBucket);
        CheckOrphansCommand = new(CheckOrphansAsync);
        CancelTransfersCommand = new(CancelTransfers);
    }

    // ── 拖放区与底栏 ───────────────────────────────────────────────────────

    /// <summary>上传落到的目录(给人看:根是 <c>/</c>)。</summary>
    public string UploadTarget => UploadPrefix.Length == 0 ? "/" : UploadPrefix;

    /// <summary>上传落到的前缀。</summary>
    private string UploadPrefix => _virtualDirs ? _prefix : "";

    /// <summary>拖放区主文字「拖拽文件到此处上传到 products/SKU-7710/」。</summary>
    public string DropText => Loc.Format("Fs_DropHere", UploadTarget);

    /// <summary>正在传输。</summary>
    public bool IsTransferring => _current is not null;

    /// <summary>底栏图标(上传 / 下载)。</summary>
    public string TransferIcon => _current?.Kind == GridFsTransferKind.Download ? "Mongo.download" : "Mongo.arrow-up-from-line";

    /// <summary>底栏图标颜色:传输中强调色,空闲淡色。</summary>
    public string TransferIconToken => _current is null ? "VelaTextMuted" : "VelaAccent";

    /// <summary>「正在上传 detail-07.jpg」;空闲时是上一批的结果或「无传输任务」。</summary>
    public string TransferTitle => _current is { } job
        ? Loc.Format(job.Kind == GridFsTransferKind.Upload ? "Fs_Uploading" : "Fs_Downloading", job.Name)
        : _lastTransfer.Length > 0 ? _lastTransfer : Loc["Fs_Idle"];

    /// <summary>当前这一个的进度(0–1)。</summary>
    public double TransferProgress
    {
        get => _transferProgress;
        private set => SetProperty(ref _transferProgress, value);
    }

    /// <summary>「62% · 3.1 MB/s · 剩余 1 s」。</summary>
    public string TransferStats
    {
        get => _transferStats;
        private set => SetProperty(ref _transferStats, value);
    }

    /// <summary>「队列:2 个文件」;队列空为空。</summary>
    public string QueueText => _queue.Count > 0 ? Loc.Format("Fs_Queue", _queue.Count) : "";

    /// <summary>队列里还有等着的。</summary>
    public bool HasQueue => _queue.Count > 0;

    private void RaiseTransfer() =>
        RaisePropertiesChanged(nameof(IsTransferring), nameof(TransferIcon), nameof(TransferIconToken), nameof(TransferTitle),
            nameof(QueueText), nameof(HasQueue));

    // ── 上传 ───────────────────────────────────────────────────────────────

    private async Task UploadFilesAsync()
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        IReadOnlyList<string> files = await Workspace.PickOpenFilesAsync(Loc["Fs_PickUpload"], [FileKind.Any], multiple: true)
            .ConfigureAwait(true);
        if (files.Count > 0)
        {
            await UploadPathsAsync(files).ConfigureAwait(true);
        }
    }

    private async Task UploadFolderAsync()
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        string? folder = await Workspace.PickFolderAsync(Loc["Fs_PickUploadFolder"]).ConfigureAwait(true);
        if (folder is not null)
        {
            await UploadPathsAsync([folder]).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 上传一批本地路径(文件或文件夹,文件夹递归、保留相对路径)到当前目录。
    /// <paramref name="ask" /> 时先弹「上传到 GridFS」小对话框:可改目标目录与 metadata(默认带 contentType 与 uploader)。
    /// 拖放与工具栏按钮都走这里。
    /// </summary>
    internal async Task UploadPathsAsync(IReadOnlyList<string> paths, bool ask = true, BsonDocument? metadata = null)
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        IReadOnlyList<GridFsUploadItem> items = ExpandPaths(paths);
        if (items.Count == 0)
        {
            Workspace.Toast(new() { Title = Loc["Fs_UploadNothing"], Kind = ToastKind.Info });
            return;
        }
        string prefix = UploadPrefix;
        BsonDocument template = metadata ?? DefaultMetadata(items);
        if (ask)
        {
            var dialog = new GridFsUploadDialogViewModel(Workspace, Bucket, prefix, items, template);
            Workspace.ShowDialog(dialog);
            if (await dialog.Result.ConfigureAwait(true) is not { } plan)
            {
                return;
            }
            prefix = plan.Prefix;
            template = plan.Metadata;
        }
        Enqueue([.. items.Select(item =>
        {
            BsonDocument meta = MetadataFor(template, item.Relative);
            return new GridFsTransfer
            {
                Kind = GridFsTransferKind.Upload,
                LocalPath = item.LocalPath,
                Filename = prefix + item.Relative,
                Size = item.Size,
                Metadata = meta,
                ContentType = meta.GetValue("contentType", "").ToString() ?? ""
            };
        })]);
    }

    /// <summary>把选中的本地路径展开成文件清单。读不到的子目录跳过(权限),而不是整批失败。</summary>
    internal static IReadOnlyList<GridFsUploadItem> ExpandPaths(IEnumerable<string> paths)
    {
        var items = new List<GridFsUploadItem>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                items.Add(new(path, Path.GetFileName(path), new FileInfo(path).Length));
            }
            else if (Directory.Exists(path))
            {
                string root = SafeSegment(new DirectoryInfo(path).Name);
                foreach (string file in Directory.EnumerateFiles(path, "*", options).Order(StringComparer.Ordinal))
                {
                    string relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                    items.Add(new(file, root + "/" + relative, new FileInfo(file).Length));
                }
            }
        }
        return items;
    }

    /// <summary>默认 metadata:所有文件同一种 contentType 时直接写上;uploader 取当前用户。</summary>
    internal BsonDocument DefaultMetadata(IReadOnlyList<GridFsUploadItem> items)
    {
        var doc = new BsonDocument();
        List<string> types = [.. items.Select(static i => GridFsPaths.GuessContentType(i.Relative)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (types.Count == 1)
        {
            doc["contentType"] = types[0];
        }
        doc["uploader"] = Uploader;
        return doc;
    }

    /// <summary>上传者:已认证用户名(去掉 <c>@库</c>),没开认证就用本机用户名。</summary>
    private string Uploader =>
        Workspace.Connection.Privileges.User is { Length: > 0 } user ? user.Split('@')[0] : Environment.UserName;

    /// <summary>某个文件的 metadata:模板里没写 contentType 的,按这个文件的扩展名补上。</summary>
    internal static BsonDocument MetadataFor(BsonDocument template, string relative)
    {
        var doc = template.DeepClone().AsBsonDocument;
        if (!doc.Contains("contentType"))
        {
            doc.InsertAt(0, new BsonElement("contentType", GridFsPaths.GuessContentType(relative)));
        }
        return doc;
    }

    // ── 下载与外部打开 ─────────────────────────────────────────────────────

    private async Task DownloadTargetsAsync()
    {
        IReadOnlyList<GridFsEntry> targets = Targets;
        if (targets.Count == 0)
        {
            return;
        }
        if (targets is [{ IsFile: true, File: { } single }])
        {
            await DownloadFileAsync(single).ConfigureAwait(true);
            return;
        }
        string? folder = await Workspace.PickFolderAsync(Loc["Fs_PickDownloadFolder"]).ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }
        await DownloadToFolderAsync(targets, folder).ConfigureAwait(true);
    }

    /// <summary>下载一份(某个版本)到用户选的路径。</summary>
    internal async Task DownloadFileAsync(GridFsFile file)
    {
        string? path = await Workspace.PickSaveFileAsync(Loc["Fs_PickSave"], SafeSegment(file.BaseName), [FileKind.Any])
            .ConfigureAwait(true);
        if (path is not null)
        {
            Enqueue([DownloadJob(file, path)]);
        }
    }

    /// <summary>
    /// 多项下载到一个文件夹:文件取最新版本,目录递归、按虚拟目录在本地建同样的结构。
    /// </summary>
    internal async Task DownloadToFolderAsync(IReadOnlyList<GridFsEntry> targets, string folder)
    {
        var jobs = new List<GridFsTransfer>();
        try
        {
            foreach (GridFsEntry target in targets)
            {
                if (target is { IsFile: true, File: { } file })
                {
                    jobs.Add(DownloadJob(file, SafeLocalPath(folder, file.BaseName)));
                }
                else if (target.IsFolder)
                {
                    string root = target.Path.TrimEnd('/');
                    root = GridFsPaths.BaseName(root);
                    foreach (GridFsFile inner in await _service.FilesUnderAsync(target.Path, latestOnly: true).ConfigureAwait(true))
                    {
                        jobs.Add(DownloadJob(inner, SafeLocalPath(folder, root + "/" + inner.Filename[target.Path.Length..])));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
            return;
        }
        Enqueue(jobs);
    }

    /// <summary>外部打开:下到临时目录,再交给系统的默认程序。</summary>
    internal Task OpenExternalAsync(GridFsFile file)
    {
        string dir = Path.Combine(Path.GetTempPath(), "VelaShell", "gridfs", SafeSegment(file.Id.ToString() ?? "file"));
        Enqueue([DownloadJob(file, Path.Combine(dir, SafeSegment(file.BaseName)), LaunchAsync)]);
        return Task.CompletedTask;
    }

    private static GridFsTransfer DownloadJob(GridFsFile file, string localPath, Func<string, Task>? then = null) => new()
    {
        Kind = GridFsTransferKind.Download,
        LocalPath = localPath,
        Filename = file.Filename,
        Source = file,
        Size = file.Length,
        ChunkSize = file.ChunkSize > 0 ? file.ChunkSize : GridFsService.DefaultChunkSize,
        Then = then
    };

    private async Task LaunchAsync(string path)
    {
        if (Launcher is { } launcher && await launcher(path).ConfigureAwait(true))
        {
            return;
        }
        Workspace.Toast(new() { Title = Loc.Format("Fs_OpenFailed", Path.GetFileName(path)), Detail = path, Kind = ToastKind.Warning });
    }

    /// <summary>
    /// GridFS 文件名 → 本地路径。文件名是**别人写进库里的字符串**:<c>../../x</c>、绝对路径、
    /// Windows 不允许的字符都可能出现。逐段清洗、丢掉 <c>.</c> / <c>..</c>,最后再确认落点仍在目标文件夹里 ——
    /// 下载功能不能变成"往任意位置写文件"。
    /// </summary>
    internal static string SafeLocalPath(string root, string relative)
    {
        string[] parts = [.. relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(static p => p is not "." and not "..")
            .Select(SafeSegment)];
        if (parts.Length == 0)
        {
            parts = ["_"];
        }
        string full = Path.GetFullPath(Path.Combine([root, .. parts]));
        string rootFull = Path.GetFullPath(root);
        string rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : Path.Combine(rootFull, parts[^1]);
    }

    /// <summary>一段文件名里换掉本机不允许的字符;Windows 的保留名(CON、NUL…)前面加下划线。</summary>
    internal static string SafeSegment(string segment)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new([.. segment.Select(c => invalid.Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c)]);
        clean = clean.TrimEnd('.', ' ');
        if (clean.Length == 0)
        {
            return "_";
        }
        string stem = Path.GetFileNameWithoutExtension(clean).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
            || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]))
            ? "_" + clean
            : clean;
    }

    // ── 传输队列 ───────────────────────────────────────────────────────────

    private void Enqueue(IReadOnlyList<GridFsTransfer> jobs)
    {
        if (jobs.Count == 0)
        {
            return;
        }
        bool uploads = false;
        foreach (GridFsTransfer job in jobs)
        {
            _queue.Enqueue(job);
            if (job.Kind == GridFsTransferKind.Upload)
            {
                _pendingRows.Add(GridFsEntry.ForUpload(GridFsPaths.BaseName(job.Filename), job));
                uploads = true;
            }
        }
        if (uploads)
        {
            ApplyEntries();
        }
        RaiseTransfer();
        if (_current is null)
        {
            _ = PumpAsync();
        }
    }

    /// <summary>
    /// 串行跑队列。上传完一个就重列一次目录(节流到每秒一次)—— 新文件立刻出现在表里,
    /// 而不是等一整批传完。失败的那一个提示出来,队列继续往下走。
    /// </summary>
    private async Task PumpAsync()
    {
        var cts = new CancellationTokenSource();
        _transferCts = cts;
        int done = 0;
        long bytes = 0;
        bool uploaded = false;
        GridFsTransferKind kind = GridFsTransferKind.Upload;
        var sinceReload = Stopwatch.StartNew();
        _transferTicker.Start();
        try
        {
            while (_queue.Count > 0 && !cts.IsCancellationRequested)
            {
                GridFsTransfer job = _queue.Dequeue();
                _current = job;
                kind = job.Kind;
                _transferClock.Restart();
                TransferProgress = 0;
                TransferStats = "";
                RaiseTransfer();
                try
                {
                    if (job.Kind == GridFsTransferKind.Upload)
                    {
                        await using var source = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await _service.UploadAsync(job.Filename, source, job.Metadata, job.Reporter, job.ChunkSize, cts.Token)
                            .ConfigureAwait(true);
                        uploaded = true;
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(job.LocalPath)!);
                        await using (var target = new FileStream(job.LocalPath, FileMode.Create, FileAccess.Write, FileShare.None,
                                         1 << 16, FileOptions.Asynchronous))
                        {
                            await _service.DownloadAsync(job.Source!.Id, target, job.Reporter, cts.Token).ConfigureAwait(true);
                        }
                        if (job.Then is { } then)
                        {
                            await then(job.LocalPath).ConfigureAwait(true);
                        }
                    }
                    done++;
                    bytes += job.Size;
                }
                catch (OperationCanceledException)
                {
                    DeletePartial(job);
                    break;
                }
                catch (Exception ex) when (ex is MongoException or TimeoutException or IOException or UnauthorizedAccessException)
                {
                    DeletePartial(job);
                    Workspace.Toast(new()
                    {
                        Title = Loc.Format("Fs_TransferFailed", job.Name, MongoConnector.Describe(ex)),
                        Kind = ToastKind.Error,
                        Duration = TimeSpan.FromSeconds(8)
                    });
                }
                finally
                {
                    RemovePendingRow(job);
                }
                if (job.Kind == GridFsTransferKind.Upload && _queue.Count > 0 && sinceReload.Elapsed > TimeSpan.FromSeconds(1))
                {
                    sinceReload.Restart();
                    await ReloadAsync().ConfigureAwait(true);
                }
            }
        }
        finally
        {
            bool cancelled = cts.IsCancellationRequested;
            _transferTicker.Stop();
            _current = null;
            if (ReferenceEquals(_transferCts, cts))
            {
                _transferCts = null;
            }
            if (cancelled)
            {
                _queue.Clear();
                _pendingRows.Clear();
            }
            _lastTransfer = cancelled
                ? Loc["Fs_TransfersCancelled"]
                : done == 0 ? "" : Loc.Format(kind == GridFsTransferKind.Upload ? "Fs_UploadDone" : "Fs_DownloadDone", done, BsonText.Bytes(bytes));
            TransferProgress = 0;
            TransferStats = "";
            RaiseTransfer();
            if (uploaded || cancelled)
            {
                await ReloadAsync().ConfigureAwait(true);
            }
            cts.Dispose();
        }
    }

    /// <summary>定时把字节数翻成进度、速度与剩余时间(驱动的回调在线程池上,界面只按 150 ms 取一次)。</summary>
    private void UpdateTransferProgress()
    {
        if (_current is not { } job)
        {
            return;
        }
        long done = job.Bytes;
        double ratio = job.Size > 0 ? Math.Min(1, done / (double)job.Size) : 0;
        double seconds = _transferClock.Elapsed.TotalSeconds;
        double speed = seconds > 0.05 ? done / seconds : 0;
        string remaining = speed > 0 ? Duration((job.Size - done) / speed) : "—";
        TransferProgress = ratio;
        TransferStats = Loc.Format("Fs_TransferStats", (ratio * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
            BsonText.Bytes((long)speed), remaining);
        if (_pendingRows.FirstOrDefault(r => ReferenceEquals(r.Transfer, job)) is { } row)
        {
            row.Progress = ratio;
            long chunks = Math.Min(job.ChunkTotal, done / Math.Max(1, job.ChunkSize));
            row.ProgressText = $"{chunks.ToString(CultureInfo.InvariantCulture)} / {job.ChunkTotal.ToString(CultureInfo.InvariantCulture)}";
        }
    }

    private string Duration(double seconds) => seconds switch
    {
        < 60 => Loc.Format("Fs_Seconds", Math.Max(1, (int)Math.Ceiling(seconds))),
        < 3600 => Loc.Format("Fs_Minutes", (int)Math.Ceiling(seconds / 60)),
        _ => Loc.Format("Fs_Hours", (seconds / 3600).ToString("0.0", CultureInfo.InvariantCulture))
    };

    private void RemovePendingRow(GridFsTransfer job)
    {
        GridFsEntry? row = _pendingRows.FirstOrDefault(r => ReferenceEquals(r.Transfer, job));
        if (row is not null)
        {
            _pendingRows.Remove(row);
            Entries.Remove(row);
        }
    }

    private static void DeletePartial(GridFsTransfer job)
    {
        if (job.Kind != GridFsTransferKind.Download)
        {
            return;
        }
        try
        {
            File.Delete(job.LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉半截文件不影响主流程;它会在下一次同名下载时被覆盖。
        }
    }

    private void CancelTransfers()
    {
        _transferCts?.Cancel();
        _queue.Clear();
        foreach (GridFsEntry row in _pendingRows.Where(r => !ReferenceEquals(r.Transfer, _current)).ToList())
        {
            _pendingRows.Remove(row);
            Entries.Remove(row);
        }
        RaiseTransfer();
    }

    // ── 重命名 ─────────────────────────────────────────────────────────────

    private Task RenameAsync()
    {
        IReadOnlyList<GridFsEntry> targets = Targets;
        if (targets.Count != 1)
        {
            Workspace.Toast(new() { Title = Loc["Fs_RenamePickOne"], Kind = ToastKind.Info });
            return Task.CompletedTask;
        }
        if (Workspace.EnsureWritable(Database))
        {
            GridFsEntry entry = targets[0];
            long affected = entry.IsFolder ? entry.FileCount : entry.Versions;
            Workspace.ShowDialog(new GridFsRenameDialogViewModel(Workspace, entry, affected, name => ApplyRenameAsync(entry, name)));
        }
        return Task.CompletedTask;
    }

    /// <summary>执行改名;返回要显示在对话框里的错误,成功为 <see langword="null" />。</summary>
    internal async Task<string?> ApplyRenameAsync(GridFsEntry entry, string newName)
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return Loc[Workspace.Guard.Check(Database) ?? "Common_ReadOnlyBlocked"];
        }
        string value = newName.Trim().Replace('\\', '/').TrimStart('/');
        if (value.Length == 0 || value.TrimEnd('/').Length == 0)
        {
            return Loc["Fs_RenameEmpty"];
        }
        try
        {
            long modified;
            string newPath;
            if (entry.IsFolder)
            {
                string to = GridFsPaths.NormalizePrefix(value);
                if (to == entry.Path)
                {
                    return null;
                }
                if (to.StartsWith(entry.Path, StringComparison.Ordinal))
                {
                    return Loc["Fs_RenameIntoSelf"];
                }
                if (await _service.AnyUnderAsync(to).ConfigureAwait(true))
                {
                    return Loc["Fs_RenameFolderExists"];
                }
                modified = await _service.RenameFolderAsync(entry.Path, to).ConfigureAwait(true);
                newPath = to;
            }
            else
            {
                if (value.EndsWith('/'))
                {
                    return Loc["Fs_RenameEmpty"];
                }
                if (value == entry.Path)
                {
                    return null;
                }
                if (await _service.ExistsAsync(value).ConfigureAwait(true))
                {
                    return Loc["Fs_RenameExists"];
                }
                modified = await _service.RenameAsync(entry.Path, value).ConfigureAwait(true);
                newPath = value;
            }
            Workspace.Toast(new() { Title = Loc.Format("Fs_Renamed", BsonText.Grouped(modified)), Kind = ToastKind.Success });
            if (_selectedEntry?.Path == entry.Path)
            {
                // 改了名的那一行在刷新后换了路径;让 ApplyEntries 按新路径把它选回来。
                _reselectPath = newPath;
            }
            await ReloadAsync().ConfigureAwait(true);
            return null;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return Loc.Format("Common_Failed", MongoConnector.Describe(ex));
        }
    }

    // ── 删除 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 删除勾选 / 选中的项:**每个文件名的全部版本**,目录则是其下全部文件的全部版本。
    /// 确认框把这一点写明(列出份数、块数与大小);单个多版本文件另给一条"只删最新版本"的出口。
    /// 只删某一个版本走右侧详情的删除按钮。
    /// </summary>
    private async Task DeleteTargetsAsync()
    {
        IReadOnlyList<GridFsEntry> targets = Targets;
        if (targets.Count == 0 || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        var victims = new List<GridFsFile>();
        try
        {
            foreach (GridFsEntry target in targets)
            {
                victims.AddRange(target.IsFolder
                    ? await _service.FilesUnderAsync(target.Path, latestOnly: false).ConfigureAwait(true)
                    : await _service.VersionsAsync(target.Path).ConfigureAwait(true));
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
            return;
        }
        if (victims.Count == 0)
        {
            await ReloadAsync().ConfigureAwait(true);
            return;
        }
        long bytes = victims.Sum(static f => f.Length);
        long chunks = victims.Sum(static f => f.ChunkCount);
        int names = victims.Select(static f => f.Filename).Distinct(StringComparer.Ordinal).Count();
        bool single = targets is [{ IsFile: true }];
        GridFsFile latest = victims[0];
        bool confirmed = await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Fs_DeleteTitle"],
            Message = single
                ? Loc.Format("Fs_DeleteBodyOne", latest.BaseName, victims.Count, BsonText.Bytes(bytes), BsonText.Grouped(chunks))
                : Loc.Format("Fs_DeleteBodyMany", targets.Count, BsonText.Grouped(names), BsonText.Grouped(victims.Count), BsonText.Bytes(bytes)),
            ConfirmLabel = Loc["Fs_DeleteConfirm"],
            IconKey = "Mongo.trash-2",
            Facts =
            [
                new(Loc["Fs_FactFiles"], BsonText.Grouped(names)),
                new(Loc["Fs_FactVersions"], BsonText.Grouped(victims.Count)),
                new(Loc["Fs_FactChunks"], BsonText.Grouped(chunks)),
                new(Loc["Fs_FactSize"], BsonText.Bytes(bytes))
            ],
            TypeToConfirm = Workspace.Guard.ConfirmWrites ? Bucket.Name : null,
            AsideLabel = single && victims.Count > 1 ? Loc.Format("Fs_DeleteOnlyLatest", victims.Count - 1) : null,
            AsideAction = single && victims.Count > 1 ? () => _ = DeleteVersionCoreAsync(latest) : null
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            long deleted = await _service.DeleteManyAsync([.. victims.Select(static f => f.Id)]).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Fs_Deleted", BsonText.Grouped(deleted)), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
        }
        foreach (GridFsEntry target in targets)
        {
            target.IsChecked = false;
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 只删一个版本(右侧详情的删除按钮)。确认框写清"其余版本保留、谁会成为最新"。
    /// </summary>
    internal async Task DeleteVersionAsync(GridFsFile file, IReadOnlyList<GridFsVersionRow> versions)
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        GridFsVersionRow? row = versions.FirstOrDefault(v => v.File.Id == file.Id);
        string label = row?.Label ?? "v1";
        string date = file.UploadDate is { } at ? GridFsEntry.FormatLocal(at, "yyyy-MM-dd HH:mm") : "—";
        string message;
        if (versions.Count <= 1)
        {
            message = Loc.Format("Fs_DeleteOnlyVersionBody", file.BaseName);
        }
        else if (row?.IsCurrent ?? true)
        {
            message = Loc.Format("Fs_DeleteLatestBody", label, file.BaseName, date, versions.Count - 1, versions[1].Label);
        }
        else
        {
            message = Loc.Format("Fs_DeleteOldBody", label, file.BaseName, date, versions.Count - 1);
        }
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Fs_DeleteVersionTitle"],
                Message = message,
                ConfirmLabel = versions.Count <= 1 ? Loc["Fs_DeleteTitle"] : Loc["Fs_DeleteVersionTitle"],
                IconKey = "Mongo.trash-2",
                Facts =
                [
                    new(Loc["Fs_FactSize"], BsonText.Bytes(file.Length)),
                    new(Loc["Fs_FactChunks"], BsonText.Grouped(file.ChunkCount))
                ],
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? Bucket.Name : null
            }).ConfigureAwait(true))
        {
            return;
        }
        await DeleteVersionCoreAsync(file).ConfigureAwait(true);
    }

    private async Task DeleteVersionCoreAsync(GridFsFile file)
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        try
        {
            await _service.DeleteAsync(file.Id).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Fs_Deleted", 1), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>恢复旧版本:以新的 uploadDate 再写一份,成为最新版本(什么都不删)。</summary>
    internal async Task RestoreVersionAsync(GridFsFile version, string label)
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        try
        {
            await _service.RestoreAsync(version).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Fs_Restored", label, version.BaseName), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
        }
        await ReloadAsync().ConfigureAwait(true);
    }

    // ── 孤儿块与新桶 ───────────────────────────────────────────────────────

    /// <summary>
    /// 检查孤儿块:先报告(数量、大小、涉及几个已不存在的文件),确认后才清理。
    /// 只读模式下照样能查 —— 查是读操作,清理那一步才过写护栏。
    /// </summary>
    private async Task CheckOrphansAsync()
    {
        IReadOnlyList<GridFsOrphan> orphans;
        try
        {
            orphans = await _service.FindOrphansAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
            return;
        }
        if (orphans.Count == 0)
        {
            Workspace.Toast(new()
            {
                Title = Loc["Fs_OrphansNone"],
                Detail = Loc.Format("Fs_OrphansNoneDetail", Bucket.ChunksCollection),
                Kind = ToastKind.Success
            });
            return;
        }
        long chunks = orphans.Sum(static o => o.Chunks);
        long bytes = orphans.Sum(static o => o.Bytes);
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Fs_OrphansTitle"],
                Message = Loc.Format("Fs_OrphansBody", BsonText.Grouped(chunks), BsonText.Bytes(bytes), BsonText.Grouped(orphans.Count),
                    Bucket.FilesCollection),
                ConfirmLabel = Loc["Fs_OrphansConfirm"],
                IconKey = "Mongo.layers",
                Facts =
                [
                    new(Loc["Fs_FactOrphanFiles"], BsonText.Grouped(orphans.Count)),
                    new(Loc["Fs_FactChunks"], BsonText.Grouped(chunks)),
                    new(Loc["Fs_FactSize"], BsonText.Bytes(bytes))
                ],
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? Bucket.Name : null
            }).ConfigureAwait(true))
        {
            return;
        }
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        try
        {
            long deleted = await _service.DeleteOrphansAsync([.. orphans.Select(static o => o.FilesId)]).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Fs_OrphansCleaned", BsonText.Grouped(deleted)), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
        }
    }

    private void NewBucket()
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        Workspace.ShowDialog(new GridFsNewBucketDialogViewModel(Workspace, Database));
    }

    /// <summary>同库的桶(路径行的桶下拉用)。</summary>
    internal async Task<IReadOnlyList<GridFsBucketInfo>> ListBucketsAsync()
    {
        try
        {
            return await GridFsService.ListBucketsAsync(Workspace.Connection, Database).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Failed(ex);
            return [Bucket];
        }
    }

    /// <summary>切到同库另一个桶(外壳打开 / 复用它的标签)。</summary>
    internal void SwitchBucket(string name)
    {
        if (name != Bucket.Name)
        {
            Workspace.OpenGridFs(Database, name);
        }
    }

    // ── 缩略图 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 缩略图视图里给图片懒加载缩略图:只取 4 MB 以内的、最多 120 张,一张一张来 ——
    /// 缩略图是锦上添花,不能为它把连接塞满。
    /// </summary>
    private async Task LoadThumbnailsAsync()
    {
        _thumbCts?.Cancel();
        var cts = new CancellationTokenSource();
        _thumbCts = cts;
        List<GridFsEntry> candidates = [.. Entries
            .Where(static e => e is { IsFile: true, Category: GridFsCategory.Image, Thumbnail: null, File.Length: > 0 and <= ThumbnailLimit })
            .Take(120)];
        foreach (GridFsEntry entry in candidates)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }
            try
            {
                byte[] bytes = await _service.ReadAllAsync(entry.File!.Id, cts.Token).ConfigureAwait(true);
                entry.Thumbnail = await Task.Run(() => Decode(bytes, 192), cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                return;
            }
        }
    }

    /// <summary>解一张图;解不了(不是图、截断、格式不支持)给 <see langword="null" />。</summary>
    internal static Bitmap? Decode(byte[] bytes, int? width = null)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return width is { } w ? Bitmap.DecodeToWidth(stream, w) : new Bitmap(stream);
        }
#pragma warning disable CA1031 // 解码器对坏图抛的异常类型因平台与格式而异;任何一种都只意味着"这张没法预览"。
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private void Failed(Exception ex) =>
        Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
}
