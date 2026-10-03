using System.Collections.ObjectModel;
using Avalonia.Threading;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>文件表可排序的列。</summary>
internal enum GridFsSort
{
    /// <summary>文件名。</summary>
    Name,

    /// <summary>大小。</summary>
    Size,

    /// <summary>contentType。</summary>
    Type,

    /// <summary>上传时间。</summary>
    Uploaded,

    /// <summary>块数。</summary>
    Chunks
}

/// <summary>
/// GridFS 文件管理(设计稿 06):36px 工具行 / 32px 路径行 / 文件表 + 拖放区 | 320px 详情 / 32px 传输底栏。
/// <para>
/// 列目录、搜索、类型芯片全在服务器端做(见 <see cref="GridFsService.ListAsync" />),
/// 界面只持有当前这一层;传输走一条串行队列,大文件全程流式。
/// 本文件管"看":目录、排序、勾选、状态行;"动"(上传、下载、改名、删除、孤儿块、新桶)见 <c>.Actions.cs</c>。
/// </para>
/// </summary>
internal sealed partial class GridFsTabViewModel : WorkspaceTab
{
    private readonly GridFsService _service;
    private readonly DispatcherTimer _searchDelay;
    private CancellationTokenSource? _loadCts;
    private List<GridFsEntry> _listed = [];
    private string _prefix = "";
    private bool _virtualDirs = true;
    private GridFsTypeFilter _typeFilter = GridFsTypeFilter.All;
    private string _searchText = "";
    private GridFsSort _sort = GridFsSort.Uploaded;
    private bool _sortDescending = true;
    private bool _isGridView;
    private GridFsEntry? _selectedEntry;
    private bool _isLoading;
    private bool _truncated;
    private string? _loadError;
    private IReadOnlyList<GridFsCrumb> _crumbs = [];
    private bool _bulkChecking;
    private bool _applying;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="bucket">桶。</param>
    public GridFsTabViewModel(IMongoWorkspace workspace, GridFsBucketInfo bucket)
        : base(workspace)
    {
        Bucket = bucket;
        Title = bucket.Name;
        Scope = "@" + bucket.Database;
        _service = new GridFsService(workspace.Connection, bucket);
        Details = new GridFsDetailsViewModel(this);
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            _ = ReloadAsync();
        };
        _transferTicker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _transferTicker.Tick += (_, _) => UpdateTransferProgress();

        NavigateCommand = new(prefix => _ = NavigateAsync(prefix));
        UpCommand = new(() => _ = NavigateAsync(GridFsPaths.Parent(_prefix)), () => _prefix.Length > 0);
        OpenEntryCommand = new(OpenEntryAsync);
        SortCommand = new(SortBy);
        InitializeActions();
        UpdateCrumbs();
        UpdateStatus();
    }

    /// <summary>桶。</summary>
    public GridFsBucketInfo Bucket { get; }

    /// <summary>所在库。</summary>
    public string Database => Bucket.Database;

    /// <inheritdoc />
    public override TabKind Kind => TabKind.GridFs;

    /// <inheritdoc />
    public override string Key => $"gridfs:{Bucket.Database}.{Bucket.Name}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.hard-drive";

    /// <inheritdoc />
    public override string IconToken => "VelaWarning";

    /// <summary>服务(详情面板与对话框共用)。</summary>
    internal GridFsService Service => _service;

    /// <summary>文件表的行(「..」、目录、文件、上传中)。</summary>
    public ObservableCollection<GridFsEntry> Entries { get; } = [];

    /// <summary>右侧详情。</summary>
    public GridFsDetailsViewModel Details { get; }

    /// <summary>面包屑(当前目录逐级)。</summary>
    public IReadOnlyList<GridFsCrumb> Crumbs
    {
        get => _crumbs;
        private set => SetProperty(ref _crumbs, value);
    }

    /// <summary>跳到某一级。</summary>
    public RelayCommand<string> NavigateCommand { get; }

    /// <summary>上一级。</summary>
    public RelayCommand UpCommand { get; }

    /// <summary>打开一行(目录进入、「..」返回、文件外部打开)。</summary>
    public AsyncCommand<GridFsEntry> OpenEntryCommand { get; }

    /// <summary>点列头排序。</summary>
    public RelayCommand<string> SortCommand { get; }

    // ── 目录与筛选 ─────────────────────────────────────────────────────────

    /// <summary>当前虚拟目录(带结尾斜杠;根为空串)。</summary>
    public string Prefix => _prefix;

    /// <summary>当前目录给人看的写法(根是 <c>/</c>)。</summary>
    public string PrefixText => _prefix.Length == 0 ? "/" : _prefix;

    /// <summary>实际列的前缀:关掉虚拟目录时平铺整个桶。</summary>
    private string ListPrefix => _virtualDirs ? _prefix : "";

    /// <summary>按 <c>/</c> 显示为虚拟目录。</summary>
    public bool VirtualDirs
    {
        get => _virtualDirs;
        set
        {
            if (SetProperty(ref _virtualDirs, value))
            {
                RaisePropertiesChanged(nameof(ShowCrumbs), nameof(UploadTarget), nameof(DropText));
                UpdateCrumbs();
                _ = ReloadAsync();
            }
        }
    }

    /// <summary>面包屑可见(平铺时没有"当前目录"可言)。</summary>
    public bool ShowCrumbs => _virtualDirs;

    /// <summary>类型芯片。</summary>
    public GridFsTypeFilter TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetProperty(ref _typeFilter, value))
            {
                RaisePropertiesChanged(nameof(IsAllTypes), nameof(IsImages), nameof(IsDocuments), nameof(IsVideos));
                _ = ReloadAsync();
            }
        }
    }

    /// <summary>芯片「全部」。</summary>
    public bool IsAllTypes
    {
        get => _typeFilter == GridFsTypeFilter.All;
        set
        {
            if (value)
            {
                TypeFilter = GridFsTypeFilter.All;
            }
        }
    }

    /// <summary>芯片「图片」。</summary>
    public bool IsImages
    {
        get => _typeFilter == GridFsTypeFilter.Images;
        set
        {
            if (value)
            {
                TypeFilter = GridFsTypeFilter.Images;
            }
        }
    }

    /// <summary>芯片「文档」。</summary>
    public bool IsDocuments
    {
        get => _typeFilter == GridFsTypeFilter.Documents;
        set
        {
            if (value)
            {
                TypeFilter = GridFsTypeFilter.Documents;
            }
        }
    }

    /// <summary>芯片「视频」。</summary>
    public bool IsVideos
    {
        get => _typeFilter == GridFsTypeFilter.Videos;
        set
        {
            if (value)
            {
                TypeFilter = GridFsTypeFilter.Videos;
            }
        }
    }

    /// <summary>搜索框(停手 300 ms 后到服务器上查)。</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                _searchDelay.Stop();
                _searchDelay.Start();
            }
        }
    }

    /// <summary>正在搜索(结果平铺,名字是相对当前目录的路径)。</summary>
    public bool IsSearching => _searchText.Trim().Length > 0;

    /// <summary>列表视图。</summary>
    public bool IsListView
    {
        get => !_isGridView;
        set
        {
            if (value)
            {
                IsGridView = false;
            }
        }
    }

    /// <summary>缩略图视图。</summary>
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (SetProperty(ref _isGridView, value))
            {
                RaisePropertyChanged(nameof(IsListView));
                if (value)
                {
                    _ = LoadThumbnailsAsync();
                }
            }
        }
    }

    // ── 排序 ───────────────────────────────────────────────────────────────

    /// <summary>排序列。</summary>
    public GridFsSort Sort => _sort;

    /// <summary>倒序。</summary>
    public bool SortDescending => _sortDescending;

    /// <summary>列头箭头的图标(<c>arrow-down</c> / <c>arrow-up</c>)。</summary>
    public string SortIcon => _sortDescending ? "Mongo.arrow-down" : "Mongo.arrow-up";

    private void SortBy(string column)
    {
        if (!Enum.TryParse(column, out GridFsSort sort))
        {
            return;
        }
        if (sort == _sort)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sort = sort;
            // 名字与类型默认正序;大小、时间、块数默认大的在前 —— 点一下就是人最常要的那个方向。
            _sortDescending = sort is GridFsSort.Size or GridFsSort.Uploaded or GridFsSort.Chunks;
        }
        RaisePropertiesChanged(nameof(Sort), nameof(SortDescending), nameof(SortIcon));
        ApplyEntries();
    }

    private IEnumerable<GridFsEntry> Sorted(IEnumerable<GridFsEntry> entries)
    {
        // 目录永远在文件前面(资源管理器的习惯);目录没有类型与块数,按这两列排时目录按名字排。
        IEnumerable<GridFsEntry> folders = entries.Where(static e => e.IsFolder);
        IEnumerable<GridFsEntry> files = entries.Where(static e => !e.IsFolder);
        folders = _sort switch
        {
            GridFsSort.Size => Order(folders, static e => e.Size),
            GridFsSort.Uploaded => Order(folders, static e => e.Uploaded ?? DateTime.MinValue),
            _ => folders.OrderBy(static e => e.Name, StringComparer.OrdinalIgnoreCase)
        };
        files = _sort switch
        {
            GridFsSort.Name => Order(files, static e => e.Name, StringComparer.OrdinalIgnoreCase),
            GridFsSort.Size => Order(files, static e => e.Size),
            GridFsSort.Type => Order(files, static e => e.ContentType, StringComparer.OrdinalIgnoreCase),
            GridFsSort.Chunks => Order(files, static e => e.File?.ChunkCount ?? 0),
            _ => Order(files, static e => e.Uploaded ?? DateTime.MinValue)
        };
        return folders.Concat(files);
    }

    private IOrderedEnumerable<GridFsEntry> Order<TKey>(IEnumerable<GridFsEntry> source, Func<GridFsEntry, TKey> key, IComparer<TKey>? comparer = null) =>
        _sortDescending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    // ── 选择与勾选 ─────────────────────────────────────────────────────────

    /// <summary>选中的行(右侧详情跟着它)。</summary>
    public GridFsEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            // 重排列表时 ListBox 会先把选中置空再由 ApplyEntries 指回去 —— 那一下不该让详情闪一次空。
            if (_applying)
            {
                return;
            }
            if (SetProperty(ref _selectedEntry, value))
            {
                _ = Details.ShowAsync(value is { IsFile: true } or { IsFolder: true } ? value : null);
                RaiseSelectionChanged();
            }
        }
    }

    /// <summary>
    /// 表头的全选框:全勾 = true、全不勾 = false、部分 = null(三态)。
    /// 设成 true / false 即全勾 / 全清。
    /// </summary>
    public bool? AllChecked
    {
        get
        {
            int checkable = Entries.Count(static e => e.CanCheck);
            int checkedCount = Entries.Count(static e => e.IsChecked);
            return checkedCount == 0 ? false : checkedCount == checkable ? true : null;
        }
        set
        {
            bool on = value ?? false;
            _bulkChecking = true;
            try
            {
                foreach (GridFsEntry entry in Entries.Where(static e => e.CanCheck))
                {
                    entry.IsChecked = on;
                }
            }
            finally
            {
                _bulkChecking = false;
            }
            RaiseSelectionChanged();
        }
    }

    /// <summary>
    /// 操作对象:勾了就是勾选的那些,没勾就是选中的那一行。
    /// 与资源管理器一致 —— 勾选是"批量",单击是"这一个"。
    /// </summary>
    internal IReadOnlyList<GridFsEntry> Targets
    {
        get
        {
            List<GridFsEntry> checkedEntries = [.. Entries.Where(static e => e.IsChecked)];
            if (checkedEntries.Count > 0)
            {
                return checkedEntries;
            }
            return _selectedEntry is { CanCheck: true } selected ? [selected] : [];
        }
    }

    /// <summary>有操作对象(下载 / 改名 / 删除可用)。</summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>底栏右侧「已选 2 项 · 1.9 MB」;没有为空。</summary>
    public string SelectionSummary
    {
        get
        {
            IReadOnlyList<GridFsEntry> targets = Targets;
            return targets.Count == 0 ? "" : Loc.Format("Fs_Selected", targets.Count, BsonText.Bytes(targets.Sum(static e => e.Size)));
        }
    }

    /// <summary>底栏最右「9 项 · 61.9 MB」。</summary>
    public string ListingSummary
    {
        get
        {
            string count = BsonText.Grouped(_listed.Count) + (_truncated ? "+" : "");
            return Loc.Format("Fs_Items", count, BsonText.Bytes(_listed.Sum(static e => e.Size)));
        }
    }

    private void OnEntryChecked(GridFsEntry entry)
    {
        if (!_bulkChecking)
        {
            RaiseSelectionChanged();
        }
    }

    private void RaiseSelectionChanged()
    {
        RaisePropertiesChanged(nameof(AllChecked), nameof(SelectionSummary), nameof(HasTargets));
        DownloadCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    // ── 加载 ───────────────────────────────────────────────────────────────

    /// <summary>正在加载。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaisePropertiesChanged(nameof(EmptyText), nameof(HasEmptyText));
            }
        }
    }

    /// <summary>加载失败的原因;成功为 <see langword="null" />。</summary>
    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
            {
                RaisePropertiesChanged(nameof(EmptyText), nameof(HasEmptyText));
            }
        }
    }

    /// <summary>被截断了(超过 <see cref="GridFsService.ListLimit" /> 项)。</summary>
    public bool IsTruncated
    {
        get => _truncated;
        private set => SetProperty(ref _truncated, value);
    }

    /// <summary>截断提示。</summary>
    public string TruncatedText => Loc.Format("Fs_Truncated", BsonText.Grouped(GridFsService.ListLimit));

    /// <summary>空态文字(加载中 / 失败 / 空目录 / 搜索无结果);有内容时为空。</summary>
    public string EmptyText =>
        _isLoading && _listed.Count == 0 ? Loc["Common_Loading"]
        : _loadError is { } error ? Loc.Format("Common_Failed", error)
        : _listed.Count > 0 ? ""
        : IsSearching ? Loc.Format("Fs_NoMatch", _searchText.Trim())
        : Loc["Fs_Empty"];

    /// <summary>有空态要显示。</summary>
    public bool HasEmptyText => EmptyText.Length > 0;

    /// <inheritdoc />
    public override Task LoadAsync() => ReloadAsync();

    /// <inheritdoc />
    public override Task RefreshAsync() => ReloadAsync();

    /// <summary>
    /// 重列当前目录。新的一次会取消上一次 —— 搜索框连敲、芯片连点时,
    /// 只有最后一次的结果会落到界面上,不会出现旧结果晚到覆盖新结果。
    /// </summary>
    public async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        try
        {
            string listPrefix = ListPrefix;
            GridFsListing listing = await _service.ListAsync(listPrefix, _virtualDirs, _typeFilter, _searchText, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            var listed = new List<GridFsEntry>(listing.Items.Count);
            foreach (GridFsListItem item in listing.Items)
            {
                GridFsEntry entry = item.IsFolder
                    ? GridFsEntry.Folder(item.Folder!.Length == 0 ? "/" : item.Folder, listPrefix + item.Folder + "/", item.Files, item.Bytes, item.LastUpload)
                    : GridFsEntry.ForFile(Relative(item.Latest!.Filename, listPrefix), item.Latest, item.Versions);
                listed.Add(entry);
            }
            _listed = listed;
            IsTruncated = listing.Truncated;
            LoadError = null;
            ApplyEntries();
            if (_isGridView)
            {
                _ = LoadThumbnailsAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            if (!cts.IsCancellationRequested)
            {
                LoadError = MongoConnector.Describe(ex);
                _listed = [];
                ApplyEntries();
                Workspace.Toast(new() { Title = Loc.Format("Common_Failed", LoadError), Kind = ToastKind.Error });
            }
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                IsLoading = false;
                RaisePropertiesChanged(nameof(EmptyText), nameof(HasEmptyText));
            }
        }
    }

    private static string Relative(string filename, string prefix) =>
        prefix.Length > 0 && filename.StartsWith(prefix, StringComparison.Ordinal) ? filename[prefix.Length..] : filename;

    /// <summary>
    /// 把已列出的项 + 「..」+ 落在当前目录的上传行排好放进 <see cref="Entries" />,
    /// 并按路径恢复勾选与选中 —— 刷新、排序、上传完一个文件都不该把用户的选择丢掉。
    /// </summary>
    private void ApplyEntries()
    {
        HashSet<string> checkedPaths = [.. Entries.Where(static e => e.IsChecked).Select(static e => e.Path)];
        GridFsEntry? previous = _selectedEntry;
        string? selectedPath = _reselectPath ?? previous?.Path;
        _reselectPath = null;
        foreach (GridFsEntry old in Entries)
        {
            old.CheckedChanged -= OnEntryChecked;
        }

        var rows = new List<GridFsEntry>();
        if (_virtualDirs && _prefix.Length > 0 && !IsSearching)
        {
            rows.Add(GridFsEntry.Parent(GridFsPaths.Parent(_prefix)));
        }
        rows.AddRange(Sorted(_listed));
        rows.AddRange(_pendingRows.Where(ShowsUploadRow));

        _bulkChecking = true;
        _applying = true;
        try
        {
            Entries.Clear();
            foreach (GridFsEntry row in rows)
            {
                if (checkedPaths.Contains(row.Path) && row.CanCheck)
                {
                    row.IsChecked = true;
                }
                row.CheckedChanged += OnEntryChecked;
                Entries.Add(row);
            }
        }
        finally
        {
            _bulkChecking = false;
            _applying = false;
        }

        GridFsEntry? reselected = selectedPath is null ? null : rows.FirstOrDefault(r => r.CanCheck && r.Path == selectedPath);
        _selectedEntry = reselected;
        RaisePropertyChanged(nameof(SelectedEntry));
        // 排序只是换了顺序(同一批对象);刷新则是新对象 —— 只有内容真的变了(多了版本、换了最新一份)才重载详情,
        // 不然每上传完一个文件、每点一次列头,右边的预览都要重新从库里读一遍。
        bool unchanged = previous is not null && reselected is not null
                         && previous.Kind == reselected.Kind
                         && Equals(previous.File?.Id, reselected.File?.Id)
                         && previous.Versions == reselected.Versions
                         && previous.FileCount == reselected.FileCount
                         && previous.Size == reselected.Size;
        if (!unchanged)
        {
            _ = Details.ShowAsync(reselected);
        }
        RaisePropertiesChanged(nameof(ListingSummary), nameof(EmptyText), nameof(HasEmptyText));
        RaiseSelectionChanged();
        UpdateStatus();
    }

    /// <summary>上传行是不是该出现在当前这一页。</summary>
    private bool ShowsUploadRow(GridFsEntry row) =>
        !IsSearching && (_virtualDirs
            ? GridFsPaths.DirectoryOf(row.Path) == _prefix
            : row.Path.StartsWith(ListPrefix, StringComparison.Ordinal));

    // ── 导航 ───────────────────────────────────────────────────────────────

    /// <summary>跳到一个虚拟目录(清空搜索与勾选)。</summary>
    public async Task NavigateAsync(string? prefix)
    {
        string target = GridFsPaths.NormalizePrefix(prefix);
        foreach (GridFsEntry entry in Entries)
        {
            entry.IsChecked = false;
        }
        _prefix = target;
        _searchDelay.Stop();
        if (_searchText.Length > 0)
        {
            _searchText = "";
            RaisePropertyChanged(nameof(SearchText));
        }
        if (!_virtualDirs)
        {
            _virtualDirs = true;
            RaisePropertiesChanged(nameof(VirtualDirs), nameof(ShowCrumbs));
        }
        RaisePropertiesChanged(nameof(Prefix), nameof(PrefixText), nameof(UploadTarget), nameof(DropText));
        UpCommand.RaiseCanExecuteChanged();
        UpdateCrumbs();
        _listed = [];
        ApplyEntries();
        await ReloadAsync().ConfigureAwait(true);
    }

    private async Task OpenEntryAsync(GridFsEntry entry)
    {
        switch (entry.Kind)
        {
            case GridFsEntryKind.Parent:
                await NavigateAsync(entry.Path).ConfigureAwait(true);
                break;
            case GridFsEntryKind.Folder:
                await NavigateAsync(entry.Path).ConfigureAwait(true);
                break;
            case GridFsEntryKind.File when entry.File is { } file:
                await OpenExternalAsync(file).ConfigureAwait(true);
                break;
        }
    }

    private void UpdateCrumbs()
    {
        var crumbs = new List<GridFsCrumb>();
        if (_virtualDirs && _prefix.Length > 0)
        {
            string[] parts = _prefix.TrimEnd('/').Split('/');
            string path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                path += parts[i] + "/";
                crumbs.Add(new(parts[i].Length == 0 ? "/" : parts[i], path, i == parts.Length - 1));
            }
        }
        Crumbs = crumbs;
    }

    /// <summary>
    /// 宿主状态栏那一行:<c>shop.fs · 9 项 · products/SKU-7710/</c>(设计稿 06 底部)。
    /// </summary>
    private void UpdateStatus() =>
        StatusText = Loc.Format("Fs_Status", $"{Bucket.Database}.{Bucket.Name}", BsonText.Grouped(_listed.Count),
            _virtualDirs ? PrefixText : Loc["Fs_Flat"]);

    /// <inheritdoc />
    public override void Dispose()
    {
        _loadCts?.Cancel();
        _searchDelay.Stop();
        _transferTicker.Stop();
        _transferCts?.Cancel();
        _thumbCts?.Cancel();
        Details.Dispose();
        base.Dispose();
    }
}
