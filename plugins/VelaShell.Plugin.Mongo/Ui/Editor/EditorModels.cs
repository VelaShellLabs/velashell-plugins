namespace VelaShell.Plugin.Mongo.Ui;

/// <summary>这块文本按哪种语法着色。</summary>
public enum CodeLanguage
{
    /// <summary>不着色。</summary>
    None,

    /// <summary>mongosh(查询编辑器、筛选栏、管道阶段、文档编辑器 JSON 页的 Shell 写法)。</summary>
    Shell,

    /// <summary>扩展 JSON(Relaxed / Canonical)。</summary>
    Json
}

/// <summary>诊断的严重程度。</summary>
public enum DiagnosticSeverity
{
    /// <summary>错误(红色波浪线 + 行号旁红点 + 行尾红底提示)。</summary>
    Error,

    /// <summary>警告(橙色波浪线)。</summary>
    Warning,

    /// <summary>提示(信息色虚线)。</summary>
    Info
}

/// <summary>
/// 一条诊断:<paramref name="Offset" />/<paramref name="Length" /> 是文档里的字符区间。
/// <paramref name="FixLabel" /> 非空时行尾提示后面带一个快捷修复的说明(<c>Alt+↵ 修复</c>)。
/// </summary>
/// <param name="Offset">起点。</param>
/// <param name="Length">长度。</param>
/// <param name="Message">提示文字。</param>
/// <param name="Severity">严重程度。</param>
/// <param name="FixText">一键修复:用它替换这个区间;没有为 <see langword="null" />。</param>
/// <param name="FixLabel">修复的快捷键说明。</param>
public sealed record EditorDiagnostic(
    int Offset,
    int Length,
    string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error,
    string? FixText = null,
    string? FixLabel = null);

/// <summary>整行标记的种类(JSON 视图里"已修改 / 新增字段"那种整行底色)。</summary>
public enum LineMarkKind
{
    /// <summary>已修改(橙色淡底 + 左侧 2px 橙条)。</summary>
    Modified,

    /// <summary>新增(绿色淡底 + 左侧 2px 绿条)。</summary>
    Added,

    /// <summary>删除(红色淡底)。</summary>
    Removed,

    /// <summary>当前 / 高亮(强调色淡底)。</summary>
    Highlight
}

/// <summary>
/// 一行的标记:底色 + 行尾小字(<c>已修改 · 原值 "paid"</c>、<c>新增字段</c>)。
/// </summary>
/// <param name="Line">行号(1 起)。</param>
/// <param name="Kind">种类。</param>
/// <param name="Annotation">行尾小字;没有为 <see langword="null" />。</param>
public sealed record LineMark(int Line, LineMarkKind Kind, string? Annotation = null);

/// <summary>
/// 补全列表里的一项(设计稿 03 / 02 / 09 的补全项:图标 + 文字 + 右侧类别;选中时右侧详情栏)。
/// </summary>
public sealed class CompletionItem
{
    /// <summary>列表里显示的文字(<c>$sort</c>)。</summary>
    public required string Label { get; init; }

    /// <summary>接受时插入的文本;缺省等于 <see cref="Label" />。可以含一个 <c>|</c> 表示插入后光标落点。</summary>
    public string? InsertText { get; init; }

    /// <summary>图标(<c>Mongo.layers</c>)。</summary>
    public string IconKey { get; init; } = "Mongo.variable";

    /// <summary>图标颜色令牌。</summary>
    public string IconToken { get; init; } = "VelaTextTertiary";

    /// <summary>右侧小字(<c>聚合阶段</c>、<c>字段 · 抽样</c>、<c>分布 62%</c>)。</summary>
    public string? Category { get; init; }

    /// <summary>详情栏标题旁的徽章(<c>聚合阶段</c>)。</summary>
    public string? Badge { get; init; }

    /// <summary>详情栏说明。</summary>
    public string? Description { get; init; }

    /// <summary>详情栏里的语法示例(等宽代码块)。</summary>
    public string? Syntax { get; init; }

    /// <summary>详情栏里一排小标签的标题(<c>上游 $group 输出的字段</c>)。</summary>
    public string? ChipsTitle { get; init; }

    /// <summary>详情栏里一排小标签(<c>_id</c>、<c>name</c>、<c>orders</c>)。</summary>
    public IReadOnlyList<string> Chips { get; init; } = [];

    /// <summary>文档链接。</summary>
    public string? DocsUrl { get; init; }

    /// <summary>值分布(0–1):值建议列表里画一根比例条(设计稿 02)。</summary>
    public double? Ratio { get; init; }

    /// <summary>是否有详情(没有就不展开右栏)。</summary>
    public bool HasDetail => Description is not null || Syntax is not null || Chips.Count > 0;

    /// <summary>有没有比例条。</summary>
    public bool HasRatio => Ratio is not null;

    /// <summary>比例条文字(<c>62%</c>)。</summary>
    public string RatioText => Ratio is { } r ? $"{r * 100:0}%" : "";

    /// <summary>比例条宽度(满 80 像素)。</summary>
    public double RatioWidth => (Ratio ?? 0) * 80;
}

/// <summary>一次补全的结果。</summary>
public sealed class CompletionSet
{
    /// <summary>候选项。</summary>
    public required IReadOnlyList<CompletionItem> Items { get; init; }

    /// <summary>接受时替换的区间起点(通常是光标前那个半截词的起点)。</summary>
    public required int ReplaceOffset { get; init; }

    /// <summary>替换区间的长度。</summary>
    public required int ReplaceLength { get; init; }

    /// <summary>列表顶上的一行标题(<c>值建议 status · 抽样 1,000 份文档</c>);没有为空。</summary>
    public string? Header { get; init; }

    /// <summary>列表底部的操作提示(<c>↵ 插入 · Tab 插入片段 · Ctrl+Space 切换详情</c>)。</summary>
    public string? Footer { get; init; }
}

/// <summary>补全请求。</summary>
/// <param name="Text">全文。</param>
/// <param name="CaretOffset">光标位置。</param>
/// <param name="Explicit">用户按了 Ctrl+Space(而不是打字自动弹出)。</param>
public sealed record CompletionRequest(string Text, int CaretOffset, bool Explicit);
