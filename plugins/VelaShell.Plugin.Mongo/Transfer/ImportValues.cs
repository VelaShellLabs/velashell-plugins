using System.Globalization;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>导入时一个单元格 / 字段上发现的问题种类(设计稿 20 右栏「问题」的分组键)。</summary>
internal enum ImportIssueKind
{
    /// <summary>缺少必填字段(集合 validator 的 required,或 upsert / 替换的匹配键)。</summary>
    MissingRequired,

    /// <summary>日期无法解析。</summary>
    BadDate,

    /// <summary>数值无法解析。</summary>
    BadNumber,

    /// <summary>其他类型无法转换(布尔、ObjectId、JSON…)。</summary>
    BadValue,

    /// <summary>字符串自动转成了数值(去掉了千分位 / 引号 / 货币符号)—— 警告。</summary>
    NumberFromText,

    /// <summary>枚举值大小写已按 validator 的 enum 规范化 —— 警告。</summary>
    EnumCase,

    /// <summary>值不在 validator 的 enum 里。</summary>
    EnumMismatch,

    /// <summary>其他验证规则(minimum、pattern…,来自客户端 $jsonSchema 预检)。</summary>
    Schema,

    /// <summary>这一条本身解析不了(坏 JSON)。</summary>
    Parse
}

/// <summary>一个值的换算结果。</summary>
/// <param name="Value">换算后的值;失败为 <see langword="null" />。</param>
/// <param name="Issue">附带的问题(警告或错误);没有为 <see langword="null" />。</param>
/// <param name="IsError">问题是不是错误(这一行不能导入)。</param>
/// <param name="Detail">问题的细节(<c>MM/dd/yy</c>、<c>Paid → paid</c>)。</param>
internal readonly record struct ConvertOutcome(BsonValue? Value, ImportIssueKind? Issue, bool IsError, string Detail)
{
    /// <summary>成功、无问题。</summary>
    public static ConvertOutcome Ok(BsonValue value) => new(value, null, false, "");

    /// <summary>成功但带警告。</summary>
    public static ConvertOutcome Warn(BsonValue value, ImportIssueKind kind, string detail) => new(value, kind, false, detail);

    /// <summary>失败。</summary>
    public static ConvertOutcome Fail(ImportIssueKind kind, string detail) => new(null, kind, true, detail);
}

/// <summary>
/// 导入的值换算:文本(CSV)或已有的 BSON 值(JSON / BSON)→ 目标类型。
/// <para>
/// 与网格编辑的 <see cref="BsonEdit.TryParse" /> 刻意**不同**的两点:
/// 1. 日期只认**无歧义**的写法(ISO 8601、<c>yyyy-MM-dd</c>、<c>yyyy/MM/dd</c>)。
///    <c>09/01/26</c> 是 9 月 1 日还是 1 月 9 日,这里不替用户猜 —— 报"日期无法解析",
///    由用户在右栏「指定日期格式」里给一个格式再重算;
/// 2. 带千分位、引号、货币符号的数字**能导入但要报警告**:那往往说明源数据是从报表里抄出来的,
///    用户该知道这件事。
/// </para>
/// </summary>
internal static partial class ImportValues
{
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
        "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd", "yyyy-MM-dd HH:mm:ssK"
    ];

    /// <summary>文本 → 目标类型。</summary>
    /// <param name="text">单元格原文。</param>
    /// <param name="kind">目标类型。</param>
    /// <param name="dateFormat">用户指定的日期格式(先按它试)。</param>
    public static ConvertOutcome FromText(string text, BsonKind kind, string? dateFormat)
    {
        string trimmed = text.Trim();
        switch (kind)
        {
            case BsonKind.String:
                return ConvertOutcome.Ok(new BsonString(text));
            case BsonKind.Null:
                return ConvertOutcome.Ok(BsonNull.Value);
            case BsonKind.Int32 or BsonKind.Int64 or BsonKind.Double or BsonKind.Decimal128:
                return Number(trimmed, kind);
            case BsonKind.Boolean:
                return trimmed.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "y" or "是" or "t" => ConvertOutcome.Ok(BsonBoolean.True),
                    "false" or "0" or "no" or "n" or "否" or "f" => ConvertOutcome.Ok(BsonBoolean.False),
                    _ => ConvertOutcome.Fail(ImportIssueKind.BadValue, trimmed)
                };
            case BsonKind.Date:
                return TryDate(trimmed, dateFormat, out DateTime utc)
                    ? ConvertOutcome.Ok(new BsonDateTime(utc))
                    : ConvertOutcome.Fail(ImportIssueKind.BadDate, GuessDateFormat(trimmed, includeTime: false) ?? trimmed);
            case BsonKind.ObjectId:
                {
                    string hex = Unwrap(trimmed, "ObjectId");
                    return ObjectId.TryParse(hex, out ObjectId id)
                        ? ConvertOutcome.Ok(id)
                        : ConvertOutcome.Fail(ImportIssueKind.BadValue, trimmed);
                }
            case BsonKind.Uuid:
                {
                    string guid = Unwrap(trimmed, "UUID");
                    return Guid.TryParse(guid, out Guid g)
                        ? ConvertOutcome.Ok(new BsonBinaryData(g, GuidRepresentation.Standard))
                        : ConvertOutcome.Fail(ImportIssueKind.BadValue, trimmed);
                }
            case BsonKind.Object or BsonKind.Array:
                try
                {
                    BsonValue parsed = ShellJson.ParseValue(trimmed);
                    return (kind == BsonKind.Object ? parsed is BsonDocument : parsed is BsonArray)
                        ? ConvertOutcome.Ok(parsed)
                        : ConvertOutcome.Fail(ImportIssueKind.BadValue, Shorten(trimmed));
                }
                catch (ShellJsonException)
                {
                    return ConvertOutcome.Fail(ImportIssueKind.BadValue, Shorten(trimmed));
                }
            default:
                return ConvertOutcome.Ok(new BsonString(text));
        }
    }

    /// <summary>已有的 BSON 值 → 目标类型(JSON / BSON 来源)。</summary>
    public static ConvertOutcome FromValue(BsonValue value, BsonKind kind, string? dateFormat)
    {
        BsonKind current = BsonKinds.Of(value);
        if (current == kind || current == BsonKind.Null)
        {
            return ConvertOutcome.Ok(value);
        }
        if (value is BsonString text)
        {
            ConvertOutcome converted = FromText(text.Value, kind, dateFormat);
            // JSON 里的字符串变成数值:能导入,但这是源数据类型不对的信号。
            return converted.Value is not null && BsonKinds.IsNumeric(kind)
                ? ConvertOutcome.Warn(converted.Value, ImportIssueKind.NumberFromText, "")
                : converted;
        }
        if (kind == BsonKind.String)
        {
            return ConvertOutcome.Ok(new BsonString(BsonEdit.EditText(value)));
        }
        if (value.IsNumeric && BsonKinds.IsNumeric(kind))
        {
            BsonValue number = BsonEdit.Convert(value, kind, out bool reset);
            return reset ? ConvertOutcome.Fail(ImportIssueKind.BadNumber, BsonText.Inline(value)) : ConvertOutcome.Ok(number);
        }
        if (kind == BsonKind.Date && value is BsonInt64 or BsonInt32 or BsonDouble)
        {
            long ms = value.ToInt64();
            return ConvertOutcome.Ok(new BsonDateTime(ms));
        }
        return ConvertOutcome.Fail(ImportIssueKind.BadValue, BsonText.Inline(value));
    }

    private static ConvertOutcome Number(string text, BsonKind kind)
    {
        if (TryNumber(text, kind, out BsonValue? value, out bool fraction))
        {
            return ConvertOutcome.Ok(value!);
        }
        if (fraction)
        {
            return ConvertOutcome.Fail(ImportIssueKind.BadNumber, text);
        }
        string cleaned = CleanNumber(text, out bool grouping);
        if (cleaned.Length > 0 && cleaned != text && TryNumber(cleaned, kind, out value, out _))
        {
            return ConvertOutcome.Warn(value!, ImportIssueKind.NumberFromText, grouping ? "grouping" : "symbols");
        }
        return ConvertOutcome.Fail(ImportIssueKind.BadNumber, text);
    }

    private static bool TryNumber(string text, BsonKind kind, out BsonValue? value, out bool fraction)
    {
        value = null;
        fraction = false;
        switch (kind)
        {
            case BsonKind.Int32 or BsonKind.Int64:
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole))
                {
                    if (kind == BsonKind.Int32 && whole is < int.MinValue or > int.MaxValue)
                    {
                        return false;
                    }
                    value = kind == BsonKind.Int32 ? new BsonInt32((int)whole) : new BsonInt64(whole);
                    return true;
                }
                // "12.00" 当整数可以接受;"12.5" 不行(静默截断会丢钱)。
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d))
                {
                    if (decimal.Truncate(d) != d)
                    {
                        fraction = true;
                        return false;
                    }
                    if (kind == BsonKind.Int32 && d is < int.MinValue or > int.MaxValue)
                    {
                        return false;
                    }
                    value = kind == BsonKind.Int32 ? new BsonInt32((int)d) : new BsonInt64((long)d);
                    return true;
                }
                return false;
            case BsonKind.Double:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double dbl))
                {
                    value = new BsonDouble(dbl);
                    return true;
                }
                return false;
            default:
                if (text.Length > 0 && (char.IsDigit(text[0]) || text[0] is '-' or '+' or '.')
                    && Decimal128.TryParse(text, out Decimal128 dec))
                {
                    value = new BsonDecimal128(dec);
                    return true;
                }
                return false;
        }
    }

    /// <summary>去掉引号、千分位、货币符号与空白(<c>"1,580.00"</c> → <c>1580.00</c>)。</summary>
    internal static string CleanNumber(string text, out bool grouping)
    {
        grouping = false;
        string t = text.Trim().Trim('"', '\'', '“', '”').Trim();
        var builder = new System.Text.StringBuilder(t.Length);
        foreach (char c in t)
        {
            switch (c)
            {
                case ',' or '_' or ' ' or ' ' or ' ':
                    grouping = true;
                    break;
                case '¥' or '￥' or '$' or '€' or '£':
                    break;
                default:
                    _ = builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 解日期:用户指定的格式优先;其余只认无歧义的写法。没有时区的按本地时间。
    /// </summary>
    public static bool TryDate(string text, string? dateFormat, out DateTime utc)
    {
        string t = Unwrap(Unwrap(text, "ISODate"), "new Date");
        if (dateFormat is { Length: > 0 }
            && DateTime.TryParseExact(t, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out DateTime custom))
        {
            utc = custom.ToUniversalTime();
            return true;
        }
        if (DateTimeOffset.TryParseExact(t, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset iso))
        {
            utc = iso.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }

    /// <summary>
    /// 猜一个日期写法的格式(<c>09/01/26 11:31</c> → <c>MM/dd/yy HH:mm</c>),
    /// 用来写"格式 MM/dd/yy"与"指定日期格式"的默认值。第一段大于 12 时猜 <c>dd/MM</c>。
    /// </summary>
    public static string? GuessDateFormat(string text, bool includeTime)
    {
        Match m = SlashDate().Match(text.Trim());
        string? date = null;
        string? time = null;
        if (m.Success)
        {
            bool dayFirst = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) > 12;
            string sep = m.Groups[2].Value;
            string year = m.Groups[4].Value.Length == 2 ? "yy" : "yyyy";
            date = dayFirst ? $"dd{sep}MM{sep}{year}" : $"MM{sep}dd{sep}{year}";
            if (m.Groups[5].Success)
            {
                time = m.Groups[7].Success ? "HH:mm:ss" : "HH:mm";
            }
        }
        else if (Compact().IsMatch(text.Trim()))
        {
            date = "yyyyMMdd";
        }
        if (date is null)
        {
            return null;
        }
        return includeTime && time is not null ? $"{date} {time}" : date;
    }

    /// <summary>剥掉 <c>Ctor("…")</c> 外壳与引号。</summary>
    private static string Unwrap(string text, string ctor)
    {
        string t = text.Trim();
        if (t.StartsWith(ctor + "(", StringComparison.Ordinal) && t.EndsWith(')'))
        {
            t = t[(ctor.Length + 1)..^1].Trim();
        }
        return t.Trim('"', '\'');
    }

    private static string Shorten(string text) => text.Length > 40 ? text[..40] + "…" : text;

    [GeneratedRegex(@"^(\d{1,2})([/.\-])(\d{1,2})\2(\d{2}|\d{4})(\s+(\d{1,2}):\d{2}(:\d{2})?)?$")]
    private static partial Regex SlashDate();

    [GeneratedRegex(@"^\d{8}$")]
    private static partial Regex Compact();
}
