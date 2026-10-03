using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// <c>$currentOp</c> 的一层薄封装(监控页认索引构建、慢查询页列当前操作共用)。
/// <para>
/// 一律打上 <see cref="Comment" /> 标记,再在 <c>$match</c> 里把带这个标记的操作排除掉 ——
/// 否则"当前操作"列表里永远挂着我们自己这条 <c>$currentOp</c>,监控在观察自己。
/// </para>
/// </summary>
internal static class MonitorOps
{
    /// <summary>本插件发起的监控类操作统一带的 <c>comment</c>。</summary>
    public const string Comment = "velashell:monitor";

    /// <summary>
    /// 列出当前操作。先按 <c>allUsers: true</c> 要全部;没有 <c>inprog</c> 权限(错误码 13)时
    /// 退回只看自己的 —— 少看一部分好过整块报错。
    /// </summary>
    /// <param name="connection">连接。</param>
    /// <param name="match">附加的 <c>$match</c> 条件;null = 不加。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作文档,以及是不是只看到了自己的操作。</returns>
    public static async Task<(IReadOnlyList<BsonDocument> Ops, bool OwnOnly)> ListAsync(
        MongoConnection connection, BsonDocument? match, CancellationToken cancellationToken)
    {
        try
        {
            return (await RunAsync(connection, match, allUsers: true, cancellationToken).ConfigureAwait(true), false);
        }
        catch (MongoCommandException ex) when (ex.Code == 13)
        {
            return (await RunAsync(connection, match, allUsers: false, cancellationToken).ConfigureAwait(true), true);
        }
    }

    private static async Task<IReadOnlyList<BsonDocument>> RunAsync(
        MongoConnection connection, BsonDocument? match, bool allUsers, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("command.comment", new BsonDocument("$ne", Comment));
        if (match is { ElementCount: > 0 })
        {
            filter = new BsonDocument("$and", new BsonArray { filter, match });
        }
        BsonDocument[] stages =
        [
            new("$currentOp", new BsonDocument { { "allUsers", allUsers }, { "idleConnections", false } }),
            new("$match", filter)
        ];
        PipelineDefinition<NoPipelineInput, BsonDocument> pipeline = PipelineDefinition<NoPipelineInput, BsonDocument>.Create(stages);
        using IAsyncCursor<BsonDocument> cursor = await connection.Database("admin")
            .AggregateAsync(pipeline, new AggregateOptions { Comment = Comment }, cancellationToken)
            .ConfigureAwait(true);
        return await cursor.ToListAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>opid 的字符串形式(mongos 上是 <c>shard0:12345</c>,单机是整数)。</summary>
    public static string OpId(BsonDocument op) =>
        op.TryGetValue("opid", out BsonValue v) ? (v.IsString ? v.AsString : v.ToString() ?? "") : "";
}
