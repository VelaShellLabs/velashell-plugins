using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Staging;

/// <summary>
/// 一处字段级修改:路径 + 原值 + 新值。
/// <para>
/// 原值与新值都可以是 <see langword="null" />,含义是"字段不存在"而不是 BSON 的 null ——
/// 原值为 null 即新增字段(提交时筛选写 <c>$exists: false</c>),新值为 null 即删除字段(<c>$unset</c>)。
/// 两者都显式记下,是因为乐观并发的筛选要原值、撤销要原值、界面的"原值 2"也要原值。
/// </para>
/// </summary>
/// <param name="Path">点路径(<c>customer.name</c>、<c>items.0.qty</c>)。</param>
/// <param name="Original">原值;字段原本不存在为 <see langword="null" />。</param>
/// <param name="Value">新值;删除字段为 <see langword="null" />。</param>
internal sealed record FieldChange(string Path, BsonValue? Original, BsonValue? Value)
{
    /// <summary>删除字段(<c>$unset</c>)。</summary>
    public bool IsUnset => Value is null;

    /// <summary>新增字段(原本不存在)。</summary>
    public bool IsAdded => Original is null && Value is not null;
}

/// <summary>某个路径与暂存修改的关系(网格 / 树 / 检查器据此配色)。</summary>
internal enum ChangeRelation
{
    /// <summary>没有关系。</summary>
    None,

    /// <summary>这个路径本身被改了。</summary>
    Exact,

    /// <summary>它的某个祖先被整体替换了(值来自新的祖先值)。</summary>
    InsideChange,

    /// <summary>它的某个后代被改了(容器里有修改)。</summary>
    ContainsChange
}

/// <summary>
/// 一份已有文档上暂存的修改。
/// <para>
/// 记的是**字段级差异**而不是整份新文档:提交时生成 <c>$set</c> / <c>$unset</c>,只碰改过的字段 ——
/// 别的会话同时改了这份文档的另一个字段,两边的修改都能保住;整份替换(<c>replaceOne</c>)会把对方的悄悄抹掉。
/// </para>
/// </summary>
internal sealed class StagedEdit
{
    private readonly List<FieldChange> _changes = [];

    /// <summary>以服务器读到的那份文档为原像开始暂存。</summary>
    /// <param name="original">原像(会深拷贝 —— 之后界面上的任何改动都碰不到它)。</param>
    public StagedEdit(BsonDocument original)
    {
        Original = (BsonDocument)original.DeepClone();
        Id = Original.GetValue("_id", BsonNull.Value);
    }

    /// <summary>文档的 <c>_id</c>。</summary>
    public BsonValue Id { get; }

    /// <summary>原像:开始编辑时从服务器读到的版本(乐观并发与撤销都以它为准)。</summary>
    public BsonDocument Original { get; private set; }

    /// <summary>按发生顺序排列的修改。</summary>
    public IReadOnlyList<FieldChange> Changes => _changes;

    /// <summary>有没有修改。</summary>
    public bool IsEmpty => _changes.Count == 0;

    /// <summary>原像 + 修改 = 界面上看到的那份文档。</summary>
    public BsonDocument Apply()
    {
        var current = (BsonDocument)Original.DeepClone();
        foreach (FieldChange change in _changes)
        {
            if (change.Value is null)
            {
                BsonPath.Unset(current, change.Path);
            }
            else
            {
                BsonPath.Set(current, change.Path, change.Value.DeepClone());
            }
        }
        return current;
    }

    /// <summary>某个路径上的修改(精确匹配)。</summary>
    public FieldChange? Find(string path) => _changes.FirstOrDefault(c => c.Path == path);

    /// <summary>某个路径与这些修改的关系。</summary>
    public ChangeRelation RelationOf(string path)
    {
        ChangeRelation best = ChangeRelation.None;
        foreach (FieldChange change in _changes)
        {
            if (change.Path == path)
            {
                return ChangeRelation.Exact;
            }
            if (IsAncestor(change.Path, path))
            {
                best = ChangeRelation.InsideChange;
            }
            else if (best == ChangeRelation.None && IsAncestor(path, change.Path))
            {
                best = ChangeRelation.ContainsChange;
            }
        }
        return best;
    }

    /// <summary>
    /// 写一个路径(<paramref name="value" /> 为 <see langword="null" /> 即删除字段)。
    /// <para>
    /// MongoDB 不允许同一条更新里出现互为祖孙的两个路径(<c>customer</c> 与 <c>customer.name</c>
    /// 会报 "would create a conflict"),所以这里把它们**合并**:改了祖先之后再改后代,就把后代写进祖先的新值;
    /// 改了后代之后再整体改祖先,后代那几条就被祖先取代。改回原值的修改直接撤掉 —— 待提交计数不该把"改了又改回来"算进去。
    /// </para>
    /// </summary>
    public void Set(string path, BsonValue? value)
    {
        int ancestorIndex = _changes.FindIndex(c => IsAncestor(c.Path, path));
        if (ancestorIndex >= 0)
        {
            FieldChange ancestor = _changes[ancestorIndex];
            string relative = path[(ancestor.Path.Length + 1)..];
            BsonValue rebuilt = Rebuild(ancestor.Value, relative, value);
            _changes.RemoveAt(ancestorIndex);
            Put(ancestor.Path, rebuilt, ancestorIndex);
            return;
        }
        _changes.RemoveAll(c => IsAncestor(path, c.Path));
        Put(path, value, -1);
    }

    /// <summary>撤掉一个路径上的修改(连同它下面的);返回是否撤掉了东西。</summary>
    public bool Revert(string path) => _changes.RemoveAll(c => c.Path == path || IsAncestor(path, c.Path)) > 0;

    /// <summary>
    /// 换原像:冲突解决时以服务器的新版本为原像,只保留 <paramref name="keep" /> 里的那些路径的修改
    /// (原值按新原像重算,与新原像相同的修改丢掉)。
    /// </summary>
    public void Rebase(BsonDocument server, IReadOnlySet<string> keep)
    {
        List<FieldChange> kept = [.. _changes.Where(c => keep.Contains(c.Path))];
        Original = (BsonDocument)server.DeepClone();
        _changes.Clear();
        foreach (FieldChange change in kept)
        {
            Put(change.Path, change.Value, -1);
        }
    }

    /// <summary>整体替换为另一组修改(JSON 视图"更新文档"算出的差异)。</summary>
    public void ReplaceAll(IEnumerable<(string Path, BsonValue? Value)> changes)
    {
        _changes.Clear();
        foreach ((string path, BsonValue? value) in changes)
        {
            Set(path, value);
        }
    }

    /// <summary>深拷贝(暂存区快照用)。</summary>
    public StagedEdit Clone()
    {
        var copy = new StagedEdit(Original);
        copy._changes.AddRange(_changes.Select(static c => c with
        {
            Original = c.Original?.DeepClone(),
            Value = c.Value?.DeepClone()
        }));
        return copy;
    }

    private void Put(string path, BsonValue? value, int index)
    {
        BsonValue? original = BsonPath.Get(Original, path);
        _changes.RemoveAll(c => c.Path == path);
        if (SameValue(original, value))
        {
            return;
        }
        var change = new FieldChange(path, original?.DeepClone(), value?.DeepClone());
        if (index >= 0 && index <= _changes.Count)
        {
            _changes.Insert(index, change);
        }
        else
        {
            _changes.Add(change);
        }
    }

    /// <summary>在祖先的新值里写 / 删一个相对路径;祖先原本被删或是标量时,从空文档起建。</summary>
    private static BsonValue Rebuild(BsonValue? ancestor, string relative, BsonValue? value)
    {
        BsonValue start = ancestor is BsonDocument or BsonArray ? ancestor.DeepClone() : new BsonDocument();
        var holder = new BsonDocument("v", start);
        if (value is null)
        {
            BsonPath.Unset(holder, "v." + relative);
        }
        else
        {
            BsonPath.Set(holder, "v." + relative, value.DeepClone());
        }
        return holder["v"];
    }

    /// <summary><paramref name="ancestor" /> 是不是 <paramref name="path" /> 的严格祖先。</summary>
    internal static bool IsAncestor(string ancestor, string path) =>
        path.Length > ancestor.Length + 1 && path.StartsWith(ancestor, StringComparison.Ordinal) && path[ancestor.Length] == '.';

    /// <summary>
    /// 类型严格的相等:<c>Int32(3)</c> 与 <c>Double(3.0)</c> 不算一样 —— 把类型从 Double 改成 Int32
    /// 本身就是一处修改,不能被数值相等吞掉。
    /// </summary>
    internal static bool SameValue(BsonValue? a, BsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        return a.BsonType == b.BsonType && a.Equals(b);
    }
}

/// <summary>暂存的一份新文档(网格底栏「+」或克隆)。</summary>
internal sealed class StagedInsert
{
    /// <summary>构造。</summary>
    /// <param name="key">暂存区内的序号(提交前还没有 <c>_id</c>,界面靠它认行)。</param>
    /// <param name="document">文档内容。</param>
    public StagedInsert(int key, BsonDocument document)
    {
        Key = key;
        Document = document;
    }

    /// <summary>暂存区内的序号。</summary>
    public int Key { get; }

    /// <summary>文档内容(没有 <c>_id</c> 时提交前现生成一个)。</summary>
    public BsonDocument Document { get; set; }

    /// <summary>深拷贝。</summary>
    public StagedInsert Clone() => new(Key, (BsonDocument)Document.DeepClone());
}

/// <summary>暂存的一次删除。</summary>
/// <param name="Id">文档 <c>_id</c>。</param>
/// <param name="Original">删除前的整份文档(撤销时原样插回去)。</param>
internal sealed record StagedDelete(BsonValue Id, BsonDocument Original);
