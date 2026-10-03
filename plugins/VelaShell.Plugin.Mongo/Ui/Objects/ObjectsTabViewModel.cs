using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 对象列表(设计稿 12,Navicat 的对象页):一个库里的集合、视图、时序集合与 GridFS 桶,
/// 带统计列、过滤芯片、三种视图与右侧详情面板。
/// <para>
/// 加载分两拍:<c>listCollections</c> 一回来就先出整张表(名字、种类、验证规则都在里面),
/// 每个集合的统计再并发(限流 6)去取,取到哪行哪行补上数字 —— 几十个集合的库,
/// 第一屏不该等最慢的那个 <c>$collStats</c>。
/// </para>
/// </summary>
internal sealed partial class ObjectsTabViewModel : WorkspaceTab
{
    private readonly List<ObjectItem> _all = [];
    private readonly ConcurrentDictionary<ObjectItem, Task> _statsTasks = new();
    private ObjectFilter _filter;
    private bool _rebuilding;
    private bool _loaded;
    private DateTime _loadedAt;
    private CancellationTokenSource? _load;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">库名。</param>
    /// <param name="filter">初始过滤芯片(工具栏「视图」按钮打开时直接落在「视图」上)。</param>
    public ObjectsTabViewModel(IMongoWorkspace workspace, string database, ObjectFilter filter)
        : base(workspace)
    {
        Database = database;
        _filter = filter;
        Title = workspace.Loc["Nav_Objects"];
        Scope = "· " + database;

        OpenCommand = new(OpenSelected, () => SelectedItem is not null);
        DesignCommand = new(() => Design(DesignPage.Indexes), () => SelectedItem?.IsCollection == true);
        PipelineCommand = new(OpenPipeline, () => SelectedItem is { Kind: not ObjectKind.Bucket });
        QueryCommand = new(OpenQuery);
        NewCollectionCommand = new(() => Workspace.ShowDialog(new NewCollectionDialogViewModel(Workspace, Database)));
        DropCommand = new(DropSelectedAsync, () => SelectedItem is not null);
        EmptyCommand = new(EmptySelectedAsync, () => SelectedItem is { Kind: ObjectKind.Collection or ObjectKind.TimeSeries or ObjectKind.Clustered });
        CopyStructureCommand = new(CopyStructureAsync, () => SelectedItem is not null);
        CopyNameCommand = new(() => SelectedItem is { } s ? Workspace.CopyAsync(s.Name) : Task.CompletedTask, () => SelectedItem is not null);
        ImportCommand = new(Import, () => SelectedItem?.IsCollection == true);
        ExportCommand = new(Export);
        SortCommand = new(SortBy);
        CopyDdlCommand = new(CopyDdlAsync, () => DdlText.Length > 0);

        ObjectsChanged.Changed += OnObjectsChanged;
    }

    /// <summary>库名。</summary>
    public string Database { get; }

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Objects;

    /// <inheritdoc />
    public override string Key => $"objects:{Database}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.layout-grid";

    /// <inheritdoc />
    public override string IconToken => "VelaTextTertiary";

    // ── 过滤 / 搜索 / 视图 / 排序 ──────────────────────────────────────────

    /// <summary>过滤芯片(外壳的「集合」「视图」「GridFS」按钮也会改它)。</summary>
    public ObjectFilter Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value))
            {
                return;
            }
            RaisePropertiesChanged(nameof(FilterAll), nameof(FilterCollections), nameof(FilterViews), nameof(FilterTimeSeries), nameof(FilterGridFs));
            Rebuild();
            // 芯片是在标签内部改的时候,工具栏上「集合 / 视图」哪个亮着要跟着变。
            (Workspace as MongoSession)?.Shell.OnTabToolChanged();
        }
    }

    /// <summary>芯片:全部。</summary>
    public bool FilterAll { get => _filter == ObjectFilter.All; set { if (value) { Filter = ObjectFilter.All; } } }

    /// <summary>芯片:集合。</summary>
    public bool FilterCollections { get => _filter == ObjectFilter.Collections; set { if (value) { Filter = ObjectFilter.Collections; } } }

    /// <summary>芯片:视图。</summary>
    public bool FilterViews { get => _filter == ObjectFilter.Views; set { if (value) { Filter = ObjectFilter.Views; } } }

    /// <summary>芯片:时序。</summary>
    public bool FilterTimeSeries { get => _filter == ObjectFilter.TimeSeries; set { if (value) { Filter = ObjectFilter.TimeSeries; } } }

    /// <summary>芯片:GridFS。</summary>
    public bool FilterGridFs { get => _filter == ObjectFilter.GridFs; set { if (value) { Filter = ObjectFilter.GridFs; } } }

    /// <summary>右上角的「搜索对象」。</summary>
    public string SearchText
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                Rebuild();
            }
        }
    } = "";

    /// <summary>视图:网格大图标 / 列表 / 详情。</summary>
    public ObjectViewMode ViewMode
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsGridMode), nameof(IsListMode), nameof(IsDetailsMode));
            }
        }
    } = ObjectViewMode.Details;

    /// <summary>网格大图标。</summary>
    public bool IsGridMode { get => ViewMode == ObjectViewMode.Grid; set { if (value) { ViewMode = ObjectViewMode.Grid; } } }

    /// <summary>列表。</summary>
    public bool IsListMode { get => ViewMode == ObjectViewMode.List; set { if (value) { ViewMode = ObjectViewMode.List; } } }

    /// <summary>详情。</summary>
    public bool IsDetailsMode { get => ViewMode == ObjectViewMode.Details; set { if (value) { ViewMode = ObjectViewMode.Details; } } }

    /// <summary>
    /// 排序列:<c>Name</c> / <c>Type</c> / <c>Count</c> / <c>AvgSize</c> / <c>DataSize</c> / <c>StorageSize</c> /
    /// <c>Indexes</c> / <c>IndexSize</c> / <c>Validation</c>。默认按数据大小倒序(设计稿 12 列头上那枚 ↓)。
    /// </summary>
    public string SortColumn
    {
        get;
        private set => SetProperty(ref field, value);
    } = "DataSize";

    /// <summary>倒序。</summary>
    public bool SortDescending
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(SortIconKey));
            }
        }
    } = true;

    /// <summary>列头上那枚箭头。</summary>
    public string SortIconKey => SortDescending ? "Mongo.arrow-down" : "Mongo.arrow-up";

    // ── 列表 ─────────────────────────────────────────────────────────────

    /// <summary>过滤、搜索、排序之后的行。</summary>
    public ObservableCollection<ObjectItem> Items { get; } = [];

    /// <summary>芯片上的计数:全部。</summary>
    public int AllCount => _all.Count;

    /// <summary>芯片上的计数:集合(含时序、固定、聚簇 —— 与对象树的「集合」分组同口径)。</summary>
    public int CollectionCount => _all.Count(static i => i.IsCollection);

    /// <summary>芯片上的计数:视图。</summary>
    public int ViewCount => _all.Count(static i => i.Kind == ObjectKind.View);

    /// <summary>芯片上的计数:时序。</summary>
    public int TimeSeriesCount => _all.Count(static i => i.Kind == ObjectKind.TimeSeries);

    /// <summary>芯片上的计数:GridFS 桶。</summary>
    public int GridFsCount => _all.Count(static i => i.Kind == ObjectKind.Bucket);

    /// <summary>正在列集合。</summary>
    public bool IsLoading
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ShowEmpty), nameof(EmptyText));
            }
        }
    }

    /// <summary>列集合失败的原因(列表区域显示它而不是一张空表)。</summary>
    public string ErrorText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ShowEmpty), nameof(EmptyText));
            }
        }
    } = "";

    /// <summary>列表区域显示空态。</summary>
    public bool ShowEmpty => Items.Count == 0;

    /// <summary>空态文字:加载中 / 失败原因 / 搜索无结果 / 空库。</summary>
    public string EmptyText =>
        IsLoading ? Loc["Common_Loading"]
        : ErrorText.Length > 0 ? ErrorText
        : _all.Count > 0 ? Loc["Obj_NoMatch"]
        : Loc.Format("Obj_EmptyDatabase", Database);

    /// <summary>选中的对象。</summary>
    public ObjectItem? SelectedItem
    {
        get;
        set
        {
            // 重建列表时 ListBox 会把选中项推回 null —— 那不是用户的意图,忽略。
            if (_rebuilding && value is null)
            {
                return;
            }
            if (ReferenceEquals(field, value))
            {
                return;
            }
            field?.IsSelected = false;
            field = value;
            value?.IsSelected = true;
            RaisePropertyChanged();
            RaisePropertiesChanged(nameof(HasSelection), nameof(DetailIconKey), nameof(DetailIconToken), nameof(DetailTitle),
                nameof(DetailSubtitle), nameof(OpenLabel), nameof(CanDesign), nameof(CanPipeline), nameof(PrivilegeActionsTitle));
            RaiseCommandStates();
            UpdateStatus();
            _ = LoadDetailAsync(value);
        }
    }

    /// <summary>有选中项(详情面板显示内容,否则显示提示)。</summary>
    public bool HasSelection => SelectedItem is not null;

    // ── 命令 ─────────────────────────────────────────────────────────────

    /// <summary>打开集合 / 视图 / 桶。</summary>
    public RelayCommand OpenCommand { get; }

    /// <summary>设计集合。</summary>
    public RelayCommand DesignCommand { get; }

    /// <summary>在管道构建器中打开。</summary>
    public RelayCommand PipelineCommand { get; }

    /// <summary>新建查询(右键菜单)。</summary>
    public RelayCommand QueryCommand { get; }

    /// <summary>新建集合。</summary>
    public RelayCommand NewCollectionCommand { get; }

    /// <summary>删除集合 / 视图 / 桶。</summary>
    public AsyncCommand DropCommand { get; }

    /// <summary>清空集合。</summary>
    public AsyncCommand EmptyCommand { get; }

    /// <summary>复制结构(mongosh 脚本)。</summary>
    public AsyncCommand CopyStructureCommand { get; }

    /// <summary>复制名称。</summary>
    public AsyncCommand CopyNameCommand { get; }

    /// <summary>导入。</summary>
    public RelayCommand ImportCommand { get; }

    /// <summary>导出。</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>点列头排序。</summary>
    public RelayCommand<string> SortCommand { get; }

    /// <summary>详情面板 DDL 页的「复制」。</summary>
    public AsyncCommand CopyDdlCommand { get; }

    private void RaiseCommandStates()
    {
        OpenCommand.RaiseCanExecuteChanged();
        DesignCommand.RaiseCanExecuteChanged();
        PipelineCommand.RaiseCanExecuteChanged();
        DropCommand.RaiseCanExecuteChanged();
        EmptyCommand.RaiseCanExecuteChanged();
        CopyStructureCommand.RaiseCanExecuteChanged();
        CopyNameCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
    }

    // ── 加载 ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override Task LoadAsync() => ReloadAsync();

    /// <inheritdoc />
    public override Task RefreshAsync() => ReloadAsync();

    /// <inheritdoc />
    protected override void OnActivated()
    {
        // 切回来时数据可能已经过时(别的标签里建删了集合);半分钟内的不重拉,免得来回切标签时闪。
        if (_loaded && !IsLoading && DateTime.UtcNow - _loadedAt > TimeSpan.FromSeconds(30))
        {
            _ = ReloadAsync();
        }
        UpdateStatus();
    }

    private void OnObjectsChanged(IMongoWorkspace workspace, string database)
    {
        if (ReferenceEquals(workspace, Workspace) && database == Database)
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>重新列集合并补统计(保留选中项与过滤条件)。</summary>
    public async Task ReloadAsync()
    {
        // 只取消不释放:上一轮的统计请求还挂在旧令牌上(见 EnsureStatsAsync)。
        _load?.Cancel();
        var cts = new CancellationTokenSource();
        _load = cts;
        IsLoading = true;
        ErrorText = "";
        IReadOnlyList<CollectionInfo> list;
        try
        {
            list = await Workspace.Connection.ListCollectionsAsync(Database, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            ErrorText = Loc.Format("Common_Failed", MongoConnector.Describe(ex));
            IsLoading = false;
            return;
        }
        if (cts.IsCancellationRequested)
        {
            return;
        }

        ObjectItem? keep = SelectedItem;
        // 按(种类, 名字)对号:桶 fs 与一个恰好也叫 fs 的集合可以同时存在。
        var previous = new Dictionary<(ObjectKind, string), ObjectItem>();
        foreach (ObjectItem old in _all)
        {
            _ = previous.TryAdd((old.Kind, old.Name), old);
        }
        List<ObjectItem> fresh = BuildItems(Database, list, Loc);
        foreach (ObjectItem item in fresh)
        {
            if (previous.TryGetValue((item.Kind, item.Name), out ObjectItem? old))
            {
                item.CarryOver(old);
            }
        }
        _all.Clear();
        _all.AddRange(fresh);
        _statsTasks.Clear();
        _loaded = true;
        _loadedAt = DateTime.UtcNow;
        RaisePropertiesChanged(nameof(AllCount), nameof(CollectionCount), nameof(ViewCount), nameof(TimeSeriesCount), nameof(GridFsCount));
        _rebuilding = true;
        try
        {
            Rebuild();
        }
        finally
        {
            _rebuilding = false;
        }
        IsLoading = false;
        SelectedItem = keep is null ? null : Items.FirstOrDefault(i => i.Name == keep.Name && i.Kind == keep.Kind);
        UpdateStatus();
        await FillStatsAsync(cts.Token).ConfigureAwait(true);
    }

    /// <summary>
    /// <c>listCollections</c> → 对象行:系统集合不列(<c>system.views</c> / <c>system.buckets.*</c> 是实现细节),
    /// GridFS 的 files / chunks 合成一个桶。
    /// </summary>
    internal static List<ObjectItem> BuildItems(string database, IReadOnlyList<CollectionInfo> list, Loc loc)
    {
        IReadOnlyList<GridFsBucketInfo> buckets = MongoConnection.FindBuckets(database, list);
        HashSet<string> bucketCollections = [.. buckets.SelectMany(static b => new[] { b.FilesCollection, b.ChunksCollection })];
        List<ObjectItem> items =
        [
            .. list.Where(c => c.Kind != CollectionKind.System && !bucketCollections.Contains(c.Name)).Select(c => new ObjectItem(c, loc)),
            .. buckets.Select(b => new ObjectItem(b, loc))
        ];
        return items;
    }

    /// <summary>并发取每一行的统计(视图没有统计)。全部到齐后按数字列重排一次 —— 边到边排会让行在眼前乱跳。</summary>
    private async Task FillStatsAsync(CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(6);
        List<ObjectItem> targets = [.. _all.Where(static i => !i.IsView)];
        try
        {
            await Task.WhenAll(targets.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await EnsureStatsAsync(item).ConfigureAwait(false);
                }
                finally
                {
                    _ = gate.Release();
                }
            })).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!cancellationToken.IsCancellationRequested && SortColumn is not ("Name" or "Type" or "Validation"))
        {
            RebuildKeepingSelection();
        }
    }

    /// <summary>
    /// 某一行的统计(同一行只取一次;详情面板与列表共用这一份)。挂在**列表加载**的令牌上而不是调用方的:
    /// 详情面板切走时取消的只是它自己的等待,统计照样取完 —— 否则那一行就永远是空白了。
    /// </summary>
    internal Task EnsureStatsAsync(ObjectItem item)
    {
        CancellationToken token = _load?.Token ?? CancellationToken.None;
        return _statsTasks.GetOrAdd(item, i => LoadStatsAsync(i, token));
    }

    private async Task LoadStatsAsync(ObjectItem item, CancellationToken cancellationToken)
    {
        MongoConnection connection = Workspace.Connection;
        try
        {
            switch (item.Kind)
            {
                case ObjectKind.View:
                    return;
                case ObjectKind.Bucket:
                    {
                        GridFsBucketInfo bucket = item.Bucket!;
                        CollectionStats files = await connection.GetStatsAsync(Database, bucket.FilesCollection, cancellationToken).ConfigureAwait(false);
                        CollectionStats chunks = await connection.GetStatsAsync(Database, bucket.ChunksCollection, cancellationToken).ConfigureAwait(false);
                        item.SetBucketStats(files, chunks);
                        return;
                    }
                default:
                    {
                        CollectionStats stats = await connection.GetStatsAsync(Database, item.Name, cancellationToken).ConfigureAwait(false);
                        long? estimated = item.Kind == ObjectKind.TimeSeries && stats.Count == 0
                            ? await connection.EstimatedCountAsync(Database, item.Name, cancellationToken).ConfigureAwait(false)
                            : null;
                        item.SetStats(stats, estimated);
                        if (item.Kind == ObjectKind.Collection)
                        {
                            item.Indexes = await connection.ListIndexesAsync(Database, item.Name, cancellationToken).ConfigureAwait(false);
                            item.IsTtl = MongoConnection.HasTtl(item.Indexes);
                        }
                        return;
                    }
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            // 某一行没权限读统计:那一行留空,不影响其余行,也不值得弹提示。
        }
    }

    // ── 过滤与排序 ───────────────────────────────────────────────────────

    private bool Matches(ObjectItem item) =>
        _filter switch
        {
            ObjectFilter.Collections => item.IsCollection,
            ObjectFilter.Views => item.Kind == ObjectKind.View,
            ObjectFilter.TimeSeries => item.Kind == ObjectKind.TimeSeries,
            ObjectFilter.GridFs => item.Kind == ObjectKind.Bucket,
            _ => true
        }
        && (SearchText.Trim().Length == 0 || item.DisplayName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>按过滤、搜索与排序重算可见行。</summary>
    private void Rebuild()
    {
        IEnumerable<ObjectItem> rows = _all.Where(Matches);
        // 视图与还没取到统计的行在数字列排序里一律沉底,不管正序倒序。
        IComparer<IComparable> comparer = Comparer<IComparable>.Default;
        string column = SortColumn;
        List<ObjectItem> sorted = SortDescending
            ? [.. rows.OrderBy(i => IsBlank(i, column)).ThenByDescending(i => i.SortKey(column), comparer).ThenBy(static i => i.DisplayName, StringComparer.Ordinal)]
            : [.. rows.OrderBy(i => IsBlank(i, column)).ThenBy(i => i.SortKey(column), comparer).ThenBy(static i => i.DisplayName, StringComparer.Ordinal)];
        if (sorted.SequenceEqual(Items))
        {
            return;
        }
        ObjectItem? selected = SelectedItem;
        bool wasRebuilding = _rebuilding;
        _rebuilding = true;
        try
        {
            Items.Clear();
            foreach (ObjectItem row in sorted)
            {
                Items.Add(row);
            }
        }
        finally
        {
            _rebuilding = wasRebuilding;
        }
        RaisePropertiesChanged(nameof(ShowEmpty), nameof(EmptyText));
        if (selected is not null && !sorted.Contains(selected))
        {
            SelectedItem = null;
        }
        else
        {
            // ListBox 在清空时丢了选中项;把它推回去。
            RaisePropertyChanged(nameof(SelectedItem));
        }
        UpdateStatus();
    }

    private void RebuildKeepingSelection()
    {
        _rebuilding = true;
        try
        {
            Rebuild();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private static bool IsBlank(ObjectItem item, string column) =>
        column is not ("Name" or "Type" or "Validation") && (item.IsView || !item.HasStats);

    private void SortBy(string column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            // 名字与类型默认正序,数字列默认倒序(最大的在最上面才是看这张表的目的)。
            SortDescending = column is not ("Name" or "Type" or "Validation");
        }
        RebuildKeepingSelection();
    }

    // ── 状态行 ───────────────────────────────────────────────────────────

    private void UpdateStatus() =>
        StatusText = SelectedItem is { } s
            ? Loc.Format("Obj_StatusSelected", s.DisplayName, s.KindName, Items.Count)
            : Loc.Format("Obj_StatusCount", Items.Count);

    /// <inheritdoc />
    public override void Dispose()
    {
        ObjectsChanged.Changed -= OnObjectsChanged;
        _load?.Cancel();
        _detail?.Cancel();
        base.Dispose();
    }
}

/// <summary>
/// "这个库里的对象变了"的广播:新建集合对话框建完之后,同一工作台里开着的对象列表据此重拉。
/// 对话框与标签页之间不互相引用(见 <see cref="IMongoWorkspace" /> 的约定),所以走一个事件。
/// </summary>
internal static class ObjectsChanged
{
    /// <summary>某个工作台的某个库里建删了对象。</summary>
    public static event Action<IMongoWorkspace, string>? Changed;

    /// <summary>广播一次。</summary>
    public static void Raise(IMongoWorkspace workspace, string database) => Changed?.Invoke(workspace, database);
}
