using Avalonia.Controls;
using Avalonia.Media.Imaging;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>「缩放预览」:大图看一眼,适应窗口 / 100% 两档。图是详情里已经解出来的那张,不再读一遍库。</summary>
internal sealed class GridFsImageDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly GridFsTabViewModel _owner;

    /// <summary>构造。</summary>
    /// <param name="owner">所属标签页(下载走它的传输队列)。</param>
    /// <param name="file">这一份。</param>
    /// <param name="image">已解码的图。</param>
    /// <param name="info">「1600 × 1600 · JPEG」。</param>
    public GridFsImageDialogViewModel(GridFsTabViewModel owner, GridFsFile file, Bitmap image, string info)
        : base(owner.Workspace)
    {
        _owner = owner;
        File = file;
        Image = image;
        Title = file.BaseName;
        Subtitle = file.Filename;
        Info = $"{info} · {BsonText.Bytes(file.Length)}";
        DownloadCommand = new AsyncCommand(() => _owner.DownloadFileAsync(File));
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.file-image";

    /// <inheritdoc />
    public override string IconToken => "VelaShellMagenta";

    /// <inheritdoc />
    public override double Width => 900;

    /// <inheritdoc />
    public override double Height => 640;

    /// <summary>这一份。</summary>
    public GridFsFile File { get; }

    /// <summary>图。</summary>
    public Bitmap Image { get; }

    /// <summary>尺寸 · 格式 · 大小。</summary>
    public string Info { get; }

    /// <summary>适应窗口。</summary>
    public bool IsFit
    {
        get => !IsActualSize;
        set
        {
            if (value)
            {
                IsActualSize = false;
            }
        }
    }

    /// <summary>100%(原始像素,可滚动)。</summary>
    public bool IsActualSize
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(IsFit));
            }
        }
    }

    /// <summary>下载。</summary>
    public AsyncCommand DownloadCommand { get; }

    /// <inheritdoc />
    public Control CreateView() => new GridFsImageDialogView(this);
}
