using System.Globalization;
using Avalonia.Media.Imaging;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>文件表里一行的种类。</summary>
internal enum GridFsEntryKind
{
    /// <summary>「..」返回上级。</summary>
    Parent,

    /// <summary>虚拟目录。</summary>
    Folder,

    /// <summary>文件(一个文件名,显示它的最新版本)。</summary>
    File,

    /// <summary>正在上传(队列里、目标落在当前目录的那几个)。</summary>
    Upload
}

/// <summary>
/// 文件表的一行(设计稿 06 中部)。
/// <para>
/// 一行代表**一个文件名**而不是一份 files 文档:同名的三份在表里是一行「v3」,
/// 旧版本在右侧详情的「版本(同名文件)」里列出 —— 这是 GridFS 版本语义在界面上的样子。
/// </para>
/// </summary>
internal sealed class GridFsEntry : ObservableObject
{
    private GridFsEntry(GridFsEntryKind kind, string name, string path)
    {
        Kind = kind;
        Name = name;
        Path = path;
    }

    /// <summary>勾选状态变了(视图模型据此重算底栏的"已选 N 项")。</summary>
    public event Action<GridFsEntry>? CheckedChanged;

    /// <summary>种类。</summary>
    public GridFsEntryKind Kind { get; }

    /// <summary>显示名(当前目录下的相对名;搜索结果是相对当前目录的路径)。</summary>
    public string Name { get; }

    /// <summary>目录:完整前缀(带结尾斜杠);文件 / 上传:完整文件名;上级:上级前缀。</summary>
    public string Path { get; }

    /// <summary>文件的最新版本;目录与上级为 <see langword="null" />。</summary>
    public GridFsFile? File { get; private init; }

    /// <summary>同名文件的份数。</summary>
    public int Versions { get; private init; }

    /// <summary>目录下(递归)的文件名个数。</summary>
    public long FileCount { get; private init; }

    /// <summary>字节数(目录为下属各文件最新版本之和)。</summary>
    public long Size { get; private init; }

    /// <summary>上传时间(目录为最近一次)。</summary>
    public DateTime? Uploaded { get; private init; }

    /// <summary>contentType(没写就按扩展名猜)。</summary>
    public string ContentType { get; private init; } = "";

    /// <summary>大类。</summary>
    public GridFsCategory Category { get; private init; }

    /// <summary>上传任务(上传行)。</summary>
    public GridFsTransfer? Transfer { get; private init; }

    /// <summary>是不是目录。</summary>
    public bool IsFolder => Kind == GridFsEntryKind.Folder;

    /// <summary>是不是文件。</summary>
    public bool IsFile => Kind == GridFsEntryKind.File;

    /// <summary>是不是「..」。</summary>
    public bool IsParent => Kind == GridFsEntryKind.Parent;

    /// <summary>是不是上传中的行。</summary>
    public bool IsUpload => Kind == GridFsEntryKind.Upload;

    /// <summary>能不能勾选(上级与上传行不能)。</summary>
    public bool CanCheck => Kind is GridFsEntryKind.Folder or GridFsEntryKind.File;

    /// <summary>勾选。</summary>
    public bool IsChecked
    {
        get;
        set
        {
            if (CanCheck && SetProperty(ref field, value))
            {
                CheckedChanged?.Invoke(this);
            }
        }
    }

    /// <summary>图标键。</summary>
    public string IconKey => Kind switch
    {
        GridFsEntryKind.Parent => "Mongo.corner-left-up",
        GridFsEntryKind.Folder => "Mongo.folder",
        GridFsEntryKind.Upload => "Mongo.file-up",
        _ => Category switch
        {
            GridFsCategory.Image => "Mongo.file-image",
            GridFsCategory.Document => "Mongo.file-text",
            GridFsCategory.Video => "Mongo.film",
            _ => "Fs.file"
        }
    };

    /// <summary>
    /// 图标颜色令牌。按设计稿:图片用 BSON 日期那支品红、PDF 用错误红、视频用信息青、
    /// 目录用宿主资源管理器的文件夹橙 —— 与宿主 SFTP 面板看同一类东西时是同一种颜色。
    /// </summary>
    public string IconToken => Kind switch
    {
        GridFsEntryKind.Parent => "VelaTextTertiary",
        GridFsEntryKind.Folder => "VelaFileFolderIcon",
        GridFsEntryKind.Upload => "VelaAccent",
        _ => Category switch
        {
            GridFsCategory.Image => "VelaShellMagenta",
            GridFsCategory.Document => ContentType.Contains("pdf", StringComparison.OrdinalIgnoreCase) ? "VelaError" : "VelaInfo",
            GridFsCategory.Video => "VelaInfo",
            _ => "VelaTextTertiary"
        }
    };

    /// <summary>大小列。</summary>
    public string SizeText => Kind == GridFsEntryKind.Parent ? "" : BsonText.Bytes(Size);

    /// <summary>contentType 列。</summary>
    public string ContentTypeText => Kind is GridFsEntryKind.File or GridFsEntryKind.Upload ? ContentType : "";

    /// <summary>上传时间列(本地时间)。</summary>
    public string UploadedText => Uploaded is { } at ? FormatLocal(at, "yyyy-MM-dd HH:mm") : "";

    /// <summary>块列(<c>4 × 255K</c>)。</summary>
    public string ChunksText => File is { } file ? GridFsPaths.ChunkSummary(file.ChunkCount, file.ChunkSize) : "";

    /// <summary>版本列(<c>v3</c>)。</summary>
    public string VersionText => Kind switch
    {
        GridFsEntryKind.File => "v" + Versions.ToString(CultureInfo.InvariantCulture),
        GridFsEntryKind.Upload => "—",
        _ => ""
    };

    /// <summary>同名多版本:版本号画成强调色徽章。</summary>
    public bool HasVersionBadge => Kind == GridFsEntryKind.File && Versions > 1;

    /// <summary>只有一份:版本号是淡色小字。</summary>
    public bool HasPlainVersion => !HasVersionBadge && VersionText.Length > 0;

    /// <summary>目录名后面的计数。</summary>
    public string CountText => Kind == GridFsEntryKind.Folder ? BsonText.Grouped(FileCount) : "";

    /// <summary>上传进度(0–1)。</summary>
    public double Progress
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>上传进度的块数(<c>3 / 5</c>)。</summary>
    public string ProgressText
    {
        get;
        set => SetProperty(ref field, value);
    } = "";

    /// <summary>缩略图(缩略图视图里懒加载)。</summary>
    public Bitmap? Thumbnail
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    /// <summary>有缩略图。</summary>
    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>「..」行。</summary>
    public static GridFsEntry Parent(string parentPrefix) => new(GridFsEntryKind.Parent, "..", parentPrefix);

    /// <summary>目录行。</summary>
    public static GridFsEntry Folder(string name, string prefix, long files, long bytes, DateTime? last) =>
        new(GridFsEntryKind.Folder, name, prefix) { FileCount = files, Size = bytes, Uploaded = last };

    /// <summary>文件行。</summary>
    public static GridFsEntry ForFile(string name, GridFsFile latest, int versions)
    {
        string type = latest.ContentType ?? GridFsPaths.GuessContentType(latest.Filename);
        return new(GridFsEntryKind.File, name, latest.Filename)
        {
            File = latest,
            Versions = versions,
            FileCount = 1,
            Size = latest.Length,
            Uploaded = latest.UploadDate,
            ContentType = type,
            Category = GridFsPaths.Categorize(latest.Filename, type)
        };
    }

    /// <summary>上传中的行。</summary>
    public static GridFsEntry ForUpload(string name, GridFsTransfer transfer)
    {
        string type = transfer.ContentType;
        return new(GridFsEntryKind.Upload, name, transfer.Filename)
        {
            Transfer = transfer,
            Size = transfer.Size,
            ContentType = type,
            Category = GridFsPaths.Categorize(transfer.Filename, type),
            ProgressText = $"0 / {transfer.ChunkTotal.ToString(CultureInfo.InvariantCulture)}"
        };
    }

    /// <summary>UTC → 本地时间文字。</summary>
    internal static string FormatLocal(DateTime utc, string format) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>传输的种类。</summary>
internal enum GridFsTransferKind
{
    /// <summary>本地 → GridFS。</summary>
    Upload,

    /// <summary>GridFS → 本地。</summary>
    Download
}

/// <summary>
/// 传输队列里的一项。上传与下载共用一条队列、一根进度条(设计稿 06 底栏),
/// 一次只跑一个:并发上传在 GridFS 上省不了多少时间(瓶颈是同一条连接上的写),
/// 却会让进度条和"剩余时间"失去意义。
/// </summary>
internal sealed class GridFsTransfer
{

    /// <summary>种类。</summary>
    public required GridFsTransferKind Kind { get; init; }

    /// <summary>本地路径(上传的源 / 下载的目标)。</summary>
    public required string LocalPath { get; init; }

    /// <summary>GridFS 文件名(上传的目标 / 下载的源)。</summary>
    public required string Filename { get; init; }

    /// <summary>下载的源文件。</summary>
    public GridFsFile? Source { get; init; }

    /// <summary>上传的 metadata。</summary>
    public MongoDB.Bson.BsonDocument? Metadata { get; init; }

    /// <summary>总字节。</summary>
    public long Size { get; init; }

    /// <summary>块大小。</summary>
    public int ChunkSize { get; init; } = GridFsService.DefaultChunkSize;

    /// <summary>contentType(上传行显示用)。</summary>
    public string ContentType { get; init; } = "";

    /// <summary>下载完成之后做的事(「外部打开」:下到临时目录再交给系统)。</summary>
    public Func<string, Task>? Then { get; init; }

    /// <summary>显示名(不含目录)。</summary>
    public string Name => GridFsPaths.BaseName(Filename);

    /// <summary>总块数。</summary>
    public long ChunkTotal => Math.Max(1, (Size + ChunkSize - 1) / ChunkSize);

    // 必须是真字段:Sink 在驱动线程上用 Interlocked.Exchange 写它,属性(含 field 关键字)取不了 ref。
    private long _bytes;

    /// <summary>已走过的字节(任意线程写,UI 定时读)。</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>进度回调(驱动在线程池线程上调)。</summary>
    public IProgress<long> Reporter => new Sink(this);

    private sealed class Sink(GridFsTransfer owner) : IProgress<long>
    {
        public void Report(long value) => Interlocked.Exchange(ref owner._bytes, value);
    }
}

/// <summary>面包屑的一节(<c>products</c> › <c>SKU-7710</c>)。</summary>
/// <param name="Name">显示名。</param>
/// <param name="Prefix">点它跳到的前缀。</param>
/// <param name="IsLast">是不是最后一节(当前目录,主色;其余是目录名青色)。</param>
internal sealed record GridFsCrumb(string Name, string Prefix, bool IsLast);

/// <summary>
/// 右侧详情「版本(同名文件)」里的一行。旧版本可以勾选(一次删几个)、单独删除、恢复、下载;
/// 最新那一份不给勾 —— 删它走详情头部的删除按钮,那里的确认框会讲清谁接替成为最新。
/// </summary>
/// <param name="file">这一份。</param>
/// <param name="number">版本号(按上传时间从旧到新 1、2、3)。</param>
/// <param name="isCurrent">是不是最新的那份。</param>
/// <param name="isShown">是不是详情当前展示的那份。</param>
internal sealed class GridFsVersionRow(GridFsFile file, int number, bool isCurrent, bool isShown) : ObservableObject
{
    /// <summary>这一份(改了 metadata 之后换成重读的那份)。</summary>
    public GridFsFile File
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(SizeText), nameof(DateText));
            }
        }
    } = file;

    /// <summary>版本号(按上传时间从旧到新 1、2、3;GridFS 不存版本号,删掉旧的之后其余的按位置重排)。</summary>
    public int Number { get; } = number;

    /// <summary>是不是最新的那份。</summary>
    public bool IsCurrent { get; } = isCurrent;

    /// <summary>是不是详情当前展示的那份。</summary>
    public bool IsShown
    {
        get;
        set => SetProperty(ref field, value);
    } = isShown;

    /// <summary>勾选了(准备一起删)。只有旧版本能勾。</summary>
    public bool IsChecked
    {
        get;
        set => SetProperty(ref field, value && !IsCurrent);
    }

    /// <summary><c>v3</c>。</summary>
    public string Label => "v" + Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>大小。</summary>
    public string SizeText => BsonText.Bytes(File.Length);

    /// <summary>上传时间(<c>09-26 18:20</c>)。</summary>
    public string DateText => File.UploadDate is { } at ? GridFsEntry.FormatLocal(at, "MM-dd HH:mm") : "—";

    /// <summary>不是最新的才能恢复(也才能在这一行上删、才能勾选)。</summary>
    public bool CanRestore => !IsCurrent;
}
