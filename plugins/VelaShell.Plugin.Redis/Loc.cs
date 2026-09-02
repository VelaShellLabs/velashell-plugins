using System.Globalization;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// 插件自带的文案表。插件的国际化由自己负责(SDK 只给 <c>context.Host.Locale</c> 与
/// <c>LocaleChanged</c> 事件),因此这些词条随插件走 —— 它们本就是 Redis 的领域词汇,
/// 留在宿主的 <c>Strings.resx</c> 里只会让宿主替一个自己并不认识的领域背词典。
/// <para>
/// 只带简体中文与英文两套(与 S3 插件同口径):宿主支持五种语言,但插件文案量大且专业词多,
/// 缺哪种语言就回落英文,而不是显示一个键名。
/// </para>
/// </summary>
/// <param name="locale">宿主当前语言(如 <c>zh-Hans</c>、<c>en</c>)。</param>
public sealed class Loc(string locale)
{
    private readonly bool _chinese = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>取一条文案;未收录的键原样返回(方便一眼看出漏了哪条)。</summary>
    /// <param name="key">文案键。</param>
    /// <returns>文案。</returns>
    public string this[string key] =>
        (_chinese ? Chinese : English).TryGetValue(key, out string? value) ? value : key;

    /// <summary>取一条文案(索引器的具名形式,便于在表达式里连用)。</summary>
    /// <param name="key">文案键。</param>
    /// <returns>文案。</returns>
    public string Get(string key) => this[key];

    /// <summary>取一条带占位符的文案并格式化。</summary>
    /// <param name="key">文案键。</param>
    /// <param name="args">占位参数。</param>
    /// <returns>格式化后的文案。</returns>
    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>
    /// 全部文案键(两种语言的并集)。**供单测用**:字典初始化器里出现重复键会在
    /// **静态构造时**抛 <c>ArgumentException</c> —— 编译期看不出来,而它一炸就是整个插件不可用。
    /// 另外这也是"两种语言键集必须一致"那条的检查入口。
    /// </summary>
    public static IReadOnlyCollection<string> AllKeys => [.. English.Keys.Union(Chinese.Keys, StringComparer.Ordinal)];

    /// <summary>某种语言收录的键(单测比对两套是否齐平)。</summary>
    /// <param name="chinese">true = 中文表,false = 英文表。</param>
    /// <returns>键集合。</returns>
    public static IReadOnlyCollection<string> KeysOf(bool chinese) => chinese ? Chinese.Keys : English.Keys;

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        // ── 连接表单 ──
        ["Redis_Host"] = "Server address",
        ["Redis_HostHint"] = "Host or IP of the Redis endpoint. For Sentinel, point this at any sentinel.",
        ["Redis_User"] = "ACL user (empty = default)",
        ["Redis_Password"] = "Password",
        ["Redis_Mode"] = "Deployment",
        ["Redis_ModeStandalone"] = "Standalone",
        ["Redis_ModeSentinel"] = "Sentinel",
        ["Redis_ModeCluster"] = "Cluster",
        ["Redis_MasterName"] = "Master name",
        ["Redis_Database"] = "Default database",
        ["Redis_UseTls"] = "Use TLS",
        ["Redis_JumpSession"] = "Reach it through an SSH session",
        ["Redis_JumpSessionHint"] =
            "Production Redis is rarely exposed to the internet. Pick a saved SSH connection and the host opens the tunnel for you — no local port to remember.",
        ["Redis_Environment"] = "Environment",
        ["Redis_EnvProduction"] = "Production",
        ["Redis_EnvStaging"] = "Staging",
        ["Redis_EnvDevelopment"] = "Development",
        ["Redis_EnvironmentHint"] = "Drives the guard rails: production defaults to read-only and locks destructive commands.",
        ["Redis_ReadOnly"] = "Read-only mode",
        ["Redis_ReadOnlyHint"] = "Blocks every write command client-side. Real read-only enforcement belongs to an ACL user (+@read).",
        ["Redis_Delimiter"] = "Key delimiter",
        ["Redis_DelimiterHint"] = "Used to fold key names into a tree. Usually a colon.",
        ["Redis_ScanCount"] = "SCAN batch size",
        ["Redis_ScanBudget"] = "Auto-scan key budget",
        ["Redis_ScanBudgetHint"] = "Auto-scanning stops after this many keys so a huge keyspace is never swept blindly.",
        ["Redis_ValuePreview"] = "Value preview limit (bytes)",
        ["Redis_ClientName"] = "Client name",
        ["Redis_ClientNameHint"] = "Reported to CLIENT LIST so a DBA can tell who is connected.",
        ["Redis_ConnectTimeout"] = "Connect timeout (ms)",

        // ── 文档头 ──
        ["Redis_Reconnect"] = "Reconnect",
        ["Redis_Refresh"] = "Refresh",
        ["Redis_ReadOnlyBadge"] = "READ-ONLY",
        ["Redis_Connecting"] = "Connecting…",
        ["Redis_Connected"] = "Connected",
        ["Redis_Disconnected"] = "Disconnected",

        // ── 键空间浏览器 ──
        ["Redis_FilterPlaceholder"] = "Filter keys",
        ["Redis_MatchPrefix"] = "Prefix",
        ["Redis_MatchContains"] = "Contains",
        ["Redis_MatchGlob"] = "Glob",
        ["Redis_AllTypes"] = "All types",
        ["Redis_ColumnName"] = "KEY",
        ["Redis_ColumnType"] = "TYPE",
        ["Redis_ColumnTtl"] = "TTL",
        ["Redis_ColumnSize"] = "SIZE",
        ["Redis_BreadcrumbAll"] = "All",
        ["Redis_BreadcrumbTip"] = "Jump to this prefix (sets the filter and rescans)",
        ["Redis_GroupTip"] = "{0} keys sharing this prefix, folded into one row. Click to expand in place — the count is what SCAN has seen so far, not the total.",
        ["Redis_GroupThreshold"] = "Fold groups at",
        ["Redis_GroupThresholdHint"] = "Keys sharing a prefix are folded into one row once there are this many. 1 = never fold.",
        ["Redis_SizeBytes"] = "{0} B",
        ["Redis_SizeItems"] = "{0} items",
        ["Redis_TtlNone"] = "—",
        ["Redis_ValueFormatText"] = "Text",
        ["Redis_ValueFormatEscaped"] = "Escaped",
        ["Redis_ValueFormatHex"] = "Hex",
        ["Redis_BinaryValue"] = "This value is not valid UTF-8. It is shown escaped (redis-cli style) — editing here is byte-exact. Plain text would have to go through a lossy decode, so that view is disabled.",
        ["Redis_HexReadOnly"] = "Hex is a read-only dump. Switch to Escaped to edit.",
        ["Redis_BadEscape"] = "Malformed escape: {0}. Nothing was written.",
        ["Redis_BinaryMemberReadOnly"] = "This member holds binary data (shown escaped). Editing members byte-exactly is not supported yet — use the console (HSET/LSET/…) so nothing gets rewritten as literal backslashes.",
        ["Redis_ScanContinue"] = "Keep scanning",
        ["Redis_ScanStop"] = "Stop",
        ["Redis_Scanning"] = "Scanning…",
        ["Redis_ScanProgress"] = "Scanned {0} of ~{1} ({2})",
        ["Redis_ScanComplete"] = "Scanned {0} keys — cursor returned to 0, this is the whole keyspace",
        ["Redis_ScanBudgetHit"] = "Stopped at {0} keys — results may be incomplete, narrow the filter",
        ["Redis_ScanNoMatch"] = "Scanned {0} keys, nothing matched {1}",
        ["Redis_EmptyDatabase"] = "db{0} has no keys",

        // ── 键详情 ──
        ["Redis_NoSelection"] = "Select a key on the left",
        ["Redis_KeyGone"] = "This key has expired or was deleted",
        ["Redis_NoTtl"] = "no expiry",
        ["Redis_Ttl"] = "TTL",
        ["Redis_Encoding"] = "encoding",
        ["Redis_Elements"] = "{0} elements",
        ["Redis_Fields"] = "{0} fields",
        ["Redis_ColumnField"] = "FIELD",
        ["Redis_ColumnValue"] = "VALUE",
        ["Redis_ColumnIndex"] = "INDEX",
        ["Redis_ColumnMember"] = "MEMBER",
        ["Redis_ColumnScore"] = "SCORE",
        ["Redis_ColumnId"] = "ID",
        ["Redis_LoadMore"] = "Load more",
        ["Redis_PageStatus"] = "{0} of {1} loaded",
        ["Redis_Truncated"] = "Showing the first {0} of {1} — the rest is not loaded",
        ["Redis_UnsupportedType"] = "No dedicated editor for type '{0}'. Use the console.",

        // ── 编辑与护栏 ──
        ["Redis_Save"] = "Save",
        ["Redis_Cancel"] = "Cancel",
        ["Redis_Edit"] = "Edit",
        ["Redis_Add"] = "Add",
        ["Redis_Remove"] = "Remove",
        ["Redis_Rename"] = "Rename",
        ["Redis_Delete"] = "Delete",
        ["Redis_SetTtl"] = "Change TTL",
        ["Redis_Persist"] = "Remove expiry",
        // 输入框里只放"不填会怎样";**格式清单放不进 180px**,它属于右边的回显位。
        ["Redis_TtlPlaceholder"] = "Leave TTL unchanged",
        ["Redis_TtlFormatHint"] = "Empty leaves it alone; accepts 900 / 15m / 2h30m / an absolute time",
        ["Redis_TtlPreview"] = "Expires {0} (in {1})",
        ["Redis_TtlInvalid"] = "Cannot read that as a duration or a point in time",
        ["Redis_NewFieldPlaceholder"] = "field",
        ["Redis_NewValuePlaceholder"] = "value",
        ["Redis_NewMemberPlaceholder"] = "member",
        ["Redis_NewScorePlaceholder"] = "score",
        ["Redis_RenameTo"] = "New key name",
        ["Redis_RenameExistsTitle"] = "{0} already exists",
        ["Redis_RenameExistsBody"] =
            "RENAME would overwrite it silently — that is a data loss you cannot undo. Overwrite anyway?",
        // 工具条上那句是**说明**,不是问句 —— 问句属于确认框(RenameExistsBody)。
        ["Redis_RenameNote"] = "Uses RENAMENX, never a silent overwrite — it stops and asks if the target exists",
        ["Redis_Overwrite"] = "Overwrite",
        ["Redis_DeleteKeyTitle"] = "Delete {0}?",
        ["Redis_DeleteKeyBody"] = "The key and its value are gone for good. Redis has no undo.",
        ["Redis_ListRemoveNote"] =
            "Lists have no delete-by-index. This removes the first element equal to that value (LREM count 1).",
        ["Redis_BlockedByReadOnly"] = "{0} is blocked: this connection is in read-only mode. Turn it off in the header.",
        ["Redis_BlockedByProduction"] =
            "{0} is locked because this connection is marked production. Unlock it in the connection settings.",
        ["Redis_RiskWrite"] = "[write]",
        ["Redis_RiskDangerous"] = "[dangerous]",
        ["Redis_RiskDestructive"] = "[destructive]",
        ["Redis_ConfirmTitle"] = "Run {0}?",
        ["Redis_ConfirmRun"] = "Run it",
        ["Redis_ConfirmDangerousBody"] =
            "{0} changes how the server behaves, not just the data. MONITOR also cuts throughput noticeably.",
        ["Redis_ConfirmDestructiveBody"] =
            "This wipes data with no way back. Type the connection below to confirm you know which server this is.",

        // ── 控制台 ──
        ["Redis_TabConsole"] = "Console",
        ["Redis_TabOverview"] = "Overview",
        ["Redis_TabSlowlog"] = "Slow log",
        ["Redis_TabClients"] = "Clients",
        ["Redis_TabPubSub"] = "Pub/Sub",
        ["Redis_TabMemory"] = "Memory",
        ["Redis_Collapse"] = "Collapse",
        ["Redis_Expand"] = "Expand",
        ["Redis_ConsoleWelcome"] = "Connected to Redis {0}. Every command goes through the guard rails.",
        ["Redis_ConsoleFallbackMetadata"] =
            "This server did not answer COMMAND, so completion and risk levels come from the built-in table.",
        ["Redis_ConsolePrompt"] = "{0} {1}",
        ["Redis_ConsoleUnsupported"] =
            "{0} cannot run on a multiplexed connection — the client library does not carry it.",
        ["Redis_ConsoleBlockedReadOnly"] = "[blocked: read-only]",
        ["Redis_ConsoleBlockedProduction"] = "[locked: production]",
        ["Redis_ConsoleClear"] = "Clear",
        ["Redis_ConsoleRun"] = "Run",
        ["Redis_ConsoleInputPlaceholder"] = "Type a command, ↑↓ for history, Ctrl+Enter to run",
        ["Redis_GenerateCommand"] = "Send to console",
        ["Redis_Favorite"] = "Pin this key",
        ["Redis_Unfavorite"] = "Unpin",
        ["Redis_Favorites"] = "Pinned",
        ["Redis_DiscoverCommand"] = "Redis: find Redis on an SSH session",
        ["Redis_DiscoverNoSessions"] = "No connected SSH session to probe. Connect to a host first.",
        ["Redis_DiscoverNoneFound"] = "No Redis instance found on the connected sessions.",
        ["Redis_DiscoverFound"] = "Found {0} Redis instance(s) on {1}.",
        ["Redis_ConsoleSelectedDatabase"] = "The console switched to db{0}; the browser followed.",
        ["Redis_ScoreInvalid"] = "That is not a number",
        ["Redis_RenamedTo"] = "Renamed to {0}",
        ["Redis_ReadOnlyOffTitle"] = "Turn off read-only?",
        ["Redis_ReadOnlyOffBody"] = "Every edit button becomes live. Redis has no undo.",
        ["Redis_ReadOnlyOffProductionBody"] =
            "This connection is marked production. Type the connection below to confirm you know which server this is.",
        ["Redis_ReadOnlyOffConfirm"] = "Turn it off",

        // ── 概览 / 慢日志 / 客户端 / 订阅 / 内存 ──
        ["Redis_Refreshing"] = "Refreshing…",
        ["Redis_AutoRefresh"] = "Auto refresh",
        ["Redis_SectionPersistence"] = "Persistence",
        ["Redis_SectionReplication"] = "Replication",
        ["Redis_SectionKeyspace"] = "Keyspace",
        ["Redis_SectionMemory"] = "Memory",
        ["Redis_SectionStats"] = "Stats",
        ["Redis_SectionServer"] = "Server",
        ["Redis_Unavailable"] = "This server does not expose {0}",
        ["Redis_ColumnDuration"] = "DURATION",
        ["Redis_ColumnTime"] = "TIME",
        ["Redis_ColumnClient"] = "CLIENT",
        ["Redis_ColumnCommand"] = "COMMAND",
        ["Redis_ColumnAddress"] = "ADDRESS",
        ["Redis_ColumnClientName"] = "NAME",
        ["Redis_ColumnAge"] = "AGE",
        ["Redis_ColumnIdle"] = "IDLE",
        ["Redis_ColumnDb"] = "DB",
        ["Redis_ColumnPrefix"] = "PREFIX",
        ["Redis_ColumnKeys"] = "KEYS",
        ["Redis_ColumnBytes"] = "BYTES",
        ["Redis_SlowlogReset"] = "Reset slow log",
        ["Redis_ThisIsUs"] = "this client",
        ["Redis_KillClient"] = "Kill",
        ["Redis_Subscribe"] = "Subscribe",
        ["Redis_Unsubscribe"] = "Unsubscribe",
        ["Redis_ChannelPlaceholder"] = "channel or pattern",
        ["Redis_PubSubNote"] =
            "Pub/Sub uses a separate connection so browsing cannot be disturbed by an incoming flood.",
        ["Redis_ColumnChannel"] = "CHANNEL",
        ["Redis_ColumnPayload"] = "PAYLOAD",
        ["Redis_AnalyzeMemory"] = "Sample memory",
        ["Redis_MemorySampleNote"] =
            "Sampled {0} keys ({1} of the estimated total) — these numbers are an estimate, not an audit.",
        ["Redis_MemoryNeedsCommand"] = "This server does not expose MEMORY USAGE (added in Redis 4.0)",
        ["Redis_Stop"] = "Stop",

        // ── 错误与降级 ──
        ["Redis_ConnectFailed"] = "Cannot reach {0}: {1}",
        ["Redis_AuthFailed"] = "Authentication failed: {0}",
        ["Redis_CommandDenied"] = "This server does not allow {0}",
        ["Redis_Error"] = "Failed: {0}",
        ["Redis_ClusterNoDatabases"] = "Cluster mode only has db0",
        ["Redis_ModeMismatchCluster"] =
            "This server reports itself as a cluster, but the connection is set to Standalone. Switch 部署形态 to Cluster — otherwise the key tree stays empty.",
        ["Redis_ModeMismatchStandalone"] =
            "This server is not a cluster, but the connection is set to Cluster. Switch 部署形态 to Standalone.",

        // ── 文档头 / 键空间工具条 ──
        ["Redis_AutoRefreshEvery"] = "Auto refresh {0}s",
        ["Redis_AutoRefreshHint"] =
            "Refreshes the open drawer tab and the selected key. It never re-sweeps the keyspace — that is what Rescan is for. "
            + "While you have unsaved edits it leaves the key detail alone, so a background refresh cannot overwrite what you are typing.",
        ["Redis_AutoRefreshHeldNotice"] =
            "Auto refresh is holding the key detail: you have unsaved edits. Save or discard them and it resumes.",
        ["Redis_Import"] = "Import",
        ["Redis_Export"] = "Export",
        ["Redis_DbKeyCount"] = "{0} keys",
        ["Redis_DbSize"] = "DBSIZE {0}",
        ["Redis_NewKey"] = "New key",
        ["Redis_Rescan"] = "Rescan",
        ["Redis_MultiSelect"] = "Multi-select",
        ["Redis_ScanCursor"] = "cursor {0}",
        ["Redis_ScannedCountShort"] = "scanned {0}",
        ["Redis_ScanNotDone"] = "not finished (the count is what has been scanned)",
        ["Redis_ScanCursorZero"] = "cursor is back to 0 — this prefix is fully scanned",

        // ── 批量 ──
        ["Redis_BatchSelected"] = "{0} keys selected",
        ["Redis_BatchCopyNames"] = "Copy key names",
        ["Redis_BatchDelete"] = "Delete selected",
        ["Redis_BatchDeleteTitle"] = "Delete {0} keys?",
        ["Redis_BatchDeleteBody"] =
            "This is the “dangerous” tier — one confirmation, no typed phrase. UNLINK is used rather than DEL: deleting a million-element collection with DEL blocks the whole instance, while UNLINK hands the reclaim to a background thread.",
        ["Redis_BatchDeleted"] = "Deleted {0} keys",
        ["Redis_BatchCopied"] = "Copied {0} key names",
        ["Redis_BatchNothing"] = "No keys selected",

        // ── 键详情头 ──
        ["Redis_CopyKeyName"] = "Copy key name",
        ["Redis_Reload"] = "Reload",
        ["Redis_DeleteUnlink"] = "Delete (UNLINK)",
        ["Redis_MemoryUsage"] = "MEMORY USAGE {0}",
        ["Redis_IdleFor"] = "idle {0}",

        // ── 解码链 ──
        ["Redis_ViewGroup"] = "View",
        ["Redis_DecompressGroup"] = "Decompress",
        ["Redis_DeserializeGroup"] = "Deserialize",
        ["Redis_CodecNone"] = "None",
        ["Redis_ValueFormatJson"] = "JSON",
        ["Redis_ChainReversible"] = "chain is reversible · editable",
        ["Redis_ChainReadOnly"] = "chain is not reversible · read-only",
        ["Redis_ChainExplain"] =
            "Every step of the current chain ({0}) is reversible, so saving pushes the value back through the same chain. The moment one step is not, saving is disabled and this line names that step.",
        ["Redis_ChainBlocked"] = "Step “{0}” cannot be reversed: {1}. This value is read-only.",
        ["Redis_CodecNotBundled"] =
            "The {0} decoder is not bundled — it needs an extra third-party dependency. The magic bytes are recognised; the bytes are not decoded, and nothing is guessed.",
        ["Redis_CodecFailed"] = "{0} decoding failed: {1}",
        ["Redis_CodecDetected"] = "detected {0}",
        ["Redis_JsonInvalid"] = "not valid JSON: {0}",

        // ── 成员表 ──
        ["Redis_MemberFilterPlaceholder"] = "{0} MATCH * — filter members",
        ["Redis_MemberFilterFallback"] = "Lists and streams have no MATCH; this filter runs client-side",
        ["Redis_SortByLabel"] = "Sort by name",
        ["Redis_SortByScoreDesc"] = "Sort by score, descending",
        ["Redis_PrevPage"] = "Previous",
        ["Redis_NextPage"] = "Next",
        ["Redis_PageSize"] = "{0} per page",
        ["Redis_MemberRange"] = "{0}–{1} of {2}",
        ["Redis_ColumnRank"] = "Rank",
        ["Redis_ColumnDelta"] = "Δ to previous",
        ["Redis_ScoreRange"] = "Score range",
        ["Redis_ScoreMin"] = "min",
        ["Redis_ScoreMax"] = "max",
        ["Redis_ColumnShare"] = "Share",
        ["Redis_CursorZeroLoaded"] = "cursor back to 0 · all {0} entries loaded",
        ["Redis_CursorMore"] = "cursor {0} · {1} of {2} loaded",
        ["Redis_TruncationHint"] =
            "Values over {0} are truncated for display; you are told before editing, and a truncated value is never written back",
        ["Redis_ScorePrecision"] =
            "Scores are doubles: past 2^53 precision is lost — what the server returned is shown verbatim",
        ["Redis_FormatValue"] = "Format",
        ["Redis_CopyValue"] = "Copy value",
        ["Redis_SaveKeepTtl"] = "Save (SET KEEPTTL)",

        // ── 状态条 ──
        ["Redis_LastCommand"] = "Last command {0} · {1}",
        ["Redis_NoError"] = "No errors",

        // ── 抽屉:监视 / 集群 ──
        ["Redis_TabMonitor"] = "Monitor",
        ["Redis_TabCluster"] = "Cluster",
        ["Redis_MonitorTitle"] = "MONITOR cannot run on this connection",
        ["Redis_MonitorBody"] =
            "The client library multiplexes: commands are dispatched to whichever physical connection is free, while MONITOR is connection-level state — once started it owns the whole connection and takes over every reply. So the command is refused honestly before you press Enter, instead of letting you send it and then hang, time out, and blame the network.",
        ["Redis_MonitorWaySlowlog"] = "Use the slow log instead",
        ["Redis_MonitorWayKeyspace"] = "Subscribe to __keyevent@0__:*",
        ["Redis_MonitorWayTerminal"] = "Run redis-cli monitor in a VelaShell terminal",
        ["Redis_ClusterNotClustered"] = "This server is not a cluster — that is its shape, not an error.",
        ["Redis_ClusterSummary"] = "{0} · {1} nodes · {2} slots assigned",
        ["Redis_ClusterNote"] =
            "A cluster has no multiple databases, so the database picker is locked to db0 with the reason spelled out. The key list SCANs each node separately and merges the pages; MOVED/ASK redirects are handled by the client library.",
        ["Redis_ColumnNode"] = "Node ID",
        ["Redis_ColumnRole"] = "Role",
        ["Redis_ColumnSlots"] = "Slots",
        ["Redis_ColumnState"] = "State",
        ["Redis_ClusterOnlyMasters"] = "Masters only",
        ["Redis_ClusterSortBySlot"] = "Sort by slot",
        ["Redis_ClusterCopyTopology"] = "Copy topology",

        // ── 抽屉:概览 / 慢日志 / 客户端 / 订阅 / 内存 ──
        ["Redis_OverviewThroughput"] = "Command throughput · last {0}s (instantaneous_ops_per_sec, sampled once a second)",
        ["Redis_Now"] = "now",
        ["Redis_OverviewChartStats"] = "peak {0} · mean {1} · now {2}",
        ["Redis_OverviewNoSamples"] = "No samples yet — the chart fills in once a second while this drawer is open.",
        ["Redis_SlowlogThreshold"] = "threshold {0} µs",
        ["Redis_SlowlogTake"] = "latest {0} entries",
        ["Redis_SlowlogNote"] =
            "The slow log is a server-side ring buffer holding only the most recent entries — nothing is kept locally, so resetting it loses the history for everyone.",
        ["Redis_ClientsFilter"] = "Filter by name",
        ["Redis_ClientsBusyOnly"] = "Busy only",
        ["Redis_ClientsSummary"] = "{0} connections",
        ["Redis_ClientsNote"] =
            "Rows marked ● are this plugin's own connections and the disconnect button is disabled for them — a client killing itself and then reporting “connection lost” is a silly but common bug.",
        ["Redis_ColumnSubs"] = "Subs",
        ["Redis_PubSubPause"] = "Pause",
        ["Redis_PubSubResume"] = "Resume",
        ["Redis_PubSubClear"] = "Clear",
        ["Redis_PubSubAutoScroll"] = "Auto-scroll",
        ["Redis_PubSubCount"] = "{0} received · ring buffer capped at {1}, oldest dropped",
        ["Redis_MemorySampleSize"] = "sample {0} keys",
        ["Redis_MemoryGrouping"] = "group by {0} prefix segment(s)",
        ["Redis_DownloadValue"] = "Save to file",
        ["Redis_ValueSaved"] = "Saved {0} ({1}) to {2}",

        // ── 新建键 ──
        ["Redis_NewKeyTitle"] = "New key",
        ["Redis_NewKeyName"] = "Key",
        ["Redis_NewKeyType"] = "Type",
        ["Redis_NewKeyValue"] = "Value",
        ["Redis_NewKeyField"] = "Field / score",
        ["Redis_NewKeyCreate"] = "Create",
        ["Redis_NewKeyExists"] =
            "{0} already exists. Creation goes through SET NX and never overwrites — delete it first, or use Save on the detail pane.",
        ["Redis_NewKeyCreated"] = "Created {0}",
        ["Redis_NewKeyTtlHint"] = "Empty = no expiry. Accepts 900 / 15m / 2h30m / an absolute timestamp.",
        ["Redis_NewKeyNamePlaceholder"] = "Key name (binary-safe, \\xNN escapes accepted)",
        ["Redis_NewKeyFieldPlaceholder"] = "hash field name, or the score for a sorted set",

        // ── 导入 / 导出 ──
        ["Redis_ExportTitle"] = "Export the selected keys",
        ["Redis_ImportTitle"] = "Replay a command file",
        ["Redis_ExportFormat"] = "Format",
        ["Redis_ExportScope"] = "Scope",
        ["Redis_ExportPath"] = "File",
        ["Redis_ExportScopeSelected"] = "the {0} selected keys",
        ["Redis_ExportScopeScanned"] = "all {0} keys scanned so far",
        ["Redis_ExportNote"] =
            "DUMP + RESTORE keeps the serialized bytes together with the encoding and the TTL — the safest thing to replay across versions. JSONL is for humans; binary values are \\xNN-escaped. Streams only travel through DUMP: replaying XADD would hand you new entry ids, which is no longer the same data.",
        ["Redis_ExportDone"] = "Exported {0} keys · {1} · {2}",
        ["Redis_ExportSkipped"] = "{0} skipped (expired, deleted, or not expressible in this format)",
        ["Redis_ImportNote"] =
            "Replayed line by line into the current database. Every line passes the same three-tier guard, so read-only mode stops the whole run.",
        ["Redis_ImportDone"] = "Replayed {0} lines · {1} failed",
        ["Redis_ImportConfirmTitle"] = "Replay {0} into {1}?",
        ["Redis_ImportConfirmBody"] =
            "Every line in the file is sent to the server as a command. This is not a transaction: a failure part-way through does not roll back what has already been written.",
        ["Redis_FileMissing"] = "No such file: {0}",
        ["Redis_PathRequired"] = "Enter a path first",
        ["Redis_Close"] = "Close",
    };

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        // ── 连接表单 ──
        ["Redis_Host"] = "服务地址",
        ["Redis_HostHint"] = "Redis 端点的主机名或 IP。哨兵模式下填任意一个哨兵的地址。",
        ["Redis_User"] = "ACL 用户(留空为 default)",
        ["Redis_Password"] = "密码",
        ["Redis_Mode"] = "部署形态",
        ["Redis_ModeStandalone"] = "独立",
        ["Redis_ModeSentinel"] = "哨兵",
        ["Redis_ModeCluster"] = "集群",
        ["Redis_MasterName"] = "主节点名",
        ["Redis_Database"] = "默认数据库",
        ["Redis_UseTls"] = "使用 TLS",
        ["Redis_JumpSession"] = "经 SSH 隧道抵达",
        ["Redis_JumpSessionHint"] = "线上 Redis 几乎从不裸露公网。选一条已保存的 SSH 配置,隧道由宿主代建 —— 不必再记本地端口。",
        ["Redis_Environment"] = "环境标记",
        ["Redis_EnvProduction"] = "生产",
        ["Redis_EnvStaging"] = "预发",
        ["Redis_EnvDevelopment"] = "开发",
        ["Redis_EnvironmentHint"] = "决定护栏强度:生产默认只读,并锁死清库类命令。",
        ["Redis_ReadOnly"] = "只读模式",
        ["Redis_ReadOnlyHint"] = "在客户端拦住一切写命令。真正的只读要靠 ACL 用户(+@read)。",
        ["Redis_Delimiter"] = "键分隔符",
        ["Redis_DelimiterHint"] = "用于把键名折成树形层级,通常是半角冒号。",
        ["Redis_ScanCount"] = "SCAN 批量",
        ["Redis_ScanBudget"] = "自动扫描键数上限",
        ["Redis_ScanBudgetHint"] = "自动扫描到这么多键就停下,免得把一个巨大的键空间盲扫到底。",
        ["Redis_ValuePreview"] = "值预览上限(字节)",
        ["Redis_ClientName"] = "客户端名",
        ["Redis_ClientNameHint"] = "上报给 CLIENT LIST,让 DBA 认得出是谁连的。",
        ["Redis_ConnectTimeout"] = "连接超时(毫秒)",

        // ── 文档头 ──
        ["Redis_Reconnect"] = "重连",
        ["Redis_Refresh"] = "刷新",
        ["Redis_ReadOnlyBadge"] = "只读",
        ["Redis_Connecting"] = "连接中…",
        ["Redis_Connected"] = "已连接",
        ["Redis_Disconnected"] = "已断开",

        // ── 键空间浏览器 ──
        ["Redis_FilterPlaceholder"] = "过滤键名",
        ["Redis_MatchPrefix"] = "前缀",
        ["Redis_MatchContains"] = "包含",
        ["Redis_MatchGlob"] = "通配",
        ["Redis_AllTypes"] = "全部类型",
        ["Redis_ColumnName"] = "键",
        ["Redis_ColumnType"] = "类型",
        ["Redis_ColumnTtl"] = "TTL",
        ["Redis_ColumnSize"] = "规模",
        ["Redis_BreadcrumbAll"] = "全部",
        ["Redis_BreadcrumbTip"] = "跳到这一层前缀(等同于把过滤条设成它并重扫)",
        ["Redis_GroupTip"] = "{0} 个同前缀的键折成了一行。点开就地展开 —— 这个数是 SCAN **已扫描到**的,不是总数。",
        ["Redis_GroupThreshold"] = "分组折叠阈值",
        ["Redis_GroupThresholdHint"] = "同前缀的键达到这么多个才折成一行。填 1 表示从不折叠。",
        ["Redis_SizeBytes"] = "{0} 字节",
        ["Redis_SizeItems"] = "{0} 项",
        ["Redis_TtlNone"] = "—",
        ["Redis_ValueFormatText"] = "文本",
        ["Redis_ValueFormatEscaped"] = "转义",
        ["Redis_ValueFormatHex"] = "十六进制",
        ["Redis_BinaryValue"] = "这个值不是合法 UTF-8,已按 redis-cli 的转义显示 —— 在这里编辑是逐字节精确的。原样文本要经过一次有损解码,故该形态不可用。",
        ["Redis_HexReadOnly"] = "十六进制是只读转储。要编辑请切到「转义」。",
        ["Redis_BadEscape"] = "转义写坏了:{0}。**没有写入任何内容**。",
        ["Redis_BinaryMemberReadOnly"] = "这个成员是二进制(界面按转义显示)。成员表暂不支持逐字节编辑 —— 请用控制台(HSET/LSET/…)改,免得把它写成一串反斜杠字面量。",
        ["Redis_ScanContinue"] = "继续扫描",
        ["Redis_ScanStop"] = "停止",
        ["Redis_Scanning"] = "扫描中…",
        ["Redis_ScanProgress"] = "已扫描 {0} / 约 {1}({2})",
        ["Redis_ScanComplete"] = "已扫描 {0} 个键 —— 游标已归零,这就是全部",
        ["Redis_ScanBudgetHit"] = "已停在 {0} 个键 —— 结果可能不完整,建议收窄过滤条件",
        ["Redis_ScanNoMatch"] = "已扫描 {0} 个键,没有匹配 {1} 的",
        ["Redis_EmptyDatabase"] = "db{0} 没有键",

        // ── 键详情 ──
        ["Redis_NoSelection"] = "在左侧选一个键",
        ["Redis_KeyGone"] = "该键已过期或被删除",
        ["Redis_NoTtl"] = "永不过期",
        ["Redis_Ttl"] = "TTL",
        ["Redis_Encoding"] = "编码",
        ["Redis_Elements"] = "{0} 个元素",
        ["Redis_Fields"] = "{0} 个字段",
        ["Redis_ColumnField"] = "字段",
        ["Redis_ColumnValue"] = "值",
        ["Redis_ColumnIndex"] = "索引",
        ["Redis_ColumnMember"] = "成员",
        ["Redis_ColumnScore"] = "分值",
        ["Redis_ColumnId"] = "ID",
        ["Redis_LoadMore"] = "加载更多",
        ["Redis_PageStatus"] = "已加载 {0} / {1}",
        ["Redis_Truncated"] = "仅显示前 {0}(共 {1})—— 其余未加载",
        ["Redis_UnsupportedType"] = "类型「{0}」没有专用编辑器,请用控制台操作。",

        // ── 编辑与护栏 ──
        ["Redis_Save"] = "保存",
        ["Redis_Cancel"] = "取消",
        ["Redis_Edit"] = "编辑",
        ["Redis_Add"] = "添加",
        ["Redis_Remove"] = "移除",
        ["Redis_Rename"] = "重命名",
        ["Redis_Delete"] = "删除",
        ["Redis_SetTtl"] = "改 TTL",
        ["Redis_Persist"] = "去掉过期时间",
        // 输入框里只放"不填会怎样";**格式清单放不进 180px**,它属于右边的回显位。
        ["Redis_TtlPlaceholder"] = "不修改 TTL",
        ["Redis_TtlFormatHint"] = "留空即不改;支持 900 / 15m / 2h30m / 绝对时间点",
        ["Redis_TtlPreview"] = "将于 {0} 过期(还剩 {1})",
        ["Redis_TtlInvalid"] = "读不出这是一段时长还是一个时间点",
        ["Redis_NewFieldPlaceholder"] = "字段",
        ["Redis_NewValuePlaceholder"] = "值",
        ["Redis_NewMemberPlaceholder"] = "成员",
        ["Redis_NewScorePlaceholder"] = "分值",
        ["Redis_RenameTo"] = "新键名",
        ["Redis_RenameExistsTitle"] = "{0} 已存在",
        ["Redis_RenameExistsBody"] = "RENAME 会静默覆盖它 —— 那是一次没法撤销的数据丢失。仍要覆盖吗?",
        // 工具条上那句是**说明**,不是问句 —— 问句属于确认框(RenameExistsBody)。
        ["Redis_RenameNote"] = "走 RENAMENX,绝不静默覆盖 —— 目标已存在时会停下来问你",
        ["Redis_Overwrite"] = "覆盖",
        ["Redis_DeleteKeyTitle"] = "删除 {0}?",
        ["Redis_DeleteKeyBody"] = "键和它的值就此没了。Redis 没有撤销。",
        ["Redis_ListRemoveNote"] = "列表没有「按索引删除」。这里删的是第一个等于该值的元素(LREM count 1)。",
        ["Redis_BlockedByReadOnly"] = "{0} 被拦住了:这条连接处于只读模式。要改请在顶栏关掉它。",
        ["Redis_BlockedByProduction"] = "{0} 已锁死,因为这条连接标了生产。要解锁请去连接设置里改。",
        ["Redis_RiskWrite"] = "[写]",
        ["Redis_RiskDangerous"] = "[危]",
        ["Redis_RiskDestructive"] = "[毁]",
        ["Redis_ConfirmTitle"] = "执行 {0}?",
        ["Redis_ConfirmRun"] = "执行",
        ["Redis_ConfirmDangerousBody"] = "{0} 改的是服务器的行为而不只是数据。MONITOR 另外会显著降低实例吞吐。",
        ["Redis_ConfirmDestructiveBody"] = "这会抹掉数据且无法回退。请键入下面这一行,确认你知道自己在动哪台服务器。",

        // ── 控制台 ──
        ["Redis_TabConsole"] = "控制台",
        ["Redis_TabOverview"] = "概览",
        ["Redis_TabSlowlog"] = "慢日志",
        ["Redis_TabClients"] = "客户端",
        ["Redis_TabPubSub"] = "订阅",
        ["Redis_TabMemory"] = "内存分析",
        ["Redis_Collapse"] = "收起",
        ["Redis_Expand"] = "展开",
        ["Redis_ConsoleWelcome"] = "已连上 Redis {0}。每一条命令都会过护栏。",
        ["Redis_ConsoleFallbackMetadata"] = "该服务器没有应答 COMMAND,所以补全与档位分级来自内置兜底表。",
        ["Redis_ConsolePrompt"] = "{0} {1}",
        ["Redis_ConsoleUnsupported"] = "{0} 在多路复用连接上跑不了 —— 客户端库不承载它。",
        ["Redis_ConsoleBlockedReadOnly"] = "[被只读模式拦住]",
        ["Redis_ConsoleBlockedProduction"] = "[生产已锁死]",
        ["Redis_ConsoleClear"] = "清屏",
        ["Redis_ConsoleRun"] = "执行",
        ["Redis_ConsoleInputPlaceholder"] = "敲命令,↑↓ 调历史,Ctrl+Enter 执行",
        ["Redis_GenerateCommand"] = "填进控制台",
        ["Redis_Favorite"] = "收藏这个键",
        ["Redis_Unfavorite"] = "取消收藏",
        ["Redis_Favorites"] = "收藏",
        ["Redis_DiscoverCommand"] = "Redis: 从 SSH 会话探测 Redis",
        ["Redis_DiscoverNoSessions"] = "没有已连接的 SSH 会话可探。先连上一台主机。",
        ["Redis_DiscoverNoneFound"] = "在已连接的会话上没有找到 Redis 实例。",
        ["Redis_DiscoverFound"] = "在 {1} 上找到 {0} 个 Redis 实例。",
        ["Redis_ConsoleSelectedDatabase"] = "控制台已切到 db{0},浏览器已跟随。",
        ["Redis_ScoreInvalid"] = "这不是一个数字",
        ["Redis_RenamedTo"] = "已重命名为 {0}",
        ["Redis_ReadOnlyOffTitle"] = "关掉只读模式?",
        ["Redis_ReadOnlyOffBody"] = "关掉之后每个编辑按钮都会真的动数据。Redis 没有撤销。",
        ["Redis_ReadOnlyOffProductionBody"] = "这条连接标了生产。请键入下面这一行,确认你知道自己在动哪台服务器。",
        ["Redis_ReadOnlyOffConfirm"] = "关掉只读",

        // ── 概览 / 慢日志 / 客户端 / 订阅 / 内存 ──
        ["Redis_Refreshing"] = "刷新中…",
        ["Redis_AutoRefresh"] = "自动刷新",
        ["Redis_SectionPersistence"] = "持久化",
        ["Redis_SectionReplication"] = "复制",
        ["Redis_SectionKeyspace"] = "键空间",
        ["Redis_SectionMemory"] = "内存",
        ["Redis_SectionStats"] = "统计",
        ["Redis_SectionServer"] = "服务器",
        ["Redis_Unavailable"] = "该服务器未开放 {0}",
        ["Redis_ColumnDuration"] = "耗时",
        ["Redis_ColumnTime"] = "时间",
        ["Redis_ColumnClient"] = "客户端",
        ["Redis_ColumnCommand"] = "命令",
        ["Redis_ColumnAddress"] = "地址",
        ["Redis_ColumnClientName"] = "名称",
        ["Redis_ColumnAge"] = "连接时长",
        ["Redis_ColumnIdle"] = "空闲",
        ["Redis_ColumnDb"] = "库",
        ["Redis_ColumnPrefix"] = "前缀",
        ["Redis_ColumnKeys"] = "键数",
        ["Redis_ColumnBytes"] = "字节",
        ["Redis_SlowlogReset"] = "清空慢日志",
        ["Redis_ThisIsUs"] = "本客户端",
        ["Redis_KillClient"] = "断开",
        ["Redis_Subscribe"] = "订阅",
        ["Redis_Unsubscribe"] = "退订",
        ["Redis_ChannelPlaceholder"] = "频道或模式",
        ["Redis_PubSubNote"] = "订阅走一条独立连接,涌进来的消息不会干扰浏览。",
        ["Redis_ColumnChannel"] = "频道",
        ["Redis_ColumnPayload"] = "载荷",
        ["Redis_AnalyzeMemory"] = "抽样分析",
        ["Redis_MemorySampleNote"] = "已抽样 {0} 个键(约占估计总数的 {1})—— 这些数字是**抽样估计**,不是全量审计。",
        ["Redis_MemoryNeedsCommand"] = "该服务器未开放 MEMORY USAGE(Redis 4.0 起才有)",
        ["Redis_Stop"] = "停止",

        // ── 错误与降级 ──
        ["Redis_ConnectFailed"] = "连不上 {0}:{1}",
        ["Redis_AuthFailed"] = "认证失败:{0}",
        ["Redis_CommandDenied"] = "该服务器未开放 {0}",
        ["Redis_Error"] = "失败:{0}",
        ["Redis_ClusterNoDatabases"] = "集群模式只有 db0",
        ["Redis_ModeMismatchCluster"] = "这台服务器自报是集群,而连接配置选的是「独立」。请把部署形态改成「集群」—— 否则键树会一直是空的。",
        ["Redis_ModeMismatchStandalone"] = "这台服务器不是集群,而连接配置选的是「集群」。请把部署形态改成「独立」。",

        // ── 文档头 / 键空间工具条 ──
        ["Redis_AutoRefreshEvery"] = "自动刷新 {0}s",
        ["Redis_AutoRefreshHint"] = "刷的是当前抽屉页与选中的键。**不重扫整个键空间** —— 那是「重扫」按钮的事。"
                                    + "有未保存的编辑时不动键详情,后台刷新不会盖掉你正在打的字。",
        ["Redis_AutoRefreshHeldNotice"] = "有未保存的编辑,自动刷新暂时不动键详情。保存或撤销后自动恢复。",
        ["Redis_Import"] = "导入",
        ["Redis_Export"] = "导出",
        ["Redis_DbKeyCount"] = "{0} 个键",
        ["Redis_DbSize"] = "DBSIZE {0}",
        ["Redis_NewKey"] = "新建键",
        ["Redis_Rescan"] = "重扫",
        ["Redis_MultiSelect"] = "多选",
        ["Redis_ScanCursor"] = "游标 {0}",
        ["Redis_ScannedCountShort"] = "已扫 {0}",
        ["Redis_ScanNotDone"] = "尚未扫完(计数是已扫到的)",
        ["Redis_ScanCursorZero"] = "游标已归零 · 本前缀已扫完",

        // ── 批量 ──
        ["Redis_BatchSelected"] = "已选 {0} 个键",
        ["Redis_BatchCopyNames"] = "复制键名",
        ["Redis_BatchDelete"] = "批量删除",
        ["Redis_BatchDeleteTitle"] = "删除 {0} 个键?",
        ["Redis_BatchDeleteBody"] =
            "这一档是「危」—— 逐次确认即可,不要求手打确认串。用 UNLINK 而不是 DEL:删一个百万元素的集合,DEL 会阻塞整个实例,UNLINK 把回收挪到后台线程。",
        ["Redis_BatchDeleted"] = "已删除 {0} 个键",
        ["Redis_BatchCopied"] = "已复制 {0} 个键名",
        ["Redis_BatchNothing"] = "没有选中任何键",

        // ── 键详情头 ──
        ["Redis_CopyKeyName"] = "复制键名",
        ["Redis_Reload"] = "重载",
        ["Redis_DeleteUnlink"] = "删除 UNLINK",
        ["Redis_MemoryUsage"] = "MEMORY USAGE {0}",
        ["Redis_IdleFor"] = "空闲 {0}",

        // ── 解码链 ──
        ["Redis_ViewGroup"] = "视图",
        ["Redis_DecompressGroup"] = "解压",
        ["Redis_DeserializeGroup"] = "反序列化",
        ["Redis_CodecNone"] = "无",
        ["Redis_ValueFormatJson"] = "JSON",
        ["Redis_ChainReversible"] = "链可逆 · 可写回",
        ["Redis_ChainReadOnly"] = "链不可逆 · 只读",
        ["Redis_ChainExplain"] =
            "当前解码链({0})每一步都可逆,所以保存时按同一条链原样压回;链里一旦出现不可逆的一步,保存会被禁用,并由这行字写明是哪一步。",
        ["Redis_ChainBlocked"] = "「{0}」这一步不可逆:{1}。这个值现在是只读的。",
        ["Redis_CodecNotBundled"] =
            "本插件未内置 {0} 解码器 —— 它需要一个额外的第三方依赖。魔数认得出来,但不假装解得开,也不猜。",
        ["Redis_CodecFailed"] = "{0} 解码失败:{1}",
        ["Redis_CodecDetected"] = "识别到 {0}",
        ["Redis_JsonInvalid"] = "不是合法 JSON:{0}",

        // ── 成员表 ──
        ["Redis_MemberFilterPlaceholder"] = "{0} MATCH * 在成员中过滤",
        ["Redis_MemberFilterFallback"] = "列表与流没有 MATCH,这一栏退回客户端逐行筛",
        ["Redis_SortByLabel"] = "按名称排序",
        ["Redis_SortByScoreDesc"] = "按分数倒序",
        ["Redis_PrevPage"] = "上一页",
        ["Redis_NextPage"] = "下一页",
        ["Redis_PageSize"] = "每页 {0}",
        ["Redis_MemberRange"] = "第 {0}–{1} 项 · 共 {2}",
        ["Redis_ColumnRank"] = "排名",
        ["Redis_ColumnDelta"] = "与上一名之差",
        ["Redis_ScoreRange"] = "分数区间",
        ["Redis_ScoreMin"] = "下界",
        ["Redis_ScoreMax"] = "上界",
        ["Redis_ColumnShare"] = "占比",
        ["Redis_CursorZeroLoaded"] = "游标已归零 · {0} 项全部载入",
        ["Redis_CursorMore"] = "游标 {0} · 已载入 {1} / {2}",
        ["Redis_TruncationHint"] = "单值超过 {0} 自动截断显示;编辑前会提示,绝不把截断结果写回",
        ["Redis_ScorePrecision"] = "分数是双精度浮点:超过 2^53 会丢精度,这里如实显示服务端返回的值",
        ["Redis_FormatValue"] = "格式化",
        ["Redis_CopyValue"] = "复制值",
        ["Redis_SaveKeepTtl"] = "保存 SET KEEPTTL",

        // ── 状态条 ──
        ["Redis_LastCommand"] = "上一条命令 {0} · {1}",
        ["Redis_NoError"] = "错误:无",

        // ── 抽屉:监视 / 集群 ──
        ["Redis_TabMonitor"] = "监视 MONITOR",
        ["Redis_TabCluster"] = "集群",
        ["Redis_MonitorTitle"] = "MONITOR 在这条连接上跑不了",
        ["Redis_MonitorBody"] =
            "客户端库用的是多路复用连接:命令会被派到任意一条物理连接上,而 MONITOR 是连接级状态 —— 一旦开启就独占整条连接并接管所有回复。所以这条命令在你按下回车之前就被如实拒绝,而不是让你敲下去然后卡住、超时、以为是网络问题。",
        ["Redis_MonitorWaySlowlog"] = "改看慢日志 SLOWLOG",
        ["Redis_MonitorWayKeyspace"] = "订阅 __keyevent@0__:*",
        ["Redis_MonitorWayTerminal"] = "在 VelaShell 终端跑 redis-cli monitor",
        ["Redis_ClusterNotClustered"] = "这台服务器不是集群 —— 这是它的形态,不是错误。",
        ["Redis_ClusterSummary"] = "{0} · {1} 个节点 · 已分配 {2} 槽",
        ["Redis_ClusterNote"] =
            "集群没有多库概念,数据库下拉会被锁在 db0 并写明原因;键列表按节点分别 SCAN 再合并,MOVED/ASK 重定向由客户端库处理。",
        ["Redis_ColumnNode"] = "节点 ID",
        ["Redis_ColumnRole"] = "角色",
        ["Redis_ColumnSlots"] = "槽位",
        ["Redis_ColumnState"] = "状态",
        ["Redis_ClusterOnlyMasters"] = "只看主节点",
        ["Redis_ClusterSortBySlot"] = "按槽位排序",
        ["Redis_ClusterCopyTopology"] = "复制拓扑",

        // ── 抽屉:概览 / 慢日志 / 客户端 / 订阅 / 内存 ──
        ["Redis_OverviewThroughput"] = "命令吞吐 · 近 {0} 秒(instantaneous_ops_per_sec,每秒采样)",
        ["Redis_Now"] = "现在",
        ["Redis_OverviewChartStats"] = "峰值 {0} · 均值 {1} · 现在 {2}",
        ["Redis_OverviewNoSamples"] = "还没有采到样本 —— 抽屉开着时,图表每秒填一格。",
        ["Redis_SlowlogThreshold"] = "阈值 {0} µs",
        ["Redis_SlowlogTake"] = "取最近 {0} 条",
        ["Redis_SlowlogNote"] =
            "慢日志是服务端的环形缓冲,只保留最近若干条;本地不留存,所以清空是对所有人清空。",
        ["Redis_ClientsFilter"] = "按名称过滤",
        ["Redis_ClientsBusyOnly"] = "只看非空闲",
        ["Redis_ClientsSummary"] = "{0} 个连接",
        ["Redis_ClientsNote"] =
            "标 ● 的是本插件自己的连接,断开按钮对它们禁用 —— 一个客户端把自己 kill 掉再报「连接丢失」,是很蠢但很常见的 bug。",
        ["Redis_ColumnSubs"] = "订阅",
        ["Redis_PubSubPause"] = "暂停",
        ["Redis_PubSubResume"] = "继续",
        ["Redis_PubSubClear"] = "清空",
        ["Redis_PubSubAutoScroll"] = "自动滚动",
        ["Redis_PubSubCount"] = "已收 {0} 条 · 环形缓冲上限 {1},超出丢最旧",
        ["Redis_MemorySampleSize"] = "抽样 {0} 键",
        ["Redis_MemoryGrouping"] = "按前缀聚合({0} 段)",
        ["Redis_DownloadValue"] = "下载为文件",
        ["Redis_ValueSaved"] = "已把 {0}({1})写到 {2}",

        // ── 新建键 ──
        ["Redis_NewKeyTitle"] = "新建键",
        ["Redis_NewKeyName"] = "键名",
        ["Redis_NewKeyType"] = "类型",
        ["Redis_NewKeyValue"] = "初始值",
        ["Redis_NewKeyField"] = "字段 / 分值",
        ["Redis_NewKeyCreate"] = "创建",
        ["Redis_NewKeyExists"] =
            "{0} 已存在。创建走 SET NX,不会覆盖 —— 要覆盖得先删掉它,或改用键详情里的「保存」。",
        ["Redis_NewKeyCreated"] = "已创建 {0}",
        ["Redis_NewKeyTtlHint"] = "留空 = 不过期;支持 900 / 15m / 2h30m / 绝对时间点。",
        ["Redis_NewKeyNamePlaceholder"] = "键名(二进制安全,可用 \\xNN 转义)",
        ["Redis_NewKeyFieldPlaceholder"] = "哈希的字段名,或有序集合的分值",

        // ── 导入 / 导出 ──
        ["Redis_ExportTitle"] = "导出选中的键",
        ["Redis_ImportTitle"] = "重放命令文件",
        ["Redis_ExportFormat"] = "格式",
        ["Redis_ExportScope"] = "范围",
        ["Redis_ExportPath"] = "路径",
        ["Redis_ExportScopeSelected"] = "选中的 {0} 个键",
        ["Redis_ExportScopeScanned"] = "已扫到的全部 {0} 个键",
        ["Redis_ExportNote"] =
            "DUMP + RESTORE 保的是序列化后的原始字节,连编码和 TTL 一起带走,跨版本回灌最稳;JSONL 只适合人看,二进制值按 \\xNN 转义。流只有 DUMP 这一条路 —— 用 XADD 重放会拿到新的条目 id,那已经不是同一份数据。",
        ["Redis_ExportDone"] = "已导出 {0} 个键 · {1} · {2}",
        ["Redis_ExportSkipped"] = "跳过 {0} 个(过期、被删,或这个格式表达不了)",
        ["Redis_ImportNote"] = "逐行重放进当前库,每一行都过一次同样的三档护栏 —— 只读模式下整条被拦。",
        ["Redis_ImportDone"] = "已重放 {0} 行 · 失败 {1} 行",
        ["Redis_ImportConfirmTitle"] = "把 {0} 重放进 {1}?",
        ["Redis_ImportConfirmBody"] =
            "文件里的每一行都会当成一条命令发给服务器。这不是一次事务:中途失败不会回滚已经写进去的那些。",
        ["Redis_FileMissing"] = "找不到文件:{0}",
        ["Redis_PathRequired"] = "先填一个路径",
        ["Redis_Close"] = "关闭",
    };
}
