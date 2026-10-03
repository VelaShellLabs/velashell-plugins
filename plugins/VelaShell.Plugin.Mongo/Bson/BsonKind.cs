using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Bson;

/// <summary>
/// 界面认的 BSON 类型。比 <see cref="BsonType" /> 多两种:<see cref="Uuid" />(Binary 子类型 4,
/// 用户眼里它是一种独立的类型)与 <see cref="Missing" />(这份文档里没有这个字段 —— 与 null 不同)。
/// </summary>
public enum BsonKind
{
    /// <summary>ObjectId。</summary>
    ObjectId,

    /// <summary>字符串。</summary>
    String,

    /// <summary>32 位整数。</summary>
    Int32,

    /// <summary>64 位整数。</summary>
    Int64,

    /// <summary>双精度浮点。</summary>
    Double,

    /// <summary>Decimal128。</summary>
    Decimal128,

    /// <summary>布尔。</summary>
    Boolean,

    /// <summary>日期。</summary>
    Date,

    /// <summary>嵌套文档。</summary>
    Object,

    /// <summary>数组。</summary>
    Array,

    /// <summary>null。</summary>
    Null,

    /// <summary>二进制(非 UUID)。</summary>
    Binary,

    /// <summary>UUID(Binary 子类型 4 / 3)。</summary>
    Uuid,

    /// <summary>正则。</summary>
    Regex,

    /// <summary>时间戳(复制内部用)。</summary>
    Timestamp,

    /// <summary>其余少见类型(MinKey / MaxKey / JavaScript / Symbol / Undefined)。</summary>
    Other,

    /// <summary>字段缺失。</summary>
    Missing
}

/// <summary>BSON 类型的识别、命名与配色(网格、JSON、表单、补全与类型下拉共用同一套令牌)。</summary>
public static class BsonKinds
{
    /// <summary>类型下拉里可选的那几种(设计稿 01 的类型菜单,顺序照抄)。</summary>
    public static IReadOnlyList<BsonKind> Editable { get; } =
    [
        BsonKind.String, BsonKind.Int32, BsonKind.Int64, BsonKind.Double, BsonKind.Decimal128,
        BsonKind.Boolean, BsonKind.Date, BsonKind.ObjectId, BsonKind.Object, BsonKind.Array,
        BsonKind.Null, BsonKind.Uuid
    ];

    /// <summary>识别一个值的类型;<see langword="null" /> 视为缺失。</summary>
    public static BsonKind Of(BsonValue? value) => value?.BsonType switch
    {
        null => BsonKind.Missing,
        BsonType.ObjectId => BsonKind.ObjectId,
        BsonType.String => BsonKind.String,
        BsonType.Int32 => BsonKind.Int32,
        BsonType.Int64 => BsonKind.Int64,
        BsonType.Double => BsonKind.Double,
        BsonType.Decimal128 => BsonKind.Decimal128,
        BsonType.Boolean => BsonKind.Boolean,
        BsonType.DateTime => BsonKind.Date,
        BsonType.Document => BsonKind.Object,
        BsonType.Array => BsonKind.Array,
        BsonType.Null => BsonKind.Null,
        BsonType.Binary when value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard or BsonBinarySubType.UuidLegacy
            => BsonKind.Uuid,
        BsonType.Binary => BsonKind.Binary,
        BsonType.RegularExpression => BsonKind.Regex,
        BsonType.Timestamp => BsonKind.Timestamp,
        _ => BsonKind.Other
    };

    /// <summary>类型显示名(列头小字、检查器右侧的类型标签)。</summary>
    public static string Name(BsonKind kind) => kind switch
    {
        BsonKind.Uuid => "UUID",
        BsonKind.Missing => "—",
        _ => kind.ToString()
    };

    /// <summary>类型下拉里的名字(UUID 那一项写全:<c>Binary · UUID</c>)。</summary>
    public static string MenuName(BsonKind kind) => kind == BsonKind.Uuid ? "Binary · UUID" : Name(kind);

    /// <summary>
    /// 类型 → 宿主颜色令牌。设计稿 09 的 BSON 色板逐色对应宿主的终端十六色那一族
    /// (VelaDark 下数值完全相同),换主题时跟着走 —— 而不是写死 Dracula 色值。
    /// </summary>
    public static string ColorToken(BsonKind kind) => kind switch
    {
        BsonKind.ObjectId => "VelaShellBlue",
        BsonKind.String or BsonKind.Regex => "VelaShellCyan",
        BsonKind.Int32 or BsonKind.Int64 or BsonKind.Double or BsonKind.Decimal128 => "VelaShellGreen",
        BsonKind.Boolean => "VelaShellYellow",
        BsonKind.Date or BsonKind.Timestamp => "VelaShellMagenta",
        BsonKind.Object or BsonKind.Array => "VelaWarning",
        BsonKind.Null or BsonKind.Binary or BsonKind.Uuid or BsonKind.Other => "VelaTextTertiary",
        _ => "VelaTextMuted"
    };

    /// <summary>数值类型(网格里右对齐)。</summary>
    public static bool IsNumeric(BsonKind kind) =>
        kind is BsonKind.Int32 or BsonKind.Int64 or BsonKind.Double or BsonKind.Decimal128;

    /// <summary>容器类型(可展开)。</summary>
    public static bool IsContainer(BsonKind kind) => kind is BsonKind.Object or BsonKind.Array;
}
