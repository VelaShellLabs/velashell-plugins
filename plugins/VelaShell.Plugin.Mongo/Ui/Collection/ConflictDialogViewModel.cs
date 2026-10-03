using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>冲突的出路。</summary>
internal enum ConflictChoice
{
    /// <summary>放弃我的修改。</summary>
    DiscardMine,

    /// <summary>覆盖服务器版本。</summary>
    Overwrite,

    /// <summary>合并(逐字段选择)。</summary>
    Merge
}

/// <summary>冲突对话框的结论。</summary>
/// <param name="Choice">出路。</param>
/// <param name="Keep">合并时保留"我的"的那些路径。</param>
internal sealed record ConflictResolution(ConflictChoice Choice, IReadOnlySet<string> Keep);

/// <summary>冲突对话框的一行:字段 · 我的 · 服务器(点哪边就保留哪边)。</summary>
internal sealed class ConflictRow : ObservableObject
{

    /// <summary>构造。</summary>
    public ConflictRow(string path, string mine, string server, bool bothChanged)
    {
        Path = path;
        Mine = mine;
        Server = server;
        BothChanged = bothChanged;
        PickMineCommand = new RelayCommand(() => UseMine = true);
        PickServerCommand = new RelayCommand(() => UseMine = false);
    }

    /// <summary>字段路径。</summary>
    public string Path { get; }

    /// <summary>我的值。</summary>
    public string Mine { get; }

    /// <summary>服务器上的值。</summary>
    public string Server { get; }

    /// <summary>两边都改了(橙底,必须选一边)。</summary>
    public bool BothChanged { get; }

    /// <summary>合并时保留我的。</summary>
    public bool UseMine
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(UseServer));
            }
        }
    } = true;

    /// <summary>合并时保留服务器的。</summary>
    public bool UseServer => !UseMine;

    /// <summary>选我的。</summary>
    public RelayCommand PickMineCommand { get; }

    /// <summary>选服务器的。</summary>
    public RelayCommand PickServerCommand { get; }
}

/// <summary>
/// 编辑冲突(设计稿 22):提交时乐观并发检查没过 —— 在我编辑期间别的会话改了同一份文档的同一批字段。
/// 逐字段列出「我的 vs 服务器」,三种出路:放弃我的修改 / 覆盖服务器版本 / 合并(点哪边留哪边)。
/// </summary>
internal sealed class ConflictDialogViewModel : DialogViewModel
{
    private readonly Func<ConflictResolution, Task> _resolve;
    private bool _resolved;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="conflict">冲突。</param>
    /// <param name="resolve">用户选定之后的处理(由集合工作台执行)。</param>
    public ConflictDialogViewModel(IMongoWorkspace workspace, EditConflict conflict, Func<ConflictResolution, Task> resolve)
        : base(workspace)
    {
        Conflict = conflict;
        _resolve = resolve;
        Title = Loc["State_ConflictTitle"];
        BsonDocument original = conflict.Edit.Original;
        BsonDocument? server = conflict.Server;
        Rows =
        [
            .. conflict.Edit.Changes.Select(change =>
            {
                BsonValue? theirs = server is null ? null : BsonPath.Get(server, change.Path);
                bool both = server is not null && !StagedEdit.SameValue(theirs, change.Original);
                return new ConflictRow(change.Path, Show(change.Value), Show(theirs), both);
            })
        ];
        string id = BsonText.Shorten(BsonText.Cell(conflict.Edit.Id));
        if (server is null)
        {
            Message = Loc.Format("State_ConflictDeleted", id);
        }
        else if (original.GetValue("updatedAt", BsonNull.Value) is BsonDateTime before
                 && server.GetValue("updatedAt", BsonNull.Value) is BsonDateTime after && !before.Equals(after))
        {
            Message = Loc.Format("State_ConflictStamp", id, Time(before), Time(after));
        }
        else
        {
            Message = Loc.Format("State_ConflictBody", id);
        }
        List<string> both = [.. Rows.Where(static r => r.BothChanged).Select(static r => r.Path)];
        Hint = both.Count > 0 ? Loc.Format("State_ConflictHint", string.Join("、", both)) : Loc["State_ConflictHintNone"];
        DiscardMineCommand = new AsyncCommand(() => FinishAsync(ConflictChoice.DiscardMine));
        OverwriteCommand = new AsyncCommand(() => FinishAsync(ConflictChoice.Overwrite), () => server is not null);
        MergeCommand = new AsyncCommand(() => FinishAsync(ConflictChoice.Merge), () => server is not null);
    }

    /// <summary>冲突。</summary>
    public EditConflict Conflict { get; }

    /// <inheritdoc />
    public override string IconKey => "Mongo.git-merge";

    /// <inheritdoc />
    public override string IconToken => "VelaWarning";

    /// <inheritdoc />
    public override double Width => 500;

    /// <summary>说明(带 updatedAt 前后时刻)。</summary>
    public string Message { get; }

    /// <summary>逐字段对比。</summary>
    public IReadOnlyList<ConflictRow> Rows { get; }

    /// <summary>底下那行橙字(哪些字段两边都改了)。</summary>
    public string Hint { get; }

    /// <summary>放弃我的修改。</summary>
    public AsyncCommand DiscardMineCommand { get; }

    /// <summary>覆盖服务器版本。</summary>
    public AsyncCommand OverwriteCommand { get; }

    /// <summary>合并(按每行的选择)。</summary>
    public AsyncCommand MergeCommand { get; }

    private async Task FinishAsync(ConflictChoice choice)
    {
        if (_resolved)
        {
            return;
        }
        _resolved = true;
        HashSet<string> keep = choice == ConflictChoice.Merge
            ? Rows.Where(static r => r.UseMine).Select(static r => r.Path).ToHashSet(StringComparer.Ordinal)
            : [with(StringComparer.Ordinal)];
        Close();
        await _resolve(new ConflictResolution(choice, keep)).ConfigureAwait(true);
    }

    /// <summary>直接关掉(× / Esc)等于"先不处理":修改留在暂存区,下次提交还会再碰到。</summary>
    internal override void OnClosed()
    {
        _resolved = true;
        base.OnClosed();
    }

    private string Show(BsonValue? value) => value is null
        ? "—"
        : value is BsonDateTime ? BsonText.FormatDate(value) : BsonText.Inline(value, Loc, shortenIds: true);

    private static string Time(BsonDateTime value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.MillisecondsSinceEpoch).ToLocalTime()
            .ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
