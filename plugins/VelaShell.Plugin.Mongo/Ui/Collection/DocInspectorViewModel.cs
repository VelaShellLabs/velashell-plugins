using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Core;
using VelaShell.Plugin.Mongo.Staging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>检查器字段树的一行(设计稿 01 右侧:键 : 值 + 类型标签)。</summary>
internal sealed class InspectorField : ObservableObject
{

    /// <summary>构造。</summary>
    public InspectorField(string path, string key, int depth, BsonValue? value, bool isExpanded, ChangeRelation change, Loc loc)
    {
        Path = path;
        Key = key;
        Depth = depth;
        Value = value;
        Kind = BsonKinds.Of(value);
        IsExpanded = isExpanded;
        Change = change;
        ValueText = Format(value, isExpanded, loc);
    }

    /// <summary>路径。</summary>
    public string Path { get; }

    /// <summary>键名(数组元素是下标)。</summary>
    public string Key { get; }

    /// <summary>层级。</summary>
    public int Depth { get; }

    /// <summary>左内边距(设计稿:顶层 10,子级 24)。</summary>
    public double Indent => 10 + Depth * 14;

    /// <summary>值;字段不存在为 <see langword="null" />。</summary>
    public BsonValue? Value { get; }

    /// <summary>类型。</summary>
    public BsonKind Kind { get; }

    /// <summary>字段不存在(这一页里别的文档有这个字段,这份没有)。</summary>
    public bool IsMissing => Kind == BsonKind.Missing;

    /// <summary>能展开。</summary>
    public bool IsExpandable => Value is BsonDocument { ElementCount: > 0 } or BsonArray { Count: > 0 };

    /// <summary>展开着。</summary>
    public bool IsExpanded { get; }

    /// <summary>展开箭头图标。</summary>
    public string Chevron => IsExpanded ? "Mongo.chevron-down" : "Mongo.chevron-right";

    /// <summary>与暂存修改的关系。</summary>
    public ChangeRelation Change { get; }

    /// <summary>改过(值橙色)。</summary>
    public bool IsModified => Change is ChangeRelation.Exact or ChangeRelation.InsideChange;

    /// <summary>值文字。</summary>
    public string ValueText { get; }

    /// <summary>值颜色。</summary>
    public string ValueToken => IsModified ? "VelaWarning" : IsMissing ? "VelaTextMuted" : BsonKinds.ColorToken(Kind);

    /// <summary>类型标签文字(缺失写「缺失」)。</summary>
    public string TypeText { get; init; } = "";

    /// <summary>内联编辑器。</summary>
    public InlineValueEditor? Editor
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertyChanged(nameof(IsEditing));
            }
        }
    }

    /// <summary>编辑中。</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>
    /// 检查器里的值写法:ObjectId 缩写、字符串带引号、Decimal 原样、日期 ISO;
    /// 展开着的容器只给 <c>{3}</c> / <c>[4]</c>,收起的文档列出键名(<c>{ sku, qty, price }</c>),
    /// 收起的短数组直接给字面量(<c>["企业","开票"]</c>)。
    /// </summary>
    private static string Format(BsonValue? value, bool expanded, Loc loc)
    {
        switch (BsonKinds.Of(value))
        {
            case BsonKind.Missing:
                return loc["Cw_FieldMissing"];
            case BsonKind.ObjectId:
                return BsonText.Shorten(value!.AsObjectId.ToString());
            case BsonKind.Decimal128:
                return value!.AsDecimal128.ToString();
            case BsonKind.Date:
                return BsonText.IsoDate(value!);
            case BsonKind.Object:
                {
                    BsonDocument doc = value!.AsBsonDocument;
                    if (expanded || doc.ElementCount == 0)
                    {
                        return $"{{{doc.ElementCount}}}";
                    }
                    string keys = string.Join(", ", doc.Names.Take(4)) + (doc.ElementCount > 4 ? ", …" : "");
                    return $"{{ {keys} }}";
                }
            case BsonKind.Array:
                {
                    BsonArray array = value!.AsBsonArray;
                    if (expanded)
                    {
                        return $"[{array.Count}]";
                    }
                    string literal = "[" + string.Join(",", array.Select(static v => v is BsonDocument or BsonArray ? "…" : BsonText.Literal(v))) + "]";
                    return literal.Length <= 40 ? literal : $"[{array.Count}]";
                }
            default:
                return BsonText.Inline(value, loc, shortenIds: true);
        }
    }
}

/// <summary>「集合信息」页的一行。</summary>
/// <param name="Label">标签。</param>
/// <param name="Value">值。</param>
internal sealed record CollectionInfoLine(string Label, string Value);

/// <summary>
/// 网格右侧的文档检查器(288px):字段树(嵌套展开、值内联编辑、类型下拉)、JSON、集合信息三页,
/// 底部是 $jsonSchema 预检状态与「放弃 / 应用修改」(只作用于这一份文档)。
/// </summary>
internal sealed class DocInspectorViewModel : ObservableObject
{
    private readonly HashSet<string> _expanded = [with(StringComparer.Ordinal)];
    private readonly HashSet<string> _collapsed = [with(StringComparer.Ordinal)];
    private InspectorField? _selected;

    /// <summary>构造。</summary>
    public DocInspectorViewModel(CollectionTabViewModel owner)
    {
        Owner = owner;
        ShowJsonCommand = new RelayCommand(() => Page = 1);
        CopyCommand = new AsyncCommand(() => Row is null ? Task.CompletedTask : owner.CopyDocumentsAsync([Row]));
        CloneCommand = new RelayCommand(() =>
        {
            if (Row is not null)
            {
                owner.Clone(Row);
            }
        });
        DeleteCommand = new RelayCommand(() =>
        {
            if (Row is not null)
            {
                owner.StageDelete([Row]);
            }
        });
        AddFieldCommand = new RelayCommand(() =>
        {
            if (Row is not null)
            {
                owner.PromptAddField(Row, "");
            }
        });
        DiscardCommand = new RelayCommand(() =>
        {
            if (Row is not null)
            {
                owner.DiscardDocument(Row);
            }
        });
        ApplyCommand = new AsyncCommand(() => Row switch
        {
            { Insert: { } insert } => owner.CommitAsync(new CommitScope(OnlyInsert: insert)),
            { Id: { } id } => owner.CommitAsync(new CommitScope(id)),
            _ => Task.CompletedTask
        });
    }

    /// <summary>文案表。</summary>
    public Loc Loc => Owner.Loc;

    /// <summary>所属工作台。</summary>
    public CollectionTabViewModel Owner { get; }

    /// <summary>看着的行。</summary>
    public CollectionRow? Row { get; private set; }

    /// <summary>有没有行。</summary>
    public bool HasRow => Row is not null;

    /// <summary>头部的 <c>#4</c>。</summary>
    public string NumberText => Row is null ? "" : "#" + Row.NumberText;

    /// <summary>页签:0 字段、1 JSON、2 集合信息。</summary>
    public int Page
    {
        get; set
        {
            if (SetProperty(ref field, value))
            {
                RaisePropertiesChanged(nameof(IsFieldsPage), nameof(IsJsonPage), nameof(IsInfoPage));
                if (value == 1)
                {
                    RefreshJson();
                }
                else if (value == 2)
                {
                    _ = LoadInfoAsync();
                }
            }
        }
    }

    /// <summary>字段页。</summary>
    public bool IsFieldsPage
    {
        get => Page == 0;
        set
        {
            if (value)
            {
                Page = 0;
            }
        }
    }

    /// <summary>JSON 页。</summary>
    public bool IsJsonPage
    {
        get => Page == 1;
        set
        {
            if (value)
            {
                Page = 1;
            }
        }
    }

    /// <summary>集合信息页。</summary>
    public bool IsInfoPage
    {
        get => Page == 2;
        set
        {
            if (value)
            {
                Page = 2;
            }
        }
    }

    /// <summary>字段树的可见行。</summary>
    public ObservableCollection<InspectorField> Fields { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>选中的字段。</summary>
    public InspectorField? SelectedField
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>JSON 页的文本。</summary>
    public string JsonText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>JSON 页的语法。</summary>
    public CodeLanguage JsonLanguage => Owner.Ejson == EjsonMode.Shell ? CodeLanguage.Shell : CodeLanguage.Json;

    /// <summary>底部验证状态文字。</summary>
    public string ValidationText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>验证状态图标。</summary>
    public string ValidationIcon { get; private set => SetProperty(ref field, value); } = "Mongo.shield";

    /// <summary>验证状态图标颜色。</summary>
    public string ValidationToken { get; private set => SetProperty(ref field, value); } = "VelaTextMuted";

    /// <summary>这份文档有暂存修改(「放弃 / 应用修改」可用)。</summary>
    public bool HasChanges => Row is { State: CollectionRowState.Modified or CollectionRowState.Added or CollectionRowState.Deleted };

    /// <summary>「集合信息」页。</summary>
    public IReadOnlyList<CollectionInfoLine> InfoLines { get; private set => SetProperty(ref field, value); } = [];

    /// <summary>JSON 页。</summary>
    public RelayCommand ShowJsonCommand { get; }

    /// <summary>复制。</summary>
    public AsyncCommand CopyCommand { get; }

    /// <summary>克隆。</summary>
    public RelayCommand CloneCommand { get; }

    /// <summary>删除(暂存)。</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>添加字段。</summary>
    public RelayCommand AddFieldCommand { get; }

    /// <summary>放弃这份文档的修改。</summary>
    public RelayCommand DiscardCommand { get; }

    /// <summary>只提交这份文档。</summary>
    public AsyncCommand ApplyCommand { get; }

    /// <summary>换一行看。</summary>
    public void Load(CollectionRow? row)
    {
        if (!ReferenceEquals(row, Row))
        {
            _expanded.Clear();
            _collapsed.Clear();
        }
        Row = row;
        RaisePropertiesChanged(nameof(Row), nameof(HasRow), nameof(NumberText));
        Reload();
    }

    /// <summary>重建字段树(暂存区变了、展开变了)。</summary>
    public void Reload()
    {
        string? keep = _selected?.Path;
        if (Row is null)
        {
            Fields = [];
            ValidationText = "";
            RaisePropertyChanged(nameof(HasChanges));
            return;
        }
        BsonDocument doc = Row.Document;
        StagedEdit? edit = Row.Insert is null ? Owner.Staging.EditOf(Row.Id) : null;
        var list = new List<InspectorField>();
        Append(list, doc, "", 0, edit);
        // 这一页里别的文档有、这一份没有的顶层字段:「— 字段不存在」+「缺失」。
        foreach (CollectionColumn column in Owner.Columns)
        {
            if (!doc.Contains(column.Name))
            {
                list.Add(new InspectorField(column.Name, column.Name, 0, null, false, ChangeRelation.None, Loc) { TypeText = Loc["Cw_TypeMissing"] });
            }
        }
        Fields = [with(list)];
        _selected = keep is null ? null : list.FirstOrDefault(f => f.Path == keep);
        RaisePropertiesChanged(nameof(SelectedField), nameof(HasChanges), nameof(NumberText));
        RefreshValidation();
        if (Page == 1)
        {
            RefreshJson();
        }
    }

    private void Append(List<InspectorField> list, BsonValue container, string parent, int depth, StagedEdit? edit)
    {
        IEnumerable<(string Name, BsonValue Value)> children = container switch
        {
            BsonDocument doc => doc.Elements.Select(static e => (e.Name, e.Value)),
            BsonArray array => array.Select(static (v, i) => (i.ToString(CultureInfo.InvariantCulture), v)),
            _ => []
        };
        foreach ((string name, BsonValue value) in children)
        {
            string path = parent.Length == 0 ? name : $"{parent}.{name}";
            bool expanded = IsExpanded(path, value, depth);
            var field = new InspectorField(path, name, depth, value, expanded, edit?.RelationOf(path) ?? ChangeRelation.None, Loc)
            {
                TypeText = BsonKinds.Name(BsonKinds.Of(value))
            };
            list.Add(field);
            if (expanded && field.IsExpandable)
            {
                Append(list, value, path, depth + 1, edit);
            }
        }
    }

    /// <summary>
    /// 默认展开:顶层的子文档与"文档数组"展开一层(设计稿:customer、items 展开,items 的元素收起);
    /// 用户点过的以用户为准。
    /// </summary>
    private bool IsExpanded(string path, BsonValue value, int depth)
    {
        if (_expanded.Contains(path))
        {
            return true;
        }
        if (_collapsed.Contains(path))
        {
            return false;
        }
        return depth == 0 && value switch
        {
            BsonDocument doc => doc.ElementCount is > 0 and <= 12,
            BsonArray array => array.Count is > 0 and <= 8 && array.All(static v => v is BsonDocument),
            _ => false
        };
    }

    /// <summary>展开 / 收起。</summary>
    public void Toggle(InspectorField field)
    {
        if (!field.IsExpandable)
        {
            return;
        }
        if (field.IsExpanded)
        {
            _ = _expanded.Remove(field.Path);
            _ = _collapsed.Add(field.Path);
        }
        else
        {
            _ = _collapsed.Remove(field.Path);
            _ = _expanded.Add(field.Path);
        }
        Reload();
    }

    /// <summary>开始编辑一个字段的值。</summary>
    public void BeginEdit(InspectorField field)
    {
        if (Row is null || field.Path == "_id" && Row.Insert is null || !Owner.EnsureCanWrite())
        {
            return;
        }
        foreach (InspectorField other in Fields.Where(static f => f.IsEditing))
        {
            other.Editor = null;
        }
        BsonKind fallback = Owner.Columns.FirstOrDefault(c => c.Name == field.Path)?.Kind ?? Owner.Sample.KindOf(field.Path);
        field.Editor = Owner.CreateEditor(field.Value, field.Path, fallback);
        SelectedField = field;
    }

    /// <summary>提交一个字段的编辑(写入暂存区)。</summary>
    public bool CommitEdit(InspectorField field) => Row is not null && CommitEdit(field, Row);

    /// <summary>
    /// 把一个字段的编辑提交到指定的那份文档。失焦提交要用它:点网格另一行时,
    /// 检查器在提交真正执行之前就已经换成了新文档。
    /// </summary>
    internal bool CommitEdit(InspectorField field, CollectionRow row)
    {
        if (field.Editor is not { } editor || !Owner.CommitEditor(editor, row, field.Path))
        {
            return false;
        }
        field.Editor = null;
        return true;
    }

    /// <summary>取消编辑。</summary>
    public static void CancelEdit(InspectorField field) => field.Editor = null;

    /// <summary>换类型。</summary>
    public void ChangeType(InspectorField field, BsonKind kind)
    {
        if (Row is not null && field.Path != "_id")
        {
            Owner.ChangeType(Row, field.Path, kind);
        }
    }

    /// <summary>JSON 页文本(当前写法)。</summary>
    public void RefreshJson()
    {
        JsonText = Row is null ? "" : BsonText.Pretty(Row.Document, Owner.Ejson);
        RaisePropertyChanged(nameof(JsonLanguage));
    }

    /// <summary>
    /// 底部的验证状态:集合有 $jsonSchema 时客户端预检这份文档(含暂存修改);
    /// 只是预检 —— 最终以服务器为准,这里的意义是在提交之前就看到"这样改过不了"。
    /// </summary>
    public void RefreshValidation()
    {
        if (Row is null)
        {
            return;
        }
        if (Owner.Info.Validator is not { } validator)
        {
            ValidationText = Loc["Cw_NoValidator"];
            ValidationIcon = "Mongo.shield";
            ValidationToken = "VelaTextMuted";
            return;
        }
        IReadOnlyList<SchemaViolation> violations = JsonSchemaValidator.Validate(validator, Row.Document, Loc);
        if (violations.Count == 0)
        {
            ValidationText = Loc["Cw_SchemaOk"];
            ValidationIcon = "Mongo.shield-check";
            ValidationToken = "VelaStatusConnected";
        }
        else
        {
            ValidationText = Loc.Format("Cw_SchemaViolations", violations.Count, violations[0].Message);
            ValidationIcon = "Mongo.shield-alert";
            ValidationToken = "VelaError";
        }
    }

    /// <summary>「集合信息」页:形态、文档数、大小、索引、验证规则。</summary>
    private async Task LoadInfoAsync()
    {
        CollectionInfo info = Owner.Info;
        InfoLines = BuildInfo(info, Owner.Stats);
        await Owner.LoadStatsAsync().ConfigureAwait(true);
        InfoLines = BuildInfo(info, Owner.Stats);
    }

    private List<CollectionInfoLine> BuildInfo(CollectionInfo info, CollectionStats? stats)
    {
        var lines = new List<CollectionInfoLine>
        {
            new(Loc["Cw_InfoNamespace"], info.Namespace),
            new(Loc["Cw_InfoKind"], info.Kind.ToString())
        };
        if (info.ViewOn is { } source)
        {
            lines.Add(new(Loc["Cw_InfoViewOn"], source));
        }
        if (stats is not null)
        {
            lines.Add(new(Loc["Cw_InfoCount"], BsonText.Grouped(stats.Count)));
            lines.Add(new(Loc["Cw_InfoSize"], BsonText.Bytes(stats.Size)));
            lines.Add(new(Loc["Cw_InfoAvg"], BsonText.Bytes(stats.AvgObjSize)));
            lines.Add(new(Loc["Cw_InfoStorage"], BsonText.Bytes(stats.StorageSize)));
            lines.Add(new(Loc["Cw_InfoIndexes"], $"{stats.IndexCount} · {BsonText.Bytes(stats.TotalIndexSize)}"));
            foreach ((string name, long size) in stats.IndexSizes)
            {
                lines.Add(new("  " + name, BsonText.Bytes(size)));
            }
        }
        if (info.CappedSize is { } capped)
        {
            lines.Add(new(Loc["Cw_InfoCapped"], BsonText.Bytes(capped)));
        }
        lines.Add(new(Loc["Cw_InfoValidator"], info.Validator is null ? Loc["Common_None"] : $"{info.ValidationLevel} · {info.ValidationAction}"));
        return lines;
    }
}
