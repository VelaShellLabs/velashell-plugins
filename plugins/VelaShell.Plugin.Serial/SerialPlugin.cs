using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Commands;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Serial;

/// <summary>
/// 串口插件的入口。
/// <para>
/// 经 manifest 的 <c>onProtocol:velashell.serial</c> **惰性激活**:用户在连接配置页点到
/// 串口页签(或打开一条串口会话)才装载本程序集 —— 于是 <c>System.IO.Ports</c> 与它的
/// 三平台原生件,在用不到串口的机器上一行都不会被读进内存。串口曾是宿主里那个禁用的
/// 占位页签,现在与 Telnet 一样以插件形式提供。
/// </para>
/// <para>
/// 激活做两件事:把协议注册成**终端协议**(此后终端桥、VT 引擎、回滚、搜索、会话日志、
/// 会话录制与 ZMODEM 全部由宿主原样复用,插件只实现字节双工),以及注册几条
/// 只有串口才有的带外动作(Break / DTR / RTS)—— 那些是命令面板里的条目,
/// 因为它们不是"往线上写字节",终端本身没有表达它们的键位。
/// </para>
/// </summary>
[VelaPlugin]
public sealed class SerialPlugin : IVelaPlugin
{
    private readonly SerialSessionRegistry _sessions = new();
    private readonly List<IDisposable> _commands = [];
    private IPluginContext? _context;
    private SerialTerminal? _terminal;
    private IDisposable? _registration;

    /// <inheritdoc />
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _terminal = new(context, _sessions);
        _registration = context.Protocols.Register(BuildDescriptor(context), _terminal);
        RegisterCommands(context);
        // 语言切换后重注册:表单标签与命令标题都是插件自己的文案,宿主不会替我们翻。
        context.Events.LocaleChanged += _ => Reregister();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable command in _commands)
        {
            command.Dispose();
        }
        _commands.Clear();
        _registration?.Dispose();
        _registration = null;
        _terminal = null;
        _context = null;
        return Task.CompletedTask;
    }

    private void Reregister()
    {
        if (_context is not { } context || _terminal is not { } terminal)
        {
            return;
        }
        // **先注册后释放**(同 S3 / Telnet 插件):先 Dispose 会触发注销事件,
        // 把用户正开着的会话一起掐掉,而这里只是换了个界面语言。
        IDisposable next = context.Protocols.Register(BuildDescriptor(context), terminal);
        _registration?.Dispose();
        _registration = next;
        foreach (IDisposable command in _commands)
        {
            command.Dispose();
        }
        _commands.Clear();
        RegisterCommands(context);
    }

    /// <summary>
    /// 带外动作。它们不能做成终端里的键位:Break 是**线路状态**而不是字符,
    /// DTR/RTS 更是两根独立的控制线 —— 没有任何字节序列能表达它们。
    /// <para>
    /// 这几条正是"串口不只是一条慢速 TCP"的地方,也是网络工程师(Cisco ROMMON 口令恢复
    /// 靠 Break)与嵌入式开发者(DTR 脉冲复位开发板)真正会用到的功能。
    /// </para>
    /// </summary>
    private void RegisterCommands(IPluginContext context)
    {
        var loc = new Loc(context.Host.Locale);
        string category = loc["Serial_Category"];
        Register($"{context.PluginId}.break", loc["Serial_CmdBreak"],
            (session, token) => session.SendBreakAsync(token));
        Register($"{context.PluginId}.resetBoard", loc["Serial_CmdReset"],
            (session, token) => session.PulseAsync(SerialControlLine.Dtr, token));
        Register($"{context.PluginId}.toggleDtr", loc["Serial_CmdToggleDtr"],
            (session, token) => session.ToggleAsync(SerialControlLine.Dtr, token));
        Register($"{context.PluginId}.toggleRts", loc["Serial_CmdToggleRts"],
            (session, token) => session.ToggleAsync(SerialControlLine.Rts, token));
        return;

        void Register(string id, string title, Func<SerialSession, CancellationToken, Task<bool>> action) =>
            _commands.Add(context.Commands.Register(new(id, title, category, async token =>
            {
                if (_sessions.Current() is not { } session)
                {
                    // 一条串口都没开着。宿主没有给插件弹提示的通道,记日志是唯一能做的 ——
                    // 好在这几条命令只在开过串口(= 插件已激活)之后才出现在面板里。
                    context.Log.Warn($"{title}: no serial session is open.");
                    return;
                }
                await action(session, token).ConfigureAwait(false);
            })));
    }

    /// <summary>
    /// 协议描述:页签、连接表单、能力位。宿主按这份声明渲染界面,
    /// 因此插件没有一行连接对话框的界面代码。
    /// <para>
    /// 字段的取舍对着市面上的串口工具(PuTTY / Tera Term / minicom / CoolTerm)对过一遍:
    /// 主表单是"连不连得上"的那几项,<see cref="ProtocolSettingField.IsAdvanced" /> 收起的
    /// 是"连上了但看着不对"的那几项 —— 后者用户不会一开始就去调,但出问题时必须找得到。
    /// </para>
    /// </summary>
    private static ProtocolDescriptor BuildDescriptor(IPluginContext context)
    {
        var loc = new Loc(context.Host.Locale);
        return new()
        {
            Id = context.PluginId,
            DisplayName = loc["Serial_DisplayName"],
            // NoEndpoint:收起"端口"那一栏(串口的目标不是 host:port)。
            // NoCredentials:串口没有协议级凭据 —— 登录发生在带内(设备自己打印 login:)。
            Features = ProtocolFeatures.NoEndpoint | ProtocolFeatures.NoCredentials,

            // ── 主机那一栏 = 串口设备 ────────────────────────────────────────
            // 与 PuTTY 的串口页同构(它把 "Host Name" 换成 "Serial line")。
            // 做成**动态**下拉:USB 转串口是热插拔的,用户很可能是先打开对话框、才想起去插线;
            // 允许手输:没插的适配器、容器里映射进来的 /dev/ttyS10、还没装驱动的板子,都得填得进去,
            // 而且一条存着 COM7 的旧配置绝不能因为"这次没枚举到"就被下拉改成别的口。
            HostLabel = loc["Serial_Port"],
            HostPlaceholder = loc["Serial_PortPlaceholder"],
            HostKind = ProtocolSettingKind.DynamicChoice,
            HostAllowsCustomValue = true,

            Fields =
            [
                new()
                {
                    Key = SerialFields.BaudRate,
                    Label = loc["Serial_BaudRate"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "115200",
                    // 可手输:250000(Marlin)、76800(部分工业模块)、1500000(ESP32)
                    // 都不在任何一张标准表上,但驱动认。
                    AllowsCustomValue = true,
                    Hint = loc["Serial_BaudRateHint"],
                    Choices =
                    [
                        new("9600", "9600"),
                        new("19200", "19200"),
                        new("38400", "38400"),
                        new("57600", "57600"),
                        new("115200", "115200"),
                        new("230400", "230400"),
                        new("460800", "460800"),
                        new("921600", "921600"),
                        new("1500000", "1500000"),
                    ],
                },
                new()
                {
                    Key = SerialFields.DataBits,
                    Label = loc["Serial_DataBits"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "8",
                    Choices = [new("8", "8"), new("7", "7"), new("6", "6"), new("5", "5")],
                },
                new()
                {
                    Key = SerialFields.StopBits,
                    Label = loc["Serial_StopBits"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "1",
                    Choices = [new("1", "1"), new("1.5", "1.5"), new("2", "2")],
                },
                new()
                {
                    Key = SerialFields.Parity,
                    Label = loc["Serial_Parity"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "none",
                    Choices =
                    [
                        new("none", loc["Serial_ParityNone"]),
                        new("even", loc["Serial_ParityEven"]),
                        new("odd", loc["Serial_ParityOdd"]),
                        new("mark", loc["Serial_ParityMark"]),
                        new("space", loc["Serial_ParitySpace"]),
                    ],
                },
                new()
                {
                    Key = SerialFields.FlowControl,
                    Label = loc["Serial_FlowControl"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "none",
                    Hint = loc["Serial_FlowControlHint"],
                    Choices =
                    [
                        new("none", loc["Serial_FlowNone"]),
                        new("rtscts", loc["Serial_FlowRtsCts"]),
                        new("xonxoff", loc["Serial_FlowXonXoff"]),
                        new("both", loc["Serial_FlowBoth"]),
                    ],
                },
                new()
                {
                    Key = SerialFields.EnterMode,
                    Label = loc["Serial_EnterMode"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "cr",
                    Hint = loc["Serial_EnterModeHint"],
                    Choices =
                    [
                        new("cr", loc["Serial_EnterCr"]),
                        new("lf", loc["Serial_EnterLf"]),
                        new("crlf", loc["Serial_EnterCrLf"]),
                    ],
                },

                // ── 以下收进「高级选项」 ─────────────────────────────────────
                new()
                {
                    Key = SerialFields.Dtr,
                    Label = loc["Serial_Dtr"],
                    Kind = ProtocolSettingKind.Boolean,
                    DefaultValue = "true",
                    IsAdvanced = true,
                    Hint = loc["Serial_DtrHint"],
                },
                new()
                {
                    Key = SerialFields.Rts,
                    Label = loc["Serial_Rts"],
                    Kind = ProtocolSettingKind.Boolean,
                    DefaultValue = "true",
                    IsAdvanced = true,
                    Hint = loc["Serial_RtsHint"],
                    // 流控制取 RTS/CTS 时这根线归驱动:留着一个设了也不生效的开关,
                    // 用户只会以为自己设过了。
                    VisibleWhen = new(SerialFields.FlowControl, ["none", "xonxoff"]),
                },
                new()
                {
                    Key = SerialFields.ImplicitLf,
                    Label = loc["Serial_ImplicitLf"],
                    Kind = ProtocolSettingKind.Boolean,
                    DefaultValue = "false",
                    IsAdvanced = true,
                    Hint = loc["Serial_ImplicitLfHint"],
                },
                new()
                {
                    Key = SerialFields.ImplicitCr,
                    Label = loc["Serial_ImplicitCr"],
                    Kind = ProtocolSettingKind.Boolean,
                    DefaultValue = "false",
                    IsAdvanced = true,
                    Hint = loc["Serial_ImplicitCrHint"],
                },
                new()
                {
                    Key = SerialFields.LocalEcho,
                    Label = loc["Serial_LocalEcho"],
                    Kind = ProtocolSettingKind.Choice,
                    DefaultValue = "off",
                    IsAdvanced = true,
                    Hint = loc["Serial_LocalEchoHint"],
                    Choices = [new("off", loc["Serial_EchoOff"]), new("on", loc["Serial_EchoOn"])],
                },
                new()
                {
                    Key = SerialFields.TxDelayChar,
                    Label = loc["Serial_TxDelayChar"],
                    Kind = ProtocolSettingKind.Integer,
                    DefaultValue = "0",
                    IsAdvanced = true,
                    Hint = loc["Serial_TxDelayCharHint"],
                },
                new()
                {
                    Key = SerialFields.TxDelayLine,
                    Label = loc["Serial_TxDelayLine"],
                    Kind = ProtocolSettingKind.Integer,
                    DefaultValue = "0",
                    IsAdvanced = true,
                    Hint = loc["Serial_TxDelayLineHint"],
                },
            ],
        };
    }
}
