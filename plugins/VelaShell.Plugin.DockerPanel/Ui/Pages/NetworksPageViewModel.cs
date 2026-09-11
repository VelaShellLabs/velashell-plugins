using Avalonia.Controls;
using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>网络详情里已接入的一个容器。</summary>
/// <param name="Id">容器 id。</param>
/// <param name="Name">容器名。</param>
/// <param name="Address">IPv4(带掩码)。</param>
public readonly record struct AttachedContainer(string Id, string Name, string Address);

/// <summary>网络页。</summary>
public sealed class NetworksPageViewModel : PageViewModel
{
    private readonly List<NetworkRow> _all = [];
    private bool _swarmActive;

    /// <summary>建网络页。</summary>
    public NetworksPageViewModel(DockerPanelViewModel shell) : base(shell)
    {
        SelectCommand = new RelayCommand(p => SelectAsync(p as NetworkRow));
        CreateCommand = new RelayCommand(_ => CreateAsync());
        RemoveCommand = new RelayCommand(p => p is NetworkRow row ? RemoveAsync(row) : Task.CompletedTask);
        PruneCommand = new RelayCommand(_ => PruneAsync());
        ConnectCommand = new RelayCommand(p => p is NetworkRow row ? ConnectAsync(row) : Task.CompletedTask);
        DisconnectCommand = new RelayCommand(p => p is AttachedContainer attached
            ? DisconnectAsync(attached)
            : Task.CompletedTask);
        RefreshCommand = new RelayCommand(_ => RefreshAsync(Shell.Lifetime));
    }

    /// <inheritdoc />
    public override PanelPage Page => PanelPage.Networks;

    /// <inheritdoc />
    public override string Title => "网络";

    /// <summary>过滤后的行。</summary>
    public KeyedCollection<NetworkRow> View { get; } = new(r => r.Id);

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

    /// <summary>自定义网络数。</summary>
    public int CustomCount => _all.Count(r => !r.IsPredefined);

    /// <summary>内置网络数。</summary>
    public int PredefinedCount => _all.Count(r => r.IsPredefined);

    /// <summary>
    /// 只看自定义网络。
    /// <para>
    /// bridge / host / none 这三个内置的删不掉、改不了,却永远占着列表最前面几行;
    /// 用户来这一页多半是找自己建的那几个。计数早就算好了(<see cref="CustomCount" />),
    /// 一直缺的只是这个开关。
    /// </para>
    /// </summary>
    public bool CustomOnly
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

    /// <summary>未接入任何容器的自定义网络数。</summary>
    public int UnusedCount => _all.Count(r => !r.IsPredefined && r.AttachedCount == 0);

    /// <summary>列表空了。</summary>
    public bool IsEmpty => LoadedOnce && _all.Count == 0;

    /// <summary>筛完之后没有匹配项 —— 与"这台机器上没有网络"是两回事,得分开说。</summary>
    public bool NoMatch => LoadedOnce && _all.Count > 0 && View.Count == 0;

    /// <summary>当前选中的网络。</summary>
    public NetworkRow? Selected
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                Drawer.IsOpen = value is not null;
                OnPropertiesChanged(nameof(HasSelection), nameof(CanRemove), nameof(RemoveHint));
            }
        }
    }

    /// <summary>有选中。</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>列表的列宽。列头与数据行共用这一份 —— 拖列头的轨道改的就是它。</summary>
    public NetworkColumns Columns { get; } = new();

    /// <inheritdoc />
    public override ListColumns ColumnLayout => Columns;

    /// <inheritdoc />
    public override IEnumerable<string> ColumnTexts(string key) => key switch
    {
        "name" => View.Select(r => r.Name),
        "driver" => View.Select(r => r.Driver),
        "scope" => View.Select(r => r.Scope),
        "subnet" => View.Select(r => r.Subnet),
        "attached" => View.Select(r => r.AttachedText),
        _ => []
    };

    /// <summary>选中网络的接入容器。</summary>
    public ObservableCollection<AttachedContainer> Attached { get; } = [];

    /// <summary>选中网络的 IPAM 明细。</summary>
    public ObservableCollection<DetailField> Ipam { get; } = [];

    /// <summary>inspect 原文;还没读过时为空。</summary>
    public string RawInspect
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasRaw));
            }
        }
    } = "";

    /// <summary>原文读到了没有。</summary>
    public bool HasRaw => RawInspect.Length > 0;

    /// <summary>显示 inspect 原文(设计稿 08 号板的「原始 JSON」)。</summary>
    public RelayCommand ShowRawCommand => field ??= new(_ => LoadRawAsync());

    /// <summary>把 inspect 原文存成本地 JSON。</summary>
    public RelayCommand ExportCommand => field ??= new(_ => ExportAsync());

    /// <summary>把所有容器从这个网络上摘掉。</summary>
    public RelayCommand DisconnectAllCommand => field ??= new(_ => DisconnectAllAsync());

    /// <summary>能不能弹本地文件对话框。</summary>
    public bool CanPickFiles => FilePicker.IsAvailable;

    private async Task LoadRawAsync()
    {
        if (Client is not { } client || Selected is not { } row)
        {
            return;
        }
        try
        {
            RawInspect = await client.InspectNetworkRawAsync(row.Id, Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RawInspect = ex.Message;
        }
    }

    /// <summary>
    /// 导出网络配置。
    /// <para>
    /// 导的是 <b>inspect 原文</b>,不是一份"能直接 create 回去"的清单 ——
    /// inspect 里带着运行态(已接入的容器、分配出去的地址),那些东西重建时既不该也无法照搬。
    /// 它的用途是留档与比对,文件头一行会写清这一点。
    /// </para>
    /// </summary>
    private async Task ExportAsync()
    {
        if (Selected is not { } row)
        {
            return;
        }
        if (RawInspect.Length == 0)
        {
            await LoadRawAsync().ConfigureAwait(true);
        }
        var target = await FilePicker
            .PickSaveAsync($"导出 {row.Name} 的配置", $"{row.Name}.json", "json").ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        try
        {
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(output);
            await writer.WriteLineAsync(
                    "// docker network inspect 的原文 —— 含运行态(已接入容器与已分配地址),不是可直接重建的清单。")
                .ConfigureAwait(true);
            await writer.WriteAsync(RawInspect).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "网络配置已导出", target.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("导出网络配置", ex);
        }
    }

    private async Task DisconnectAllAsync()
    {
        if (Client is not { } client || Selected is not { } row || Attached.Count == 0)
        {
            return;
        }
        AttachedContainer[] targets = [.. Attached];
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"把 {targets.Length} 个容器从 {row.Name} 上摘掉?",
            Icon = "Icon.unplug",
            HostName = "",
            ConfirmLabel = "全部摘除",
            ConfirmIcon = "Icon.unplug",
            Commands = [.. targets.Select(t => $"POST /networks/{row.ShortId}/disconnect ({t.Name})")],
            CommandNote = $"等价于逐个执行  docker network disconnect {row.Name} <容器>",
            Targets = [.. targets.Select(t => new ConfirmTarget(t.Name, t.Address, "", false))],
            Consequences =
            [
                new(3, "容器之间将无法再通过这个网络互相解析与访问 —— 依赖服务名通信的会立刻失败。"),
                new(1, "容器本身不受影响,不会停止;重新接入即可恢复。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        foreach (var target in targets)
        {
            try
            {
                await client.DisconnectNetworkAsync(row.Id, target.Id, false, Shell.Lifetime).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 逐个判定:一个摘不掉不该让其余的也不摘。
                Shell.Feedback.Notify(FeedbackKind.Warning, $"{target.Name} 摘不掉", ex.Message);
            }
        }
        Shell.Feedback.Status(FeedbackKind.Success, $"已从 {row.Name} 摘除 {targets.Length} 个容器");
        await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
    }

    /// <summary>选中网络的基本信息。</summary>
    public ObservableCollection<DetailField> Basics { get; } = [];

    /// <summary>能不能删。</summary>
    public bool CanRemove => Selected is { IsPredefined: false, AttachedCount: 0 };

    /// <summary>不能删时的原因。</summary>
    public string RemoveHint => Selected switch
    {
        null => "",
        { IsPredefined: true } => "Docker 内置的 bridge / host / none 删不掉。",
        { AttachedCount: > 0 } row => $"仍有 {row.AttachedCount} 个容器接入,必须先摘除或停止它们。",
        _ => "删除网络本身不会丢数据。"
    };

    /// <summary>选中一行。</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>关掉右侧详情。</summary>
    public RelayCommand ClearSelectionCommand => field ??= new(_ => SelectAsync(null));

    /// <summary>新建网络。</summary>
    public RelayCommand CreateCommand { get; }

    /// <summary>删除网络。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>清理未使用网络。</summary>
    public RelayCommand PruneCommand { get; }

    /// <summary>接入容器。</summary>
    public RelayCommand ConnectCommand { get; }

    /// <summary>摘除容器。</summary>
    public RelayCommand DisconnectCommand { get; }

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
            var networks = await client.ListNetworksAsync(cancellationToken).ConfigureAwait(true);
            List<NetworkRow> incoming =
            [
                .. networks
                    .OrderBy(n => n.IsPredefined)
                    .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(n => new NetworkRow(n))
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
            ApplyView();
            if (Selected is { } selected)
            {
                var still = _all.FirstOrDefault(r => r.Id == selected.Id);
                Selected = still;
                if (still is not null)
                {
                    await LoadDetailAsync(still, cancellationToken).ConfigureAwait(true);
                }
            }
            OnPropertiesChanged(nameof(TotalCount), nameof(CustomCount), nameof(PredefinedCount), nameof(UnusedCount));
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
        View.Clear();
        Attached.Clear();
        Ipam.Clear();
        Basics.Clear();
        Selected = null;
        LoadedOnce = false;
        OnPropertiesChanged(nameof(TotalCount), nameof(CustomCount), nameof(PredefinedCount), nameof(IsEmpty));
    }

    /// <inheritdoc />
    public override bool WantsRefresh(DockerEvent dockerEvent) => dockerEvent.Type == "network";

    private void ApplyView()
    {
        var needle = Search.Trim();
        IEnumerable<NetworkRow> filtered = _all;
        if (CustomOnly)
        {
            filtered = filtered.Where(r => !r.IsPredefined);
        }
        if (needle.Length > 0)
        {
            filtered = filtered.Where(r =>
                r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.Subnet.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                r.Driver.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        View.Merge([.. filtered], (_, _) => { });
        OnPropertiesChanged(nameof(IsEmpty), nameof(NoMatch), nameof(CustomCount), nameof(PredefinedCount));
    }

    private async Task SelectAsync(NetworkRow? row)
    {
        Selected = row;
        if (row is not null)
        {
            await LoadDetailAsync(row, Shell.Lifetime).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 一个 CIDR 里能给容器用多少个地址;解不出来返回 <see langword="null" />。
    /// <para>
    /// 减 2 是网络地址与广播地址,再减 1 是网关 —— docker 的 bridge 网络会占掉 <c>.1</c>。
    /// </para>
    /// </summary>
    internal static long? SubnetCapacity(string? cidr)
    {
        if (cidr is not { Length: > 0 } text || text.IndexOf('/') is not (> 0 and int slash))
        {
            return null;
        }
        if (!int.TryParse(text[(slash + 1)..], out var prefix))
        {
            return null;
        }
        // IPv6 的地址空间大到显示出来毫无意义,不给。
        if (text.Contains(':', StringComparison.Ordinal) || prefix is < 0 or > 32)
        {
            return null;
        }
        var total = 1L << (32 - prefix);
        return total > 3 ? total - 3 : 0;
    }

    private async Task LoadDetailAsync(NetworkRow row, CancellationToken cancellationToken)
    {
        if (Client is not { } client)
        {
            return;
        }
        try
        {
            var detail = await client.InspectNetworkAsync(row.Id, cancellationToken).ConfigureAwait(true);
            Attached.Clear();
            foreach ((var id, var container) in detail.Containers ?? [])
            {
                Attached.Add(new(id, container.Name ?? Humanize.ShortId(id), container.IPv4Address ?? "—"));
            }
            Ipam.Clear();
            Ipam.Add(new("子网", detail.FirstSubnet ?? "(自动)", RowTone.Ok));
            Ipam.Add(new("网关", detail.FirstGateway ?? "(自动)", RowTone.Ok));
            Ipam.Add(new("IPAM 驱动", detail.IPAM?.Driver ?? "default"));
            // 「已分配 3 / 65533」:子网快用满是一个到了才发现就太晚的问题。
            if (SubnetCapacity(detail.FirstSubnet) is { } capacity)
            {
                var used = Attached.Count;
                Ipam.Add(new("已分配", $"{used} / {capacity:N0}",
                    capacity > 0 && used > capacity * 0.8 ? RowTone.Warn : RowTone.Idle));
            }
            Basics.Clear();
            Basics.Add(new("网络 ID", Humanize.ShortId(detail.Id)));
            Basics.Add(new("驱动", detail.Driver ?? "—"));
            Basics.Add(new("作用域", detail.Scope ?? "—"));
            Basics.Add(new("创建于", Humanize.LocalTime(detail.Created)));
            Basics.Add(new("internal", detail.Internal ? "是 · 不通外网" : "否 · 可访问外网"));
            Basics.Add(new("attachable", detail.Attachable ? "是" : "否"));
            Basics.Add(new("IPv6", detail.EnableIPv6 ? "已启用" : "关闭"));
            OnPropertiesChanged(nameof(CanRemove), nameof(RemoveHint));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("读取网络详情", ex);
        }
    }

    private async Task CreateAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        try
        {
            var info = await client.InfoAsync(Shell.Lifetime).ConfigureAwait(true);
            _swarmActive = info.Swarm?.LocalNodeState == "active";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _swarmActive = false;
        }
        var form = new CreateNetworkForm(_swarmActive);
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        try
        {
            var created = await client.CreateNetworkAsync(form.Name, form.Driver, form.Subnet,
                form.Gateway, form.Internal, form.Attachable, form.EnableIPv6, Shell.Lifetime).ConfigureAwait(true);
            if (created.Warning is { Length: > 0 } warning)
            {
                Shell.Feedback.Notify(FeedbackKind.Warning, "daemon 有话说", warning);
            }
            else
            {
                Shell.Feedback.Status(FeedbackKind.Success, $"已新建网络 {form.Name}");
            }
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("新建网络", ex);
        }
    }

    private async Task RemoveAsync(NetworkRow row)
    {
        if (Client is not { } client || row.IsPredefined)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"删除网络 {row.Name}?",
            Icon = "Icon.trash-2",
            HostName = "",
            ConfirmLabel = "删除网络",
            Commands = [$"DELETE /networks/{Humanize.ShortId(row.Id)}"],
            CommandNote = $"等价于  docker network rm {row.Name}",
            Consequences =
            [
                new(1, "删除网络本身不会丢数据。"),
                new(row.AttachedCount > 0 ? 3 : 0,
                    row.AttachedCount > 0
                        ? $"仍有 {row.AttachedCount} 个容器接入 —— daemon 会拒绝,先摘除或停止它们。"
                        : "当前没有容器接入。"),
                new(0, "compose 项目的默认网络删掉后,下次 up 会自动重建。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await client.RemoveNetworkAsync(row.Id, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, $"已删除网络 {row.Name}");
            Selected = null;
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 409 = 还连着容器。RemoveHint 里那句话本来就是为这件事写的,
            // 却只在按钮置灰时才说过 —— 真撞上了反而只剩 daemon 的原文。
            ToastAction[] actions = ex is DockerApiException { IsConflict: true }
                ? [new("看看还连着谁", () => Selected = row)]
                : [];
            Shell.Feedback.ReportError("删除网络", ex, actions);
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
            Title = "清理未使用的网络?",
            Icon = "Docker.broom",
            HostName = "",
            ConfirmLabel = "开始清理",
            ConfirmIcon = "Docker.broom",
            Commands = ["POST /networks/prune"],
            CommandNote = "等价于  docker network prune",
            Consequences =
            [
                new(1, "不丢数据 —— 网络只是配置。"),
                new(0, $"当前有 {UnusedCount} 个自定义网络没有容器接入。"),
                new(2, "已停止的 compose 项目,它的网络也在这个名单里;下次 up 会自动重建。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            var report = await client.PruneNetworksAsync(Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Notify(FeedbackKind.Success, "清理完成", $"删除 {report.DeletedCount} 个网络");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("清理网络", ex);
        }
    }

    private async Task ConnectAsync(NetworkRow row)
    {
        if (Client is not { } client)
        {
            return;
        }
        var containers = await client.ListContainersAsync(true, Shell.Lifetime).ConfigureAwait(true);
        HashSet<string> already = [.. Attached.Select(a => a.Id)];
        var form = new ConnectNetworkForm(
            $"{row.Name} · {row.Driver} · {row.Subnet}",
            containers.Select(c => (
                c.Id,
                c.Name,
                Meta: already.Contains(c.Id) ? "已在这个网络里" : c.State == "running" ? "运行中" : "已停止 · 接入后下次启动生效",
                Enabled: !already.Contains(c.Id),
                Reason: already.Contains(c.Id) ? "已在这个网络里" : "")));
        if (!await Shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        var result = await BatchRunner.RunAsync(
            [.. form.SelectedIds.Select(id => (Target: id, containers.First(c => c.Id == id).Name))],
            (id, ct) => client.ConnectNetworkAsync(row.Id, id, form.Aliases.Length > 0 ? form.Aliases : null, ct),
            null, Shell.Lifetime).ConfigureAwait(true);
        Shell.Feedback.ReportBatch("接入", result, Shell.CurrentPage == PanelPage.Networks);
        await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
    }

    private async Task DisconnectAsync(AttachedContainer attached)
    {
        if (Client is not { } client || Selected is not { } row)
        {
            return;
        }
        var confirmed = await Shell.Confirm.AskAsync(Shell.BuildConfirm(new()
        {
            Title = $"把 {attached.Name} 从 {row.Name} 上摘掉?",
            Icon = "Icon.unplug",
            HostName = "",
            ConfirmLabel = "摘除",
            ConfirmIcon = "Icon.unplug",
            Commands = [$"POST /networks/{Humanize.ShortId(row.Id)}/disconnect"],
            CommandNote = $"等价于  docker network disconnect {row.Name} {attached.Name}",
            Consequences =
            [
                new(2, "容器会立刻失去这个网络上的连通性 —— 正在进行的连接会断。"),
                new(0, "不重启容器,也不丢数据。")
            ]
        })).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }
        try
        {
            await client.DisconnectNetworkAsync(row.Id, attached.Id, false, Shell.Lifetime).ConfigureAwait(true);
            Shell.Feedback.Status(FeedbackKind.Success, $"已把 {attached.Name} 从 {row.Name} 摘掉");
            await RefreshAsync(Shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Shell.Feedback.ReportError("摘除容器", ex);
        }
    }
}

/// <summary>
/// 网络列表的列宽。默认宽度取自设计稿 08 号板的表头
/// (名称 362 / 驱动 92 / 作用域 84 / 子网 164 / 已接入 112)。
/// </summary>
public sealed class NetworkColumns : ListColumns
{

    /// <inheritdoc />
    public override IReadOnlyList<string> Keys { get; } = ["name", "driver", "scope", "subnet", "attached"];

    /// <summary>名称列。</summary>
    public GridLength Name
    {
        get;
        set => SetField(ref field, Clamp(value, "name"));
    } = new(362);

    /// <summary>驱动列。</summary>
    public GridLength Driver
    {
        get;
        set => SetField(ref field, Clamp(value, "driver"));
    } = new(92);

    /// <summary>作用域列。</summary>
    public GridLength Scope
    {
        get;
        set => SetField(ref field, Clamp(value, "scope"));
    } = new(84);

    /// <summary>子网列。</summary>
    public GridLength Subnet
    {
        get;
        set => SetField(ref field, Clamp(value, "subnet"));
    } = new(164);

    /// <summary>已接入列。</summary>
    public GridLength Attached
    {
        get;
        set => SetField(ref field, Clamp(value, "attached"));
    } = new(112);

    /// <inheritdoc />
    public override double Get(string key) => key switch
    {
        "name" => Name.Value,
        "driver" => Driver.Value,
        "scope" => Scope.Value,
        "subnet" => Subnet.Value,
        _ => Attached.Value
    };

    /// <inheritdoc />
    public override void Set(string key, double width)
    {
        GridLength value = new(width);
        switch (key)
        {
            case "name": Name = value; break;
            case "driver": Driver = value; break;
            case "scope": Scope = value; break;
            case "subnet": Subnet = value; break;
            case "attached": Attached = value; break;
        }
    }

    /// <inheritdoc />
    public override double Min(string key) => key switch
    {
        "name" => 160,
        "driver" => 70,
        "scope" => 62,
        "subnet" => 90,
        _ => 70
    };

    /// <inheritdoc />
    public override double MaxAutoFit(string key) => key is "name" ? 760 : 300;

    /// <inheritdoc />
    // 名称格里还坐着一枚网络图标,内置网络还多一块「内置」徽标。
    public override double Padding(string key) => key is "name" ? 84 : 18;
}
