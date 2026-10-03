using MongoDB.Bson;
using MongoDB.Driver;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>对象目录:库、集合、视图、GridFS 桶与统计。</summary>
internal sealed partial class MongoConnection
{
    /// <summary>
    /// 列出数据库。用户没有 <c>listDatabases</c> 权限时(只授了某几个库的 read),
    /// 服务器会只回它有权限的那几个(<c>authorizedDatabases</c>)—— 那是正常形态,不是错误。
    /// </summary>
    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        BsonDocument reply = await RunCommandAsync("admin", new BsonDocument
        {
            { "listDatabases", 1 },
            { "authorizedDatabases", true }
        }, cancellationToken).ConfigureAwait(false);
        List<DatabaseInfo> list =
        [
            .. reply.GetValue("databases", new BsonArray()).AsBsonArray
                .Select(static d => d.AsBsonDocument)
                .Select(static d => new DatabaseInfo(
                    d["name"].AsString,
                    d.GetValue("sizeOnDisk", 0L).ToInt64(),
                    d.GetValue("empty", false).ToBoolean()))
        ];
        // 默认库可能还不存在(空库不在 listDatabases 里),但用户在连接里点名要它 —— 补一行。
        if (Settings.Database.Length > 0 && list.All(d => d.Name != Settings.Database))
        {
            list.Add(new(Settings.Database, 0, true));
        }
        // 系统库在前(admin / config / local),其余按名字 —— 与设计稿的对象树一致。
        return [.. list.OrderBy(static d => d.IsSystem ? 0 : 1).ThenBy(static d => d.Name, StringComparer.Ordinal)];
    }

    /// <summary>列出一个库里的集合与视图(含 <c>options</c>)。</summary>
    public async Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(string database, CancellationToken cancellationToken = default)
    {
        using IAsyncCursor<BsonDocument> cursor = await Database(database)
            .ListCollectionsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        List<BsonDocument> raw = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. raw.Select(doc => ToCollectionInfo(database, doc))
                .OrderBy(static c => c.Name, StringComparer.Ordinal)
        ];
    }

    internal static CollectionInfo ToCollectionInfo(string database, BsonDocument doc)
    {
        string name = doc["name"].AsString;
        string type = doc.GetValue("type", "collection").AsString;
        BsonDocument options = doc.GetValue("options", new BsonDocument()).AsBsonDocument;
        CollectionKind kind = type switch
        {
            "view" => CollectionKind.View,
            "timeseries" => CollectionKind.TimeSeries,
            _ when name.StartsWith("system.", StringComparison.Ordinal) => CollectionKind.System,
            _ when options.GetValue("capped", false).ToBoolean() => CollectionKind.Capped,
            _ when options.Contains("clusteredIndex") => CollectionKind.Clustered,
            _ => CollectionKind.Collection
        };
        return new(database, name, kind, options);
    }

    /// <summary>
    /// 从集合列表里认出 GridFS 桶:<c>X.files</c> 与 <c>X.chunks</c> 成对出现即算一个桶。
    /// 只有一半的不算(那多半是手工建的同名集合,或桶被删了一半)。
    /// </summary>
    internal static IReadOnlyList<GridFsBucketInfo> FindBuckets(string database, IReadOnlyList<CollectionInfo> collections)
    {
        var names = collections.Select(static c => c.Name).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. names
                .Where(static n => n.EndsWith(".files", StringComparison.Ordinal))
                .Select(static n => n[..^".files".Length])
                .Where(bucket => names.Contains(bucket + ".chunks"))
                .Order(StringComparer.Ordinal)
                .Select(bucket => new GridFsBucketInfo(database, bucket))
        ];
    }

    /// <summary>集合统计。6.2 起 <c>collStats</c> 命令弃用,改走 <c>$collStats</c> 聚合;老版本仍用命令。</summary>
    public async Task<CollectionStats> GetStatsAsync(string database, string collection, CancellationToken cancellationToken = default)
    {
        BsonDocument storage;
        try
        {
            var pipeline = new[]
            {
                new BsonDocument("$collStats", new BsonDocument("storageStats", new BsonDocument()))
            };
            using IAsyncCursor<BsonDocument> cursor = await Collection(database, collection)
                .AggregateAsync<BsonDocument>(pipeline, cancellationToken: cancellationToken).ConfigureAwait(false);
            BsonDocument? first = await cursor.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            storage = first?.GetValue("storageStats", new BsonDocument()).AsBsonDocument ?? [];
        }
        catch (MongoCommandException)
        {
            // 视图、老版本或权限不够:退回 collStats 命令;再不行就给一份空统计。
            try
            {
                storage = await RunCommandAsync(database, new BsonDocument("collStats", collection), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MongoCommandException)
            {
                return new();
            }
        }
        var indexSizes = new Dictionary<string, long>(StringComparer.Ordinal);
        if (storage.TryGetValue("indexSizes", out BsonValue sizes) && sizes.IsBsonDocument)
        {
            foreach (BsonElement element in sizes.AsBsonDocument)
            {
                indexSizes[element.Name] = element.Value.ToInt64();
            }
        }
        return new()
        {
            Count = Number(storage, "count"),
            Size = Number(storage, "size"),
            AvgObjSize = Number(storage, "avgObjSize"),
            StorageSize = Number(storage, "storageSize"),
            TotalIndexSize = Number(storage, "totalIndexSize"),
            IndexCount = (int)Number(storage, "nindexes"),
            FreeStorageSize = Number(storage, "freeStorageSize"),
            IndexSizes = indexSizes,
            Engine = storage.Contains("wiredTiger") ? "WiredTiger" : "WiredTiger",
            Raw = storage
        };
    }

    /// <summary>数据库统计(<c>dbStats</c>)。</summary>
    public Task<BsonDocument> GetDatabaseStatsAsync(string database, CancellationToken cancellationToken = default) =>
        RunCommandAsync(database, new BsonDocument("dbStats", 1), cancellationToken);

    /// <summary>估算文档数(元数据,不扫描)。视图没有元数据,返回 <see langword="null" />。</summary>
    public async Task<long?> EstimatedCountAsync(string database, string collection, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Collection(database, collection)
                .EstimatedDocumentCountAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException)
        {
            return null;
        }
    }

    /// <summary>列出索引(<c>listIndexes</c> 原文)。</summary>
    public async Task<IReadOnlyList<BsonDocument>> ListIndexesAsync(string database, string collection, CancellationToken cancellationToken = default)
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await Collection(database, collection).Indexes
                .ListAsync(cancellationToken).ConfigureAwait(false);
            return await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException)
        {
            // 视图没有索引。
            return [];
        }
    }

    /// <summary>某集合是否带 TTL 索引(对象树上的 TTL 徽章)。</summary>
    internal static bool HasTtl(IReadOnlyList<BsonDocument> indexes) =>
        indexes.Any(static i => i.Contains("expireAfterSeconds"));

    private static long Number(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue value) && value.IsNumeric ? value.ToInt64() : 0;
}
