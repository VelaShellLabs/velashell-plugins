using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>
/// 查询编辑器 · 执行目标(Navicat 查询窗口工具行上的「连接 ▾ 数据库 ▾」):语句在哪条连接、哪个库上跑。
/// <para>
/// 连接定死在标签上(<see cref="WorkspaceTab.Workspace" /> 在构造时给定,标签按连接分组、断开时随连接关闭),
/// 所以**切换连接 = 原位换一个属于新连接的查询标签**:文本、未保存基线、光标、maxTimeMS 与右侧页签带过去,
/// 旧标签的结果是旧连接上的,不带。没连上的连接先安静地连(不开占位标签、不动对象树)。
/// </para>
/// <para>
/// 库是标签自己的状态:下拉改它,脚本里的 <c>use</c> 也改它。集合永远按 <c>db.名</c> 在**生效的库**里解析 ——
/// 语句之前最后一条 <c>use</c>,没有就是下拉里的库;那个库里没有这个集合时,集合名下画橙色波浪线
/// (插入类语句除外:往不存在的集合里插就是新建它)。
/// </para>
/// </summary>
internal sealed partial class QueryTabViewModel
{
    /// <summary>往不存在的集合里调也正常的方法(插入即新建集合),不报"库里没有这个集合"。</summary>
    private static readonly HashSet<string> CreatingMethods = new(StringComparer.Ordinal)
    {
        "insertOne", "insertMany", "insert", "bulkWrite", "createIndex", "createIndexes", "ensureIndex"
    };

    private string? _switchingTo;
    private int _collectionCheck;

    /// <summary>连接下拉上的名字(这个标签所属的连接)。</summary>
    public string ConnectionName => Workspace.ConnectionName;

    /// <summary>正在切过去的连接名(连接下拉上转圈、写「正在连接 X…」);没在切为 <see langword="null" />。</summary>
    public string? SwitchingTo
    {
        get => _switchingTo;
        private set
        {
            if (SetProperty(ref _switchingTo, value))
            {
                RaisePropertiesChanged(nameof(IsSwitching), nameof(SwitchingText));
            }
        }
    }

    /// <summary>正在切换连接。</summary>
    public bool IsSwitching => _switchingTo is not null;

    /// <summary>连接下拉在切换期间的字。</summary>
    public string SwitchingText => _switchingTo is null ? "" : Loc.Format("Query_SwitchingTo", _switchingTo);

    /// <summary>工具行右侧那句「在 mongo-inner-01 / shop 上执行」。</summary>
    public string TargetText => Loc.Format("Query_TargetHint", ConnectionName, _database);

    /// <summary>切换连接(连接下拉的菜单项)。</summary>
    public AsyncCommand<ConnectionEntry> SwitchConnectionCommand { get; private set; } = null!;

    /// <summary>
    /// 连接下拉的候选:插件里全部已保存的连接(连着的、没连的都列,Navicat 也是这样)。
    /// 单测的假工作台不是 <see cref="MongoSession" />,只有当前这一条,列表为空。
    /// </summary>
    internal IReadOnlyList<ConnectionEntry> ConnectionChoices =>
        Workspace is MongoSession session ? [.. session.Shell.Connections] : [];

    /// <summary>是不是这个标签所属的连接(菜单里打勾)。</summary>
    internal bool IsCurrentConnection(ConnectionEntry entry) => ReferenceEquals((Workspace as MongoSession)?.Entry, entry);

    private void InitializeTarget() =>
        SwitchConnectionCommand = new AsyncCommand<ConnectionEntry>(async entry => await SwitchConnectionAsync(entry).ConfigureAwait(true));

    /// <summary>
    /// 切到另一条连接:没连上先连;库名在那边也有就沿用,没有就用那条连接的默认库(提示里写明换了库)。
    /// 返回换上去的新标签;没切(同一条、正在跑、没连上)为 <see langword="null" />。
    /// </summary>
    internal async Task<QueryTabViewModel?> SwitchConnectionAsync(ConnectionEntry target)
    {
        if (Workspace is not MongoSession current || ReferenceEquals(current.Entry, target) || IsSwitching)
        {
            return null;
        }
        if (IsRunning)
        {
            Workspace.Toast(new ToastRequest { Title = Loc["Query_SwitchWhileRunning"], Kind = ToastKind.Info });
            return null;
        }
        MongoWorkspaceViewModel shell = current.Shell;
        if (target.Session is null)
        {
            SwitchingTo = target.Name;
            try
            {
                await shell.ConnectAsync(target, quiet: true).ConfigureAwait(true);
            }
            finally
            {
                SwitchingTo = null;
            }
            if (target.Session is null)
            {
                Workspace.Toast(new ToastRequest
                {
                    Title = Loc.Format("Query_SwitchFailed", target.Name),
                    Detail = target.Failure?.Message,
                    Kind = ToastKind.Error
                });
                return null;
            }
        }
        MongoSession next = target.Session;
        bool keepDatabase = next.Databases.Contains(_database, StringComparer.Ordinal) || _database is "admin" or "config" or "local";
        string database = keepDatabase ? _database : next.DefaultDatabase() ?? next.Databases.FirstOrDefault() ?? "test";
        var replacement = new QueryTabViewModel(next, database, _text, run: false, next.NextQueryNumber());
        replacement.CarryOver(this);
        shell.ReplaceTab(this, replacement);
        replacement.Workspace.Toast(new ToastRequest
        {
            Title = Loc.Format("Query_SwitchedTo", target.Name, database),
            Detail = keepDatabase ? null : Loc.Format("Query_SwitchedDbMissing", target.Name, _database, database),
            Kind = keepDatabase ? ToastKind.Success : ToastKind.Info
        });
        return replacement;
    }

    /// <summary>
    /// 从被换掉的标签带过来的状态。保存的查询按连接存,新连接上没有那一条 —— 保存名不带,未保存基线带
    /// (切换前就有未保存的修改,切过去照样是未保存)。
    /// </summary>
    private void CarryOver(QueryTabViewModel from)
    {
        _baseline = from._baseline;
        IsModified = _text != _baseline;
        MaxTimeMs = from.MaxTimeMs;
        CaretOffset = from.CaretOffset;
        HelperTab = from.HelperTab;
        ResultView = from.ResultView;
        UpdateStatus();
    }

    /// <summary>
    /// 诊断的第二段(异步):每条 <c>db.集合.方法(…)</c> 的集合在它生效的库里有没有。
    /// 光标所在的那条不查(还在敲);库的集合列表拿不到(没权限、超时)就不下结论;<c>system.*</c> 不查。
    /// </summary>
    private async Task CheckCollectionsAsync(IReadOnlyList<ShellStatement> statements, IReadOnlyList<EditorDiagnostic> found)
    {
        int check = ++_collectionCheck;
        try
        {
            await CheckCollectionsCoreAsync(statements, found, check).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoDB.Driver.MongoException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            // 只是提示性的检查:连接断了、标签关了都不该打扰人。
            Workspace.Log.Info($"Checking collection names for the query editor failed: {ex.Message}");
        }
    }

    private async Task CheckCollectionsCoreAsync(IReadOnlyList<ShellStatement> statements, IReadOnlyList<EditorDiagnostic> found, int check)
    {
        var warnings = new List<EditorDiagnostic>();
        foreach (ShellStatement statement in statements)
        {
            if (statement.Contains(_caretOffset))
            {
                continue;
            }
            (string? collection, string? method, string? db) = ShellCompletion.Head(statement.Text);
            if (collection is null || method is null || CreatingMethods.Contains(method)
                || collection.StartsWith("system.", StringComparison.Ordinal))
            {
                continue;
            }
            string database = db ?? DatabaseAt(statement.Offset);
            IReadOnlyList<CollectionInfo> known = await CollectionsAsync(database).ConfigureAwait(true);
            if (check != _collectionCheck)
            {
                return;
            }
            if (!_collections.ContainsKey(database) || known.Any(c => c.Name == collection))
            {
                continue;
            }
            int at = statement.Text.IndexOf(collection, StringComparison.Ordinal);
            warnings.Add(new EditorDiagnostic(statement.Offset + Math.Max(0, at), collection.Length,
                Loc.Format("Query_CollectionMissing", collection, database), DiagnosticSeverity.Warning));
        }
        if (check == _collectionCheck && warnings.Count > 0)
        {
            Diagnostics = [.. found, .. warnings];
        }
    }

    /// <summary>某个库的集合变了(脚本里建删了集合):丢掉它的缓存,下一轮诊断重新列。</summary>
    private void ForgetCollections(string database) => _collections.Remove(database);
}
