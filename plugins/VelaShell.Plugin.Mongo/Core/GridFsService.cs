using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.GridFS;

namespace VelaShell.Plugin.Mongo.Core;

/// <summary>
/// <c>fs.files</c> 里的一份文件(一个版本)。
/// <para>
/// GridFS 没有"版本"这个概念,只有"同名的若干份文件";规范里约定 <c>uploadDate</c> 最新的那份就是
/// "当前版本"(驱动的 <c>DownloadByName</c> 默认 revision = -1 正是这个意思)。界面上的 v1 / v2 / v3
/// 就是把同名文件按 <c>uploadDate</c> 从旧到新编号。
/// </para>
/// </summary>
/// <param name="Id"><c>_id</c>(驱动写入的是 ObjectId,但规范允许任意类型)。</param>
/// <param name="Filename">完整文件名(含 <c>/</c> 虚拟目录)。</param>
/// <param name="Length">字节数。</param>
/// <param name="ChunkSize">块大小(字节)。</param>
/// <param name="UploadDate">上传时间(UTC);缺字段为 <see langword="null" />。</param>
/// <param name="Metadata"><c>metadata</c> 子文档;没有为 <see langword="null" />。</param>
/// <param name="LegacyContentType">顶层 <c>contentType</c>(规范已弃用,老工具仍会写)。</param>
internal sealed record GridFsFile(
    BsonValue Id,
    string Filename,
    long Length,
    int ChunkSize,
    DateTime? UploadDate,
    BsonDocument? Metadata,
    string? LegacyContentType)
{
    /// <summary>
    /// contentType:先看 <c>metadata.contentType</c>(规范推荐的位置),再看弃用的顶层字段。
    /// 两处都没有返回 <see langword="null" />,由界面按扩展名猜。
    /// </summary>
    public string? ContentType =>
        Metadata is { } meta && meta.TryGetValue("contentType", out BsonValue v) && v.IsString && v.AsString.Length > 0
            ? v.AsString
            : LegacyContentType;

    /// <summary>块数(最后一块可以不满)。</summary>
    public long ChunkCount => ChunkSize <= 0 ? 0 : (Length + ChunkSize - 1) / ChunkSize;

    /// <summary>不含虚拟目录的文件名(<c>main.jpg</c>)。</summary>
    public string BaseName => GridFsPaths.BaseName(Filename);

    /// <summary>从 <c>fs.files</c> 的一份文档读出。字段缺失或类型不对时给零值 —— 别的工具写坏的文档也要能列出来、能删掉。</summary>
    public static GridFsFile From(BsonDocument doc)
    {
        long length = doc.TryGetValue("length", out BsonValue l) && l.IsNumeric ? l.ToInt64() : 0;
        int chunk = doc.TryGetValue("chunkSize", out BsonValue c) && c.IsNumeric ? (int)c.ToInt64() : 0;
        DateTime? uploaded = doc.TryGetValue("uploadDate", out BsonValue u) && u.IsValidDateTime ? u.ToUniversalTime() : null;
        BsonDocument? meta = doc.TryGetValue("metadata", out BsonValue m) && m.IsBsonDocument ? m.AsBsonDocument : null;
        string? legacy = doc.TryGetValue("contentType", out BsonValue t) && t.IsString ? t.AsString : null;
        string name = doc.TryGetValue("filename", out BsonValue f) && f.IsString ? f.AsString : "";
        return new(doc.GetValue("_id", BsonNull.Value), name, length, chunk, uploaded, meta, legacy);
    }
}

/// <summary>列表里的一项:一个虚拟目录,或一个文件名(带它的最新版本与版本数)。</summary>
/// <param name="Folder">虚拟目录名(不含斜杠);文件为 <see langword="null" />。</param>
/// <param name="Latest">文件的最新版本;目录为 <see langword="null" />。</param>
/// <param name="Versions">同名文件的份数(目录为 0)。</param>
/// <param name="Files">目录下(递归)的文件名个数;文件为 1。</param>
/// <param name="Bytes">目录下各文件最新版本的总字节;文件为它自己的长度。</param>
/// <param name="LastUpload">目录下最近一次上传;文件为它的上传时间。</param>
internal sealed record GridFsListItem(string? Folder, GridFsFile? Latest, int Versions, long Files, long Bytes, DateTime? LastUpload)
{
    /// <summary>是不是虚拟目录。</summary>
    public bool IsFolder => Folder is not null;
}

/// <summary>一次列目录的结果。</summary>
/// <param name="Items">各项(服务器按名字排好;界面再按用户选的列排)。</param>
/// <param name="Truncated">超过 <see cref="GridFsService.ListLimit" /> 被截断了。</param>
internal sealed record GridFsListing(IReadOnlyList<GridFsListItem> Items, bool Truncated);

/// <summary>一组孤儿块:<c>files_id</c> 在 <c>fs.files</c> 里已经不存在。</summary>
/// <param name="FilesId">块上记的 <c>files_id</c>。</param>
/// <param name="Chunks">块数。</param>
/// <param name="Bytes">块数据总字节。</param>
internal sealed record GridFsOrphan(BsonValue FilesId, long Chunks, long Bytes);

/// <summary>类型芯片(设计稿 06 路径行右侧:全部 / 图片 / 文档 / 视频)。</summary>
internal enum GridFsTypeFilter
{
    /// <summary>全部。</summary>
    All,

    /// <summary>图片。</summary>
    Images,

    /// <summary>文档。</summary>
    Documents,

    /// <summary>视频。</summary>
    Videos
}

/// <summary>按 contentType / 扩展名归的大类(决定图标、颜色与能不能预览)。</summary>
internal enum GridFsCategory
{
    /// <summary>其他。</summary>
    Other,

    /// <summary>图片。</summary>
    Image,

    /// <summary>文档(PDF、Office、文本)。</summary>
    Document,

    /// <summary>视频。</summary>
    Video
}

/// <summary>
/// GridFS 的路径、类型与搜索规则 —— 纯函数,单测直接打。
/// <para>
/// "虚拟目录"完全是客户端的约定:GridFS 里只有一个平铺的 <c>filename</c>,
/// 按 <c>/</c> 切开显示成目录树,是 Compass、Studio 3T 与 mongofiles 用户的共同习惯。
/// 所以这里所有"目录"运算都只是字符串前缀运算。
/// </para>
/// </summary>
internal static partial class GridFsPaths
{
    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".avif"] = "image/avif", [".heic"] = "image/heic",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv",
        [".json"] = "application/json", [".xml"] = "application/xml", [".html"] = "text/html", [".htm"] = "text/html",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint", [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".rtf"] = "application/rtf", [".odt"] = "application/vnd.oasis.opendocument.text",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo", [".wmv"] = "video/x-ms-wmv",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
        [".zip"] = "application/zip", [".gz"] = "application/gzip", [".tar"] = "application/x-tar",
        [".js"] = "text/javascript", [".css"] = "text/css", [".bin"] = "application/octet-stream"
    };

    private static readonly string[] ImageExtensions = ["jpg", "jpeg", "png", "gif", "webp", "bmp", "svg", "ico", "tif", "tiff", "avif", "heic"];
    private static readonly string[] DocumentExtensions = ["pdf", "txt", "md", "csv", "json", "xml", "html", "htm", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "rtf", "odt"];
    private static readonly string[] VideoExtensions = ["mp4", "m4v", "mov", "webm", "mkv", "avi", "wmv"];

    /// <summary>不含目录的文件名。</summary>
    public static string BaseName(string filename)
    {
        int slash = filename.LastIndexOf('/');
        return slash < 0 ? filename : filename[(slash + 1)..];
    }

    /// <summary>所在虚拟目录(带结尾斜杠;根为空串)。</summary>
    public static string DirectoryOf(string filename)
    {
        int slash = filename.LastIndexOf('/');
        return slash < 0 ? "" : filename[..(slash + 1)];
    }

    /// <summary>上一级目录(<c>products/SKU-7710/</c> → <c>products/</c>)。</summary>
    public static string Parent(string prefix)
    {
        if (prefix.Length == 0)
        {
            return "";
        }
        string trimmed = prefix[..^1];
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? "" : trimmed[..(slash + 1)];
    }

    /// <summary>把用户敲的目录规整成前缀:去掉开头的 <c>/</c>、把 <c>\</c> 换成 <c>/</c>、补结尾斜杠;空即根。</summary>
    public static string NormalizePrefix(string? text)
    {
        string value = (text ?? "").Trim().Replace('\\', '/');
        while (value.StartsWith('/'))
        {
            value = value[1..];
        }
        while (value.Contains("//", StringComparison.Ordinal))
        {
            value = value.Replace("//", "/", StringComparison.Ordinal);
        }
        return value.Length == 0 || value.EndsWith('/') ? value : value + "/";
    }

    /// <summary>按扩展名猜 contentType;认不出给 <c>application/octet-stream</c>。</summary>
    public static string GuessContentType(string filename) =>
        MimeByExtension.TryGetValue(Path.GetExtension(BaseName(filename)), out string? mime) ? mime : "application/octet-stream";

    /// <summary>归大类:contentType 优先(它是上传者明说的),扩展名兜底。</summary>
    public static GridFsCategory Categorize(string filename, string? contentType)
    {
        if (contentType is { Length: > 0 } type)
        {
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return GridFsCategory.Image;
            }
            if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return GridFsCategory.Video;
            }
            if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || IsDocumentType(type))
            {
                return GridFsCategory.Document;
            }
        }
        string ext = Path.GetExtension(BaseName(filename)).TrimStart('.').ToLowerInvariant();
        return ImageExtensions.Contains(ext) ? GridFsCategory.Image
            : VideoExtensions.Contains(ext) ? GridFsCategory.Video
            : DocumentExtensions.Contains(ext) ? GridFsCategory.Document
            : GridFsCategory.Other;
    }

    private static bool IsDocumentType(string type) =>
        type.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
        || type.Equals("application/rtf", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/msword", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/vnd.ms-", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/vnd.openxmlformats-officedocument", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("application/vnd.oasis.opendocument", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 类型芯片 → 服务器端筛选。与 <see cref="Categorize" /> 同一套规则(contentType 前缀或扩展名),
    /// 只是写成查询:筛选必须在服务器上做,不然"图片"芯片只能在已经拉回来的那一页里挑。
    /// </summary>
    public static BsonDocument? TypeFilter(GridFsTypeFilter filter)
    {
        (string typePattern, string[] extensions) = filter switch
        {
            GridFsTypeFilter.Images => ("^image/", ImageExtensions),
            GridFsTypeFilter.Videos => ("^video/", VideoExtensions),
            GridFsTypeFilter.Documents => (
                "^(text/|application/(pdf|json|xml|rtf|msword|vnd\\.ms-|vnd\\.openxmlformats-officedocument|vnd\\.oasis\\.opendocument))",
                DocumentExtensions),
            _ => ("", [])
        };
        if (typePattern.Length == 0)
        {
            return null;
        }
        var type = new BsonRegularExpression(typePattern, "i");
        var ext = new BsonRegularExpression($"\\.({string.Join('|', extensions)})$", "i");
        return new BsonDocument("$or", new BsonArray
        {
            new BsonDocument("metadata.contentType", type),
            new BsonDocument("contentType", type),
            new BsonDocument("filename", ext)
        });
    }

    /// <summary>
    /// 搜索框(设计稿占位「文件名 / metadata.sku …」)→ 服务器端筛选。
    /// <list type="bullet">
    /// <item><c>键:值</c> 按 metadata 字段找(<c>sku:SKU-7710</c> 与 <c>metadata.sku:SKU-7710</c> 等价;
    /// 字符串按"包含、不区分大小写",数字 / 布尔另按等值也算命中);</item>
    /// <item>其余按文件名包含(不区分大小写)。</item>
    /// </list>
    /// </summary>
    public static BsonDocument? SearchFilter(string? text)
    {
        string query = (text ?? "").Trim();
        if (query.Length == 0)
        {
            return null;
        }
        Match kv = KeyValuePattern().Match(query);
        if (!kv.Success)
        {
            return new BsonDocument("filename", new BsonRegularExpression(Regex.Escape(query), "i"));
        }
        string key = kv.Groups["key"].Value;
        string value = kv.Groups["value"].Value.Trim().Trim('"', '\'');
        string field = key switch
        {
            "filename" or "contentType" or "length" or "chunkSize" or "md5" => key,
            _ when key.StartsWith("metadata.", StringComparison.Ordinal) => key,
            _ => "metadata." + key
        };
        var any = new BsonArray { new BsonDocument(field, new BsonRegularExpression(Regex.Escape(value), "i")) };
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        {
            any.Add(new BsonDocument(field, integer));
        }
        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
        {
            any.Add(new BsonDocument(field, real));
        }
        else if (bool.TryParse(value, out bool flag))
        {
            any.Add(new BsonDocument(field, flag));
        }
        return any.Count == 1 ? any[0].AsBsonDocument : new BsonDocument("$or", any);
    }

    /// <summary>"在这个前缀下"的筛选:锚定开头的正则,能走 <c>filename_1_uploadDate_1</c> 索引的区间扫描。</summary>
    public static BsonDocument PrefixFilter(string prefix) =>
        prefix.Length == 0
            ? []
            : new BsonDocument("filename", new BsonRegularExpression("^" + Regex.Escape(prefix)));

    /// <summary>
    /// 桶名校验:返回文案键,合法为 <see langword="null" />。规则取集合名的限制
    /// (不能空、不能含 <c>$</c> 与空字符、不能以 <c>system.</c> 开头),再加一条:不能以点结尾 ——
    /// 否则 <c>x..files</c> 这种名字在 shell 里根本点不出来。
    /// </summary>
    public static string? ValidateBucketName(string? name)
    {
        string value = (name ?? "").Trim();
        if (value.Length == 0)
        {
            return "Fs_BucketNameEmpty";
        }
        if (value.Contains('$') || value.Contains('\0') || value.StartsWith('.') || value.EndsWith('.'))
        {
            return "Fs_BucketNameInvalid";
        }
        if (value.StartsWith("system.", StringComparison.Ordinal))
        {
            return "Fs_BucketNameSystem";
        }
        return value.Length > 100 ? "Fs_BucketNameInvalid" : null;
    }

    /// <summary>"4 × 255K" —— 块数 × 块大小(KB)。</summary>
    public static string ChunkSummary(long chunks, int chunkSize) =>
        chunkSize <= 0 ? "—" : $"{chunks.ToString(CultureInfo.InvariantCulture)} × {(chunkSize / 1024).ToString(CultureInfo.InvariantCulture)}K";

    [GeneratedRegex(@"^(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)\s*:\s*(?<value>.+)$")]
    private static partial Regex KeyValuePattern();
}

/// <summary>
/// 一个 GridFS 存储桶上的全部操作(设计稿 06)。
/// <para>
/// 流式的那几样(上传、下载、恢复旧版本)走驱动的 <see cref="GridFSBucket" />:它按 chunkSize 切块、
/// 失败时回删已写的块,这些细节没理由自己再写一遍。列目录、改名、改 metadata、找孤儿块这几样
/// 驱动不管,直接对 <c>&lt;bucket&gt;.files</c> / <c>&lt;bucket&gt;.chunks</c> 两个集合下命令。
/// </para>
/// <para>
/// <b>大文件从不整块进内存</b>:上传读本地文件流、下载写本地文件流,中间只有驱动的一块缓冲。
/// 唯一整块读的是 <see cref="ReadAllAsync" />(图片预览),调用方负责先看长度。
/// </para>
/// </summary>
internal sealed class GridFsService
{
    /// <summary>默认块大小(255 KB,规范默认值:一块正好塞进 256 KB 的 BSON 文档,不浪费)。</summary>
    public const int DefaultChunkSize = 255 * 1024;

    /// <summary>列一个目录最多回多少项。再多就该用搜索,而不是往界面上堆十万行。</summary>
    public const int ListLimit = 5000;

    /// <summary>图片预览的字节上限(8 MB):再大就不往内存里读了。</summary>
    public const long PreviewLimit = 8L * 1024 * 1024;

    private readonly MongoConnection _connection;

    /// <summary>构造。</summary>
    /// <param name="connection">连接。</param>
    /// <param name="bucket">桶。</param>
    public GridFsService(MongoConnection connection, GridFsBucketInfo bucket)
    {
        _connection = connection;
        Bucket = bucket;
    }

    /// <summary>桶。</summary>
    public GridFsBucketInfo Bucket { get; }

    /// <summary><c>&lt;bucket&gt;.files</c>。</summary>
    public IMongoCollection<BsonDocument> Files => _connection.Collection(Bucket.Database, Bucket.FilesCollection);

    /// <summary><c>&lt;bucket&gt;.chunks</c>。</summary>
    public IMongoCollection<BsonDocument> Chunks => _connection.Collection(Bucket.Database, Bucket.ChunksCollection);

    private GridFSBucket Driver(int chunkSize = DefaultChunkSize) =>
        new(_connection.Database(Bucket.Database), new GridFSBucketOptions
        {
            BucketName = Bucket.Name,
            ChunkSizeBytes = chunkSize
        });

    // ── 读 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 列一个虚拟目录。
    /// <para>
    /// 全在服务器上做:按前缀(走索引)筛、按文件名分组取最新版本、再按"前缀之后到下一个 <c>/</c>"
    /// 二次分组成子目录。一个装了百万文件的桶,界面拿回来的只是当前这一层的几十项。
    /// </para>
    /// </summary>
    /// <param name="prefix">当前目录(带结尾斜杠;根为空串)。</param>
    /// <param name="virtualDirs">按 <c>/</c> 显示虚拟目录;关掉即平铺前缀下的全部文件。</param>
    /// <param name="type">类型芯片。</param>
    /// <param name="search">搜索框;非空时平铺显示前缀下(递归)的命中项。</param>
    /// <param name="cancellationToken">取消。</param>
    public async Task<GridFsListing> ListAsync(string prefix, bool virtualDirs, GridFsTypeFilter type, string? search,
        CancellationToken cancellationToken = default)
    {
        var clauses = new BsonArray();
        BsonDocument prefixFilter = GridFsPaths.PrefixFilter(prefix);
        if (prefixFilter.ElementCount > 0)
        {
            clauses.Add(prefixFilter);
        }
        if (GridFsPaths.TypeFilter(type) is { } typeFilter)
        {
            clauses.Add(typeFilter);
        }
        BsonDocument? searchFilter = GridFsPaths.SearchFilter(search);
        if (searchFilter is not null)
        {
            clauses.Add(searchFilter);
        }
        BsonDocument match = clauses.Count switch
        {
            0 => [],
            1 => clauses[0].AsBsonDocument,
            _ => new BsonDocument("$and", clauses)
        };

        // 倒序扫 filename_1_uploadDate_1 正好是"同名里最新的在前",$first 即最新版本。
        var pipeline = new List<BsonDocument>
        {
            new("$match", match),
            new("$sort", new BsonDocument { { "filename", -1 }, { "uploadDate", -1 } }),
            new("$group", new BsonDocument
            {
                { "_id", "$filename" },
                { "doc", new BsonDocument("$first", "$$ROOT") },
                { "versions", new BsonDocument("$sum", 1) }
            })
        };
        bool tree = virtualDirs && searchFilter is null;
        if (tree)
        {
            int start = prefix.EnumerateRunes().Count();
            pipeline.Add(new("$addFields", new BsonDocument("rest",
                new BsonDocument("$substrCP", new BsonArray { "$_id", start, new BsonDocument("$strLenCP", "$_id") }))));
            pipeline.Add(new("$addFields", new BsonDocument("slash", new BsonDocument("$indexOfCP", new BsonArray { "$rest", "/" }))));
            pipeline.Add(new("$group", new BsonDocument
            {
                {
                    "_id", new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$gte", new BsonArray { "$slash", 0 }),
                        new BsonDocument("dir", new BsonDocument("$substrCP", new BsonArray { "$rest", 0, "$slash" })),
                        new BsonDocument("file", "$_id")
                    })
                },
                { "doc", new BsonDocument("$first", "$doc") },
                { "versions", new BsonDocument("$first", "$versions") },
                { "files", new BsonDocument("$sum", 1) },
                { "bytes", new BsonDocument("$sum", "$doc.length") },
                { "last", new BsonDocument("$max", "$doc.uploadDate") }
            }));
            pipeline.Add(new("$sort", new BsonDocument("_id", 1)));
        }
        else
        {
            pipeline.Add(new("$sort", new BsonDocument("doc.uploadDate", -1)));
        }
        pipeline.Add(new("$limit", ListLimit + 1));

        using IAsyncCursor<BsonDocument> cursor = await Files.AggregateAsync<BsonDocument>(pipeline,
            new AggregateOptions { AllowDiskUse = true }, cancellationToken).ConfigureAwait(false);
        List<BsonDocument> rows = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<GridFsListItem>(rows.Count);
        foreach (BsonDocument row in rows.Take(ListLimit))
        {
            GridFsFile latest = GridFsFile.From(row["doc"].AsBsonDocument);
            int versions = row["versions"].ToInt32();
            if (tree && row["_id"].AsBsonDocument.TryGetValue("dir", out BsonValue dir))
            {
                DateTime? last = row["last"].IsValidDateTime ? row["last"].ToUniversalTime() : null;
                items.Add(new(dir.AsString, null, 0, row["files"].ToInt64(), row["bytes"].ToInt64(), last));
            }
            else
            {
                items.Add(new(null, latest, versions, 1, latest.Length, latest.UploadDate));
            }
        }
        return new(items, rows.Count > ListLimit);
    }

    /// <summary>同名文件的全部版本,最新在前。</summary>
    public async Task<IReadOnlyList<GridFsFile>> VersionsAsync(string filename, CancellationToken cancellationToken = default)
    {
        List<BsonDocument> docs = await Files.Find(new BsonDocument("filename", filename))
            .Sort(new BsonDocument("uploadDate", -1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. docs.Select(GridFsFile.From)];
    }

    /// <summary>一个目录下(递归)的全部文件;<paramref name="latestOnly" /> 时同名只取最新版本(下载用),否则全部版本(删除用)。</summary>
    public async Task<IReadOnlyList<GridFsFile>> FilesUnderAsync(string prefix, bool latestOnly, CancellationToken cancellationToken = default)
    {
        List<BsonDocument> docs = await Files.Find(GridFsPaths.PrefixFilter(prefix))
            .Sort(new BsonDocument { { "filename", 1 }, { "uploadDate", -1 } })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<GridFsFile> files = docs.Select(GridFsFile.From);
        if (latestOnly)
        {
            files = files.GroupBy(static f => f.Filename, StringComparer.Ordinal).Select(static g => g.First());
        }
        return [.. files];
    }

    /// <summary>有没有这个文件名(任意版本)。</summary>
    public async Task<bool> ExistsAsync(string filename, CancellationToken cancellationToken = default) =>
        await Files.Find(new BsonDocument("filename", filename)).Limit(1).AnyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>这个前缀下有没有任何文件。</summary>
    public async Task<bool> AnyUnderAsync(string prefix, CancellationToken cancellationToken = default) =>
        await Files.Find(GridFsPaths.PrefixFilter(prefix)).Limit(1).AnyAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>重读一份文件(改完 metadata 之后)。</summary>
    public async Task<GridFsFile?> GetAsync(BsonValue id, CancellationToken cancellationToken = default)
    {
        BsonDocument? doc = await Files.Find(new BsonDocument("_id", id)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return doc is null ? null : GridFsFile.From(doc);
    }

    /// <summary>
    /// 整块读出(图片预览、缩略图)。**调用方先看长度** —— 这是全类唯一会把文件整个放进内存的地方。
    /// </summary>
    public Task<byte[]> ReadAllAsync(BsonValue id, CancellationToken cancellationToken = default) =>
        Driver().DownloadAsBytesAsync(id, null, cancellationToken);

    /// <summary>下载到一个流(本地文件)。<paramref name="progress" /> 报已写字节数。</summary>
    public async Task DownloadAsync(BsonValue id, Stream destination, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Stream target = progress is null ? destination : new ProgressStream(destination, progress);
        await Driver().DownloadToStreamAsync(id, target, null, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── 写 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 从一个流上传。同名文件已存在时这就是它的新版本(GridFS 的语义,不是覆盖)。
    /// <para>
    /// 用 <c>OpenUploadStream</c> 自己搬字节,而不是 <c>UploadFromStream</c>:中途失败或**取消**时
    /// 要用一个不会被取消的令牌 <c>Abort</c>,把已经写进去的块回删 —— 用户点了"取消传输",
    /// 留在 chunks 里的半个文件就是下一次"检查孤儿块"要清的垃圾。
    /// </para>
    /// </summary>
    /// <param name="filename">完整文件名(含虚拟目录)。</param>
    /// <param name="source">源(按顺序读一遍)。</param>
    /// <param name="metadata">metadata;没有为 <see langword="null" />。</param>
    /// <param name="progress">报已读字节数。</param>
    /// <param name="chunkSize">块大小。</param>
    /// <param name="cancellationToken">取消。</param>
    /// <returns>新文件的 <c>_id</c>。</returns>
    public async Task<ObjectId> UploadAsync(string filename, Stream source, BsonDocument? metadata,
        IProgress<long>? progress = null, int chunkSize = DefaultChunkSize, CancellationToken cancellationToken = default)
    {
        Stream input = progress is null ? source : new ProgressStream(source, progress);
        GridFSUploadStream upload = await Driver(chunkSize).OpenUploadStreamAsync(filename, new GridFSUploadOptions
        {
            ChunkSizeBytes = chunkSize,
            Metadata = metadata
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            await input.CopyToAsync(upload, chunkSize, cancellationToken).ConfigureAwait(false);
            await upload.CloseAsync(cancellationToken).ConfigureAwait(false);
            return upload.Id;
        }
        catch
        {
            // 回删已写的块与(若已写)files 文档;用 None —— 拿已取消的令牌去清理,清理本身也会被取消。
            try
            {
                await upload.AbortAsync(CancellationToken.None).ConfigureAwait(false);
                // 取消时最后那一批块的 insert 可能已经发到服务器、只是客户端不再等它 ——
                // 它会在 Abort 的 deleteMany 之后才落地。隔一小会儿按 files_id 再扫一遍,不留孤儿块。
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
                await Chunks.DeleteManyAsync(new BsonDocument("files_id", upload.Id), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception abort) when (abort is MongoException or InvalidOperationException or TimeoutException)
            {
                // 回删失败(连接已断)不掩盖原始错误;剩下的块由「检查孤儿块」兜底。
            }
            throw;
        }
        finally
        {
            await upload.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 恢复旧版本:把它的内容以**新的 uploadDate** 再写一份,于是它成了最新版本。
    /// 旧的几份原样保留 —— 恢复本身也可以再被"恢复"回去,这一步没有任何东西被删。
    /// 流式拷贝(下载流直接喂给上传),大文件也不进内存。
    /// </summary>
    public async Task<ObjectId> RestoreAsync(GridFsFile version, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        GridFSBucket bucket = Driver(version.ChunkSize > 0 ? version.ChunkSize : DefaultChunkSize);
        await using Stream source = await bucket.OpenDownloadStreamAsync(version.Id, null, cancellationToken).ConfigureAwait(false);
        Stream input = progress is null ? source : new ProgressStream(source, progress);
        return await bucket.UploadFromStreamAsync(version.Filename, input, new GridFSUploadOptions
        {
            ChunkSizeBytes = version.ChunkSize > 0 ? version.ChunkSize : DefaultChunkSize,
            Metadata = version.Metadata?.DeepClone().AsBsonDocument
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 改名:同名的**全部版本**一起改 —— 只改最新一份的话,旧版本会留在原名下,
    /// 版本历史就此断成两截。
    /// </summary>
    /// <returns>改了几份。</returns>
    public async Task<long> RenameAsync(string from, string to, CancellationToken cancellationToken = default)
    {
        UpdateResult result = await Files.UpdateManyAsync(new BsonDocument("filename", from),
            new BsonDocument("$set", new BsonDocument("filename", to)), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount;
    }

    /// <summary>改一个虚拟目录的名:前缀下每份文件的 filename 换前缀(分批 bulkWrite)。</summary>
    /// <returns>改了几份。</returns>
    public async Task<long> RenameFolderAsync(string fromPrefix, string toPrefix, CancellationToken cancellationToken = default)
    {
        List<BsonDocument> docs = await Files.Find(GridFsPaths.PrefixFilter(fromPrefix))
            .Project(new BsonDocument { { "_id", 1 }, { "filename", 1 } })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        long modified = 0;
        foreach (BsonDocument[] batch in docs.Chunk(1000))
        {
            var writes = batch.Select(doc => (WriteModel<BsonDocument>)new UpdateOneModel<BsonDocument>(
                new BsonDocument("_id", doc["_id"]),
                new BsonDocument("$set", new BsonDocument("filename", toPrefix + doc["filename"].AsString[fromPrefix.Length..])))).ToList();
            BulkWriteResult<BsonDocument> result = await Files.BulkWriteAsync(writes, new BulkWriteOptions { IsOrdered = false },
                cancellationToken).ConfigureAwait(false);
            modified += result.ModifiedCount;
        }
        return modified;
    }

    /// <summary>删一份(一个版本):驱动先删 files 文档再删块,与规范一致。</summary>
    public Task DeleteAsync(BsonValue id, CancellationToken cancellationToken = default) =>
        Driver().DeleteAsync(id, cancellationToken);

    /// <summary>
    /// 批量删(一个文件名的全部版本、一个目录下的全部文件)。顺序同驱动:先 files 后 chunks ——
    /// 中途断掉留下的是"没有主人的块"(检查孤儿块能清掉),而不是"列得出、下载就坏"的半个文件。
    /// </summary>
    /// <returns>删掉的文件份数。</returns>
    public async Task<long> DeleteManyAsync(IReadOnlyCollection<BsonValue> ids, CancellationToken cancellationToken = default)
    {
        long deleted = 0;
        foreach (BsonValue[] batch in ids.Chunk(1000))
        {
            var inBatch = new BsonDocument("$in", new BsonArray(batch));
            DeleteResult files = await Files.DeleteManyAsync(new BsonDocument("_id", inBatch), cancellationToken).ConfigureAwait(false);
            await Chunks.DeleteManyAsync(new BsonDocument("files_id", inBatch), cancellationToken).ConfigureAwait(false);
            deleted += files.DeletedCount;
        }
        return deleted;
    }

    /// <summary>改 metadata(整份替换;传 <see langword="null" /> 即删掉 metadata 字段)。</summary>
    public async Task UpdateMetadataAsync(BsonValue id, BsonDocument? metadata, CancellationToken cancellationToken = default)
    {
        BsonDocument update = metadata is null
            ? new BsonDocument("$unset", new BsonDocument("metadata", ""))
            : new BsonDocument("$set", new BsonDocument("metadata", metadata));
        await Files.UpdateOneAsync(new BsonDocument("_id", id), update, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // ── 孤儿块 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 找孤儿块:<c>files_id</c> 在 files 里已经没有的块(删到一半断开、或别的工具只删了 files 文档)。
    /// <para>
    /// 分两步,刻意不在第一步就算字节:先在 <c>files_id_1_n_1</c> 索引上取不同的 files_id
    /// (DISTINCT_SCAN,不读块数据),<c>$lookup</c> 到 files 里找不到的才是孤儿;
    /// 第二步只对孤儿那几个 id 读块、算大小。直接对整个 chunks 集合 <c>$binarySize</c> 等于把整个桶读一遍。
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<GridFsOrphan>> FindOrphansAsync(CancellationToken cancellationToken = default)
    {
        var pipeline = new[]
        {
            new BsonDocument("$sort", new BsonDocument("files_id", 1)),
            new BsonDocument("$group", new BsonDocument("_id", "$files_id")),
            new BsonDocument("$lookup", new BsonDocument
            {
                { "from", Bucket.FilesCollection },
                { "localField", "_id" },
                { "foreignField", "_id" },
                { "as", "owner" }
            }),
            new BsonDocument("$match", new BsonDocument("owner", new BsonDocument("$size", 0))),
            new BsonDocument("$project", new BsonDocument("_id", 1))
        };
        using IAsyncCursor<BsonDocument> cursor = await Chunks.AggregateAsync<BsonDocument>(pipeline,
            new AggregateOptions { AllowDiskUse = true }, cancellationToken).ConfigureAwait(false);
        List<BsonValue> ids = [.. (await cursor.ToListAsync(cancellationToken).ConfigureAwait(false)).Select(static d => d["_id"])];
        if (ids.Count == 0)
        {
            return [];
        }

        var orphans = new List<GridFsOrphan>(ids.Count);
        foreach (BsonValue[] batch in ids.Chunk(1000))
        {
            var stats = new[]
            {
                new BsonDocument("$match", new BsonDocument("files_id", new BsonDocument("$in", new BsonArray(batch)))),
                new BsonDocument("$group", new BsonDocument
                {
                    { "_id", "$files_id" },
                    { "chunks", new BsonDocument("$sum", 1) },
                    { "bytes", new BsonDocument("$sum", new BsonDocument("$binarySize", "$data")) }
                })
            };
            using IAsyncCursor<BsonDocument> rows = await Chunks.AggregateAsync<BsonDocument>(stats,
                new AggregateOptions { AllowDiskUse = true }, cancellationToken).ConfigureAwait(false);
            foreach (BsonDocument row in await rows.ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                orphans.Add(new(row["_id"], row["chunks"].ToInt64(), row["bytes"].IsNumeric ? row["bytes"].ToInt64() : 0));
            }
        }
        return orphans;
    }

    /// <summary>清理孤儿块。删之前**再确认一遍**这些 id 在 files 里确实不存在 —— 找与删之间可能有人刚好上传完。</summary>
    /// <returns>删掉的块数。</returns>
    public async Task<long> DeleteOrphansAsync(IReadOnlyCollection<BsonValue> filesIds, CancellationToken cancellationToken = default)
    {
        long deleted = 0;
        foreach (BsonValue[] batch in filesIds.Chunk(1000))
        {
            var alive = (await Files.Find(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(batch))))
                    .Project(new BsonDocument("_id", 1)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(static d => d["_id"]).ToHashSet();
            BsonValue[] dead = [.. batch.Where(id => !alive.Contains(id))];
            if (dead.Length == 0)
            {
                continue;
            }
            DeleteResult result = await Chunks.DeleteManyAsync(new BsonDocument("files_id", new BsonDocument("$in", new BsonArray(dead))),
                cancellationToken).ConfigureAwait(false);
            deleted += result.DeletedCount;
        }
        return deleted;
    }

    // ── 桶 ─────────────────────────────────────────────────────────────────

    /// <summary>同库的全部桶(按名字排)。</summary>
    public static async Task<IReadOnlyList<GridFsBucketInfo>> ListBucketsAsync(MongoConnection connection, string database,
        CancellationToken cancellationToken = default) =>
        MongoConnection.FindBuckets(database, await connection.ListCollectionsAsync(database, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// 建一个桶:<c>&lt;name&gt;.files</c> 与 <c>&lt;name&gt;.chunks</c> 两个集合 + 规范要求的两条索引
    /// (<c>{filename:1, uploadDate:1}</c>、唯一的 <c>{files_id:1, n:1}</c>)。
    /// 驱动在第一次上传时也会补索引,但"新建存储桶"之后马上能在对象树里看到它、看到索引,才是用户预期的样子。
    /// </summary>
    public static async Task CreateBucketAsync(MongoConnection connection, string database, string name,
        CancellationToken cancellationToken = default)
    {
        IMongoDatabase db = connection.Database(database);
        foreach (string collection in new[] { name + ".files", name + ".chunks" })
        {
            try
            {
                await db.CreateCollectionAsync(collection, null, cancellationToken).ConfigureAwait(false);
            }
            catch (MongoCommandException ex) when (ex.Code == 48)
            {
                // NamespaceExists:集合已在(比如只建了一半的桶),补索引即可。
            }
        }
        await db.GetCollection<BsonDocument>(name + ".files").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "filename", 1 }, { "uploadDate", 1 } },
            new CreateIndexOptions { Name = "filename_1_uploadDate_1" }), cancellationToken: cancellationToken).ConfigureAwait(false);
        await db.GetCollection<BsonDocument>(name + ".chunks").Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "files_id", 1 }, { "n", 1 } },
            new CreateIndexOptions { Name = "files_id_1_n_1", Unique = true }), cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 数字节的流包装:上传时数"驱动读走了多少",下载时数"写进本地多少"。
/// 只转发,不缓冲 —— 进度条的数字就是真实走过的字节。
/// </summary>
internal sealed class ProgressStream(Stream inner, IProgress<long> progress) : Stream
{
    private long _total;

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override long Length => inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _total;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Count(count);
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        Count(count);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Count(buffer.Length);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    private int Count(int bytes)
    {
        if (bytes > 0)
        {
            _total += bytes;
            progress.Report(_total);
        }
        return bytes;
    }
}
