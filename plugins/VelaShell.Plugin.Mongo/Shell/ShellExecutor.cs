using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Shell;

/// <summary>要确认的写操作种类。</summary>
internal enum ShellConfirmKind
{
    /// <summary><c>updateMany</c>。</summary>
    UpdateMany,

    /// <summary><c>deleteMany</c>。</summary>
    DeleteMany,

    /// <summary><c>bulkWrite</c>(含批量更新 / 删除)。</summary>
    BulkWrite,

    /// <summary><c>drop()</c>(要手打集合名)。</summary>
    Drop,

    /// <summary><c>dropIndex</c>。</summary>
    DropIndex,

    /// <summary><c>dropDatabase()</c>(要手打库名)。</summary>
    DropDatabase
}

/// <summary>一次写前确认的内容。</summary>
/// <param name="Kind">种类。</param>
/// <param name="Database">库。</param>
/// <param name="Collection">集合;库级为 <see langword="null" />。</param>
/// <param name="Filter">筛选(批量更新 / 删除)。</param>
/// <param name="Affected">预计影响的文档数;没数为 <see langword="null" />。</param>
/// <param name="Detail">补充说明(索引名之类)。</param>
internal sealed record ShellConfirmation(ShellConfirmKind Kind, string Database, string? Collection, BsonDocument? Filter, long? Affected, string? Detail = null);

/// <summary>
/// 执行器与外壳护栏之间的那层:只读拦截与写前确认。查询标签页用 <c>IMongoWorkspace</c> 实现它,
/// 单测用一个桩 —— 执行器因此不认识界面。
/// </summary>
internal interface IShellGuard
{
    /// <summary>写前要不要确认(连接设置「写操作前二次确认」)。</summary>
    bool ConfirmWrites { get; }

    /// <summary>不提供 dropDatabase。</summary>
    bool DisableDropDatabase { get; }

    /// <summary>只读 / 无权限时拦下(并自己提示用户),返回 <see langword="false" />。</summary>
    bool EnsureWritable(string database);

    /// <summary>确认一次危险写;用户取消返回 <see langword="false" />。</summary>
    Task<bool> ConfirmAsync(ShellConfirmation confirmation);
}

/// <summary>一次运行的选项。</summary>
internal sealed record ShellRunOptions
{
    /// <summary>默认 maxTimeMS(语句里写了 <c>.maxTimeMS()</c> 以语句为准);0 = 不限。</summary>
    public int MaxTimeMs { get; init; }

    /// <summary>
    /// 打在每个操作上的 <c>comment</c>。「停止」按钮靠它在 <c>currentOp</c> 里找到这一批操作并 killOp ——
    /// 客户端取消令牌只能放弃等待,服务器那边照跑不误。
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>单条语句最多取回几份文档。</summary>
    public int MaxDocuments { get; init; } = ShellExecutor.DefaultMaxDocuments;
}

/// <summary>结果的种类。</summary>
internal enum ShellResultKind
{
    /// <summary>一批文档(查询、聚合、命令返回的一份文档)。</summary>
    Documents,

    /// <summary>执行计划。</summary>
    Explain,

    /// <summary>只有一句话(<c>use</c>、写操作被取消)。</summary>
    Message
}

/// <summary>一条语句的执行结果。</summary>
internal sealed record ShellResult
{
    /// <summary>种类。</summary>
    public required ShellResultKind Kind { get; init; }

    /// <summary>操作名(<c>find</c>、<c>aggregate</c>、<c>use</c>)。</summary>
    public required string Operation { get; init; }

    /// <summary>执行时的库。</summary>
    public required string Database { get; init; }

    /// <summary>集合;库级为 <see langword="null" />。</summary>
    public string? Collection { get; init; }

    /// <summary>文档。</summary>
    public IReadOnlyList<BsonDocument> Documents { get; init; } = [];

    /// <summary>超过单条语句上限被截断了。</summary>
    public bool Truncated { get; init; }

    /// <summary>执行计划原文(<see cref="ShellResultKind.Explain" />)。</summary>
    public BsonDocument? Explain { get; init; }

    /// <summary>一句话摘要(消息页)。</summary>
    public string Message { get; init; } = "";

    /// <summary>耗时。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>find 的完整请求(导出向导、hint 对比用)。</summary>
    public FindRequest? Query { get; init; }

    /// <summary>aggregate 的管道。</summary>
    public BsonArray? Pipeline { get; init; }

    /// <summary>改了库 / 集合目录(建删改名之后外壳要刷对象树)。</summary>
    public bool ChangesCatalog { get; init; }

    /// <summary>用户在确认框里取消了。</summary>
    public bool Declined { get; init; }

    /// <summary>命名空间(<c>shop.orders</c>)。</summary>
    public string Namespace => Collection is null ? Database : $"{Database}.{Collection}";
}

/// <summary>执行期的语义错误(参数形状不对、不支持的写法)。</summary>
/// <param name="message">给人看的一句话(已本地化)。</param>
internal sealed class ShellExecutionException(string message) : Exception(message);

/// <summary>
/// 跑一条解析好的语句。
/// <para>
/// 每条语句**单独**计时、单独取结果(最多 <see cref="DefaultMaxDocuments" /> 份 —— 查询编辑器不是导出工具,
/// 一个忘了写 limit 的 find 不该把几百万份文档拖进内存);写操作先过 <see cref="IShellGuard" />。
/// 驱动异常原样抛出,由调用方翻成提示 —— 执行器不认识界面。
/// </para>
/// </summary>
internal sealed class ShellExecutor(MongoConnection connection, IShellGuard guard, Loc loc)
{
    /// <summary>单条语句的默认结果上限。</summary>
    public const int DefaultMaxDocuments = 1000;

    /// <summary>执行一条语句。</summary>
    /// <param name="command">语句。</param>
    /// <param name="session">会话(当前库;<c>use</c> 会改它)。</param>
    /// <param name="options">选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<ShellResult> ExecuteAsync(ShellCommand command, ShellSession session, ShellRunOptions options,
        CancellationToken cancellationToken = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        ShellResult result = command.Kind switch
        {
            ShellCommandKind.Use => Use(command, session),
            ShellCommandKind.Show => await ShowAsync(command, session, cancellationToken).ConfigureAwait(false),
            ShellCommandKind.Database when command.Explain is null =>
                await DatabaseAsync(command, command.Database ?? session.Database, options, cancellationToken).ConfigureAwait(false),
            _ when command.Explain is { } verbosity =>
                await ExplainAsync(command, command.Database ?? session.Database, verbosity, options, null, cancellationToken).ConfigureAwait(false),
            _ => await CollectionAsync(command, command.Database ?? session.Database, options, cancellationToken).ConfigureAwait(false)
        };
        return result with { Elapsed = watch.Elapsed };
    }

    // ── use / show ──────────────────────────────────────────────────────────

    private ShellResult Use(ShellCommand command, ShellSession session)
    {
        session.Database = command.Target!;
        return new ShellResult
        {
            Kind = ShellResultKind.Message,
            Operation = "use",
            Database = session.Database,
            Message = loc.Format("Query_MsgUse", session.Database)
        };
    }

    private async Task<ShellResult> ShowAsync(ShellCommand command, ShellSession session, CancellationToken cancellationToken)
    {
        if (command.Target == "dbs")
        {
            IReadOnlyList<DatabaseInfo> databases = await connection.ListDatabasesAsync(cancellationToken).ConfigureAwait(false);
            return Documents(command, session.Database, null,
                [.. databases.Select(static d => new BsonDocument { { "name", d.Name }, { "sizeOnDisk", d.SizeOnDisk }, { "empty", d.Empty } })],
                false);
        }
        IReadOnlyList<CollectionInfo> collections = await connection.ListCollectionsAsync(session.Database, cancellationToken).ConfigureAwait(false);
        return Documents(command, session.Database, null,
            [.. collections.Select(static c => new BsonDocument { { "name", c.Name }, { "type", c.Kind.ToString() } })], false);
    }

    // ── 库级 ────────────────────────────────────────────────────────────────

    private async Task<ShellResult> DatabaseAsync(ShellCommand command, string database, ShellRunOptions options, CancellationToken cancellationToken)
    {
        ShellCall method = command.Method!;
        IMongoDatabase db = connection.Database(database);
        switch (method.Name)
        {
            case "getCollectionNames":
            {
                IReadOnlyList<CollectionInfo> collections = await connection.ListCollectionsAsync(database, cancellationToken).ConfigureAwait(false);
                return Documents(command, database, null, [.. collections.Select(static c => new BsonDocument("name", c.Name))], false);
            }
            case "getCollectionInfos":
            {
                var listOptions = new ListCollectionsOptions { Filter = method.Document(0) ?? [] };
                using IAsyncCursor<BsonDocument> cursor = await db.ListCollectionsAsync(listOptions, cancellationToken).ConfigureAwait(false);
                return Documents(command, database, null, await cursor.ToListAsync(cancellationToken).ConfigureAwait(false), false);
            }
            case "stats":
            {
                var stats = new BsonDocument("dbStats", 1);
                if (method.Arg(0) is { IsNumeric: true } scale)
                {
                    stats["scale"] = scale;
                }
                return Single(command, database, null, await connection.RunCommandAsync(database, stats, cancellationToken).ConfigureAwait(false));
            }
            case "runCommand" or "adminCommand":
            {
                string target = method.Name == "adminCommand" ? "admin" : database;
                BsonDocument body = method.Arg(0) switch
                {
                    BsonDocument d => d,
                    BsonString s => new BsonDocument(s.Value, 1),
                    _ => throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", method.Name))
                };
                if (body.ElementCount > 0 && ShellParser.WriteCommands.Contains(body.GetElement(0).Name))
                {
                    if (!guard.EnsureWritable(target))
                    {
                        return Blocked(command, database);
                    }
                    if (body.GetElement(0).Name.Equals("dropDatabase", StringComparison.OrdinalIgnoreCase)
                        && !await ConfirmDropDatabaseAsync(target).ConfigureAwait(false))
                    {
                        return Declined(command, target);
                    }
                }
                BsonDocument reply = await connection.RunCommandAsync(target, body, cancellationToken).ConfigureAwait(false);
                bool catalog = body.ElementCount > 0 && body.GetElement(0).Name is "create" or "drop" or "renameCollection" or "dropDatabase";
                return Single(command, target, null, reply) with { ChangesCatalog = catalog };
            }
            case "createCollection" or "createView":
            {
                if (method.Arg(0) is not BsonString name)
                {
                    throw new ShellExecutionException(loc.Format("Query_ErrNeedsName", method.Name));
                }
                if (!guard.EnsureWritable(database))
                {
                    return Blocked(command, database);
                }
                var create = new BsonDocument("create", name.Value);
                if (method.Name == "createView")
                {
                    create["viewOn"] = method.Arg(1) as BsonString ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsName", "viewOn"));
                    create["pipeline"] = method.Arg(2) as BsonArray ?? [];
                    create.Merge(method.Document(3) ?? [], overwriteExistingElements: false);
                }
                else
                {
                    create.Merge(method.Document(1) ?? [], overwriteExistingElements: false);
                }
                BsonDocument reply = await connection.RunCommandAsync(database, create, cancellationToken).ConfigureAwait(false);
                return Single(command, database, name.Value, reply) with { ChangesCatalog = true };
            }
            case "dropDatabase":
            {
                if (guard.DisableDropDatabase)
                {
                    throw new ShellExecutionException(loc["Query_ErrDropDatabaseDisabled"]);
                }
                if (!guard.EnsureWritable(database))
                {
                    return Blocked(command, database);
                }
                if (!await ConfirmDropDatabaseAsync(database).ConfigureAwait(false))
                {
                    return Declined(command, database);
                }
                BsonDocument reply = await connection.RunCommandAsync(database, new BsonDocument("dropDatabase", 1), cancellationToken).ConfigureAwait(false);
                return Single(command, database, null, reply) with { ChangesCatalog = true };
            }
            case "getName":
                return Single(command, database, null, new BsonDocument("name", database));
            case "version":
                return Single(command, database, null, new BsonDocument("version", connection.Server.Version));
            case "serverStatus" or "hostInfo" or "listCommands":
                return Single(command, database, null,
                    await connection.RunCommandAsync("admin", new BsonDocument(method.Name, 1), cancellationToken).ConfigureAwait(false));
            case "getProfilingStatus":
                return Single(command, database, null,
                    await connection.RunCommandAsync(database, new BsonDocument("profile", -1), cancellationToken).ConfigureAwait(false));
            case "currentOp":
            {
                var current = new BsonDocument("currentOp", 1);
                current.Merge(method.Document(0) ?? [], overwriteExistingElements: true);
                BsonDocument reply = await connection.RunCommandAsync("admin", current, cancellationToken).ConfigureAwait(false);
                return Documents(command, database, null, Docs(reply.GetValue("inprog", new BsonArray())), false);
            }
            case "getUsers" or "getRoles":
            {
                string commandName = method.Name == "getUsers" ? "usersInfo" : "rolesInfo";
                BsonDocument reply = await connection.RunCommandAsync(database, new BsonDocument(commandName, 1), cancellationToken).ConfigureAwait(false);
                return Documents(command, database, null, Docs(reply.GetValue(method.Name == "getUsers" ? "users" : "roles", new BsonArray())), false);
            }
            case "aggregate":
            {
                BsonArray pipeline = method.Arg(0) as BsonArray ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsPipeline", "aggregate"));
                var aggregateOptions = new AggregateOptions { MaxTime = MaxTime(options.MaxTimeMs), Comment = Comment(options) };
                using IAsyncCursor<BsonDocument> cursor = await db.AggregateAsync(
                    PipelineDefinition<NoPipelineInput, BsonDocument>.Create(pipeline.Select(static s => s.AsBsonDocument)),
                    aggregateOptions, cancellationToken).ConfigureAwait(false);
                (List<BsonDocument> docs, bool truncated) = await DrainAsync(cursor, options.MaxDocuments, cancellationToken).ConfigureAwait(false);
                return Documents(command, database, null, docs, truncated) with { Pipeline = pipeline };
            }
            default:
                throw new ShellExecutionException(loc.Format("Query_ErrUnsupportedMethod", "db." + method.Name));
        }
    }

    // ── 集合级 ──────────────────────────────────────────────────────────────

    private async Task<ShellResult> CollectionAsync(ShellCommand command, string database, ShellRunOptions options, CancellationToken cancellationToken)
    {
        ShellCall method = command.Method!;
        string name = command.Collection!;
        IMongoCollection<BsonDocument> collection = connection.Collection(database, name);
        TimeSpan? maxTime = MaxTime(command.Modifier("maxTimeMS")?.Arg(0) is { IsNumeric: true } ms ? ms.ToInt32() : options.MaxTimeMs);
        BsonValue? comment = Comment(options);

        if (ShellParser.CollectionWrites.Contains(method.Name) && !guard.EnsureWritable(database))
        {
            return Blocked(command, database);
        }

        switch (method.Name)
        {
            case "find" or "findOne":
                return await FindAsync(command, collection, database, maxTime, comment, options, cancellationToken).ConfigureAwait(false);

            case "aggregate":
            {
                BsonArray pipeline = Pipeline(method);
                bool writes = pipeline.Any(static s => s is BsonDocument d && (d.Contains("$out") || d.Contains("$merge")));
                if (writes && !guard.EnsureWritable(database))
                {
                    return Blocked(command, database);
                }
                BsonDocument settings = method.Document(1) ?? [];
                var aggregateOptions = new AggregateOptions
                {
                    AllowDiskUse = settings.TryGetValue("allowDiskUse", out BsonValue disk) ? disk.ToBoolean()
                        : command.Modifier("allowDiskUse") is not null ? true : null,
                    MaxTime = settings.TryGetValue("maxTimeMS", out BsonValue max) && max.IsNumeric ? MaxTime(max.ToInt32()) : maxTime,
                    Hint = settings.GetValue("hint", null) ?? command.Modifier("hint")?.Arg(0),
                    Collation = Collation(settings.GetValue("collation", null) ?? command.Modifier("collation")?.Arg(0)),
                    Comment = comment,
                    BatchSize = settings.TryGetValue("batchSize", out BsonValue batch) && batch.IsNumeric ? batch.ToInt32() : null
                };
                using IAsyncCursor<BsonDocument> cursor = await collection.AggregateAsync(
                    PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline.Select(static s => s.AsBsonDocument)),
                    aggregateOptions, cancellationToken).ConfigureAwait(false);
                (List<BsonDocument> docs, bool truncated) = await DrainAsync(cursor, options.MaxDocuments, cancellationToken).ConfigureAwait(false);
                return Documents(command, database, name, docs, truncated) with { Pipeline = pipeline, ChangesCatalog = writes };
            }

            case "countDocuments":
            {
                BsonDocument settings = method.Document(1) ?? [];
                var countOptions = new CountOptions
                {
                    Skip = settings.TryGetValue("skip", out BsonValue skip) && skip.IsNumeric ? skip.ToInt64() : null,
                    Limit = settings.TryGetValue("limit", out BsonValue limit) && limit.IsNumeric ? limit.ToInt64() : null,
                    Hint = settings.GetValue("hint", null),
                    MaxTime = maxTime,
                    Comment = comment
                };
                long count = await collection.CountDocumentsAsync(Filter(method, 0), countOptions, cancellationToken).ConfigureAwait(false);
                return Single(command, database, name, new BsonDocument("count", count));
            }

            case "estimatedDocumentCount":
            {
                long count = await collection.EstimatedDocumentCountAsync(
                    new EstimatedDocumentCountOptions { MaxTime = maxTime, Comment = comment }, cancellationToken).ConfigureAwait(false);
                return Single(command, database, name, new BsonDocument("count", count));
            }

            case "distinct":
            {
                string field = method.Arg(0) is BsonString f ? f.Value : throw new ShellExecutionException(loc.Format("Query_ErrNeedsName", "distinct"));
                using IAsyncCursor<BsonValue> cursor = await collection.DistinctAsync(
                    new StringFieldDefinition<BsonDocument, BsonValue>(field), Filter(method, 1),
                    new DistinctOptions { MaxTime = maxTime, Comment = comment }, cancellationToken).ConfigureAwait(false);
                List<BsonValue> values = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
                bool truncated = values.Count > options.MaxDocuments;
                return Documents(command, database, name,
                    [.. values.Take(options.MaxDocuments).Select(static v => new BsonDocument("value", v))], truncated);
            }

            case "insertOne" or "insert" when method.Arg(0) is BsonDocument document:
            {
                await collection.InsertOneAsync(document, new InsertOneOptions { Comment = comment }, cancellationToken).ConfigureAwait(false);
                return Write(command, database, name, new BsonDocument { { "acknowledged", true }, { "insertedId", document.GetValue("_id", BsonNull.Value) } },
                    loc.Format("Query_MsgInserted", 1));
            }

            case "insertOne":
                throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", method.Name));

            case "insertMany" or "insert":
            {
                BsonArray array = method.Arg(0) as BsonArray ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsArray", method.Name));
                List<BsonDocument> documents = [.. array.Select(static d => d.AsBsonDocument)];
                bool ordered = method.Document(1)?.GetValue("ordered", true).ToBoolean() ?? true;
                await collection.InsertManyAsync(documents, new InsertManyOptions { IsOrdered = ordered, Comment = comment }, cancellationToken)
                    .ConfigureAwait(false);
                return Write(command, database, name,
                    new BsonDocument { { "acknowledged", true }, { "insertedIds", new BsonArray(documents.Select(static d => d.GetValue("_id", BsonNull.Value))) } },
                    loc.Format("Query_MsgInserted", documents.Count));
            }

            case "updateOne" or "updateMany" or "update":
            {
                BsonDocument filter = Filter(method, 0);
                BsonDocument settings = method.Document(2) ?? [];
                bool many = method.Name == "updateMany" || method.Name == "update" && settings.GetValue("multi", false).ToBoolean();
                if (many && !await ConfirmAsync(ShellConfirmKind.UpdateMany, collection, database, name, filter, cancellationToken).ConfigureAwait(false))
                {
                    return Declined(command, database, name);
                }
                UpdateDefinition<BsonDocument> update = Update(method.Arg(1), method.Name);
                var updateOptions = new UpdateOptions
                {
                    IsUpsert = settings.GetValue("upsert", false).ToBoolean(),
                    ArrayFilters = settings.GetValue("arrayFilters", null) is BsonArray filters
                        ? [.. filters.Select(static f => new BsonDocumentArrayFilterDefinition<BsonDocument>(f.AsBsonDocument))]
                        : null,
                    Hint = settings.GetValue("hint", null),
                    Collation = Collation(settings.GetValue("collation", null)),
                    Comment = comment
                };
                UpdateResult result = many
                    ? await collection.UpdateManyAsync(filter, update, updateOptions, cancellationToken).ConfigureAwait(false)
                    : await collection.UpdateOneAsync(filter, update, updateOptions, cancellationToken).ConfigureAwait(false);
                return Write(command, database, name, UpdateReply(result), loc.Format("Query_MsgUpdated", result.MatchedCount, result.ModifiedCount));
            }

            case "replaceOne":
            {
                BsonDocument replacement = method.Arg(1) as BsonDocument ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", "replaceOne"));
                BsonDocument settings = method.Document(2) ?? [];
                ReplaceOneResult result = await collection.ReplaceOneAsync(Filter(method, 0), replacement, new ReplaceOptions
                {
                    IsUpsert = settings.GetValue("upsert", false).ToBoolean(),
                    Hint = settings.GetValue("hint", null),
                    Collation = Collation(settings.GetValue("collation", null)),
                    Comment = comment
                }, cancellationToken).ConfigureAwait(false);
                var reply = new BsonDocument
                {
                    { "acknowledged", result.IsAcknowledged },
                    { "matchedCount", result.IsAcknowledged ? result.MatchedCount : 0 },
                    { "modifiedCount", result.IsAcknowledged && result.IsModifiedCountAvailable ? result.ModifiedCount : 0 },
                    { "upsertedId", result.IsAcknowledged ? result.UpsertedId ?? BsonNull.Value : BsonNull.Value }
                };
                return Write(command, database, name, reply, loc.Format("Query_MsgUpdated", reply["matchedCount"], reply["modifiedCount"]));
            }

            case "deleteOne" or "deleteMany" or "remove":
            {
                BsonDocument filter = Filter(method, 0);
                bool many = method.Name == "deleteMany" || method.Name == "remove" && method.Arg(1) is not BsonBoolean { Value: true }
                            && method.Document(1)?.GetValue("justOne", false).ToBoolean() != true;
                if (many && !await ConfirmAsync(ShellConfirmKind.DeleteMany, collection, database, name, filter, cancellationToken).ConfigureAwait(false))
                {
                    return Declined(command, database, name);
                }
                BsonDocument settings = method.Document(1) ?? [];
                var deleteOptions = new DeleteOptions { Hint = settings.GetValue("hint", null), Collation = Collation(settings.GetValue("collation", null)), Comment = comment };
                DeleteResult result = many
                    ? await collection.DeleteManyAsync(filter, deleteOptions, cancellationToken).ConfigureAwait(false)
                    : await collection.DeleteOneAsync(filter, deleteOptions, cancellationToken).ConfigureAwait(false);
                long deleted = result.IsAcknowledged ? result.DeletedCount : 0;
                return Write(command, database, name, new BsonDocument { { "acknowledged", result.IsAcknowledged }, { "deletedCount", deleted } },
                    loc.Format("Query_MsgDeleted", deleted));
            }

            case "findOneAndUpdate" or "findOneAndReplace" or "findOneAndDelete":
                return await FindOneAndAsync(command, collection, database, name, maxTime, comment, cancellationToken).ConfigureAwait(false);

            case "bulkWrite":
                return await BulkWriteAsync(command, collection, database, name, comment, cancellationToken).ConfigureAwait(false);

            case "createIndex" or "createIndexes":
            {
                IEnumerable<BsonDocument> keys = method.Name == "createIndexes"
                    ? (method.Arg(0) as BsonArray ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsArray", method.Name))).Select(static k => k.AsBsonDocument)
                    : [method.Document(0) ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", method.Name))];
                BsonDocument indexOptions = method.Document(1) ?? [];
                var indexes = new BsonArray();
                foreach (BsonDocument key in keys)
                {
                    var spec = new BsonDocument { { "key", key }, { "name", indexOptions.GetValue("name", IndexName(key)) } };
                    spec.Merge(indexOptions, overwriteExistingElements: false);
                    indexes.Add(spec);
                }
                var createIndexes = new BsonDocument { { "createIndexes", name }, { "indexes", indexes } };
                if (comment is not null)
                {
                    createIndexes["comment"] = comment;
                }
                BsonDocument reply = await connection.RunCommandAsync(database, createIndexes, cancellationToken).ConfigureAwait(false);
                return Write(command, database, name, reply, loc.Format("Query_MsgIndexCreated", string.Join(", ", indexes.Select(static i => i["name"].ToString()))));
            }

            case "dropIndex" or "dropIndexes":
            {
                BsonValue index = method.Arg(0) ?? (method.Name == "dropIndexes" ? "*" : throw new ShellExecutionException(loc.Format("Query_ErrNeedsName", method.Name)));
                if (guard.ConfirmWrites && !await guard.ConfirmAsync(new ShellConfirmation(ShellConfirmKind.DropIndex, database, name, null, null,
                        index.IsString ? index.AsString : index.ToString())).ConfigureAwait(true))
                {
                    return Declined(command, database, name);
                }
                BsonDocument reply = await connection.RunCommandAsync(database, new BsonDocument { { "dropIndexes", name }, { "index", index } }, cancellationToken)
                    .ConfigureAwait(false);
                return Write(command, database, name, reply, loc.Format("Query_MsgIndexDropped", index.IsString ? index.AsString : index.ToString()));
            }

            case "getIndexes":
            {
                IReadOnlyList<BsonDocument> indexes = await connection.ListIndexesAsync(database, name, cancellationToken).ConfigureAwait(false);
                return Documents(command, database, name, [.. indexes], false);
            }

            case "stats":
            {
                CollectionStats stats = await connection.GetStatsAsync(database, name, cancellationToken).ConfigureAwait(false);
                return Single(command, database, name, stats.Raw);
            }

            case "drop":
            {
                if (guard.ConfirmWrites && !await guard.ConfirmAsync(new ShellConfirmation(ShellConfirmKind.Drop, database, name, null,
                        await connection.EstimatedCountAsync(database, name, cancellationToken).ConfigureAwait(true))).ConfigureAwait(true))
                {
                    return Declined(command, database, name);
                }
                await connection.Database(database).DropCollectionAsync(name, cancellationToken).ConfigureAwait(false);
                return Write(command, database, name, new BsonDocument("dropped", true), loc.Format("Query_MsgDropped", name)) with { ChangesCatalog = true };
            }

            case "renameCollection":
            {
                string target = method.Arg(0) is BsonString t ? t.Value : throw new ShellExecutionException(loc.Format("Query_ErrNeedsName", method.Name));
                bool dropTarget = method.Arg(1) is BsonBoolean { Value: true };
                BsonDocument reply = await connection.RunCommandAsync("admin", new BsonDocument
                {
                    { "renameCollection", $"{database}.{name}" },
                    { "to", $"{database}.{target}" },
                    { "dropTarget", dropTarget }
                }, cancellationToken).ConfigureAwait(false);
                return Write(command, database, name, reply, loc.Format("Query_MsgRenamed", name, target)) with { ChangesCatalog = true };
            }

            default:
                throw new ShellExecutionException(loc.Format("Query_ErrUnsupportedMethod", method.Name));
        }
    }

    private async Task<ShellResult> FindAsync(ShellCommand command, IMongoCollection<BsonDocument> collection, string database, TimeSpan? maxTime,
        BsonValue? comment, ShellRunOptions options, CancellationToken cancellationToken)
    {
        ShellCall method = command.Method!;
        FindRequest request = BuildFindRequest(command, database, options.MaxTimeMs);
        var findOptions = new FindOptions<BsonDocument, BsonDocument>
        {
            MaxTime = maxTime,
            Comment = comment,
            Hint = request.Hint,
            Collation = Collation(request.Collation),
            Projection = request.Projection is { } p ? new BsonDocumentProjectionDefinition<BsonDocument, BsonDocument>(p) : null,
            Sort = request.Sort is { } sortSpec ? new BsonDocumentSortDefinition<BsonDocument>(sortSpec) : null,
            Skip = request.Skip > 0 ? request.Skip : null,
            BatchSize = command.Modifier("batchSize")?.Arg(0) is { IsNumeric: true } batch ? batch.ToInt32() : null,
            AllowDiskUse = command.Modifier("allowDiskUse") is not null ? true : null
        };

        // count() / size() / itcount() 接在 find 后面:变成按同一组条件计数。
        if ((command.Modifier("count") ?? command.Modifier("size") ?? command.Modifier("itcount")) is { } counter)
        {
            var countOptions = new CountOptions
            {
                Skip = counter.Name != "count" && request.Skip > 0 ? request.Skip : null,
                Limit = counter.Name != "count" && request.Limit > 0 ? request.Limit : null,
                Hint = request.Hint,
                MaxTime = maxTime,
                Comment = comment
            };
            long count = await collection.CountDocumentsAsync(request.Filter, countOptions, cancellationToken).ConfigureAwait(false);
            return Single(command, database, command.Collection, new BsonDocument("count", count)) with { Query = request };
        }

        int requested = method.Name == "findOne" ? 1 : request.Limit;
        int max = options.MaxDocuments;
        // 用户 limit 不超过上限:按原样取,不会截断;否则多取一份,用来判断"后面还有"。
        int fetch = requested > 0 && requested <= max ? requested : max + 1;
        findOptions.Limit = method.Name == "findOne" ? -1 : fetch;
        using IAsyncCursor<BsonDocument> cursor = await collection.FindAsync(request.Filter, findOptions, cancellationToken).ConfigureAwait(false);
        (List<BsonDocument> docs, bool truncated) = await DrainAsync(cursor, max, cancellationToken).ConfigureAwait(false);
        return Documents(command, database, command.Collection, docs, truncated) with { Query = request };
    }

    /// <summary>
    /// 把 find 语句收成一个 <see cref="FindRequest" />(导出向导拿去全量导出,hint 对比拿去重跑 explain)。
    /// <c>Limit</c> 为 0 表示没写 limit。
    /// </summary>
    internal static FindRequest BuildFindRequest(ShellCommand command, string database, int defaultMaxTimeMs)
    {
        ShellCall method = command.Method!;
        BsonDocument settings = method.Document(2) ?? [];
        BsonDocument? projection = method.Document(1) ?? settings.GetValue("projection", null) as BsonDocument
            ?? command.Modifier("project")?.Document(0) ?? command.Modifier("projection")?.Document(0);
        return new FindRequest
        {
            Database = database,
            Collection = command.Collection ?? "",
            Filter = method.Document(0) ?? [],
            Projection = projection is { ElementCount: > 0 } ? projection : null,
            Sort = command.Modifier("sort")?.Document(0) ?? settings.GetValue("sort", null) as BsonDocument,
            Skip = command.Modifier("skip")?.Arg(0) is { IsNumeric: true } skip ? skip.ToInt32() : settings.GetValue("skip", 0).ToInt32(),
            Limit = method.Name == "findOne" ? 1
                : command.Modifier("limit")?.Arg(0) is { IsNumeric: true } limit ? Math.Abs(limit.ToInt32()) : settings.GetValue("limit", 0).ToInt32(),
            Hint = command.Modifier("hint")?.Arg(0) ?? settings.GetValue("hint", null),
            MaxTimeMs = command.Modifier("maxTimeMS")?.Arg(0) is { IsNumeric: true } ms ? ms.ToInt32() : defaultMaxTimeMs,
            Collation = command.Modifier("collation")?.Document(0) ?? settings.GetValue("collation", null) as BsonDocument
        };
    }

    private async Task<ShellResult> FindOneAndAsync(ShellCommand command, IMongoCollection<BsonDocument> collection, string database, string name,
        TimeSpan? maxTime, BsonValue? comment, CancellationToken cancellationToken)
    {
        ShellCall method = command.Method!;
        BsonDocument filter = Filter(method, 0);
        BsonDocument settings = method.Document(method.Name == "findOneAndDelete" ? 1 : 2) ?? [];
        bool returnNew = settings.GetValue("returnNewDocument", false).ToBoolean()
                         || settings.GetValue("returnDocument", "before").ToString() == "after";
        BsonDocument? projection = settings.GetValue("projection", null) as BsonDocument;
        BsonDocument? sort = settings.GetValue("sort", null) as BsonDocument;
        BsonDocument? document;
        switch (method.Name)
        {
            case "findOneAndUpdate":
                document = await collection.FindOneAndUpdateAsync(filter, Update(method.Arg(1), method.Name), new FindOneAndUpdateOptions<BsonDocument>
                {
                    ReturnDocument = returnNew ? ReturnDocument.After : ReturnDocument.Before,
                    IsUpsert = settings.GetValue("upsert", false).ToBoolean(),
                    Projection = projection is null ? null : new BsonDocumentProjectionDefinition<BsonDocument, BsonDocument>(projection),
                    Sort = sort,
                    MaxTime = maxTime,
                    Comment = comment
                }, cancellationToken).ConfigureAwait(false);
                break;
            case "findOneAndReplace":
                document = await collection.FindOneAndReplaceAsync(filter,
                    method.Arg(1) as BsonDocument ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", method.Name)),
                    new FindOneAndReplaceOptions<BsonDocument>
                    {
                        ReturnDocument = returnNew ? ReturnDocument.After : ReturnDocument.Before,
                        IsUpsert = settings.GetValue("upsert", false).ToBoolean(),
                        Projection = projection is null ? null : new BsonDocumentProjectionDefinition<BsonDocument, BsonDocument>(projection),
                        Sort = sort,
                        MaxTime = maxTime,
                        Comment = comment
                    }, cancellationToken).ConfigureAwait(false);
                break;
            default:
                document = await collection.FindOneAndDeleteAsync(filter, new FindOneAndDeleteOptions<BsonDocument>
                {
                    Projection = projection is null ? null : new BsonDocumentProjectionDefinition<BsonDocument, BsonDocument>(projection),
                    Sort = sort,
                    MaxTime = maxTime,
                    Comment = comment
                }, cancellationToken).ConfigureAwait(false);
                break;
        }
        return Documents(command, database, name, document is null ? [] : [document], false) with
        {
            Message = document is null ? loc["Query_MsgNoMatch"] : loc.Format("Query_MsgDocs", 1)
        };
    }

    private async Task<ShellResult> BulkWriteAsync(ShellCommand command, IMongoCollection<BsonDocument> collection, string database, string name,
        BsonValue? comment, CancellationToken cancellationToken)
    {
        ShellCall method = command.Method!;
        BsonArray operations = method.Arg(0) as BsonArray ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsArray", "bulkWrite"));
        var models = new List<WriteModel<BsonDocument>>();
        bool bulk = false;
        foreach (BsonValue item in operations)
        {
            if (item is not BsonDocument { ElementCount: 1 } op || op[0] is not BsonDocument body)
            {
                throw new ShellExecutionException(loc.Format("Query_ErrBulkOperation", item.ToString()));
            }
            string kind = op.GetElement(0).Name;
            BsonDocument filter = body.GetValue("filter", new BsonDocument()).AsBsonDocument;
            bool upsert = body.GetValue("upsert", false).ToBoolean();
            switch (kind)
            {
                case "insertOne":
                    models.Add(new InsertOneModel<BsonDocument>(body.GetValue("document", body).AsBsonDocument));
                    break;
                case "updateOne":
                    models.Add(new UpdateOneModel<BsonDocument>(filter, Update(body.GetValue("update", null), kind)) { IsUpsert = upsert });
                    break;
                case "updateMany":
                    bulk = true;
                    models.Add(new UpdateManyModel<BsonDocument>(filter, Update(body.GetValue("update", null), kind)) { IsUpsert = upsert });
                    break;
                case "replaceOne":
                    models.Add(new ReplaceOneModel<BsonDocument>(filter, body.GetValue("replacement", new BsonDocument()).AsBsonDocument) { IsUpsert = upsert });
                    break;
                case "deleteOne":
                    models.Add(new DeleteOneModel<BsonDocument>(filter));
                    break;
                case "deleteMany":
                    bulk = true;
                    models.Add(new DeleteManyModel<BsonDocument>(filter));
                    break;
                default:
                    throw new ShellExecutionException(loc.Format("Query_ErrBulkOperation", kind));
            }
        }
        if (bulk && guard.ConfirmWrites
                 && !await guard.ConfirmAsync(new ShellConfirmation(ShellConfirmKind.BulkWrite, database, name, null, models.Count)).ConfigureAwait(true))
        {
            return Declined(command, database, name);
        }
        bool ordered = method.Document(1)?.GetValue("ordered", true).ToBoolean() ?? true;
        BulkWriteResult<BsonDocument> result = await collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = ordered, Comment = comment }, cancellationToken)
            .ConfigureAwait(false);
        var reply = new BsonDocument
        {
            { "acknowledged", result.IsAcknowledged },
            { "insertedCount", result.IsAcknowledged ? result.InsertedCount : 0 },
            { "matchedCount", result.IsAcknowledged ? result.MatchedCount : 0 },
            { "modifiedCount", result.IsAcknowledged && result.IsModifiedCountAvailable ? result.ModifiedCount : 0 },
            { "deletedCount", result.IsAcknowledged ? result.DeletedCount : 0 },
            { "upsertedCount", result.IsAcknowledged ? result.Upserts.Count : 0 }
        };
        return Write(command, database, name, reply, loc.Format("Query_MsgBulk", models.Count));
    }

    // ── 执行计划 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 对一条语句跑 explain(语句里写没写 <c>.explain()</c> 都行 —— F6 就是对当前语句这么做的)。
    /// <paramref name="hint" /> 非空时覆盖语句里的 hint(「用 hint 对比」)。
    /// </summary>
    public async Task<ShellResult> ExplainAsync(ShellCommand command, string database, string verbosity, ShellRunOptions options,
        BsonValue? hint, CancellationToken cancellationToken = default)
    {
        Stopwatch watch = Stopwatch.StartNew();
        BsonDocument inner = BuildExplainTarget(command, database, options, hint, loc);
        BsonDocument explain = await connection.RunCommandAsync(command.Database ?? database,
            new BsonDocument { { "explain", inner }, { "verbosity", verbosity } }, cancellationToken).ConfigureAwait(false);
        return new ShellResult
        {
            Kind = ShellResultKind.Explain,
            Operation = command.Operation,
            Database = command.Database ?? database,
            Collection = command.Collection,
            Explain = explain,
            Message = loc.Format("Query_MsgExplain", command.Operation, verbosity),
            Elapsed = watch.Elapsed,
            Query = command.Method?.Name is "find" or "findOne" ? BuildFindRequest(command, database, options.MaxTimeMs) : null,
            Pipeline = command.Method?.Name == "aggregate" ? command.Method.Arg(0) as BsonArray : null
        };
    }

    /// <summary>语句 → explain 命令里的那条内层命令(<c>{ find: … }</c> / <c>{ aggregate: … }</c> / …)。</summary>
    /// <exception cref="ShellExecutionException">这种语句没有执行计划。</exception>
    internal static BsonDocument BuildExplainTarget(ShellCommand command, string database, ShellRunOptions options, BsonValue? hint, Loc loc)
    {
        if (command.Kind != ShellCommandKind.Collection || command.Method is not { } method || command.Collection is not { } name)
        {
            throw new ShellExecutionException(loc.Format("Query_ErrExplainUnsupported", command.Operation));
        }
        int maxTimeMs = command.Modifier("maxTimeMS")?.Arg(0) is { IsNumeric: true } ms ? ms.ToInt32() : options.MaxTimeMs;
        BsonDocument target;
        switch (method.Name)
        {
            case "find" or "findOne":
            {
                FindRequest request = BuildFindRequest(command, database, options.MaxTimeMs);
                if (command.Modifier("count") is not null)
                {
                    target = new BsonDocument { { "count", name }, { "query", request.Filter } };
                    break;
                }
                target = new BsonDocument { { "find", name }, { "filter", request.Filter } };
                if (request.Projection is { } projection)
                {
                    target["projection"] = projection;
                }
                if (request.Sort is { } sort)
                {
                    target["sort"] = sort;
                }
                if (request.Skip > 0)
                {
                    target["skip"] = request.Skip;
                }
                if (request.Limit > 0)
                {
                    target["limit"] = request.Limit;
                }
                if (method.Name == "findOne")
                {
                    target["singleBatch"] = true;
                }
                if ((hint ?? request.Hint) is { } h)
                {
                    target["hint"] = h;
                }
                if (request.Collation is { } collation)
                {
                    target["collation"] = collation;
                }
                break;
            }
            case "aggregate":
            {
                BsonArray pipeline = method.Arg(0) as BsonArray ?? throw new ShellExecutionException(loc.Format("Query_ErrNeedsPipeline", "aggregate"));
                target = new BsonDocument { { "aggregate", name }, { "pipeline", pipeline }, { "cursor", new BsonDocument() } };
                BsonDocument settings = method.Document(1) ?? [];
                if ((hint ?? settings.GetValue("hint", null) ?? command.Modifier("hint")?.Arg(0)) is { } h)
                {
                    target["hint"] = h;
                }
                if (settings.GetValue("allowDiskUse", null) is { } disk)
                {
                    target["allowDiskUse"] = disk;
                }
                break;
            }
            case "countDocuments":
            {
                var pipeline = new BsonArray { new BsonDocument("$match", method.Document(0) ?? []) };
                BsonDocument settings = method.Document(1) ?? [];
                if (settings.GetValue("skip", null) is { IsNumeric: true } skip)
                {
                    pipeline.Add(new BsonDocument("$skip", skip));
                }
                if (settings.GetValue("limit", null) is { IsNumeric: true } limit)
                {
                    pipeline.Add(new BsonDocument("$limit", limit));
                }
                pipeline.Add(new BsonDocument("$group", new BsonDocument { { "_id", 1 }, { "n", new BsonDocument("$sum", 1) } }));
                target = new BsonDocument { { "aggregate", name }, { "pipeline", pipeline }, { "cursor", new BsonDocument() } };
                if ((hint ?? settings.GetValue("hint", null)) is { } h)
                {
                    target["hint"] = h;
                }
                break;
            }
            case "distinct":
                target = new BsonDocument { { "distinct", name }, { "key", method.Arg(0) ?? "" }, { "query", method.Document(1) ?? [] } };
                break;
            case "updateOne" or "updateMany" or "replaceOne":
            {
                var update = new BsonDocument
                {
                    { "q", method.Document(0) ?? [] },
                    { "u", method.Arg(1) ?? new BsonDocument() },
                    { "multi", method.Name == "updateMany" },
                    { "upsert", method.Document(2)?.GetValue("upsert", false) ?? false }
                };
                if (hint is not null)
                {
                    update["hint"] = hint;
                }
                target = new BsonDocument { { "update", name }, { "updates", new BsonArray { update } } };
                break;
            }
            case "deleteOne" or "deleteMany":
            {
                var delete = new BsonDocument { { "q", method.Document(0) ?? [] }, { "limit", method.Name == "deleteOne" ? 1 : 0 } };
                if (hint is not null)
                {
                    delete["hint"] = hint;
                }
                target = new BsonDocument { { "delete", name }, { "deletes", new BsonArray { delete } } };
                break;
            }
            case "findOneAndUpdate" or "findOneAndReplace" or "findOneAndDelete":
            {
                BsonDocument settings = method.Document(method.Name == "findOneAndDelete" ? 1 : 2) ?? [];
                target = new BsonDocument { { "findAndModify", name }, { "query", method.Document(0) ?? [] } };
                if (method.Name == "findOneAndDelete")
                {
                    target["remove"] = true;
                }
                else
                {
                    target["update"] = method.Arg(1) ?? new BsonDocument();
                }
                if (settings.GetValue("sort", null) is BsonDocument sort)
                {
                    target["sort"] = sort;
                }
                break;
            }
            default:
                throw new ShellExecutionException(loc.Format("Query_ErrExplainUnsupported", method.Name));
        }
        if (maxTimeMs > 0)
        {
            target["maxTimeMS"] = maxTimeMs;
        }
        if (options.Comment is { Length: > 0 } comment)
        {
            target["comment"] = comment;
        }
        return target;
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────

    private async Task<bool> ConfirmAsync(ShellConfirmKind kind, IMongoCollection<BsonDocument> collection, string database, string name,
        BsonDocument filter, CancellationToken cancellationToken)
    {
        if (!guard.ConfirmWrites)
        {
            return true;
        }
        long? affected = null;
        try
        {
            // 数一下会动多少份:确认框里写清后果,而不是一句"确定吗"。数不过来(超时)就不写。
            affected = await collection.CountDocumentsAsync(filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(3) }, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (MongoException)
        {
        }
        return await guard.ConfirmAsync(new ShellConfirmation(kind, database, name, filter, affected)).ConfigureAwait(true);
    }

    private async Task<bool> ConfirmDropDatabaseAsync(string database) =>
        await guard.ConfirmAsync(new ShellConfirmation(ShellConfirmKind.DropDatabase, database, null, null, null)).ConfigureAwait(true);

    private static async Task<(List<BsonDocument> Documents, bool Truncated)> DrainAsync(IAsyncCursor<BsonDocument> cursor, int max,
        CancellationToken cancellationToken)
    {
        var documents = new List<BsonDocument>();
        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (BsonDocument document in cursor.Current)
            {
                if (documents.Count >= max)
                {
                    // 多出来的那一份只用来判断"后面还有";游标随 using 关掉,服务器端随之释放。
                    return (documents, true);
                }
                documents.Add(document);
            }
        }
        return (documents, false);
    }

    private BsonArray Pipeline(ShellCall method) => method.Arg(0) switch
    {
        BsonArray array => array,
        BsonDocument single => [single],
        _ => throw new ShellExecutionException(loc.Format("Query_ErrNeedsPipeline", method.Name))
    };

    private BsonDocument Filter(ShellCall method, int index) => method.Arg(index) switch
    {
        null => [],
        BsonDocument d => d,
        _ => throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", method.Name))
    };

    private UpdateDefinition<BsonDocument> Update(BsonValue? value, string method) => value switch
    {
        BsonDocument document => new BsonDocumentUpdateDefinition<BsonDocument>(document),
        BsonArray pipeline => new PipelineUpdateDefinition<BsonDocument>(
            PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline.Select(static s => s.AsBsonDocument))),
        _ => throw new ShellExecutionException(loc.Format("Query_ErrNeedsDocument", method))
    };

    private static BsonDocument UpdateReply(UpdateResult result) => new()
    {
        { "acknowledged", result.IsAcknowledged },
        { "matchedCount", result.IsAcknowledged ? result.MatchedCount : 0 },
        { "modifiedCount", result.IsAcknowledged && result.IsModifiedCountAvailable ? result.ModifiedCount : 0 },
        { "upsertedId", result.IsAcknowledged ? result.UpsertedId ?? BsonNull.Value : BsonNull.Value }
    };

    private static Collation? Collation(BsonValue? value) => value is BsonDocument document ? MongoDB.Driver.Collation.FromBsonDocument(document) : null;

    private static TimeSpan? MaxTime(int ms) => ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;

    private static BsonValue? Comment(ShellRunOptions options) => options.Comment is { Length: > 0 } c ? new BsonString(c) : null;

    /// <summary>与 mongosh / 驱动一致的默认索引名:<c>status_1_createdAt_-1</c>。</summary>
    internal static string IndexName(BsonDocument keys) =>
        string.Join('_', keys.Elements.Select(static e => $"{e.Name}_{e.Value}"));

    private static List<BsonDocument> Docs(BsonValue value) =>
        value is BsonArray array ? [.. array.OfType<BsonDocument>()] : [];

    private ShellResult Documents(ShellCommand command, string database, string? collection, IReadOnlyList<BsonDocument> documents, bool truncated) => new()
    {
        Kind = ShellResultKind.Documents,
        Operation = command.Operation,
        Database = database,
        Collection = collection,
        Documents = documents,
        Truncated = truncated,
        Message = truncated ? loc.Format("Query_MsgTruncated", documents.Count) : loc.Format("Query_MsgDocs", documents.Count)
    };

    private ShellResult Single(ShellCommand command, string database, string? collection, BsonDocument document) =>
        Documents(command, database, collection, [document], false) with { Message = loc["Query_MsgOk"] };

    private ShellResult Write(ShellCommand command, string database, string collection, BsonDocument reply, string message) =>
        Documents(command, database, collection, [reply], false) with { Message = message };

    private ShellResult Blocked(ShellCommand command, string database) => new()
    {
        Kind = ShellResultKind.Message,
        Operation = command.Operation,
        Database = database,
        Collection = command.Collection,
        Message = loc["Query_MsgBlocked"],
        Declined = true
    };

    private ShellResult Declined(ShellCommand command, string database, string? collection = null) => new()
    {
        Kind = ShellResultKind.Message,
        Operation = command.Operation,
        Database = database,
        Collection = collection ?? command.Collection,
        Message = loc["Query_MsgDeclined"],
        Declined = true
    };
}

/// <summary>一次运行的会话状态(当前库;<c>use</c> 改它)。</summary>
/// <param name="database">起始库。</param>
internal sealed class ShellSession(string database)
{
    /// <summary>当前库。</summary>
    public string Database { get; set; } = database;
}
