using System.Globalization;
using StackExchange.Redis;

namespace VelaShell.Plugin.Redis;

/// <summary><c>CLUSTER NODES</c> 的一行。</summary>
/// <param name="Id">节点 id(40 位十六进制)。</param>
/// <param name="Address">对客户端公布的地址。</param>
/// <param name="IsMaster">是主节点。</param>
/// <param name="MasterId">从节点跟随的主 id;主节点为空串。</param>
/// <param name="Slots">负责的槽位区间(主节点);从节点为空串。</param>
/// <param name="LinkState">链路状态(<c>connected</c> / <c>disconnected</c>)。</param>
/// <param name="Flags">原始 flags(<c>myself</c>、<c>fail?</c> 等)。</param>
/// <param name="IsSelf">当前连接落在这个节点上。</param>
public sealed record RedisClusterNode(
    string Id,
    string Address,
    bool IsMaster,
    string MasterId,
    string Slots,
    string LinkState,
    string Flags,
    bool IsSelf)
{
    /// <summary>节点 id 的短形式(表格里全长 40 位纯属噪音)。</summary>
    public string ShortId => Id.Length > 8 ? Id[..8] + "…" : Id;

    /// <summary>角色文案(格式名不翻译:<c>master</c> / <c>replica</c> 就是服务器自己的用词)。</summary>
    public string RoleText => IsMaster ? "master" : "replica";

    /// <summary>链路正常。</summary>
    public bool IsHealthy => string.Equals(LinkState, "connected", StringComparison.OrdinalIgnoreCase)
                             && !Flags.Contains("fail", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一次 <c>CLUSTER</c> 采集的结果。</summary>
/// <param name="Nodes">节点表(主在前,各自的从紧随其后)。</param>
/// <param name="State"><c>cluster_state</c>(<c>ok</c> / <c>fail</c>);拿不到为空串。</param>
/// <param name="SlotsAssigned">已分配的槽位数;未知为 -1。</param>
/// <param name="KnownNodes">已知节点数;未知为 -1。</param>
/// <param name="Available">这台服务器是不是集群(非集群时整页给"不适用"的空状态)。</param>
public sealed record RedisClusterView(
    IReadOnlyList<RedisClusterNode> Nodes,
    string State,
    int SlotsAssigned,
    int KnownNodes,
    bool Available);

/// <summary>
/// 集群拓扑。
/// <para>
/// 与其余运维页同一条纪律:<b>「不是集群」是空状态,不是错误</b>。单机实例上
/// <c>CLUSTER NODES</c> 会回一句 "This instance has cluster support disabled",
/// 那不该在界面上变成一条红字 —— 它只是这台服务器的形态。
/// </para>
/// </summary>
internal sealed partial class RedisConnection
{
    /// <summary>读集群拓扑。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>拓扑;非集群时 <see cref="RedisClusterView.Available" /> 为 false。</returns>
    public async Task<RedisClusterView> ReadClusterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // 连接时的 INFO server 已经报过 redis_mode,而上一次拒绝也记在 _clusterAvailable 里。
        // 两者任一说"不是集群",就别再把 CLUSTER NODES 发出去等一句必然的报错。
        if (_clusterAvailable is false || !IsClusterMode())
        {
            _clusterAvailable = false;
            return new([], string.Empty, -1, -1, Available: false);
        }

        IDatabase db = Db();
        RedisResult raw;
        try
        {
            raw = await db.ExecuteAsync("CLUSTER", "NODES").ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeniedOrUnsupported(ex) || IsClusterDisabled(ex))
        {
            // INFO server 说是集群、CLUSTER 却被拒:托管实例改写 redis_mode 或禁掉 CLUSTER 都会这样。
            // 能力探测是最终答案,记下来别再问第二次。
            _clusterAvailable = false;
            return new([], string.Empty, -1, -1, Available: false);
        }

        _clusterAvailable = true;
        string text = AsString(raw);
        if (text.Length == 0)
        {
            return new([], string.Empty, -1, -1, Available: false);
        }

        var nodes = new List<RedisClusterNode>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ParseNode(line) is { } node)
            {
                nodes.Add(node);
            }
        }
        // 主在前、从紧随其主:表格按拓扑读,而不是按服务器返回的任意顺序读。
        var ordered = new List<RedisClusterNode>(nodes.Count);
        foreach (RedisClusterNode master in nodes.Where(node => node.IsMaster)
                     .OrderBy(node => SlotStart(node.Slots)))
        {
            ordered.Add(master);
            ordered.AddRange(nodes.Where(node => !node.IsMaster
                                                 && string.Equals(node.MasterId, master.Id, StringComparison.Ordinal)));
        }
        // 认不出主的从节点(拓扑正在变)也要出现:漏掉一行比排得不好看糟糕得多。
        ordered.AddRange(nodes.Where(node => !ordered.Contains(node)));

        (string state, int assigned, int known) = await ReadClusterInfoAsync().ConfigureAwait(false);
        return new(ordered, state, assigned, known, Available: true);
    }

    /// <summary>
    /// 这台服务器自称是集群。依据是连接时 <c>INFO server</c> 里的 <c>redis_mode</c>,
    /// 拿不到时(字段缺失 / <c>INFO</c> 被禁)按连接设置里选的形态算 —— <b>说不准就去问</b>,
    /// 让 <see cref="ReadClusterAsync" /> 的能力探测给出最终答案,而不是在这里替服务器下结论。
    /// </summary>
    private bool IsClusterMode() =>
        string.Equals(Info.Mode, "cluster", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrEmpty(Info.Mode) && _settings.Deployment == RedisDeployment.Cluster);

    /// <summary>
    /// 单机实例上 <c>CLUSTER NODES</c> 的拒绝措辞与"命令被禁"不同,单独认一下 ——
    /// 否则这一页会把"这不是集群"报成"该服务器未开放 CLUSTER",两件事的处置完全不一样。
    /// </summary>
    private static bool IsClusterDisabled(Exception ex) =>
        ex.Message.Contains("cluster support disabled", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("This instance has cluster support disabled", StringComparison.OrdinalIgnoreCase);

    private async Task<(string State, int Assigned, int Known)> ReadClusterInfoAsync()
    {
        try
        {
            RedisResult raw = await Db().ExecuteAsync("CLUSTER", "INFO").ConfigureAwait(false);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string name, string value) in ParseInfo(AsString(raw)))
            {
                fields[name] = value;
            }
            return (
                fields.GetValueOrDefault("cluster_state", string.Empty),
                ParseInt(fields.GetValueOrDefault("cluster_slots_assigned")),
                ParseInt(fields.GetValueOrDefault("cluster_known_nodes")));
        }
        catch (Exception ex) when (IsDeniedOrUnsupported(ex) || IsClusterDisabled(ex))
        {
            return (string.Empty, -1, -1);
        }

        static int ParseInt(string? text) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;
    }

    /// <summary>
    /// 解析 <c>CLUSTER NODES</c> 的一行。
    /// <para>格式:<c>&lt;id&gt; &lt;ip:port@cport[,hostname]&gt; &lt;flags&gt; &lt;master&gt; &lt;ping-sent&gt;
    /// &lt;pong-recv&gt; &lt;config-epoch&gt; &lt;link-state&gt; &lt;slot&gt;…</c></para>
    /// </summary>
    private static RedisClusterNode? ParseNode(string line)
    {
        string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8)
        {
            return null;
        }
        string flags = parts[2];
        bool isMaster = flags.Contains("master", StringComparison.Ordinal);
        // 地址里的 @cport(总线端口)与逗号后的 hostname 对使用者没有意义,切掉。
        string address = parts[1].Split('@', 2)[0].Split(',', 2)[0];
        string slots = parts.Length > 8
            ? string.Join(" ", parts.Skip(8).Where(part => !part.StartsWith('[')))
            : string.Empty;
        return new(
            parts[0],
            address,
            isMaster,
            isMaster ? string.Empty : parts[3] is "-" ? string.Empty : parts[3],
            slots.Replace("-", " – ", StringComparison.Ordinal),
            parts[7],
            flags,
            flags.Contains("myself", StringComparison.Ordinal));
    }

    private static int SlotStart(string slots)
    {
        if (slots.Length == 0)
        {
            return int.MaxValue;
        }
        string head = slots.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : int.MaxValue;
    }

    /// <summary>
    /// 取一次瞬时吞吐(<c>INFO stats</c> 的 <c>instantaneous_ops_per_sec</c>)。
    /// <para>概览页那条柱状图逐秒采它。<b>拿不到就返回 -1</b> —— 补一个 0 会被读成
    /// "此刻真的没有请求",而那是一句谎话。</para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>每秒命令数;不可得为 -1。</returns>
    public async Task<int> ReadInstantaneousOpsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Dictionary<string, string> fields = await ReadInfoFieldsAsync("stats").ConfigureAwait(false);
            return int.TryParse(fields.GetValueOrDefault("instantaneous_ops_per_sec"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : -1;
        }
        catch (Exception ex) when (IsDeniedOrUnsupported(ex))
        {
            return -1;
        }
    }
}
