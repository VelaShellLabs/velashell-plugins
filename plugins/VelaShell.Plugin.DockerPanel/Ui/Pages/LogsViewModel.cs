using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>合并流里的一个来源。</summary>
/// <param name="ContainerId">容器 id。</param>
/// <param name="Name">容器名(显示在来源列上)。</param>
/// <param name="Tty">容器是否分配了 TTY(决定日志流要不要解多路复用帧)。</param>
public readonly record struct LogSource(string ContainerId, string Name, bool Tty);

/// <summary>日志里的一行。</summary>
public sealed class LogLineItem(string timestamp, string source, int sourceIndex, bool isError, string text)
{
    /// <summary>时间戳文本;没开时间戳时为空。</summary>
    public string Timestamp { get; } = timestamp;

    /// <summary>来源容器名;单容器时为空。</summary>
    public string Source { get; } = source;

    /// <summary>
    /// 来源在当前来源列表里的序号。界面按它取一个稳定的颜色 ——
    /// 合并五条流时,靠颜色分辨来源比逐行读容器名快得多。
    /// </summary>
    public int SourceIndex { get; } = sourceIndex;

    /// <summary>有没有来源列。</summary>
    public bool HasSource => Source.Length > 0;

    /// <summary>来自标准错误。</summary>
    public bool IsError { get; } = isError;

    /// <summary>正文。</summary>
    public string Text { get; } = text;

    /// <summary>认出来的级别。</summary>
    public LogLevel Level { get; } = LogLevels.Detect(text);

    /// <summary>级别文字;认不出来时为空。</summary>
    public string LevelLabel => LogLevels.Label(Level);

    /// <summary>有没有级别标记。</summary>
    public bool HasLevel => Level != LogLevel.None;

    /// <summary>
    /// 这一行的语气。标准错误一律按错误算 —— 哪怕正文里没有 ERROR 字样,
    /// 它走的是 stderr 这件事本身就是信息。
    /// </summary>
    public RowTone Tone => IsError ? RowTone.Danger : LogLevels.Tone(Level);

    /// <summary>是不是当前搜索的命中行。</summary>
    public bool Matched { get; set; }
}

/// <summary>来源选择器里的一项。</summary>
public sealed class LogSourceItem(LogSource source, string status, RowTone tone, int index) : ObservableObject
{

    /// <summary>底层来源。</summary>
    public LogSource Source { get; } = source;

    /// <summary>容器名。</summary>
    public string Name => Source.Name;

    /// <summary>状态短语(运行中 / 已停止 / 退出 1…)。</summary>
    public string Status { get; } = status;

    /// <summary>状态语气。</summary>
    public RowTone Tone { get; } = tone;

    /// <summary>颜色序号(与 <see cref="LogLineItem.SourceIndex" /> 对齐)。</summary>
    public int Index { get; set; } = index;

    /// <summary>过滤后是否显示。</summary>
    public bool Visible
    {
        get;
        set => SetField(ref field, value);
    } = true;

    /// <summary>选中没有。</summary>
    public bool Selected
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                SelectionChanged?.Invoke();
            }
        }
    }

    /// <summary>勾选变了。</summary>
    public event Action? SelectionChanged;
}

/// <summary>
/// 日志视图。
/// <para>
/// 「跟随」是一条真正的 <c>docker logs -f</c>,不是按间隔补拉:新行到达即出现。
/// 刷屏的容器一秒能吐几千行,所以界面**攒批**更新(每 120ms 交一次),
/// 并把缓冲钉在 512 KB 上 —— <b>按行截断</b>,半行开头的日志比少几行更难读。
/// </para>
/// </summary>
public sealed partial class LogsViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>缓冲上限。</summary>
    private const int MaxBufferedChars = 512 * 1024;

    /// <summary>界面上最多留多少行。</summary>
    private const int MaxLines = 5000;

    private readonly DockerPanelViewModel shell;
    private readonly Func<bool> ttyAccessor;
    private readonly List<LogLineItem> _pending = [];
    private readonly Lock _pendingGate = new();
    private readonly List<LogSource> _sources = [];
    private DateTimeOffset _startedAt;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _flushTimer;
    private Regex? _filter;
    private long _bufferedChars;
    private bool _started;

    /// <summary>单容器(详情抽屉的日志页签)。</summary>
    public LogsViewModel(DockerPanelViewModel shell, string containerId, Func<bool> ttyAccessor)
        : this(shell, ttyAccessor)
    {
        _sources.Add(new(containerId, "", ttyAccessor()));
    }

    /// <summary>多容器合并流(容器页的日志模式)。</summary>
    public LogsViewModel(DockerPanelViewModel shell, IEnumerable<LogSource> sources)
        : this(shell, () => false)
    {
        _sources.AddRange(sources);
    }

    private LogsViewModel(DockerPanelViewModel shell, Func<bool> ttyAccessor)
    {
        this.shell = shell;
        this.ttyAccessor = ttyAccessor;
    }

    /// <summary>界面上的行。</summary>
    public ObservableCollection<LogLineItem> Lines { get; } = [];

    /// <summary>当前接着几条流。</summary>
    public int SourceCount => _sources.Count;

    /// <summary>合并了多于一条流(界面据此显示来源列)。</summary>
    public bool IsMerged => _sources.Count > 1;

    /// <summary>当前来源的名字(顶部 chips 用)。</summary>
    public IReadOnlyList<LogSource> Sources => _sources;

    /// <summary>
    /// 顶部 chip 上那个 × 该做什么。由拥有来源清单的那一方(容器页)装上。
    /// <para>
    /// 不在这里直接改 <see cref="_sources" />:左边面板的勾选才是唯一的真相,
    /// 两处各改各的,取消一个来源之后左边还亮着,就成了两套互相矛盾的状态。
    /// </para>
    /// </summary>
    public Func<LogSource, Task>? SourceRemover { get; set; }

    /// <summary>能不能从 chip 上摘掉来源(单容器的日志页签不能)。</summary>
    public bool CanRemoveSources => SourceRemover is not null;

    /// <summary>从合并流里摘掉一条来源。</summary>
    public RelayCommand RemoveSourceCommand => field ??= new(p =>
        p is LogSource source && SourceRemover is { } remover ? remover(source) : Task.CompletedTask);

    /// <summary>底部那句"几条流、跑了多久"。</summary>
    public string StreamSummary
    {
        get
        {
            if (_sources.Count == 0)
            {
                return "没有选中任何来源";
            }
            var elapsed = _startedAt == default ? "" : $" · 已运行 {Humanize.Duration(DateTimeOffset.UtcNow - _startedAt)}";
            return $"docker logs{(Follow ? " -f" : "")} · {_sources.Count} 条流{elapsed}";
        }
    }

    /// <summary>
    /// 换一组来源并重开流。
    /// <para>
    /// 换来源会清屏:合并流的行序是**到达顺序**,把旧来源的行留着而新来源从头补 tail,
    /// 拼出来的时间线是假的。
    /// </para>
    /// </summary>
    public async Task SetSourcesAsync(IEnumerable<LogSource> sources)
    {
        _sources.Clear();
        _sources.AddRange(sources);
        OnPropertiesChanged(nameof(SourceCount), nameof(IsMerged), nameof(Sources), nameof(StreamSummary));
        if (_started)
        {
            await RestartAsync().ConfigureAwait(true);
        }
    }

    /// <summary>是否跟随(<c>-f</c>)。</summary>
    public bool Follow
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(FollowLabel));
                _ = RestartAsync();
            }
        }
    } = true;

    /// <summary>跟随按钮上的字。开着时说"跟随中" —— 这颗按钮本身就是状态灯。</summary>
    public string FollowLabel => Follow ? "跟随中" : "跟随";

    /// <summary>只看标准错误。</summary>
    public bool ErrorsOnly
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>搜索词。</summary>
    public string Search
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                CompileFilter();
                ApplyFilter();
            }
        }
    } = "";

    /// <summary>搜索按正则解释。</summary>
    public bool UseRegex
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                CompileFilter();
                ApplyFilter();
            }
        }
    }

    /// <summary>命中数。</summary>
    public int MatchCount
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(MatchText));
            }
        }
    }

    /// <summary>命中数文本。</summary>
    public string MatchText => Search.Length == 0 ? "" : $"{MatchCount} 处";

    /// <summary>底部状态条那句话。</summary>
    public string StatusText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>缓冲用量文本。</summary>
    public string BufferText => $"缓冲 {Humanize.Bytes(_bufferedChars)} / {Humanize.Bytes(MaxBufferedChars)}(按行截断)";

    /// <summary>行数文本。</summary>
    public string LineCountText => $"{Lines.Count:N0} 行";

    /// <summary>补多少行历史。</summary>
    public string Tail
    {
        get => shell.Settings.LogTail;
        set
        {
            shell.Settings.LogTail = value;
            OnPropertyChanged();
            _ = RestartAsync();
        }
    }

    /// <summary>带时间戳。</summary>
    public bool Timestamps
    {
        get => shell.Settings.LogTimestamps;
        set
        {
            shell.Settings.LogTimestamps = value;
            OnPropertyChanged();
            _ = RestartAsync();
        }
    }

    /// <summary>自动换行。</summary>
    public bool Wrap
    {
        get => shell.Settings.LogWrap;
        set
        {
            shell.Settings.LogWrap = value;
            OnPropertyChanged();
        }
    }

    /// <summary>切换跟随。</summary>
    public RelayCommand ToggleFollowCommand => field ??= new(_ => Follow = !Follow);

    /// <summary>清屏。</summary>
    public RelayCommand ClearCommand => field ??= new(_ =>
    {
        Lines.Clear();
        _bufferedChars = 0;
        OnPropertiesChanged(nameof(BufferText), nameof(LineCountText));
    });

    /// <summary>复制全部。</summary>
    public RelayCommand CopyCommand => field ??= new(_ =>
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            if (line.Timestamp.Length > 0)
            {
                sb.Append(line.Timestamp).Append(' ');
            }
            sb.AppendLine(line.Text);
        }
        return shell.Context.Clipboard.SetTextAsync(sb.ToString(), shell.Lifetime);
    });

    /// <summary>把当前缓冲里的行存成本地文件。</summary>
    public RelayCommand DownloadCommand => field ??= new(_ => DownloadAsync());

    /// <summary>能不能弹本地文件对话框。</summary>
    public bool CanPickFiles => FilePicker.IsAvailable;

    /// <summary>
    /// 导出当前缓冲。
    /// <para>
    /// 导的是**界面上这些行**,不是重新去 daemon 拉一遍 —— 用户按下按钮时看到的那一屏
    /// (含筛选与来源合并的结果)才是他要的东西;重拉会得到一份不一样的、没有来源标记的日志。
    /// </para>
    /// </summary>
    private async Task DownloadAsync()
    {
        var suggested = _sources.Count == 1 && _sources[0].Name.Length > 0
            ? $"{_sources[0].Name}.log"
            : $"docker-logs-{_sources.Count}.log";
        var target = await FilePicker
            .PickSaveAsync("保存日志", suggested, "log").ConfigureAwait(true);
        if (target is null)
        {
            return;
        }
        try
        {
            await using var output = await target.OpenWriteAsync().ConfigureAwait(true);
            await using var writer = new StreamWriter(output, Encoding.UTF8);
            foreach (var line in Lines)
            {
                if (line.Timestamp.Length > 0)
                {
                    await writer.WriteAsync(line.Timestamp + " ").ConfigureAwait(true);
                }
                if (line.HasSource)
                {
                    await writer.WriteAsync($"[{line.Source}] ").ConfigureAwait(true);
                }
                await writer.WriteLineAsync(line.Text).ConfigureAwait(true);
            }
            shell.Feedback.Notify(FeedbackKind.Success, "日志已保存", $"{target.Name} · {Lines.Count:N0} 行");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            shell.Feedback.ReportError("保存日志", ex);
        }
    }

    /// <summary>设置补历史的行数。</summary>
    public RelayCommand SetTailCommand => field ??= new(p =>
    {
        if (p is string tail)
        {
            Tail = tail;
        }
    });

    /// <summary>第一次进这一页时才起流。</summary>
    public async Task EnsureStartedAsync()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        await RestartAsync().ConfigureAwait(true);
    }

    /// <summary>按当前选项重开一条日志流。</summary>
    public async Task RestartAsync()
    {
        if (!_started || shell.Client is not { } client)
        {
            return;
        }
        await StopAsync().ConfigureAwait(true);
        Lines.Clear();
        _bufferedChars = 0;
        if (_sources.Count == 0)
        {
            StatusText = "没有选中任何来源";
            OnPropertiesChanged(nameof(BufferText), nameof(LineCountText), nameof(StreamSummary));
            return;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shell.Lifetime);
        var token = _cts.Token;
        _startedAt = DateTimeOffset.UtcNow;
        StartFlushTimer();
        StatusText = Follow ? "跟随中 · 新行即时到达" : "已加载历史";
        OnPropertyChanged(nameof(StreamSummary));
        var timestamps = Timestamps;
        var tail = Tail;
        var live = _sources.Count;

        // 每个来源一条独立的流,全部写进同一个待刷队列 ——
        // 合并的顺序就是**到达顺序**,不按时间戳重排:重排要缓冲、要等,
        // 而"跟随"这个功能的全部意义就是不等。
        for (var i = 0; i < _sources.Count; i++)
        {
            var source = _sources[i];
            var index = i;
            var tty = _sources.Count == 1 ? ttyAccessor() : source.Tty;
            _ = Task.Run(async () =>
            {
                try
                {
                    await client.StreamLogsAsync(source.ContainerId, tty, Follow, tail, timestamps, null,
                        line => Enqueue(line, timestamps, source, index), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 一条流断了不该把整块面板说成失败 —— 另外几条还在跑。
                    Ui.Post(() => StatusText = _sources.Count > 1
                        ? $"{source.Name} 的日志流断开:{ex.Message}"
                        : $"日志流断开:{ex.Message}");
                    return;
                }
                Ui.Post(() =>
                {
                    if (--live <= 0)
                    {
                        StatusText = Follow ? "全部流已结束(容器可能停了)" : "已加载历史";
                    }
                });
            }, token);
        }
    }

    private void Enqueue(DockerLogLine line, bool timestamps, LogSource source, int index)
    {
        var item = new LogLineItem(
            timestamps && line.Timestamp is { } stamp ? stamp.ToLocalTime().ToString("HH:mm:ss.fff") : "",
            // 只有合并流才显示来源列:单容器时那一列每行都一样,纯属占地方。
            _sources.Count > 1 ? source.Name : "",
            index,
            line.Kind == DockerStreamKind.StdErr,
            line.Text);
        lock (_pendingGate)
        {
            _pending.Add(item);
        }
    }

    private void StartFlushTimer()
    {
        _flushTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        if (_flushTimer.IsEnabled)
        {
            return;
        }
        _flushTimer.Tick += OnFlush;
        _flushTimer.Start();
    }

    private void OnFlush(object? sender, EventArgs e)
    {
        List<LogLineItem> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }
            batch = [.. _pending];
            _pending.Clear();
        }
        foreach (var item in batch)
        {
            if (ErrorsOnly && !item.IsError)
            {
                continue;
            }
            item.Matched = _filter?.IsMatch(item.Text) ?? false;
            Lines.Add(item);
            _bufferedChars += item.Text.Length + 1;
        }
        // 按行截断:从最旧的开始扔,直到缓冲回到上限之内。
        while ((_bufferedChars > MaxBufferedChars || Lines.Count > MaxLines) && Lines.Count > 0)
        {
            _bufferedChars -= Lines[0].Text.Length + 1;
            Lines.RemoveAt(0);
        }
        MatchCount = _filter is null ? 0 : Lines.Count(l => l.Matched);
        OnPropertiesChanged(nameof(BufferText), nameof(LineCountText), nameof(StreamSummary));
    }

    private void CompileFilter()
    {
        if (Search.Length == 0)
        {
            _filter = null;
            return;
        }
        try
        {
            _filter = new(UseRegex ? Search : Regex.Escape(Search),
                RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            // 正则还没写完就开始匹配是常态,写坏了不该把日志视图弄崩。
            _filter = null;
        }
    }

    private void ApplyFilter()
    {
        foreach (var line in Lines)
        {
            line.Matched = _filter?.IsMatch(line.Text) ?? false;
        }
        MatchCount = _filter is null ? 0 : Lines.Count(l => l.Matched);
    }

    /// <summary>停掉流。</summary>
    public async Task StopAsync()
    {
        if (_flushTimer is { IsEnabled: true } timer)
        {
            timer.Stop();
            timer.Tick -= OnFlush;
        }
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync().ConfigureAwait(true);
        _cts.Dispose();
        _cts = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
