using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Staging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合工作台(设计稿 01 网格 / 02 JSON / 13 树)。
/// <para>
/// 三种视图看的是**同一页文档**与**同一个暂存区**:在网格里改一格、在树里改一个嵌套字段、
/// 在 JSON 卡片里改一段,都落到 <see cref="Staging" />,底栏的「待提交」与标签上的橙点只认它。
/// 切视图不重查,只是换一种画法。
/// </para>
/// <para>分部文件:<c>.Query</c> 查询栏与分页,<c>.Edit</c> 暂存与提交,<c>.Views</c> 树 / JSON / 检查器的联动。</para>
/// </summary>
internal sealed partial class CollectionTabViewModel : WorkspaceTab
{
    private List<BsonDocument> _documents = [];
    private CollectionRow? _selectedRow;
    private IReadOnlyList<CollectionRow> _selectedRows = [];
    private CollectionColumn? _currentColumn;
    private EjsonMode _ejson;
    private bool _loadedOnce;
    private Task? _sampleTask;
    private bool _disposed;

    /// <summary>构造(外壳在用的签名,保持不变)。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="info">集合信息(对象树没加载到它时是一份占位,<see cref="LoadAsync" /> 里重探)。</param>
    /// <param name="filter">打开时带的筛选(对象列表「按此值筛选」之类)。</param>
    public CollectionTabViewModel(IMongoWorkspace workspace, CollectionInfo info, string? filter)
        : base(workspace)
    {
        Info = info;
        Title = info.Name;
        Scope = "@" + info.Database;
        _ejson = workspace.Connection.Settings.Ejson;
        _filterText = filter ?? "";
        _limitText = workspace.Connection.Settings.PageSize.ToString(CultureInfo.InvariantCulture);
        Staging.Changed += OnStagingChanged;
        workspace.Guard.Changed += OnGuardChanged;
        Inspector = new DocInspectorViewModel(this);
        InitializeQueryCommands();
        InitializeEditCommands();
        ValidateFilter();
    }

    /// <summary>集合信息。</summary>
    public CollectionInfo Info { get; private set; }

    /// <summary>库名。</summary>
    public string Database => Info.Database;

    /// <summary>集合名。</summary>
    public string CollectionName => Info.Name;

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Collection;

    /// <inheritdoc />
    public override string Key => $"collection:{Info.Namespace}";

    /// <inheritdoc />
    public override string IconKey => Info.Kind == CollectionKind.View ? "Mongo.eye" : "Mongo.table-2";

    /// <inheritdoc />
    public override string IconToken => "VelaInfo";

    /// <summary>暂存区。</summary>
    public StagingArea Staging { get; } = new();

    /// <summary>右侧检查器。</summary>
    public DocInspectorViewModel Inspector { get; }

    /// <summary>驱动集合。</summary>
    internal IMongoCollection<BsonDocument> Collection => Workspace.Connection.Collection(Database, CollectionName);

    // ── 视图 ───────────────────────────────────────────────────────────────

    /// <summary>当前视图。</summary>
    public CollectionViewMode ViewMode
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsGridMode), nameof(IsTreeMode), nameof(IsJsonMode), nameof(PendingText));
                if (value == CollectionViewMode.Tree)
                {
                    RebuildTree();
                }
                else if (value == CollectionViewMode.Json)
                {
                    RebuildCards();
                }
            }
        }
    } = CollectionViewMode.Grid;

    /// <summary>上一次右侧面板是开着还是收着:新开的集合标签照这个来(Navicat 也记着)。</summary>
    private static bool s_sidePanelVisible = true;

    /// <summary>
    /// 数据区右侧的面板(网格的文档检查器、JSON 视图的大纲)开着吗。底栏右下角那颗按钮切换它;
    /// 收起之后数据区占满整宽,展开时回到收起前拖到的宽度。
    /// </summary>
    public bool IsSidePanelVisible
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                s_sidePanelVisible = value;
                RaisePropertyChanged(nameof(SidePanelTip));
            }
        }
    } = s_sidePanelVisible;

    /// <summary>按钮的提示:「隐藏右侧面板」/「显示右侧面板」。</summary>
    public string SidePanelTip => Loc[IsSidePanelVisible ? "Cw_HideSidePanel" : "Cw_ShowSidePanel"];

    /// <summary>收起 / 展开右侧面板。</summary>
    public RelayCommand ToggleSidePanelCommand => field ??= new RelayCommand(() => IsSidePanelVisible = !IsSidePanelVisible);

    /// <summary>网格视图(分段按钮双向绑定)。</summary>
    public bool IsGridMode
    {
        get => ViewMode == CollectionViewMode.Grid;
        set
        {
            if (value)
            {
                ViewMode = CollectionViewMode.Grid;
            }
        }
    }

    /// <summary>树视图。</summary>
    public bool IsTreeMode
    {
        get => ViewMode == CollectionViewMode.Tree;
        set
        {
            if (value)
            {
                ViewMode = CollectionViewMode.Tree;
            }
        }
    }

    /// <summary>JSON 视图。</summary>
    public bool IsJsonMode
    {
        get => ViewMode == CollectionViewMode.Json;
        set
        {
            if (value)
            {
                ViewMode = CollectionViewMode.Json;
            }
        }
    }

    /// <summary>扩展 JSON 写法(JSON 视图大纲里的「显示」分段、复制为 JSON)。</summary>
    public EjsonMode Ejson
    {
        get => _ejson;
        set
        {
            if (SetProperty(ref _ejson, value))
            {
                RaisePropertiesChanged(nameof(IsShellMode), nameof(IsRelaxedMode), nameof(IsCanonicalMode));
                foreach (JsonCardViewModel card in Cards)
                {
                    card.Refresh();
                }
                Inspector.RefreshJson();
            }
        }
    }

    /// <summary>Shell 写法。</summary>
    public bool IsShellMode
    {
        get => _ejson == EjsonMode.Shell;
        set
        {
            if (value)
            {
                Ejson = EjsonMode.Shell;
            }
        }
    }

    /// <summary>Relaxed 写法。</summary>
    public bool IsRelaxedMode
    {
        get => _ejson == EjsonMode.Relaxed;
        set
        {
            if (value)
            {
                Ejson = EjsonMode.Relaxed;
            }
        }
    }

    /// <summary>Canonical 写法。</summary>
    public bool IsCanonicalMode
    {
        get => _ejson == EjsonMode.Canonical;
        set
        {
            if (value)
            {
                Ejson = EjsonMode.Canonical;
            }
        }
    }

    // ── 网格数据 ─────────────────────────────────────────────────────────────

    /// <summary>列。</summary>
    public IReadOnlyList<CollectionColumn> Columns
    {
        get; private set
        {
            if (SetProperty(ref field, value) && _drill is null)
            {
                RaisePropertyChanged(nameof(GridColumns));
            }
        }
    } = [];

    /// <summary>行(本页文档 + 暂存的新文档)。</summary>
    public ObservableCollection<CollectionRow> Rows
    {
        get; private set
        {
            if (SetProperty(ref field, value) && _drill is null)
            {
                RaisePropertyChanged(nameof(GridRows));
            }
        }
    } = [];

    /// <summary>本页从服务器读到的文档。</summary>
    public IReadOnlyList<BsonDocument> Documents => _documents;

    /// <summary>选中的行(焦点行;检查器看它)。</summary>
    public CollectionRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            CollectionRow? previous = _selectedRow;
            if (!SetProperty(ref _selectedRow, value))
            {
                return;
            }
            if (_drill is null)
            {
                UpdateCurrentCell(previous, value);
                RaisePropertyChanged(nameof(GridSelectedRow));
            }
            Inspector.Load(value);
            SyncCardSelection();
            RaisePropertiesChanged(nameof(HasSelection));
        }
    }

    /// <summary>多选的全部行(工具栏删除 / 复制为 JSON 作用于它们)。</summary>
    public IReadOnlyList<CollectionRow> SelectedRows
    {
        get => _selectedRows.Count > 0 ? _selectedRows : _selectedRow is { } one ? [one] : [];
        set
        {
            _selectedRows = value;
            RaisePropertyChanged(nameof(HasSelection));
        }
    }

    /// <summary>有没有选中。</summary>
    public bool HasSelection => _selectedRow is not null;

    /// <summary>当前列(键盘编辑落在哪一格)。</summary>
    public CollectionColumn? CurrentColumn
    {
        get => _currentColumn;
        set
        {
            if (ReferenceEquals(_currentColumn, value))
            {
                return;
            }
            _currentColumn = value;
            RaisePropertyChanged();
            if (GridSelectedRow?.Cells is { } cells)
            {
                foreach (CollectionCell cell in cells)
                {
                    cell.IsCurrent = ReferenceEquals(cell.Column, value);
                }
            }
        }
    }

    /// <summary>是不是网格的焦点行(钻入时是子表里选中的那一行)。</summary>
    public bool IsCurrentRow(CollectionRow row) => ReferenceEquals(row, GridSelectedRow);

    /// <summary>补全用的抽样(没抽到时为空样本)。</summary>
    public CollectionSample Sample { get; private set; } = CollectionSample.Empty;

    /// <summary>集合统计(检查器「集合信息」页、对象树底部之外的另一个入口)。</summary>
    public CollectionStats? Stats { get; private set => SetProperty(ref field, value); }

    // ── 加载状态 ─────────────────────────────────────────────────────────────

    /// <summary>正在查询。</summary>
    public bool IsLoading
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsEmptyCollection), nameof(IsNoResult), nameof(ShowData));
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>本页往返耗时。</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>执行计划摘要(<c>IXSCAN status_1_createdAt_-1</c>)。</summary>
    public string PlanSummary
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                UpdateStatus();
            }
        }
    } = "";

    /// <summary>空集合(没有筛选、一份文档都没有):设计稿 22 的「空集合」卡。</summary>
    public bool IsEmptyCollection => _loadedOnce && !IsLoading && _documents.Count == 0 && Staging.InsertCount == 0
                                     && !HasActiveFilter && _pageIndex == 0;

    /// <summary>筛选无结果。</summary>
    public bool IsNoResult => _loadedOnce && !IsLoading && _documents.Count == 0 && Staging.InsertCount == 0
                              && (HasActiveFilter || _pageIndex > 0);

    /// <summary>显示数据区(不是空态)。</summary>
    public bool ShowData => !IsEmptyCollection && !IsNoResult;

    /// <summary>空集合卡片的标题(<c>orders_archive 中还没有文档</c>)。</summary>
    public string EmptyTitle => Loc.Format("State_EmptyTitle", CollectionName);

    // ── 可写性 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 能不能写:视图一律不能;只读模式下工具栏的写按钮置灰(横幅里写明了),
    /// 但双击编辑之类的手势仍会走到 <see cref="IMongoWorkspace.EnsureWritable" />,由它给出"怎么解锁"的提示。
    /// </summary>
    public bool CanEdit => Info.IsEditable && !Workspace.Guard.IsReadOnly && Workspace.Guard.Privileges.CanWrite(Database);

    /// <summary>集合本身可写(视图、系统集合不可)。</summary>
    public bool IsEditable => Info.IsEditable;

    /// <summary>只读横幅。</summary>
    public bool ShowReadOnlyBanner => Workspace.Guard.IsReadOnly;

    /// <summary>视图横幅(视图本来就不能改,与只读模式是两回事)。</summary>
    public bool ShowViewBanner => Info.Kind == CollectionKind.View && !Workspace.Guard.IsReadOnly;

    /// <summary>只读横幅的主文字(生产连接带上「· 生产连接」)。</summary>
    public string ReadOnlyTitle => Workspace.Guard.IsProduction ? Loc["State_ReadOnlyProd"] : Loc["State_ReadOnly"];

    /// <summary>视图横幅文字。</summary>
    public string ViewBannerText => Loc.Format("Cw_ViewBanner", Info.ViewOn ?? "?");

    // ── 加载 ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task LoadAsync()
    {
        await RefreshInfoAsync().ConfigureAwait(true);
        _ = LoadIndexesAsync();
        _ = LoadHistoryAsync();
        await RunQueryAsync(resetPage: true).ConfigureAwait(true);
        EnsureSample();
    }

    /// <inheritdoc />
    public override Task RefreshAsync() => RunQueryAsync(resetPage: false);

    /// <summary>
    /// 重探集合信息:外壳在对象树还没展开这个库时给的是一份"普通集合"占位 ——
    /// 是不是视图、有没有验证规则,都得问一次服务器才知道。
    /// </summary>
    private async Task RefreshInfoAsync()
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await Workspace.Connection.Database(Database)
                .ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionName) })
                .ConfigureAwait(true);
            BsonDocument? raw = await cursor.FirstOrDefaultAsync().ConfigureAwait(true);
            if (raw is not null)
            {
                Info = MongoConnection.ToCollectionInfo(Database, raw);
                RaisePropertiesChanged(nameof(Info), nameof(IconKey), nameof(CanEdit), nameof(IsEditable), nameof(ShowViewBanner),
                    nameof(ViewBannerText));
                Inspector.RefreshValidation();
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"Probing {Info.Namespace} failed: {ex.Message}");
        }
    }

    /// <summary>后台抽一次样(补全、网格下拉候选用);失败就用空样本,不打扰用户。</summary>
    internal void EnsureSample()
    {
        _sampleTask ??= LoadSampleAsync();

        async Task LoadSampleAsync()
        {
            try
            {
                Sample = await CollectionSample.LoadAsync(Workspace.Connection, Database, CollectionName,
                    Workspace.Connection.Settings.SampleSize, _lifetime.Token).ConfigureAwait(true);
                RaisePropertyChanged(nameof(Sample));
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
            {
                Workspace.Log.Info($"Sampling {Info.Namespace} failed: {ex.Message}");
            }
        }
    }

    /// <summary>统计(检查器「集合信息」页第一次打开时取)。</summary>
    internal async Task LoadStatsAsync()
    {
        if (Stats is not null)
        {
            return;
        }
        try
        {
            Stats = await Workspace.Connection.GetStatsAsync(Database, CollectionName, _lifetime.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            Workspace.Log.Info($"Stats for {Info.Namespace} failed: {ex.Message}");
        }
    }

    /// <summary>把一页结果铺进网格(并按需重建树 / 卡片)。</summary>
    private void ApplyResults(List<BsonDocument> documents, int firstNumber)
    {
        BsonValue? keepId = _selectedRow?.Id;
        int? keepInsert = _selectedRow?.Insert?.Key;
        _documents = documents;
        Columns = BuildColumns(documents, Staging.Inserts.Select(static i => i.Document));
        var rows = new List<CollectionRow>(documents.Count + Staging.InsertCount);
        for (int i = 0; i < documents.Count; i++)
        {
            rows.Add(new CollectionRow(this, firstNumber + i, documents[i]));
        }
        foreach (StagedInsert insert in Staging.Inserts)
        {
            rows.Add(new CollectionRow(this, 0, insert));
        }
        _selectedRows = [];
        _selectedRow = null;
        Rows = [with(rows)];
        _trackedIds = [.. Staging.Edits.Select(static e => e.Id).Concat(Staging.Deletes.Select(static d => d.Id))];
        CollectionRow? select = rows.FirstOrDefault(r => keepId is not null && keepId.Equals(r.Id))
                          ?? rows.FirstOrDefault(r => keepInsert is not null && r.Insert?.Key == keepInsert)
                          ?? rows.FirstOrDefault();
        _currentColumn ??= Columns.FirstOrDefault(c => c.Name != "_id") ?? Columns.FirstOrDefault();
        if (_currentColumn is not null && Columns.FirstOrDefault(c => c.Name == _currentColumn.Name) is { } same)
        {
            _currentColumn = same;
        }
        SelectedRow = select;
        RaisePropertyChanged(nameof(SelectedRow));
        RaisePropertyChanged(nameof(GridSelectedRow));
        RaisePropertiesChanged(nameof(Documents), nameof(IsEmptyCollection), nameof(IsNoResult), nameof(ShowData));
        ReattachDrill();
        RebuildTree();
        RebuildCards();
    }

    /// <summary>
    /// 列:本页文档顶层字段的并集,按首次出现顺序(<c>_id</c> 在前);宽度按类型与内容估,
    /// 用户拖过的宽度在翻页 / 重查之间保留(同名列沿用旧宽度)。
    /// </summary>
    private IReadOnlyList<CollectionColumn> BuildColumns(IReadOnlyList<BsonDocument> documents, IEnumerable<BsonDocument> extra)
    {
        var order = new List<string>();
        var kinds = new Dictionary<string, Dictionary<BsonKind, int>>(StringComparer.Ordinal);
        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        int seen = 0;
        foreach (BsonDocument doc in documents.Concat(extra))
        {
            // 宽度只看前 200 份就够估了;5 万行的页里逐格算显示文本是白花几百毫秒。
            bool measure = seen++ < 200;
            foreach (BsonElement element in doc)
            {
                if (!kinds.TryGetValue(element.Name, out Dictionary<BsonKind, int>? counts))
                {
                    counts = [];
                    kinds[element.Name] = counts;
                    order.Add(element.Name);
                }
                BsonKind kind = BsonKinds.Of(element.Value);
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                if (measure && lengths.GetValueOrDefault(element.Name) < 40)
                {
                    lengths[element.Name] = Math.Max(lengths.GetValueOrDefault(element.Name), BsonText.Cell(element.Value).Length);
                }
            }
        }
        if (order.Remove("_id"))
        {
            order.Insert(0, "_id");
        }
        var previous = Columns.ToDictionary(static c => c.Name, StringComparer.Ordinal);
        BsonDocument sort = ParsedSort ?? [];
        var columns = new List<CollectionColumn>(order.Count);
        foreach (string name in order)
        {
            BsonKind kind = Dominant(kinds[name]);
            double width = previous.TryGetValue(name, out CollectionColumn? old) ? old.Width : EstimateWidth(name, kind, lengths.GetValueOrDefault(name));
            var column = new CollectionColumn(name, kind, width);
            if (sort.TryGetValue(name, out BsonValue direction) && direction.IsNumeric)
            {
                column.SortDirection = direction.ToInt32() < 0 ? -1 : 1;
            }
            columns.Add(column);
        }
        return columns;
    }

    /// <summary>一列的主导类型:出现最多的那种,null 与缺失不计;全是 null 时就是 null。</summary>
    internal static BsonKind Dominant(Dictionary<BsonKind, int> counts) =>
        counts.Where(static k => k.Key is not (BsonKind.Null or BsonKind.Missing))
            .OrderByDescending(static k => k.Value).Select(static k => k.Key).DefaultIfEmpty(BsonKind.Null).First();

    /// <summary>估列宽:类型给底(ObjectId 180、日期 156、布尔 60),字符串按内容长度,夹在 [60, 280]。</summary>
    internal static double EstimateWidth(string name, BsonKind kind, int contentLength)
    {
        double header = (name.Length + BsonKinds.Name(kind).Length) * 6.6 + 30;
        double content = kind switch
        {
            BsonKind.ObjectId => 180,
            BsonKind.Date => 156,
            BsonKind.Boolean => 60,
            BsonKind.Object => 148,
            BsonKind.Array => 118,
            BsonKind.Uuid => 270,
            _ when BsonKinds.IsNumeric(kind) => Math.Max(84, contentLength * 7 + 18),
            _ => contentLength * 7 + 18
        };
        return Math.Round(Math.Clamp(Math.Max(header, content), 60, 280));
    }

    private void UpdateCurrentCell(CollectionRow? previous, CollectionRow? next)
    {
        if (previous?.Cells is { } old)
        {
            foreach (CollectionCell cell in old)
            {
                cell.IsCurrent = false;
                cell.Editor = null;
            }
        }
        if (next is not null && _currentColumn is not null)
        {
            foreach (CollectionCell cell in next.Cells)
            {
                cell.IsCurrent = ReferenceEquals(cell.Column, _currentColumn);
            }
        }
    }

    // ── 状态行 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 宿主状态栏那一行:<c>shop.orders · 50 行 · 12 ms · IXSCAN status_1_createdAt_-1 · 3 项待提交</c>。
    /// </summary>
    private void UpdateStatus()
    {
        string text = Loc.Format("Cw_Status", Info.Namespace, BsonText.Grouped(_documents.Count),
            ((int)Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        if (PlanSummary.Length > 0)
        {
            text += " · " + PlanSummary;
        }
        if (!Staging.IsEmpty)
        {
            text += " · " + Loc.Format("Cw_StatusPending", Staging.OperationCount);
        }
        StatusText = text;
    }

    /// <summary>从执行计划里挑出获胜阶段的摘要(<c>IXSCAN 索引名</c> / <c>COLLSCAN</c> / <c>IDHACK</c>)。</summary>
    internal static string SummarizePlan(BsonDocument explain)
    {
        var planner = explain.GetValue("queryPlanner", BsonNull.Value) as BsonDocument;
        if (planner is null && explain.GetValue("stages", BsonNull.Value) is BsonArray stages
            && stages.FirstOrDefault() is BsonDocument first
            && first.GetValue("$cursor", BsonNull.Value) is BsonDocument cursor)
        {
            planner = cursor.GetValue("queryPlanner", BsonNull.Value) as BsonDocument;
        }
        if (planner?.GetValue("winningPlan", BsonNull.Value) is not BsonDocument winning)
        {
            return "";
        }
        if (winning.GetValue("queryPlan", BsonNull.Value) is BsonDocument sbe)
        {
            winning = sbe;
        }
        string[] interesting = ["IXSCAN", "EXPRESS_IXSCAN", "COLLSCAN", "IDHACK", "EXPRESS_IDHACK", "COUNT_SCAN", "DISTINCT_SCAN", "TEXT_MATCH", "GEO_NEAR_2DSPHERE", "EOF"];
        var queue = new Queue<BsonDocument>([winning]);
        while (queue.Count > 0)
        {
            BsonDocument stage = queue.Dequeue();
            string name = stage.GetValue("stage", "").AsString;
            if (interesting.Contains(name))
            {
                return stage.GetValue("indexName", BsonNull.Value) is BsonString index ? $"{name} {index.Value}" : name;
            }
            if (stage.GetValue("inputStage", BsonNull.Value) is BsonDocument input)
            {
                queue.Enqueue(input);
            }
            if (stage.GetValue("inputStages", BsonNull.Value) is BsonArray inputs)
            {
                foreach (BsonDocument item in inputs.OfType<BsonDocument>())
                {
                    queue.Enqueue(item);
                }
            }
        }
        return winning.GetValue("stage", "").AsString;
    }

    // ── 生命周期 ─────────────────────────────────────────────────────────────

    private readonly CancellationTokenSource _lifetime = new();

    private void OnGuardChanged() => Dispatcher.UIThread.Post(() =>
    {
        RaisePropertiesChanged(nameof(CanEdit), nameof(ShowReadOnlyBanner), nameof(ShowViewBanner), nameof(ReadOnlyTitle));
        if (!Workspace.Guard.IsReadOnly)
        {
            return;
        }
        _relockTimer?.Stop();
        _relockTimer = null;
    });

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Workspace.Guard.Changed -= OnGuardChanged;
        Staging.Changed -= OnStagingChanged;
        _queryCts?.Cancel();
        _longQueryTimer?.Stop();
        _relockTimer?.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.Dispose();
    }

    /// <summary>界面上的毫秒数。</summary>
    internal static string Ms(TimeSpan elapsed) => ((int)Math.Round(elapsed.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>计时开始(供查询与提交共用)。</summary>
    internal static Stopwatch StartWatch() => Stopwatch.StartNew();
}
