using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>详情面板的页签。</summary>
internal enum ObjectDetailTab
{
    /// <summary>常规:类型选项、统计、最近活动。</summary>
    General,

    /// <summary>DDL:能重放的 mongosh 脚本。</summary>
    Ddl,

    /// <summary>权限:当前用户对这个集合的有效动作。</summary>
    Privileges
}

/// <summary>对象列表:右侧详情面板与工具行动作。</summary>
internal sealed partial class ObjectsTabViewModel
{
    /// <summary>算"写"的那些动作(权限页上单独上色;只读用户一眼看出自己少了哪一块)。</summary>
    private static readonly HashSet<string> WriteActionNames =
    [
        with(StringComparer.Ordinal),
        "insert", "update", "remove", "createCollection", "dropCollection", "createIndex", "dropIndex", "collMod",
        "convertToCapped", "renameCollectionSameDB", "compact", "reIndex", "bypassDocumentValidation",
        "createSearchIndexes", "dropSearchIndex", "updateSearchIndex", "enableProfiler", "dropDatabase"
    ];

    private CancellationTokenSource? _detail;
    private BsonDocument? _connectionStatus;

    /// <summary>当前页签。</summary>
    public ObjectDetailTab DetailTab
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsGeneralTab), nameof(IsDdlTab), nameof(IsPrivilegesTab));
            }
        }
    }

    /// <summary>页签:常规。</summary>
    public bool IsGeneralTab { get => DetailTab == ObjectDetailTab.General; set { if (value) { DetailTab = ObjectDetailTab.General; } } }

    /// <summary>页签:DDL。</summary>
    public bool IsDdlTab { get => DetailTab == ObjectDetailTab.Ddl; set { if (value) { DetailTab = ObjectDetailTab.Ddl; } } }

    /// <summary>页签:权限。</summary>
    public bool IsPrivilegesTab { get => DetailTab == ObjectDetailTab.Privileges; set { if (value) { DetailTab = ObjectDetailTab.Privileges; } } }

    /// <summary>面板头的图标。</summary>
    public string DetailIconKey => SelectedItem?.IconKey ?? "Mongo.layout-grid";

    /// <summary>面板头的图标颜色。</summary>
    public string DetailIconToken => SelectedItem?.IconToken ?? "VelaTextTertiary";

    /// <summary>面板头的名字。</summary>
    public string DetailTitle => SelectedItem?.DisplayName ?? "";

    /// <summary>面板头的副标题(<c>时序集合 · shop</c>)。</summary>
    public string DetailSubtitle => SelectedItem is { } s ? $"{s.KindName} · {Database}" : "";

    /// <summary>底部主按钮的文字(集合 / 视图 / 存储桶各说各的)。</summary>
    public string OpenLabel => SelectedItem?.Kind switch
    {
        ObjectKind.Bucket => Loc["Obj_OpenBucket"],
        ObjectKind.View => Loc["Obj_OpenView"],
        _ => Loc["Obj_OpenCollection"]
    };

    /// <summary>「设计集合」可用(视图与桶没有可设计的结构)。</summary>
    public bool CanDesign => SelectedItem?.IsCollection == true;

    /// <summary>「在管道构建器中打开」可用。</summary>
    public bool CanPipeline => SelectedItem is { Kind: not ObjectKind.Bucket };

    /// <summary>常规页的小节。</summary>
    public ObservableCollection<ObjectFactSection> Sections { get; } = [];

    /// <summary>DDL 页的脚本。</summary>
    public string DdlText
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                CopyDdlCommand.RaiseCanExecuteChanged();
            }
        }
    } = "";

    /// <summary>权限页:当前用户(<c>ops_reader@admin</c>)。</summary>
    public string PrivilegeUser
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>权限页的提示(未开认证 / 没有任何动作 / 取不到)。</summary>
    public string PrivilegeNotice
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>权限页「对 shop.events 的有效动作」那一行标题。</summary>
    public string PrivilegeActionsTitle => SelectedItem is { } s
        ? Loc.Format("Obj_PrivActions", s.Bucket is { } b ? $"{Database}.{b.FilesCollection}" : s.Namespace)
        : "";

    /// <summary>权限页:角色。</summary>
    public ObservableCollection<string> PrivilegeRoles { get; } = [];

    /// <summary>权限页:对选中集合的有效动作。</summary>
    public ObservableCollection<ObjectPrivilege> PrivilegeActions { get; } = [];

    // ── 详情加载 ─────────────────────────────────────────────────────────

    private async Task LoadDetailAsync(ObjectItem? item)
    {
        // 只取消不释放:上一轮的驱动调用可能还挂在这个令牌上,释放掉会让它们在注册回调时抛。
        _detail?.Cancel();
        var cts = new CancellationTokenSource();
        _detail = cts;
        CancellationToken cancellationToken = cts.Token;
        Sections.Clear();
        DdlText = "";
        PrivilegeUser = "";
        PrivilegeNotice = "";
        PrivilegeRoles.Clear();
        PrivilegeActions.Clear();
        if (item is null)
        {
            return;
        }
        ObjectFact writes = new(Loc["Obj_FactWrites"], "…");
        ObjectFact last = new(Loc["Obj_FactLastDoc"], "…", "VelaShellMagenta");
        ObjectFact created = new(Loc["Obj_FactCreated"], "…", "VelaShellMagenta");
        ObjectFact[] activity = [writes, last, created];
        ShowSections(item, activity);
        try
        {
            await EnsureStatsAsync(item).WaitAsync(cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            ShowSections(item, activity);
            DdlText = await BuildDdlAsync(item, cancellationToken).ConfigureAwait(true);
            await LoadPrivilegesAsync(item, cancellationToken).ConfigureAwait(true);
            if (!item.IsView)
            {
                await FillActivityAsync(item, writes, last, created, cancellationToken).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // 详情是锦上添花:某一项取不到就让它停在「—」,不打断用户。
            foreach (ObjectFact fact in activity.Where(static f => f.Value == "…"))
            {
                fact.Value = "—";
                fact.Token = "VelaTextMuted";
            }
        }
    }

    private void ShowSections(ObjectItem item, ObjectFact[] activity)
    {
        Sections.Clear();
        foreach (ObjectFactSection section in BuildSections(item, activity))
        {
            Sections.Add(section);
        }
    }

    /// <summary>常规页的小节:类型专属选项 → 统计 → 最近活动(视图只有第一节)。</summary>
    internal IReadOnlyList<ObjectFactSection> BuildSections(ObjectItem item, IReadOnlyList<ObjectFact> activity)
    {
        var sections = new List<ObjectFactSection> { KindSection(item) };
        if (item.IsView)
        {
            return sections;
        }
        sections.Add(StatsSection(item));
        sections.Add(new(Loc["Obj_SecActivity"], activity));
        return sections;
    }

    private ObjectFactSection KindSection(ObjectItem item)
    {
        BsonDocument options = item.Info?.Options ?? [];
        var facts = new List<ObjectFact>();
        switch (item.Kind)
        {
            case ObjectKind.TimeSeries:
                {
                    BsonDocument ts = options.TryGetValue("timeseries", out BsonValue t) && t.IsBsonDocument ? t.AsBsonDocument : [];
                    facts.Add(new("timeField", ts.GetValue("timeField", "—").ToString() ?? "—", "VelaShellCyan"));
                    facts.Add(ts.TryGetValue("metaField", out BsonValue meta)
                        ? new("metaField", meta.ToString() ?? "", "VelaShellCyan")
                        : new("metaField", "—", "VelaTextMuted"));
                    facts.Add(ts.TryGetValue("granularity", out BsonValue granularity)
                        ? new("granularity", granularity.ToString() ?? "")
                        : new("bucketMaxSpanSeconds", Seconds(ts.GetValue("bucketMaxSpanSeconds", 0).ToInt64())));
                    facts.Add(Expire(options));
                    return new(Loc["Obj_SecTimeSeries"], facts);
                }
            case ObjectKind.Capped:
                {
                    long size = options.GetValue("size", 0).ToInt64();
                    facts.Add(new("size", $"{BsonText.Grouped(size)} ({ObjectItem.ShortBytes(size)})"));
                    facts.Add(options.TryGetValue("max", out BsonValue max) && max.IsNumeric
                        ? new("max", BsonText.Grouped(max.ToInt64()))
                        : new("max", "—", "VelaTextMuted"));
                    AddValidation(item, facts);
                    return new(Loc["Obj_SecCapped"], facts);
                }
            case ObjectKind.Clustered:
                {
                    BsonDocument clustered = options.TryGetValue("clusteredIndex", out BsonValue c) && c.IsBsonDocument ? c.AsBsonDocument : [];
                    facts.Add(new("clusteredIndex", BsonText.Literal(clustered.GetValue("key", new BsonDocument("_id", 1))), "VelaShellCyan"));
                    facts.Add(new("unique", clustered.GetValue("unique", true).ToBoolean() ? "true" : "false"));
                    if (options.Contains("expireAfterSeconds"))
                    {
                        facts.Add(Expire(options));
                    }
                    AddValidation(item, facts);
                    return new(Loc["Obj_SecClustered"], facts);
                }
            case ObjectKind.View:
                {
                    BsonArray pipeline = item.Info?.Pipeline ?? [];
                    facts.Add(new(Loc["Obj_FactViewOn"], item.Info?.ViewOn ?? "—", "VelaShellCyan"));
                    facts.Add(new(Loc["Obj_FactStages"], pipeline.Count.ToString(CultureInfo.InvariantCulture)));
                    if (pipeline.Count > 0)
                    {
                        facts.Add(new(Loc["Obj_FactPipeline"], string.Join(" → ", pipeline
                            .Where(static s => s.IsBsonDocument && s.AsBsonDocument.ElementCount > 0)
                            .Select(static s => s.AsBsonDocument.GetElement(0).Name))));
                    }
                    return new(Loc["Obj_SecView"], facts);
                }
            case ObjectKind.Bucket:
                {
                    GridFsBucketInfo bucket = item.Bucket!;
                    facts.Add(new(Loc["Obj_FactFilesCollection"], bucket.FilesCollection, "VelaShellCyan"));
                    facts.Add(new(Loc["Obj_FactChunksCollection"], bucket.ChunksCollection, "VelaShellCyan"));
                    if (item.ChunkStats is { } chunks)
                    {
                        facts.Add(new(Loc["Obj_FactChunks"], BsonText.Grouped(chunks.Count)));
                    }
                    return new(Loc["Obj_SecBucket"], facts);
                }
            default:
                {
                    AddValidation(item, facts);
                    if (item.Info?.Validator is null)
                    {
                        facts.Add(new(Loc["Obj_FactValidation"], Loc["Obj_None"], "VelaTextMuted"));
                    }
                    if (item.Indexes?.FirstOrDefault(static i => i.Contains("expireAfterSeconds")) is { } ttl)
                    {
                        facts.Add(new(Loc["Obj_FactTtl"],
                            $"{ttl.GetValue("name", "").AsString} · {Seconds(ttl["expireAfterSeconds"].ToInt64())}", "VelaWarning"));
                    }
                    if (options.TryGetValue("collation", out BsonValue collation) && collation.IsBsonDocument)
                    {
                        facts.Add(new(Loc["Obj_FactCollation"], collation.AsBsonDocument.GetValue("locale", "simple").ToString() ?? ""));
                    }
                    return new(Loc["Obj_SecCollection"], facts);
                }
        }
    }

    private void AddValidation(ObjectItem item, List<ObjectFact> facts)
    {
        if (item.Info?.Validator is not { } validator)
        {
            return;
        }
        facts.Add(new("validationLevel", item.Info.ValidationLevel, item.ValidationToken));
        facts.Add(new("validationAction", item.Info.ValidationAction, item.Info.ValidationAction == "error" ? "VelaWarning" : "VelaTextSecondary"));
        facts.Add(new(Loc["Obj_FactRules"], string.Join(", ", validator.Names), "VelaShellCyan"));
    }

    private ObjectFact Expire(BsonDocument options) =>
        options.TryGetValue("expireAfterSeconds", out BsonValue expire) && expire.IsNumeric
            ? new("expireAfterSeconds", $"{BsonText.Grouped(expire.ToInt64())} ({Duration(expire.ToInt64())})", "VelaWarning")
            : new("expireAfterSeconds", "—", "VelaTextMuted");

    private ObjectFactSection StatsSection(ObjectItem item)
    {
        const string pending = "…";
        bool ready = item.HasStats;
        var facts = new List<ObjectFact>
        {
            new(Loc[item.Kind == ObjectKind.Bucket ? "Obj_FactFiles" : "Detail_Documents"],
                ready ? BsonText.Grouped(item.Count ?? 0) : pending),
            new(Loc["Obj_FactDataStorage"], ready ? $"{BsonText.Bytes(item.DataSize ?? 0)} / {BsonText.Bytes(item.StorageSize ?? 0)}" : pending)
        };
        if (ready && item.StorageSize is > 0 && item.DataSize is { } data)
        {
            double ratio = (double)data / item.StorageSize.Value;
            facts.Add(new(Loc["Obj_FactCompression"], ratio.ToString("0.0", CultureInfo.InvariantCulture) + " ×",
                ratio >= 1 ? "VelaStatusConnected" : "VelaTextSecondary"));
        }
        if (item.Kind == ObjectKind.TimeSeries)
        {
            facts.Add(new(Loc["Obj_FactBuckets"], item.BucketCount is { } n ? BsonText.Grouped(n) : ready ? "—" : pending));
        }
        facts.Add(new(Loc["Obj_FactIndexes"], ready ? $"{item.IndexCount ?? 0} · {BsonText.Bytes(item.IndexSize ?? 0)}" : pending));
        return new(Loc["Obj_SecStats"], facts);
    }

    /// <summary><c>7776000</c> → <c>90 天</c>(整天 / 整时 / 整分优先)。</summary>
    internal string Duration(long seconds) => seconds switch
    {
        > 0 when seconds % 86_400 == 0 => Loc.Format("Obj_Days", BsonText.Grouped(seconds / 86_400)),
        > 0 when seconds % 3_600 == 0 => Loc.Format("Obj_Hours", BsonText.Grouped(seconds / 3_600)),
        > 0 when seconds % 60 == 0 => Loc.Format("Obj_Minutes", BsonText.Grouped(seconds / 60)),
        _ => Loc.Format("Obj_Seconds", BsonText.Grouped(seconds))
    };

    private string Seconds(long seconds) => $"{BsonText.Grouped(seconds)} s · {Duration(seconds)}";

    // ── DDL ──────────────────────────────────────────────────────────────

    /// <summary>选中对象的结构脚本(桶 = files 与 chunks 两个集合的)。</summary>
    internal async Task<string> BuildDdlAsync(ObjectItem item, CancellationToken cancellationToken)
    {
        MongoConnection connection = Workspace.Connection;
        switch (item.Kind)
        {
            case ObjectKind.View:
                return ObjectScripts.Ddl(item.Info!, []);
            case ObjectKind.Bucket:
                {
                    GridFsBucketInfo bucket = item.Bucket!;
                    var parts = new List<string>();
                    foreach (string name in new[] { bucket.FilesCollection, bucket.ChunksCollection })
                    {
                        IReadOnlyList<BsonDocument> indexes = await connection.ListIndexesAsync(Database, name, cancellationToken).ConfigureAwait(true);
                        parts.Add(ObjectScripts.Ddl(new CollectionInfo(Database, name, CollectionKind.Collection, []), indexes));
                    }
                    return string.Join("\n", parts);
                }
            default:
                {
                    item.Indexes ??= await connection.ListIndexesAsync(Database, item.Name, cancellationToken).ConfigureAwait(true);
                    return ObjectScripts.Ddl(item.Info!, item.Indexes);
                }
        }
    }

    // ── 权限 ─────────────────────────────────────────────────────────────

    private async Task LoadPrivilegesAsync(ObjectItem item, CancellationToken cancellationToken)
    {
        try
        {
            _connectionStatus ??= await Workspace.Connection.RunCommandAsync("admin", new BsonDocument
            {
                { "connectionStatus", 1 },
                { "showPrivileges", true }
            }, cancellationToken).ConfigureAwait(true);
        }
        catch (MongoCommandException)
        {
            PrivilegeNotice = Loc["Obj_PrivUnavailable"];
            return;
        }
        BsonDocument auth = _connectionStatus.GetValue("authInfo", new BsonDocument()).AsBsonDocument;
        BsonArray users = auth.GetValue("authenticatedUsers", new BsonArray()).AsBsonArray;
        if (users.Count == 0)
        {
            PrivilegeNotice = Loc["Obj_PrivNoAuth"];
            return;
        }
        PrivilegeUser = string.Join(", ", users.Select(static u => $"{u["user"].AsString}@{u["db"].AsString}"));
        foreach (BsonValue role in auth.GetValue("authenticatedUserRoles", new BsonArray()).AsBsonArray)
        {
            PrivilegeRoles.Add($"{role["role"].AsString}@{role["db"].AsString}");
        }
        string collection = item.Bucket?.FilesCollection ?? item.Name;
        IReadOnlyCollection<string> actions = EffectiveActions(auth.GetValue("authenticatedUserPrivileges", new BsonArray()).AsBsonArray, Database, collection);
        foreach (ObjectPrivilege action in actions
                     .Select(static a => new ObjectPrivilege(a, WriteActionNames.Contains(a)))
                     .OrderBy(static a => a.IsWrite)
                     .ThenBy(static a => a.Name, StringComparer.Ordinal))
        {
            PrivilegeActions.Add(action);
        }
        if (actions.Count == 0)
        {
            PrivilegeNotice = Loc["Obj_PrivNone"];
        }
    }

    /// <summary>
    /// <c>authenticatedUserPrivileges</c> 里作用在 <c>db.collection</c> 上的动作并集。
    /// 资源匹配规则与服务器一致:<c>anyResource</c> 全中;<c>db: ""</c> 是任意库;
    /// <c>collection: ""</c> 是任意**非系统**集合;<c>cluster</c> 资源与集合无关,不算。
    /// </summary>
    internal static IReadOnlyCollection<string> EffectiveActions(BsonArray privileges, string database, string collection)
    {
        var actions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (BsonValue entry in privileges)
        {
            if (!entry.IsBsonDocument)
            {
                continue;
            }
            BsonDocument privilege = entry.AsBsonDocument;
            BsonDocument resource = privilege.GetValue("resource", new BsonDocument()).AsBsonDocument;
            bool matches;
            if (resource.GetValue("anyResource", false).ToBoolean())
            {
                matches = true;
            }
            else if (resource.TryGetValue("db", out BsonValue db) && db.IsString
                     && resource.TryGetValue("collection", out BsonValue coll) && coll.IsString)
            {
                bool dbMatches = db.AsString.Length == 0 || db.AsString == database;
                bool collMatches = coll.AsString.Length == 0
                    ? !collection.StartsWith("system.", StringComparison.Ordinal)
                    : coll.AsString == collection;
                matches = dbMatches && collMatches;
            }
            else
            {
                matches = false;
            }
            if (!matches)
            {
                continue;
            }
            foreach (BsonValue action in privilege.GetValue("actions", new BsonArray()).AsBsonArray)
            {
                if (action.IsString)
                {
                    _ = actions.Add(action.AsString);
                }
            }
        }
        return actions;
    }

    // ── 最近活动 ─────────────────────────────────────────────────────────

    private async Task FillActivityAsync(ObjectItem item, ObjectFact writes, ObjectFact last, ObjectFact created, CancellationToken cancellationToken)
    {
        try
        {
            (DateTime? newest, DateTime? oldest) = await ProbeTimesAsync(item, cancellationToken).ConfigureAwait(true);
            SetTime(last, newest, recent: true);
            SetTime(created, oldest, recent: false);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            SetTime(last, null, recent: true);
            SetTime(created, null, recent: false);
        }
        try
        {
            double rate = await SampleWritesAsync(item, cancellationToken).ConfigureAwait(true);
            writes.Value = Loc.Format("Obj_PerSecond", rate >= 10
                ? BsonText.Grouped((long)Math.Round(rate))
                : rate.ToString("0.#", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            // top 要 clusterMonitor 权限;业务账号多半没有 —— 那就不显示,而不是报错。
            writes.Value = "—";
            writes.Token = "VelaTextMuted";
        }
    }

    private static void SetTime(ObjectFact fact, DateTime? utc, bool recent)
    {
        if (utc is not { } value)
        {
            fact.Value = "—";
            fact.Token = "VelaTextMuted";
            return;
        }
        DateTime local = value.ToLocalTime();
        fact.Value = recent && local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        fact.Token = "VelaShellMagenta";
    }

    /// <summary>
    /// 最近一份文档与最早一份文档的时间。全部走有索引的路径:
    /// 普通集合按 <c>_id</c>(ObjectId 自带创建时间);时序集合读它的桶集合(按 <c>_id</c> 聚簇,
    /// 桶的 <c>control.min / max</c> 就是时间范围)—— 在时序视图上按时间字段排序会把整个集合解包一遍。
    /// 一律带 3 秒 <c>maxTimeMS</c>:这是面板上的一行小字,不值得在大集合上拖住服务器。
    /// </summary>
    private async Task<(DateTime? Newest, DateTime? Oldest)> ProbeTimesAsync(ObjectItem item, CancellationToken cancellationToken)
    {
        MongoConnection connection = Workspace.Connection;
        var options = new FindOptions { MaxTime = TimeSpan.FromSeconds(3) };
        var newestFirst = new BsonDocument("_id", -1);
        var oldestFirst = new BsonDocument("_id", 1);
        switch (item.Kind)
        {
            case ObjectKind.TimeSeries:
                {
                    string timeField = item.Info!.Options.GetValue("timeseries", new BsonDocument()).AsBsonDocument.GetValue("timeField", "ts").AsString;
                    try
                    {
                        IMongoCollection<BsonDocument> buckets = connection.Collection(Database, "system.buckets." + item.Name);
                        BsonDocument? newest = await First(buckets, newestFirst).ConfigureAwait(true);
                        BsonDocument? oldest = await First(buckets, oldestFirst).ConfigureAwait(true);
                        return (TimeAt(newest, "control", "max", timeField), TimeAt(oldest, "control", "min", timeField));
                    }
                    catch (MongoCommandException)
                    {
                        // 读不了桶集合(权限只授到视图上):退回在时序视图上按时间字段排序,有超时兜底。
                        IMongoCollection<BsonDocument> view = connection.Collection(Database, item.Name);
                        BsonDocument? newest = await First(view, new BsonDocument(timeField, -1)).ConfigureAwait(true);
                        BsonDocument? oldest = await First(view, new BsonDocument(timeField, 1)).ConfigureAwait(true);
                        return (TimeAt(newest, timeField), TimeAt(oldest, timeField));
                    }
                }
            case ObjectKind.Bucket:
                {
                    IMongoCollection<BsonDocument> files = connection.Collection(Database, item.Bucket!.FilesCollection);
                    BsonDocument? newest = await First(files, newestFirst).ConfigureAwait(true);
                    BsonDocument? oldest = await First(files, oldestFirst).ConfigureAwait(true);
                    return (TimeAt(newest, "uploadDate") ?? IdTime(newest), IdTime(oldest) ?? TimeAt(oldest, "uploadDate"));
                }
            default:
                {
                    IMongoCollection<BsonDocument> collection = connection.Collection(Database, item.Name);
                    // 固定集合按插入序($natural)才是"最近";其余按 _id(ObjectId 单调增)。
                    BsonDocument newestSort = item.Kind == ObjectKind.Capped ? new BsonDocument("$natural", -1) : newestFirst;
                    BsonDocument oldestSort = item.Kind == ObjectKind.Capped ? new BsonDocument("$natural", 1) : oldestFirst;
                    BsonDocument? newest = await First(collection, newestSort).ConfigureAwait(true);
                    BsonDocument? oldest = await First(collection, oldestSort).ConfigureAwait(true);
                    return (IdTime(newest), IdTime(oldest));
                }
        }

        async Task<BsonDocument?> First(IMongoCollection<BsonDocument> source, BsonDocument sort) =>
            await source.Find(FilterDefinition<BsonDocument>.Empty, options).Sort(sort).Limit(1)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(true);
    }

    private static DateTime? IdTime(BsonDocument? doc) =>
        doc?.GetValue("_id", BsonNull.Value) is BsonObjectId id ? id.Value.CreationTime : null;

    private static DateTime? TimeAt(BsonDocument? doc, params string[] path)
    {
        BsonValue? value = doc;
        foreach (string step in path)
        {
            if (value is not BsonDocument d || !d.TryGetValue(step, out value))
            {
                return null;
            }
        }
        return value is BsonDateTime date ? date.ToUniversalTime() : null;
    }

    /// <summary>
    /// 写入速率:<c>top</c> 两次采样(间隔 1 秒)求差。<c>top</c> 的计数是服务器启动以来的累计值,
    /// 单次读数没有意义,差分才是"现在每秒写多少"。
    /// </summary>
    private async Task<double> SampleWritesAsync(ObjectItem item, CancellationToken cancellationToken)
    {
        string ns = item.Bucket is { } bucket ? $"{Database}.{bucket.FilesCollection}" : item.Namespace;
        long first = await TopWritesAsync(ns, cancellationToken).ConfigureAwait(true);
        var watch = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(true);
        long second = await TopWritesAsync(ns, cancellationToken).ConfigureAwait(true);
        return Math.Max(0, second - first) / Math.Max(0.001, watch.Elapsed.TotalSeconds);
    }

    private async Task<long> TopWritesAsync(string ns, CancellationToken cancellationToken)
    {
        BsonDocument top = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument("top", 1), cancellationToken).ConfigureAwait(true);
        if (top.GetValue("totals", new BsonDocument()).AsBsonDocument.TryGetValue(ns, out BsonValue entry) && entry.IsBsonDocument)
        {
            return new[] { "insert", "update", "remove" }
                .Sum(op => entry.AsBsonDocument.TryGetValue(op, out BsonValue counter) && counter.IsBsonDocument
                    ? counter.AsBsonDocument.GetValue("count", 0).ToInt64()
                    : 0);
        }
        return 0;
    }

    // ── 动作 ─────────────────────────────────────────────────────────────

    /// <summary>打开选中的对象(双击 / 回车 / 主按钮)。</summary>
    internal void OpenSelected()
    {
        switch (SelectedItem)
        {
            case null:
                return;
            case { Kind: ObjectKind.Bucket } bucket:
                Workspace.OpenGridFs(Database, bucket.Name);
                return;
            default:
                Workspace.OpenCollection(Database, SelectedItem.Name);
                return;
        }
    }

    private void Design(DesignPage page)
    {
        if (SelectedItem is { IsCollection: true } item)
        {
            Workspace.OpenDesign(Database, item.Name, page);
        }
    }

    private void OpenPipeline()
    {
        if (SelectedItem is { Kind: not ObjectKind.Bucket } item)
        {
            Workspace.OpenPipeline(Database, item.Name);
        }
    }

    private void OpenQuery() =>
        Workspace.OpenQuery(Database, SelectedItem is { Kind: not ObjectKind.Bucket } item
            ? $"{ObjectScripts.CollectionRef(item.Name)}.find({{}}).limit(50)"
            : null);

    private void Import()
    {
        if (SelectedItem is { IsCollection: true } item)
        {
            Workspace.ShowDialog(new ImportWizardViewModel(Workspace, Database, item.Name));
        }
    }

    private void Export() =>
        Workspace.ShowDialog(new ExportWizardViewModel(Workspace, Database, SelectedItem is { Kind: not ObjectKind.Bucket } item ? item.Name : null, null, null));

    private async Task CopyStructureAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }
        try
        {
            string script = await BuildDdlAsync(item, CancellationToken.None).ConfigureAwait(true);
            await Workspace.CopyAsync(script).ConfigureAwait(true);
            Workspace.Toast(new()
            {
                Title = Loc.Format("Obj_CopiedDdl", item.DisplayName),
                Detail = Loc.Format("Obj_CopiedDdlDetail", script.Count(static c => c == '\n') + 1),
                Kind = ToastKind.Success
            });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    private async Task CopyDdlAsync()
    {
        await Workspace.CopyAsync(DdlText).ConfigureAwait(true);
        Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
    }

    /// <summary>删除选中的集合 / 视图 / 桶:先过写护栏,再确认(有数据或写前确认时手打名称),再删。</summary>
    private async Task DropSelectedAsync()
    {
        if (SelectedItem is not { } item || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        await EnsureStatsAsync(item).ConfigureAwait(true);
        var facts = new List<ConfirmFact>();
        if (!item.IsView && item.HasStats)
        {
            facts.Add(new(Loc[item.Kind == ObjectKind.Bucket ? "Obj_FactFiles" : "Detail_Documents"], BsonText.Grouped(item.Count ?? 0)));
            facts.Add(new(Loc["Confirm_DataSize"], BsonText.Bytes(item.DataSize ?? 0)));
        }
        string title = item.Kind switch
        {
            ObjectKind.View => Loc["Confirm_DropViewTitle"],
            ObjectKind.Bucket => Loc["Obj_DropBucketTitle"],
            _ => Loc["Confirm_DropTitle"]
        };
        string message = item.Kind switch
        {
            ObjectKind.View => Loc.Format("Confirm_DropViewBody", item.Namespace),
            ObjectKind.Bucket => Loc.Format("Obj_DropBucketBody", item.Namespace),
            _ => Loc.Format("Confirm_DropBody", item.Namespace, item.IndexCount ?? 0)
        };
        bool hasData = (item.Count ?? 0) > 0;
        bool confirmed = await Workspace.ConfirmAsync(new()
        {
            Title = title,
            Message = message,
            ConfirmLabel = title,
            Facts = facts,
            // 与对象树的删除同口径:写前确认开着,或这次删的东西里有数据,都要手打名称。
            TypeToConfirm = Workspace.Guard.ConfirmWrites || hasData ? item.Name : null,
            AsideLabel = item.IsCollection ? Loc["Confirm_BackupFirst"] : null,
            AsideAction = item.IsCollection
                ? () => Workspace.ShowDialog(new ExportWizardViewModel(Workspace, Database, item.Name, null, ExportFormat.BsonDump))
                : null
        }).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            IMongoDatabase database = Workspace.Connection.Database(Database);
            if (item.Bucket is { } bucket)
            {
                await database.DropCollectionAsync(bucket.FilesCollection).ConfigureAwait(true);
                await database.DropCollectionAsync(bucket.ChunksCollection).ConfigureAwait(true);
            }
            else
            {
                await database.DropCollectionAsync(item.Name).ConfigureAwait(true);
            }
            Workspace.Toast(new() { Title = Loc.Format("Confirm_Dropped", item.Namespace), Kind = ToastKind.Success });
            CloseTabsOf(item);
            SelectedItem = null;
            await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    /// <summary>
    /// 删掉之后,还开着的那个集合的网格 / 设计 / 管道标签都指向一个不存在的东西了 —— 关掉它们
    /// (外壳从对象树删除时也这么做;只关本连接的,别的连接里同名的集合不相干)。
    /// </summary>
    private void CloseTabsOf(ObjectItem item) =>
        (Workspace as MongoSession)?.CloseTabsOf(item.Namespace, except: this);

    /// <summary>清空集合(<c>deleteMany({})</c>):索引与验证规则保留。</summary>
    private async Task EmptySelectedAsync()
    {
        if (SelectedItem is not { Kind: ObjectKind.Collection or ObjectKind.TimeSeries or ObjectKind.Clustered } item
            || !Workspace.EnsureWritable(Database))
        {
            return;
        }
        try
        {
            long? count = await Workspace.Connection.EstimatedCountAsync(Database, item.Name).ConfigureAwait(true);
            if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Tree_EmptyTitle"],
                Message = Loc.Format("Tree_EmptyBody", item.Namespace, BsonText.Grouped(count ?? 0)),
                ConfirmLabel = Loc["Tree_EmptyConfirm"],
                IconKey = "Mongo.eraser",
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? item.Name : null
            }).ConfigureAwait(true))
            {
                return;
            }
            DeleteResult result = await Workspace.Connection.Collection(Database, item.Name)
                .DeleteManyAsync(FilterDefinition<BsonDocument>.Empty).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc.Format("Tree_EmptyDone", BsonText.Grouped(result.DeletedCount)), Kind = ToastKind.Success });
            _ = _statsTasks.TryRemove(item, out _);
            await EnsureStatsAsync(item).ConfigureAwait(true);
            if (ReferenceEquals(SelectedItem, item))
            {
                _ = LoadDetailAsync(item);
            }
            await Workspace.RefreshTreeAsync(Database).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }
}
