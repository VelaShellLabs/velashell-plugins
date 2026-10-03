using System.Globalization;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>暂存、提交、撤销、冲突与工具栏的写操作。</summary>
internal sealed partial class CollectionTabViewModel
{
    private HashSet<BsonValue> _trackedIds = [];
    private bool _committing;
    private DispatcherTimer? _relockTimer;

    /// <summary>添加文档(打开文档编辑器)。</summary>
    public RelayCommand AddDocumentCommand { get; private set; } = null!;

    /// <summary>克隆选中文档(去掉 <c>_id</c> 交给文档编辑器)。</summary>
    public RelayCommand CloneCommand { get; private set; } = null!;

    /// <summary>删除选中(暂存;已暂存删除的再点一次是撤销删除)。</summary>
    public RelayCommand DeleteCommand { get; private set; } = null!;

    /// <summary>导入。</summary>
    public RelayCommand ImportCommand { get; private set; } = null!;

    /// <summary>导出(带上当前查询)。</summary>
    public RelayCommand ExportCommand { get; private set; } = null!;

    /// <summary>选中文档按当前 EJSON 写法复制。</summary>
    public AsyncCommand CopyJsonCommand { get; private set; } = null!;

    /// <summary>底栏「+」:暂存一行新文档。</summary>
    public RelayCommand AddRowCommand { get; private set; } = null!;

    /// <summary>应用(提交暂存区,Ctrl+S)。</summary>
    public AsyncCommand ApplyCommand { get; private set; } = null!;

    /// <summary>放弃(Esc)。</summary>
    public RelayCommand DiscardCommand { get; private set; } = null!;

    /// <summary>只读横幅「解锁写入 15 分钟」。</summary>
    public AsyncCommand UnlockCommand { get; private set; } = null!;

    private void InitializeEditCommands()
    {
        AddDocumentCommand = new(OpenNewDocument);
        CloneCommand = new(() =>
        {
            if (SelectedRow is { } row)
            {
                Clone(row);
            }
        });
        DeleteCommand = new(DeleteGridSelection);
        ImportCommand = new(() =>
        {
            if (EnsureCanWrite())
            {
                Workspace.ShowDialog(new ImportWizardViewModel(Workspace, Database, CollectionName));
            }
        });
        ExportCommand = new(() => Workspace.ShowDialog(new ExportWizardViewModel(Workspace, Database, CollectionName, _lastRequest, null)));
        CopyJsonCommand = new(() => CopyDocumentsAsync(SelectedRows));
        AddRowCommand = new(() =>
        {
            if (_drill is { } drill && _viewMode == CollectionViewMode.Grid)
            {
                AddDrillRow(drill);
            }
            else
            {
                AddRow();
            }
        });
        ApplyCommand = new(() => CommitAsync(CommitScope.All));
        DiscardCommand = new(DiscardAll);
        UnlockCommand = new(UnlockAsync);
        InitializeDrillCommands();
    }

    // ── 待提交 ───────────────────────────────────────────────────────────────

    /// <summary>有没有待提交的修改。</summary>
    public bool HasPending => !Staging.IsEmpty;

    /// <summary>
    /// 底栏的「待提交:1 修改 · 1 新增 · 1 删除」;JSON 视图里只有字段修改时换成
    /// 「待提交:文档 #3 · #4 共 3 处修改」(那里看的是一份份文档,不是行)。
    /// </summary>
    public string PendingText
    {
        get
        {
            if (Staging.IsEmpty)
            {
                return Loc["Cw_PendingNone"];
            }
            if (_viewMode == CollectionViewMode.Json && Staging.InsertCount == 0 && Staging.DeleteCount == 0)
            {
                IEnumerable<string> numbers = Staging.Edits
                    .Select(e => _rows.FirstOrDefault(r => r.Insert is null && e.Id.Equals(r.Id))?.Number)
                    .Where(static n => n is not null)
                    .Take(4)
                    .Select(static n => "#" + n!.Value.ToString(CultureInfo.InvariantCulture));
                return Loc.Format("Cw_PendingDocs", string.Join(" · ", numbers), Staging.FieldChangeCount);
            }
            var parts = new List<string>(3);
            if (Staging.EditCount > 0)
            {
                parts.Add(Loc.Format("Cw_PendingEdits", Staging.EditCount));
            }
            if (Staging.InsertCount > 0)
            {
                parts.Add(Loc.Format("Cw_PendingInserts", Staging.InsertCount));
            }
            if (Staging.DeleteCount > 0)
            {
                parts.Add(Loc.Format("Cw_PendingDeletes", Staging.DeleteCount));
            }
            return Loc["Cw_PendingPrefix"] + string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 暂存区变了:只重算受影响的行(暂存过的 <c>_id</c> 与上一次暂存过的 <c>_id</c>),
    /// 新增行增删,树 / 卡片 / 检查器跟着刷新。5 万行的页里改一格不该把 5 万行都过一遍。
    /// </summary>
    private void OnStagingChanged()
    {
        IsModified = !Staging.IsEmpty;
        HashSet<BsonValue> now = [.. Staging.Edits.Select(static e => e.Id), .. Staging.Deletes.Select(static d => d.Id)];
        HashSet<BsonValue> affected = [.. now, .. _trackedIds];
        _trackedIds = now;
        if (affected.Count > 0)
        {
            foreach (CollectionRow row in _rows)
            {
                if (row.Insert is null && row.Id is { } id && affected.Contains(id))
                {
                    row.Recompute();
                }
            }
        }
        SyncInsertRows();
        RefreshDrill();
        RebuildTree();
        foreach (JsonCardViewModel card in Cards)
        {
            if (card.Row.Insert is not null || card.Row.Id is { } id && affected.Contains(id))
            {
                card.Refresh();
            }
        }
        Inspector.Reload();
        RaisePropertiesChanged(nameof(HasPending), nameof(PendingText), nameof(IsEmptyCollection), nameof(IsNoResult), nameof(ShowData));
        UpdateStatus();
    }

    /// <summary>暂存的新文档与网格末尾的新增行对齐。</summary>
    private void SyncInsertRows()
    {
        var staged = Staging.Inserts.ToHashSet();
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (_rows[i].Insert is { } insert && !staged.Contains(insert))
            {
                if (ReferenceEquals(_rows[i], _selectedRow))
                {
                    SelectedRow = null;
                }
                _rows.RemoveAt(i);
            }
        }
        var present = _rows.Where(static r => r.Insert is not null).Select(static r => r.Insert!).ToHashSet();
        foreach (StagedInsert insert in Staging.Inserts)
        {
            if (!present.Contains(insert))
            {
                _rows.Add(new CollectionRow(this, 0, insert));
            }
        }
        foreach (CollectionRow row in _rows.Where(static r => r.Insert is not null))
        {
            row.Recompute();
        }
    }

    // ── 写入口 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 写之前的检查:视图一律不能写;其余交给 <see cref="IMongoWorkspace.EnsureWritable" />
    /// (只读模式 / 没权限时它会弹出"怎么解锁"的提示)。暂存这一步就拦,
    /// 而不是等提交时才说不行 —— 在只读连接上攒了一堆改动最后提交不了,比一开始就告诉他更糟。
    /// </summary>
    internal bool EnsureCanWrite()
    {
        if (!Info.IsEditable)
        {
            Workspace.Toast(new() { Title = Loc["Cw_ViewReadOnly"], Kind = ToastKind.Warning });
            return false;
        }
        return Workspace.EnsureWritable(Database);
    }

    /// <summary>暂存一个字段的新值(<paramref name="value" /> 为 null 即删除字段)。</summary>
    internal bool Stage(CollectionRow row, string path, BsonValue? value)
    {
        if (path == "_id" && row.Insert is null)
        {
            Workspace.Toast(new() { Title = Loc["Cw_IdImmutable"], Kind = ToastKind.Warning });
            return false;
        }
        if (!EnsureCanWrite() || row.IsDeleted)
        {
            return false;
        }
        if (row.Insert is { } insert)
        {
            Staging.SetInsertField(insert, path, value);
        }
        else if (row.Original is { } original)
        {
            Staging.SetField(original, path, value);
        }
        return true;
    }

    /// <summary>暂存删除(或撤销暂存的删除;新增行直接拿掉)。</summary>
    internal void StageDelete(IReadOnlyList<CollectionRow> rows)
    {
        if (rows.Count == 0 || !EnsureCanWrite())
        {
            return;
        }
        List<CollectionRow> existing = [.. rows.Where(static r => r.Insert is null && r.Original is not null)];
        bool undo = existing.Count > 0 && existing.All(r => Staging.IsDeleted(r.Id));
        foreach (CollectionRow row in rows.ToList())
        {
            if (row.Insert is { } insert)
            {
                Staging.RemoveInsert(insert);
            }
            else if (undo)
            {
                Staging.UnmarkDeleted(row.Id!);
            }
            else
            {
                Staging.MarkDeleted(row.Original!);
            }
        }
    }

    /// <summary>撤掉一份文档上的全部暂存(检查器「放弃」)。</summary>
    internal void DiscardDocument(CollectionRow row)
    {
        if (row.Insert is { } insert)
        {
            Staging.RemoveInsert(insert);
        }
        else if (row.Id is { } id)
        {
            Staging.Discard(id);
        }
    }

    /// <summary>底栏「+」:按当前列给一份空模板(数值 0、对象 {}、数组 [],日期取现在),排在网格末尾。</summary>
    private void AddRow()
    {
        if (!EnsureCanWrite())
        {
            return;
        }
        var doc = new BsonDocument();
        foreach (CollectionColumn column in _columns.Where(static c => c.Name != "_id"))
        {
            doc[column.Name] = column.Kind is BsonKind.Null or BsonKind.Missing or BsonKind.Binary or BsonKind.Regex
                or BsonKind.Timestamp or BsonKind.Other or BsonKind.ObjectId
                ? BsonNull.Value
                : BsonEdit.Empty(column.Kind);
        }
        StagedInsert insert = Staging.AddInsert(doc);
        if (_rows.FirstOrDefault(r => ReferenceEquals(r.Insert, insert)) is { } row)
        {
            SelectedRow = row;
            RowAdded?.Invoke(row);
        }
    }

    /// <summary>新增了一行(视图滚到它)。</summary>
    public event Action<CollectionRow>? RowAdded;

    private void OpenNewDocument()
    {
        if (EnsureCanWrite())
        {
            Workspace.ShowDialog(new DocumentEditorDialogViewModel(Workspace, Info, null, OnEditorSavedAsync));
        }
    }

    /// <summary>克隆:去掉 <c>_id</c> 交给文档编辑器(保存即插入一份新的)。</summary>
    internal void Clone(CollectionRow row)
    {
        if (!EnsureCanWrite())
        {
            return;
        }
        var copy = (BsonDocument)row.Document.DeepClone();
        copy.Remove("_id");
        Workspace.ShowDialog(new DocumentEditorDialogViewModel(Workspace, Info, copy, OnEditorSavedAsync));
    }

    /// <summary>在文档编辑器中编辑(编辑器直接写服务器;这份文档在这里的暂存随之作废)。</summary>
    internal void OpenInEditor(CollectionRow row)
    {
        if (!Info.IsEditable)
        {
            Workspace.Toast(new() { Title = Loc["Cw_ViewReadOnly"], Kind = ToastKind.Warning });
            return;
        }
        BsonDocument document = (BsonDocument)row.Document.DeepClone();
        Workspace.ShowDialog(new DocumentEditorDialogViewModel(Workspace, Info, document, async saved =>
        {
            DiscardDocument(row);
            await OnEditorSavedAsync(saved).ConfigureAwait(true);
        }));
    }

    private async Task OnEditorSavedAsync(BsonDocument? saved)
    {
        await RunQueryAsync(resetPage: false).ConfigureAwait(true);
        if (saved?.GetValue("_id", BsonNull.Value) is { IsBsonNull: false } id
            && _rows.FirstOrDefault(r => id.Equals(r.Id)) is { } row)
        {
            SelectedRow = row;
        }
    }

    // ── 复制 / 筛选 ──────────────────────────────────────────────────────────

    /// <summary>选中文档按当前写法复制(多份写成数组)。</summary>
    internal async Task CopyDocumentsAsync(IReadOnlyList<CollectionRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }
        string text = rows.Count == 1
            ? BsonText.Pretty(rows[0].Document, _ejson)
            : "[\n" + string.Join(",\n", rows.Select(r => Indent(BsonText.Pretty(r.Document, _ejson)))) + "\n]";
        await Workspace.CopyAsync(text).ConfigureAwait(true);
        Workspace.Toast(new() { Title = Loc.Format("Cw_CopiedDocs", rows.Count), Kind = ToastKind.Success });

        static string Indent(string block) => "  " + block.Replace("\n", "\n  ", StringComparison.Ordinal);
    }

    /// <summary>复制一个值(字符串复制原文,其余复制 mongosh 字面量)。</summary>
    internal async Task CopyValueAsync(BsonValue? value)
    {
        string text = value switch
        {
            null => "",
            BsonString s => s.Value,
            _ => BsonText.Literal(value)
        };
        await Workspace.CopyAsync(text).ConfigureAwait(true);
        Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success, Duration = TimeSpan.FromSeconds(2) });
    }

    /// <summary>复制路径。</summary>
    internal async Task CopyTextAsync(string text)
    {
        await Workspace.CopyAsync(text).ConfigureAwait(true);
        Workspace.Toast(new() { Title = Loc["Common_Copied"], Kind = ToastKind.Success, Duration = TimeSpan.FromSeconds(2) });
    }

    /// <summary>复制为 JSON(当前写法,多行)。</summary>
    internal Task CopyValueAsJsonAsync(BsonValue? value) =>
        CopyTextAsync(value is null ? "" : BsonText.Pretty(value, _ejson));

    /// <summary>「按此值筛选」生成的筛选文本(右键菜单底部的预览也用它)。</summary>
    internal static string FilterFor(string path, BsonValue? value)
    {
        string key = BsonText.FieldName(path);
        string literal = value is null ? "{ $exists: false }" : BsonText.Literal(value);
        return $"{{ {key}: {literal} }}";
    }

    /// <summary>按此值筛选。</summary>
    internal Task FilterByAsync(string path, BsonValue? value)
    {
        FilterText = FilterFor(path, value);
        return RunQueryAsync(resetPage: true);
    }

    /// <summary>在新查询中打开(按这个值查)。</summary>
    internal void OpenInQuery(string path, BsonValue? value) =>
        Workspace.OpenQuery(Database, $"db.{MongoWorkspaceViewModel.ShellCollectionRef(CollectionName)}.find({FilterFor(path, value)})");

    // ── 字段级编辑(网格 / 树 / 检查器共用) ───────────────────────────────────

    /// <summary>
    /// 编辑器的初始状态:类型(null / 缺失时按列的主导类型)、文本与候选值。
    /// 日期不给候选值 —— 它的按钮弹的是日历(<see cref="DatePickFlyout" />),「现在」在日历里。
    /// </summary>
    internal InlineValueEditor CreateEditor(BsonValue? value, string path, BsonKind fallback)
    {
        BsonKind kind = BsonKinds.Of(value);
        if (kind is BsonKind.Missing or BsonKind.Null)
        {
            kind = fallback is BsonKind.Missing or BsonKind.Null ? BsonKind.String : fallback;
        }
        IReadOnlyList<string> choices = kind switch
        {
            BsonKind.Boolean => ["true", "false"],
            BsonKind.String => EnumChoices(path),
            _ => []
        };
        return new InlineValueEditor(BsonEdit.EditText(value), kind, choices);
    }

    /// <summary>
    /// 字符串列的候选值:抽样里取值不多的(<c>status</c>),或本页里不超过 8 种取值的。
    /// 不给候选就是普通文本框 —— 单号、名字这类列给一列"候选"只会碍事。
    /// </summary>
    private IReadOnlyList<string> EnumChoices(string path)
    {
        IReadOnlyList<string> sampled = _sample.EnumValuesOf(path);
        if (sampled.Count > 0)
        {
            return sampled;
        }
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (BsonDocument doc in _documents)
        {
            if (BsonPath.Get(doc, path) is BsonString s)
            {
                distinct.Add(s.Value);
                if (distinct.Count > 8)
                {
                    return [];
                }
            }
        }
        return distinct.Count >= 2 && _documents.Count >= distinct.Count * 3 ? [.. distinct.Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>
    /// 按编辑器解析文本并暂存。空文本对非字符串类型视为 null(清空一格的直觉意思)。
    /// 解析失败把原因挂在编辑器上(红框 + 提示),返回 <see langword="false" />,编辑器不关。
    /// </summary>
    internal bool CommitEditor(InlineValueEditor editor, CollectionRow row, string path)
    {
        BsonValue value;
        if (editor.Text.Length == 0 && editor.Kind != BsonKind.String)
        {
            value = BsonNull.Value;
        }
        else if (!BsonEdit.TryParse(editor.Text, editor.Kind, out value, out string? error))
        {
            editor.Error = Loc[error ?? "Edit_BadLiteral"];
            return false;
        }
        BsonValue? current = BsonPath.Get(row.Document, path);
        if (current is not null && StagedEdit.SameValue(current, value))
        {
            return true;
        }
        return Stage(row, path, value);
    }

    /// <summary>网格:开始编辑一格(双击 / Enter / F2)。</summary>
    internal void BeginCellEdit(CollectionCell cell)
    {
        if (cell.Row.IsDeleted)
        {
            return;
        }
        if (cell.Path == "_id" && cell.Row.Insert is null)
        {
            Workspace.Toast(new() { Title = Loc["Cw_IdImmutable"], Kind = ToastKind.Warning });
            return;
        }
        if (!EnsureCanWrite())
        {
            return;
        }
        if (!ReferenceEquals(GridSelectedRow, cell.Row))
        {
            GridSelectedRow = cell.Row;
        }
        CurrentColumn = cell.Column;
        foreach (CollectionCell other in cell.Row.Cells.Where(static c => c.IsEditing))
        {
            other.Editor = null;
        }
        cell.Editor = CreateEditor(cell.Value, cell.Path, cell.Column.Kind);
    }

    /// <summary>网格:提交一格的编辑(写入暂存区);解析失败返回 false,编辑器留着。</summary>
    internal bool CommitCellEdit(CollectionCell cell)
    {
        if (cell.Editor is not { } editor || !CommitEditor(editor, cell.Row, cell.Path))
        {
            return false;
        }
        cell.Editor = null;
        return true;
    }

    /// <summary>网格:取消一格的编辑。</summary>
    internal static void CancelCellEdit(CollectionCell cell) => cell.Editor = null;

    /// <summary>换类型(检查器 / 树的类型菜单):值按 <see cref="BsonEdit.Convert" /> 换算,换不了就给新类型的空值并提示。</summary>
    internal void ChangeType(CollectionRow row, string path, BsonKind kind)
    {
        BsonValue? current = BsonPath.Get(row.Document, path);
        BsonValue converted = BsonEdit.Convert(current, kind, out bool reset);
        if (Stage(row, path, converted) && reset)
        {
            Workspace.Toast(new() { Title = Loc["Edit_ValueReset"], Kind = ToastKind.Warning });
        }
    }

    /// <summary>删除字段;数组元素按下标删掉并前移(<c>$unset</c> 数组元素只会留下一个 null)。</summary>
    internal void DeleteField(CollectionRow row, string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot > 0 && BsonPath.Get(row.Document, path[..dot]) is BsonArray array
            && int.TryParse(path[(dot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < array.Count)
        {
            var copy = (BsonArray)array.DeepClone();
            copy.RemoveAt(index);
            Stage(row, path[..dot], copy);
            return;
        }
        Stage(row, path, null);
    }

    /// <summary>在数组末尾插入一个元素(类型照第一项;文档元素给同样的键、空值)。</summary>
    internal void InsertArrayElement(CollectionRow row, string arrayPath)
    {
        if (BsonPath.Get(row.Document, arrayPath) is not BsonArray array)
        {
            return;
        }
        BsonValue element = array.Count == 0
            ? new BsonString("")
            : array[0] is BsonDocument template
                ? new BsonDocument(template.Elements.Select(static e => new BsonElement(e.Name, BsonEdit.Empty(BsonKinds.Of(e.Value)))))
                : BsonEdit.Empty(BsonKinds.Of(array[0]));
        Stage(row, $"{arrayPath}.{array.Count.ToString(CultureInfo.InvariantCulture)}", element);
    }

    /// <summary>添加字段(弹一个小对话框问名字、类型、值)。</summary>
    internal void PromptAddField(CollectionRow row, string parentPath)
    {
        if (!EnsureCanWrite())
        {
            return;
        }
        Workspace.ShowDialog(new AddStagedFieldDialogViewModel(Workspace, row.Document, parentPath, (path, value) => Stage(row, path, value)));
    }

    // ── 提交 / 撤销 / 冲突 ────────────────────────────────────────────────────

    /// <summary>
    /// 提交。删除在「写前确认」打开时要确认(生产连接还要手打集合名);成功后右下角
    /// 「已提交 N 处修改 · 撤销 (9)」;冲突逐份弹冲突对话框;写失败的留在暂存区并报原因。
    /// </summary>
    internal async Task CommitAsync(CommitScope scope)
    {
        if (Staging.IsEmpty || _committing || !EnsureCanWrite())
        {
            return;
        }
        IReadOnlyList<StagedOperation> ops = StagingCommitter.Plan(Staging, scope);
        if (ops.Count == 0)
        {
            return;
        }
        int deletes = ops.Count(static o => o is DeleteOperation);
        if (deletes > 0 && Workspace.Guard.ConfirmWrites)
        {
            bool confirmed = await Workspace.ConfirmAsync(new()
            {
                Title = Loc["State_DeleteDocsTitle"],
                Message = Loc.Format("State_DeleteDocsBody", deletes, Info.Namespace),
                ConfirmLabel = Loc.Format("State_DeleteDocsConfirm", deletes),
                IconKey = "Mongo.trash-2",
                Danger = true,
                TypeToConfirm = Workspace.Guard.IsProduction ? CollectionName : null,
                Facts =
                [
                    new(Loc["State_FactUpdates"], BsonText.Grouped(ops.Count(static o => o is UpdateOperation))),
                    new(Loc["State_FactInserts"], BsonText.Grouped(ops.Count(static o => o is InsertOperation))),
                    new(Loc["State_FactDeletes"], BsonText.Grouped(deletes))
                ]
            }).ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
        }
        _committing = true;
        ApplyCommand.RaiseCanExecuteChanged();
        try
        {
            CommitOutcome outcome = await StagingCommitter.CommitAsync(Collection, Staging, scope, _lifetime.Token).ConfigureAwait(true);
            if (outcome.Applied > 0)
            {
                UndoPlan undo = outcome.Undo;
                Workspace.Toast(new()
                {
                    Title = Loc.Format("State_Committed", outcome.Applied),
                    Detail = CommitDetail(outcome),
                    Kind = ToastKind.Success,
                    ActionLabel = undo.Count > 0 ? Loc["State_Undo"] : null,
                    Action = undo.Count > 0 ? () => UndoAsync(undo) : null,
                    Duration = TimeSpan.FromSeconds(10)
                });
            }
            if (outcome.Failures.Count > 0)
            {
                Workspace.Toast(new()
                {
                    Title = Loc.Format("State_CommitFailed", outcome.Failures.Count),
                    Detail = outcome.Failures[0].Message,
                    Kind = ToastKind.Error,
                    Duration = TimeSpan.FromSeconds(8)
                });
            }
            await RunQueryAsync(resetPage: false).ConfigureAwait(true);
            if (outcome.Conflicts.Count > 0)
            {
                ShowConflicts(outcome.Conflicts);
            }
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            _committing = false;
            ApplyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary><c>1 更新 · 1 插入 · 1 删除 · 12 ms</c>。</summary>
    private string CommitDetail(CommitOutcome outcome)
    {
        var parts = new List<string>(4);
        if (outcome.Updated > 0)
        {
            parts.Add(Loc.Format("State_DetailUpdated", outcome.Updated));
        }
        if (outcome.Inserted > 0)
        {
            parts.Add(Loc.Format("State_DetailInserted", outcome.Inserted));
        }
        if (outcome.Deleted > 0)
        {
            parts.Add(Loc.Format("State_DetailDeleted", outcome.Deleted));
        }
        parts.Add(Loc.Format("Common_Ms", Ms(outcome.Elapsed)));
        return string.Join(" · ", parts);
    }

    /// <summary>撤销刚才那一批(10 秒内):用提交前的原像字段级回滚。</summary>
    private async Task UndoAsync(UndoPlan plan)
    {
        if (!EnsureCanWrite())
        {
            return;
        }
        try
        {
            (int restored, int skipped) = await plan.ExecuteAsync(Collection, _lifetime.Token).ConfigureAwait(true);
            Workspace.Toast(new()
            {
                Title = skipped == 0 ? Loc.Format("State_UndoDone", restored) : Loc.Format("State_UndoPartial", restored, skipped),
                Kind = skipped == 0 ? ToastKind.Success : ToastKind.Warning
            });
            await RunQueryAsync(resetPage: false).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new() { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
    }

    /// <summary>逐份弹冲突对话框(处理完一份再弹下一份)。</summary>
    private void ShowConflicts(IReadOnlyList<EditConflict> conflicts)
    {
        var queue = new Queue<EditConflict>(conflicts);
        Next();

        void Next()
        {
            if (queue.TryDequeue(out EditConflict? conflict))
            {
                Workspace.ShowDialog(new ConflictDialogViewModel(Workspace, conflict, async resolution =>
                {
                    await ResolveConflictAsync(conflict, resolution).ConfigureAwait(true);
                    Next();
                }));
            }
        }
    }

    /// <summary>
    /// 冲突的三种出路:放弃我的(撤掉暂存、看服务器版本);覆盖(以服务器版本为新原像、我的修改全保留,再提交);
    /// 合并(以服务器版本为新原像、只保留勾了「我的」的那几个字段,再提交)。
    /// 覆盖与合并都是**重新走一遍乐观并发**:在我看冲突的这几秒里服务器又变了,照样会再被拦下来。
    /// </summary>
    private async Task ResolveConflictAsync(EditConflict conflict, ConflictResolution resolution)
    {
        BsonValue id = conflict.Edit.Id;
        switch (resolution.Choice)
        {
            case ConflictChoice.DiscardMine:
                Staging.Discard(id);
                await RunQueryAsync(resetPage: false).ConfigureAwait(true);
                return;
            case ConflictChoice.Overwrite when conflict.Server is not null:
                Staging.Rebase(id, conflict.Server, conflict.Edit.Changes.Select(static c => c.Path).ToHashSet(StringComparer.Ordinal));
                break;
            case ConflictChoice.Merge when conflict.Server is not null:
                Staging.Rebase(id, conflict.Server, resolution.Keep);
                break;
            default:
                return;
        }
        if (Staging.EditOf(id) is null)
        {
            await RunQueryAsync(resetPage: false).ConfigureAwait(true);
            return;
        }
        await CommitAsync(new CommitScope(id)).ConfigureAwait(true);
    }

    /// <summary>放弃全部(Esc)。不碰服务器,所以给一个「撤销」把暂存区原样恢复。</summary>
    private void DiscardAll()
    {
        if (Staging.IsEmpty)
        {
            return;
        }
        StagingArea.Snapshot snapshot = Staging.TakeSnapshot();
        Staging.Clear();
        Workspace.Toast(new()
        {
            Title = Loc.Format("State_Discarded", snapshot.OperationCount),
            Kind = ToastKind.Info,
            ActionLabel = Loc["State_Undo"],
            Action = () =>
            {
                Staging.Restore(snapshot);
                return Task.CompletedTask;
            },
            Duration = TimeSpan.FromSeconds(8)
        });
    }

    // ── 只读解锁 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 「解锁写入 15 分钟」:确认一次后关掉只读,15 分钟后自动恢复。限时而不是一直开着,
    /// 是因为"解锁了忘了锁回去"正是生产事故最常见的前奏。
    /// </summary>
    private async Task UnlockAsync()
    {
        if (!await Workspace.ConfirmAsync(new()
            {
                Title = Loc["State_UnlockTitle"],
                Message = Loc.Format("State_UnlockBody", Workspace.ConnectionName),
                ConfirmLabel = Loc["State_UnlockConfirm"],
                IconKey = "Mongo.lock-open",
                Danger = Workspace.Guard.IsProduction
            }).ConfigureAwait(true))
        {
            return;
        }
        Workspace.Guard.IsReadOnly = false;
        _relockTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        timer.Tick += (_, _) => Relock();
        _relockTimer = timer;
        timer.Start();
        Workspace.Toast(new() { Title = Loc["State_Unlocked"], Kind = ToastKind.Warning });
    }

    private void Relock()
    {
        _relockTimer?.Stop();
        _relockTimer = null;
        if (!Workspace.Guard.IsReadOnly)
        {
            Workspace.Guard.IsReadOnly = true;
            Workspace.Toast(new() { Title = Loc["State_Relocked"], Kind = ToastKind.Info });
        }
    }
}
