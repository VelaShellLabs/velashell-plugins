using System.Collections.ObjectModel;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>拉取对话框的三个状态。</summary>
public enum PullStage
{
    /// <summary>填表。</summary>
    Form,

    /// <summary>拉取中。</summary>
    Running,

    /// <summary>完成。</summary>
    Done
}

/// <summary>拉取进度里的一层。</summary>
public sealed class PullLayerItem(string id, string status, double progress, string sizeText) : ObservableObject
{
    /// <summary>层 id。</summary>
    public string Id { get; } = id;

    /// <summary>状态。</summary>
    public string Status { get; private set; } = status;

    /// <summary>进度 0–1。</summary>
    public double Progress { get; private set; } = progress;

    /// <summary>大小文本。</summary>
    public string SizeText { get; private set; } = sizeText;

    /// <summary>这一层是不是已经好了。</summary>
    public bool Complete => Progress >= 1;

    /// <summary>更新。</summary>
    public void Update(string status, double progress, string sizeText)
    {
        Status = status;
        Progress = progress;
        SizeText = sizeText;
        OnPropertiesChanged(nameof(Status), nameof(Progress), nameof(SizeText), nameof(Complete));
    }
}

/// <summary>
/// 拉取镜像。
/// <para>
/// 同一个对话框**原地换态**,不弹第二层:填表 → 拉取中 → 完成。
/// 「转入后台」把进度移交给顶栏的任务中心,任务继续跑 ——
/// 拉一个 2 GB 的镜像时,用户多半想一边等一边去看别的容器。
/// </para>
/// </summary>
public sealed class PullImageViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DockerPanelViewModel _shell;
    private readonly PullAggregator _aggregator = new();
    private readonly Dictionary<string, PullLayerItem> _layerMap = [];
    private CancellationTokenSource? _cts;
    private PanelTask? _task;
    private string _reference = "";
    private string _tag = "latest";
    private DateTimeOffset _startedAt;
    private long _lastBytes;
    private DateTimeOffset _lastSample;

    /// <summary>建对话框。</summary>
    public PullImageViewModel(DockerPanelViewModel shell, string? initialReference)
    {
        _shell = shell;
        if (!string.IsNullOrWhiteSpace(initialReference))
        {
            var colon = initialReference!.LastIndexOf(':');
            var slash = initialReference.LastIndexOf('/');
            if (colon > slash && colon > 0)
            {
                _reference = initialReference[..colon];
                _tag = initialReference[(colon + 1)..];
            }
            else
            {
                _reference = initialReference;
            }
        }
        StartCommand = new RelayCommand(_ => StartAsync(), _ => Stage == PullStage.Form && Reference.Trim().Length > 0);
        CancelCommand = new RelayCommand(_ => CancelAsync());
        BackgroundCommand = new RelayCommand(_ => _shell.CloseDialogCommand.Execute(null));
        CloseCommand = new RelayCommand(_ => _shell.CloseDialogCommand.Execute(null));
        _ = RefreshAuthAsync();
    }

    /// <summary>当前状态。</summary>
    public PullStage Stage
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsForm), nameof(IsRunning), nameof(IsDone), nameof(Title), nameof(ChipText));
                StartCommand.RaiseCanExecuteChanged();
            }
        }
    } = PullStage.Form;

    /// <summary>填表态。</summary>
    public bool IsForm => Stage == PullStage.Form;

    /// <summary>拉取中。</summary>
    public bool IsRunning => Stage == PullStage.Running;

    /// <summary>完成态。</summary>
    public bool IsDone => Stage == PullStage.Done;

    /// <summary>标题。</summary>
    public string Title => Stage switch
    {
        PullStage.Running => "拉取中",
        PullStage.Done => Error.Length > 0 ? "拉取失败" : "拉取完成",
        _ => "拉取镜像"
    };

    /// <summary>标题右侧的徽章文字。</summary>
    public string ChipText => Stage switch
    {
        PullStage.Running => "进行中",
        PullStage.Done => Error.Length > 0 ? "失败" : "成功",
        _ => ""
    };

    /// <summary>镜像引用(不含标签)。</summary>
    public string Reference
    {
        get => _reference;
        set
        {
            if (SetField(ref _reference, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                OnPropertiesChanged(nameof(FullReference), nameof(CommandPreview), nameof(CommandNote));
                _ = RefreshAuthAsync();
            }
        }
    }

    /// <summary>标签。</summary>
    public string Tag
    {
        get => _tag;
        set
        {
            if (SetField(ref _tag, value))
            {
                OnPropertiesChanged(nameof(FullReference), nameof(CommandPreview), nameof(CommandNote));
            }
        }
    }

    /// <summary>平台。</summary>
    public string Platform
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(CommandNote));
            }
        }
    } = "";

    /// <summary>拉取全部标签。</summary>
    public bool AllTags
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(FullReference), nameof(CommandPreview), nameof(CommandNote));
            }
        }
    }

    /// <summary>完整引用。</summary>
    public string FullReference => AllTags ? $"{Reference}(全部标签)" : $"{Reference}:{(Tag.Length > 0 ? Tag : "latest")}";

    /// <summary>
    /// 这一拉是拉到**哪台机器**上。
    /// <para>
    /// 与确认闸门同一个理由:同时开着生产与测试两个面板时,
    /// 一个不写清主机的「开始拉取」会把 8 GB 拉到错的那台机器上。
    /// </para>
    /// </summary>
    public string HostName => _shell.EndpointName;

    /// <summary>主机那一行右边的补充(传输方式 / 引擎版本)。</summary>
    public string HostDetail =>
        $"{_shell.EndpointDetail}{(_shell.EngineVersion.Length > 0 ? $" · {_shell.EngineVersion}" : "")}";

    /// <summary>等效命令。</summary>
    public string CommandPreview =>
        $"POST /images/create?fromImage={Reference}{(AllTags ? "" : $"&tag={(Tag.Length > 0 ? Tag : "latest")}")}";

    /// <summary>等价命令行。</summary>
    public string CommandNote =>
        $"等价于  docker pull {(AllTags ? "-a " : "")}{Reference}{(AllTags ? "" : $":{(Tag.Length > 0 ? Tag : "latest")}")}" +
        $"{(Platform.Trim().Length > 0 ? $" --platform {Platform.Trim()}" : "")}";

    /// <summary>仓库登录状态文本。</summary>
    public string AuthText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>仓库登录状态。</summary>
    public RegistryAuthState AuthState
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(AuthOk), nameof(AuthWarn), nameof(AuthTone));
            }
        }
    } = RegistryAuthState.NotRequired;

    /// <summary>凭据没问题。</summary>
    public bool AuthOk => AuthState is RegistryAuthState.Available or RegistryAuthState.NotRequired;

    /// <summary>凭据取不到。</summary>
    public bool AuthWarn => AuthState is RegistryAuthState.HelperOnly or RegistryAuthState.Missing;

    /// <summary>
    /// 凭据状态的语气,界面据此上色与选图标。
    /// <para>
    /// 三档而不是两档:<b>HelperOnly</b>(凭据交给了 credential helper,面板取不到)与
    /// <b>Missing</b>(压根没有)对用户是两件不同的事,而原来两者都跟"认证失败"一个样子 ——
    /// 前者往往拉公开镜像照样成功,后者一定失败。
    /// </para>
    /// </summary>
    public RowTone AuthTone => AuthState switch
    {
        RegistryAuthState.Available or RegistryAuthState.NotRequired => RowTone.Ok,
        RegistryAuthState.HelperOnly => RowTone.Warn,
        _ => RowTone.Danger
    };

    /// <summary>总进度 0–1。</summary>
    public double Progress
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(PercentText));
            }
        }
    }

    /// <summary>百分比文本。</summary>
    public string PercentText => $"{Progress * 100:0}%";

    /// <summary>速率文本。</summary>
    public string SpeedText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>层摘要。</summary>
    public string SummaryText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>字节进度文本。</summary>
    public string BytesText => _aggregator.TotalBytes > 0
        ? $"{Humanize.Bytes(_aggregator.CurrentBytes)} / {Humanize.Bytes(_aggregator.TotalBytes)}"
        : "";

    /// <summary>已复用的层数摘要。</summary>
    public string ReusedText => _aggregator.ReusedLayers > 0
        ? $"{_aggregator.ReusedLayers} 层已存在,已折叠"
        : "";

    /// <summary>有没有折叠掉的复用层。</summary>
    public bool HasReused => _aggregator.ReusedLayers > 0;

    /// <summary>正在动的层。</summary>
    public ObservableCollection<PullLayerItem> ActiveLayers { get; } = [];

    /// <summary>错误(拉取失败时 daemon 的原话)。</summary>
    public string Error
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(HasError), nameof(Title), nameof(ChipText));
            }
        }
    } = "";

    /// <summary>有没有出错。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>完成后的摘要:摘要串。</summary>
    public string DoneDigest
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>完成后的摘要:大小。</summary>
    public string DoneSize
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>完成后的摘要:用时。</summary>
    public string DoneElapsed
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>开始拉取。</summary>
    public RelayCommand StartCommand { get; }

    /// <summary>取消拉取。</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>转入后台。</summary>
    public RelayCommand BackgroundCommand { get; }

    /// <summary>关掉。</summary>
    public RelayCommand CloseCommand { get; }

    private async Task RefreshAuthAsync()
    {
        if (_shell.RegistryAuth is not { } auth || Reference.Trim().Length == 0)
        {
            AuthText = "";
            return;
        }
        try
        {
            var status = await auth.GetStatusAsync(Reference.Trim(), _shell.Lifetime).ConfigureAwait(true);
            AuthState = status.State;
            AuthText = status.State switch
            {
                RegistryAuthState.Available => $"{status.Registry} 已登录 · {status.Detail}",
                RegistryAuthState.NotRequired => $"{status.Registry} · {status.Detail}",
                RegistryAuthState.HelperOnly => $"{status.Registry} · {status.Detail}",
                _ => $"{status.Registry} · {status.Detail}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AuthText = "";
        }
    }

    private async Task StartAsync()
    {
        if (_shell.Client is not { } client)
        {
            return;
        }
        Stage = PullStage.Running;
        Error = "";
        _startedAt = DateTimeOffset.UtcNow;
        _lastSample = _startedAt;
        _lastBytes = 0;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_shell.Lifetime);
        _task = _shell.Tasks.Start("Docker.arrow-down-to-line", $"拉取 {FullReference}", indeterminate: false);
        var reference = Reference.Trim();
        var tag = Tag.Trim();
        var platform = Platform.Trim();
        var allTags = AllTags;
        try
        {
            var header = _shell.RegistryAuth is { } auth
                ? await auth.GetAuthHeaderAsync(reference, _cts.Token).ConfigureAwait(true)
                : null;
            await client.PullImageAsync(reference, tag, platform, allTags, header,
                new DirectProgress<PullProgressFrame>(OnFrame), _cts.Token).ConfigureAwait(true);
            _task.Finish(PanelTaskState.Succeeded, "完成", _aggregator.Summary);
            DoneElapsed = Humanize.Duration(DateTimeOffset.UtcNow - _startedAt);
            DoneSize = _aggregator.TotalBytes > 0
                ? $"{Humanize.Bytes(_aggregator.TotalBytes)}({_aggregator.LayerCount} 层,复用 {_aggregator.ReusedLayers} 层)"
                : $"{_aggregator.LayerCount} 层,全部复用";
            Stage = PullStage.Done;
            _shell.Feedback.Status(FeedbackKind.Success, $"已拉取 {FullReference} · 用时 {DoneElapsed}");
            await _shell.Images.RefreshAsync(_shell.Lifetime).ConfigureAwait(true);
            DoneDigest = _shell.Images.View.FirstOrDefault(r =>
                $"{r.Repository}:{r.Tag}" == $"{reference}:{(tag.Length > 0 ? tag : "latest")}")?.ShortId ?? "";
        }
        catch (OperationCanceledException)
        {
            _task?.Finish(PanelTaskState.Cancelled, "已取消");
            Error = "已取消。";
            Stage = PullStage.Done;
        }
        catch (Exception ex)
        {
            _task?.Finish(PanelTaskState.Failed, "失败", ex.Message);
            Error = ex is DockerApiException api ? api.Message : ex.Message;
            Stage = PullStage.Done;
        }
    }

    private void OnFrame(PullProgressFrame frame)
    {
        _aggregator.Accept(frame);
        Ui.Post(() =>
        {
            Progress = _aggregator.Progress;
            SummaryText = _aggregator.Summary;
            if (_task is { } task)
            {
                task.Progress = _aggregator.Progress;
                task.Detail = _aggregator.Summary;
            }
            UpdateSpeed();
            SyncLayers();
            OnPropertiesChanged(nameof(BytesText), nameof(ReusedText), nameof(HasReused));
        });
    }

    private void UpdateSpeed()
    {
        var now = DateTimeOffset.UtcNow;
        var span = now - _lastSample;
        if (span < TimeSpan.FromMilliseconds(700))
        {
            return;
        }
        var bytes = _aggregator.CurrentBytes;
        var delta = bytes - _lastBytes;
        _lastBytes = bytes;
        _lastSample = now;
        if (delta <= 0)
        {
            return;
        }
        var perSecond = delta / span.TotalSeconds;
        var remaining = _aggregator.TotalBytes - bytes;
        SpeedText = remaining > 0 && perSecond > 0
            ? $"{Humanize.Bytes((long)perSecond)}/s · 剩余 ~{Humanize.Duration(TimeSpan.FromSeconds(remaining / perSecond))}"
            : $"{Humanize.Bytes((long)perSecond)}/s";
    }

    /// <summary>
    /// 只把**正在动**的层摆进列表。复用的那些折成一行 ——
    /// 一次命中缓存的拉取能有三十层 "Already exists",逐条列出来只是噪音。
    /// </summary>
    private void SyncLayers()
    {
        foreach ((var id, var status, var progress, var sizeText) in _aggregator.Snapshot())
        {
            var complete = progress >= 1;
            var reused = status.Contains("Already exists", StringComparison.OrdinalIgnoreCase);
            if (reused)
            {
                continue;
            }
            if (_layerMap.TryGetValue(id, out var item))
            {
                item.Update(status, progress, sizeText);
            }
            else if (!complete || ActiveLayers.Count < 12)
            {
                var created = new PullLayerItem(Humanize.ShortId(id), status, progress, sizeText);
                _layerMap[id] = created;
                ActiveLayers.Add(created);
            }
        }
    }

    private async Task CancelAsync()
    {
        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(true);
        }
        _task?.Cancel();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // 关掉对话框**不取消任务** —— 进度移交给任务中心,拉取继续。
        if (_cts is { } cts && Stage != PullStage.Running)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            _cts = null;
        }
    }
}
