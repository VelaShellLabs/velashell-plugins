using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 右侧 320px 详情(设计稿 06 右栏):文件名 + 版本徽章 + 下载 / 外部打开 / 删除;
/// 图片预览;fs.files 字段;metadata(只读显示,可切到编辑并保存);同名文件的版本列表(恢复 / 下载)。
/// <para>
/// 点版本列表里的旧版本,详情就切到那一份 —— 于是头部的删除按钮删的永远是"正在看的这一份",
/// 与工具栏上"删除这个文件名的全部版本"界线分明。
/// </para>
/// </summary>
internal sealed class GridFsDetailsViewModel : ObservableObject, IDisposable
{
    private readonly GridFsTabViewModel _owner;
    private CancellationTokenSource? _loadCts;
    private GridFsEntry? _entry;
    private GridFsFile? _file;
    private Bitmap? _image;
    private string _previewMessage = "";
    private string _imageInfo = "";
    private bool _isPreviewLoading;
    private string _metadataText = "";
    private string _metadataDraft = "";
    private string? _metadataError;
    private bool _isEditing;

    /// <summary>构造。</summary>
    /// <param name="owner">所属标签页。</param>
    public GridFsDetailsViewModel(GridFsTabViewModel owner)
    {
        _owner = owner;
        DownloadCommand = new(() => _file is { } f ? _owner.DownloadFileAsync(f) : Task.CompletedTask, () => _file is not null);
        OpenExternalCommand = new(() => _file is { } f ? _owner.OpenExternalAsync(f) : Task.CompletedTask, () => _file is not null);
        DeleteCommand = new(() => _file is { } f ? _owner.DeleteVersionAsync(f, [.. Versions]) : Task.CompletedTask, () => _file is not null);
        ZoomCommand = new(Zoom, () => _image is not null);
        EditMetadataCommand = new(BeginEdit, () => _file is not null);
        SaveMetadataCommand = new(SaveMetadataAsync);
        CancelMetadataCommand = new(() => IsEditing = false);
        RestoreCommand = new(row => _owner.RestoreVersionAsync(row.File, row.Label), static row => row.CanRestore);
        DownloadVersionCommand = new(row => _owner.DownloadFileAsync(row.File));
        ShowVersionCommand = new(row => _ = ShowFileAsync(row.File));
    }

    private Loc Loc => _owner.Loc;

    /// <summary>当前行(文件或目录);没选为 <see langword="null" />。</summary>
    public GridFsEntry? Entry
    {
        get => _entry;
        private set
        {
            if (SetProperty(ref _entry, value))
            {
                RaisePropertiesChanged(nameof(HasEntry), nameof(IsEmpty), nameof(IsFolder), nameof(IsFile), nameof(Name),
                    nameof(IconKey), nameof(IconToken), nameof(FolderSummary), nameof(FolderLastUpload));
            }
        }
    }

    /// <summary>正在看的那一份(默认最新版本)。</summary>
    public GridFsFile? File
    {
        get => _file;
        private set
        {
            if (SetProperty(ref _file, value))
            {
                RaisePropertiesChanged(nameof(IdText), nameof(FilenameText), nameof(LengthText), nameof(ChunkText), nameof(UploadText),
                    nameof(BadgeText), nameof(IsShowingLatest), nameof(Name));
                DownloadCommand.RaiseCanExecuteChanged();
                OpenExternalCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                EditMetadataCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>有选中。</summary>
    public bool HasEntry => _entry is not null;

    /// <summary>空态(没选)。</summary>
    public bool IsEmpty => _entry is null;

    /// <summary>选的是文件。</summary>
    public bool IsFile => _entry?.IsFile == true;

    /// <summary>选的是目录。</summary>
    public bool IsFolder => _entry?.IsFolder == true;

    /// <summary>头部名字。</summary>
    public string Name => _file?.BaseName ?? _entry?.Name ?? "";

    /// <summary>头部图标。</summary>
    public string IconKey => _entry?.IconKey ?? "Fs.file";

    /// <summary>头部图标颜色。</summary>
    public string IconToken => _entry?.IconToken ?? "VelaTextTertiary";

    /// <summary>头部徽章:最新版本「v3 · 最新」,旧版本「v2」。</summary>
    public string BadgeText
    {
        get
        {
            GridFsVersionRow? row = Versions.FirstOrDefault(v => _file is not null && v.File.Id == _file.Id);
            if (row is null)
            {
                return "";
            }
            return row.IsCurrent ? Loc.Format("Fs_Latest", row.Number) : row.Label;
        }
    }

    /// <summary>看的是最新版本(徽章强调色);旧版本徽章转警告色,提醒"这不是当前那一份"。</summary>
    public bool IsShowingLatest => Versions.FirstOrDefault(v => _file is not null && v.File.Id == _file.Id)?.IsCurrent ?? true;

    // ── 目录 ───────────────────────────────────────────────────────────────

    /// <summary>目录摘要「12 个文件 · 61.9 MB」。</summary>
    public string FolderSummary => _entry is { IsFolder: true } folder
        ? Loc.Format("Fs_FolderSummary", BsonText.Grouped(folder.FileCount), BsonText.Bytes(folder.Size))
        : "";

    /// <summary>目录里最近一次上传。</summary>
    public string FolderLastUpload => _entry is { IsFolder: true, Uploaded: { } at }
        ? Loc.Format("Fs_LastUpload", GridFsEntry.FormatLocal(at, "yyyy-MM-dd HH:mm"))
        : "";

    // ── 预览 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 解出来的图片。换图时**不**主动 Dispose 旧的:「缩放预览」对话框可能还挂着它,
    /// 而列表刷新随时会让详情重载 —— 提前释放就是一张画到一半的空图;交给终结器回收。
    /// </summary>
    public Bitmap? Image
    {
        get => _image;
        private set
        {
            if (SetProperty(ref _image, value))
            {
                RaisePropertyChanged(nameof(HasImage));
                ZoomCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>有图。</summary>
    public bool HasImage => _image is not null;

    /// <summary>没图时预览框里那句(「无法预览」「超过 8 MB,不预览」「正在加载预览…」)。</summary>
    public string PreviewMessage
    {
        get => _previewMessage;
        private set => SetProperty(ref _previewMessage, value);
    }

    /// <summary>预览加载中。</summary>
    public bool IsPreviewLoading
    {
        get => _isPreviewLoading;
        private set => SetProperty(ref _isPreviewLoading, value);
    }

    /// <summary>「1600 × 1600 · JPEG」。</summary>
    public string ImageInfo
    {
        get => _imageInfo;
        private set => SetProperty(ref _imageInfo, value);
    }

    // ── fs.files 字段 ──────────────────────────────────────────────────────

    /// <summary>小节标题(<c>fs.files</c>)。</summary>
    public string FilesTitle => _owner.Bucket.FilesCollection;

    /// <summary><c>_id</c>(ObjectId 缩写)。</summary>
    public string IdText => _file?.Id switch
    {
        BsonObjectId oid => BsonText.Shorten(oid.Value.ToString()),
        { } other => BsonText.Cell(other),
        null => ""
    };

    /// <summary>完整文件名。</summary>
    public string FilenameText => _file?.Filename ?? "";

    /// <summary>字节数(千分位)。</summary>
    public string LengthText => _file is { } f ? BsonText.Grouped(f.Length) : "";

    /// <summary>「261,120 · 4 块」。</summary>
    public string ChunkText => _file is { } f ? Loc.Format("Fs_ChunkInfo", BsonText.Grouped(f.ChunkSize), BsonText.Grouped(f.ChunkCount)) : "";

    /// <summary>上传时间(本地,秒)。</summary>
    public string UploadText => _file?.UploadDate is { } at ? GridFsEntry.FormatLocal(at, "yyyy-MM-dd HH:mm:ss") : "—";

    // ── metadata ───────────────────────────────────────────────────────────

    /// <summary>metadata 的 mongosh 写法(只读显示)。</summary>
    public string MetadataText
    {
        get => _metadataText;
        private set
        {
            if (SetProperty(ref _metadataText, value))
            {
                RaisePropertyChanged(nameof(MetadataHeight));
            }
        }
    }

    /// <summary>
    /// 只读 metadata 框的高度:按行数撑开(每行 14px + 编辑器上下内边距与边框),最多 180 再滚动。
    /// 编辑器(AvaloniaEdit)放在 StackPanel 里拿到的是无限高,量不出内容高度 —— 只能由这里给。
    /// </summary>
    public double MetadataHeight => Math.Clamp(((_metadataText.Count(static c => c == '\n') + 1) * 14) + 18, 40, 180);

    /// <summary>编辑中的草稿。</summary>
    public string MetadataDraft
    {
        get => _metadataDraft;
        set
        {
            if (SetProperty(ref _metadataDraft, value ?? ""))
            {
                MetadataError = ShellJson.TryParseDocument(_metadataDraft, out _, out string? error)
                    ? null
                    : Loc.Format("Fs_MetadataInvalid", error);
            }
        }
    }

    /// <summary>草稿解析错误;没有为 <see langword="null" />。</summary>
    public string? MetadataError
    {
        get => _metadataError;
        private set
        {
            if (SetProperty(ref _metadataError, value))
            {
                RaisePropertyChanged(nameof(HasMetadataError));
            }
        }
    }

    /// <summary>有解析错误。</summary>
    public bool HasMetadataError => _metadataError is not null;

    /// <summary>正在编辑 metadata。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (SetProperty(ref _isEditing, value))
            {
                RaisePropertyChanged(nameof(IsViewing));
            }
        }
    }

    /// <summary>只读显示中。</summary>
    public bool IsViewing => !_isEditing;

    // ── 版本 ───────────────────────────────────────────────────────────────

    /// <summary>同名文件的各个版本,最新在前。</summary>
    public ObservableCollection<GridFsVersionRow> Versions { get; } = [];

    // ── 命令 ───────────────────────────────────────────────────────────────

    /// <summary>下载正在看的这一份。</summary>
    public AsyncCommand DownloadCommand { get; }

    /// <summary>外部打开(下到临时目录交给系统)。</summary>
    public AsyncCommand OpenExternalCommand { get; }

    /// <summary>只删正在看的这一份(一个版本)。</summary>
    public AsyncCommand DeleteCommand { get; }

    /// <summary>缩放预览。</summary>
    public RelayCommand ZoomCommand { get; }

    /// <summary>编辑 metadata。</summary>
    public RelayCommand EditMetadataCommand { get; }

    /// <summary>保存 metadata(updateOne)。</summary>
    public AsyncCommand SaveMetadataCommand { get; }

    /// <summary>放弃编辑。</summary>
    public RelayCommand CancelMetadataCommand { get; }

    /// <summary>恢复某个旧版本。</summary>
    public AsyncCommand<GridFsVersionRow> RestoreCommand { get; }

    /// <summary>下载某个版本。</summary>
    public AsyncCommand<GridFsVersionRow> DownloadVersionCommand { get; }

    /// <summary>在详情里看某个版本。</summary>
    public RelayCommand<GridFsVersionRow> ShowVersionCommand { get; }

    // ── 加载 ───────────────────────────────────────────────────────────────

    /// <summary>换一行显示(目录只显示摘要;文件读版本列表并加载预览)。</summary>
    public async Task ShowAsync(GridFsEntry? entry)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsEditing = false;
        Entry = entry;
        if (entry is not { IsFile: true, File: { } latest })
        {
            Versions.Clear();
            File = null;
            Image = null;
            ImageInfo = "";
            PreviewMessage = "";
            MetadataText = "";
            RaisePropertiesChanged(nameof(BadgeText), nameof(IsShowingLatest));
            return;
        }
        bool sameName = Versions.Count > 0 && Versions[0].File.Filename == latest.Filename;
        GridFsFile shown = sameName && _file is { } current && current.Filename == latest.Filename ? current : latest;
        try
        {
            IReadOnlyList<GridFsFile> versions = await _owner.Service.VersionsAsync(latest.Filename, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            if (versions.Count == 0)
            {
                versions = [latest];
            }
            if (versions.All(v => v.Id != shown.Id))
            {
                shown = versions[0];
            }
            else
            {
                shown = versions.First(v => v.Id == shown.Id);
            }
            SetVersions(versions, shown);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            SetVersions([latest], latest);
            shown = latest;
        }
        await ShowFileAsync(shown).ConfigureAwait(true);
    }

    private void SetVersions(IReadOnlyList<GridFsFile> versions, GridFsFile shown)
    {
        Versions.Clear();
        for (int i = 0; i < versions.Count; i++)
        {
            Versions.Add(new(versions[i], versions.Count - i, i == 0, versions[i].Id == shown.Id));
        }
    }

    /// <summary>切到某一份(版本列表里点了 v2)。</summary>
    private async Task ShowFileAsync(GridFsFile file)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsEditing = false;
        File = file;
        if (Versions.Count > 0)
        {
            List<GridFsVersionRow> rows = [.. Versions];
            Versions.Clear();
            foreach (GridFsVersionRow row in rows)
            {
                Versions.Add(row with { IsShown = row.File.Id == file.Id });
            }
        }
        RaisePropertiesChanged(nameof(BadgeText), nameof(IsShowingLatest));
        MetadataText = file.Metadata is { } meta ? BsonText.Pretty(meta) : "{}";
        await LoadPreviewAsync(file, cts.Token).ConfigureAwait(true);
    }

    /// <summary>
    /// 图片预览:image/* 且不超过 8 MB 才读(整块进内存),解码放到线程池上。
    /// 解不出来(假字节、不支持的格式)就老实说「无法预览」,布局不塌。
    /// </summary>
    private async Task LoadPreviewAsync(GridFsFile file, CancellationToken cancellationToken)
    {
        string type = file.ContentType ?? GridFsPaths.GuessContentType(file.Filename);
        string format = FormatName(type, file.Filename);
        if (GridFsPaths.Categorize(file.Filename, type) != GridFsCategory.Image)
        {
            Image = null;
            ImageInfo = type;
            PreviewMessage = Loc["Fs_NoPreview"];
            return;
        }
        if (file.Length > GridFsService.PreviewLimit)
        {
            Image = null;
            ImageInfo = format;
            PreviewMessage = Loc["Fs_PreviewTooLarge"];
            return;
        }
        Image = null;
        PreviewMessage = Loc["Fs_PreviewLoading"];
        IsPreviewLoading = true;
        try
        {
            byte[] bytes = await _owner.Service.ReadAllAsync(file.Id, cancellationToken).ConfigureAwait(true);
            Bitmap? bitmap = await Task.Run(() => GridFsTabViewModel.Decode(bytes), cancellationToken).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }
            Image = bitmap;
            ImageInfo = bitmap is null ? $"— · {format}" : $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} · {format}";
            PreviewMessage = bitmap is null ? Loc["Fs_NoPreview"] : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Image = null;
            ImageInfo = format;
            PreviewMessage = Loc["Fs_NoPreview"];
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>「JPEG」「PNG」「SVG」:contentType 的子类型大写;没有就用扩展名。</summary>
    internal static string FormatName(string contentType, string filename)
    {
        int slash = contentType.IndexOf('/');
        string sub = slash >= 0 ? contentType[(slash + 1)..] : Path.GetExtension(filename).TrimStart('.');
        int plus = sub.IndexOf('+');
        if (plus > 0)
        {
            sub = sub[..plus];
        }
        return sub.ToUpperInvariant();
    }

    private void Zoom()
    {
        if (_image is { } image && _file is { } file)
        {
            _owner.Workspace.ShowDialog(new GridFsImageDialogViewModel(_owner, file, image, ImageInfo));
        }
    }

    private void BeginEdit()
    {
        if (_file is null)
        {
            return;
        }
        MetadataDraft = _file.Metadata is { } meta ? BsonText.Pretty(meta) : "{\n  \n}";
        MetadataError = null;
        IsEditing = true;
    }

    /// <summary>保存 metadata:解析草稿 → 过写护栏 → updateOne → 重读这一份并刷新列表(contentType 可能变了)。</summary>
    private async Task SaveMetadataAsync()
    {
        if (_file is not { } file)
        {
            return;
        }
        if (!ShellJson.TryParseDocument(_metadataDraft, out BsonDocument parsed, out string? error))
        {
            MetadataError = Loc.Format("Fs_MetadataInvalid", error);
            return;
        }
        if (!_owner.Workspace.EnsureWritable(_owner.Database))
        {
            return;
        }
        try
        {
            await _owner.Service.UpdateMetadataAsync(file.Id, parsed.ElementCount == 0 ? null : parsed).ConfigureAwait(true);
            _owner.Workspace.Toast(new() { Title = Loc["Fs_MetadataSaved"], Kind = ToastKind.Success });
            IsEditing = false;
            if (await _owner.Service.GetAsync(file.Id).ConfigureAwait(true) is { } fresh)
            {
                List<GridFsVersionRow> rows = [.. Versions];
                Versions.Clear();
                foreach (GridFsVersionRow row in rows)
                {
                    Versions.Add(row.File.Id == fresh.Id ? row with { File = fresh } : row);
                }
                _file = null;
                await ShowFileAsync(fresh).ConfigureAwait(true);
            }
            await _owner.ReloadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            MetadataError = Loc.Format("Common_Failed", MongoConnector.Describe(ex));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _loadCts?.Cancel();
        Image = null;
    }
}
