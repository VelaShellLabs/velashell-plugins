namespace VelaShell.Plugin.Mongo;

public sealed partial class Loc
{
    /// <summary>对象列表(Navicat 对象页)。</summary>
    private static readonly (string Key, string En, string Zh)[] ObjectsTexts =
    [
        // ── 对象列表:工具行 / 芯片 / 列头 ──
        ("Obj_OpenCollection", "Open collection", "打开集合"),
        ("Obj_OpenView", "Open view", "打开视图"),
        ("Obj_OpenBucket", "Open bucket", "打开存储桶"),
        ("Obj_Empty", "Empty", "清空"),
        ("Obj_CopyStructure", "Copy structure", "复制结构"),
        ("Obj_Search", "Search objects", "搜索对象"),
        ("Obj_ModeGrid", "Large icons", "大图标"),
        ("Obj_ModeList", "List", "列表"),
        ("Obj_ModeDetails", "Details", "详情"),
        ("Obj_ColName", "Name", "名称"),
        ("Obj_ColType", "Type", "类型"),
        ("Obj_ColCount", "Documents", "文档数"),
        ("Obj_ColAvgSize", "Avg size", "平均大小"),
        ("Obj_ColDataSize", "Data size", "数据大小"),
        ("Obj_ColStorageSize", "Storage", "存储大小"),
        ("Obj_ColIndexes", "Indexes", "索引"),
        ("Obj_ColIndexSize", "Index size", "索引大小"),
        ("Obj_ColValidation", "Validation", "验证"),

        // ── 种类 ──
        ("Obj_KindCollectionShort", "Collection", "集合"),
        ("Obj_KindCollection", "Collection", "集合"),
        ("Obj_KindTimeSeries", "Time-series collection", "时序集合"),
        ("Obj_KindCapped", "Capped collection", "固定集合"),
        ("Obj_KindClustered", "Clustered collection", "聚簇集合"),
        ("Obj_KindView", "View", "视图"),
        ("Obj_KindBucket", "GridFS bucket", "GridFS 存储桶"),
        ("Obj_TagView", "View", "视图"),
        ("Obj_TagCappedSize", "Capped {0}", "固定 {0}"),

        // ── 空态与状态栏 ──
        ("Obj_NoMatch", "No objects match", "没有匹配的对象"),
        ("Obj_EmptyDatabase", "{0} has no collections yet", "{0} 里还没有集合"),
        ("Obj_NoSelection", "Select an object to see its details", "选择一个对象查看详情"),
        ("Obj_StatusSelected", "Selected {0} · {1} · {2} objects", "已选 {0} · {1} · {2} 个对象"),
        ("Obj_StatusCount", "{0} objects", "{0} 个对象"),

        // ── 详情面板 ──
        ("Obj_TabGeneral", "General", "常规"),
        ("Obj_TabPrivileges", "Privileges", "权限"),
        ("Obj_SecTimeSeries", "Time-series options", "时序选项"),
        ("Obj_SecCapped", "Capped options", "固定集合选项"),
        ("Obj_SecClustered", "Clustered index", "聚簇索引"),
        ("Obj_SecView", "View definition", "视图定义"),
        ("Obj_SecBucket", "Bucket", "存储桶"),
        ("Obj_SecCollection", "Collection options", "集合选项"),
        ("Obj_SecStats", "Statistics", "统计"),
        ("Obj_SecActivity", "Recent activity", "最近活动"),
        ("Obj_FactViewOn", "Source", "源集合"),
        ("Obj_FactStages", "Stages", "阶段数"),
        ("Obj_FactPipeline", "Pipeline", "管道"),
        ("Obj_FactFilesCollection", "Files collection", "文件集合"),
        ("Obj_FactChunksCollection", "Chunks collection", "分块集合"),
        ("Obj_FactChunks", "Chunks", "块数"),
        ("Obj_FactFiles", "Files", "文件"),
        ("Obj_FactValidation", "Validation", "验证"),
        ("Obj_FactRules", "Rules", "规则"),
        ("Obj_FactTtl", "TTL index", "TTL 索引"),
        ("Obj_FactCollation", "Collation", "排序规则"),
        ("Obj_FactDataStorage", "Data / storage", "数据 / 存储"),
        ("Obj_FactCompression", "Compression", "压缩比"),
        ("Obj_FactBuckets", "Buckets", "bucket 数"),
        ("Obj_FactIndexes", "Indexes", "索引"),
        ("Obj_FactWrites", "Writes", "写入"),
        ("Obj_FactLastDoc", "Latest document", "最近文档"),
        ("Obj_FactCreated", "Created", "创建于"),
        ("Obj_None", "None", "无"),
        ("Obj_PerSecond", "{0} / s", "{0} / s"),
        ("Obj_Days", "{0} days", "{0} 天"),
        ("Obj_Hours", "{0} h", "{0} 小时"),
        ("Obj_Minutes", "{0} min", "{0} 分钟"),
        ("Obj_Seconds", "{0} s", "{0} 秒"),
        ("Obj_DdlHint", "Replays in mongosh: createCollection + createIndex + collMod",
            "可在 mongosh 中重放:createCollection + createIndex + collMod"),
        ("Obj_PrivUser", "Current user", "当前用户"),
        ("Obj_PrivRoles", "Roles", "角色"),
        ("Obj_PrivActions", "Effective actions on {0}", "对 {0} 的有效动作"),
        ("Obj_PrivNoAuth", "Authentication is off on this server — every action is allowed.",
            "服务器未开启认证 —— 所有动作均可执行。"),
        ("Obj_PrivNone", "Your user has no actions on this collection.", "当前用户对这个集合没有任何动作。"),
        ("Obj_PrivUnavailable", "Could not read privileges (connectionStatus failed).", "读不到权限(connectionStatus 失败)。"),

        // ── 动作结果 ──
        ("Obj_CopiedDdl", "Copied the structure of {0}", "已复制 {0} 的结构"),
        ("Obj_CopiedDdlDetail", "{0} lines of mongosh", "{0} 行 mongosh 脚本"),
        ("Obj_DropBucketTitle", "Drop GridFS bucket", "删除 GridFS 存储桶"),
        ("Obj_DropBucketBody", "This permanently deletes the bucket {0} — both .files and .chunks — and every file in it. This cannot be undone.",
            "将永久删除存储桶 {0}(.files 与 .chunks 两个集合)及其中全部文件。此操作无法撤销。")
    ];
}
