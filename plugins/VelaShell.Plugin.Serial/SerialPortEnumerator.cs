using System.IO.Ports;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// 一个可选的串口设备。
/// </summary>
/// <param name="PortName">打开时用的名字(<c>COM3</c> / <c>/dev/ttyUSB0</c> / <c>/dev/cu.usbserial-A50285BI</c>)。</param>
/// <param name="Description">人能认出来的描述(<c>USB-SERIAL CH340</c>);取不到时为空。</param>
internal readonly record struct SerialPortInfo(string PortName, string Description)
{
    /// <summary>下拉里显示的文案:有描述就 <c>描述 (端口)</c>,没有就只有端口名。</summary>
    public string Label => Description.Length > 0 ? $"{Description} ({PortName})" : PortName;
}

/// <summary>
/// 三平台的串口枚举。三套做法差别很大,而且每一处"想当然"都有具体代价 ——
/// 每条都写在对应方法的注释里。
/// <para>
/// 全局纪律:枚举**永不抛**。它跑在"用户打开连接对话框"这条画界面的路径上,
/// 一次列不出设备不该变成一个连表单都打不开的错误 —— 手输端口名本来就是允许的。
/// </para>
/// </summary>
internal static class SerialPortEnumerator
{
    /// <summary>列出本机当前可用的串口。</summary>
    /// <returns>按自然序排好的设备列表;失败时为空表。</returns>
    public static IReadOnlyList<SerialPortInfo> List()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ListWindows();
            }
            if (OperatingSystem.IsMacOS())
            {
                return ListMacOs();
            }
            return OperatingSystem.IsLinux() ? ListLinux() : ListFallback();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Windows:端口清单取 <see cref="SerialPort.GetPortNames" />(读注册表的
    /// <c>SERIALCOMM</c>,是"有哪些口"的权威),友好名取 SetupAPI。
    /// <para>
    /// 两者分开取是刻意的:SetupAPI 那一路失败(权限、精简系统、被安全软件挡)时,
    /// 用户仍然拿得到一份能用的端口列表,只是没有描述。
    /// </para>
    /// <para>
    /// <b>排序必须自己来</b>:微软的文档明写 <c>GetPortNames</c> 的返回顺序未定义。
    /// 就算它凑巧有序,字符串序下 <c>COM10</c> 也排在 <c>COM2</c> 前面 ——
    /// 一台插了十个适配器的机器上,下拉是乱的。
    /// </para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<SerialPortInfo> ListWindows()
    {
        IReadOnlyDictionary<string, string> descriptions;
        try
        {
            descriptions = WindowsSerialPortNames.Describe();
        }
        catch
        {
            descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        return Sort(SerialPort.GetPortNames()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SerialPortInfo(
                name,
                descriptions.TryGetValue(name, out string? description) ? description : string.Empty)));
    }

    /// <summary>
    /// macOS:**只列 <c>/dev/cu.*</c>**。
    /// <para>
    /// 这不是风格问题:<c>/dev/tty.*</c> 是 dial-in 设备,打开时会**阻塞等待 DCD 载波**。
    /// 对着一个没接线的 USB 适配器打开它,进程就永久挂在那里 —— 而且用户看到的是
    /// "点了连接没反应",完全没法自己诊断。call-out 的 <c>/dev/cu.*</c> 立即打开。
    /// 终端类应用一律用 cu,pySerial 与 minicom 亦然。
    /// </para>
    /// </summary>
    private static IReadOnlyList<SerialPortInfo> ListMacOs()
    {
        if (!Directory.Exists("/dev"))
        {
            return [];
        }
        return Sort(Directory.EnumerateFiles("/dev", "cu.*")
            .Select(path => new SerialPortInfo(path, DescribeUnixName(Path.GetFileName(path)[3..]))));
    }

    /// <summary>
    /// Linux:USB 适配器走 <c>/dev/serial/by-id/</c>(udev 建的稳定符号链接,名字里自带
    /// 厂商/型号/序列号,是白捡的友好名),真 UART 与其余按设备名枚举。
    /// <para>
    /// <b>必须过滤 <c>ttyS*</c></b>:8250 驱动会**无条件**注册 <c>ttyS0..ttyS3</c>(很多发行版上是 0..31),
    /// 其中绝大多数背后根本没有硬件。不过滤的话下拉里是三十多个假端口,真的那个反而找不着。
    /// 判据用 <c>/sys/class/tty/&lt;名&gt;/device</c> 是否存在 —— 幽灵口没有这个符号链接。
    /// </para>
    /// </summary>
    private static IReadOnlyList<SerialPortInfo> ListLinux()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        // 1) by-id:名字自带描述,优先。
        const string byId = "/dev/serial/by-id";
        if (Directory.Exists(byId))
        {
            foreach (string link in Directory.EnumerateFileSystemEntries(byId))
            {
                string? target = ResolveLink(link);
                if (target is null)
                {
                    continue;
                }
                found[target] = DescribeUnixName(Path.GetFileName(link));
            }
        }
        // 2) 设备名枚举,补上 by-id 覆盖不到的(板载 UART、蓝牙串口、容器里手工映射进来的)。
        foreach (string pattern in (string[])["ttyUSB*", "ttyACM*", "ttyAMA*", "ttyS*", "rfcomm*"])
        {
            if (!Directory.Exists("/dev"))
            {
                break;
            }
            foreach (string path in Directory.EnumerateFileSystemEntries("/dev", pattern))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("ttyS", StringComparison.Ordinal) && !IsRealUart(name))
                {
                    continue;
                }
                if (!found.ContainsKey(path))
                {
                    found[path] = DescribeLinuxDriver(name);
                }
            }
        }
        return Sort(found.Select(pair => new SerialPortInfo(pair.Key, pair.Value)));
    }

    /// <summary>其余平台(FreeBSD 之类):交给 <see cref="SerialPort.GetPortNames" />,取不到就空表。</summary>
    private static IReadOnlyList<SerialPortInfo> ListFallback() =>
        Sort(SerialPort.GetPortNames().Select(name => new SerialPortInfo(name, string.Empty)));

    /// <summary>
    /// <c>/sys/class/tty/&lt;名&gt;/device</c> 存在即认为背后有真硬件。
    /// 8250 注册的幽灵 <c>ttyS*</c> 没有这个符号链接。
    /// </summary>
    private static bool IsRealUart(string name)
    {
        try
        {
            string sys = $"/sys/class/tty/{name}/device";
            return Directory.Exists(sys) || File.Exists(sys);
        }
        catch
        {
            // 读不到 sysfs(容器里没挂)时**放行**:宁可多列一个,也别把用户真有的口藏起来。
            return true;
        }
    }

    /// <summary>从 <c>/sys/class/tty/&lt;名&gt;/device/driver</c> 取驱动名当描述(<c>ch341-uart</c> / <c>ftdi_sio</c> / <c>cdc_acm</c>)。</summary>
    private static string DescribeLinuxDriver(string name)
    {
        try
        {
            string driver = $"/sys/class/tty/{name}/device/driver";
            string? target = ResolveLink(driver);
            return target is null ? string.Empty : Path.GetFileName(target);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>解析符号链接;不是链接或解析失败时返回 null。</summary>
    private static string? ResolveLink(string path)
    {
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(path, returnFinalTarget: true)
                                    ?? Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 Unix 设备名整理成人话:
    /// <c>usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0</c> → <c>FTDI FT232R USB UART A50285BI</c>。
    /// <para>纯字符串处理,单测钉在这儿 —— 这是 Linux/macOS 上友好名的唯一来源。</para>
    /// </summary>
    /// <param name="name">by-id 链接名,或 <c>cu.</c> 之后的部分。</param>
    /// <returns>描述;整理不出东西时为空串。</returns>
    internal static string DescribeUnixName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }
        string text = name.Trim();
        if (text.StartsWith("usb-", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }
        // udev 给同一个物理设备的每个接口都建一条链接,尾巴形如 -if00 / -if00-port0。
        // 留着只会让下拉里每一条都拖着一串对用户毫无意义的后缀。
        int cut = text.IndexOf("-if", StringComparison.OrdinalIgnoreCase);
        if (cut > 0)
        {
            text = text[..cut];
        }
        text = text.Replace('_', ' ').Trim();
        return text;
    }

    /// <summary>
    /// 自然序:数字段按数值比,其余按序数比。<c>COM2</c> 排在 <c>COM10</c> 前面,
    /// <c>ttyUSB2</c> 排在 <c>ttyUSB10</c> 前面。
    /// </summary>
    /// <param name="ports">待排序的设备。</param>
    /// <returns>排好序的列表。</returns>
    internal static IReadOnlyList<SerialPortInfo> Sort(IEnumerable<SerialPortInfo> ports) =>
        [.. ports.OrderBy(port => port.PortName, NaturalOrder.Instance)];

    /// <summary>把"字母段 + 数字段"交替比较的比较器。</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) { i++; }
                    while (j < y.Length && char.IsAsciiDigit(y[j])) { j++; }
                    // 按数值比;长到 long 都装不下的数字段(不会有)退回按长度比。
                    ReadOnlySpan<char> a = x.AsSpan(si, i - si).TrimStart('0');
                    ReadOnlySpan<char> b = y.AsSpan(sj, j - sj).TrimStart('0');
                    if (a.Length != b.Length)
                    {
                        return a.Length - b.Length;
                    }
                    int digits = a.SequenceCompareTo(b);
                    if (digits != 0)
                    {
                        return digits;
                    }
                    continue;
                }
                int one = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (one != 0)
                {
                    return one;
                }
                i++;
                j++;
            }
            return (x.Length - i) - (y.Length - j);
        }
    }
}
