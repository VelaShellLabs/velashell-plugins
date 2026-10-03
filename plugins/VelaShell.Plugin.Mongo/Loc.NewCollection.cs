namespace VelaShell.Plugin.Mongo;

public sealed partial class Loc
{
    /// <summary>新建集合。</summary>
    private static readonly (string Key, string En, string Zh)[] NewCollectionTexts =
    [
        // ── 名称 ──
        ("NewColl_Name", "Collection name", "集合名称"),
        ("NewColl_NamePlaceholder", "collection_name", "collection_name"),
        ("NewColl_NameAvailable", "Available · {0}", "可用 · {0}"),
        ("NewColl_NameExists", "Already exists · {0}", "已存在 · {0}"),
        ("NewColl_NameInvalid", "Not a valid collection name", "不是合法的集合名"),
        ("NewColl_NameWhitespace", "No leading or trailing spaces", "首尾不能有空格"),
        ("NewColl_NameDollar", "Cannot contain $ or the null character", "不能包含 $ 或空字符"),
        ("NewColl_NameSystem", "Names starting with system. are reserved", "以 system. 开头的名字保留给服务器"),
        ("NewColl_NameDot", "Cannot start or end with a dot", "不能以点号开头或结尾"),
        ("NewColl_NameTooLong", "database.collection exceeds 255 bytes", "库名.集合名 超过 255 字节"),

        // ── 类型卡片 ──
        ("NewColl_Kind", "Type", "类型"),
        ("NewColl_KindPlain", "Collection", "普通集合"),
        ("NewColl_KindPlainDesc", "Default WiredTiger", "默认 WiredTiger"),
        ("NewColl_KindTimeSeriesDesc", "Bucketed by time, compressed", "按时间桶压缩存储"),
        ("NewColl_KindCappedDesc", "capped · fixed size, circular", "capped · 定长循环写"),
        ("NewColl_KindClusteredDesc", "Stored in _id order", "按 _id 聚簇存储"),
        ("NewColl_KindViewDesc", "Read-only · pipeline based", "只读 · 基于管道"),

        // ── 选项面板 ──
        ("NewColl_OptPlain", "Options", "选项"),
        ("NewColl_NotePlain", "None needed", "无需额外选项"),
        ("NewColl_PlainHint", "A regular collection needs no options. Add indexes and validation rules later from Design collection.",
            "普通集合无需额外选项;索引与验证规则可在创建后的「设计集合」里添加。"),
        ("NewColl_OptTimeSeries", "Time-series options", "时序选项"),
        ("NewColl_NoteTimeSeries", "timeField / metaField cannot be changed later", "创建后 timeField / metaField 不可修改"),
        ("NewColl_OptCapped", "Capped options", "固定集合选项"),
        ("NewColl_NoteCapped", "Resizable later with collMod (6.0+)", "6.0 起可用 collMod 调整上限"),
        ("NewColl_OptClustered", "Clustered index", "聚簇索引"),
        ("NewColl_NoteClustered", "Requires MongoDB 5.3+", "需要 MongoDB 5.3+"),
        ("NewColl_OptView", "View definition", "视图定义"),
        ("NewColl_NoteView", "The pipeline runs on every query", "每次查询都会重跑管道"),
        ("NewColl_Granularity", "Granularity", "粒度 granularity"),
        ("NewColl_Expire", "Auto-expire expireAfterSeconds", "自动过期 expireAfterSeconds"),
        ("NewColl_Sample", "Sample document", "示例文档"),
        ("NewColl_Optional", "optional", "可选"),
        ("NewColl_UnitSeconds", "s", "秒"),
        ("NewColl_UnitMinutes", "min", "分"),
        ("NewColl_UnitHours", "h", "时"),
        ("NewColl_UnitDays", "days", "天"),
        ("NewColl_CappedSize", "Size limit size *", "容量上限 size *"),
        ("NewColl_CappedMax", "Max documents max", "文档数上限 max"),
        ("NewColl_Unlimited", "unlimited", "不限"),
        ("NewColl_ClusteredHint", "Documents are stored in _id order: range scans and inserts on _id are faster, and there is no separate _id index.",
            "文档按 _id 顺序存放:按 _id 的范围查询与插入更快,也不再另建一份 _id 索引。"),
        ("NewColl_ViewSource", "Source viewOn", "源集合 viewOn"),

        // ── 更多 ──
        ("NewColl_More", "More", "更多"),
        ("NewColl_Collation", "Collation", "排序规则 collation"),
        ("NewColl_CopyIndexes", "Copy indexes from", "复制索引自"),
        ("NewColl_NoCopy", "(don't copy)", "(不复制)"),
        ("NewColl_EditValidation", "Edit validation rules right after creating", "创建后立即编辑验证规则"),

        // ── 预览与提示 ──
        ("NewColl_Preview", "Command to run", "将执行的命令"),
        ("NewColl_TipPlainTitle", "Collection", "普通集合"),
        ("NewColl_TipPlain", "WiredTiger compresses with snappy by default; a single document is limited to 16 MB.",
            "WiredTiger 默认用 snappy 压缩;单个文档上限 16 MB。"),
        ("NewColl_TipTimeSeriesTitle", "Time-series tips", "时序集合提示"),
        ("NewColl_TipTimeSeries", "Query with the metaField and a time range; put secondary indexes on metaField sub-fields.",
            "查询请尽量带 metaField 与时间范围;二级索引建议建在 metaField 子字段上。"),
        ("NewColl_TipCappedTitle", "Capped collection tips", "固定集合提示"),
        ("NewColl_TipCapped", "Oldest documents are overwritten in insertion order. Single deletes are not allowed and documents cannot grow — good for logs and audit trails.",
            "按插入顺序循环覆盖最旧的文档;不能删除单份文档,也不能让文档变大 —— 适合日志与审计。"),
        ("NewColl_TipClusteredTitle", "Clustered collection tips", "聚簇集合提示"),
        ("NewColl_TipClustered", "Fast range queries on _id; secondary indexes get larger because they point at the _id value.",
            "按 _id 的范围查询很快;二级索引会变大(它们存的是 _id 值)。"),
        ("NewColl_TipViewTitle", "View tips", "视图提示"),
        ("NewColl_TipView", "Views are read-only. Indexes on the source only help the leading $match / $sort of the pipeline.",
            "视图只读;源集合上的索引只对管道开头的 $match / $sort 生效。"),

        // ── 校验 ──
        ("NewColl_ErrTimeField", "timeField is required", "timeField 必填"),
        ("NewColl_ErrFieldName", "timeField must be a top-level field name without $ or .", "timeField 必须是顶层字段名(不含 $ 与 .)"),
        ("NewColl_ErrMetaField", "metaField must differ from timeField and cannot be _id", "metaField 不能与 timeField 相同,也不能是 _id"),
        ("NewColl_ErrExpire", "Expiry must be a positive integer", "过期时间须为正整数"),
        ("NewColl_ErrSize", "size must be a positive number", "容量上限须为正数"),
        ("NewColl_ErrMax", "max must be a positive integer", "文档数上限须为正整数"),
        ("NewColl_ErrViewSource", "Pick a source collection", "请选择源集合"),
        ("NewColl_ErrPipeline", "Pipeline: {0}", "管道有误:{0}"),
        ("NewColl_ErrStage", "Every stage must be a document with exactly one operator", "每个阶段必须是只含一个运算符的文档"),
        ("NewColl_ErrCollation", "Collation needs a locale", "排序规则需要 locale"),
        ("NewColl_ErrCopySource", "Pick a collection to copy indexes from", "请选择复制索引的源集合"),

        // ── 结果 ──
        ("NewColl_Create", "Create collection", "创建集合"),
        ("NewColl_Created", "Created {0}", "已创建 {0}"),
        ("NewColl_IndexesCopied", "Copied {0} indexes", "已复制 {0} 个索引"),
        ("NewColl_IndexesFailed", "Copied {0} indexes; failed: {1}", "已复制 {0} 个索引;失败:{1}")
    ];
}
