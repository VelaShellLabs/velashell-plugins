using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 集合设计(设计稿 07 索引与建议 / 08 Schema 分析 / 16 验证规则,外加选项与统计两页)。
/// <para>
/// 五个子页共用一个标签、一份视图模型:它们说的是同一个集合,彼此要递东西 ——
/// Schema 分析生成的规则要送进验证规则页,抽样到的字段要进新建索引的字段下拉与规则编辑器的补全,
/// 索引统计要进状态栏。拆成五个视图模型就得在它们之间再架一层消息,不值得。
/// 各子页的成员按页拆在 <c>DesignTabViewModel.*.cs</c> 分部文件里。
/// </para>
/// <para>
/// 子页**按需加载**:打开时只加载当前页(以及子页条上要显示计数的索引列表),
/// 其余页第一次切过去时再加载 —— Schema 抽样、全集合预检都不便宜,用户可能根本不看。
/// </para>
/// </summary>
internal sealed partial class DesignTabViewModel : WorkspaceTab
{
    private readonly CancellationTokenSource _life = new();
    private DesignPage _page;
    private CollectionInfo _live;
    private bool _indexesLoaded;
    private bool _validationLoaded;
    private bool _optionsLoaded;
    private bool _statsLoaded;
    private bool _disposed;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="info">集合信息(对象树里那份;可能是占位,加载时会重新探)。</param>
    /// <param name="page">初始子页。</param>
    public DesignTabViewModel(IMongoWorkspace workspace, CollectionInfo info, DesignPage page)
        : base(workspace)
    {
        Info = info;
        _live = info;
        _page = page;
        Title = Loc.Format("Design_Title", info.Name);
        Scope = "@" + info.Database;
        _sampleSize = workspace.Connection.Settings.SampleSize is 100 or 1000 or 5000 ? workspace.Connection.Settings.SampleSize : 1000;
        InitializeIndexes();
        InitializeNewIndex();
        InitializeSchema();
        InitializeValidation();
        InitializeOptions();
        InitializeStats();
        UpdateStatus();
    }

    /// <summary>打开时的集合信息。</summary>
    public CollectionInfo Info { get; }

    /// <summary>重新探过的集合信息(验证规则、选项以它为准)。</summary>
    public CollectionInfo Live
    {
        get => _live;
        private set
        {
            if (SetProperty(ref _live, value))
            {
                RaisePropertiesChanged(nameof(IsView), nameof(IsTimeSeries), nameof(IsCapped), nameof(ValidationTabHint), nameof(CanValidate));
            }
        }
    }

    /// <summary>库名。</summary>
    public string Database => Info.Database;

    /// <summary>集合名。</summary>
    public string CollectionName => Info.Name;

    /// <summary>命名空间。</summary>
    public string Namespace => Info.Namespace;

    /// <summary>当前子页。</summary>
    public DesignPage Page
    {
        get => _page;
        set
        {
            if (SetProperty(ref _page, value))
            {
                (Workspace as MongoSession)?.Shell.OnTabToolChanged();
            }
            RaisePropertyChanged(nameof(PageName));
            UpdateStatus();
            _ = EnsurePageAsync(force: false);
        }
    }

    /// <summary>子页名(子页条的单选绑它:<c>IsEqual</c> 转换器两头都是字符串)。</summary>
    public string PageName
    {
        get => _page.ToString();
        set
        {
            if (Enum.TryParse(value, out DesignPage page))
            {
                Page = page;
            }
        }
    }

    /// <summary>视图(没有索引、不能设验证规则)。</summary>
    public bool IsView => _live.Kind == CollectionKind.View;

    /// <summary>时序集合。</summary>
    public bool IsTimeSeries => _live.Kind == CollectionKind.TimeSeries;

    /// <summary>固定集合。</summary>
    public bool IsCapped => _live.Kind == CollectionKind.Capped;

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Design;

    /// <inheritdoc />
    public override string Key => $"design:{Info.Namespace}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.pencil-ruler";

    /// <inheritdoc />
    public override string IconToken => "VelaAccent";

    /// <summary>集合的 mongosh 引用(<c>db.orders</c> / <c>db.getCollection("a-b")</c>)。</summary>
    internal string ShellRef => "db." + MongoWorkspaceViewModel.ShellCollectionRef(CollectionName);

    /// <summary>生命周期取消令牌(标签关掉就取消在途的请求)。</summary>
    internal CancellationToken Lifetime => _life.Token;

    /// <inheritdoc />
    public override async Task LoadAsync()
    {
        await RefreshInfoAsync().ConfigureAwait(true);
        // 子页条上的「索引 N」要计数,所以索引总是先加载;其余页按需。
        if (_page != DesignPage.Indexes)
        {
            await EnsureIndexesAsync().ConfigureAwait(true);
        }
        await EnsurePageAsync(force: false).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public override Task RefreshAsync() => EnsurePageAsync(force: true);

    /// <inheritdoc />
    protected override void OnActivated() => UpdateStatus();

    /// <summary>确保某个子页的数据已加载(<paramref name="force" /> = 重新加载)。</summary>
    internal async Task EnsurePageAsync(bool force)
    {
        if (_disposed)
        {
            return;
        }
        switch (_page)
        {
            case DesignPage.Indexes when force:
                await LoadIndexesAsync().ConfigureAwait(true);
                break;
            case DesignPage.Indexes:
                await EnsureIndexesAsync().ConfigureAwait(true);
                break;
            case DesignPage.Schema when force || Schema is null:
                await AnalyzeAsync().ConfigureAwait(true);
                break;
            case DesignPage.Validation when force || !_validationLoaded:
                if (force)
                {
                    await RefreshInfoAsync().ConfigureAwait(true);
                }
                // 刷新不吞掉未应用的改动:编辑器里有改动时只重跑预检与历史,不覆盖文本。
                await LoadValidationAsync(resetEditor: !_editorReady || (force && !IsModified)).ConfigureAwait(true);
                break;
            case DesignPage.Options when force || !_optionsLoaded:
                await LoadOptionsAsync().ConfigureAwait(true);
                break;
            case DesignPage.Stats when force || !_statsLoaded:
                await LoadStatsAsync().ConfigureAwait(true);
                break;
        }
    }

    /// <summary>重新探一次集合信息(<c>listCollections</c> 的 options:验证规则、TTL、时序参数…)。</summary>
    internal async Task RefreshInfoAsync()
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await Workspace.Connection.Database(Database)
                .ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionName) }, Lifetime)
                .ConfigureAwait(true);
            BsonDocument? doc = await cursor.FirstOrDefaultAsync(Lifetime).ConfigureAwait(true);
            if (doc is not null)
            {
                Live = MongoConnection.ToCollectionInfo(Database, doc);
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Workspace.Log.Info($"listCollections for {Namespace} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 状态栏那一行:<c>设计 · shop.orders · 6 个索引 · 1.9 GB</c>。随子页换摘要。
    /// </summary>
    internal void UpdateStatus()
    {
        string summary = _page switch
        {
            DesignPage.Indexes => _indexesLoaded
                ? Loc.Format("Design_StatusIndexes", ReadyIndexCount, BsonText.Bytes(_totalIndexSize))
                : Loc["Common_Loading"],
            DesignPage.Schema => IsAnalyzing
                ? Loc["Design_StatusAnalyzing"]
                : Loc.Format("Design_StatusSchema", SchemaFields.Count),
            DesignPage.Validation => Loc.Format("Design_StatusValidation", RuleLevel, RuleAction)
                                     + (IsModified ? " · " + Loc["Design_RuleUnsaved"] : ""),
            DesignPage.Options => Loc.Format("Design_StatusOptions", TtlRows.Count),
            DesignPage.Stats => _statsLoaded
                ? Loc.Format("Design_StatusStats", BsonText.Grouped(_stats?.Count ?? 0), BsonText.Bytes(_stats?.Size ?? 0))
                : Loc["Common_Loading"],
            _ => ""
        };
        StatusText = Loc.Format("Design_Status", Namespace, summary);
    }

    /// <summary>驱动 / 解析的"预期内"失败(弹提示,不崩)。</summary>
    internal static bool IsExpected(Exception ex) =>
        ex is MongoException or TimeoutException or ShellJsonException or FormatException or OperationCanceledException
            or InvalidOperationException;

    /// <summary>把失败弹成右下角的红色提示(取消不算失败)。</summary>
    internal void Fail(Exception ex)
    {
        if (ex is OperationCanceledException || _disposed)
        {
            return;
        }
        Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
    }

    /// <summary>秒数 → 人话(<c>1 天</c>、<c>2 小时 30 分</c>)。</summary>
    internal string Duration(long seconds)
    {
        Loc loc = Loc;
        if (seconds < 0)
        {
            return "";
        }
        if (seconds < 60)
        {
            return loc.Format("Design_Seconds", seconds);
        }
        if (seconds < 3600)
        {
            return loc.Format("Design_Minutes", Math.Round(seconds / 60.0, 1).ToString("0.#", CultureInfo.InvariantCulture));
        }
        if (seconds < 86400)
        {
            return loc.Format("Design_Hours", Math.Round(seconds / 3600.0, 1).ToString("0.#", CultureInfo.InvariantCulture));
        }
        return loc.Format("Design_Days", Math.Round(seconds / 86400.0, 1).ToString("0.#", CultureInfo.InvariantCulture));
    }

    /// <summary>集合名在命令文本里的写法(<c>"orders"</c>)。</summary>
    internal string QuotedCollection => BsonText.Quote(CollectionName);

    /// <summary>当前时间(UTC;单测可以替换,建议里的"未使用 N 天"按它算)。</summary>
    internal Func<DateTime> Now { get; set; } = static () => DateTime.UtcNow;

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _life.Cancel();
        _buildPoll?.Cancel();
        _tryTimer?.Stop();
        _ruleTimer?.Stop();
        base.Dispose();
    }
}
