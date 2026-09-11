using System.Globalization;

namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>把机器的数字翻成人看的字。</summary>
public static class Humanize
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// 字节数。与 <c>docker</c> 自己一样用 1000 进制 —— 面板里显示的 "214 MB"
    /// 要和用户在 <c>docker images</c> 里看到的对得上,不然他会以为哪里算错了。
    /// </summary>
    public static string Bytes(long value)
    {
        if (value < 0)
        {
            return "—";
        }
        if (value < 1000)
        {
            return $"{value} B";
        }
        double size = value;
        var unit = 0;
        while (size >= 1000 && unit < SizeUnits.Length - 1)
        {
            size /= 1000;
            unit++;
        }
        return size >= 100
            ? $"{size:0} {SizeUnits[unit]}"
            : size >= 10
                ? $"{size:0.#} {SizeUnits[unit]}"
                : $"{size:0.##} {SizeUnits[unit]}";
    }

    /// <summary>字节数(无符号重载)。</summary>
    public static string Bytes(ulong value) => Bytes(value > long.MaxValue ? long.MaxValue : (long)value);

    /// <summary>百分比,保留一位小数。</summary>
    public static string Percent(double value) => value <= 0 ? "0%" : $"{value.ToString("0.#", CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// 一段时长,精确到两档("3d 4h"、"6h 12m"、"48s")。
    /// 运维要的是量级,不是秒数 —— 多一档只会把列挤宽。
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                    : $"{(int)span.TotalSeconds}s";
    }

    /// <summary>把一个时刻说成"多久以前"。</summary>
    public static string Ago(DateTimeOffset moment)
    {
        var span = DateTimeOffset.UtcNow - moment.ToUniversalTime();
        return span < TimeSpan.FromSeconds(5) ? "刚刚" : $"{Duration(span)} 前";
    }

    /// <summary>把 ISO8601 文本说成"多久以前";解析不了返回破折号。</summary>
    /// <remarks>
    /// 镜像的两个接口对创建时间用了两种表示:列表给 unix 秒,inspect 给 RFC3339 串。
    /// 所以这两个入口都得有。
    /// </remarks>
    public static string AgoFromIso(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment)
            ? Ago(moment)
            : "—";

    /// <summary>把 unix 秒说成"多久以前"。</summary>
    public static string AgoFromUnix(long unixSeconds) =>
        unixSeconds <= 0 ? "—" : Ago(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    /// <summary>把 ISO8601 文本解析成本地时间文本;解析不了就原样返回。</summary>
    public static string LocalTime(string? iso)
    {
        return string.IsNullOrWhiteSpace(iso) ? "—"
            : DateTimeOffset.TryParse(iso, null, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : iso;
    }

    /// <summary>
    /// 容器的端口摘要:<c>8080→80</c>,多条用逗号连;没有发布端口就给一个破折号。
    /// </summary>
    public static string Ports(DockerPort[]? ports)
    {
        if (ports is null or { Length: 0 })
        {
            return "—";
        }
        // 同一个容器端口可能同时绑在 0.0.0.0 与 :: 上,daemon 会给两条。
        // 界面上它们是同一件事,去重之后才不会显示成 "8080→80, 8080→80"。
        var published = ports
            .Where(p => p.PublicPort > 0)
            .Select(p => $"{p.PublicPort}→{p.PrivatePort}")
            .Distinct();
        var exposed = ports
            .Where(p => p.PublicPort == 0)
            .Select(p => $"{p.PrivatePort}/{p.Type ?? "tcp"}")
            .Distinct();
        var text = string.Join(", ", published.Concat(exposed));
        return text.Length == 0 ? "—" : text;
    }

    /// <summary>短 id(12 位),Docker 自己在命令行里就是这么显示的。</summary>
    public static string ShortId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "";
        }
        var text = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return text.Length <= 12 ? text : text[..12];
    }
}
