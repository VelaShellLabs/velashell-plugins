using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Staging;

/// <summary>
/// 集合工作台的暂存区:网格、树、JSON 三视图与检查器的一切写操作都先落到这里,
/// 按 Ctrl+S 才合成**一次** BulkWrite 发出去。
/// <para>
/// 为什么不是"改一格写一次":生产库上逐格直写,一次手滑就是一次线上事故,而且没有整体撤销的抓手;
/// 攒成一批之后,确认、乐观并发检查、提交后 10 秒内撤销,都是对"这一批"做的 —— 与 Navicat / DataGrip 的
/// "待提交"同一个心智模型。
/// </para>
/// <para>只在 UI 线程上用(界面的每一处改动都从 UI 线程来),不加锁。</para>
/// </summary>
internal sealed class StagingArea
{
    private readonly Dictionary<BsonValue, StagedEdit> _edits = [];
    private readonly List<StagedEdit> _editOrder = [];
    private readonly List<StagedInsert> _inserts = [];
    private readonly Dictionary<BsonValue, StagedDelete> _deletes = [];
    private readonly List<StagedDelete> _deleteOrder = [];
    private int _insertSequence;

    /// <summary>内容变了(界面据此刷新行状态、待提交计数与标签橙点)。</summary>
    public event Action? Changed;

    /// <summary>改了字段的文档(按开始修改的先后)。</summary>
    public IReadOnlyList<StagedEdit> Edits => _editOrder;

    /// <summary>新增的文档。</summary>
    public IReadOnlyList<StagedInsert> Inserts => _inserts;

    /// <summary>要删除的文档。</summary>
    public IReadOnlyList<StagedDelete> Deletes => _deleteOrder;

    /// <summary>什么都没有。</summary>
    public bool IsEmpty => _editOrder.Count == 0 && _inserts.Count == 0 && _deleteOrder.Count == 0;

    /// <summary>"N 修改":改了字段的文档数。</summary>
    public int EditCount => _editOrder.Count;

    /// <summary>"N 新增"。</summary>
    public int InsertCount => _inserts.Count;

    /// <summary>"N 删除"。</summary>
    public int DeleteCount => _deleteOrder.Count;

    /// <summary>写操作条数(一份文档一条):提交后 Toast 里「已提交 N 处修改」的 N。</summary>
    public int OperationCount => EditCount + InsertCount + DeleteCount;

    /// <summary>字段级修改总数(JSON 视图底栏「共 3 处修改」)。</summary>
    public int FieldChangeCount => _editOrder.Sum(static e => e.Changes.Count);

    /// <summary>某份文档的暂存修改。</summary>
    public StagedEdit? EditOf(BsonValue? id) => id is not null && _edits.TryGetValue(id, out StagedEdit? edit) ? edit : null;

    /// <summary>某份文档是否已暂存删除。</summary>
    public bool IsDeleted(BsonValue? id) => id is not null && _deletes.ContainsKey(id);

    /// <summary>界面上该显示的版本:有暂存修改就是原像 + 修改,否则原样返回。</summary>
    public BsonDocument Current(BsonDocument original) =>
        EditOf(original.GetValue("_id", BsonNull.Value)) is { } edit ? edit.Apply() : original;

    /// <summary>
    /// 写一个字段。<paramref name="original" /> 是服务器读到的那份(第一次改这份文档时它成为原像;
    /// 之后再改同一份,沿用第一次的原像 —— 乐观并发要比对的正是"我开始改时它长什么样")。
    /// </summary>
    /// <param name="original">服务器版本。</param>
    /// <param name="path">点路径。</param>
    /// <param name="value">新值;<see langword="null" /> = 删除字段。</param>
    public void SetField(BsonDocument original, string path, BsonValue? value)
    {
        if (path == "_id")
        {
            throw new InvalidOperationException("_id is immutable.");
        }
        BsonValue id = original.GetValue("_id", BsonNull.Value);
        if (!_edits.TryGetValue(id, out StagedEdit? edit))
        {
            edit = new StagedEdit(original);
            _edits[id] = edit;
            _editOrder.Add(edit);
        }
        edit.Set(path, value);
        Prune(edit);
        Raise();
    }

    /// <summary>用一组差异整体替换某份文档的暂存修改(JSON 视图「更新文档」)。</summary>
    public void ReplaceEdits(BsonDocument original, IEnumerable<(string Path, BsonValue? Value)> changes)
    {
        BsonValue id = original.GetValue("_id", BsonNull.Value);
        if (!_edits.TryGetValue(id, out StagedEdit? edit))
        {
            edit = new StagedEdit(original);
            _edits[id] = edit;
            _editOrder.Add(edit);
        }
        edit.ReplaceAll(changes);
        Prune(edit);
        Raise();
    }

    /// <summary>撤掉一个字段的修改。</summary>
    public void RevertField(BsonValue id, string path)
    {
        if (EditOf(id) is { } edit && edit.Revert(path))
        {
            Prune(edit);
            Raise();
        }
    }

    /// <summary>暂存一份新文档。</summary>
    public StagedInsert AddInsert(BsonDocument document)
    {
        var insert = new StagedInsert(++_insertSequence, document);
        _inserts.Add(insert);
        Raise();
        return insert;
    }

    /// <summary>改一份暂存新文档的字段(新文档没有原像,直接改内容)。</summary>
    public void SetInsertField(StagedInsert insert, string path, BsonValue? value)
    {
        if (value is null)
        {
            Bson.BsonPath.Unset(insert.Document, path);
        }
        else
        {
            Bson.BsonPath.Set(insert.Document, path, value);
        }
        Raise();
    }

    /// <summary>整体替换一份暂存新文档。</summary>
    public void ReplaceInsert(StagedInsert insert, BsonDocument document)
    {
        insert.Document = document;
        Raise();
    }

    /// <summary>撤掉一份暂存新文档。</summary>
    public void RemoveInsert(StagedInsert insert)
    {
        if (_inserts.Remove(insert))
        {
            Raise();
        }
    }

    /// <summary>
    /// 暂存删除。删除压过修改:同一份文档先改后删,改的那部分没有意义了,一并撤掉 ——
    /// 否则提交时会先 update 再 delete,撤销时又得按相反顺序回滚,白白多两次往返。
    /// </summary>
    public void MarkDeleted(BsonDocument original)
    {
        BsonValue id = original.GetValue("_id", BsonNull.Value);
        if (_deletes.ContainsKey(id))
        {
            return;
        }
        if (_edits.Remove(id, out StagedEdit? edit))
        {
            _editOrder.Remove(edit);
        }
        var staged = new StagedDelete(id, (BsonDocument)(edit?.Original ?? original).DeepClone());
        _deletes[id] = staged;
        _deleteOrder.Add(staged);
        Raise();
    }

    /// <summary>撤掉暂存的删除。</summary>
    public void UnmarkDeleted(BsonValue id)
    {
        if (_deletes.Remove(id, out StagedDelete? staged))
        {
            _deleteOrder.Remove(staged);
            Raise();
        }
    }

    /// <summary>放弃一份文档上的全部暂存(修改与删除)。</summary>
    public void Discard(BsonValue id)
    {
        bool changed = false;
        if (_edits.Remove(id, out StagedEdit? edit))
        {
            _editOrder.Remove(edit);
            changed = true;
        }
        if (_deletes.Remove(id, out StagedDelete? staged))
        {
            _deleteOrder.Remove(staged);
            changed = true;
        }
        if (changed)
        {
            Raise();
        }
    }

    /// <summary>冲突解决:以服务器新版本为原像,只保留选中的那些路径。</summary>
    public void Rebase(BsonValue id, BsonDocument server, IReadOnlySet<string> keep)
    {
        if (EditOf(id) is not { } edit)
        {
            return;
        }
        edit.Rebase(server, keep);
        Prune(edit);
        Raise();
    }

    /// <summary>全部放弃。</summary>
    public void Clear()
    {
        if (IsEmpty)
        {
            return;
        }
        _edits.Clear();
        _editOrder.Clear();
        _inserts.Clear();
        _deletes.Clear();
        _deleteOrder.Clear();
        Raise();
    }

    /// <summary>提交成功的那部分从暂存区里拿掉(失败与冲突的留着,等用户处理)。</summary>
    internal void Remove(IEnumerable<StagedEdit> edits, IEnumerable<StagedInsert> inserts, IEnumerable<StagedDelete> deletes)
    {
        foreach (StagedEdit edit in edits)
        {
            if (_edits.Remove(edit.Id))
            {
                _editOrder.Remove(edit);
            }
        }
        foreach (StagedInsert insert in inserts)
        {
            _inserts.Remove(insert);
        }
        foreach (StagedDelete delete in deletes)
        {
            if (_deletes.Remove(delete.Id))
            {
                _deleteOrder.Remove(delete);
            }
        }
        Raise();
    }

    /// <summary>拍一张快照(Esc 放弃之后的「撤销」靠它恢复 —— 放弃本身不碰服务器,恢复也不必)。</summary>
    public Snapshot TakeSnapshot() => new(
        [.. _editOrder.Select(static e => e.Clone())],
        [.. _inserts.Select(static i => i.Clone())],
        [.. _deleteOrder],
        _insertSequence);

    /// <summary>恢复到某张快照。</summary>
    public void Restore(Snapshot snapshot)
    {
        _edits.Clear();
        _editOrder.Clear();
        _inserts.Clear();
        _deletes.Clear();
        _deleteOrder.Clear();
        foreach (StagedEdit edit in snapshot.Edits)
        {
            StagedEdit copy = edit.Clone();
            _edits[copy.Id] = copy;
            _editOrder.Add(copy);
        }
        _inserts.AddRange(snapshot.Inserts.Select(static i => i.Clone()));
        foreach (StagedDelete delete in snapshot.Deletes)
        {
            _deletes[delete.Id] = delete;
            _deleteOrder.Add(delete);
        }
        _insertSequence = Math.Max(_insertSequence, snapshot.InsertSequence);
        Raise();
    }

    private void Prune(StagedEdit edit)
    {
        if (edit.IsEmpty && _edits.Remove(edit.Id))
        {
            _editOrder.Remove(edit);
        }
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>暂存区快照。</summary>
    /// <param name="Edits">修改。</param>
    /// <param name="Inserts">新增。</param>
    /// <param name="Deletes">删除。</param>
    /// <param name="InsertSequence">新增序号的高水位。</param>
    internal sealed record Snapshot(
        IReadOnlyList<StagedEdit> Edits,
        IReadOnlyList<StagedInsert> Inserts,
        IReadOnlyList<StagedDelete> Deletes,
        int InsertSequence)
    {
        /// <summary>快照里的操作条数。</summary>
        public int OperationCount => Edits.Count + Inserts.Count + Deletes.Count;
    }
}
