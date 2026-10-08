using VelaShell.Plugin.DockerPanel.Docker;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>进程表里的一行。</summary>
/// <param name="Pid">进程号。</param>
/// <param name="User">属主。</param>
/// <param name="Cpu">CPU 时间或占比,取决于 daemon 给了哪一列。</param>
/// <param name="Command">命令行。</param>
public readonly record struct ProcessRow(string Pid, string User, string Cpu, string Command);

/// <summary>
/// <c>docker top</c> 的列归一化。
/// <para>
/// 之所以要归一化:<c>/top</c> 的列完全由远端那台机器的 <c>ps</c> 决定 ——
/// GNU 的 <c>ps -ef</c> 给 <c>UID PID PPID C STIME TTY TIME CMD</c>,
/// busybox 给 <c>PID USER TIME COMMAND</c>,<c>ps aux</c> 又是另一套。
/// 界面要的是一张列宽对齐的四列表,所以在这里按标题名认列,
/// 而不是按下标 —— 按下标取会在 Alpine 容器上把 TIME 显示成用户名。
/// </para>
/// </summary>
public static class ProcessTable
{
    /// <summary>把一份 <c>top</c> 结果拍平成四列。</summary>
    public static IReadOnlyList<ProcessRow> Normalize(ContainerTopResult? result)
    {
        string[] titles = result?.Titles ?? [];
        string[][] rows = result?.Processes ?? [];
        if (rows.Length == 0)
        {
            return [];
        }
        int pid = IndexOf(titles, "PID");
        int user = IndexOf(titles, "USER", "UID", "RUSER");
        int cpu = IndexOf(titles, "%CPU", "TIME", "PCPU");
        int command = IndexOf(titles, "COMMAND", "CMD", "ARGS");

        List<ProcessRow> normalized = [with(rows.Length)];
        foreach (string[] row in rows)
        {
            // 命令列认不出来时退回"最后一列":ps 的输出里命令永远排在末尾,
            // 因为只有它能带空格。
            string commandText = command >= 0 ? Cell(row, command)
                : row.Length > 0 ? row[^1]
                : "";
            normalized.Add(new(
                pid >= 0 ? Cell(row, pid) : "—",
                user >= 0 ? Cell(row, user) : "—",
                cpu >= 0 ? Cell(row, cpu) : "—",
                commandText));
        }
        return normalized;
    }

    /// <summary>这一列在 <c>top</c> 结果里叫什么(空表示这台机器的 ps 没给这一列)。</summary>
    public static string CpuColumnTitle(ContainerTopResult? result)
    {
        string[] titles = result?.Titles ?? [];
        int index = IndexOf(titles, "%CPU", "TIME", "PCPU");
        return index >= 0 ? titles[index] : "CPU";
    }

    private static string Cell(string[] row, int index) =>
        index < row.Length ? row[index] : "";

    private static int IndexOf(string[] titles, params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            for (int i = 0; i < titles.Length; i++)
            {
                if (string.Equals(titles[i], candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        return -1;
    }
}
