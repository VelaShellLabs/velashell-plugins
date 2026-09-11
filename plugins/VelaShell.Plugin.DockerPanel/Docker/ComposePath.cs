namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>
/// compose 项目路径的一点点常识。
/// <para>
/// 面板同时对着两种文件系统:远端是 POSIX(<c>/srv/stacks/web/compose.yaml</c>),
/// 本机可能是 Windows(盘符打头、反斜杠分隔)。原来这些地方一律按 <c>/</c> 处理,
/// 于是本机端点上目录永远算成空串,"要用绝对路径"还会拦下一个本来就绝对的路径。
/// </para>
/// <para>
/// 这里<b>不</b>用 <see cref="System.IO.Path" />:那套 API 按**跑面板的这台机器**判断分隔符,
/// 而路径说的是**Docker 所在的那台机器** —— 一个 Windows 客户端连远端 Linux 时,两者不是一回事。
/// 分隔符只能从路径本身看出来。
/// </para>
/// </summary>
internal static class ComposePath
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>这条路径用的是哪种分隔符(看不出来时按 POSIX)。</summary>
    internal static char SeparatorOf(string path) =>
        path.Contains('\\') && !path.Contains('/') ? '\\' : '/';

    /// <summary>绝对路径?POSIX 的 <c>/…</c>、Windows 的盘符式与 UNC 式都算。</summary>
    internal static bool IsAbsolute(string path) =>
        path.StartsWith('/')
        || path.StartsWith(@"\\")
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && Separators.Contains(path[2]));

    /// <summary>去掉最后一段(取所在目录);没有分隔符就原样返回。</summary>
    internal static string DirectoryOf(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var slash = trimmed.LastIndexOfAny(Separators);
        return slash > 0 ? trimmed[..slash] : trimmed;
    }

    /// <summary>取最后一段(目录名 / 文件名)。</summary>
    internal static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var slash = trimmed.LastIndexOfAny(Separators);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    /// <summary>在目录后面接一段,沿用这条路径自己的分隔符。</summary>
    internal static string Combine(string directory, string segment) =>
        $"{directory.TrimEnd(Separators)}{SeparatorOf(directory)}{segment}";
}
