using System.Collections.ObjectModel;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 用户与角色(设计稿 18):36px 工具行 + 左栏 420px 用户 / 角色列表 + 右侧编辑器。
/// <para>
/// 全部读写走服务器的用户管理命令(usersInfo / rolesInfo / createUser / grantRolesToUser …),
/// 不直接碰 <c>admin.system.users</c>:直接改那张表绕过了服务器的校验与用户缓存失效,
/// 改完要等缓存过期才生效,而且在分片集群上根本不是同一份。
/// </para>
/// <para>
/// 草稿按用户 / 角色各存一份:可以在几个用户之间来回比对、各改一点,列表行一直标着「编辑中」,
/// 状态栏列出所有未保存的对象;保存只提交当前那一个。
/// </para>
/// </summary>
internal sealed class UsersTabViewModel : WorkspaceTab
{
    private readonly Dictionary<string, UserEditorViewModel> _userDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoleEditorViewModel> _roleDrafts = new(StringComparer.Ordinal);
    private readonly Dictionary<RoleRef, IReadOnlyList<Privilege>> _privilegeCache = [];
    private bool _showRoles;
    private bool _isLoading;
    private bool _loaded;
    private string _notice = "";
    private UserRowViewModel? _selectedUser;
    private RoleRowViewModel? _selectedRole;
    private UserEditorViewModel? _userEditor;
    private RoleEditorViewModel? _roleEditor;
    private UserEditorViewModel? _newUser;
    private RoleEditorViewModel? _newRole;
    private IReadOnlyList<(string User, string Db)> _authenticated = [];
    private IReadOnlyList<string> _databases = [];
    private IReadOnlyList<MongoRole> _customRoles = [];

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="database">打开时的作用库(矩阵与标签里优先显示它)。</param>
    /// <param name="roles">直接进角色模式。</param>
    public UsersTabViewModel(IMongoWorkspace workspace, string? database, bool roles)
        : base(workspace)
    {
        Database = database;
        _showRoles = roles;
        Title = workspace.Loc["Tree_Users"];
        NewUserCommand = new(StartNewUser);
        NewRoleCommand = new(StartNewRole);
        ResetPasswordCommand = new(ResetPassword, () => !_showRoles && _userEditor is { IsNew: false });
        DeleteCommand = new(DeleteAsync, () => _showRoles ? _selectedRole is not null : _selectedUser is not null);
    }

    /// <summary>打开时的作用库。</summary>
    public string? Database { get; }

    /// <summary>角色模式(工具行右侧分段「用户 / 角色」)。</summary>
    public bool ShowRoles
    {
        get => _showRoles;
        set
        {
            if (!SetProperty(ref _showRoles, value))
            {
                return;
            }
            (Workspace as MongoSession)?.Shell.OnTabToolChanged();
            OnModeChanged();
        }
    }

    /// <summary>分段「用户」。</summary>
    public bool IsUsersMode
    {
        get => !_showRoles;
        set
        {
            if (value)
            {
                ShowRoles = false;
            }
        }
    }

    /// <summary>分段「角色」。</summary>
    public bool IsRolesMode
    {
        get => _showRoles;
        set
        {
            if (value)
            {
                ShowRoles = true;
            }
        }
    }

    /// <inheritdoc />
    public override TabKind Kind => TabKind.Users;

    /// <inheritdoc />
    public override string Key => "users";

    /// <inheritdoc />
    public override string IconKey => "Mongo.users";

    /// <inheritdoc />
    public override string IconToken => "VelaInfo";

    // ── 列表 ─────────────────────────────────────────────────────────────

    /// <summary>用户行。</summary>
    public ObservableCollection<UserRowViewModel> Users { get; } = [];

    /// <summary>自定义角色行。</summary>
    public ObservableCollection<RoleRowViewModel> Roles { get; } = [];

    /// <summary>全部自定义角色(各库)。</summary>
    public IReadOnlyList<MongoRole> CustomRoles => _customRoles;

    /// <summary>用户库(矩阵的行;不含 admin / config / local)。</summary>
    public IReadOnlyList<string> MatrixDatabases => _databases;

    /// <summary>用户库(<see cref="MatrixDatabases" /> 的别名,给角色编辑器判断"是不是现有库")。</summary>
    public IReadOnlyList<string> Databases => _databases;

    /// <summary>认证库 / 角色所在库的候选:admin 在前,其后是用户库。</summary>
    public IReadOnlyList<string> AuthDatabases => ["admin", .. _databases];

    /// <summary>正在加载。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaisePropertiesChanged(nameof(IsListEmpty), nameof(EmptyText));
            }
        }
    }

    /// <summary>列表顶上的一条说明(没有 forAllDBs 权限时只列了当前库)。</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                RaisePropertyChanged(nameof(HasNotice));
            }
        }
    }

    /// <summary>有没有那条说明。</summary>
    public bool HasNotice => _notice.Length > 0;

    /// <summary>当前模式的列表是空的(加载完之后)。</summary>
    public bool IsListEmpty => !_isLoading && _loaded && (_showRoles ? Roles.Count == 0 : Users.Count == 0);

    /// <summary>空态文字。</summary>
    public string EmptyText => _isLoading ? Loc["Common_Loading"] : _showRoles ? Loc["Users_NoRoles"] : Loc["Users_NoUsers"];

    /// <summary>列头第一列(用户 / 角色)。</summary>
    public string NameHeader => _showRoles ? Loc["Users_ColRole"] : Loc["Users_ColUser"];

    /// <summary>列头第二列(认证库 / 所在库)。</summary>
    public string DbHeader => _showRoles ? Loc["Users_ColRoleDb"] : Loc["Users_ColAuthDb"];

    /// <summary>列头第三列(角色 / 继承)。</summary>
    public string RolesHeader => _showRoles ? Loc["Users_ColInherits"] : Loc["Users_ColRoles"];

    /// <summary>右侧没有编辑器时的那句话。</summary>
    public string EmptyEditorText => _showRoles ? Loc["Users_EmptyRoleEditor"] : Loc["Users_EmptyEditor"];

    /// <summary>工具行「删除」的图标(用户模式 user-x,角色模式 trash-2)。</summary>
    public string DeleteIcon => _showRoles ? "Mongo.trash-2" : "Mongo.user-x";

    /// <summary>选中的用户行。</summary>
    public UserRowViewModel? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (!SetProperty(ref _selectedUser, value))
            {
                return;
            }
            if (value is not null)
            {
                UserEditor = DraftFor(value.User);
            }
            RefreshCommands();
        }
    }

    /// <summary>选中的角色行。</summary>
    public RoleRowViewModel? SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (!SetProperty(ref _selectedRole, value))
            {
                return;
            }
            if (value is not null)
            {
                RoleEditor = DraftFor(value.Role);
            }
            RefreshCommands();
        }
    }

    /// <summary>右侧的用户编辑器。</summary>
    public UserEditorViewModel? UserEditor
    {
        get => _userEditor;
        private set
        {
            if (SetProperty(ref _userEditor, value))
            {
                RaisePropertiesChanged(nameof(ShowUserEditor), nameof(ShowEmptyEditor));
                RefreshCommands();
            }
        }
    }

    /// <summary>右侧的角色编辑器。</summary>
    public RoleEditorViewModel? RoleEditor
    {
        get => _roleEditor;
        private set
        {
            if (SetProperty(ref _roleEditor, value))
            {
                RaisePropertiesChanged(nameof(ShowRoleEditor), nameof(ShowEmptyEditor));
                RefreshCommands();
            }
        }
    }

    /// <summary>右侧显示用户编辑器。</summary>
    public bool ShowUserEditor => !_showRoles && _userEditor is not null;

    /// <summary>右侧显示角色编辑器。</summary>
    public bool ShowRoleEditor => _showRoles && _roleEditor is not null;

    /// <summary>右侧什么也没选(空态)。</summary>
    public bool ShowEmptyEditor => !ShowUserEditor && !ShowRoleEditor;

    // ── 工具行命令 ───────────────────────────────────────────────────────

    /// <summary>新建用户。</summary>
    public RelayCommand NewUserCommand { get; }

    /// <summary>新建角色。</summary>
    public RelayCommand NewRoleCommand { get; }

    /// <summary>重置密码。</summary>
    public RelayCommand ResetPasswordCommand { get; }

    /// <summary>删除(用户模式删用户,角色模式删角色)。</summary>
    public AsyncCommand DeleteCommand { get; }

    // ── 加载 ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override Task LoadAsync() => ReloadAsync(keepDrafts: false);

    /// <inheritdoc />
    public override async Task RefreshAsync()
    {
        if (IsModified && !await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Users_RefreshDiscardTitle"],
                Message = Loc["Users_RefreshDiscardBody"],
                ConfirmLabel = Loc["Tab_CloseDiscard"],
                IconKey = "Mongo.triangle-alert",
                Danger = true
            }).ConfigureAwait(true))
        {
            return;
        }
        await ReloadAsync(keepDrafts: false).ConfigureAwait(true);
    }

    /// <summary>
    /// 重新读身份、库、用户与自定义角色。
    /// <paramref name="keepDrafts" /> 为真时(保存之后)保留其余对象上未保存的草稿;否则全部丢弃。
    /// </summary>
    internal async Task ReloadAsync(bool keepDrafts, string? selectUserId = null, string? selectRoleId = null)
    {
        IsLoading = true;
        string? userId = selectUserId ?? _selectedUser?.User.Id;
        string? roleId = selectRoleId ?? (_selectedRole is { } r ? $"{r.Db}.{r.Name}" : null);
        try
        {
            _authenticated = await LoadIdentityAsync().ConfigureAwait(true);
            _databases = await LoadDatabasesAsync().ConfigureAwait(true);
            (IReadOnlyList<MongoUser> users, string notice) = await LoadUsersAsync().ConfigureAwait(true);
            _customRoles = await LoadCustomRolesAsync(users).ConfigureAwait(true);
            _privilegeCache.Clear();
            Notice = notice;

            if (!keepDrafts)
            {
                DropDrafts();
            }
            _selectedUser = null;
            _selectedRole = null;
            Users.Clear();
            foreach (MongoUser user in users
                         .OrderBy(static u => u.IsRoot ? 0 : 1)
                         .ThenBy(static u => u.Db == "admin" ? 0 : 1)
                         .ThenBy(static u => u.Db, StringComparer.Ordinal)
                         .ThenBy(static u => u.Name, StringComparer.Ordinal))
            {
                var row = new UserRowViewModel(user, Loc, Database, UserAdmin.IsSelf(user.Name, user.Db, _authenticated));
                if (_userDrafts.TryGetValue(user.Id, out UserEditorViewModel? draft))
                {
                    // 没改动的草稿以服务器的新状态为准;改了一半的保留(那是人的工作)。
                    if (!draft.IsModified)
                    {
                        draft.Rebase(user);
                        _ = LoadDetailsAsync(draft);
                    }
                    row.IsEditing = draft.IsModified;
                }
                Users.Add(row);
            }
            foreach (string stale in _userDrafts.Keys.Where(id => users.All(u => u.Id != id)).ToList())
            {
                _userDrafts[stale].Dispose();
                _userDrafts.Remove(stale);
            }
            Roles.Clear();
            foreach (MongoRole role in _customRoles)
            {
                var row = new RoleRowViewModel(role, Loc, Database, HoldersOf(role.Ref).Count);
                if (_roleDrafts.TryGetValue(RoleId(role), out RoleEditorViewModel? draft))
                {
                    if (!draft.IsModified)
                    {
                        draft.Rebase(role);
                    }
                    row.IsEditing = draft.IsModified;
                }
                Roles.Add(row);
            }
            foreach (string stale in _roleDrafts.Keys.Where(id => _customRoles.All(c => RoleId(c) != id)).ToList())
            {
                _roleDrafts[stale].Dispose();
                _roleDrafts.Remove(stale);
            }
            _loaded = true;

            RaisePropertiesChanged(nameof(SelectedUser), nameof(SelectedRole), nameof(CustomRoles), nameof(MatrixDatabases),
                nameof(AuthDatabases), nameof(IsListEmpty));
            bool editingNewUser = _userEditor is not null && ReferenceEquals(_userEditor, _newUser);
            bool editingNewRole = _roleEditor is not null && ReferenceEquals(_roleEditor, _newRole);
            SelectedUser = Users.FirstOrDefault(u => u.User.Id == userId) ?? (editingNewUser ? null : Users.FirstOrDefault());
            if (_selectedUser is null && !editingNewUser)
            {
                UserEditor = null;
            }
            SelectedRole = Roles.FirstOrDefault(x => $"{x.Db}.{x.Name}" == roleId) ?? (editingNewRole ? null : Roles.FirstOrDefault());
            if (_selectedRole is null && !editingNewRole)
            {
                RoleEditor = null;
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Notice = Loc.Format("Users_LoadFailed", MongoConnector.Describe(ex));
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            IsLoading = false;
            UpdateModified();
        }
    }

    /// <summary>当前连接是以谁的身份认证的(删自己、撤自己的权限要拦)。</summary>
    private async Task<IReadOnlyList<(string User, string Db)>> LoadIdentityAsync()
    {
        try
        {
            BsonDocument status = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument("connectionStatus", 1)).ConfigureAwait(true);
            BsonArray users = status.GetValue("authInfo", new BsonDocument()).AsBsonDocument
                .GetValue("authenticatedUsers", new BsonArray()).AsBsonArray;
            return
            [
                .. users.OfType<BsonDocument>()
                    .Where(static u => u.Contains("user") && u.Contains("db"))
                    .Select(static u => (u["user"].AsString, u["db"].AsString))
            ];
        }
        catch (MongoCommandException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> LoadDatabasesAsync()
    {
        IEnumerable<string> names;
        try
        {
            IReadOnlyList<DatabaseInfo> databases = await Workspace.Connection.ListDatabasesAsync().ConfigureAwait(true);
            names = databases.Select(static d => d.Name);
        }
        catch (MongoCommandException)
        {
            // 没有 listDatabases 权限:退到对象树里已知的库。
            names = Workspace.Databases;
        }
        // 标签页的作用库排第一(从 shop 打开就先看到 shop 那一行),其余按名字。
        return
        [
            .. names.Where(static n => n is not ("admin" or "config" or "local"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n == Database ? 0 : 1)
                .ThenBy(static n => n, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// <c>usersInfo: { forAllDBs: true }</c> 列出全部库的用户;没有这个权限(只是某个库的 userAdmin)
    /// 时退到当前库,并在列表顶上说一句"只列了 shop 的用户" —— 否则空列表会被误读成"这台服务器没有用户"。
    /// </summary>
    private async Task<(IReadOnlyList<MongoUser> Users, string Notice)> LoadUsersAsync()
    {
        try
        {
            BsonDocument reply = await Workspace.Connection.RunCommandAsync("admin",
                new BsonDocument("usersInfo", new BsonDocument("forAllDBs", true))).ConfigureAwait(true);
            return (ParseUsers(reply), "");
        }
        catch (MongoCommandException ex) when (ex.Code == 13)
        {
            string db = Database is { Length: > 0 } d ? d : "admin";
            BsonDocument reply = await Workspace.Connection.RunCommandAsync(db, new BsonDocument("usersInfo", 1)).ConfigureAwait(true);
            return (ParseUsers(reply), Loc.Format("Users_ScopedNotice", db));
        }
    }

    private static List<MongoUser> ParseUsers(BsonDocument reply) =>
        [.. reply.GetValue("users", new BsonArray()).AsBsonArray.OfType<BsonDocument>().Select(MongoUser.Parse)];

    /// <summary>
    /// 自定义角色分散在各库(rolesInfo 只看当前库),逐库并行地问:admin + 用户库 + 用户角色里提到的库。
    /// 某个库没有 viewRole 权限就跳过它,而不是让整页加载失败。
    /// </summary>
    private async Task<IReadOnlyList<MongoRole>> LoadCustomRolesAsync(IReadOnlyList<MongoUser> users)
    {
        var databases = new SortedSet<string>(StringComparer.Ordinal) { "admin" };
        databases.UnionWith(_databases);
        databases.UnionWith(users.SelectMany(static u => u.Roles).Where(static r => !BuiltinRoles.IsBuiltin(r.Role)).Select(static r => r.Db));
        IEnumerable<Task<IReadOnlyList<MongoRole>>> tasks = databases.Select(async db =>
        {
            try
            {
                BsonDocument reply = await Workspace.Connection.RunCommandAsync(db, new BsonDocument
                {
                    { "rolesInfo", 1 },
                    { "showPrivileges", true },
                    { "showBuiltinRoles", false }
                }).ConfigureAwait(false);
                return (IReadOnlyList<MongoRole>)
                [
                    .. reply.GetValue("roles", new BsonArray()).AsBsonArray.OfType<BsonDocument>()
                        .Select(MongoRole.Parse).Where(static r => !r.IsBuiltin)
                ];
            }
            catch (MongoCommandException)
            {
                return [];
            }
        });
        IReadOnlyList<MongoRole>[] all = await Task.WhenAll(tasks).ConfigureAwait(true);
        return [.. all.SelectMany(static x => x).OrderBy(static r => r.Db, StringComparer.Ordinal).ThenBy(static r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 一个用户的详情:登录限制只有精确的 usersInfo(指名用户)才回 —— forAllDBs 的列表里没有。
    /// </summary>
    internal async Task<MongoUser?> FetchUserAsync(string user, string db)
    {
        BsonDocument reply = await Workspace.Connection.RunCommandAsync(db, new BsonDocument
        {
            { "usersInfo", new BsonDocument { { "user", user }, { "db", db } } },
            { "showCustomData", true },
            { "showAuthenticationRestrictions", true }
        }).ConfigureAwait(true);
        return ParseUsers(reply).FirstOrDefault();
    }

    private async Task<MongoRole?> FetchRoleAsync(string role, string db)
    {
        BsonDocument reply = await Workspace.Connection.RunCommandAsync(db, new BsonDocument
        {
            { "rolesInfo", new BsonDocument { { "role", role }, { "db", db } } },
            { "showPrivileges", true }
        }).ConfigureAwait(true);
        return reply.GetValue("roles", new BsonArray()).AsBsonArray.OfType<BsonDocument>().Select(MongoRole.Parse).FirstOrDefault();
    }

    private async Task LoadDetailsAsync(UserEditorViewModel editor)
    {
        if (editor.User is not { } user)
        {
            return;
        }
        try
        {
            if (await FetchUserAsync(user.Name, user.Db).ConfigureAwait(true) is { } detailed)
            {
                editor.ApplyDetails(detailed);
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"usersInfo for {user.Id} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 把一组角色展开成权限(rolesInfo showPrivileges 的 inheritedPrivileges,已含继承链)。
    /// 结果按角色缓存到下一次整页重载 —— 内置角色的权限在一台服务器上是不变的。
    /// </summary>
    internal async Task<IReadOnlyDictionary<RoleRef, IReadOnlyList<Privilege>>> ResolvePrivilegesAsync(
        IEnumerable<RoleRef> roles,
        CancellationToken cancellationToken)
    {
        List<RoleRef> distinct = [.. roles.Distinct()];
        List<RoleRef> missing = [.. distinct.Where(r => !_privilegeCache.ContainsKey(r))];
        if (missing.Count > 0)
        {
            BsonDocument reply = await Workspace.Connection.RunCommandAsync("admin", new BsonDocument
            {
                { "rolesInfo", new BsonArray(missing.Select(static r => r.ToBson())) },
                { "showPrivileges", true }
            }, cancellationToken).ConfigureAwait(true);
            foreach (BsonDocument doc in reply.GetValue("roles", new BsonArray()).AsBsonArray.OfType<BsonDocument>())
            {
                var key = new RoleRef(doc.GetValue("role", "").AsString, doc.GetValue("db", "").AsString);
                _privilegeCache[key] = doc.TryGetValue("inheritedPrivileges", out BsonValue p) && p.IsBsonArray
                    ? [.. p.AsBsonArray.Select(Privilege.From).Where(static x => x is not null).Select(static x => x!)]
                    : [];
            }
            foreach (RoleRef unknown in missing.Where(r => !_privilegeCache.ContainsKey(r)))
            {
                // 服务器不认识的角色(拼错了 / 刚被删)不带来任何权限。
                _privilegeCache[unknown] = [];
            }
        }
        return distinct.ToDictionary(static r => r, r => _privilegeCache[r]);
    }

    // ── 草稿 ─────────────────────────────────────────────────────────────

    private UserEditorViewModel DraftFor(MongoUser user)
    {
        if (_userDrafts.TryGetValue(user.Id, out UserEditorViewModel? draft))
        {
            return draft;
        }
        draft = new UserEditorViewModel(this, user);
        draft.Changed += OnUserDraftChanged;
        _userDrafts[user.Id] = draft;
        _ = LoadDetailsAsync(draft);
        return draft;
    }

    private RoleEditorViewModel DraftFor(MongoRole role)
    {
        string id = RoleId(role);
        if (_roleDrafts.TryGetValue(id, out RoleEditorViewModel? draft))
        {
            return draft;
        }
        draft = new RoleEditorViewModel(this, role);
        draft.Changed += OnRoleDraftChanged;
        _roleDrafts[id] = draft;
        return draft;
    }

    private static string RoleId(MongoRole role) => $"{role.Db}.{role.Name}";

    private void OnUserDraftChanged(UserEditorViewModel editor)
    {
        if (Users.FirstOrDefault(u => u.User.Id == editor.Id) is { } row)
        {
            row.IsEditing = editor.IsModified;
        }
        UpdateModified();
    }

    private void OnRoleDraftChanged(RoleEditorViewModel editor)
    {
        if (Roles.FirstOrDefault(r => $"{r.Db}.{r.Name}" == editor.Id) is { } row)
        {
            row.IsEditing = editor.IsModified;
        }
        UpdateModified();
    }

    private void DropDrafts()
    {
        foreach (UserEditorViewModel draft in _userDrafts.Values)
        {
            draft.Changed -= OnUserDraftChanged;
            draft.Dispose();
        }
        foreach (RoleEditorViewModel draft in _roleDrafts.Values)
        {
            draft.Changed -= OnRoleDraftChanged;
            draft.Dispose();
        }
        _userDrafts.Clear();
        _roleDrafts.Clear();
        UserEditor = ReferenceEquals(_userEditor, _newUser) ? _newUser : null;
        RoleEditor = ReferenceEquals(_roleEditor, _newRole) ? _newRole : null;
    }

    /// <summary>所有未保存的对象名(状态栏「未保存:ops_writer」)。</summary>
    private IReadOnlyList<string> PendingNames =>
    [
        .. _userDrafts.Values.Where(static d => d.IsModified).Select(static d => d.DisplayName),
        .. _newUser is { IsModified: true } nu ? [nu.DisplayName] : Array.Empty<string>(),
        .. _roleDrafts.Values.Where(static d => d.IsModified).Select(static d => d.DisplayName),
        .. _newRole is { IsModified: true } nr ? [nr.DisplayName] : Array.Empty<string>()
    ];

    private void UpdateModified()
    {
        IsModified = PendingNames.Count > 0;
        UpdateStatus();
    }

    /// <summary>
    /// 状态栏:<c>admin.system.users · 5 个用户 · 2 个自定义角色</c>,有草稿时再接 <c>未保存:ops_writer</c>。
    /// </summary>
    private void UpdateStatus()
    {
        string ns = _showRoles ? "admin.system.roles" : "admin.system.users";
        string text = Loc.Format("Users_Status", ns, Users.Count, _customRoles.Count);
        IReadOnlyList<string> pending = PendingNames;
        if (pending.Count > 0)
        {
            text += "  ·  " + Loc.Format("Users_StatusPending", string.Join(", ", pending));
        }
        StatusText = text;
    }

    private void OnModeChanged()
    {
        RaisePropertiesChanged(nameof(IsUsersMode), nameof(IsRolesMode), nameof(NameHeader), nameof(DbHeader), nameof(RolesHeader), nameof(DeleteIcon), nameof(EmptyEditorText),
            nameof(ShowUserEditor), nameof(ShowRoleEditor), nameof(ShowEmptyEditor), nameof(IsListEmpty), nameof(EmptyText));
        if (_loaded)
        {
            if (_showRoles && _roleEditor is null && Roles.Count > 0)
            {
                SelectedRole = Roles[0];
            }
            else if (!_showRoles && _userEditor is null && Users.Count > 0)
            {
                SelectedUser = Users[0];
            }
        }
        RefreshCommands();
        UpdateStatus();
    }

    private void RefreshCommands()
    {
        ResetPasswordCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }

    // ── 查询助手(编辑器用) ─────────────────────────────────────────────

    /// <summary>这个用户是不是当前连接的身份。</summary>
    internal bool IsSelf(MongoUser user) => UserAdmin.IsSelf(user.Name, user.Db, _authenticated);

    /// <summary>直接持有某个角色的用户(<c>ops_writer@admin</c>)。</summary>
    internal IReadOnlyList<string> HoldersOf(RoleRef role) =>
        [.. Users.Where(u => u.User.Roles.Contains(role)).Select(static u => $"{u.Name}@{u.Db}")];

    // ── 新建 ─────────────────────────────────────────────────────────────

    private void StartNewUser()
    {
        if (_newUser is null)
        {
            _newUser = new UserEditorViewModel(this, null);
            _newUser.Changed += OnNewUserChanged;
        }
        ShowRoles = false;
        SelectedUser = null;
        UserEditor = _newUser;
    }

    private void StartNewRole()
    {
        if (_newRole is null)
        {
            _newRole = new RoleEditorViewModel(this, null);
            _newRole.Changed += OnNewRoleChanged;
        }
        ShowRoles = true;
        SelectedRole = null;
        RoleEditor = _newRole;
    }

    private void OnNewUserChanged(UserEditorViewModel editor) => UpdateModified();

    private void OnNewRoleChanged(RoleEditorViewModel editor) => UpdateModified();

    // ── 写:保存 / 删除 / 密码 ─────────────────────────────────────────────

    /// <summary>
    /// 依次执行一组命令;中途失败就停在那里并提示是哪一条(前面成功的已经生效 —— 用户管理命令没有事务,
    /// 所以保存后无论成败都会重读一次,让界面与服务器对齐)。
    /// </summary>
    private async Task<bool> ExecuteAsync(IReadOnlyList<AdminCommand> commands)
    {
        foreach (AdminCommand command in commands)
        {
            try
            {
                await Workspace.Connection.RunCommandAsync(command.Database, command.Command).ConfigureAwait(true);
                Workspace.Log.Info($"users: {command.Shell}");
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                Workspace.Toast(new()
                {
                    Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)),
                    Detail = command.Command.GetElement(0).Name,
                    Kind = ToastKind.Error,
                    Duration = TimeSpan.FromSeconds(8)
                });
                return false;
            }
        }
        return true;
    }

    /// <summary>写前的统一关口:只读 / 无权限拦下;生产连接(写前确认)再弹一次、手打名称。</summary>
    private async Task<bool> GuardAsync(string name, IReadOnlyList<AdminCommand> commands, string titleKey)
    {
        if (!Workspace.EnsureWritable("admin"))
        {
            return false;
        }
        if (!Workspace.Guard.ConfirmWrites && !Workspace.Guard.IsProduction)
        {
            return true;
        }
        return await Workspace.ConfirmAsync(new()
        {
            Title = Loc.Format(titleKey, name),
            Message = Loc.Format("Users_ConfirmCommands", commands.Count) + Environment.NewLine + Environment.NewLine
                      + string.Join(Environment.NewLine, commands.Select(static c => c.Shell)),
            ConfirmLabel = Loc["Common_Save"],
            IconKey = "Mongo.shield-check",
            Danger = false,
            TypeToConfirm = name
        }).ConfigureAwait(true);
    }

    /// <summary>保存一个用户草稿(新建即 createUser)。</summary>
    internal async Task SaveUserAsync(UserEditorViewModel editor)
    {
        IReadOnlyList<AdminCommand> commands = editor.BuildCommands();
        if (!editor.CanSave || commands.Count == 0)
        {
            return;
        }
        if (editor.User is { } self && IsSelf(self) && UserAdmin.Diff(editor.Roles.Original, editor.Roles.Current).Removed.Count > 0
            && !await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Users_RevokeSelfTitle"],
                Message = Loc.Format("Users_RevokeSelfBody", $"{self.Name}@{self.Db}"),
                ConfirmLabel = Loc["Users_RevokeSelfConfirm"],
                IconKey = "Mongo.shield-alert",
                Danger = true
            }).ConfigureAwait(true))
        {
            return;
        }
        if (!await GuardAsync(editor.DisplayName, commands, "Users_SaveUserTitle").ConfigureAwait(true))
        {
            return;
        }
        bool ok = await ExecuteAsync(commands).ConfigureAwait(true);
        if (editor.IsNew)
        {
            if (!ok)
            {
                return;
            }
            string id = $"{editor.NewDb}.{editor.NewName}";
            string label = $"{editor.NewName}@{editor.NewDb}";
            string password = editor.Password;
            bool generated = editor.PasswordGenerated;
            _newUser!.Changed -= OnNewUserChanged;
            _newUser.Dispose();
            _newUser = null;
            UserEditor = null;
            if (generated)
            {
                await Workspace.CopyAsync(password).ConfigureAwait(true);
            }
            Workspace.Toast(new()
            {
                Title = Loc.Format("Users_Created", label),
                Detail = generated ? Loc["Users_PasswordCopied"] : null,
                Kind = ToastKind.Success
            });
            await ReloadAsync(keepDrafts: true, selectUserId: id).ConfigureAwait(true);
            return;
        }
        if (ok)
        {
            Workspace.Toast(new()
            {
                Title = Loc.Format("Users_Saved", editor.DisplayName),
                Detail = Loc.Format("Users_SavedDetail", commands.Count),
                Kind = ToastKind.Success
            });
        }
        await RefreshUserAsync(editor).ConfigureAwait(true);
    }

    /// <summary>保存之后:以服务器状态为准重建草稿,再重读列表(行上的角色标签跟着变)。</summary>
    private async Task RefreshUserAsync(UserEditorViewModel editor)
    {
        if (editor.User is not { } user)
        {
            return;
        }
        try
        {
            if (await FetchUserAsync(user.Name, user.Db).ConfigureAwait(true) is { } fresh)
            {
                editor.Rebase(fresh);
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"usersInfo for {user.Id} failed: {ex.Message}");
        }
        await ReloadAsync(keepDrafts: true, selectUserId: user.Id).ConfigureAwait(true);
    }

    /// <summary>保存一个角色草稿(新建即 createRole)。</summary>
    internal async Task SaveRoleAsync(RoleEditorViewModel editor)
    {
        IReadOnlyList<AdminCommand> commands = editor.BuildCommands();
        if (!editor.CanSave || commands.Count == 0)
        {
            return;
        }
        if (!await GuardAsync(editor.DisplayName, commands, "Users_SaveRoleTitle").ConfigureAwait(true))
        {
            return;
        }
        bool ok = await ExecuteAsync(commands).ConfigureAwait(true);
        if (editor.IsNew)
        {
            if (!ok)
            {
                return;
            }
            string id = $"{editor.NewDb}.{editor.NewName}";
            string label = $"{editor.NewName}@{editor.NewDb}";
            _newRole!.Changed -= OnNewRoleChanged;
            _newRole.Dispose();
            _newRole = null;
            RoleEditor = null;
            Workspace.Toast(new() { Title = Loc.Format("Users_Created", label), Kind = ToastKind.Success });
            await ReloadAsync(keepDrafts: true, selectRoleId: id).ConfigureAwait(true);
            return;
        }
        if (ok)
        {
            Workspace.Toast(new()
            {
                Title = Loc.Format("Users_Saved", editor.DisplayName),
                Detail = Loc.Format("Users_SavedDetail", commands.Count),
                Kind = ToastKind.Success
            });
        }
        if (editor.Role is { } role)
        {
            try
            {
                if (await FetchRoleAsync(role.Name, role.Db).ConfigureAwait(true) is { } fresh)
                {
                    editor.Rebase(fresh);
                }
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                Workspace.Log.Info($"rolesInfo for {role.Name}@{role.Db} failed: {ex.Message}");
            }
            await ReloadAsync(keepDrafts: true, selectRoleId: editor.Id).ConfigureAwait(true);
        }
    }

    private Task DeleteAsync() => _showRoles ? DeleteRoleAsync() : DeleteUserAsync();

    /// <summary>
    /// 删除用户。当前连接正在用的那个身份**拦下不删**:删掉之后这条连接的下一条命令就会认证失败,
    /// 而能把它建回来的人恰恰就是它自己。
    /// </summary>
    private async Task DeleteUserAsync()
    {
        if (_selectedUser is not { } row)
        {
            return;
        }
        MongoUser user = row.User;
        if (IsSelf(user))
        {
            Workspace.Toast(new()
            {
                Title = Loc.Format("Users_CannotDeleteSelf", $"{user.Name}@{user.Db}"),
                Detail = Loc["Users_CannotDeleteSelfDetail"],
                Kind = ToastKind.Warning,
                Duration = TimeSpan.FromSeconds(6)
            });
            return;
        }
        if (!Workspace.EnsureWritable("admin"))
        {
            return;
        }
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Users_DeleteUserTitle"],
                Message = Loc.Format("Users_DeleteUserBody", $"{user.Name}@{user.Db}"),
                ConfirmLabel = Loc["Users_DeleteUserTitle"],
                IconKey = "Mongo.user-x",
                Facts =
                [
                    new(Loc["Users_ColAuthDb"], user.Db),
                    new(Loc["Users_ColRoles"], user.Roles.Count == 0 ? Loc["Common_None"] : string.Join(", ", user.Roles))
                ],
                TypeToConfirm = Workspace.Guard.ConfirmWrites || user.IsRoot ? user.Name : null
            }).ConfigureAwait(true))
        {
            return;
        }
        if (!await ExecuteAsync([UserAdmin.DropUser(user.Name, user.Db)]).ConfigureAwait(true))
        {
            return;
        }
        if (_userDrafts.Remove(user.Id, out UserEditorViewModel? draft))
        {
            draft.Changed -= OnUserDraftChanged;
            draft.Dispose();
        }
        UserEditor = null;
        Workspace.Toast(new() { Title = Loc.Format("Users_Deleted", $"{user.Name}@{user.Db}"), Kind = ToastKind.Success });
        await ReloadAsync(keepDrafts: true, selectUserId: "").ConfigureAwait(true);
    }

    /// <summary>删除自定义角色(持有者会立即失去它带来的权限 —— 在确认框里列出来)。</summary>
    private async Task DeleteRoleAsync()
    {
        if (_selectedRole is not { } row)
        {
            return;
        }
        MongoRole role = row.Role;
        if (!Workspace.EnsureWritable("admin"))
        {
            return;
        }
        IReadOnlyList<string> holders = HoldersOf(role.Ref);
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["Users_DeleteRoleTitle"],
                Message = Loc.Format("Users_DeleteRoleBody", role.Ref, holders.Count),
                ConfirmLabel = Loc["Users_DeleteRoleTitle"],
                IconKey = "Mongo.shield-alert",
                Facts =
                [
                    new(Loc["Users_Privileges"], role.Privileges.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new(Loc["Users_Holders"], holders.Count == 0 ? Loc["Common_None"] : string.Join(", ", holders))
                ],
                TypeToConfirm = Workspace.Guard.ConfirmWrites || holders.Count > 0 ? role.Name : null
            }).ConfigureAwait(true))
        {
            return;
        }
        if (!await ExecuteAsync([UserAdmin.DropRole(role.Name, role.Db)]).ConfigureAwait(true))
        {
            return;
        }
        if (_roleDrafts.Remove(RoleId(role), out RoleEditorViewModel? draft))
        {
            draft.Changed -= OnRoleDraftChanged;
            draft.Dispose();
        }
        RoleEditor = null;
        Workspace.Toast(new() { Title = Loc.Format("Users_Deleted", role.Ref), Kind = ToastKind.Success });
        await ReloadAsync(keepDrafts: true, selectRoleId: "").ConfigureAwait(true);
    }

    private void ResetPassword()
    {
        if (_userEditor?.User is not { } user)
        {
            return;
        }
        Workspace.ShowDialog(new ResetPasswordDialogViewModel(Workspace, user, async (password, generated) =>
        {
            if (!await ChangePasswordAsync(user, password).ConfigureAwait(true))
            {
                return false;
            }
            if (generated)
            {
                await Workspace.CopyAsync(password).ConfigureAwait(true);
            }
            return true;
        }));
    }

    /// <summary>
    /// 轮换密码:生成强随机密码 → updateUser → 复制到剪贴板,并且只显示这一次。
    /// 先确认:旧密码立即失效,还在用它的应用下次认证就会失败。
    /// </summary>
    internal async Task RotatePasswordAsync(UserEditorViewModel editor)
    {
        if (editor.User is not { } user || !Workspace.EnsureWritable("admin"))
        {
            return;
        }
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc.Format("Users_RotateTitle", user.Name),
                Message = Loc.Format("Users_RotateBody", $"{user.Name}@{user.Db}"),
                ConfirmLabel = Loc["Users_Rotate"],
                IconKey = "Mongo.key-round",
                Danger = false,
                TypeToConfirm = Workspace.Guard.ConfirmWrites ? user.Name : null
            }).ConfigureAwait(true))
        {
            return;
        }
        string password = UserAdmin.GeneratePassword();
        if (!await ChangePasswordAsync(user, password, guarded: true).ConfigureAwait(true))
        {
            return;
        }
        await Workspace.CopyAsync(password).ConfigureAwait(true);
        Workspace.ShowDialog(new RevealPasswordDialogViewModel(Workspace, $"{user.Name}@{user.Db}", password));
    }

    /// <summary>改密码:updateUser { pwd, customData(合并,刷新修改时间) }。</summary>
    private async Task<bool> ChangePasswordAsync(MongoUser user, string password, bool guarded = false)
    {
        if (!guarded && !Workspace.EnsureWritable("admin"))
        {
            return false;
        }
        // customData 要以服务器上的最新一份为底(列表里那份可能早于别人刚写的备注)。
        MongoUser current = user;
        try
        {
            current = await FetchUserAsync(user.Name, user.Db).ConfigureAwait(true) ?? user;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Log.Info($"usersInfo for {user.Id} failed: {ex.Message}");
        }
        AdminCommand command = UserAdmin.UpdatePassword(user.Name, user.Db, password, UserAdmin.MergeCustomData(current.CustomData, DateTime.UtcNow));
        if (!guarded && !await GuardAsync(user.Name, [command], "Users_SaveUserTitle").ConfigureAwait(true))
        {
            return false;
        }
        if (!await ExecuteAsync([command]).ConfigureAwait(true))
        {
            return false;
        }
        Workspace.Toast(new() { Title = Loc.Format("Users_PasswordChanged", $"{user.Name}@{user.Db}"), Kind = ToastKind.Success });
        if (_userDrafts.TryGetValue(user.Id, out UserEditorViewModel? draft) && !draft.IsModified)
        {
            await RefreshUserAsync(draft).ConfigureAwait(true);
        }
        return true;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        DropDrafts();
        _newUser?.Dispose();
        _newRole?.Dispose();
        base.Dispose();
    }
}
