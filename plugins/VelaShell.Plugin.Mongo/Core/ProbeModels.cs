namespace VelaShell.Plugin.Mongo.Core;

/// <summary>语气色:成员角色徽章、环境标记与测试步骤用它着色(视图映射到宿主令牌)。</summary>
internal enum Tone
{
    /// <summary>中性(三级字、默认描边)。</summary>
    Neutral,

    /// <summary>强调色。</summary>
    Accent,

    /// <summary>信息(蓝)。</summary>
    Info,

    /// <summary>成功(绿)。</summary>
    Success,

    /// <summary>警告(橙)。</summary>
    Warning,

    /// <summary>危险(红)。</summary>
    Danger
}

/// <summary>连接串预览里一段文字的角色(视图按角色着色)。</summary>
internal enum PreviewRole
{
    /// <summary>普通文字。</summary>
    Plain,

    /// <summary><c>mongodb://</c> / <c>mongodb+srv://</c>。</summary>
    Scheme,

    /// <summary>用户名。</summary>
    User,

    /// <summary>口令(永远是 <c>****</c>)。</summary>
    Secret,

    /// <summary>主机。</summary>
    Host,

    /// <summary>默认库。</summary>
    Path,

    /// <summary>参数名。</summary>
    Key,

    /// <summary>参数值。</summary>
    Value
}

/// <summary>预览里的一段。</summary>
/// <param name="Text">文字。</param>
/// <param name="Role">角色。</param>
internal sealed record PreviewSpan(string Text, PreviewRole Role = PreviewRole.Plain);

/// <summary>连接对话框右侧栏的「连接字符串」:按角色分段的脱敏连接串。</summary>
internal sealed record ConnectionPreview
{
    /// <summary>标题(「连接字符串」)。</summary>
    public required string Title { get; init; }

    /// <summary>各段。</summary>
    public required IReadOnlyList<PreviewSpan> Spans { get; init; }

    /// <summary>底下的一行说明(口令不写进连接串)。</summary>
    public string? Note { get; init; }

    /// <summary>拼起来的整串(复制按钮、页脚用)。</summary>
    public string Text => string.Concat(Spans.Select(static s => s.Text));
}

/// <summary>测试连接一步的状态。</summary>
internal enum ProbeState
{
    /// <summary>还没轮到。</summary>
    Pending,

    /// <summary>进行中。</summary>
    Running,

    /// <summary>通过。</summary>
    Passed,

    /// <summary>通过但有保留(只读账号、部分成员不可达)。</summary>
    Warning,

    /// <summary>失败。</summary>
    Failed,

    /// <summary>前面断了,没执行。</summary>
    Skipped
}

/// <summary>测试连接的一步。<paramref name="Key" /> 用来原地更新同一行。</summary>
/// <param name="Key">步骤键(ssh / tcp / auth / hello / privileges)。</param>
/// <param name="Title">标题。</param>
/// <param name="State">状态。</param>
/// <param name="Detail">细节(一行)。</param>
/// <param name="ElapsedMs">耗时。</param>
internal sealed record ProbeStep(string Key, string Title, ProbeState State, string? Detail = null, int? ElapsedMs = null);

/// <summary>测试时发现的一个成员。</summary>
/// <param name="Address">host:port。</param>
/// <param name="Role">角色(PRIMARY / SECONDARY / ARBITER …)。</param>
/// <param name="Tone">徽章的语气色。</param>
/// <param name="Detail">补充(复制延迟)。</param>
internal sealed record ProbeEndpoint(string Address, string? Role = null, Tone Tone = Tone.Neutral, string? Detail = null);

/// <summary>一次测试连接的结论。</summary>
internal sealed record ProbeReport
{
    /// <summary>连得上吗。</summary>
    public required bool Succeeded { get; init; }

    /// <summary>一句话结论(「连接成功 · 38 ms」/「认证失败」)。</summary>
    public string? Summary { get; init; }

    /// <summary>各步。</summary>
    public IReadOnlyList<ProbeStep> Steps { get; init; } = [];

    /// <summary>成员表的标题(「发现的成员」)。</summary>
    public string? EndpointsTitle { get; init; }

    /// <summary>发现的成员。</summary>
    public IReadOnlyList<ProbeEndpoint> Endpoints { get; init; } = [];

    /// <summary>第一个失败的步骤。</summary>
    public ProbeStep? FirstFailure => Steps.FirstOrDefault(static s => s.State == ProbeState.Failed);
}
