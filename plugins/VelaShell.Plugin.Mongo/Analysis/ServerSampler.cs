using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Analysis;

/// <summary>
/// 一次 <c>serverStatus</c> 的要点(设计稿 11 的六张指标卡与两张柱状图的原料)。
/// <para>
/// 计数器(opcounters、网络字节、淘汰页数)一律**原样存累计值**:率值只能由相邻两次采样相减得出 ——
/// 服务器给的是"自启动以来",单看一次毫无意义。
/// </para>
/// </summary>
internal sealed record ServerSample
{
    /// <summary>采样时刻(本机 UTC;率值按本机时钟算,避免与服务器时钟漂移搅在一起)。</summary>
    public DateTime Time { get; init; }

    /// <summary>运行时长(秒)。变小即服务器重启过,计数器从零重来。</summary>
    public double Uptime { get; init; }

    /// <summary>当前连接数。</summary>
    public long Connections { get; init; }

    /// <summary>还能接受的连接数。</summary>
    public long ConnectionsAvailable { get; init; }

    /// <summary>opcounters.query(累计)。</summary>
    public long Query { get; init; }

    /// <summary>opcounters.getmore(累计)。</summary>
    public long GetMore { get; init; }

    /// <summary>opcounters.insert(累计)。</summary>
    public long Insert { get; init; }

    /// <summary>opcounters.update(累计)。</summary>
    public long Update { get; init; }

    /// <summary>opcounters.delete(累计)。</summary>
    public long Delete { get; init; }

    /// <summary>network.bytesIn(累计)。</summary>
    public long BytesIn { get; init; }

    /// <summary>network.bytesOut(累计)。</summary>
    public long BytesOut { get; init; }

    /// <summary>WiredTiger 缓存当前占用(字节);非 WiredTiger 为 0。</summary>
    public long CacheUsed { get; init; }

    /// <summary>WiredTiger 缓存上限(字节);非 WiredTiger 为 0。</summary>
    public long CacheMax { get; init; }

    /// <summary>缓存里的脏页字节。</summary>
    public long CacheDirty { get; init; }

    /// <summary>淘汰过的页数(干净页 + 脏页,累计)。</summary>
    public long Evicted { get; init; }

    /// <summary>已提交的索引构建数(累计,<c>indexBuilds.phases.commit</c>;老版本没有为 -1)。</summary>
    public long IndexBuilds { get; init; } = -1;

    /// <summary>存储引擎(<c>WiredTiger</c>)。</summary>
    public string Engine { get; init; } = "";

    /// <summary>featureCompatibilityVersion(<c>7.0</c>);serverStatus 里没有为 null。</summary>
    public string? Fcv { get; init; }

    /// <summary>
    /// 从 <c>serverStatus</c> 原文取要点。缺字段一律按 0 处理:mongos 没有 wiredTiger 段,
    /// 老版本没有 indexBuilds —— 少一张卡的数字好过整页报错。
    /// </summary>
    /// <param name="status">serverStatus 原文。</param>
    /// <param name="time">采样时刻。</param>
    public static ServerSample Parse(BsonDocument status, DateTime time)
    {
        BsonDocument connections = Section(status, "connections");
        BsonDocument ops = Section(status, "opcounters");
        BsonDocument network = Section(status, "network");
        BsonDocument cache = Section(Section(status, "wiredTiger"), "cache");
        BsonDocument builds = Section(status, "indexBuilds");
        string? fcv = null;
        if (status.TryGetValue("featureCompatibilityVersion", out BsonValue f) && f is BsonDocument fd && fd.Contains("major"))
        {
            fcv = $"{Number(fd, "major")}.{Number(fd, "minor")}";
        }
        string engine = status.TryGetValue("storageEngine", out BsonValue se) && se is BsonDocument sd && sd.TryGetValue("name", out BsonValue n) && n.IsString
            ? n.AsString
            : "";
        return new()
        {
            Time = time,
            Uptime = status.TryGetValue("uptime", out BsonValue up) && up.IsNumeric ? up.ToDouble() : 0,
            Connections = Number(connections, "current"),
            ConnectionsAvailable = Number(connections, "available"),
            Query = Number(ops, "query"),
            GetMore = Number(ops, "getmore"),
            Insert = Number(ops, "insert"),
            Update = Number(ops, "update"),
            Delete = Number(ops, "delete"),
            BytesIn = Number(network, "bytesIn"),
            BytesOut = Number(network, "bytesOut"),
            CacheUsed = Number(cache, "bytes currently in the cache"),
            CacheMax = Number(cache, "maximum bytes configured"),
            CacheDirty = Number(cache, "tracked dirty bytes in the cache"),
            Evicted = Number(cache, "unmodified pages evicted") + Number(cache, "modified pages evicted"),
            // total 是"开始过"的构建数;完成要看 phases.commit —— 事件栏说的是"构建完成"。
            IndexBuilds = Section(builds, "phases").Contains("commit")
                ? Number(Section(builds, "phases"), "commit")
                : builds.Contains("total") ? Number(builds, "total") : -1,
            Engine = engine == "wiredTiger" ? "WiredTiger" : engine,
            Fcv = fcv
        };
    }

    private static BsonDocument Section(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v is BsonDocument d ? d : [];

    private static long Number(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsNumeric ? v.ToInt64() : 0;
}

/// <summary>
/// 相邻两次采样之间的率值(每秒)。
/// <para>
/// "读"只算 query + getmore,"写"是 insert + update + delete;<c>command</c> 不进图 ——
/// 它包含驱动的心跳与本页自己每 2 秒一次的 serverStatus,空闲的服务器上它会是图里最高的一根,
/// 却说明不了业务负载。
/// </para>
/// </summary>
/// <param name="Time">后一次采样的时刻。</param>
/// <param name="Query">query + getmore / 秒。</param>
/// <param name="Insert">insert / 秒。</param>
/// <param name="Update">update / 秒。</param>
/// <param name="Delete">delete / 秒。</param>
/// <param name="BytesIn">入站字节 / 秒。</param>
/// <param name="BytesOut">出站字节 / 秒。</param>
/// <param name="Evictions">淘汰页 / 秒。</param>
/// <param name="Connections">当时的连接数。</param>
internal sealed record RateSample(
    DateTime Time,
    double Query,
    double Insert,
    double Update,
    double Delete,
    double BytesIn,
    double BytesOut,
    double Evictions,
    long Connections)
{
    /// <summary>读 / 秒。</summary>
    public double Reads => Query;

    /// <summary>写 / 秒。</summary>
    public double Writes => Insert + Update + Delete;

    /// <summary>合计 / 秒。</summary>
    public double Total => Reads + Writes;

    /// <summary>
    /// 两次采样相减。服务器重启过(uptime 变小)或时间没走,返回 null —— 那一段的差值是负数或无穷大,
    /// 画出来就是一根直冲天花板的假尖峰。
    /// </summary>
    /// <param name="previous">前一次。</param>
    /// <param name="current">后一次。</param>
    public static RateSample? Between(ServerSample previous, ServerSample current)
    {
        double seconds = (current.Time - previous.Time).TotalSeconds;
        if (seconds <= 0 || current.Uptime < previous.Uptime)
        {
            return null;
        }
        double Rate(long before, long after) => Math.Max(0, after - before) / seconds;
        return new(
            current.Time,
            Rate(previous.Query + previous.GetMore, current.Query + current.GetMore),
            Rate(previous.Insert, current.Insert),
            Rate(previous.Update, current.Update),
            Rate(previous.Delete, current.Delete),
            Rate(previous.BytesIn, current.BytesIn),
            Rate(previous.BytesOut, current.BytesOut),
            Rate(previous.Evicted, current.Evicted),
            current.Connections);
    }
}

/// <summary>柱状图的一根柱子(一个时间桶里各率值的平均)。</summary>
/// <param name="Start">桶的起点。</param>
/// <param name="Query">query / 秒。</param>
/// <param name="Insert">insert / 秒。</param>
/// <param name="Update">update / 秒。</param>
/// <param name="Delete">delete / 秒。</param>
/// <param name="Connections">平均连接数。</param>
/// <param name="Samples">桶里有几次采样(0 = 这段时间没采到)。</param>
internal sealed record RateBucket(DateTime Start, double Query, double Insert, double Update, double Delete, double Connections, int Samples);

/// <summary>一次分桶的结果。</summary>
/// <param name="Buckets">柱子(从早到晚)。</param>
/// <param name="BarSeconds">每根柱子覆盖的秒数。</param>
/// <param name="Covered">实际覆盖的时长(历史不足一个窗口时小于窗口)。</param>
internal sealed record BucketSet(IReadOnlyList<RateBucket> Buckets, double BarSeconds, TimeSpan Covered);

/// <summary>
/// 采样历史(只在内存里,标签关掉即丢)。
/// <para>
/// 最长留 6 小时 —— 时间窗分段的上限。2 秒一次是 10,800 条小记录,
/// 远小于一张截图;不落盘是刻意的:监控页是"现在怎么样",历史趋势归服务器自己的监控体系。
/// </para>
/// </summary>
internal sealed class SampleHistory
{
    /// <summary>保留时长。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    private readonly List<RateSample> _rates = [];

    /// <summary>全部率值(从早到晚)。</summary>
    public IReadOnlyList<RateSample> Rates => _rates;

    /// <summary>最近一次率值。</summary>
    public RateSample? Latest => _rates.Count == 0 ? null : _rates[^1];

    /// <summary>追加一次率值并裁掉超出保留时长的。</summary>
    /// <param name="rate">率值。</param>
    public void Add(RateSample rate)
    {
        _rates.Add(rate);
        DateTime horizon = rate.Time - Retention;
        int drop = 0;
        while (drop < _rates.Count && _rates[drop].Time < horizon)
        {
            drop++;
        }
        if (drop > 0)
        {
            _rates.RemoveRange(0, drop);
        }
    }

    /// <summary>清空(服务器重启、换连接)。</summary>
    public void Clear() => _rates.Clear();

    /// <summary>某个窗口里的率值。</summary>
    public IEnumerable<RateSample> Within(TimeSpan window, DateTime now) =>
        _rates.Where(r => r.Time > now - window && r.Time <= now);

    /// <summary>
    /// 按窗口分桶。窗口攒满之后每桶 = 窗口 / 柱数(15 分钟 30 根 = 每柱 30 秒),桶边界对齐到整倍数 ——
    /// 否则每次采样整排柱子都会左右挪一点,数值跟着抖。
    /// <para>
    /// 刚打开时历史远不足一个窗口:按窗口切的话,30 根柱子里 29 根是空的,一排空位没有信息量。
    /// 这时按"已有的历史"铺满(每柱至少一个采样间隔),攒够一个窗口后自然过渡到固定切法。
    /// </para>
    /// </summary>
    /// <param name="window">时间窗。</param>
    /// <param name="maxBars">最多几根柱子。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="interval">采样间隔。</param>
    public BucketSet Buckets(TimeSpan window, int maxBars, DateTime now, TimeSpan interval)
    {
        List<RateSample> points = Within(window, now).ToList();
        if (points.Count == 0 || maxBars <= 0)
        {
            return new([], window.TotalSeconds / Math.Max(1, maxBars), TimeSpan.Zero);
        }
        TimeSpan covered = now - points[0].Time + interval;
        double bar;
        int bars;
        DateTime start;
        if (covered >= window)
        {
            covered = window;
            bar = window.TotalSeconds / maxBars;
            bars = maxBars;
            // 整数刻度上取整:Ticks 是 10^17 量级,走 double 会丢掉几十个 tick,边界就不再落在整 30 秒上。
            long barTicks = (long)Math.Round(bar * TimeSpan.TicksPerSecond);
            long endTicks = (now.Ticks + barTicks - 1) / barTicks * barTicks;
            start = new DateTime(endTicks - (bars * barTicks), DateTimeKind.Utc);
        }
        else
        {
            bar = Math.Max(interval.TotalSeconds, covered.TotalSeconds / maxBars);
            bars = Math.Clamp((int)Math.Ceiling(covered.TotalSeconds / bar - 1e-9), 1, maxBars);
            start = now - TimeSpan.FromSeconds(bars * bar);
        }
        var sums = new (double Q, double I, double U, double D, double C, int N)[bars];
        foreach (RateSample r in points)
        {
            // 一个率值代表"上一次采样到这一次"那一段,所以桶取左开右闭 (start + i·bar, start + (i+1)·bar]:
            // 恰好落在边界上的那一次归前一根柱子。
            int index = (int)Math.Ceiling(((r.Time - start).TotalSeconds / bar) - 1e-9) - 1;
            index = Math.Clamp(index, 0, bars - 1);
            (double q, double i, double u, double d, double c, int n) = sums[index];
            sums[index] = (q + r.Query, i + r.Insert, u + r.Update, d + r.Delete, c + r.Connections, n + 1);
        }
        var buckets = new RateBucket[bars];
        for (int b = 0; b < bars; b++)
        {
            (double q, double i, double u, double d, double c, int n) = sums[b];
            DateTime at = start + TimeSpan.FromSeconds(b * bar);
            buckets[b] = n == 0
                ? new RateBucket(at, 0, 0, 0, 0, 0, 0)
                : new RateBucket(at, q / n, i / n, u / n, d / n, c / n, n);
        }
        return new(buckets, bar, covered);
    }

    /// <summary>
    /// 尖峰检测:最近一次比之前 5 分钟的均值高出 35% 以上,且绝对增量够大(空闲库上 2 → 3 次 / 秒不算尖峰)。
    /// 返回涨幅(0.35 = +35%);不是尖峰返回 null。
    /// </summary>
    /// <param name="select">取哪个量。</param>
    /// <param name="minimumDelta">最小绝对增量。</param>
    /// <param name="ratio">相对阈值(默认 1.35)。</param>
    public double? Spike(Func<RateSample, double> select, double minimumDelta, double ratio = 1.35)
    {
        if (_rates.Count < 6)
        {
            return null;
        }
        RateSample latest = _rates[^1];
        List<double> baseline = _rates.Take(_rates.Count - 1)
            .Where(r => r.Time > latest.Time - TimeSpan.FromMinutes(5))
            .Select(select)
            .ToList();
        if (baseline.Count < 5)
        {
            return null;
        }
        double mean = baseline.Average();
        double value = select(latest);
        if (value - mean < minimumDelta || value <= mean * ratio)
        {
            return null;
        }
        return mean <= 0 ? 1 : (value / mean) - 1;
    }
}

/// <summary>副本集的一个成员(设计稿 11 右上的成员卡)。</summary>
/// <param name="Name">地址(<c>10.20.3.21:27017</c>)。</param>
/// <param name="State">状态(<c>PRIMARY</c> / <c>SECONDARY</c> / <c>ARBITER</c> / …)。</param>
/// <param name="Healthy">心跳是否正常。</param>
/// <param name="LagSeconds">相对主节点的复制延迟;主节点、仲裁节点为 null。</param>
/// <param name="Priority">选举优先级。</param>
/// <param name="Votes">票数。</param>
/// <param name="LastHeartbeat">最近一次心跳(本机这个成员为 null)。</param>
/// <param name="IsSelf">是不是当前连着的这台。</param>
internal sealed record ReplicaMember(
    string Name,
    string State,
    bool Healthy,
    double? LagSeconds,
    double Priority,
    int Votes,
    DateTime? LastHeartbeat,
    bool IsSelf);

/// <summary>一次 <c>replSetGetStatus</c>(+ 可选的 <c>replSetGetConfig</c>)的要点。</summary>
/// <param name="SetName">副本集名。</param>
/// <param name="Members">成员。</param>
/// <param name="MajorityHealthy">有投票权且健康的成员是否构成多数派。</param>
internal sealed record ReplicaSnapshot(string SetName, IReadOnlyList<ReplicaMember> Members, bool MajorityHealthy)
{
    /// <summary>最慢的从节点(延迟最大);没有从节点为 null。</summary>
    public ReplicaMember? Slowest => Members
        .Where(static m => m.LagSeconds is not null && m.State != "PRIMARY")
        .MaxBy(static m => m.LagSeconds);

    /// <summary>主节点地址;没有主节点为 null。</summary>
    public string? Primary => Members.FirstOrDefault(static m => m.State == "PRIMARY")?.Name;

    /// <summary>
    /// 解析。延迟按"主节点最近应用时间 − 成员最近应用时间"算,优先用毫秒精度的
    /// <c>lastAppliedWallTime</c>(4.2+),没有再退回只有秒精度的 <c>optimeDate</c> ——
    /// 用秒精度判断"超过 1 秒"会在 0 与 1 之间来回跳。
    /// </summary>
    /// <param name="status">replSetGetStatus 原文。</param>
    /// <param name="config">replSetGetConfig 原文(拿优先级与票数);拿不到为 null,按默认 1 / 1。</param>
    public static ReplicaSnapshot Parse(BsonDocument status, BsonDocument? config)
    {
        var settings = new Dictionary<string, (double Priority, int Votes)>(StringComparer.OrdinalIgnoreCase);
        BsonDocument? cfg = config?.TryGetValue("config", out BsonValue c) == true && c is BsonDocument cd ? cd : config;
        if (cfg?.TryGetValue("members", out BsonValue cm) == true && cm is BsonArray cfgMembers)
        {
            foreach (BsonDocument m in cfgMembers.OfType<BsonDocument>())
            {
                string host = m.GetValue("host", "").ToString() ?? "";
                double priority = m.TryGetValue("priority", out BsonValue p) && p.IsNumeric ? p.ToDouble() : 1;
                int votes = m.TryGetValue("votes", out BsonValue v) && v.IsNumeric ? v.ToInt32() : 1;
                settings[host] = (priority, votes);
            }
        }
        var raw = new List<(string Name, string State, bool Healthy, DateTime? Applied, DateTime? Heartbeat, bool Self)>();
        if (status.TryGetValue("members", out BsonValue ms) && ms is BsonArray members)
        {
            foreach (BsonDocument m in members.OfType<BsonDocument>())
            {
                string name = m.GetValue("name", "").ToString() ?? "";
                string state = m.TryGetValue("stateStr", out BsonValue s) && s.IsString ? s.AsString : "UNKNOWN";
                bool healthy = !m.TryGetValue("health", out BsonValue h) || !h.IsNumeric || h.ToDouble() > 0;
                DateTime? applied = Date(m, "lastAppliedWallTime") ?? Date(m, "optimeDate");
                DateTime? heartbeat = Date(m, "lastHeartbeat");
                bool self = m.TryGetValue("self", out BsonValue me) && me.ToBoolean();
                raw.Add((name, state, healthy, applied, heartbeat, self));
            }
        }
        DateTime? primaryApplied = raw.FirstOrDefault(static r => r.State == "PRIMARY").Applied
                                   ?? raw.Where(static r => r.Applied is not null).Select(static r => r.Applied).Max();
        var result = new List<ReplicaMember>();
        int healthyVoters = 0;
        int voters = 0;
        foreach ((string name, string state, bool healthy, DateTime? applied, DateTime? heartbeat, bool self) in raw)
        {
            (double priority, int votes) = settings.TryGetValue(name, out var cfgMember) ? cfgMember : (1, 1);
            double? lag = state is "PRIMARY" or "ARBITER" || applied is null || primaryApplied is null
                ? null
                : Math.Max(0, (primaryApplied.Value - applied.Value).TotalSeconds);
            result.Add(new(name, state, healthy, lag, priority, votes, heartbeat, self));
            if (votes > 0)
            {
                voters++;
                if (healthy)
                {
                    healthyVoters++;
                }
            }
        }
        int majority = status.TryGetValue("majorityVoteCount", out BsonValue mv) && mv.IsNumeric
            ? mv.ToInt32()
            : (voters / 2) + 1;
        string set = status.TryGetValue("set", out BsonValue sn) && sn.IsString ? sn.AsString : "";
        return new(set, result, healthyVoters >= majority);
    }

    private static DateTime? Date(BsonDocument doc, string name) =>
        doc.TryGetValue(name, out BsonValue v) && v.IsValidDateTime ? v.ToUniversalTime() : null;
}
