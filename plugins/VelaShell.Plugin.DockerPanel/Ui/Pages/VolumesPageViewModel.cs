using Avalonia.Controls;
using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>卷页。</summary>
public sealed class VolumesPageViewModel : PageViewModel
{
    private readonly List<VolumeRow> _all = [];
    private readonly Dictionary<string, List<string>> _users = [];

    /// <summary>建卷页。</summary>
    public VolumesPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        SelectCommand = new RelayCommand(p => Selected = p as VolumeRow);
        CreateCommand = new RelayCommand(_ => CreateAsync());
        RemoveCommand = new RelayCommand(p => p is VolumeRow row ? RemoveAsync(row) : Task.CompletedTask);
        PruneCommand = new RelayCommand(_ => PruneAsync());
        BrowseCommand = new RelayCommand(p => p is VolumeRow row ? BrowseAsync(row) : Task.CompletedTask);
        BackupCommand = new RelayCommand(p => p is VolumeRow backup ? BackupAsync(backup) : Task.CompletedTask);
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
        ToggleUnusedCommand = new RelayCommand(_ =>
        {
            UnusedOnly = !UnusedOnly;
            return Task.CompletedTask;
        });
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Volumes;

    /// <inheritdoc />
    public override string Title => "卷";

    /// <summary>过滤后的行。</summary>
    public KeyedCollection<VolumeRow> View { get; } = new(r => r.Id);

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

    /// <summary>只看未使用的。</summary>
    public bool UnusedOnly
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyView();
            }
        }
    }

    /// <summary>总数。</summary>
    public int TotalCount => _all.Count;

    /// <summary>使用中的数量。</summary>
    public int UsedCount => _all.Count(r => r.RefCount > 0);

    /// <summary>未使用的数量。</summary>
    public int UnusedCount => _all.Count(r => r.RefCount == 0);

    /// <summary>总占用文本。</summary>
    public string TotalSizeText
    {
        get
        {
            var total = _all.Sum(r => r.Summary.UsageData is { Size: > 0 } u ? u.Size : 0);
            return total > 0 ? Humanize.Bytes(total) : "未统计";
        }
    }

    /// <summary>列表空了。</summary>
    public bool IsEmpty => LoadedOnce && _all.Count == 0;

    /// <summary>筛完之后没有匹配项 —— 与"这台机器上没有卷"是两回事,得分开说。</summary>
    public bool NoMatch => LoadedOnce && _all.Count > 0 && View.Count == 0;

    /// <summary>当前选中的卷(右侧详情)。</summary>
    public VolumeRow? Selected
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                BuildSelectedDetails(value);
                Drawer.IsOpen = value is not null;
                OnPropertiesChanged(nameof(HasSelection), nameof(SelectedUsers), nameof(CanBrowse), nameof(CanBackup), nameof(BrowseHint));
            }
        }
    }

    /// <summary>有选中。</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>列表的列宽。列头与数据行共用这一份 —— 拖列头的轨道改的就是它。</summary>
    public VolumeColumns Columns { get; } = new();

    /// <inheritdoc />
    public override ListColumns ColumnLayout => Columns;

    /// <inheritdoc />
    public override IEnumerable<string> ColumnTexts(string key) => key switch
    {
        "name" => View.Select(r => r.Name),
        "driver" => View.Select(r => r.Driver),
        "users" => View.Select(r => r.UsageText),
        "size" => View.Select(r => r.SizeText),
        "created" => View.Select(r => r.CreatedText),
        _ => []
    };

    /// <summary>选中卷的标签。</summary>
    public ObservableCollection<DetailField> SelectedLabels { get; } = [];

    /// <summary>选中卷的驱动选项。</summary>
    public ObservableCollection<DetailField> SelectedOptions { get; } = [];

    /// <summary>选中卷的使用者。</summary>
    public IReadOnlyList<string> SelectedUsers =>
        Selected is { } row && _users.TryGetValue(row.Name, out var users) ? users : [];

    /// <summary>
    /// 能不能浏览卷内文件。
    /// <para>
    /// Engine API 没有"读一个卷"的端点,而为了浏览去拉一个 alpine 起临时容器,
    /// 代价与副作用都不该由一次"看一眼"来承担。所以面板走一条更省的路:
    /// <b>借一个已经挂着这个卷的运行中容器</b>去 exec —— 有就能看,没有就明说。
    /// </para>
    /// </summary>
    public bool CanBrowse => SelectedUsers.Count > 0;

    /// <summary>不能浏览时的说明。</summary>
    public string BrowseHint => CanBrowse
        ? "经一个挂着它的运行中容器浏览"
        : "没有运行中的容器挂着它 —— 先起一个挂载了这个卷的容器再来看。";

    /// <summary>选中一行。</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>关掉右侧详情。</summary>
    public RelayCommand ClearSelectionCommand => field ??= new(_ =>
    {
        Selected = null;
        return Task.CompletedTask;
    });

    /// <summary>新建卷。</summary>
    public RelayCommand CreateCommand { get; }

    /// <summary>删除卷。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>清理未使用的卷。</summary>
    public RelayCommand PruneCommand { get; }

    /// <summary>浏览卷内文件。</summary>
    public RelayCommand BrowseCommand { get; }

    /// <summary>把卷内容备份成本地 tar。</summary>
    public RelayCommand BackupCommand { get; }

    /// <summary>能不能备份(和浏览同一个前提:得有容器挂着它)。</summary>
    public bool CanBackup => CanBrowse && FilePicker.IsAvailable;

    /// <summary>刷新。</summary>
    public RelayCommand RefreshCommand { get; }

    /// <summary>切换"只看未使用"。</summary>
    public RelayCommand ToggleUnusedCommand { get; }

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
            var volumes = await client.ListVolumesAsync(cancellationToken).ConfigureAwait(true);
            // 引用计数得自己算:/volumes 不带 UsageData,只有 /system/df 才带,
            // 而后者在镜像多的机器上要几秒 —— 不值得为一列数字每次都付这个钱。
            var containers = await client.ListContainersAsync(true, cancellationToken).ConfigureAwait(true);
            _users.Clear();
            foreach (var container in containers)
            {
                foreach (var mount in container.Mounts ?? [])
                {
                    if (mount.Type != "volume" || mount.Name is not { Length: > 0 } name)
                    {
                        continue;
                    }
                    if (!_users.TryGetValue(name, out var list))
                    {
                        _users[name] = list = [];
                    }
                    list.Add($"{container.Name} → {mount.Destination}{(mount.RW ? "" : " (ro)")}");
                }
            }
            List<VolumeRow> incoming =
            [
                .. volumes
                    .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(v => new VolumeRow(v, _users.TryGetValue(v.Name, out var u) ? u.Count : 0))
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
                    // 行要回指页面:列宽绑在页面上,行模板得找得到它。
                    row.Owner = this;
                    _all.Add(row);
                }
            }
            LoadedOnce = true;
            Shell.SetVolumeCount(_all.Count);
            ApplyView();
            if (Selected is { } selected && _all.All(r => r.Id != selected.Id))
            {
                Selected = null;
            }
            OnPropertiesChanged(nameof(TotalCount), nameof(UsedCount), nameof(UnusedCount), nameof(TotalSizeText),
                nameof(SelectedUsers), nameof(CanBrowse), nameof(CanBackup), nameof(BrowseHint));
        }
        finally
        {
            Busy = false;
        }
    }

    /// <inheritdoc />
    public override void Reset()
    {
        _all.Clear();
        _users.Clear();
        View.Clear();
        Selected = null;
        LoadedOnce = false;
        OnPropertiesChanged(nameof(TotalCount), nameof(UsedCount), nameof(UnusedCount), nameof(IsEmpty));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) =>
        dockerEvent.Type is "volume" or "container";

    private void ApplyView()
    {
        var needle = Search.Trim();
        IEnumerable<VolumeRow> filtered = _all;
        if (UnusedOnly)
        {
            filtered = filtered.Where(r => r.RefCount == 0);
        }
        if (needle.Length > 0)
        {
            filtered = filtered.Where(r =>
                r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.Project.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        View.Merge([.. filtered], (_, _) => { });
        OnPropertiesChanged(nameof(IsEmpty), nameof(NoMatch), nameof(UnusedCount), nameof(UsedCount));
    }

    private async Task CreateAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var form = new CreateVolumeForm();
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            await client.CreateVolumeAsync(form.Name, form.Driver, form.DriverOptions, form.LabelMap, Shell.Lifetime)
                        .ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, $"已新建卷 {form.Name}");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("新建卷", ex);
        }
    }

    private async Task RemoveAsync(VolumeRow row)
    {
        if (Client is not { } client)
        {
            return;
        }
        var users = _users.TryGetValue(row.Name, out var list) ? list : [];
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"删除卷 {row.Name}?",
            Icon = "Docker.shield-alert",
            Tier = ConfirmTier.DataLoss,
            ConfirmWord = "delete",
            ConfirmLabel = "永久删除卷",
            HostName = "",
            Commands = [$"DELETE /volumes/{row.Name}"],
            CommandNote = $"等价于  docker volume rm {row.Name}",
            DataLossHeadline = row.SizeText == "—"
                ? "卷里的数据将被永久删除,无法撤销"
                : $"{row.SizeText} 数据将被永久删除,无法撤销",
            DataLossPoints =
            [
                "Docker 不做回收站,也没有快照 —— 删了就是删了。",
                users.Count > 0
                    ? $"仍有 {users.Count} 个容器引用它:{string.Join('、', users.Take(3))}"
                    : "当前没有容器引用它。",
                "本面板没有找到这个卷的备份记录。"
            ],
            // 只在真备得了的时候才给这个勾选 —— 一个点了没反应的复选框比没有更糟。
            PrecautionLabel = CanBackupRow(row) ? "先备份为 tar 再删除" : null,
            PrecautionDefault = true
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        if (Shell.Confirm.HasPrecaution && Shell.Confirm.Precaution)
        {
            // 备份失败就**不删** —— 用户勾这个框的意思正是"没有备份就别删"。
            if (!await BackupAsync(row).ConfigureAwait(true))
            {
                Shell.Feedback.Notify(FeedbackKind.Warning, "没有删除", "备份没成功,卷保留原样。");
                return;
            }
        }
        try
        {
            await client.RemoveVolumeAsync(row.Name, false, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "卷已删除", row.Name);
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 409 的真实含义是"还有容器挂着它",而挂着它的是谁,这一页早就知道 ——
            // 把这句话直接说出来,并给一条过去看的路。
            ToastAction[] actions = ex is DockerApiException { IsConflict: true } && _users.TryGetValue(row.Name, out var holders) && holders.Count > 0
                ? [new($"看看是谁在占({holders.Count} 个容器)", () => Selected = row)]
                : [];
            Shell.Feedback.ReportError("删除卷", ex, actions);
        }
    }

    private async Task PruneAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = "清理全部未使用的卷?",
            Icon = "Docker.shield-alert",
            Tier = ConfirmTier.DataLoss,
            ConfirmWord = "delete",
            ConfirmLabel = "清理未使用的卷",
            HostName = "",
            Commands = ["POST /volumes/prune"],
            CommandNote = "等价于  docker volume prune",
            DataLossHeadline = $"当前有 {UnusedCount} 个未使用的卷,它们里面的数据会被永久删除",
            DataLossPoints =
            [
                "\"未使用\"只是说没有容器「现在」挂着它 —— 一个刚 down 掉的 compose 项目,它的数据卷就在这个名单里。",
                "Docker 不做回收站。",
                .. _all.Where(r => r.RefCount == 0).Take(5).Select(r => $"将被删除:{r.Name}({r.SizeText})")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            var report = await client.PruneVolumesAsync(Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "清理完成",
                $"删除 {report.DeletedCount} 个卷 · 回收 {Humanize.Bytes(report.SpaceReclaimed)}");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("清理卷", ex);
        }
    }

    private async Task BrowseAsync(VolumeRow row)
    {
        if (await FindMountAsync(row.Name).ConfigureAwait(true) is not { } mount)
        {
            Shell.Feedback.Notify(FeedbackKind.Warning, "没法浏览这个卷", BrowseHint);
            return;
        }
        (var container, var destination) = mount;
        await Shell.GoToAsync(PanelPage.Containers).ConfigureAwait(true);
        var target = Shell.Containers.View.FirstOrDefault(r => r.Id == container.Id);
        if (target is null)
        {
            return;
        }
        // 直接落到挂载点。让用户从 / 一级一级点到 /var/lib/postgresql/data,
        // 是把面板已经知道的答案又要了一遍。
        await Shell.Containers.OpenDetailAtFilesAsync(target, destination).ConfigureAwait(true);
        Shell.Feedback.Status(FeedbackKind.Info,
            $"经容器 {container.Name} 浏览卷 {row.Name} —— 它挂在 {destination}");
    }

    /// <summary>
    /// 把卷的内容整包取到本地 tar。
    /// <para>
    /// 和浏览走的是同一条路 —— 借一个已经挂着它的运行中容器,从那个容器的
    /// <c>/archive</c> 端点把挂载点整个取走。Engine API 没有"读一个卷"的端点,
    /// 而为了备份去拉一个 alpine 起临时容器,是在用户没要求的情况下动他的机器。
    /// </para>
    /// </summary>
    /// <summary>这一行能不能备份(有运行中的容器挂着它,且能弹文件对话框)。</summary>
    private bool CanBackupRow(VolumeRow row) =>
        FilePicker.IsAvailable && _users.TryGetValue(row.Name, out var users) && users.Count > 0;

    /// <summary>备份;返回是否真的存下来了(删卷前的那个勾选靠它决定要不要继续)。</summary>
    private async Task<bool> BackupAsync(VolumeRow row)
    {
        if (Client is not { } client)
        {
            return false;
        }
        if (await FindMountAsync(row.Name).ConfigureAwait(true) is not { } mount)
        {
            Shell.Feedback.Notify(FeedbackKind.Warning, "没法备份这个卷",
                "没有运行中的容器挂着它 —— 先起一个挂载了这个卷的容器再来备份。");
            return false;
        }
        (var container, var destination) = mount;
        var target = await FilePicker
            .PickSaveAsync($"把卷 {row.Name} 存成 tar", $"{row.Name}.tar", "tar")
            .ConfigureAwait(true);
        if (target is null)
        {
            return false;
        }
        Busy = true;
        try
        {
            await using var archive = await client
                .DownloadArchiveAsync(container.Id, destination, Shell.Lifetime).ConfigureAwait(true);
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            await archive.CopyToAsync(output, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "卷已备份",
                $"{target.Name} · 经容器 {container.Name} 的 {destination}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("备份卷", ex);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 把选中卷的标签与驱动选项摊平。
    /// <para>
    /// compose 起的卷会带上 <c>com.docker.compose.*</c> 那几条标签 ——
    /// 它们是"这个卷归谁管"的唯一线索,删卷之前值得看一眼。
    /// </para>
    /// </summary>
    private void BuildSelectedDetails(VolumeRow? row)
    {
        SelectedLabels.Clear();
        SelectedOptions.Clear();
        if (row is null)
        {
            return;
        }
        foreach ((var key, var value) in row.Summary.Labels ?? [])
        {
            SelectedLabels.Add(new(key, value));
        }
        foreach ((var key, var value) in row.Summary.Options ?? [])
        {
            SelectedOptions.Add(new(key, value));
        }
    }

    /// <summary>找一个正在跑、并且挂着这个卷的容器,连同它的挂载点一起给出来。</summary>
    private async Task<(ContainerSummary Container, string Destination)?> FindMountAsync(string volumeName)
    {
        if (Client is not { } client)
        {
            return null;
        }
        var containers = await client.ListContainersAsync(false, Shell.Lifetime).ConfigureAwait(true);
        foreach (var container in containers)
        {
            var mount = (container.Mounts ?? [])
                .FirstOrDefault(m => m.Type == "volume" && m.Name == volumeName);
            if (mount?.Destination is { Length: > 0 } destination)
            {
                return (container, destination);
            }
        }
        return null;
    }
}

/// <summary>
/// 卷列表的列宽。默认宽度取自设计稿 07 号板的表头
/// (名称 336 / 驱动 90 / 使用者 180 / 大小 96 / 创建 112)。
/// </summary>
public sealed class VolumeColumns : ListColumns
{

    /// <inheritdoc />
    public override IReadOnlyList<string> Keys { get; } = ["name", "driver", "users", "size", "created"];

    /// <summary>名称列。</summary>
    public GridLength Name
    {
        get;
        set => SetField(ref field, Clamp(value, "name"));
    } = new(336);

    /// <summary>驱动列。</summary>
    public GridLength Driver
    {
        get;
        set => SetField(ref field, Clamp(value, "driver"));
    } = new(90);

    /// <summary>使用者列。</summary>
    public GridLength Users
    {
        get;
        set => SetField(ref field, Clamp(value, "users"));
    } = new(180);

    /// <summary>大小列。</summary>
    public GridLength Size
    {
        get;
        set => SetField(ref field, Clamp(value, "size"));
    } = new(96);

    /// <summary>创建时间列。</summary>
    public GridLength Created
    {
        get;
        set => SetField(ref field, Clamp(value, "created"));
    } = new(112);

    /// <inheritdoc />
    public override double Get(string key) => key switch
    {
        "name" => Name.Value,
        "driver" => Driver.Value,
        "users" => Users.Value,
        "size" => Size.Value,
        _ => Created.Value
    };

    /// <inheritdoc />
    public override void Set(string key, double width)
    {
        GridLength value = new(width);
        switch (key)
        {
            case "name": Name = value; break;
            case "driver": Driver = value; break;
            case "users": Users = value; break;
            case "size": Size = value; break;
            case "created": Created = value; break;
        }
    }

    /// <inheritdoc />
    public override double Min(string key) => key switch
    {
        "name" => 160,
        "driver" => 70,
        "users" => 90,
        "size" => 62,
        _ => 70
    };

    /// <inheritdoc />
    public override double MaxAutoFit(string key) => key is "name" ? 760 : 300;

    /// <inheritdoc />
    // 名称格里还坐着一枚卷图标。
    public override double Padding(string key) => key is "name" ? 46 : 18;
}
