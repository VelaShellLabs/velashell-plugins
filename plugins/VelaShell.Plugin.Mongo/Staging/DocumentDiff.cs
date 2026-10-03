using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Staging;

/// <summary>
/// 两份文档的字段级差异:<c>$set</c> 新值 / <c>$unset</c>(值为 <see langword="null" />)。
/// 子文档递归比较(只改 <c>customer.name</c> 就只写这一个路径);数组整体比较 ——
/// 按下标逐项 <c>$set</c> 遇到插入 / 删除元素就全错位,整体替换才说得清。
/// </summary>
internal static class DocumentDiff
{
    /// <summary>算差异(<c>_id</c> 不参与)。</summary>
    public static List<(string Path, BsonValue? Value)> Compute(BsonDocument original, BsonDocument edited)
    {
        var changes = new List<(string, BsonValue?)>();
        Walk(original, edited, null, changes);
        return changes;
    }

    private static void Walk(BsonDocument before, BsonDocument after, string? prefix, List<(string, BsonValue?)> changes)
    {
        foreach (BsonElement element in after)
        {
            if (prefix is null && element.Name == "_id")
            {
                continue;
            }
            string path = BsonPath.Join(prefix, element.Name);
            if (!before.TryGetValue(element.Name, out BsonValue old))
            {
                changes.Add((path, element.Value));
            }
            else if (old is BsonDocument oldDoc && element.Value is BsonDocument newDoc && oldDoc.ElementCount > 0 && newDoc.ElementCount > 0)
            {
                Walk(oldDoc, newDoc, path, changes);
            }
            else if (!StagedEdit.SameValue(old, element.Value))
            {
                changes.Add((path, element.Value));
            }
        }
        foreach (BsonElement element in before)
        {
            if (prefix is null && element.Name == "_id")
            {
                continue;
            }
            if (!after.Contains(element.Name))
            {
                changes.Add((BsonPath.Join(prefix, element.Name), null));
            }
        }
    }
}
