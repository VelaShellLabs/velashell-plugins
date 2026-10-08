using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 数据传输向导(设计稿 21):源与目标 → 选择对象 → 选项 → 执行。
/// <para>
/// 目标只能是**本插件此刻开着的另一条会话**,或用户手填的一条连接串 —— 插件读不到宿主的会话库
/// (那里有凭据,明令不出宿主),见 <see cref="MongoSessionRegistry" />。
/// 对象按顺序一个一个传(状态列一目了然:完成 / 传输中 / 排队),集合内部按批并发写。
/// 执行中可以暂停(停在批边界)、停止(当前对象停下,后面的不再开始)、转入后台(关掉对话框继续跑)。
/// </para>
/// </summary>
internal sealed class TransferWizardViewModel : XferWizardViewModel
{
    private readonly List<(TimeSpan At, long Done)> _rateWindow = [];
    private readonly IReadOnlyList<XferOption> _actions;
    private TransferTarget? _target;
    private TransferTarget? _selectedSession;
    private string _targetDatabase;
    private XferOption _readPreference;
    private string _resultTone = "ok";
    private CancellationTokenSource? _run;
    private XferPauseGate? _gate;
    private DispatcherTimer? _ticker;
    private Stopwatch? _watch;
    private bool _objectsLoaded;
    private string _objectsFor = "";

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">源库。</param>
    public TransferWizardViewModel(IMongoWorkspace workspace, string database)
        : base(workspace, [
            workspace.Loc["Xfer_StepEndpoints"], workspace.Loc["Xfer_StepObjects"], workspace.Loc["Xfer_StepOptions"], workspace.Loc["Xfer_StepRun"]
        ])
    {
        Database = database;
        _targetDatabase = database;
        Title = Loc["Toolbar_Transfer"];
        Subtitle = workspace.ConnectionName;
        _actions =
        [
            new(XferAction.Overwrite, Loc["Xfer_ActOverwrite"]), new(XferAction.Append, Loc["Xfer_ActAppend"]),
            new(XferAction.Create, Loc["Xfer_ActCreate"]), new(XferAction.Skip, Loc["Xfer_ActSkip"])
        ];
        ReadPreferences =
        [
            new("secondaryPreferred", "secondaryPreferred"), new("primary", "primary"), new("primaryPreferred", "primaryPreferred"),
            new("secondary", "secondary"), new("nearest", "nearest")
        ];
        _readPreference = ReadPreferences[0];
        ConnectCommand = new AsyncCommand(ConnectUriAsync, () => !Connecting && Uri.Trim().Length > 0);
        PauseCommand = new RelayCommand(TogglePause, () => IsRunning);
        StopCommand = new AsyncCommand(StopAsync, () => IsRunning);
        CopyLogCommand = new AsyncCommand(() => Workspace.CopyAsync(string.Join(Environment.NewLine, Log.Select(static l => $"{l.Time}  {l.Text}"))));
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        foreach (MongoSessionEntry entry in MongoSessionRegistry.Shared.Snapshot())
        {
            if (!ReferenceEquals(entry.Connection, workspace.Connection))
            {
                Sessions.Add(new TransferTarget(entry.Name, entry.Connection, owned: false));
            }
        }
        UpdateSummaries();
    }

    /// <summary>源库。</summary>
    public string Database { get; }

    /// <inheritdoc />
    public override string IconKey => "Mongo.arrow-left-right";

    // ── 端点卡片 ────────────────────────────────────────────────────────

    /// <summary>源连接名。</summary>
    public string SourceName => Workspace.ConnectionName;

    /// <summary>源:<c>shop · rs0 PRIMARY</c>。</summary>
    public string SourceDetail => $"{Database} · {RoleText(Workspace.Connection)}";

    /// <summary>源环境徽章文字。</summary>
    public string SourceEnvText => EnvText(Workspace.Guard.Environment);

    /// <summary>源环境徽章语气。</summary>
    public string SourceEnvTone => EnvTone(Workspace.Guard.Environment);

    /// <summary>选了目标。</summary>
    public bool HasTarget => _target is not null;

    /// <summary>目标连接名。</summary>
    public string TargetName => _target?.Name ?? Loc["Xfer_NoTarget"];

    /// <summary>目标:<c>shop_staging · 单节点</c>。</summary>
    public string TargetDetail => _target is { } t ? $"{_targetDatabase} · {RoleText(t.Connection)}" : Loc["Xfer_PickTarget"];

    /// <summary>目标环境徽章文字。</summary>
    public string TargetEnvText => _target is { } t ? EnvText(t.Environment) : "";

    /// <summary>目标环境徽章语气。</summary>
    public string TargetEnvTone => _target is { } t ? EnvTone(t.Environment) : "muted";

    /// <summary>箭头下的实时吞吐(<c>18.2k 文档/s</c>)。</summary>
    public string RateText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "—";

    // ── 第一步:目标 ─────────────────────────────────────────────────────

    /// <summary>本插件开着的其他会话。</summary>
    public ObservableCollection<TransferTarget> Sessions { get; } = [];

    /// <summary>有其他会话可选。</summary>
    public bool HasSessions => Sessions.Count > 0;

    /// <summary>选中的会话。</summary>
    public TransferTarget? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (SetProperty(ref _selectedSession, value) && value is not null)
            {
                SetTarget(value);
            }
        }
    }

    /// <summary>手填的连接串。</summary>
    public string Uri
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    } = "";

    /// <summary>连接中。</summary>
    public bool Connecting
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>连接失败的原因。</summary>
    public string ConnectError
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasConnectError));
            }
        }
    } = "";

    /// <summary>连接失败了。</summary>
    public bool HasConnectError => ConnectError.Length > 0;

    /// <summary>用连接串连。</summary>
    public AsyncCommand ConnectCommand { get; }

    /// <summary>目标库名。</summary>
    public string TargetDatabase
    {
        get => _targetDatabase;
        set
        {
            if (SetProperty(ref _targetDatabase, value))
            {
                RaisePropertyChanged(nameof(TargetDetail));
                UpdateSummaries();
            }
        }
    }

    // ── 第二步:对象 ─────────────────────────────────────────────────────

    /// <summary>对象表。</summary>
    public ObservableCollection<TransferObjectRow> Objects { get; } = [];

    /// <summary>全选。</summary>
    public RelayCommand SelectAllCommand { get; }

    /// <summary>全不选。</summary>
    public RelayCommand SelectNoneCommand { get; }

    // ── 第三步:选项 ─────────────────────────────────────────────────────

    /// <summary>保留 _id。</summary>
    public bool KeepId
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = true;

    /// <summary>传输索引。</summary>
    public bool Indexes
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = true;

    /// <summary>传输验证规则。</summary>
    public bool Validation
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = true;

    /// <summary>批大小。</summary>
    public string BatchSize
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = "1000";

    /// <summary>并发。</summary>
    public string Concurrency
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnOptionsChanged();
            }
        }
    } = "4";

    /// <summary>读偏好候选。</summary>
    public IReadOnlyList<XferOption> ReadPreferences { get; }

    /// <summary>源端读偏好。</summary>
    public XferOption ReadPreference
    {
        get => _readPreference;
        set
        {
            if (value is not null && SetProperty(ref _readPreference, value))
            {
                OnOptionsChanged();
            }
        }
    }

    /// <summary>右栏:保留 _id。</summary>
    public string KeepIdText => KeepId ? Loc["Common_Yes"] : Loc["Common_No"];

    /// <summary>右栏:传输索引。</summary>
    public string IndexesText => Indexes ? Loc["Xfer_IndexesYes"] : Loc["Common_No"];

    /// <summary>右栏:传输验证规则。</summary>
    public string ValidationText => Validation ? Loc["Common_Yes"] : Loc["Common_No"];

    /// <summary>右栏:批大小 · 并发。</summary>
    public string BatchText => Loc.Format("Xfer_BatchText", BsonText.Grouped(Batch), Parallel);

    private int Batch => int.TryParse(BatchSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 100_000) : 1000;

    private int Parallel => int.TryParse(Concurrency, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? Math.Clamp(n, 1, 16) : 4;

    // ── 第四步:执行 ─────────────────────────────────────────────────────

    /// <summary>总进度 0–100。</summary>
    public double Overall
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>「908,330 / 1,812,640 文档 · 已用 50 s · 预计剩余 50 s」。</summary>
    public string OverallText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>百分比。</summary>
    public string PercentText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "0%";

    /// <summary>暂停中。</summary>
    public bool Paused
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(PauseText), nameof(PauseIcon));
            }
        }
    }

    /// <summary>暂停 / 继续 按钮的字。</summary>
    public string PauseText => Paused ? Loc["Xfer_Resume"] : Loc["Xfer_Pause"];

    /// <summary>暂停 / 继续 按钮的图标。</summary>
    public string PauseIcon => Paused ? "Mongo.play" : "Mongo.pause";

    /// <summary>结果说明。</summary>
    public string ResultText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ResultOk), nameof(ResultFailed));
            }
        }
    } = "";

    /// <summary>成功。</summary>
    public bool ResultOk => ResultText.Length > 0 && _resultTone == "ok";

    /// <summary>失败 / 停止。</summary>
    public bool ResultFailed => ResultText.Length > 0 && _resultTone != "ok";

    /// <summary>日志。</summary>
    public ObservableCollection<XferLogLine> Log { get; } = [];

    /// <summary>还没有日志。</summary>
    public bool LogEmpty => Log.Count == 0;

    /// <summary>暂停 / 继续。</summary>
    public RelayCommand PauseCommand { get; }

    /// <summary>停止传输。</summary>
    public AsyncCommand StopCommand { get; }

    /// <summary>复制日志。</summary>
    public AsyncCommand CopyLogCommand { get; }

    /// <inheritdoc />
    protected override string StartText
    {
        get
        {
            long docs = Objects.Where(static o => o.IsChecked && o.ActionValue != XferAction.Skip).Sum(static o => o.Total);
            return docs > 0 ? Loc.Format("Xfer_Start", BsonText.Grouped(docs)) : Loc["Xfer_StartPlain"];
        }
    }

    // ── 目标 ────────────────────────────────────────────────────────────

    private void SetTarget(TransferTarget target)
    {
        if (_target is { Owned: true } previous && !ReferenceEquals(previous, target))
        {
            _ = previous.Connection.DisposeAsync().AsTask();
        }
        _target = target;
        ConnectError = "";
        _objectsLoaded = false;
        RaisePropertiesChanged(nameof(HasTarget), nameof(TargetName), nameof(TargetDetail), nameof(TargetEnvText), nameof(TargetEnvTone));
        Subtitle = $"{Workspace.ConnectionName} → {target.Name}";
        UpdateSummaries();
    }

    /// <summary>选一条已开着的会话(单测直接调)。</summary>
    internal void UseTarget(TransferTarget target)
    {
        if (!Sessions.Contains(target))
        {
            Sessions.Add(target);
            RaisePropertyChanged(nameof(HasSessions));
        }
        SelectedSession = target;
    }

    private async Task ConnectUriAsync()
    {
        string uri = Uri.Trim();
        Connecting = true;
        ConnectError = "";
        try
        {
            MongoConnection connection = await MongoConnection.ConnectAsync(uri, CancellationToken.None).ConfigureAwait(true);
            var target = new TransferTarget(MongoConnection.RedactUri(uri), connection, owned: true);
            _selectedSession = null;
            RaisePropertyChanged(nameof(SelectedSession));
            SetTarget(target);
            var url = new MongoUrl(uri);
            if (url.DatabaseName is { Length: > 0 } db)
            {
                TargetDatabase = db;
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or ArgumentException or FormatException)
        {
            ConnectError = MongoConnector.Describe(ex);
        }
        finally
        {
            Connecting = false;
        }
    }

    // ── 对象 ────────────────────────────────────────────────────────────

    private async Task LoadObjectsAsync()
    {
        if (_target is not { } target)
        {
            return;
        }
        string key = $"{target.Endpoint}/{_targetDatabase}";
        if (_objectsLoaded && _objectsFor == key)
        {
            return;
        }
        IsBusy = true;
        try
        {
            IReadOnlyList<CollectionInfo> all = Workspace.CollectionsOf(Database);
            if (all.Count == 0)
            {
                all = await Workspace.Connection.ListCollectionsAsync(Database).ConfigureAwait(true);
            }
            HashSet<string> existing = await CollectionCopier.ExistingNamesAsync(target.Connection, _targetDatabase.Trim(), CancellationToken.None)
                .ConfigureAwait(true);
            IReadOnlyList<GridFsBucketInfo> buckets = MongoConnection.FindBuckets(Database, all);
            var bucketCollections = buckets.SelectMany(static b => new[] { b.FilesCollection, b.ChunksCollection }).ToHashSet(StringComparer.Ordinal);
            var previous = Objects.ToDictionary(static o => $"{o.Kind}:{o.Name}", StringComparer.Ordinal);
            Objects.Clear();
            IEnumerable<CollectionInfo> plain = all.Where(c => c.Kind != CollectionKind.System && !bucketCollections.Contains(c.Name));
            foreach (CollectionInfo info in plain.OrderBy(static c => c.Kind == CollectionKind.View ? 1 : 0))
            {
                XferObjectKind kind = info.Kind == CollectionKind.View ? XferObjectKind.View : XferObjectKind.Collection;
                var row = new TransferObjectRow(info.Name, kind, info, _actions, Loc, OnObjectsChanged);
                Apply(row, existing.Contains(info.Name), previous);
                if (kind == XferObjectKind.Collection)
                {
                    row.Total = await Workspace.Connection.EstimatedCountAsync(Database, info.Name).ConfigureAwait(true) ?? 0;
                }
                Objects.Add(row);
            }
            foreach (GridFsBucketInfo bucket in buckets)
            {
                var row = new TransferObjectRow(bucket.Name, XferObjectKind.Bucket, null, _actions, Loc, OnObjectsChanged);
                Apply(row, existing.Contains(bucket.FilesCollection) || existing.Contains(bucket.ChunksCollection), previous);
                long files = await Workspace.Connection.EstimatedCountAsync(Database, bucket.FilesCollection).ConfigureAwait(true) ?? 0;
                long chunks = await Workspace.Connection.EstimatedCountAsync(Database, bucket.ChunksCollection).ConfigureAwait(true) ?? 0;
                row.Files = files;
                row.Bytes = (await Workspace.Connection.GetStatsAsync(Database, bucket.ChunksCollection).ConfigureAwait(true)).Size;
                row.Total = files + chunks;
                Objects.Add(row);
            }
            _objectsLoaded = true;
            _objectsFor = key;
            OnObjectsChanged();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>目标已有同名对象时默认「跳过」(灰字说明),否则默认「新建」;用户改过的保留。</summary>
    private void Apply(TransferObjectRow row, bool exists, Dictionary<string, TransferObjectRow> previous)
    {
        row.ExistsOnTarget = exists;
        if (previous.TryGetValue($"{row.Kind}:{row.Name}", out TransferObjectRow? old) && old.ExistsOnTarget == exists)
        {
            row.IsChecked = old.IsChecked;
            row.Action = old.Action;
            return;
        }
        row.Action = _actions.First(a => (XferAction)a.Value == (exists ? XferAction.Skip : XferAction.Create));
    }

    private void SetAll(bool value)
    {
        foreach (TransferObjectRow row in Objects)
        {
            row.IsChecked = value;
        }
    }

    private void OnObjectsChanged()
    {
        UpdateSummaries();
        RaiseStartTextChanged();
    }

    private void OnOptionsChanged()
    {
        RaisePropertiesChanged(nameof(KeepIdText), nameof(IndexesText), nameof(ValidationText), nameof(BatchText));
        UpdateSummaries();
    }

    private void UpdateSummaries()
    {
        Steps[0].Summary = _target is null ? "" : $"{Database} → {_targetDatabase.Trim()}";
        int collections = Objects.Count(static o => o.IsChecked && o.Kind != XferObjectKind.Bucket);
        int buckets = Objects.Count(static o => o.IsChecked && o.Kind == XferObjectKind.Bucket);
        Steps[1].Summary = Objects.Count == 0 ? "" : buckets > 0
            ? Loc.Format("Xfer_ObjectsSummaryBuckets", collections, buckets)
            : Loc.Format("Xfer_ObjectsSummary", collections);
        var parts = new List<string>();
        if (KeepId)
        {
            parts.Add(Loc["Xfer_KeepIdShort"]);
        }
        if (Indexes)
        {
            parts.Add(Loc["Xfer_IndexesShort"]);
        }
        Steps[2].Summary = string.Join(" · ", parts);
    }

    // ── 校验与执行 ─────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override async Task<bool> ValidateAsync(int step)
    {
        switch (step)
        {
            case 0:
                if (_target is null)
                {
                    Workspace.Toast(new() { Title = Loc["Xfer_NeedTarget"], Kind = ToastKind.Warning });
                    return false;
                }
                if (_targetDatabase.Trim().Length == 0 || _targetDatabase.IndexOfAny(['/', '\\', '.', ' ', '"', '$']) >= 0)
                {
                    Workspace.Toast(new() { Title = Loc["Xfer_BadDatabase"], Kind = ToastKind.Warning });
                    return false;
                }
                if (ReferenceEquals(_target.Connection, Workspace.Connection) && _targetDatabase.Trim() == Database)
                {
                    Workspace.Toast(new() { Title = Loc["Xfer_SameTarget"], Kind = ToastKind.Warning });
                    return false;
                }
                return true;
            case 1:
                if (!Objects.Any(static o => o.IsChecked && o.ActionValue != XferAction.Skip))
                {
                    Workspace.Toast(new() { Title = Loc["Xfer_NothingToDo"], Kind = ToastKind.Warning });
                    return false;
                }
                return true;
            default:
                await Task.CompletedTask.ConfigureAwait(true);
                return true;
        }
    }

    /// <inheritdoc />
    protected override async Task OnEnteredAsync(int step)
    {
        if (step == 1)
        {
            await LoadObjectsAsync().ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    protected override async Task StartAsync()
    {
        if (_target is not { } target)
        {
            return;
        }
        string targetDb = _targetDatabase.Trim();
        TransferObjectRow[] rows = [.. Objects.Where(static o => o.IsChecked)];
        long total = rows.Where(static r => r.ActionValue != XferAction.Skip).Sum(static r => r.Total);
        if (!target.Connection.Privileges.CanWrite(targetDb))
        {
            Workspace.Toast(new() { Title = Loc["Common_NoPrivilege"], Kind = ToastKind.Warning });
            return;
        }
        bool overwrites = rows.Any(static r => r.ActionValue == XferAction.Overwrite);
        if (target.Environment == MongoEnvironment.Production || overwrites)
        {
            bool production = target.Environment == MongoEnvironment.Production;
            if (!await Workspace.ConfirmAsync(new()
            {
                Title = production ? Loc["Xfer_ConfirmProdTitle"] : Loc["Xfer_ConfirmOverwriteTitle"],
                Message = Loc.Format(production ? "Xfer_ConfirmProdBody" : "Xfer_ConfirmOverwriteBody", $"{target.Name} / {targetDb}",
                        string.Join(", ", rows.Where(static r => r.ActionValue == XferAction.Overwrite).Select(static r => r.Name))),
                ConfirmLabel = Loc["Xfer_StartPlain"],
                IconKey = "Mongo.arrow-left-right",
                TypeToConfirm = production ? targetDb : null,
                Facts =
                    [
                        new(Loc["Xfer_FactTarget"], $"{target.Name} / {targetDb}"),
                        new(Loc["Xfer_FactObjects"], rows.Count(static r => r.ActionValue != XferAction.Skip).ToString(CultureInfo.InvariantCulture)),
                        new(Loc["Xfer_FactDocs"], BsonText.Grouped(total))
                    ]
            }).ConfigureAwait(true))
            {
                return;
            }
        }

        EnterRunStep();
        Log.Clear();
        RaisePropertyChanged(nameof(LogEmpty));
        ResultText = "";
        foreach (TransferObjectRow row in Objects)
        {
            row.Running = true;
            row.Done = 0;
            row.State = row.IsChecked ? TransferObjectState.Queued : TransferObjectState.Skipped;
        }
        PauseCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        _run = new CancellationTokenSource();
        _gate = new XferPauseGate();
        Paused = false;
        _watch = Stopwatch.StartNew();
        _rateWindow.Clear();
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ticker.Tick += (_, _) => Tick(total);
        _ticker.Start();
        Steps[RunStep].Summary = Loc["Xfer_Running"];
        CancellationToken token = _run.Token;
        var options = new XferOptions
        {
            KeepId = KeepId,
            Indexes = Indexes,
            Validation = Validation,
            BatchSize = Batch,
            Concurrency = Parallel,
            ReadPreference = (string)_readPreference.Value
        };
        AddLog(Loc.Format("Xfer_LogSource", SourceName, Workspace.Connection.Endpoint, (string)_readPreference.Value), XferTone.Normal);
        AddLog(Loc.Format("Xfer_LogTarget", target.Name, targetDb), XferTone.Normal);
        int done = 0, failed = 0;
        bool stopped = false;
        foreach (TransferObjectRow row in rows)
        {
            if (token.IsCancellationRequested)
            {
                row.State = TransferObjectState.Stopped;
                stopped = true;
                continue;
            }
            if (row.ActionValue == XferAction.Skip)
            {
                row.State = TransferObjectState.Skipped;
                AddLog(Loc.Format(row.ExistsOnTarget ? "Xfer_LogSkippedExists" : "Xfer_LogSkipped", row.DisplayName), XferTone.Muted);
                continue;
            }
            row.State = TransferObjectState.Running;
            if (row.ActionValue == XferAction.Overwrite)
            {
                AddLog(Loc.Format("Xfer_LogOverwrite", row.DisplayName), XferTone.Warn);
            }
            AddLog(Loc.Format("Xfer_LogBegin", row.DisplayName, BsonText.Grouped(row.Total)), XferTone.Normal);
            try
            {
                XferItem item = row.ToItem();
                XferOutcome outcome = await Task.Run(() => CollectionCopier.CopyAsync(
                    Workspace.Connection, Database, target.Connection, targetDb, item, options, _gate,
                    row.Add, (text, tone) => AddLog(text, tone), Loc, token), token).ConfigureAwait(true);
                row.Done = outcome.Copied;
                row.State = TransferObjectState.Done;
                done++;
                string tail = row.Kind == XferObjectKind.View
                    ? Loc["Xfer_LogViewDone"]
                    : Loc.Format("Xfer_LogDone", BsonText.Grouped(outcome.Copied), outcome.Indexes);
                AddLog($"{row.DisplayName} {tail}" + (row.ActionValue == XferAction.Append ? Loc["Xfer_LogAppendSuffix"] : ""), XferTone.Ok);
                if (outcome.Failed > 0)
                {
                    AddLog(Loc.Format("Xfer_LogFailedDocs", row.DisplayName, BsonText.Grouped(outcome.Failed)), XferTone.Warn);
                }
            }
            catch (OperationCanceledException)
            {
                row.State = TransferObjectState.Stopped;
                stopped = true;
                AddLog(Loc.Format("Xfer_LogStopped", row.DisplayName, BsonText.Grouped(row.Done)), XferTone.Warn);
            }
            catch (Exception ex)
            {
                // 一个对象失败不拖垮整次传输:记下原因,接着传下一个。
                Workspace.Log.Error($"Transferring {row.Name} failed.", ex);
                row.State = TransferObjectState.Failed;
                failed++;
                AddLog($"{row.DisplayName}: {MongoConnector.Describe(ex)}", XferTone.Error);
            }
        }
        _ticker.Stop();
        Tick(total);
        TimeSpan elapsed = _watch.Elapsed;
        long copied = rows.Sum(static r => r.Done);
        if (stopped)
        {
            _resultTone = "warn";
            ResultText = Loc.Format("Xfer_ResultStopped", done, BsonText.Grouped(copied));
            Steps[RunStep].Summary = Loc["Xfer_Cancelled"];
        }
        else if (failed > 0)
        {
            _resultTone = "err";
            ResultText = Loc.Format("Xfer_ResultFailed", done, failed);
            Steps[RunStep].Summary = Loc["Xfer_Failed"];
        }
        else
        {
            _resultTone = "ok";
            Overall = 100;
            PercentText = "100%";
            ResultText = Loc.Format("Xfer_ResultDone", done, BsonText.Grouped(copied), TransferText.Duration(elapsed));
            Steps[RunStep].Summary = Loc["Xfer_Completed"];
        }
        RaisePropertiesChanged(nameof(ResultOk), nameof(ResultFailed));
        AddLog(ResultText, stopped ? XferTone.Warn : failed > 0 ? XferTone.Error : XferTone.Ok);
        _run.Dispose();
        _run = null;
        Paused = false;
        FinishRun(new ToastRequest
        {
            Title = ResultText,
            Detail = $"{SourceName}/{Database} → {target.Name}/{targetDb}",
            Kind = stopped ? ToastKind.Warning : failed > 0 ? ToastKind.Error : ToastKind.Success,
            Duration = TimeSpan.FromSeconds(8)
        });
        PauseCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        if (IsBackground && target.Owned)
        {
            // 对话框早就关了(转入后台):手填连接串开的目标连接没人再用,现在释放。
            await target.Connection.DisposeAsync().ConfigureAwait(true);
            _target = null;
        }
    }

    private void Tick(long total)
    {
        if (_watch is null)
        {
            return;
        }
        long copied = Objects.Where(static o => o.IsChecked && o.ActionValue != XferAction.Skip).Sum(static o => o.Done);
        TimeSpan now = _watch.Elapsed;
        _rateWindow.Add((now, copied));
        _ = _rateWindow.RemoveAll(s => now - s.At > TimeSpan.FromSeconds(5));
        double rate = 0;
        if (_rateWindow.Count >= 2)
        {
            (TimeSpan at0, long done0) = _rateWindow[0];
            double seconds = (now - at0).TotalSeconds;
            rate = seconds > 0 ? (copied - done0) / seconds : 0;
        }
        if (rate <= 0 && now.TotalSeconds > 0.5)
        {
            rate = copied / now.TotalSeconds;
        }
        RateText = rate > 0 ? Loc.Format("Xfer_Rate", TransferText.Rate(rate)) : "—";
        double fraction = total > 0 ? Math.Min(1, (double)copied / total) : 0;
        Overall = fraction * 100;
        PercentText = (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        string remaining = rate > 0 && total > copied ? TransferText.Duration(TimeSpan.FromSeconds((total - copied) / rate)) : "—";
        OverallText = Loc.Format("Xfer_OverallLine", BsonText.Grouped(copied), BsonText.Grouped(total), TransferText.Duration(now), remaining);
        if (IsRunning)
        {
            Steps[RunStep].Summary = Loc.Format("Xfer_RunningPercent", PercentText);
        }
    }

    private void TogglePause()
    {
        if (_gate is null)
        {
            return;
        }
        if (_gate.IsPaused)
        {
            _gate.Resume();
            Paused = false;
            AddLog(Loc["Xfer_LogResumed"], XferTone.Normal);
        }
        else
        {
            _gate.Pause();
            Paused = true;
            AddLog(Loc["Xfer_LogPaused"], XferTone.Warn);
        }
    }

    private async Task StopAsync()
    {
        if (_run is null)
        {
            return;
        }
        if (!await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Xfer_StopTitle"],
            Message = Loc["Xfer_StopBody"],
            ConfirmLabel = Loc["Xfer_Stop"],
            IconKey = "Mongo.octagon-x"
        }).ConfigureAwait(true))
        {
            return;
        }
        _run?.Cancel();
        _gate?.Resume();
    }

    private void AddLog(string text, XferTone tone) => OnUi(() =>
    {
        Log.Add(new XferLogLine(Now(), text, tone));
        RaisePropertyChanged(nameof(LogEmpty));
    });

    /// <summary>副本集 / 角色(<c>rs0 PRIMARY</c>;单机写「单节点」)。</summary>
    private string RoleText(MongoConnection connection)
    {
        ServerInfo server = connection.Server;
        return server.SetName is { Length: > 0 } set ? $"{set} {server.Role}" : server.Role == "MONGOS" ? "mongos" : Loc["Xfer_Standalone"];
    }

    private string EnvText(MongoEnvironment environment) => environment switch
    {
        MongoEnvironment.Production => Loc["Mongo_EnvProduction"],
        MongoEnvironment.Testing => Loc["Mongo_EnvTesting"],
        _ => Loc["Mongo_EnvDevelopment"]
    };

    private static string EnvTone(MongoEnvironment environment) => environment switch
    {
        MongoEnvironment.Production => "err",
        MongoEnvironment.Testing => "info",
        _ => "muted"
    };

    /// <inheritdoc />
    internal override void OnClosed()
    {
        // 手填连接串开的目标连接:向导关掉且没有任务在跑时就释放(跑着的话等跑完、对话框再关时释放)。
        if (!IsRunning && _target is { Owned: true } owned)
        {
            _ = owned.Connection.DisposeAsync().AsTask();
            _target = null;
        }
        base.OnClosed();
    }
}
