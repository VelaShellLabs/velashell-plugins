using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.PluginSdk.Sessions;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>主机列表里的一行(设计稿 10:主机 + 端口 + 测试后发现的角色 + 删除)。</summary>
internal sealed class HostRowViewModel : ObservableObject
{
    private string _host;
    private string _port;

    /// <summary>构造。</summary>
    public HostRowViewModel(string host, int port, bool isFirst, Action<HostRowViewModel> remove, Action changed)
    {
        _host = host;
        _port = port.ToString(CultureInfo.InvariantCulture);
        IsFirst = isFirst;
        RemoveCommand = new RelayCommand(() => remove(this));
        Changed = changed;
    }

    private Action Changed { get; }

    /// <summary>主机。</summary>
    public string Host
    {
        get => _host;
        set
        {
            if (SetProperty(ref _host, value))
            {
                Changed();
            }
        }
    }

    /// <summary>端口(文本:用户可能正敲到一半)。</summary>
    public string Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
            {
                Changed();
            }
        }
    }

    /// <summary>端口数值;写坏了按 27017。</summary>
    public int PortNumber => int.TryParse(_port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536 ? p : 27017;

    /// <summary>第一台(就是连接的主机,不能删)。</summary>
    public bool IsFirst { get; }

    /// <summary>能删(除第一台外)。</summary>
    public bool CanRemove => !IsFirst;

    /// <summary>测试后发现的角色。</summary>
    public string? Badge
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasBadge));
            }
        }
    }

    /// <summary>有角色徽章。</summary>
    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>徽章语气色。</summary>
    public Tone BadgeTone
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsBadgeOk), nameof(IsBadgeInfo), nameof(IsBadgeWarn), nameof(IsBadgeErr));
            }
        }
    }

    /// <summary>主节点 / 单机(绿)。</summary>
    public bool IsBadgeOk => BadgeTone == Tone.Success;

    /// <summary>从节点(蓝)。</summary>
    public bool IsBadgeInfo => BadgeTone == Tone.Info;

    /// <summary>恢复中(橙)。</summary>
    public bool IsBadgeWarn => BadgeTone == Tone.Warning;

    /// <summary>不可达(红)。</summary>
    public bool IsBadgeErr => BadgeTone == Tone.Danger;

    /// <summary>删掉这一行。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary><c>host:port</c>(IPv6 加方括号)。</summary>
    public string Address => _host.Contains(':', StringComparison.Ordinal) && !_host.StartsWith('[')
        ? $"[{_host.Trim()}]:{PortNumber}"
        : $"{_host.Trim()}:{PortNumber}";
}

/// <summary>连接串预览的一段(视图按角色着色)。</summary>
/// <param name="Text">文字。</param>
/// <param name="Role">角色。</param>
internal sealed record PreviewSpanViewModel(string Text, PreviewRole Role)
{
    /// <summary>scheme。</summary>
    public bool IsScheme => Role == PreviewRole.Scheme;

    /// <summary>用户名。</summary>
    public bool IsUser => Role == PreviewRole.User;

    /// <summary>口令。</summary>
    public bool IsSecret => Role == PreviewRole.Secret;

    /// <summary>主机。</summary>
    public bool IsHost => Role == PreviewRole.Host;

    /// <summary>库。</summary>
    public bool IsPath => Role == PreviewRole.Path;

    /// <summary>参数名。</summary>
    public bool IsKey => Role == PreviewRole.Key;

    /// <summary>参数值。</summary>
    public bool IsValue => Role == PreviewRole.Value;
}

/// <summary>测试结果的一行。</summary>
internal sealed class ProbeStepViewModel(ProbeStep step) : ObservableObject
{
    private ProbeStep _step = step;

    /// <summary>步骤键。</summary>
    public string Key => _step.Key;

    /// <summary>标题。</summary>
    public string Title => _step.Title;

    /// <summary>细节。</summary>
    public string Detail => _step.Detail ?? "";

    /// <summary>有细节。</summary>
    public bool HasDetail => !string.IsNullOrEmpty(_step.Detail);

    /// <summary>耗时(<c>9 ms</c>)。</summary>
    public string Elapsed => _step.ElapsedMs is { } ms ? $"{ms} ms" : "";

    /// <summary>状态。</summary>
    public ProbeState State => _step.State;

    /// <summary>通过。</summary>
    public bool IsPassed => _step.State == ProbeState.Passed;

    /// <summary>有保留。</summary>
    public bool IsWarning => _step.State == ProbeState.Warning;

    /// <summary>失败。</summary>
    public bool IsFailed => _step.State == ProbeState.Failed;

    /// <summary>进行中。</summary>
    public bool IsRunning => _step.State == ProbeState.Running;

    /// <summary>没轮到 / 没执行。</summary>
    public bool IsIdle => _step.State is ProbeState.Pending or ProbeState.Skipped;

    /// <summary>原地更新。</summary>
    public void Update(ProbeStep step)
    {
        _step = step;
        RaisePropertiesChanged(nameof(Title), nameof(Detail), nameof(HasDetail), nameof(Elapsed), nameof(State),
            nameof(IsPassed), nameof(IsWarning), nameof(IsFailed), nameof(IsRunning), nameof(IsIdle));
    }
}

/// <summary>发现的一个成员。</summary>
/// <param name="Endpoint">端点。</param>
internal sealed record ProbeEndpointViewModel(ProbeEndpoint Endpoint)
{
    /// <summary>地址。</summary>
    public string Address => Endpoint.Address;

    /// <summary>角色。</summary>
    public string Role => Endpoint.Role ?? "";

    /// <summary>补充(延迟)。</summary>
    public string Detail => Endpoint.Detail ?? "";

    /// <summary>主节点 / 单机。</summary>
    public bool IsOk => Endpoint.Tone == Tone.Success;

    /// <summary>从节点。</summary>
    public bool IsInfo => Endpoint.Tone == Tone.Info;

    /// <summary>恢复中。</summary>
    public bool IsWarn => Endpoint.Tone == Tone.Warning;

    /// <summary>不可达。</summary>
    public bool IsErr => Endpoint.Tone == Tone.Danger;
}

/// <summary>下拉里的一项:存的值与显示的字分开(<c>DEFAULT</c> 显示成「默认(协商 SCRAM-SHA-256)」)。</summary>
/// <param name="Value">存的值。</param>
/// <param name="Label">显示的字。</param>
internal sealed record Choice(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>可选的 SSH 跳板(宿主里已保存的 SSH 连接)。</summary>
/// <param name="Id">已保存连接的 id。</param>
/// <param name="Display">显示(<c>bastion-ops · 10.0.0.5</c>)。</param>
internal sealed record SshChoice(string Id, string Display)
{
    /// <inheritdoc />
    public override string ToString() => Display;
}

/// <summary>
/// 新建 / 编辑 MongoDB 连接(设计稿 10,1020 × 760):左边 700 的表单(基本 / 服务器 / 认证 / 安全通道,
/// 设计稿外的调优项收在末尾的「高级」里),右边 320 的侧栏(连接字符串 / 测试结果 / 发现的成员 / 安全策略),
/// 页脚「测试连接 · 取消 · 仅保存 · 保存并连接」。
/// <para>
/// 口令只在这里过手:存进宿主的加密密钥库,不写进连接串、不写进插件存储。编辑时口令框留空表示不改。
/// </para>
/// </summary>
internal sealed class ConnectionDialogViewModel : DialogViewModel, IDisposable
{
    private static readonly string[] ReadPreferenceNames = ["primary", "primaryPreferred", "secondary", "secondaryPreferred", "nearest"];

    private readonly MongoWorkspaceViewModel _shell;
    private readonly MongoProfile _original;
    private readonly string? _passwordFrom;
    private string _name;
    private string _group;
    private string _environment;
    private string _topology;
    private string _srvHost = "";
    private string _uri = "";
    private string _replicaSet;
    private string _database;
    private string _readPreference;
    private string _mechanism;
    private string _authSource;
    private string _username;
    private bool _sshEnabled;
    private SshChoice? _sshChoice;
    private bool _tlsEnabled;
    private string _tlsCaFile;
    private string _tlsCertFile;
    private bool _readOnly;
    private bool _confirmWrites;
    private bool _disableDropDatabase;
    private bool _policyTouched;
    private bool _directConnection;
    private string _appName;
    private string _connectTimeout;
    private string _maxTimeMs;
    private string _pageSize;
    private string _sampleSize;
    private string _ejson;
    private bool? _testSucceeded;
    private CancellationTokenSource? _probe;

    /// <summary>构造。</summary>
    /// <param name="shell">工作台外壳。</param>
    /// <param name="profile">要编辑的连接(副本;新建时是一条空的)。</param>
    /// <param name="isNew">新建还是编辑。</param>
    /// <param name="passwordFrom">复制连接时,口令从哪条已存的连接带过来。</param>
    public ConnectionDialogViewModel(MongoWorkspaceViewModel shell, MongoProfile profile, bool isNew, string? passwordFrom = null)
        : base(shell)
    {
        _shell = shell;
        _original = profile;
        _passwordFrom = passwordFrom;
        IsNew = isNew;
        Title = isNew ? Loc["Conn_NewTitle"] : Loc["Conn_EditTitle"];
        Subtitle = isNew ? "" : profile.Name;

        MongoSettings s = profile.Parsed;
        _name = profile.Name;
        _group = profile.Group;
        Groups = [.. shell.Connections.Select(static c => c.Profile.Group).Where(static g => g.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
        MechanismChoices =
        [
            new("DEFAULT", Loc["Mongo_AuthDefault"]),
            new("SCRAM-SHA-256", "SCRAM-SHA-256"),
            new("SCRAM-SHA-1", "SCRAM-SHA-1"),
            new("MONGODB-X509", "X.509"),
            new("PLAIN", Loc["Conn_AuthPlain"])
        ];
        EjsonChoices =
        [
            new("relaxed", "Relaxed EJSON"),
            new("canonical", "Canonical EJSON"),
            new("shell", Loc["Conn_EjsonShell"])
        ];
        _environment = profile.Get(MongoSettings.KeyEnvironment, "development");
        _topology = profile.Get(MongoSettings.KeyTopology, "hosts");
        switch (s.Topology)
        {
            case MongoTopology.Srv:
                _srvHost = profile.Host;
                break;
            case MongoTopology.Uri:
                _uri = profile.Host;
                break;
        }
        Hosts.Add(new HostRowViewModel(s.Topology == MongoTopology.Hosts ? profile.Host : "",
            s.Topology == MongoTopology.Hosts ? profile.Port : 27017, true, RemoveHost, OnChanged));
        foreach (string extra in s.AdditionalHosts)
        {
            MongoDB.Driver.MongoServerAddress address = MongoConnection.ParseAddress(extra);
            Hosts.Add(new HostRowViewModel(address.Host, address.Port, false, RemoveHost, OnChanged));
        }
        _replicaSet = s.ReplicaSet;
        _database = s.Database;
        _readPreference = s.ReadPreference;
        _mechanism = s.AuthMechanism;
        _authSource = s.AuthSource;
        _username = profile.Username;
        _sshEnabled = s.JumpSessionId.Length > 0;
        _tlsEnabled = s.UseTls;
        _tlsCaFile = s.TlsCaFile;
        _tlsCertFile = s.TlsCertificateFile;
        _readOnly = s.ReadOnly;
        _confirmWrites = s.ConfirmWrites;
        _disableDropDatabase = s.DisableDropDatabase;
        _policyTouched = profile.Settings.ContainsKey(MongoSettings.KeyReadOnly) || profile.Settings.ContainsKey(MongoSettings.KeyConfirmWrites);
        _directConnection = s.DirectConnection;
        _appName = s.AppName;
        _connectTimeout = s.ConnectTimeoutMs.ToString(CultureInfo.InvariantCulture);
        _maxTimeMs = s.MaxTimeMs.ToString(CultureInfo.InvariantCulture);
        _pageSize = s.PageSize.ToString(CultureInfo.InvariantCulture);
        _sampleSize = s.SampleSize.ToString(CultureInfo.InvariantCulture);
        _ejson = profile.Get(MongoSettings.KeyEjson, "relaxed");

        AddHostCommand = new RelayCommand(AddHost);
        TogglePasswordCommand = new RelayCommand(() => PasswordVisible = !PasswordVisible);
        ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);
        CopyPreviewCommand = new AsyncCommand(() => _shell.CopyAsync(PreviewText));
        BrowseCaCommand = new AsyncCommand(async () => TlsCaFile = await PickAsync(TlsCaFile, Loc["Mongo_TlsCaFile"], new FileKind("PEM", "*.pem", "*.crt", "*.cer")).ConfigureAwait(true));
        BrowseCertCommand = new AsyncCommand(async () => TlsCertFile = await PickAsync(TlsCertFile, Loc["Mongo_TlsCertFile"], new FileKind("PFX / PEM", "*.pfx", "*.p12", "*.pem")).ConfigureAwait(true));
        TestCommand = new AsyncCommand(TestAsync);
        SaveCommand = new AsyncCommand(() => SaveAsync(connect: false));
        SaveAndConnectCommand = new AsyncCommand(() => SaveAsync(connect: true));
        RefreshPreview();
        _ = LoadAsync(profile.Id);
    }

    /// <inheritdoc />
    public override string IconKey => "Mongo.leaf";

    /// <inheritdoc />
    public override string IconToken => "VelaStatusConnected";

    /// <inheritdoc />
    public override double Width => 1020;

    /// <inheritdoc />
    public override double Height => 760;

    /// <inheritdoc />
    public override bool CanCloseWithEscape => true;

    /// <summary>新建(否则是编辑)。</summary>
    public bool IsNew { get; }

    // ── 基本 ───────────────────────────────────────────────────────────────

    /// <summary>连接名。</summary>
    public string Name
    {
        get => _name;
        set
        {
            // 改名不影响连不连得上:不作废测试结果。
            if (SetProperty(ref _name, value))
            {
                OnChanged();
            }
        }
    }

    /// <summary>分组(对象树按它分节;空 = 不分组)。</summary>
    public string Group
    {
        get => _group;
        set
        {
            if (SetProperty(ref _group, value ?? ""))
            {
                OnChanged();
            }
        }
    }

    /// <summary>已有的分组(下拉候选)。</summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>开发。</summary>
    public bool IsDevelopment
    {
        get => _environment == "development";
        set => SetEnvironment(value, "development");
    }

    /// <summary>测试。</summary>
    public bool IsTesting
    {
        get => _environment == "testing";
        set => SetEnvironment(value, "testing");
    }

    /// <summary>生产(默认只读、写前确认)。</summary>
    public bool IsProduction
    {
        get => _environment == "production";
        set => SetEnvironment(value, "production");
    }

    private void SetEnvironment(bool on, string value)
    {
        if (!on || _environment == value)
        {
            return;
        }
        _environment = value;
        // 安全策略没被用户动过时跟着环境走:换成生产就默认只读 + 写前确认。
        if (!_policyTouched)
        {
            _readOnly = _confirmWrites = value == "production";
            RaisePropertiesChanged(nameof(ReadOnly), nameof(ConfirmWrites));
        }
        RaisePropertiesChanged(nameof(IsDevelopment), nameof(IsTesting), nameof(IsProduction));
        OnChanged();
    }

    // ── 服务器 ─────────────────────────────────────────────────────────────

    /// <summary>主机列表形态。</summary>
    public bool IsHostsTopology
    {
        get => _topology == "hosts";
        set => SetTopology(value, "hosts");
    }

    /// <summary>SRV 形态。</summary>
    public bool IsSrvTopology
    {
        get => _topology == "srv";
        set => SetTopology(value, "srv");
    }

    /// <summary>连接字符串形态。</summary>
    public bool IsUriTopology
    {
        get => _topology == "uri";
        set => SetTopology(value, "uri");
    }

    private void SetTopology(bool on, string value)
    {
        if (!on || _topology == value)
        {
            return;
        }
        _topology = value;
        // SRV 默认走 TLS(Atlas 就是这样);另外两种保持用户的选择。
        if (value == "srv" && !_tlsEnabled)
        {
            _tlsEnabled = true;
            RaisePropertyChanged(nameof(TlsEnabled));
        }
        RaisePropertiesChanged(nameof(IsHostsTopology), nameof(IsSrvTopology), nameof(IsUriTopology), nameof(CanUseSsh), nameof(SshHint));
        ClearProbe();
        OnChanged();
    }

    /// <summary>主机列表(第一行是连接的主机)。</summary>
    public ObservableCollection<HostRowViewModel> Hosts { get; } = [];

    /// <summary>SRV 域名。</summary>
    public string SrvHost
    {
        get => _srvHost;
        set => Set(ref _srvHost, value);
    }

    /// <summary>连接字符串。</summary>
    public string Uri
    {
        get => _uri;
        set => Set(ref _uri, value);
    }

    /// <summary>副本集名。</summary>
    public string ReplicaSet
    {
        get => _replicaSet;
        set => Set(ref _replicaSet, value);
    }

    /// <summary>默认库。</summary>
    public string Database
    {
        get => _database;
        set => Set(ref _database, value);
    }

    /// <summary>读偏好的候选。</summary>
    public IReadOnlyList<string> ReadPreferences => ReadPreferenceNames;

    /// <summary>读偏好。</summary>
    public string ReadPreference
    {
        get => _readPreference;
        set => Set(ref _readPreference, value ?? "primary");
    }

    /// <summary>加一台主机。</summary>
    public RelayCommand AddHostCommand { get; }

    private void AddHost()
    {
        Hosts.Add(new HostRowViewModel("", 27017, false, RemoveHost, OnChanged));
        OnChanged();
    }

    private void RemoveHost(HostRowViewModel row)
    {
        if (!row.IsFirst && Hosts.Remove(row))
        {
            OnChanged();
        }
    }

    // ── 认证 ───────────────────────────────────────────────────────────────

    /// <summary>认证机制的候选。</summary>
    public IReadOnlyList<Choice> MechanismChoices { get; }

    /// <summary>下拉里选中的认证机制。</summary>
    public Choice? SelectedMechanism
    {
        get => MechanismChoices.FirstOrDefault(c => c.Value == _mechanism) ?? MechanismChoices[0];
        set
        {
            if (value is not null)
            {
                Mechanism = value.Value;
            }
        }
    }

    /// <summary>认证机制。</summary>
    public string Mechanism
    {
        get => _mechanism;
        set
        {
            Set(ref _mechanism, value ?? "DEFAULT");
            RaisePropertyChanged(nameof(SelectedMechanism));
        }
    }

    /// <summary>authSource。</summary>
    public string AuthSource
    {
        get => _authSource;
        set => Set(ref _authSource, value);
    }

    /// <summary>用户名(空 = 匿名)。</summary>
    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    /// <summary>口令(编辑时留空 = 不改)。</summary>
    public string Password
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                ClearProbe();
            }
        }
    } = "";

    /// <summary>口令明文显示。</summary>
    public bool PasswordVisible
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>密钥库里已经存着这条连接的口令(口令框的占位字写「已保存」)。</summary>
    public bool HasStoredPassword
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(PasswordPlaceholder));
            }
        }
    }

    /// <summary>口令框的占位字。</summary>
    public string PasswordPlaceholder => HasStoredPassword ? Loc["Conn_PasswordStored"] : "";

    /// <summary>切换明文显示。</summary>
    public RelayCommand TogglePasswordCommand { get; }

    // ── 安全通道 ───────────────────────────────────────────────────────────

    /// <summary>宿主里已保存的 SSH 连接(跳板的候选)。</summary>
    public ObservableCollection<SshChoice> SshChoices { get; } = [];

    /// <summary>走 SSH 跳板。</summary>
    public bool SshEnabled
    {
        get => _sshEnabled;
        set
        {
            if (!SetProperty(ref _sshEnabled, value))
            {
                return;
            }
            if (value)
            {
                _sshChoice ??= SshChoices.FirstOrDefault();
                RaisePropertyChanged(nameof(SshChoice));
            }
            ClearProbe();
            OnChanged();
        }
    }

    /// <summary>选中的跳板。</summary>
    public SshChoice? SshChoice
    {
        get => _sshChoice;
        set
        {
            if (SetProperty(ref _sshChoice, value))
            {
                ClearProbe();
                OnChanged();
            }
        }
    }

    /// <summary>跳板开关能不能开:SRV 不能走跳板;一条已保存的 SSH 连接都没有也开不了(已开着的仍能关)。</summary>
    public bool CanUseSsh => (_topology != "srv" && SshChoices.Count > 0) || _sshEnabled;

    /// <summary>跳板卡片上的说明。</summary>
    public string SshHint => _topology == "srv"
        ? Loc["Conn_SshNoSrv"]
        : SshChoices.Count == 0 ? Loc["Conn_SshNone"] : Loc["Mongo_JumpSessionHint"];

    /// <summary>TLS。</summary>
    public bool TlsEnabled
    {
        get => _tlsEnabled;
        set => Set(ref _tlsEnabled, value);
    }

    /// <summary>CA 证书文件。</summary>
    public string TlsCaFile
    {
        get => _tlsCaFile;
        set => Set(ref _tlsCaFile, value);
    }

    /// <summary>客户端证书文件。</summary>
    public string TlsCertFile
    {
        get => _tlsCertFile;
        set => Set(ref _tlsCertFile, value);
    }

    /// <summary>选 CA 证书。</summary>
    public AsyncCommand BrowseCaCommand { get; }

    /// <summary>选客户端证书。</summary>
    public AsyncCommand BrowseCertCommand { get; }

    // ── 安全策略(右侧栏)──────────────────────────────────────────────────

    /// <summary>默认以只读模式打开。</summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (SetProperty(ref _readOnly, value))
            {
                _policyTouched = true;
            }
        }
    }

    /// <summary>写操作前二次确认。</summary>
    public bool ConfirmWrites
    {
        get => _confirmWrites;
        set
        {
            if (SetProperty(ref _confirmWrites, value))
            {
                _policyTouched = true;
            }
        }
    }

    /// <summary>禁用 dropDatabase。</summary>
    public bool DisableDropDatabase
    {
        get => _disableDropDatabase;
        set => SetProperty(ref _disableDropDatabase, value);
    }

    // ── 高级(设计稿外的调优项)───────────────────────────────────────────

    /// <summary>「高级」展开着。</summary>
    public bool ShowAdvanced
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>展开 / 收起「高级」。</summary>
    public RelayCommand ToggleAdvancedCommand { get; }

    /// <summary>直连(不做副本集发现)。</summary>
    public bool DirectConnection
    {
        get => _directConnection;
        set => Set(ref _directConnection, value);
    }

    /// <summary>appName。</summary>
    public string AppName
    {
        get => _appName;
        set => Set(ref _appName, value);
    }

    /// <summary>连接超时(毫秒)。</summary>
    public string ConnectTimeout
    {
        get => _connectTimeout;
        set => SetProperty(ref _connectTimeout, value);
    }

    /// <summary>默认 maxTimeMS。</summary>
    public string MaxTimeMs
    {
        get => _maxTimeMs;
        set => SetProperty(ref _maxTimeMs, value);
    }

    /// <summary>每页行数。</summary>
    public string PageSize
    {
        get => _pageSize;
        set => SetProperty(ref _pageSize, value);
    }

    /// <summary>Schema 抽样数。</summary>
    public string SampleSize
    {
        get => _sampleSize;
        set => SetProperty(ref _sampleSize, value);
    }

    /// <summary>扩展 JSON 模式的候选。</summary>
    public IReadOnlyList<Choice> EjsonChoices { get; }

    /// <summary>下拉里选中的扩展 JSON 模式。</summary>
    public Choice? SelectedEjson
    {
        get => EjsonChoices.FirstOrDefault(c => c.Value == _ejson) ?? EjsonChoices[0];
        set
        {
            if (value is not null && SetProperty(ref _ejson, value.Value, nameof(Ejson)))
            {
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>扩展 JSON 模式。</summary>
    public string Ejson
    {
        get => _ejson;
        set => SetProperty(ref _ejson, value ?? "relaxed");
    }

    /// <summary>对象树列出系统库。</summary>
    public bool ShowSystemDatabases
    {
        get;
        set => SetProperty(ref field, value);
    }

    // ── 右侧栏:连接字符串 ─────────────────────────────────────────────────

    /// <summary>预览的各段(按角色着色)。</summary>
    public ObservableCollection<PreviewSpanViewModel> PreviewSpans { get; } = [];

    /// <summary>整串(复制用)。</summary>
    public string PreviewText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>复制连接串。</summary>
    public AsyncCommand CopyPreviewCommand { get; }

    // ── 右侧栏:测试结果与发现的成员 ───────────────────────────────────────

    /// <summary>测试结果。</summary>
    public ObservableCollection<ProbeStepViewModel> Steps { get; } = [];

    /// <summary>还没测过(给一行「点测试连接逐步检查」)。</summary>
    public bool ShowStepsHint => Steps.Count == 0;

    /// <summary>发现的成员。</summary>
    public ObservableCollection<ProbeEndpointViewModel> Members { get; } = [];

    /// <summary>有成员。</summary>
    public bool HasMembers => Members.Count > 0;

    /// <summary>正在测。</summary>
    public bool IsProbing
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>页脚的结论(<c>连接成功 · 38 ms</c>)。</summary>
    public string TestSummary
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasTestSummary));
            }
        }
    } = "";

    /// <summary>有结论。</summary>
    public bool HasTestSummary => TestSummary.Length > 0;

    /// <summary>测试通过了。</summary>
    public bool TestPassed => _testSucceeded == true;

    /// <summary>测试失败了。</summary>
    public bool TestFailed => _testSucceeded == false;

    /// <summary>测试连接。</summary>
    public AsyncCommand TestCommand { get; }

    // ── 页脚 ───────────────────────────────────────────────────────────────

    /// <summary>页脚的错误(名字没填、主机没填、存不下)。</summary>
    public string Error
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    } = "";

    /// <summary>有错误。</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>正在存(按钮灰掉,免得点两次)。</summary>
    public bool IsSaving
    {
        get;
        private set => SetProperty(ref field, value);
    }

    /// <summary>仅保存。</summary>
    public AsyncCommand SaveCommand { get; }

    /// <summary>保存并连接。</summary>
    public AsyncCommand SaveAndConnectCommand { get; }

    // ── 行为 ───────────────────────────────────────────────────────────────

    /// <summary>编辑时问一下密钥库里有没有口令;顺带列出宿主里已保存的 SSH 连接。</summary>
    private async Task LoadAsync(string id)
    {
        try
        {
            string stored = await _shell.Profiles.GetPasswordAsync(_passwordFrom ?? id).ConfigureAwait(true);
            HasStoredPassword = stored.Length > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.Log.Info($"Reading the stored password failed: {ex.Message}");
        }
        try
        {
            IReadOnlyList<SavedSessionInfo> saved = await _shell.Context.Sessions.ListSavedAsync().ConfigureAwait(true);
            foreach (SavedSessionInfo session in saved)
            {
                SshChoices.Add(new SshChoice(session.SavedSessionId, $"{session.Name} · {session.Host}"));
            }
            string jump = _original.Parsed.JumpSessionId;
            _sshChoice = SshChoices.FirstOrDefault(c => c.Id == jump);
            if (_sshEnabled && _sshChoice is null && jump.Length > 0)
            {
                // 跳板那条 SSH 连接在宿主里被删了:列表里补一条占位,开关照旧开着,让用户看得到、换得掉。
                _sshChoice = new SshChoice(jump, Loc["Conn_SshMissing"]);
                SshChoices.Add(_sshChoice);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _shell.Log.Info($"Listing saved SSH sessions failed: {ex.Message}");
        }
        RaisePropertiesChanged(nameof(SshChoice), nameof(CanUseSsh), nameof(SshHint));
    }

    private async Task<string> PickAsync(string current, string title, FileKind kind)
    {
        IReadOnlyList<string> files = await _shell.PickOpenFilesAsync(title, [kind, FileKind.Any]).ConfigureAwait(true);
        return files.Count > 0 ? files[0] : current;
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            ClearProbe();
            OnChanged();
        }
    }

    private void OnChanged()
    {
        Error = "";
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        ConnectionPreview? preview = ConnectionProbe.Preview(ToProfile().ToRequest(""), Loc);
        PreviewSpans.Clear();
        foreach (PreviewSpan span in preview?.Spans ?? [])
        {
            PreviewSpans.Add(new PreviewSpanViewModel(span.Text, span.Role));
        }
        PreviewText = preview?.Text ?? "";
    }

    /// <summary>参数一改,上一次的测试结果就不再代表它了。</summary>
    private void ClearProbe()
    {
        _probe?.Cancel();
        if (Steps.Count == 0 && Members.Count == 0 && TestSummary.Length == 0)
        {
            return;
        }
        Steps.Clear();
        Members.Clear();
        foreach (HostRowViewModel row in Hosts)
        {
            row.Badge = null;
        }
        _testSucceeded = null;
        TestSummary = "";
        RaisePropertiesChanged(nameof(ShowStepsHint), nameof(HasMembers), nameof(TestPassed), nameof(TestFailed));
    }

    /// <summary>表单 → 连接(id、上次连的时间与信任过的证书指纹照原样带着)。</summary>
    /// <returns>连接。</returns>
    internal MongoProfile ToProfile()
    {
        MongoProfile p = _original.Clone();
        p.Name = _name.Trim();
        p.Group = _group.Trim();
        p.Username = _username.Trim();
        p.Set(MongoSettings.KeyTopology, _topology == "hosts" ? null : _topology);
        switch (_topology)
        {
            case "srv":
                p.Host = _srvHost.Trim();
                p.Set(MongoSettings.KeyHosts, null);
                break;
            case "uri":
                p.Host = _uri.Trim();
                p.Set(MongoSettings.KeyHosts, null);
                break;
            default:
                HostRowViewModel first = Hosts[0];
                p.Host = first.Host.Trim();
                p.Port = first.PortNumber;
                p.Set(MongoSettings.KeyHosts, string.Join(",", Hosts.Skip(1).Where(static h => h.Host.Trim().Length > 0).Select(static h => h.Address)));
                break;
        }
        p.Set(MongoSettings.KeyEnvironment, _environment);
        p.Set(MongoSettings.KeyReplicaSet, _replicaSet.Trim());
        p.Set(MongoSettings.KeyDatabase, _database.Trim());
        p.Set(MongoSettings.KeyReadPreference, _readPreference == "primary" ? null : _readPreference);
        p.Set(MongoSettings.KeyAuthMechanism, _mechanism == "DEFAULT" ? null : _mechanism);
        p.Set(MongoSettings.KeyAuthSource, _authSource.Trim() is "admin" or "" ? null : _authSource.Trim());
        p.Set(MongoSettings.KeyJumpSession, _sshEnabled && _topology != "srv" ? _sshChoice?.Id : null);
        p.Set(MongoSettings.KeyTls, _tlsEnabled ? "true" : "false");
        p.Set(MongoSettings.KeyTlsCaFile, _tlsEnabled ? _tlsCaFile.Trim() : null);
        p.Set(MongoSettings.KeyTlsCertFile, _tlsEnabled ? _tlsCertFile.Trim() : null);
        p.Set(MongoSettings.KeyReadOnly, _policyTouched ? Bool(_readOnly) : null);
        p.Set(MongoSettings.KeyConfirmWrites, _policyTouched ? Bool(_confirmWrites) : null);
        p.Set(MongoSettings.KeyDisableDropDatabase, _disableDropDatabase ? null : "false");
        p.Set(MongoSettings.KeyDirect, _directConnection ? "true" : null);
        p.Set(MongoSettings.KeyAppName, _appName.Trim() is "velashell" or "" ? null : _appName.Trim());
        p.Set(MongoSettings.KeyConnectTimeout, NonDefault(_connectTimeout, 10_000));
        p.Set(MongoSettings.KeyMaxTimeMs, NonDefault(_maxTimeMs, 30_000));
        p.Set(MongoSettings.KeyPageSize, NonDefault(_pageSize, 50));
        p.Set(MongoSettings.KeySampleSize, NonDefault(_sampleSize, 1000));
        p.Set(MongoSettings.KeyEjson, _ejson == "relaxed" ? null : _ejson);
        p.Set(MongoSettings.KeyShowSystemDatabases, ShowSystemDatabases ? "true" : null);
        return p;

        static string Bool(bool value) => value ? "true" : "false";

        static string? NonDefault(string text, int fallback) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n != fallback
                ? n.ToString(CultureInfo.InvariantCulture)
                : null;
    }

    /// <summary>名字与主机必填;名字不能与别的连接重复(树上分不清)。</summary>
    private string? Validate(MongoProfile profile)
    {
        if (profile.Name.Length == 0)
        {
            return Loc["Conn_NeedName"];
        }
        if (_shell.Connections.Any(c => c.Profile.Id != profile.Id && string.Equals(c.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return Loc.Format("Conn_DuplicateName", profile.Name);
        }
        if (profile.Host.Length == 0)
        {
            return Loc[_topology == "uri" ? "Conn_NeedUri" : "Conn_NeedHost"];
        }
        return null;
    }

    /// <summary>口令框为空时,测试与连接用密钥库里存着的那个。</summary>
    private async Task<string> EffectivePasswordAsync()
    {
        if (Password.Length > 0 || _username.Trim().Length == 0)
        {
            return Password;
        }
        return await _shell.Profiles.GetPasswordAsync(_passwordFrom ?? _original.Id).ConfigureAwait(true);
    }

    private async Task TestAsync()
    {
        MongoProfile profile = ToProfile();
        if (profile.Host.Length == 0)
        {
            Error = Loc[_topology == "uri" ? "Conn_NeedUri" : "Conn_NeedHost"];
            return;
        }
        _probe?.Cancel();
        var cts = new CancellationTokenSource();
        _probe = cts;
        Steps.Clear();
        Members.Clear();
        foreach (HostRowViewModel row in Hosts)
        {
            row.Badge = null;
        }
        _testSucceeded = null;
        TestSummary = "";
        IsProbing = true;
        RaisePropertiesChanged(nameof(ShowStepsHint), nameof(HasMembers), nameof(TestPassed), nameof(TestFailed));
        try
        {
            string password = await EffectivePasswordAsync().ConfigureAwait(true);
            ProbeReport report = await _shell.Connector.ProbeAsync(profile, password, new UiProgress(Apply), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            foreach (ProbeStep step in report.Steps)
            {
                Apply(step);
            }
            foreach (ProbeEndpoint endpoint in report.Endpoints)
            {
                Members.Add(new ProbeEndpointViewModel(endpoint));
            }
            ApplyBadges(report.Endpoints);
            _testSucceeded = report.Succeeded;
            TestSummary = report.Succeeded
                ? report.Summary ?? ""
                : report.FirstFailure is { } failed ? Loc.Format("Conn_TestFailed", failed.Title) : report.Summary ?? "";
        }
        catch (OperationCanceledException)
        {
            // 参数改了或对话框关了:结果作废,什么都不显示。
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _testSucceeded = false;
            TestSummary = MongoConnector.Describe(ex);
        }
        finally
        {
            if (ReferenceEquals(_probe, cts))
            {
                _probe = null;
                IsProbing = false;
            }
            cts.Dispose();
            RaisePropertiesChanged(nameof(ShowStepsHint), nameof(HasMembers), nameof(TestPassed), nameof(TestFailed));
        }
    }

    private void Apply(ProbeStep step)
    {
        ProbeStepViewModel? row = Steps.FirstOrDefault(s => s.Key == step.Key);
        if (row is null)
        {
            Steps.Add(new ProbeStepViewModel(step));
            RaisePropertyChanged(nameof(ShowStepsHint));
        }
        else
        {
            row.Update(step);
        }
    }

    /// <summary>测试后给主机列表各行标上发现的角色。</summary>
    private void ApplyBadges(IReadOnlyList<ProbeEndpoint> endpoints)
    {
        foreach (HostRowViewModel row in Hosts)
        {
            ProbeEndpoint? match = endpoints.FirstOrDefault(e => string.Equals(e.Address, row.Address, StringComparison.OrdinalIgnoreCase));
            row.Badge = match?.Role;
            row.BadgeTone = match?.Tone ?? Tone.Neutral;
        }
    }

    private async Task SaveAsync(bool connect)
    {
        MongoProfile profile = ToProfile();
        if (Validate(profile) is { } problem)
        {
            Error = problem;
            return;
        }
        // 口令:填了就存;匿名就把存着的删掉;复制来的连接带上原来那条的;其余(编辑时留空)不动。
        string? password;
        if (Password.Length > 0)
        {
            password = Password;
        }
        else if (profile.Username.Length == 0)
        {
            password = "";
        }
        else if (_passwordFrom is not null)
        {
            password = await _shell.Profiles.GetPasswordAsync(_passwordFrom).ConfigureAwait(true);
        }
        else
        {
            password = null;
        }
        IsSaving = true;
        try
        {
            _probe?.Cancel();
            Close();
            await _shell.SaveProfileAsync(profile, password, connect).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 存不下:把对话框放回去,填的东西不丢。
            Error = Loc.Format("Conn_SaveFailed", MongoConnector.Describe(ex));
            _shell.ShowDialog(this);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <inheritdoc />
    internal override void OnClosed()
    {
        _probe?.Cancel();
        base.OnClosed();
    }

    /// <summary>对话框关了还在测就取消(<see cref="MongoWorkspaceViewModel.CloseDialog" /> 会调它)。</summary>
    public void Dispose() => _probe?.Cancel();

    /// <summary>进度回到界面线程。</summary>
    private sealed class UiProgress(Action<ProbeStep> apply) : IProgress<ProbeStep>
    {
        public void Report(ProbeStep value)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                apply(value);
            }
            else
            {
                Dispatcher.UIThread.Post(() => apply(value));
            }
        }
    }
}
