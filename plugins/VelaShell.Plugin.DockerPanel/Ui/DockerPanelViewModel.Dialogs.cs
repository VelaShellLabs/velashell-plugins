using VelaShell.Plugin.DockerPanel.Docker;
using VelaShell.Plugin.DockerPanel.Ui.Pages;

namespace VelaShell.Plugin.DockerPanel.Ui;

public sealed partial class DockerPanelViewModel
{
    private TaskCompletionSource<bool>? _formPending;

    /// <summary>当前打开的表单;没有时为 <see langword="null" />。</summary>
    public PanelForm? ActiveForm
    {
        get;
        private set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasForm));
            }
        }
    }

    /// <summary>有表单开着。</summary>
    public bool HasForm => ActiveForm is not null;

    /// <summary>当前打开的自定义对话框(拉取镜像那种有自己状态机的)。</summary>
    public object? ActiveDialog
    {
        get;
        set
        {
            if (SetField(ref field, value))
            {
                OnPropertyChanged(nameof(HasDialog));
            }
        }
    }

    /// <summary>有自定义对话框开着。</summary>
    public bool HasDialog => ActiveDialog is not null;

    /// <summary>确认表单。</summary>
    public RelayCommand FormConfirmCommand => field ??= new(_ =>
    {
        if (ActiveForm is not { } form)
        {
            return;
        }
        form.FormError = null;
        foreach (var each in form.Fields)
        {
            each.Error = null;
        }
        // 校验不过就停在原地,并且把原因写在**那一格**下面 ——
        // 一句"输入有误"等于让用户自己去猜是哪一格。
        if (!form.Validate())
        {
            return;
        }
        CompleteForm(true);
    });

    /// <summary>取消表单。</summary>
    public RelayCommand FormCancelCommand => field ??= new(_ => CompleteForm(false));

    /// <summary>
    /// 关掉最上面那一层弹层(Esc 与点击遮罩都走这里)。
    /// <para>
    /// 次序与它们在视觉上的层叠次序一致:对话框 → 表单 → 闸门 → 命令面板。
    /// 一次只关一层 —— 命令面板唤出的动作往往还要再弹一次闸门,
    /// 一按 Esc 就把两层一起收掉,用户会以为自己刚才那下点丢了。
    /// </para>
    /// </summary>
    /// <returns>确实关掉了一层。</returns>
    public bool CloseTopOverlay()
    {
        if (HasDialog)
        {
            CloseDialogCommand.Execute(null);
            return true;
        }
        if (HasForm)
        {
            FormCancelCommand.Execute(null);
            return true;
        }
        if (Confirm.IsOpen)
        {
            Confirm.CancelCommand.Execute(null);
            return true;
        }
        if (Palette.IsOpen)
        {
            Palette.CloseCommand.Execute(null);
            return true;
        }
        return false;
    }

    /// <summary>关掉自定义对话框。</summary>
    public RelayCommand CloseDialogCommand => field ??= new(_ =>
    {
        if (ActiveDialog is IAsyncDisposable disposable)
        {
            _ = disposable.DisposeAsync();
        }
        ActiveDialog = null;
    });

    /// <summary>
    /// 打开一个表单并等用户确认。
    /// <para>
    /// 与确认闸门一样,同一时刻只允许一个 —— 两层表单叠在一起,
    /// 用户不可能说清自己在填哪一个。
    /// </para>
    /// </summary>
    public Task<bool> ShowFormAsync(PanelForm form)
    {
        if (_formPending is not null)
        {
            return Task.FromResult(false);
        }
        _formPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Post(() => ActiveForm = form);
        return _formPending.Task;
    }

    private void CompleteForm(bool confirmed)
    {
        var pending = _formPending;
        _formPending = null;
        Ui.Post(() => ActiveForm = null);
        pending?.TrySetResult(confirmed);
    }

    /// <summary>打开拉取镜像对话框。</summary>
    public Task ShowPullDialogAsync(string? initialReference)
    {
        ActiveDialog = new PullImageViewModel(this, initialReference);
        return Task.CompletedTask;
    }

    /// <summary>打开「运行容器」表单,确认后创建并按需启动。</summary>
    public async Task ShowRunContainerAsync(string image)
    {
        if (Client is not { } client)
        {
            return;
        }
        string[] networks;
        try
        {
            var all = await client.ListNetworksAsync(Lifetime).ConfigureAwait(true);
            networks = [.. all.Select(n => n.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            networks = ["bridge"];
        }
        // 镜像的小字:大小与平台。表单里原来完全看不到自己要跑的是哪个镜像
        // (只有最底下那条等效命令里出现过),而这颗按钮是从好几个地方点进来的。
        var detail = "";
        try
        {
            var images = await client.ListImagesAsync(false, Lifetime).ConfigureAwait(true);
            if (images.FirstOrDefault(i => i.RepoTags?.Contains(image) == true) is { } match)
            {
                detail = Humanize.Bytes(match.Size);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 小字而已,取不到就不写 —— 不值得为它把整张表单挡下来。
            Context.Log.Debug($"read image size failed: {ex.Message}");
        }
        var form = new RunContainerForm(image, detail, networks);
        if (!await ShowFormAsync(form).ConfigureAwait(true))
        {
            return;
        }
        var task = Tasks.Start("Icon.play", $"运行 {image}", indeterminate: true);
        try
        {
            var created = await client
                .CreateContainerAsync(form.ContainerName, form.ToRequest(), task.Token).ConfigureAwait(true);
            foreach (var warning in created.Warnings ?? [])
            {
                Feedback.Notify(FeedbackKind.Warning, "daemon 有话说", warning);
            }
            if (form.Detach)
            {
                await client.StartContainerAsync(created.Id, task.Token).ConfigureAwait(true);
            }
            task.Finish(PanelTaskState.Succeeded, "完成", Humanize.ShortId(created.Id));
            Feedback.Notify(FeedbackKind.Success,
                form.Detach ? "容器已启动" : "容器已创建",
                $"{(form.ContainerName.Length > 0 ? form.ContainerName : Humanize.ShortId(created.Id))} · {image}");
            await GoToAsync(PanelPage.Containers).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            task.Finish(PanelTaskState.Failed, "失败", ex.Message);
            Feedback.ReportError("运行容器", ex);
        }
    }
}
