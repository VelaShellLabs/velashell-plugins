using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace VelaShell.Plugin.Mongo.Bson;

/// <summary>
/// BSON 值 → 给人看的文本。三种场合三种形态:
/// <list type="bullet">
///   <item><see cref="Cell" />:网格单元格 —— 一行之内说清"是什么",容器只给摘要(<c>{3} 陈立 · VIP</c>);</item>
///   <item><see cref="Inline" />:检查器、树视图的值列 —— 带类型构造器的单行字面量;</item>
///   <item><see cref="Pretty" />:JSON 视图、复制、导出 —— 完整的多行文本,三种扩展 JSON 写法任选。</item>
/// </list>
/// </summary>
public static partial class BsonText
{
    /// <summary>网格日期列的格式(本地时间,与设计稿一致)。</summary>
    public const string DateFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// 网格单元格文本。字符串不加引号(列头已标了类型),数值按类型给千分位,
    /// 日期转本地时间,容器给"个数 + 头两个标量"的摘要。
    /// </summary>
    public static string Cell(BsonValue? value) => BsonKinds.Of(value) switch
    {
        BsonKind.Missing => "—",
        BsonKind.Null => "null",
        BsonKind.ObjectId => value!.AsObjectId.ToString(),
        BsonKind.String => OneLine(value!.AsString),
        BsonKind.Int32 => value!.AsInt32.ToString("N0", CultureInfo.InvariantCulture),
        BsonKind.Int64 => value!.AsInt64.ToString("N0", CultureInfo.InvariantCulture),
        BsonKind.Double => FormatDouble(value!.AsDouble),
        BsonKind.Decimal128 => FormatDecimal(value!.AsDecimal128),
        BsonKind.Boolean => value!.AsBoolean ? "true" : "false",
        BsonKind.Date => FormatDate(value!),
        BsonKind.Object => $"{{{value!.AsBsonDocument.ElementCount}}} {Summary(value.AsBsonDocument)}".TrimEnd(),
        BsonKind.Array => $"[{value!.AsBsonArray.Count}] {ArraySummary(value.AsBsonArray)}".TrimEnd(),
        BsonKind.Uuid => value!.AsBsonBinaryData.ToGuid().ToString(),
        BsonKind.Binary => $"Binary({value!.AsBsonBinaryData.Bytes.Length} B)",
        BsonKind.Regex => value!.AsBsonRegularExpression.ToString(),
        BsonKind.Timestamp => $"Timestamp({value!.AsBsonTimestamp.Timestamp}, {value.AsBsonTimestamp.Increment})",
        _ => value!.ToString() ?? ""
    };

    /// <summary>
    /// 单行字面量(mongosh 写法):<c>"paid"</c>、<c>ObjectId("…")</c>、<c>ISODate("…")</c>、
    /// <c>NumberDecimal("8740.00")</c>;容器只给 <c>{ 3 个字段 }</c> / <c>[ 4 项 ]</c> 这种摘要。
    /// </summary>
    /// <param name="value">值。</param>
    /// <param name="loc">文案表(容器摘要的量词)。</param>
    /// <param name="shortenIds">ObjectId 是否缩写成 <c>66f5c2a1…e3b7</c>(检查器里地方窄)。</param>
    public static string Inline(BsonValue? value, Loc? loc = null, bool shortenIds = false) => BsonKinds.Of(value) switch
    {
        BsonKind.Missing => "—",
        BsonKind.Null => "null",
        BsonKind.ObjectId => shortenIds ? Shorten(value!.AsObjectId.ToString()) : value!.AsObjectId.ToString(),
        BsonKind.String => Quote(OneLine(value!.AsString)),
        BsonKind.Int32 => value!.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonKind.Int64 => value!.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonKind.Double => FormatDouble(value!.AsDouble),
        BsonKind.Decimal128 => value!.AsDecimal128.ToString(),
        BsonKind.Boolean => value!.AsBoolean ? "true" : "false",
        BsonKind.Date => FormatDate(value!),
        BsonKind.Object => loc is null
            ? $"{{ {value!.AsBsonDocument.ElementCount} }}"
            : loc.Format("Bson_FieldsSummary", value!.AsBsonDocument.ElementCount),
        BsonKind.Array => loc is null
            ? $"[ {value!.AsBsonArray.Count} ]"
            : loc.Format("Bson_ItemsSummary", value!.AsBsonArray.Count),
        BsonKind.Uuid => $"UUID(\"{value!.AsBsonBinaryData.ToGuid()}\")",
        _ => Literal(value!)
    };

    /// <summary>
    /// 一个标量值的 mongosh 字面量(可以原样粘回查询编辑器)。容器递归展开成单行。
    /// </summary>
    public static string Literal(BsonValue value)
    {
        var builder = new StringBuilder();
        WriteShell(builder, value, indent: null, depth: 0);
        return builder.ToString();
    }

    /// <summary>
    /// 完整的多行文本。
    /// <para>
    /// Shell 写法自己拼:设计稿的 JSON 视图是 mongosh 的样子 —— 键名不加引号、
    /// <c>ObjectId("…")</c>、<c>ISODate("…")</c>、<c>NumberDecimal("…")</c>;
    /// 驱动的 Shell 输出模式会给键名加引号、日期写成 <c>ISODate("…")</c> 但数字长整写法不同,
    /// 与设计稿和 mongosh 都对不上。Relaxed / Canonical 是标准格式,交给驱动的写出器。
    /// </para>
    /// </summary>
    /// <param name="value">值(通常是一整份文档)。</param>
    /// <param name="mode">扩展 JSON 写法。</param>
    /// <param name="indent">缩进字符串(默认两个空格)。</param>
    public static string Pretty(BsonValue value, EjsonMode mode = EjsonMode.Shell, string indent = "  ")
    {
        if (mode == EjsonMode.Shell)
        {
            var builder = new StringBuilder();
            WriteShell(builder, value, indent, 0);
            return builder.ToString();
        }
        var settings = new JsonWriterSettings
        {
            OutputMode = mode == EjsonMode.Canonical ? JsonOutputMode.CanonicalExtendedJson : JsonOutputMode.RelaxedExtendedJson,
            Indent = indent.Length > 0,
            IndentChars = indent,
            NewLineChars = "\n"
        };
        if (value is BsonDocument document)
        {
            return document.ToJson(settings);
        }
        // 驱动的写出器只写文档;单值包一层再剥掉。
        string wrapped = new BsonDocument("v", value).ToJson(new JsonWriterSettings { OutputMode = settings.OutputMode });
        int colon = wrapped.IndexOf(':');
        return wrapped[(colon + 1)..^1].Trim();
    }

    /// <summary>单行的完整文本(复制为 JSON、筛选栏回显)。</summary>
    public static string Compact(BsonValue value, EjsonMode mode = EjsonMode.Shell)
    {
        if (mode == EjsonMode.Shell)
        {
            return Literal(value);
        }
        return Pretty(value, mode, indent: "");
    }

    private static void WriteShell(StringBuilder b, BsonValue value, string? indent, int depth)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                {
                    BsonDocument doc = value.AsBsonDocument;
                    if (doc.ElementCount == 0)
                    {
                        _ = b.Append("{}");
                        return;
                    }
                    _ = b.Append('{');
                    int i = 0;
                    foreach (BsonElement element in doc)
                    {
                        _ = b.Append(i++ == 0 ? "" : ",");
                        NewLine(b, indent, depth + 1);
                        _ = b.Append(FieldName(element.Name)).Append(": ");
                        WriteShell(b, element.Value, indent, depth + 1);
                    }
                    NewLine(b, indent, depth);
                    _ = b.Append('}');
                    return;
                }
            case BsonType.Array:
                {
                    BsonArray array = value.AsBsonArray;
                    if (array.Count == 0)
                    {
                        _ = b.Append("[]");
                        return;
                    }
                    // 全是标量的短数组写在一行:["企业", "开票"] 拆成三行只会把 JSON 视图拉长。
                    bool flat = indent is null || array.All(static v => !v.IsBsonDocument && !v.IsBsonArray) && array.Count <= 8;
                    _ = b.Append('[');
                    for (int i = 0; i < array.Count; i++)
                    {
                        _ = b.Append(i == 0 ? "" : ",");
                        if (flat)
                        {
                            _ = b.Append(' ');
                        }
                        else
                        {
                            NewLine(b, indent, depth + 1);
                        }
                        WriteShell(b, array[i], indent, depth + 1);
                    }
                    if (flat)
                    {
                        _ = b.Append(" ]");
                    }
                    else
                    {
                        NewLine(b, indent, depth);
                        _ = b.Append(']');
                    }
                    return;
                }
            case BsonType.ObjectId:
                _ = b.Append("ObjectId(\"").Append(value.AsObjectId).Append("\")");
                return;
            case BsonType.String:
                _ = b.Append(Quote(value.AsString));
                return;
            case BsonType.Int32:
                _ = b.Append(value.AsInt32.ToString(CultureInfo.InvariantCulture));
                return;
            case BsonType.Int64:
                _ = b.Append("NumberLong(\"").Append(value.AsInt64.ToString(CultureInfo.InvariantCulture)).Append("\")");
                return;
            case BsonType.Double:
                _ = b.Append(FormatDouble(value.AsDouble, forceDecimalPoint: false));
                return;
            case BsonType.Decimal128:
                _ = b.Append("NumberDecimal(\"").Append(value.AsDecimal128).Append("\")");
                return;
            case BsonType.Boolean:
                _ = b.Append(value.AsBoolean ? "true" : "false");
                return;
            case BsonType.DateTime:
                _ = b.Append("ISODate(\"").Append(IsoDate(value)).Append("\")");
                return;
            case BsonType.Null:
                _ = b.Append("null");
                return;
            case BsonType.Binary:
                {
                    BsonBinaryData binary = value.AsBsonBinaryData;
                    if (binary.SubType == BsonBinarySubType.UuidStandard)
                    {
                        _ = b.Append("UUID(\"").Append(binary.ToGuid()).Append("\")");
                    }
                    else
                    {
                        _ = b.Append("BinData(").Append((int)binary.SubType).Append(", \"")
                            .Append(Convert.ToBase64String(binary.Bytes)).Append("\")");
                    }
                    return;
                }
            case BsonType.RegularExpression:
                {
                    BsonRegularExpression regex = value.AsBsonRegularExpression;
                    _ = b.Append('/').Append(regex.Pattern.Replace("/", "\\/", StringComparison.Ordinal)).Append('/').Append(regex.Options);
                    return;
                }
            case BsonType.Timestamp:
                _ = b.Append("Timestamp({ t: ").Append(value.AsBsonTimestamp.Timestamp)
                    .Append(", i: ").Append(value.AsBsonTimestamp.Increment).Append(" })");
                return;
            case BsonType.MinKey:
                _ = b.Append("MinKey()");
                return;
            case BsonType.MaxKey:
                _ = b.Append("MaxKey()");
                return;
            case BsonType.Undefined:
                _ = b.Append("undefined");
                return;
            default:
                _ = b.Append(value.ToJson());
                return;
        }
    }

    private static void NewLine(StringBuilder b, string? indent, int depth)
    {
        if (indent is null)
        {
            _ = b.Append(' ');
            return;
        }
        _ = b.Append('\n');
        for (int i = 0; i < depth; i++)
        {
            _ = b.Append(indent);
        }
    }

    /// <summary>键名:合法标识符不加引号(mongosh 风格),其余加双引号。</summary>
    public static string FieldName(string name) => IdentifierPattern().IsMatch(name) ? name : Quote(name);

    /// <summary>双引号字符串字面量(按 JSON 规则转义)。</summary>
    public static string Quote(string text)
    {
        var b = new StringBuilder(text.Length + 2);
        _ = b.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"': _ = b.Append("\\\""); break;
                case '\\': _ = b.Append("\\\\"); break;
                case '\n': _ = b.Append("\\n"); break;
                case '\r': _ = b.Append("\\r"); break;
                case '\t': _ = b.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        _ = b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _ = b.Append(c);
                    }
                    break;
            }
        }
        _ = b.Append('"');
        return b.ToString();
    }

    /// <summary>ISO 8601 UTC(毫秒精度),与 mongosh 的 <c>ISODate</c> 输出一致。</summary>
    public static string IsoDate(BsonValue value)
    {
        long ms = value.AsBsonDateTime.MillisecondsSinceEpoch;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return at.UtcDateTime.ToString(at.Millisecond == 0 ? "yyyy-MM-ddTHH:mm:ssZ" : "yyyy-MM-ddTHH:mm:ss.fffZ",
            CultureInfo.InvariantCulture);
    }

    /// <summary>网格日期:本地时间。超出 .NET 可表示范围的毫秒值(BSON 允许)原样给 ISO 串。</summary>
    public static string FormatDate(BsonValue value)
    {
        long ms = value.AsBsonDateTime.MillisecondsSinceEpoch;
        if (ms is < -62135596800000L or > 253402300799999L)
        {
            return ms.ToString(CultureInfo.InvariantCulture);
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(DateFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Decimal128:保留原始小数位,整数部分加千分位(<c>8,740.00</c>)。</summary>
    public static string FormatDecimal(Decimal128 value)
    {
        string raw = value.ToString();
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed))
        {
            return raw;
        }
        int dot = raw.IndexOf('.');
        int scale = dot < 0 || raw.Contains('E', StringComparison.OrdinalIgnoreCase) ? 0 : raw.Length - dot - 1;
        return parsed.ToString("N" + scale.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Double:最短往返表示;整数值补一位小数以免看成 Int32(<c>12.0</c>)。</summary>
    public static string FormatDouble(double value, bool forceDecimalPoint = true)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }
        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return forceDecimalPoint && !text.Contains('.') && !text.Contains('E') ? text + ".0" : text;
    }

    /// <summary>文档摘要:头两个标量值(<c>陈立 · VIP</c>)。</summary>
    public static string Summary(BsonDocument doc)
    {
        IEnumerable<string> parts = doc.Elements
            .Where(static e => e.Name != "_id" && e.Value.BsonType is BsonType.String or BsonType.Int32 or BsonType.Int64
                or BsonType.Double or BsonType.Decimal128 or BsonType.Boolean)
            .Take(2)
            .Select(static e => OneLine(e.Value.BsonType == BsonType.String ? e.Value.AsString : Cell(e.Value)));
        return string.Join(" · ", parts);
    }

    /// <summary>数组摘要:第一项(容器取它的第一个字符串)+ 省略号。</summary>
    public static string ArraySummary(BsonArray array)
    {
        if (array.Count == 0)
        {
            return "";
        }
        BsonValue first = array[0];
        string head = first.BsonType switch
        {
            BsonType.Document => first.AsBsonDocument.Elements.FirstOrDefault(static e => e.Value.IsString).Value?.AsString
                                 ?? $"{{{first.AsBsonDocument.ElementCount}}}",
            BsonType.String => first.AsString,
            _ => Cell(first)
        };
        return array.Count > 1 ? OneLine(head) + "…" : OneLine(head);
    }

    /// <summary>把多行文本压成一行(网格与检查器里换行会把行高撑破)。</summary>
    public static string OneLine(string text)
    {
        if (text.Length > 300)
        {
            text = text[..300] + "…";
        }
        return text.Replace("\r\n", " ⏎ ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
    }

    /// <summary>ObjectId 缩写:<c>66f5c2a1…e3b7</c>。</summary>
    public static string Shorten(string hex) => hex.Length > 14 ? $"{hex[..8]}…{hex[^4..]}" : hex;

    /// <summary>字节数 → <c>42.1 GB</c>。</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>计数缩写(对象树右侧):<c>1.28M</c>、<c>86.4K</c>、<c>640</c>。</summary>
    public static string Count(long count) => count switch
    {
        >= 1_000_000_000 => (count / 1e9).ToString(count >= 10_000_000_000 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (count / 1e6).ToString(count >= 10_000_000 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (count / 1e3).ToString(count >= 100_000 ? "0" : "0.0", CultureInfo.InvariantCulture) + "K",
        _ => count.ToString(CultureInfo.InvariantCulture)
    };

    /// <summary>带千分位的整数(<c>1,284,902</c>)。</summary>
    public static string Grouped(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierPattern();
}
