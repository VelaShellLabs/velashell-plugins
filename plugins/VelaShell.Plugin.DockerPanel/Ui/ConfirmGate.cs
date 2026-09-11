using System.Collections.ObjectModel;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>确认闸门的档位。</summary>
public enum ConfirmTier
{
    /// <summary>
    /// 一般破坏性:删容器、删镜像、删网络、compose down、多数 prune。
    /// 后果说明 + 危险色按钮,点一下就执行。
    /// </summary>
    Destructive,

    /// <summary>
    /// <b>会丢数据</b>:删卷、<c>compose down -v</c>、<c>system prune --volumes</c>、
    /// 覆盖容器内文件或远端 compose 文件。额外要求**手打确认串**,与删仓库同款 ——
    /// 因为后果同款。
    /// </summary>
    DataLoss
}

/// <summary>后果说明的一条。</summary>
/// <param name="Severity">0=中性 1=好消息 2=注意 3=危险,决定图标与颜色。</param>
/// <param name="Text">一句话。</param>
public readonly record struct ConfirmConsequence(int Severity, string Text);

/// <summary>确认框里列出的一个目标。</summary>
/// <param name="Name">名字。</param>
/// <param name="Meta">中间那段小字(镜像、驱动)。</param>
/// <param name="Status">右侧状态。</param>
/// <param name="Running">是否在运行(决定左侧圆点的颜色)。</param>
public readonly record struct ConfirmTarget(string Name, string Meta, string Status, bool Running);

/// <summary>一次确认请求。</summary>
public sealed record ConfirmRequest
{
    /// <summary>标题,如“删除 2 个容器?”。</summary>
    public required string Title { get; init; }

    /// <summary>标题左侧的图标资源键。</summary>
    public string Icon { get; init; } = "Icon.trash-2";

    /// <summary>档位。</summary>
    public ConfirmTier Tier { get; init; } = ConfirmTier.Destructive;

    /// <summary>
    /// 目标主机名。<b>一行都不能省</b> —— 同时开着生产与测试两台机器时,
    /// 一个不写清主机的“确定删除 3 个卷吗”是这个面板能犯的最贵的错误。
    /// </summary>
    public required string HostName { get; init; }

    /// <summary>主机名后面那行小字(地址、传输方式)。</summary>
    public string HostDetail { get; init; } = "";

    /// <summary>主机小字是否用警示色(生产环境提醒)。</summary>
    public bool HostWarning { get; init; }

    /// <summary>将要执行的**那几条真请求**,不是一句概括。</summary>
    public IReadOnlyList<string> Commands { get; init; } = [];

    /// <summary>请求下面的等价命令行(给用户核对与复制)。</summary>
    public string? CommandNote { get; init; }

    /// <summary>目标列表。</summary>
    public IReadOnlyList<ConfirmTarget> Targets { get; init; } = [];

    /// <summary>后果说明。</summary>
    public IReadOnlyList<ConfirmConsequence> Consequences { get; init; } = [];

    /// <summary>确认按钮文字。</summary>
    public required string ConfirmLabel { get; init; }

    /// <summary>确认按钮的图标。</summary>
    public string ConfirmIcon { get; init; } = "Icon.trash-2";

    /// <summary>
    /// <see cref="ConfirmTier.DataLoss" /> 档要求手打的确认串,如 <c>delete</c> / <c>save</c>。
    /// </summary>
    public string ConfirmWord { get; init; } = "delete";

    /// <summary>会丢数据那一档要额外醒目显示的一句话。</summary>
    public string? DataLossHeadline { get; init; }

    /// <summary>会丢数据那一档的补充说明(逐条)。</summary>
    public IReadOnlyList<string> DataLossPoints { get; init; } = [];

    /// <summary>
    /// 闸门上那个可选的"先做一件事再执行"勾选(删卷之前先备份为 tar)。
    /// 留空表示这次不提供这个选项。
    /// </summary>
    public string? PrecautionLabel { get; init; }

    /// <summary>勾选默认打开没有。删数据这一档默认**打开** —— 默认值该偏向不丢东西那一边。</summary>
    public bool PrecautionDefault { get; init; } = true;
}

/// <summary>
/// 面板内的确认闸门。
/// <para>
/// 它**贴在这个面板上**,不是一个飘在屏幕中央的系统弹窗:同时开着两个 Docker 面板时,
/// 一个居中的对话框根本说不清自己属于哪一台机器。
/// </para>
/// </summary>
public sealed class ConfirmGate : ObservableObject
{
    private TaskCompletionSource<bool>? _pending;

    /// <summary>建一个闸门。</summary>
    public ConfirmGate()
    {
        ConfirmCommand = new RelayCommand(_ => Complete(true), _ => CanConfirm);
        CancelCommand = new RelayCommand(_ => Complete(false));
    }

    /// <summary>
    /// 那个可选的"先做一件事再执行"勾选是否打开。发起方在 <c>AskAsync</c> 返回之后读它。
    /// </summary>
    public bool Precaution
    {
        get;
        set => SetField(ref field, value);
    } = true;

    /// <summary>这次闸门有没有提供那个勾选。</summary>
    public bool HasPrecaution => Request?.PrecautionLabel is { Length: > 0 };

    /// <summary>那个勾选的文字。</summary>
    public string PrecautionLabel => Request?.PrecautionLabel ?? "";

    /// <summary>当前请求;没有待确认的事情时为 <see langword="null" />。</summary>
    public ConfirmRequest? Request
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(IsOpen), nameof(IsDataLoss), nameof(HasTargets),
                    nameof(HasConsequences), nameof(HasCommandNote), nameof(CanConfirm), nameof(RemainingHint),
                    nameof(HasPrecaution), nameof(PrecautionLabel), nameof(Icon), nameof(Title),
                    nameof(HostName), nameof(HostDetail), nameof(CommandNote), nameof(DataLossHeadline),
                    nameof(ConfirmWord), nameof(ConfirmIcon), nameof(ConfirmLabel));
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>闸门是否打开着。</summary>
    public bool IsOpen => Request is not null;

    // 下面这些都是 Request 的空安全转发。界面不直接绑 Request.Xxx ——
    // 闸门关着时 Request 是 null,而 IsVisible 只是不画:子树照样建、绑定照样求值,
    // 于是每关一次就刷一屏 “binding … Value is null”。转发一层,关着时给的是空串。

    /// <summary>标题左侧的图标资源键。</summary>
    public string Icon => Request?.Icon ?? "Icon.trash-2";

    /// <summary>标题。</summary>
    public string Title => Request?.Title ?? "";

    /// <summary>目标主机名。</summary>
    public string HostName => Request?.HostName ?? "";

    /// <summary>主机名后面那行小字。</summary>
    public string HostDetail => Request?.HostDetail ?? "";

    /// <summary>
    /// 目标不是本机时,主机那行小字要用警示色。
    /// <para>
    /// 请求里早就带着这一位(<see cref="ConfirmRequest.HostWarning" />),界面却一直没读 ——
    /// 于是"在生产机上删 3 个卷"和"在本机删 3 个卷"长得一模一样。
    /// </para>
    /// </summary>
    public bool HostWarning => Request?.HostWarning == true;

    /// <summary>请求下面那行等价的命令行。</summary>
    public string CommandNote => Request?.CommandNote ?? "";

    /// <summary>会丢数据那一档要额外醒目显示的一句话。</summary>
    public string DataLossHeadline => Request?.DataLossHeadline ?? "";

    /// <summary>会丢数据那一档要手打的确认串。</summary>
    public string ConfirmWord => Request?.ConfirmWord ?? "";

    /// <summary>确认按钮的图标。</summary>
    public string ConfirmIcon => Request?.ConfirmIcon ?? "Icon.trash-2";

    /// <summary>确认按钮文字。</summary>
    public string ConfirmLabel => Request?.ConfirmLabel ?? "";

    /// <summary>是不是"会丢数据"那一档。</summary>
    public bool IsDataLoss => Request?.Tier == ConfirmTier.DataLoss;

    /// <summary>有没有目标列表。</summary>
    public bool HasTargets => Request?.Targets.Count > 0;

    /// <summary>有没有后果说明。</summary>
    public bool HasConsequences => Request?.Consequences.Count > 0;

    /// <summary>有没有等价命令。</summary>
    public bool HasCommandNote => !string.IsNullOrEmpty(Request?.CommandNote);

    /// <summary>目标列表(绑定用)。</summary>
    public ObservableCollection<ConfirmTarget> Targets { get; } = [];

    /// <summary>后果说明(绑定用)。</summary>
    public ObservableCollection<ConfirmConsequence> Consequences { get; } = [];

    /// <summary>将要执行的请求(绑定用)。</summary>
    public ObservableCollection<string> Commands { get; } = [];

    /// <summary>会丢数据那一档的补充说明(绑定用)。</summary>
    public ObservableCollection<string> DataLossPoints { get; } = [];

    /// <summary>用户手打的确认串。</summary>
    public string TypedWord
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertiesChanged(nameof(CanConfirm), nameof(RemainingHint));
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    } = "";

    /// <summary>确认按钮能不能按。</summary>
    public bool CanConfirm =>
        Request is not null &&
        (Request.Tier != ConfirmTier.DataLoss ||
         string.Equals(TypedWord.Trim(), Request.ConfirmWord, StringComparison.Ordinal));

    /// <summary>还差几个字符的提示。</summary>
    public string RemainingHint
    {
        get
        {
            if (Request is not { Tier: ConfirmTier.DataLoss } request)
            {
                return "";
            }
            var typed = TypedWord.Trim();
            return typed == request.ConfirmWord
                ? "可以确认了"
                : request.ConfirmWord.StartsWith(typed, StringComparison.Ordinal)
                    ? $"还差 {request.ConfirmWord.Length - typed.Length} 个字符"
                    : $"请输入 {request.ConfirmWord}";
        }
    }

    /// <summary>确认。</summary>
    public RelayCommand ConfirmCommand { get; }

    /// <summary>取消。</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// 打开闸门并等用户决定。已经有一个在等的时候,新请求直接被拒绝
    /// (返回 false)—— 两层确认框叠在一起,用户不可能说清自己在确认哪一个。
    /// </summary>
    public Task<bool> AskAsync(ConfirmRequest request)
    {
        if (_pending is not null)
        {
            return Task.FromResult(false);
        }
        _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Post(() =>
        {
            TypedWord = "";
            Precaution = request.PrecautionDefault;
            Commands.Clear();
            foreach (var command in request.Commands)
            {
                Commands.Add(command);
            }
            Targets.Clear();
            foreach (var target in request.Targets)
            {
                Targets.Add(target);
            }
            Consequences.Clear();
            foreach (var consequence in request.Consequences)
            {
                Consequences.Add(consequence);
            }
            DataLossPoints.Clear();
            foreach (var point in request.DataLossPoints)
            {
                DataLossPoints.Add(point);
            }
            Request = request;
        });
        return _pending.Task;
    }

    /// <summary>面板关闭时把等待中的请求当作取消。</summary>
    public void CancelPending() => Complete(false);

    private void Complete(bool confirmed)
    {
        var pending = _pending;
        _pending = null;
        Ui.Post(() =>
        {
            Request = null;
            TypedWord = "";
        });
        pending?.TrySetResult(confirmed);
    }
}
