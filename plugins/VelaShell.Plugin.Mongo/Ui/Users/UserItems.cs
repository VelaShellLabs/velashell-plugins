using System.Collections.ObjectModel;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>列表里的一枚角色标签(<c>read@shop</c>,按角色上色)。</summary>
/// <param name="Text">文字。</param>
/// <param name="Token">字色令牌。</param>
internal sealed record RoleTag(string Text, string Token)
{
    /// <summary>
    /// 把一组授予排成列表那一格里放得下的标签,放不下的折成「+N」。
    /// <para>
    /// 角色列只有 150px(设计稿),标签宽度按 9px 等宽字估:每字 ≈5.4px + 左右各 5px 内边距,间距 4px。
    /// 估算而不是测量 —— 列表行要虚拟化,逐个测 TextBlock 的代价不值得;估宽偏差一两个像素无所谓。
    /// </para>
    /// </summary>
    /// <param name="roles">授予。</param>
    /// <param name="ownerDb">用户 / 角色自己的库(同库的角色省掉 <c>@库</c>,与设计稿 <c>app_rw</c> 一致)。</param>
    /// <param name="focusDb">标签页的作用库(这个库上的角色排在前面)。</param>
    /// <param name="budget">可用宽度。</param>
    public static (IReadOnlyList<RoleTag> Tags, string? More) Fit(IEnumerable<RoleRef> roles, string ownerDb, string? focusDb, double budget = 130)
    {
        List<RoleRef> ordered =
        [
            .. roles
                .Select(static (r, i) => (Role: r, Index: i))
                .OrderBy(static x => x.Role.Role is "root" or "__system" ? 0 : 1)
                .ThenBy(x => focusDb is not null && x.Role.Db == focusDb ? 0 : 1)
                .ThenBy(static x => x.Index)
                .Select(static x => x.Role)
        ];
        var tags = new List<RoleTag>();
        double used = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            RoleRef role = ordered[i];
            string text = role.Db == ownerDb ? role.Role : role.ToString();
            double width = Width(text) + (tags.Count > 0 ? 4 : 0);
            bool last = i == ordered.Count - 1;
            // 不是最后一个时要给「+N」留位置。
            double reserve = last ? 0 : 4 + Width($"+{ordered.Count - i - 1}");
            if (tags.Count > 0 && used + width + reserve > budget)
            {
                return (tags, $"+{ordered.Count - i}");
            }
            tags.Add(new(text, BuiltinRoles.Token(role.Role)));
            used += width;
        }
        return (tags, null);
    }

    private static double Width(string text) => 10 + text.Length * 5.4;
}

/// <summary>左栏用户列表的一行。</summary>
internal sealed class UserRowViewModel : ObservableObject
{
    private readonly Loc _loc;
    private bool _isEditing;

    /// <summary>构造。</summary>
    public UserRowViewModel(MongoUser user, Loc loc, string? focusDb, bool isSelf)
    {
        User = user;
        _loc = loc;
        IsSelf = isSelf;
        Note = UserAdmin.Note(user.CustomData);
        (Tags, More) = RoleTag.Fit(user.Roles, user.Db, focusDb);
    }

    /// <summary>用户。</summary>
    public MongoUser User { get; }

    /// <summary>用户名。</summary>
    public string Name => User.Name;

    /// <summary>认证库。</summary>
    public string Db => User.Db;

    /// <summary>root 级用户(红色盾牌头像)。</summary>
    public bool IsRoot => User.IsRoot;

    /// <summary>就是当前连接所用的身份。</summary>
    public bool IsSelf { get; }

    /// <summary>头像图标。</summary>
    public string AvatarIcon => IsRoot ? "Mongo.shield-alert" : "Mongo.user";

    /// <summary>customData 里的备注。</summary>
    public string? Note { get; }

    /// <summary>角色标签。</summary>
    public IReadOnlyList<RoleTag> Tags { get; }

    /// <summary>放不下的那几个(<c>+1</c>);全放下了为 <see langword="null" />。</summary>
    public string? More { get; }

    /// <summary>有没有「+N」。</summary>
    public bool HasMore => More is not null;

    /// <summary>有未保存的修改(副标题换成橙色「编辑中」)。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value))
            {
                RaisePropertiesChanged(nameof(Subtitle), nameof(HasSubtitle));
            }
        }
    }

    /// <summary>灰字副标题:备注;没有备注而它正是当前连接的身份时标「当前连接身份」;编辑中时换成「编辑中」。</summary>
    public string Subtitle => _isEditing ? _loc["Users_Editing"] : Note ?? (IsSelf ? _loc["Users_CurrentIdentity"] : "");

    /// <summary>有没有副标题(没有时名字垂直居中)。</summary>
    public bool HasSubtitle => Subtitle.Length > 0;
}

/// <summary>角色模式下左栏的一行(自定义角色)。</summary>
internal sealed class RoleRowViewModel : ObservableObject
{
    private readonly Loc _loc;
    private bool _isEditing;

    /// <summary>构造。</summary>
    public RoleRowViewModel(MongoRole role, Loc loc, string? focusDb, int holders)
    {
        Role = role;
        _loc = loc;
        Holders = holders;
        (Tags, More) = RoleTag.Fit(role.Roles, role.Db, focusDb);
    }

    /// <summary>角色。</summary>
    public MongoRole Role { get; }

    /// <summary>角色名。</summary>
    public string Name => Role.Name;

    /// <summary>所在库。</summary>
    public string Db => Role.Db;

    /// <summary>持有它的用户数。</summary>
    public int Holders { get; }

    /// <summary>继承的角色标签。</summary>
    public IReadOnlyList<RoleTag> Tags { get; }

    /// <summary>放不下的那几个。</summary>
    public string? More { get; }

    /// <summary>有没有「+N」。</summary>
    public bool HasMore => More is not null;

    /// <summary>有未保存的修改。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (SetProperty(ref _isEditing, value))
            {
                RaisePropertyChanged(nameof(Subtitle));
            }
        }
    }

    /// <summary>副标题:<c>3 条权限 · 2 个用户</c>;编辑中时换成「编辑中」。</summary>
    public string Subtitle => _isEditing
        ? _loc["Users_Editing"]
        : _loc.Format("Users_RoleRowSummary", Role.Privileges.Count, Holders);
}

/// <summary>内置角色矩阵的一格。</summary>
internal sealed class MatrixCellViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    /// <summary>构造。</summary>
    /// <param name="role">这一格对应的授予;<see langword="null" /> 表示不适用(anyDatabase × dbOwner)。</param>
    /// <param name="original">服务器现状里有没有。</param>
    /// <param name="changed">勾选变化时回调。</param>
    public MatrixCellViewModel(RoleRef? role, bool original, Action changed)
    {
        Role = role;
        Original = original;
        _isChecked = original;
        _changed = changed;
    }

    /// <summary>对应的授予。</summary>
    public RoleRef? Role { get; }

    /// <summary>这一格能不能勾。</summary>
    public bool IsApplicable => Role is not null;

    /// <summary>服务器现状。</summary>
    public bool Original { get; }

    /// <summary>当前勾选(含未保存)。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!IsApplicable || !SetProperty(ref _isChecked, value))
            {
                return;
            }
            RaisePropertyChanged(nameof(IsPending));
            _changed();
        }
    }

    /// <summary>与服务器现状不同(橙色淡底 = 待保存)。</summary>
    public bool IsPending => _isChecked != Original;

    /// <summary>悬停提示(<c>read@logs</c>)。</summary>
    public string Tip => Role?.ToString() ?? "";
}

/// <summary>矩阵的一行(一个库,或「所有数据库」)。</summary>
internal sealed class MatrixRowViewModel
{
    /// <summary>构造。</summary>
    public MatrixRowViewModel(string? database, string label, IReadOnlyList<MatrixCellViewModel> cells)
    {
        Database = database;
        Label = label;
        Cells = cells;
    }

    /// <summary>库名;<see langword="null" /> 是「所有数据库」。</summary>
    public string? Database { get; }

    /// <summary>行首文字。</summary>
    public string Label { get; }

    /// <summary>是不是「所有数据库」行(红色地球图标、界面字体)。</summary>
    public bool IsAny => Database is null;

    /// <summary>五格。</summary>
    public IReadOnlyList<MatrixCellViewModel> Cells { get; }
}

/// <summary>「自定义角色」那排芯片里的一枚(<c>refund_operator@shop ×</c>)。</summary>
internal sealed class RoleChipViewModel
{
    /// <summary>构造。</summary>
    public RoleChipViewModel(RoleRef role, bool pending, Action<RoleChipViewModel> remove)
    {
        Ref = role;
        IsPending = pending;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>授予。</summary>
    public RoleRef Ref { get; }

    /// <summary>文字。</summary>
    public string Text => Ref.ToString();

    /// <summary>是新加的(未保存,橙色)。</summary>
    public bool IsPending { get; }

    /// <summary>是不是内置角色(灰而不是强调色:那不是"自定义"的)。</summary>
    public bool IsBuiltin => BuiltinRoles.IsBuiltin(Ref.Role);

    /// <summary>移除。</summary>
    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// 一组角色授予的编辑器:内置角色矩阵 + 其余角色的芯片。用户的角色与角色继承的角色共用它。
/// <para>
/// 当前状态 = 矩阵里勾着的 + 芯片;与服务器现状(<see cref="Original" />)的差就是要执行的
/// grant / revoke。矩阵格子各自知道自己的原值,所以"勾上又取消"自然回到不变,不会留下空操作。
/// </para>
/// </summary>
internal sealed class RoleSetEditor : ObservableObject
{
    private readonly Loc _loc;
    private readonly IReadOnlyList<string> _databases;
    private readonly string? _onlyDatabase;
    private HashSet<RoleRef> _original;

    /// <summary>构造。</summary>
    /// <param name="loc">文案表。</param>
    /// <param name="original">服务器现状。</param>
    /// <param name="databases">矩阵的行(用户库;系统库除非被引用到,否则不列)。</param>
    /// <param name="onlyDatabase">
    /// 只列这一个库、不列「所有数据库」:非 admin 库里的角色只能继承同库的角色(服务器的规则),
    /// 把别的库摆出来让人勾,只会在保存时换来一句拒绝。
    /// </param>
    public RoleSetEditor(Loc loc, IEnumerable<RoleRef> original, IReadOnlyList<string> databases, string? onlyDatabase = null)
    {
        _loc = loc;
        _databases = databases;
        _onlyDatabase = onlyDatabase;
        _original = [.. original];
        Build(_original);
    }

    /// <summary>当前状态变了。</summary>
    public event Action? Changed;

    /// <summary>矩阵的行。</summary>
    public ObservableCollection<MatrixRowViewModel> Rows { get; } = [];

    /// <summary>其余角色的芯片。</summary>
    public ObservableCollection<RoleChipViewModel> Extras { get; } = [];

    /// <summary>服务器现状。</summary>
    public IReadOnlyCollection<RoleRef> Original => _original;

    /// <summary>当前状态(含未保存)。</summary>
    public IReadOnlyList<RoleRef> Current =>
    [
        .. Rows.SelectMany(static r => r.Cells).Where(static c => c is { IsChecked: true, Role: not null }).Select(static c => c.Role!.Value),
        .. Extras.Select(static e => e.Ref)
    ];

    /// <summary>有没有改动。</summary>
    public bool IsModified
    {
        get
        {
            (IReadOnlyList<RoleRef> added, IReadOnlyList<RoleRef> removed) = UserAdmin.Diff(_original, Current);
            return added.Count > 0 || removed.Count > 0;
        }
    }

    /// <summary>有没有芯片(没有时显示一行灰字)。</summary>
    public bool HasExtras => Extras.Count > 0;

    /// <summary>加一个授予:矩阵上有位置就勾格子(没有那一行就补一行),否则加芯片。</summary>
    public void Add(RoleRef role)
    {
        if (Current.Contains(role))
        {
            return;
        }
        if (BuiltinRoles.TryLocate(role, out string? db, out int column) && (_onlyDatabase is null || db == _onlyDatabase))
        {
            MatrixRowViewModel? row = Rows.FirstOrDefault(r => r.Database == db);
            if (row is null && db is not null)
            {
                row = NewRow(db, _original);
                // 新库插在「所有数据库」那一行之前。
                int anyIndex = Rows.ToList().FindIndex(static r => r.IsAny);
                Rows.Insert(anyIndex < 0 ? Rows.Count : anyIndex, row);
            }
            if (row is not null)
            {
                row.Cells[column].IsChecked = true;
                return;
            }
        }
        Extras.Add(new(role, !_original.Contains(role), Remove));
        RaisePropertyChanged(nameof(HasExtras));
        OnChanged();
    }

    /// <summary>去掉一枚芯片。</summary>
    public void Remove(RoleChipViewModel chip)
    {
        if (Extras.Remove(chip))
        {
            RaisePropertyChanged(nameof(HasExtras));
            OnChanged();
        }
    }

    /// <summary>放弃改动,回到服务器现状。</summary>
    public void Reset() => Rebase(_original);

    /// <summary>保存成功后把当前状态当作新的现状。</summary>
    public void Rebase(IEnumerable<RoleRef> original)
    {
        _original = [.. original];
        Build(_original);
        OnChanged();
    }

    private void Build(IReadOnlyCollection<RoleRef> roles)
    {
        Rows.Clear();
        Extras.Clear();
        var databases = new List<string>();
        if (_onlyDatabase is not null)
        {
            databases.Add(_onlyDatabase);
        }
        else
        {
            databases.AddRange(_databases);
            // 被授予了、但不在用户库清单里的库(admin 上的 read、还没建出来的库)也要有一行,否则那个授予看不见。
            foreach (RoleRef role in roles)
            {
                if (BuiltinRoles.TryLocate(role, out string? db, out _) && db is not null && !databases.Contains(db))
                {
                    databases.Add(db);
                }
            }
        }
        foreach (string db in databases)
        {
            Rows.Add(NewRow(db, roles));
        }
        if (_onlyDatabase is null)
        {
            var cells = new List<MatrixCellViewModel>();
            foreach (string? name in BuiltinRoles.AnyDatabaseColumns)
            {
                RoleRef? role = name is null ? null : new RoleRef(name, "admin");
                cells.Add(new(role, role is { } r && roles.Contains(r), OnChanged));
            }
            Rows.Add(new(null, _loc["Users_AnyDatabase"], cells));
        }
        foreach (RoleRef role in UserAdmin.Sort(roles))
        {
            bool onMatrix = BuiltinRoles.TryLocate(role, out string? db, out _)
                            && (_onlyDatabase is null || db == _onlyDatabase);
            if (!onMatrix)
            {
                Extras.Add(new(role, false, Remove));
            }
        }
        RaisePropertyChanged(nameof(HasExtras));
    }

    private MatrixRowViewModel NewRow(string db, IReadOnlyCollection<RoleRef> roles) =>
        new(db, db, [.. BuiltinRoles.Columns.Select(c => new MatrixCellViewModel(new RoleRef(c, db), roles.Contains(new RoleRef(c, db)), OnChanged))]);

    private void OnChanged()
    {
        RaisePropertyChanged(nameof(IsModified));
        Changed?.Invoke();
    }
}

/// <summary>角色编辑器里一条权限的一个动作勾选。</summary>
internal sealed class ActionCheckViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    /// <summary>构造。</summary>
    /// <param name="name">动作名。</param>
    /// <param name="original">服务器现状里有没有。</param>
    /// <param name="changed">变化回调。</param>
    /// <param name="initial">初始勾选(新建权限时与现状不同);默认等于现状。</param>
    public ActionCheckViewModel(string name, bool original, Action changed, bool? initial = null)
    {
        Name = name;
        Original = original;
        _isChecked = initial ?? original;
        _changed = changed;
    }

    /// <summary>动作名。</summary>
    public string Name { get; }

    /// <summary>服务器现状。</summary>
    public bool Original { get; }

    /// <summary>当前勾选。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                RaisePropertyChanged(nameof(IsPending));
                _changed();
            }
        }
    }

    /// <summary>与现状不同。</summary>
    public bool IsPending => _isChecked != Original;
}

/// <summary>
/// 角色编辑器里的一条权限:资源(库 + 集合)与动作勾选清单。
/// <para>
/// 只有命名空间资源(<c>{ db, collection }</c>)能在这里改库名 / 集合名;
/// <c>cluster</c>、<c>anyResource</c>、时序桶这类资源只读显示,动作照样可勾 ——
/// 它们多半是 DBA 手写的,界面没有把握改对,但也绝不能在保存时把它们丢掉。
/// </para>
/// </summary>
internal sealed class PrivilegeRowViewModel : ObservableObject
{
    private readonly Action _changed;
    private readonly BsonDocument? _resource;
    private string _db;
    private string _collection;

    /// <summary>构造。</summary>
    /// <param name="loc">文案表(行模板里的占位文字要用)。</param>
    /// <param name="original">服务器上的那条;新加的为 <see langword="null" />。</param>
    /// <param name="defaultDb">新加时的库名。</param>
    /// <param name="changed">变化回调。</param>
    /// <param name="remove">删除回调。</param>
    public PrivilegeRowViewModel(Loc loc, Privilege? original, string defaultDb, Action changed, Action<PrivilegeRowViewModel> remove)
    {
        Loc = loc;
        _changed = changed;
        IsNew = original is null;
        _resource = original?.Resource;
        IsNamespace = original is null
                      || (original.Resource.ElementCount == 2
                          && original.Resource.TryGetValue("db", out BsonValue d) && d.IsString
                          && original.Resource.TryGetValue("collection", out BsonValue c) && c.IsString);
        _db = original?.Resource.GetValue("db", defaultDb).ToString() ?? defaultDb;
        _collection = IsNamespace && original is not null ? original.Resource["collection"].AsString : "";
        ResourceLabel = original is null ? "" : UserAdmin.ResourceLabel(original.Resource);
        var have = new HashSet<string>(original?.Actions ?? [], StringComparer.Ordinal);
        // 可勾清单 + 已有但不在清单里的动作(照样列出来,否则保存时会被悄悄去掉)。
        // 新权限默认勾上 find:一条没有动作的权限保存不了,先给最常见的那一个。
        foreach (string action in UserAdmin.EditableActions.Concat(UserAdmin.OrderActions(have)).Distinct(StringComparer.Ordinal))
        {
            Actions.Add(new(action, have.Contains(action), changed, IsNew && action == "find" ? true : null));
        }
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>文案表。</summary>
    public Loc Loc { get; }

    /// <summary>是新加的。</summary>
    public bool IsNew { get; }

    /// <summary>是命名空间资源(库 / 集合可编辑)。</summary>
    public bool IsNamespace { get; }

    /// <summary>非命名空间资源的只读写法(<c>cluster</c>)。</summary>
    public string ResourceLabel { get; }

    /// <summary>库名(空 = 所有库)。</summary>
    public string Db
    {
        get => _db;
        set
        {
            if (SetProperty(ref _db, value.Trim()))
            {
                _changed();
            }
        }
    }

    /// <summary>集合名(空 = 库里所有集合)。</summary>
    public string Collection
    {
        get => _collection;
        set
        {
            if (SetProperty(ref _collection, value.Trim()))
            {
                _changed();
            }
        }
    }

    /// <summary>动作勾选。</summary>
    public ObservableCollection<ActionCheckViewModel> Actions { get; } = [];

    /// <summary>删除这条。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>资源文档。</summary>
    public BsonDocument Resource => IsNamespace
        ? new BsonDocument { { "db", _db }, { "collection", _collection } }
        : _resource!;

    /// <summary>当前状态的权限;一个动作都没勾时为 <see langword="null" />(保存时等于删除)。</summary>
    public Privilege? ToPrivilege()
    {
        List<string> actions = [.. Actions.Where(static a => a.IsChecked).Select(static a => a.Name)];
        return actions.Count == 0 ? null : new Privilege(Resource, actions);
    }
}
