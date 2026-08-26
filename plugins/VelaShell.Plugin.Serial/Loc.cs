namespace VelaShell.Plugin.Serial;

/// <summary>
/// 插件自带的文案表(理由同 S3 / Telnet 插件:领域词汇随插件走,宿主不替一个自己不认识的协议背词典)。
/// 只带英文与简体中文两套,其余语言回落英文。
/// </summary>
/// <param name="locale">宿主当前语言(如 <c>zh-Hans</c>、<c>en</c>)。</param>
internal sealed class Loc(string locale)
{
    private readonly bool _chinese = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>取一条文案;未收录的键原样返回(方便一眼看出漏了哪条)。</summary>
    /// <param name="key">文案键。</param>
    /// <returns>文案。</returns>
    public string this[string key] =>
        (_chinese ? Chinese : English).TryGetValue(key, out string? value) ? value : key;

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Serial_DisplayName"] = "Serial",
        ["Serial_Port"] = "Serial device",
        ["Serial_PortPlaceholder"] = "COM3  ·  /dev/ttyUSB0  ·  /dev/cu.usbserial-A50285BI",

        ["Serial_BaudRate"] = "Speed (baud)",
        ["Serial_BaudRateHint"] = "Pick one or type your own — non-standard rates such as 250000 (Marlin) or 76800 are accepted as-is.",

        ["Serial_DataBits"] = "Data bits",
        ["Serial_StopBits"] = "Stop bits",

        ["Serial_Parity"] = "Parity",
        ["Serial_ParityNone"] = "None",
        ["Serial_ParityEven"] = "Even",
        ["Serial_ParityOdd"] = "Odd",
        ["Serial_ParityMark"] = "Mark",
        ["Serial_ParitySpace"] = "Space",

        ["Serial_FlowControl"] = "Flow control",
        ["Serial_FlowNone"] = "None",
        ["Serial_FlowRtsCts"] = "RTS/CTS (hardware)",
        ["Serial_FlowXonXoff"] = "XON/XOFF (software)",
        ["Serial_FlowBoth"] = "RTS/CTS + XON/XOFF",
        ["Serial_FlowControlHint"] = "XON/XOFF consumes 0x11 and 0x13 on the wire — it breaks ZMODEM transfers and Ctrl+S / Ctrl+Q. Use RTS/CTS if the cable has the wires for it.",

        ["Serial_EnterMode"] = "Enter key sends",
        ["Serial_EnterCr"] = "CR (recommended)",
        ["Serial_EnterLf"] = "LF",
        ["Serial_EnterCrLf"] = "CR LF",
        ["Serial_EnterModeHint"] = "CR suits nearly every device console (U-Boot, getty, Cisco IOS). Anything other than CR rewrites the whole outgoing stream — pastes included — so switch back to CR before transferring files.",

        ["Serial_Dtr"] = "Assert DTR on open",
        ["Serial_DtrHint"] = "Some USB CDC devices stay silent until DTR is asserted; on an Arduino Uno/Nano it triggers the auto-reset. Turn it off to attach without resetting the board.",
        ["Serial_Rts"] = "Assert RTS on open",
        ["Serial_RtsHint"] = "Hidden while RTS/CTS flow control is on — the driver owns the line there.",

        ["Serial_ImplicitLf"] = "Add LF to a bare CR (incoming)",
        ["Serial_ImplicitLfHint"] = "Turn on when every line overwrites the previous one. Leave off if the device draws progress bars — those use a bare CR on purpose.",
        ["Serial_ImplicitCr"] = "Add CR to a bare LF (incoming)",
        ["Serial_ImplicitCrHint"] = "Turn on when the output walks down the screen in a staircase.",

        ["Serial_LocalEcho"] = "Local echo",
        ["Serial_EchoOff"] = "Off (device echoes)",
        ["Serial_EchoOn"] = "On",
        ["Serial_LocalEchoHint"] = "Off suits device consoles, which echo what you type. Turn it on for gear that never echoes, otherwise typing is invisible.",

        ["Serial_TxDelayChar"] = "Send delay per character (ms)",
        ["Serial_TxDelayCharHint"] = "Paces the outgoing stream for devices with no flow control, which drop characters when a config is pasted in. 1–5 ms is the usual fix; 0 disables pacing.",
        ["Serial_TxDelayLine"] = "Extra send delay per line (ms)",
        ["Serial_TxDelayLineHint"] = "Added after every CR or LF, on top of the per-character delay.",

        ["Serial_Category"] = "Serial",
        ["Serial_CmdBreak"] = "Serial: send BREAK",
        ["Serial_CmdReset"] = "Serial: pulse DTR (reset the board)",
        ["Serial_CmdToggleDtr"] = "Serial: toggle DTR",
        ["Serial_CmdToggleRts"] = "Serial: toggle RTS",

        ["Serial_ErrNoPort"] = "Pick a serial device first (for example COM3 or /dev/ttyUSB0).",
        ["Serial_ErrBusy"] = "{0} is already open in another program, or this user is not allowed to use it. On Linux that usually means the account is not in the 'dialout' group: sudo usermod -aG dialout $USER, then sign out and back in.",
        ["Serial_ErrMissing"] = "{0} does not exist. The adapter may have been unplugged, or it may be named differently now.",
        ["Serial_ErrOpen"] = "Could not open {0}: {1}"
    };

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["Serial_DisplayName"] = "串口",
        ["Serial_Port"] = "串口设备",
        ["Serial_PortPlaceholder"] = "COM3  ·  /dev/ttyUSB0  ·  /dev/cu.usbserial-A50285BI",

        ["Serial_BaudRate"] = "波特率",
        ["Serial_BaudRateHint"] = "可从表里选,也可以直接填 —— 250000(Marlin 固件)、76800 这类非标值原样接受。",

        ["Serial_DataBits"] = "数据位",
        ["Serial_StopBits"] = "停止位",

        ["Serial_Parity"] = "校验位",
        ["Serial_ParityNone"] = "无",
        ["Serial_ParityEven"] = "偶校验",
        ["Serial_ParityOdd"] = "奇校验",
        ["Serial_ParityMark"] = "标记(Mark)",
        ["Serial_ParitySpace"] = "空格(Space)",

        ["Serial_FlowControl"] = "流控制",
        ["Serial_FlowNone"] = "无",
        ["Serial_FlowRtsCts"] = "RTS/CTS(硬件)",
        ["Serial_FlowXonXoff"] = "XON/XOFF(软件)",
        ["Serial_FlowBoth"] = "RTS/CTS + XON/XOFF",
        ["Serial_FlowControlHint"] = "XON/XOFF 会吃掉线上的 0x11 与 0x13 —— ZMODEM 传输和 Ctrl+S / Ctrl+Q 都会因此失效。线材接了流控信号线的话优先用 RTS/CTS。",

        ["Serial_EnterMode"] = "回车键发送",
        ["Serial_EnterCr"] = "CR(推荐)",
        ["Serial_EnterLf"] = "LF",
        ["Serial_EnterCrLf"] = "CR LF",
        ["Serial_EnterModeHint"] = "CR 适用于几乎所有设备控制台(U-Boot、getty、Cisco IOS)。选 CR 以外的值会改写**整条出方向流**(粘贴的内容也算),传文件前请调回 CR。",

        ["Serial_Dtr"] = "打开时置 DTR",
        ["Serial_DtrHint"] = "部分 USB CDC 设备不置 DTR 就不出数据;Arduino Uno/Nano 上它会触发自动复位。想接上去而不复位板子就关掉它。",
        ["Serial_Rts"] = "打开时置 RTS",
        ["Serial_RtsHint"] = "流控制选了 RTS/CTS 时本项隐藏 —— 那时这根线归驱动管。",

        ["Serial_ImplicitLf"] = "收到裸 CR 时补 LF",
        ["Serial_ImplicitLfHint"] = "输出每行都盖在上一行上时打开它。设备会画进度条的话建议关着 —— 那种回退是故意发裸 CR 的。",
        ["Serial_ImplicitCr"] = "收到裸 LF 时补 CR",
        ["Serial_ImplicitCrHint"] = "输出呈阶梯状一路往右下走时打开它。",

        ["Serial_LocalEcho"] = "本地回显",
        ["Serial_EchoOff"] = "关(设备自己回显)",
        ["Serial_EchoOn"] = "开",
        ["Serial_LocalEchoHint"] = "设备控制台会回显你敲的字符,关着即可。对着从不回显的设备(裸 UART 模块)才需要打开,否则打字看不见。",

        ["Serial_TxDelayChar"] = "发送延时:每字符(毫秒)",
        ["Serial_TxDelayCharHint"] = "给没有流控的设备用:往那种设备里粘一段配置会丢字符。常用 1–5 毫秒;填 0 表示不节流。",
        ["Serial_TxDelayLine"] = "发送延时:每行额外(毫秒)",
        ["Serial_TxDelayLineHint"] = "每遇到 CR 或 LF 时在上面那个延时之外再等这么久。",

        ["Serial_Category"] = "串口",
        ["Serial_CmdBreak"] = "串口:发送 BREAK 信号",
        ["Serial_CmdReset"] = "串口:脉冲 DTR(复位开发板)",
        ["Serial_CmdToggleDtr"] = "串口:切换 DTR",
        ["Serial_CmdToggleRts"] = "串口:切换 RTS",

        ["Serial_ErrNoPort"] = "请先选一个串口设备(例如 COM3 或 /dev/ttyUSB0)。",
        ["Serial_ErrBusy"] = "{0} 已被其他程序占用,或当前用户无权访问。Linux 上通常是账户不在 dialout 组:执行 sudo usermod -aG dialout $USER 后重新登录。",
        ["Serial_ErrMissing"] = "{0} 不存在。适配器可能已被拔下,或者这次被分到了别的名字。",
        ["Serial_ErrOpen"] = "打不开 {0}:{1}"
    };
}
