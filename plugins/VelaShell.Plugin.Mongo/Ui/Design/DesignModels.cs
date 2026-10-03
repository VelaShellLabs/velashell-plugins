using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Analysis;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>比例条里的一段(类型分布、通过 / 不通过、使用率)。</summary>
/// <param name="Ratio">占比(0–1)。</param>
/// <param name="Token">颜色令牌。</param>
internal sealed record BarPiece(double Ratio, string Token);

/// <summary>图例一项(色块 + 文字)。</summary>
/// <param name="Text">文字(<c>String 97%</c>)。</param>
/// <param name="Token">色块令牌。</param>
internal sealed record LegendItem(string Text, string Token);

/// <summary>索引键里的一个字段芯片(<c>status ↑</c>、<c>note text</c>)。</summary>
internal sealed class IndexKeyChip
{
    /// <summary>构造。</summary>
    /// <param name="field">字段。</param>
    /// <param name="value">键值(1 / -1 / "text" / "2dsphere" / "hashed")。</param>
    public IndexKeyChip(string field, BsonValue value)
    {
        Field = field;
        if (value.IsNumeric)
        {
            bool down = value.ToDouble() < 0;
            ArrowKey = down ? "Mongo.arrow-down" : "Mongo.arrow-up";
            ArrowToken = down ? "VelaWarning" : "VelaStatusConnected";
        }
        else
        {
            Special = value.ToString();
        }
    }

    /// <summary>字段名。</summary>
    public string Field { get; }

    /// <summary>方向箭头图标;特殊索引为 <see langword="null" />。</summary>
    public string? ArrowKey { get; }

    /// <summary>箭头颜色(升序绿、降序橙)。</summary>
    public string ArrowToken { get; } = "VelaTextTertiary";

    /// <summary>特殊类型(<c>text</c> / <c>2dsphere</c> / <c>hashed</c>)。</summary>
    public string? Special { get; }

    /// <summary>画箭头。</summary>
    public bool HasArrow => ArrowKey is not null;

    /// <summary>画类型字样。</summary>
    public bool HasSpecial => Special is not null;
}

/// <summary>属性列里的一个小标签(唯一 / 稀疏 / 部分 / TTL / 隐藏 / 未使用 N 天)。</summary>
/// <param name="Text">文字。</param>
/// <param name="Tone">语气:<c>accent</c> / <c>muted</c> / <c>info</c> / <c>warn</c>。</param>
internal sealed record IndexAttrChip(string Text, string Tone)
{
    /// <summary>灰底。</summary>
    public bool IsMuted => Tone == "muted";

    /// <summary>信息色底。</summary>
    public bool IsInfo => Tone == "info";

    /// <summary>警告色底。</summary>
    public bool IsWarn => Tone == "warn";
}

/// <summary>索引表的一行(设计稿 07)。</summary>
internal sealed class IndexRow : ObservableObject
{
    /// <summary>使用列比例条的满宽。</summary>
    public const double BarWidth = 60;

    private bool _isBuilding;
    private double _buildPercent;
    private string _buildText = "";

    /// <summary>索引名。</summary>
    public required string Name { get; init; }

    /// <summary><c>listIndexes</c> 的规格原文。</summary>
    public required BsonDocument Spec { get; init; }

    /// <summary>键模式。</summary>
    public BsonDocument Key => Spec.GetValue("key", new BsonDocument()) as BsonDocument ?? [];

    /// <summary>键芯片。</summary>
    public IReadOnlyList<IndexKeyChip> Keys { get; init; } = [];

    /// <summary>形态。</summary>
    public IndexForm Form { get; init; }

    /// <summary>类型列文字。</summary>
    public string TypeText { get; init; } = "";

    /// <summary>属性标签。</summary>
    public IReadOnlyList<IndexAttrChip> Attributes { get; init; } = [];

    /// <summary>没有任何属性(属性列画一个灰色破折号)。</summary>
    public bool NoAttributes => Attributes.Count == 0;

    /// <summary>大小(字节);未知为 0。</summary>
    public long Size { get; init; }

    /// <summary>大小文字。</summary>
    public string SizeText { get; init; } = "—";

    /// <summary>$indexStats 的 ops;拿不到为 <see langword="null" />。</summary>
    public long? Ops { get; init; }

    /// <summary>ops 文字。</summary>
    public string OpsText => Ops is { } ops ? BsonText.Grouped(ops) : "—";

    /// <summary>使用率(相对本集合使用最多的那个索引)。</summary>
    public double UsageRatio { get; init; }

    /// <summary>使用条的像素宽。</summary>
    public double UsageWidth => Math.Clamp(UsageRatio, 0, 1) * BarWidth;

    /// <summary>统计起点。</summary>
    public DateTime? Since { get; init; }

    /// <summary>多键。</summary>
    public bool MultiKey { get; init; }

    /// <summary>被标成"未使用"(名称与 ops 变橙)。</summary>
    public bool IsUnused { get; init; }

    /// <summary>隐藏索引。</summary>
    public bool IsHidden => Spec.GetValue("hidden", false).ToBoolean();

    /// <summary><c>_id_</c>(不能隐藏、不能删除)。</summary>
    public bool IsId => Name == "_id_";

    /// <summary>正在构建。</summary>
    public bool IsBuilding
    {
        get => _isBuilding;
        set
        {
            if (SetProperty(ref _isBuilding, value))
            {
                RaisePropertiesChanged(nameof(IconKey), nameof(IconToken), nameof(IsReady));
            }
        }
    }

    /// <summary>已建好(使用列画 ops 而不是进度)。</summary>
    public bool IsReady => !_isBuilding;

    /// <summary>构建进度(0–100)。</summary>
    public double BuildPercent
    {
        get => _buildPercent;
        set
        {
            if (SetProperty(ref _buildPercent, value))
            {
                RaisePropertyChanged(nameof(BuildWidth));
            }
        }
    }

    /// <summary>进度条像素宽。</summary>
    public double BuildWidth => Math.Clamp(_buildPercent, 0, 100) / 100 * BarWidth;

    /// <summary>进度文字(<c>构建中 64% · 约 2 分</c>)。</summary>
    public string BuildText
    {
        get => _buildText;
        set => SetProperty(ref _buildText, value);
    }

    /// <summary>名称列图标。</summary>
    public string IconKey => _isBuilding ? "Mongo.loader-circle" : IsHidden ? "Mongo.eye-off" : "Mongo.key-round";

    /// <summary>名称列图标颜色。</summary>
    public string IconToken => _isBuilding ? "VelaAccent" : IsUnused ? "VelaWarning" : "VelaTextTertiary";

    /// <summary>名称颜色。</summary>
    public string NameToken => IsUnused ? "VelaWarning" : IsHidden ? "VelaTextTertiary" : "VelaTextPrimary";

    /// <summary>ops 颜色。</summary>
    public string OpsToken => IsUnused ? "VelaWarning" : "VelaTextSecondary";
}

/// <summary>「索引建议」的一张卡片(设计稿 07 下半部分)。</summary>
internal sealed class AdviceCard
{
    /// <summary>依据的建议;纯提示卡(profiler 未开)为 <see langword="null" />。</summary>
    public AdvisorSuggestion? Suggestion { get; init; }

    /// <summary>左侧大图标。</summary>
    public string IconKey { get; init; } = "Mongo.zap";

    /// <summary>图标颜色。</summary>
    public string IconToken { get; init; } = "VelaWarning";

    /// <summary>标题。</summary>
    public required string Title { get; init; }

    /// <summary>说明。</summary>
    public string Detail { get; init; } = "";

    /// <summary>说明下面的等宽代码块(建议键);没有为 <see langword="null" />。</summary>
    public string? Code { get; init; }

    /// <summary>有代码块。</summary>
    public bool HasCode => Code is not null;

    /// <summary>按钮文字。</summary>
    public required string ActionLabel { get; init; }

    /// <summary>主操作(半透明药丸)还是次操作(描边)。</summary>
    public bool IsPrimary { get; init; }

    /// <summary>次操作。</summary>
    public bool IsSecondary => !IsPrimary;

    /// <summary>按钮要做的事。</summary>
    public required Func<Task> Action { get; init; }
}

/// <summary>新建索引面板里的一行字段。</summary>
internal sealed class NewKeyRow : ObservableObject
{
    private readonly Action _changed;
    private readonly Func<string, string> _tokenOf;
    private readonly Func<BsonValue, string> _label;
    private string _field;
    private int _direction;
    private string _role;
    private string _kind = "normal";
    private bool _isFirst;

    /// <summary>构造。</summary>
    /// <param name="field">字段。</param>
    /// <param name="direction">方向(1 / -1)。</param>
    /// <param name="role">ESR 角色文字(<c>等值</c> / <c>排序</c> / <c>范围</c>);未知为空。</param>
    /// <param name="tokenOf">字段 → 类型色令牌(来自抽样)。</param>
    /// <param name="changed">任何改动后通知面板重算命令预览。</param>
    /// <param name="label">键值 → 方向下拉上的字(<c>1  升序</c>)。</param>
    public NewKeyRow(string field, int direction, string role, Func<string, string> tokenOf, Action changed, Func<BsonValue, string> label)
    {
        _label = label;
        _field = field;
        _direction = direction;
        _role = role;
        _tokenOf = tokenOf;
        _changed = changed;
    }

    /// <summary>字段路径。</summary>
    public string Field
    {
        get => _field;
        set
        {
            if (SetProperty(ref _field, value ?? ""))
            {
                RaisePropertyChanged(nameof(SwatchToken));
                _changed();
            }
        }
    }

    /// <summary>方向(1 / -1)。</summary>
    public int Direction
    {
        get => _direction;
        set
        {
            if (SetProperty(ref _direction, value))
            {
                RaisePropertiesChanged(nameof(DirectionValue), nameof(DirectionText));
                _changed();
            }
        }
    }

    /// <summary>ESR 角色文字。</summary>
    public string Role
    {
        get => _role;
        set
        {
            if (SetProperty(ref _role, value))
            {
                RaisePropertyChanged(nameof(HasRole));
            }
        }
    }

    /// <summary>有角色小标签。</summary>
    public bool HasRole => _role.Length > 0;

    /// <summary>字段类型色。</summary>
    public string SwatchToken => _tokenOf(_field);

    /// <summary>抽样结果到了:类型色要重算。</summary>
    internal void RefreshSwatch() => RaisePropertyChanged(nameof(SwatchToken));

    /// <summary>当前索引类型(面板的分段:normal / text / 2dsphere / hashed / wildcard)。</summary>
    public string Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                RaisePropertiesChanged(nameof(DirectionValue), nameof(HasDirection), nameof(DirectionText));
            }
        }
    }

    /// <summary>是第一行(哈希索引只把第一行设成 hashed)。</summary>
    public bool IsFirst
    {
        get => _isFirst;
        set
        {
            if (SetProperty(ref _isFirst, value))
            {
                RaisePropertiesChanged(nameof(DirectionValue), nameof(HasDirection), nameof(DirectionText));
            }
        }
    }

    /// <summary>这一行的键值能不能选方向(普通索引、哈希索引的非首行)。</summary>
    public bool HasDirection => _kind == "normal" || (_kind == "hashed" && !_isFirst) || _kind == "wildcard";

    /// <summary>方向下拉上的字。</summary>
    public string DirectionText => _label(DirectionValue);

    /// <summary>这一行在键模式里的值。</summary>
    public BsonValue DirectionValue => _kind switch
    {
        "text" => "text",
        "2dsphere" => "2dsphere",
        "hashed" when _isFirst => "hashed",
        "wildcard" => 1,
        _ => _direction
    };
}

/// <summary>抽样字段(新建索引的字段下拉、验证规则的补全)。</summary>
/// <param name="Path">路径。</param>
/// <param name="Kind">主类型。</param>
internal sealed record FieldOption(string Path, BsonKind Kind)
{
    /// <summary>类型色。</summary>
    public string Token => BsonKinds.ColorToken(Kind);

    /// <summary>类型名。</summary>
    public string KindName => BsonKinds.Name(Kind);
}

/// <summary>值分布里的一根横向比例条。</summary>
/// <param name="Label">取值。</param>
/// <param name="Percent">百分比文字。</param>
/// <param name="Ratio">占比。</param>
/// <param name="Token">颜色。</param>
internal sealed record ValueBar(string Label, string Percent, double Ratio, string Token)
{
    /// <summary>条本身(给 <see cref="DesignBar" />)。</summary>
    public IReadOnlyList<BarPiece> Pieces => [new(Ratio, Token)];
}

/// <summary>数组长度分布的一根柱。</summary>
/// <param name="Label">长度(<c>1</c> … <c>5+</c>)。</param>
/// <param name="Height">柱高(像素,满 36)。</param>
/// <param name="Count">次数。</param>
internal sealed record LengthBar(string Label, double Height, int Count);

/// <summary>Schema 分析表的一行(设计稿 08)。</summary>
internal sealed class SchemaFieldRow
{
    /// <summary>路径。</summary>
    public required string Path { get; init; }

    /// <summary>灰字提示。</summary>
    public string Hint { get; init; } = "";

    /// <summary>提示是警告(混合类型)。</summary>
    public bool HintWarn { get; init; }

    /// <summary>类型与出现率的分段。</summary>
    public IReadOnlyList<BarPiece> Pieces { get; init; } = [];

    /// <summary>图例。</summary>
    public IReadOnlyList<LegendItem> Legend { get; init; } = [];

    /// <summary>画法。</summary>
    public AnalyzedViz Viz { get; init; }

    /// <summary>文字分布。</summary>
    public string VizText { get; init; } = "";

    /// <summary>横向比例条。</summary>
    public IReadOnlyList<ValueBar> Bars { get; init; } = [];

    /// <summary>直方图数据。</summary>
    public IReadOnlyList<ChartSeries>? Histogram { get; init; }

    /// <summary>直方图悬停标签。</summary>
    public IReadOnlyList<string>? HistogramLabels { get; init; }

    /// <summary>直方图左端标签。</summary>
    public string AxisMin { get; init; } = "";

    /// <summary>直方图右端标签。</summary>
    public string AxisMax { get; init; } = "";

    /// <summary>数组长度柱。</summary>
    public IReadOnlyList<LengthBar> Lengths { get; init; } = [];

    /// <summary>文字分布。</summary>
    public bool IsText => Viz == AnalyzedViz.Text;

    /// <summary>比例条分布。</summary>
    public bool IsBars => Viz == AnalyzedViz.Bars;

    /// <summary>直方图(数值或日期)。</summary>
    public bool IsHistogram => Viz is AnalyzedViz.Histogram or AnalyzedViz.Timeline;

    /// <summary>数组长度。</summary>
    public bool IsLengths => Viz == AnalyzedViz.Lengths;
}

/// <summary>预检里的一类不通过原因(设计稿 16 右栏)。</summary>
internal sealed class FailureReasonRow
{
    /// <summary>字段路径。</summary>
    public required string Path { get; init; }

    /// <summary>关键字摘要(<c>required</c>、<c>minimum: 0</c>)。</summary>
    public required string Keyword { get; init; }

    /// <summary>关键字本身(<c>minimum</c>;悬停说明按它找这一组)。</summary>
    public string RawKeyword { get; init; } = "";

    /// <summary>份数。</summary>
    public int Count { get; init; }

    /// <summary>份数文字(<c>29 份</c>)。</summary>
    public required string CountText { get; init; }

    /// <summary>示例(<c>例:_id 66f5c2a1…91c0</c>)。</summary>
    public string Example { get; init; } = "";

    /// <summary>在网格中查看时用的筛选。</summary>
    public required string Filter { get; init; }
}

/// <summary>选项页里的一条 TTL 索引。</summary>
internal sealed class TtlRow : ObservableObject
{
    private readonly Func<long, string> _describe;
    private string _seconds;

    /// <summary>构造。</summary>
    /// <param name="index">索引名。</param>
    /// <param name="field">日期字段。</param>
    /// <param name="seconds">当前生效的秒数。</param>
    /// <param name="describe">秒数 → 人话。</param>
    public TtlRow(string index, string field, long seconds, Func<long, string> describe)
    {
        _describe = describe;
        Index = index;
        Field = field;
        Original = seconds;
        _seconds = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>索引名。</summary>
    public string Index { get; }

    /// <summary>日期字段。</summary>
    public string Field { get; }

    /// <summary>当前生效的秒数。</summary>
    public long Original { get; }

    /// <summary>编辑中的秒数。</summary>
    public string Seconds
    {
        get => _seconds;
        set
        {
            if (SetProperty(ref _seconds, value))
            {
                RaisePropertiesChanged(nameof(IsDirty), nameof(Human));
            }
        }
    }

    /// <summary>改过了。</summary>
    public bool IsDirty => long.TryParse(_seconds, out long s) && s != Original;

    /// <summary>换算成人话(<c>= 1 天</c>)。</summary>
    public string Human => long.TryParse(_seconds, out long s) ? _describe(s) : "";
}

/// <summary>统计页的一张指标卡。</summary>
/// <param name="Label">名称。</param>
/// <param name="Value">值。</param>
/// <param name="Sub">小字说明。</param>
/// <param name="Token">值的颜色。</param>
internal sealed record StatCard(string Label, string Value, string Sub, string Token = "VelaTextPrimary");

/// <summary>统计页的一根索引大小条。</summary>
/// <param name="Name">索引名。</param>
/// <param name="SizeText">大小。</param>
/// <param name="Ratio">占索引总量的比例。</param>
internal sealed record IndexSizeBar(string Name, string SizeText, double Ratio)
{
    /// <summary>条本身。</summary>
    public IReadOnlyList<BarPiece> Pieces => [new(Ratio, "VelaInfo")];

    /// <summary>百分比文字。</summary>
    public string Percent => $"{Ratio * 100:0.#}%";
}
