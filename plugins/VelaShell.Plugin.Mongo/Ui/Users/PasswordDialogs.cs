using Avalonia.Controls;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 「重置密码」小对话框:新密码 + 确认 + 一键生成。
/// <para>
/// 两遍输入不是形式主义:密码框默认掩码,打错一个字符的后果是应用全部连不上、
/// 而且没人知道正确的那个是什么。点了「生成」就不必再输第二遍(两栏同时填好并明文显示)。
/// </para>
/// </summary>
internal sealed class ResetPasswordDialogViewModel : DialogViewModel, IViewFactory
{
    private readonly Func<string, bool, Task<bool>> _apply;
    private string _password = "";
    private string _confirm = "";
    private bool _reveal;
    private bool _generated;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="user">要改密码的用户。</param>
    /// <param name="apply">执行改密码(参数:新密码、是否刚生成);成功返回 true(对话框随之关闭)。</param>
    public ResetPasswordDialogViewModel(IMongoWorkspace workspace, MongoUser user, Func<string, bool, Task<bool>> apply)
        : base(workspace)
    {
        _apply = apply;
        Title = Loc["Users_ResetTitle"];
        Subtitle = $"{user.Name}@{user.Db}";
        GenerateCommand = new(() =>
        {
            string generated = UserAdmin.GeneratePassword();
            Password = generated;
            Confirm = generated;
            Reveal = true;
            _generated = true;
        });
        ApplyCommand = new(async () =>
        {
            if (await _apply(_password, _generated).ConfigureAwait(true))
            {
                Close();
            }
        }, () => CanApply);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.key-round";

    /// <inheritdoc />
    public override double Width => 420;

    /// <summary>新密码。</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                _generated = false;
                Revalidate();
            }
        }
    }

    /// <summary>再输一遍。</summary>
    public string Confirm
    {
        get => _confirm;
        set
        {
            if (SetProperty(ref _confirm, value))
            {
                Revalidate();
            }
        }
    }

    /// <summary>明文显示。</summary>
    public bool Reveal
    {
        get => _reveal;
        set
        {
            if (SetProperty(ref _reveal, value))
            {
                RaisePropertyChanged(nameof(PasswordChar));
            }
        }
    }

    /// <summary>掩码字符。</summary>
    public char PasswordChar => _reveal ? '\0' : '•';

    /// <summary>两遍不一致。</summary>
    public bool Mismatch => _confirm.Length > 0 && !string.Equals(_confirm, _password, StringComparison.Ordinal);

    /// <summary>太短(只提醒,不拦:有的环境另有策略插件管长度)。</summary>
    public bool Weak => _password.Length is > 0 and < 12;

    /// <summary>能不能保存。</summary>
    public bool CanApply => _password.Length > 0 && string.Equals(_confirm, _password, StringComparison.Ordinal);

    /// <summary>生成。</summary>
    public RelayCommand GenerateCommand { get; }

    /// <summary>保存。</summary>
    public AsyncCommand ApplyCommand { get; }

    private void Revalidate()
    {
        RaisePropertiesChanged(nameof(Mismatch), nameof(Weak), nameof(CanApply));
        ApplyCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    public Control CreateView() => new ResetPasswordDialogView(this);
}

/// <summary>
/// 轮换 / 生成密码之后的"只显示一次"对话框:密码已经复制到剪贴板,这里再给人看一眼、可再复制。
/// 关掉就再也看不到 —— 服务器只存加盐摘要,插件也不留底。
/// </summary>
internal sealed class RevealPasswordDialogViewModel : DialogViewModel, IViewFactory
{
    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="user">用户(<c>ops_writer@admin</c>)。</param>
    /// <param name="password">新密码。</param>
    public RevealPasswordDialogViewModel(IMongoWorkspace workspace, string user, string password)
        : base(workspace)
    {
        Title = Loc["Users_RevealTitle"];
        Subtitle = user;
        Password = password;
        CopyCommand = new(async () =>
        {
            await Workspace.CopyAsync(password).ConfigureAwait(true);
            Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success });
        });
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.key-round";

    /// <inheritdoc />
    public override string IconToken => "VelaStatusConnected";

    /// <inheritdoc />
    public override double Width => 420;

    /// <summary>新密码。</summary>
    public string Password { get; }

    /// <summary>再复制一次。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <inheritdoc />
    public Control CreateView() => new RevealPasswordDialogView(this);
}
