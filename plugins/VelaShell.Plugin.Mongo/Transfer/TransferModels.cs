using MongoDB.Bson;
using VelaShell.Plugin.Mongo.Bson;

namespace VelaShell.Plugin.Mongo.Transfer;

/// <summary>
/// CSV / Excel 单元格的值换算(设计稿 19 字段表的「转换」列)。
/// <para>
/// 换算按**字段**挑,而不是全局一个开关:同一张表里 <c>_id</c> 要十六进制串、<c>total</c> 要两位小数、
/// <c>createdAt</c> 要本地时间 —— 这正是从 MongoDB 导出给财务看的那张表的样子。
/// </para>
/// </summary>
internal enum CellConversion
{
    /// <summary>原样(字符串、整数;Decimal 保留原始小数位)。</summary>
    None,

    /// <summary>ObjectId → 24 位十六进制字符串。</summary>
    Hex,

    /// <summary>数值 → 保留 2 位小数(不带千分位 —— 千分位会让 CSV 的逗号分隔串位)。</summary>
    Fixed2,

    /// <summary>日期 → 本地时间 <c>yyyy-MM-dd HH:mm:ss</c>。</summary>
    LocalTime,

    /// <summary>日期 → ISO 8601 UTC。</summary>
    IsoTime,

    /// <summary>数组 / 嵌套文档 → 单行 JSON 字符串(Relaxed EJSON)。</summary>
    Json
}

/// <summary>CSV 里嵌套文档的写法。</summary>
internal enum NestedMode
{
    /// <summary>展开为 <c>a.b</c> 列。</summary>
    Flatten,

    /// <summary>整个对象写成一列 JSON 字符串。</summary>
    Json
}

/// <summary>文本文件的编码。</summary>
internal enum TextEncodingKind
{
    /// <summary>UTF-8(无 BOM)。</summary>
    Utf8,

    /// <summary>UTF-8 带 BOM —— Excel 双击打开 CSV 时靠它认出 UTF-8,否则中文全是乱码。</summary>
    Utf8Bom,

    /// <summary>GBK(代码页 936):老版本 Excel 与国内不少系统的默认编码。</summary>
    Gbk,

    /// <summary>UTF-16 LE(带 BOM)。</summary>
    Utf16
}

/// <summary>CSV 选项。</summary>
internal sealed record CsvOptions
{
    /// <summary>分隔符。</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>编码。</summary>
    public TextEncodingKind Encoding { get; init; } = TextEncodingKind.Utf8Bom;

    /// <summary>嵌套对象的写法。</summary>
    public NestedMode Nested { get; init; } = NestedMode.Flatten;

    /// <summary>null 写成空单元格(否则写字面量 <c>null</c>)。</summary>
    public bool NullAsEmpty { get; init; } = true;

    /// <summary>首行写入列名。</summary>
    public bool WriteHeader { get; init; } = true;
}

/// <summary>JSON 选项。</summary>
internal sealed record JsonOptions
{
    /// <summary>行分隔(JSONL,一行一份文档 —— 与 <c>mongoexport</c> 默认一致);否则写成一个数组。</summary>
    public bool Lines { get; init; } = true;

    /// <summary>扩展 JSON 写法。</summary>
    public EjsonMode Mode { get; init; } = EjsonMode.Relaxed;
}

/// <summary>Excel 选项。</summary>
internal sealed record ExcelOptions
{
    /// <summary>工作表名(空 = 用集合名)。</summary>
    public string SheetName { get; init; } = "";

    /// <summary>冻结首行。</summary>
    public bool FreezeHeader { get; init; } = true;

    /// <summary>首行写入列名。</summary>
    public bool WriteHeader { get; init; } = true;
}

/// <summary>BSON 转储选项。</summary>
internal sealed record DumpOptions
{
    /// <summary>gzip 压缩(<c>mongodump --gzip</c> 的布局:<c>.bson.gz</c> + <c>.metadata.json.gz</c>)。</summary>
    public bool Gzip { get; init; }
}

/// <summary>Shell 脚本选项。</summary>
internal sealed record ShellOptions
{
    /// <summary>每条 <c>insertMany</c> 带多少份文档(mongosh 单条语句太大会解析得很慢)。</summary>
    public int BatchSize { get; init; } = 1000;
}

/// <summary>CSV / Excel 的一列。</summary>
/// <param name="Path">字段路径(<c>customer.name</c>)。</param>
/// <param name="Header">列名(可改,<c>客户</c>)。</param>
/// <param name="Conversion">值换算。</param>
internal sealed record ExportColumn(string Path, string Header, CellConversion Conversion);

/// <summary>抽样得到的一个字段。</summary>
/// <param name="Path">路径。</param>
/// <param name="Kinds">出现过的类型(按出现次数降序,null 排在非空类型之后)。</param>
/// <param name="Count">出现在多少份文档里。</param>
/// <param name="Total">抽样文档数。</param>
internal sealed record XferField(string Path, IReadOnlyList<BsonKind> Kinds, int Count, int Total)
{
    /// <summary>主类型(第一个非 null 的类型;全是 null 时就是 Null)。</summary>
    public BsonKind Dominant => Kinds.FirstOrDefault(static k => k != BsonKind.Null, Kinds.Count > 0 ? Kinds[0] : BsonKind.Missing);

    /// <summary>出现率。</summary>
    public double Presence => Total == 0 ? 0 : (double)Count / Total;

    /// <summary>出现过 null(或在部分文档里缺失)。</summary>
    public bool Nullable => Kinds.Contains(BsonKind.Null) || Count < Total;

    /// <summary>类型文字(<c>String | Null</c>)。</summary>
    public string KindsText => string.Join(" | ", Kinds.Take(3).Select(BsonKinds.Name));

    /// <summary>这个字段默认的换算。</summary>
    public CellConversion DefaultConversion => DefaultConversionFor(Dominant);

    /// <summary>某种类型默认的换算。</summary>
    public static CellConversion DefaultConversionFor(BsonKind kind) => kind switch
    {
        BsonKind.ObjectId => CellConversion.Hex,
        BsonKind.Decimal128 => CellConversion.Fixed2,
        BsonKind.Date => CellConversion.LocalTime,
        BsonKind.Array or BsonKind.Object => CellConversion.Json,
        _ => CellConversion.None
    };

    /// <summary>某种类型可选的换算(字段表的下拉)。</summary>
    public static IReadOnlyList<CellConversion> ChoicesFor(BsonKind kind) => kind switch
    {
        BsonKind.ObjectId => [CellConversion.Hex],
        BsonKind.Decimal128 or BsonKind.Double => [CellConversion.None, CellConversion.Fixed2],
        BsonKind.Date => [CellConversion.LocalTime, CellConversion.IsoTime],
        BsonKind.Array or BsonKind.Object => [CellConversion.Json],
        _ => [CellConversion.None]
    };
}

/// <summary>导出进度。</summary>
/// <param name="Documents">已写出的文档数。</param>
/// <param name="Total">预计总数(未知为 0)。</param>
/// <param name="Bytes">已写出的字节。</param>
/// <param name="Elapsed">已用时间。</param>
/// <param name="Collection">当前集合。</param>
internal sealed record ExportProgress(long Documents, long Total, long Bytes, TimeSpan Elapsed, string Collection);

/// <summary>导出结果。</summary>
/// <param name="Documents">写出的文档数。</param>
/// <param name="Bytes">写出的字节。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Files">生成的文件(转储与多集合导出是多个)。</param>
internal sealed record ExportResult(long Documents, long Bytes, TimeSpan Elapsed, IReadOnlyList<string> Files);

/// <summary>一行日志的语气(数据传输右栏、导出 / 导入执行页)。</summary>
internal enum XferTone
{
    /// <summary>普通。</summary>
    Normal,

    /// <summary>成功(绿)。</summary>
    Ok,

    /// <summary>警告(橙)。</summary>
    Warn,

    /// <summary>错误(红)。</summary>
    Error,

    /// <summary>弱化(跳过之类)。</summary>
    Muted,

    /// <summary>进行中(强调色)。</summary>
    Accent
}

/// <summary>一些共用的小工具。</summary>
internal static class TransferText
{
    /// <summary>剩余 / 已用时间:<c>&lt; 1 s</c>、<c>50 s</c>、<c>3 min 20 s</c>、<c>1 h 05 min</c>。</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1))
        {
            return "< 1 s";
        }
        if (span < TimeSpan.FromMinutes(1))
        {
            return $"{(int)span.TotalSeconds} s";
        }
        if (span < TimeSpan.FromHours(1))
        {
            return span.Seconds == 0 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalMinutes} min {span.Seconds} s";
        }
        return $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }

    /// <summary>速率:<c>18.2k</c>、<c>940</c>。</summary>
    public static string Rate(double perSecond) => perSecond switch
    {
        >= 1_000_000 => (perSecond / 1_000_000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (perSecond / 1_000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k",
        _ => perSecond.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>本机时区的 <c>UTC+8</c> 写法(设计稿「本地时间 UTC+8」)。</summary>
    public static string LocalOffset()
    {
        TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return offset.Minutes == 0
            ? $"UTC{sign}{offset.Hours}"
            : $"UTC{sign}{offset.Hours}:{offset.Minutes:00}";
    }

    /// <summary>文件名里不能出现的字符换成下划线(集合名可以带 <c>:</c> <c>/</c> 之类)。</summary>
    public static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string safe = new string(chars).Trim();
        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>一个值是不是值得在字段表里展开的嵌套文档。</summary>
    public static bool IsNestedDocument(BsonValue value) => value is BsonDocument { ElementCount: > 0 };
}
