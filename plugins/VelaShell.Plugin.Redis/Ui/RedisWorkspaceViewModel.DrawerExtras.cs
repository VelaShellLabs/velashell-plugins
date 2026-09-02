using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;

namespace VelaShell.Plugin.Redis.Ui;

/// <summary>概览页吞吐图上的一根柱子。</summary>
public sealed class RedisThroughputSample : ObservableObject
{
    /// <summary>每秒命令数;这一拍取不到时为 -1。</summary>
    public required int Ops { get; init; }

    /// <summary>柱高(像素)。由视图模型按当前峰值换算 —— 视图里不做算术。</summary>
    public double BarHeight
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>这根柱子是不是当前窗口里的峰值(界面据此换成警示色)。</summary>
    public bool IsPeak
    {
        get;
        set => SetProperty(ref field, value);
    }
}

/// <summary>
/// 抽屉里新增的两页(监视 / 集群),以及概览页那条吞吐图。
/// <para>
/// 监视页是整套界面里最"空"的一页,也是最该存在的一页:它把
/// "<c>MONITOR</c> 在这条连接上跑不了"这个结论,连同三条能走的路,一次说清 ——
/// 而不是让用户敲下去、卡住、然后怀疑网络。
/// </para>
/// </summary>
public sealed partial class RedisWorkspaceViewModel
{
    private DispatcherTimer? _throughputTimer;

    /// <summary>当前是监视页。</summary>
    public bool IsMonitorTab => ActiveTab == RedisDrawerTab.Monitor;

    /// <summary>当前是集群页。</summary>
    public bool IsClusterTab => ActiveTab == RedisDrawerTab.Cluster;

    /// <summary>切到监视页。</summary>
    public AsyncCommand ShowMonitorCommand { get; private set; } = null!;

    /// <summary>切到集群页。</summary>
    public AsyncCommand ShowClusterCommand { get; private set; } = null!;

    // ── 监视(恒为空状态)────────────────────────────────────────

    /// <summary>监视页的标题。</summary>
    public string MonitorTitle => Loc["Redis_MonitorTitle"];

    /// <summary>监视页的正文:为什么跑不了。</summary>
    public string MonitorBody => Loc["Redis_MonitorBody"];

    /// <summary>三条出路的第一条:改看慢日志(点了就切过去)。</summary>
    public AsyncCommand MonitorGoSlowlogCommand { get; private set; } = null!;

    /// <summary>第二条:订阅键空间事件(点了切到订阅页并预填频道)。</summary>
    public AsyncCommand MonitorGoKeyspaceCommand { get; private set; } = null!;

    // ── 集群 ──────────────────────────────────────────────────────

    /// <summary>集群节点。</summary>
    public ObservableCollection<RedisClusterNode> ClusterNodes { get; } = [];

    /// <summary>集群页的空状态说明(不是集群 / 命令被禁 / 出错)。</summary>
    public string ClusterNotice
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasClusterNotice));
        }
    } = string.Empty;

    /// <summary>有说明要显示。</summary>
    public bool HasClusterNotice => ClusterNotice.Length > 0;

    /// <summary>集群页右上角的摘要。</summary>
    public string ClusterSummary
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>只看主节点。</summary>
    public bool ClusterMastersOnly
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>切换"只看主节点"。</summary>
    public AsyncCommand ToggleClusterMastersCommand { get; private set; } = null!;

    /// <summary>按槽位起点排序(关掉则按服务器给的拓扑顺序:主在前、从紧随其主)。</summary>
    public bool ClusterSortBySlot
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>切换排序。</summary>
    public AsyncCommand ToggleClusterSortCommand { get; private set; } = null!;

    /// <summary>把整份拓扑复制成一段可粘贴的文本。</summary>
    public AsyncCommand CopyClusterTopologyCommand { get; private set; } = null!;

    private RedisClusterView? _cluster;

    private async Task LoadClusterAsync()
    {
        try
        {
            _cluster = await _connection.ReadClusterAsync().ConfigureAwait(true);
            ApplyClusterFilter();
            if (!_cluster.Available)
            {
                // 单机实例上这不是错误,是形态。措辞必须和"命令被禁"分开。
                ClusterNotice = Loc["Redis_ClusterNotClustered"];
                ClusterSummary = string.Empty;
                return;
            }
            ClusterNotice = string.Empty;
            ClusterSummary = Loc.Format("Redis_ClusterSummary",
                _cluster.State.Length > 0 ? _cluster.State : "?",
                _cluster.KnownNodes >= 0 ? _cluster.KnownNodes.ToString("N0", CultureInfo.CurrentCulture) : "?",
                _cluster.SlotsAssigned >= 0 ? _cluster.SlotsAssigned.ToString("N0", CultureInfo.CurrentCulture) : "?");
        }
        catch (Exception ex)
        {
            ClusterNotice = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Reading CLUSTER NODES failed.", ex);
        }
    }

    private void ApplyClusterFilter()
    {
        IEnumerable<RedisClusterNode> nodes = _cluster?.Nodes ?? [];
        if (ClusterMastersOnly)
        {
            nodes = nodes.Where(node => node.IsMaster);
        }
        if (ClusterSortBySlot)
        {
            // 从节点没有槽位,按主的槽位跟在主后面排 —— 单独按空串排会把它们全甩到一头去。
            nodes = nodes.OrderBy(node => SlotKey(node), StringComparer.Ordinal);
        }
        ClusterNodes.Clear();
        foreach (RedisClusterNode node in nodes)
        {
            ClusterNodes.Add(node);
        }

        string SlotKey(RedisClusterNode node)
        {
            RedisClusterNode? anchor = node.IsMaster
                ? node
                : _cluster?.Nodes.FirstOrDefault(m => string.Equals(m.Id, node.MasterId, StringComparison.Ordinal));
            string slots = anchor?.Slots ?? string.Empty;
            // 数字前缀补零成定宽,免得 "10923" 排到 "5461" 前面。
            string head = slots.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "99999";
            return head.PadLeft(6, '0') + (node.IsMaster ? "0" : "1");
        }
    }

    private Task CopyClusterTopologyAsync()
    {
        if (ClusterNodes.Count == 0)
        {
            StatusMessage = Loc["Redis_ClusterNotClustered"];
            return Task.CompletedTask;
        }
        // 制表符分隔:粘进工单、聊天窗口、表格都还能读。
        var text = new StringBuilder();
        foreach (RedisClusterNode node in ClusterNodes)
        {
            text.Append(node.Id).Append('\t').Append(node.Address).Append('\t')
                .Append(node.RoleText).Append('\t').Append(node.Slots).Append('\t')
                .Append(node.LinkState).Append('\n');
        }
        CopyRequested?.Invoke(this, text.ToString().TrimEnd('\n'));
        StatusMessage = Loc.Format("Redis_BatchCopied",
            ClusterNodes.Count.ToString("N0", CultureInfo.CurrentCulture));
        return Task.CompletedTask;
    }

    // ── 概览:吞吐图 ──────────────────────────────────────────────

    /// <summary>近 N 秒的吞吐采样(最旧在前,与横轴一致)。</summary>
    public ObservableCollection<RedisThroughputSample> Throughput { get; } = [];

    /// <summary>图表标题。</summary>
    public string ThroughputTitle =>
        Loc.Format("Redis_OverviewThroughput", ThroughputWindow.ToString(CultureInfo.CurrentCulture));

    /// <summary>图表右上角的三个数。</summary>
    public string ThroughputStats
    {
        get;
        private set => SetProperty(ref field, value);
    } = string.Empty;

    /// <summary>还没有样本(界面据此显示一句说明而不是一片空白)。</summary>
    public bool HasThroughput => Throughput.Count > 0;

    /// <summary>横轴左端刻度。数字由采样窗口算出来,不写死 —— 窗口改了刻度必须跟着改。</summary>
    public string ThroughputAxisStart => $"−{ThroughputWindow.ToString(CultureInfo.CurrentCulture)}s";

    /// <summary>横轴中点刻度。</summary>
    public string ThroughputAxisMid => $"−{(ThroughputWindow / 2).ToString(CultureInfo.CurrentCulture)}s";

    /// <summary>横轴右端刻度("现在")。</summary>
    public string ThroughputAxisNow => Loc["Redis_Now"];

    /// <summary>图表窗口(秒)。</summary>
    private const int ThroughputWindow = 40;

    /// <summary>柱体区域的高度(像素),与 AXAML 里那一格一致。</summary>
    private const double ThroughputBarArea = 80;

    /// <summary>
    /// 只在"抽屉开着 + 停在概览页"时采样。
    /// <para>后台持续对服务器发 <c>INFO stats</c> 是在替用户制造负载,而他压根没看这一页。</para>
    /// </summary>
    private void SyncThroughputSampling()
    {
        bool wanted = IsDrawerOpen && IsOverviewTab && !_disposed;
        if (!wanted)
        {
            StopThroughputSampling();
            return;
        }
        if (_throughputTimer is not null)
        {
            return;
        }
        _throughputTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        _throughputTimer.Tick += (_, _) => _ = SampleThroughputAsync();
        _throughputTimer.Start();
        _ = SampleThroughputAsync();
    }

    private void StopThroughputSampling()
    {
        _throughputTimer?.Stop();
        _throughputTimer = null;
    }

    private async Task SampleThroughputAsync()
    {
        if (_disposed)
        {
            return;
        }
        int ops;
        try
        {
            ops = await _connection.ReadInstantaneousOpsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Info($"instantaneous_ops_per_sec unavailable: {ex.Message}");
            StopThroughputSampling();
            return;
        }
        if (ops < 0)
        {
            // 拿不到就停,并且**不补一根 0 的柱子** —— 那会被读成"此刻真的没有请求"。
            StopThroughputSampling();
            return;
        }
        Throughput.Add(new() { Ops = ops });
        while (Throughput.Count > ThroughputWindow)
        {
            Throughput.RemoveAt(0);
        }
        RescaleThroughput();
    }

    /// <summary>按窗口内的峰值重算每根柱子的高度,并刷新那三个数。</summary>
    private void RescaleThroughput()
    {
        if (Throughput.Count == 0)
        {
            ThroughputStats = string.Empty;
            RaisePropertyChanged(nameof(HasThroughput));
            return;
        }
        int peak = Throughput.Max(sample => sample.Ops);
        double mean = Throughput.Average(sample => sample.Ops);
        foreach (RedisThroughputSample sample in Throughput)
        {
            // 峰值为 0 时给所有柱子一个可见的最小高度:一整排看不见的柱子读不出
            // "吞吐是 0"还是"图表坏了"。
            sample.BarHeight = peak > 0
                ? Math.Max(2, sample.Ops / (double)peak * ThroughputBarArea)
                : 2;
            sample.IsPeak = peak > 0 && sample.Ops == peak;
        }
        ThroughputStats = Loc.Format("Redis_OverviewChartStats",
            peak.ToString("N0", CultureInfo.CurrentCulture),
            mean.ToString("N0", CultureInfo.CurrentCulture),
            Throughput[^1].Ops.ToString("N0", CultureInfo.CurrentCulture));
        RaisePropertyChanged(nameof(HasThroughput));
    }

    /// <summary>没有样本时的说明。</summary>
    public string ThroughputEmptyNotice => Loc["Redis_OverviewNoSamples"];

    // ── 慢日志 / 客户端 / 订阅 的工具条 ───────────────────────────

    /// <summary>慢日志页右上角的摘要。</summary>
    public string SlowlogSummary => Slowlog.Count > 0
        ? Loc.Format("Redis_SlowlogTake", Slowlog.Count.ToString("N0", CultureInfo.CurrentCulture))
        : string.Empty;

    /// <summary>
    /// 慢日志的阈值(<c>slowlog-log-slower-than</c>)。
    /// <para>它决定了这一页**看得见什么** —— 阈值 10ms 的实例上,一条 8ms 的慢查询根本不会被记下来。
    /// 不显示出来,用户会把"没有慢日志"读成"没有慢查询"。<c>CONFIG</c> 被禁时留空。</para>
    /// </summary>
    public string SlowlogThresholdText
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasSlowlogThreshold));
        }
    } = string.Empty;

    /// <summary>阈值取到了(取不到时那一格整个不出现)。</summary>
    public bool HasSlowlogThreshold => SlowlogThresholdText.Length > 0;

    /// <summary>慢日志的保留条数(<c>slowlog-max-len</c>)。</summary>
    public string SlowlogMaxLengthText
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(HasSlowlogMaxLength));
        }
    } = string.Empty;

    /// <summary>保留条数取到了。</summary>
    public bool HasSlowlogMaxLength => SlowlogMaxLengthText.Length > 0;

    /// <summary>慢日志页的脚注。</summary>
    public string SlowlogNote => Loc["Redis_SlowlogNote"];

    private async Task LoadSlowlogConfigAsync()
    {
        try
        {
            (long threshold, long maxLength) = await _connection.ReadSlowlogConfigAsync().ConfigureAwait(true);
            // 拿不到就留空,**不填默认值** —— 一个写着 10000 µs 的假阈值比空白危险得多。
            SlowlogThresholdText = threshold >= 0
                ? Loc.Format("Redis_SlowlogThreshold", threshold.ToString("N0", CultureInfo.CurrentCulture))
                : string.Empty;
            SlowlogMaxLengthText = maxLength >= 0
                ? Loc.Format("Redis_SlowlogTake", maxLength.ToString("N0", CultureInfo.CurrentCulture))
                : string.Empty;
        }
        catch (Exception ex)
        {
            SlowlogThresholdText = string.Empty;
            SlowlogMaxLengthText = string.Empty;
            _log.Info($"CONFIG GET slowlog-* unavailable: {ex.Message}");
        }
    }

    // ── 客户端页的过滤 ────────────────────────────────────────────

    /// <summary>服务端给的完整客户端列表(过滤前)。</summary>
    private readonly List<RedisClientEntry> _allClients = [];

    /// <summary>按名称/地址过滤。</summary>
    public string ClientFilter
    {
        get;
        set
        {
            SetProperty(ref field, value);
            ApplyClientFilter();
        }
    } = string.Empty;

    /// <summary>只看非空闲的连接(空闲 0 秒的那些)。</summary>
    public bool ClientsBusyOnly
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>切换"只看非空闲"。</summary>
    public AsyncCommand ToggleClientsBusyCommand { get; private set; } = null!;

    /// <summary>
    /// 在客户端侧过滤。<b>不重新问服务器</b> —— <c>CLIENT LIST</c> 没有过滤参数,
    /// 而为了筛一下就再拉一遍几百条连接是纯浪费。
    /// </summary>
    private void ApplyClientFilter()
    {
        string needle = ClientFilter.Trim();
        Clients.Clear();
        foreach (RedisClientEntry entry in _allClients)
        {
            if (ClientsBusyOnly && entry.Idle > TimeSpan.Zero)
            {
                continue;
            }
            if (needle.Length > 0
                && !entry.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !entry.Address.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !entry.LastCommand.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Clients.Add(entry);
        }
        SelectedClient = null;
        RaisePropertyChanged(nameof(ClientsSummary));
    }

    /// <summary>客户端页右上角的摘要:筛过之后要同时报"筛出多少 / 一共多少"。</summary>
    public string ClientsSummary => _allClients.Count == 0
        ? string.Empty
        : Clients.Count == _allClients.Count
            ? Loc.Format("Redis_ClientsSummary", _allClients.Count.ToString("N0", CultureInfo.CurrentCulture))
            : $"{Clients.Count.ToString("N0", CultureInfo.CurrentCulture)} / "
              + Loc.Format("Redis_ClientsSummary", _allClients.Count.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>客户端页的脚注。</summary>
    public string ClientsNote => Loc["Redis_ClientsNote"];

    /// <summary>订阅页暂停接收(消息仍在服务端流走,只是不再往列表里塞)。</summary>
    public bool IsPubSubPaused
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(PubSubPauseLabel));
        }
    }

    /// <summary>暂停 / 继续按钮的文案。</summary>
    public string PubSubPauseLabel => Loc[IsPubSubPaused ? "Redis_PubSubResume" : "Redis_PubSubPause"];

    /// <summary>订阅页右上角:收了多少条、缓冲上限多少。</summary>
    public string PubSubCountText => Loc.Format("Redis_PubSubCount",
        _messagesSeen.ToString("N0", CultureInfo.CurrentCulture),
        MaxMessages.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>自此次会话开始一共收到过多少条(列表被截断也不影响这个数)。</summary>
    private long _messagesSeen;

    /// <summary>暂停 / 继续。</summary>
    public AsyncCommand TogglePubSubPauseCommand { get; private set; } = null!;

    /// <summary>清空消息列表。</summary>
    public AsyncCommand ClearMessagesCommand { get; private set; } = null!;

    /// <summary>
    /// 自动滚到最新一条。
    /// <para>新消息插在**表头**(最新在前),所以"自动滚动"= 把视野钉在第一行;
    /// 关掉它,涌进来的消息就不会把你正在读的那一条推走。</para>
    /// </summary>
    public bool PubSubAutoScroll
    {
        get;
        private set => SetProperty(ref field, value);
    } = true;

    /// <summary>切换自动滚动。</summary>
    public AsyncCommand TogglePubSubAutoScrollCommand { get; private set; } = null!;

    /// <summary>把已收到的消息导成 JSONL。</summary>
    public AsyncCommand ExportMessagesCommand { get; private set; } = null!;

    /// <summary>有新消息到达且自动滚动开着 —— 视图据此把列表滚回顶部。</summary>
    public event EventHandler? MessagesScrollRequested;

    private async Task ExportMessagesAsync()
    {
        if (Messages.Count == 0)
        {
            StatusMessage = Loc["Redis_BatchNothing"];
            return;
        }
        string path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "redis-export",
            $"pubsub-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jsonl");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // 一行一条 JSON:给人看与喂给脚本都直接可用。
            IEnumerable<string> lines = Messages.Reverse().Select(message => System.Text.Json.JsonSerializer.Serialize(
                new { at = message.At, channel = message.Channel, payload = message.Payload },
                new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
            await System.IO.File.WriteAllLinesAsync(path, lines).ConfigureAwait(true);
            StatusMessage = Loc.Format("Redis_ExportDone",
                Messages.Count.ToString("N0", CultureInfo.CurrentCulture),
                new System.IO.FileInfo(path).Length.ToString("N0", CultureInfo.CurrentCulture) + " B",
                path);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("Redis_Error", ex.Message);
            _log.Error("Exporting pub/sub messages failed.", ex);
        }
    }

    // ── 内存分析:聚合粒度 ────────────────────────────────────────

    /// <summary>按键名的前几段聚合(1 或 2)。</summary>
    public int MemoryPrefixSegments
    {
        get;
        private set
        {
            SetProperty(ref field, value);
            RaisePropertyChanged(nameof(MemoryGroupingLabel));
        }
    } = 1;

    /// <summary>聚合粒度按钮的文案。</summary>
    public string MemoryGroupingLabel =>
        Loc.Format("Redis_MemoryGrouping", MemoryPrefixSegments.ToString(CultureInfo.CurrentCulture));

    /// <summary>在 1 段 / 2 段之间切换,并重跑一次抽样。</summary>
    public AsyncCommand ToggleMemoryGroupingCommand { get; private set; } = null!;

    /// <summary>内存分析页的抽样规模文案。</summary>
    public string MemorySampleSizeText => Loc.Format("Redis_MemorySampleSize",
        _connection.Settings.ScanBudget.ToString("N0", CultureInfo.CurrentCulture));

    private void InitializeDrawerExtras()
    {
        ToggleClusterMastersCommand = new(() =>
        {
            ClusterMastersOnly = !ClusterMastersOnly;
            ApplyClusterFilter();
            return Task.CompletedTask;
        });
        ToggleClusterSortCommand = new(() =>
        {
            ClusterSortBySlot = !ClusterSortBySlot;
            ApplyClusterFilter();
            return Task.CompletedTask;
        });
        CopyClusterTopologyCommand = new(CopyClusterTopologyAsync, () => ClusterNodes.Count > 0);
        ToggleClientsBusyCommand = new(() =>
        {
            ClientsBusyOnly = !ClientsBusyOnly;
            ApplyClientFilter();
            return Task.CompletedTask;
        });
        TogglePubSubAutoScrollCommand = new(() =>
        {
            PubSubAutoScroll = !PubSubAutoScroll;
            return Task.CompletedTask;
        });
        ExportMessagesCommand = new(ExportMessagesAsync, () => Messages.Count > 0);
        ToggleMemoryGroupingCommand = new(() =>
        {
            MemoryPrefixSegments = MemoryPrefixSegments == 1 ? 2 : 1;
            // 换粒度就得重算 —— 拿旧结果按新粒度重新分桶是做不到的(桶里已经没有键名了)。
            return IsSamplingMemory ? Task.CompletedTask : SampleMemoryAsync();
        });
        TogglePubSubPauseCommand = new(() =>
        {
            IsPubSubPaused = !IsPubSubPaused;
            return Task.CompletedTask;
        });
        ClearMessagesCommand = new(() =>
        {
            Messages.Clear();
            RaisePropertyChanged(nameof(PubSubCountText));
            return Task.CompletedTask;
        });
        MonitorGoSlowlogCommand = new(() => SwitchTabAsync(RedisDrawerTab.Slowlog));
        MonitorGoKeyspaceCommand = new(async () =>
        {
            // 预填的是**当前库**的键事件频道:写死 db0 在 db3 上就是一句谎话。
            ChannelDraft = $"__keyevent@{CurrentDatabase.ToString(CultureInfo.InvariantCulture)}__:*";
            await SwitchTabAsync(RedisDrawerTab.PubSub).ConfigureAwait(true);
        });
    }

    /// <summary>订阅页收到一条消息(由 <see cref="SubscribeAsync" /> 在 UI 线程上调用)。</summary>
    private void OnMessageReceived(string channel, string payload)
    {
        _messagesSeen++;
        RaisePropertyChanged(nameof(PubSubCountText));
        if (IsPubSubPaused)
        {
            return;
        }
        Messages.Insert(0, new(DateTimeOffset.Now, channel, payload));
        while (Messages.Count > MaxMessages)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
        ExportMessagesCommand.RaiseCanExecuteChanged();
        if (PubSubAutoScroll)
        {
            // 最新在表头,所以"跟到最新"就是滚回顶部。关掉它,涌进来的消息不会把
            // 你正在读的那一条推走。
            MessagesScrollRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
