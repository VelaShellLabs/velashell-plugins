using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>历史 / 收藏下拉里的一项。</summary>
/// <param name="Text">筛选原文。</param>
/// <param name="IsFavorite">是不是收藏。</param>
internal sealed record CollectionFilterItem(string Text, bool IsFavorite)
{
    /// <summary>图标。</summary>
    public string IconKey => IsFavorite ? "Mongo.star" : "Mongo.history";

    /// <summary>图标颜色。</summary>
    public string IconToken => IsFavorite ? "VelaShellYellow" : "VelaTextTertiary";
}

/// <summary>查询栏、分页、长查询与执行计划。</summary>
internal sealed partial class CollectionTabViewModel
{
    /// <summary>长查询卡片出现前的等待:短查询闪一下卡片比不显示更烦人。</summary>
    private static readonly TimeSpan LongQueryThreshold = TimeSpan.FromMilliseconds(700);

    private string _filterText;
    private string _limitText;
    private string _selectedHint = "";
    private IReadOnlyList<string> _favorites = [];
    private IReadOnlyList<string> _history = [];
    private int _pageIndex;
    private CancellationTokenSource? _queryCts;
    private string? _queryComment;
    private Stopwatch? _queryWatch;
    private DispatcherTimer? _longQueryTimer;
    private long? _opid;
    private int _countVersion;

    /// <summary>查找(Ctrl+Enter)。</summary>
    public AsyncCommand FindCommand { get; private set; } = null!;

    /// <summary>重置查询栏。</summary>
    public AsyncCommand ResetCommand { get; private set; } = null!;

    /// <summary>清除筛选(无结果卡片)。</summary>
    public AsyncCommand ClearFilterCommand { get; private set; } = null!;

    /// <summary>执行计划(在新查询标签里跑 explain("executionStats"))。</summary>
    public RelayCommand ExplainCommand { get; private set; } = null!;

    /// <summary>收藏 / 取消收藏当前筛选。</summary>
    public AsyncCommand ToggleFavoriteCommand { get; private set; } = null!;

    /// <summary>从历史 / 收藏里套用一条。</summary>
    public AsyncCommand<CollectionFilterItem> ApplyHistoryCommand { get; private set; } = null!;

    /// <summary>刷新本页。</summary>
    public AsyncCommand RefreshCommand { get; private set; } = null!;

    /// <summary>停止长查询(killOp)。</summary>
    public AsyncCommand StopCommand { get; private set; } = null!;

    /// <summary>首页。</summary>
    public AsyncCommand FirstPageCommand { get; private set; } = null!;

    /// <summary>上一页。</summary>
    public AsyncCommand PreviousPageCommand { get; private set; } = null!;

    /// <summary>下一页。</summary>
    public AsyncCommand NextPageCommand { get; private set; } = null!;

    /// <summary>末页。</summary>
    public AsyncCommand LastPageCommand { get; private set; } = null!;

    /// <summary>跳到页码框里的那一页。</summary>
    public AsyncCommand GoToPageCommand { get; private set; } = null!;

    /// <summary>按列排序(点列头):降序 → 升序 → 不排。</summary>
    public AsyncCommand<CollectionColumn> SortByColumnCommand { get; private set; } = null!;

    private void InitializeQueryCommands()
    {
        FindCommand = new(() => RunQueryAsync(resetPage: true));
        ResetCommand = new(ResetAsync);
        ClearFilterCommand = new(() =>
        {
            FilterText = "";
            return RunQueryAsync(resetPage: true);
        });
        ExplainCommand = new(OpenExplain);
        ToggleFavoriteCommand = new(ToggleFavoriteAsync);
        ApplyHistoryCommand = new(item =>
        {
            FilterText = item.Text;
            return RunQueryAsync(resetPage: true);
        });
        RefreshCommand = new(() => RunQueryAsync(resetPage: false));
        StopCommand = new(StopAsync, () => _isLoading);
        FirstPageCommand = new(() => GoToPageAsync(0), () => _pageIndex > 0);
        PreviousPageCommand = new(() => GoToPageAsync(_pageIndex - 1), () => _pageIndex > 0);
        NextPageCommand = new(() => GoToPageAsync(_pageIndex + 1), () => _pageIndex < PageCount - 1);
        LastPageCommand = new(() => GoToPageAsync(PageCount - 1), () => TotalCount is not null && _pageIndex < PageCount - 1);
        GoToPageCommand = new(() =>
        {
            if (int.TryParse(PageText.Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page))
            {
                return GoToPageAsync(Math.Clamp(page - 1, 0, Math.Max(0, PageCount - 1)));
            }
            PageText = (_pageIndex + 1).ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
        SortByColumnCommand = new(column =>
        {
            int next = column.SortDirection switch { 0 => -1, -1 => 1, _ => 0 };
            SortText = next == 0 ? "" : $"{{ {BsonText.FieldName(column.Name)}: {next.ToString(CultureInfo.InvariantCulture)} }}";
            return RunQueryAsync(resetPage: true);
        });
    }

    // ── 查询栏 ───────────────────────────────────────────────────────────────

    /// <summary>筛选原文(mongosh 写法)。外壳读它判断要不要 <see cref="ApplyFilter" />。</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? ""))
            {
                ValidateFilter();
                IsFavorite = _favorites.Contains(_filterText.Trim());
                RaisePropertyChanged(nameof(EchoText));
            }
        }
    }

    /// <summary>外壳从别处带着筛选打开这个标签(对象列表、树的「按此值筛选」):换上并重查。</summary>
    public void ApplyFilter(string filter)
    {
        FilterText = filter;
        _ = RunQueryAsync(resetPage: true);
    }

    /// <summary>筛选的语法错误(标红 + 提示);没有为 <see langword="null" />。</summary>
    public string? FilterError
    {
        get; private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasFilterError));
            }
        }
    }

    /// <summary>有语法错误。</summary>
    public bool HasFilterError => FilterError is not null;

    /// <summary>编辑器上的诊断(红色波浪线)。</summary>
    public IReadOnlyList<EditorDiagnostic> FilterDiagnostics { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>投影。</summary>
    public string ProjectionText
    {
        get; set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                ProjectionInvalid = !ShellJson.TryParseDocument(field, out _, out _);
                RaisePropertyChanged(nameof(EchoText));
            }
        }
    } = "";

    /// <summary>投影写错了。</summary>
    public bool ProjectionInvalid { get; private set => SetProperty(ref field, value); }

    /// <summary>排序。</summary>
    public string SortText
    {
        get; set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                SortInvalid = !ShellJson.TryParseDocument(field, out _, out _);
                RaisePropertyChanged(nameof(EchoText));
            }
        }
    } = "";

    /// <summary>排序写错了。</summary>
    public bool SortInvalid { get; private set => SetProperty(ref field, value); }

    /// <summary>跳过。</summary>
    public string SkipText
    {
        get; set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                SkipInvalid = ParseCount(field, 0) is null;
            }
        }
    } = "0";

    /// <summary>跳过写错了。</summary>
    public bool SkipInvalid { get; private set => SetProperty(ref field, value); }

    /// <summary>限制(每页行数)。</summary>
    public string LimitText
    {
        get => _limitText;
        set
        {
            if (SetProperty(ref _limitText, value ?? ""))
            {
                LimitInvalid = ParseCount(_limitText, 1) is null;
            }
        }
    }

    /// <summary>限制写错了。</summary>
    public bool LimitInvalid { get; private set => SetProperty(ref field, value); }

    /// <summary>索引提示的选项(第一项「自动」)。</summary>
    public IReadOnlyList<string> HintChoices { get => field.Count == 0 ? [Loc["Cw_HintAuto"]] : field; private set => SetProperty(ref field, value); } = [];

    /// <summary>选中的索引提示。</summary>
    public string SelectedHint
    {
        get => _selectedHint.Length == 0 ? Loc["Cw_HintAuto"] : _selectedHint;
        set => SetProperty(ref _selectedHint, value == Loc["Cw_HintAuto"] ? "" : value ?? "");
    }

    /// <summary>当前筛选是不是收藏(星标填色)。</summary>
    public bool IsFavorite { get; private set => SetProperty(ref field, value); }

    /// <summary>历史下拉的内容:收藏在前,历史在后。</summary>
    public IReadOnlyList<CollectionFilterItem> HistoryItems =>
    [
        .. _favorites.Select(static f => new CollectionFilterItem(f, true)),
        .. _history.Where(h => !_favorites.Contains(h)).Select(static h => new CollectionFilterItem(h, false))
    ];

    /// <summary>有没有历史。</summary>
    public bool HasHistory => _favorites.Count > 0 || _history.Count > 0;

    /// <summary>筛选非空(决定空态是「空集合」还是「无结果」)。</summary>
    public bool HasActiveFilter => LastRunFilter.Length > 0 && LastRunFilter != "{}";

    /// <summary>最近一次执行的筛选(无结果卡片里回显)。</summary>
    public string LastRunFilter { get; private set; } = "";

    /// <summary>当前查询(导出向导「导出当前查询结果」用)。</summary>
    public FindRequest? CurrentRequest { get; private set; }

    /// <summary>解析好的排序(列头的箭头用);写错为 <see langword="null" />。</summary>
    internal BsonDocument? ParsedSort => ShellJson.TryParseDocument(SortText, out BsonDocument doc, out _) ? doc : null;

    // ── 分页 ─────────────────────────────────────────────────────────────────

    /// <summary>总数(有筛选用 countDocuments,无筛选用 estimatedDocumentCount);没数完为 <see langword="null" />。</summary>
    public long? TotalCount { get; private set; }

    /// <summary>每页行数。</summary>
    public int PageSize => ParseCount(_limitText, 1) ?? Workspace.Connection.Settings.PageSize;

    /// <summary>基础跳过数。</summary>
    public int BaseSkip => ParseCount(SkipText, 0) ?? 0;

    /// <summary>总页数(总数未知时至少当前页 + 1)。</summary>
    public int PageCount => TotalCount is { } total
        ? (int)Math.Max(1, Math.Ceiling(Math.Max(0, total - BaseSkip) / (double)PageSize))
        : _pageIndex + (_documents.Count >= PageSize ? 2 : 1);

    /// <summary>页码框。</summary>
    public string PageText { get; set => SetProperty(ref field, value ?? ""); } = "1";

    /// <summary><c>/ 25,699 页</c>。</summary>
    public string PageCountText => TotalCount is null ? Loc["Cw_PageCountUnknown"] : Loc.Format("Cw_PageCount", BsonText.Grouped(PageCount));

    /// <summary><c>1–50 / 1,284,902</c>。</summary>
    public string RangeText
    {
        get
        {
            long first = BaseSkip + (long)_pageIndex * PageSize;
            string total = TotalCount is { } t ? BsonText.Grouped(t) : "…";
            return _documents.Count == 0
                ? $"0 / {total}"
                : $"{BsonText.Grouped(first + 1)}–{BsonText.Grouped(first + _documents.Count)} / {total}";
        }
    }

    /// <summary>底栏回显的 mongosh 语句(筛选缩成 <c>{…}</c>)。</summary>
    public string EchoText => BuildStatement(abbreviate: true);

    // ── 长查询 ───────────────────────────────────────────────────────────────

    /// <summary>长查询卡片(设计稿 22「正在查询 shop.orders…」)。</summary>
    public bool IsLongQuery { get; private set => SetProperty(ref field, value); }

    /// <summary>长查询卡片标题。</summary>
    public string LongQueryTitle => Loc.Format("State_LongQueryTitle", Info.Namespace);

    /// <summary><c>已运行 6.2 s · 服务端 opid 8812045</c>。</summary>
    public string LongQueryText { get; private set => SetProperty(ref field, value); } = "";

    // ── 执行 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 执行查询。<paramref name="resetPage" /> 为真回到第一页(改了筛选 / 排序时);
    /// 刷新、翻页留在当前页。
    /// </summary>
    public async Task RunQueryAsync(bool resetPage)
    {
        if (_disposed)
        {
            return;
        }
        if (!TryBuildRequest(resetPage ? 0 : _pageIndex, out FindRequest? request))
        {
            return;
        }
        if (resetPage)
        {
            SetPage(0);
        }
        _queryCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _queryCts = cts;
        _queryComment = request.Comment;
        _opid = null;
        IsLoading = true;
        StartLongQueryTimer();
        Stopwatch watch = StartWatch();
        try
        {
            var options = new FindOptions<BsonDocument>
            {
                Projection = request.Projection,
                Sort = request.Sort,
                Skip = request.Skip,
                Limit = request.Limit,
                Comment = request.Comment,
                MaxTime = request.MaxTimeMs > 0 ? TimeSpan.FromMilliseconds(request.MaxTimeMs) : null
            };
            if (request.Hint is not null)
            {
                options.Hint = request.Hint;
            }
            using IAsyncCursor<BsonDocument> cursor = await Collection.FindAsync(request.Filter, options, cts.Token).ConfigureAwait(true);
            List<BsonDocument> documents = await cursor.ToListAsync(cts.Token).ConfigureAwait(true);
            watch.Stop();
            if (!ReferenceEquals(_queryCts, cts))
            {
                return;
            }
            _elapsed = watch.Elapsed;
            _loadedOnce = true;
            CurrentRequest = request;
            LastRunFilter = _filterText.Trim();
            ApplyResults(documents, request.Skip + 1);
            RaisePropertiesChanged(nameof(Elapsed), nameof(LastRunFilter), nameof(HasActiveFilter), nameof(CurrentRequest),
                nameof(EchoText), nameof(RangeText), nameof(PageCountText));
            UpdateStatus();
            _ = CountAsync(request);
            _ = ExplainPlanAsync(request);
            if (HasActiveFilter)
            {
                _ = RememberFilterAsync(LastRunFilter);
            }
        }
        catch (OperationCanceledException)
        {
            // 被新查询顶掉或用户点了停止:停止那条路自己报。
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            if (ex is MongoCommandException { Code: 2 or 9 } command)
            {
                // BadValue / FailedToParse:多半是筛选写得服务器不认($foo 之类),标到筛选框上。
                FilterError = command.ErrorMessage;
            }
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            if (ReferenceEquals(_queryCts, cts))
            {
                IsLoading = false;
                StopLongQueryTimer();
                _queryCts = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>按查询栏拼一次查询;有语法错误就标红并返回 <see langword="false" />。</summary>
    private bool TryBuildRequest(int pageIndex, out FindRequest request)
    {
        request = null!;
        ValidateFilter();
        if (FilterError is not null || ProjectionInvalid || SortInvalid || SkipInvalid || LimitInvalid)
        {
            return false;
        }
        BsonDocument filter = ShellJson.ParseDocument(_filterText);
        BsonDocument projection = ShellJson.ParseDocument(ProjectionText);
        BsonDocument sort = ShellJson.ParseDocument(SortText);
        request = new FindRequest
        {
            Database = Database,
            Collection = CollectionName,
            Filter = filter,
            Projection = projection.ElementCount > 0 ? projection : null,
            Sort = sort.ElementCount > 0 ? sort : null,
            Skip = BaseSkip + pageIndex * PageSize,
            Limit = PageSize,
            Hint = _selectedHint.Length > 0 ? new BsonString(_selectedHint) : null,
            MaxTimeMs = Workspace.Connection.Settings.MaxTimeMs,
            Comment = "velashell:" + Guid.NewGuid().ToString("N")[..12]
        };
        return true;
    }

    /// <summary>边打字边做语法检查(含"带点号的裸键要加引号"诊断)。</summary>
    private void ValidateFilter()
    {
        string text = _filterText;
        var diagnostics = new List<EditorDiagnostic>();
        foreach (ShellDiagnostic diagnostic in ShellJson.Diagnose(text))
        {
            diagnostics.Add(new EditorDiagnostic(diagnostic.Offset, diagnostic.Length,
                Loc.Format(diagnostic.MessageKey, diagnostic.Argument), DiagnosticSeverity.Error, diagnostic.Fix, Loc["Cw_FixHint"]));
        }
        string? error = diagnostics.FirstOrDefault()?.Message;
        if (error is null && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                _ = ShellJson.ParseDocument(text);
            }
            catch (ShellJsonException ex)
            {
                error = Loc["Cw_FilterSyntax"];
                int at = ex.Offset >= 0 ? Math.Min(ex.Offset, Math.Max(0, text.Length - 1)) : 0;
                diagnostics.Add(new EditorDiagnostic(at, ex.Offset >= 0 ? 1 : text.Length, error));
            }
        }
        FilterError = error;
        FilterDiagnostics = diagnostics;
    }

    private static int? ParseCount(string text, int min) =>
        int.TryParse(text.Trim().Replace(",", "", StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        && value >= min && value <= 1_000_000
            ? value
            : string.IsNullOrWhiteSpace(text) && min == 0 ? 0 : null;

    private async Task ResetAsync()
    {
        FilterText = "";
        ProjectionText = "";
        SortText = "";
        SkipText = "0";
        LimitText = Workspace.Connection.Settings.PageSize.ToString(CultureInfo.InvariantCulture);
        SelectedHint = "";
        await RunQueryAsync(resetPage: true).ConfigureAwait(true);
    }

    private async Task GoToPageAsync(int page)
    {
        if (page < 0 || page == _pageIndex && !_isLoading && _documents.Count > 0)
        {
            return;
        }
        SetPage(page);
        await RunQueryAsync(resetPage: false).ConfigureAwait(true);
    }

    private void SetPage(int page)
    {
        _pageIndex = Math.Max(0, page);
        PageText = (_pageIndex + 1).ToString(CultureInfo.InvariantCulture);
        RaisePropertiesChanged(nameof(RangeText), nameof(PageCountText));
        RaisePagingCanExecute();
    }

    private void RaisePagingCanExecute()
    {
        FirstPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        LastPageCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 数总数:有筛选用 <c>countDocuments</c>(要扫描,给 5 秒上限),没有筛选用 <c>estimatedDocumentCount</c>
    /// (元数据,O(1))—— 百万级集合上对空筛选做 countDocuments 会白白扫一遍。
    /// </summary>
    private async Task CountAsync(FindRequest request)
    {
        int version = ++_countVersion;
        TotalCount = null;
        RaisePropertiesChanged(nameof(TotalCount), nameof(PageCountText), nameof(RangeText));
        try
        {
            long? count = null;
            if (request.Filter.ElementCount == 0)
            {
                count = await Workspace.Connection.EstimatedCountAsync(Database, CollectionName, _lifetime.Token).ConfigureAwait(true);
            }
            count ??= await Collection.CountDocumentsAsync(request.Filter,
                new CountOptions { MaxTime = TimeSpan.FromSeconds(5), Hint = request.Hint }, _lifetime.Token).ConfigureAwait(true);
            if (version != _countVersion)
            {
                return;
            }
            TotalCount = count;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            Workspace.Log.Info($"Counting {Info.Namespace} failed: {ex.Message}");
        }
        RaisePropertiesChanged(nameof(TotalCount), nameof(PageCountText), nameof(RangeText), nameof(PageCount));
        RaisePagingCanExecute();
    }

    /// <summary>取一次 <c>explain("queryPlanner")</c> 的获胜阶段给状态栏(不跑查询本身,开销很小)。</summary>
    private async Task ExplainPlanAsync(FindRequest request)
    {
        try
        {
            var find = new BsonDocument { { "find", CollectionName }, { "filter", request.Filter } };
            if (request.Sort is not null)
            {
                find["sort"] = request.Sort;
            }
            if (request.Projection is not null)
            {
                find["projection"] = request.Projection;
            }
            if (request.Hint is not null)
            {
                find["hint"] = request.Hint;
            }
            find["skip"] = request.Skip;
            find["limit"] = request.Limit;
            BsonDocument reply = await Workspace.Connection.RunCommandAsync(Database,
                new BsonDocument { { "explain", find }, { "verbosity", "queryPlanner" } }, _lifetime.Token).ConfigureAwait(true);
            PlanSummary = SummarizePlan(reply);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            PlanSummary = "";
        }
    }

    // ── 长查询:计时、opid、killOp ─────────────────────────────────────────────

    private void StartLongQueryTimer()
    {
        _queryWatch = Stopwatch.StartNew();
        _longQueryTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => OnLongQueryTick());
        _longQueryTimer.Start();
    }

    private void StopLongQueryTimer()
    {
        _longQueryTimer?.Stop();
        _queryWatch?.Stop();
        IsLongQuery = false;
    }

    private void OnLongQueryTick()
    {
        if (_queryWatch is null || !_isLoading)
        {
            return;
        }
        TimeSpan elapsed = _queryWatch.Elapsed;
        if (elapsed < LongQueryThreshold)
        {
            return;
        }
        if (!IsLongQuery)
        {
            IsLongQuery = true;
            _ = FindOpidAsync(_queryComment);
        }
        string seconds = elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        LongQueryText = _opid is { } opid
            ? Loc.Format("State_LongQueryOpid", seconds, opid.ToString(CultureInfo.InvariantCulture))
            : Loc.Format("State_LongQueryElapsed", seconds);
    }

    /// <summary>按 comment 在 currentOp 里认出这次查询的 opid(卡片上给 DBA 看的那串数)。</summary>
    private async Task FindOpidAsync(string? comment)
    {
        if (comment is null)
        {
            return;
        }
        try
        {
            BsonDocument reply = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument
            {
                { "currentOp", true },
                { "command.comment", comment }
            }).ConfigureAwait(true);
            if (reply.GetValue("inprog", new BsonArray()).AsBsonArray.FirstOrDefault() is BsonDocument op
                && op.TryGetValue("opid", out BsonValue opid) && opid.IsNumeric)
            {
                _opid = opid.ToInt64();
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"currentOp failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 停止:先 killOp(服务器那边真的停下),再放弃本地等待。只取消令牌的话,
    /// 客户端不等了,服务器上的扫描照跑不误 —— 在生产库上那正是要拦的那种查询。
    /// </summary>
    private async Task StopAsync()
    {
        string? comment = _queryComment;
        CancellationTokenSource? cts = _queryCts;
        try
        {
            if (comment is not null)
            {
                int killed = await Workspace.Connection.KillByCommentAsync(comment).ConfigureAwait(true);
                Workspace.Toast(new()
                {
                    Title = killed > 0 ? Loc["State_Killed"] : Loc["State_Cancelled"],
                    Kind = ToastKind.Info
                });
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            cts?.Cancel();
        }
    }

    // ── 历史 / 收藏 / 索引 ─────────────────────────────────────────────────────

    private async Task LoadHistoryAsync()
    {
        _favorites = await Workspace.Store.LoadFilterFavoritesAsync(Workspace.ConnectionKey, Info.Namespace).ConfigureAwait(true);
        _history = await Workspace.Store.LoadFilterHistoryAsync(Workspace.ConnectionKey, Info.Namespace).ConfigureAwait(true);
        IsFavorite = _favorites.Contains(_filterText.Trim());
        RaisePropertiesChanged(nameof(HistoryItems), nameof(HasHistory));
    }

    private async Task RememberFilterAsync(string filter)
    {
        await Workspace.Store.AddFilterHistoryAsync(Workspace.ConnectionKey, Info.Namespace, filter).ConfigureAwait(true);
        _history = [filter, .. _history.Where(h => h != filter).Take(29)];
        RaisePropertiesChanged(nameof(HistoryItems), nameof(HasHistory));
    }

    private async Task ToggleFavoriteAsync()
    {
        string filter = _filterText.Trim();
        if (filter.Length == 0 || filter == "{}")
        {
            return;
        }
        _favorites = _favorites.Contains(filter) ? [.. _favorites.Where(f => f != filter)] : [filter, .. _favorites];
        IsFavorite = _favorites.Contains(filter);
        RaisePropertiesChanged(nameof(HistoryItems), nameof(HasHistory));
        await Workspace.Store.SaveFilterFavoritesAsync(Workspace.ConnectionKey, Info.Namespace, _favorites).ConfigureAwait(true);
    }

    private async Task LoadIndexesAsync()
    {
        try
        {
            IReadOnlyList<BsonDocument> indexes = await Workspace.Connection.ListIndexesAsync(Database, CollectionName, _lifetime.Token).ConfigureAwait(true);
            HintChoices = [Loc["Cw_HintAuto"], .. indexes.Select(static i => i.GetValue("name", "").AsString).Where(static n => n.Length > 0)];
            RaisePropertyChanged(nameof(SelectedHint));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException)
        {
            Workspace.Log.Info($"Listing indexes of {Info.Namespace} failed: {ex.Message}");
        }
    }

    // ── 语句 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 拼 mongosh 语句:<c>db.orders.find({…}).sort({ createdAt: -1 }).limit(50)</c>。
    /// 底栏回显把筛选缩成 <c>{…}</c>(那里放不下,完整的在筛选框里);执行计划用完整的。
    /// </summary>
    internal string BuildStatement(bool abbreviate, int? skipOverride = null)
    {
        string filter = string.IsNullOrWhiteSpace(_filterText) ? "{}" : abbreviate ? "{…}" : _filterText.Trim();
        string projection = string.IsNullOrWhiteSpace(ProjectionText) ? "" : ", " + ProjectionText.Trim();
        StringBuilder text = new System.Text.StringBuilder()
            .Append("db.").Append(MongoWorkspaceViewModel.ShellCollectionRef(CollectionName))
            .Append(".find(").Append(filter).Append(projection).Append(')');
        if (!string.IsNullOrWhiteSpace(SortText))
        {
            _ = text.Append(".sort(").Append(SortText.Trim()).Append(')');
        }
        if (_selectedHint.Length > 0)
        {
            _ = text.Append(".hint(").Append(BsonText.Quote(_selectedHint)).Append(')');
        }
        int skip = skipOverride ?? (BaseSkip + _pageIndex * PageSize);
        if (skip > 0)
        {
            _ = text.Append(".skip(").Append(skip.ToString(CultureInfo.InvariantCulture)).Append(')');
        }
        _ = text.Append(".limit(").Append(PageSize.ToString(CultureInfo.InvariantCulture)).Append(')');
        return text.ToString();
    }

    private void OpenExplain()
    {
        if (FilterError is not null)
        {
            return;
        }
        Workspace.OpenQuery(Database, BuildStatement(abbreviate: false) + ".explain(\"executionStats\")", run: true);
    }
}
