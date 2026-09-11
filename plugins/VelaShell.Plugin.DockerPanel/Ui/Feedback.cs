using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Net;
using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>反馈的语气。</summary>
public enum FeedbackKind
{
    /// <summary>中性。</summary>
    Info,

    /// <summary>成功。</summary>
    Success,

    /// <summary>部分成功 / 需要注意。</summary>
    Warning,

    /// <summary>失败。</summary>
    Error
}

/// <summary>面板右下角的一条 toast。</summary>
/// <remarks>建一条 toast。</remarks>
public sealed class Toast(FeedbackKind kind, string title, string detail) : ObservableObject
{

    /// <summary>语气。</summary>
    public FeedbackKind Kind { get; } = kind;

    /// <summary>标题。</summary>
    public string Title { get; } = title;

    /// <summary>正文。</summary>
    public string Detail { get; } = detail;

    /// <summary>可点的动作(文字 → 回调)。</summary>
    public ObservableCollection<ToastAction> Actions { get; } = [];

    /// <summary>图标资源键。</summary>
    public string Icon => Kind switch
    {
        FeedbackKind.Success => "Docker.circle-check-big",
        FeedbackKind.Warning => "Icon.triangle-alert",
        FeedbackKind.Error => "Docker.circle-x",
        _ => "Icon.info"
    };

    /// <summary>是否自动消失。成功的 4 秒后自己走,失败的**不自动消失**。</summary>
    public bool AutoDismiss => Kind is FeedbackKind.Success or FeedbackKind.Info;
}

/// <summary>toast 上的一个动作。</summary>
/// <param name="Label">文字。</param>
/// <param name="Invoke">点击回调。</param>
public sealed record ToastAction(string Label, Action Invoke);

/// <summary>
/// 结果反馈。
/// <para>
/// 规则只有一条:<b>动作发起点还在视野里,就只更新状态栏和那一行;用户已经切走了
/// (换了页签、关了对话框),才弹 toast。</b> 在用户正盯着的列表上方再弹一个
/// "已停止 nginx-proxy",只是把他刚看到的事情又说了一遍。
/// </para>
/// </summary>
public sealed class Feedback : ObservableObject
{
    private const int MaxToasts = 3;

    /// <summary>当前的 toast(最多三条,新的在下面)。</summary>
    public ObservableCollection<Toast> Toasts { get; } = [];

    /// <summary>状态栏左侧那句话 —— 永远说最后一件事的结果。</summary>
    public string StatusText
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>状态栏那句话的语气。</summary>
    public FeedbackKind StatusKind
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(StatusIcon));
            }
        }
    } = FeedbackKind.Info;

    /// <summary>状态栏图标。</summary>
    public string StatusIcon => StatusKind switch
    {
        FeedbackKind.Success => "Docker.circle-check-big",
        FeedbackKind.Warning => "Icon.triangle-alert",
        FeedbackKind.Error => "Docker.circle-x",
        _ => "Icon.info"
    };

    /// <summary>只更新状态栏。</summary>
    public void Status(FeedbackKind kind, string text) => Ui.Post(() =>
    {
        StatusKind = kind;
        StatusText = text;
    });

    /// <summary>弹一条 toast,同时更新状态栏。</summary>
    public Toast Notify(FeedbackKind kind, string title, string detail, params ToastAction[] actions)
    {
        var toast = new Toast(kind, title, detail);
        foreach (var action in actions)
        {
            toast.Actions.Add(action);
        }
        Ui.Post(() =>
        {
            StatusKind = kind;
            StatusText = detail.Length > 0 ? $"{title} · {detail.Split('\n')[0]}" : title;
            Toasts.Add(toast);
            while (Toasts.Count > MaxToasts)
            {
                Toasts.RemoveAt(0);
            }
            if (toast.AutoDismiss)
            {
                DispatcherTimer.RunOnce(() => Toasts.Remove(toast), TimeSpan.FromSeconds(4));
            }
        });
        return toast;
    }

    /// <summary>关掉一条 toast。</summary>
    public void Dismiss(Toast toast) => Ui.Post(() => Toasts.Remove(toast));

    /// <summary>
    /// 把一次批量操作的结果如实报出来。成功全过就一句话;有失败的话**逐个目标**列原因,
    /// 而不是把整批说成"操作失败"。
    /// </summary>
    /// <param name="verb">动作名,如“停止”。</param>
    /// <param name="result">批量结果。</param>
    /// <param name="inView">发起点是否还在用户视野里(决定要不要弹 toast)。</param>
    /// <param name="onShowDetail">用户点"查看详情"时的回调。</param>
    public void ReportBatch(string verb, BatchResult result, bool inView, Action? onShowDetail = null)
    {
        if (result.AllSucceeded)
        {
            var text = $"已{verb} {result.SucceededCount} 个";
            if (inView)
            {
                Status(FeedbackKind.Success, text);
            }
            else
            {
                Notify(FeedbackKind.Success, text, "");
            }
            return;
        }
        var summary = $"已{verb} {result.SucceededCount} 个,{result.FailedCount} 个失败";
        var detail = string.Join('\n', result.Failures.Take(3).Select(f => $"{f.Target}:{f.Failure}"));
        if (result.FailedCount > 3)
        {
            detail += $"\n…还有 {result.FailedCount - 3} 个";
        }
        Status(FeedbackKind.Warning, $"{summary} · {result.Failures.First().Target}:{result.Failures.First().Failure}");
        List<ToastAction> actions = [];
        if (onShowDetail is not null)
        {
            actions.Add(new("查看详情", onShowDetail));
        }
        Notify(FeedbackKind.Warning, summary, detail, [.. actions]);
    }

    /// <summary>
    /// 把一个异常报出来。连不上与操作失败的语气不一样。
    /// <para>
    /// daemon 的原文一律留着 —— 它是唯一的事实。但只给原文不够:
    /// <c>409 volume is in use</c> 这种话,用户读完还是不知道下一步该做什么。
    /// 所以在原文**前面**加一句人话,并且允许调用方挂一颗补救按钮
    /// (「看看是谁在占」这类);两者都没有的时候,行为与从前一致。
    /// </para>
    /// </summary>
    /// <param name="what">在做什么(“删除卷”)。</param>
    /// <param name="ex">异常。</param>
    /// <param name="actions">补救动作,调用方按场景给。</param>
    public void ReportError(string what, Exception ex, params ToastAction[] actions)
    {
        if (ex is OperationCanceledException)
        {
            Status(FeedbackKind.Info, $"{what} 已取消");
            return;
        }
        var detail = ex switch
        {
            DockerApiException api => Explain(api) is { Length: > 0 } why ? $"{why}\n{api.Message}" : api.Message,
            DockerUnreachableException unreachable => unreachable.Message,
            _ => ex.Message
        };
        Notify(FeedbackKind.Error, $"{what} 失败", detail, actions);
    }

    /// <summary>
    /// 给 daemon 的状态码配一句人话。
    /// <para>
    /// 只翻译**状态码本身**的含义,不猜具体是哪个资源出的事 ——
    /// 后者要看上下文,由调用方用补救动作去说。
    /// </para>
    /// </summary>
    private static string Explain(DockerApiException api) => api.StatusCode switch
    {
        HttpStatusCode.Conflict => "冲突:目标正被别的东西占着,或者当前状态不允许这个动作。",
        HttpStatusCode.NotFound => "目标已经不在了 —— 多半是别处(或另一个面板)已经删掉了。",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "认证不通过:凭据过期、没登录,或者这个仓库不让你做这件事。",
        HttpStatusCode.BadRequest => "daemon 认为这个请求本身有问题 —— 多半是参数或名字不合法。",
        HttpStatusCode.InternalServerError => "daemon 内部出错。它的日志(journalctl -u docker)里会有更完整的一段。",
        _ => ""
    };
}
