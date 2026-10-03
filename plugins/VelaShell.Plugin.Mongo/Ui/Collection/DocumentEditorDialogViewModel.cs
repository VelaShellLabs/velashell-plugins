using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>文档编辑器的三种模式(子标题行的分段)。</summary>
internal enum DocumentEditorMode
{
    /// <summary>表单。</summary>
    Form,

    /// <summary>JSON(mongosh 写法,与表单双向同步)。</summary>
    Json,

    /// <summary>对比(原文档 vs 当前,逐行)。</summary>
    Diff
}

/// <summary>
/// 文档编辑器(设计稿 05:表单 + 实时预览)。
/// <para>
/// 一份文档的编辑过程有三样东西要时刻对得上:表单里的行树(编辑中的文档本身)、
/// 与原文档的差异(决定行的橙 / 绿底与将要发出的 <c>$set</c> / <c>$unset</c>)、
/// 集合的验证规则(决定红底与能不能保存)。每次编辑之后在 UI 线程空闲时统一重算一遍 ——
/// 而不是让每一行各自维护状态:一行是"新增"还是"修改",取决于它在整份文档里的路径,
/// 拖动、改名、换类型都会让它变。
/// </para>
/// <para>
/// 保存只提交变更字段;原文档带 <c>updatedAt</c> 时筛选里一并带上原值做乐观并发 ——
/// 匹配不到就是别人先改了,提示冲突而不是覆盖回去。
/// </para>
/// </summary>
internal sealed class DocumentEditorDialogViewModel : DialogViewModel
{
    /// <summary>"按 Schema 补全"的出现率门槛:三成以上的文档都有、这份没有,才算"缺"。</summary>
    internal const double FillThreshold = 0.3;

    private readonly List<DocumentEditorRow> _roots = [];
    private readonly DocumentEditorTail _tail;
    private readonly HashSet<string> _expanded = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _collapsed = [with(StringComparer.Ordinal)];
    private bool _recomputePending;
    private bool _jsonStale = true;
    private bool _applyingJson;
    private bool _regeneratingJson;
    private DispatcherTimer? _jsonTimer;
    private string? _jsonError;
    private int _modified;
    private int _added;
    private int _removed;
    private int _errors;
    private int _warnings;
    private bool _dirty;
    private bool _closed;
    private DocumentEditorPlan? _plan;
    private DocumentEditorSchema? _schema;
    private Task<DocumentEditorSchema?>? _sampling;
    private string? _sampleError;

    /// <summary>构造。</summary>
    /// <param name="workspace">外壳服务。</param>
    /// <param name="collection">所属集合。</param>
    /// <param name="document">要编辑的文档;<see langword="null" /> = 新建。</param>
    /// <param name="saved">保存成功后回调(参数是写进去的那份文档;删除为 null)—— 集合工作台据此刷新。</param>
    public DocumentEditorDialogViewModel(IMongoWorkspace workspace, CollectionInfo collection, BsonDocument? document, Func<BsonDocument?, Task>? saved)
        : base(workspace)
    {
        Collection = collection;
        // 没有 _id 的文档是**新文档的模板**(网格的「克隆」就这样传进来):按插入处理,
        // 而不是当成一份已存在的文档去 updateOne —— 那会因为筛选里没有 _id 而改不到任何东西。
        bool template = document is not null && !document.Contains("_id");
        // 拷一份:调用方(网格)手里那份文档之后还会被它自己改,原文档必须是打开那一刻的样子。
        Original = template ? null : document?.DeepClone().AsBsonDocument;
        Saved = saved;
        Title = workspace.Loc[!collection.IsEditable ? "Doc_TitleView" : Original is null ? "Doc_TitleNew" : "Doc_TitleEdit"];
        Subtitle = collection.Namespace;
        _tail = new(this);

        SaveCommand = new AsyncCommand(() => SaveAsync(asNew: false), () => CanSave);
        SaveAsNewCommand = new AsyncCommand(() => SaveAsync(asNew: true), () => CanSaveAsNew);
        FillCommand = new AsyncCommand(FillFromSchemaAsync, () => CanEdit);
        AddFieldCommand = new RelayCommand(() => AddFieldAtEnd(), () => CanEdit);
        CopyCommand = new AsyncCommand(CopyAsync);
        ToggleSearchCommand = new RelayCommand(() => IsSearching = !IsSearching);

        BsonDocument initial = Original?.DeepClone().AsBsonDocument ?? new BsonDocument("_id", ObjectId.GenerateNewId());
        if (template)
        {
            _ = initial.AddRange(document!.DeepClone().AsBsonDocument);
        }
        LoadRows(initial, keepExpansion: false);
        _dirty = false;
        Workspace.Guard.Changed += OnGuardChanged;
        RecomputeNow();
    }

    // ── 约定的公开面 ────────────────────────────────────────────────────────

    /// <summary>所属集合。</summary>
    public CollectionInfo Collection { get; }

    /// <summary>原文档(冲突后"基于最新版本重做"会换成服务器上的最新版本)。</summary>
    public BsonDocument? Original { get; private set; }

    /// <summary>保存回调。</summary>
    public Func<BsonDocument?, Task>? Saved { get; }

    /// <inheritdoc />
    public override string IconKey => "Mongo.file-pen-line";

    /// <inheritdoc />
    public override double Width => 1100;

    /// <inheritdoc />
    public override double Height => 708;

    /// <summary>有未保存的修改时 Esc 不关(误触一下就丢掉一屏编辑,代价太大)。</summary>
    public override bool CanCloseWithEscape => !_dirty && !IsBusy;

    // ── 状态 ────────────────────────────────────────────────────────────────

    /// <summary>编辑的是库里已有的文档(否则是新建)。</summary>
    public bool IsExisting => Original is not null;

    /// <summary>视图 / 系统集合:只能看。</summary>
    public bool IsReadOnlyView => !Collection.IsEditable;

    /// <summary>能编辑(不是只读视图)。</summary>
    public bool CanEdit => !IsReadOnlyView;

    /// <summary>正在保存。</summary>
    public bool IsBusy
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaiseSaveState();
            }
        }
    }

    /// <summary>子标题行的 <c>_id</c> 芯片:<c>ObjectId("…")</c>。</summary>
    public string IdText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>当前模式。</summary>
    public DocumentEditorMode Mode
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }
            if (field == DocumentEditorMode.Json)
            {
                LeaveJson();
            }
            field = value;
            if (value == DocumentEditorMode.Json && _jsonStale)
            {
                RegenerateJson();
            }
            RaisePropertiesChanged(nameof(Mode), nameof(IsFormMode), nameof(IsJsonMode), nameof(IsDiffMode));
            RecomputeNow();
        }
    }

    /// <summary>表单模式(分段按钮)。</summary>
    public bool IsFormMode
    {
        get => Mode == DocumentEditorMode.Form;
        set
        {
            if (value)
            {
                Mode = DocumentEditorMode.Form;
            }
        }
    }

    /// <summary>JSON 模式。</summary>
    public bool IsJsonMode
    {
        get => Mode == DocumentEditorMode.Json;
        set
        {
            if (value)
            {
                Mode = DocumentEditorMode.Json;
            }
        }
    }

    /// <summary>对比模式。</summary>
    public bool IsDiffMode
    {
        get => Mode == DocumentEditorMode.Diff;
        set
        {
            if (value)
            {
                Mode = DocumentEditorMode.Diff;
            }
        }
    }

    // ── 查找字段 ────────────────────────────────────────────────────────────

    /// <summary>查找框开着。</summary>
    public bool IsSearching
    {
        get;
        set
        {
            if (SetProperty(ref field, value) && !value)
            {
                Search = "";
            }
        }
    }

    /// <summary>查找的字段名片段(过滤表单行;命中嵌套字段时连同它的祖先一起显示)。</summary>
    public string Search
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                RefreshVisible();
            }
        }
    } = "";

    // ── 表单 ────────────────────────────────────────────────────────────────

    /// <summary>表单里看得见的行(展开、查找之后的扁平列表;末尾是「添加字段」那一格)。</summary>
    public ObservableCollection<object> FormItems { get; } = [];

    /// <summary>落不到具体哪一行上的问题(缺少必填字段、根上的类型…)。</summary>
    public ObservableCollection<string> Issues { get; } = [];

    /// <summary>顶层行(测试与视图的拖动用)。</summary>
    internal IReadOnlyList<DocumentEditorRow> Roots => _roots;

    /// <summary>请视图把焦点放到某一行的字段名上(新加的字段)。</summary>
    public event Action<DocumentEditorRow>? FocusRequested;

    // ── JSON 与对比 ─────────────────────────────────────────────────────────

    /// <summary>JSON 模式的文本(mongosh 写法;改它 = 改表单)。</summary>
    public string JsonText
    {
        get;
        set
        {
            value ??= "";
            if (field == value)
            {
                return;
            }
            field = value;
            RaisePropertyChanged();
            if (_regeneratingJson || !CanEdit)
            {
                return;
            }
            _dirty = true;
            // 边打字边解析会让表单在半截文本上来回跳:停手 300ms 再应用。
            _jsonTimer ??= CreateJsonTimer();
            _jsonTimer.Stop();
            _jsonTimer.Start();
        }
    } = "";

    /// <summary>JSON 的语法诊断。</summary>
    public IReadOnlyList<EditorDiagnostic> JsonDiagnostics
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>对比:原文档。</summary>
    public string DiffLeftText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>对比:当前。</summary>
    public string DiffRightText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>对比:原文档里被删掉的行(红)。</summary>
    public IReadOnlyList<LineMark> DiffLeftMarks
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>对比:当前里新增的行(绿)。</summary>
    public IReadOnlyList<LineMark> DiffRightMarks
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    // ── 预览与命令 ──────────────────────────────────────────────────────────

    /// <summary>实时预览(紧凑的 mongosh 文本)。</summary>
    public string PreviewText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>预览里与原文档不同的行。</summary>
    public IReadOnlyList<LineMark> PreviewMarks
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>将执行的命令名(<c>updateOne</c> / <c>replaceOne</c> / <c>insertOne</c>)。</summary>
    public string CommandName
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>将执行的命令全文。</summary>
    public string CommandText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>命令下面那行说明(只提交变更字段 · 乐观并发…)。</summary>
    public string CommandNote
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    // ── 底栏 ────────────────────────────────────────────────────────────────

    /// <summary><c>1 处修改</c>。</summary>
    public string ModifiedText => Loc.Format("Doc_CountModified", _modified);

    /// <summary>有修改。</summary>
    public bool HasModified => _modified > 0;

    /// <summary><c>1 个新字段</c>。</summary>
    public string AddedText => Loc.Format("Doc_CountAdded", _added);

    /// <summary>有新字段。</summary>
    public bool HasAdded => _added > 0;

    /// <summary><c>1 处删除</c>。</summary>
    public string RemovedText => Loc.Format("Doc_CountRemoved", _removed);

    /// <summary>有删除。</summary>
    public bool HasRemoved => _removed > 0;

    /// <summary><c>1 个错误</c>。</summary>
    public string ErrorText => Loc.Format("Doc_CountErrors", _errors);

    /// <summary>有错误(挡保存)。</summary>
    public bool HasErrors => _errors > 0;

    /// <summary><c>1 个警告</c>(validationAction = warn)。</summary>
    public string WarningText => Loc.Format("Doc_CountWarnings", _warnings);

    /// <summary>有警告。</summary>
    public bool HasWarnings => _warnings > 0;

    /// <summary>顶层字段顺序变了(要整份替换)。</summary>
    public bool HasReordered { get; private set; }

    /// <summary><c>字段顺序已调整</c>。</summary>
    public string ReorderedText => Loc["Doc_Reordered"];

    /// <summary>新文档:<c>新文档 · 9 个字段</c>。</summary>
    public string NewDocumentText => Loc.Format("Doc_NewFields", _roots.Count);

    /// <summary>显示新文档的字段数。</summary>
    public bool ShowNewDocument => !IsExisting;

    /// <summary>有要提交的东西。</summary>
    public bool HasChanges => !IsExisting || _plan is { IsEmpty: false };

    /// <summary>底栏的灰字提示。</summary>
    public string FooterHint =>
        IsReadOnlyView ? Loc["Doc_ViewReadOnly"]
        : HasErrors ? Loc["Doc_FixToSave"]
        : Workspace.Guard.IsReadOnly ? Loc["Doc_ReadOnlyMode"]
        : !HasChanges ? Loc["Doc_NoChanges"]
        : "";

    /// <summary>能保存。</summary>
    public bool CanSave => CanEdit && !IsBusy && _errors == 0 && _jsonError is null && HasChanges;

    /// <summary>能另存为新文档。</summary>
    public bool CanSaveAsNew => CanEdit && !IsBusy && _errors == 0 && _jsonError is null;

    // ── 命令 ────────────────────────────────────────────────────────────────

    /// <summary>保存(Ctrl+S)。</summary>
    public AsyncCommand SaveCommand { get; }

    /// <summary>另存为新文档(去掉 _id 插入)。</summary>
    public AsyncCommand SaveAsNewCommand { get; }

    /// <summary>按 Schema 补全缺失字段。</summary>
    public AsyncCommand FillCommand { get; }

    /// <summary>添加字段(末尾)。</summary>
    public RelayCommand AddFieldCommand { get; }

    /// <summary>复制当前文档(完整的 mongosh 文本)。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <summary>开 / 关查找框。</summary>
    public RelayCommand ToggleSearchCommand { get; }

    // ── 行树 ────────────────────────────────────────────────────────────────

    /// <summary>当前文档(由行树拼出)。</summary>
    public BsonDocument BuildDocument() => DocumentEditorRow.BuildDocument(_roots);

    /// <summary>按路径找行(只穿过对象,不穿过数组)。</summary>
    internal DocumentEditorRow? FindRow(string path)
    {
        IReadOnlyList<DocumentEditorRow> level = _roots;
        DocumentEditorRow? found = null;
        foreach (string segment in path.Split('.'))
        {
            found = level.FirstOrDefault(r => r.Name == segment);
            if (found is null)
            {
                return null;
            }
            level = found.Children;
        }
        return found;
    }

    /// <summary>整份换掉行树(打开、JSON 应用、粘贴、冲突重做);展开状态按路径保留。</summary>
    private void LoadRows(BsonDocument document, bool keepExpansion)
    {
        if (!keepExpansion)
        {
            _expanded.Clear();
            _collapsed.Clear();
        }
        _roots.Clear();
        foreach (BsonElement element in document)
        {
            var row = new DocumentEditorRow(this, element.Name, element.Value, null);
            _roots.Add(row);
            ApplyExpansion(row);
        }
        if (!_applyingJson)
        {
            _jsonStale = true;
        }
        _dirty = true;
        RaisePropertyChanged(nameof(NewDocumentText));
        RefreshVisible();
        ScheduleRecompute();
    }

    /// <summary>
    /// 默认展开:顶层的小对象(customer 那种三五个字段的)展开,数组与深层对象收起 ——
    /// 设计稿就是这样:customer 展开、items 与 tags 收起。用户点过的按路径记住。
    /// </summary>
    private void ApplyExpansion(DocumentEditorRow row)
    {
        // 每行只定一次:之后的结构性编辑(删兄弟、加子项)不该把用户或程序已经定好的展开状态冲掉。
        if (row.IsContainer && !row.ExpansionDecided)
        {
            string path = row.Path;
            row.IsExpanded = _expanded.Contains(path)
                             || (!_collapsed.Contains(path) && row.Kind == BsonKind.Object && row.Depth == 0 && row.Children.Count is > 0 and <= 12);
            row.ExpansionDecided = true;
        }
        foreach (DocumentEditorRow child in row.Children)
        {
            ApplyExpansion(child);
        }
    }

    /// <summary>某一行所在的兄弟列表。</summary>
    private IList<DocumentEditorRow> SiblingsOf(DocumentEditorRow row) => row.Parent?.Children ?? (IList<DocumentEditorRow>)_roots;

    /// <summary>重排表单里看得见的行。按引用增量同步 —— 整表重建会把正在输入的那个框销毁掉。</summary>
    internal void RefreshVisible()
    {
        var visible = new List<object>();
        string query = Search.Trim();
        foreach (DocumentEditorRow root in _roots)
        {
            AddVisible(root, query, visible);
        }
        visible.Add(_tail);
        Sync(FormItems, visible);
    }

    private static void AddVisible(DocumentEditorRow row, string query, List<object> visible)
    {
        if (query.Length > 0)
        {
            bool self = row.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool below = row.Children.Any(c => Matches(c, query));
            if (!self && !below)
            {
                return;
            }
            visible.Add(row);
            if (below || row.IsExpanded)
            {
                foreach (DocumentEditorRow child in row.Children)
                {
                    if (below)
                    {
                        AddVisible(child, query, visible);
                    }
                    else
                    {
                        AddVisible(child, "", visible);
                    }
                }
            }
            return;
        }
        visible.Add(row);
        if (row.IsContainer && row.IsExpanded)
        {
            foreach (DocumentEditorRow child in row.Children)
            {
                AddVisible(child, query, visible);
            }
        }
    }

    private static bool Matches(DocumentEditorRow row, string query) =>
        row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || row.Children.Any(c => Matches(c, query));

    /// <summary>把 <paramref name="target" /> 增量改成 <paramref name="source" /> 的样子(按引用)。</summary>
    internal static void Sync(ObservableCollection<object> target, IReadOnlyList<object> source)
    {
        var keep = new HashSet<object>(source, ReferenceEqualityComparer.Instance);
        for (int k = target.Count - 1; k >= 0; k--)
        {
            if (!keep.Contains(target[k]))
            {
                target.RemoveAt(k);
            }
        }
        for (int i = 0; i < source.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], source[i]))
            {
                continue;
            }
            int existing = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], source[i]))
                {
                    existing = j;
                    break;
                }
            }
            if (existing >= 0)
            {
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, source[i]);
            }
        }
    }

    // ── 行操作(由行与视图调用)──────────────────────────────────────────────

    /// <summary>一行被编辑了。<paramref name="structural" /> = 行数或展开形态变了。</summary>
    internal void OnEdited(DocumentEditorRow row, bool structural)
    {
        _dirty = true;
        if (!_applyingJson)
        {
            _jsonStale = true;
        }
        for (DocumentEditorRow? ancestor = row.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.RaiseShape();
        }
        if (structural)
        {
            // 新建出来的子行按默认规则定展开;这一行自己的展开状态由调用方定(换成容器时已经展开)。
            foreach (DocumentEditorRow child in row.Children)
            {
                ApplyExpansion(child);
            }
            RefreshVisible();
        }
        ScheduleRecompute();
    }

    /// <summary>展开 / 收起。</summary>
    internal void Toggle(DocumentEditorRow row)
    {
        if (!row.IsContainer)
        {
            return;
        }
        row.IsExpanded = !row.IsExpanded;
        string path = row.Path;
        _ = (row.IsExpanded ? _expanded : _collapsed).Add(path);
        _ = (row.IsExpanded ? _collapsed : _expanded).Remove(path);
        RefreshVisible();
        ScheduleRecompute();
    }

    /// <summary>行尾的「+」:容器加子项,标量加同级。</summary>
    internal void AddFrom(DocumentEditorRow row)
    {
        if (!CanEdit)
        {
            return;
        }
        if (row.IsContainer)
        {
            _ = AddChild(row);
        }
        else
        {
            _ = AddAfter(row);
        }
    }

    /// <summary>在容器里加一项(对象加字段、数组加一项)。</summary>
    internal DocumentEditorRow? AddChild(DocumentEditorRow container)
    {
        if (!CanEdit || !container.IsContainer)
        {
            return null;
        }
        DocumentEditorRow child = container.Kind == BsonKind.Array
            ? new DocumentEditorRow(this, container.Children.Count.ToString(CultureInfo.InvariantCulture), BsonEdit.Empty(container.NewItemKind), container)
            : new DocumentEditorRow(this, NewFieldName(container.Children), new BsonString(""), container);
        container.Children.Add(child);
        container.OnChildrenChanged();
        if (!container.IsExpanded)
        {
            container.IsExpanded = true;
            _ = _expanded.Add(container.Path);
            _ = _collapsed.Remove(container.Path);
        }
        OnEdited(child, structural: true);
        RequestFocus(child);
        return child;
    }

    /// <summary>在某一行后面加一个同级字段(Ctrl+Enter)。</summary>
    internal DocumentEditorRow? AddAfter(DocumentEditorRow row)
    {
        if (!CanEdit)
        {
            return null;
        }
        IList<DocumentEditorRow> siblings = SiblingsOf(row);
        DocumentEditorRow fresh = row.IsArrayItem
            ? new DocumentEditorRow(this, "0", BsonEdit.Empty(row.IsContainer ? BsonKind.String : row.Kind), row.Parent)
            : new DocumentEditorRow(this, NewFieldName(siblings), new BsonString(""), row.Parent);
        siblings.Insert(siblings.IndexOf(row) + 1, fresh);
        row.Parent?.OnChildrenChanged();
        OnEdited(fresh, structural: true);
        RequestFocus(fresh);
        return fresh;
    }

    /// <summary>在末尾加一个顶层字段(「添加字段」)。</summary>
    internal DocumentEditorRow? AddFieldAtEnd()
    {
        if (!CanEdit)
        {
            return null;
        }
        var row = new DocumentEditorRow(this, NewFieldName(_roots), new BsonString(""), null);
        _roots.Add(row);
        RaisePropertyChanged(nameof(NewDocumentText));
        OnEdited(row, structural: true);
        RequestFocus(row);
        return row;
    }

    /// <summary>芯片行的「+ String」确定之后追加一项。</summary>
    internal void AppendItem(DocumentEditorRow array, BsonValue item)
    {
        if (!CanEdit || array.Kind != BsonKind.Array)
        {
            return;
        }
        array.Children.Add(new DocumentEditorRow(this, array.Children.Count.ToString(CultureInfo.InvariantCulture), item, array));
        array.OnChildrenChanged();
        OnEdited(array, structural: true);
    }

    /// <summary>删掉一行(数组项删除后重排下标)。</summary>
    internal void Remove(DocumentEditorRow row)
    {
        if (!row.CanRemove)
        {
            return;
        }
        _ = SiblingsOf(row).Remove(row);
        row.Parent?.OnChildrenChanged();
        RaisePropertyChanged(nameof(NewDocumentText));
        if (row.Parent is { } parent)
        {
            OnEdited(parent, structural: true);
        }
        else
        {
            _dirty = true;
            _jsonStale = true;
            RefreshVisible();
            ScheduleRecompute();
        }
    }

    /// <summary>还原成原文档里的值(连同类型)。</summary>
    internal void Revert(DocumentEditorRow row)
    {
        if (CanEdit && row.OriginalValue is { } original)
        {
            row.SetValue(original.DeepClone());
        }
    }

    /// <summary>
    /// 拖动排序:把 <paramref name="row" /> 放到 <paramref name="target" /> 之前或之后。
    /// 只在同一层之间挪;落点在别的层时取落点那一侧与它同层的祖先 —— 把字段拖进别的对象
    /// 是"移动 + 改路径",那该用剪切粘贴,不该在一次拖动里悄悄发生。
    /// </summary>
    /// <returns>是否挪动了。</returns>
    internal bool Move(DocumentEditorRow row, DocumentEditorRow target, bool after)
    {
        if (!row.CanDrag || ReferenceEquals(row, target))
        {
            return false;
        }
        DocumentEditorRow? anchor = target;
        while (anchor is not null && !ReferenceEquals(anchor.Parent, row.Parent))
        {
            anchor = anchor.Parent;
        }
        if (anchor is null || ReferenceEquals(anchor, row))
        {
            return false;
        }
        if (!ReferenceEquals(anchor, target))
        {
            // 落在祖先的子孙上:视作放在那个祖先之后。
            after = true;
        }
        IList<DocumentEditorRow> siblings = SiblingsOf(row);
        if (anchor.IsId && !after)
        {
            // _id 留在第一位(mongosh 与驱动都这么写,网格的列序也以它打头)。
            after = true;
        }
        _ = siblings.Remove(row);
        int index = siblings.IndexOf(anchor) + (after ? 1 : 0);
        siblings.Insert(Math.Clamp(index, 0, siblings.Count), row);
        row.Parent?.OnChildrenChanged();
        OnEdited(row, structural: true);
        return true;
    }

    /// <summary><c>newField</c>、<c>newField2</c>…(新字段的占位名;聚焦时全选,一打字就替换掉)。</summary>
    private static string NewFieldName(IEnumerable<DocumentEditorRow> siblings)
    {
        var taken = new HashSet<string>(siblings.Select(static s => s.Name), StringComparer.Ordinal);
        if (!taken.Contains("newField"))
        {
            return "newField";
        }
        for (int i = 2; ; i++)
        {
            string name = "newField" + i.ToString(CultureInfo.InvariantCulture);
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    private void RequestFocus(DocumentEditorRow row) => FocusRequested?.Invoke(row);

    /// <summary>行里的提示(换类型时值被重置…)。</summary>
    internal void Toast(string title, ToastKind kind, string? detail = null) =>
        Workspace.Toast(new ToastRequest { Title = title, Kind = kind, Detail = detail });

    // ── 重算 ────────────────────────────────────────────────────────────────

    /// <summary>空闲时重算一次(一阵连续输入合并成一次)。</summary>
    private void ScheduleRecompute()
    {
        if (_recomputePending)
        {
            return;
        }
        _recomputePending = true;
        Dispatcher.UIThread.Post(RecomputeNow, DispatcherPriority.Background);
    }

    /// <summary>
    /// 重算:拼出当前文档 → 校验 → 逐行定状态 → 计数 → 预览、命令、对比、JSON。
    /// 测试直接调它,不必等调度器。
    /// </summary>
    internal void RecomputeNow()
    {
        _recomputePending = false;
        if (_closed)
        {
            return;
        }
        BsonDocument current = BuildDocument();
        BsonDocument? validator = Collection.Validator;
        bool warnOnly = string.Equals(Collection.ValidationAction, "warn", StringComparison.Ordinal);
        IReadOnlyList<SchemaViolation> violations = validator is null ? [] : JsonSchemaValidator.Validate(validator, current, Loc);
        var context = new EvaluationContext(violations, validator, warnOnly);

        EvaluateGroup(_roots, Original, parentIsArray: false, ancestorNew: false, context);
        if (Original is not null)
        {
            context.Removed += Original.Names.Count(n => _roots.All(r => r.Name != n));
        }

        // 文档级的问题:JSON 语法、_id 被删、落不到任何一行上的违规。
        var issues = new List<string>();
        if (_jsonError is not null)
        {
            issues.Add(Loc.Format("Doc_JsonError", _jsonError));
            context.Errors++;
        }
        if (Original is not null && !current.Contains("_id"))
        {
            issues.Add(Loc["Doc_IdChanged"]);
            context.Errors++;
        }
        foreach (SchemaViolation violation in violations)
        {
            if (context.Consumed.Contains(violation))
            {
                continue;
            }
            issues.Add(Loc.Format("Doc_RuleViolation", violation.Message));
            if (warnOnly)
            {
                context.Warnings++;
            }
            else
            {
                context.Errors++;
            }
        }
        SyncIssues(issues);

        _modified = context.Modified;
        _added = context.Added;
        _removed = context.Removed;
        _errors = context.Errors;
        _warnings = context.Warnings;

        BuildCommand(current);
        BuildPreview(current, context);
        if (Mode == DocumentEditorMode.Diff)
        {
            BuildDiff(current);
        }
        if (Mode == DocumentEditorMode.Json && _jsonStale && !_applyingJson && _jsonError is null)
        {
            RegenerateJson(current);
        }
        IdText = current.TryGetValue("_id", out BsonValue id)
            ? id.IsObjectId ? $"ObjectId(\"{id.AsObjectId}\")" : BsonText.Literal(id)
            : "—";
        RaisePropertiesChanged(nameof(ModifiedText), nameof(HasModified), nameof(AddedText), nameof(HasAdded),
            nameof(RemovedText), nameof(HasRemoved), nameof(ErrorText), nameof(HasErrors), nameof(WarningText),
            nameof(HasWarnings), nameof(HasReordered), nameof(HasChanges), nameof(NewDocumentText));
        RaiseSaveState();
    }

    private void RaiseSaveState()
    {
        RaisePropertiesChanged(nameof(CanSave), nameof(CanSaveAsNew), nameof(FooterHint), nameof(CanCloseWithEscape));
        SaveCommand.RaiseCanExecuteChanged();
        SaveAsNewCommand.RaiseCanExecuteChanged();
    }

    private void SyncIssues(List<string> issues)
    {
        if (!Issues.SequenceEqual(issues))
        {
            Issues.Clear();
            foreach (string issue in issues)
            {
                Issues.Add(issue);
            }
        }
    }

    /// <summary>一趟重算里各行共用的东西。</summary>
    private sealed class EvaluationContext(IReadOnlyList<SchemaViolation> violations, BsonDocument? validator, bool warnOnly)
    {
        public Dictionary<string, SchemaViolation> ByPath { get; } = violations
            .GroupBy(static v => v.Path, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.First(), StringComparer.Ordinal);

        public HashSet<SchemaViolation> Consumed { get; } = [];

        public BsonDocument? Validator { get; } = validator;

        public bool WarnOnly { get; } = warnOnly;

        /// <summary>非"没变"的行:路径 → 状态(预览标行用)。</summary>
        public List<(string Path, DocumentEditorRowState State)> States { get; } = [];

        public int Modified { get; set; }

        public int Added { get; set; }

        public int Removed { get; set; }

        public int Errors { get; set; }

        public int Warnings { get; set; }
    }

    private void EvaluateGroup(IReadOnlyList<DocumentEditorRow> rows, BsonValue? parentOriginal, bool parentIsArray, bool ancestorNew,
        EvaluationContext context)
    {
        Dictionary<string, int>? names = parentIsArray
            ? null
            : rows.GroupBy(static r => r.Name, StringComparer.Ordinal).ToDictionary(static g => g.Key, static g => g.Count(), StringComparer.Ordinal);
        foreach (DocumentEditorRow row in rows)
        {
            Evaluate(row, parentOriginal, names, ancestorNew, context);
        }
    }

    private void Evaluate(DocumentEditorRow row, BsonValue? parentOriginal, Dictionary<string, int>? siblingNames, bool ancestorNew,
        EvaluationContext context)
    {
        BsonValue? original = parentOriginal switch
        {
            BsonDocument doc when !row.IsArrayItem => doc.TryGetValue(row.Name, out BsonValue v) ? v : null,
            BsonArray array when row.IsArrayItem && int.TryParse(row.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int i)
                                 && i < array.Count => array[i],
            _ => null
        };
        // 新建文档没有"原文档",也就无所谓修改 / 新增:只校验。
        bool tracked = Original is not null;
        row.OriginalValue = tracked ? original : null;
        row.NameError = siblingNames is null ? null : NameProblem(row, siblingNames, original);
        if (context.Validator is { } validator && row.Kind == BsonKind.String
            && JsonSchemaValidator.EnumAt(validator, row.Segments) is { } options && options.All(static o => o.IsString))
        {
            row.EnumOptions = [.. options.Select(static o => o.AsString)];
        }
        else
        {
            row.EnumOptions = [];
        }

        string? error = row.NameError ?? row.ParseError;
        if (error is null && row.IsId && tracked && (original is null || !row.BuildValue().Equals(original)))
        {
            error = Loc["Doc_IdChanged"];
        }
        SchemaViolation? violation = context.ByPath.GetValueOrDefault(row.Path);
        if (violation is not null)
        {
            _ = context.Consumed.Add(violation);
        }

        DocumentEditorRowState state;
        string? message = null;
        if (error is not null)
        {
            state = DocumentEditorRowState.Error;
            message = error;
        }
        else if (violation is not null)
        {
            state = context.WarnOnly ? DocumentEditorRowState.Warning : DocumentEditorRowState.Error;
            message = Loc.Format("Doc_RuleViolation", violation.Message);
        }
        else if (tracked && original is null)
        {
            state = DocumentEditorRowState.Added;
        }
        else if (tracked && Differs(row, original!))
        {
            state = DocumentEditorRowState.Modified;
        }
        else
        {
            state = DocumentEditorRowState.None;
        }

        bool lifted = false;
        if (row.IsContainer)
        {
            EvaluateGroup(row.Children, original, row.Kind == BsonKind.Array, ancestorNew || (tracked && original is null), context);
            if (tracked && row.Kind == BsonKind.Object && original is BsonDocument before)
            {
                context.Removed += before.Names.Count(n => row.Children.All(c => c.Name != n));
            }
            // 收起的容器把子孙里的错误提到自己这一行 —— 错误藏在看不见的地方,用户只会看到一个灰掉的保存按钮。
            // 提上来的只是显示,不再计数(子孙那一行已经数过了)。
            if (!row.IsExpanded && state is not (DocumentEditorRowState.Error or DocumentEditorRowState.Warning)
                && FirstProblem(row) is { } inner)
            {
                state = inner.State;
                // 违规说明自带完整路径;值 / 字段名的错误不带,补上相对路径(qty:不是 32 位整数)。
                string relative = inner.Path.Length > row.Path.Length ? inner.Path[(row.Path.Length + 1)..] : inner.Path;
                message = inner.Message is { } text && !text.Contains(inner.Path, StringComparison.Ordinal)
                    ? Loc.Format("Doc_LiftedProblem", relative, text)
                    : inner.Message;
                lifted = true;
            }
        }
        if (!lifted)
        {
            switch (state)
            {
                case DocumentEditorRowState.Error:
                    context.Errors++;
                    break;
                case DocumentEditorRowState.Warning:
                    context.Warnings++;
                    break;
                case DocumentEditorRowState.Added when !ancestorNew:
                    // 新加的容器下面那些子字段不再各算一个"新字段"。
                    context.Added++;
                    break;
                case DocumentEditorRowState.Modified:
                    context.Modified++;
                    break;
            }
        }
        row.SetState(state, message);
        if (state != DocumentEditorRowState.None)
        {
            context.States.Add((row.Path, state));
        }
    }

    private static DocumentEditorRow? FirstProblem(DocumentEditorRow container)
    {
        foreach (DocumentEditorRow child in container.Children)
        {
            if (child.State is DocumentEditorRowState.Error or DocumentEditorRowState.Warning)
            {
                return child;
            }
            if (child.IsContainer && FirstProblem(child) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    /// <summary>
    /// 字段名的问题。<c>$</c> 开头、含点号的名字 MongoDB 5.0 起能存,但写不进 <c>$set</c> 路径;
    /// 原文档里本来就有的这种字段不拦(拦了就再也保存不了这份文档),只拦新起的名字。
    /// </summary>
    private string? NameProblem(DocumentEditorRow row, Dictionary<string, int> siblingNames, BsonValue? original)
    {
        string name = row.Name;
        if (name.Length == 0)
        {
            return Loc["Doc_NameEmpty"];
        }
        if (siblingNames.GetValueOrDefault(name) > 1)
        {
            return Loc.Format("Doc_NameDuplicate", name);
        }
        if (original is null && name[0] == '$')
        {
            return Loc["Doc_NameDollar"];
        }
        if (original is null && name.Contains('.'))
        {
            return Loc["Doc_NameDot"];
        }
        return null;
    }

    /// <summary>
    /// 一行算不算"改过"。标量与收起的容器比值;展开的容器只看自己这一层:
    /// 类型变了、数组长度变了、对象删了字段或已有字段的顺序变了(子行自己的修改由子行标)。
    /// </summary>
    private static bool Differs(DocumentEditorRow row, BsonValue original)
    {
        if (BsonKinds.Of(original) != row.Kind)
        {
            return true;
        }
        if (!row.IsContainer || !row.IsExpanded)
        {
            return !row.BuildValue().Equals(original);
        }
        if (row.Kind == BsonKind.Array)
        {
            return row.Children.Count != original.AsBsonArray.Count;
        }
        BsonDocument before = original.AsBsonDocument;
        BsonDocument now = DocumentEditorRow.BuildDocument(row.Children);
        return before.Names.Any(n => !now.Contains(n)) || !DocumentEditorDiff.SameOrder(before, now, skipId: false);
    }

    // ── 命令与预览 ──────────────────────────────────────────────────────────

    /// <summary>乐观并发的版本字段(原文档里有才用)。</summary>
    private const string VersionField = "updatedAt";

    /// <summary>updateOne / replaceOne 的筛选:<c>_id</c>,原文档有 updatedAt 时带上它的原值。</summary>
    internal BsonDocument? BuildFilter()
    {
        if (Original is null || !Original.TryGetValue("_id", out BsonValue id))
        {
            return null;
        }
        var filter = new BsonDocument("_id", id);
        if (Original.TryGetValue(VersionField, out BsonValue version))
        {
            filter[VersionField] = version;
        }
        return filter;
    }

    /// <summary>
    /// 更新文档:<c>$set</c> / <c>$unset</c>,外加刷新版本字段 —— 只校验不刷新的话,
    /// 下一个人拿着同一个 updatedAt 照样能把这次修改覆盖掉,乐观并发就形同虚设。
    /// 用户自己改了 updatedAt 就不替他刷新。
    /// </summary>
    internal BsonDocument BuildUpdate(DocumentEditorPlan plan)
    {
        var update = new BsonDocument();
        if (plan.Set.ElementCount > 0)
        {
            update["$set"] = plan.Set;
        }
        if (plan.Unset.ElementCount > 0)
        {
            update["$unset"] = plan.Unset;
        }
        if (VersionBump(plan) is { } bump)
        {
            update["$currentDate"] = new BsonDocument(VersionField, bump);
        }
        return update;
    }

    /// <summary>版本字段怎么刷:Date → <c>true</c>,Timestamp → <c>{ $type: "timestamp" }</c>;别的类型不碰。</summary>
    private BsonValue? VersionBump(DocumentEditorPlan plan)
    {
        if (Original is null || !Original.TryGetValue(VersionField, out BsonValue version) || plan.IsEmpty
            || plan.Paths.Any(static p => p == VersionField || p.StartsWith(VersionField + ".", StringComparison.Ordinal)))
        {
            return null;
        }
        return version.BsonType switch
        {
            BsonType.DateTime => BsonBoolean.True,
            BsonType.Timestamp => new BsonDocument("$type", "timestamp"),
            _ => null
        };
    }

    private void BuildCommand(BsonDocument current)
    {
        string collectionRef = MongoWorkspaceViewModel.ShellCollectionRef(Collection.Name);
        if (Original is null)
        {
            _plan = null;
            HasReordered = false;
            CommandName = "insertOne";
            CommandText = DocumentEditorFormat.Command(collectionRef, "insertOne", null, current);
            CommandNote = Loc["Doc_InsertNote"];
            return;
        }
        DocumentEditorPlan plan = DocumentEditorDiff.Plan(Original, current);
        _plan = plan;
        HasReordered = plan.Replace && !DocumentEditorDiff.SameOrder(Original, current, skipId: true);
        BsonDocument? filter = BuildFilter();
        bool versioned = Original.Contains(VersionField);
        if (plan.IsEmpty)
        {
            CommandName = "updateOne";
            CommandText = Loc["Doc_NoCommand"];
        }
        else if (plan.Replace)
        {
            CommandName = "replaceOne";
            CommandText = DocumentEditorFormat.Command(collectionRef, "replaceOne", filter, current);
        }
        else
        {
            CommandName = "updateOne";
            CommandText = DocumentEditorFormat.Command(collectionRef, "updateOne", filter, BuildUpdate(plan));
        }
        CommandNote = plan.Replace
            ? Loc[versioned ? "Doc_ReplaceNote" : "Doc_ReplaceNoteNoVersion"]
            : Loc[versioned ? "Doc_OnlyChanged" : "Doc_OnlyChangedNoVersion"];
    }

    private void BuildPreview(BsonDocument current, EvaluationContext context)
    {
        DocumentEditorPreview preview = DocumentEditorFormat.Preview(current, Original, Loc);
        var lineOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < preview.LinePaths.Count; i++)
        {
            if (preview.LinePaths[i] is { } path)
            {
                _ = lineOf.TryAdd(path, i + 1);
            }
        }
        var marks = new Dictionary<int, LineMarkKind>();
        foreach ((string path, DocumentEditorRowState state) in context.States)
        {
            // 预览里折起来的结构(对象数组写成 [ …4 项 ])没有子路径的行:落到最近的祖先那一行。
            string? at = path;
            while (at is not null && !lineOf.ContainsKey(at))
            {
                int dot = at.LastIndexOf('.');
                at = dot < 0 ? null : at[..dot];
            }
            if (at is null)
            {
                continue;
            }
            LineMarkKind kind = state switch
            {
                DocumentEditorRowState.Error => LineMarkKind.Removed,
                DocumentEditorRowState.Added => LineMarkKind.Added,
                _ => LineMarkKind.Modified
            };
            int line = lineOf[at];
            if (!marks.TryGetValue(line, out LineMarkKind existing) || Rank(kind) > Rank(existing))
            {
                marks[line] = kind;
            }
        }
        foreach (int line in preview.RemovedLines)
        {
            marks[line] = LineMarkKind.Removed;
        }
        PreviewText = preview.Text;
        PreviewMarks = [.. marks.OrderBy(static m => m.Key).Select(static m => new LineMark(m.Key, m.Value))];

        static int Rank(LineMarkKind kind) => kind switch
        {
            LineMarkKind.Removed => 3,
            LineMarkKind.Modified => 2,
            _ => 1
        };
    }

    private void BuildDiff(BsonDocument current)
    {
        string left = Original is null ? "" : BsonText.Pretty(Original, EjsonMode.Shell);
        string right = BsonText.Pretty(current, EjsonMode.Shell);
        (HashSet<int> removed, HashSet<int> added) = DocumentEditorDiff.Lines(SplitLines(left), SplitLines(right));
        DiffLeftText = left;
        DiffRightText = right;
        DiffLeftMarks = [.. removed.Order().Select(static l => new LineMark(l, LineMarkKind.Removed))];
        DiffRightMarks = [.. added.Order().Select(static l => new LineMark(l, LineMarkKind.Added))];
    }

    private static string[] SplitLines(string text) => text.Length == 0 ? [] : text.Split('\n');

    // ── JSON 模式 ───────────────────────────────────────────────────────────

    private DispatcherTimer CreateJsonTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => ApplyJson();
        return timer;
    }

    /// <summary>JSON 文本 → 表单(停手之后;测试直接调)。</summary>
    internal void ApplyJson()
    {
        _jsonTimer?.Stop();
        if (_closed || !CanEdit)
        {
            return;
        }
        List<EditorDiagnostic> diagnostics = [.. ShellJson.Diagnose(JsonText).Select(d => new EditorDiagnostic(
            d.Offset, Math.Max(1, d.Length), Loc.Format(d.MessageKey, d.Argument), DiagnosticSeverity.Error, d.Fix, d.Fix is null ? null : "Alt+↵"))];
        BsonDocument document;
        try
        {
            document = ShellJson.ParseDocument(JsonText);
        }
        catch (ShellJsonException ex)
        {
            _jsonError = ex.Message;
            if (diagnostics.Count == 0)
            {
                int offset = ex.Offset >= 0 ? Math.Min(ex.Offset, Math.Max(0, JsonText.Length - 1)) : Math.Max(0, JsonText.TrimEnd().Length - 1);
                diagnostics.Add(new(offset, 1, Loc.Format("Doc_JsonError", ex.Message)));
            }
            JsonDiagnostics = diagnostics;
            RecomputeNow();
            return;
        }
        _jsonError = null;
        JsonDiagnostics = [];
        _applyingJson = true;
        try
        {
            LoadRows(document, keepExpansion: true);
            RecomputeNow();
        }
        finally
        {
            _applyingJson = false;
        }
    }

    private void RegenerateJson(BsonDocument? current = null)
    {
        _regeneratingJson = true;
        try
        {
            JsonText = BsonText.Pretty(current ?? BuildDocument(), EjsonMode.Shell);
        }
        finally
        {
            _regeneratingJson = false;
        }
        _jsonStale = false;
        _jsonError = null;
        JsonDiagnostics = [];
    }

    /// <summary>离开 JSON 模式:还没应用的文本先应用;有语法错误就丢掉它,回到上一份有效内容。</summary>
    private void LeaveJson()
    {
        if (_jsonTimer?.IsEnabled == true)
        {
            ApplyJson();
        }
        if (_jsonError is not null)
        {
            _jsonError = null;
            JsonDiagnostics = [];
            _jsonStale = true;
            Toast(Loc["Doc_JsonDiscarded"], ToastKind.Warning);
        }
    }

    // ── 粘贴与补全 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 剪贴板里的 JSON / EJSON / mongosh 文档载入表单(视图读剪贴板后调用)。
    /// 已有文档保留原 <c>_id</c>:粘贴的是"内容",换 _id 等于换一份文档,那该用「另存为新文档」。
    /// </summary>
    internal void ApplyPastedText(string? text)
    {
        if (!CanEdit)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast(Loc["Doc_PasteEmpty"], ToastKind.Warning);
            return;
        }
        BsonDocument document;
        try
        {
            BsonValue value = ShellJson.ParseValue(text.Trim());
            document = value as BsonDocument
                       ?? (value as BsonArray)?.OfType<BsonDocument>().FirstOrDefault()
                       ?? throw new ShellJsonException("Expected a document { … }", 0);
        }
        catch (ShellJsonException ex)
        {
            Toast(Loc.Format("Doc_PasteBad", ex.Message), ToastKind.Error);
            return;
        }
        BsonValue? keepId = null;
        if (Original is not null && Original.TryGetValue("_id", out BsonValue originalId))
        {
            keepId = originalId;
        }
        else if (!document.Contains("_id") && BuildDocument().TryGetValue("_id", out BsonValue currentId))
        {
            keepId = currentId;
        }
        if (keepId is not null)
        {
            document.Remove("_id");
            document.InsertAt(0, new BsonElement("_id", keepId));
        }
        LoadRows(document, keepExpansion: true);
        Toast(Loc.Format("Doc_Pasted", document.ElementCount), ToastKind.Success,
            Original is not null ? Loc["Doc_PasteKeptId"] : null);
    }

    /// <summary>抽样(只抽一次;失败可以再试)。</summary>
    internal async Task<DocumentEditorSchema?> EnsureSampleAsync(bool reportErrors)
    {
        if (_schema is not null)
        {
            return _schema;
        }
        _sampling ??= SampleAsync();
        DocumentEditorSchema? schema = await _sampling.ConfigureAwait(true);
        if (schema is null)
        {
            _sampling = null;
            if (reportErrors && _sampleError is { } error)
            {
                Toast(Loc.Format("Doc_SampleFailed", error), ToastKind.Error);
            }
        }
        return schema;
    }

    private async Task<DocumentEditorSchema?> SampleAsync()
    {
        try
        {
            int size = Math.Clamp(Workspace.Connection.Settings.SampleSize, 50, 10_000);
            _schema = await DocumentEditorSchema.SampleAsync(Workspace.Connection.Collection(Collection.Database, Collection.Name), size,
                CancellationToken.None).ConfigureAwait(true);
            _sampleError = null;
            return _schema;
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            // 抽样是锦上添花:失败只让补全与"按 Schema 补全"没有候选,不影响编辑本身。
            _sampleError = MongoConnector.Describe(ex);
            Workspace.Log.Info($"Document editor: sampling {Collection.Namespace} failed: {_sampleError}");
            return null;
        }
    }

    /// <summary>直接给一份抽样结果(测试与"已经抽过"的场合)。</summary>
    internal void UseSchema(DocumentEditorSchema schema) => _schema = schema;

    /// <summary>
    /// 按 Schema 补全缺失字段:抽样里出现率 ≥ 30%、本文档没有的字段,按主导类型补一个空值。
    /// 只补到对象里(数组里的字段属于每一项,补哪一项都不对)。
    /// </summary>
    internal async Task FillFromSchemaAsync()
    {
        if (!CanEdit)
        {
            return;
        }
        DocumentEditorSchema? schema = await EnsureSampleAsync(reportErrors: true).ConfigureAwait(true);
        if (schema is null)
        {
            return;
        }
        List<string> added = FillFrom(schema);
        if (added.Count == 0)
        {
            Toast(Loc["Doc_FillNone"], ToastKind.Info);
            return;
        }
        Toast(Loc.Format("Doc_Filled", added.Count, string.Join(", ", added.Take(6)) + (added.Count > 6 ? " …" : "")), ToastKind.Success);
    }

    /// <summary>补全的核心(同步,测试直接调)。返回补上的路径。</summary>
    internal List<string> FillFrom(DocumentEditorSchema schema)
    {
        var added = new List<string>();
        IEnumerable<DocumentEditorFieldStat> candidates = schema.Fields
            .Where(f => schema.Ratio(f) >= FillThreshold && f.Path != "_id")
            .OrderBy(static f => f.Path.Count(static c => c == '.'))
            .ThenByDescending(static f => f.Documents);
        foreach (DocumentEditorFieldStat field in candidates)
        {
            DocumentEditorRow? parent = null;
            IList<DocumentEditorRow> siblings = _roots;
            if (field.Parent.Length > 0)
            {
                parent = FindRow(field.Parent);
                if (parent is null || parent.Kind != BsonKind.Object)
                {
                    continue;
                }
                siblings = parent.Children;
            }
            if (siblings.Any(s => s.Name == field.Name))
            {
                continue;
            }
            siblings.Add(new DocumentEditorRow(this, field.Name, FillValue(field.Dominant), parent));
            if (parent is not null)
            {
                parent.OnChildrenChanged();
                parent.IsExpanded = true;
                _ = _expanded.Add(parent.Path);
            }
            added.Add(field.Path);
        }
        if (added.Count > 0)
        {
            _dirty = true;
            _jsonStale = true;
            RaisePropertyChanged(nameof(NewDocumentText));
            RefreshVisible();
            ScheduleRecompute();
        }
        return added;
    }

    /// <summary>补全用的空值:容器给空容器,少见类型给 null,其余用 <see cref="BsonEdit.Empty" />(与新增字段同一口径)。</summary>
    private static BsonValue FillValue(BsonKind kind) => kind switch
    {
        BsonKind.Object => new BsonDocument(),
        BsonKind.Array => new BsonArray(),
        BsonKind.Null or BsonKind.Missing or BsonKind.Binary or BsonKind.Regex or BsonKind.Timestamp or BsonKind.Other => BsonNull.Value,
        _ => BsonEdit.Empty(kind)
    };

    /// <summary>字段名补全:同一层里抽样见过、这份文档还没有的名字,按出现率排。</summary>
    internal IReadOnlyList<DocumentEditorSuggestion> SuggestNames(DocumentEditorRow row, string prefix)
    {
        if (!row.IsNameEditable)
        {
            return [];
        }
        if (_schema is null)
        {
            // 第一次要补全时才去抽样:打开编辑器不该先打一次 $sample。
            _ = EnsureSampleAsync(reportErrors: false);
            return [];
        }
        string parent = row.Parent?.SchemaPath ?? "";
        var taken = new HashSet<string>(SiblingsOf(row).Where(s => !ReferenceEquals(s, row)).Select(static s => s.Name), StringComparer.Ordinal);
        string typed = prefix.Trim();
        return [.. _schema.ChildrenOf(parent)
            .Where(f => !taken.Contains(f.Name) && f.Name != typed
                        && (typed.Length == 0 || typed.StartsWith("newField", StringComparison.Ordinal)
                            || f.Name.Contains(typed, StringComparison.OrdinalIgnoreCase)))
            .Take(8)
            .Select(f => new DocumentEditorSuggestion(f.Name, f.Dominant, _schema.Ratio(f)))];
    }

    /// <summary>选了一个补全:改名;刚加的空字段顺手换成抽样里的主导类型。</summary>
    internal void AcceptSuggestion(DocumentEditorRow row, DocumentEditorSuggestion suggestion)
    {
        bool fresh = row.OriginalValue is null && row.Kind == BsonKind.String && row.Text.Length == 0;
        row.Name = suggestion.Name;
        if (fresh && suggestion.Kind != BsonKind.String)
        {
            row.SetValue(FillValue(suggestion.Kind));
        }
    }

    // ── 保存 ────────────────────────────────────────────────────────────────

    private async Task SaveAsync(bool asNew)
    {
        // JSON 模式里刚敲完就按 Ctrl+S:停手计时器还没到点,先把文本应用到表单再算。
        if (_jsonTimer?.IsEnabled == true)
        {
            ApplyJson();
        }
        RecomputeNow();
        if (asNew ? !CanSaveAsNew : !CanSave)
        {
            return;
        }
        if (!Workspace.EnsureWritable(Collection.Database))
        {
            return;
        }
        BsonDocument current = BuildDocument();
        bool insert = asNew || Original is null;
        if (asNew)
        {
            current.Remove("_id");
        }
        DocumentEditorPlan? plan = insert ? null : DocumentEditorDiff.Plan(Original!, current);
        if (plan is { IsEmpty: true })
        {
            Close();
            return;
        }
        string method = insert ? "insertOne" : plan!.Replace ? "replaceOne" : "updateOne";
        if (Workspace.Guard.ConfirmWrites && !await ConfirmAsync(method, plan, current).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        try
        {
            IMongoCollection<BsonDocument> collection = Workspace.Connection.Collection(Collection.Database, Collection.Name);
            var watch = Stopwatch.StartNew();
            BsonDocument stored;
            if (insert)
            {
                // 驱动在插入时把生成的 _id 写回这份文档 —— 回调拿到的就是库里那份。
                await collection.InsertOneAsync(current).ConfigureAwait(true);
                stored = current;
                Workspace.Toast(new ToastRequest
                {
                    Title = Loc.Format("Doc_Inserted", Collection.Namespace),
                    Detail = Loc.Format("Common_Ms", watch.ElapsedMilliseconds),
                    Kind = ToastKind.Success
                });
            }
            else
            {
                BsonDocument filter = BuildFilter()!;
                long matched;
                if (plan!.Replace)
                {
                    BsonDocument replacement = current.DeepClone().AsBsonDocument;
                    if (VersionBump(plan) is BsonBoolean)
                    {
                        // replaceOne 用不了 $currentDate:日期型的版本字段在客户端填当前时刻。
                        replacement[VersionField] = new BsonDateTime(DateTime.UtcNow);
                    }
                    ReplaceOneResult result = await collection.ReplaceOneAsync(filter, replacement).ConfigureAwait(true);
                    matched = result.MatchedCount;
                }
                else
                {
                    UpdateResult result = await collection.UpdateOneAsync(filter, new BsonDocumentUpdateDefinition<BsonDocument>(BuildUpdate(plan)))
                        .ConfigureAwait(true);
                    matched = result.MatchedCount;
                }
                if (matched == 0)
                {
                    // 乐观并发失败:别人先改了(或删了)。不覆盖,编辑留在原处,给一个"基于最新版本重做"。
                    Workspace.Toast(new ToastRequest
                    {
                        Title = Loc["Doc_Conflict"],
                        Detail = Loc[Original!.Contains(VersionField) ? "Doc_ConflictDetail" : "Doc_ConflictDeleted"],
                        Kind = ToastKind.Warning,
                        ActionLabel = Loc["Doc_Rebase"],
                        Action = RebaseAsync,
                        Duration = TimeSpan.FromSeconds(10)
                    });
                    return;
                }
                stored = await collection.Find(new BsonDocument("_id", filter["_id"])).FirstOrDefaultAsync().ConfigureAwait(true) ?? current;
                Workspace.Toast(new ToastRequest
                {
                    Title = Loc.Format("Doc_Saved", Collection.Namespace),
                    Detail = Loc.Format("Doc_SavedDetail", method, plan.Set.ElementCount + plan.Unset.ElementCount, watch.ElapsedMilliseconds),
                    Kind = ToastKind.Success
                });
            }
            _dirty = false;
            if (Saved is { } saved)
            {
                await saved(stored).ConfigureAwait(true);
            }
            Close();
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Code == 121)
        {
            // 121 = DocumentValidationFailure:客户端预检放过了(或规则里有它认不出的关键字),服务器拒了。
            Workspace.Toast(new ToastRequest
            {
                Title = Loc["Doc_ServerRejected"],
                Detail = MongoConnector.Describe(ex),
                Kind = ToastKind.Error,
                Duration = TimeSpan.FromSeconds(8)
            });
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Workspace.Toast(new ToastRequest { Title = Loc.Format("Common_Failed", MongoConnector.Describe(ex)), Kind = ToastKind.Error });
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>生产连接(写前确认)保存前确认一次:列出命令与涉及的字段。</summary>
    private Task<bool> ConfirmAsync(string method, DocumentEditorPlan? plan, BsonDocument current)
    {
        string fields = plan is null
            ? Loc.Format("Doc_NewFields", current.ElementCount)
            : plan.Replace ? Loc["Doc_WholeDocument"] : string.Join(", ", plan.Paths.Take(8)) + (plan.Paths.Count() > 8 ? " …" : "");
        return Workspace.ConfirmAsync(new ConfirmRequest
        {
            Title = Loc["Doc_ConfirmTitle"],
            Message = Loc.Format("Doc_ConfirmBody", Workspace.ConnectionName, Collection.Namespace, method),
            ConfirmLabel = Loc["Common_Save"],
            IconKey = "Mongo.save",
            Danger = false,
            Facts = [new(Loc["Doc_ConfirmCommand"], method), new(Loc["Doc_ConfirmFields"], fields)]
        });
    }

    /// <summary>冲突之后:取服务器上的最新版本,把自己的修改重放上去,继续编辑。</summary>
    private async Task RebaseAsync()
    {
        if (_closed || Original is null)
        {
            return;
        }
        try
        {
            BsonDocument? latest = await Workspace.Connection.Collection(Collection.Database, Collection.Name)
                .Find(new BsonDocument("_id", Original["_id"])).FirstOrDefaultAsync().ConfigureAwait(true);
            if (latest is null)
            {
                Toast(Loc["Doc_Deleted"], ToastKind.Warning);
                return;
            }
            BsonDocument current = BuildDocument();
            DocumentEditorPlan plan = DocumentEditorDiff.Plan(Original, current);
            BsonDocument rebased = plan.Replace ? current : Apply(plan, latest.DeepClone().AsBsonDocument);
            Original = latest;
            RaisePropertyChanged(nameof(Original));
            LoadRows(rebased, keepExpansion: true);
            RecomputeNow();
            Toast(Loc["Doc_Rebased"], ToastKind.Success);
        }
        catch (Exception ex) when (ex is MongoException or TimeoutException)
        {
            Toast(Loc.Format("Common_Failed", MongoConnector.Describe(ex)), ToastKind.Error);
        }
    }

    /// <summary>把一份更新计划在内存里应用到文档上(冲突重做用)。</summary>
    internal static BsonDocument Apply(DocumentEditorPlan plan, BsonDocument target)
    {
        foreach (BsonElement unset in plan.Unset)
        {
            _ = BsonPath.Unset(target, unset.Name);
        }
        foreach (BsonElement set in plan.Set)
        {
            _ = BsonPath.Set(target, set.Name, set.Value.DeepClone());
        }
        return target;
    }

    private async Task CopyAsync()
    {
        await Workspace.CopyAsync(BsonText.Pretty(BuildDocument(), EjsonMode.Shell)).ConfigureAwait(true);
        Toast(Loc["Common_Copied"], ToastKind.Success);
    }

    // ── 生命周期 ────────────────────────────────────────────────────────────

    private void OnGuardChanged() => RaiseSaveState();

    /// <inheritdoc />
    internal override void OnClosed()
    {
        _closed = true;
        _jsonTimer?.Stop();
        Workspace.Guard.Changed -= OnGuardChanged;
        base.OnClosed();
    }
}
