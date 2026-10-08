using System.Collections.ObjectModel;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>信息区的一行。</summary>
/// <param name="Label">左边的标签。</param>
/// <param name="Value">右边的值。</param>
internal sealed record DetailRow(string Label, string Value);

/// <summary>
/// 外壳的对象树:根上是全部已保存的连接,连着的那几条下面挂各自的库(那一支由 <see cref="MongoSession" /> 维护)。
/// 这里只管拍平成可见行、选中、筛选与底部信息区。
/// </summary>
internal sealed partial class MongoWorkspaceViewModel
{
    private CancellationTokenSource? _detailLoad;
    private readonly Dictionary<string, TreeNode> _groups = [with(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>拍平后的可见行(虚拟化的 ListBox 直接绑它)。</summary>
    public ObservableCollection<TreeNode> VisibleNodes { get; } = [];

    /// <summary>选中的行(底部信息区与工具栏的作用范围跟着它)。</summary>
    public TreeNode? SelectedNode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                UpdateCurrentSession();
                RaisePropertiesChanged(nameof(StatusScope), nameof(StatusDetail));
                _ = LoadDetailsAsync(value);
            }
        }
    }

    /// <summary>对象树筛选(只按名字;连接行永远留着)。</summary>
    public string TreeFilter
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RebuildVisible();
            }
        }
    } = "";

    /// <summary>信息区标题。</summary>
    public string DetailTitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>信息区标题右边的小字。</summary>
    public string DetailKind
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>信息区的行。</summary>
    public ObservableCollection<DetailRow> DetailRows { get; } = [];

    /// <summary>当前连接是否列出系统库(眼睛按钮)。</summary>
    public bool ShowSystemDatabases
    {
        get => CurrentSession?.ShowSystemDatabases ?? false;
        set
        {
            if (CurrentSession is { } session && session.ShowSystemDatabases != value)
            {
                session.ShowSystemDatabases = value;
                RaisePropertiesChanged(nameof(ShowSystemDatabases), nameof(SystemDatabasesTip));
            }
        }
    }

    /// <summary>眼睛按钮的提示。</summary>
    public string SystemDatabasesTip => Loc[ShowSystemDatabases ? "Nav_HideSystemDbs" : "Nav_ShowSystemDbs"];

    private void DisposeTree()
    {
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
    }

    /// <summary>重新拍平可见行。只在真变了时替换 —— 整表重设会让列表丢掉滚动位置。</summary>
    internal void RebuildVisible()
    {
        TreeNode? selected = SelectedNode;
        var rows = new List<TreeNode>();
        string filter = TreeFilter.Trim();
        // 不分组的连接在最上面;分了组的按组名排,每组一行可收起的节标题(设计稿 10 的「分组」)。
        foreach (ConnectionEntry entry in Connections.Where(static c => c.Profile.Group.Length == 0))
        {
            AddConnection(entry, rows);
        }
        foreach (IGrouping<string, ConnectionEntry> group in Connections
                     .Where(static c => c.Profile.Group.Length > 0)
                     .GroupBy(static c => c.Profile.Group, StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(static g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_groups.TryGetValue(group.Key, out TreeNode? header))
            {
                header = new TreeNode(NodeKind.Group, group.Key, 0, null) { IsLoaded = true, IsExpanded = true };
                _groups[group.Key] = header;
            }
            header.Meta = group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var below = new List<TreeNode>();
            foreach (ConnectionEntry entry in group)
            {
                AddConnection(entry, below);
            }
            if (filter.Length == 0 || below.Count > 0 || header.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                rows.Add(header);
                if (header.IsExpanded)
                {
                    rows.AddRange(below);
                }
            }
        }

        void AddConnection(ConnectionEntry entry, List<TreeNode> into)
        {
            if (filter.Length == 0)
            {
                Walk(entry.Root, into);
            }
            else
            {
                _ = WalkFiltered(entry.Root, into, filter);
            }
        }
        if (rows.SequenceEqual(VisibleNodes))
        {
            return;
        }
        VisibleNodes.Clear();
        foreach (TreeNode row in rows)
        {
            VisibleNodes.Add(row);
        }
        if (selected is not null && rows.Contains(selected))
        {
            RaisePropertyChanged(nameof(SelectedNode));
        }
    }

    private static void Walk(TreeNode node, List<TreeNode> rows)
    {
        rows.Add(node);
        if (!node.IsExpanded)
        {
            return;
        }
        foreach (TreeNode child in node.Children)
        {
            Walk(child, rows);
        }
    }

    private static bool WalkFiltered(TreeNode node, List<TreeNode> rows, string filter)
    {
        bool self = node.Kind != NodeKind.Placeholder && node.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var below = new List<TreeNode>();
        bool any = false;
        foreach (TreeNode child in node.Children)
        {
            any |= WalkFiltered(child, below, filter);
        }
        if (self || any || node.Kind == NodeKind.Connection)
        {
            rows.Add(node);
            rows.AddRange(below);
            return true;
        }
        return false;
    }

    /// <summary>切到一个标签时,在树上选中它对应的那一行(那条连接没展开到那儿就不动)。</summary>
    internal void RevealInTree(WorkspaceTab? tab)
    {
        if (tab?.Owner is not MongoSession session)
        {
            return;
        }
        (string? db, string? name) = tab switch
        {
            CollectionTabViewModel c => (c.Database, c.CollectionName),
            DesignTabViewModel d => (d.Database, d.CollectionName),
            PipelineTabViewModel p => (p.Database, p.CollectionName),
            GridFsTabViewModel g => (g.Bucket.Database, g.Bucket.Name),
            ObjectsTabViewModel o => (o.Database, null),
            ProfilerTabViewModel pr => (pr.Database, null),
            _ => (null, null)
        };
        if (db is null || session.Root.Children.FirstOrDefault(n => n.Name == db) is not { } dbNode)
        {
            return;
        }
        TreeNode? target = dbNode;
        if (name is not null)
        {
            target = dbNode.Children
                .SelectMany(static folder => folder.Children)
                .FirstOrDefault(n => n.Name == name && n.Kind is NodeKind.Collection or NodeKind.View or NodeKind.Bucket);
        }
        if (target is not null && !ReferenceEquals(target, SelectedNode) && VisibleNodes.Contains(target))
        {
            SelectedNode = target;
        }
    }

    internal void CollapseAll()
    {
        foreach (ConnectionEntry entry in Connections)
        {
            foreach (TreeNode db in entry.Root.Children)
            {
                db.IsExpanded = false;
            }
        }
        RebuildVisible();
    }

    /// <summary>点箭头:连接行没连着就去连;连着的收起 / 展开。</summary>
    public async Task ToggleAsync(TreeNode node)
    {
        if (!node.IsExpandable)
        {
            return;
        }
        if (node.Kind == NodeKind.Connection && node.Owner is { } entry && entry.State != ConnectionState.Connected)
        {
            await ConnectAsync(entry).ConfigureAwait(true);
            return;
        }
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            RebuildVisible();
            return;
        }
        await ExpandAsync(node).ConfigureAwait(true);
    }

    private async Task ExpandAsync(TreeNode node)
    {
        if (node.Kind == NodeKind.Database && node.Session is { } session)
        {
            await session.ExpandDatabaseAsync(node).ConfigureAwait(true);
            return;
        }
        node.IsExpanded = true;
        RebuildVisible();
    }

    /// <summary>双击 / 回车:连接行连上(或展开),对象行打开对应的标签。</summary>
    internal async Task OpenNodeAsync(TreeNode node)
    {
        if (node.Kind == NodeKind.Group)
        {
            await ToggleAsync(node).ConfigureAwait(true);
            return;
        }
        if (node.Kind == NodeKind.Connection)
        {
            if (node.Owner is { State: not ConnectionState.Connected } entry)
            {
                await ConnectAsync(entry).ConfigureAwait(true);
            }
            else
            {
                await ToggleAsync(node).ConfigureAwait(true);
            }
            return;
        }
        if (node.Session is not { } session)
        {
            return;
        }
        switch (node.Kind)
        {
            case NodeKind.Collection or NodeKind.View:
                session.OpenCollection(node.Database, node.Collection!.Name);
                break;
            case NodeKind.Bucket:
                session.OpenGridFs(node.Database, node.Bucket!.Name);
                break;
            case NodeKind.Database:
                session.OpenObjects(node.Database);
                if (!node.IsExpanded)
                {
                    await ExpandAsync(node).ConfigureAwait(true);
                }
                break;
            case NodeKind.Folder when node.Folder == FolderKind.Users:
                session.OpenUsers(node.Database);
                break;
            case NodeKind.User:
                session.OpenUsers(node.Database);
                break;
            case NodeKind.Role:
                session.OpenUsers(node.Database, roles: true);
                break;
            case NodeKind.Function:
                session.OpenQuery(node.Database, $"db.system.js.findOne({{ _id: {BsonText.Quote(node.Name)} }})", run: true);
                break;
            case NodeKind.Folder:
                await ToggleAsync(node).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>一条连接的状态变了(连上、没连上、断开):选中的正是它那一行就重读底部信息区。</summary>
    internal void RefreshDetailsFor(ConnectionEntry entry)
    {
        if (SelectedNode is { Kind: NodeKind.Connection } node && node.Owner == entry)
        {
            _ = LoadDetailsAsync(node);
        }
    }

    private async Task LoadDetailsAsync(TreeNode? node)
    {
        _detailLoad?.Cancel();
        _detailLoad?.Dispose();
        var cts = new CancellationTokenSource();
        _detailLoad = cts;
        DetailRows.Clear();
        if (node is null)
        {
            DetailTitle = "";
            DetailKind = "";
            return;
        }
        DetailTitle = node.Name;
        if (node.Kind == NodeKind.Connection && node.Owner is { } owner && owner.Session is null)
        {
            // 没连着的连接:信息区讲它连的是哪儿,不去连。
            DetailKind = Loc[owner.State == ConnectionState.Failed ? "Conn_StateFailed" : "Conn_StateOffline"];
            DetailRows.Add(new(Loc["Detail_Endpoint"], owner.Detail.Replace("MongoDB · ", "", StringComparison.Ordinal)));
            DetailRows.Add(new(Loc["Detail_User"], owner.Profile.Username is { Length: > 0 } u ? u : Loc["Detail_Anonymous"]));
            return;
        }
        if (node.Session is not { } session)
        {
            DetailKind = "";
            return;
        }
        try
        {
            switch (node.Kind)
            {
                case NodeKind.Collection:
                    {
                        DetailKind = "WiredTiger";
                        CollectionStats stats = await session.GetStatsCachedAsync(node.Database, node.Name, cts.Token).ConfigureAwait(true);
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }
                        DetailRows.Add(new(Loc["Detail_Documents"], BsonText.Grouped(stats.Count)));
                        DetailRows.Add(new(Loc["Detail_StorageIndex"], $"{BsonText.Bytes(stats.Size)} / {BsonText.Bytes(stats.TotalIndexSize)}"));
                        DetailRows.Add(new(Loc["Detail_Indexes"], Loc.Format("Detail_IndexSummary", stats.IndexCount, BsonText.Bytes(stats.AvgObjSize))));
                        break;
                    }
                case NodeKind.View:
                    DetailKind = Loc["Detail_View"];
                    DetailRows.Add(new(Loc["Detail_ViewOn"], node.Collection?.ViewOn ?? "—"));
                    DetailRows.Add(new(Loc["Detail_Stages"], (node.Collection?.Pipeline?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    break;
                case NodeKind.Database:
                    {
                        DetailKind = Loc["Detail_Database"];
                        BsonDocument stats = await session.Connection.GetDatabaseStatsAsync(node.Name, cts.Token).ConfigureAwait(true);
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }
                        IReadOnlyList<CollectionInfo> all = session.CollectionsOf(node.Name);
                        int views = all.Count(static c => c.Kind == CollectionKind.View);
                        int buckets = MongoConnection.FindBuckets(node.Name, all).Count;
                        DetailRows.Add(new(Loc["Detail_CollectionsViews"], $"{stats.GetValue("collections", 0).ToInt64() - (buckets * 2)} / {views}"));
                        DetailRows.Add(new(Loc["Detail_DataIndex"],
                            $"{BsonText.Bytes(stats.GetValue("dataSize", 0).ToInt64())} / {BsonText.Bytes(stats.GetValue("indexSize", 0).ToInt64())}"));
                        DetailRows.Add(new("GridFS", Loc.Format("Detail_Buckets", buckets)));
                        break;
                    }
                case NodeKind.Bucket:
                    {
                        DetailKind = Loc["Detail_Bucket"];
                        GridFsBucketInfo bucket = node.Bucket!;
                        CollectionStats files = await session.GetStatsCachedAsync(node.Database, bucket.FilesCollection, cts.Token).ConfigureAwait(true);
                        CollectionStats chunks = await session.GetStatsCachedAsync(node.Database, bucket.ChunksCollection, cts.Token).ConfigureAwait(true);
                        if (cts.IsCancellationRequested)
                        {
                            return;
                        }
                        DetailRows.Add(new(Loc["Detail_Files"], BsonText.Grouped(files.Count)));
                        DetailRows.Add(new(Loc["Detail_TotalSize"], BsonText.Bytes(chunks.Size)));
                        DetailRows.Add(new(Loc["Detail_Chunks"], $"{BsonText.Grouped(chunks.Count)} × 255 KB"));
                        break;
                    }
                case NodeKind.Connection:
                    DetailKind = session.Connection.Server.Badge;
                    DetailRows.Add(new(Loc["Detail_Version"], session.Connection.Server.Version));
                    DetailRows.Add(new(Loc["Detail_Endpoint"], session.Connection.Endpoint));
                    DetailRows.Add(new(Loc["Detail_User"], session.Connection.Privileges.User is { Length: > 0 } u ? u : Loc["Detail_Anonymous"]));
                    break;
                default:
                    DetailKind = "";
                    break;
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            // 信息区是锦上添花:取不到就留空。
        }
    }
}
