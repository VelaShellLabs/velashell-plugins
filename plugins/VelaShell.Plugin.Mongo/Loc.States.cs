namespace VelaShell.Plugin.Mongo;

public sealed partial class Loc
{
    /// <summary>状态、反馈与确认(空态、长查询、只读、撤销、删除确认、编辑冲突)。</summary>
    private static readonly (string Key, string En, string Zh)[] StatesTexts =
    [
        // ── 数据区三态 ──
        ("State_EmptyTitle", "{0} has no documents yet", "{0} 中还没有文档"),
        ("State_EmptyHint", "Add one by hand, or import from JSON / CSV", "可以手动添加,或从 JSON / CSV 导入"),
        ("State_ImportEllipsis", "Import…", "导入…"),
        ("State_NoResultTitle", "No documents match the filter", "没有匹配筛选条件的文档"),
        ("State_ClearFilter", "Clear filter", "清除筛选"),
        ("State_ViewPlan", "View execution plan", "查看执行计划"),
        ("State_LongQueryTitle", "Querying {0}…", "正在查询 {0}…"),
        ("State_LongQueryElapsed", "Running for {0} s", "已运行 {0} s"),
        ("State_LongQueryOpid", "Running for {0} s · server opid {1}", "已运行 {0} s · 服务端 opid {1}"),
        ("State_KillOp", "Cancel (killOp)", "取消(killOp)"),
        ("State_Killed", "Query stopped on the server (killOp)", "已在服务器上停止查询(killOp)"),
        ("State_Cancelled", "Query cancelled", "已取消查询"),

        // ── 只读 ──
        ("State_ReadOnly", "Read-only mode", "只读模式"),
        ("State_ReadOnlyProd", "Read-only mode · production connection", "只读模式 · 生产连接"),
        ("State_ReadOnlyDetail", "Editing, deleting and importing are disabled", "编辑、删除、导入已禁用"),
        ("State_UnlockButton", "Unlock writes for 15 min", "解锁写入 15 分钟"),
        ("State_UnlockTitle", "Unlock writes for 15 minutes?", "解锁写入 15 分钟?"),
        ("State_UnlockBody", "{0} will accept edits, deletes and imports for 15 minutes, then switch back to read-only automatically.",
            "{0} 将在 15 分钟内允许编辑、删除与导入,到时自动恢复只读。"),
        ("State_UnlockConfirm", "Unlock for 15 min", "解锁 15 分钟"),
        ("State_Unlocked", "Writes unlocked for 15 minutes", "已解锁写入,15 分钟后自动恢复只读"),
        ("State_Relocked", "Back to read-only mode", "已恢复只读模式"),

        // ── 提交 / 撤销 ──
        ("State_Committed", "Committed {0} change(s)", "已提交 {0} 处修改"),
        ("State_DetailUpdated", "{0} updated", "{0} 更新"),
        ("State_DetailInserted", "{0} inserted", "{0} 插入"),
        ("State_DetailDeleted", "{0} deleted", "{0} 删除"),
        ("State_CommitFailed", "{0} change(s) were not written", "{0} 处修改没有写进去"),
        ("State_Undo", "Undo", "撤销"),
        ("State_UndoDone", "Reverted {0} change(s)", "已撤销 {0} 处修改"),
        ("State_UndoPartial", "Reverted {0}; {1} were changed by someone else and left as they are",
            "已撤销 {0} 处;{1} 处已被别人改过,保持原样"),
        ("State_Discarded", "Discarded {0} pending change(s)", "已放弃 {0} 处待提交的修改"),

        // ── 删除确认 ──
        ("State_DeleteDocsTitle", "Delete documents", "删除文档"),
        ("State_DeleteDocsBody", "{0} document(s) will be permanently deleted from {1}. You can undo for 10 seconds after committing.",
            "将从 {1} 永久删除 {0} 份文档。提交后 10 秒内可以撤销。"),
        ("State_DeleteDocsConfirm", "Delete {0} document(s)", "删除 {0} 个文档"),
        ("State_FactUpdates", "Updates", "更新"),
        ("State_FactInserts", "Inserts", "插入"),
        ("State_FactDeletes", "Deletes", "删除"),

        // ── 编辑冲突 ──
        ("State_ConflictTitle", "Edit conflict", "编辑冲突"),
        ("State_ConflictBody", "Document {0} was changed by another session while you were editing it.",
            "文档 {0} 在你编辑期间已被其他会话修改。"),
        ("State_ConflictStamp", "Document {0} was changed by another session while you were editing it (updatedAt {1} → {2}).",
            "文档 {0} 在你编辑期间已被其他会话修改(updatedAt {1} → {2})。"),
        ("State_ConflictDeleted", "Document {0} was deleted by another session while you were editing it.",
            "文档 {0} 在你编辑期间已被其他会话删除。"),
        ("State_ConflictMine", "Mine", "我的"),
        ("State_ConflictServer", "Server", "服务器"),
        ("State_ConflictHint", "{0} changed on both sides — pick which one to keep.", "{0} 两边都改了,需要选择保留哪一个。"),
        ("State_ConflictHintNone", "These fields are unchanged on the server — another field or updatedAt moved. Merge keeps your edits.",
            "服务器上这些字段没变 —— 变的是别的字段或 updatedAt。合并即可保留你的修改。"),
        ("State_ConflictDiscard", "Discard my changes", "放弃我的修改"),
        ("State_ConflictOverwrite", "Overwrite server version", "覆盖服务器版本"),
        ("State_ConflictMerge", "Merge (pick per field)", "合并(逐字段选择)"),
    ];
}
