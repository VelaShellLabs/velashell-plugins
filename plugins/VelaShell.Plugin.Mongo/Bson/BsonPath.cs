using System.Globalization;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Bson;

/// <summary>
/// 点路径(<c>customer.name</c>、<c>items.0.sku</c>)的读、写、删。
/// 与 MongoDB 的点号语义一致:数组下标也是一段路径。
/// </summary>
public static class BsonPath
{
    /// <summary>拆路径。</summary>
    public static string[] Split(string path) => path.Split('.');

    /// <summary>拼路径。</summary>
    public static string Join(string? parent, string name) => string.IsNullOrEmpty(parent) ? name : $"{parent}.{name}";

    /// <summary>取值;任何一段不存在都返回 <see langword="null" />(= 缺失)。</summary>
    public static BsonValue? Get(BsonValue root, string path)
    {
        BsonValue? current = root;
        foreach (string segment in Split(path))
        {
            current = current switch
            {
                BsonDocument doc => doc.TryGetValue(segment, out BsonValue v) ? v : null,
                BsonArray array when TryIndex(segment, array.Count, out int index) => array[index],
                _ => null
            };
            if (current is null)
            {
                return null;
            }
        }
        return current;
    }

    /// <summary>
    /// 写值:沿途缺的文档层自动补上(与 <c>$set</c> 的语义一致),数组下标越界则失败。
    /// </summary>
    /// <returns>是否写成。</returns>
    public static bool Set(BsonDocument root, string path, BsonValue value)
    {
        string[] segments = Split(path);
        BsonValue current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string segment = segments[i];
            switch (current)
            {
                case BsonDocument doc:
                    if (!doc.TryGetValue(segment, out BsonValue next) || next is not (BsonDocument or BsonArray))
                    {
                        next = new BsonDocument();
                        doc[segment] = next;
                    }
                    current = next;
                    break;
                case BsonArray array when TryIndex(segment, array.Count, out int index):
                    current = array[index];
                    break;
                default:
                    return false;
            }
        }
        string last = segments[^1];
        switch (current)
        {
            case BsonDocument target:
                target[last] = value;
                return true;
            case BsonArray list when TryIndex(last, list.Count, out int at):
                list[at] = value;
                return true;
            case BsonArray append when last == append.Count.ToString(CultureInfo.InvariantCulture):
                _ = append.Add(value);
                return true;
            default:
                return false;
        }
    }

    /// <summary>删字段(数组元素按下标删除并前移)。</summary>
    /// <returns>是否删掉了东西。</returns>
    public static bool Unset(BsonDocument root, string path)
    {
        string[] segments = Split(path);
        string parentPath = string.Join('.', segments[..^1]);
        BsonValue? parent = segments.Length == 1 ? root : Get(root, parentPath);
        string last = segments[^1];
        switch (parent)
        {
            case BsonDocument doc when doc.Contains(last):
                doc.Remove(last);
                return true;
            case BsonArray array when TryIndex(last, array.Count, out int index):
                array.RemoveAt(index);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 一份文档里出现的全部叶子路径(Schema 抽样、导出字段表、补全用)。
    /// 数组不展开下标,而是把元素合并看成"数组里的那种对象"—— <c>items.sku</c> 而不是 <c>items.0.sku</c>。
    /// </summary>
    public static IEnumerable<(string Path, BsonValue Value)> Walk(BsonDocument doc, string? prefix = null, int maxDepth = 6)
    {
        foreach (BsonElement element in doc)
        {
            string path = Join(prefix, element.Name);
            yield return (path, element.Value);
            if (maxDepth <= 0)
            {
                continue;
            }
            if (element.Value is BsonDocument child)
            {
                foreach ((string Path, BsonValue Value) nested in Walk(child, path, maxDepth - 1))
                {
                    yield return nested;
                }
            }
            else if (element.Value is BsonArray array)
            {
                foreach (BsonDocument item in array.OfType<BsonDocument>())
                {
                    foreach ((string Path, BsonValue Value) nested in Walk(item, path, maxDepth - 1))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    private static bool TryIndex(string segment, int count, out int index) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0 && index < count;
}
