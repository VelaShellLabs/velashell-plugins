using System.Diagnostics.CodeAnalysis;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>JSON 视图右侧大纲里的一项(字段 + 类型)。</summary>
internal sealed class JsonOutlineItem
{
    /// <summary>构造。</summary>
    public JsonOutlineItem(string path, string name, int depth, BsonKind kind, bool isAdded, bool isCurrent, int line)
    {
        Path = path;
        Name = name;
        Depth = depth;
        Kind = kind;
        IsAdded = isAdded;
        IsCurrent = isCurrent;
        Line = line;
    }

    /// <summary>路径。</summary>
    public string Path { get; }

    /// <summary>字段名。</summary>
    public string Name { get; }

    /// <summary>层级。</summary>
    public int Depth { get; }

    /// <summary>左内边距(设计稿:顶层 12,子级 26)。</summary>
    public double Indent => 12 + Depth * 14;

    /// <summary>类型。</summary>
    public BsonKind Kind { get; }

    /// <summary>新增的字段(绿色 <c>+ Object</c>)。</summary>
    public bool IsAdded { get; }

    /// <summary>光标所在的字段(左 2px 强调条)。</summary>
    public bool IsCurrent { get; }

    /// <summary>在编辑文本里的行号(点一下跳过去)。</summary>
    public int Line { get; }

    /// <summary>右侧类型小字(数组带长度:<c>Array[4]</c>)。</summary>
    public string TypeText { get; init; } = "";

    /// <summary>色块令牌。</summary>
    public string SwatchToken => BsonKinds.ColorToken(Kind);

    /// <summary>名字颜色。</summary>
    public string NameToken => IsAdded ? "VelaStatusConnected" : IsCurrent ? "VelaAccent" : "VelaTextSecondary";

    /// <summary>类型颜色。</summary>
    public string TypeToken => IsAdded ? "VelaStatusConnected" : "VelaTextMuted";
}

/// <summary>
/// JSON 视图的一张卡片(设计稿 02):头部(#序号、ObjectId、大小、字段数、编辑 / 复制 / 克隆 / 删除),
/// 只读时显示排版好的文档(暂存修改用行标记标出"已修改 · 原值 …"),编辑时是一个带补全与校验的编辑器。
/// <para>
/// 「更新文档」**写入暂存区**而不是直接提交:与网格 / 树里的修改走同一条路 —— 同一份乐观并发检查、
/// 同一个「待提交」计数、同一个 10 秒撤销;在 JSON 里改了 #3、又在网格里改了 #4,一次 Ctrl+S 一起提交。
/// 直接提交会让 JSON 视图成为唯一一个"改了就写"的地方,用户得记住哪种视图是哪种语义。
/// </para>
/// </summary>
internal sealed class JsonCardViewModel : ObservableObject
{
    private readonly CollectionTabViewModel _owner;
    private string _editText = "";
    private string _editStart = "";
    private int _changeCount;
    private BsonDocument? _parsed;

    /// <summary>构造。</summary>
    public JsonCardViewModel(CollectionTabViewModel owner, CollectionRow row)
    {
        _owner = owner;
        Row = row;
        EditCommand = new RelayCommand(BeginEdit);
        CopyCommand = new AsyncCommand(() => owner.CopyDocumentsAsync([Row]));
        CloneCommand = new RelayCommand(() => owner.Clone(Row));
        DeleteCommand = new RelayCommand(() => owner.StageDelete([Row]));
        CancelCommand = new RelayCommand(CancelEdit);
        UpdateCommand = new RelayCommand(() => Update());
        FormatCommand = new RelayCommand(Format);
        RevertCommand = new RelayCommand(() => EditText = _editStart);
        Completion = request => Task.FromResult(owner.Sample.Complete(owner.Loc, request.Text, request.CaretOffset, filterMode: false));
    }

    /// <summary>所属行。</summary>
    public CollectionRow Row { get; }

    /// <summary>文案表。</summary>
    public Loc Loc => _owner.Loc;

    /// <summary><c>#3</c>。</summary>
    public string NumberText => "#" + Row.NumberText;

    /// <summary><c>ObjectId("66f5c2a1…")</c>;新增行为「(自动生成)」。</summary>
    public string IdText => Row.Document.GetValue("_id", BsonNull.Value) is { IsBsonNull: false } id
        ? (_owner.Ejson == EjsonMode.Shell ? BsonText.Literal(id) : BsonText.Compact(id, _owner.Ejson))
        : Loc["Cw_AutoId"];

    /// <summary><c>· 1.2 KB · 8 个字段</c>。</summary>
    public string MetaText => Loc.Format("Cw_CardMeta", BsonText.Bytes(Row.Document.ToBson().LongLength), Row.Document.ElementCount);

    /// <summary>
    /// 只读排版(按需算:JSON 视图里一页几万张卡片,虚拟化只会让看得见的那几张来取它)。
    /// </summary>
    [AllowNull]
    private PrintedCard Printed { get => field ??= CardPrinter.Print(Row.Document, _owner.Ejson, Fold); set; }

    /// <summary>只读时的文本。</summary>
    public string Text => Printed.Text;

    /// <summary>只读时的行标记(暂存修改)。</summary>
    [AllowNull]
    public IReadOnlyList<LineMark> Marks { get => field ??= BuildMarks(); private set; }

    /// <summary>编辑器的语法:Shell 写法用 mongosh 着色,另外两种用 JSON。</summary>
    public CodeLanguage Language => _owner.Ejson == EjsonMode.Shell ? CodeLanguage.Shell : CodeLanguage.Json;

    /// <summary>只读文本的行数(视图据此定高:卡片要按内容撑开,不能自己滚)。</summary>
    public int LineCount => Printed.LinePaths.Count;

    /// <summary>编辑文本的行数。</summary>
    public int EditLineCount => _editText.Count(static c => c == '\n') + 1;

    /// <summary>编辑中。</summary>
    public bool IsEditing
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsReadOnlyView), nameof(EditBadge));
                _owner.OnCardEditingChanged(this);
            }
        }
    }

    /// <summary>只读显示中。</summary>
    public bool IsReadOnlyView => !IsEditing;

    /// <summary>编辑中的文本。</summary>
    public string EditText
    {
        get => _editText;
        set
        {
            if (SetProperty(ref _editText, value ?? ""))
            {
                Analyze();
                RaisePropertyChanged(nameof(EditLineCount));
            }
        }
    }

    /// <summary>编辑器上的诊断。</summary>
    public IReadOnlyList<EditorDiagnostic> Diagnostics
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>编辑器上的行标记(与原文档相比改了 / 加了的字段)。</summary>
    public IReadOnlyList<LineMark> EditMarks
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary><c>语法正确 · 符合验证规则</c>。</summary>
    public string ValidityText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>语法与验证都通过。</summary>
    public bool IsValid
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(ValidityIcon), nameof(ValidityToken));
            }
        }
    } = true;

    /// <summary>校验图标。</summary>
    public string ValidityIcon => IsValid ? "Mongo.circle-check" : "Mongo.circle-x";

    /// <summary>校验图标颜色。</summary>
    public string ValidityToken => IsValid ? "VelaStatusConnected" : "VelaError";

    /// <summary>光标行(编辑器双向写回)。</summary>
    public int CaretLine
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                UpdateCaret();
            }
        }
    } = 1;

    /// <summary>光标列。</summary>
    public int CaretColumn
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                UpdateCaret();
            }
        }
    } = 1;

    /// <summary>光标偏移。</summary>
    public int CaretOffset
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                UpdateCaret();
            }
        }
    }

    /// <summary><c>Ln 11, Col 13 · status: String · Shell 语法</c>。</summary>
    public string CaretText
    {
        get;
        private set => SetProperty(ref field, value);
    } = "";

    /// <summary>头部「编辑中 · 2 处修改」。</summary>
    public string EditBadge => Loc.Format("Cw_CardEditing", _changeCount);

    /// <summary>大纲。</summary>
    public IReadOnlyList<JsonOutlineItem> Outline
    {
        get;
        private set => SetProperty(ref field, value);
    } = [];

    /// <summary>补全(键位置给字段名,值位置给抽样里的取值分布)。</summary>
    public Func<CompletionRequest, Task<CompletionSet?>> Completion { get; }

    /// <summary>编辑。</summary>
    public RelayCommand EditCommand { get; }

    /// <summary>复制。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <summary>克隆。</summary>
    public RelayCommand CloneCommand { get; }

    /// <summary>删除(暂存)。</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>取消编辑。</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>更新文档(写入暂存区)。</summary>
    public RelayCommand UpdateCommand { get; }

    /// <summary>格式化。</summary>
    public RelayCommand FormatCommand { get; }

    /// <summary>撤销本次编辑里的改动(回到进入编辑时的文本)。</summary>
    public RelayCommand RevertCommand { get; }

    /// <summary>暂存区 / 写法变了:重排只读文本与行标记。</summary>
    public void Refresh()
    {
        Printed = null;
        Marks = null;
        RaisePropertiesChanged(nameof(Text), nameof(Marks), nameof(IdText), nameof(MetaText), nameof(Language), nameof(LineCount));
        if (!IsEditing)
        {
            RefreshOutline();
        }
    }

    /// <summary>进入编辑(当前写法的排版文本)。</summary>
    public void BeginEdit()
    {
        if (IsEditing || !_owner.EnsureCanWrite())
        {
            return;
        }
        if (Row.IsDeleted)
        {
            return;
        }
        _editStart = CardPrinter.Print(Row.Document, _owner.Ejson).Text;
        _editText = _editStart;
        RaisePropertiesChanged(nameof(EditText), nameof(EditLineCount));
        Analyze();
        IsEditing = true;
    }

    /// <summary>取消编辑。</summary>
    public void CancelEdit()
    {
        if (!IsEditing)
        {
            return;
        }
        IsEditing = false;
        Diagnostics = [];
        EditMarks = [];
        RefreshOutline();
    }

    /// <summary>「更新文档」:解析、比对原文档、差异写进暂存区。有错不关编辑器。</summary>
    public bool Update()
    {
        if (!IsEditing)
        {
            return false;
        }
        Analyze();
        if (_parsed is null)
        {
            return false;
        }
        BsonDocument edited = _parsed;
        if (Row.Insert is { } insert)
        {
            if (!_owner.EnsureCanWrite())
            {
                return false;
            }
            _owner.Staging.ReplaceInsert(insert, edited);
        }
        else if (Row.Original is { } original)
        {
            if (edited.TryGetValue("_id", out BsonValue id) && !id.Equals(original["_id"]))
            {
                ValidityText = Loc["Cw_IdImmutable"];
                IsValid = false;
                return false;
            }
            if (!_owner.EnsureCanWrite())
            {
                return false;
            }
            _owner.Staging.ReplaceEdits(original, DocumentDiff.Compute(original, edited));
        }
        IsEditing = false;
        EditMarks = [];
        Diagnostics = [];
        Refresh();
        return true;
    }

    /// <summary>格式化(按当前写法重排;语法错误时不动)。</summary>
    public void Format()
    {
        if (_parsed is not null)
        {
            EditText = CardPrinter.Print(_parsed, _owner.Ejson).Text;
        }
    }

    /// <summary>光标动了:底栏的位置文字与大纲的高亮。</summary>
    public void UpdateCaret()
    {
        string type = "";
        string path = "";
        if (IsEditing)
        {
            CaretPathScanner.Result at = CaretPathScanner.Scan(_editText, Math.Clamp(CaretOffset, 0, _editText.Length));
            path = at.Path;
            if (_owner.InlineTypeHints && path.Length > 0 && _parsed is not null)
            {
                BsonValue? value = BsonPath.Get(_parsed, path);
                type = value is null ? "" : $"{path}: {BsonKinds.Name(BsonKinds.Of(value))} · ";
            }
        }
        CaretText = Loc.Format("Cw_CardCaret", CaretLine, CaretColumn, type, Language == CodeLanguage.Shell ? Loc["Cw_SyntaxShell"] : Loc["Cw_SyntaxJson"]);
        if (IsEditing)
        {
            RefreshOutline(path);
        }
    }

    /// <summary>重建大纲(<paramref name="currentPath" /> 高亮)。</summary>
    public void RefreshOutline(string? currentPath = null)
    {
        BsonDocument doc = IsEditing && _parsed is not null ? _parsed : Row.Document;
        BsonDocument? baseline = Row.Original;
        IReadOnlyDictionary<string, int> lines = IsEditing
            ? CaretPathScanner.Scan(_editText, -1).KeyLines
            : Printed.LinePaths.Select((p, i) => (p, i)).Where(static t => t.p is not null)
                .GroupBy(static t => t.p!).ToDictionary(static g => g.Key, static g => g.First().i + 1);
        string top = currentPath?.Split('.')[0] ?? "";
        var items = new List<JsonOutlineItem>();
        foreach (BsonElement element in doc)
        {
            BsonKind kind = BsonKinds.Of(element.Value);
            bool added = baseline is not null && !baseline.Contains(element.Name) || Row.IsAdded;
            items.Add(new JsonOutlineItem(element.Name, element.Name, 0, kind, added && !Row.IsAdded, element.Name == top && top.Length > 0,
                lines.GetValueOrDefault(element.Name, 1))
            {
                TypeText = TypeLabel(element.Value, added && !Row.IsAdded)
            });
            if (element.Value is BsonDocument nested && nested.ElementCount <= 8)
            {
                foreach (BsonElement child in nested)
                {
                    string path = $"{element.Name}.{child.Name}";
                    items.Add(new JsonOutlineItem(path, child.Name, 1, BsonKinds.Of(child.Value), false, currentPath == path,
                        lines.GetValueOrDefault(path, lines.GetValueOrDefault(element.Name, 1)))
                    {
                        TypeText = TypeLabel(child.Value, false)
                    });
                }
            }
        }
        Outline = items;
    }

    private static string TypeLabel(BsonValue value, bool added)
    {
        string name = value is BsonArray array ? $"Array[{array.Count}]" : BsonKinds.Name(BsonKinds.Of(value));
        return added ? "+ " + name : name;
    }

    /// <summary>
    /// 编辑时实时分析:语法(诊断)、与原文档的差异(行标记 + 「编辑中 · N 处修改」)、
    /// $jsonSchema 预检(有验证规则时)。
    /// </summary>
    private void Analyze()
    {
        var diagnostics = new List<EditorDiagnostic>();
        foreach (ShellDiagnostic d in ShellJson.Diagnose(_editText))
        {
            diagnostics.Add(new EditorDiagnostic(d.Offset, d.Length, Loc.Format(d.MessageKey, d.Argument), DiagnosticSeverity.Error, d.Fix, Loc["Cw_FixHint"]));
        }
        _parsed = null;
        if (diagnostics.Count == 0)
        {
            try
            {
                _parsed = ShellJson.ParseDocument(_editText);
            }
            catch (ShellJsonException ex)
            {
                int at = ex.Offset >= 0 ? Math.Min(ex.Offset, Math.Max(0, _editText.Length - 1)) : 0;
                diagnostics.Add(new EditorDiagnostic(at, 1, Loc["Cw_SyntaxError"] + " " + ex.Message));
            }
        }
        Diagnostics = diagnostics;
        if (_parsed is null)
        {
            ValidityText = Loc["Cw_SyntaxError"];
            IsValid = false;
            _changeCount = 0;
            EditMarks = [];
            RaisePropertyChanged(nameof(EditBadge));
            return;
        }
        BsonDocument baseline = Row.Original ?? Row.Document;
        List<(string Path, BsonValue? Value)> diff = Row.Original is null ? [] : DocumentDiff.Compute(baseline, _parsed);
        _changeCount = Row.Original is null ? 0 : diff.Count;
        RaisePropertyChanged(nameof(EditBadge));
        IReadOnlyDictionary<string, int> lines = CaretPathScanner.Scan(_editText, -1).KeyLines;
        var marks = new List<LineMark>();
        foreach ((string path, BsonValue? value) in diff)
        {
            if (value is null)
            {
                continue;
            }
            int line = lines.GetValueOrDefault(path, 0);
            if (line == 0)
            {
                continue;
            }
            BsonValue? before = BsonPath.Get(baseline, path);
            marks.Add(before is null
                ? new LineMark(line, LineMarkKind.Added, Loc["Cw_MarkAdded"])
                : new LineMark(line, LineMarkKind.Modified, Loc.Format("Cw_MarkModified", Short(before))));
        }
        EditMarks = marks;
        IReadOnlyList<SchemaViolation> violations = _owner.Info.Validator is { } validator
            ? JsonSchemaValidator.Validate(validator, _parsed, Loc)
            : [];
        if (violations.Count > 0)
        {
            ValidityText = Loc.Format("Cw_SchemaViolations", violations.Count, violations[0].Message);
            IsValid = false;
        }
        else
        {
            ValidityText = _owner.Info.Validator is null ? Loc["Cw_SyntaxOk"] : Loc["Cw_SyntaxOkSchema"];
            IsValid = true;
        }
        UpdateCaret();
    }

    /// <summary>只读文本上的行标记:暂存修改落在哪一行就标哪一行(删除的字段标在它父级那一行)。</summary>
    private List<LineMark> BuildMarks()
    {
        var marks = new List<LineMark>();
        if (Row.Insert is not null || _owner.Staging.EditOf(Row.Id) is not { } edit)
        {
            return marks;
        }
        foreach (FieldChange change in edit.Changes)
        {
            int line = Printed.LineOf(change.Path);
            if (change.Value is null)
            {
                marks.Add(new LineMark(line, LineMarkKind.Removed, Loc.Format("Cw_MarkRemoved", change.Path)));
            }
            else if (change.Original is null)
            {
                marks.Add(new LineMark(line, LineMarkKind.Added, Loc["Cw_MarkAdded"]));
            }
            else
            {
                marks.Add(new LineMark(line, LineMarkKind.Modified, Loc.Format("Cw_MarkModified", Short(change.Original))));
            }
        }
        return marks;
    }

    private string Short(BsonValue value)
    {
        string text = BsonText.Inline(value, Loc, shortenIds: true);
        return text.Length > 40 ? text[..39] + "…" : text;
    }

    /// <summary>只读预览里对象数组的折叠文字(<c>…2 项</c>)。</summary>
    private string Fold(int count) => Loc.Format("Cw_FoldItems", count);
}
