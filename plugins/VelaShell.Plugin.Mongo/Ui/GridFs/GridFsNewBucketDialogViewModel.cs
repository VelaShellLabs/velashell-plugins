using Avalonia.Controls;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 「新建存储桶」对话框:一个名字 → <c>&lt;name&gt;.files</c> + <c>&lt;name&gt;.chunks</c> 两个集合与两条标准索引,
/// 建完刷新对象树并直接打开新桶。框里把要建的东西逐条列出 —— 新建桶在库里是"两个集合",
/// 用户该知道对象树里会多出什么。
/// </summary>
internal sealed class GridFsNewBucketDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly string _database;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">在哪个库里建。</param>
    public GridFsNewBucketDialogViewModel(IMongoWorkspace workspace, string database)
        : base(workspace)
    {
        _database = database;
        Title = Loc["Fs_NewBucketTitle"];
        Subtitle = database;
        CreateCommand = new AsyncCommand(CreateAsync, () => GridFsPaths.ValidateBucketName(Name) is null);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.folder-plus";

    /// <inheritdoc />
    public override string IconToken => "VelaWarning";

    /// <inheritdoc />
    public override double Width => 440;

    /// <summary>桶名。</summary>
    public string Name
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                string? key = field.Length == 0 ? null : GridFsPaths.ValidateBucketName(field);
                Error = key is null ? null : Loc[key];
                RaisePropertiesChanged(nameof(FilesName), nameof(ChunksName));
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    } = "";

    /// <summary>预览:<c>images.files</c>。</summary>
    public string FilesName => (Name.Trim().Length == 0 ? "…" : Name.Trim()) + ".files";

    /// <summary>预览:<c>images.chunks</c>。</summary>
    public string ChunksName => (Name.Trim().Length == 0 ? "…" : Name.Trim()) + ".chunks";

    /// <summary>错误。</summary>
    public string? Error
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有错误。</summary>
    public bool HasError => Error is not null;

    /// <summary>新建。</summary>
    public AsyncCommand CreateCommand { get; }

    private async Task CreateAsync()
    {
        string name = Name.Trim();
        if (GridFsPaths.ValidateBucketName(name) is { } invalid)
        {
            Error = Loc[invalid];
            return;
        }
        if (!Workspace.EnsureWritable(_database))
        {
            return;
        }
        try
        {
            IReadOnlyList<CollectionInfo> existing = await Workspace.Connection.ListCollectionsAsync(_database).ConfigureAwait(true);
            if (existing.Any(c => c.Name == name + ".files" || c.Name == name + ".chunks"))
            {
                Error = Loc["Fs_BucketExists"];
                return;
            }
            await GridFsService.CreateBucketAsync(Workspace.Connection, _database, name).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Error = Loc.Format("Common_Failed", MongoConnector.Describe(ex));
            return;
        }
        Close();
        Workspace.Toast(new() { Title = Loc.Format("Fs_BucketCreated", $"{_database}.{name}"), Kind = ToastKind.Success });
        await Workspace.RefreshTreeAsync(_database).ConfigureAwait(true);
        Workspace.OpenGridFs(_database, name);
    }

    /// <inheritdoc />
    public Control CreateView() => new GridFsNewBucketDialogView(this);
}
