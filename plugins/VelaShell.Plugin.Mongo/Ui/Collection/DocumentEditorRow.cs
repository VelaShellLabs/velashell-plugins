using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>表单行的状态(决定整行底色、左侧 2px 色条与行尾提示)。</summary>
internal enum DocumentEditorRowState
{
    /// <summary>没变。</summary>
    None,

    /// <summary>改了值或类型(橙)。</summary>
    Modified,

    /// <summary>原文档里没有(绿)。</summary>
    Added,

    /// <summary>违反验证规则,但集合的 validationAction 是 warn(橙字,不挡保存)。</summary>
    Warning,

    /// <summary>错误:字段名不合法、值解析不了、违反验证规则(红,挡保存)。</summary>
    Error
}

/// <summary>
/// 表单里的一行 = 文档里的一个字段(或数组里的一项)。
/// <para>
/// 行树就是编辑中的文档本身:容器行的值由子行拼出来,标量行持有值与编辑文本。
/// 行只管"自己这一格怎么编辑";增删、排序、展开、校验与差异由对话框视图模型统一算 ——
/// 一行的状态取决于它在整份文档里的位置(原文档同一路径上有没有值、验证规则落在哪条路径上),
/// 单看一行算不出来。
/// </para>
/// </summary>
internal sealed class DocumentEditorRow : ObservableObject
{
    private readonly DocumentEditorDialogViewModel _owner;
    private string _name;
    private BsonValue _value = BsonNull.Value;
    private string _text = "";
    private string? _parseError;
    private bool _isExpanded;

    /// <summary>构造(容器值会递归建出子行)。</summary>
    /// <param name="owner">所属对话框。</param>
    /// <param name="name">字段名(数组项为下标)。</param>
    /// <param name="value">值。</param>
    /// <param name="parent">父行;顶层为 <see langword="null" />。</param>
    public DocumentEditorRow(DocumentEditorDialogViewModel owner, string name, BsonValue value, DocumentEditorRow? parent)
    {
        _owner = owner;
        _name = name;
        Parent = parent;
        LoadValue(value);
        ToggleCommand = new RelayCommand(() => _owner.Toggle(this), () => IsContainer);
        AddCommand = new RelayCommand(() => _owner.AddFrom(this), () => _owner.CanEdit);
        RemoveCommand = new RelayCommand(() => _owner.Remove(this), () => CanRemove);
        RevertCommand = new RelayCommand(() => _owner.Revert(this));
        GenerateCommand = new RelayCommand(GenerateId, () => IsValueEditable);
        SetStringCommand = new RelayCommand(() => ChangeKind(BsonKind.String), () => IsValueEditable);
        BeginItemCommand = new RelayCommand(() => IsAddingItem = true, () => IsValueEditable);
        CommitItemCommand = new RelayCommand(CommitItem);
        CancelItemCommand = new RelayCommand(() => IsAddingItem = false);
    }

    /// <summary>文案表(行模板里直接绑)。</summary>
    public Loc Loc => _owner.Loc;

    // ── 位置 ────────────────────────────────────────────────────────────────

    /// <summary>父行。</summary>
    public DocumentEditorRow? Parent { get; internal set; }

    /// <summary>子行(对象的字段 / 数组的项)。</summary>
    public ObservableCollection<DocumentEditorRow> Children { get; } = [];

    /// <summary>嵌套深度(顶层 0)。</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>字段名列的内边距:每深一层缩进 16(设计稿 customer 的子行左距 20 = 4 + 16)。</summary>
    public Thickness KeyPadding => new(4 + (Depth * 16), 0, 8, 0);

    /// <summary>点路径(<c>customer.level</c>、<c>items.0.qty</c>)。</summary>
    public string Path => Parent is null ? _name : Parent.Path + "." + _name;

    /// <summary>路径分段。</summary>
    public IReadOnlyList<string> Segments
    {
        get
        {
            var segments = new List<string>();
            for (DocumentEditorRow? row = this; row is not null; row = row.Parent)
            {
                segments.Insert(0, row._name);
            }
            return segments;
        }
    }

    /// <summary>
    /// 抽样里的合并路径:数组下标那一段去掉(<c>items.0.sku</c> → <c>items.sku</c>),
    /// 与 <see cref="DocumentEditorSchema" /> 的路径口径一致。
    /// </summary>
    public string SchemaPath => Parent is null ? _name : IsArrayItem ? Parent.SchemaPath : BsonPath.Join(Parent.SchemaPath, _name);

    /// <summary>是不是数组里的一项(名字是下标,不能改)。</summary>
    public bool IsArrayItem => Parent?.Kind == BsonKind.Array;

    /// <summary>顶层 <c>_id</c>。</summary>
    public bool IsId => Parent is null && _name == "_id";

    /// <summary>
    /// 锁定的 <c>_id</c>:已有文档的 <c>_id</c> 不可改(服务器上它不可变;要换 _id 走「另存为新文档」)。
    /// 新建文档的 <c>_id</c> 名字锁定、值可改(products 那种字符串主键)。
    /// </summary>
    public bool IsIdLocked => IsId && _owner.IsExisting;

    /// <summary>字段名能不能改。</summary>
    public bool IsNameEditable => !IsId && !IsArrayItem && _owner.CanEdit;

    /// <summary>值能不能改。</summary>
    public bool IsValueEditable => !IsIdLocked && _owner.CanEdit;

    /// <summary>值框只读(锁定的 _id、只读视图)。</summary>
    public bool IsValueReadOnly => !IsValueEditable;

    /// <summary>类型下拉能不能动。</summary>
    public bool IsKindEditable => IsValueEditable;

    /// <summary>能不能删(_id 不能)。</summary>
    public bool CanRemove => !IsId && _owner.CanEdit;

    /// <summary>能不能拖动排序(数组项与对象字段都能;_id 不动)。</summary>
    public bool CanDrag => !IsId && _owner.CanEdit;

    // ── 名字 ────────────────────────────────────────────────────────────────

    /// <summary>字段名(数组项是下标)。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!IsNameEditable || value is null || _name == value)
            {
                return;
            }
            _name = value;
            RaisePropertiesChanged(nameof(Name), nameof(DisplayName), nameof(Path));
            _owner.OnEdited(this, structural: false);
        }
    }

    /// <summary>显示名:数组项写成 <c>[0]</c>。</summary>
    public string DisplayName => IsArrayItem ? $"[{_name}]" : _name;

    /// <summary>字段名本身的问题(空、重复、<c>$</c> 开头、含点号);由视图模型在重算时填。</summary>
    public string? NameError
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    /// <summary>数组项重排之后改下标。</summary>
    internal void SetIndex(int index)
    {
        string name = index.ToString(CultureInfo.InvariantCulture);
        if (_name != name)
        {
            _name = name;
            RaisePropertiesChanged(nameof(Name), nameof(DisplayName), nameof(Path));
        }
    }

    // ── 类型与值 ────────────────────────────────────────────────────────────

    /// <summary>类型。</summary>
    public BsonKind Kind { get; private set; }

    /// <summary>类型下拉的选中项(设它 = 换类型)。</summary>
    public BsonKind SelectedKind
    {
        get => Kind;
        set
        {
            if (value != Kind)
            {
                ChangeKind(value);
            }
        }
    }

    /// <summary>类型下拉的可选项(设计稿 01 的类型菜单;当前是少见类型时把它也放进去,否则下拉显示为空)。</summary>
    public IReadOnlyList<BsonKind> KindOptions =>
        BsonKinds.Editable.Contains(Kind) ? BsonKinds.Editable : [.. BsonKinds.Editable, Kind];

    /// <summary>编辑框里的文本(标量)。</summary>
    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (_text == value)
            {
                return;
            }
            _text = value;
            RaisePropertyChanged();
            ParseText();
            _owner.OnEdited(this, structural: false);
        }
    }

    /// <summary>值解析失败的原因(已本地化);没有为 <see langword="null" />。</summary>
    public string? ParseError
    {
        get => _parseError;
        private set => SetProperty(ref _parseError, value);
    }

    /// <summary>布尔值(开关)。</summary>
    public bool BoolValue
    {
        get => _value is BsonBoolean { Value: true };
        set
        {
            if (BoolValue == value || !IsValueEditable)
            {
                return;
            }
            _value = value ? BsonBoolean.True : BsonBoolean.False;
            _text = value ? "true" : "false";
            RaisePropertiesChanged(nameof(BoolValue), nameof(Text));
            _owner.OnEdited(this, structural: false);
        }
    }

    /// <summary>枚举下拉的选中项(= 文本)。</summary>
    public string? EnumValue
    {
        get => _text;
        set
        {
            if (value is not null)
            {
                Text = value;
                RaisePropertyChanged();
            }
        }
    }

    /// <summary>验证规则里这条路径的 enum(字符串)。由视图模型在重算时填。</summary>
    public IReadOnlyList<string> EnumOptions
    {
        get; internal set
        {
            if (!field.SequenceEqual(value))
            {
                field = value;
                RaisePropertiesChanged(nameof(EnumOptions), nameof(EnumHint));
                RaiseShape();
            }
        }
    } = [];

    /// <summary><c>enum: 普通 · VIP · SVIP</c>。</summary>
    public string EnumHint => _owner.Loc.Format("Doc_EnumHint", string.Join(" · ", EnumOptions));

    /// <summary>当前值(容器由子行拼出;解析失败的标量保留上一个合法值 —— 反正挡着保存)。</summary>
    public BsonValue BuildValue() => Kind switch
    {
        BsonKind.Object => BuildDocument(Children),
        BsonKind.Array => new BsonArray(Children.Select(static c => c.BuildValue())),
        _ => _value
    };

    /// <summary>
    /// 一组字段行 → 文档。重名字段后者覆盖前者(重名本身已标成错误挡着保存,
    /// 这里只是不让 BsonDocument 因重名抛出来)。
    /// </summary>
    internal static BsonDocument BuildDocument(IEnumerable<DocumentEditorRow> rows)
    {
        var document = new BsonDocument();
        foreach (DocumentEditorRow row in rows)
        {
            document[row._name] = row.BuildValue();
        }
        return document;
    }

    /// <summary>换类型:按 <see cref="BsonEdit.Convert" /> 换算;换算不了的值重置并提示。</summary>
    public void ChangeKind(BsonKind kind)
    {
        if (!IsValueEditable || kind == Kind)
        {
            RaisePropertyChanged(nameof(SelectedKind));
            return;
        }
        BsonValue converted = BsonEdit.Convert(BuildValue(), kind, out bool reset);
        LoadValue(converted);
        if (BsonKinds.IsContainer(kind))
        {
            // 刚换成的容器是空的,展开着才好往里加东西。
            _isExpanded = true;
            ExpansionDecided = true;
        }
        RaiseAll();
        if (reset)
        {
            _owner.Toast(_owner.Loc.Format("Doc_KindReset", Path, BsonKinds.Name(kind)), ToastKind.Warning);
        }
        _owner.OnEdited(this, structural: true);
    }

    /// <summary>整体换值(还原、补全、粘贴):容器重建子行。</summary>
    public void SetValue(BsonValue value)
    {
        LoadValue(value);
        RaiseAll();
        _owner.OnEdited(this, structural: true);
    }

    /// <summary>设值但不通知(构造与重建时用)。</summary>
    private void LoadValue(BsonValue value)
    {
        Kind = BsonKinds.Of(value);
        Children.Clear();
        _parseError = null;
        if (value is BsonDocument document)
        {
            _value = BsonNull.Value;
            _text = "";
            foreach (BsonElement element in document)
            {
                Children.Add(new DocumentEditorRow(_owner, element.Name, element.Value, this));
            }
            return;
        }
        if (value is BsonArray array)
        {
            _value = BsonNull.Value;
            _text = "";
            for (int i = 0; i < array.Count; i++)
            {
                Children.Add(new DocumentEditorRow(_owner, i.ToString(CultureInfo.InvariantCulture), array[i], this));
            }
            return;
        }
        _value = value;
        _text = BsonEdit.EditText(value);
    }

    private void ParseText()
    {
        if (BsonEdit.TryParse(_text, Kind, out BsonValue parsed, out string? error))
        {
            _value = parsed;
            ParseError = null;
        }
        else
        {
            ParseError = _owner.Loc[error ?? "Edit_BadLiteral"];
        }
        RaisePropertiesChanged(nameof(IsoEcho), nameof(TimeZoneText), nameof(ChipText));
    }

    private void GenerateId()
    {
        if (!IsValueEditable)
        {
            return;
        }
        _value = ObjectId.GenerateNewId();
        _text = BsonEdit.EditText(_value);
        ParseError = null;
        RaisePropertyChanged(nameof(Text));
        _owner.OnEdited(this, structural: false);
    }

    // ── 容器 ────────────────────────────────────────────────────────────────

    /// <summary>是不是容器(对象 / 数组)。</summary>
    public bool IsContainer => BsonKinds.IsContainer(Kind);

    /// <summary>展开着。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RaiseShape();
            }
        }
    }

    /// <summary>展开状态已经定过(默认规则只对新出现的容器行生效一次)。</summary>
    internal bool ExpansionDecided { get; set; }

    /// <summary>数组里全是标量(收起时画成一排芯片)。</summary>
    public bool IsScalarArray => Kind == BsonKind.Array && Children.All(static c => !c.IsContainer);

    /// <summary>容器的个数徽记:<c>{3}</c> / <c>[4]</c>。</summary>
    public string ContainerCount => Kind == BsonKind.Object ? $"{{{Children.Count}}}" : $"[{Children.Count}]";

    /// <summary>
    /// 容器摘要:对象给字段名(<c>id, name, level</c>),对象数组给每项的"名字 ×数量"
    /// (<c>SKU-7710 ×2 · SKU-1182 ×1</c>,与设计稿一致),标量数组给前几项。
    /// </summary>
    public string ContainerSummary
    {
        get
        {
            if (Kind == BsonKind.Object)
            {
                return string.Join(", ", Children.Take(8).Select(static c => c._name)) + (Children.Count > 8 ? ", …" : "");
            }
            IEnumerable<string> parts = Children.Take(6).Select(static c => c.ItemSummary());
            return string.Join(" · ", parts) + (Children.Count > 6 ? " · …" : "");
        }
    }

    /// <summary>数组项的一句话摘要。</summary>
    private string ItemSummary()
    {
        if (Kind != BsonKind.Object)
        {
            return IsContainer ? ContainerCount : BsonText.Cell(_value);
        }
        string? head = Children.FirstOrDefault(static c => c.Kind == BsonKind.String)?._value.AsString;
        // 订单行那种"SKU ×数量":只认常见的数量字段名,免得把 { name, age } 写成"张三 ×30"。
        DocumentEditorRow? count = Children.FirstOrDefault(static c =>
            c._name is "qty" or "quantity" or "count" or "num" or "amount" && BsonKinds.IsNumeric(c.Kind));
        string text = head is null ? ContainerCount : BsonText.OneLine(head);
        return count is null ? text : $"{text} ×{BsonText.Cell(count._value)}";
    }

    /// <summary>芯片上的字(标量数组的项)。</summary>
    public string ChipText => Kind == BsonKind.String ? BsonText.OneLine(_text) : BsonText.Cell(_value);

    /// <summary>「+ String」:追加一项时用的类型 —— 跟最后一项走,空数组给字符串。</summary>
    public BsonKind NewItemKind => Children.Count > 0 && !Children[^1].IsContainer ? Children[^1].Kind : BsonKind.String;

    /// <summary>「+ String」上的字。</summary>
    public string NewItemLabel => BsonKinds.Name(NewItemKind);

    /// <summary>正在芯片行里输入新的一项。</summary>
    public bool IsAddingItem
    {
        get; set
        {
            if (SetProperty(ref field, value) && !value)
            {
                NewItemText = "";
            }
        }
    }

    /// <summary>新一项的文本。</summary>
    public string NewItemText
    {
        get; set
        {
            if (SetProperty(ref field, value ?? ""))
            {
                NewItemInvalid = false;
            }
        }
    } = "";

    /// <summary>新一项解析不了(输入框标红)。</summary>
    public bool NewItemInvalid { get; private set => SetProperty(ref field, value); }

    /// <summary>
    /// 回车:追加这一项,输入框留着接着输下一项(打标签通常是一口气打好几个);
    /// 空着回车等于收起。
    /// </summary>
    private void CommitItem()
    {
        if (NewItemText.Length == 0)
        {
            IsAddingItem = false;
            return;
        }
        if (!BsonEdit.TryParse(NewItemText, NewItemKind, out BsonValue item, out _))
        {
            NewItemInvalid = true;
            return;
        }
        _owner.AppendItem(this, item);
        NewItemText = "";
    }

    /// <summary>输入框失焦:有内容就追加,然后收起。</summary>
    internal void FinishItem()
    {
        if (!IsAddingItem)
        {
            return;
        }
        if (NewItemText.Trim().Length > 0)
        {
            CommitItem();
            if (NewItemInvalid)
            {
                // 解析不了的内容不悄悄丢掉:留着输入框(标红)让用户改。
                return;
            }
        }
        IsAddingItem = false;
    }

    /// <summary>子行增删之后重排下标、刷新摘要。</summary>
    internal void OnChildrenChanged()
    {
        if (Kind == BsonKind.Array)
        {
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].SetIndex(i);
            }
        }
        RaiseShape();
    }

    // ── 状态 ────────────────────────────────────────────────────────────────

    /// <summary>原文档同一路径上的值(没有 = 新增);由视图模型在重算时填。</summary>
    internal BsonValue? OriginalValue
    {
        get; set
        {
            field = value;
            RaisePropertyChanged(nameof(OriginalText));
        }
    }

    /// <summary>状态。</summary>
    public DocumentEditorRowState State { get; private set; }

    /// <summary>行尾那句话(错误 / 警告)。</summary>
    public string? Message { get; private set; }

    /// <summary>重算后设状态。</summary>
    internal void SetState(DocumentEditorRowState state, string? message)
    {
        if (State == state && Message == message)
        {
            return;
        }
        State = state;
        Message = message;
        RaisePropertiesChanged(nameof(State), nameof(Message), nameof(IsModified), nameof(IsAdded), nameof(IsError),
            nameof(IsValueError), nameof(IsWarning), nameof(ShowMessage), nameof(ShowRevert), nameof(ShowEnumHint), nameof(ShowDateExtras),
            nameof(IsoEcho), nameof(TimeZoneText));
    }

    /// <summary>已修改(橙底)。</summary>
    public bool IsModified => State == DocumentEditorRowState.Modified;

    /// <summary>新增(绿底)。</summary>
    public bool IsAdded => State == DocumentEditorRowState.Added;

    /// <summary>错误(红底)。</summary>
    public bool IsError => State == DocumentEditorRowState.Error;

    /// <summary>错误出在值上(值框标红;字段名的错误只把字段名标红)。</summary>
    public bool IsValueError => State == DocumentEditorRowState.Error && NameError is null;

    /// <summary>警告(validationAction = warn 时的违规)。</summary>
    public bool IsWarning => State == DocumentEditorRowState.Warning;

    /// <summary>显示行尾的错误 / 警告。</summary>
    public bool ShowMessage => Message is not null && State is DocumentEditorRowState.Error or DocumentEditorRowState.Warning;

    /// <summary>显示「原值 … ↺ 还原」。</summary>
    public bool ShowRevert => State == DocumentEditorRowState.Modified && OriginalValue is not null && _owner.CanEdit;

    /// <summary><c>原值 "paid"</c>。</summary>
    public string OriginalText
    {
        get
        {
            if (OriginalValue is null)
            {
                return "";
            }
            // 原值只是提示:长字符串截到 16 个字符,别让它把输入框挤窄(完整的原值在对比模式里)。
            string text = OriginalValue.IsString && OriginalValue.AsString.Length > 16
                ? BsonText.Quote(BsonText.OneLine(OriginalValue.AsString[..16]) + "…")
                : BsonText.Inline(OriginalValue, _owner.Loc, shortenIds: true);
            return _owner.Loc.Format("Doc_WasValue", text);
        }
    }

    /// <summary>显示 enum 提示(没有更要紧的提示时)。</summary>
    public bool ShowEnumHint => ShowEnum && !ShowMessage && !ShowRevert;

    // ── 值编辑器的形态 ──────────────────────────────────────────────────────

    /// <summary>枚举下拉。</summary>
    public bool ShowEnum => Kind == BsonKind.String && EnumOptions.Count > 0 && !IsIdLocked;

    /// <summary>宽文本框(字符串、UUID 与少见类型的字面量)。</summary>
    public bool ShowText => Kind is BsonKind.String or BsonKind.Uuid or BsonKind.Binary or BsonKind.Regex
        or BsonKind.Timestamp or BsonKind.Other && !ShowEnum;

    /// <summary>数值框(右对齐、160 宽)。</summary>
    public bool ShowNumber => BsonKinds.IsNumeric(Kind);

    /// <summary>开关。</summary>
    public bool ShowBool => Kind == BsonKind.Boolean;

    /// <summary>日期。</summary>
    public bool ShowDate => Kind == BsonKind.Date;

    /// <summary>ObjectId(+ 生成)。</summary>
    public bool ShowObjectId => Kind == BsonKind.ObjectId;

    /// <summary>null(+ 设为字符串)。</summary>
    public bool ShowNull => Kind == BsonKind.Null;

    /// <summary>对象摘要。</summary>
    public bool ShowObjectSummary => Kind == BsonKind.Object;

    /// <summary>芯片(收起的标量数组)。</summary>
    public bool ShowChips => Kind == BsonKind.Array && !_isExpanded && IsScalarArray;

    /// <summary>数组摘要(对象数组,或展开着的数组)。</summary>
    public bool ShowArraySummary => Kind == BsonKind.Array && !ShowChips;

    /// <summary>「展开 / 收起」链接上的字。</summary>
    public string ToggleText => _owner.Loc[_isExpanded ? "Doc_Collapse" : "Doc_Expand"];

    /// <summary>折叠箭头的图标。</summary>
    public string ChevronKey => _isExpanded ? "Mongo.chevron-down" : "Mongo.chevron-right";

    /// <summary>日期旁的时区芯片与 ISO 回显(改过、新加的日期才显示,免得每个日期行都拖一串)。</summary>
    public bool ShowDateExtras => ShowDate && State is DocumentEditorRowState.Modified or DocumentEditorRowState.Added;

    /// <summary>时区芯片:<c>UTC+08:00</c>(表单里的日期按本地时间填)。</summary>
    public string TimeZoneText
    {
        get
        {
            DateTime at = _value is BsonDateTime date && date.IsValidDateTime ? date.ToUniversalTime() : DateTime.UtcNow;
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(at);
            return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// ISO 回显:<c>= 02:30Z</c> —— 本地时间填进去,存进库的是 UTC;同一天只给时刻,跨了天给完整日期。
    /// </summary>
    public string IsoEcho
    {
        get
        {
            if (_value is not BsonDateTime date || !date.IsValidDateTime)
            {
                return "";
            }
            DateTime utc = date.ToUniversalTime();
            DateTime local = utc.ToLocalTime();
            string time = utc.ToString(utc.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
            return "= " + (utc.Date == local.Date ? time : utc.ToString("yyyy-MM-dd'T'", CultureInfo.InvariantCulture) + time);
        }
    }

    /// <summary>日期部分(日历弹层用,本地)。</summary>
    internal DateTime? LocalDate => _value is BsonDateTime date && date.IsValidDateTime ? date.ToUniversalTime().ToLocalTime() : null;

    /// <summary>日历里选了一天:保留时刻,换日期。</summary>
    internal void PickDate(DateTime day)
    {
        DateTime current = LocalDate ?? DateTime.Now;
        var picked = new DateTime(day.Year, day.Month, day.Day, current.Hour, current.Minute, current.Second, DateTimeKind.Local);
        Text = picked.ToString(BsonText.DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>「+」按钮的提示:容器加子项,标量加同级。</summary>
    public string AddTip => _owner.Loc[IsContainer ? "Doc_AddChild" : "Doc_AddSibling"];

    // ── 命令 ────────────────────────────────────────────────────────────────

    /// <summary>展开 / 收起。</summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>+(容器加子项,标量加同级)。</summary>
    public RelayCommand AddCommand { get; }

    /// <summary>删除。</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>还原成原值。</summary>
    public RelayCommand RevertCommand { get; }

    /// <summary>生成新的 ObjectId。</summary>
    public RelayCommand GenerateCommand { get; }

    /// <summary>null → 空字符串。</summary>
    public RelayCommand SetStringCommand { get; }

    /// <summary>「+ String」:开始输入新的一项。</summary>
    public RelayCommand BeginItemCommand { get; }

    /// <summary>新的一项:确定。</summary>
    public RelayCommand CommitItemCommand { get; }

    /// <summary>新的一项:取消。</summary>
    public RelayCommand CancelItemCommand { get; }

    // ── 通知 ────────────────────────────────────────────────────────────────

    /// <summary>形态相关的派生属性全部重发(换类型、展开、子行增删之后)。</summary>
    internal void RaiseShape() => RaisePropertiesChanged(
        nameof(IsContainer), nameof(IsScalarArray), nameof(ContainerCount), nameof(ContainerSummary),
        nameof(ShowEnum), nameof(ShowText), nameof(ShowNumber), nameof(ShowBool), nameof(ShowDate), nameof(ShowObjectId),
        nameof(ShowNull), nameof(ShowObjectSummary), nameof(ShowChips), nameof(ShowArraySummary), nameof(ShowEnumHint),
        nameof(ShowDateExtras), nameof(ToggleText), nameof(ChevronKey), nameof(NewItemKind), nameof(NewItemLabel), nameof(AddTip));

    private void RaiseAll()
    {
        RaisePropertiesChanged(nameof(Kind), nameof(SelectedKind), nameof(KindOptions), nameof(Text), nameof(ParseError),
            nameof(BoolValue), nameof(EnumValue), nameof(IsExpanded), nameof(IsoEcho), nameof(TimeZoneText), nameof(ChipText));
        RaiseShape();
        ToggleCommand.RaiseCanExecuteChanged();
    }

    /// <summary>只读状态变了(视图 / 写护栏)。</summary>
    internal void RaiseEditability()
    {
        RaisePropertiesChanged(nameof(IsNameEditable), nameof(IsValueEditable), nameof(IsValueReadOnly), nameof(IsKindEditable),
            nameof(CanRemove), nameof(CanDrag), nameof(ShowRevert), nameof(ShowEnum), nameof(ShowText));
        AddCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }

    /// <inheritdoc />
    public override string ToString() => $"{Path} ({BsonKinds.Name(Kind)})";
}

/// <summary>表单末尾那一格:文档级的问题(缺少必填字段…)+「添加字段」。</summary>
/// <param name="Owner">所属对话框。</param>
internal sealed record DocumentEditorTail(DocumentEditorDialogViewModel Owner);

/// <summary>字段名补全的一项(来自 Schema 抽样)。</summary>
/// <param name="Name">字段名。</param>
/// <param name="Kind">主导类型。</param>
/// <param name="Ratio">出现率(0–1)。</param>
internal sealed record DocumentEditorSuggestion(string Name, BsonKind Kind, double Ratio)
{
    /// <summary>类型名。</summary>
    public string KindName => BsonKinds.Name(Kind);

    /// <summary>出现率文字。</summary>
    public string RatioText => (Ratio * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
