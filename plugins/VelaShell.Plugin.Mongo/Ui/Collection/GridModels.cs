using Avalonia.Media;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;
using VelaShell.Plugin.Mongo.Staging;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>集合工作台的三种视图(右上角分段)。</summary>
internal enum CollectionViewMode
{
    /// <summary>网格(设计稿 01)。</summary>
    Grid,

    /// <summary>树(设计稿 13)。</summary>
    Tree,

    /// <summary>JSON 卡片(设计稿 02)。</summary>
    Json
}

/// <summary>一行在暂存区里的状态(设计稿 09「网格单元格状态」)。</summary>
internal enum CollectionRowState
{
    /// <summary>原样。</summary>
    Normal,

    /// <summary>有暂存修改。</summary>
    Modified,

    /// <summary>暂存的新文档。</summary>
    Added,

    /// <summary>暂存删除。</summary>
    Deleted
}

/// <summary>
/// 网格的一列:本页文档顶层字段的并集,按首次出现的顺序(<c>_id</c> 在前)。
/// 列头是「字段名 + 主导类型小字」—— 同一列里混着几种类型很常见,标出主导类型让人一眼看出"这列大体是什么"。
/// 钻进一个数组 / 对象之后(<see cref="GridDrill" />),列是那一层的子字段。
/// </summary>
internal sealed class CollectionColumn : ObservableObject
{
    private double _width;
    private int _sortDirection;

    /// <summary>构造。</summary>
    /// <param name="name">字段名(<paramref name="isElementValue" /> 时只是列头文字)。</param>
    /// <param name="kind">主导类型。</param>
    /// <param name="width">初始宽度。</param>
    /// <param name="isElementValue">钻进数组后,标量元素自身的那一列(路径就是元素本身,不再往下接字段名)。</param>
    public CollectionColumn(string name, BsonKind kind, double width, bool isElementValue = false)
    {
        Name = name;
        Kind = kind;
        _width = width;
        IsElementValue = isElementValue;
    }

    /// <summary>字段名。</summary>
    public string Name { get; }

    /// <summary>数组里标量元素自身的那一列。</summary>
    public bool IsElementValue { get; }

    /// <summary>主导类型(本页里出现最多的那种,缺失不计)。</summary>
    public BsonKind Kind { get; }

    /// <summary>列头小字(<c>ObjectId</c>、<c>Decimal128</c>、布尔写短一点:<c>Bool</c>)。</summary>
    public string KindLabel => Kind switch
    {
        BsonKind.Boolean => "Bool",
        BsonKind.Missing => "",
        _ => BsonKinds.Name(Kind)
    };

    /// <summary>数值列右对齐(列头也跟着)。</summary>
    public bool IsNumeric => BsonKinds.IsNumeric(Kind);

    /// <summary>宽度(可拖)。</summary>
    public double Width
    {
        get => _width;
        set => SetProperty(ref _width, Math.Clamp(value, 48, 900));
    }

    /// <summary>排序方向:1 升序、-1 降序、0 不排序(列头的箭头)。</summary>
    public int SortDirection
    {
        get => _sortDirection;
        set
        {
            if (SetProperty(ref _sortDirection, value))
            {
                RaisePropertiesChanged(nameof(IsSorted), nameof(SortIcon));
            }
        }
    }

    /// <summary>按这一列排序着。</summary>
    public bool IsSorted => _sortDirection != 0;

    /// <summary>排序箭头图标。</summary>
    public string SortIcon => _sortDirection > 0 ? "Mongo.arrow-up" : "Mongo.arrow-down";
}

/// <summary>
/// 网格的一行。单元格按需生成(虚拟化只给可见的几十行建容器,大页里绝大多数行的单元格从来不会被算出来)。
/// </summary>
internal sealed class CollectionRow : ObservableObject
{
    private readonly CollectionTabViewModel _owner;
    private IReadOnlyList<CollectionCell>? _cells;
    private BsonDocument _document;
    private CollectionRowState _state;

    /// <summary>服务器上读到的一行。</summary>
    public CollectionRow(CollectionTabViewModel owner, int number, BsonDocument original)
    {
        _owner = owner;
        Root = this;
        Number = number;
        Original = original;
        _document = original;
        Recompute();
    }

    /// <summary>暂存的新文档那一行。</summary>
    public CollectionRow(CollectionTabViewModel owner, int number, StagedInsert insert)
    {
        _owner = owner;
        Root = this;
        Number = number;
        Insert = insert;
        _document = insert.Document;
        Recompute();
    }

    /// <summary>
    /// 钻入后子表里的一行:数组的第 <paramref name="index" /> 个元素,或对象本身(<paramref name="index" /> 为 -1)。
    /// <para>
    /// 它与 <paramref name="root" /> 共用同一份文档、同一个暂存身份(<see cref="Original" /> / <see cref="Insert" /> 照抄),
    /// 单元格的路径是从文档根算起的**绝对路径**(<c>items.2.qty</c>)—— 所以暂存、提交、撤销、冲突处理
    /// 一行都不用改:在子表里改一格,与在树视图里改那个嵌套字段是同一件事。
    /// </para>
    /// </summary>
    public CollectionRow(CollectionTabViewModel owner, CollectionRow root, GridDrill drill, int index)
    {
        _owner = owner;
        Root = root;
        Drill = drill;
        Number = index;
        Original = root.Original;
        Insert = root.Insert;
        _document = root.Document;
        Recompute();
    }

    /// <summary>行号(1 起,跨页连续);子表里是数组下标。</summary>
    public int Number { get; }

    /// <summary>
    /// 行号文字:新增行是 <c>*</c>;子表里是数组下标(与路径里的 <c>items.2</c> 同一个数,从 0 起),对象那一行留空。
    /// </summary>
    public string NumberText => Drill is { } drill
        ? drill.IsArray ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""
        : Insert is null ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "*";

    /// <summary>所属文档的那一行(顶层行就是自己)。</summary>
    public CollectionRow Root { get; }

    /// <summary>子表那一层;顶层行为 <see langword="null" />。</summary>
    public GridDrill? Drill { get; }

    /// <summary>
    /// 这一行在文档里的位置:顶层为空;数组元素是 <c>items.2</c>;对象那一行是对象自身的路径。
    /// </summary>
    public string BasePath => Drill is not { } drill ? ""
        : drill.IsArray ? $"{drill.Path}.{Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : drill.Path;

    /// <summary>某一列在这一行里的绝对路径。</summary>
    public string PathOf(CollectionColumn column) =>
        Drill is null ? column.Name : column.IsElementValue ? BasePath : $"{BasePath}.{column.Name}";

    /// <summary>服务器版本;新增行为 <see langword="null" />。</summary>
    public BsonDocument? Original { get; }

    /// <summary>暂存的新文档;已有文档为 <see langword="null" />。</summary>
    public StagedInsert? Insert { get; }

    /// <summary><c>_id</c>(新增行还没有)。</summary>
    public BsonValue? Id => Original?.GetValue("_id", BsonNull.Value);

    /// <summary>所属工作台(单元格取文案用)。</summary>
    internal CollectionTabViewModel Owner => _owner;

    /// <summary>界面上该显示的版本(原像 + 暂存修改)。</summary>
    public BsonDocument Document => _document;

    /// <summary>状态。</summary>
    public CollectionRowState State => _state;

    /// <summary>有暂存修改。</summary>
    public bool IsModified => _state == CollectionRowState.Modified;

    /// <summary>暂存的新文档。</summary>
    public bool IsAdded => _state == CollectionRowState.Added;

    /// <summary>暂存删除。</summary>
    public bool IsDeleted => _state == CollectionRowState.Deleted;

    /// <summary>行号格里的状态图标(删除 <c>−</c>、新增 <c>+</c>)。</summary>
    public bool HasStateIcon => _state is CollectionRowState.Added or CollectionRowState.Deleted;

    /// <summary>行号格状态图标的键。</summary>
    public string StateIcon => _state == CollectionRowState.Added ? "Mongo.plus" : "Mongo.minus";

    /// <summary>行号格状态图标的颜色令牌。</summary>
    public string StateIconToken => _state == CollectionRowState.Added ? "VelaStatusConnected" : "VelaError";

    /// <summary>单元格。</summary>
    public IReadOnlyList<CollectionCell> Cells => _cells ??= BuildCells();

    /// <summary>暂存区变了之后重算这一行(只对已经生成过单元格的行做实事)。</summary>
    public void Recompute()
    {
        StagingArea staging = _owner.Staging;
        if (Insert is not null)
        {
            _document = Insert.Document;
            _state = CollectionRowState.Added;
        }
        else if (staging.IsDeleted(Id))
        {
            _document = Original!;
            _state = CollectionRowState.Deleted;
        }
        else
        {
            _document = staging.Current(Original!);
            _state = staging.EditOf(Id) is { IsEmpty: false } ? CollectionRowState.Modified : CollectionRowState.Normal;
        }
        if (_cells is not null)
        {
            _cells = null;
            RaisePropertyChanged(nameof(Cells));
        }
        RaisePropertiesChanged(nameof(Document), nameof(State), nameof(IsModified), nameof(IsAdded), nameof(IsDeleted),
            nameof(HasStateIcon), nameof(StateIcon), nameof(StateIconToken));
    }

    /// <summary>某一列的单元格。</summary>
    public CollectionCell? CellOf(CollectionColumn column) => Cells.FirstOrDefault(c => ReferenceEquals(c.Column, column));

    private List<CollectionCell> BuildCells()
    {
        StagedEdit? edit = Insert is null ? _owner.Staging.EditOf(Id) : null;
        IReadOnlyList<CollectionColumn> columns = Drill?.Columns ?? _owner.Columns;
        var cells = new List<CollectionCell>(columns.Count);
        foreach (CollectionColumn column in columns)
        {
            string path = PathOf(column);
            // 顶层按字段名直取(字段名里带点也取得到);子表按绝对路径走。
            BsonValue? value = Drill is null
                ? _document.TryGetValue(column.Name, out BsonValue v) ? v : null
                : BsonPath.Get(_document, path);
            ChangeRelation relation = edit?.RelationOf(path) ?? ChangeRelation.None;
            cells.Add(new CollectionCell(this, column, path, value, relation != ChangeRelation.None,
                _owner.CurrentColumn == column && _owner.IsCurrentRow(this)));
        }
        return cells;
    }
}

/// <summary>
/// 一个单元格。内容一经生成就不变(暂存区变了整行重建),只有"当前格"与"编辑中"两种状态会动。
/// </summary>
internal sealed class CollectionCell : ObservableObject
{
    private bool _isCurrent;
    private InlineValueEditor? _editor;

    /// <summary>构造。</summary>
    public CollectionCell(CollectionRow row, CollectionColumn column, string path, BsonValue? value, bool isModified, bool isCurrent)
    {
        Row = row;
        Column = column;
        Path = path;
        Value = value;
        Kind = BsonKinds.Of(value);
        IsModified = isModified;
        _isCurrent = isCurrent;
        (Prefix, Text) = Split(value, Kind, row);
    }

    /// <summary>所在行。</summary>
    public CollectionRow Row { get; }

    /// <summary>所在列。</summary>
    public CollectionColumn Column { get; }

    /// <summary>值;字段不存在为 <see langword="null" />。</summary>
    public BsonValue? Value { get; }

    /// <summary>类型。</summary>
    public BsonKind Kind { get; }

    /// <summary>容器的前缀(<c>{3}</c>、<c>[4]</c>);标量为空。</summary>
    public string Prefix { get; }

    /// <summary>主文字。</summary>
    public string Text { get; }

    /// <summary>有没有前缀。</summary>
    public bool HasPrefix => Prefix.Length > 0;

    /// <summary>暂存修改过(橙色淡底 + 左 2px 橙条)。</summary>
    public bool IsModified { get; }

    /// <summary>空的对象 / 数组(后面跟一句淡色的「(空对象)」「0 项」)。</summary>
    public bool IsEmptyContainer => Value is BsonDocument { ElementCount: 0 } or BsonArray { Count: 0 };

    /// <summary>字段不存在。</summary>
    public bool IsMissing => Kind == BsonKind.Missing;

    /// <summary>null(斜体)。</summary>
    public bool IsNull => Kind == BsonKind.Null || (Row.IsAdded && Path == "_id");

    /// <summary>数值右对齐。</summary>
    public TextAlignment TextAlignment => BsonKinds.IsNumeric(Kind) ? TextAlignment.Right : TextAlignment.Left;

    /// <summary>
    /// 主文字的颜色令牌。字符串用正文色而不是类型色(设计稿 01:单号、状态这些"读的东西"要显眼),
    /// 容器的摘要退一档;改过的格子统一橙色。
    /// </summary>
    public string TextToken => IsModified ? "VelaWarning" : Kind switch
    {
        BsonKind.Object or BsonKind.Array when IsEmptyContainer => "VelaTextMuted",
        BsonKind.String => "VelaTextPrimary",
        BsonKind.Object => "VelaTextSecondary",
        BsonKind.Array => "VelaTextTertiary",
        BsonKind.Missing => "VelaTextMuted",
        _ when Row.IsAdded && Path == "_id" => "VelaTextMuted",
        _ => BsonKinds.ColorToken(Kind)
    };

    /// <summary>
    /// 主文字走界面字体还是等宽字体:等宽字体多半不带中文字形,回落出来的字宽忽大忽小;
    /// 摘要(<c>陈立 · VIP</c>)与含中文的字符串用界面字体,与设计稿一致。
    /// </summary>
    public bool UseUiFont => Kind is BsonKind.Object || (Kind is BsonKind.String or BsonKind.Array && !IsAscii(Text))
                             || (Row.IsAdded && Path == "_id");

    /// <summary>当前格(键盘 F2 / Enter 编辑的那一格)。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>编辑器;不在编辑为 <see langword="null" />。</summary>
    public InlineValueEditor? Editor
    {
        get => _editor;
        set
        {
            if (SetProperty(ref _editor, value))
            {
                RaisePropertyChanged(nameof(IsEditing));
            }
        }
    }

    /// <summary>正在编辑。</summary>
    public bool IsEditing => _editor is not null;

    /// <summary>值从文档根算起的点路径(顶层字段就是列名;子表里是 <c>items.2.qty</c>)。</summary>
    public string Path { get; }

    /// <summary>能钻进去看(对象 / 数组,空的也算 —— 进去才能往空数组里加元素)。</summary>
    public bool IsContainer => Kind is BsonKind.Object or BsonKind.Array;

    private (string Prefix, string Text) Split(BsonValue? value, BsonKind kind, CollectionRow row)
    {
        Loc loc = row.Owner.Loc;
        if (row.IsAdded && value is null && kind == BsonKind.Missing && Path == "_id")
        {
            return ("", loc["Cw_AutoId"]);
        }
        switch (kind)
        {
            case BsonKind.Object:
            {
                BsonDocument doc = value!.AsBsonDocument;
                return doc.ElementCount == 0 ? ("{ }", loc["Cw_EmptyObject"]) : ($"{{{doc.ElementCount}}}", BsonText.Summary(doc));
            }
            case BsonKind.Array:
            {
                BsonArray array = value!.AsBsonArray;
                return array.Count == 0 ? ("[ ]", loc.Format("Cw_ItemCount", 0)) : ($"[{array.Count}]", BsonText.ArraySummary(array));
            }
            default:
                return ("", BsonText.Cell(value));
        }
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// 单元格 / 字段的内联编辑器:文本 + 目标类型 + 可选的候选值(布尔、本页里取值不多的字符串列、日期的"现在")。
/// </summary>
internal sealed class InlineValueEditor : ObservableObject
{
    private string _text;
    private string? _error;

    /// <summary>构造。</summary>
    /// <param name="text">初始文本(<see cref="BsonEdit.EditText" />)。</param>
    /// <param name="kind">目标类型。</param>
    /// <param name="choices">候选值;没有为空。</param>
    public InlineValueEditor(string text, BsonKind kind, IReadOnlyList<string> choices)
    {
        _text = text;
        Kind = kind;
        Choices = choices;
    }

    /// <summary>编辑中的文本。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                Error = null;
            }
        }
    }

    /// <summary>目标类型。</summary>
    public BsonKind Kind { get; }

    /// <summary>候选值(下拉箭头里的那些)。</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>有没有候选值。</summary>
    public bool HasChoices => Choices.Count > 0;

    /// <summary>解析失败的原因;没有为 <see langword="null" />。</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    /// <summary>有错。</summary>
    public bool HasError => _error is not null;
}
