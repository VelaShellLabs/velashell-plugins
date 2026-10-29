using System.Globalization;

namespace VelaShell.Plugin.Rdp;

/// <summary>Plugin-owned English and Chinese text; other locales fall back to English.</summary>
internal sealed class Loc(string locale)
{
    private readonly bool _chinese = locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public string this[string key] => (_chinese ? Chinese : English).GetValueOrDefault(key, key);
    public string Get(string key) => this[key];
    public string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, this[key], args);
    public static IReadOnlyCollection<string> KeysOf(bool chinese) => chinese ? Chinese.Keys : English.Keys;

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["Rdp_Clipboard"] = "Share text clipboard",
        ["Rdp_ClipboardHint"] = "Allow text exchange with the focused remote desktop. Requires the native extension; images and file clipboard are not supported.",
        ["Rdp_Drives"] = "Windows drives to share",
        ["Rdp_DrivesHint"] = "Explicit full volumes, e.g. D:\\;E:\\. Empty disables sharing. The remote can access the entire selected volume with your local permissions; folders and UNC are unsupported. Requires the Windows native extension.",
        ["Rdp_DrivesReadOnly"] = "Read-only shared drives",
        ["Rdp_Host"] = "Windows host",
        ["Rdp_User"] = "Username (user, DOMAIN\\user or user@domain)",
        ["Rdp_Password"] = "Password",
        ["Rdp_Domain"] = "Domain (optional)",
        ["Rdp_DomainHint"] = "For local accounts use the Windows computer name. Leave empty for a UPN username; '.' interoperability is not verified.",
        ["Rdp_Width"] = "Desktop width (pixels)",
        ["Rdp_Height"] = "Desktop height (pixels)",
        ["Rdp_Scale"] = "Local display scaling",
        ["Rdp_ScaleFit"] = "Fit to window",
        ["Rdp_ScaleActual"] = "Actual size (1:1)",
        ["Rdp_ConnectTimeout"] = "Connect timeout (ms)",
        ["Rdp_JumpSession"] = "Reach it through an SSH session",
        ["Rdp_JumpSessionHint"] = "Choose a saved SSH connection; the host creates and owns the tunnel. The Windows target remains the TLS/NLA identity.",
        ["Rdp_Connected"] = "Connected",
        ["Rdp_Connecting"] = "Connecting…",
        ["Rdp_Disconnected"] = "Disconnected",
        ["Rdp_Closed"] = "Closed",
        ["Rdp_Faulted"] = "Connection failed",
        ["Rdp_Reconnect"] = "Reconnect",
        ["Rdp_Disconnect"] = "Disconnect",
        ["Rdp_SendCtrlAltDel"] = "Send Ctrl+Alt+Del",
        ["Rdp_Resolution"] = "{0} × {1}",
        ["Rdp_Error"] = "RDP connection failed. Check the endpoint, network and server security settings.",
        ["Rdp_AuthFailed"] = "RDP authentication failed. Check the account, domain and password.",
        ["Rdp_Timeout"] = "RDP connection timed out.",
        ["Rdp_Unsupported"] = "The server requires an unsupported RDP feature or security mode.",
        ["Rdp_Certificate"] = "The RDP server certificate requires confirmation.",
        ["Rdp_CertificateReconnect"] = "The server certificate needs confirmation or has changed. Close this tab and reopen the saved connection to verify it.",
        ["Rdp_UnsupportedInput"] = "This key/input method is not supported.",
        ["Rdp_InvalidSetting"] = "Invalid RDP setting: {0}.",
        ["Rdp_InvalidAccount"] = "Specify a username; use DOMAIN\\user or a separate domain, not conflicting forms. UPN usernames require an empty domain.",
        ["Rdp_KeyboardHint"] = "Host and OS shortcuts take precedence. Rebind host shortcuts in Settings if needed; IME/Unicode input is not enabled.",
        ["Rdp_NoFrame"] = "Waiting for the remote desktop…",
        ["Rdp_SendInputError"] = "Input could not be delivered. Disconnect and reconnect the session.",
        ["Rdp_Desktop"] = "Remote desktop"
    };

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        ["Rdp_Clipboard"] = "共享文本剪贴板",
        ["Rdp_ClipboardHint"] = "允许与当前取得焦点的远程桌面交换文本。需要原生扩展，不支持图片或文件剪贴板。",
        ["Rdp_Drives"] = "共享的 Windows 盘符",
        ["Rdp_DrivesHint"] = "明确选择整个卷，例如 D:\\;E:\\。留空关闭。远端可按你的本地权限访问所选整个卷；不支持文件夹或 UNC，需要 Windows 原生扩展。",
        ["Rdp_DrivesReadOnly"] = "共享磁盘只读",
        ["Rdp_Host"] = "Windows 主机",
        ["Rdp_User"] = "用户名（user、DOMAIN\\user 或 user@domain）",
        ["Rdp_Password"] = "密码",
        ["Rdp_Domain"] = "域（可选）",
        ["Rdp_DomainHint"] = "本地账户使用 Windows 计算机名；UPN 用户名请留空，'.' 的互操作尚未验证。",
        ["Rdp_Width"] = "桌面宽度（像素）",
        ["Rdp_Height"] = "桌面高度（像素）",
        ["Rdp_Scale"] = "本地显示缩放",
        ["Rdp_ScaleFit"] = "适应窗口",
        ["Rdp_ScaleActual"] = "原始尺寸（1:1）",
        ["Rdp_ConnectTimeout"] = "连接超时（毫秒）",
        ["Rdp_JumpSession"] = "经 SSH 会话抵达",
        ["Rdp_JumpSessionHint"] = "选择已保存的 SSH 连接，由宿主创建并管理隧道；TLS/NLA 身份仍为真实 Windows 目标。",
        ["Rdp_Connected"] = "已连接",
        ["Rdp_Connecting"] = "正在连接…",
        ["Rdp_Disconnected"] = "已断开",
        ["Rdp_Closed"] = "已关闭",
        ["Rdp_Faulted"] = "连接失败",
        ["Rdp_Reconnect"] = "重连",
        ["Rdp_Disconnect"] = "断开",
        ["Rdp_SendCtrlAltDel"] = "发送 Ctrl+Alt+Del",
        ["Rdp_Resolution"] = "{0} × {1}",
        ["Rdp_Error"] = "RDP 连接失败，请检查端点、网络与服务器安全设置。",
        ["Rdp_AuthFailed"] = "RDP 认证失败，请检查账户、域与密码。",
        ["Rdp_Timeout"] = "RDP 连接超时。",
        ["Rdp_Unsupported"] = "服务器要求不支持的 RDP 功能或安全模式。",
        ["Rdp_Certificate"] = "需要确认 RDP 服务器证书。",
        ["Rdp_CertificateReconnect"] = "服务器证书需要确认或已经变化，请关闭此标签页后从已保存连接重新打开以核实。",
        ["Rdp_UnsupportedInput"] = "暂不支持此按键或输入方式。",
        ["Rdp_InvalidSetting"] = "RDP 设置无效：{0}。",
        ["Rdp_InvalidAccount"] = "请填写用户名；DOMAIN\\user 与独立域字段不得冲突，UPN 用户名须将域留空。",
        ["Rdp_KeyboardHint"] = "宿主与操作系统快捷键优先；可在设置中改键或解绑宿主快捷键，暂未启用 IME/Unicode 输入。",
        ["Rdp_NoFrame"] = "正在等待远程桌面…",
        ["Rdp_SendInputError"] = "输入发送失败，请断开后重新连接会话。",
        ["Rdp_Desktop"] = "远程桌面"
    };
}
