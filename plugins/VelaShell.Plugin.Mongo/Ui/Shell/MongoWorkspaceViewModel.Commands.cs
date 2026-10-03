using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 工具栏与对象树的命令。工具栏作用于当前连接(<see cref="CurrentSession" />);
/// 树上的右键命令作用于那一行所在的连接 —— 根上挂着好几条连接时,两者不一定是同一条。
/// </summary>
internal sealed partial class MongoWorkspaceViewModel
{
    /// <summary>新建连接(工具栏最左的「连接」、对象树表头的 +)。</summary>
    public RelayCommand NewConnectionCommand { get; private set; } = null!;

    /// <summary>新查询。</summary>
    public RelayCommand NewQueryCommand { get; private set; } = null!;

    /// <summary>聚合。</summary>
    public RelayCommand AggregateCommand { get; private set; } = null!;

    /// <summary>集合。</summary>
    public RelayCommand CollectionsCommand { get; private set; } = null!;

    /// <summary>视图。</summary>
    public RelayCommand ViewsCommand { get; private set; } = null!;

    /// <summary>索引。</summary>
    public RelayCommand IndexesCommand { get; private set; } = null!;

    /// <summary>GridFS。</summary>
    public RelayCommand GridFsCommand { get; private set; } = null!;

    /// <summary>用户。</summary>
    public RelayCommand UsersCommand { get; private set; } = null!;

    /// <summary>角色。</summary>
    public RelayCommand RolesCommand { get; private set; } = null!;

    /// <summary>导入。</summary>
    public RelayCommand ImportCommand { get; private set; } = null!;

    /// <summary>导出。</summary>
    public RelayCommand ExportCommand { get; private set; } = null!;

    /// <summary>转储。</summary>
    public RelayCommand DumpCommand { get; private set; } = null!;

    /// <summary>数据传输。</summary>
    public RelayCommand TransferCommand { get; private set; } = null!;

    /// <summary>服务器监控。</summary>
    public RelayCommand MonitorCommand { get; private set; } = null!;

    /// <summary>慢查询。</summary>
    public RelayCommand ProfilerCommand { get; private set; } = null!;

    /// <summary>只读开关(解锁生产连接要确认)。</summary>
    public AsyncCommand ToggleReadOnlyCommand { get; private set; } = null!;

    /// <summary>新建集合(对象树右键)。</summary>
    public RelayCommand NewCollectionCommand { get; private set; } = null!;

    /// <summary>刷新当前连接的树。</summary>
    public AsyncCommand RefreshTreeCommand { get; private set; } = null!;

    /// <summary>当前连接列不列系统库。</summary>
    public RelayCommand ToggleSystemDatabasesCommand { get; private set; } = null!;

    /// <summary>收起全部库。</summary>
    public RelayCommand CollapseAllCommand { get; private set; } = null!;

    /// <summary>点箭头。</summary>
    public AsyncCommand<TreeNode> ToggleNodeCommand { get; private set; } = null!;

    /// <summary>双击 / 回车。</summary>
    public AsyncCommand<TreeNode> OpenNodeCommand { get; private set; } = null!;

    /// <summary>连接(连接行右键)。</summary>
    public AsyncCommand<TreeNode> ConnectNodeCommand { get; private set; } = null!;

    /// <summary>断开(连接行右键)。</summary>
    public AsyncCommand<TreeNode> DisconnectNodeCommand { get; private set; } = null!;

    /// <summary>编辑连接(连接行右键)。</summary>
    public RelayCommand<TreeNode> EditConnectionCommand { get; private set; } = null!;

    /// <summary>复制一条连接(连接行右键)。</summary>
    public RelayCommand<TreeNode> DuplicateConnectionCommand { get; private set; } = null!;

    /// <summary>删除连接(连接行右键)。</summary>
    public AsyncCommand<TreeNode> DeleteConnectionCommand { get; private set; } = null!;

    /// <summary>设计。</summary>
    public RelayCommand<TreeNode> DesignNodeCommand { get; private set; } = null!;

    /// <summary>聚合。</summary>
    public RelayCommand<TreeNode> PipelineNodeCommand { get; private set; } = null!;

    /// <summary>新查询。</summary>
    public RelayCommand<TreeNode> QueryNodeCommand { get; private set; } = null!;

    /// <summary>导出。</summary>
    public RelayCommand<TreeNode> ExportNodeCommand { get; private set; } = null!;

    /// <summary>导入。</summary>
    public RelayCommand<TreeNode> ImportNodeCommand { get; private set; } = null!;

    /// <summary>复制名称。</summary>
    public AsyncCommand<TreeNode> CopyNameCommand { get; private set; } = null!;

    /// <summary>清空集合。</summary>
    public AsyncCommand<TreeNode> EmptyCollectionCommand { get; private set; } = null!;

    /// <summary>删除集合 / 视图。</summary>
    public AsyncCommand<TreeNode> DropCollectionCommand { get; private set; } = null!;

    /// <summary>删除数据库。</summary>
    public AsyncCommand<TreeNode> DropDatabaseCommand { get; private set; } = null!;

    /// <summary>刷新这一支。</summary>
    public AsyncCommand<TreeNode> RefreshNodeCommand { get; private set; } = null!;

    /// <summary>慢查询。</summary>
    public RelayCommand<TreeNode> ProfilerNodeCommand { get; private set; } = null!;

    private void InitializeCommands()
    {
        NewConnectionCommand = new(() => NewConnection());
        NewQueryCommand = new(() => WithDatabase((s, db) => s.OpenQuery(db)));
        AggregateCommand = new(() => WithCollection((s, db, coll) => s.OpenPipeline(db, coll)));
        CollectionsCommand = new(() => WithSession(s =>
        {
            (string? db, string? coll) = s.Scope;
            if (db is not null && coll is not null && SelectedNode?.Kind is NodeKind.Collection or NodeKind.View)
            {
                s.OpenCollection(db, coll);
            }
            else if (db is not null)
            {
                s.OpenObjects(db, ObjectFilter.Collections);
            }
        }));
        ViewsCommand = new(() => WithDatabase((s, db) => s.OpenObjects(db, ObjectFilter.Views)));
        IndexesCommand = new(() => WithCollection((s, db, coll) => s.OpenDesign(db, coll, DesignPage.Indexes)));
        GridFsCommand = new(() => WithDatabase((s, db) =>
        {
            string? bucket = SelectedNode?.Owner == s.Entry ? SelectedNode?.Bucket?.Name : null;
            bucket ??= MongoConnection.FindBuckets(db, s.CollectionsOf(db)).FirstOrDefault()?.Name;
            if (bucket is null)
            {
                s.OpenObjects(db, ObjectFilter.GridFs);
                return;
            }
            s.OpenGridFs(db, bucket);
        }));
        UsersCommand = new(() => WithSession(s => s.OpenUsers(s.Scope.Database)));
        RolesCommand = new(() => WithSession(s => s.OpenUsers(s.Scope.Database, roles: true)));
        ImportCommand = new(() => WithCollection((s, db, coll) => ShowDialog(new ImportWizardViewModel(s, db, coll))));
        ExportCommand = new(() => WithDatabase((s, db) =>
            ShowDialog(new ExportWizardViewModel(s, db, s.Scope.Collection, CurrentQueryFor(s, db, s.Scope.Collection), null))));
        DumpCommand = new(() => WithDatabase((s, db) => ShowDialog(new ExportWizardViewModel(s, db, s.Scope.Collection, null, ExportFormat.BsonDump))));
        TransferCommand = new(() => WithDatabase((s, db) => ShowDialog(new TransferWizardViewModel(s, db))));
        MonitorCommand = new(() => WithSession(s => s.OpenMonitor()));
        ProfilerCommand = new(() => WithDatabase((s, db) => s.OpenProfiler(db)));
        ToggleReadOnlyCommand = new(async () =>
        {
            if (_currentSession is not { } session)
            {
                return;
            }
            if (session.Guard.IsReadOnly)
            {
                await session.UnlockAsync().ConfigureAwait(true);
            }
            else
            {
                session.Guard.IsReadOnly = true;
            }
        });

        NewCollectionCommand = new(() => WithDatabase((s, db) => ShowDialog(new NewCollectionDialogViewModel(s, db))));
        RefreshTreeCommand = new(async () =>
        {
            if (_currentSession is { } session)
            {
                session.ClearStats();
                await session.ReloadTreeAsync().ConfigureAwait(true);
            }
        });
        ToggleSystemDatabasesCommand = new(() => ShowSystemDatabases = !ShowSystemDatabases);
        CollapseAllCommand = new(CollapseAll);
        ToggleNodeCommand = new(ToggleAsync);
        OpenNodeCommand = new(OpenNodeAsync);

        ConnectNodeCommand = new(node => node.Owner is { } entry ? ConnectAsync(entry) : Task.CompletedTask,
            static node => node.Owner is { State: ConnectionState.Disconnected or ConnectionState.Failed });
        DisconnectNodeCommand = new(node => node.Owner is { } entry ? DisconnectAsync(entry) : Task.CompletedTask,
            static node => node.Owner is { State: ConnectionState.Connected or ConnectionState.Connecting });
        EditConnectionCommand = new(node => EditConnection(node.Owner!), static node => node.Owner is not null);
        DuplicateConnectionCommand = new(node => DuplicateConnection(node.Owner!), static node => node.Owner is not null);
        DeleteConnectionCommand = new(node => node.Owner is { } entry ? DeleteConnectionAsync(entry) : Task.CompletedTask,
            static node => node.Owner is not null);

        DesignNodeCommand = new(node => node.Session?.OpenDesign(node.Database, node.Name), static node => node is { Kind: NodeKind.Collection, Session: not null });
        PipelineNodeCommand = new(node => node.Session?.OpenPipeline(node.Database, node.Name),
            static node => node is { Kind: NodeKind.Collection or NodeKind.View, Session: not null });
        QueryNodeCommand = new(node => node.Session?.OpenQuery(
                node.Database.Length > 0 ? node.Database : node.Session.DefaultDatabase() ?? "test",
                node.Kind is NodeKind.Collection or NodeKind.View ? $"db.{ShellCollectionRef(node.Name)}.find({{}}).limit(50)" : null),
            static node => node.Session is not null);
        ExportNodeCommand = new(node => ShowDialog(new ExportWizardViewModel(node.Session!, node.Database,
                node.Kind is NodeKind.Collection or NodeKind.View ? node.Name : null, null, null)),
            static node => node is { Session: not null, Database.Length: > 0 });
        ImportNodeCommand = new(node => ShowDialog(new ImportWizardViewModel(node.Session!, node.Database, node.Name)),
            static node => node is { Kind: NodeKind.Collection, Session: not null });
        CopyNameCommand = new(node => CopyAsync(node.Namespace ?? node.Name));
        EmptyCollectionCommand = new(EmptyCollectionAsync, static node => node is { Kind: NodeKind.Collection, Session: not null });
        DropCollectionCommand = new(DropCollectionAsync, static node => node is { Kind: NodeKind.Collection or NodeKind.View, Session: not null });
        DropDatabaseCommand = new(DropDatabaseAsync,
            static node => node is { Kind: NodeKind.Database, Session: { } session } && !session.Guard.DisableDropDatabase);
        RefreshNodeCommand = new(node => node.Session?.RefreshTreeAsync(node.Database.Length > 0 ? node.Database : null) ?? Task.CompletedTask,
            static node => node.Session is not null);
        ProfilerNodeCommand = new(node => node.Session?.OpenProfiler(node.Database), static node => node is { Session: not null, Database.Length: > 0 });
    }

    private FindRequest? CurrentQueryFor(MongoSession session, string database, string? collection) =>
        collection is null
            ? null
            : Tabs.OfType<CollectionTabViewModel>()
                .FirstOrDefault(t => ReferenceEquals(t.Owner, session) && t.Database == database && t.CollectionName == collection)?.CurrentRequest;

    /// <summary>没有当前连接时,工具栏的按钮说一句「先连一条」,而不是什么都不发生。</summary>
    private void WithSession(Action<MongoSession> action)
    {
        if (_currentSession is { } session)
        {
            action(session);
            return;
        }
        Toast(new() { Title = Loc[Connections.Count == 0 ? "Conn_NoneYet" : "Conn_PickFirst"], Kind = ToastKind.Info });
    }

    private void WithDatabase(Action<MongoSession, string> action) => WithSession(s =>
    {
        if (s.Scope.Database is { } db)
        {
            action(s, db);
        }
    });

    private void WithCollection(Action<MongoSession, string, string> action) => WithSession(s =>
    {
        (string? db, string? coll) = s.Scope;
        if (db is null)
        {
            return;
        }
        // 没选中集合:落到该库的第一个集合,而不是一个"请先选择"的空弹窗 —— 选错了换一个比弹窗快。
        coll ??= s.CollectionsOf(db).FirstOrDefault(static c => c.Kind is CollectionKind.Collection or CollectionKind.TimeSeries)?.Name;
        if (coll is null)
        {
            Toast(new() { Title = Loc["Toolbar_PickCollection"], Kind = ToastKind.Info });
            return;
        }
        action(s, db, coll);
    });

    private async Task EmptyCollectionAsync(TreeNode node)
    {
        if (node.Session is not { } session || !session.EnsureWritable(node.Database))
        {
            return;
        }
        long? count = await session.Connection.EstimatedCountAsync(node.Database, node.Name).ConfigureAwait(true);
        if (!await ConfirmAsync(new()
            {
                Title = Loc["Tree_EmptyTitle"],
                Message = Loc.Format("Tree_EmptyBody", node.Namespace, BsonText.Grouped(count ?? 0)),
                ConfirmLabel = Loc["Tree_EmptyConfirm"],
                IconKey = "Mongo.eraser",
                TypeToConfirm = session.Guard.ConfirmWrites ? node.Name : null
            }).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            DeleteResult result = await session.Connection.Collection(node.Database, node.Name)
                .DeleteManyAsync(FilterDefinition<BsonDocument>.Empty).ConfigureAwait(true);
            Toast(new() { Title = Loc.Format("Tree_EmptyDone", BsonText.Grouped(result.DeletedCount)), Kind = ToastKind.Success });
            session.ClearStats();
            await session.RefreshTreeAsync(node.Database).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    internal async Task DropCollectionAsync(TreeNode node)
    {
        if (node.Session is not { } session || !session.EnsureWritable(node.Database))
        {
            return;
        }
        var facts = new List<ConfirmFact>();
        int indexCount = 0;
        if (node.Kind == NodeKind.Collection)
        {
            try
            {
                CollectionStats stats = await session.Connection.GetStatsAsync(node.Database, node.Name).ConfigureAwait(true);
                indexCount = stats.IndexCount;
                facts.Add(new(Loc["Detail_Documents"], BsonText.Grouped(stats.Count)));
                facts.Add(new(Loc["Confirm_DataSize"], BsonText.Bytes(stats.Size)));
                BsonDocument? last = await session.Connection.Collection(node.Database, node.Name)
                    .Find(FilterDefinition<BsonDocument>.Empty)
                    .Sort(new BsonDocument("$natural", -1)).Limit(1).FirstOrDefaultAsync().ConfigureAwait(true);
                if (last?.GetValue("_id", BsonNull.Value) is BsonObjectId id)
                {
                    facts.Add(new(Loc["Confirm_LastWrite"], id.Value.CreationTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
            }
        }
        bool confirmed = await ConfirmAsync(new()
        {
            Title = node.Kind == NodeKind.View ? Loc["Confirm_DropViewTitle"] : Loc["Confirm_DropTitle"],
            Message = Loc.Format(node.Kind == NodeKind.View ? "Confirm_DropViewBody" : "Confirm_DropBody", node.Namespace, indexCount),
            ConfirmLabel = node.Kind == NodeKind.View ? Loc["Confirm_DropViewTitle"] : Loc["Confirm_DropTitle"],
            Facts = facts,
            // 名称确认在两种情况下都要:生产连接(写前确认),或这次要删的东西有数据。
            TypeToConfirm = session.Guard.ConfirmWrites || facts.Count > 0 ? node.Name : null,
            AsideLabel = node.Kind == NodeKind.Collection ? Loc["Confirm_BackupFirst"] : null,
            AsideAction = () => ShowDialog(new ExportWizardViewModel(session, node.Database, node.Name, null, ExportFormat.BsonDump))
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await session.Connection.Database(node.Database).DropCollectionAsync(node.Name).ConfigureAwait(true);
            Toast(new() { Title = Loc.Format("Confirm_Dropped", node.Namespace), Kind = ToastKind.Success });
            session.CloseTabsOf($"{node.Database}.{node.Name}");
            session.ClearStats();
            await session.RefreshTreeAsync(node.Database).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    private async Task DropDatabaseAsync(TreeNode node)
    {
        if (node.Session is not { } session || session.Guard.DisableDropDatabase || !session.EnsureWritable(node.Database))
        {
            return;
        }
        if (!await ConfirmAsync(new()
            {
                Title = Loc["Confirm_DropDbTitle"],
                Message = Loc.Format("Confirm_DropDbBody", node.Name),
                ConfirmLabel = Loc["Confirm_DropDbTitle"],
                TypeToConfirm = node.Name
            }).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await session.Connection.Client.DropDatabaseAsync(node.Name).ConfigureAwait(true);
            Toast(new() { Title = Loc.Format("Confirm_Dropped", node.Name), Kind = ToastKind.Success });
            await session.ReloadTreeAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }
}
