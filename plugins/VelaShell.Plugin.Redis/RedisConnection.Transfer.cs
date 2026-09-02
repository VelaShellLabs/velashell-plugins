using System.Globalization;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary>导出落盘的格式。</summary>
public enum RedisExportFormat
{
    /// <summary>
    /// <c>DUMP</c> + <c>RESTORE</c>:保的是服务端序列化后的**原始字节**,连编码与 TTL 一起带走。
    /// 五种类型都精确,跨版本回灌最稳 —— 代价是文件不给人看。
    /// </summary>
    DumpRestore,

    /// <summary>可直接喂给 <c>redis-cli</c> 的命令行(<c>SET</c>/<c>HSET</c>/…)。人能读、能改。</summary>
    RespCommands,

    /// <summary>一行一个 JSON 对象。给人看与给脚本吃都方便;二进制值按 <c>\xNN</c> 转义。</summary>
    Jsonl
}

/// <summary>一次导出的结果。</summary>
/// <param name="Keys">写出去的键数。</param>
/// <param name="Bytes">文件字节数。</param>
/// <param name="Skipped">跳过的键(过期/被删/该格式表达不了)。</param>
/// <param name="Path">落盘路径。</param>
public sealed record RedisExportResult(int Keys, long Bytes, IReadOnlyList<string> Skipped, string Path);

/// <summary>
/// 键的搬运:新建、导出。
/// <para>
/// 这里刻意**不做**迁移作业 —— 长时任务 + 一致性校验是 <c>RIOT</c> 一类工具的战场。
/// 能做的只有一件事:把**你选中的这些键**原样搬出去(见 README §八)。
/// </para>
/// </summary>
internal sealed partial class RedisConnection
{
    /// <summary>
    /// 新建一个键。
    /// <para>
    /// <b>不覆盖</b>是默认:字符串走 <c>SET … NX</c>,其余类型先 <c>EXISTS</c> 再写。
    /// "新建"这个词在用户脑子里就是"原来没有",让它悄悄盖掉一个同名键是背叛。
    /// </para>
    /// </summary>
    /// <param name="key">键名。</param>
    /// <param name="type">类型名(<c>string</c>/<c>hash</c>/…)。</param>
    /// <param name="value">初始值(字符串类型是整个值;集合类是第一个成员的值)。</param>
    /// <param name="field">哈希字段名 / 有序集合分值文本;其余类型忽略。</param>
    /// <param name="ttl">存活时间;null 表示不过期。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建成功;键已存在时为 false。</returns>
    public async Task<bool> CreateKeyAsync(
        RedisKeyName key,
        string type,
        byte[] value,
        string field,
        TimeSpan? ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        IDatabase db = Db();
        RedisKey redisKey = key.ToRedisKey();

        if (type is "string")
        {
            // 一条命令同时做掉"不存在才写"与"顺带设过期":两步写在中间断电就会留下一个永不过期的键。
            bool created = ttl is { } span
                ? await db.StringSetAsync(redisKey, value, span, When.NotExists).ConfigureAwait(false)
                : await db.StringSetAsync(redisKey, value, when: When.NotExists).ConfigureAwait(false);
            return created;
        }

        if (await db.KeyExistsAsync(redisKey).ConfigureAwait(false))
        {
            return false;
        }
        switch (type)
        {
            case "hash":
                await db.HashSetAsync(redisKey, field.Length > 0 ? field : "field", value).ConfigureAwait(false);
                break;
            case "list":
                await db.ListRightPushAsync(redisKey, value).ConfigureAwait(false);
                break;
            case "set":
                await db.SetAddAsync(redisKey, value).ConfigureAwait(false);
                break;
            case "zset":
                await db.SortedSetAddAsync(redisKey, value,
                    double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double score) ? score : 0)
                    .ConfigureAwait(false);
                break;
            case "stream":
                await db.StreamAddAsync(redisKey, field.Length > 0 ? field : "field", value).ConfigureAwait(false);
                break;
            default:
                return false;
        }
        if (ttl is { } expiry)
        {
            await db.KeyExpireAsync(redisKey, expiry).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// 把一批键导出到文件。
    /// <para>
    /// 逐键一个往返(而不是流水线):导出是人按一下就走开的动作,吞吐无关紧要,
    /// 而**逐键失败不牵连其余**要紧 —— 一个在导出途中过期的键只该出现在"跳过"清单里。
    /// </para>
    /// </summary>
    /// <param name="keys">要导出的键。</param>
    /// <param name="format">格式。</param>
    /// <param name="path">落盘路径(已展开 <c>~</c>)。</param>
    /// <param name="progress">进度回调(已导出键数)。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>导出结果。</returns>
    public async Task<RedisExportResult> ExportAsync(
        IReadOnlyList<RedisKeyName> keys,
        RedisExportFormat format,
        string path,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IDatabase db = Db();
        var skipped = new List<string>();
        int written = 0;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        await using (var file = new StreamWriter(path, append: false, new UTF8Encoding(false)))
        {
            if (format == RedisExportFormat.RespCommands)
            {
                await file.WriteLineAsync($"# velashell redis export · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · db{_database}")
                    .ConfigureAwait(false);
            }
            foreach (RedisKeyName key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line;
                try
                {
                    line = await BuildExportLineAsync(db, key, format).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDeniedOrUnsupported(ex))
                {
                    skipped.Add(key.Display);
                    continue;
                }
                if (line is null)
                {
                    skipped.Add(key.Display);
                    continue;
                }
                await file.WriteLineAsync(line).ConfigureAwait(false);
                written++;
                progress?.Invoke(written);
            }
        }
        return new(written, new FileInfo(path).Length, skipped, path);
    }

    private async Task<string?> BuildExportLineAsync(IDatabase db, RedisKeyName key, RedisExportFormat format)
    {
        RedisKey redisKey = key.ToRedisKey();
        TimeSpan? ttl = await db.KeyTimeToLiveAsync(redisKey).ConfigureAwait(false);
        long ttlMs = ttl is { } span ? (long)span.TotalMilliseconds : 0;

        if (format == RedisExportFormat.DumpRestore)
        {
            byte[]? payload = await db.KeyDumpAsync(redisKey).ConfigureAwait(false);
            // DUMP 对不存在的键回 nil —— 那是"导出途中它过期了",跳过并记名,不是错误。
            return payload is null
                ? null
                : $"RESTORE \"{RedisValueText.Escape(key.Raw.ToArray())}\" {ttlMs.ToString(CultureInfo.InvariantCulture)} "
                  + $"\"{RedisValueText.Escape(payload)}\" REPLACE";
        }

        RedisType type = await db.KeyTypeAsync(redisKey).ConfigureAwait(false);
        string typeName = TypeName(type);
        if (type == RedisType.None)
        {
            return null;
        }
        return format == RedisExportFormat.Jsonl
            ? await BuildJsonLineAsync(db, key, typeName, ttlMs).ConfigureAwait(false)
            : await BuildRespLineAsync(db, key, typeName, ttlMs).ConfigureAwait(false);
    }

    private static async Task<string?> BuildRespLineAsync(IDatabase db, RedisKeyName key, string type, long ttlMs)
    {
        RedisKey redisKey = key.ToRedisKey();
        string name = Quote(key.Raw.ToArray());
        string expire = ttlMs > 0
            ? $"\nPEXPIRE {name} {ttlMs.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        switch (type)
        {
            case "string":
            {
                RedisValue value = await db.StringGetAsync(redisKey).ConfigureAwait(false);
                return $"SET {name} {Quote((byte[]?)value ?? [])}{expire}";
            }
            case "hash":
            {
                HashEntry[] entries = await db.HashGetAllAsync(redisKey).ConfigureAwait(false);
                if (entries.Length == 0)
                {
                    return null;
                }
                var builder = new StringBuilder("HSET ").Append(name);
                foreach (HashEntry entry in entries)
                {
                    builder.Append(' ').Append(Quote((byte[]?)entry.Name ?? []))
                        .Append(' ').Append(Quote((byte[]?)entry.Value ?? []));
                }
                return builder.Append(expire).ToString();
            }
            case "list":
            {
                RedisValue[] items = await db.ListRangeAsync(redisKey).ConfigureAwait(false);
                return items.Length == 0 ? null : Join("RPUSH", name, items) + expire;
            }
            case "set":
            {
                RedisValue[] members = await db.SetMembersAsync(redisKey).ConfigureAwait(false);
                return members.Length == 0 ? null : Join("SADD", name, members) + expire;
            }
            case "zset":
            {
                SortedSetEntry[] entries = await db.SortedSetRangeByRankWithScoresAsync(redisKey).ConfigureAwait(false);
                if (entries.Length == 0)
                {
                    return null;
                }
                var builder = new StringBuilder("ZADD ").Append(name);
                foreach (SortedSetEntry entry in entries)
                {
                    builder.Append(' ').Append(FormatScore(entry.Score))
                        .Append(' ').Append(Quote((byte[]?)entry.Element ?? []));
                }
                return builder.Append(expire).ToString();
            }
            default:
                // 流的条目 id 是服务端生成的,用 XADD 重放会拿到**新的 id** —— 那不是同一份数据。
                // 与其写一行看着像成功的命令,不如如实跳过,并让界面提示改用 DUMP。
                return null;
        }

        static string Join(string command, string name, RedisValue[] values)
        {
            var builder = new StringBuilder(command).Append(' ').Append(name);
            foreach (RedisValue value in values)
            {
                builder.Append(' ').Append(Quote((byte[]?)value ?? []));
            }
            return builder.ToString();
        }
    }

    private static async Task<string?> BuildJsonLineAsync(IDatabase db, RedisKeyName key, string type, long ttlMs)
    {
        RedisKey redisKey = key.ToRedisKey();
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["key"] = key.Display,
            ["type"] = type,
            ["ttlMs"] = ttlMs
        };
        switch (type)
        {
            case "string":
                payload["value"] = Text((byte[]?)await db.StringGetAsync(redisKey).ConfigureAwait(false) ?? []);
                break;
            case "hash":
                payload["value"] = (await db.HashGetAllAsync(redisKey).ConfigureAwait(false))
                    .ToDictionary(entry => Text((byte[]?)entry.Name ?? []), entry => Text((byte[]?)entry.Value ?? []),
                        StringComparer.Ordinal);
                break;
            case "list":
                payload["value"] = (await db.ListRangeAsync(redisKey).ConfigureAwait(false))
                    .Select(value => Text((byte[]?)value ?? [])).ToArray();
                break;
            case "set":
                payload["value"] = (await db.SetMembersAsync(redisKey).ConfigureAwait(false))
                    .Select(value => Text((byte[]?)value ?? [])).ToArray();
                break;
            case "zset":
                payload["value"] = (await db.SortedSetRangeByRankWithScoresAsync(redisKey).ConfigureAwait(false))
                    .Select(entry => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["member"] = Text((byte[]?)entry.Element ?? []),
                        ["score"] = entry.Score
                    }).ToArray();
                break;
            default:
                return null;
        }
        return JsonSerializer.Serialize(payload,
            new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
    }

    /// <summary>
    /// JSONL 里的一段值。二进制按 <c>\xNN</c> 转义 —— JSON 没有字节串,
    /// 硬塞进去只会得到一串替换字符,而那已经不是原来的值了。
    /// </summary>
    private static string Text(byte[] raw) =>
        RedisValueText.IsTextSafe(raw) ? Encoding.UTF8.GetString(raw) : RedisValueText.Escape(raw);

    /// <summary>命令行里的一段参数:一律加引号 + 转义,带空格与二进制的值才粘得进 redis-cli。</summary>
    private static string Quote(byte[] raw) => $"\"{RedisValueText.Escape(raw)}\"";
}
