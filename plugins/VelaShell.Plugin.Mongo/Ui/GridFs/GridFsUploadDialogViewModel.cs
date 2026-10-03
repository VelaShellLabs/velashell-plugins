using Avalonia.Controls;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>上传对话框的结果:落到哪个目录、用什么 metadata 模板。</summary>
/// <param name="Prefix">目标前缀(带结尾斜杠;根为空串)。</param>
/// <param name="Metadata">metadata 模板(没写 contentType 的文件按扩展名补)。</param>
internal sealed record GridFsUploadPlan(string Prefix, BsonDocument Metadata);

/// <summary>
/// 「上传到 GridFS」小对话框(设计稿 06 拖放区那句"可在上传前编辑 metadata")。
/// <para>
/// 一批文件共用一份 metadata 模板,默认带 contentType(全批同一种时)与 uploader;
/// 模板里不写 contentType,就按每个文件的扩展名逐个补 —— 一次拖进来图片和 PDF 混在一起是常态。
/// </para>
/// </summary>
internal sealed class GridFsUploadDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly TaskCompletionSource<GridFsUploadPlan?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _target;
    private string _metadataText;
    private string? _error;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="bucket">桶。</param>
    /// <param name="prefix">默认目标目录。</param>
    /// <param name="items">要上传的文件。</param>
    /// <param name="metadata">默认 metadata 模板。</param>
    public GridFsUploadDialogViewModel(IMongoWorkspace workspace, GridFsBucketInfo bucket, string prefix,
        IReadOnlyList<GridFsUploadItem> items, BsonDocument metadata)
        : base(workspace)
    {
        Items = items;
        Title = Loc["Fs_UploadTitle"];
        Subtitle = $"{bucket.Database}.{bucket.Name}";
        _target = prefix;
        _metadataText = BsonText.Pretty(metadata);
        ConfirmCommand = new RelayCommand(Confirm, () => _error is null);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.upload";

    /// <inheritdoc />
    public override double Width => 480;

    /// <summary>要上传的文件。</summary>
    public IReadOnlyList<GridFsUploadItem> Items { get; }

    /// <summary>「3 个文件 · 4.2 MB」。</summary>
    public string Summary => Loc.Format("Fs_UploadFiles", Items.Count, BsonText.Bytes(Items.Sum(static i => i.Size)));

    /// <summary>列出的前几个文件名。</summary>
    public IReadOnlyList<string> Preview => [.. Items.Take(5).Select(static i => i.Relative)];

    /// <summary>「…还有 12 个」;不足 6 个为空。</summary>
    public string More => Items.Count > 5 ? Loc.Format("Fs_UploadMore", Items.Count - 5) : "";

    /// <summary>目标目录(可改;空即根)。</summary>
    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value ?? "");
    }

    /// <summary>metadata 模板文本(mongosh 写法)。</summary>
    public string MetadataText
    {
        get => _metadataText;
        set
        {
            if (SetProperty(ref _metadataText, value ?? ""))
            {
                Error = ShellJson.TryParseDocument(_metadataText, out _, out string? error) ? null : Loc.Format("Fs_MetadataInvalid", error);
            }
        }
    }

    /// <summary>metadata 解析错误。</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                RaisePropertyChanged(nameof(HasError));
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>有错误。</summary>
    public bool HasError => _error is not null;

    /// <summary>确认按钮文字「上传 3 个文件」。</summary>
    public string ConfirmLabel => Loc.Format("Fs_UploadConfirm", Items.Count);

    /// <summary>上传。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>结果;取消为 <see langword="null" />。</summary>
    public Task<GridFsUploadPlan?> Result => _result.Task;

    private void Confirm()
    {
        if (!ShellJson.TryParseDocument(_metadataText, out BsonDocument metadata, out string? error))
        {
            Error = Loc.Format("Fs_MetadataInvalid", error);
            return;
        }
        _result.TrySetResult(new(GridFsPaths.NormalizePrefix(_target), metadata));
        Close();
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        _result.TrySetResult(null);
        base.OnClosed();
    }

    /// <inheritdoc />
    public Control CreateView() => new GridFsUploadDialogView(this);
}
