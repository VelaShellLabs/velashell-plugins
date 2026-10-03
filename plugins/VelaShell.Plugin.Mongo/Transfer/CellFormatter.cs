using System.Globalization;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// BSON 值 → CSV / Excel 单元格文本。与网格的 <see cref="BsonText.Cell" /> 不同:
/// 这里的文本是**给下游程序读的**,所以数值不带千分位、字符串不截断不压行、容器写完整的 JSON。
/// </summary>
internal static class CellFormatter
{
    /// <summary>本地时间格式。</summary>
    public const string LocalFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>格式化一个单元格。</summary>
    /// <param name="value">值;<see langword="null" /> = 字段缺失。</param>
    /// <param name="conversion">换算。</param>
    /// <param name="nullAsEmpty">null 写成空(否则写 <c>null</c>)。</param>
    public static string Format(BsonValue? value, CellConversion conversion, bool nullAsEmpty)
    {
        switch (BsonKinds.Of(value))
        {
            case BsonKind.Missing:
                return "";
            case BsonKind.Null:
                return nullAsEmpty ? "" : "null";
            case BsonKind.ObjectId:
                return value!.AsObjectId.ToString();
            case BsonKind.String:
                return value!.AsString;
            case BsonKind.Int32:
                return conversion == CellConversion.Fixed2
                    ? value!.AsInt32.ToString("0.00", CultureInfo.InvariantCulture)
                    : value!.AsInt32.ToString(CultureInfo.InvariantCulture);
            case BsonKind.Int64:
                return conversion == CellConversion.Fixed2
                    ? value!.AsInt64.ToString("0.00", CultureInfo.InvariantCulture)
                    : value!.AsInt64.ToString(CultureInfo.InvariantCulture);
            case BsonKind.Double:
                return conversion == CellConversion.Fixed2 && double.IsFinite(value!.AsDouble)
                    ? value.AsDouble.ToString("0.00", CultureInfo.InvariantCulture)
                    : BsonText.FormatDouble(value!.AsDouble, forceDecimalPoint: false);
            case BsonKind.Decimal128:
                return FormatDecimal(value!.AsDecimal128, conversion == CellConversion.Fixed2);
            case BsonKind.Boolean:
                return value!.AsBoolean ? "true" : "false";
            case BsonKind.Date:
                return conversion == CellConversion.IsoTime ? BsonText.IsoDate(value!) : LocalDate(value!);
            case BsonKind.Object or BsonKind.Array:
                return BsonText.Compact(value!, EjsonMode.Relaxed);
            case BsonKind.Uuid:
                return value!.AsBsonBinaryData.ToGuid().ToString();
            case BsonKind.Binary:
                return Convert.ToBase64String(value!.AsBsonBinaryData.Bytes);
            case BsonKind.Regex:
                return value!.AsBsonRegularExpression.ToString();
            case BsonKind.Timestamp:
                return $"Timestamp({value!.AsBsonTimestamp.Timestamp}, {value.AsBsonTimestamp.Increment})";
            default:
                return BsonText.Literal(value!);
        }
    }

    /// <summary>Decimal128 → 文本;<paramref name="fixed2" /> 时四舍五入到两位(远离零)。</summary>
    public static string FormatDecimal(Decimal128 value, bool fixed2)
    {
        string raw = value.ToString();
        if (!fixed2)
        {
            return raw;
        }
        return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed)
            ? Math.Round(parsed, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture)
            : raw;
    }

    /// <summary>本地时间;超出 .NET 可表示范围的毫秒值给 ISO 串。</summary>
    public static string LocalDate(BsonValue value)
    {
        long ms = value.AsBsonDateTime.MillisecondsSinceEpoch;
        if (ms < -62135596800000L || ms > 253402300799999L)
        {
            return ms.ToString(CultureInfo.InvariantCulture);
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString(LocalFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 数值单元格(Excel 里写成数字而不是文本,才能求和排序)。不是数值或换算成文本时返回 <see langword="null" />。
    /// </summary>
    public static double? Number(BsonValue? value, CellConversion conversion) => BsonKinds.Of(value) switch
    {
        BsonKind.Int32 => value!.AsInt32,
        BsonKind.Int64 => value!.AsInt64,
        BsonKind.Double when double.IsFinite(value!.AsDouble) => conversion == CellConversion.Fixed2
            ? Math.Round(value.AsDouble, 2, MidpointRounding.AwayFromZero)
            : value.AsDouble,
        BsonKind.Decimal128 when decimal.TryParse(value!.AsDecimal128.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d) =>
            (double)(conversion == CellConversion.Fixed2 ? Math.Round(d, 2, MidpointRounding.AwayFromZero) : d),
        _ => null
    };
}
