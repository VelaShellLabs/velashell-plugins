using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合设计 · 索引与建议(设计稿 07)。</summary>
internal sealed partial class DesignTabViewModel
{
    private IndexRow? _selectedIndex;
    private long _totalIndexSize;
    private int _readyIndexCount;
    private string _indexStatsSince = "";
    private Task? _indexesTask;
    private bool _hasAdvice;
    private string _adviceEmpty = "";
    private CancellationTokenSource? _buildPoll;
    private CollectionStats? _indexStats;

    /// <summary>索引表。</summary>
    public ObservableCollection<IndexRow> Indexes { get; } = [];

    /// <summary>索引建议卡片。</summary>
    public ObservableCollection<AdviceCard> Advice { get; } = [];

    /// <summary>选中的索引(隐藏 / 删除作用的对象)。</summary>
    public IndexRow? SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value))
            {
                RaisePropertiesChanged(nameof(CanHideSelected), nameof(CanDropSelected), nameof(HideLabel), nameof(HideIcon));
                ToggleHiddenCommand.RaiseCanExecuteChanged();
                DropIndexCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>已建好的索引个数(子页条上的计数)。</summary>
    public int ReadyIndexCount
    {
        get => _readyIndexCount;
        private set => SetProperty(ref _readyIndexCount, value);
    }

    /// <summary>工具行右侧:<c>$indexStats · 自 2026-09-12 起统计</c>。</summary>
    public string IndexStatsSince
    {
        get => _indexStatsSince;
        private set => SetProperty(ref _indexStatsSince, value);
    }

    /// <summary>有建议卡片。</summary>
    public bool HasAdvice
    {
        get => _hasAdvice;
        private set => SetProperty(ref _hasAdvice, value);
    }

    /// <summary>没有建议时的那行说明。</summary>
    public string AdviceEmpty
    {
        get => _adviceEmpty;
        private set
        {
            if (SetProperty(ref _adviceEmpty, value))
            {
                RaisePropertyChanged(nameof(HasAdviceEmpty));
            }
        }
    }

    /// <summary>显示空态说明。</summary>
    public bool HasAdviceEmpty => _adviceEmpty.Length > 0;

    /// <summary>选中的索引能隐藏 / 取消隐藏(<c>_id_</c> 与构建中的不行)。</summary>
    public bool CanHideSelected => _selectedIndex is { IsId: false, IsBuilding: false };

    /// <summary>选中的索引能删(<c>_id_</c> 不行)。</summary>
    public bool CanDropSelected => _selectedIndex is { IsId: false };

    /// <summary>隐藏按钮的字:选中的已隐藏就是「取消隐藏」。</summary>
    public string HideLabel => _selectedIndex?.IsHidden == true ? Loc["Design_Unhide"] : Loc["Design_Hide"];

    /// <summary>隐藏按钮的图标。</summary>
    public string HideIcon => _selectedIndex?.IsHidden == true ? "Mongo.eye" : "Mongo.eye-off";

    /// <summary>隐藏 / 取消隐藏选中的索引。</summary>
    public AsyncCommand ToggleHiddenCommand { get; private set; } = null!;

    /// <summary>删除选中的索引。</summary>
    public AsyncCommand DropIndexCommand { get; private set; } = null!;

    /// <summary>刷新索引页。</summary>
    public AsyncCommand RefreshIndexesCommand { get; private set; } = null!;

    /// <summary>建议卡片上的按钮。</summary>
    public AsyncCommand<AdviceCard> AdviceActionCommand { get; private set; } = null!;

    private void InitializeIndexes()
    {
        ToggleHiddenCommand = new(() => _selectedIndex is { } row ? SetHiddenAsync(row, !row.IsHidden) : Task.CompletedTask, () => CanHideSelected);
        DropIndexCommand = new(() => _selectedIndex is { } row ? DropIndexAsync(row) : Task.CompletedTask, () => CanDropSelected);
        RefreshIndexesCommand = new(LoadIndexesAsync);
        AdviceActionCommand = new(static card => card.Action());
    }

    /// <summary>
    /// 加载索引页:<c>listIndexes</c>(含构建中的)、<c>$collStats</c> 的 indexSizes、<c>$indexStats</c>、
    /// 多键探测(执行计划里的 <c>isMultiKey</c>)、<c>currentOp</c> 里的构建进度,最后是 profile 慢查询 → 建议。
    /// 每一步失败都只让那一列空着,不让整页失败 —— 权限不够看 <c>$indexStats</c> 的用户照样要能看索引表。
    /// </summary>
    internal async Task LoadIndexesAsync()
    {
        // 已有一次在途:等它做完再重读一次 —— 调用方(建完索引、进选项页)要的是"此刻之后"的状态,
        // 直接返回那次在途的结果可能早于它刚做的改动。
        while (_indexesTask is { IsCompleted: false } running)
        {
            await running.ConfigureAwait(true);
        }
        if (_disposed)
        {
            return;
        }
        _indexesTask = LoadIndexesCoreAsync();
        await _indexesTask.ConfigureAwait(true);
    }

    /// <summary>没加载过才加载;正在加载就等那一次(打开标签时 LoadAsync 与切页会同时要索引,不必读两遍)。</summary>
    internal async Task EnsureIndexesAsync()
    {
        if (_indexesTask is { IsCompleted: false } running)
        {
            await running.ConfigureAwait(true);
        }
        else if (!_indexesLoaded)
        {
            await LoadIndexesAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadIndexesCoreAsync()
    {
        try
        {
            MongoConnection conn = Workspace.Connection;
            IReadOnlyList<(BsonDocument Spec, bool Building)> specs = await ListIndexSpecsAsync().ConfigureAwait(true);
            CollectionStats stats = await SafeAsync(() => conn.GetStatsAsync(Database, CollectionName, Lifetime), new CollectionStats()).ConfigureAwait(true);
            _indexStats = stats;
            Dictionary<string, (long Ops, DateTime Since)> usage = await LoadIndexUsageAsync().ConfigureAwait(true);
            Dictionary<string, double> progress = await LoadBuildProgressAsync().ConfigureAwait(true);
            var multiKey = new HashSet<string>(StringComparer.Ordinal);
            foreach ((BsonDocument spec, bool building) in specs)
            {
                if (!building && await IsMultiKeyAsync(spec).ConfigureAwait(true))
                {
                    multiKey.Add(spec.GetValue("name", "").ToString()!);
                }
            }

            DateTime now = Now();
            long maxOps = usage.Count == 0 ? 0 : usage.Values.Max(static u => u.Ops);
            var rows = new List<IndexRow>();
            foreach ((BsonDocument spec, bool building) in specs)
            {
                string name = spec.GetValue("name", "").ToString()!;
                bool hasUsage = usage.TryGetValue(name, out var u);
                bool unused = hasUsage && u.Ops == 0 && name != "_id_" && !building
                              && (now - u.Since).TotalDays >= IndexAdvisor.UnusedWindowDays;
                IndexForm form = IndexAdvisor.ShapeOf(spec, multiKey.Contains(name));
                long size = stats.IndexSizes.GetValueOrDefault(name);
                var row = new IndexRow
                {
                    Name = name,
                    Spec = spec,
                    Keys = [.. IndexAdvisor.DisplayKeys(spec).Select(static k => new IndexKeyChip(k.Field, k.Value))],
                    Form = form,
                    TypeText = FormText(form),
                    Attributes = Attributes(spec, unused ? (int)(now - u.Since).TotalDays : 0),
                    Size = size,
                    SizeText = size > 0 ? BsonText.Bytes(size) : "—",
                    Ops = hasUsage ? u.Ops : null,
                    UsageRatio = hasUsage && maxOps > 0 ? (double)u.Ops / maxOps : 0,
                    Since = hasUsage ? u.Since : null,
                    MultiKey = multiKey.Contains(name),
                    IsUnused = unused,
                    IsBuilding = building || progress.ContainsKey(name)
                };
                if (row.IsBuilding)
                {
                    ApplyProgress(row, progress.GetValueOrDefault(name));
                }
                rows.Add(row);
            }

            string? selected = _selectedIndex?.Name;
            Indexes.Clear();
            foreach (IndexRow row in rows)
            {
                Indexes.Add(row);
            }
            SelectedIndex = Indexes.FirstOrDefault(r => r.Name == selected);
            ReadyIndexCount = rows.Count(static r => !r.IsBuilding);
            _totalIndexSize = stats.TotalIndexSize > 0 ? stats.TotalIndexSize : rows.Sum(static r => r.Size);
            IndexStatsSince = usage.Count == 0
                ? Loc["Design_IndexStatsUnavailable"]
                : Loc.Format("Design_IndexStatsSince", usage.Values.Min(static v => v.Since).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            _indexesLoaded = true;
            UpdateStatus();
            RefreshTtlRows();
            if (rows.Any(static r => r.IsBuilding))
            {
                StartBuildPoll();
            }
            await LoadAdviceAsync(rows).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// 列索引。7.0 起 <c>includeIndexBuildInfo</c> 能把构建中的索引一起列出来(带 <c>indexBuildInfo</c>);
    /// 老版本不认这个参数,退回普通的 <c>listIndexes</c>。
    /// </summary>
    private async Task<IReadOnlyList<(BsonDocument Spec, bool Building)>> ListIndexSpecsAsync()
    {
        if (IsView)
        {
            return [];
        }
        try
        {
            BsonDocument reply = await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "listIndexes", CollectionName },
                { "includeIndexBuildInfo", true }
            }, Lifetime).ConfigureAwait(true);
            var list = new List<(BsonDocument, bool)>();
            foreach (BsonDocument item in reply["cursor"]["firstBatch"].AsBsonArray.OfType<BsonDocument>())
            {
                if (item.GetValue("spec", BsonNull.Value) is BsonDocument spec)
                {
                    list.Add((spec, item.Contains("indexBuildInfo")));
                }
                else
                {
                    list.Add((item, false));
                }
            }
            return list;
        }
        catch (MongoCommandException)
        {
            IReadOnlyList<BsonDocument> plain = await Workspace.Connection.ListIndexesAsync(Database, CollectionName, Lifetime).ConfigureAwait(true);
            return [.. plain.Select(static s => (s, false))];
        }
    }

    /// <summary><c>$indexStats</c>:每个索引的 ops 与统计起点。没权限 / 老版本返回空。</summary>
    private async Task<Dictionary<string, (long Ops, DateTime Since)>> LoadIndexUsageAsync()
    {
        var map = new Dictionary<string, (long, DateTime)>(StringComparer.Ordinal);
        if (IsView)
        {
            return map;
        }
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await Workspace.Connection.Collection(Database, CollectionName)
                .AggregateAsync<BsonDocument>(new[] { new BsonDocument("$indexStats", new BsonDocument()) }, cancellationToken: Lifetime)
                .ConfigureAwait(true);
            foreach (BsonDocument stat in await cursor.ToListAsync(Lifetime).ConfigureAwait(true))
            {
                if (stat.GetValue("accesses", BsonNull.Value) is BsonDocument accesses)
                {
                    long ops = accesses.GetValue("ops", 0L).ToInt64();
                    DateTime since = accesses.GetValue("since", BsonNull.Value) is BsonDateTime d ? d.ToUniversalTime() : Now();
                    map[stat["name"].AsString] = (ops, since);
                }
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"$indexStats on {Namespace} failed: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// <c>currentOp</c> 里本集合正在进行的索引构建 → 索引名 → 进度(0–100)。
    /// 构建线程的那条操作带 <c>progress.done / total</c>;要索引名得看 <c>command.indexes[].name</c>。
    /// </summary>
    private async Task<Dictionary<string, double>> LoadBuildProgressAsync()
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (IsView)
        {
            return map;
        }
        try
        {
            BsonDocument reply = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument
            {
                { "currentOp", true },
                { "command.createIndexes", CollectionName }
            }, Lifetime).ConfigureAwait(true);
            foreach (BsonDocument op in reply.GetValue("inprog", new BsonArray()).AsBsonArray.OfType<BsonDocument>())
            {
                // 同名集合可能在别的库里也在建索引:按 ns 或命令里的 $db 认库(查询条件里写不了 $ 开头的路径)。
                string ns = op.GetValue("ns", "").ToString() ?? "";
                string db = (op.GetValue("command", BsonNull.Value) as BsonDocument)?.GetValue("$db", "").ToString() ?? "";
                if (ns != Namespace && db != Database && !ns.StartsWith(Database + ".", StringComparison.Ordinal))
                {
                    continue;
                }
                double percent = 0;
                if (op.GetValue("progress", BsonNull.Value) is BsonDocument p && p.GetValue("total", 0).ToDouble() > 0)
                {
                    percent = p.GetValue("done", 0).ToDouble() * 100 / p["total"].ToDouble();
                }
                if (op.GetValue("command", BsonNull.Value) is BsonDocument command
                    && command.GetValue("indexes", BsonNull.Value) is BsonArray indexes)
                {
                    foreach (BsonDocument index in indexes.OfType<BsonDocument>())
                    {
                        string name = index.GetValue("name", "").ToString()!;
                        map[name] = Math.Max(map.GetValueOrDefault(name), percent);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"currentOp for index builds failed: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// 多键:键模式看不出来,要看执行计划。用 <c>hint</c> 指定这个索引做一次 <c>queryPlanner</c> 级的 explain
    /// (不执行查询),读 IXSCAN 阶段的 <c>isMultiKey</c>。文本 / 隐藏 / 通配符索引不能这样 hint,跳过。
    /// </summary>
    private async Task<bool> IsMultiKeyAsync(BsonDocument spec)
    {
        IndexForm form = IndexAdvisor.ShapeOf(spec);
        if (form is IndexForm.Text or IndexForm.Wildcard or IndexForm.Geo || spec.GetValue("hidden", false).ToBoolean())
        {
            return false;
        }
        try
        {
            var find = new BsonDocument
            {
                { "find", CollectionName },
                { "filter", spec.GetValue("partialFilterExpression", new BsonDocument()) },
                { "hint", spec["name"] },
                { "limit", 1 }
            };
            BsonDocument reply = await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "explain", find },
                { "verbosity", "queryPlanner" }
            }, Lifetime).ConfigureAwait(true);
            return FindMultiKey(reply.GetValue("queryPlanner", new BsonDocument()).AsBsonDocument.GetValue("winningPlan", BsonNull.Value));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            return false;
        }
    }

    private static bool FindMultiKey(BsonValue plan)
    {
        if (plan is not BsonDocument doc)
        {
            return false;
        }
        if (doc.GetValue("isMultiKey", false).ToBoolean())
        {
            return true;
        }
        foreach (string child in new[] { "inputStage", "queryPlan" })
        {
            if (FindMultiKey(doc.GetValue(child, BsonNull.Value)))
            {
                return true;
            }
        }
        return doc.GetValue("inputStages", BsonNull.Value) is BsonArray stages && stages.Any(FindMultiKey);
    }

    /// <summary>
    /// 建议:读近 7 天的 <c>system.profile</c>(只取本集合),交给 <see cref="IndexAdvisor" />。
    /// profiler 没开(或没权限读)时给一张"去开慢查询"的提示卡,而不是空着让人以为"没问题"。
    /// </summary>
    private async Task LoadAdviceAsync(IReadOnlyList<IndexRow> rows)
    {
        var profile = new List<BsonDocument>();
        int? level = null;
        bool profileReadable = true;
        if (!IsView)
        {
            try
            {
                BsonDocument status = await Workspace.Connection.RunCommandAsync(Database, new BsonDocument("profile", -1), Lifetime).ConfigureAwait(true);
                level = status.GetValue("was", 0).ToInt32();
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                Workspace.Log.Info($"profile status on {Database} failed: {ex.Message}");
            }
            try
            {
                var filter = new BsonDocument
                {
                    { "ns", Namespace },
                    { "ts", new BsonDocument("$gte", Now().AddDays(-7)) }
                };
                profile = await Workspace.Connection.Collection(Database, "system.profile")
                    .Find(filter)
                    .Sort(new BsonDocument("ts", -1))
                    .Limit(2000)
                    .ToListAsync(Lifetime).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                profileReadable = false;
                Workspace.Log.Info($"Reading system.profile on {Database} failed: {ex.Message}");
            }
        }

        var usages = rows.Where(static r => !r.IsBuilding)
            .Select(static r => new AdvisorIndex(r.Name, r.Key, r.Spec, r.Ops, r.Since, r.Size))
            .ToList();
        IReadOnlyList<AdvisorQueryGroup> groups = IndexAdvisor.GroupSlowQueries(profile);
        IReadOnlyList<AdvisorSuggestion> suggestions = IndexAdvisor.Advise(groups, usages, Now());

        Advice.Clear();
        foreach (AdvisorSuggestion suggestion in suggestions)
        {
            Advice.Add(Card(suggestion));
        }
        if (!IsView && (level == 0 || !profileReadable) && profile.Count == 0)
        {
            Advice.Add(new AdviceCard
            {
                IconKey = "Mongo.info",
                IconToken = "VelaInfo",
                Title = Loc["Design_AdviceProfilerOffTitle"],
                Detail = profileReadable ? Loc["Design_AdviceProfilerOffDetail"] : Loc["Design_AdviceProfilerDenied"],
                ActionLabel = Loc["Design_AdviceOpenProfiler"],
                Action = () =>
                {
                    Workspace.OpenProfiler(Database);
                    return Task.CompletedTask;
                }
            });
        }
        HasAdvice = Advice.Count > 0;
        AdviceEmpty = HasAdvice || IsView ? "" : Loc["Design_AdviceNone"];
    }

    /// <summary>一条建议 → 卡片(标题、说明、代码块、按钮)。</summary>
    private AdviceCard Card(AdvisorSuggestion s)
    {
        switch (s.Kind)
        {
            case AdvisorKind.CreateIndex:
            {
                AdvisorQueryGroup q = s.Queries!;
                return new AdviceCard
                {
                    Suggestion = s,
                    IconKey = "Mongo.zap",
                    IconToken = "VelaWarning",
                    Title = Loc.Format("Design_AdviceCreateTitle", FieldSet(q.Shape.Fields), PlanLabel(q)),
                    Detail = Loc.Format("Design_AdviceCreateDetail", BsonText.Grouped(q.Count), Math.Round(q.AverageMillis),
                        BsonText.Count((long)q.AverageExamined), BsonText.Count((long)Math.Round(q.AverageReturned))),
                    Code = BsonText.Literal(s.Key!),
                    ActionLabel = Loc["Design_AdviceCreate"],
                    IsPrimary = true,
                    Action = () =>
                    {
                        OpenCreatePanel(s.Key!, q.Shape);
                        return Task.CompletedTask;
                    }
                };
            }
            case AdvisorKind.ExtendIndex:
            {
                AdvisorQueryGroup q = s.Queries!;
                return new AdviceCard
                {
                    Suggestion = s,
                    IconKey = "Mongo.copy-minus",
                    IconToken = "VelaTextTertiary",
                    Title = Loc.Format("Design_AdviceExtendTitle", s.Index!.Name),
                    Detail = Loc.Format("Design_AdviceExtendDetail", BsonText.Grouped(q.Count),
                        q.InMemorySort ? Loc["Design_AdviceInMemorySort"] : Loc["Design_AdviceOverScan"], BsonText.Literal(s.Key!)),
                    ActionLabel = Loc["Design_AdviceViewQuery"],
                    Action = () =>
                    {
                        Workspace.OpenQuery(Database, QueryText(q));
                        return Task.CompletedTask;
                    }
                };
            }
            case AdvisorKind.RedundantIndex:
                return new AdviceCard
                {
                    Suggestion = s,
                    IconKey = "Mongo.copy-minus",
                    IconToken = "VelaTextTertiary",
                    Title = Loc.Format("Design_AdviceRedundantTitle", s.Index!.Name, s.Other!.Name),
                    Detail = Loc.Format("Design_AdviceRedundantDetail", s.Other.Name, s.Index.Name, BsonText.Bytes(s.Index.Size)),
                    ActionLabel = Loc["Design_AdviceHide"],
                    Action = () => Indexes.FirstOrDefault(r => r.Name == s.Index.Name) is { } row ? SetHiddenAsync(row, true) : Task.CompletedTask
                };
            default:
            {
                double share = _totalIndexSize > 0 ? s.Index!.Size * 100.0 / _totalIndexSize : 0;
                return new AdviceCard
                {
                    Suggestion = s,
                    IconKey = "Mongo.archive-x",
                    IconToken = "VelaWarning",
                    Title = Loc.Format("Design_AdviceUnusedTitle", s.Index!.Name, s.UnusedDays),
                    Detail = Loc.Format("Design_AdviceUnusedDetail", BsonText.Bytes(s.Index.Size), share.ToString("0.#", CultureInfo.InvariantCulture)),
                    ActionLabel = Loc["Design_AdviceHide"],
                    Action = () => Indexes.FirstOrDefault(r => r.Name == s.Index.Name) is { } row ? SetHiddenAsync(row, true) : Task.CompletedTask
                };
            }
        }
    }

    /// <summary>慢查询组的代表查询(给"查看查询"打开到查询编辑器里)。</summary>
    private string QueryText(AdvisorQueryGroup q)
    {
        string text = $"{ShellRef}.find({BsonText.Literal(q.Filter)})";
        if (q.Sort is { ElementCount: > 0 } sort)
        {
            text += $".sort({BsonText.Literal(sort)})";
        }
        return text + ".explain(\"executionStats\")";
    }

    /// <summary><c>{ status, total }</c>。</summary>
    private static string FieldSet(IReadOnlyList<string> fields) => "{ " + string.Join(", ", fields) + " }";

    /// <summary>计划标签:全表扫描 / 内存排序 / 低效扫描。</summary>
    private string PlanLabel(AdvisorQueryGroup q) =>
        q.Collscan ? "COLLSCAN" : q.InMemorySort ? Loc["Design_AdviceInMemorySort"] : Loc["Design_AdviceOverScan"];

    /// <summary>类型列文字。</summary>
    private string FormText(IndexForm form) => Loc[form switch
    {
        IndexForm.Compound => "Design_FormCompound",
        IndexForm.MultiKey => "Design_FormMultiKey",
        IndexForm.Text => "Design_FormText",
        IndexForm.Geo => "Design_FormGeo",
        IndexForm.Hashed => "Design_FormHashed",
        IndexForm.Wildcard => "Design_FormWildcard",
        _ => "Design_FormSingle"
    }];

    /// <summary>属性列的标签。</summary>
    private IReadOnlyList<IndexAttrChip> Attributes(BsonDocument spec, int unusedDays)
    {
        var list = new List<IndexAttrChip>();
        // _id_ 的规格里不写 unique,但它天然唯一 —— 设计稿 07 也给它标了「唯一」。
        if (spec.GetValue("unique", false).ToBoolean() || spec.GetValue("name", "").ToString() == "_id_")
        {
            list.Add(new(Loc["Design_AttrUnique"], "accent"));
        }
        if (spec.GetValue("sparse", false).ToBoolean())
        {
            list.Add(new(Loc["Design_AttrSparse"], "muted"));
        }
        if (spec.Contains("partialFilterExpression"))
        {
            list.Add(new(Loc["Design_AttrPartial"], "info"));
        }
        if (spec.TryGetValue("expireAfterSeconds", out BsonValue ttl) && ttl.IsNumeric)
        {
            list.Add(new(Loc.Format("Design_AttrTtl", Duration(ttl.ToInt64())), "warn"));
        }
        if (spec.GetValue("hidden", false).ToBoolean())
        {
            list.Add(new(Loc["Design_AttrHidden"], "muted"));
        }
        if (unusedDays > 0)
        {
            list.Add(new(Loc.Format("Design_AttrUnused", unusedDays), "warn"));
        }
        return list;
    }

    /// <summary>把进度写进一行(<c>构建中 64% · 约 2 分</c>)。</summary>
    private void ApplyProgress(IndexRow row, double percent)
    {
        row.IsBuilding = true;
        row.BuildPercent = percent;
        row.BuildText = Loc.Format("Design_Building", Math.Round(percent).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>有构建中的索引时每秒刷一次进度,直到都建完(再整页重载一次)。</summary>
    private void StartBuildPoll()
    {
        if (_buildPoll is not null)
        {
            return;
        }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);
        _buildPoll = cts;
        _ = PollBuildsAsync(cts);
    }

    private async Task PollBuildsAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000, cts.Token).ConfigureAwait(true);
                Dictionary<string, double> progress = await LoadBuildProgressAsync().ConfigureAwait(true);
                foreach (IndexRow row in Indexes.Where(static r => r.IsBuilding))
                {
                    if (progress.TryGetValue(row.Name, out double p))
                    {
                        ApplyProgress(row, p);
                    }
                }
                if (progress.Count == 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_buildPoll, cts))
            {
                _buildPoll = null;
            }
            cts.Dispose();
        }
        if (!_disposed)
        {
            await LoadIndexesAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 隐藏 / 取消隐藏(<c>collMod: { index: { name, hidden } }</c>,4.4+)。
    /// 隐藏是"删之前先观察"的安全步骤:优化器不再用它,但照常维护,随时能恢复 —— 所以提示里带一个「撤销」。
    /// </summary>
    internal async Task SetHiddenAsync(IndexRow row, bool hidden)
    {
        if (row.IsId || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        if (Workspace.Guard.ConfirmWrites && !await Workspace.ConfirmAsync(new()
            {
                Title = hidden ? Loc["Design_HideTitle"] : Loc["Design_UnhideTitle"],
                Message = Loc.Format(hidden ? "Design_HideBody" : "Design_UnhideBody", row.Name, Namespace),
                ConfirmLabel = hidden ? Loc["Design_Hide"] : Loc["Design_Unhide"],
                IconKey = hidden ? "Mongo.eye-off" : "Mongo.eye",
                Danger = false
            }).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "collMod", CollectionName },
                { "index", new BsonDocument { { "name", row.Name }, { "hidden", hidden } } }
            }, Lifetime).ConfigureAwait(true);
            string name = row.Name;
            Workspace.Toast(new()
            {
                Title = Loc.Format(hidden ? "Design_HiddenDone" : "Design_UnhiddenDone", name),
                Kind = ToastKind.Success,
                ActionLabel = Loc["Design_Undo"],
                Duration = TimeSpan.FromSeconds(8),
                Action = async () =>
                {
                    if (Indexes.FirstOrDefault(r => r.Name == name) is { } again)
                    {
                        await SetHiddenAsync(again, !hidden).ConfigureAwait(true);
                    }
                }
            });
            await LoadIndexesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
    }

    /// <summary>
    /// 删除索引。确认框写清后果(查询会退回别的计划、重建要重新扫全表),并给一条"改为隐藏"的退路。
    /// </summary>
    internal async Task DropIndexAsync(IndexRow row)
    {
        if (row.IsId || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        var facts = new List<ConfirmFact>
        {
            new(Loc["Design_ColKey"], BsonText.Literal(row.Key)),
            new(Loc["Design_ColSize"], row.SizeText),
            new(Loc["Design_ColUsage"], row.OpsText)
        };
        bool confirmed = await Workspace.ConfirmAsync(new()
        {
            Title = Loc["Design_DropTitle"],
            Message = Loc.Format("Design_DropBody", row.Name, Namespace),
            ConfirmLabel = Loc["Design_DropTitle"],
            Facts = facts,
            TypeToConfirm = Workspace.Guard.ConfirmWrites ? row.Name : null,
            AsideLabel = row.IsHidden || row.IsBuilding ? null : Loc["Design_DropHideInstead"],
            AsideAction = () => _ = SetHiddenAsync(row, true)
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await Workspace.Connection.RunCommandAsync(Database, new BsonDocument
            {
                { "dropIndexes", CollectionName },
                { "index", row.Name }
            }, Lifetime).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Design_Dropped", row.Name), Kind = ToastKind.Success });
            SelectedIndex = null;
            await LoadIndexesAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Fail(ex);
        }
    }

    /// <summary>跑一个可能因权限 / 版本失败的读取;失败给兜底值。</summary>
    private async Task<T> SafeAsync<T>(Func<Task<T>> body, T fallback)
    {
        try
        {
            return await body().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"{Namespace}: {ex.Message}");
            return fallback;
        }
    }
}
