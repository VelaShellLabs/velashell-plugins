using System.Text;
using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.PluginSdk;
using VelaShell.PluginSdk.TerminalView;

namespace VelaShell.Plugin.DockerPanel.Ui.Pages;

/// <summary>
/// 容器内的**真终端**(<c>exec</c> + TTY)。
/// <para>
/// 它借的是宿主的终端仿真器(SDK 1.3 的 <see cref="ITerminalViewApi" />):VT 解析、
/// 屏幕缓冲、回滚、选区、IME、鼠标上报、键盘编码全都是宿主那一套 —— 与用户在 SSH 标签里
/// 用的是同一个控件、同一份字体与配色。面板这一层只做三件事:开 exec、把流接上去、
/// 把尺寸变化告诉远端。
/// </para>
/// <para>
/// 早先这里是一个"一条命令一份输出"的行式控制台。它对 <c>cat</c> 与改配置够用,
/// 但 <c>top</c>、<c>vim</c>、<c>less</c> 要的是一个会重绘的屏幕 —— 而重写一个 ANSI
/// 解析器是几周的活,宿主里那一个已经跑了很久了。
/// </para>
/// </summary>
public sealed class ContainerTerminalViewModel(DockerPanelViewModel shell, string containerId, string containerName)
    : ObservableObject, IAsyncDisposable
{
    /// <summary>依次尝试的 shell:多数镜像有 bash,精简镜像只有 sh。</summary>
    private static readonly string[] ShellCandidates = ["/bin/bash", "/bin/sh", "/bin/ash"];

    private IPluginTerminalView? _view;
    private DockerExecSession? _session;
    private CancellationTokenSource? _sessionCts;
    private bool _starting;

    /// <summary>终端控件(交给视图去承载;宿主给的是 Avalonia <c>Control</c>)。</summary>
    public object? TerminalControl => _view?.Control;

    /// <summary>用哪个 shell。</summary>
    public string Shell
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(HeaderText), nameof(ShellName));
            }
        }
    } = "/bin/bash";

    /// <summary>工作目录;留空用镜像默认。</summary>
    public string WorkingDir
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    } = "";

    /// <summary>以哪个用户执行;留空用镜像默认。</summary>
    public string User
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HeaderText));
            }
        }
    } = "";

    /// <summary>会话是否连着。</summary>
    public bool Connected
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(Disconnected), nameof(ConnectionTone));
            }
        }
    }

    /// <summary>会话断了。</summary>
    public bool Disconnected => !Connected;

    /// <summary>连接状态的语气(界面据此给状态点上色)。</summary>
    public RowTone ConnectionTone => Connected ? RowTone.Ok : RowTone.Idle;

    /// <summary>状态短语。</summary>
    public string Status
    {
        get;
        private set => SetField(ref field, value);
    } = "未连接";

    /// <summary>头部那行小字。</summary>
    public string HeaderText
    {
        get
        {
            var sb = new StringBuilder(containerName);
            sb.Append(" · ").Append(Shell);
            if (User.Length > 0)
            {
                sb.Append(" · ").Append(User);
            }
            if (WorkingDir.Length > 0)
            {
                sb.Append(" · ").Append(WorkingDir);
            }
            return sb.ToString();
        }
    }

    /// <summary>终端尺寸文字。</summary>
    public string SizeText => _view is { } view ? $"{view.Columns} × {view.Rows}" : "—";

    /// <summary>状态条上显示的 shell 名(去掉路径,只留 bash / sh / ash)。</summary>
    public string ShellName => Shell[(Shell.LastIndexOf('/') + 1)..];

    /// <summary>exec 实例 id 的短形态(排查时对得上 daemon 的日志)。</summary>
    public string ExecIdText => _session is { } session ? Humanize.ShortId(session.ExecId) : "—";

    /// <summary>清屏。</summary>
    public RelayCommand ClearCommand => field ??= new(_ =>
    {
        _view?.Clear();
        return Task.CompletedTask;
    });

    /// <summary>把屏幕上的文本复制走。</summary>
    public RelayCommand CopyCommand => field ??= new(_ =>
        _view is { } view
            ? shell.Context.Clipboard.SetTextAsync(view.GetText(2000), shell.Lifetime)
            : Task.CompletedTask);

    /// <summary>结束这个 exec 会话。</summary>
    public RelayCommand DisconnectCommand => field ??= new(_ => StopSessionAsync("会话已结束"));

    /// <summary>重开一个会话(换 shell / 换用户之后用)。</summary>
    public RelayCommand ReconnectCommand => field ??= new(_ => RestartAsync());

    /// <summary>
    /// 换用户 / 换 shell,然后重开会话。
    /// <para>
    /// exec 的用户与 shell 是**开会话时**定死的,改不了活着的那一个 ——
    /// 所以这里改完就直接重连,而不是让用户自己再想起来按一次刷新。
    /// </para>
    /// </summary>
    public RelayCommand SwitchUserCommand => field ??= new(_ => SwitchUserAsync());

    /// <summary>换工作目录,然后重开会话。</summary>
    public RelayCommand SwitchWorkingDirCommand => field ??= new(_ => SwitchWorkingDirAsync());

    private async Task SwitchUserAsync()
    {
        var form = new ExecUserForm(User, Shell);
        if (!await shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        User = form.User;
        Shell = form.Shell;
        await RestartAsync().ConfigureAwait(true);
    }

    private async Task SwitchWorkingDirAsync()
    {
        var form = new ExecWorkingDirForm(WorkingDir);
        if (!await shell.ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        WorkingDir = form.WorkingDir;
        await RestartAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 在宿主那个 SSH 终端里开一个会话。
    /// <para>
    /// 面板里这一个已经是真终端了,但它活在面板的生命周期里 ——
    /// 想让会话跟着 SSH 标签一起留着,还是宿主的终端更合适。
    /// </para>
    /// </summary>
    public RelayCommand OpenInHostTerminalCommand => field ??= new(_ => OpenInHostTerminalAsync());

    /// <summary>能不能跳到宿主终端(本机端点没有 SSH 会话可跳)。</summary>
    public bool CanOpenInHostTerminal => shell.SelectedEndpoint?.IsLocal == false;

    /// <summary>第一次进这一页时才建终端并连上。</summary>
    public async Task EnsureStartedAsync()
    {
        if (_view is not null || _starting)
        {
            return;
        }
        _starting = true;
        try
        {
            if (!shell.Context.TerminalView.IsAvailable)
            {
                // 清单钉了 hostMode: inProcess,理论上到不了这里;真到了要说人话,
                // 而不是让用户对着一块空白发呆。
                Status = "这个宿主不提供终端视图(需要 hostMode: inProcess)";
                return;
            }
            _view = shell.Context.TerminalView.Create(new()
            {
                ScrollbackLines = 5000,
                FollowHostAppearance = true
            });
            _view.Resized += OnResized;
            OnPropertiesChanged(nameof(TerminalControl), nameof(SizeText));
            await StartSessionAsync().ConfigureAwait(true);
        }
        finally
        {
            _starting = false;
        }
    }

    private async Task StartSessionAsync()
    {
        if (_view is not { } view || shell.Client is not { } client)
        {
            return;
        }
        Status = "正在建立 exec 会话…";
        try
        {
            var resolved = await ResolveShellAsync(client).ConfigureAwait(true);
            Shell = resolved;
            var session = await client.StartExecAsync(containerId, [resolved], tty: true,
                string.IsNullOrWhiteSpace(User) ? null : User,
                string.IsNullOrWhiteSpace(WorkingDir) ? null : WorkingDir,
                shell.Lifetime).ConfigureAwait(true);
            _session = session;
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(shell.Lifetime);
            Connected = true;
            Status = "已连接";
            OnPropertiesChanged(nameof(ExecIdText), nameof(HeaderText));
            // 先把当前尺寸报过去:远端默认 80×24,而控件多半不是这个大小。
            await ResizeRemoteAsync(view.Columns, view.Rows).ConfigureAwait(true);
            _ = PumpAsync(view, session, _sessionCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Connected = false;
            Status = $"建立失败:{ex.Message}";
            view.Write($"\u001b[31m无法在 {containerName} 里启动 {Shell}:{ex.Message}\u001b[0m\r\n");
            // daemon 的原文(多半是一句 "no such file or directory")指不出下一步该做什么。
            // 建不起来的原因就那么三种,直说 —— 对着一句原文没人猜得到该换什么。
            var warn = (char)0x1b + "[33m";
            var reset = (char)0x1b + "[0m";
            view.Write($"{warn}常见原因:容器没在跑;镜像里根本没有 shell(distroless 一类);" +
                       $"或者指定的用户在容器里不存在。{reset}\r\n");
            view.Write($"{warn}工具条上的「切换用户」可以换 shell 与身份,换完会自动重连。{reset}\r\n");
        }
    }

    /// <summary>
    /// 挑一个容器里真的存在的 shell。
    /// <para>
    /// 直接开 <c>/bin/bash</c> 在 alpine 上会失败,而失败信息是 daemon 的
    /// "no such file or directory" —— 对着它没人猜得到该换 <c>/bin/sh</c>。
    /// 所以先探一次,探不动就退回 <c>/bin/sh</c>。
    /// </para>
    /// </summary>
    private async Task<string> ResolveShellAsync(DockerClient client)
    {
        // 用户手动指定过就不猜了。
        if (Shell.Length > 0 && !ShellCandidates.Contains(Shell))
        {
            return Shell;
        }
        foreach (var candidate in ShellCandidates)
        {
            try
            {
                var probe = await client
                    .ExecCaptureAsync(containerId, ["/bin/sh", "-c", $"command -v {candidate} >/dev/null"],
                        cancellationToken: shell.Lifetime)
                    .ConfigureAwait(true);
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 探测本身失败(容器里连 /bin/sh 都没有,或者刚好停了)——
                // 不在这里报错,让真正的 StartExec 去报,那条消息更准。
                break;
            }
        }
        return "/bin/sh";
    }

    private async Task PumpAsync(IPluginTerminalView view, DockerExecSession session, CancellationToken token)
    {
        try
        {
            // TTY 模式下 daemon 两个方向都走裸字节,可以整条流直接交给终端。
            await view.AttachAsync(session.Stream, token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Ui.Post(() => Status = $"会话中断:{ex.Message}");
        }
        finally
        {
            Ui.Post(() =>
            {
                if (Connected)
                {
                    Connected = false;
                    Status = "会话已结束";
                    view.Write("\u001b[90m\r\n[会话已结束 —— 按「重新连接」再开一个]\u001b[0m\r\n");
                }
            });
        }
    }

    private void OnResized(int columns, int rows)
    {
        OnPropertyChanged(nameof(SizeText));
        _ = ResizeRemoteAsync(columns, rows);
    }

    /// <summary>
    /// 把终端尺寸告诉远端。不报的话 <c>vim</c> 会照 80×24 画,画出来是错位的。
    /// </summary>
    private async Task ResizeRemoteAsync(int columns, int rows)
    {
        if (_session is not { } session || !Connected || columns <= 0 || rows <= 0)
        {
            return;
        }
        try
        {
            await session.ResizeAsync(rows, columns, shell.Lifetime).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 会话正在结束时 resize 必然失败,而那不是一件要告诉用户的事。
        }
    }

    private async Task RestartAsync()
    {
        await StopSessionAsync(null).ConfigureAwait(true);
        _view?.Clear();
        await StartSessionAsync().ConfigureAwait(true);
    }

    private async Task StopSessionAsync(string? status)
    {
        if (_sessionCts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(true);
            cts.Dispose();
            _sessionCts = null;
        }
        if (_session is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(true);
            _session = null;
        }
        Connected = false;
        if (status is not null)
        {
            Status = status;
        }
        OnPropertyChanged(nameof(ExecIdText));
    }

    private async Task OpenInHostTerminalAsync()
    {
        if (shell.SelectedEndpoint?.Endpoint is not { Kind: DockerEndpointKind.Remote } endpoint)
        {
            return;
        }
        var command = $"docker exec -it {Sh.Quote(containerName)} {Sh.Quote(Shell)}";
        try
        {
            // 回写走宿主的输入队列("如同用户键入"),并且需要用户授权 ——
            // 面板不直写 SSH 流,这条边界不为方便让路。
            await shell.Context.Terminal.WriteAsync(endpoint.SessionId, command + "\n", shell.Lifetime)
                       .ConfigureAwait(true);
            shell.Feedback.Notify(FeedbackKind.Info, "已送到宿主终端", command);
        }
        catch (PluginPermissionDeniedException)
        {
            shell.Feedback.Notify(FeedbackKind.Warning, "没有向终端回写的授权",
                $"可以自己在那条会话里执行:{command}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            shell.Feedback.ReportError("送往宿主终端", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync(null).ConfigureAwait(false);
        if (_view is { } view)
        {
            view.Resized -= OnResized;
            view.Dispose();
            _view = null;
        }
    }
}
