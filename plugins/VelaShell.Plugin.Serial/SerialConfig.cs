using System.Globalization;
using System.IO.Ports;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Serial;

/// <summary>连接表单里各字段的键。发布后不可更改 —— 它们会落进用户的会话配置。</summary>
/// <remarks>
/// 端口名**不在这里** —— 它走宿主的"主机"那一栏(<see cref="ProtocolDescriptor.HostFieldKey" />),
/// 与 PuTTY 把 Host Name 换成 Serial line 是同一个取舍:那一栏本来就是"连到哪儿",
/// 而且最近连接列表、会话名这些按"连到哪儿"理解数据的地方,填的正好是设备名。
/// </remarks>
internal static class SerialFields
{
    /// <summary>波特率(可手输,不限于表里那些)。</summary>
    public const string BaudRate = "baudRate";

    /// <summary>数据位:<c>5</c> / <c>6</c> / <c>7</c> / <c>8</c>。</summary>
    public const string DataBits = "dataBits";

    /// <summary>停止位:<c>1</c> / <c>1.5</c> / <c>2</c>。</summary>
    public const string StopBits = "stopBits";

    /// <summary>校验:<c>none</c> / <c>even</c> / <c>odd</c> / <c>mark</c> / <c>space</c>。</summary>
    public const string Parity = "parity";

    /// <summary>流控制:<c>none</c> / <c>rtscts</c> / <c>xonxoff</c> / <c>both</c>。</summary>
    public const string FlowControl = "flowControl";

    /// <summary>回车键发送:<c>cr</c> / <c>lf</c> / <c>crlf</c>。</summary>
    public const string EnterMode = "enterMode";

    /// <summary>打开时置 DTR。</summary>
    public const string Dtr = "dtr";

    /// <summary>打开时置 RTS(流控制为 RTS/CTS 时由驱动接管,本项不参与)。</summary>
    public const string Rts = "rts";

    /// <summary>收到裸 CR 时补一个 LF。</summary>
    public const string ImplicitLf = "implicitLf";

    /// <summary>收到裸 LF 时补一个 CR。</summary>
    public const string ImplicitCr = "implicitCr";

    /// <summary>本地回显:<c>off</c> / <c>on</c>。</summary>
    public const string LocalEcho = "localEcho";

    /// <summary>发送节流:每字节之间的毫秒数。</summary>
    public const string TxDelayChar = "txDelayChar";

    /// <summary>发送节流:每行之后额外的毫秒数。</summary>
    public const string TxDelayLine = "txDelayLine";
}

/// <summary>回车键(宿主发过来的裸 <c>CR</c>)在出方向如何改写。</summary>
internal enum SerialEnterMode
{
    /// <summary>不改写(默认)。绝大多数串口控制台(U-Boot / getty / Cisco IOS)认 CR。</summary>
    Cr,

    /// <summary>改写成 LF。少数只认 LF 的固件(部分 Arduino 草稿、MicroPython REPL 粘贴模式)。</summary>
    Lf,

    /// <summary>改写成 CR LF。Windows 侧的串口服务、以及要求"换行"的 AT 指令集设备。</summary>
    CrLf
}

/// <summary>
/// 一条串口会话的全部参数。表单是一张字符串字典,这里把它一次性收成强类型 ——
/// 于是"哪个键、什么默认值、怎么解析"只在 <see cref="Parse" /> 这一处回答,
/// 会话代码里不再出现 <c>GetString("parity")</c> 这种散落的解析。
/// </summary>
/// <param name="PortName">端口名(<c>COM3</c> / <c>/dev/ttyUSB0</c> / <c>/dev/cu.usbserial-A50285BI</c>)。</param>
/// <param name="BaudRate">波特率。</param>
/// <param name="DataBits">数据位。</param>
/// <param name="StopBits">停止位。</param>
/// <param name="Parity">校验。</param>
/// <param name="Handshake">流控制。</param>
/// <param name="Dtr">打开时是否置 DTR。</param>
/// <param name="Rts">打开时是否置 RTS。</param>
/// <param name="EnterMode">回车键改写方式。</param>
/// <param name="ImplicitLf">收到裸 CR 是否补 LF。</param>
/// <param name="ImplicitCr">收到裸 LF 是否补 CR。</param>
/// <param name="LocalEcho">是否本地回显。</param>
/// <param name="TxDelayPerByte">每字节之间的发送间隔。</param>
/// <param name="TxDelayPerLine">每行之后额外的发送间隔。</param>
internal sealed record SerialConfig(
    string PortName,
    int BaudRate,
    int DataBits,
    StopBits StopBits,
    Parity Parity,
    Handshake Handshake,
    bool Dtr,
    bool Rts,
    SerialEnterMode EnterMode,
    bool ImplicitLf,
    bool ImplicitCr,
    bool LocalEcho,
    TimeSpan TxDelayPerByte,
    TimeSpan TxDelayPerLine)
{
    /// <summary>是否开启了发送节流(两个延时都为零时走零开销的直发路径)。</summary>
    public bool IsPaced => TxDelayPerByte > TimeSpan.Zero || TxDelayPerLine > TimeSpan.Zero;

    /// <summary>
    /// 从连接请求解析出配置。
    /// <para>
    /// 所有取值都**容错**:表单里的值可能来自旧版本插件、也可能是用户手输的
    /// (波特率与端口名都允许手输)。解析不出来一律回落到默认值而不是抛异常 ——
    /// 一条存了十个月的配置不该因为某一项拼错就整条打不开。唯一会拦下来的是
    /// 空端口名,那是真的没法连(见 <see cref="SerialTerminal.ConnectAsync" />)。
    /// </para>
    /// </summary>
    /// <param name="request">宿主递来的连接参数。</param>
    /// <returns>强类型配置。</returns>
    public static SerialConfig Parse(ProtocolConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(
            PortName: request.Host.Trim(),
            BaudRate: ParseBaudRate(request.GetString(SerialFields.BaudRate, "115200")),
            DataBits: request.GetString(SerialFields.DataBits, "8") switch
            {
                "5" => 5,
                "6" => 6,
                "7" => 7,
                _ => 8
            },
            StopBits: request.GetString(SerialFields.StopBits, "1") switch
            {
                "1.5" => System.IO.Ports.StopBits.OnePointFive,
                "2" => System.IO.Ports.StopBits.Two,
                _ => System.IO.Ports.StopBits.One
            },
            Parity: request.GetString(SerialFields.Parity, "none") switch
            {
                "even" => System.IO.Ports.Parity.Even,
                "odd" => System.IO.Ports.Parity.Odd,
                "mark" => System.IO.Ports.Parity.Mark,
                "space" => System.IO.Ports.Parity.Space,
                _ => System.IO.Ports.Parity.None
            },
            Handshake: request.GetString(SerialFields.FlowControl, "none") switch
            {
                "rtscts" => Handshake.RequestToSend,
                "xonxoff" => Handshake.XOnXOff,
                "both" => Handshake.RequestToSendXOnXOff,
                _ => Handshake.None
            },
            Dtr: request.GetBoolean(SerialFields.Dtr, true),
            Rts: request.GetBoolean(SerialFields.Rts, true),
            EnterMode: request.GetString(SerialFields.EnterMode, "cr") switch
            {
                "lf" => SerialEnterMode.Lf,
                "crlf" => SerialEnterMode.CrLf,
                _ => SerialEnterMode.Cr
            },
            ImplicitLf: request.GetBoolean(SerialFields.ImplicitLf),
            ImplicitCr: request.GetBoolean(SerialFields.ImplicitCr),
            LocalEcho: request.GetString(SerialFields.LocalEcho, "off") == "on",
            TxDelayPerByte: ParseDelay(request.GetInt32(SerialFields.TxDelayChar)),
            TxDelayPerLine: ParseDelay(request.GetInt32(SerialFields.TxDelayLine)));
    }

    /// <summary>
    /// 波特率:不变文化解析,非正数回落 115200。
    /// <para>
    /// 刻意**不**校验"是不是标准波特率":250000(Marlin 固件)、76800(部分工业模块)、
    /// 1500000(ESP32)都不在任何一张标准表上,但驱动认。把表当白名单等于告诉这些用户
    /// "本工具不支持你的设备"。真的填错了,由 <see cref="SerialPort.Open" /> 报出来。
    /// </para>
    /// </summary>
    /// <param name="value">表单里的原文。</param>
    /// <returns>波特率。</returns>
    internal static int ParseBaudRate(string value) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : 115200;

    /// <summary>
    /// 发送节流的毫秒数:负数当零;上限 1000ms/字节。
    /// <para>
    /// 上限不是洁癖:节流是**逐字节**生效的,粘贴一段 2KB 的配置在 100ms/字节下就是 200 秒,
    /// 期间写侧的锁一直握着,用户会以为终端死了。1000 已经远超任何真实需要
    /// (Tera Term 的送信延时常用值是 1–10ms)。
    /// </para>
    /// </summary>
    /// <param name="milliseconds">表单里的毫秒数。</param>
    /// <returns>间隔。</returns>
    internal static TimeSpan ParseDelay(int milliseconds) =>
        milliseconds <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(milliseconds, 1000));
}
