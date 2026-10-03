using System.Globalization;
using MongoDB.Bson;

namespace VelaShell.Plugin.Mongo.Bson;

/// <summary>
/// 用户敲进单元格 / 表单的文本 → 指定类型的 BSON 值;以及"修改类型"时的值换算。
/// <para>
/// 这里是"类型化编辑"的全部规则所在:网格的内联编辑、检查器、文档编辑器的表单
/// 三处都走它,所以同一段文本在三处得到同一个值 —— 不会出现网格里存成 Int32、
/// 表单里却存成 Double 这种两套口径。
/// </para>
/// </summary>
public static class BsonEdit
{
    /// <summary>按目标类型解析文本。</summary>
    /// <param name="text">用户输入。</param>
    /// <param name="kind">目标类型。</param>
    /// <param name="value">解析结果。</param>
    /// <param name="error">失败原因(文案键)。</param>
    /// <returns>是否成功。</returns>
    public static bool TryParse(string? text, BsonKind kind, out BsonValue value, out string? error)
    {
        string raw = text ?? "";
        string trimmed = raw.Trim();
        error = null;
        value = BsonNull.Value;
        switch (kind)
        {
            case BsonKind.String:
                value = new BsonString(raw);
                return true;
            case BsonKind.Null:
                return true;
            case BsonKind.Int32:
                if (long.TryParse(StripGrouping(trimmed), NumberStyles.Integer, CultureInfo.InvariantCulture, out long i32)
                    && i32 is >= int.MinValue and <= int.MaxValue)
                {
                    value = new BsonInt32((int)i32);
                    return true;
                }
                error = "Edit_NotInt32";
                return false;
            case BsonKind.Int64:
                if (long.TryParse(StripGrouping(trimmed), NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64))
                {
                    value = new BsonInt64(i64);
                    return true;
                }
                error = "Edit_NotInt64";
                return false;
            case BsonKind.Double:
                if (double.TryParse(StripGrouping(trimmed), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    value = new BsonDouble(d);
                    return true;
                }
                error = "Edit_NotNumber";
                return false;
            case BsonKind.Decimal128:
                if (Decimal128.TryParse(StripGrouping(trimmed), out Decimal128 dec))
                {
                    value = new BsonDecimal128(dec);
                    return true;
                }
                error = "Edit_NotNumber";
                return false;
            case BsonKind.Boolean:
                switch (trimmed.ToLowerInvariant())
                {
                    case "true" or "1" or "yes" or "是":
                        value = BsonBoolean.True;
                        return true;
                    case "false" or "0" or "no" or "否":
                        value = BsonBoolean.False;
                        return true;
                    default:
                        error = "Edit_NotBoolean";
                        return false;
                }
            case BsonKind.Date:
                if (TryParseDate(trimmed, out DateTime date))
                {
                    value = new BsonDateTime(date);
                    return true;
                }
                error = "Edit_NotDate";
                return false;
            case BsonKind.ObjectId:
            {
                string hex = Unwrap(trimmed, "ObjectId");
                if (ObjectId.TryParse(hex, out ObjectId id))
                {
                    value = id;
                    return true;
                }
                error = "Edit_NotObjectId";
                return false;
            }
            case BsonKind.Uuid:
            {
                string guid = Unwrap(trimmed, "UUID");
                if (Guid.TryParse(guid, out Guid g))
                {
                    value = new BsonBinaryData(g, GuidRepresentation.Standard);
                    return true;
                }
                error = "Edit_NotUuid";
                return false;
            }
            case BsonKind.Object or BsonKind.Array:
                try
                {
                    BsonValue parsed = ShellJson.ParseValue(trimmed.Length == 0 ? (kind == BsonKind.Object ? "{}" : "[]") : trimmed);
                    if (kind == BsonKind.Object ? parsed is BsonDocument : parsed is BsonArray)
                    {
                        value = parsed;
                        return true;
                    }
                    error = kind == BsonKind.Object ? "Edit_NotObject" : "Edit_NotArray";
                    return false;
                }
                catch (ShellJsonException)
                {
                    error = kind == BsonKind.Object ? "Edit_NotObject" : "Edit_NotArray";
                    return false;
                }
            default:
                try
                {
                    value = ShellJson.ParseValue(trimmed);
                    return true;
                }
                catch (ShellJsonException)
                {
                    error = "Edit_BadLiteral";
                    return false;
                }
        }
    }

    /// <summary>
    /// 编辑框里的初始文本:与 <see cref="TryParse" /> 互逆(数值不带千分位、字符串不带引号、
    /// 日期用本地时间,容器给单行 shell 字面量)。
    /// </summary>
    public static string EditText(BsonValue? value) => BsonKinds.Of(value) switch
    {
        BsonKind.Missing or BsonKind.Null => "",
        BsonKind.String => value!.AsString,
        BsonKind.Int32 => value!.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonKind.Int64 => value!.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonKind.Double => BsonText.FormatDouble(value!.AsDouble, forceDecimalPoint: false),
        BsonKind.Decimal128 => value!.AsDecimal128.ToString(),
        BsonKind.Boolean => value!.AsBoolean ? "true" : "false",
        BsonKind.Date => BsonText.FormatDate(value!),
        BsonKind.ObjectId => value!.AsObjectId.ToString(),
        BsonKind.Uuid => value!.AsBsonBinaryData.ToGuid().ToString(),
        _ => BsonText.Literal(value!)
    };

    /// <summary>
    /// 换类型:先把当前值写成编辑文本,再按新类型解析;解析不了就给新类型的空值
    /// (换成 Object 给 <c>{}</c>,换成 Int32 给 0)并报告"值已重置"。
    /// </summary>
    /// <param name="value">当前值。</param>
    /// <param name="kind">新类型。</param>
    /// <param name="reset">值是否因无法换算被重置。</param>
    /// <returns>换算后的值。</returns>
    public static BsonValue Convert(BsonValue? value, BsonKind kind, out bool reset)
    {
        reset = false;
        if (BsonKinds.Of(value) == kind)
        {
            return value!;
        }
        // 数值之间直接换算,不经文本(避免 Decimal 的尾零与 Double 的精度丢失被放大)。
        if (value is not null && value.IsNumeric)
        {
            switch (kind)
            {
                case BsonKind.Int32 when value.ToDouble() is >= int.MinValue and <= int.MaxValue:
                    return new BsonInt32((int)Math.Round(value.ToDouble()));
                case BsonKind.Int64:
                    return new BsonInt64((long)Math.Round(value.ToDouble()));
                case BsonKind.Double:
                    return new BsonDouble(value.ToDouble());
                case BsonKind.Decimal128:
                    return new BsonDecimal128(value.ToDecimal128());
            }
        }
        if (kind == BsonKind.String)
        {
            return new BsonString(value is null ? "" : EditText(value));
        }
        if (TryParse(value is null ? "" : EditText(value), kind, out BsonValue converted, out _))
        {
            return converted;
        }
        reset = true;
        return Empty(kind);
    }

    /// <summary>某类型的"空值"(新增字段、换类型失败时的初值)。</summary>
    public static BsonValue Empty(BsonKind kind) => kind switch
    {
        BsonKind.String => new BsonString(""),
        BsonKind.Int32 => new BsonInt32(0),
        BsonKind.Int64 => new BsonInt64(0),
        BsonKind.Double => new BsonDouble(0),
        BsonKind.Decimal128 => new BsonDecimal128(Decimal128.Zero),
        BsonKind.Boolean => BsonBoolean.False,
        BsonKind.Date => new BsonDateTime(DateTime.UtcNow),
        BsonKind.ObjectId => ObjectId.GenerateNewId(),
        BsonKind.Object => new BsonDocument(),
        BsonKind.Array => new BsonArray(),
        BsonKind.Uuid => new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard),
        _ => BsonNull.Value
    };

    /// <summary>日期:本地 <c>yyyy-MM-dd HH:mm[:ss]</c>,或 ISO 8601(带 Z / 偏移即按它)。</summary>
    public static bool TryParseDate(string text, out DateTime utc)
    {
        string trimmed = Unwrap(Unwrap(text.Trim(), "ISODate"), "new Date");
        string[] localFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(trimmed, localFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime local))
        {
            utc = local.ToUniversalTime();
            return true;
        }
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset iso))
        {
            utc = iso.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    private static string StripGrouping(string text) => text.Replace(",", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);

    /// <summary>剥掉 <c>Ctor("…")</c> 外壳(用户从别处粘了带构造器的值进来)。</summary>
    private static string Unwrap(string text, string ctor)
    {
        if (text.StartsWith(ctor + "(", StringComparison.Ordinal) && text.EndsWith(')'))
        {
            text = text[(ctor.Length + 1)..^1].Trim();
        }
        return text.Trim('"', '\'');
    }
}
