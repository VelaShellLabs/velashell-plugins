using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 服务器监控(设计稿 11):六张指标卡、操作 / 秒堆叠柱、副本集成员、连接数、存储 Top 与由采样推断的事件。
/// <para>
/// 数据全部来自周期性的 <c>serverStatus</c> + <c>replSetGetStatus</c>(率值 = 相邻两次计数器之差 / 时间差);
/// 历史只在内存里,按时间窗裁剪与分桶。**只在标签可见时采样**:切到别的标签就停,
/// 一个没人看的监控页每 2 秒打一次服务器,是在给被监控的对象添负载。
/// </para>
/// </summary>
internal sealed class MonitorTabViewModel : WorkspaceTab
{
    /// <summary>柱状图的柱数(15 分钟窗口 → 每柱 30 秒,与设计稿一致)。</summary>
    private const int Bars = 30;

    /// <summary>存储 Top 列几个集合。</summary>
    private const int StorageTop = 10;

    /// <summary>复制延迟告警阈值(秒)。</summary>
    internal const double LagThreshold = 1;

    /// <summary>事件只留近 1 小时(设计稿事件栏的口径)。</summary>
    private static readonly TimeSpan EventRetention = TimeSpan.FromHours(1);

    private readonly SampleHistory _history = new();
    private readonly MonitorPoller _poller;
    private readonly Dictionary<string, string> _indexBuilds = [with(StringComparer.Ordinal)];
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private ServerSample? _last;
    private BsonDocument? _lastStatus;
    private BsonDocument? _lastReplStatus;
    private BsonDocument? _replConfig;
    private ReplicaSnapshot? _replica;
    private DateTime _replConfigAt;
    private DateTime _oplogAt;
    private DateTime _storageAt;
    private DateTime _profileLevelsAt;
    private DateTime _slowCheckedAt;
    private List<(string Database, int SlowMs)> _profiled = [];
    private bool _loaded;
    private bool _disposed;
    private bool _failureShown;
    private bool _currentOpDenied;
    private string? _fcv;
    private MonitorEvent? _lagEvent;
    private DateTime _lastOpsSpike = DateTime.MinValue;
    private DateTime _lastConnSpike = DateTime.MinValue;

    /// <summary>构造。不在这里发任何请求:同键标签已开着时,外壳会把新建的这个直接丢掉。</summary>
    /// <param name="workspace">外壳服务。</param>
    public MonitorTabViewModel(IMongoWorkspace workspace)
        : base(workspace)
    {
        Title = workspace.Loc["Toolbar_Monitor"];
        _poller = new MonitorPoller(TickAsync, Interval);
        Connections = new(Loc["Mon_KpiConnections"], "Mongo.plug");
        Ops = new(Loc["Mon_KpiOps"], "Mongo.zap");
        Network = new(Loc["Mon_KpiNetwork"], "Mongo.arrow-down-up");
        Cache = new(Loc["Mon_KpiCache"], "Mongo.memory-stick");
        Lag = new(Loc["Mon_KpiLag"], "Mongo.timer") { AlertText = Loc["Mon_High"] };
        Oplog = new(Loc["Mon_KpiOplog"], "Mongo.history");
        PauseCommand = new RelayCommand(TogglePause);
        ExportCommand = new AsyncCommand(ExportAsync);
        ToggleEventsCommand = new RelayCommand(() => ShowAllEvents = !ShowAllEvents);
        Events.CollectionChanged += (_, _) => RaisePropertiesChanged(nameof(VisibleEvents), nameof(HasEvents));
        PropertyChanged += OnOwnPropertyChanged;
    }

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Monitor;

    /// <inheritdoc />
    public override string Key => "monitor";

    /// <inheritdoc />
    public override string IconKey => "Mongo.activity";

    /// <inheritdoc />
    public override string IconToken => "VelaStatusConnected";

    // ── 工具行 ────────────────────────────────────────────────────────────

    /// <summary>暂停中(「● 实时」变灰,按钮变「继续」)。</summary>
    public bool IsPaused
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(PauseLabel), nameof(PauseIcon));
                UpdatePolling();
                UpdateStatus();
            }
        }
    }

    /// <summary>暂停按钮文字。</summary>
    public string PauseLabel => IsPaused ? Loc["Mon_Resume"] : Loc["Mon_Pause"];

    /// <summary>暂停按钮图标。</summary>
    public string PauseIcon => IsPaused ? "Mongo.play" : "Mongo.pause";

    /// <summary>时间窗(<c>5m</c> / <c>15m</c> / <c>1h</c> / <c>6h</c>;分段按钮直接绑字符串)。</summary>
    public string Window
    {
        get;
        set
        {
            if (value is "5m" or "15m" or "1h" or "6h" && SetProperty(ref field, value))
            {
                UpdateCharts();
            }
        }
    } = "15m";

    /// <summary>采样间隔(秒)。</summary>
    public int IntervalSeconds
    {
        get;
        set
        {
            value = Math.Clamp(value, 1, 60);
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(IntervalText));
                _poller.Interval = Interval;
                if (_poller.IsRunning)
                {
                    _poller.Restart(delayFirst: true);
                }
                UpdateStatus();
            }
        }
    } = 2;

    /// <summary>可选的采样间隔。</summary>
    public static IReadOnlyList<int> IntervalChoices { get; } = [1, 2, 5, 10];

    /// <summary>下拉上的字(<c>每 2 s</c>)。</summary>
    public string IntervalText => Loc.Format("Mon_Every", IntervalSeconds);

    /// <summary>
    /// 测试用:把采样间隔压到 1 秒以下(截图要先攒 15 个以上的采样点,按 2 秒一次就得干等半分钟)。
    /// </summary>
    internal TimeSpan? IntervalOverride
    {
        get;
        set
        {
            field = value;
            _poller.Interval = Interval;
        }
    }

    private TimeSpan Interval => IntervalOverride ?? TimeSpan.FromSeconds(IntervalSeconds);

    /// <summary>右侧灰字(<c>uptime 14 天 6 小时 · WiredTiger · featureCompatibilityVersion 7.0</c>)。</summary>
    public string ServerLine
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>采样失败的说明(权限不够、连接断了);空 = 正常。</summary>
    public string ErrorText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    } = "";

    /// <summary>有没有失败说明。</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>暂停 / 继续。</summary>
    public RelayCommand PauseCommand { get; }

    /// <summary>导出快照(serverStatus + replSetGetStatus + 当前序列,存成 JSON)。</summary>
    public AsyncCommand ExportCommand { get; }

    // ── 指标卡 ────────────────────────────────────────────────────────────

    /// <summary>连接。</summary>
    public MonitorKpi Connections { get; }

    /// <summary>操作 / 秒。</summary>
    public MonitorKpi Ops { get; }

    /// <summary>网络。</summary>
    public MonitorKpi Network { get; }

    /// <summary>缓存使用。</summary>
    public MonitorKpi Cache { get; }

    /// <summary>复制延迟。</summary>
    public MonitorKpi Lag { get; }

    /// <summary>Oplog 窗口。</summary>
    public MonitorKpi Oplog { get; }

    // ── 图表 ──────────────────────────────────────────────────────────────

    /// <summary>操作 / 秒 的四组堆叠数据。</summary>
    public IReadOnlyList<ChartSeries> OpsSeries
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>操作 / 秒 的横轴标签。</summary>
    public IReadOnlyList<string> OpsLabels
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>操作 / 秒 的副标题(<c>opcounters · 近 15 分钟 · 每柱 30 s</c>)。</summary>
    public string OpsSubtitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>右上图例(带当前值)。</summary>
    public IReadOnlyList<MonitorLegend> Legend
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>操作 / 秒 的纵轴上限(三等分后刻度好读)。</summary>
    public double OpsMax
    {
        get;
        private set => SetProperty(ref field, value);
    } = 3;

    /// <summary>连接数的纵轴上限。</summary>
    public double ConnMax
    {
        get;
        private set => SetProperty(ref field, value);
    } = 3;

    /// <summary>两张柱状图至少分几格(满窗口的柱数;刚开始采样时柱子也保持最终宽度,靠右排)。</summary>
    public int ChartSlots => Bars;

    /// <summary>连接数柱状图。</summary>
    public IReadOnlyList<ChartSeries> ConnSeries
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>连接数横轴标签。</summary>
    public IReadOnlyList<string> ConnLabels
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>连接数副标题(<c>当前 128 · 峰值 164</c>)。</summary>
    public string ConnSubtitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    // ── 副本集 ────────────────────────────────────────────────────────────

    /// <summary>成员卡标题(<c>副本集 rs0</c>)。</summary>
    public string ReplicaTitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>成员卡副标题(<c>3 成员 · 多数派健康</c>)。</summary>
    public string ReplicaSubtitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>多数派不可用(副标题转红)。</summary>
    public bool MajorityLost
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>成员。</summary>
    public IReadOnlyList<MonitorMember> Members
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasMembers));
            }
        }
    } = [];

    /// <summary>有没有成员可列(单机 / mongos 时显示空态)。</summary>
    public bool HasMembers => Members.Count > 0;

    /// <summary>空态标题(<c>单机部署</c>)。</summary>
    public string ReplicaEmptyTitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>空态说明。</summary>
    public string ReplicaEmptyHint
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    // ── 存储 Top ──────────────────────────────────────────────────────────

    /// <summary>存储 Top 的行(前 6)。</summary>
    public IReadOnlyList<MonitorStorageRow> Storage
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasStorage));
                RaisePropertyChanged(nameof(StorageEmpty));
            }
        }
    } = [];

    /// <summary>有没有行。</summary>
    public bool HasStorage => Storage.Count > 0;

    /// <summary>加载过但一行都没有(空库)。</summary>
    public bool StorageEmpty { get => field && Storage.Count == 0; private set; }

    /// <summary>存储 Top 副标题(<c>shop · 数据 + 索引</c>)。</summary>
    public string StorageSubtitle
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    // ── 事件 ──────────────────────────────────────────────────────────────

    /// <summary>近 1 小时的事件(新的在前)。</summary>
    public ObservableCollection<MonitorEvent> Events { get; } = [];

    /// <summary>展开「全部」。</summary>
    public bool ShowAllEvents
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(VisibleEvents), nameof(EventsToggleText));
            }
        }
    }

    /// <summary>列出来的事件(收起时前 5 条)。</summary>
    public IReadOnlyList<MonitorEvent> VisibleEvents => ShowAllEvents ? Events.ToList() : Events.Take(5).ToList();

    /// <summary>有没有事件。</summary>
    public bool HasEvents => Events.Count > 0;

    /// <summary>「全部」/「收起」。</summary>
    public string EventsToggleText => ShowAllEvents ? Loc["Mon_EventsLess"] : Loc["Mon_EventsAll"];

    /// <summary>展开 / 收起事件。</summary>
    public RelayCommand ToggleEventsCommand { get; }

    /// <summary>已攒下的率值个数(测试等采样点用)。</summary>
    internal int SampleCount => _history.Rates.Count;

    // ── 生命周期 ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override async Task LoadAsync()
    {
        UpdateStatus();
        await TickAsync(CancellationToken.None).ConfigureAwait(true);
        _loaded = true;
        UpdatePolling(delayFirst: true);
    }

    /// <inheritdoc />
    protected override void OnActivated() => UpdatePolling();

    /// <inheritdoc />
    public override async Task RefreshAsync()
    {
        // F5:低频项(存储 Top、Oplog、成员配置)也一并重拉。
        _storageAt = _oplogAt = _replConfigAt = _profileLevelsAt = DateTime.MinValue;
        await TickAsync(CancellationToken.None).ConfigureAwait(true);
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

    /// <summary>只在"加载过、可见、没暂停、没关掉"时采样。</summary>
    private void UpdatePolling(bool delayFirst = false)
    {
        if (_loaded && IsActive && !IsPaused && !_disposed)
        {
            _poller.Start(delayFirst);
        }
        else
        {
            _poller.Stop();
        }
    }

    private void TogglePause() => IsPaused = !IsPaused;

    // ── 一轮采样 ──────────────────────────────────────────────────────────

    /// <summary>采一次(测试也直接调它)。</summary>
    internal async Task TickAsync(CancellationToken token)
    {
        if (_disposed)
        {
            return;
        }
        // 手动刷新与定时采样可能撞在一起:同一时刻只跑一轮,另一轮直接让路。
        if (!await _tickGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await SampleAsync(token).ConfigureAwait(true);
        }
        finally
        {
            _ = _tickGate.Release();
        }
    }

    private async Task SampleAsync(CancellationToken token)
    {
        MongoConnection connection = Workspace.Connection;
        DateTime now = DateTime.UtcNow;
        BsonDocument status;
        try
        {
            status = await connection.RunCommandAsync("admin", new BsonDocument("serverStatus", 1), token).ConfigureAwait(true);
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
        ErrorText = "";
        _failureShown = false;
        _lastStatus = status;
        var sample = ServerSample.Parse(status, now);
        ServerSample? previous = _last;
        RateSample? rate = previous is null ? null : RateSample.Between(previous, sample);
        if (previous is not null && sample.Uptime < previous.Uptime)
        {
            // 服务器重启过:旧计数器作废,历史从头攒。
            _history.Clear();
        }
        if (rate is not null)
        {
            _history.Add(rate);
        }
        _last = sample;

        await SampleReplicaAsync(now, token).ConfigureAwait(true);
        await TrackIndexBuildsAsync(previous, sample, now, token).ConfigureAwait(true);
        if (now - _oplogAt > TimeSpan.FromSeconds(60))
        {
            _oplogAt = now;
            await LoadOplogAsync(token).ConfigureAwait(true);
        }
        if (now - _storageAt > TimeSpan.FromSeconds(120))
        {
            _storageAt = now;
            await LoadStorageAsync(token).ConfigureAwait(true);
        }
        if (now - _profileLevelsAt > TimeSpan.FromMinutes(5))
        {
            _profileLevelsAt = now;
            await LoadProfileLevelsAsync(token).ConfigureAwait(true);
        }
        if (now - _slowCheckedAt > TimeSpan.FromSeconds(60))
        {
            await CheckSlowQueriesAsync(now, token).ConfigureAwait(true);
        }
        if (token.IsCancellationRequested || _disposed)
        {
            return;
        }

        if (rate is not null)
        {
            DetectSpikes(rate);
        }
        await EnsureFcvAsync(sample, token).ConfigureAwait(true);
        UpdateKpis(sample, _history.Latest);
        UpdateCharts();
        PruneEvents(now);
        UpdateStatus();
    }

    private void Fail(Exception ex)
    {
        string reason = MongoConnector.Describe(ex);
        ErrorText = ex is MongoCommandException { Code: 13 }
            ? Loc.Format("Mon_FailedPrivilege", reason)
            : Loc.Format("Mon_Failed", reason);
        if (!_failureShown)
        {
            // 每 2 秒失败一次就弹一次提示,那是骚扰;只在由好转坏的那一下提示,之后看页面上的横条。
            _failureShown = true;
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", reason), Kind = ToastKind.Error });
        }
    }

    private async Task EnsureFcvAsync(ServerSample sample, CancellationToken token)
    {
        if (sample.Fcv is { } fcv)
        {
            _fcv = fcv;
        }
        else if (_fcv is null)
        {
            // 老版本的 serverStatus 不带 FCV:问一次 getParameter,之后缓存。
            _fcv = "";
            try
            {
                BsonDocument p = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument
                {
                    { "getParameter", 1 },
                    { "featureCompatibilityVersion", 1 }
                }, token).ConfigureAwait(true);
                if (p.TryGetValue("featureCompatibilityVersion", out BsonValue v) && v is BsonDocument d && d.TryGetValue("version", out BsonValue ver))
                {
                    _fcv = ver.ToString() ?? "";
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
            }
        }
        string engine = sample.Engine.Length > 0 ? sample.Engine : Workspace.Connection.Server.StorageEngine;
        string line = Loc.Format("Mon_Uptime", Duration(TimeSpan.FromSeconds(sample.Uptime))) + " · " + engine;
        if (_fcv is { Length: > 0 })
        {
            line += " · featureCompatibilityVersion " + _fcv;
        }
        ServerLine = line;
    }

    // ── 副本集 ────────────────────────────────────────────────────────────

    private bool IsReplicaSet => Workspace.Connection.Server is { SetName.Length: > 0, Role: not "MONGOS" };

    private async Task SampleReplicaAsync(DateTime now, CancellationToken token)
    {
        ServerInfo server = Workspace.Connection.Server;
        if (!IsReplicaSet)
        {
            _replica = null;
            Members = [];
            ReplicaTitle = Loc["Mon_ReplicaPlain"];
            ReplicaSubtitle = server.Role;
            bool mongos = server.Role == "MONGOS";
            ReplicaEmptyTitle = mongos ? Loc["Mon_MongosTitle"] : Loc["Mon_StandaloneTitle"];
            ReplicaEmptyHint = mongos ? Loc["Mon_MongosHint"] : Loc["Mon_StandaloneHint"];
            return;
        }
        try
        {
            BsonDocument status = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument("replSetGetStatus", 1), token)
                .ConfigureAwait(true);
            _lastReplStatus = status;
            if (now - _replConfigAt > TimeSpan.FromMinutes(5) || _replConfig is null)
            {
                _replConfigAt = now;
                try
                {
                    _replConfig = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument("replSetGetConfig", 1), token)
                        .ConfigureAwait(true);
                }
                catch (MongoCommandException)
                {
                    // 没有 replSetGetConfig 权限:优先级与票数按默认值显示,不影响别的。
                }
            }
            var snapshot = ReplicaSnapshot.Parse(status, _replConfig);
            DetectReplicaEvents(_replica, snapshot, now);
            _replica = snapshot;
            ApplyReplica(snapshot, now);
        }
        catch (MongoCommandException ex)
        {
            _replica = null;
            Members = [];
            ReplicaTitle = Loc["Mon_ReplicaPlain"];
            ReplicaSubtitle = "";
            ReplicaEmptyTitle = Loc["Mon_ReplicaDenied"];
            ReplicaEmptyHint = MongoConnector.Describe(ex);
        }
    }

    private void ApplyReplica(ReplicaSnapshot snapshot, DateTime now)
    {
        ReplicaTitle = Loc.Format("Mon_ReplicaTitle", snapshot.SetName);
        ReplicaSubtitle = Loc.Format("Mon_ReplicaSubtitle", snapshot.Members.Count,
            snapshot.MajorityHealthy ? Loc["Mon_MajorityOk"] : Loc["Mon_MajorityLost"]);
        MajorityLost = !snapshot.MajorityHealthy;
        // 延迟条的满格:至少 4 秒 —— 正常的从节点(0.x 秒)只占一小截,一眼看出谁在掉队。
        double scale = Math.Max(4, snapshot.Members.Max(static m => m.LagSeconds ?? 0));
        Members =
        [
            .. snapshot.Members
                .OrderBy(static m => m.State == "PRIMARY" ? 0 : 1)
                .ThenBy(static m => m.Name, StringComparer.Ordinal)
                .Select(m => ToRow(m, scale, now))
        ];
    }

    private MonitorMember ToRow(ReplicaMember m, double scale, DateTime now)
    {
        bool lagging = m.LagSeconds > LagThreshold;
        string stateToken = !m.Healthy ? "VelaError" : m.State switch
        {
            "PRIMARY" => "VelaStatusConnected",
            "SECONDARY" => "VelaTextSecondary",
            "ARBITER" => "VelaTextTertiary",
            _ => "VelaWarning"
        };
        string dot = !m.Healthy ? "VelaError" : lagging ? "VelaStatusConnecting" : "VelaStatusConnected";
        string heartbeat = m.IsSelf
            ? Loc["Mon_MemberSelf"]
            : m.LastHeartbeat is { Year: > 2000 } beat
                ? Loc.Format("Mon_Heartbeat", Math.Max(0, (int)Math.Round((now - beat).TotalSeconds)))
                : "—";
        string meta = Loc.Format("Mon_MemberMeta", m.Priority.ToString("0.#", CultureInfo.InvariantCulture), m.Votes, heartbeat);
        return new(
            MonitorFormat.Host(m.Name),
            m.State,
            stateToken,
            dot,
            m.LagSeconds is { } lag && m.State != "PRIMARY" ? lag / scale : 0,
            lagging ? "VelaWarning" : "VelaTextTertiary",
            m.LagSeconds is { } l && m.State != "PRIMARY" ? MonitorFormat.Seconds(l) : "—",
            lagging ? "VelaWarning" : "VelaTextSecondary",
            meta,
            Loc["Mon_ReplLag"]);
    }

    // ── Oplog / 存储 Top / 慢查询 ─────────────────────────────────────────

    private async Task LoadOplogAsync(CancellationToken token)
    {
        if (!IsReplicaSet)
        {
            Oplog.Set("—", "", Workspace.Connection.Server.Role == "MONGOS" ? Loc["Mon_LagMongos"] : Loc["Mon_OplogNone"]);
            Oplog.FootWarn = false;
            return;
        }
        try
        {
            IMongoCollection<BsonDocument> oplog = Workspace.Connection.Collection("local", "oplog.rs");
            var projection = new BsonDocument("ts", 1);
            BsonDocument? first = await oplog.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { Comment = MonitorOps.Comment })
                .Sort(new BsonDocument("$natural", 1)).Project(projection).Limit(1)
                .FirstOrDefaultAsync(token).ConfigureAwait(true);
            BsonDocument? last = await oplog.Find(FilterDefinition<BsonDocument>.Empty, new FindOptions { Comment = MonitorOps.Comment })
                .Sort(new BsonDocument("$natural", -1)).Project(projection).Limit(1)
                .FirstOrDefaultAsync(token).ConfigureAwait(true);
            if (first?.GetValue("ts", BsonNull.Value) is not BsonTimestamp from || last?.GetValue("ts", BsonNull.Value) is not BsonTimestamp to)
            {
                Oplog.Set("—", "", Loc["Mon_OplogNone"]);
                return;
            }
            CollectionStats stats = await Workspace.Connection.GetStatsAsync("local", "oplog.rs", token).ConfigureAwait(true);
            long size = stats.Raw.TryGetValue("maxSize", out BsonValue max) && max.IsNumeric ? max.ToInt64() : stats.Size;
            var window = TimeSpan.FromSeconds(Math.Max(0, to.Timestamp - from.Timestamp));
            (string value, string unit) = OplogSpan(window);
            // 24 小时是常见的底线:够一次周末维护或一次大批量导入期间从节点掉线后追平。
            bool shortWindow = window < TimeSpan.FromHours(24);
            Oplog.Set(value, unit, Loc.Format(shortWindow ? "Mon_OplogShort" : "Mon_OplogEnough", BsonText.Bytes(size)));
            Oplog.FootWarn = shortWindow;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Oplog.Set("—", "", Loc["Mon_OplogDenied"]);
            Oplog.FootWarn = false;
        }
    }

    private (string Value, string Unit) OplogSpan(TimeSpan window) => window.TotalHours switch
    {
        < 1 => (Math.Max(0, Math.Round(window.TotalMinutes)).ToString("0", CultureInfo.InvariantCulture), Loc["Mon_UnitMinutes"]),
        < 10 => (window.TotalHours.ToString("0.0", CultureInfo.InvariantCulture), Loc["Mon_UnitHours"]),
        < 72 => (Math.Round(window.TotalHours).ToString("0", CultureInfo.InvariantCulture), Loc["Mon_UnitHours"]),
        _ => (window.TotalDays.ToString("0.0", CultureInfo.InvariantCulture), Loc["Mon_UnitDays"])
    };

    /// <summary>
    /// 存储 Top:用户库里按磁盘占用(数据 + 索引)排前 10 的集合(面板矮时在表内滚动)。
    /// 只看占用最大的 4 个库、每库至多 25 个集合 —— 每个集合一次 <c>$collStats</c>,
    /// 一个有上千集合的库全扫一遍,本身就是一次不小的负载。
    /// </summary>
    private async Task LoadStorageAsync(CancellationToken token)
    {
        try
        {
            IReadOnlyList<DatabaseInfo> databases = await Workspace.Connection.ListDatabasesAsync(token).ConfigureAwait(true);
            var items = new List<(string Database, string Collection, long Data, long Index)>();
            foreach (DatabaseInfo db in databases.Where(static d => !d.IsSystem && !d.Empty)
                         .OrderByDescending(static d => d.SizeOnDisk).Take(4))
            {
                IReadOnlyList<CollectionInfo> collections = await Workspace.Connection.ListCollectionsAsync(db.Name, token).ConfigureAwait(true);
                foreach (CollectionInfo c in collections.Where(static x => x.Kind is not (CollectionKind.View or CollectionKind.System)).Take(25))
                {
                    CollectionStats stats = await Workspace.Connection.GetStatsAsync(db.Name, c.Name, token).ConfigureAwait(true);
                    items.Add((db.Name, c.Name, stats.StorageSize, stats.TotalIndexSize));
                }
            }
            if (token.IsCancellationRequested)
            {
                return;
            }
            var top = items.OrderByDescending(static i => i.Data + i.Index).Take(StorageTop).ToList();
            bool oneDb = top.Select(static i => i.Database).Distinct(StringComparer.Ordinal).Count() <= 1;
            double max = Math.Max(1, top.Count == 0 ? 1 : top.Max(static i => i.Data + i.Index));
            Storage =
            [
                .. top.Select(i => new MonitorStorageRow(
                    oneDb ? "" : i.Database + ".",
                    i.Collection,
                    i.Data / max,
                    i.Index / max,
                    BsonText.Bytes(i.Data + i.Index),
                    $"{i.Database}.{i.Collection}\n" + Loc.Format("Mon_StorageTip", BsonText.Bytes(i.Data), BsonText.Bytes(i.Index))))
            ];
            StorageSubtitle = Loc.Format("Mon_StorageSubtitle", oneDb && top.Count > 0 ? top[0].Database : Loc["Mon_StorageAllDbs"]);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            StorageSubtitle = MongoConnector.Describe(ex);
        }
        StorageEmpty = true;
        RaisePropertyChanged(nameof(StorageEmpty));
    }

    /// <summary>哪些库开着 profiler(事件栏的"慢查询 N 条"只能从这些库的 system.profile 里数)。</summary>
    private async Task LoadProfileLevelsAsync(CancellationToken token)
    {
        var profiled = new List<(string, int)>();
        foreach (string db in Workspace.Databases.Where(static d => d is not ("admin" or "config" or "local")).Take(20))
        {
            try
            {
                // 带上监控标记:level 2 的库会把这条命令也记进 system.profile,慢查询页按标记把它滤掉。
                BsonDocument r = await Workspace.Connection.RunCommandAsync(db, new BsonDocument
                {
                    { "profile", -1 },
                    { "comment", MonitorOps.Comment }
                }, token).ConfigureAwait(true);
                if (r.GetValue("was", 0).ToInt32() > 0)
                {
                    profiled.Add((db, r.GetValue("slowms", 100).ToInt32()));
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
            }
        }
        _profiled = profiled;
    }

    /// <summary>
    /// 数一数上次检查以来各库 system.profile 里新增的慢操作(只算 ≥ slowms 的,level 2 会把所有操作都记下来)。
    /// 第一次看近 1 小时 —— 事件栏的口径。
    /// </summary>
    private async Task CheckSlowQueriesAsync(DateTime now, CancellationToken token)
    {
        DateTime since = _slowCheckedAt == default ? now - EventRetention : _slowCheckedAt;
        _slowCheckedAt = now;
        foreach ((string db, int slowMs) in _profiled)
        {
            try
            {
                BsonDocument[] stages =
                [
                    new("$match", new BsonDocument
                    {
                        { "ts", new BsonDocument("$gt", since) },
                        { "millis", new BsonDocument("$gte", slowMs) },
                        { "op", new BsonDocument("$ne", "getmore") }
                    }),
                    new("$group", new BsonDocument
                    {
                        { "_id", new BsonDocument { { "ns", "$ns" }, { "op", "$op" }, { "plan", "$planSummary" } } },
                        { "n", new BsonDocument("$sum", 1) },
                        { "last", new BsonDocument("$max", "$ts") }
                    }),
                    new("$sort", new BsonDocument("n", -1))
                ];
                using IAsyncCursor<BsonDocument> cursor = await Workspace.Connection.Collection(db, "system.profile")
                    .AggregateAsync<BsonDocument>(stages, new AggregateOptions { Comment = MonitorOps.Comment }, token)
                    .ConfigureAwait(true);
                List<BsonDocument> groups = await cursor.ToListAsync(token).ConfigureAwait(true);
                if (groups.Count == 0)
                {
                    continue;
                }
                int total = groups.Sum(static g => g["n"].ToInt32());
                BsonDocument top = groups[0];
                BsonDocument id = top["_id"].AsBsonDocument;
                string ns = id.GetValue("ns", "").ToString() ?? "";
                string coll = ns.Contains('.') ? ns[(ns.IndexOf('.') + 1)..] : ns;
                string verb = (id.GetValue("op", "").ToString() ?? "") switch
                {
                    "query" => "find",
                    "remove" => "delete",
                    "command" => "command",
                    var other => other
                };
                string plan = id.TryGetValue("plan", out BsonValue p) && p.IsString ? " " + p.AsString : "";
                DateTime at = groups.Max(static g => g["last"].ToUniversalTime());
                AddEvent(new MonitorEvent(at, "slow", "Mongo.timer", "VelaInfo",
                    Loc.Format("Mon_EventSlow", total),
                    $"{coll}.{verb}{plan} × {top["n"].ToInt32()}"));
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
            }
        }
    }

    // ── 事件推断 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 索引构建:跟踪 <c>$currentOp</c> 里的 createIndexes,它从列表里消失、且 <c>indexBuilds.phases.commit</c>
    /// 涨了,就是构建完成;消失了计数没涨,是被终止或失败了。构建快到两次采样之间就做完的,
    /// 只能从计数器的增量知道"有几个",叫不出名字。
    /// </summary>
    private async Task TrackIndexBuildsAsync(ServerSample? previous, ServerSample sample, DateTime now, CancellationToken token)
    {
        var finished = new List<string>();
        if (!_currentOpDenied)
        {
            try
            {
                // 没有 inprog 权限时只看得到自己的操作:别人发起的构建认不出,只剩计数器那条路。
                (IReadOnlyList<BsonDocument> ops, _) = await MonitorOps.ListAsync(Workspace.Connection,
                    new BsonDocument("command.createIndexes", new BsonDocument("$exists", true)), token).ConfigureAwait(true);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (BsonDocument op in ops)
                {
                    string opid = MonitorOps.OpId(op);
                    _ = seen.Add(opid);
                    if (!_indexBuilds.ContainsKey(opid) && op.GetValue("command", new BsonDocument()) is BsonDocument cmd)
                    {
                        string coll = cmd.GetValue("createIndexes", "").ToString() ?? "";
                        string names = cmd.GetValue("indexes", new BsonArray()) is BsonArray specs
                            ? string.Join(", ", specs.OfType<BsonDocument>().Select(static s => s.GetValue("name", "").ToString()))
                            : "";
                        _indexBuilds[opid] = names.Length > 0 ? $"{coll}.{names}" : coll;
                    }
                }
                foreach (string opid in _indexBuilds.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    finished.Add(_indexBuilds[opid]);
                    _ = _indexBuilds.Remove(opid);
                }
            }
            catch (MongoCommandException)
            {
                _currentOpDenied = true;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
            }
        }
        long committed = previous is { IndexBuilds: >= 0 } && sample.IndexBuilds >= previous.IndexBuilds
            ? sample.IndexBuilds - previous.IndexBuilds
            : finished.Count;
        int named = (int)Math.Min(committed, finished.Count);
        foreach (string name in finished.Take(named))
        {
            AddEvent(new MonitorEvent(now, "index", "Mongo.key-round", "VelaStatusConnected", Loc["Mon_EventIndexBuilt"], name));
        }
        foreach (string name in finished.Skip(named))
        {
            AddEvent(new MonitorEvent(now, "index", "Mongo.circle-x", "VelaWarning", Loc["Mon_EventIndexAborted"], name));
        }
        if (committed > named)
        {
            AddEvent(new MonitorEvent(now, "index", "Mongo.key-round", "VelaStatusConnected", Loc["Mon_EventIndexBuilt"],
                Loc.Format("Mon_EventIndexQuick", committed - named)));
        }
    }

    private void DetectSpikes(RateSample rate)
    {
        if (_history.Spike(static r => r.Total, minimumDelta: 20) is { } jump && rate.Time - _lastOpsSpike > TimeSpan.FromSeconds(60))
        {
            _lastOpsSpike = rate.Time;
            (string name, double value) = new[]
            {
                ("query", rate.Query), ("insert", rate.Insert), ("update", rate.Update), ("delete", rate.Delete)
            }.MaxBy(static x => x.Item2);
            AddEvent(new MonitorEvent(rate.Time, "spike", "Mongo.zap", "VelaWarning",
                Loc.Format("Mon_EventSpike", Math.Round(jump * 100)),
                Loc.Format("Mon_EventSpikeDetail", name, MonitorFormat.Rate(value))));
        }
        if (_history.Spike(static r => r.Connections, minimumDelta: 10) is { } connJump && rate.Time - _lastConnSpike > TimeSpan.FromSeconds(60))
        {
            _lastConnSpike = rate.Time;
            double mean = _history.Within(TimeSpan.FromMinutes(5), rate.Time).Average(static r => (double)r.Connections);
            AddEvent(new MonitorEvent(rate.Time, "conn", "Mongo.plug-zap", "VelaWarning",
                Loc.Format("Mon_EventConnSpike", Math.Round(connJump * 100)),
                Loc.Format("Mon_EventConnSpikeDetail", BsonText.Grouped(rate.Connections), Math.Round(mean).ToString("N0", CultureInfo.InvariantCulture))));
        }
    }

    private void DetectReplicaEvents(ReplicaSnapshot? before, ReplicaSnapshot after, DateTime now)
    {
        // 复制延迟超阈值:开始时记一条,持续期间更新"持续 N 分钟",回落即结束。
        ReplicaMember? slowest = after.Slowest;
        if (slowest is { LagSeconds: > LagThreshold })
        {
            if (_lagEvent is null || !Events.Contains(_lagEvent))
            {
                _lagEvent = new MonitorEvent(now, "lag", "Mongo.triangle-alert", "VelaWarning",
                    Loc.Format("Mon_EventLag", LagThreshold.ToString("0.#", CultureInfo.InvariantCulture) + " s"), "");
                AddEvent(_lagEvent);
            }
            _lagEvent.Detail = Loc.Format("Mon_EventLagDetail", MonitorFormat.Host(slowest.Name), Duration(now - _lagEvent.Time));
        }
        else
        {
            _lagEvent = null;
        }
        if (before is null)
        {
            return;
        }
        if (before.Primary is { } oldPrimary && after.Primary is { } newPrimary && oldPrimary != newPrimary)
        {
            AddEvent(new MonitorEvent(now, "primary", "Mongo.flag", "VelaInfo", Loc["Mon_EventPrimary"],
                $"{MonitorFormat.Host(oldPrimary)} → {MonitorFormat.Host(newPrimary)}"));
        }
        foreach (ReplicaMember member in after.Members)
        {
            ReplicaMember? old = before.Members.FirstOrDefault(m => m.Name == member.Name);
            if (old is null || old.Healthy == member.Healthy)
            {
                continue;
            }
            AddEvent(member.Healthy
                ? new MonitorEvent(now, "member", "Mongo.circle-check", "VelaStatusConnected", Loc["Mon_EventRecovered"], MonitorFormat.Host(member.Name))
                : new MonitorEvent(now, "member", "Mongo.circle-x", "VelaError", Loc["Mon_EventUnhealthy"], MonitorFormat.Host(member.Name)));
        }
    }

    /// <summary>按时间倒序插入(慢查询事件的时刻可能早于刚加的那条)。</summary>
    private void AddEvent(MonitorEvent e)
    {
        int index = 0;
        while (index < Events.Count && Events[index].Time >= e.Time)
        {
            index++;
        }
        Events.Insert(index, e);
    }

    private void PruneEvents(DateTime now)
    {
        for (int i = Events.Count - 1; i >= 0; i--)
        {
            if (now - Events[i].Time > EventRetention && !ReferenceEquals(Events[i], _lagEvent))
            {
                Events.RemoveAt(i);
            }
        }
    }

    // ── 指标卡与图表 ──────────────────────────────────────────────────────

    private void UpdateKpis(ServerSample s, RateSample? rate)
    {
        Connections.Set(BsonText.Grouped(s.Connections), "/ " + BsonText.Grouped(s.Connections + s.ConnectionsAvailable),
            Loc.Format("Mon_ConnFoot", BsonText.Grouped(s.ConnectionsAvailable)));

        if (rate is null)
        {
            Ops.Set("—", "", Loc["Mon_Warming"]);
            Network.Set("—", "", Loc["Mon_Warming"]);
        }
        else
        {
            Ops.Set(MonitorFormat.Rate(rate.Total), "", Loc.Format("Mon_OpsFoot", MonitorFormat.Rate(rate.Reads), MonitorFormat.Rate(rate.Writes)));
            (double div, string unit) = MonitorFormat.ByteUnit(rate.BytesIn + rate.BytesOut);
            Network.Set(MonitorFormat.One((rate.BytesIn + rate.BytesOut) / div), unit + "/s",
                Loc.Format("Mon_NetFoot", MonitorFormat.One(rate.BytesIn / div), MonitorFormat.One(rate.BytesOut / div)));
        }

        if (s.CacheMax > 0)
        {
            (double div, string unit) = MonitorFormat.ByteUnit(s.CacheUsed);
            (double maxDiv, string maxUnit) = MonitorFormat.ByteUnit(s.CacheMax);
            string max = (s.CacheMax / maxDiv).ToString("0.#", CultureInfo.InvariantCulture);
            string dirty = (100.0 * s.CacheDirty / s.CacheMax).ToString("0.0", CultureInfo.InvariantCulture) + "%";
            Cache.Set(MonitorFormat.One(s.CacheUsed / div), unit == maxUnit ? $"/ {max} {maxUnit}" : $"{unit} / {max} {maxUnit}",
                Loc.Format("Mon_CacheFoot", dirty, rate is null ? "—" : MonitorFormat.Rate(rate.Evictions)));
        }
        else
        {
            Cache.Set("—", "", s.Engine);
        }

        if (!IsReplicaSet)
        {
            Lag.Set("—", "", Workspace.Connection.Server.Role == "MONGOS" ? Loc["Mon_LagMongos"] : Loc["Mon_LagStandalone"]);
            Lag.IsAlert = false;
        }
        else if (_replica?.Slowest is { LagSeconds: { } lag } slowest)
        {
            Lag.Set(lag.ToString("0.0", CultureInfo.InvariantCulture), "s", Loc.Format("Mon_LagFoot", MonitorFormat.Host(slowest.Name)));
            Lag.IsAlert = lag > LagThreshold;
        }
        else
        {
            Lag.Set("—", "", _replica is null ? Loc["Mon_ReplicaDenied"] : Loc["Mon_LagNoSecondary"]);
            Lag.IsAlert = false;
        }
    }

    private TimeSpan WindowSpan => Window switch
    {
        "5m" => TimeSpan.FromMinutes(5),
        "1h" => TimeSpan.FromHours(1),
        "6h" => TimeSpan.FromHours(6),
        _ => TimeSpan.FromMinutes(15)
    };

    private string WindowText => Window switch
    {
        "5m" => Loc.Format("Mon_DurMinutes", 5),
        "1h" => Loc.Format("Mon_DurHoursOnly", 1),
        "6h" => Loc.Format("Mon_DurHoursOnly", 6),
        _ => Loc.Format("Mon_DurMinutes", 15)
    };

    private void UpdateCharts()
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan window = WindowSpan;
        BucketSet set = _history.Buckets(window, Bars, now, Interval);
        IReadOnlyList<RateBucket> buckets = set.Buckets;
        string format = set.BarSeconds < 60 ? "HH:mm:ss" : "HH:mm";
        string[] labels = [.. buckets.Select(b => b.Start.ToLocalTime().ToString(format, CultureInfo.InvariantCulture))];
        if (labels.Length > 0)
        {
            // 最右一根是"正在进行"的那一段(设计稿横轴的「21:09 现在」)。
            labels[^1] += " " + Loc["Mon_Now"];
        }
        OpsLabels = labels;
        OpsSeries =
        [
            new("query", "MongoChart1", [.. buckets.Select(static b => b.Query)]),
            new("insert", "MongoChart2", [.. buckets.Select(static b => b.Insert)]),
            new("update", "MongoChart3", [.. buckets.Select(static b => b.Update)]),
            new("delete", "MongoChart4", [.. buckets.Select(static b => b.Delete)])
        ];
        string bar = BarText(set.BarSeconds);
        OpsSubtitle = set.Covered >= window
            ? Loc.Format("Mon_OpsSubtitle", WindowText, bar)
            : Loc.Format("Mon_OpsSubtitleWarm", Duration(set.Covered), bar);
        RateSample? latest = _history.Latest;
        Legend =
        [
            new("query", "MongoChart1", MonitorFormat.Rate(latest?.Query ?? 0)),
            new("insert", "MongoChart2", MonitorFormat.Rate(latest?.Insert ?? 0)),
            new("update", "MongoChart3", MonitorFormat.Rate(latest?.Update ?? 0)),
            new("delete", "MongoChart4", MonitorFormat.Rate(latest?.Delete ?? 0))
        ];
        ConnLabels = labels;
        ConnSeries = [new(Loc["Mon_ConnSeries"], "MongoChart1", [.. buckets.Select(static b => b.Connections)])];
        OpsMax = MonitorFormat.NiceThirds(buckets.Select(static b => b.Query + b.Insert + b.Update + b.Delete).DefaultIfEmpty(0).Max());
        ConnMax = MonitorFormat.NiceThirds(buckets.Select(static b => b.Connections).DefaultIfEmpty(0).Max());
        long current = _last?.Connections ?? 0;
        long peak = Math.Max(current, _history.Within(window, now).Select(static r => r.Connections).DefaultIfEmpty(0).Max());
        ConnSubtitle = Loc.Format("Mon_ConnSubtitle", BsonText.Grouped(current), BsonText.Grouped(peak));
    }

    private static string BarText(double seconds) => seconds switch
    {
        < 1 => seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s",
        < 60 => Math.Round(seconds).ToString("0", CultureInfo.InvariantCulture) + " s",
        _ => Math.Round(seconds / 60).ToString("0", CultureInfo.InvariantCulture) + " min"
    };

    /// <summary>时长(<c>14 天 6 小时</c>、<c>3 小时 12 分</c>、<c>5 分钟</c>、<c>40 秒</c>)。</summary>
    internal string Duration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            return Loc.Format("Mon_DurDays", (int)span.TotalDays, span.Hours);
        }
        if (span.TotalHours >= 1)
        {
            return Loc.Format("Mon_DurHours", (int)span.TotalHours, span.Minutes);
        }
        if (span.TotalMinutes >= 1)
        {
            return Loc.Format("Mon_DurMinutes", (int)span.TotalMinutes);
        }
        return Loc.Format("Mon_DurSeconds", Math.Max(0, (int)Math.Round(span.TotalSeconds)));
    }

    private void UpdateStatus()
    {
        ServerInfo server = Workspace.Connection.Server;
        string who = server.SetName is { Length: > 0 } set ? set : server.Role;
        string commands = IsReplicaSet ? " · replSetGetStatus" : "";
        StatusText = IsPaused
            ? Loc.Format("Mon_StatusPaused", who, commands)
            : Loc.Format("Mon_Status", who, commands, IntervalOverride is { } o ? o.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) : IntervalSeconds.ToString(CultureInfo.InvariantCulture));
    }

    // ── 导出快照 ──────────────────────────────────────────────────────────

    private async Task ExportAsync()
    {
        if (_lastStatus is null)
        {
            return;
        }
        string name = $"mongo-monitor-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
        string? path = await Workspace.PickSaveFileAsync(Loc["Mon_ExportTitle"], name, [FileKind.Json]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, BuildSnapshot().ToJson(new JsonWriterSettings
            {
                OutputMode = JsonOutputMode.RelaxedExtendedJson,
                Indent = true,
                NewLineChars = "\n"
            })).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc["Mon_Exported"], Detail = path, Kind = ToastKind.Success });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", ex.Message), Kind = ToastKind.Error });
        }
    }

    /// <summary>
    /// 快照:最近一次 serverStatus 与 replSetGetStatus 的原文 + 内存里的整段率值序列 + 事件。
    /// 原文照存不裁剪 —— 拿快照的人多半是要发给 DBA 或附在工单里,缺哪一段都得回头再要。
    /// </summary>
    internal BsonDocument BuildSnapshot()
    {
        ServerInfo server = Workspace.Connection.Server;
        var series = new BsonArray(_history.Rates.Select(static r => new BsonDocument
        {
            { "t", r.Time },
            { "query", r.Query },
            { "insert", r.Insert },
            { "update", r.Update },
            { "delete", r.Delete },
            { "bytesInPerSec", r.BytesIn },
            { "bytesOutPerSec", r.BytesOut },
            { "evictionsPerSec", r.Evictions },
            { "connections", r.Connections }
        }));
        var events = new BsonArray(Events.Select(static e => new BsonDocument
        {
            { "t", e.Time },
            { "kind", e.Kind },
            { "title", e.Title },
            { "detail", e.Detail }
        }));
        return new BsonDocument
        {
            { "capturedAt", DateTime.UtcNow },
            { "connection", Workspace.ConnectionName },
            { "endpoint", Workspace.Connection.Endpoint },
            { "server", new BsonDocument { { "version", server.Version }, { "setName", (BsonValue?)server.SetName ?? BsonNull.Value }, { "role", server.Role } } },
            { "intervalSeconds", Interval.TotalSeconds },
            { "serverStatus", (BsonValue?)_lastStatus ?? BsonNull.Value },
            { "replSetGetStatus", (BsonValue?)_lastReplStatus ?? BsonNull.Value },
            { "series", series },
            { "events", events }
        };
    }
}
