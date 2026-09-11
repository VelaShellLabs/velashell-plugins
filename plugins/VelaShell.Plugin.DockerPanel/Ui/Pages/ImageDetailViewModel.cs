using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>镜像详情抽屉的页签。</summary>
public enum ImageDetailTab
{
    /// <summary>概览。</summary>
    Overview,

    /// <summary>层历史。</summary>
    History,

    /// <summary>原文。</summary>
    Raw
}

/// <summary>构建历史里的一层。</summary>
/// <param name="Instruction">产生这一层的指令(已剥掉 <c>/bin/sh -c #(nop)</c> 这类噪声)。</param>
/// <param name="SizeText">这一层带来的体积。</param>
/// <param name="AgeText">距今多久。</param>
/// <param name="IsEmpty">空层(只改元数据,不占体积)。</param>
/// <param name="Weight">相对最大层的宽度比 0–1,用来画条。</param>
/// <param name="Rest">条右边剩下的那一段(<c>1 - Weight</c>)—— <see cref="WeightedStackPanel" /> 要两段才分得了宽度。</param>
/// <param name="Missing">层已经不在本地(基于其它镜像共享,daemon 只留了元数据)。</param>
public readonly record struct LayerRow(
    string Instruction,
    string SizeText,
    string AgeText,
    bool IsEmpty,
    double Weight,
    double Rest,
    bool Missing);

/// <summary>
/// 镜像详情抽屉。
/// <para>
/// 它回答的是运维在删镜像之前会问的三个问题:这东西是什么时候、怎么来的;
/// 它跑起来默认执行什么;以及那 1.2 GB 到底胖在哪一层。
/// 前两个靠 inspect,第三个只有 <c>history</c> 能答。
/// </para>
/// </summary>
public sealed class ImageDetailViewModel : ObservableObject
{
    private readonly DockerPanelViewModel _shell;
    private ImageRow _row;
    private bool _historyLoaded;

    /// <summary>建镜像详情。</summary>
    public ImageDetailViewModel(DockerPanelViewModel shell, ImagesPageViewModel page, ImageRow row)
    {
        _shell = shell;
        Owner = page;
        _row = row;
        SetTabCommand = new RelayCommand(p => p is ImageDetailTab tab ? SetTabAsync(tab) : Task.CompletedTask);
        CopyIdCommand = new RelayCommand(_ => shell.Context.Clipboard.SetTextAsync(row.Id, shell.Lifetime));
        RunCommand = new RelayCommand(_ => _shell.ShowRunContainerAsync(PrimaryReference));
        TagCommand = new RelayCommand(_ => Owner.TagCommand.Execute(_row));
        PushCommand = new RelayCommand(_ => Owner.PushCommand.Execute(_row));
        RemoveCommand = new RelayCommand(_ => Owner.RemoveCommand.Execute(_row));
        CloseCommand = new RelayCommand(_ => Owner.CloseDetailCommand.Execute(null));
    }

    /// <summary>镜像 id。</summary>
    public string ImageId => _row.Id;

    /// <summary>短 id。</summary>
    public string ShortId => _row.ShortId;

    /// <summary>标题(第一个标签,悬空镜像用短 id)。</summary>
    public string Title => _row.IsDangling ? $"<none> · {ShortId}" : $"{_row.Repository}:{_row.Tag}";

    /// <summary>
    /// 抽屉所在的页面。
    /// <para>抽屉的宽度与铺没铺满是**页面**的布局状态,界面要绑得到它。</para>
    /// </summary>
    public ImagesPageViewModel Owner { get; }

    /// <summary>拿去 <c>run</c> 的引用:有标签用标签,悬空镜像只能用 id。</summary>
    public string PrimaryReference =>
        _row.Summary.RepoTags?.FirstOrDefault(t => t != "<none>:<none>") ?? _row.Id;

    /// <summary>大小。</summary>
    public string SizeText => _row.SizeText;

    /// <summary>被多少容器用着。</summary>
    public string UsageText => _row.UsageText;

    /// <summary>当前页签。</summary>
    public ImageDetailTab Tab
    {
        get; private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsOverview), nameof(IsHistory), nameof(IsRaw));
            }
        }
    } = ImageDetailTab.Overview;

    /// <summary>在概览页。</summary>
    public bool IsOverview => Tab == ImageDetailTab.Overview;

    /// <summary>在层历史页。</summary>
    public bool IsHistory => Tab == ImageDetailTab.History;

    /// <summary>在原文页。</summary>
    public bool IsRaw => Tab == ImageDetailTab.Raw;

    /// <summary>基本信息。</summary>
    public ObservableCollection<DetailField> Basics { get; } = [];

    /// <summary>全部标签。</summary>
    public ObservableCollection<string> Tags { get; } = [];

    /// <summary>摘要引用。</summary>
    public ObservableCollection<string> Digests { get; } = [];

    /// <summary>默认执行什么。</summary>
    public ObservableCollection<DetailField> Runtime { get; } = [];

    /// <summary>环境变量。</summary>
    public ObservableCollection<DetailField> Environment { get; } = [];

    /// <summary>镜像标签(<c>LABEL</c>)。</summary>
    public ObservableCollection<DetailField> Labels { get; } = [];

    /// <summary>构建历史。</summary>
    public ObservableCollection<LayerRow> Layers { get; } = [];

    /// <summary>层历史读不到时的说明。</summary>
    public string HistoryNote
    {
        get; private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasHistoryNote));
            }
        }
    } = "";

    /// <summary>有没有要说的。</summary>
    public bool HasHistoryNote => HistoryNote.Length > 0;

    /// <summary>inspect 原文。</summary>
    public string RawInspect { get; private set => SetField(ref field, value); } = "";

    /// <summary>切页签。</summary>
    public RelayCommand SetTabCommand { get; }

    /// <summary>复制 id。</summary>
    public RelayCommand CopyIdCommand { get; }

    /// <summary>用它跑一个容器。</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>打标签。</summary>
    public RelayCommand TagCommand { get; }

    /// <summary>推送。</summary>
    public RelayCommand PushCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>关掉抽屉。</summary>
    public RelayCommand CloseCommand { get; }

    /// <summary>加载详情。</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        var inspect = await client.InspectImageAsync(ImageId, cancellationToken).ConfigureAwait(true);
        BuildOverview(inspect);
    }

    /// <summary>列表刷新后同步这一行。</summary>
    public void ApplyRow(ImageRow row)
    {
        _row = row;
        OnPropertiesChanged(nameof(Title), nameof(SizeText), nameof(UsageText), nameof(PrimaryReference));
    }

    private void BuildOverview(ImageInspect inspect)
    {
        Basics.Clear();
        Tags.Clear();
        Digests.Clear();
        Runtime.Clear();
        Environment.Clear();
        Labels.Clear();

        Basics.Add(new("镜像 id", ShortId));
        Basics.Add(new("大小", Humanize.Bytes(inspect.Size > 0 ? inspect.Size : _row.Summary.Size)));
        if (_row.Summary.SharedSize > 0)
        {
            Basics.Add(new("与其它镜像共享", Humanize.Bytes(_row.Summary.SharedSize)));
        }
        Basics.Add(new("创建", Humanize.AgoFromIso(inspect.Created)));
        var platform = string.Join("/", new[] { inspect.Os, inspect.Architecture, inspect.Variant }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (platform.Length > 0)
        {
            // 平台不匹配是"镜像拉下来了但跑不起来"最常见的原因,值得单独一行。
            Basics.Add(new("平台", platform));
        }
        if (inspect.DockerVersion is { Length: > 0 } dockerVersion)
        {
            Basics.Add(new("构建于 Docker", dockerVersion));
        }
        if (inspect.RootFS?.Layers is { Length: > 0 } layers)
        {
            Basics.Add(new("层数", layers.Length.ToString()));
        }
        Basics.Add(new("使用情况", UsageText, _row.Summary.Containers > 0 ? RowTone.Ok : RowTone.Idle));

        foreach (var tag in inspect.RepoTags ?? _row.Summary.RepoTags ?? [])
        {
            Tags.Add(tag);
        }
        foreach (var digest in inspect.RepoDigests ?? _row.Summary.RepoDigests ?? [])
        {
            Digests.Add(digest);
        }

        var config = inspect.Config;
        if (config?.Entrypoint is { Length: > 0 } entrypoint)
        {
            Runtime.Add(new("Entrypoint", string.Join(" ", entrypoint)));
        }
        if (config?.Cmd is { Length: > 0 } cmd)
        {
            Runtime.Add(new("Cmd", string.Join(" ", cmd)));
        }
        if (config?.WorkingDir is { Length: > 0 } workingDir)
        {
            Runtime.Add(new("工作目录", workingDir));
        }
        // 以 root 跑是个安全事实,不该只躺在原文里等人翻。
        Runtime.Add(config?.User is { Length: > 0 } user
            ? new("用户", user)
            : new("用户", "root(镜像未指定 USER)", RowTone.Warn));
        if (config?.ExposedPorts is { Count: > 0 } exposed)
        {
            Runtime.Add(new("暴露端口", string.Join(", ", exposed.Keys.OrderBy(k => k, StringComparer.Ordinal))));
        }

        foreach (var entry in config?.Env ?? [])
        {
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            Environment.Add(split > 0
                ? new(entry[..split], entry[(split + 1)..])
                : new(entry, ""));
        }
        foreach ((var key, var value) in config?.Labels ?? [])
        {
            Labels.Add(new(key, value));
        }
        OnPropertiesChanged(nameof(Title));
    }

    private async Task SetTabAsync(ImageDetailTab tab)
    {
        Tab = tab;
        switch (tab)
        {
            case ImageDetailTab.History when !_historyLoaded:
                await LoadHistoryAsync(_shell.Lifetime).ConfigureAwait(true);
                break;
            case ImageDetailTab.Raw when RawInspect.Length == 0:
                await LoadRawAsync(_shell.Lifetime).ConfigureAwait(true);
                break;
        }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        try
        {
            var history = await client.ImageHistoryAsync(ImageId, cancellationToken)
                                                     .ConfigureAwait(true);
            Layers.Clear();
            var largest = history.Length > 0 ? history.Max(h => h.Size) : 0;
            // daemon 返回的是从新到旧;Dockerfile 是从旧到新读的,按后者排更容易对上。
            foreach (var entry in history.Reverse())
            {
                var weight = largest > 0 ? Math.Clamp((double)entry.Size / largest, 0, 1) : 0;
                Layers.Add(new(
                    CleanInstruction(entry.CreatedBy),
                    entry.Size > 0 ? Humanize.Bytes(entry.Size) : "—",
                    Humanize.AgoFromUnix(entry.Created),
                    entry.Size == 0,
                    weight,
                    1 - weight,
                    entry.Id is "" or "<missing>"));
            }
            _historyLoaded = true;
            HistoryNote = Layers.Count == 0 ? "这个镜像没有历史记录。" : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Layers.Clear();
            HistoryNote = $"读不到构建历史:{ex.Message}";
        }
    }

    private async Task LoadRawAsync(CancellationToken cancellationToken)
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        try
        {
            RawInspect = await client.InspectImageRawAsync(ImageId, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RawInspect = ex.Message;
        }
    }

    /// <summary>
    /// 把 <c>history</c> 里那句指令收拾干净。
    /// <para>
    /// daemon 存的是构建器当时执行的完整命令,前面挂着 <c>/bin/sh -c #(nop) </c>
    /// 这样的脚手架 —— 对着一屏这种前缀,真正的 <c>COPY</c> / <c>ENV</c> 反而看不见了。
    /// </para>
    /// </summary>
    public static string CleanInstruction(string? createdBy)
    {
        var text = (createdBy ?? "").Trim();
        if (text.Length == 0)
        {
            return "(无记录)";
        }
        const string ShellNop = "/bin/sh -c #(nop) ";
        const string Shell = "/bin/sh -c ";
        if (text.StartsWith(ShellNop, StringComparison.Ordinal))
        {
            text = text[ShellNop.Length..];
        }
        else if (text.StartsWith(Shell, StringComparison.Ordinal))
        {
            // 真正的 RUN 层:补回 RUN,让它和上下那些 COPY/ENV 读起来是同一套语言。
            text = "RUN " + text[Shell.Length..];
        }
        // BuildKit 走的是另一套记法,前缀已经是 "RUN /bin/sh -c" 之类,不再动它。
        return text.Trim();
    }
}
