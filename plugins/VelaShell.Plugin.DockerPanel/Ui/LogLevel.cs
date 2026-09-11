namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>一行日志的级别。</summary>
public enum LogLevel
{
    /// <summary>认不出来。</summary>
    None,

    /// <summary>调试。</summary>
    Debug,

    /// <summary>普通信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warn,

    /// <summary>错误。</summary>
    Error
}

/// <summary>
/// 从一行日志里认出级别。
/// <para>
/// 日志没有统一格式:nginx 写 <c>[warn]</c>,Go 服务写 <c>"level":"error"</c>,
/// Java 写 <c>ERROR</c> 顶格,systemd 风格写 <c>&lt;3&gt;</c>。这里只认前三种最常见的形态,
/// <b>并且只在行首那一小段里认</b> —— 全行扫描会把正文里提到 "error" 的成功日志
/// 也染成红色,那比不着色更糟。
/// </para>
/// </summary>
public static class LogLevels
{
    /// <summary>只在这么多字符里找级别标记。</summary>
    private const int HeadWindow = 64;

    /// <summary>认一行的级别。</summary>
    public static LogLevel Detect(string text)
    {
        if (text.Length == 0)
        {
            return LogLevel.None;
        }
        // 结构化日志(以 { 开头)**只**认显式的 level 字段。
        // 对它做裸词扫描会把 {"msg":"error budget remaining"} 染成红色 ——
        // 那是一条报告余量的正常日志,而 JSON 日志把级别放在字段里本来就是约定。
        if (text.AsSpan().TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return TryJsonLevel(text) ?? LogLevel.None;
        }
        if (TryJsonLevel(text) is { } fromJson)
        {
            return fromJson;
        }
        var head = text.AsSpan(0, Math.Min(text.Length, HeadWindow));
        return HasWord(head, "ERROR") || HasWord(head, "FATAL") || HasWord(head, "PANIC") ? LogLevel.Error
            : HasWord(head, "WARN") || HasWord(head, "WARNING") ? LogLevel.Warn
            : HasWord(head, "DEBUG") || HasWord(head, "TRACE") ? LogLevel.Debug
            : HasWord(head, "INFO") || HasWord(head, "NOTICE") ? LogLevel.Info
            : LogLevel.None;
    }

    /// <summary>
    /// 整词匹配。
    /// <para>
    /// 不能用裸的 <c>Contains</c>:一条 200 的访问日志里带着 <c>/api/v1/errors?page=2</c>,
    /// 子串匹配会把它染成红色,然后有人去查一个不存在的故障。要求前后都不是字母数字,
    /// <c>errors</c> 就不再命中 <c>ERROR</c>。
    /// </para>
    /// </summary>
    private static bool HasWord(ReadOnlySpan<char> haystack, ReadOnlySpan<char> word)
    {
        var from = 0;
        while (from < haystack.Length)
        {
            var at = haystack[from..].IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }
            at += from;
            var after = at + word.Length;
            var leftOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
            var rightOk = after >= haystack.Length || !char.IsLetterOrDigit(haystack[after]);
            if (leftOk && rightOk)
            {
                return true;
            }
            from = at + 1;
        }
        return false;
    }

    private static LogLevel? TryJsonLevel(string text)
    {
        foreach (var key in (string[])["\"level\"", "\"lvl\"", "\"severity\""])
        {
            var at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                continue;
            }
            // 取键之后一小段,足够覆盖 `: "error"` 这种带空格的写法。
            var from = at + key.Length;
            var value = text.AsSpan(from, Math.Min(24, text.Length - from));
            if (Contains(value, "error") || Contains(value, "fatal") || Contains(value, "panic"))
            {
                return LogLevel.Error;
            }
            if (Contains(value, "warn"))
            {
                return LogLevel.Warn;
            }
            if (Contains(value, "debug") || Contains(value, "trace"))
            {
                return LogLevel.Debug;
            }
            if (Contains(value, "info"))
            {
                return LogLevel.Info;
            }
        }
        return null;
    }

    private static bool Contains(ReadOnlySpan<char> haystack, ReadOnlySpan<char> needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>级别的显示文字;认不出来时为空。</summary>
    public static string Label(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => ""
    };

    /// <summary>级别对应的语气(界面按它取颜色)。</summary>
    public static RowTone Tone(LogLevel level) => level switch
    {
        LogLevel.Error => RowTone.Danger,
        LogLevel.Warn => RowTone.Warn,
        LogLevel.Debug => RowTone.Idle,
        LogLevel.Info => RowTone.Ok,
        _ => RowTone.Idle
    };
}
