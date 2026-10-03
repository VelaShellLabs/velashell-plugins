using System.Collections.Concurrent;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk.Logging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一条连着的连接:驱动连接、写护栏,以及对象树上它那一支(库 → 集合 / 视图 / 桶 / 用户)。
/// 标签页与对话框拿到的 <see cref="IMongoWorkspace" /> 就是它 —— 根上挂着好几条连接时,
/// 每个标签都清楚自己连的是哪一条;外壳的覆盖层、提示与剪贴板经它转给 <see cref="Shell" />。
/// </summary>
internal sealed class MongoSession : IMongoWorkspace, IDisposable
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<CollectionInfo>> _collections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollectionStats> _statsCache = [with(StringComparer.Ordinal)];
    private readonly List<TreeNode> _databaseNodes = [];
    private readonly IDisposable _registration;
    private IReadOnlyList<DatabaseInfo> _databases = [];
    private bool _showSystemDatabases;
    private int _queryCounter;

    /// <summary>构造(连上之后)。</summary>
    /// <param name="shell">工作台外壳。</param>
    /// <param name="entry">对象树上的那条连接。</param>
    /// <param name="link">连上的连接(连同跳板转发)。</param>
    public MongoSession(MongoWorkspaceViewModel shell, ConnectionEntry entry, MongoLink link)
    {
        Shell = shell;
        Entry = entry;
        Link = link;
        Guard = new(Connection.Settings, Connection.Privileges);
        Guard.Changed += OnGuardChanged;
        LatencyMs = Connection.LatencyMs;
        _showSystemDatabases = Connection.Settings.ShowSystemDatabases;
        Connection.Availability += OnAvailability;
        Connection.LatencyChanged += OnLatency;
        _registration = MongoSessionRegistry.Shared.Add(new(entry.Name, Connection));
    }

    /// <summary>工作台外壳。</summary>
    public MongoWorkspaceViewModel Shell { get; }

    /// <summary>对象树上的那条连接。</summary>
    public ConnectionEntry Entry { get; }

    /// <summary>连上的连接(连同跳板转发)。</summary>
    public MongoLink Link { get; }

    /// <inheritdoc />
    public MongoConnection Connection => Link.Connection;

    /// <inheritdoc />
    public Loc Loc => Shell.Loc;

    /// <inheritdoc />
    public MongoStore Store => Shell.Store;

    /// <inheritdoc />
    public IPluginLogger Log => Shell.Log;

    /// <inheritdoc />
    public string ConnectionKey => Entry.Profile.Id;

    /// <inheritdoc />
    public string ConnectionName => Entry.Name;

    /// <inheritdoc />
    public WriteGuard Guard { get; }

    /// <summary>对象树上这条连接的根行。</summary>
    public TreeNode Root => Entry.Root;

    /// <summary>服务器还在不在(驱动的心跳)。</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>最近一次往返的延迟。</summary>
    public int? LatencyMs { get; private set; }

    /// <summary>新查询标签的编号(每条连接各数各的)。</summary>
    internal int NextQueryNumber() => ++_queryCounter;

    // ── 对象树:这条连接的一支 ────────────────────────────────────────────

    /// <summary>对象树是否列出 admin / config / local(只影响显示;连接里点名的默认库照列)。</summary>
    public bool ShowSystemDatabases
    {
        get => _showSystemDatabases;
        set
        {
            if (_showSystemDatabases == value)
            {
                return;
            }
            _showSystemDatabases = value;
            ApplyDatabaseFilter();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Databases => [.. _databases.Where(d => IsListed(d.Name)).Select(static d => d.Name)];

    private bool IsListed(string database) =>
        _showSystemDatabases || !DatabaseInfo.IsSystemName(database) || database == Connection.Settings.Database;

    /// <inheritdoc />
    public IReadOnlyList<CollectionInfo> CollectionsOf(string database) =>
        _collections.TryGetValue(database, out IReadOnlyList<CollectionInfo>? list) ? list : [];

    /// <summary>连接里配了默认库就用它;没配就是第一个业务库。</summary>
    internal string? DefaultDatabase() =>
        Connection.Settings.Database is { Length: > 0 } configured
            ? configured
            : _databases.FirstOrDefault(static d => !d.IsSystem)?.Name ?? _databases.FirstOrDefault()?.Name;

    internal CollectionInfo FindCollection(string database, string collection) =>
        CollectionsOf(database).FirstOrDefault(c => c.Name == collection)
        ?? new CollectionInfo(database, collection, CollectionKind.Collection, []);

    /// <summary>扔掉集合统计的缓存(建删、清空、刷新之后)。</summary>
    internal void ClearStats() => _statsCache.Clear();

    /// <summary>重读库列表;之前展开着的库照样展开(第一次连上时展开默认库)。</summary>
    /// <returns>任务。</returns>
    public async Task ReloadTreeAsync()
    {
        HashSet<string> expanded = [.. _databaseNodes.Where(static n => n.IsExpanded).Select(static n => n.Name)];
        try
        {
            _databases = await Connection.ListDatabasesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
            return;
        }
        Root.Meta = Connection.Server.Version;
        _databaseNodes.Clear();
        _collections.Clear();
        string? defaultDb = DefaultDatabase();
        foreach (DatabaseInfo db in _databases)
        {
            _databaseNodes.Add(new TreeNode(NodeKind.Database, db.Name, 1, Root)
            {
                Database = db.Name,
                Meta = db.SizeOnDisk > 0 ? BsonText.Bytes(db.SizeOnDisk) : ""
            });
        }
        ListDatabaseNodes();
        Root.IsExpanded = true;
        Shell.RebuildVisible();
        foreach (TreeNode node in Root.Children.ToList())
        {
            if (expanded.Contains(node.Name) || (expanded.Count == 0 && node.Name == defaultDb))
            {
                await ExpandDatabaseAsync(node).ConfigureAwait(true);
            }
        }
    }

    private void ListDatabaseNodes()
    {
        Root.Children.Clear();
        foreach (TreeNode node in _databaseNodes.Where(n => IsListed(n.Name)))
        {
            Root.Children.Add(node);
        }
    }

    private void ApplyDatabaseFilter()
    {
        ListDatabaseNodes();
        // 选中的行跟着它的库一起被藏起来了:选中退回连接那一行,底部信息区不再讲一个看不见的东西。
        if (Shell.SelectedNode is { Database.Length: > 0 } selected && selected.Owner == Entry && !IsListed(selected.Database))
        {
            Shell.SelectedNode = Root;
        }
        Shell.RebuildVisible();
    }

    /// <inheritdoc />
    public async Task RefreshTreeAsync(string? database = null)
    {
        await RefreshTreeCoreAsync(database).ConfigureAwait(true);
        // 建、删、清空、导入之后都会走到这里:顺带告诉开着的对象列表重载,
        // 而不是让每个写路径各自记得去通知它。
        foreach (string db in database is null ? Databases : [database])
        {
            ObjectsChanged.Raise(this, db);
        }
    }

    private async Task RefreshTreeCoreAsync(string? database)
    {
        if (database is null || _databaseNodes.FirstOrDefault(n => n.Name == database) is not { } node)
        {
            await ReloadTreeAsync().ConfigureAwait(true);
            return;
        }
        node.IsLoaded = false;
        bool wasExpanded = node.IsExpanded;
        node.IsExpanded = false;
        if (wasExpanded)
        {
            await ExpandDatabaseAsync(node).ConfigureAwait(true);
        }
        else
        {
            await LoadDatabaseAsync(node).ConfigureAwait(true);
        }
    }

    /// <summary>展开一个库(第一次展开时才去读它的集合)。</summary>
    /// <param name="node">库那一行。</param>
    /// <returns>任务。</returns>
    internal async Task ExpandDatabaseAsync(TreeNode node)
    {
        node.IsExpanded = true;
        if (node.Kind == NodeKind.Database && !node.IsLoaded)
        {
            node.Children.Clear();
            node.Children.Add(new TreeNode(NodeKind.Placeholder, Loc["Common_Loading"], node.Depth + 1, node));
            Shell.RebuildVisible();
            await LoadDatabaseAsync(node).ConfigureAwait(true);
        }
        Shell.RebuildVisible();
    }

    private async Task LoadDatabaseAsync(TreeNode dbNode)
    {
        string db = dbNode.Name;
        IReadOnlyList<CollectionInfo> all;
        try
        {
            all = await Connection.ListCollectionsAsync(db).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            dbNode.Children.Clear();
            dbNode.Children.Add(new TreeNode(NodeKind.Placeholder, MongoConnector.Describe(ex), dbNode.Depth + 1, dbNode));
            Shell.RebuildVisible();
            return;
        }
        _collections[db] = all;
        IReadOnlyList<GridFsBucketInfo> buckets = MongoConnection.FindBuckets(db, all);
        HashSet<string> bucketCollections = [.. buckets.SelectMany(static b => new[] { b.FilesCollection, b.ChunksCollection })];
        List<CollectionInfo> collections =
        [
            .. all.Where(c => c.Kind is not (CollectionKind.View or CollectionKind.System) && !bucketCollections.Contains(c.Name))
        ];
        List<CollectionInfo> views = [.. all.Where(static c => c.Kind == CollectionKind.View)];
        bool hasSystemJs = all.Any(static c => c.Name == "system.js");

        int depth = dbNode.Depth + 1;
        dbNode.Children.Clear();
        TreeNode collectionsFolder = Folder(dbNode, FolderKind.Collections, Loc["Tree_Collections"], collections.Count, depth);
        foreach (CollectionInfo info in collections)
        {
            var node = new TreeNode(NodeKind.Collection, info.Name, depth + 1, collectionsFolder) { Database = db, Collection = info };
            switch (info.Kind)
            {
                case CollectionKind.TimeSeries:
                    node.Tag = Loc["Tag_TimeSeries"];
                    node.TagClass = "info";
                    break;
                case CollectionKind.Capped:
                    node.Tag = Loc["Tag_Capped"];
                    node.TagClass = "muted";
                    break;
                case CollectionKind.Clustered:
                    node.Tag = Loc["Tag_Clustered"];
                    node.TagClass = "muted";
                    break;
            }
            collectionsFolder.Children.Add(node);
        }
        TreeNode viewsFolder = Folder(dbNode, FolderKind.Views, Loc["Tree_Views"], views.Count, depth);
        foreach (CollectionInfo view in views)
        {
            viewsFolder.Children.Add(new TreeNode(NodeKind.View, view.Name, depth + 1, viewsFolder) { Database = db, Collection = view });
        }
        TreeNode bucketsFolder = Folder(dbNode, FolderKind.Buckets, Loc["Tree_Buckets"], buckets.Count, depth);
        foreach (GridFsBucketInfo bucket in buckets)
        {
            bucketsFolder.Children.Add(new TreeNode(NodeKind.Bucket, bucket.Name, depth + 1, bucketsFolder) { Database = db, Bucket = bucket });
        }
        TreeNode usersFolder = Folder(dbNode, FolderKind.Users, Loc["Tree_Users"], 0, depth);
        usersFolder.Meta = "";
        TreeNode? functionsFolder = hasSystemJs ? Folder(dbNode, FolderKind.Functions, Loc["Tree_Functions"], 0, depth) : null;
        dbNode.IsLoaded = true;
        Shell.RebuildVisible();

        // 计数、TTL、用户数与存储函数:并发补,补到哪行哪行就亮 —— 不让第一次展开等它们。
        _ = FillCountsAsync(collectionsFolder.Children.ToList());
        _ = FillBucketCountsAsync(bucketsFolder.Children.ToList());
        _ = FillUsersAsync(usersFolder);
        if (functionsFolder is not null)
        {
            _ = FillFunctionsAsync(functionsFolder);
        }
    }

    private static TreeNode Folder(TreeNode dbNode, FolderKind kind, string label, int count, int depth)
    {
        var folder = new TreeNode(NodeKind.Folder, label, depth, dbNode)
        {
            Database = dbNode.Name,
            Folder = kind,
            Meta = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IsLoaded = true,
            // 集合与视图默认展开(设计稿);用户与存储函数默认收起 —— 那两组很少是此行的目的。
            IsExpanded = kind is FolderKind.Collections or FolderKind.Views or FolderKind.Buckets
        };
        dbNode.Children.Add(folder);
        return folder;
    }

    private async Task FillCountsAsync(IReadOnlyList<TreeNode> nodes)
    {
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(nodes.Select(async node =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                CollectionInfo info = node.Collection!;
                long? count = await Connection.EstimatedCountAsync(info.Database, info.Name).ConfigureAwait(false);
                if (count is { } n)
                {
                    node.Meta = BsonText.Count(n);
                }
                // TTL 徽章要看索引;集合太多时只看前 60 个(徽章是提示,不值得为它打几百次往返)。
                if (info.Kind == CollectionKind.Collection && nodes.Count <= 60)
                {
                    IReadOnlyList<BsonDocument> indexes = await Connection.ListIndexesAsync(info.Database, info.Name).ConfigureAwait(false);
                    if (MongoConnection.HasTtl(indexes))
                    {
                        node.Tag = Loc["Tag_Ttl"];
                        node.TagClass = "warn";
                    }
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                // 某个集合没权限读计数:那一行不显示计数,不影响其余。
            }
            finally
            {
                _ = gate.Release();
            }
        })).ConfigureAwait(false);
    }

    private async Task FillBucketCountsAsync(IReadOnlyList<TreeNode> nodes)
    {
        foreach (TreeNode node in nodes)
        {
            try
            {
                long? files = await Connection.EstimatedCountAsync(node.Database, node.Bucket!.FilesCollection).ConfigureAwait(false);
                if (files is { } n)
                {
                    node.Meta = Loc.Format("Tree_FileCount", BsonText.Grouped(n));
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
            }
        }
    }

    private async Task FillUsersAsync(TreeNode folder)
    {
        try
        {
            BsonDocument users = await Connection.RunCommandAsync(folder.Database, new BsonDocument("usersInfo", 1)).ConfigureAwait(false);
            BsonDocument roles = await Connection.RunCommandAsync(folder.Database, new BsonDocument
            {
                { "rolesInfo", 1 },
                { "showBuiltinRoles", false }
            }).ConfigureAwait(false);
            List<TreeNode> children =
            [
                .. users.GetValue("users", new BsonArray()).AsBsonArray
                    .Select(u => new TreeNode(NodeKind.User, u["user"].AsString, folder.Depth + 1, folder) { Database = folder.Database }),
                .. roles.GetValue("roles", new BsonArray()).AsBsonArray
                    .Select(r => new TreeNode(NodeKind.Role, r["role"].AsString, folder.Depth + 1, folder) { Database = folder.Database })
            ];
            Dispatcher.UIThread.Post(() =>
            {
                folder.Children.Clear();
                foreach (TreeNode child in children)
                {
                    folder.Children.Add(child);
                }
                folder.Meta = children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (folder.IsExpanded)
                {
                    Shell.RebuildVisible();
                }
            });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // 没有 viewUser 权限:这一组留空,不报错 —— 大多数业务账号本来就没这个权限。
        }
    }

    private async Task FillFunctionsAsync(TreeNode folder)
    {
        try
        {
            List<BsonDocument> functions = await Connection.Collection(folder.Database, "system.js")
                .Find(FilterDefinition<BsonDocument>.Empty).Limit(200).ToListAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                folder.Children.Clear();
                foreach (BsonDocument fn in functions)
                {
                    folder.Children.Add(new TreeNode(NodeKind.Function, fn.GetValue("_id", "").ToString() ?? "", folder.Depth + 1, folder)
                    {
                        Database = folder.Database
                    });
                }
                folder.Meta = functions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (folder.IsExpanded)
                {
                    Shell.RebuildVisible();
                }
            });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
        }
    }

    /// <summary>集合统计(信息区、删除确认用;同一个集合只取一次)。</summary>
    internal async Task<CollectionStats> GetStatsCachedAsync(string database, string collection, CancellationToken cancellationToken = default)
    {
        string key = $"{database}.{collection}";
        if (_statsCache.TryGetValue(key, out CollectionStats? cached))
        {
            return cached;
        }
        CollectionStats stats = await Connection.GetStatsAsync(database, collection, cancellationToken).ConfigureAwait(true);
        _statsCache[key] = stats;
        return stats;
    }

    // ── 写护栏 ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool EnsureWritable(string database)
    {
        if (Guard.Check(database) is not { } reason)
        {
            return true;
        }
        Toast(new()
        {
            Title = Loc[reason],
            Kind = ToastKind.Warning,
            ActionLabel = Guard.IsReadOnly ? Loc["Toolbar_Unlock"] : null,
            Action = Guard.IsReadOnly ? UnlockAsync : null,
            Duration = TimeSpan.FromSeconds(6)
        });
        return false;
    }

    /// <summary>解除只读:生产连接要先确认一句(写清楚是哪条连接)。</summary>
    /// <returns>任务。</returns>
    internal async Task UnlockAsync()
    {
        if (Guard.IsProduction && !await ConfirmAsync(new()
        {
            Title = Loc["Toolbar_UnlockTitle"],
            Message = Loc.Format("Toolbar_UnlockBody", ConnectionName),
            ConfirmLabel = Loc["Toolbar_Unlock"],
            IconKey = "Mongo.lock-open",
            Danger = true
        }).ConfigureAwait(true))
        {
            return;
        }
        Guard.IsReadOnly = false;
    }

    private void OnGuardChanged() => Shell.OnSessionChanged(this);

    private void OnAvailability(bool available) => Dispatcher.UIThread.Post(() =>
    {
        IsAvailable = available;
        Shell.OnSessionChanged(this);
    });

    private void OnLatency(int ms) => Dispatcher.UIThread.Post(() =>
    {
        LatencyMs = ms;
        Shell.OnSessionChanged(this);
    });

    // ── 范围与打开标签 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public (string? Database, string? Collection) Scope
    {
        get
        {
            TreeNode? node = Shell.SelectedNode?.Owner == Entry ? Shell.SelectedNode : null;
            string? db = node?.Database is { Length: > 0 } d ? d : DefaultDatabase();
            string? coll = node?.Collection?.Name ?? (node?.Bucket is { } b ? b.Name : null);
            if (coll is null && ReferenceEquals(Shell.ActiveTab?.Owner, this))
            {
                switch (Shell.ActiveTab)
                {
                    case CollectionTabViewModel collection:
                        return (collection.Database, collection.CollectionName);
                    case DesignTabViewModel design:
                        return (design.Database, design.CollectionName);
                    case PipelineTabViewModel pipeline:
                        return (pipeline.Database, pipeline.CollectionName);
                }
            }
            return (db, coll);
        }
    }

    /// <inheritdoc />
    public void OpenCollection(string database, string collection, string? filter = null)
    {
        CollectionTabViewModel tab = Shell.Activate(new CollectionTabViewModel(this, FindCollection(database, collection), filter));
        if (filter is not null && tab.FilterText != filter)
        {
            tab.ApplyFilter(filter);
        }
    }

    /// <inheritdoc />
    public void OpenQuery(string database, string? text = null, bool run = false) =>
        Shell.Activate(new QueryTabViewModel(this, database, text, run, NextQueryNumber()));

    /// <inheritdoc />
    public void OpenPipeline(string database, string collection, BsonArray? pipeline = null) =>
        Shell.Activate(new PipelineTabViewModel(this, FindCollection(database, collection), pipeline));

    /// <inheritdoc />
    public void OpenDesign(string database, string collection, DesignPage page = DesignPage.Indexes)
    {
        DesignTabViewModel tab = Shell.Activate(new DesignTabViewModel(this, FindCollection(database, collection), page));
        tab.Page = page;
    }

    /// <inheritdoc />
    public void OpenGridFs(string database, string bucket) => ActivateGridFs(database, bucket);

    /// <summary>打开(或切到)一个桶的标签并把它交回来(对象树的「上传文件…」要接着对它发起上传)。</summary>
    internal GridFsTabViewModel ActivateGridFs(string database, string bucket) =>
        Shell.Activate(new GridFsTabViewModel(this, new GridFsBucketInfo(database, bucket)));

    /// <inheritdoc />
    public void OpenObjects(string database, ObjectFilter filter = ObjectFilter.All)
    {
        ObjectsTabViewModel tab = Shell.Activate(new ObjectsTabViewModel(this, database, filter));
        tab.Filter = filter;
        Shell.OnTabToolChanged();
    }

    /// <inheritdoc />
    public void OpenMonitor() => Shell.Activate(new MonitorTabViewModel(this));

    /// <inheritdoc />
    public void OpenProfiler(string database) => Shell.Activate(new ProfilerTabViewModel(this, database));

    /// <inheritdoc />
    public void OpenUsers(string? database, bool roles = false)
    {
        UsersTabViewModel tab = Shell.Activate(new UsersTabViewModel(this, database, roles));
        tab.ShowRoles = roles;
        Shell.OnTabToolChanged();
    }

    /// <summary>关掉本连接里指向某个集合的标签(删掉它之后)。</summary>
    /// <param name="ns">命名空间 <c>db.coll</c>。</param>
    /// <param name="except">留着的那个(发起删除的对象列表本身)。</param>
    internal void CloseTabsOf(string ns, WorkspaceTab? except = null) =>
        Shell.CloseTabsWhere(t => ReferenceEquals(t.Owner, this) && !ReferenceEquals(t, except)
                                  && t.Key.EndsWith($":{ns}", StringComparison.Ordinal));

    // ── 外壳服务(转给外壳)──────────────────────────────────────────────

    /// <inheritdoc />
    public void ShowDialog(DialogViewModel dialog) => Shell.ShowDialog(dialog);

    /// <inheritdoc />
    public void CloseDialog(DialogViewModel dialog) => Shell.CloseDialog(dialog);

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(ConfirmRequest request) => Shell.ConfirmAsync(request);

    /// <inheritdoc />
    public void Toast(ToastRequest toast) => Shell.Toast(toast);

    /// <inheritdoc />
    public Task CopyAsync(string text) => Shell.CopyAsync(text);

    /// <inheritdoc />
    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<FileKind> kinds) =>
        Shell.PickSaveFileAsync(title, suggestedName, kinds);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileKind> kinds, bool multiple = false) =>
        Shell.PickOpenFilesAsync(title, kinds, multiple);

    /// <inheritdoc />
    public Task<string?> PickFolderAsync(string title) => Shell.PickFolderAsync(title);

    /// <inheritdoc />
    public void NotifyStatusChanged() => Shell.NotifyStatusChanged();

    /// <summary>断开前摘掉事件与登记(连接本身由外壳异步关)。</summary>
    public void Dispose()
    {
        Guard.Changed -= OnGuardChanged;
        Connection.Availability -= OnAvailability;
        Connection.LatencyChanged -= OnLatency;
        _registration.Dispose();
        Root.Children.Clear();
        Root.IsExpanded = false;
        _databaseNodes.Clear();
        _collections.Clear();
        _statsCache.Clear();
    }
}
