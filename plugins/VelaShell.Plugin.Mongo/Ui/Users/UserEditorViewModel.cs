using System.Globalization;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>机制下拉的一项。</summary>
/// <param name="Choice">选项。</param>
/// <param name="Label">显示文字。</param>
internal sealed record MechanismOption(MechanismChoice Choice, string Label);

/// <summary>
/// 右侧的用户编辑器(设计稿 18 右半):头部身份与密码年龄、内置角色矩阵、其余角色芯片、
/// 登录限制、有效权限预览、命令预览 + 放弃 / 保存。
/// <para>
/// 编辑器按用户各存一份草稿(由标签页持有):在 ops_writer 上勾了一半去看 ops_reader,
/// 回来时改动还在,列表里那一行一直挂着橙色「编辑中」—— 而不是一切换就弹"放弃修改?"。
/// 新建用户也是同一个编辑器的「新用户」状态,多出账号那一节(用户名、认证库、密码、机制)。
/// </para>
/// </summary>
internal sealed class UserEditorViewModel : ObservableObject, IDisposable
{
    private readonly UsersTabViewModel _owner;
    private MongoUser? _user;
    private string _newName = "";
    // 新用户默认建在 admin:那是认证库的惯例,应用连接串里 authSource=admin 也是驱动的默认。
    private string _newDb = "admin";
    private string _password = "";
    private bool _revealPassword;
    private bool _passwordGenerated;
    private MechanismOption _mechanism;
    private string _clientSource = "";
    private string _serverAddress = "";
    private string _originalClientSource = "";
    private string _originalServerAddress = "";
    private BsonArray? _originalRestrictions;
    private bool _restrictionsLoaded;
    private IReadOnlyList<AdminCommand> _commands = [];

    /// <summary>构造。</summary>
    /// <param name="owner">标签页。</param>
    /// <param name="user">要编辑的用户;<see langword="null" /> 即新建。</param>
    public UserEditorViewModel(UsersTabViewModel owner, MongoUser? user)
    {
        _owner = owner;
        _user = user;
        MechanismOptions =
        [
            new(MechanismChoice.Sha256, "SCRAM-SHA-256"),
            new(MechanismChoice.Both, "SCRAM-SHA-256 + SCRAM-SHA-1"),
            new(MechanismChoice.Sha1, "SCRAM-SHA-1")
        ];
        _mechanism = MechanismOptions[0];
        Roles = new(owner.Loc, user?.Roles ?? [], owner.MatrixDatabases);
        Roles.Changed += OnChanged;
        Effective = new(owner);
        if (user?.Restrictions is { } restrictions)
        {
            ApplyRestrictions(restrictions);
        }

        SaveCommand = new(() => _owner.SaveUserAsync(this), () => CanSave);
        DiscardCommand = new(Discard, () => IsModified);
        RotateCommand = new(() => _owner.RotatePasswordAsync(this), () => !IsNew);
        CopyCommandsCommand = new(() => _owner.Workspace.CopyAsync(string.Join(Environment.NewLine, _commands.Select(static c => c.Shell))));
        GenerateCommand = new(() =>
        {
            Password = UserAdmin.GeneratePassword();
            RevealPassword = true;
            _passwordGenerated = true;
        });
        AddRoleCommand = new(Roles.Add);
        Recompute();
    }

    /// <summary>标签页。</summary>
    public UsersTabViewModel Owner => _owner;

    /// <summary>文案表。</summary>
    public Loc Loc => _owner.Loc;

    /// <summary>正在编辑的用户(新建时为空)。</summary>
    public MongoUser? User => _user;

    /// <summary>新建状态。</summary>
    public bool IsNew => _user is null;

    /// <summary>是不是已有用户(新建那一节反着显示)。</summary>
    public bool IsExisting => _user is not null;

    /// <summary>草稿的键(<c>admin.ops_writer</c>;新建为空)。</summary>
    public string Id => _user?.Id ?? "";

    /// <summary>显示名。</summary>
    public string DisplayName => _user?.Name ?? (_newName.Length > 0 ? _newName : Loc["Users_NewUserTitle"]);

    /// <summary>root 级用户(红色盾牌头像)。</summary>
    public bool IsRoot => _user?.IsRoot == true;

    /// <summary>头像图标。</summary>
    public string AvatarIcon => IsNew ? "Mongo.user-plus" : IsRoot ? "Mongo.shield-alert" : "Mongo.user";

    /// <summary>
    /// 名字下面那行:<c>admin · SCRAM-SHA-256 · 创建于 2026-03-12</c>。
    /// 机制取自 usersInfo 的 mechanisms;创建时间服务器不记,只有 customData 里写了才显示。
    /// </summary>
    public string Subtitle
    {
        get
        {
            if (_user is not { } user)
            {
                return string.Join(" · ", _newDb, _mechanism.Label, Loc["Users_NotCreated"]);
            }
            var parts = new List<string> { user.Db };
            if (UserAdmin.PrimaryMechanism(user.Mechanisms) is { Length: > 0 } mechanism)
            {
                parts.Add(mechanism);
            }
            if (UserAdmin.CreatedAt(user.CustomData) is { } created)
            {
                parts.Add(Loc.Format("Users_CreatedOn", created.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }
            if (_owner.IsSelf(user))
            {
                parts.Add(Loc["Users_CurrentIdentity"]);
            }
            return string.Join(" · ", parts);
        }
    }

    // ── 密码年龄徽章 ─────────────────────────────────────────────────────

    private (int Days, int Policy, bool Expired)? Age =>
        _user is null ? null : UserAdmin.PasswordAge(_user.CustomData, DateTime.UtcNow);

    /// <summary>显示徽章(customData 有修改时间、且已接近或超过策略)。</summary>
    public bool HasPasswordAge => Age is not null;

    /// <summary>已超过策略(徽章转红)。</summary>
    public bool PasswordExpired => Age?.Expired == true;

    /// <summary>徽章文字(<c>密码 62 天未修改 · 策略 90 天</c>)。</summary>
    public string PasswordAgeText => Age is { } age ? Loc.Format("Users_PasswordAge", age.Days, age.Policy) : "";

    // ── 新建用户那一节 ─────────────────────────────────────────────────────

    /// <summary>用户名。</summary>
    public string NewName
    {
        get => _newName;
        set
        {
            if (SetProperty(ref _newName, value.Trim()))
            {
                RaisePropertyChanged(nameof(DisplayName));
                OnChanged();
            }
        }
    }

    /// <summary>认证库。</summary>
    public string NewDb
    {
        get => _newDb;
        set
        {
            if (SetProperty(ref _newDb, string.IsNullOrWhiteSpace(value) ? "admin" : value.Trim()))
            {
                RaisePropertyChanged(nameof(Subtitle));
                OnChanged();
            }
        }
    }

    /// <summary>认证库的候选(admin + 用户库)。</summary>
    public IReadOnlyList<string> AuthDatabases => _owner.AuthDatabases;

    /// <summary>密码。</summary>
    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                _passwordGenerated = false;
                OnChanged();
            }
        }
    }

    /// <summary>明文显示密码。</summary>
    public bool RevealPassword
    {
        get => _revealPassword;
        set
        {
            if (SetProperty(ref _revealPassword, value))
            {
                RaisePropertyChanged(nameof(PasswordChar));
            }
        }
    }

    /// <summary>密码框的掩码字符(明文时为 0)。</summary>
    public char PasswordChar => _revealPassword ? '\0' : '•';

    /// <summary>密码是刚生成的(建好之后顺手复制到剪贴板)。</summary>
    public bool PasswordGenerated => _passwordGenerated;

    /// <summary>机制选项。</summary>
    public IReadOnlyList<MechanismOption> MechanismOptions { get; }

    /// <summary>选中的机制。</summary>
    public MechanismOption Mechanism
    {
        get => _mechanism;
        set
        {
            if (value is not null && SetProperty(ref _mechanism, value))
            {
                RaisePropertyChanged(nameof(Subtitle));
                OnChanged();
            }
        }
    }

    // ── 角色 ──────────────────────────────────────────────────────────────

    /// <summary>矩阵 + 芯片。</summary>
    public RoleSetEditor Roles { get; }

    /// <summary>「添加角色」下拉里能加的(还没有的自定义角色 + 其余内置角色)。</summary>
    public IReadOnlyList<RoleRef> AddableRoles
    {
        get
        {
            var have = new HashSet<RoleRef>(Roles.Current);
            return
            [
                .. _owner.CustomRoles.Select(static r => r.Ref).Where(r => !have.Contains(r)),
                .. BuiltinRoles.Others.Select(static r => new RoleRef(r, "admin")).Where(r => !have.Contains(r))
            ];
        }
    }

    // ── 登录限制 ──────────────────────────────────────────────────────────

    /// <summary>clientSource(逗号分隔 CIDR)。</summary>
    public string ClientSource
    {
        get => _clientSource;
        set
        {
            if (SetProperty(ref _clientSource, value))
            {
                RaisePropertiesChanged(nameof(ClientSourceModified), nameof(ClientSourceInvalid));
                OnChanged();
            }
        }
    }

    /// <summary>serverAddress(逗号分隔 CIDR)。</summary>
    public string ServerAddress
    {
        get => _serverAddress;
        set
        {
            if (SetProperty(ref _serverAddress, value))
            {
                RaisePropertiesChanged(nameof(ServerAddressModified), nameof(ServerAddressInvalid));
                OnChanged();
            }
        }
    }

    /// <summary>clientSource 与现状不同(输入框橙色描边)。</summary>
    public bool ClientSourceModified => Normalize(_clientSource) != Normalize(_originalClientSource);

    /// <summary>serverAddress 与现状不同。</summary>
    public bool ServerAddressModified => Normalize(_serverAddress) != Normalize(_originalServerAddress);

    /// <summary>clientSource 有写错的地址(输入框标红)。</summary>
    public bool ClientSourceInvalid => UserAdmin.FirstInvalidAddress(_clientSource) is not null;

    /// <summary>serverAddress 有写错的地址。</summary>
    public bool ServerAddressInvalid => UserAdmin.FirstInvalidAddress(_serverAddress) is not null;

    /// <summary>服务器上还有更多份限制(界面只编辑第一份,其余原样保留)。</summary>
    public bool HasMoreRestrictions => _originalRestrictions is { Count: > 1 };

    /// <summary>那条说明。</summary>
    public string MoreRestrictionsText => Loc.Format("Users_MoreRestrictions", (_originalRestrictions?.Count ?? 1) - 1);

    private bool RestrictionsModified => ClientSourceModified || ServerAddressModified;

    private static string Normalize(string text) => UserAdmin.JoinList(UserAdmin.SplitList(text));

    /// <summary>
    /// 详情(登录限制只有精确的 usersInfo 才回)到了:更新现状。
    /// 用户在详情回来之前已经动了输入框的话,保留他输入的,只更新"现状"那一侧。
    /// </summary>
    internal void ApplyDetails(MongoUser detailed)
    {
        _user = detailed;
        ApplyRestrictions(detailed.Restrictions ?? []);
        RaisePropertiesChanged(nameof(Subtitle), nameof(HasPasswordAge), nameof(PasswordExpired), nameof(PasswordAgeText));
        Recompute();
    }

    private void ApplyRestrictions(BsonArray restrictions)
    {
        bool touched = _restrictionsLoaded ? RestrictionsModified : _clientSource.Length > 0 || _serverAddress.Length > 0;
        _originalRestrictions = restrictions;
        (_originalClientSource, _originalServerAddress) = UserAdmin.ReadRestriction(restrictions.Count > 0 ? restrictions[0] : null);
        _restrictionsLoaded = true;
        if (!touched)
        {
            _clientSource = _originalClientSource;
            _serverAddress = _originalServerAddress;
        }
        RaisePropertiesChanged(nameof(ClientSource), nameof(ServerAddress), nameof(ClientSourceModified),
            nameof(ServerAddressModified), nameof(ClientSourceInvalid), nameof(ServerAddressInvalid),
            nameof(HasMoreRestrictions), nameof(MoreRestrictionsText));
    }

    // ── 有效权限、命令、保存 ───────────────────────────────────────────────

    /// <summary>有效权限预览。</summary>
    public EffectivePreview Effective { get; }

    /// <summary>将执行的命令。</summary>
    public IReadOnlyList<AdminCommand> Commands => _commands;

    /// <summary>有没有要执行的命令。</summary>
    public bool HasCommands => _commands.Count > 0;

    /// <summary>底栏的命令预览(多条用 <c>;</c> 连成一行,完整的经复制按钮拿)。</summary>
    public string CommandPreview => _commands.Count == 0 ? Loc["Users_NoChanges"] : string.Join(";  ", _commands.Select(static c => c.Shell));

    /// <summary>有未保存的改动。</summary>
    public bool IsModified =>
        IsNew
            ? _newName.Length > 0 || _password.Length > 0 || Roles.Current.Count > 0 || _clientSource.Length > 0 || _serverAddress.Length > 0
            : Roles.IsModified || RestrictionsModified;

    /// <summary>为什么不能保存(给保存按钮的悬停提示);能保存为空。</summary>
    public string ValidationError
    {
        get
        {
            if (UserAdmin.FirstInvalidAddress(_clientSource) is { } badClient)
            {
                return Loc.Format("Users_BadAddress", badClient);
            }
            if (UserAdmin.FirstInvalidAddress(_serverAddress) is { } badServer)
            {
                return Loc.Format("Users_BadAddress", badServer);
            }
            if (!IsNew)
            {
                return "";
            }
            if (_newName.Length == 0)
            {
                return Loc["Users_NeedName"];
            }
            if (_owner.Users.Any(u => u.Name == _newName && u.Db == _newDb))
            {
                return Loc.Format("Users_NameTaken", $"{_newName}@{_newDb}");
            }
            return _password.Length == 0 ? Loc["Users_NeedPassword"] : "";
        }
    }

    /// <summary>能不能保存。</summary>
    public bool CanSave => IsModified && ValidationError.Length == 0 && (IsNew || HasCommands);

    /// <summary>有改动但保存不了时,在底栏按钮旁用红字说明原因。</summary>
    public bool ShowValidation => IsModified && ValidationError.Length > 0;

    /// <summary>保存。</summary>
    public AsyncCommand SaveCommand { get; }

    /// <summary>放弃。</summary>
    public RelayCommand DiscardCommand { get; }

    /// <summary>轮换密码。</summary>
    public AsyncCommand RotateCommand { get; }

    /// <summary>复制命令预览。</summary>
    public AsyncCommand CopyCommandsCommand { get; }

    /// <summary>生成密码(新建状态)。</summary>
    public RelayCommand GenerateCommand { get; }

    /// <summary>加一个角色(下拉选中时)。</summary>
    public RelayCommand<RoleRef> AddRoleCommand { get; }

    /// <summary>草稿状态变了(列表行的「编辑中」、标签的修改圆点、状态栏)。</summary>
    public event Action<UserEditorViewModel>? Changed;

    /// <summary>按当前状态拼出要执行的命令。</summary>
    public IReadOnlyList<AdminCommand> BuildCommands()
    {
        if (_user is not { } user)
        {
            if (_newName.Length == 0)
            {
                return [];
            }
            DateTime now = DateTime.UtcNow;
            return
            [
                UserAdmin.CreateUser(_newName, _newDb, _password.Length > 0 ? _password : "", Roles.Current, _mechanism.Choice,
                    UserAdmin.BuildRestrictions(_clientSource, _serverAddress, null),
                    UserAdmin.MergeCustomData([], new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc), created: true))
            ];
        }
        var commands = new List<AdminCommand>();
        (IReadOnlyList<RoleRef> added, IReadOnlyList<RoleRef> removed) = UserAdmin.Diff(Roles.Original, Roles.Current);
        if (added.Count > 0)
        {
            commands.Add(UserAdmin.GrantRolesToUser(user.Name, user.Db, added));
        }
        if (removed.Count > 0)
        {
            commands.Add(UserAdmin.RevokeRolesFromUser(user.Name, user.Db, removed));
        }
        if (RestrictionsModified)
        {
            commands.Add(UserAdmin.UpdateRestrictions(user.Name, user.Db,
                UserAdmin.BuildRestrictions(_clientSource, _serverAddress, _originalRestrictions)));
        }
        return commands;
    }

    /// <summary>保存成功之后:以服务器的新状态为现状,草稿清空。</summary>
    internal void Rebase(MongoUser saved)
    {
        // forAllDBs 的列表里没有登录限制:拿列表那份重建时沿用手里的(随后详情会再刷新一次),
        // 否则输入框会先闪成空、再跳回原值。
        BsonArray? restrictions = saved.Restrictions ?? _user?.Restrictions;
        _user = saved with { Restrictions = restrictions };
        Roles.Rebase(saved.Roles);
        _clientSource = "";
        _serverAddress = "";
        _restrictionsLoaded = false;
        ApplyRestrictions(restrictions ?? []);
        RaisePropertiesChanged(nameof(Subtitle), nameof(DisplayName), nameof(HasPasswordAge), nameof(PasswordExpired), nameof(PasswordAgeText));
        Recompute();
    }

    private void Discard()
    {
        if (IsNew)
        {
            _newName = "";
            _password = "";
            _revealPassword = false;
            RaisePropertiesChanged(nameof(NewName), nameof(Password), nameof(RevealPassword), nameof(PasswordChar), nameof(DisplayName));
        }
        Roles.Reset();
        _clientSource = _originalClientSource;
        _serverAddress = _originalServerAddress;
        RaisePropertiesChanged(nameof(ClientSource), nameof(ServerAddress), nameof(ClientSourceModified), nameof(ServerAddressModified),
            nameof(ClientSourceInvalid), nameof(ServerAddressInvalid));
        Recompute();
    }

    private void OnChanged() => Recompute();

    private void Recompute()
    {
        _commands = BuildCommands();
        RaisePropertiesChanged(nameof(Commands), nameof(HasCommands), nameof(CommandPreview), nameof(IsModified),
            nameof(ValidationError), nameof(ShowValidation), nameof(CanSave), nameof(AddableRoles));
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        Effective.Request(Roles.Original, [], Roles.Current, []);
        Changed?.Invoke(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Roles.Changed -= OnChanged;
        Effective.Dispose();
    }
}
