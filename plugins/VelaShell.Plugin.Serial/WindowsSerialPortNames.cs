using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// Windows 上的串口**友好名**:<c>USB-SERIAL CH340 (COM3)</c> 里的前半截。
/// <para>
/// 为什么值得为它写一段 P/Invoke:<c>SerialPort.GetPortNames()</c> 只给
/// <c>COM3 / COM7 / COM12</c>,而一台开发机上同时插着三四个适配器是常态 ——
/// 让用户去回忆"这次 CH340 被分到几号"是把系统本来就知道的事推给用户。
/// 市面上每一个串口工具都显示友好名,这是**及格线**而不是加分项。
/// </para>
/// <para>
/// 取法有三条,这里选了第三条:
/// </para>
/// <list type="number">
///   <item>WMI <c>Win32_PnPEntity</c> —— 调研文档给的方案。能用,但要把 <c>System.Management</c>
///     连同它那一串 COM 互操作塞进插件目录,首次查询还有几百毫秒的 WMI 启动开销。</item>
///   <item>扫注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum</c> —— <c>Microsoft.Win32.Registry</c>
///     在 <c>net11.0</c>(非 <c>-windows</c> TFM)上不可用,又得多一个包;而且要靠"扫哪几个枚举器、
///     扫几层"的启发式,漏掉一类设备时是静默的。</item>
///   <item><b>SetupAPI</b> —— 设备管理器自己用的那套 API,一次调用取完,零依赖、零启发式、几毫秒。
///     代价是几十行互操作声明。</item>
/// </list>
/// <para>
/// 任何一步失败都**只是没有友好名**:端口列表本身来自
/// <c>SerialPort.GetPortNames()</c>,不受这里影响(见 <see cref="SerialPortEnumerator" />)。
/// </para>
/// </summary>
internal static partial class WindowsSerialPortNames
{
    /// <summary>
    /// "端口(COM 和 LPT)"设备类。用**类**而不是 COM 端口设备接口:
    /// com0com 这类虚拟串口对不一定注册设备接口,却一定在这个类下。
    /// <para>不加 <c>readonly</c> 是被互操作签名逼的:<c>SetupDiGetClassDevsW</c> 收
    /// <c>ref Guid</c>,而只读静态字段借不出可写引用。它事实上不会被改。</para>
    /// </summary>
    private static Guid _devClassPorts = new("4D36E978-E325-11CE-BFC1-08002BE10318");

    /// <summary>只要当前在场的设备(拔掉的适配器不列)。</summary>
    private const uint DigcfPresent = 0x02;

    /// <summary>SPDRP_FRIENDLYNAME。</summary>
    private const uint SpdrpFriendlyName = 0x0C;

    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>
    /// 列出当前在场的 COM 端口及其友好名。
    /// </summary>
    /// <returns>端口名 → 友好名(不含末尾的 <c>(COMn)</c>);失败时为空表。</returns>
    /// <remarks>
    /// 平台标注只打在这个方法上,不打在类上:同一个类里的
    /// <see cref="TrySplitFriendlyName" /> 是纯字符串处理,任何平台都跑得了 ——
    /// 标在类上会让它的单测在三平台的 CI 上一律报 CA1416。
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<string, string> Describe()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IntPtr set = SetupDiGetClassDevs(ref _devClassPorts, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
        if (set == InvalidHandle || set == IntPtr.Zero)
        {
            return result;
        }
        try
        {
            var info = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            byte[] buffer = new byte[1024];
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref info); index++)
            {
                if (!SetupDiGetDeviceRegistryProperty(set, ref info, SpdrpFriendlyName,
                        out _, buffer, (uint)buffer.Length, out uint size))
                {
                    continue;
                }
                // 返回的是以 NUL 结尾的 UTF-16;size 含那个结尾。
                int chars = (int)Math.Min(size, (uint)buffer.Length) / 2;
                string friendly = System.Text.Encoding.Unicode.GetString(buffer, 0, chars * 2).TrimEnd('\0');
                if (TrySplitFriendlyName(friendly, out string port, out string description))
                {
                    result[port] = description;
                }
                info = new() { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    /// <summary>
    /// 把 <c>"USB-SERIAL CH340 (COM3)"</c> 拆成 <c>COM3</c> 与 <c>USB-SERIAL CH340</c>。
    /// <para>
    /// 这个类下还有并口(<c>"打印机端口 (LPT1)"</c>)与一些没有端口号的条目,
    /// 拆不出 <c>(COMn)</c> 的一律丢弃 —— 这正是过滤器本身。
    /// </para>
    /// <para>纯函数,单测钉在这儿:友好名的格式是系统给的,拆错了表现为"下拉里没有描述",不报错。</para>
    /// </summary>
    /// <param name="friendlyName">设备友好名。</param>
    /// <param name="portName">拆出的端口名。</param>
    /// <param name="description">拆出的描述(去掉端口那一段后的部分)。</param>
    /// <returns>是否拆出了一个 COM 端口。</returns>
    internal static bool TrySplitFriendlyName(string friendlyName, out string portName, out string description)
    {
        portName = string.Empty;
        description = string.Empty;
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }
        int close = friendlyName.LastIndexOf(')');
        if (close != friendlyName.Length - 1)
        {
            return false;
        }
        int open = friendlyName.LastIndexOf('(', close);
        if (open < 0)
        {
            return false;
        }
        string inner = friendlyName[(open + 1)..close].Trim();
        if (!inner.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || inner.Length <= 3
            || !inner[3..].All(char.IsAsciiDigit))
        {
            return false;
        }
        portName = inner.ToUpperInvariant();
        description = friendlyName[..open].Trim();
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        [Out] byte[] propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
