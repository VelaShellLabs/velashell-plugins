namespace VelaShell.Plugin.Mongo;

public sealed partial class Loc
{
    /// <summary>服务器监控。</summary>
    private static readonly (string Key, string En, string Zh)[] MonitorTexts =
    [
        // ── 工具行 ──
        ("Mon_Live", "Live", "实时"),
        ("Mon_Range5m", "5 min", "5 分"),
        ("Mon_Range15m", "15 min", "15 分"),
        ("Mon_Range1h", "1 h", "1 时"),
        ("Mon_Range6h", "6 h", "6 时"),
        ("Mon_Every", "Every {0} s", "每 {0} s"),
        ("Mon_IntervalTip", "Sampling interval. Sampling only runs while this tab is visible.", "采样间隔。只在本标签可见时采样,切走即停。"),
        ("Mon_Pause", "Pause", "暂停"),
        ("Mon_Resume", "Resume", "继续"),
        ("Mon_Export", "Export snapshot", "导出快照"),
        ("Mon_ExportTitle", "Export monitor snapshot", "导出监控快照"),
        ("Mon_Exported", "Snapshot exported", "已导出快照"),
        ("Mon_Uptime", "uptime {0}", "uptime {0}"),
        ("Mon_Failed", "serverStatus failed: {0}", "serverStatus 失败:{0}"),
        ("Mon_FailedPrivilege", "serverStatus is not allowed for this user (needs the clusterMonitor role): {0}",
            "当前用户无权执行 serverStatus(需要 clusterMonitor 角色):{0}"),

        // ── 时长与单位 ──
        ("Mon_DurDays", "{0} d {1} h", "{0} 天 {1} 小时"),
        ("Mon_DurHours", "{0} h {1} min", "{0} 小时 {1} 分"),
        ("Mon_DurHoursOnly", "{0} hour(s)", "{0} 小时"),
        ("Mon_DurMinutes", "{0} min", "{0} 分钟"),
        ("Mon_DurSeconds", "{0} s", "{0} 秒"),
        ("Mon_UnitMinutes", "min", "分钟"),
        ("Mon_UnitHours", "hours", "小时"),
        ("Mon_UnitDays", "days", "天"),

        // ── 指标卡 ──
        ("Mon_KpiConnections", "Connections", "连接"),
        ("Mon_KpiOps", "Ops / s", "操作 / 秒"),
        ("Mon_KpiNetwork", "Network", "网络"),
        ("Mon_KpiCache", "Cache", "缓存使用"),
        ("Mon_KpiLag", "Replication lag", "复制延迟"),
        ("Mon_KpiOplog", "Oplog window", "Oplog 窗口"),
        ("Mon_High", "High", "偏高"),
        ("Mon_Warming", "waiting for the next sample", "等待下一次采样"),
        ("Mon_ConnFoot", "current · {0} available", "当前 · 可用 {0}"),
        ("Mon_OpsFoot", "read {0} · write {1}", "读 {0} · 写 {1}"),
        ("Mon_NetFoot", "in {0} · out {1}", "入 {0} · 出 {1}"),
        ("Mon_CacheFoot", "dirty {0} · evicted {1}/s", "脏页 {0} · 淘汰 {1}/s"),
        ("Mon_LagFoot", "slowest {0}", "最慢 {0}"),
        ("Mon_LagStandalone", "standalone, no replication", "单机部署,无复制"),
        ("Mon_LagMongos", "mongos — see each shard", "mongos,请看各分片"),
        ("Mon_LagNoSecondary", "no secondaries", "没有从节点"),
        ("Mon_OplogEnough", "{0} · enough", "{0} · 够用"),
        ("Mon_OplogShort", "{0} · short", "{0} · 偏短"),
        ("Mon_OplogNone", "no oplog", "没有 oplog"),
        ("Mon_OplogDenied", "no access to local.oplog.rs", "无权读取 local.oplog.rs"),

        // ── 图表 ──
        ("Mon_OpsTitle", "Ops / s", "操作 / 秒"),
        ("Mon_OpsSubtitle", "opcounters · last {0} · {1} per bar", "opcounters · 近 {0} · 每柱 {1}"),
        ("Mon_OpsSubtitleWarm", "opcounters · {0} sampled so far · {1} per bar", "opcounters · 已采 {0} · 每柱 {1}"),
        ("Mon_ConnTitle", "Connections", "连接数"),
        ("Mon_ConnSubtitle", "current {0} · peak {1}", "当前 {0} · 峰值 {1}"),
        ("Mon_ConnSeries", "connections", "连接"),
        ("Mon_Now", "now", "现在"),

        // ── 副本集 ──
        ("Mon_ReplicaPlain", "Replica set", "副本集"),
        ("Mon_ReplicaTitle", "Replica set {0}", "副本集 {0}"),
        ("Mon_ReplicaSubtitle", "{0} members · {1}", "{0} 成员 · {1}"),
        ("Mon_MajorityOk", "majority healthy", "多数派健康"),
        ("Mon_MajorityLost", "majority lost", "多数派不可用"),
        ("Mon_ReplLag", "Lag", "复制延迟"),
        ("Mon_MemberMeta", "priority {0} · votes {1} · {2}", "优先级 {0} · 投票 {1} · {2}"),
        ("Mon_Heartbeat", "heartbeat {0} s ago", "心跳 {0} s 前"),
        ("Mon_MemberSelf", "this connection", "当前连接"),
        ("Mon_StandaloneTitle", "Standalone server", "单机部署"),
        ("Mon_StandaloneHint", "No replica set members. Replication lag and the oplog window only apply to replica sets.",
            "没有副本集成员。复制延迟与 Oplog 窗口只在副本集上有意义。"),
        ("Mon_MongosTitle", "mongos router", "mongos 路由"),
        ("Mon_MongosHint", "Member status lives on each shard's replica set — connect to a shard to see it.",
            "成员状态在各分片的副本集上,连到分片即可查看。"),
        ("Mon_ReplicaDenied", "replica set status unavailable", "拿不到副本集状态"),

        // ── 存储 Top ──
        ("Mon_StorageTitle", "Storage top", "存储 Top"),
        ("Mon_StorageSubtitle", "{0} · data + indexes", "{0} · 数据 + 索引"),
        ("Mon_StorageAllDbs", "all databases", "全部库"),
        ("Mon_StorageData", "Data", "数据"),
        ("Mon_StorageIndex", "Indexes", "索引"),
        ("Mon_StorageTip", "data {0} · indexes {1}", "数据 {0} · 索引 {1}"),
        ("Mon_StorageEmpty", "No user collections yet", "还没有用户集合"),
        ("Mon_StorageColName", "Collection", "集合"),
        ("Mon_StorageColBar", "Data + indexes", "数据 + 索引"),
        ("Mon_StorageColSize", "Size", "占用"),

        // ── 事件 ──
        ("Mon_EventsTitle", "Events", "事件"),
        ("Mon_EventsSubtitle", "last hour", "近 1 小时"),
        ("Mon_EventsAll", "All", "全部"),
        ("Mon_EventsLess", "Less", "收起"),
        ("Mon_EventsEmpty", "Nothing notable in the last hour.", "近 1 小时没有值得注意的事。"),
        ("Mon_EventLag", "Replication lag above {0}", "复制延迟超过 {0}"),
        ("Mon_EventLagDetail", "{0} · for {1}", "{0} · 持续 {1}"),
        ("Mon_EventSpike", "Ops spike +{0}%", "操作量尖峰 +{0}%"),
        ("Mon_EventSpikeDetail", "{0} {1}/s", "{0} {1}/s"),
        ("Mon_EventConnSpike", "Connection spike +{0}%", "连接数尖峰 +{0}%"),
        ("Mon_EventConnSpikeDetail", "{0} now · average {1}", "当前 {0} · 均值 {1}"),
        ("Mon_EventSlow", "{0} slow queries", "慢查询 {0} 条"),
        ("Mon_EventIndexBuilt", "Index build finished", "索引构建完成"),
        ("Mon_EventIndexQuick", "{0} finished between two samples", "{0} 个在两次采样之间完成"),
        ("Mon_EventIndexAborted", "Index build ended without committing", "索引构建未提交即结束"),
        ("Mon_EventPrimary", "Primary changed", "主节点切换"),
        ("Mon_EventUnhealthy", "Member unreachable", "成员不可达"),
        ("Mon_EventRecovered", "Member reachable again", "成员恢复"),

        // ── 状态栏 ──
        ("Mon_Status", "{0} · serverStatus{1} · sampling · every {2} s", "{0} · serverStatus{1} · 采样中 · 每 {2} s"),
        ("Mon_StatusPaused", "{0} · serverStatus{1} · paused", "{0} · serverStatus{1} · 已暂停")
    ];
}
