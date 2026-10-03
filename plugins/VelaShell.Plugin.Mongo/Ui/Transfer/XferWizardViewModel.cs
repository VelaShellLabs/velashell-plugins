using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Transfer;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>向导左栏一步的状态。</summary>
internal enum XferStepState
{
    /// <summary>还没到。</summary>
    Pending,

    /// <summary>当前。</summary>
    Current,

    /// <summary>已完成(绿勾,可点回去改)。</summary>
    Done
}

/// <summary>向导左栏的一步(一行标题 + 一行灰字摘要)。</summary>
internal sealed class XferStep : ObservableObject
{

    /// <summary>构造。</summary>
    /// <param name="index">下标(从 0 起)。</param>
    /// <param name="title">标题。</param>
    public XferStep(int index, string title)
    {
        Index = index;
        Title = title;
    }

    /// <summary>下标。</summary>
    public int Index { get; }

    /// <summary>圆里的数字。</summary>
    public string NumberText => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>摘要(<c>shop.orders · 当前筛选</c>)。</summary>
    public string Summary
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasSummary));
            }
        }
    } = "";

    /// <summary>有没有摘要。</summary>
    public bool HasSummary => Summary.Length > 0;

    /// <summary>状态。</summary>
    public XferStepState State
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsDone), nameof(IsCurrent), nameof(IsPending), nameof(CanJump));
            }
        }
    }

    /// <summary>执行中 / 执行完之后不许再点回设置步骤。</summary>
    public bool Locked
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(CanJump));
            }
        }
    }

    /// <summary>已完成。</summary>
    public bool IsDone => State == XferStepState.Done;

    /// <summary>当前。</summary>
    public bool IsCurrent => State == XferStepState.Current;

    /// <summary>未到。</summary>
    public bool IsPending => State == XferStepState.Pending;

    /// <summary>能点回去。</summary>
    public bool CanJump => State == XferStepState.Done && !Locked;
}

/// <summary>下拉里的一项(值 + 给人看的名字)。</summary>
/// <param name="Value">值。</param>
/// <param name="Label">名字。</param>
internal sealed record XferOption(object Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>日志一行(数据传输右栏、导出 / 导入执行页)。</summary>
/// <param name="Time">时间(<c>21:12:01</c>)。</param>
/// <param name="Text">内容。</param>
/// <param name="Tone">语气。</param>
internal sealed record XferLogLine(string Time, string Text, XferTone Tone)
{
    /// <summary>颜色令牌。</summary>
    public string Token => Tone switch
    {
        XferTone.Ok => "VelaStatusConnected",
        XferTone.Warn => "VelaWarning",
        XferTone.Error => "VelaError",
        XferTone.Muted => "VelaTextMuted",
        XferTone.Accent => "VelaAccent",
        _ => "VelaTextSecondary"
    };
}

/// <summary>
/// 三个向导(导出 / 导入 / 数据传输)共用的骨架:左栏四步、底栏 取消 / 上一步 / 下一步、
/// 第四步是执行页(执行中不许 Esc 关、不许回到设置步骤)。
/// <para>
/// 执行中点右上角 ✕ 不中断任务,而是**转入后台**:关掉对话框、跑完弹提示,提示上的「查看」把对话框原样叫回来。
/// 中途真要停,底栏有明确的红色「停止」—— 一个关窗动作不该顺带丢掉做了一半的导入。
/// </para>
/// </summary>
internal abstract class XferWizardViewModel : DialogViewModel
{
    /// <summary>执行页的下标。</summary>
    protected const int RunStep = 3;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="stepTitles">四步的标题。</param>
    protected XferWizardViewModel(IMongoWorkspace workspace, IReadOnlyList<string> stepTitles)
        : base(workspace)
    {
        Steps = [.. stepTitles.Select(static (t, i) => new XferStep(i, t))];
        NextCommand = new AsyncCommand(NextAsync, () => !IsRunning && !IsBusy);
        BackCommand = new RelayCommand(() => GoTo(StepIndex - 1), () => CanGoBack);
        JumpCommand = new RelayCommand<XferStep>(step => GoTo(step.Index), static step => step.CanJump);
        BackgroundCommand = new RelayCommand(MoveToBackground);
        StepIndex = 0;
    }

    /// <inheritdoc />
    public override double Width => 1100;

    /// <inheritdoc />
    public override double Height => 720;

    /// <inheritdoc />
    public override bool CanCloseWithEscape => !IsRunning;

    /// <summary>四步。</summary>
    public ObservableCollection<XferStep> Steps { get; }

    /// <summary>当前步。</summary>
    public int StepIndex
    {
        get;
        protected set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }
            foreach (XferStep step in Steps)
            {
                step.State = step.Index < value ? XferStepState.Done
                    : step.Index == value ? XferStepState.Current
                    : XferStepState.Pending;
                step.Locked = value == RunStep;
            }
            RaisePropertiesChanged(nameof(IsStep0), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsSetup),
                nameof(CanGoBack), nameof(NextText), nameof(ShowSetupFooter), nameof(ShowRunFooter), nameof(ShowDoneFooter));
            BackCommand.RaiseCanExecuteChanged();
            JumpCommand?.RaiseCanExecuteChanged();
        }
    } = -1;

    /// <summary>第 1 步。</summary>
    public bool IsStep0 => StepIndex == 0;

    /// <summary>第 2 步。</summary>
    public bool IsStep1 => StepIndex == 1;

    /// <summary>第 3 步。</summary>
    public bool IsStep2 => StepIndex == 2;

    /// <summary>执行页。</summary>
    public bool IsStep3 => StepIndex == RunStep;

    /// <summary>设置步骤(1–3)。</summary>
    public bool IsSetup => StepIndex < RunStep;

    /// <summary>执行中。</summary>
    public bool IsRunning
    {
        get;
        protected set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ShowRunFooter), nameof(ShowDoneFooter), nameof(CanGoBack));
                NextCommand.RaiseCanExecuteChanged();
                BackCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>执行完了(成功、失败或取消)。</summary>
    public bool IsFinished
    {
        get;
        protected set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ShowDoneFooter));
            }
        }
    }

    /// <summary>某一步在加载 / 校验(下一步暂不可点)。</summary>
    public bool IsBusy
    {
        get;
        protected set
        {
            if (SetProperty(ref field, value))
            {
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>已转入后台(对话框关着,任务还在跑)。</summary>
    public bool IsBackground
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>能回上一步。</summary>
    public bool CanGoBack => StepIndex > 0 && StepIndex < RunStep && !IsRunning;

    /// <summary>底栏:设置阶段(取消 / 上一步 / 下一步)。</summary>
    public bool ShowSetupFooter => StepIndex < RunStep;

    /// <summary>底栏:执行中(转入后台 / 停止)。</summary>
    public bool ShowRunFooter => StepIndex == RunStep && IsRunning;

    /// <summary>底栏:执行完(关闭)。</summary>
    public bool ShowDoneFooter => StepIndex == RunStep && !IsRunning;

    /// <summary>「下一步」按钮上的字:最后一个设置步骤变成主操作(<c>开始导出 · 312 份文档</c>)。</summary>
    public string NextText => StepIndex == RunStep - 1 ? StartText : Loc["Common_Next"];

    /// <summary>主操作的字。</summary>
    protected abstract string StartText { get; }

    /// <summary>下一步 / 开始。</summary>
    public AsyncCommand NextCommand { get; }

    /// <summary>上一步。</summary>
    public RelayCommand BackCommand { get; }

    /// <summary>点左栏已完成的一步回去。</summary>
    public RelayCommand<XferStep> JumpCommand { get; }

    /// <summary>转入后台。</summary>
    public RelayCommand BackgroundCommand { get; }

    /// <summary>主操作的字变了(派生类在计数、格式变化时调)。</summary>
    protected void RaiseStartTextChanged() => RaisePropertyChanged(nameof(NextText));

    /// <summary>离开某一步之前的校验;返回 <see langword="false" /> 留在原地(派生类负责提示原因)。</summary>
    protected abstract Task<bool> ValidateAsync(int step);

    /// <summary>进入某一步之后(加载这一步要的东西)。</summary>
    protected virtual Task OnEnteredAsync(int step) => Task.CompletedTask;

    /// <summary>从最后一个设置步骤点「开始」:确认、切到执行页、跑。</summary>
    protected abstract Task StartAsync();

    /// <summary>切到某一步(只许往回跳,或由派生类往前推)。</summary>
    protected void GoTo(int index)
    {
        if (index < 0 || index >= RunStep || IsRunning)
        {
            return;
        }
        StepIndex = index;
        _ = EnterAsync(index);
    }

    private async Task NextAsync()
    {
        if (IsRunning || StepIndex >= RunStep)
        {
            return;
        }
        IsBusy = true;
        try
        {
            if (!await ValidateAsync(StepIndex).ConfigureAwait(true))
            {
                return;
            }
        }
        finally
        {
            IsBusy = false;
        }
        if (StepIndex == RunStep - 1)
        {
            await StartAsync().ConfigureAwait(true);
            return;
        }
        StepIndex++;
        await EnterAsync(StepIndex).ConfigureAwait(true);
    }

    private async Task EnterAsync(int step)
    {
        try
        {
            await OnEnteredAsync(step).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoDB.Driver.MongoException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    /// <summary>进入执行页(派生类在确认通过之后调)。</summary>
    protected void EnterRunStep()
    {
        StepIndex = RunStep;
        IsFinished = false;
        IsRunning = true;
    }

    /// <summary>执行结束(成功、失败、取消都走这里)。转入后台时弹一条带「查看」的提示。</summary>
    protected void FinishRun(ToastRequest toast)
    {
        IsRunning = false;
        IsFinished = true;
        if (IsBackground)
        {
            Workspace.Toast(toast with
            {
                ActionLabel = toast.ActionLabel ?? Loc["Xfer_View"],
                Action = toast.Action ?? (() =>
                {
                    Reopen();
                    return Task.CompletedTask;
                }),
                Duration = TimeSpan.FromSeconds(10)
            });
        }
        else
        {
            Workspace.Toast(toast);
        }
    }

    /// <summary>把对话框叫回来。</summary>
    public void Reopen()
    {
        IsBackground = false;
        Workspace.ShowDialog(this);
    }

    private void MoveToBackground()
    {
        if (!IsRunning)
        {
            Close();
            return;
        }
        IsBackground = true;
        Close();
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        if (IsRunning && !IsBackground)
        {
            // 执行中点了 ✕:任务不停,转入后台。
            IsBackground = true;
        }
        if (IsRunning)
        {
            Workspace.Toast(new()
            {
                Title = Loc["Xfer_Backgrounded"],
                Detail = Subtitle,
                Kind = ToastKind.Info,
                ActionLabel = Loc["Xfer_View"],
                Action = () =>
                {
                    Reopen();
                    return Task.CompletedTask;
                },
                Duration = TimeSpan.FromSeconds(6)
            });
        }
        base.OnClosed();
    }

    /// <summary>时间戳(日志用)。</summary>
    protected static string Now() => DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>在 UI 线程上做一件事(集合类属性只能在 UI 线程改)。</summary>
    protected static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>
    /// 在系统文件管理器里显示一个文件(Windows 选中它,macOS 用 Finder 定位,其余打开所在目录)。
    /// SDK 没有这项宿主服务,这里直接拉起系统的文件管理器;失败静默(这只是一个便利动作)。
    /// </summary>
    internal static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = false });
            }
            else if (OperatingSystem.IsMacOS())
            {
                _ = Process.Start(new ProcessStartInfo("open", File.Exists(path) ? $"-R \"{path}\"" : $"\"{path}\"") { UseShellExecute = false });
            }
            else
            {
                string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
                _ = Process.Start(new ProcessStartInfo("xdg-open", $"\"{folder}\"") { UseShellExecute = false });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }
}
