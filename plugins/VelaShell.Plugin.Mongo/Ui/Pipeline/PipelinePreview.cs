using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>一个阶段在预览里的结局。</summary>
internal enum PipelineStageState
{
    /// <summary>还没算(或自动预览关着)。</summary>
    Pending,

    /// <summary>算出来了。</summary>
    Ok,

    /// <summary>服务器报错(错误记在这个阶段上)。</summary>
    Error,

    /// <summary>停用,跳过。</summary>
    Skipped,

    /// <summary>上游有错,算不了。</summary>
    Blocked,

    /// <summary>阶段体本身解析不了。</summary>
    Invalid,

    /// <summary>写入阶段(<c>$out</c> / <c>$merge</c>):预览绝不能真写,不跑。</summary>
    WriteStage
}

/// <summary>一个阶段的预览结果。</summary>
/// <param name="State">结局。</param>
/// <param name="Documents">输出预览(最多 <see cref="PipelinePreview.DocsPerStage" /> 份)。</param>
/// <param name="Count">基于抽样的输出文档数。</param>
/// <param name="Error">服务器的报错。</param>
internal sealed record PipelineStageOutcome(PipelineStageState State, IReadOnlyList<BsonDocument> Documents, long Count, string? Error = null)
{
    /// <summary>未算。</summary>
    public static PipelineStageOutcome Pending { get; } = new(PipelineStageState.Pending, [], 0);

    /// <summary>无数据的某种结局。</summary>
    public static PipelineStageOutcome Of(PipelineStageState state) => new(state, [], 0);
}

/// <summary>一次预览的结果。</summary>
/// <param name="Input">抽样的输入文档(第一个阶段的"上游字段"从这里来)。</param>
/// <param name="Stages">逐阶段的结局,与传入的阶段一一对应。</param>
/// <param name="Elapsed">总耗时。</param>
internal sealed record PipelinePreviewRun(IReadOnlyList<BsonDocument> Input, IReadOnlyList<PipelineStageOutcome> Stages, TimeSpan Elapsed);

/// <summary>
/// 逐阶段预览:对抽样输入跑每一段**前缀管道**,拿到每个阶段的输出预览与计数。
/// <para>
/// 为什么是"前缀管道"而不是一次跑完再拆:聚合管道只吐最后一个阶段的输出,
/// 想看第 3 个阶段吐了什么,只能把管道截到第 3 个阶段再跑一次。输入先用 <c>$limit</c> 截成 N 份,
/// 所以每一段都很便宜;末尾挂一个 <c>$facet</c>,一次往返同时拿回前几份文档与总数。
/// </para>
/// <para>
/// 计数因此**是抽样上的计数**(20 份输入经 <c>$unwind</c> 变成 61 份),界面上必须照实标注 ——
/// 把它画成全集合的数字会让人拿它去估容量。
/// </para>
/// </summary>
internal static class PipelinePreview
{
    /// <summary>每个阶段带回多少份文档(卡片只摆两张,"看全部"摆这些)。</summary>
    public const int DocsPerStage = 50;

    /// <summary>必须是管道第一个阶段的那些(抽样的 <c>$limit</c> 只能挂在它们后面)。</summary>
    internal static readonly HashSet<string> FirstOnly =
    [
        with(StringComparer.Ordinal),
        "$geoNear", "$collStats", "$indexStats", "$documents", "$search", "$searchMeta", "$vectorSearch",
        "$currentOp", "$listSessions", "$listLocalSessions", "$changeStream", "$planCacheStats",
        "$listSearchIndexes", "$shardedDataDistribution", "$querySettings"
    ];

    /// <summary>写入阶段。</summary>
    internal static readonly HashSet<string> WriteStages = [with(StringComparer.Ordinal), "$out", "$merge"];

    /// <summary>跑一次预览。</summary>
    /// <param name="collection">源集合。</param>
    /// <param name="stages">阶段(<see langword="null" /> = 阶段体解析不了)与启用状态。</param>
    /// <param name="sampleSize">抽样的输入文档数。</param>
    /// <param name="options">聚合选项(maxTimeMS、allowDiskUse、comment)。</param>
    /// <param name="cancellationToken">取消(新的改动作废上一次预览)。</param>
    public static async Task<PipelinePreviewRun> RunAsync(
        IMongoCollection<BsonDocument> collection,
        IReadOnlyList<(BsonDocument? Stage, bool Enabled)> stages,
        int sampleSize,
        AggregateOptions options,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var outcomes = new PipelineStageOutcome[stages.Count];
        int firstEnabled = -1;
        for (int i = 0; i < stages.Count; i++)
        {
            if (stages[i].Enabled)
            {
                firstEnabled = i;
                break;
            }
        }
        bool firstOnly = firstEnabled >= 0 && stages[firstEnabled].Stage is { ElementCount: > 0 } head && FirstOnly.Contains(head.GetElement(0).Name);
        var limit = new BsonDocument("$limit", Math.Max(1, sampleSize));

        var prefix = new List<BsonDocument>();
        if (!firstOnly)
        {
            prefix.Add(limit);
        }
        Task<(IReadOnlyList<BsonDocument> Docs, long Count)> input = firstOnly
            ? Task.FromResult<(IReadOnlyList<BsonDocument>, long)>(([], 0))
            : RunPrefixAsync(collection, [.. prefix], options, cancellationToken);

        var pending = new List<(int Index, Task<(IReadOnlyList<BsonDocument> Docs, long Count)> Task)>();
        bool blocked = false;
        for (int i = 0; i < stages.Count; i++)
        {
            (BsonDocument? stage, bool enabled) = stages[i];
            if (!enabled)
            {
                outcomes[i] = PipelineStageOutcome.Of(PipelineStageState.Skipped);
                continue;
            }
            if (blocked)
            {
                outcomes[i] = PipelineStageOutcome.Of(PipelineStageState.Blocked);
                continue;
            }
            if (stage is not { ElementCount: > 0 })
            {
                outcomes[i] = PipelineStageOutcome.Of(PipelineStageState.Invalid);
                blocked = true;
                continue;
            }
            if (WriteStages.Contains(stage.GetElement(0).Name))
            {
                // $out / $merge 只能在最后;预览跑它就是真写进去了。
                outcomes[i] = PipelineStageOutcome.Of(PipelineStageState.WriteStage);
                blocked = true;
                continue;
            }
            prefix.Add(stage);
            if (firstOnly && i == firstEnabled)
            {
                prefix.Add(limit);
            }
            pending.Add((i, RunPrefixAsync(collection, [.. prefix], options, cancellationToken)));
        }

        // 并行跑:前缀之间互不依赖,而抽样很小 —— 串行只会把延迟按阶段数放大。
        try
        {
            await Task.WhenAll(pending.Select(static p => (Task)p.Task).Append(input)).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 每个任务的异常下面逐个看:只把**第一个**出错的阶段记成错误,后面的都是被它连累的。
        }
        cancellationToken.ThrowIfCancellationRequested();

        bool failed = false;
        foreach ((int index, Task<(IReadOnlyList<BsonDocument> Docs, long Count)> task) in pending)
        {
            if (failed)
            {
                outcomes[index] = PipelineStageOutcome.Of(PipelineStageState.Blocked);
                continue;
            }
            if (task.IsCompletedSuccessfully)
            {
                (IReadOnlyList<BsonDocument> docs, long count) = task.Result;
                outcomes[index] = new(PipelineStageState.Ok, docs, count);
                continue;
            }
            failed = true;
            Exception? error = task.Exception?.GetBaseException();
            outcomes[index] = new(PipelineStageState.Error, [], 0, error is null ? "?" : MongoConnector.Describe(error));
        }
        IReadOnlyList<BsonDocument> inputDocs = input.IsCompletedSuccessfully ? input.Result.Docs : [];
        watch.Stop();
        return new(inputDocs, outcomes, watch.Elapsed);
    }

    /// <summary>跑一段前缀,末尾挂 <c>$facet</c> 同时拿回前几份文档与总数。</summary>
    private static async Task<(IReadOnlyList<BsonDocument> Docs, long Count)> RunPrefixAsync(
        IMongoCollection<BsonDocument> collection,
        List<BsonDocument> prefix,
        AggregateOptions options,
        CancellationToken cancellationToken)
    {
        var facet = new BsonDocument("$facet", new BsonDocument
        {
            { "docs", new BsonArray { new BsonDocument("$limit", DocsPerStage) } },
            { "n", new BsonArray { new BsonDocument("$count", "n") } }
        });
        List<BsonDocument> pipeline = [.. prefix, facet];
        using IAsyncCursor<BsonDocument> cursor = await collection
            .AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline), options, cancellationToken)
            .ConfigureAwait(false);
        BsonDocument? result = await cursor.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return ([], 0);
        }
        List<BsonDocument> docs = result.TryGetValue("docs", out BsonValue d) && d is BsonArray array
            ? [.. array.OfType<BsonDocument>()]
            : [];
        long count = result.TryGetValue("n", out BsonValue n) && n is BsonArray { Count: > 0 } counts
                     && counts[0] is BsonDocument first && first.TryGetValue("n", out BsonValue value) && value.IsNumeric
            ? value.ToInt64()
            : 0;
        return (docs, count);
    }
}
