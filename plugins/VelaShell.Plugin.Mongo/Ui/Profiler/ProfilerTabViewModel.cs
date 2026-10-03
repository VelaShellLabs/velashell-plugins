using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 慢查询与当前操作(设计稿 15):profiler 级别 / slowms / sampleRate、<c>$currentOp</c> 实时列表(可 killOp)、
/// 近 24 小时 <c>system.profile</c> 按查询形状聚合,以及选中形状的样本、计划与建议索引。
/// <para>
/// <c>$currentOp</c> 每 2 秒刷新一次、慢查询每 30 秒重读一次,**只在标签可见时**;
/// 切走就停 —— 与监控页同一个理由:没人看的页面不该一直打服务器。
/// </para>
/// </summary>
internal sealed class ProfilerTabViewModel : WorkspaceTab
{
    /// <summary>一次最多读多少条 profile 记录(按时间倒序)。再多就只算条数,不再逐条聚合。</summary>
    internal const int ProfileLimit = 5000;

    private static readonly TimeSpan SlowWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromSeconds(30);

    private readonly MonitorPoller _poller;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private bool _loaded;
    private bool _disposed;
    private bool _failureShown;
    private int _level;
    private int _slowMs = 100;
    private double _sampleRate = 1;
    private string _slowMsText = "100";
    private string _sampleRateText = "1.0";
    private bool _onlyLong;
    private bool _ownOpsOnly;
    private int _activeCount;
    private string _mode = "shape";
    private string _sortKey = "avg";
    private ProfilerShapeRow? _selectedShape;
    private ProfilerEntryRow? _selectedEntry;
    private ProfilerDetail? _detail;
    private long _profileCount;
    private bool _profileDenied;
    private string _slowEmptyText = "";
    private DateTime _slowLoadedAt = DateTime.MinValue;
    private List<ProfilerShapeRow> _groups = [];

    /// <summary>构造。不在这里发请求:同键标签已开着时,外壳会把新建的这个直接丢掉。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">库名。</param>
    public ProfilerTabViewModel(IMongoWorkspace workspace, string database)
        : base(workspace)
    {
        Database = database;
        Title = workspace.Loc["Toolbar_SlowQueries"];
        Scope = "· " + database;
        _poller = new MonitorPoller(TickAsync, TimeSpan.FromSeconds(2));
        SetLevelCommand = new AsyncCommand<string>(SetLevelAsync);
        ApplySettingsCommand = new AsyncCommand(ApplySettingsAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        ClearCommand = new AsyncCommand(ClearAsync);
        KillCommand = new AsyncCommand<ProfilerOpRow>(KillAsync);
        SortCommand = new RelayCommand<string>(key => SortKey = key);
        CreateIndexCommand = new AsyncCommand(CreateIndexAsync, () => _detail?.CanCreateIndex == true);
        OpenInEditorCommand = new RelayCommand(() =>
        {
            if (_detail is { } d)
            {
                Workspace.OpenQuery(Database, d.Statement);
            }
        });
        ExplainCommand = new RelayCommand(() =>
        {
            if (_detail is { } d)
            {
                Workspace.OpenQuery(Database, d.Explain, run: true);
            }
        });
        PropertyChanged += OnOwnPropertyChanged;
    }

    /// <summary>库名。</summary>
    public string Database { get; }

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Profiler;

    /// <inheritdoc />
    public override string Key => $"profiler:{Database}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.timer";

    /// <inheritdoc />
    public override string IconToken => "VelaWarning";

    // ── profiler 设置 ─────────────────────────────────────────────────────

    /// <summary>当前 profiler 级别(0 / 1 / 2)。</summary>
    public int Level
    {
        get => _level;
        private set
        {
            _level = value;
            // 无论值变没变都通知:分段按钮被点过之后自己已经切了勾,确认框里点了取消要把它扳回来。
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(LevelKey));
        }
    }

    /// <summary>级别的字符串形式(分段按钮用 IsEqual 比较)。</summary>
    public string LevelKey => _level.ToString(CultureInfo.InvariantCulture);

    /// <summary>slowms 输入框。</summary>
    public string SlowMsText
    {
        get => _slowMsText;
        set
        {
            if (SetProperty(ref _slowMsText, value))
            {
                RaisePropertyChanged(nameof(SlowMsInvalid));
            }
        }
    }

    /// <summary>sampleRate 输入框。</summary>
    public string SampleRateText
    {
        get => _sampleRateText;
        set
        {
            if (SetProperty(ref _sampleRateText, value))
            {
                RaisePropertyChanged(nameof(SampleRateInvalid));
            }
        }
    }

    /// <summary>slowms 不是非负整数。</summary>
    public bool SlowMsInvalid => !TryParseSlowMs(_slowMsText, out _);

    /// <summary>sampleRate 不在 (0, 1]。</summary>
    public bool SampleRateInvalid => !TryParseSampleRate(_sampleRateText, out _);

    /// <summary>切级别(参数 "0" / "1" / "2";level 2 要确认)。</summary>
    public AsyncCommand<string> SetLevelCommand { get; }

    /// <summary>应用 slowms / sampleRate(回车或失焦)。</summary>
    public AsyncCommand ApplySettingsCommand { get; }

    /// <summary>刷新(profile 状态、当前操作、慢查询)。</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>清空 system.profile。</summary>
    public AsyncCommand ClearCommand { get; }

    // ── 当前操作 ──────────────────────────────────────────────────────────

    /// <summary>当前操作。</summary>
    public ObservableCollection<ProfilerOpRow> Ops { get; } = [];

    /// <summary>活跃操作数。</summary>
    public int ActiveCount
    {
        get => _activeCount;
        private set
        {
            if (SetProperty(ref _activeCount, value))
            {
                RaisePropertiesChanged(nameof(OpsHeader), nameof(HasOps));
            }
        }
    }

    /// <summary>有没有操作。</summary>
    public bool HasOps => _activeCount > 0;

    /// <summary>「$currentOp · 活跃 4 · 每 2 s 刷新」。</summary>
    public string OpsHeader => Loc.Format(_ownOpsOnly ? "Prof_OpsHeaderOwn" : "Prof_OpsHeader", _activeCount);

    /// <summary>仅显示运行 &gt; 1 s。</summary>
    public bool OnlyLong
    {
        get => _onlyLong;
        set
        {
            if (SetProperty(ref _onlyLong, value))
            {
                _ = RefreshOpsAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>终止一个操作。</summary>
    public AsyncCommand<ProfilerOpRow> KillCommand { get; }

    // ── 慢查询 ────────────────────────────────────────────────────────────

    /// <summary>按形状(<c>shape</c>)还是逐条(<c>entry</c>)。</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (value is "shape" or "entry" && SetProperty(ref _mode, value))
            {
                RaisePropertiesChanged(nameof(ShapeMode), nameof(EntryMode));
                // 换模式时详情跟着那一边的选中项走;逐条那边还没选过就保留当前详情。
                if (ShapeMode && _selectedShape is { } shape)
                {
                    ShowDetail(shape, shape.Latest);
                }
                else if (EntryMode && _selectedEntry is { } entry)
                {
                    ShowDetail(entry.Group, entry.Entry);
                }
            }
        }
    }

    /// <summary>按形状模式。</summary>
    public bool ShapeMode => _mode == "shape";

    /// <summary>逐条模式。</summary>
    public bool EntryMode => _mode == "entry";

    /// <summary>按哪一列排序(<c>count</c> / <c>avg</c> / <c>max</c>,都是降序)。</summary>
    public string SortKey
    {
        get => _sortKey;
        set
        {
            if (value is "count" or "avg" or "max" && SetProperty(ref _sortKey, value))
            {
                ApplyShapes(_groups);
            }
        }
    }

    /// <summary>点列头换排序。</summary>
    public RelayCommand<string> SortCommand { get; }

    /// <summary>形状行。</summary>
    public ObservableCollection<ProfilerShapeRow> Shapes { get; } = [];

    /// <summary>逐条行。</summary>
    public ObservableCollection<ProfilerEntryRow> Entries { get; } = [];

    /// <summary>选中的形状。</summary>
    public ProfilerShapeRow? SelectedShape
    {
        get => _selectedShape;
        set
        {
            if (SetProperty(ref _selectedShape, value) && value is not null)
            {
                ShowDetail(value, value.Latest);
            }
        }
    }

    /// <summary>选中的一条。</summary>
    public ProfilerEntryRow? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value) && value is not null)
            {
                ShowDetail(value.Group, value.Entry);
            }
        }
    }

    /// <summary>右侧详情;没选中为 null。</summary>
    public ProfilerDetail? Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                RaisePropertyChanged(nameof(HasDetail));
                CreateIndexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>有没有详情。</summary>
    public bool HasDetail => _detail is not null;

    /// <summary>慢查询列表为空时的说明(profiler 没开 / 近 24 小时没有 / 没权限)。</summary>
    public string SlowEmptyText
    {
        get => _slowEmptyText;
        private set
        {
            if (SetProperty(ref _slowEmptyText, value))
            {
                RaisePropertyChanged(nameof(SlowEmpty));
            }
        }
    }

    /// <summary>慢查询列表是空的。</summary>
    public bool SlowEmpty => _slowEmptyText.Length > 0;

    /// <summary>近 24 小时的 profile 记录数。</summary>
    public long ProfileCount => _profileCount;

    /// <summary>创建建议索引。</summary>
    public AsyncCommand CreateIndexCommand { get; }

    /// <summary>在查询编辑器中打开样本语句。</summary>
    public RelayCommand OpenInEditorCommand { get; }

    /// <summary>查看执行计划(语句 + explain("executionStats"),打开即运行)。</summary>
    public RelayCommand ExplainCommand { get; }

    // ── 生命周期 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task LoadAsync()
    {
        await LoadProfileStatusAsync(CancellationToken.None).ConfigureAwait(true);
        await RefreshOpsAsync(CancellationToken.None).ConfigureAwait(true);
        await LoadSlowAsync(CancellationToken.None).ConfigureAwait(true);
        _loaded = true;
        UpdatePolling(delayFirst: true);
    }

    /// <inheritdoc />
    protected override void OnActivated() => UpdatePolling();

    /// <inheritdoc />
    public override async Task RefreshAsync()
    {
        await LoadProfileStatusAsync(CancellationToken.None).ConfigureAwait(true);
        await RefreshOpsAsync(CancellationToken.None).ConfigureAwait(true);
        await LoadSlowAsync(CancellationToken.None).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _disposed = true;
        _poller.Stop();
        PropertyChanged -= OnOwnPropertyChanged;
        base.Dispose();
    }

    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsActive))
        {
            UpdatePolling();
        }
    }

    private void UpdatePolling(bool delayFirst = false)
    {
        if (_loaded && IsActive && !_disposed)
        {
            _poller.Start(delayFirst);
        }
        else
        {
            _poller.Stop();
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        if (!await _tickGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await RefreshOpsAsync(token).ConfigureAwait(true);
            if (DateTime.UtcNow - _slowLoadedAt > SlowRefresh && !token.IsCancellationRequested)
            {
                await LoadSlowAsync(token).ConfigureAwait(true);
            }
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private void Fail(Exception ex)
    {
        if (_failureShown)
        {
            return;
        }
        _failureShown = true;
        Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
    }

    /// <summary>
    /// 本页发出的命令都带上监控标记:level 2 下 profiler 会把每条命令都记下来,
    /// 不打标记的话"慢查询"里会混进本页自己每 30 秒一次的 <c>profile</c> 查询。
    /// </summary>
    private static BsonDocument Tagged(BsonDocument command)
    {
        command["comment"] = MonitorOps.Comment;
        return command;
    }

    // ── profiler 设置 ─────────────────────────────────────────────────────

    private async Task LoadProfileStatusAsync(CancellationToken token)
    {
        try
        {
            BsonDocument r = await Workspace.Connection.RunCommandAsync(Database, Tagged(new BsonDocument("profile", -1)), token)
                .ConfigureAwait(true);
            ApplyStatus(r);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Fail(ex);
        }
        UpdateStatus();
    }

    private void ApplyStatus(BsonDocument r)
    {
        Level = r.GetValue("was", 0).ToInt32();
        _slowMs = r.GetValue("slowms", 100).ToInt32();
        _sampleRate = r.TryGetValue("sampleRate", out BsonValue sr) && sr.IsNumeric ? sr.ToDouble() : 1;
        SlowMsText = _slowMs.ToString(CultureInfo.InvariantCulture);
        SampleRateText = _sampleRate.ToString("0.0##", CultureInfo.InvariantCulture);
    }

    private async Task SetLevelAsync(string key)
    {
        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) || level is < 0 or > 2)
        {
            return;
        }
        if (level == _level)
        {
            Level = _level;
            return;
        }
        if (level == 2 && !await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Prof_Level2Title"],
                Message = Loc.Format("Prof_Level2Body", Database),
                ConfirmLabel = Loc["Prof_Level2Confirm"],
                IconKey = "Mongo.triangle-alert",
                Danger = false
            }).ConfigureAwait(true))
        {
            Level = _level;
            return;
        }
        await ApplyProfileAsync(level, _slowMs, _sampleRate).ConfigureAwait(true);
    }

    private async Task ApplySettingsAsync()
    {
        if (!TryParseSlowMs(_slowMsText, out int slowMs) || !TryParseSampleRate(_sampleRateText, out double rate))
        {
            return;
        }
        if (slowMs == _slowMs && Math.Abs(rate - _sampleRate) < 1e-9)
        {
            return;
        }
        await ApplyProfileAsync(_level, slowMs, rate).ConfigureAwait(true);
    }

    /// <summary>
    /// <c>db.setProfilingLevel(level, { slowms, sampleRate })</c> 的等价命令。
    /// 注意 slowms 与 sampleRate 是**整个 mongod 实例**的设置(所有库共用),只有级别是按库的 ——
    /// 输入框的提示里写明了这一点。
    /// </summary>
    private async Task ApplyProfileAsync(int level, int slowMs, double sampleRate)
    {
        try
        {
            await Workspace.Connection.RunCommandAsync(Database, Tagged(new BsonDocument
            {
                { "profile", level },
                { "slowms", slowMs },
                { "sampleRate", sampleRate }
            })).ConfigureAwait(true);
            await LoadProfileStatusAsync(CancellationToken.None).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Prof_LevelApplied", _level, _slowMs), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Level = _level;
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    /// <summary>
    /// 清空 system.profile。profiler 开着时这个集合删不掉(服务器会拒绝),所以是三步:
    /// 先把级别设 0 → drop → 恢复原级别与阈值。中间失败也要尽量把级别恢复回去 ——
    /// 用户点的是"清空记录",不是"关掉 profiler"。
    /// </summary>
    private async Task ClearAsync()
    {
        if (!Workspace.EnsureWritable(Database))
        {
            return;
        }
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Prof_ClearTitle"],
                Message = Loc.Format("Prof_ClearBody", Database, BsonText.Grouped(_profileCount), _level),
                ConfirmLabel = Loc["Prof_ClearConfirm"],
                IconKey = "Mongo.eraser",
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? "system.profile" : null
            }).ConfigureAwait(true))
        {
            return;
        }
        int original = _level;
        try
        {
            if (original > 0)
            {
                await Workspace.Connection.RunCommandAsync(Database, Tagged(new BsonDocument("profile", 0))).ConfigureAwait(true);
            }
            await Workspace.Connection.Database(Database).DropCollectionAsync("system.profile").ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Prof_Cleared", Database), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            if (original > 0)
            {
                try
                {
                    await Workspace.Connection.RunCommandAsync(Database, Tagged(new BsonDocument
                    {
                        { "profile", original },
                        { "slowms", _slowMs },
                        { "sampleRate", _sampleRate }
                    })).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is MongoException or TimeoutException)
                {
                    Workspace.Toast(new() { Title = Loc.Format("Prof_RestoreFailed", original, MongoConnector.Describe(ex)), Kind = ToastKind.Error });
                }
            }
        }
        await RefreshAsync().ConfigureAwait(true);
    }

    internal static bool TryParseSlowMs(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;

    internal static bool TryParseSampleRate(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is > 0 and <= 1;

    // ── 当前操作 ──────────────────────────────────────────────────────────

    private async Task RefreshOpsAsync(CancellationToken token)
    {
        var match = new BsonDocument
        {
            { "active", true },
            { "op", new BsonDocument("$ne", "none") },
            // 驱动的心跳(awaitable hello)常年挂在列表里、一挂十秒,却不是任何人的负载。
            { "command.hello", new BsonDocument("$exists", false) },
            { "command.isMaster", new BsonDocument("$exists", false) },
            { "command.ismaster", new BsonDocument("$exists", false) }
        };
        if (_onlyLong)
        {
            match["microsecs_running"] = new BsonDocument("$gte", 1_000_000);
        }
        IReadOnlyList<BsonDocument> ops;
        try
        {
            (ops, _ownOpsOnly) = await MonitorOps.ListAsync(Workspace.Connection, match, token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Fail(ex);
            return;
        }
        if (token.IsCancellationRequested || _disposed)
        {
            return;
        }
        double longest = ops.Select(Seconds).DefaultIfEmpty(0).Max();
        var rows = ops
            .OrderByDescending(Seconds)
            .Select(op => ToRow(op, Math.Max(longest, 1)))
            .ToList();
        Ops.Clear();
        foreach (ProfilerOpRow row in rows)
        {
            Ops.Add(row);
        }
        ActiveCount = rows.Count;
        RaisePropertyChanged(nameof(OpsHeader));
    }

    private static double Seconds(BsonDocument op) =>
        op.TryGetValue("microsecs_running", out BsonValue us) && us.IsNumeric
            ? us.ToDouble() / 1_000_000
            : op.TryGetValue("secs_running", out BsonValue s) && s.IsNumeric ? s.ToDouble() : 0;

    private static ProfilerOpRow ToRow(BsonDocument op, double longest)
    {
        double seconds = Seconds(op);
        string client = op.GetValue("client", "").ToString() ?? "";
        if (op.TryGetValue("effectiveUsers", out BsonValue users) && users is BsonArray { Count: > 0 } list
                                                                 && list[0] is BsonDocument user && user.TryGetValue("user", out BsonValue name))
        {
            string host = client.Contains(':') ? client[..client.LastIndexOf(':')] : client;
            client = $"{name}@{host}";
        }
        return new ProfilerOpRow(
            op.GetValue("opid", BsonNull.Value),
            MonitorOps.OpId(op),
            op.GetValue("op", "").ToString() ?? "",
            op.GetValue("ns", "").ToString() ?? "",
            CommandSummary(op),
            client.Length > 0 ? client : (op.GetValue("desc", "").ToString() ?? ""),
            seconds,
            seconds / longest);
    }

    /// <summary>
    /// 命令摘要(设计稿 15:<c>find { customer.level: "SVIP", total: { $gte: 5000 } }</c>、
    /// <c>aggregate [$match, $group] · cursor 7f3a…</c>、<c>updateMany { expireAt: { $lt: … } }</c>、
    /// <c>createIndexes orders.paidAt_1</c>)。只取能一眼认出"这是哪个查询"的部分,全文看日志。
    /// </summary>
    internal static string CommandSummary(BsonDocument op)
    {
        BsonDocument cmd = op.GetValue("command", new BsonDocument()) as BsonDocument ?? [];
        string type = op.GetValue("op", "").ToString() ?? "";
        if (cmd.Contains("q") && type is "update" or "remove")
        {
            bool multi = type == "update" ? cmd.GetValue("multi", false).ToBoolean() : cmd.GetValue("limit", 1).ToDouble() == 0;
            string verb = type == "update" ? multi ? "updateMany" : "updateOne" : multi ? "deleteMany" : "deleteOne";
            return Line($"{verb} {Filter(cmd.GetValue("q", new BsonDocument()))}");
        }
        if (cmd.ElementCount == 0)
        {
            return op.GetValue("desc", "").ToString() ?? "";
        }
        string head = cmd.GetElement(0).Name;
        switch (head)
        {
            case "find":
                return Line($"find {Filter(cmd.GetValue("filter", new BsonDocument()))}");
            case "aggregate":
                return Line($"aggregate {Stages(cmd)}");
            case "getMore":
            {
                BsonDocument origin = op.GetValue("cursor", new BsonDocument()) is BsonDocument c
                                      && c.GetValue("originatingCommand", BsonNull.Value) is BsonDocument oc
                    ? oc
                    : op.GetValue("originatingCommand", new BsonDocument()) as BsonDocument ?? [];
                string id = cmd.GetValue("getMore", 0).ToString() ?? "";
                string cursor = id.Length > 4 ? $"cursor {id[..4]}…" : $"cursor {id}";
                string what = origin.Contains("aggregate") ? $"aggregate {Stages(origin)}"
                    : origin.Contains("find") ? $"find {Filter(origin.GetValue("filter", new BsonDocument()))}"
                    : "getMore";
                return Line($"{what} · {cursor}");
            }
            case "update" when cmd.GetValue("updates", BsonNull.Value) is BsonArray { Count: > 0 } updates && updates[0] is BsonDocument first:
                return Line($"{(first.GetValue("multi", false).ToBoolean() ? "updateMany" : "updateOne")} {Filter(first.GetValue("q", new BsonDocument()))}");
            case "delete" when cmd.GetValue("deletes", BsonNull.Value) is BsonArray { Count: > 0 } deletes && deletes[0] is BsonDocument d:
                return Line($"{(d.GetValue("limit", 0).ToDouble() == 0 ? "deleteMany" : "deleteOne")} {Filter(d.GetValue("q", new BsonDocument()))}");
            case "createIndexes":
            {
                string names = cmd.GetValue("indexes", new BsonArray()) is BsonArray specs
                    ? string.Join(", ", specs.OfType<BsonDocument>().Select(static s => s.GetValue("name", "").ToString()))
                    : "";
                return Line($"createIndexes {cmd[0]}.{names}");
            }
            case "count" or "distinct":
                return Line($"{head} {Filter(cmd.GetValue("query", new BsonDocument()))}");
            default:
                return Line($"{head} {cmd[0]}");
        }
    }

    private static string Filter(BsonValue value) => value is BsonDocument doc ? QueryShape.Plain(doc) : BsonText.Literal(value);

    private static string Stages(BsonDocument command) =>
        command.GetValue("pipeline", new BsonArray()) is BsonArray pipeline
            ? "[" + string.Join(", ", pipeline.OfType<BsonDocument>().Where(static s => s.ElementCount > 0).Select(static s => s.GetElement(0).Name)) + "]"
            : "[]";

    private static string Line(string text) => BsonText.OneLine(text);

    /// <summary>
    /// killOp。被杀的操作在客户端那边收到 Interrupted 错误;写操作已经写下去的部分不会回滚 ——
    /// 这一点写进确认框,生产连接要手打 opid。
    /// </summary>
    private async Task KillAsync(ProfilerOpRow row)
    {
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Prof_KillTitle"],
                Message = Loc.Format("Prof_KillBody", row.OpIdText, row.Type, row.Namespace, row.ElapsedText),
                ConfirmLabel = Loc["Prof_Kill"],
                IconKey = "Mongo.octagon-x",
                Facts =
                [
                    new(Loc["Prof_ColCommand"], row.Summary.Length > 60 ? row.Summary[..60] + "…" : row.Summary),
                    new(Loc["Prof_ColClient"], row.Client)
                ],
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? row.OpIdText : null
            }).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await Workspace.Connection.RunCommandAsync("admin", Tagged(new BsonDocument { { "killOp", 1 }, { "op", row.OpId } }))
                .ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Prof_Killed", row.OpIdText), Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        await RefreshOpsAsync(CancellationToken.None).ConfigureAwait(true);
    }

    // ── 慢查询 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 读近 24 小时的 system.profile(按时间倒序,至多 <see cref="ProfileLimit" /> 条),客户端按形状聚合。
    /// 不用 <c>$group</c> 在服务器上聚:形状归一化(字面值 → ?)是递归改写整个条件文档,
    /// 用聚合表达式写不出来;5000 条记录在客户端分组是毫秒级的事。
    /// </summary>
    internal async Task LoadSlowAsync(CancellationToken token)
    {
        _slowLoadedAt = DateTime.UtcNow;
        var filter = new BsonDocument
        {
            { "ts", new BsonDocument("$gte", DateTime.UtcNow - SlowWindow) },
            { "command.comment", new BsonDocument("$ne", MonitorOps.Comment) },
            { "command.profile", new BsonDocument("$exists", false) }
        };
        List<BsonDocument> docs;
        long count;
        try
        {
            IMongoCollection<BsonDocument> profile = Workspace.Connection.Collection(Database, "system.profile");
            docs = await profile.Find(filter, new FindOptions { Comment = MonitorOps.Comment })
                .Sort(new BsonDocument("ts", -1))
                .Limit(ProfileLimit)
                .Project(new BsonDocument { { "locks", 0 }, { "storage", 0 }, { "flowControl", 0 }, { "readConcern", 0 }, { "writeConcern", 0 } })
                .ToListAsync(token).ConfigureAwait(true);
            count = docs.Count < ProfileLimit
                ? docs.Count
                : await profile.CountDocumentsAsync(filter, new CountOptions { Comment = MonitorOps.Comment }, token).ConfigureAwait(true);
            _profileDenied = false;
        }
        catch (MongoCommandException ex) when (ex.Code == 13)
        {
            _profileDenied = true;
            docs = [];
            count = 0;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Fail(ex);
            return;
        }
        if (token.IsCancellationRequested || _disposed)
        {
            return;
        }
        _profileCount = count;
        RaisePropertyChanged(nameof(ProfileCount));
        List<ProfileEntry> entries = [.. docs.Select(ProfileEntry.Parse).Where(static e => !e.IsDiagnostic)];
        double overall = entries.Sum(static e => e.Millis);
        _groups = [.. entries.GroupBy(static e => e.Shape.Key).Select(g => new ProfilerShapeRow([.. g], overall))];
        ApplyShapes(_groups);

        // 逐条:按时间倒序,复用形状行里的统计(详情要"占比")。
        Dictionary<string, ProfilerShapeRow> byKey = _groups.ToDictionary(static g => g.Shape.Key, StringComparer.Ordinal);
        ProfileEntry? keepEntry = _selectedEntry?.Entry;
        Entries.Clear();
        foreach (ProfileEntry e in entries)
        {
            Entries.Add(new ProfilerEntryRow(e, byKey[e.Shape.Key]));
        }
        if (keepEntry is not null)
        {
            ProfilerEntryRow? again = Entries.FirstOrDefault(r => r.Entry.Time == keepEntry.Time && r.Entry.Shape.Key == keepEntry.Shape.Key);
            _selectedEntry = again;
            RaisePropertyChanged(nameof(SelectedEntry));
            if (again is not null && EntryMode)
            {
                ShowDetail(again.Group, again.Entry);
            }
        }

        SlowEmptyText = _profileDenied
            ? Loc["Prof_EmptyDenied"]
            : _groups.Count > 0
                ? ""
                : _level == 0 ? Loc["Prof_EmptyOff"] : Loc["Prof_EmptyNone"];
        if (_groups.Count == 0)
        {
            Detail = null;
        }
        UpdateStatus();
    }

    private void ApplyShapes(List<ProfilerShapeRow> groups)
    {
        string? keep = _selectedShape?.Shape.Key;
        IEnumerable<ProfilerShapeRow> sorted = _sortKey switch
        {
            "count" => groups.OrderByDescending(static g => g.Count),
            "max" => groups.OrderByDescending(static g => g.MaxMillis),
            _ => groups.OrderByDescending(static g => g.AvgMillis)
        };
        Shapes.Clear();
        foreach (ProfilerShapeRow g in sorted)
        {
            Shapes.Add(g);
        }
        ProfilerShapeRow? select = (keep is null ? null : Shapes.FirstOrDefault(s => s.Shape.Key == keep)) ?? Shapes.FirstOrDefault();
        _selectedShape = null;
        SelectedShape = select;
        if (select is null)
        {
            RaisePropertyChanged(nameof(SelectedShape));
        }
    }

    private void ShowDetail(ProfilerShapeRow group, ProfileEntry sample)
    {
        var detail = new ProfilerDetail(group, sample, Loc);
        Detail = detail;
        _ = CheckExistingIndexAsync(detail);
    }

    /// <summary>
    /// 建议的索引是不是已经有了(同序同向的前缀,或整体反向)。有的话不再让人重复创建,
    /// 改为提示"已有索引却没用上"—— 那往往是统计或计划缓存的问题,该去看执行计划。
    /// </summary>
    private async Task CheckExistingIndexAsync(ProfilerDetail detail)
    {
        if (detail.Advice is not { } advice || !detail.CanCreateIndex)
        {
            return;
        }
        IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(Database, detail.Collection).ConfigureAwait(true);
        BsonDocument? hit = indexes.FirstOrDefault(i => i.GetValue("key", BsonNull.Value) is BsonDocument key && advice.CoveredBy(key));
        if (hit is not null && ReferenceEquals(detail, _detail))
        {
            detail.AdviceText = Loc.Format("Prof_AdviceExists", hit.GetValue("name", "").ToString());
            detail.CanCreateIndex = false;
            CreateIndexCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task CreateIndexAsync()
    {
        if (_detail is not { Advice: { } advice } detail || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        string ns = $"{Database}.{detail.Collection}";
        long? docs = await Workspace.Connection.EstimatedCountAsync(Database, detail.Collection).ConfigureAwait(true);
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Prof_CreateIndexTitle"],
                Message = Loc.Format("Prof_CreateIndexBody", ns),
                ConfirmLabel = Loc["Prof_CreateIndex"],
                IconKey = "Mongo.key-round",
                Danger = false,
                Facts =
                [
                    new(Loc["Prof_FactKeys"], advice.KeysText),
                    new(Loc["Prof_FactDocs"], docs is { } n ? BsonText.Grouped(n) : "—")
                ],
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? detail.Collection : null
            }).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            string name = await Workspace.Connection.Collection(Database, detail.Collection).Indexes
                .CreateOneAsync(new CreateIndexModel<BsonDocument>(advice.Keys)).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Prof_IndexCreated", name), Detail = ns, Kind = ToastKind.Success });
            detail.AdviceText = Loc.Format("Prof_AdviceExists", name);
            detail.CanCreateIndex = false;
            CreateIndexCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    private void UpdateStatus()
    {
        string level = _level == 0
            ? Loc["Prof_StatusOff"]
            : Loc.Format("Prof_StatusLevel", _level, _slowMs);
        StatusText = Loc.Format("Prof_Status", BsonText.Grouped(_profileCount), level);
    }
}
