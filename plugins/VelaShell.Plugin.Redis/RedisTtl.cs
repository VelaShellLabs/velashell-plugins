using System.Globalization;
using System.Text;

namespace VelaShell.Plugin.Redis;

/// <summary>
/// TTL 的输入解析与显示。
/// <para>
/// 三种写法都要接:纯秒数(<c>900</c>)、带单位的时长(<c>15m</c> / <c>2h30m</c> / <c>7d</c>)、
/// 绝对时间点(<c>2026-08-20 12:00</c>)。**这不是花活** —— 运维脑子里想的是"再放半小时"
/// 或"活到明天中午",逼他先换算成秒是把机器的口径强加给人。
/// </para>
/// </summary>
public static class RedisTtl
{
    /// <summary>
    /// 解析一段 TTL 输入。
    /// </summary>
    /// <param name="text">用户输入。</param>
    /// <param name="now">
    /// 当前时间。绝对时间点写法拿它算差值,**并且用它的时区偏移解释不带时区的输入** ——
    /// 本方法完全不摸机器本地时区,同一份输入在任何机器上结果都一样。
    /// 传入而不是取 <c>DateTimeOffset.Now</c> 也是为了单测。
    /// </param>
    /// <param name="ttl">解析出的存活时长。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(string? text, DateTimeOffset now, out TimeSpan ttl)
    {
        ttl = default;
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }
        // 纯数字 = 秒。放在最前面:它是 redis-cli 的口径,也是复制粘贴最常见的形式。
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds <= 0)
            {
                return false;
            }
            ttl = TimeSpan.FromSeconds(seconds);
            return true;
        }
        if (TryParseDuration(trimmed, out ttl))
        {
            return true;
        }
        // 绝对时间点:用户填的是**他表上的时间**,所以按 now 所在的时区解析,再折算成剩余时长。
        //
        // 刻意用 now.Offset 而不是机器本地时区:now 是调用方显式传进来的,再去摸
        // TimeZoneInfo.Local 等于让同一份输入在不同机器上算出不同结果 —— 在 UTC 的 CI runner 上
        // "2026-08-17 18:00" 相对 12:00+08:00 会算出 14 小时而不是 6 小时(2026-08-22 踩过)。
        // 生产路径全部传 DateTimeOffset.Now,now.Offset 就是本地时区,行为不变。
        //
        // DateTimeStyles.None(而非 AssumeLocal):无时区的输入解析出 Kind=Unspecified,
        // 才有资格被安到 now.Offset 上;输入自带 Z / ±hh:mm 时 TryParse 会转成 Local,
        // 那已经是一个确定的时刻,照原样用。
        if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime absolute)
            || DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out absolute))
        {
            DateTimeOffset instant = absolute.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(absolute, now.Offset)
                : new DateTimeOffset(absolute);
            TimeSpan remaining = instant - now;
            if (remaining <= TimeSpan.Zero)
            {
                // 过去的时间点不当成"立刻过期":那等于用一个看着像笔误的输入删掉一个键。
                return false;
            }
            ttl = remaining;
            return true;
        }
        return false;
    }

    /// <summary>带单位的时长:<c>7d</c> / <c>2h30m</c> / <c>90s</c>,可连写、大小写不敏感。</summary>
    private static bool TryParseDuration(string text, out TimeSpan ttl)
    {
        ttl = TimeSpan.Zero;
        long value = 0;
        bool sawDigit = false;
        bool sawUnit = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c))
            {
                value = (value * 10) + (c - '0');
                sawDigit = true;
                continue;
            }
            if (!sawDigit)
            {
                return false;
            }
            TimeSpan unit = char.ToLowerInvariant(c) switch
            {
                'd' => TimeSpan.FromDays(value),
                'h' => TimeSpan.FromHours(value),
                'm' => TimeSpan.FromMinutes(value),
                's' => TimeSpan.FromSeconds(value),
                _ => TimeSpan.Zero
            };
            if (unit == TimeSpan.Zero)
            {
                return false;
            }
            ttl += unit;
            value = 0;
            sawDigit = false;
            sawUnit = true;
        }
        // 结尾还有没消费掉的数字(如 "2h30")= 输入不完整,不猜它的单位。
        return sawUnit && !sawDigit && ttl > TimeSpan.Zero;
    }

    /// <summary>把时长渲染成人能读的形式(<c>2 天 3 小时</c> / <c>29:58</c>)。</summary>
    /// <param name="ttl">时长。</param>
    /// <returns>显示文本。</returns>
    public static string Describe(TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
        {
            return "0";
        }
        if (ttl.TotalDays >= 1)
        {
            var builder = new StringBuilder();
            builder.Append(CultureInfo.CurrentCulture, $"{(int)ttl.TotalDays}d");
            if (ttl.Hours > 0)
            {
                builder.Append(CultureInfo.CurrentCulture, $" {ttl.Hours}h");
            }
            return builder.ToString();
        }
        return ttl.TotalHours >= 1
            ? $"{(int)ttl.TotalHours}:{ttl.Minutes:00}:{ttl.Seconds:00}"
            : $"{ttl.Minutes:00}:{ttl.Seconds:00}";
    }
}
