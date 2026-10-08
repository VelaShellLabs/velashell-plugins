using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 一次角色授予(<c>{ role: "read", db: "logs" }</c>)。
/// 用户的角色、角色继承的角色都是这个形状;值相等即同一授予,集合运算直接用它。
/// </summary>
/// <param name="Role">角色名。</param>
/// <param name="Db">角色所在库。</param>
internal readonly record struct RoleRef(string Role, string Db)
{
    /// <summary><c>read@logs</c>。</summary>
    public override string ToString() => $"{Role}@{Db}";

    /// <summary>命令里的形状。</summary>
    public BsonDocument ToBson() => new() { { "role", Role }, { "db", Db } };

    /// <summary>从 <c>{ role, db }</c> 读;老服务器偶尔回裸字符串(角色与所在库同库),按 <paramref name="defaultDb" /> 补库。</summary>
    public static RoleRef? From(BsonValue value, string defaultDb)
    {
        if (value is BsonString name)
        {
            return new(name.AsString, defaultDb);
        }
        if (value is BsonDocument doc && doc.TryGetValue("role", out BsonValue role) && role.IsString)
        {
            return new(role.AsString, doc.TryGetValue("db", out BsonValue db) && db.IsString ? db.AsString : defaultDb);
        }
        return null;
    }
}

/// <summary>一条权限(资源 + 动作)。</summary>
/// <param name="Resource">资源文档(<c>{ db, collection }</c> / <c>{ cluster: true }</c> / <c>{ anyResource: true }</c>)。</param>
/// <param name="Actions">动作。</param>
internal sealed record Privilege(BsonDocument Resource, IReadOnlyList<string> Actions)
{
    /// <summary>命令里的形状。</summary>
    public BsonDocument ToBson() => new() { { "resource", Resource.DeepClone() }, { "actions", new BsonArray(Actions) } };

    /// <summary>从 <c>rolesInfo</c> 的权限条目读。</summary>
    public static Privilege? From(BsonValue value)
    {
        if (value is not BsonDocument doc
            || !doc.TryGetValue("resource", out BsonValue resource) || !resource.IsBsonDocument)
        {
            return null;
        }
        List<string> actions = doc.TryGetValue("actions", out BsonValue a) && a.IsBsonArray
            ? [.. a.AsBsonArray.Where(static x => x.IsString).Select(static x => x.AsString)]
            : [];
        return new(resource.AsBsonDocument, actions);
    }
}

/// <summary><c>usersInfo</c> 回来的一个用户。</summary>
/// <param name="Name">用户名。</param>
/// <param name="Db">认证库。</param>
/// <param name="Roles">直接授予的角色。</param>
/// <param name="Mechanisms">认证机制(<c>SCRAM-SHA-256</c>…)。</param>
/// <param name="CustomData">customData(没有为空文档)。</param>
/// <param name="Restrictions">authenticationRestrictions(没查或没有为 <see langword="null" />)。</param>
internal sealed record MongoUser(
    string Name,
    string Db,
    IReadOnlyList<RoleRef> Roles,
    IReadOnlyList<string> Mechanisms,
    BsonDocument CustomData,
    BsonArray? Restrictions)
{
    /// <summary><c>admin.ops_writer</c>(与服务器的 <c>_id</c> 同形,做字典键)。</summary>
    public string Id => $"{Db}.{Name}";

    /// <summary>有没有 root 级别的角色(列表里红色盾牌头像)。</summary>
    public bool IsRoot => Roles.Any(static r => r.Role is "root" or "__system");

    /// <summary>从 <c>usersInfo</c> 的一项读。</summary>
    public static MongoUser Parse(BsonDocument doc)
    {
        string db = doc.GetValue("db", "admin").AsString;
        return new(
            doc.GetValue("user", "").AsString,
            db,
            UserAdmin.ParseRoles(doc.GetValue("roles", new BsonArray()), db),
            doc.TryGetValue("mechanisms", out BsonValue m) && m.IsBsonArray
                ? [.. m.AsBsonArray.Where(static x => x.IsString).Select(static x => x.AsString)]
                : [],
            doc.TryGetValue("customData", out BsonValue c) && c.IsBsonDocument ? c.AsBsonDocument : [],
            doc.TryGetValue("authenticationRestrictions", out BsonValue r) && r.IsBsonArray ? r.AsBsonArray : null);
    }
}

/// <summary><c>rolesInfo</c> 回来的一个角色。</summary>
/// <param name="Name">角色名。</param>
/// <param name="Db">所在库。</param>
/// <param name="Roles">继承的角色。</param>
/// <param name="Privileges">直接定义的权限(不含继承来的)。</param>
/// <param name="IsBuiltin">是不是内置角色。</param>
internal sealed record MongoRole(string Name, string Db, IReadOnlyList<RoleRef> Roles, IReadOnlyList<Privilege> Privileges, bool IsBuiltin)
{
    /// <summary>授予形状。</summary>
    public RoleRef Ref => new(Name, Db);

    /// <summary>从 <c>rolesInfo</c> 的一项读。</summary>
    public static MongoRole Parse(BsonDocument doc)
    {
        string db = doc.GetValue("db", "admin").AsString;
        return new(
            doc.GetValue("role", "").AsString,
            db,
            UserAdmin.ParseRoles(doc.GetValue("roles", new BsonArray()), db),
            doc.TryGetValue("privileges", out BsonValue p) && p.IsBsonArray
                ? [.. p.AsBsonArray.Select(Privilege.From).Where(static x => x is not null).Select(static x => x!)]
                : [],
            doc.GetValue("isBuiltin", false).ToBoolean());
    }
}

/// <summary>一条要执行的用户管理命令:在哪个库跑、命令本体、给人看的 mongosh 写法。</summary>
/// <param name="Database">执行库(用户 / 角色所在库)。</param>
/// <param name="Command">命令文档。</param>
/// <param name="Shell">mongosh 写法(命令预览与复制;密码一律写成 <c>passwordPrompt()</c>)。</param>
internal sealed record AdminCommand(string Database, BsonDocument Command, string Shell);

/// <summary>有效权限预览一行相对服务器现状的变化。</summary>
internal enum PrivilegeChange
{
    /// <summary>没变。</summary>
    Same,

    /// <summary>整条资源是新得到的。</summary>
    Added,

    /// <summary>整条资源会失去。</summary>
    Removed,

    /// <summary>资源还在,动作有增有减。</summary>
    Changed
}

/// <summary>有效权限预览的一行(<c>shop.*  find · insert · update …</c>)。</summary>
/// <param name="Resource">资源(<c>shop.*</c>)。</param>
/// <param name="Change">变化。</param>
/// <param name="Actions">主动作列表(新增 / 失去的整行即全部动作;其余为保留的动作)。</param>
/// <param name="AddedActions">资源还在时新得到的动作(<c>+ dropIndex</c>);没有为空。</param>
/// <param name="RemovedActions">资源还在时会失去的动作;没有为空。</param>
/// <param name="FullText">完整动作清单(悬停提示)。</param>
internal sealed record EffectiveLine(
    string Resource,
    PrivilegeChange Change,
    string Actions,
    string AddedActions,
    string RemovedActions,
    string FullText)
{
    /// <summary>整行新增。</summary>
    public bool IsAdded => Change == PrivilegeChange.Added;

    /// <summary>整行失去。</summary>
    public bool IsRemoved => Change == PrivilegeChange.Removed;

    /// <summary>行首要不要画 <c>+</c> / <c>−</c>。</summary>
    public bool HasPrefix => Change is PrivilegeChange.Added or PrivilegeChange.Removed;

    /// <summary>行首符号。</summary>
    public string Prefix => Change == PrivilegeChange.Removed ? "−" : "+";

    /// <summary>有没有附加的新增动作。</summary>
    public bool HasAddedActions => AddedActions.Length > 0;

    /// <summary>有没有附加的失去动作。</summary>
    public bool HasRemovedActions => RemovedActions.Length > 0;
}

/// <summary>认证机制的三种选法(新建用户)。</summary>
internal enum MechanismChoice
{
    /// <summary>只要 SCRAM-SHA-256(推荐;MongoDB 4.0+)。</summary>
    Sha256,

    /// <summary>两者都要(仍有老驱动要连时)。</summary>
    Both,

    /// <summary>只要 SCRAM-SHA-1。</summary>
    Sha1
}

/// <summary>
/// 内置角色的分类:哪些进矩阵、哪些算"其他内置"、列表标签怎么上色。
/// <para>
/// 矩阵只收"按库授予"的五个(read / readWrite / dbAdmin / userAdmin / dbOwner)外加它们的
/// <c>*AnyDatabase</c> 版本 —— 这几个占了真实部署里绝大多数授予,做成勾选比在下拉里挑快得多;
/// 其余内置角色(backup、clusterMonitor、root…)数量少、而且只在 admin 库上有意义,与自定义角色一起走芯片。
/// </para>
/// </summary>
internal static class BuiltinRoles
{
    /// <summary>矩阵的五列(按库授予的内置角色)。</summary>
    public static IReadOnlyList<string> Columns { get; } = ["read", "readWrite", "dbAdmin", "userAdmin", "dbOwner"];

    /// <summary>「所有数据库」那一行对应的角色(都在 admin 库);dbOwner 没有对应的 AnyDatabase 版本。</summary>
    public static IReadOnlyList<string?> AnyDatabaseColumns { get; } =
        ["readAnyDatabase", "readWriteAnyDatabase", "dbAdminAnyDatabase", "userAdminAnyDatabase", null];

    /// <summary>不进矩阵的内置角色(全在 admin 库),给「添加角色」下拉。</summary>
    public static IReadOnlyList<string> Others { get; } =
        ["clusterMonitor", "clusterManager", "clusterAdmin", "hostManager", "backup", "restore", "enableSharding", "root"];

    private static readonly HashSet<string> All =
    [
        with(StringComparer.Ordinal),
        "read", "readWrite", "dbAdmin", "userAdmin", "dbOwner",
        "readAnyDatabase", "readWriteAnyDatabase", "dbAdminAnyDatabase", "userAdminAnyDatabase",
        "clusterMonitor", "clusterManager", "clusterAdmin", "hostManager", "backup", "restore",
        "enableSharding", "root", "__system", "__queryableBackup", "directShardOperations", "searchCoordinator"
    ];

    /// <summary>是不是内置角色(按名字;内置角色名不会与自定义角色撞 —— 服务器不让建同名的)。</summary>
    public static bool IsBuiltin(string role) => All.Contains(role);

    /// <summary>这个授予在矩阵上的位置:行 = 库名(<see langword="null" /> 表示「所有数据库」),列 = 0–4。不在矩阵上返回 false。</summary>
    public static bool TryLocate(RoleRef role, out string? database, out int column)
    {
        int index = IndexOf(Columns, role.Role);
        if (index >= 0)
        {
            database = role.Db;
            column = index;
            return true;
        }
        index = IndexOf(AnyDatabaseColumns, role.Role);
        if (index >= 0 && role.Db == "admin")
        {
            database = null;
            column = index;
            return true;
        }
        database = null;
        column = -1;
        return false;
    }

    /// <summary>列表标签的颜色令牌:root 红、只读蓝、读写橙、管理类品红、自定义强调色、其余灰。</summary>
    public static string Token(string role) => role switch
    {
        "root" or "__system" or "clusterAdmin" => "VelaError",
        "read" or "readAnyDatabase" => "VelaInfo",
        "readWrite" or "readWriteAnyDatabase" => "VelaWarning",
        "dbAdmin" or "userAdmin" or "dbOwner" or "dbAdminAnyDatabase" or "userAdminAnyDatabase"
            or "clusterManager" or "clusterMonitor" or "hostManager" => "VelaShellMagenta",
        _ when !IsBuiltin(role) => "VelaAccent",
        _ => "VelaTextSecondary"
    };

    private static int IndexOf(IReadOnlyList<string?> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }
}

/// <summary>
/// 用户与角色页的纯逻辑:解析、差异、命令与 mongosh 预览、强密码、有效权限汇总。
/// 全部无状态、不碰界面与连接,单测直接打。
/// </summary>
internal static class UserAdmin
{
    /// <summary>新建用户 / 轮换密码时写进 customData 的"密码修改时间"键。</summary>
    public const string PasswordChangedKey = "passwordChangedAt";

    /// <summary>新建用户时写进 customData 的"创建时间"键(服务器自己不记)。</summary>
    public const string CreatedKey = "createdAt";

    /// <summary>没有在 customData 里写策略时的默认密码有效期(天)。</summary>
    public const int DefaultPolicyDays = 90;

    /// <summary>
    /// 生成密码的字母表:去掉了易混的 0/O、1/l/I,符号只留 URI 不保留字符(<c>- _ . ~</c>)——
    /// 这样密码贴进连接串的 userinfo 段不需要百分号转义,复制粘贴不会因转义出错。
    /// </summary>
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "-_.~";

    private static readonly string[] NoteKeys = ["note", "description", "comment", "purpose"];
    private static readonly string[] CreatedKeys = [CreatedKey, "created", "createdOn"];
    private static readonly string[] ChangedKeys = [PasswordChangedKey, "pwdChangedAt", "passwordUpdatedAt", "passwordRotatedAt"];
    private static readonly string[] PolicyKeys = ["passwordPolicyDays", "passwordMaxAgeDays"];

    /// <summary>
    /// 动作的显示顺序:日常最关心的增删改查与索引在前,其余按字母。
    /// readWrite 有二十多个动作,全列出来没人看得完 —— 预览只露前几个,完整清单在悬停提示里。
    /// </summary>
    private static readonly string[] ActionPriority =
    [
        "find", "insert", "update", "remove", "createIndex", "dropIndex", "createCollection", "dropCollection",
        "collMod", "listCollections", "listIndexes", "collStats", "dbStats", "renameCollectionSameDB",
        "convertToCapped", "changeStream", "killCursors", "bypassDocumentValidation"
    ];

    /// <summary>角色编辑器里可勾选的动作清单(其余动作只要已在权限里就照样保留并显示)。</summary>
    public static IReadOnlyList<string> EditableActions { get; } =
    [
        "find", "insert", "update", "remove", "createIndex", "dropIndex", "collMod", "createCollection",
        "dropCollection", "listCollections", "listIndexes", "collStats", "dbStats", "changeStream",
        "killCursors", "renameCollectionSameDB", "convertToCapped", "bypassDocumentValidation"
    ];

    /// <summary>解析角色数组。</summary>
    public static IReadOnlyList<RoleRef> ParseRoles(BsonValue roles, string defaultDb) =>
        roles is BsonArray array
            ? [.. array.Select(r => RoleRef.From(r, defaultDb)).Where(static r => r is not null).Select(static r => r!.Value)]
            : [];

    // ── customData 里约定俗成的几个字段 ───────────────────────────────────────

    /// <summary>灰字副标题(<c>仅紧急使用</c>):customData.note / description / comment。</summary>
    public static string? Note(BsonDocument customData)
    {
        foreach (string key in NoteKeys)
        {
            if (customData.TryGetValue(key, out BsonValue value) && value.IsString && value.AsString.Trim().Length > 0)
            {
                return value.AsString.Trim();
            }
        }
        return null;
    }

    /// <summary>创建时间(服务器不记;我们建的用户写在 customData.createdAt)。</summary>
    public static DateTime? CreatedAt(BsonDocument customData) => FirstDate(customData, CreatedKeys);

    /// <summary>密码最近一次修改的时间。</summary>
    public static DateTime? PasswordChangedAt(BsonDocument customData) => FirstDate(customData, ChangedKeys);

    /// <summary>密码有效期策略(天):customData.passwordPolicyDays,没有就 90。</summary>
    public static int PolicyDays(BsonDocument customData)
    {
        foreach (string key in PolicyKeys)
        {
            if (customData.TryGetValue(key, out BsonValue value) && value.IsNumeric && value.ToDouble() >= 1)
            {
                return (int)value.ToDouble();
            }
        }
        return DefaultPolicyDays;
    }

    /// <summary>
    /// 密码年龄徽章:返回(天数, 策略天数, 是否已过期);没有修改时间、或离到期还早时返回 <see langword="null" />。
    /// <para>
    /// 「还早」的界线取策略的三分之二:90 天的策略在第 60 天开始提醒 —— 等过期了才亮,
    /// 那时应用早已因为改密码手忙脚乱;提前一个月亮出来,轮换才能排进正常的变更窗口。
    /// </para>
    /// </summary>
    public static (int Days, int Policy, bool Expired)? PasswordAge(BsonDocument customData, DateTime nowUtc)
    {
        if (PasswordChangedAt(customData) is not { } changed)
        {
            return null;
        }
        int days = Math.Max(0, (int)(nowUtc - changed).TotalDays);
        int policy = PolicyDays(customData);
        if (days * 3 < policy * 2)
        {
            return null;
        }
        return (days, policy, days > policy);
    }

    /// <summary>
    /// 合并出新的 customData:保留原有字段,只改给定的几个时间。
    /// <c>updateUser</c> 的 customData 是整体替换 —— 不合并就会把别人写的备注抹掉。
    /// </summary>
    public static BsonDocument MergeCustomData(BsonDocument original, DateTime nowUtc, bool created = false)
    {
        BsonDocument merged = original.DeepClone().AsBsonDocument;
        merged[PasswordChangedKey] = new BsonDateTime(nowUtc);
        if (created)
        {
            merged[CreatedKey] = new BsonDateTime(nowUtc);
        }
        return merged;
    }

    private static DateTime? FirstDate(BsonDocument doc, string[] keys)
    {
        foreach (string key in keys)
        {
            if (!doc.TryGetValue(key, out BsonValue value))
            {
                continue;
            }
            if (value is BsonDateTime date)
            {
                return date.ToUniversalTime();
            }
            if (value.IsString && DateTime.TryParse(value.AsString, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
            {
                return parsed;
            }
        }
        return null;
    }

    /// <summary>头部显示的机制:有 SCRAM-SHA-256 就说它(更强的那个才是实际协商出来的),否则第一个。</summary>
    public static string PrimaryMechanism(IReadOnlyList<string> mechanisms) =>
        mechanisms.Contains("SCRAM-SHA-256") ? "SCRAM-SHA-256" : mechanisms.FirstOrDefault() ?? "";

    /// <summary>机制选项 → 命令里的数组。</summary>
    public static BsonArray Mechanisms(MechanismChoice choice) => choice switch
    {
        MechanismChoice.Sha1 => ["SCRAM-SHA-1"],
        MechanismChoice.Both => ["SCRAM-SHA-256", "SCRAM-SHA-1"],
        _ => ["SCRAM-SHA-256"]
    };

    // ── 登录限制 ──────────────────────────────────────────────────────────

    /// <summary>逗号 / 空白 / 分号分隔的地址清单。</summary>
    public static IReadOnlyList<string> SplitList(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>把清单写回输入框的样子(<c>10.20.0.0/16, 10.0.0.5</c>)。</summary>
    public static string JoinList(IEnumerable<string> items) => string.Join(", ", items);

    /// <summary>从 authenticationRestrictions 的某一项读出 clientSource / serverAddress 的文本。</summary>
    public static (string ClientSource, string ServerAddress) ReadRestriction(BsonValue? restriction)
    {
        if (restriction is not BsonDocument doc)
        {
            return ("", "");
        }
        return (JoinList(Strings(doc.GetValue("clientSource", BsonNull.Value))),
            JoinList(Strings(doc.GetValue("serverAddress", BsonNull.Value))));
    }

    /// <summary>
    /// 按两个输入框重建 authenticationRestrictions。
    /// <para>
    /// 一个限制文档里的 clientSource 与 serverAddress 是"同时满足",多个文档之间是"满足其一"。
    /// 界面只编辑第一份文档;其余的(多半是别处精心配的)原样保留在后面,不能因为界面画不下就把它们抹掉。
    /// 两栏都清空 = 去掉第一份;一份不剩时给空数组(即不限制)。
    /// </para>
    /// </summary>
    public static BsonArray BuildRestrictions(string clientSource, string serverAddress, BsonArray? original)
    {
        var result = new BsonArray();
        IReadOnlyList<string> clients = SplitList(clientSource);
        IReadOnlyList<string> servers = SplitList(serverAddress);
        if (clients.Count > 0 || servers.Count > 0)
        {
            var first = new BsonDocument();
            if (clients.Count > 0)
            {
                first["clientSource"] = new BsonArray(clients);
            }
            if (servers.Count > 0)
            {
                first["serverAddress"] = new BsonArray(servers);
            }
            _ = result.Add(first);
        }
        if (original is not null)
        {
            foreach (BsonValue rest in original.Skip(1))
            {
                _ = result.Add(rest.DeepClone());
            }
        }
        return result;
    }

    /// <summary>
    /// 检查一个 CIDR / IP 写法。只做形状检查(驱动不帮忙,服务器要到 updateUser 时才拒),
    /// 让明显的笔误在保存前就标红。返回第一个写错的项;都对返回 <see langword="null" />。
    /// </summary>
    public static string? FirstInvalidAddress(string? text)
    {
        foreach (string item in SplitList(text))
        {
            string host = item;
            int slash = item.IndexOf('/');
            if (slash >= 0)
            {
                host = item[..slash];
                if (!int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int bits) || bits > 128)
                {
                    return item;
                }
            }
            if (!System.Net.IPAddress.TryParse(host, out _))
            {
                return item;
            }
        }
        return null;
    }

    private static IEnumerable<string> Strings(BsonValue value) => value switch
    {
        BsonArray array => array.Where(static v => v.IsString).Select(static v => v.AsString),
        BsonString s => [s.AsString],
        _ => []
    };

    // ── 强密码 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 生成强随机密码:密码学随机源,四类字符各至少一个(有些策略插件按类别数打分),默认 24 位(≈ 140 bit)。
    /// </summary>
    public static string GeneratePassword(int length = 24)
    {
        length = Math.Max(length, 8);
        const string all = Upper + Lower + Digits + Symbols;
        while (true)
        {
            string candidate = RandomNumberGenerator.GetString(all, length);
            if (candidate.Any(Upper.Contains) && candidate.Any(Lower.Contains)
                && candidate.Any(Digits.Contains) && candidate.Any(Symbols.Contains))
            {
                return candidate;
            }
        }
    }

    // ── 差异与命令 ──────────────────────────────────────────────────────────

    /// <summary>两组授予的差:(新增, 撤销),各自按库、角色排序 —— 命令与预览的顺序稳定,不随勾选顺序跳。</summary>
    public static (IReadOnlyList<RoleRef> Added, IReadOnlyList<RoleRef> Removed) Diff(IEnumerable<RoleRef> original, IEnumerable<RoleRef> current)
    {
        var before = new HashSet<RoleRef>(original);
        var after = new HashSet<RoleRef>(current);
        return (Sort(after.Where(r => !before.Contains(r))), Sort(before.Where(r => !after.Contains(r))));
    }

    /// <summary>授予排序:库、角色。</summary>
    public static IReadOnlyList<RoleRef> Sort(IEnumerable<RoleRef> roles) =>
        [.. roles.OrderBy(static r => r.Db, StringComparer.Ordinal).ThenBy(static r => r.Role, StringComparer.Ordinal)];

    /// <summary><c>grantRolesToUser</c>。</summary>
    public static AdminCommand GrantRolesToUser(string user, string db, IReadOnlyList<RoleRef> roles) =>
        RoleCommand("grantRolesToUser", user, db, roles);

    /// <summary><c>revokeRolesFromUser</c>。</summary>
    public static AdminCommand RevokeRolesFromUser(string user, string db, IReadOnlyList<RoleRef> roles) =>
        RoleCommand("revokeRolesFromUser", user, db, roles);

    /// <summary><c>grantRolesToRole</c>。</summary>
    public static AdminCommand GrantRolesToRole(string role, string db, IReadOnlyList<RoleRef> roles) =>
        RoleCommand("grantRolesToRole", role, db, roles);

    /// <summary><c>revokeRolesFromRole</c>。</summary>
    public static AdminCommand RevokeRolesFromRole(string role, string db, IReadOnlyList<RoleRef> roles) =>
        RoleCommand("revokeRolesFromRole", role, db, roles);

    private static AdminCommand RoleCommand(string name, string target, string db, IReadOnlyList<RoleRef> roles)
    {
        var array = new BsonArray(roles.Select(static r => r.ToBson()));
        return new(db, new BsonDocument { { name, target }, { "roles", array } },
            $"{Sibling(db)}.{name}({BsonText.Quote(target)}, {Js(array)})");
    }

    /// <summary><c>updateUser</c>:只改登录限制。</summary>
    public static AdminCommand UpdateRestrictions(string user, string db, BsonArray restrictions) =>
        new(db, new BsonDocument { { "updateUser", user }, { "authenticationRestrictions", restrictions } },
            $"{Sibling(db)}.updateUser({BsonText.Quote(user)}, {{ authenticationRestrictions: {Js(restrictions)} }})");

    /// <summary>
    /// <c>updateUser</c>:改密码(连带把 customData 里的修改时间刷新)。
    /// 预览里密码一律写 <c>passwordPrompt()</c> —— 命令预览是给人复制的,明文密码不该进剪贴板历史。
    /// </summary>
    public static AdminCommand UpdatePassword(string user, string db, string password, BsonDocument customData) =>
        new(db, new BsonDocument { { "updateUser", user }, { "pwd", password }, { "customData", customData } },
            $"{Sibling(db)}.updateUser({BsonText.Quote(user)}, {{ pwd: passwordPrompt(), customData: {Js(customData)} }})");

    /// <summary><c>createUser</c>。</summary>
    public static AdminCommand CreateUser(
        string user,
        string db,
        string password,
        IReadOnlyList<RoleRef> roles,
        MechanismChoice mechanism,
        BsonArray restrictions,
        BsonDocument customData)
    {
        var rolesArray = new BsonArray(Sort(roles).Select(static r => r.ToBson()));
        BsonArray mechanisms = Mechanisms(mechanism);
        var command = new BsonDocument
        {
            { "createUser", user },
            { "pwd", password },
            { "roles", rolesArray },
            { "mechanisms", mechanisms },
            { "customData", customData }
        };
        var shell = new StringBuilder();
        _ = shell.Append(Sibling(db)).Append(".createUser({ user: ").Append(BsonText.Quote(user))
            .Append(", pwd: passwordPrompt(), roles: ").Append(Js(rolesArray))
            .Append(", mechanisms: ").Append(Js(mechanisms))
            .Append(", customData: ").Append(Js(customData));
        if (restrictions.Count > 0)
        {
            command["authenticationRestrictions"] = restrictions;
            _ = shell.Append(", authenticationRestrictions: ").Append(Js(restrictions));
        }
        _ = shell.Append(" })");
        return new(db, command, shell.ToString());
    }

    /// <summary><c>dropUser</c>。</summary>
    public static AdminCommand DropUser(string user, string db) =>
        new(db, new BsonDocument("dropUser", user), $"{Sibling(db)}.dropUser({BsonText.Quote(user)})");

    /// <summary><c>createRole</c>。</summary>
    public static AdminCommand CreateRole(string role, string db, IReadOnlyList<Privilege> privileges, IReadOnlyList<RoleRef> roles)
    {
        var privilegeArray = new BsonArray(privileges.Select(static p => p.ToBson()));
        var rolesArray = new BsonArray(Sort(roles).Select(static r => r.ToBson()));
        return new(db, new BsonDocument { { "createRole", role }, { "privileges", privilegeArray }, { "roles", rolesArray } },
            $"{Sibling(db)}.createRole({{ role: {BsonText.Quote(role)}, privileges: {Js(privilegeArray)}, roles: {Js(rolesArray)} }})");
    }

    /// <summary><c>grantPrivilegesToRole</c>。</summary>
    public static AdminCommand GrantPrivilegesToRole(string role, string db, IReadOnlyList<Privilege> privileges) =>
        PrivilegeCommand("grantPrivilegesToRole", role, db, privileges);

    /// <summary><c>revokePrivilegesFromRole</c>。</summary>
    public static AdminCommand RevokePrivilegesFromRole(string role, string db, IReadOnlyList<Privilege> privileges) =>
        PrivilegeCommand("revokePrivilegesFromRole", role, db, privileges);

    private static AdminCommand PrivilegeCommand(string name, string role, string db, IReadOnlyList<Privilege> privileges)
    {
        var array = new BsonArray(privileges.Select(static p => p.ToBson()));
        return new(db, new BsonDocument { { name, role }, { "privileges", array } },
            $"{Sibling(db)}.{name}({BsonText.Quote(role)}, {Js(array)})");
    }

    /// <summary><c>dropRole</c>。</summary>
    public static AdminCommand DropRole(string role, string db) =>
        new(db, new BsonDocument("dropRole", role), $"{Sibling(db)}.dropRole({BsonText.Quote(role)})");

    /// <summary>
    /// 两组权限的差 → (要 grant 的, 要 revoke 的)。按资源对齐:同一资源上多出来的动作 grant,少掉的 revoke;
    /// 资源本身改了(库名 / 集合名改了)就是旧资源整条 revoke、新资源整条 grant。
    /// 这正是 grantPrivilegesToRole / revokePrivilegesFromRole 的语义(按资源合并 / 剔除动作)。
    /// </summary>
    public static (IReadOnlyList<Privilege> Grant, IReadOnlyList<Privilege> Revoke) DiffPrivileges(
        IEnumerable<Privilege> original,
        IEnumerable<Privilege> current)
    {
        Dictionary<string, (BsonDocument Resource, HashSet<string> Actions)> before = Group(original);
        Dictionary<string, (BsonDocument Resource, HashSet<string> Actions)> after = Group(current);
        var grant = new List<Privilege>();
        var revoke = new List<Privilege>();
        foreach ((string key, (BsonDocument resource, HashSet<string> actions)) in after)
        {
            IEnumerable<string> added = before.TryGetValue(key, out (BsonDocument Resource, HashSet<string> Actions) old) ? actions.Except(old.Actions) : actions;
            List<string> list = OrderActions(added);
            if (list.Count > 0)
            {
                grant.Add(new(resource, list));
            }
        }
        foreach ((string key, (BsonDocument resource, HashSet<string> actions)) in before)
        {
            IEnumerable<string> removed = after.TryGetValue(key, out (BsonDocument Resource, HashSet<string> Actions) now) ? actions.Except(now.Actions) : actions;
            List<string> list = OrderActions(removed);
            if (list.Count > 0)
            {
                revoke.Add(new(resource, list));
            }
        }
        return (grant, revoke);
    }

    private static Dictionary<string, (BsonDocument Resource, HashSet<string> Actions)> Group(IEnumerable<Privilege> privileges)
    {
        var map = new Dictionary<string, (BsonDocument, HashSet<string>)>(StringComparer.Ordinal);
        foreach (Privilege privilege in privileges)
        {
            string key = ResourceKey(privilege.Resource);
            if (!map.TryGetValue(key, out (BsonDocument, HashSet<string>) entry))
            {
                entry = (privilege.Resource, new HashSet<string>(StringComparer.Ordinal));
                map[key] = entry;
            }
            entry.Item2.UnionWith(privilege.Actions);
        }
        return map;
    }

    /// <summary>资源的规范键(字段顺序无关):同一资源无论服务器回来的字段顺序如何都对得上。</summary>
    public static string ResourceKey(BsonDocument resource) =>
        string.Join(";", resource.Elements.OrderBy(static e => e.Name, StringComparer.Ordinal)
            .Select(static e => $"{e.Name}={e.Value}"));

    /// <summary>资源 → 预览里的写法:<c>shop.*</c>、<c>shop.orders</c>、<c>*.*</c>、<c>cluster</c>、<c>anyResource</c>。</summary>
    public static string ResourceLabel(BsonDocument resource)
    {
        if (resource.GetValue("anyResource", false).ToBoolean())
        {
            return "anyResource";
        }
        if (resource.GetValue("cluster", false).ToBoolean())
        {
            return "cluster";
        }
        string? db = resource.TryGetValue("db", out BsonValue d) && d.IsString ? d.AsString : null;
        if (resource.TryGetValue("system_buckets", out BsonValue buckets) && buckets.IsString)
        {
            return $"{Star(db ?? "")}.system.buckets.{Star(buckets.AsString)}";
        }
        if (db is not null && resource.TryGetValue("collection", out BsonValue c) && c.IsString)
        {
            return $"{Star(db)}.{Star(c.AsString)}";
        }
        return resource.ToJson();
    }

    private static string Star(string name) => name.Length == 0 ? "*" : name;

    /// <summary>
    /// 汇总有效权限:按资源合并动作。<c>shop.system.js</c> 这类系统集合的动作如果整个被
    /// <c>shop.*</c> 覆盖,就折进去 —— 内置角色总会给 system.js 再发一遍同样的动作,列出来只是噪音。
    /// </summary>
    public static IReadOnlyDictionary<string, SortedSet<string>> Aggregate(IEnumerable<Privilege> privileges)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (Privilege privilege in privileges)
        {
            string label = ResourceLabel(privilege.Resource);
            if (!map.TryGetValue(label, out SortedSet<string>? set))
            {
                set = [with(StringComparer.Ordinal)];
                map[label] = set;
            }
            set.UnionWith(privilege.Actions);
        }
        foreach (string label in map.Keys.ToList())
        {
            int dot = label.IndexOf('.');
            if (dot <= 0 || !label[(dot + 1)..].StartsWith("system.", StringComparison.Ordinal))
            {
                continue;
            }
            if (map.TryGetValue(label[..dot] + ".*", out SortedSet<string>? parent) && map[label].IsSubsetOf(parent))
            {
                _ = map.Remove(label);
            }
        }
        return map;
    }

    /// <summary>
    /// 有效权限预览:服务器现状 vs 含未保存改动的状态,逐资源对齐成行。
    /// 行序:没变 / 有增减的在前,整行新增的其次,整行失去的最后;同组内按库名、库级(<c>*</c>)在前,
    /// 集群级 / anyResource 殿后。
    /// </summary>
    public static IReadOnlyList<EffectiveLine> EffectiveLines(
        IReadOnlyDictionary<string, SortedSet<string>> original,
        IReadOnlyDictionary<string, SortedSet<string>> current,
        int maxActions = 6)
    {
        var lines = new List<EffectiveLine>();
        foreach (string label in original.Keys.Union(current.Keys).OrderBy(LineOrder, StringComparer.Ordinal))
        {
            _ = original.TryGetValue(label, out SortedSet<string>? before);
            _ = current.TryGetValue(label, out SortedSet<string>? after);
            if (after is null || after.Count == 0)
            {
                if (before is { Count: > 0 })
                {
                    lines.Add(new(label, PrivilegeChange.Removed, FormatActions(before, maxActions), "", "", Full(before)));
                }
                continue;
            }
            if (before is null || before.Count == 0)
            {
                lines.Add(new(label, PrivilegeChange.Added, FormatActions(after, maxActions), "", "", Full(after)));
                continue;
            }
            List<string> kept = [.. after.Where(before.Contains)];
            List<string> added = [.. after.Where(a => !before.Contains(a))];
            List<string> removed = [.. before.Where(a => !after.Contains(a))];
            bool changed = added.Count > 0 || removed.Count > 0;
            lines.Add(new(label, changed ? PrivilegeChange.Changed : PrivilegeChange.Same,
                FormatActions(kept, maxActions),
                added.Count > 0 ? "+ " + FormatActions(added, maxActions) : "",
                removed.Count > 0 ? "− " + FormatActions(removed, maxActions) : "",
                Full(after)));
        }
        // 现有的在前,新得到的(+)接在后面,失去的(−)殿后:读起来是"原来有这些,这次多了这些"(设计稿 18)。
        return [.. lines.OrderBy(static l => l.Change switch { PrivilegeChange.Added => 1, PrivilegeChange.Removed => 2, _ => 0 })];
    }

    private static string LineOrder(string label) =>
        label is "cluster" or "anyResource"
            ? "￿" + label
            : label.Replace(".*", ". ", StringComparison.Ordinal);

    private static string Full(IEnumerable<string> actions) => string.Join(" · ", OrderActions(actions));

    /// <summary>动作按显示顺序排。</summary>
    public static List<string> OrderActions(IEnumerable<string> actions) =>
    [
        .. actions.Distinct(StringComparer.Ordinal)
            .OrderBy(static a => Array.IndexOf(ActionPriority, a) is var i && i >= 0 ? i : ActionPriority.Length)
            .ThenBy(static a => a, StringComparer.Ordinal)
    ];

    /// <summary>动作清单 → <c>find · insert · update · +17</c>(只露前 <paramref name="max" /> 个)。</summary>
    public static string FormatActions(IEnumerable<string> actions, int max = 6)
    {
        List<string> ordered = OrderActions(actions);
        if (ordered.Count <= max)
        {
            return string.Join(" · ", ordered);
        }
        return string.Join(" · ", ordered.Take(max)) + $" · +{ordered.Count - max}";
    }

    // ── mongosh 写法 ────────────────────────────────────────────────────────

    /// <summary><c>db.getSiblingDB("admin")</c>。</summary>
    public static string Sibling(string db) => $"db.getSiblingDB({BsonText.Quote(db)})";

    /// <summary>
    /// 命令预览用的 JS 字面量:与设计稿一致的紧凑写法 —— 文档 <c>{ role: "read", db: "logs" }</c>(花括号内留空格),
    /// 数组 <c>[{ … }, { … }]</c>(方括号内不留),标量交给 <see cref="BsonText.Literal" />。
    /// </summary>
    public static string Js(BsonValue value) => value switch
    {
        BsonDocument { ElementCount: 0 } => "{}",
        BsonDocument doc => "{ " + string.Join(", ", doc.Elements.Select(static e => $"{BsonText.FieldName(e.Name)}: {Js(e.Value)}")) + " }",
        BsonArray array => "[" + string.Join(", ", array.Select(Js)) + "]",
        _ => BsonText.Literal(value)
    };

    /// <summary>
    /// 有没有与当前连接身份是同一个人(<c>connectionStatus</c> 的 authenticatedUsers)。
    /// 删除自己、撤掉自己的用户管理权限,下一条命令就会被拒 —— 那时已经没法再改回来了。
    /// </summary>
    public static bool IsSelf(string user, string db, IEnumerable<(string User, string Db)> authenticated) =>
        authenticated.Any(a => string.Equals(a.User, user, StringComparison.Ordinal) && string.Equals(a.Db, db, StringComparison.Ordinal));
}
