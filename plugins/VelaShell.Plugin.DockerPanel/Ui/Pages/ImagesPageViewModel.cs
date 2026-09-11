using Avalonia.Controls;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>镜像页的筛选档。</summary>
public enum ImageFilter
{
    /// <summary>全部。</summary>
    All,

    /// <summary>被容器使用中。</summary>
    Used,

    /// <summary>没有容器在用。</summary>
    Unused,

    /// <summary>悬空(没有标签)。</summary>
    Dangling
}

/// <summary>镜像页。</summary>
public sealed class ImagesPageViewModel : PageViewModel
{
    private readonly List<ImageRow> _all = [];

    /// <summary>建镜像页。</summary>
    public ImagesPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        SetFilterCommand = new RelayCommand(p =>
        {
            if (p is ImageFilter filter)
            {
                Filter = filter;
            }
        });
        PullCommand = new RelayCommand(_ => Shell.ShowPullDialogAsync(null));
        RunCommand = new RelayCommand(p => p is ImageRow row
            ? Shell.ShowRunContainerAsync($"{row.Repository}:{row.Tag}")
            : Task.CompletedTask);
        TagCommand = new RelayCommand(p => p is ImageRow row ? TagAsync(row) : Task.CompletedTask);
        PushCommand = new RelayCommand(p => p is ImageRow row ? PushAsync(row) : Task.CompletedTask);
        RemoveCommand = new RelayCommand(p => RemoveAsync(Targets(p)));
        PruneDanglingCommand = new RelayCommand(_ => PruneAsync(danglingOnly: true));
        PruneAllCommand = new RelayCommand(_ => PruneAsync(danglingOnly: false));
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection());
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
        OpenDetailCommand = new RelayCommand(p => p is ImageRow row ? OpenDetailAsync(row) : Task.CompletedTask);
        CloseDetailCommand = new RelayCommand(_ => CloseDetail());
        SaveCommand = new RelayCommand(p => SaveAsync(Targets(p)));
        LoadCommand = new RelayCommand(_ => LoadAsync());
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Images;

    /// <inheritdoc />
    public override string Title => "镜像";

    /// <summary>过滤后的行。</summary>
    public KeyedCollection<ImageRow> View { get; } = new(r => r.Id);

    /// <summary>当前筛选。</summary>
    public ImageFilter Filter
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsAll), nameof(IsUsed), nameof(IsUnused), nameof(IsDangling));
                ApplyView();
            }
        }
    } = ImageFilter.All;

    /// <summary>筛选:全部。</summary>
    public bool IsAll => Filter == ImageFilter.All;

    /// <summary>筛选:使用中。</summary>
    public bool IsUsed => Filter == ImageFilter.Used;

    /// <summary>筛选:未使用。</summary>
    public bool IsUnused => Filter == ImageFilter.Unused;

    /// <summary>筛选:悬空。</summary>
    public bool IsDangling => Filter == ImageFilter.Dangling;

    /// <summary>搜索词。</summary>
    public string Search
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyView();
            }
        }
    } = "";

    /// <summary>总数。</summary>
    public int TotalCount => _all.Count;

    /// <summary>使用中的数量。</summary>
    public int UsedCount => _all.Count(r => r.Summary.Containers > 0);

    /// <summary>未使用的数量。</summary>
    public int UnusedCount => _all.Count(r => r.Summary.Containers <= 0 && !r.IsDangling);

    /// <summary>悬空数量。</summary>
    public int DanglingCount => _all.Count(r => r.IsDangling);

    /// <summary>已勾选数量。</summary>
    public int SelectedCount
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(HasSelection), nameof(SelectionText));
            }
        }
    }

    /// <summary>有勾选。</summary>
    public bool HasSelection => SelectedCount > 0;

    /// <summary>选中条文字。</summary>
    public string SelectionText => $"已选 {SelectedCount} 个镜像";

    /// <summary>
    /// 列头那枚全选框。作用范围是当前**筛选出来**的那些行 ——
    /// 筛到「悬空 12」时按全选,要的是这 12 个,不是背后的 34 个。
    /// </summary>
    public bool? AllSelected
    {
        get
        {
            if (View.Count == 0)
            {
                return false;
            }
            var picked = View.Count(r => r.Selected);
            return picked == 0 ? false : picked == View.Count ? true : null;
        }
        set
        {
            var select = value is true;
            foreach (var row in View)
            {
                row.Selected = select;
            }
            RecountSelection();
        }
    }

    /// <summary>列表空了。</summary>
    public bool IsEmpty => LoadedOnce && _all.Count == 0;

    /// <summary>右侧详情抽屉;没打开时为 <see langword="null" />。</summary>
    public ImageDetailViewModel? Detail
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                Drawer.IsOpen = value is not null;
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    /// <summary>抽屉开着没。</summary>
    public bool HasDetail => Detail is not null;

    /// <summary>列表的列宽。列头与数据行共用这一份 —— 拖列头的轨道改的就是它。</summary>
    public ImageColumns Columns { get; } = new();

    /// <inheritdoc />
    public override ListColumns ColumnLayout => Columns;

    /// <inheritdoc />
    public override IEnumerable<string> ColumnTexts(string key) => key switch
    {
        "repo" => View.Select(r => r.Repository),
        "tag" => View.Select(r => r.Tag),
        "id" => View.Select(r => r.ShortId),
        "size" => View.Select(r => r.SizeText),
        "created" => View.Select(r => r.CreatedText),
        "used" => View.Select(r => r.UsageText),
        _ => []
    };

    /// <summary>导出成 tar(<c>docker save</c>)。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>从 tar 导入(<c>docker load</c>)。</summary>
    public RelayCommand LoadCommand { get; }

    /// <summary>能不能弹本地文件对话框。</summary>
    public bool CanPickFiles => FilePicker.IsAvailable;

    /// <summary>复制镜像 id。</summary>
    public RelayCommand RowCopyIdCommand => field ??= new(async p =>
    {
        if (p is ImageRow row)
        {
            await Shell.Context.Clipboard.SetTextAsync(row.Id, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, "已复制镜像 ID");
        }
    });

    /// <summary>打开详情。</summary>
    public RelayCommand OpenDetailCommand { get; }

    /// <summary>关掉详情。</summary>
    public RelayCommand CloseDetailCommand { get; }

    /// <summary>切筛选。</summary>
    public RelayCommand SetFilterCommand { get; }

    /// <summary>拉取镜像。</summary>
    public RelayCommand PullCommand { get; }

    /// <summary>用这个镜像跑一个容器。</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>打标签。</summary>
    public RelayCommand TagCommand { get; }

    /// <summary>推送。</summary>
    public RelayCommand PushCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>清理悬空镜像。</summary>
    public RelayCommand PruneDanglingCommand { get; }

    /// <summary>清理全部未使用镜像。</summary>
    public RelayCommand PruneAllCommand { get; }

    /// <summary>取消勾选。</summary>
    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <inheritdoc />
    public override async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        Busy = true;
        try
        {
            var summaries = await client.ListImagesAsync(false, cancellationToken).ConfigureAwait(true);
            List<ImageRow> incoming =
            [
                .. summaries
                    .OrderByDescending(s => s.Created)
                    .Select(s => new ImageRow(s))
            ];
            var previous = _all.ToDictionary(r => r.Id);
            _all.Clear();
            foreach (var row in incoming)
            {
                if (previous.TryGetValue(row.Id, out var existing))
                {
                    existing.Update(row);
                    _all.Add(existing);
                }
                else
                {
                    row.SelectionChanged += RecountSelection;
                    row.Owner = this;
                    _all.Add(row);
                }
            }
            LoadedOnce = true;
            Shell.SetImageCount(_all.Count);
            ApplyView();
            OnPropertiesChanged(nameof(TotalCount), nameof(UsedCount), nameof(UnusedCount), nameof(DanglingCount));
            if (Detail is { } detail)
            {
                var updated = _all.FirstOrDefault(r => r.Id == detail.ImageId);
                // 抽屉里那个镜像被删掉了(或者被 prune 掉了)就关上,
                // 留一个指向已消失对象的抽屉只会让下一步操作报一个看不懂的错。
                if (updated is null)
                {
                    CloseDetail();
                }
                else
                {
                    detail.ApplyRow(updated);
                }
            }
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task OpenDetailAsync(ImageRow row)
    {
        if (Detail is { } existing && existing.ImageId == row.Id)
        {
            return;
        }
        var detail = new ImageDetailViewModel(Shell, this, row);
        Detail = detail;
        MarkCurrent(row.Id);
        try
        {
            await detail.LoadAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("镜像详情", ex);
        }
    }

    private void CloseDetail()
    {
        Detail = null;
        MarkCurrent(null);
    }

    /// <summary>把"抽屉里开着的那一行"标出来。</summary>
    private void MarkCurrent(string? id)
    {
        foreach (var row in _all)
        {
            row.Current = row.Id == id;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        CloseDetail();
        _all.Clear();
        View.Clear();
        LoadedOnce = false;
        SelectedCount = 0;
        OnPropertiesChanged(nameof(TotalCount), nameof(UsedCount), nameof(UnusedCount), nameof(DanglingCount), nameof(IsEmpty));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type == "image";

    private void ApplyView()
    {
        var needle = Search.Trim();
        var filtered = _all.Where(row => Filter switch
        {
            ImageFilter.Used => row.Summary.Containers > 0,
            ImageFilter.Unused => row.Summary.Containers <= 0 && !row.IsDangling,
            ImageFilter.Dangling => row.IsDangling,
            _ => true
        });
        if (needle.Length > 0)
        {
            filtered = filtered.Where(row =>
                row.Repository.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.Tag.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                row.ShortId.StartsWith(needle, StringComparison.OrdinalIgnoreCase));
        }
        View.Merge([.. filtered], (_, _) => { });
        OnPropertiesChanged(nameof(IsEmpty), nameof(AllSelected));
    }

    private void RecountSelection()
    {
        SelectedCount = _all.Count(r => r.Selected);
        OnPropertyChanged(nameof(AllSelected));
    }

    private void ClearSelection()
    {
        foreach (var row in _all.Where(r => r.Selected))
        {
            row.Selected = false;
        }
        SelectedCount = 0;
        OnPropertyChanged(nameof(AllSelected));
    }

    private IReadOnlyList<ImageRow> Targets(object? parameter) =>
        parameter is ImageRow row ? [row] : [.. _all.Where(r => r.Selected)];

    private async Task TagAsync(ImageRow row)
    {
        if (Client is not { } client)
        {
            return;
        }
        var form = new TagImageForm(row.Id, $"{row.Repository}:{row.Tag}");
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await client.TagImageAsync(row.Id, form.Repository, form.Tag, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, $"已打标签 {form.Repository}:{form.Tag}");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("打标签", ex);
        }
    }

    /// <summary>
    /// 从仓库名里取出 registry 主机。
    /// <para>
    /// 第一段带点或冒号(或者就是 localhost)才是主机名 —— 否则那是 Docker Hub 的
    /// 用户名段,<c>docker login</c> 不带参数就是登它。
    /// </para>
    /// </summary>
    private static string RegistryOf(string repository)
    {
        var head = repository.Split('/')[0];
        return repository.Contains('/') &&
               (head.Contains('.') || head.Contains(':') || head == "localhost")
            ? head
            : "";
    }

    private async Task PushAsync(ImageRow row)
    {
        if (Client is not { } client || Shell.RegistryAuth is not { } auth)
        {
            return;
        }
        var reference = $"{row.Repository}:{row.Tag}";
        var task = Shell.Tasks.Start("Icon.upload", $"推送 {reference}", indeterminate: false);
        var aggregator = new PullAggregator();
        try
        {
            var header = await auth.GetAuthHeaderAsync(row.Repository, task.Token).ConfigureAwait(true);
            await client.PushImageAsync(row.Repository, row.Tag, header,
                new DirectProgress<PullProgressFrame>(frame =>
                {
                    aggregator.Accept(frame);
                    Ui.Post(() =>
                    {
                        task.Progress = aggregator.Progress;
                        task.Detail = aggregator.Summary;
                    });
                }), task.Token).ConfigureAwait(true);
            task.Finish(PanelTaskState.Succeeded, "完成");
            Shell.Feedback.Notify(FeedbackKind.Success, "推送完成", reference);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            task.Finish(PanelTaskState.Failed, "失败", ex.Message);
            // 401/403 的出路只有一条:去登录。面板不代劳(那要一个它不该拿的口令),
            // 但可以把命令连着 registry 一起送到终端里。
            ToastAction[] actions = ex is DockerApiException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? [new("去终端登录", () => _ = Shell.SendToHostTerminalAsync($"docker login {RegistryOf(row.Repository)}"))]
                : [];
            Shell.Feedback.ReportError("推送", ex, actions);
        }
    }

    /// <summary>
    /// 导出成 tar(<c>docker save</c>)。
    /// <para>
    /// 全程流式:镜像动辄上 GB,先攒在内存里再落盘就是在等 OOM。
    /// 进度只能是不确定型 —— <c>/images/get</c> 不给 <c>Content-Length</c>。
    /// </para>
    /// </summary>
    private async Task SaveAsync(IReadOnlyList<ImageRow> targets)
    {
        if (Client is not { } client || targets.Count == 0)
        {
            return;
        }
        // 悬空镜像没有可用的引用,只能按 id 存。
        List<string> names = [.. targets.Select(t => t.IsDangling ? t.Id : $"{t.Repository}:{t.Tag}")];
        var suggested = targets.Count == 1
            ? $"{(targets[0].IsDangling ? targets[0].ShortId : targets[0].Repository.Replace('/', '_'))}.tar"
            : $"images-{targets.Count}.tar";
        var target = await FilePicker
            .PickSaveAsync(targets.Count == 1 ? $"导出 {names[0]}" : $"导出 {targets.Count} 个镜像", suggested, "tar")
            .ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        var task = Shell.Tasks.Start("Docker.file-archive",
            targets.Count == 1 ? $"导出 {names[0]}" : $"导出 {targets.Count} 个镜像", indeterminate: true);
        try
        {
            await using var archive = await client.SaveImagesAsync(names, task.Token).ConfigureAwait(true);
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await archive.ReadAsync(buffer, task.Token).ConfigureAwait(true)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), task.Token).ConfigureAwait(true);
                total += read;
                var written = total;
                Ui.Post(() => task.Detail = $"已写出 {Humanize.Bytes(written)}");
            }
            task.Finish(PanelTaskState.Succeeded, "完成", Humanize.Bytes(total));
            Shell.Feedback.Notify(FeedbackKind.Success, "镜像已导出", $"{target.Name} · {Humanize.Bytes(total)}");
        }
        catch (OperationCanceledException)
        {
            task.Finish(PanelTaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            task.Finish(PanelTaskState.Failed, "失败", ex.Message);
            Shell.Feedback.ReportError("导出镜像", ex);
        }
    }

    /// <summary>从 tar 导入(<c>docker load</c>)。</summary>
    private async Task LoadAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var source = await FilePicker.PickOpenAsync("选一个 docker save 出来的 tar").ConfigureAwait(true);
        if (source is null)
        {
            return;
        }
        var task = Shell.Tasks.Start("Docker.arrow-down-to-line", $"导入 {source.Name}", indeterminate: false);
        var aggregator = new PullAggregator();
        try
        {
            // 请求体直接挂在文件流上,不缓冲。
            await using var input = await source.OpenReadAsync().ConfigureAwait(true);
            await client.LoadImagesAsync(input, new DirectProgress<PullProgressFrame>(frame =>
            {
                aggregator.Accept(frame);
                Ui.Post(() =>
                {
                    task.Progress = aggregator.Progress;
                    task.Detail = aggregator.Summary;
                });
            }), task.Token).ConfigureAwait(true);
            task.Finish(PanelTaskState.Succeeded, "完成");
            Shell.Feedback.Notify(FeedbackKind.Success, "镜像已导入", source.Name);
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            task.Finish(PanelTaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            task.Finish(PanelTaskState.Failed, "失败", ex.Message);
            Shell.Feedback.ReportError("导入镜像", ex);
        }
    }

    private async Task RemoveAsync(IReadOnlyList<ImageRow> targets)
    {
        if (Client is not { } client || targets.Count == 0)
        {
            return;
        }
        var anyInUse = targets.Any(t => t.Summary.Containers > 0);
        List<ConfirmConsequence> consequences =
        [
            new(2, "镜像层被删掉之后,重新拉回来要花时间与带宽。"),
            new(1, "被其它镜像共享的层不会被删。")
        ];
        if (anyInUse)
        {
            consequences.Insert(0, new(3, "其中有正在被容器使用的镜像 —— 需要 force,那会让那些容器失去它们的镜像引用。"));
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = targets.Count == 1 ? $"删除镜像 {targets[0].Repository}:{targets[0].Tag}?" : $"删除 {targets.Count} 个镜像?",
            Icon = "Icon.trash-2",
            HostName = "",
            ConfirmLabel = "删除镜像",
            Commands = [.. targets.Select(t => $"DELETE /images/{t.ShortId}?force={(anyInUse ? "true" : "false")}")],
            CommandNote = $"等价于  docker rmi {(anyInUse ? "-f " : "")}{string.Join(' ', targets.Select(t => t.ShortId))}",
            Targets = [.. targets.Select(t => new ConfirmTarget($"{t.Repository}:{t.Tag}", t.ShortId, t.SizeText, false))],
            Consequences = consequences
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        var result = await BatchRunner.RunAsync(
            [.. targets.Select(t => (Target: t, Name: $"{t.Repository}:{t.Tag}"))],
            async (row, ct) => await client.RemoveImageAsync(row.Id, anyInUse, ct).ConfigureAwait(false),
            null, Shell.Lifetime).ConfigureAwait(true);
        Shell.Feedback.ReportBatch("删除", result, Shell.CurrentPage == PanelPage.Images);
        ClearSelection();
        await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
    }

    private async Task PruneAsync(bool danglingOnly)
    {
        if (Client is not { } client)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = danglingOnly ? "清理悬空镜像?" : "清理全部未使用的镜像?",
            Icon = "Docker.broom",
            HostName = "",
            ConfirmLabel = "开始清理",
            ConfirmIcon = "Docker.broom",
            Commands = [$"POST /images/prune?filters={{\"dangling\":[\"{danglingOnly.ToString().ToLowerInvariant()}\"]}}"],
            CommandNote = $"等价于  docker image prune{(danglingOnly ? "" : " -a")}",
            Consequences = danglingOnly
                ?
                [
                    new(1, "只删没有标签、也没有容器引用的中间层。"),
                    new(0, $"当前有 {DanglingCount} 个悬空镜像。")
                ]
                :
                [
                    new(2, "会连带删掉「有标签但当前没有容器在用」的镜像 —— 重新拉要花时间与带宽。"),
                    new(0, $"当前有 {UnusedCount} 个未使用镜像 + {DanglingCount} 个悬空镜像。")
                ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            var report = await client.PruneImagesAsync(danglingOnly, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "清理完成",
                $"删除 {report.DeletedCount} 项 · 回收 {Humanize.Bytes(report.SpaceReclaimed)}");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("清理镜像", ex);
        }
    }
}

/// <summary>
/// 直接转发的 <see cref="IProgress{T}" />。
/// <para>
/// <b>不能用 <see cref="Progress{T}" />。</b> 它会把每次回调 <c>Post</c> 到捕获的同步上下文,
/// 既丢掉顺序也可能并发进入 —— 对进度百分比无所谓,对逐层日志与逐行输出则是灾难。
/// </para>
/// </summary>
public sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    /// <inheritdoc />
    public void Report(T value) => handler(value);
}

/// <summary>
/// 镜像列表的列宽。默认宽度取自设计稿 <c>C/ImageRow</c>
/// (ID 104 / 大小 84 / 创建 118 / 使用中 96)。
/// <para>
/// 设计稿把仓库名与标签画在同一格里(共 356),这里拆成**两列**:
/// 标签是一个可以独立排序、独立读的维度 —— <c>latest</c> 与 <c>1.27-alpine</c> 之间的差别,
/// 常常比仓库名本身更要紧;挤在一格里,它只能是名字后面一块跟着跑的小徽标。
/// 240 + 116 与稿子的 356 等宽,整张表的其余部分一根像素都不动。
/// </para>
/// </summary>
public sealed class ImageColumns : ListColumns
{

    /// <inheritdoc />
    public override IReadOnlyList<string> Keys { get; } = ["repo", "tag", "id", "size", "created", "used"];

    /// <summary>镜像列(仓库名)。</summary>
    public GridLength Repo
    {
        get;
        set => SetField(ref field, Clamp(value, "repo"));
    } = new(240);

    /// <summary>标签列。</summary>
    public GridLength Tag
    {
        get;
        set => SetField(ref field, Clamp(value, "tag"));
    } = new(116);

    /// <summary>镜像 ID 列。</summary>
    public GridLength Id
    {
        get;
        set => SetField(ref field, Clamp(value, "id"));
    } = new(104);

    /// <summary>大小列。</summary>
    public GridLength Size
    {
        get;
        set => SetField(ref field, Clamp(value, "size"));
    } = new(84);

    /// <summary>创建时间列。</summary>
    public GridLength Created
    {
        get;
        set => SetField(ref field, Clamp(value, "created"));
    } = new(118);

    /// <summary>使用中列。</summary>
    public GridLength Used
    {
        get;
        set => SetField(ref field, Clamp(value, "used"));
    } = new(96);

    /// <inheritdoc />
    public override double Get(string key) => key switch
    {
        "repo" => Repo.Value,
        "tag" => Tag.Value,
        "id" => Id.Value,
        "size" => Size.Value,
        "created" => Created.Value,
        _ => Used.Value
    };

    /// <inheritdoc />
    public override void Set(string key, double width)
    {
        GridLength value = new(width);
        switch (key)
        {
            case "repo": Repo = value; break;
            case "tag": Tag = value; break;
            case "id": Id = value; break;
            case "size": Size = value; break;
            case "created": Created = value; break;
            case "used": Used = value; break;
        }
    }

    /// <inheritdoc />
    public override double Min(string key) => key switch
    {
        "repo" => 120,
        "tag" => 70,
        "id" => 80,
        "size" => 62,
        "created" => 70,
        _ => 70
    };

    /// <inheritdoc />
    public override double MaxAutoFit(string key) => key is "repo" ? 760 : 300;

    /// <inheritdoc />
    public override double Padding(string key) => key is "repo" ? 44 : 18;

}
