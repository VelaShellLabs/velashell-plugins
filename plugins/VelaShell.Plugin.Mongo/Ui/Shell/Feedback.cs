using Avalonia.Threading;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>确认框里列出的一条事实(<c>文档 214,006</c>)。</summary>
/// <param name="Label">标签。</param>
/// <param name="Value">值。</param>
internal sealed record ConfirmFact(string Label, string Value);

/// <summary>
/// 一次确认请求(设计稿 22「保护与确认」:危险操作一律描边红按钮 + 明确后果;生产连接要手打名称)。
/// </summary>
internal sealed record ConfirmRequest
{
    /// <summary>标题(<c>删除集合</c>)。</summary>
    public required string Title { get; init; }

    /// <summary>说明(写清后果:"将永久删除 … 及其 3 个索引。此操作无法撤销。")。</summary>
    public required string Message { get; init; }

    /// <summary>确认按钮文字(<c>删除集合</c>)。</summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>标题栏图标。</summary>
    public string IconKey { get; init; } = "Mongo.trash-2";

    /// <summary>危险操作(确认按钮为红色描边)。</summary>
    public bool Danger { get; init; } = true;

    /// <summary>要求手打的名称;为空即不要求。</summary>
    public string? TypeToConfirm { get; init; }

    /// <summary>事实清单。</summary>
    public IReadOnlyList<ConfirmFact> Facts { get; init; } = [];

    /// <summary>底部的一条辅助链接文字(<c>先用 mongodump 备份…</c>)。</summary>
    public string? AsideLabel { get; init; }

    /// <summary>辅助链接的动作。</summary>
    public Action? AsideAction { get; init; }
}

/// <summary>确认框的视图模型。</summary>
internal sealed class ConfirmDialogViewModel : DialogViewModel
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _typed = "";

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="request">请求。</param>
    public ConfirmDialogViewModel(IWorkbench workspace, ConfirmRequest request)
        : base(workspace)
    {
        Request = request;
        Title = request.Title;
        ConfirmCommand = new RelayCommand(Confirm, () => CanConfirm);
        AsideCommand = new RelayCommand(() =>
        {
            Close();
            request.AsideAction?.Invoke();
        });
    }

    /// <summary>请求。</summary>
    public ConfirmRequest Request { get; }

    /// <inheritdoc />
    public override string IconKey => Request.IconKey;

    /// <inheritdoc />
    public override string IconToken => Request.Danger ? "VelaError" : "VelaAccent";

    /// <inheritdoc />
    public override double Width => 400;

    /// <summary>要不要手打名称。</summary>
    public bool RequiresTyping => !string.IsNullOrEmpty(Request.TypeToConfirm);

    /// <summary>手打的内容。</summary>
    public string Typed
    {
        get => _typed;
        set
        {
            if (SetProperty(ref _typed, value))
            {
                RaisePropertyChanged(nameof(CanConfirm));
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>能不能点确认。</summary>
    public bool CanConfirm => !RequiresTyping || string.Equals(Typed.Trim(), Request.TypeToConfirm, StringComparison.Ordinal);

    /// <summary>确认。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>辅助链接。</summary>
    public RelayCommand AsideCommand { get; }

    /// <summary>结果。</summary>
    public Task<bool> Result => _result.Task;

    private void Confirm()
    {
        _result.TrySetResult(true);
        Close();
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        _result.TrySetResult(false);
        base.OnClosed();
    }
}

/// <summary>提示的语气。</summary>
internal enum ToastKind
{
    /// <summary>普通。</summary>
    Info,

    /// <summary>成功(绿勾)。</summary>
    Success,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error
}

/// <summary>一条右下角提示。</summary>
internal sealed record ToastRequest
{
    /// <summary>主文字(<c>已提交 3 处修改</c>)。</summary>
    public required string Title { get; init; }

    /// <summary>副文字(<c>1 更新 · 1 插入 · 1 删除 · 12 ms</c>)。</summary>
    public string? Detail { get; init; }

    /// <summary>语气。</summary>
    public ToastKind Kind { get; init; } = ToastKind.Info;

    /// <summary>动作按钮文字(<c>撤销</c>);没有为 <see langword="null" />。</summary>
    public string? ActionLabel { get; init; }

    /// <summary>动作。</summary>
    public Func<Task>? Action { get; init; }

    /// <summary>动作按钮的图标(默认「撤销」的回退箭头;「在资源管理器中显示」之类换成别的)。</summary>
    public string ActionIconKey { get; init; } = "Mongo.undo-2";

    /// <summary>动作按钮上要不要带倒计时(撤销要,"查看""打开"这种不必)。</summary>
    public bool ShowCountdown { get; init; } = true;

    /// <summary>停留多久;带动作的提示按秒倒数显示在按钮上(<c>撤销 (9)</c>)。</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(4);
}

/// <summary>右下角提示的视图模型(带倒计时)。</summary>
internal sealed class ToastViewModel : ObservableObject
{
    private readonly Action<ToastViewModel> _dismiss;
    private readonly DispatcherTimer _timer;
    private int _remaining;

    /// <summary>构造并开始倒计时。</summary>
    public ToastViewModel(ToastRequest request, Action<ToastViewModel> dismiss)
    {
        Request = request;
        _dismiss = dismiss;
        _remaining = Math.Max(1, (int)Math.Round(request.Duration.TotalSeconds));
        ActionCommand = new AsyncCommand(async () =>
        {
            Dismiss();
            if (request.Action is { } action)
            {
                await action().ConfigureAwait(true);
            }
        });
        DismissCommand = new RelayCommand(Dismiss);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            Remaining--;
            if (_remaining <= 0)
            {
                Dismiss();
            }
        };
        _timer.Start();
    }

    /// <summary>请求。</summary>
    public ToastRequest Request { get; }

    /// <summary>主文字。</summary>
    public string Title => Request.Title;

    /// <summary>副文字。</summary>
    public string? Detail => Request.Detail;

    /// <summary>有没有副文字。</summary>
    public bool HasDetail => !string.IsNullOrEmpty(Request.Detail);

    /// <summary>有没有动作。</summary>
    public bool HasAction => Request.ActionLabel is not null && Request.Action is not null;

    /// <summary>图标。</summary>
    public string IconKey => Request.Kind switch
    {
        ToastKind.Success => "Mongo.circle-check",
        ToastKind.Warning => "Mongo.lock",
        ToastKind.Error => "Mongo.circle-x",
        _ => "Mongo.info"
    };

    /// <summary>图标颜色令牌。</summary>
    public string IconToken => Request.Kind switch
    {
        ToastKind.Success => "VelaStatusConnected",
        ToastKind.Warning => "VelaWarning",
        ToastKind.Error => "VelaError",
        _ => "VelaInfo"
    };

    /// <summary>剩余秒数。</summary>
    public int Remaining
    {
        get => _remaining;
        private set
        {
            if (SetProperty(ref _remaining, value))
            {
                RaisePropertyChanged(nameof(ActionText));
            }
        }
    }

    /// <summary>动作按钮上的字(<c>撤销 (9)</c>)。</summary>
    public string ActionText => Request.ShowCountdown ? $"{Request.ActionLabel} ({_remaining})" : Request.ActionLabel ?? "";

    /// <summary>动作按钮的图标。</summary>
    public string ActionIconKey => Request.ActionIconKey;

    /// <summary>动作命令。</summary>
    public AsyncCommand ActionCommand { get; }

    /// <summary>关掉。</summary>
    public RelayCommand DismissCommand { get; }

    private void Dismiss()
    {
        _timer.Stop();
        _dismiss(this);
    }
}
