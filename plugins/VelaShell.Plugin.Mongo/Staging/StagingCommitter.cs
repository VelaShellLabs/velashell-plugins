using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Staging;

/// <summary>暂存区里的一条写操作(与发出去的 <see cref="WriteModel{TDocument}" /> 一一对应)。</summary>
internal abstract record StagedOperation
{
    /// <summary>对应的驱动写模型。</summary>
    public abstract WriteModel<BsonDocument> ToModel();
}

/// <summary>改字段:<c>updateOne({ _id, 被改字段: 原值 }, { $set, $unset })</c>。</summary>
/// <param name="Edit">暂存的修改。</param>
internal sealed record UpdateOperation(StagedEdit Edit) : StagedOperation
{
    /// <summary>
    /// 乐观并发的筛选:<c>_id</c> + 每个被改字段的原值(原本不存在的写 <c>$exists: false</c>),
    /// 原像里有 <c>updatedAt</c> 且这次没改它时再加上它。
    /// <para>
    /// 一律用 <c>$eq</c> 而不是 <c>{ 字段: 值 }</c> 的简写:原值是正则时简写会变成"按正则匹配",
    /// 是数组时简写还会匹配"包含这个元素"—— 都不是"它还是原来那个值"的意思。
    /// </para>
    /// <para>
    /// matched 为 0 就说明在我编辑期间有人动过这些字段(或删了这份文档):不覆盖,转给冲突对话框。
    /// 只比对被改的字段而不是整份文档,是为了让"两人改同一份文档的不同字段"这种常态不被误报。
    /// </para>
    /// </summary>
    public BsonDocument Filter
    {
        get
        {
            var filter = new BsonDocument("_id", Edit.Id);
            foreach (FieldChange change in Edit.Changes)
            {
                filter[change.Path] = change.Original is null
                    ? new BsonDocument("$exists", false)
                    : new BsonDocument("$eq", change.Original);
            }
            if (Edit.Original.TryGetValue("updatedAt", out BsonValue stamp) && !filter.Contains("updatedAt")
                && Edit.Changes.All(static c => c.Path != "updatedAt" && !StagedEdit.IsAncestor("updatedAt", c.Path)))
            {
                filter["updatedAt"] = new BsonDocument("$eq", stamp);
            }
            return filter;
        }
    }

    /// <summary><c>{ $set: {…}, $unset: {…} }</c>。</summary>
    public BsonDocument Update => BuildUpdate(Edit.Changes, static c => c.Value);

    /// <inheritdoc />
    public override WriteModel<BsonDocument> ToModel() => new UpdateOneModel<BsonDocument>(Filter, Update);

    /// <summary>按给定取值拼 <c>$set</c> / <c>$unset</c>(撤销时取原值、提交时取新值)。</summary>
    internal static BsonDocument BuildUpdate(IEnumerable<FieldChange> changes, Func<FieldChange, BsonValue?> pick)
    {
        var set = new BsonDocument();
        var unset = new BsonDocument();
        foreach (FieldChange change in changes)
        {
            if (pick(change) is { } value)
            {
                set[change.Path] = value;
            }
            else
            {
                unset[change.Path] = "";
            }
        }
        var update = new BsonDocument();
        if (set.ElementCount > 0)
        {
            update["$set"] = set;
        }
        if (unset.ElementCount > 0)
        {
            update["$unset"] = unset;
        }
        return update;
    }
}

/// <summary>新增:<c>insertOne</c>(没有 <c>_id</c> 时在客户端先生成 —— 撤销要靠它删回去)。</summary>
/// <param name="Insert">暂存的新文档。</param>
internal sealed record InsertOperation(StagedInsert Insert) : StagedOperation
{
    /// <summary>发出去的那份文档(带 <c>_id</c>)。</summary>
    public BsonDocument Document { get; } = WithId(Insert.Document);

    /// <inheritdoc />
    public override WriteModel<BsonDocument> ToModel() => new InsertOneModel<BsonDocument>(Document);

    private static BsonDocument WithId(BsonDocument source)
    {
        var doc = (BsonDocument)source.DeepClone();
        if (!doc.Contains("_id"))
        {
            // _id 放在第一位,与服务器自动生成时的位置一致。
            doc.InsertAt(0, new BsonElement("_id", ObjectId.GenerateNewId()));
        }
        return doc;
    }
}

/// <summary>删除:<c>deleteOne({ _id })</c>。</summary>
/// <param name="Delete">暂存的删除。</param>
internal sealed record DeleteOperation(StagedDelete Delete) : StagedOperation
{
    /// <inheritdoc />
    public override WriteModel<BsonDocument> ToModel() => new DeleteOneModel<BsonDocument>(new BsonDocument("_id", Delete.Id));
}

/// <summary>一次提交的范围:全部,或只提交某一份文档(检查器的「应用修改」)。</summary>
/// <param name="OnlyId">只提交这个 <c>_id</c> 的修改 / 删除;<see langword="null" /> = 全部。</param>
/// <param name="OnlyInsert">只提交这一份新文档。</param>
internal sealed record CommitScope(BsonValue? OnlyId = null, StagedInsert? OnlyInsert = null)
{
    /// <summary>全部。</summary>
    public static CommitScope All { get; } = new();

    /// <summary>是不是只针对一份文档。</summary>
    public bool IsScoped => OnlyId is not null || OnlyInsert is not null;
}

/// <summary>一处编辑冲突:我的修改 + 服务器上现在的版本(被删了为 <see langword="null" />)。</summary>
/// <param name="Edit">我的修改(仍留在暂存区里)。</param>
/// <param name="Server">服务器当前版本。</param>
internal sealed record EditConflict(StagedEdit Edit, BsonDocument? Server);

/// <summary>一条写失败(校验不过、重复键……)。</summary>
/// <param name="Operation">哪条。</param>
/// <param name="Code">错误码。</param>
/// <param name="Message">服务器给的原因。</param>
internal sealed record CommitFailure(StagedOperation Operation, int Code, string Message);

/// <summary>一次提交的结果。</summary>
/// <param name="Updated">成功更新的文档数。</param>
/// <param name="Inserted">成功插入的文档数。</param>
/// <param name="Deleted">成功删除的文档数。</param>
/// <param name="Elapsed">BulkWrite 往返耗时。</param>
/// <param name="Conflicts">乐观并发冲突(留在暂存区)。</param>
/// <param name="Failures">写失败(留在暂存区)。</param>
/// <param name="Undo">撤销计划(只含真正写进去的那些)。</param>
internal sealed record CommitOutcome(
    int Updated,
    int Inserted,
    int Deleted,
    TimeSpan Elapsed,
    IReadOnlyList<EditConflict> Conflicts,
    IReadOnlyList<CommitFailure> Failures,
    UndoPlan Undo)
{
    /// <summary>成功写入的条数。</summary>
    public int Applied => Updated + Inserted + Deleted;
}

/// <summary>
/// 撤销计划:用提交前的原像把这一批写回去。
/// <para>
/// 字段级回滚而不是整份替换:提交之后到点「撤销」之间,别人可能已经改了同一份文档的别的字段,
/// 整份替换会连带把那些也抹掉。筛选带上"我写进去的值",被人改过的字段就不回滚(matched 0),
/// 而不是把别人的修改当成我的撤掉。
/// </para>
/// </summary>
internal sealed class UndoPlan
{
    private readonly List<WriteModel<BsonDocument>> _models = [];

    /// <summary>有多少条回滚。</summary>
    public int Count => _models.Count;

    /// <summary>回滚用的写模型(供单测检查)。</summary>
    public IReadOnlyList<WriteModel<BsonDocument>> Models => _models;

    /// <summary>加一条"把更新改回去"。</summary>
    internal void AddRevertUpdate(StagedEdit edit)
    {
        var filter = new BsonDocument("_id", edit.Id);
        foreach (FieldChange change in edit.Changes)
        {
            filter[change.Path] = change.Value is null
                ? new BsonDocument("$exists", false)
                : new BsonDocument("$eq", change.Value);
        }
        BsonDocument update = UpdateOperation.BuildUpdate(edit.Changes, static c => c.Original);
        if (update.ElementCount > 0)
        {
            _models.Add(new UpdateOneModel<BsonDocument>(filter, update));
        }
    }

    /// <summary>加一条"把插入删掉"。</summary>
    internal void AddRevertInsert(BsonValue id) => _models.Add(new DeleteOneModel<BsonDocument>(new BsonDocument("_id", id)));

    /// <summary>加一条"把删除插回来"。</summary>
    internal void AddRevertDelete(BsonDocument original) => _models.Add(new InsertOneModel<BsonDocument>((BsonDocument)original.DeepClone()));

    /// <summary>执行回滚;返回成功回滚的条数(被别人改过的那些跳过)。</summary>
    public async Task<(int Restored, int Skipped)> ExecuteAsync(IMongoCollection<BsonDocument> collection, CancellationToken cancellationToken = default)
    {
        if (_models.Count == 0)
        {
            return (0, 0);
        }
        BulkWriteResult<BsonDocument> result;
        try
        {
            result = await collection.BulkWriteAsync(_models, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
        {
            result = ex.Result;
        }
        int restored = (int)(result.ModifiedCount + result.DeletedCount + result.InsertedCount);
        return (restored, _models.Count - restored);
    }
}

/// <summary>
/// 把暂存区合成一次 BulkWrite 发出去,并把结果分拣回三类:写进去了、冲突、失败。
/// <para>
/// 用**无序**批写(<c>ordered: false</c>):一条校验不过,不该拖累同一批里其他文档 ——
/// 有序批写会在第一条失败处整个停下,剩下的既没写进去也没报错,界面上就说不清哪些成了。
/// </para>
/// </summary>
internal static class StagingCommitter
{
    /// <summary>按范围列出要发的操作(顺序:更新、插入、删除)。</summary>
    public static IReadOnlyList<StagedOperation> Plan(StagingArea area, CommitScope scope)
    {
        var ops = new List<StagedOperation>();
        foreach (StagedEdit edit in area.Edits)
        {
            if (!edit.IsEmpty && (scope.OnlyId is null ? scope.OnlyInsert is null : scope.OnlyId.Equals(edit.Id)))
            {
                ops.Add(new UpdateOperation(edit));
            }
        }
        foreach (StagedInsert insert in area.Inserts)
        {
            if (!scope.IsScoped || ReferenceEquals(scope.OnlyInsert, insert))
            {
                ops.Add(new InsertOperation(insert));
            }
        }
        foreach (StagedDelete delete in area.Deletes)
        {
            if (scope.OnlyId is null ? scope.OnlyInsert is null : scope.OnlyId.Equals(delete.Id))
            {
                ops.Add(new DeleteOperation(delete));
            }
        }
        return ops;
    }

    /// <summary>提交。成功的从暂存区拿掉;冲突与失败的留下。</summary>
    public static async Task<CommitOutcome> CommitAsync(
        IMongoCollection<BsonDocument> collection,
        StagingArea area,
        CommitScope scope,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StagedOperation> ops = Plan(area, scope);
        var undo = new UndoPlan();
        if (ops.Count == 0)
        {
            return new(0, 0, 0, TimeSpan.Zero, [], [], undo);
        }
        List<WriteModel<BsonDocument>> models = [.. ops.Select(static o => o.ToModel())];
        var watch = Stopwatch.StartNew();
        BulkWriteResult<BsonDocument>? result;
        IReadOnlyList<BulkWriteError> errors = [];
        try
        {
            result = await collection.BulkWriteAsync(models, new BulkWriteOptions { IsOrdered = false }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
        {
            result = ex.Result;
            errors = ex.WriteErrors;
        }
        watch.Stop();

        var failed = new HashSet<int>(errors.Select(static e => e.Index));
        var failures = errors
            .Where(e => e.Index >= 0 && e.Index < ops.Count)
            .Select(e => new CommitFailure(ops[e.Index], e.Code, e.Message))
            .ToList();

        // 更新:matched 合计等于成功的更新条数,就全写进去了(常态,不必再往返);
        // 少了,就读回这几份文档逐一判定哪几份没对上原值。
        List<UpdateOperation> updates = [.. ops.Select((op, i) => (op, i))
            .Where(t => t.op is UpdateOperation && !failed.Contains(t.i))
            .Select(static t => (UpdateOperation)t.op)];
        var conflicts = new List<EditConflict>();
        var appliedEdits = new List<StagedEdit>();
        if (result is not null && result.IsAcknowledged && result.MatchedCount >= updates.Count)
        {
            appliedEdits.AddRange(updates.Select(static u => u.Edit));
        }
        else if (updates.Count > 0)
        {
            var ids = new BsonArray(updates.Select(static u => u.Edit.Id));
            List<BsonDocument> current = await collection
                .Find(new BsonDocument("_id", new BsonDocument("$in", ids)))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var byId = current.ToDictionary(static d => d["_id"]);
            foreach (UpdateOperation update in updates)
            {
                BsonDocument? server = byId.GetValueOrDefault(update.Edit.Id);
                if (server is not null && IsApplied(update.Edit, server))
                {
                    appliedEdits.Add(update.Edit);
                }
                else
                {
                    conflicts.Add(new EditConflict(update.Edit, server));
                }
            }
        }

        var appliedInserts = new List<InsertOperation>();
        var appliedDeletes = new List<DeleteOperation>();
        for (int i = 0; i < ops.Count; i++)
        {
            if (failed.Contains(i))
            {
                continue;
            }
            switch (ops[i])
            {
                case InsertOperation insert:
                    appliedInserts.Add(insert);
                    break;
                case DeleteOperation delete:
                    appliedDeletes.Add(delete);
                    break;
            }
        }

        foreach (StagedEdit edit in appliedEdits)
        {
            undo.AddRevertUpdate(edit);
        }
        foreach (InsertOperation insert in appliedInserts)
        {
            undo.AddRevertInsert(insert.Document["_id"]);
        }
        foreach (DeleteOperation delete in appliedDeletes)
        {
            undo.AddRevertDelete(delete.Delete.Original);
        }

        area.Remove(appliedEdits, appliedInserts.Select(static i => i.Insert), appliedDeletes.Select(static d => d.Delete));
        return new(
            appliedEdits.Count,
            appliedInserts.Count,
            (int)Math.Min(appliedDeletes.Count, result?.IsAcknowledged == true ? result.DeletedCount : appliedDeletes.Count),
            watch.Elapsed,
            conflicts,
            failures,
            undo);
    }

    /// <summary>服务器上的这份文档是否已经是"我改完之后"的样子(每个被改路径都等于我的新值)。</summary>
    internal static bool IsApplied(StagedEdit edit, BsonDocument server) =>
        edit.Changes.All(c => StagedEdit.SameValue(BsonPath.Get(server, c.Path), c.Value));
}
