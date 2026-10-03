using System.ComponentModel;
using System.Globalization;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 连接中 / 连接失败的占位标签(设计稿 22「连接状态」):照宿主「先建标签、后连会话」的习惯,
/// 在标签里显示卡片,不弹模态框。连上了外壳就把它换成对象列表;没连上就留着,给「编辑连接 / 关闭标签页 / 重新连接」。
/// </summary>
internal sealed class ConnectionStateTabViewModel : WorkspaceTab
{
    private readonly MongoWorkspaceViewModel _shell;
    private string _jumpName = "";

    /// <summary>构造。</summary>
    /// <param name="shell">工作台外壳。</param>
    /// <param name="entry">那条连接。</param>
    public ConnectionStateTabViewModel(MongoWorkspaceViewModel shell, ConnectionEntry entry)
        : base(shell)
    {
        _shell = shell;
        Entry = entry;
        Title = entry.Name;
        entry.PropertyChanged += OnEntryChanged;
        CancelCommand = new RelayCommand(() => entry.Connecting?.Cancel());
        EditCommand = new RelayCommand(() => shell.EditConnection(entry));
        ReconnectCommand = new AsyncCommand(() => shell.ConnectAsync(entry));
        TrustCommand = new AsyncCommand(() => shell.TrustAndReconnectAsync(entry));
        _ = ResolveJumpNameAsync();
    }

    /// <summary>那条连接。</summary>
    public ConnectionEntry Entry { get; }

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Connection;

    /// <inheritdoc />
    public override string Key => $"conn:{Entry.Profile.Id}";

    /// <inheritdoc />
    public override string IconKey => "Mongo.leaf";

    /// <inheritdoc />
    public override string IconToken => IsFailed ? "VelaError" : "VelaAccent";

    /// <summary>正在连(「正在连接」卡片)。</summary>
    public bool IsConnecting => Entry.State == ConnectionState.Connecting;

    /// <summary>没连上(「无法连接」卡片)。</summary>
    public bool IsFailed => Entry.State == ConnectionState.Failed;

    /// <summary>「正在连接 mongo-inner-01」。</summary>
    public string ConnectingTitle => Loc.Format("Conn_Connecting", Entry.Name);

    /// <summary>「无法连接 mongo-inner-01」。</summary>
    public string FailedTitle => Loc.Format("Conn_FailedTitle", Entry.Name);

    /// <summary>
    /// 卡片副标题:连接中写「MongoDB · 经 SSH 跳板 bastion-ops」,失败写全(<c>MongoDB · 10.20.3.21:27017 · 经 bastion-ops</c>)——
    /// 失败时用户要知道到底连的是哪儿。
    /// </summary>
    public string Detail => IsConnecting
        ? _jumpName.Length > 0 ? $"MongoDB · {Loc.Format("Conn_ViaJump", _jumpName)}" : Entry.Detail
        : _jumpName.Length > 0 ? $"{Entry.Detail} · {Loc.Format("Conn_ViaJump", _jumpName)}" : Entry.Detail;

    /// <summary>失败原因(等宽字的错误框里)。</summary>
    public string ErrorMessage => Entry.Failure?.Message ?? "";

    /// <summary>证书不受信任且拿到了证书本体:给「信任此证书并重连」。</summary>
    public bool CanTrust => Entry.Failure?.Certificate is not null;

    /// <summary>那张证书的一行说明。</summary>
    public string CertificateText => Entry.Failure?.Certificate is { } c
        ? string.Format(CultureInfo.CurrentCulture, Loc["Conn_CertLine"], c.Subject, c.Issuer, c.NotAfter, c.Thumbprint)
        : "";

    /// <summary>取消正在进行的连接。</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>编辑连接(设计稿 10 的对话框)。</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>重新连接。</summary>
    public AsyncCommand ReconnectCommand { get; }

    /// <summary>信任此证书并重连。</summary>
    public AsyncCommand TrustCommand { get; }

    /// <summary>跳板的名字要问宿主(已保存的 SSH 连接);问不到就不写。</summary>
    private async Task ResolveJumpNameAsync()
    {
        string id = Entry.Profile.Parsed.JumpSessionId;
        if (id.Length == 0 || Entry.Profile.Parsed.Topology == MongoTopology.Srv)
        {
            return;
        }
        try
        {
            IReadOnlyList<VelaShell.PluginSdk.Sessions.SavedSessionInfo> saved =
                await _shell.Context.Sessions.ListSavedAsync().ConfigureAwait(true);
            _jumpName = saved.FirstOrDefault(s => s.SavedSessionId == id)?.Name ?? "";
            RaisePropertyChanged(nameof(Detail));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 列不出来(宿主不支持、权限)只是少一个名字。
        }
    }

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionEntry.State) or nameof(ConnectionEntry.Failure) or nameof(ConnectionEntry.Profile))
        {
            Title = Entry.Name;
            RaisePropertiesChanged(nameof(IsConnecting), nameof(IsFailed), nameof(ConnectingTitle), nameof(FailedTitle),
                nameof(Detail), nameof(ErrorMessage), nameof(CanTrust), nameof(CertificateText), nameof(IconToken));
        }
    }

    /// <summary>关掉标签时还在连就一并取消 —— 用户不想等了。</summary>
    public override void Dispose()
    {
        Entry.PropertyChanged -= OnEntryChanged;
        if (Entry.State == ConnectionState.Connecting)
        {
            Entry.Connecting?.Cancel();
        }
    }
}
