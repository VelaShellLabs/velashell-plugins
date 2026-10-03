using Avalonia.Media.Imaging;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 用户与角色(设计稿 18):纯逻辑(差异、命令与预览、权限汇总、密码)单测,
/// 加上打真实服务器的读写流程与截图。
/// 集成测试只碰自己建的 <c>velashell_users_*</c> 用户与角色,收尾一定删掉;
/// 绝不改 ops_reader / ops_writer / backup 这三个现成用户。
/// </summary>
[TestClass]
public sealed class UsersTests
{
    // ── 命令与预览 ───────────────────────────────────────────────────────

    /// <summary>命令预览与设计稿底栏那一行逐字一致,命令本体是同一份授予。</summary>
    [TestMethod]
    public void Grant_preview_matches_the_design()
    {
        AdminCommand command = UserAdmin.GrantRolesToUser("ops_writer", "admin", [new("read", "logs")]);

        Assert.AreEqual("""db.getSiblingDB("admin").grantRolesToUser("ops_writer", [{ role: "read", db: "logs" }])""", command.Shell);
        Assert.AreEqual("admin", command.Database);
        Assert.AreEqual(
            new BsonDocument { { "grantRolesToUser", "ops_writer" }, { "roles", new BsonArray { new BsonDocument { { "role", "read" }, { "db", "logs" } } } } },
            command.Command);
    }

    /// <summary>预览里密码一律是 passwordPrompt(),明文只在命令本体里。</summary>
    [TestMethod]
    public void Password_never_appears_in_the_preview()
    {
        AdminCommand create = UserAdmin.CreateUser("app", "shop", "S3cret-Pass", [new("readWrite", "shop")], MechanismChoice.Sha256, [], []);
        AdminCommand update = UserAdmin.UpdatePassword("app", "shop", "S3cret-Pass", []);

        Assert.IsFalse(create.Shell.Contains("S3cret", StringComparison.Ordinal));
        Assert.IsFalse(update.Shell.Contains("S3cret", StringComparison.Ordinal));
        Assert.IsTrue(create.Shell.Contains("passwordPrompt()", StringComparison.Ordinal));
        Assert.AreEqual("S3cret-Pass", create.Command["pwd"].AsString);
        Assert.AreEqual(new BsonArray { "SCRAM-SHA-256" }, create.Command["mechanisms"]);
    }

    /// <summary>授予差按库、角色排序,没变的不出现。</summary>
    [TestMethod]
    public void Diff_is_sorted_and_ignores_unchanged_grants()
    {
        (IReadOnlyList<RoleRef> added, IReadOnlyList<RoleRef> removed) = UserAdmin.Diff(
            [new("readWrite", "shop"), new("read", "logs")],
            [new("read", "logs"), new("dbAdmin", "shop"), new("read", "analytics")]);

        CollectionAssert.AreEqual(new[] { new RoleRef("read", "analytics"), new RoleRef("dbAdmin", "shop") }, added.ToArray());
        CollectionAssert.AreEqual(new[] { new RoleRef("readWrite", "shop") }, removed.ToArray());
    }

    /// <summary>按库授予的内置角色与 AnyDatabase 版本落到矩阵的正确格子,其余角色不上矩阵。</summary>
    [TestMethod]
    public void Builtin_roles_land_on_the_matrix()
    {
        Assert.IsTrue(BuiltinRoles.TryLocate(new("readWrite", "shop"), out string? db, out int column));
        Assert.AreEqual("shop", db);
        Assert.AreEqual(1, column);

        Assert.IsTrue(BuiltinRoles.TryLocate(new("userAdminAnyDatabase", "admin"), out db, out column));
        Assert.IsNull(db, "anyDatabase roles sit on the 'all databases' row");
        Assert.AreEqual(3, column);

        Assert.IsFalse(BuiltinRoles.TryLocate(new("readAnyDatabase", "shop"), out _, out _), "AnyDatabase roles only exist in admin");
        Assert.IsFalse(BuiltinRoles.TryLocate(new("backup", "admin"), out _, out _));
        Assert.IsFalse(BuiltinRoles.TryLocate(new("refund_operator", "shop"), out _, out _));
    }

    /// <summary>矩阵格子与芯片跟踪待保存状态;勾上再取消即无改动;放弃回到现状。</summary>
    [TestMethod]
    public void Role_set_editor_tracks_pending_cells_and_chips()
    {
        var editor = new RoleSetEditor(new Loc("zh-CN"), [new("readWrite", "shop"), new("backup", "admin")], ["logs", "shop"]);

        Assert.AreEqual(3, editor.Rows.Count, "logs, shop and the anyDatabase row");
        Assert.AreEqual(1, editor.Extras.Count, "backup is not on the matrix");
        Assert.IsFalse(editor.IsModified);

        MatrixCellViewModel logsRead = editor.Rows.First(r => r.Database == "logs").Cells[0];
        logsRead.IsChecked = true;
        Assert.IsTrue(logsRead.IsPending);
        Assert.IsTrue(editor.IsModified);
        CollectionAssert.Contains(editor.Current.ToList(), new RoleRef("read", "logs"));

        logsRead.IsChecked = false;
        Assert.IsFalse(editor.IsModified, "ticking and unticking again is no change");

        editor.Add(new("refund_operator", "shop"));
        Assert.IsTrue(editor.Extras.Any(e => e.Ref == new RoleRef("refund_operator", "shop") && e.IsPending));
        editor.Add(new("dbAdmin", "crm"));
        Assert.IsTrue(editor.Rows.Any(r => r.Database == "crm"), "a matrix role on an unlisted database adds a row");
        Assert.IsNull(editor.Rows[^1].Database, "the anyDatabase row stays last");

        editor.Reset();
        Assert.IsFalse(editor.IsModified);
        Assert.IsFalse(editor.Rows.Any(r => r.Database == "crm"));
    }

    /// <summary>非 admin 库的角色只能继承同库角色,矩阵只列那一个库。</summary>
    [TestMethod]
    public void Roles_outside_admin_only_see_their_own_database()
    {
        var editor = new RoleSetEditor(new Loc("en"), [new("read", "shop")], ["logs", "shop"], onlyDatabase: "shop");

        Assert.AreEqual(1, editor.Rows.Count);
        Assert.AreEqual("shop", editor.Rows[0].Database);
        Assert.IsTrue(editor.Rows[0].Cells[0].IsChecked);
    }

    // ── 有效权限 ─────────────────────────────────────────────────────────

    /// <summary>有效权限按资源对齐:新增整行、增减动作、失去整行各自标出;system.js 被覆盖时折叠。</summary>
    [TestMethod]
    public void Effective_lines_mark_what_a_change_adds_and_removes()
    {
        Privilege shopRw = new(Ns("shop", ""), ["find", "insert", "update", "remove"]);
        Privilege shopJs = new(Ns("shop", "system.js"), ["find", "insert"]);
        Privilege logsRead = new(Ns("logs", ""), ["find", "listCollections"]);
        Privilege cluster = new(new BsonDocument("cluster", true), ["listDatabases"]);

        IReadOnlyDictionary<string, SortedSet<string>> before = UserAdmin.Aggregate([shopRw, shopJs, cluster]);
        IReadOnlyDictionary<string, SortedSet<string>> after = UserAdmin.Aggregate(
            [new Privilege(Ns("shop", ""), ["find", "insert", "update", "remove", "createIndex"]), shopJs, logsRead]);

        Assert.IsFalse(before.ContainsKey("shop.system.js"), "system.js covered by shop.* folds away");
        IReadOnlyList<EffectiveLine> lines = UserAdmin.EffectiveLines(before, after);

        Assert.AreEqual(3, lines.Count);
        EffectiveLine logs = lines.Single(l => l.Resource == "logs.*");
        Assert.AreEqual(PrivilegeChange.Added, logs.Change);
        Assert.AreEqual("find · listCollections", logs.Actions);
        EffectiveLine shop = lines.Single(l => l.Resource == "shop.*");
        Assert.AreEqual(PrivilegeChange.Changed, shop.Change);
        Assert.AreEqual("find · insert · update · remove", shop.Actions);
        Assert.AreEqual("+ createIndex", shop.AddedActions);
        Assert.AreEqual(PrivilegeChange.Removed, lines.Single(l => l.Resource == "cluster").Change);
        Assert.AreEqual("cluster", lines[^1].Resource, "cluster-wide resources come last");
    }

    /// <summary>动作清单按优先顺序只露前几个,其余折成计数。</summary>
    [TestMethod]
    public void Long_action_lists_are_cut_with_a_count()
    {
        string text = UserAdmin.FormatActions(["planCacheRead", "find", "dbHash", "insert", "update", "remove", "dropIndex", "createIndex", "collStats"], 6);

        Assert.AreEqual("find · insert · update · remove · createIndex · dropIndex · +3", text);
    }

    /// <summary>角色权限差按资源拆成 grant / revoke。</summary>
    [TestMethod]
    public void Privilege_diff_grants_and_revokes_per_resource()
    {
        Privilege[] original = [new(Ns("shop", "orders"), ["find", "update"]), new(Ns("shop", "refunds"), ["insert"])];
        Privilege[] current = [new(Ns("shop", "orders"), ["find", "remove"]), new(Ns("logs", ""), ["find"])];

        (IReadOnlyList<Privilege> grant, IReadOnlyList<Privilege> revoke) = UserAdmin.DiffPrivileges(original, current);

        Assert.AreEqual(2, grant.Count);
        CollectionAssert.AreEqual(new[] { "remove" }, grant.Single(p => p.Resource["collection"] == "orders").Actions.ToArray());
        Assert.IsTrue(grant.Any(p => p.Resource["db"] == "logs"));
        Assert.AreEqual(2, revoke.Count);
        CollectionAssert.AreEqual(new[] { "update" }, revoke.Single(p => p.Resource["collection"] == "orders").Actions.ToArray());
        Assert.IsTrue(revoke.Any(p => p.Resource["collection"] == "refunds"));
    }

    // ── 登录限制、密码、标签 ─────────────────────────────────────────────

    /// <summary>登录限制只改第一份,服务器上的其余几份原样保留;地址写法校验。</summary>
    [TestMethod]
    public void Restrictions_keep_the_extra_sets_from_the_server()
    {
        var original = new BsonArray
        {
            new BsonDocument("clientSource", new BsonArray { "10.0.0.1" }),
            new BsonDocument("serverAddress", new BsonArray { "192.168.1.0/24" })
        };

        BsonArray built = UserAdmin.BuildRestrictions("10.20.0.0/16, 10.0.0.5", "", original);

        Assert.AreEqual(2, built.Count);
        Assert.AreEqual(new BsonArray { "10.20.0.0/16", "10.0.0.5" }, built[0]["clientSource"]);
        Assert.IsFalse(built[0].AsBsonDocument.Contains("serverAddress"));
        Assert.AreEqual(original[1], built[1]);
        Assert.AreEqual(1, UserAdmin.BuildRestrictions("", "", original).Count, "clearing both boxes drops only the first set");
        Assert.AreEqual(("10.0.0.1", ""), UserAdmin.ReadRestriction(original[0]));

        Assert.IsNull(UserAdmin.FirstInvalidAddress("10.20.0.0/16, 10.0.0.5, ::1"));
        Assert.AreEqual("10.0.0.300", UserAdmin.FirstInvalidAddress("10.0.0.1, 10.0.0.300"));
        Assert.AreEqual("10.0.0.0/x", UserAdmin.FirstInvalidAddress("10.0.0.0/x"));
    }

    /// <summary>生成的密码够长、四类字符齐全、放进连接串无需转义、互不重复。</summary>
    [TestMethod]
    public void Generated_passwords_are_strong_and_connection_string_safe()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 200; i++)
        {
            string password = UserAdmin.GeneratePassword();
            Assert.AreEqual(24, password.Length);
            Assert.IsTrue(password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit));
            Assert.IsTrue(password.All(c => char.IsAsciiLetterOrDigit(c) || "-_.~".Contains(c)), password);
            Assert.AreEqual(password, Uri.EscapeDataString(password), "no escaping needed inside a URI");
            seen.Add(password);
        }
        Assert.AreEqual(200, seen.Count);
    }

    /// <summary>密码年龄徽章在策略的三分之二处出现,超过策略即过期;合并 customData 保留原字段。</summary>
    [TestMethod]
    public void Password_age_badge_appears_two_thirds_into_the_policy()
    {
        DateTime now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var custom = new BsonDocument(UserAdmin.PasswordChangedKey, new BsonDateTime(now.AddDays(-62)));

        Assert.AreEqual((62, 90, false), UserAdmin.PasswordAge(custom, now));
        Assert.IsNull(UserAdmin.PasswordAge(new BsonDocument(UserAdmin.PasswordChangedKey, new BsonDateTime(now.AddDays(-30))), now));
        Assert.IsNull(UserAdmin.PasswordAge([], now), "no change time, no badge");
        custom["passwordPolicyDays"] = 45;
        Assert.AreEqual((62, 45, true), UserAdmin.PasswordAge(custom, now));

        BsonDocument merged = UserAdmin.MergeCustomData(new BsonDocument("note", "仅紧急使用"), now, created: true);
        Assert.AreEqual("仅紧急使用", UserAdmin.Note(merged), "updateUser replaces customData, so the note must be carried over");
        Assert.AreEqual(now, UserAdmin.CreatedAt(merged));
    }

    /// <summary>列表角色标签按列宽估算,放不下的折成 +N;作用库的角色排前。</summary>
    [TestMethod]
    public void Role_tags_fit_the_column_and_fold_into_a_count()
    {
        (IReadOnlyList<RoleTag> writer, string? more) = RoleTag.Fit([new("readWrite", "shop"), new("read", "logs")], "admin", "shop");
        Assert.AreEqual(1, writer.Count);
        Assert.AreEqual("readWrite@shop", writer[0].Text);
        Assert.AreEqual("VelaWarning", writer[0].Token);
        Assert.AreEqual("+1", more);

        (IReadOnlyList<RoleTag> reader, more) = RoleTag.Fit([new("read", "logs"), new("read", "shop")], "admin", "shop");
        CollectionAssert.AreEqual(new[] { "read@shop", "read@logs" }, reader.Select(t => t.Text).ToArray(), "the tab's database first");
        Assert.IsNull(more);

        (IReadOnlyList<RoleTag> root, _) = RoleTag.Fit([new("root", "admin")], "admin", "shop");
        Assert.AreEqual("root", root[0].Text, "roles in the user's own database drop the @db");
        Assert.AreEqual("VelaError", root[0].Token);
    }

    /// <summary>认出当前连接所用的身份(同名不同库不算)。</summary>
    [TestMethod]
    public void The_connected_identity_is_recognised()
    {
        (string, string)[] authenticated = [("ops_writer", "admin")];

        Assert.IsTrue(UserAdmin.IsSelf("ops_writer", "admin", authenticated));
        Assert.IsFalse(UserAdmin.IsSelf("ops_writer", "shop", authenticated), "same name in another auth database is someone else");
        Assert.IsFalse(UserAdmin.IsSelf("ops_reader", "admin", authenticated));
    }

    // ── 真实服务器 ───────────────────────────────────────────────────────

    /// <summary>真实服务器:新建用户 → 改角色与登录限制 → 重置密码 → 删除,每步都以服务器为准核对。</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void Users_are_created_edited_reset_and_deleted_on_the_server() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string name = "velashell_users_" + Guid.NewGuid().ToString("N")[..8];
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        IMongoDatabase admin = bench.Connection.Database("admin");
        try
        {
            UsersTabViewModel tab = await OpenUsersAsync(bench);
            Assert.IsTrue(tab.Users.Any(u => u.Name == "ops_writer"), "usersInfo forAllDBs lists the seeded users");

            // 新建:账号 + 矩阵里勾 read@shop。
            tab.NewUserCommand.Execute(null);
            UserEditorViewModel created = tab.UserEditor!;
            Assert.IsTrue(created.IsNew);
            created.NewName = name;
            created.Password = "Init-Pass-" + Guid.NewGuid().ToString("N")[..10];
            created.Roles.Rows.First(r => r.Database == "shop").Cells[0].IsChecked = true;
            Assert.IsTrue(created.CanSave, created.ValidationError);
            await Screens.PumpAsync(30);
            Screens.Capture(bench.Window, "18-users-new");
            await tab.SaveUserAsync(created);

            BsonDocument user = await UserDocAsync(admin, name);
            CollectionAssert.AreEqual(new[] { "read@shop" }, Roles(user));
            Assert.IsTrue(user["customData"].AsBsonDocument.Contains(UserAdmin.CreatedKey));
            Assert.AreEqual(name, tab.SelectedUser?.Name, "the new user is selected after saving");

            // 改:加 readWrite@logs、撤 read@shop、限制 clientSource。
            UserEditorViewModel editor = tab.UserEditor!;
            Assert.AreEqual(name, editor.DisplayName);
            editor.Roles.Rows.First(r => r.Database == "logs").Cells[1].IsChecked = true;
            editor.Roles.Rows.First(r => r.Database == "shop").Cells[0].IsChecked = false;
            editor.ClientSource = "10.20.0.0/16, 127.0.0.1";
            Assert.AreEqual(3, editor.Commands.Count, "grant + revoke + updateUser");
            Assert.IsTrue(tab.IsModified);
            Assert.IsTrue(tab.SelectedUser!.IsEditing);
            await tab.SaveUserAsync(editor);

            user = await UserDocAsync(admin, name);
            CollectionAssert.AreEqual(new[] { "readWrite@logs" }, Roles(user));
            Assert.AreEqual(new BsonArray { "10.20.0.0/16", "127.0.0.1" },
                user["authenticationRestrictions"].AsBsonArray[0]["clientSource"]);
            Assert.IsFalse(tab.IsModified, "the draft is rebased on the saved state");
            await Screens.PumpAsync(20);
            Assert.AreEqual("10.20.0.0/16, 127.0.0.1", tab.UserEditor!.ClientSource);

            // 只读模式:保存被外壳的护栏拦下,服务器上不变。
            bench.Session.Guard.IsReadOnly = true;
            tab.UserEditor!.Roles.Rows.First(r => r.Database == "shop").Cells[1].IsChecked = true;
            await tab.SaveUserAsync(tab.UserEditor!);
            user = await UserDocAsync(admin, name);
            CollectionAssert.AreEqual(new[] { "readWrite@logs" }, Roles(user), "read-only mode must not write");
            Assert.IsTrue(tab.IsModified, "the blocked change stays as a draft");
            bench.Session.Guard.IsReadOnly = false;
            tab.UserEditor!.DiscardCommand.Execute(null);
            Assert.IsFalse(tab.IsModified);

            // 重置密码(对话框):两遍一致才能保存,customData 记下修改时间。
            tab.ResetPasswordCommand.Execute(null);
            var reset = (ResetPasswordDialogViewModel)bench.ViewModel.Dialog!;
            reset.Password = "Reset-Pass-123456";
            reset.Confirm = "Reset-Pass-12345";
            Assert.IsFalse(reset.CanApply);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "18-users-reset");
            reset.Confirm = "Reset-Pass-123456";
            reset.ApplyCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is null);
            user = await UserDocAsync(admin, name);
            Assert.IsTrue(user["customData"].AsBsonDocument.Contains(UserAdmin.PasswordChangedKey));
            Assert.IsTrue(user["customData"].AsBsonDocument.Contains(UserAdmin.CreatedKey), "the rest of customData is carried over");

            // 轮换密码:确认 → 生成强密码并生效 → 只显示一次。
            DateTime before = user["customData"][UserAdmin.PasswordChangedKey].ToUniversalTime();
            await Task.Delay(20);
            tab.UserEditor!.RotateCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
            ((ConfirmDialogViewModel)bench.ViewModel.Dialog!).ConfirmCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is RevealPasswordDialogViewModel);
            var reveal = (RevealPasswordDialogViewModel)bench.ViewModel.Dialog!;
            Assert.AreEqual(24, reveal.Password.Length);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "18-users-reveal");
            reveal.Close();
            user = await UserDocAsync(admin, name);
            Assert.IsTrue(user["customData"][UserAdmin.PasswordChangedKey].ToUniversalTime() > before, "rotation refreshes the change time");

            // 删除:确认框 → dropUser。
            tab.DeleteCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
            var confirm = (ConfirmDialogViewModel)bench.ViewModel.Dialog!;
            if (confirm.RequiresTyping)
            {
                confirm.Typed = name;
            }
            confirm.ConfirmCommand.Execute(null);
            await WaitAsync(() => tab.Users.All(u => u.Name != name));
            BsonDocument gone = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("usersInfo", name));
            Assert.AreEqual(0, gone["users"].AsBsonArray.Count);
        }
        finally
        {
            await DropUserQuietlyAsync(admin, name);
        }
    });

    /// <summary>真实服务器:新建自定义角色 → 列出持有者 → 增量改权限与继承 → 删除(手打名称)。</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void Custom_roles_are_created_edited_and_dropped_on_the_server() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string role = "velashell_users_role_" + suffix;
        string holder = "velashell_users_" + suffix;
        string db = "velashell_users_" + suffix;
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        IMongoDatabase admin = bench.Connection.Database("admin");
        try
        {
            UsersTabViewModel tab = await OpenUsersAsync(bench);

            tab.NewRoleCommand.Execute(null);
            Assert.IsTrue(tab.ShowRoles);
            RoleEditorViewModel created = tab.RoleEditor!;
            created.NewName = role;
            created.NewDb = "admin";
            created.AddPrivilegeCommand.Execute(null);
            PrivilegeRowViewModel row = created.Privileges[0];
            row.Db = db;
            row.Actions.First(a => a.Name == "insert").IsChecked = true;
            created.Inherits.Rows.First(r => r.Database == "logs").Cells[0].IsChecked = true;
            Assert.IsTrue(created.CanSave, created.ValidationError);
            await tab.SaveRoleAsync(created);

            BsonDocument info = await RoleDocAsync(admin, role);
            BsonDocument privilege = info["privileges"].AsBsonArray.Single().AsBsonDocument;
            Assert.AreEqual(db, privilege["resource"]["db"].AsString);
            CollectionAssert.AreEquivalent(new[] { "find", "insert" }, privilege["actions"].AsBsonArray.Select(a => a.AsString).ToArray());
            CollectionAssert.AreEqual(new[] { "read@logs" }, Roles(info));

            // 有人持有它 → 编辑器里列出持有者。
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument
            {
                { "createUser", holder }, { "pwd", "Holder-Pass-123456" },
                { "roles", new BsonArray { new BsonDocument { { "role", role }, { "db", "admin" } } } }
            });
            await tab.ReloadAsync(keepDrafts: false, selectRoleId: "admin." + role);
            RoleEditorViewModel editor = tab.RoleEditor!;
            Assert.AreEqual(role, editor.DisplayName);
            CollectionAssert.Contains(editor.Holders.ToList(), holder + "@admin");
            await WaitAsync(() => !editor.Effective.IsBusy && editor.Effective.Lines.Count > 0);
            await Screens.PumpAsync(20);
            Screens.Capture(bench.Window, "18-users-roles");

            // 改:去掉 insert、加 update;不再继承 read@logs → revoke + grant 增量命令。
            PrivilegeRowViewModel existing = editor.Privileges.Single();
            existing.Actions.First(a => a.Name == "insert").IsChecked = false;
            existing.Actions.First(a => a.Name == "update").IsChecked = true;
            editor.Inherits.Rows.First(r => r.Database == "logs").Cells[0].IsChecked = false;
            CollectionAssert.AreEquivalent(
                new[] { "revokeRolesFromRole", "grantPrivilegesToRole", "revokePrivilegesFromRole" },
                editor.Commands.Select(c => c.Command.GetElement(0).Name).ToArray());
            await tab.SaveRoleAsync(editor);

            info = await RoleDocAsync(admin, role);
            CollectionAssert.AreEquivalent(new[] { "find", "update" },
                info["privileges"].AsBsonArray.Single()["actions"].AsBsonArray.Select(a => a.AsString).ToArray());
            Assert.AreEqual(0, info["roles"].AsBsonArray.Count);

            // 删除:确认框里列出持有者,要手打角色名。
            tab.DeleteCommand.Execute(null);
            await WaitAsync(() => bench.ViewModel.Dialog is ConfirmDialogViewModel);
            var confirm = (ConfirmDialogViewModel)bench.ViewModel.Dialog!;
            Assert.IsTrue(confirm.RequiresTyping, "a role somebody holds asks for its name");
            confirm.Typed = role;
            confirm.ConfirmCommand.Execute(null);
            await WaitAsync(() => tab.Roles.All(r => r.Name != role));
            BsonDocument gone = await admin.RunCommandAsync<BsonDocument>(new BsonDocument("rolesInfo", role));
            Assert.AreEqual(0, gone["roles"].AsBsonArray.Count);
        }
        finally
        {
            await DropUserQuietlyAsync(admin, holder);
            try
            {
                await admin.RunCommandAsync<BsonDocument>(new BsonDocument("dropRole", role));
            }
            catch (MongoCommandException)
            {
            }
        }
    });

    /// <summary>设计稿 18 截图:选中 ops_writer、在矩阵里给 logs 勾上 read(不保存)。</summary>
    [TestMethod]
    [TestCategory("Screenshots")]
    public void Board18_users_and_roles() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        UsersTabViewModel tab = await OpenUsersAsync(bench);

        tab.SelectedUser = tab.Users.Single(u => u.Name == "ops_writer");
        UserEditorViewModel editor = tab.UserEditor!;
        editor.Roles.Rows.First(r => r.Database == "logs").Cells[0].IsChecked = true;
        await Screens.PumpAsync(40);
        await WaitAsync(() => !editor.Effective.IsBusy && editor.Effective.Lines.Count > 0);
        await Screens.PumpAsync(20);

        WriteableBitmap? frame = Screens.Capture(bench.Window, "18-users");
        Assert.IsNotNull(frame);
        Assert.AreEqual("""db.getSiblingDB("admin").grantRolesToUser("ops_writer", [{ role: "read", db: "logs" }])""", editor.CommandPreview);
        Assert.IsTrue(tab.SelectedUser!.IsEditing);
        Assert.IsTrue(tab.StatusText.Contains("ops_writer", StringComparison.Ordinal), tab.StatusText);
        Assert.IsTrue(editor.Effective.Lines.Any(l => l.Resource == "logs.*" && l.IsAdded));

        // 不保存:截完放弃,服务器上的 ops_writer 原封不动。
        editor.DiscardCommand.Execute(null);
        Assert.IsFalse(tab.IsModified);
    });

    // ── 助手 ─────────────────────────────────────────────────────────────

    private static async Task<UsersTabViewModel> OpenUsersAsync(Workbench bench)
    {
        bench.Session.OpenUsers("shop");
        var tab = (UsersTabViewModel)bench.ViewModel.ActiveTab!;
        await WaitAsync(() => !tab.IsLoading && tab.Users.Count > 0);
        await Screens.PumpAsync(20);
        return tab;
    }

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the users tab.");
            }
            await Screens.PumpAsync(5);
        }
    }

    private static BsonDocument Ns(string db, string collection) => new() { { "db", db }, { "collection", collection } };

    private static string[] Roles(BsonDocument doc) =>
        [.. doc["roles"].AsBsonArray.Select(r => $"{r["role"].AsString}@{r["db"].AsString}").Order(StringComparer.Ordinal)];

    private static async Task<BsonDocument> UserDocAsync(IMongoDatabase admin, string name)
    {
        BsonDocument reply = await admin.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "usersInfo", name }, { "showCustomData", true }, { "showAuthenticationRestrictions", true }
        });
        return reply["users"].AsBsonArray.Single().AsBsonDocument;
    }

    private static async Task<BsonDocument> RoleDocAsync(IMongoDatabase admin, string role)
    {
        BsonDocument reply = await admin.RunCommandAsync<BsonDocument>(new BsonDocument { { "rolesInfo", role }, { "showPrivileges", true } });
        return reply["roles"].AsBsonArray.Single().AsBsonDocument;
    }

    private static async Task DropUserQuietlyAsync(IMongoDatabase admin, string name)
    {
        try
        {
            await admin.RunCommandAsync<BsonDocument>(new BsonDocument("dropUser", name));
        }
        catch (MongoCommandException)
        {
            // 已经被测试本身删掉了。
        }
    }
}
