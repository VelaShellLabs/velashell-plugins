using System.Collections.ObjectModel;
using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Shell;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>结果区的页签种类。</summary>
internal enum QueryPaneKind
{
    /// <summary>一份结果(结果 1 / 结果 2 …)。</summary>
    Result,

    /// <summary>执行计划。</summary>
    Explain,

    /// <summary>消息。</summary>
    Messages
}

/// <summary>
/// 结果区下面那一排页签里的一个(设计稿 03:「结果 1 find · 100」「结果 2 aggregate · 24」「执行计划」「消息 2」)。
/// </summary>
internal abstract class QueryPane(Loc loc) : ObservableObject
{
    private bool _isSelected;

    /// <summary>文案表。</summary>
    public Loc Loc { get; } = loc;

    /// <summary>种类。</summary>
    public abstract QueryPaneKind PaneKind { get; }

    /// <summary>标题(<c>结果 2</c>)。</summary>
    public abstract string Header { get; }

    /// <summary>标题右边的等宽小字(<c>aggregate · 24</c>)。</summary>
    public virtual string Detail => "";

    /// <summary>有没有小字。</summary>
    public bool HasDetail => Detail.Length > 0;

    /// <summary>图标。</summary>
    public abstract string IconKey { get; }

    /// <summary>图标颜色(选中时信息色)。</summary>
    public string IconToken => _isSelected ? "VelaInfo" : "VelaTextTertiary";

    /// <summary>选中。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaisePropertyChanged(nameof(IconToken));
            }
        }
    }

    /// <summary>通知标题小字变了。</summary>
    protected void RaiseDetail()
    {
        RaisePropertyChanged(nameof(Detail));
        RaisePropertyChanged(nameof(HasDetail));
    }
}

/// <summary>网格的一列(类型化列头:名字 + 主类型)。</summary>
/// <param name="Name">字段名。</param>
/// <param name="Kind">主类型。</param>
internal sealed record ResultColumn(string Name, BsonKind Kind)
{
    /// <summary>类型显示名。</summary>
    public string TypeName => BsonKinds.Name(Kind);

    /// <summary>数值列(右对齐)。</summary>
    public bool Numeric => BsonKinds.IsNumeric(Kind);

    /// <summary>钻进数组后,标量元素自身的那一列(列头就叫「值」,路径是元素本身)。</summary>
    public bool IsElementValue { get; init; }
}

/// <summary>
/// 一份结果:网格 / 树 / JSON 三种看法共用同一批文档。可以「固定」—— 固定的结果不被下一次运行覆盖。
/// </summary>
internal sealed class QueryResultSet : QueryPane
{
    private readonly EjsonMode _ejson;
    private IReadOnlyList<ResultColumn>? _columns;
    private ObservableCollection<ResultTreeRow>? _treeRows;
    private string? _json;
    private int _number;
    private bool _isPinned;
    private string _viewMode = "grid";

    /// <summary>构造。</summary>
    public QueryResultSet(Loc loc, EjsonMode ejson, int number, ShellResult result, ShellStatement statement)
        : base(loc)
    {
        _ejson = ejson;
        _number = number;
        Result = result;
        Statement = statement;
        TogglePinCommand = new RelayCommand(() => IsPinned = !IsPinned);
    }

    /// <inheritdoc />
    public override QueryPaneKind PaneKind => QueryPaneKind.Result;

    /// <summary>执行结果。</summary>
    public ShellResult Result { get; }

    /// <summary>来源语句。</summary>
    public ShellStatement Statement { get; }

    /// <summary>文档。</summary>
    public IReadOnlyList<BsonDocument> Documents => Result.Documents;

    /// <summary>编号(结果 N)。</summary>
    public int Number
    {
        get => _number;
        set
        {
            if (SetProperty(ref _number, value))
            {
                RaisePropertyChanged(nameof(Header));
            }
        }
    }

    /// <inheritdoc />
    public override string Header => Loc.Format("Query_ResultN", _number);

    /// <inheritdoc />
    public override string Detail => $"{Result.Operation} · {Documents.Count.ToString(CultureInfo.InvariantCulture)}{(Result.Truncated ? "+" : "")}";

    /// <inheritdoc />
    public override string IconKey => _isPinned ? "Mongo.pin" : "Mongo.table";

    /// <summary>固定了(下次运行不覆盖)。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                RaisePropertyChanged(nameof(IconKey));
            }
        }
    }

    /// <summary>固定 / 取消固定。</summary>
    public RelayCommand TogglePinCommand { get; }

    /// <summary>被截断了(只取了前 N 份)。</summary>
    public bool Truncated => Result.Truncated;

    /// <summary>截断提示。</summary>
    public string TruncatedText => Loc.Format("Query_TruncatedNotice", Documents.Count);

    /// <summary>没有文档。</summary>
    public bool IsEmpty => Documents.Count == 0;

    /// <summary>当前看法(<c>grid</c> / <c>tree</c> / <c>json</c>)。</summary>
    public string ViewMode
    {
        get => _viewMode;
        set
        {
            if (SetProperty(ref _viewMode, value))
            {
                RaisePropertyChanged(nameof(IsGrid));
                RaisePropertyChanged(nameof(IsTree));
                RaisePropertyChanged(nameof(IsJson));
                RaisePropertyChanged(nameof(TreeRows));
                RaisePropertyChanged(nameof(JsonText));
            }
        }
    }

    /// <summary>网格。</summary>
    public bool IsGrid => _viewMode == "grid";

    /// <summary>树。</summary>
    public bool IsTree => _viewMode == "tree";

    /// <summary>JSON。</summary>
    public bool IsJson => _viewMode == "json";

    /// <summary>JSON 视图的语法(Shell 写法按 mongosh 着色,其余按 JSON)。</summary>
    public CodeLanguage JsonLanguage => _ejson == EjsonMode.Shell ? CodeLanguage.Shell : CodeLanguage.Json;

    /// <summary>
    /// 列:所有文档顶层字段的并集,按首次出现排序,<c>_id</c> 最前;列头类型取出现最多的那种。
    /// </summary>
    public IReadOnlyList<ResultColumn> Columns => _columns ??= BuildColumns(Documents);

    /// <summary>树的行(展开 / 折叠就地增删);只在切到树视图时才建。</summary>
    public ObservableCollection<ResultTreeRow> TreeRows
    {
        get
        {
            if (_treeRows is null && IsTree)
            {
                _treeRows = [];
                for (int i = 0; i < Documents.Count; i++)
                {
                    _treeRows.Add(new ResultTreeRow(this, 0, (i + 1).ToString(CultureInfo.InvariantCulture), Documents[i], isDocument: true));
                }
            }
            return _treeRows ?? [];
        }
    }

    /// <summary>JSON 全文;只在切到 JSON 视图时才拼(一千份文档的 JSON 不小)。</summary>
    public string JsonText => IsJson ? ToJson() : "";

    /// <summary>整批结果的 JSON(复制 / 导出用,与视图无关)。</summary>
    public string ToJson() => _json ??= BuildJson(Documents, _ejson);

    /// <summary>
    /// 文档数组的多行 JSON:逐份按连接设置的扩展 JSON 写法缩进,再包一层方括号。
    /// 驱动的写出器只会缩进文档,整个数组交给它会挤成一行 —— 一千份文档挤在一行里没法读。
    /// </summary>
    internal static string BuildJson(IReadOnlyList<BsonDocument> documents, EjsonMode mode)
    {
        var builder = new System.Text.StringBuilder("[\n");
        for (int i = 0; i < documents.Count; i++)
        {
            builder.Append("  ").Append(BsonText.Pretty(documents[i], mode).Replace("\n", "\n  ", StringComparison.Ordinal));
            builder.Append(i < documents.Count - 1 ? ",\n" : "\n");
        }
        return builder.Append(']').ToString();
    }

    /// <summary>展开 / 折叠树的一行。</summary>
    internal void Toggle(ResultTreeRow row)
    {
        if (_treeRows is null || !row.IsExpandable)
        {
            return;
        }
        int index = _treeRows.IndexOf(row);
        if (index < 0)
        {
            return;
        }
        if (row.IsExpanded)
        {
            int end = index + 1;
            while (end < _treeRows.Count && _treeRows[end].Depth > row.Depth)
            {
                end++;
            }
            for (int k = end - 1; k > index; k--)
            {
                _treeRows.RemoveAt(k);
            }
            row.IsExpanded = false;
            return;
        }
        int insert = index + 1;
        foreach (ResultTreeRow child in row.CreateChildren())
        {
            _treeRows.Insert(insert++, child);
        }
        row.IsExpanded = true;
    }

    internal static List<ResultColumn> BuildColumns(IReadOnlyList<BsonDocument> documents)
    {
        var order = new List<string>();
        var kinds = new Dictionary<string, Dictionary<BsonKind, int>>(StringComparer.Ordinal);
        foreach (BsonDocument document in documents)
        {
            foreach (BsonElement element in document)
            {
                if (!kinds.TryGetValue(element.Name, out Dictionary<BsonKind, int>? counts))
                {
                    counts = [];
                    kinds[element.Name] = counts;
                    order.Add(element.Name);
                }
                BsonKind kind = BsonKinds.Of(element.Value);
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }
        if (order.Remove("_id"))
        {
            order.Insert(0, "_id");
        }
        return
        [
            .. order.Select(name =>
            {
                Dictionary<BsonKind, int> counts = kinds[name];
                BsonKind dominant = counts.Where(static p => p.Key != BsonKind.Null).OrderByDescending(static p => p.Value).Select(static p => p.Key)
                    .DefaultIfEmpty(BsonKind.Null).First();
                return new ResultColumn(name, dominant);
            })
        ];
    }
}

/// <summary>结果树的一行(键、值预览、类型;容器可展开)。</summary>
internal sealed class ResultTreeRow : ObservableObject
{
    private readonly QueryResultSet _owner;
    private bool _isExpanded;

    /// <summary>构造。</summary>
    public ResultTreeRow(QueryResultSet owner, int depth, string key, BsonValue value, bool isDocument = false)
    {
        _owner = owner;
        Depth = depth;
        Key = key;
        Value = value;
        IsDocumentRow = isDocument;
        ToggleCommand = new RelayCommand(() => owner.Toggle(this));
    }

    /// <summary>深度。</summary>
    public int Depth { get; }

    /// <summary>键(顶层是序号)。</summary>
    public string Key { get; }

    /// <summary>值。</summary>
    public BsonValue Value { get; }

    /// <summary>顶层文档行(键用序号色)。</summary>
    public bool IsDocumentRow { get; }

    /// <summary>类型。</summary>
    public BsonKind Kind => BsonKinds.Of(Value);

    /// <summary>类型名。</summary>
    public string TypeName => BsonKinds.Name(Kind);

    /// <summary>值预览。</summary>
    public string Preview => Kind switch
    {
        BsonKind.Object when IsDocumentRow => Value.AsBsonDocument.GetValue("_id", null) is { } id
            ? $"{{ _id: {BsonText.Inline(id)} … }}  {_owner.Loc.Format("Bson_FieldsSummary", Value.AsBsonDocument.ElementCount)}"
            : _owner.Loc.Format("Bson_FieldsSummary", Value.AsBsonDocument.ElementCount),
        BsonKind.Object => _owner.Loc.Format("Bson_FieldsSummary", Value.AsBsonDocument.ElementCount),
        BsonKind.Array => _owner.Loc.Format("Bson_ItemsSummary", Value.AsBsonArray.Count),
        _ => BsonText.Inline(Value)
    };

    /// <summary>值颜色令牌。</summary>
    public string ColorToken => BsonKinds.ColorToken(Kind);

    /// <summary>可展开。</summary>
    public bool IsExpandable => Kind is BsonKind.Object or BsonKind.Array && (Value is BsonDocument { ElementCount: > 0 } || Value is BsonArray { Count: > 0 });

    /// <summary>展开着。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RaisePropertyChanged(nameof(ChevronKey));
            }
        }
    }

    /// <summary>展开箭头。</summary>
    public string ChevronKey => _isExpanded ? "Mongo.chevron-down" : "Mongo.chevron-right";

    /// <summary>左缩进。</summary>
    public double Indent => 8 + Depth * 16;

    /// <summary>展开 / 折叠。</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>子行。</summary>
    internal IEnumerable<ResultTreeRow> CreateChildren() => Value switch
    {
        BsonDocument document => document.Elements.Select(e => new ResultTreeRow(_owner, Depth + 1, e.Name, e.Value)),
        BsonArray array => array.Select((v, i) => new ResultTreeRow(_owner, Depth + 1, $"[{i}]", v)),
        _ => []
    };
}

/// <summary>消息的语气。</summary>
internal enum QueryMessageKind
{
    /// <summary>成功。</summary>
    Ok,

    /// <summary>说明。</summary>
    Info,

    /// <summary>警告(结果被截断、写被取消)。</summary>
    Warning,

    /// <summary>失败。</summary>
    Error
}

/// <summary>消息页的一条。</summary>
/// <param name="At">时间。</param>
/// <param name="Kind">语气。</param>
/// <param name="Text">正文(<c>orders.updateMany:匹配 3,修改 3</c>)。</param>
/// <param name="Statement">相关语句(单行化)。</param>
/// <param name="Elapsed">耗时文字。</param>
internal sealed record QueryMessage(DateTime At, QueryMessageKind Kind, string Text, string? Statement, string? Elapsed)
{
    /// <summary>时间文字。</summary>
    public string Time => At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>图标。</summary>
    public string IconKey => Kind switch
    {
        QueryMessageKind.Ok => "Mongo.circle-check",
        QueryMessageKind.Warning => "Mongo.triangle-alert",
        QueryMessageKind.Error => "Mongo.circle-x",
        _ => "Mongo.info"
    };

    /// <summary>图标颜色。</summary>
    public string IconToken => Kind switch
    {
        QueryMessageKind.Ok => "VelaStatusConnected",
        QueryMessageKind.Warning => "VelaWarning",
        QueryMessageKind.Error => "VelaError",
        _ => "VelaInfo"
    };

    /// <summary>有没有语句。</summary>
    public bool HasStatement => !string.IsNullOrEmpty(Statement);

    /// <summary>有没有耗时。</summary>
    public bool HasElapsed => !string.IsNullOrEmpty(Elapsed);
}

/// <summary>「消息」页签。</summary>
internal sealed class MessagesPane : QueryPane
{
    /// <summary>构造。</summary>
    public MessagesPane(Loc loc)
        : base(loc)
    {
        Messages.CollectionChanged += (_, _) =>
        {
            RaiseDetail();
            RaisePropertyChanged(nameof(IsEmpty));
        };
    }

    /// <inheritdoc />
    public override QueryPaneKind PaneKind => QueryPaneKind.Messages;

    /// <inheritdoc />
    public override string Header => Loc["Query_TabMessages"];

    /// <inheritdoc />
    public override string Detail => Messages.Count == 0 ? "" : Messages.Count.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string IconKey => "Mongo.message-square-text";

    /// <summary>消息(新的在后)。</summary>
    public ObservableCollection<QueryMessage> Messages { get; } = [];

    /// <summary>空。</summary>
    public bool IsEmpty => Messages.Count == 0;
}
