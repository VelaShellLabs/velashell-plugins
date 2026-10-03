namespace VelaShell.Plugin.Mongo;

public sealed partial class Loc
{
    /// <summary>GridFS 文件管理(设计稿 06)与它的小对话框(上传、重命名、新建存储桶、缩放预览)。</summary>
    private static readonly (string Key, string En, string Zh)[] GridFsTexts =
    [
        // ── 工具行 ──
        ("Fs_Upload", "Upload files", "上传文件"),
        ("Fs_UploadFolder", "Upload folder", "上传文件夹"),
        ("Fs_Download", "Download", "下载"),
        ("Fs_Rename", "Rename", "重命名"),
        ("Fs_Delete", "Delete", "删除"),
        ("Fs_NewBucket", "New bucket", "新建存储桶"),
        ("Fs_CheckOrphans", "Check orphan chunks", "检查孤儿块"),
        ("Fs_SearchPlaceholder", "File name / metadata.sku …", "文件名 / metadata.sku …"),
        ("Fs_ListView", "List", "列表"),
        ("Fs_GridView", "Thumbnails", "缩略图"),

        // ── 路径行 ──
        ("Fs_VirtualDirs", "Show “/” as folders", "按 “/” 显示为虚拟目录"),
        ("Fs_Flat", "All files (flat)", "全部文件(平铺)"),
        ("Fs_TypeAll", "All", "全部"),
        ("Fs_TypeImages", "Images", "图片"),
        ("Fs_TypeDocs", "Docs", "文档"),
        ("Fs_TypeVideos", "Videos", "视频"),
        ("Fs_SwitchBucket", "Switch bucket", "切换存储桶"),
        ("Fs_BucketsOf", "Buckets in {0}", "{0} 的存储桶"),
        ("Fs_GoRoot", "Go to root /", "转到根目录 /"),

        // ── 文件表 ──
        ("Fs_ColName", "File name", "文件名"),
        ("Fs_ColSize", "Size", "大小"),
        ("Fs_ColType", "contentType", "contentType"),
        ("Fs_ColUploaded", "Uploaded", "上传时间"),
        ("Fs_ColChunks", "Chunks", "块"),
        ("Fs_ColVersion", "Version", "版本"),
        ("Fs_Empty", "This folder is empty", "此目录为空"),
        ("Fs_NoMatch", "No files match “{0}”", "没有匹配 “{0}” 的文件"),
        ("Fs_Truncated", "Showing the first {0} items — narrow it down with search", "仅显示前 {0} 项 · 用搜索缩小范围"),
        ("Fs_OpenFolder", "Open", "打开"),
        ("Fs_DropHere", "Drop files here to upload to {0}", "拖拽文件到此处上传到 {0}"),
        ("Fs_DropHint", "No 16 MB per-file limit · chunkSize 255 KB · metadata can be edited before upload",
            "单文件上限不受 16 MB 限制 · chunkSize 255 KB · 可在上传前编辑 metadata"),

        // ── 详情 ──
        ("Fs_SelectHint", "Select a file to see its details", "选择一个文件查看详情"),
        ("Fs_FolderSummary", "{0} files · {1}", "{0} 个文件 · {1}"),
        ("Fs_LastUpload", "Last upload {0}", "最近上传 {0}"),
        ("Fs_Latest", "v{0} · latest", "v{0} · 最新"),
        ("Fs_OpenExternal", "Open externally", "外部打开"),
        ("Fs_NoPreview", "No preview", "无法预览"),
        ("Fs_PreviewTooLarge", "Over 8 MB — not previewed", "超过 8 MB,不预览"),
        ("Fs_PreviewLoading", "Loading preview…", "正在加载预览…"),
        ("Fs_Zoom", "Zoom preview", "缩放预览"),
        ("Fs_Fit", "Fit", "适应窗口"),
        ("Fs_ChunkInfo", "{0} · {1} chunks", "{0} · {1} 块"),
        ("Fs_EditMetadata", "Edit metadata", "编辑 metadata"),
        ("Fs_MetadataSaved", "metadata saved", "已保存 metadata"),
        ("Fs_MetadataInvalid", "metadata is not a valid document: {0}", "metadata 不是有效的文档:{0}"),
        ("Fs_Versions", "Versions (same file name)", "版本(同名文件)"),
        ("Fs_Current", "current", "当前"),
        ("Fs_Restore", "Restore", "恢复"),
        ("Fs_Restored", "Restored {0} of {1} as the latest version", "已将 {1} 的 {0} 恢复为最新版本"),

        // ── 底栏与传输 ──
        ("Fs_Selected", "{0} selected · {1}", "已选 {0} 项 · {1}"),
        ("Fs_Items", "{0} items · {1}", "{0} 项 · {1}"),
        ("Fs_Status", "{0} · {1} items · {2}", "{0} · {1} 项 · {2}"),
        ("Fs_Uploading", "Uploading {0}", "正在上传 {0}"),
        ("Fs_Downloading", "Downloading {0}", "正在下载 {0}"),
        ("Fs_TransferStats", "{0} · {1}/s · {2} left", "{0} · {1}/s · 剩余 {2}"),
        ("Fs_Queue", "Queue: {0} files", "队列:{0} 个文件"),
        ("Fs_Idle", "No transfers", "无传输任务"),
        ("Fs_CancelTransfers", "Cancel transfers", "取消传输"),
        ("Fs_TransfersCancelled", "Transfers cancelled", "已取消传输"),
        ("Fs_UploadDone", "Uploaded {0} files · {1}", "已上传 {0} 个文件 · {1}"),
        ("Fs_DownloadDone", "Downloaded {0} files · {1}", "已下载 {0} 个文件 · {1}"),
        ("Fs_TransferFailed", "{0} failed: {1}", "{0} 失败:{1}"),
        ("Fs_Seconds", "{0} s", "{0} s"),
        ("Fs_Minutes", "{0} min", "{0} 分钟"),
        ("Fs_Hours", "{0} h", "{0} 小时"),
        ("Fs_OpenFailed", "Couldn’t open {0}", "无法打开 {0}"),

        // ── 选择框标题 ──
        ("Fs_PickUpload", "Choose files to upload", "选择要上传的文件"),
        ("Fs_PickUploadFolder", "Choose a folder to upload", "选择要上传的文件夹"),
        ("Fs_PickSave", "Save file", "保存文件"),
        ("Fs_PickDownloadFolder", "Choose a download folder", "选择下载到的文件夹"),

        // ── 删除 ──
        ("Fs_DeleteTitle", "Delete files", "删除文件"),
        ("Fs_DeleteBodyOne", "Delete {0} and ALL {1} of its versions ({2}, {3} chunks)? This cannot be undone.",
            "将删除 {0} 的全部 {1} 个版本(共 {2},{3} 个块)。此操作无法撤销。"),
        ("Fs_DeleteBodyMany", "Delete the {0} selected items — every version of {1} files, {2} documents ({3}) in total. This cannot be undone.",
            "将删除选中的 {0} 项 —— {1} 个文件的全部版本,共 {2} 份({3})。此操作无法撤销。"),
        ("Fs_DeleteConfirm", "Delete all versions", "删除全部版本"),
        ("Fs_DeleteOnlyLatest", "Delete only the latest version instead (keep the other {0})", "改为只删除最新版本(保留其余 {0} 个)"),
        ("Fs_DeleteVersionTitle", "Delete this version", "删除这个版本"),
        ("Fs_DeleteLatestBody", "Delete only {0} of {1} (uploaded {2})? The other {3} versions are kept and {4} becomes the latest.",
            "只删除 {1} 的 {0}(上传于 {2})。其余 {3} 个版本保留,{4} 将成为最新版本。"),
        ("Fs_DeleteOldBody", "Delete only {0} of {1} (uploaded {2})? The other {3} versions are kept; the latest stays as it is.",
            "只删除 {1} 的 {0}(上传于 {2})。其余 {3} 个版本保留,最新版本不变。"),
        ("Fs_DeleteOnlyVersionBody", "Delete {0}? It has a single version, so the file disappears from the bucket. This cannot be undone.",
            "删除 {0}?它只有这一个版本,删除后文件将从桶中消失。此操作无法撤销。"),
        ("Fs_Deleted", "Deleted {0} file documents", "已删除 {0} 份文件"),
        ("Fs_FactFiles", "Files", "文件"),
        ("Fs_FactVersions", "Versions (documents)", "版本(份)"),
        ("Fs_FactChunks", "Chunks", "块"),
        ("Fs_FactSize", "Size", "大小"),
        ("Fs_FactOrphanFiles", "Missing files", "已不存在的文件"),

        // ── 重命名 ──
        ("Fs_RenameTitle", "Rename", "重命名"),
        ("Fs_RenameLabel", "New name (a “/” moves it to another folder)", "新名称(含 “/” 即移到别的目录)"),
        ("Fs_RenameHintFile", "All {0} versions of this file are renamed together.", "同名的全部 {0} 个版本会一起改名。"),
        ("Fs_RenameHintFolder", "Every file under this folder ({0}) is renamed.", "该目录下的全部文件({0} 个)都会改名。"),
        ("Fs_RenameExists", "A file with this name already exists", "已存在同名文件"),
        ("Fs_RenameFolderExists", "The target folder already contains files", "目标目录下已有文件"),
        ("Fs_RenameIntoSelf", "A folder cannot be moved into itself", "不能把目录移到它自己里面"),
        ("Fs_RenameEmpty", "Name cannot be empty", "名称不能为空"),
        ("Fs_RenamePickOne", "Select a single file or folder to rename", "请只选一个文件或目录来重命名"),
        ("Fs_Renamed", "Renamed {0} file documents", "已改名 {0} 份文件"),

        // ── 新建存储桶 ──
        ("Fs_NewBucketTitle", "New GridFS bucket", "新建存储桶"),
        ("Fs_BucketName", "Bucket name", "存储桶名称"),
        ("Fs_BucketWillCreate", "Will create", "将创建"),
        ("Fs_BucketIndexFiles", "index filename_1_uploadDate_1", "索引 filename_1_uploadDate_1"),
        ("Fs_BucketIndexChunks", "unique index files_id_1_n_1", "唯一索引 files_id_1_n_1"),
        ("Fs_BucketNameEmpty", "Enter a bucket name", "请输入桶名"),
        ("Fs_BucketNameInvalid", "Bucket names cannot contain “$” or NUL, nor start or end with “.”", "桶名不能含 “$” 或空字符,也不能以 “.” 开头或结尾"),
        ("Fs_BucketNameSystem", "Names starting with “system.” are reserved", "以 “system.” 开头的名称是保留的"),
        ("Fs_BucketExists", "A bucket or collection with this name already exists", "已存在同名的桶或集合"),
        ("Fs_BucketCreated", "Created bucket {0}", "已创建存储桶 {0}"),
        ("Fs_CreateBucket", "Create bucket", "新建存储桶"),

        // ── 孤儿块 ──
        ("Fs_OrphansNone", "No orphan chunks", "没有孤儿块"),
        ("Fs_OrphansNoneDetail", "Every chunk in {0} belongs to an existing file.", "{0} 里的每个块都有对应的文件。"),
        ("Fs_OrphansTitle", "Orphan chunks found", "发现孤儿块"),
        ("Fs_OrphansBody", "{0} chunks ({1}) belong to {2} files that no longer exist in {3}. Cleaning up permanently deletes these chunks.",
            "{0} 个块({1})属于 {2} 个已不存在于 {3} 的文件。清理会永久删除这些块。"),
        ("Fs_OrphansConfirm", "Clean up orphan chunks", "清理孤儿块"),
        ("Fs_OrphansCleaned", "Deleted {0} orphan chunks", "已删除 {0} 个孤儿块"),

        // ── 上传对话框 ──
        ("Fs_UploadTitle", "Upload to GridFS", "上传到 GridFS"),
        ("Fs_TargetDir", "Target folder", "目标目录"),
        ("Fs_UploadFiles", "{0} files · {1}", "{0} 个文件 · {1}"),
        ("Fs_UploadMore", "…and {0} more", "…还有 {0} 个"),
        ("Fs_UploadMetadata", "metadata (applied to every file)", "metadata(应用到每个文件)"),
        ("Fs_UploadHint", "Without a contentType each file gets one from its extension · chunkSize 255 KB · an existing name becomes a new version",
            "未写 contentType 时按扩展名逐个填入 · chunkSize 255 KB · 同名文件会成为新版本"),
        ("Fs_UploadConfirm", "Upload {0} files", "上传 {0} 个文件"),
        ("Fs_UploadNothing", "No files to upload", "没有可上传的文件")
    ];
}
