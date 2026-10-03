using System.Collections.ObjectModel;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 角色模式的右侧编辑器:继承的角色(同一套矩阵 + 芯片)、自有权限(资源 + 动作勾选)、
/// 哪些用户持有它、有效权限预览、命令预览 + 放弃 / 保存。
/// <para>
/// 保存走增量命令(grantRolesToRole / revokeRolesFromRole / grantPrivilegesToRole / revokePrivilegesFromRole)
/// 而不是 updateRole 整体覆盖:命令预览里看得出"这次到底动了什么",
/// 而且别人同时在另一处给这个角色加的权限不会被我们手里的旧快照整个盖掉。
/// </para>
/// </summary>
internal sealed class RoleEditorViewModel : ObservableObject, IDisposable
{
    private readonly UsersTabViewModel _owner;
    private MongoRole? _role;
    private string _newName = "";
    private string _newDb;
    private RoleSetEditor _inherits;
    private IReadOnlyList<AdminCommand> _commands = [];

    /// <summary>构造。</summary>
    /// <param name="owner">标签页。</param>
    /// <param name="role">要编辑的角色;<see langword="null" /> 即新建。</param>
    public RoleEditorViewModel(UsersTabViewModel owner, MongoRole? role)
    {
        _owner = owner;
        _role = role;
        _newDb = owner.Database is { Length: > 0 } db && db is not ("local" or "config") ? db : "admin";
        _inherits = NewInherits(role?.Roles ?? [], role?.Db ?? _newDb);
        Effective = new(owner);
        foreach (Privilege privilege in role?.Privileges ?? [])
        {
            Privileges.Add(new(owner.Loc, privilege, role!.Db, OnChanged, RemovePrivilege));
        }
        SaveCommand = new(() => _owner.SaveRoleAsync(this), () => CanSave);
        DiscardCommand = new(Discard, () => IsModified);
        CopyCommandsCommand = new(() => _owner.Workspace.CopyAsync(string.Join(Environment.NewLine, _commands.Select(static c => c.Shell))));
        AddPrivilegeCommand = new(() =>
        {
            Privileges.Add(new(_owner.Loc, null, Db, OnChanged, RemovePrivilege));
            OnChanged();
        });
        AddRoleCommand = new(r => _inherits.Add(r));
        Recompute();
    }

    /// <summary>标签页。</summary>
    public UsersTabViewModel Owner => _owner;

    /// <summary>文案表。</summary>
    public Loc Loc => _owner.Loc;

    /// <summary>正在编辑的角色(新建时为空)。</summary>
    public MongoRole? Role => _role;

    /// <summary>新建状态。</summary>
    public bool IsNew => _role is null;

    /// <summary>草稿键(<c>shop.refund_operator</c>)。</summary>
    public string Id => _role is null ? "" : $"{_role.Db}.{_role.Name}";

    /// <summary>角色所在库。</summary>
    public string Db => _role?.Db ?? _newDb;

    /// <summary>显示名。</summary>
    public string DisplayName => _role?.Name ?? (_newName.Length > 0 ? _newName : Loc["Users_NewRoleTitle"]);

    /// <summary>头部第二行:<c>shop · 3 条权限 · 2 个用户持有</c>。</summary>
    public string Subtitle => _role is null
        ? string.Join(" · ", _newDb, Loc["Users_NotCreated"])
        : string.Join(" · ", _role.Db, Loc.Format("Users_RoleSummary", _role.Privileges.Count, Holders.Count));

    /// <summary>角色名(新建)。</summary>
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

    /// <summary>所在库(新建)。换库会重建继承矩阵:非 admin 库只能继承同库角色。</summary>
    public string NewDb
    {
        get => _newDb;
        set
        {
            string db = string.IsNullOrWhiteSpace(value) ? "admin" : value.Trim();
            string previous = _newDb;
            if (!SetProperty(ref _newDb, db))
            {
                return;
            }
            IReadOnlyList<RoleRef> keep = _inherits.Current;
            _inherits.Changed -= OnChanged;
            _inherits = NewInherits([], db);
            foreach (RoleRef role in keep)
            {
                _inherits.Add(role);
            }
            // 新加的权限默认跟着角色所在库走 —— 只改还停在旧库名上的(手动改过库名的那条不动)。
            foreach (PrivilegeRowViewModel row in Privileges.Where(p => p.IsNew && p.IsNamespace && p.Db == previous))
            {
                row.Db = db;
            }
            RaisePropertiesChanged(nameof(Inherits), nameof(Subtitle));
            OnChanged();
        }
    }

    /// <summary>库的候选。</summary>
    public IReadOnlyList<string> AuthDatabases => _owner.AuthDatabases;

    /// <summary>继承的角色。</summary>
    public RoleSetEditor Inherits => _inherits;

    /// <summary>「添加角色」下拉:非 admin 库的角色只能继承同库的自定义角色;admin 库的还可以继承其余内置角色。</summary>
    public IReadOnlyList<RoleRef> AddableRoles
    {
        get
        {
            var have = new HashSet<RoleRef>(_inherits.Current);
            string db = Db;
            RoleRef self = new(DisplayName, db);
            IEnumerable<RoleRef> custom = _owner.CustomRoles.Select(static r => r.Ref)
                .Where(r => r != self && !have.Contains(r) && (db == "admin" || r.Db == db));
            IEnumerable<RoleRef> builtin = db == "admin"
                ? BuiltinRoles.Others.Select(static r => new RoleRef(r, "admin")).Where(r => !have.Contains(r))
                : [];
            return [.. custom, .. builtin];
        }
    }

    /// <summary>自有权限。</summary>
    public ObservableCollection<PrivilegeRowViewModel> Privileges { get; } = [];

    /// <summary>有没有自有权限(没有时显示一行灰字)。</summary>
    public bool HasPrivileges => Privileges.Count > 0;

    /// <summary>持有这个角色的用户(<c>ops_writer@admin</c>)。</summary>
    public IReadOnlyList<string> Holders => _role is null ? [] : _owner.HoldersOf(_role.Ref);

    /// <summary>有没有持有者。</summary>
    public bool HasHolders => Holders.Count > 0;

    /// <summary>有效权限预览。</summary>
    public EffectivePreview Effective { get; }

    /// <summary>将执行的命令。</summary>
    public IReadOnlyList<AdminCommand> Commands => _commands;

    /// <summary>有没有要执行的命令。</summary>
    public bool HasCommands => _commands.Count > 0;

    /// <summary>底栏的命令预览。</summary>
    public string CommandPreview => _commands.Count == 0 ? Loc["Users_NoChanges"] : string.Join(";  ", _commands.Select(static c => c.Shell));

    private IReadOnlyList<Privilege> CurrentPrivileges =>
        [.. Privileges.Select(static p => p.ToPrivilege()).Where(static p => p is not null).Select(static p => p!)];

    /// <summary>有未保存的改动。</summary>
    public bool IsModified => IsNew ? _newName.Length > 0 || Privileges.Count > 0 || _inherits.Current.Count > 0 : _commands.Count > 0;

    /// <summary>为什么不能保存;能保存为空。</summary>
    public string ValidationError
    {
        get
        {
            // 库名留空(= 所有库)的资源只有 admin 库里的角色才能用,服务器会拒;提前说清楚。
            if (Db != "admin" && Privileges.Any(static p => p.IsNamespace && p.Db.Length == 0))
            {
                return Loc["Users_AnyDbOnlyAdmin"];
            }
            if (!IsNew)
            {
                return "";
            }
            if (_newName.Length == 0)
            {
                return Loc["Users_NeedRoleName"];
            }
            if (BuiltinRoles.IsBuiltin(_newName) || _owner.CustomRoles.Any(r => r.Name == _newName && r.Db == _newDb))
            {
                return Loc.Format("Users_NameTaken", $"{_newName}@{_newDb}");
            }
            return "";
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

    /// <summary>复制命令预览。</summary>
    public AsyncCommand CopyCommandsCommand { get; }

    /// <summary>加一条权限。</summary>
    public RelayCommand AddPrivilegeCommand { get; }

    /// <summary>加一个继承的角色。</summary>
    public RelayCommand<RoleRef> AddRoleCommand { get; }

    /// <summary>草稿状态变了。</summary>
    public event Action<RoleEditorViewModel>? Changed;

    /// <summary>按当前状态拼出要执行的命令。</summary>
    public IReadOnlyList<AdminCommand> BuildCommands()
    {
        if (_role is not { } role)
        {
            return _newName.Length == 0 ? [] : [UserAdmin.CreateRole(_newName, _newDb, CurrentPrivileges, _inherits.Current)];
        }
        var commands = new List<AdminCommand>();
        (IReadOnlyList<RoleRef> added, IReadOnlyList<RoleRef> removed) = UserAdmin.Diff(_inherits.Original, _inherits.Current);
        if (added.Count > 0)
        {
            commands.Add(UserAdmin.GrantRolesToRole(role.Name, role.Db, added));
        }
        if (removed.Count > 0)
        {
            commands.Add(UserAdmin.RevokeRolesFromRole(role.Name, role.Db, removed));
        }
        (IReadOnlyList<Privilege> grant, IReadOnlyList<Privilege> revoke) = UserAdmin.DiffPrivileges(role.Privileges, CurrentPrivileges);
        if (grant.Count > 0)
        {
            commands.Add(UserAdmin.GrantPrivilegesToRole(role.Name, role.Db, grant));
        }
        if (revoke.Count > 0)
        {
            commands.Add(UserAdmin.RevokePrivilegesFromRole(role.Name, role.Db, revoke));
        }
        return commands;
    }

    /// <summary>保存成功之后:以服务器的新状态为现状。</summary>
    internal void Rebase(MongoRole saved)
    {
        _role = saved;
        _inherits.Changed -= OnChanged;
        _inherits = NewInherits(saved.Roles, saved.Db);
        Privileges.Clear();
        foreach (Privilege privilege in saved.Privileges)
        {
            Privileges.Add(new(_owner.Loc, privilege, saved.Db, OnChanged, RemovePrivilege));
        }
        RaisePropertiesChanged(nameof(Inherits), nameof(IsNew), nameof(DisplayName), nameof(Subtitle), nameof(Holders), nameof(HasHolders));
        Recompute();
    }

    private RoleSetEditor NewInherits(IEnumerable<RoleRef> roles, string db)
    {
        var editor = new RoleSetEditor(_owner.Loc, roles, _owner.MatrixDatabases, db == "admin" ? null : db);
        editor.Changed += OnChanged;
        return editor;
    }

    private void RemovePrivilege(PrivilegeRowViewModel row)
    {
        if (Privileges.Remove(row))
        {
            OnChanged();
        }
    }

    private void Discard()
    {
        if (_role is null)
        {
            _newName = "";
            Privileges.Clear();
            _inherits.Rebase([]);
            RaisePropertiesChanged(nameof(NewName), nameof(DisplayName));
        }
        else
        {
            Rebase(_role);
            return;
        }
        Recompute();
    }

    private void OnChanged() => Recompute();

    private void Recompute()
    {
        _commands = BuildCommands();
        RaisePropertiesChanged(nameof(Commands), nameof(HasCommands), nameof(CommandPreview), nameof(IsModified),
            nameof(ValidationError), nameof(ShowValidation), nameof(CanSave), nameof(AddableRoles), nameof(HasPrivileges));
        SaveCommand.RaiseCanExecuteChanged();
        DiscardCommand.RaiseCanExecuteChanged();
        Effective.Request(_inherits.Original, _role?.Privileges ?? [], _inherits.Current, CurrentPrivileges);
        Changed?.Invoke(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _inherits.Changed -= OnChanged;
        Effective.Dispose();
    }
}
