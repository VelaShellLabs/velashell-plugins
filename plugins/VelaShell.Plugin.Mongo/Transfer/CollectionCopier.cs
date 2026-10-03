using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>目标上同名对象的处理(设计稿 21「目标动作」)。</summary>
internal enum XferAction
{
    /// <summary>覆盖:drop + 按源的选项重建。</summary>
    Overwrite,

    /// <summary>追加:目标有就往里插,没有就建。</summary>
    Append,

    /// <summary>新建:目标上不存在时建。</summary>
    Create,

    /// <summary>跳过。</summary>
    Skip
}

/// <summary>传输对象的种类。</summary>
internal enum XferObjectKind
{
    /// <summary>集合(含固定集合、时序集合、聚簇集合)。</summary>
    Collection,

    /// <summary>视图(只传定义)。</summary>
    View,

    /// <summary>GridFS 桶(<c>.files</c> + <c>.chunks</c> 两个集合)。</summary>
    Bucket
}

/// <summary>传输选项(设计稿 21 右栏「选项」)。</summary>
internal sealed record XferOptions
{
    /// <summary>保留 _id(否则由目标重新生成 —— 追加到已有集合时避免撞键)。</summary>
    public bool KeepId { get; init; } = true;

    /// <summary>传输索引(数据写完后在目标上建)。</summary>
    public bool Indexes { get; init; } = true;

    /// <summary>传输验证规则(建集合时带上 validator)。</summary>
    public bool Validation { get; init; } = true;

    /// <summary>批大小。</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>同时在途的批数。</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>源端读偏好(默认 secondaryPreferred:别把主节点读满)。</summary>
    public string ReadPreference { get; init; } = "secondaryPreferred";
}

/// <summary>要传的一个对象。</summary>
/// <param name="Name">名字(桶是桶名)。</param>
/// <param name="Kind">种类。</param>
/// <param name="Info">集合信息(桶为 <see langword="null" />)。</param>
/// <param name="Action">目标动作。</param>
internal sealed record XferItem(string Name, XferObjectKind Kind, CollectionInfo? Info, XferAction Action);

/// <summary>一个对象传完的结果。</summary>
/// <param name="Copied">写进去的文档数。</param>
/// <param name="Failed">写失败的文档数(重复键等)。</param>
/// <param name="Indexes">建了几个索引。</param>
internal sealed record XferOutcome(long Copied, long Failed, int Indexes);

/// <summary>
/// 暂停闸:批与批之间检查一次。暂停时在途的批照常写完 —— 半截的批没有意义,
/// 停在批边界上,恢复时游标从原处接着读。
/// </summary>
internal sealed class XferPauseGate
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _paused;

    /// <summary>是否暂停中。</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused is not null;
            }
        }
    }

    /// <summary>暂停。</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>恢复。</summary>
    public void Resume()
    {
        TaskCompletionSource? paused;
        lock (_gate)
        {
            paused = _paused;
            _paused = null;
        }
        paused?.TrySetResult();
    }

    /// <summary>暂停中就等到恢复(或取消)。</summary>
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _paused?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
        }
    }
}

/// <summary>
/// 把一个集合 / 视图 / GridFS 桶从源连接复制到目标连接。
/// <para>
/// 顺序是**先建集合(带选项与验证规则)→ 灌数据 → 最后建索引**:边写边维护索引比一次性建慢得多,
/// 这也是 mongorestore 的做法。数据走源端读偏好(默认 secondaryPreferred)的游标,
/// 按批 <c>insertMany(ordered: false)</c>,最多 <see cref="XferOptions.Concurrency" /> 批同时在途。
/// </para>
/// </summary>
internal static class CollectionCopier
{
    /// <summary>传一个对象。</summary>
    /// <param name="source">源连接。</param>
    /// <param name="sourceDb">源库。</param>
    /// <param name="target">目标连接。</param>
    /// <param name="targetDb">目标库。</param>
    /// <param name="item">对象。</param>
    /// <param name="options">选项。</param>
    /// <param name="gate">暂停闸。</param>
    /// <param name="onDocuments">每写完一批回报一次条数(在线程池线程上)。</param>
    /// <param name="log">日志。</param>
    /// <param name="loc">文案表(日志用)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<XferOutcome> CopyAsync(
        MongoConnection source,
        string sourceDb,
        MongoConnection target,
        string targetDb,
        XferItem item,
        XferOptions options,
        XferPauseGate gate,
        Action<long> onDocuments,
        Action<string, XferTone> log,
        Loc loc,
        CancellationToken cancellationToken)
    {
        if (item.Action == XferAction.Skip)
        {
            return new(0, 0, 0);
        }
        switch (item.Kind)
        {
            case XferObjectKind.View:
                await CopyViewAsync(target, targetDb, item, cancellationToken).ConfigureAwait(false);
                return new(0, 0, 0);
            case XferObjectKind.Bucket:
            {
                // 先 chunks 后 files:读者从 files 找文件,files 晚到就不会看见"有文件没数据"的半截桶。
                XferOutcome chunks = await CopyCollectionAsync(source, sourceDb, target, targetDb, $"{item.Name}.chunks",
                    new BsonDocument(), item.Action, options, gate, onDocuments, log, loc, cancellationToken).ConfigureAwait(false);
                XferOutcome files = await CopyCollectionAsync(source, sourceDb, target, targetDb, $"{item.Name}.files",
                    new BsonDocument(), item.Action, options, gate, onDocuments, log, loc, cancellationToken).ConfigureAwait(false);
                return new(chunks.Copied + files.Copied, chunks.Failed + files.Failed, chunks.Indexes + files.Indexes);
            }
            default:
                return await CopyCollectionAsync(source, sourceDb, target, targetDb, item.Name, item.Info?.Options ?? [],
                    item.Action, options, gate, onDocuments, log, loc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>目标库里已有的集合 / 视图名。</summary>
    public static async Task<HashSet<string>> ExistingNamesAsync(MongoConnection target, string database, CancellationToken cancellationToken)
    {
        using IAsyncCursor<string> cursor = await target.Database(database).ListCollectionNamesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        List<string> names = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. names];
    }

    private static async Task<XferOutcome> CopyCollectionAsync(
        MongoConnection source,
        string sourceDb,
        MongoConnection target,
        string targetDb,
        string name,
        BsonDocument sourceOptions,
        XferAction action,
        XferOptions options,
        XferPauseGate gate,
        Action<long> onDocuments,
        Action<string, XferTone> log,
        Loc loc,
        CancellationToken cancellationToken)
    {
        IMongoDatabase targetDatabase = target.Database(targetDb);
        bool exists = (await ExistingNamesAsync(target, targetDb, cancellationToken).ConfigureAwait(false)).Contains(name);
        bool created = false;
        switch (action)
        {
            case XferAction.Overwrite:
                if (exists)
                {
                    await targetDatabase.DropCollectionAsync(name, cancellationToken).ConfigureAwait(false);
                }
                await CreateAsync(target, targetDb, name, sourceOptions, options, cancellationToken).ConfigureAwait(false);
                created = true;
                break;
            case XferAction.Create when exists:
                throw new InvalidOperationException($"{targetDb}.{name} already exists.");
            case XferAction.Create:
            case XferAction.Append when !exists:
                await CreateAsync(target, targetDb, name, sourceOptions, options, cancellationToken).ConfigureAwait(false);
                created = true;
                break;
        }

        IMongoCollection<BsonDocument> from = source.Collection(sourceDb, name)
            .WithReadPreference(MongoConnection.ParseReadPreference(options.ReadPreference));
        IMongoCollection<BsonDocument> to = target.Collection(targetDb, name);
        long copied = 0, failed = 0;
        int concurrency = Math.Clamp(options.Concurrency, 1, 16);
        using var slots = new SemaphoreSlim(concurrency, concurrency);
        var inflight = new List<Task>();
        var errors = new List<Exception>();

        async Task InsertAsync(List<BsonDocument> documents)
        {
            try
            {
                await to.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = false }, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref copied, documents.Count);
                onDocuments(documents.Count);
            }
            catch (MongoBulkWriteException<BsonDocument> ex)
            {
                long bad = ex.WriteErrors.Count;
                Interlocked.Add(ref copied, documents.Count - bad);
                Interlocked.Add(ref failed, bad);
                onDocuments(documents.Count);
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
            finally
            {
                slots.Release();
            }
        }

        try
        {
            using IAsyncCursor<BsonDocument> cursor = await from.FindAsync(FilterDefinition<BsonDocument>.Empty,
                new FindOptions<BsonDocument> { BatchSize = options.BatchSize, Comment = "velashell-transfer" }, cancellationToken)
                .ConfigureAwait(false);
            var buffer = new List<BsonDocument>(options.BatchSize);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (BsonDocument document in cursor.Current)
                {
                    if (!options.KeepId)
                    {
                        document.Remove("_id");
                    }
                    buffer.Add(document);
                    if (buffer.Count >= options.BatchSize)
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                        inflight.Add(InsertAsync(buffer));
                        buffer = new List<BsonDocument>(options.BatchSize);
                        inflight.RemoveAll(static t => t.IsCompleted);
                        ThrowIfFailed(errors);
                    }
                }
            }
            if (buffer.Count > 0)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                inflight.Add(InsertAsync(buffer));
            }
            await Task.WhenAll(inflight).ConfigureAwait(false);
            ThrowIfFailed(errors);
        }
        finally
        {
            // 取消或出错时,在途的批稍后还会回来 Release 信号量 —— 等它们落地再离开(释放信号量),
            // 否则那几次 Release 撞上已释放的对象,在线程池里抛一个没人接的异常。
            try
            {
                await Task.WhenAll(inflight).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 真正的失败已经由上面的 try 抛出去了。
            }
        }

        int indexes = 0;
        if (options.Indexes)
        {
            indexes = await CopyIndexesAsync(source, sourceDb, target, targetDb, name, log, loc, cancellationToken).ConfigureAwait(false);
        }
        if (!created && options.Validation && sourceOptions.Contains("validator"))
        {
            log(loc.Format("Xfer_LogValidatorKept", name), XferTone.Muted);
        }
        return new(copied, failed, indexes);
    }

    private static void ThrowIfFailed(List<Exception> errors)
    {
        lock (errors)
        {
            if (errors.Count > 0)
            {
                throw errors[0];
            }
        }
    }

    /// <summary>
    /// 按源的选项建集合:固定集合、时序、聚簇、排序规则原样带上;验证规则看选项。
    /// 时序集合的 <c>bucketMaxSpanSeconds</c> 与 <c>granularity</c> 同时给会被服务器拒,有 granularity 时去掉前者。
    /// </summary>
    internal static BsonDocument CreateCommand(string name, BsonDocument sourceOptions, XferOptions options)
    {
        var command = new BsonDocument("create", name);
        foreach (BsonElement element in sourceOptions)
        {
            switch (element.Name)
            {
                case "validator" or "validationLevel" or "validationAction" when !options.Validation:
                case "viewOn" or "pipeline":
                    continue;
                case "timeseries" when element.Value is BsonDocument series:
                {
                    BsonDocument copy = series.DeepClone().AsBsonDocument;
                    if (copy.Contains("granularity"))
                    {
                        copy.Remove("bucketMaxSpanSeconds");
                        copy.Remove("bucketRoundingSeconds");
                    }
                    command[element.Name] = copy;
                    continue;
                }
                case "clusteredIndex" when element.Value is BsonDocument clustered:
                {
                    var copy = new BsonDocument
                    {
                        { "key", clustered.GetValue("key", new BsonDocument("_id", 1)) },
                        { "unique", clustered.GetValue("unique", true) }
                    };
                    if (clustered.TryGetValue("name", out BsonValue clusteredName))
                    {
                        copy["name"] = clusteredName;
                    }
                    command[element.Name] = copy;
                    continue;
                }
                default:
                    command[element.Name] = element.Value;
                    continue;
            }
        }
        return command;
    }

    private static Task CreateAsync(MongoConnection target, string database, string name, BsonDocument sourceOptions, XferOptions options,
        CancellationToken cancellationToken) =>
        target.RunCommandAsync(database, CreateCommand(name, sourceOptions, options), cancellationToken);

    private static async Task CopyViewAsync(MongoConnection target, string database, XferItem item, CancellationToken cancellationToken)
    {
        bool exists = (await ExistingNamesAsync(target, database, cancellationToken).ConfigureAwait(false)).Contains(item.Name);
        if (exists)
        {
            if (item.Action != XferAction.Overwrite)
            {
                return;
            }
            await target.Database(database).DropCollectionAsync(item.Name, cancellationToken).ConfigureAwait(false);
        }
        BsonDocument options = item.Info?.Options ?? [];
        var command = new BsonDocument
        {
            { "create", item.Name },
            { "viewOn", options.GetValue("viewOn", "") },
            { "pipeline", options.GetValue("pipeline", new BsonArray()) }
        };
        if (options.TryGetValue("collation", out BsonValue collation))
        {
            command["collation"] = collation;
        }
        await target.RunCommandAsync(database, command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CopyIndexesAsync(
        MongoConnection source,
        string sourceDb,
        MongoConnection target,
        string targetDb,
        string name,
        Action<string, XferTone> log,
        Loc loc,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BsonDocument> indexes = await source.ListIndexesAsync(sourceDb, name, cancellationToken).ConfigureAwait(false);
        var specs = new BsonArray();
        foreach (BsonDocument index in indexes)
        {
            if (index.GetValue("name", "").AsString == "_id_" || index.Contains("clustered"))
            {
                continue;
            }
            BsonDocument spec = index.DeepClone().AsBsonDocument;
            spec.Remove("ns");
            spec.Remove("v");
            specs.Add(spec);
        }
        if (specs.Count == 0)
        {
            return 0;
        }
        try
        {
            await target.RunCommandAsync(targetDb, new BsonDocument { { "createIndexes", name }, { "indexes", specs } }, cancellationToken)
                .ConfigureAwait(false);
            return specs.Count;
        }
        catch (MongoCommandException ex)
        {
            log(loc.Format("Xfer_LogIndexFailed", name, MongoConnector.Describe(ex)), XferTone.Warn);
            return 0;
        }
    }
}
