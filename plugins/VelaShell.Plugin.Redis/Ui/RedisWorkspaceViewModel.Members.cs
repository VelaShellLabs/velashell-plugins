using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>
/// 成员表:分页、成员内过滤、排序,以及有序集合的排名列。
/// <para>
/// 分页走的是 <c>*SCAN</c> 的游标,而游标是**单向**的 —— 服务端没有"上一页"。
/// 所以这里记着每一页的起始游标,回退时按记下来的那个重取。<b>不假装能随机跳页</b>:
/// 一个能点到第 37 页的分页条,在游标分页上只能靠从头翻,而那正是 <c>KEYS</c> 式的灾难。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    /// <summary>每一页的起始游标(<see cref="_pageIndex" /> 指向当前那一页)。</summary>
    private readonly List<string> _pageCursors = ["0"];
    private int _pageIndex;
    private long _elementTotal = -1;

    /// <summary>成员内过滤(通配)。</summary>
    public string MemberFilter
    {
        get;
        set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(MemberFilterEcho));
        }
    } = string.Empty;

    /// <summary>成员搜索框的占位提示(按类型换命令名)。</summary>
    public string MemberFilterPlaceholder =>
        Loc.Format("Redis_MemberFilterPlaceholder", ScanCommandForType);

    /// <summary>
    /// 成员过滤的口径说明。列表与流没有 <c>MATCH</c>,这一栏退回客户端逐行筛 ——
    /// 界面上写着在过滤,就必须说清是谁在过滤。
    /// </summary>
    public string MemberFilterEcho => Selected?.Type is "list" or "stream" && MemberFilter.Trim().Length > 0
        ? Loc["Redis_MemberFilterFallback"]
        : string.Empty;

    /// <summary>当前类型对应的成员扫描命令名(回显里用)。</summary>
    private string ScanCommandForType => Selected?.Type switch
    {
        "hash" => "HSCAN",
        "set" => "SSCAN",
        "zset" => "ZSCAN",
        "list" => "LRANGE",
        "stream" => "XRANGE",
        _ => "SCAN"
    };

    /// <summary>应用成员过滤(回到第一页重取)。</summary>
    public AsyncCommand ApplyMemberFilterCommand { get; private set; } = null!;

    /// <summary>有序集合按分数倒序显示。</summary>
    public bool SortByScoreDescending
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(SortLabel));
        }
    } = true;

    /// <summary>排序按钮的文案。</summary>
    public string SortLabel => ShowsScore ? Loc["Redis_SortByScoreDesc"] : Loc["Redis_SortByLabel"];

    /// <summary>切换排序。</summary>
    public AsyncCommand ToggleSortCommand { get; private set; } = null!;

    // ── 有序集合:按分数区间取 ────────────────────────────────────

    /// <summary>
    /// 用分数区间取有序集合(<c>ZRANGEBYSCORE</c>)而不是 <c>ZSCAN</c>。
    /// <para>
    /// 顺带解决一个 <c>ZSCAN</c> 给不了的东西:<b>真正的名次</b>。<c>ZSCAN</c> 不保证顺序,
    /// 所以那条路上的"排名"只能是"这一页里的第几行";按分数区间走的是索引分页,
    /// 顺序由服务端保证,名次因此是全局的。
    /// </para>
    /// </summary>
    public bool ScoreRangeOn
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(RankIsGlobal));
        }
    }

    /// <summary>当前这一列名次是不是全局的(据此决定表头措辞)。</summary>
    public bool RankIsGlobal => ShowsScore && ScoreRangeOn;

    /// <summary>分数下界(空 = <c>-inf</c>)。</summary>
    public string ScoreMin
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>分数上界(空 = <c>+inf</c>)。</summary>
    public string ScoreMax
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>开关分数区间模式。</summary>
    public AsyncCommand ToggleScoreRangeCommand { get; private set; } = null!;

    /// <summary>每页行数的可选值。</summary>
    public IReadOnlyList<int> PageSizeOptions { get; } = [100, 200, 500, 1000];

    /// <summary>每页行数。</summary>
    public int MemberPageSize
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(PageSizeLabel));
            // 换每页行数等于换分页,记着的那串游标全部作废 —— 回第一页重取。
            _ = ReloadElementsAsync();
        }
    } = 200;

    /// <summary>每页行数的显示文案。</summary>
    public string PageSizeLabel =>
        Loc.Format("Redis_PageSize", MemberPageSize.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>下一页。</summary>
    public AsyncCommand NextPageCommand { get; private set; } = null!;

    /// <summary>上一页。</summary>
    public AsyncCommand PrevPageCommand { get; private set; } = null!;

    /// <summary>能往后翻(游标没归零)。</summary>
    public bool CanGoNextPage => HasMoreElements;

    /// <summary>能往回翻(不在第一页)。</summary>
    public bool CanGoPrevPage => _pageIndex > 0;

    /// <summary>"第 1–200 项 · 共 12 480"。</summary>
    public string MemberRangeText
    {
        get
        {
            if (Elements.Count == 0)
            {
                return string.Empty;
            }
            long first = ((long)_pageIndex * MemberPageSize) + 1;
            long last = first + Elements.Count - 1;
            return Loc.Format("Redis_MemberRange",
                first.ToString("N0", CultureInfo.CurrentCulture),
                last.ToString("N0", CultureInfo.CurrentCulture),
                _elementTotal >= 0 ? _elementTotal.ToString("N0", CultureInfo.CurrentCulture) : "?");
        }
    }

    /// <summary>
    /// 底部条那句载入状态。**只有游标归零才敢说"全部载入"** —— 与键列表同一条纪律。
    /// </summary>
    public string LoadedText => IsCollectionSelected
        ? HasMoreElements
            ? Loc.Format("Redis_CursorMore", _elementCursor,
                Elements.Count.ToString("N0", CultureInfo.CurrentCulture),
                _elementTotal >= 0 ? _elementTotal.ToString("N0", CultureInfo.CurrentCulture) : "?")
            : Loc.Format("Redis_CursorZeroLoaded", Elements.Count.ToString("N0", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>行表是不是要显示排名与差值两列(只有有序集合)。</summary>
    public bool ShowsRank => Selected?.Type is "zset";

    /// <summary>字节列的表头对不对得上(集合类给字节数,有序集合那一列换成差值)。</summary>
    public string TrailingColumnHeader => ShowsRank ? Loc["Redis_ColumnDelta"] : Loc["Redis_ColumnBytes"];

    /// <summary>截断说明的常驻提示(底部条右侧)。</summary>
    public string TruncationHint =>
        Loc.Format("Redis_TruncationHint", FormatBytes(_connection.Settings.ValuePreviewBytes));

    /// <summary>有序集合的精度提醒。</summary>
    public string ScorePrecisionHint => ShowsScore ? Loc["Redis_ScorePrecision"] : string.Empty;

    private void InitializeMembers()
    {
        ApplyMemberFilterCommand = new(ReloadElementsAsync);
        ToggleSortCommand = new(() =>
        {
            SortByScoreDescending = !SortByScoreDescending;
            // 分数区间那条路的顺序由服务端给,换了方向就得重取;ZSCAN 那条路本地重排即可。
            if (ScoreRangeOn && ShowsScore)
            {
                return ReloadElementsAsync();
            }
            ResortElements();
            return Task.CompletedTask;
        });
        ToggleScoreRangeCommand = new(() =>
        {
            ScoreRangeOn = !ScoreRangeOn;
            return ReloadElementsAsync();
        });
        NextPageCommand = new(NextPageAsync, () => CanGoNextPage && !IsLoadingDetail);
        PrevPageCommand = new(PrevPageAsync, () => CanGoPrevPage && !IsLoadingDetail);
    }

    /// <summary>回到第一页重取(换过滤条件、换每页行数、写完之后)。</summary>
    private Task ReloadElementsAsync()
    {
        _pageCursors.Clear();
        _pageCursors.Add("0");
        _pageIndex = 0;
        return LoadElementsAsync("0", append: false);
    }

    private Task NextPageAsync()
    {
        if (!CanGoNextPage)
        {
            return Task.CompletedTask;
        }
        _pageIndex++;
        if (_pageCursors.Count <= _pageIndex)
        {
            _pageCursors.Add(_elementCursor);
        }
        return LoadElementsAsync(_pageCursors[_pageIndex], append: false);
    }

    private Task PrevPageAsync()
    {
        if (!CanGoPrevPage)
        {
            return Task.CompletedTask;
        }
        _pageIndex--;
        return LoadElementsAsync(_pageCursors[_pageIndex], append: false);
    }

    /// <summary>取一页成员。</summary>
    /// <param name="cursor">起始游标。</param>
    /// <param name="append">true = 追加(「加载更多」),false = 换页(替换当前这批)。</param>
    private async Task LoadElementsAsync(string cursor, bool append)
    {
        if (Selected is not { } info || info.IsGone || info.Key is null)
        {
            return;
        }
        IsLoadingDetail = true;
        try
        {
            // 有序集合开了分数区间就走索引分页那条路(顺序由服务端保证,名次才是全局的);
            // 其余情形一律走各自的 *SCAN 游标。
            RedisElementPage page = ScoreRangeOn && info.Type is "zset"
                ? await _connection
                    .ReadSortedSetByScoreAsync(info.Key, ScoreMin, ScoreMax, cursor, MemberPageSize,
                        SortByScoreDescending)
                    .ConfigureAwait(true)
                : await _connection
                    .ReadElementsAsync(info.Key, info.Type, cursor, MemberPageSize, MemberFilter.Trim())
                    .ConfigureAwait(true);
            if (!append)
            {
                Elements.Clear();
                SelectedElement = null;
            }
            foreach (RedisElement row in page.Rows)
            {
                Elements.Add(new(row));
            }
            _elementCursor = page.Cursor;
            _elementTotal = page.Total;
            HasMoreElements = !page.IsComplete;
            ResortElements();
            PageStatus = page.Total >= 0
                ? Loc.Format("Redis_PageStatus", Elements.Count.ToString("N0", CultureInfo.CurrentCulture), Approx(page.Total))
                : Elements.Count.ToString("N0", CultureInfo.CurrentCulture);
            RaiseMemberState();
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error($"Reading elements of '{info.Key?.Display}' failed.", ex);
        }
        finally
        {
            IsLoadingDetail = false;
            NextPageCommand.RaiseCanExecuteChanged();
            PrevPageCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 给当前这批行编号,并为有序集合算排名与"与上一名之差"。
    /// <para>
    /// 排序**只作用在这一页上**,并且界面必须说清这一点 —— 服务端的 <c>ZSCAN</c> 不保证顺序,
    /// 想要全局排名得走 <c>ZREVRANGE</c>。这里给的是"这一页里的名次",配合分数列足够回答
    /// "谁在前面、差多少"。
    /// </para>
    /// </summary>
    private void ResortElements()
    {
        List<RedisElementRow> rows = [.. Elements];
        if (ShowsScore)
        {
            rows.Sort((left, right) => SortByScoreDescending
                ? right.Score.CompareTo(left.Score)
                : left.Score.CompareTo(right.Score));
        }
        else if (!SortByScoreDescending)
        {
            rows.Sort((left, right) => string.CompareOrdinal(left.Label, right.Label));
        }
        long ordinal = (long)_pageIndex * MemberPageSize;
        double? previous = null;
        for (int i = 0; i < rows.Count; i++)
        {
            RedisElementRow row = rows[i];
            row.Ordinal = ordinal + i;
            row.OrdinalText = ShowsRank
                ? "#" + (ordinal + i + 1).ToString(CultureInfo.CurrentCulture)
                : (ordinal + i).ToString(CultureInfo.CurrentCulture);
            row.TrailingText = ShowsRank
                ? previous is { } before
                    ? (before - row.Score).ToString("N1", CultureInfo.CurrentCulture)
                    : "—"
                : FormatBytes(Encoding.UTF8.GetByteCount(row.Value.Length > 0 ? row.Value : row.Label));
            previous = row.Score;
        }
        if (rows.Count != Elements.Count)
        {
            return;
        }
        for (int i = 0; i < rows.Count; i++)
        {
            if (!ReferenceEquals(Elements[i], rows[i]))
            {
                Elements[i] = rows[i];
            }
        }
    }

    private void RaiseMemberState()
    {
        RaisePropertyChanged(nameof(MemberRangeText));
        RaisePropertyChanged(nameof(LoadedText));
        RaisePropertyChanged(nameof(CanGoNextPage));
        RaisePropertyChanged(nameof(CanGoPrevPage));
        RaisePropertyChanged(nameof(ShowsRank));
        RaisePropertyChanged(nameof(TrailingColumnHeader));
        RaisePropertyChanged(nameof(ScorePrecisionHint));
        RaisePropertyChanged(nameof(SortLabel));
        RaisePropertyChanged(nameof(MemberFilterPlaceholder));
        RaisePropertyChanged(nameof(MemberFilterEcho));
    }

    /// <summary>选中的键变了 → 成员表的分页、过滤与排序全部复位。</summary>
    private void ResetMembersForSelection()
    {
        _pageCursors.Clear();
        _pageCursors.Add("0");
        _pageIndex = 0;
        _elementTotal = -1;
        MemberFilter = string.Empty;
        ScoreRangeOn = false;
        ScoreMin = string.Empty;
        ScoreMax = string.Empty;
        RaiseMemberState();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
