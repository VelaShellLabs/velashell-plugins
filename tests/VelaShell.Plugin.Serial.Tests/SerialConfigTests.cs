using System.IO.Ports;
using VelaShell.PluginSdk.Protocols;

namespace VelaShell.Plugin.Serial.Tests;

/// <summary>
/// 表单值 → 强类型配置。这里的纪律是**容错**:一条存了十个月的配置,不该因为某一项
/// 拼错(旧版本的枚举值、用户手输的波特率)就整条打不开。
/// </summary>
[TestClass]
public sealed class SerialConfigTests
{
    private static ProtocolConnectRequest Request(string host = "COM3", params (string Key, string Value)[] settings) =>
        new()
        {
            Host = host,
            Port = 22,
            Settings = settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };

    [TestMethod]
    public void Parse_DefaultsTo115200_8N1_NoFlowControl()
    {
        // 这是市面上串口工具的通行默认,也是绝大多数设备控制台的出厂设置。
        SerialConfig config = SerialConfig.Parse(Request());

        Assert.AreEqual("COM3", config.PortName);
        Assert.AreEqual(115200, config.BaudRate);
        Assert.AreEqual(8, config.DataBits);
        Assert.AreEqual(StopBits.One, config.StopBits);
        Assert.AreEqual(Parity.None, config.Parity);
        Assert.AreEqual(Handshake.None, config.Handshake);
    }

    [TestMethod]
    public void Parse_DefaultsToNoRewritingAtAll()
    {
        // 默认不改写任何字节:粘贴内容与 ZMODEM 帧因此天然安全,
        // 而"每行盖上一行"这类症状要用户明确打开开关才修 —— 与 PuTTY 一致。
        SerialConfig config = SerialConfig.Parse(Request());

        Assert.AreEqual(SerialEnterMode.Cr, config.EnterMode);
        Assert.IsFalse(config.ImplicitLf);
        Assert.IsFalse(config.ImplicitCr);
        Assert.IsFalse(config.LocalEcho);
        Assert.IsFalse(config.IsPaced);
    }

    [TestMethod]
    public void Parse_DefaultsDtrAndRtsToAsserted()
    {
        // 部分 USB CDC 设备不置 DTR 就不出数据;PuTTY / Tera Term 也都默认拉起两根线。
        SerialConfig config = SerialConfig.Parse(Request());

        Assert.IsTrue(config.Dtr);
        Assert.IsTrue(config.Rts);
    }

    [TestMethod]
    public void Parse_AcceptsNonStandardBaudRates()
    {
        // 250000 是 Marlin 固件的默认,76800 见于一些工业模块 —— 都不在任何标准表上,但驱动认。
        // 把表当白名单等于告诉这些用户"本工具不支持你的设备"。
        Assert.AreEqual(250000, SerialConfig.Parse(Request("COM3", ("baudRate", "250000"))).BaudRate);
        Assert.AreEqual(76800, SerialConfig.Parse(Request("COM3", ("baudRate", " 76800 "))).BaudRate);
    }

    [TestMethod]
    public void ParseBaudRate_FallsBackWhenTheValueIsUnusable()
    {
        Assert.AreEqual(115200, SerialConfig.ParseBaudRate("abc"));
        Assert.AreEqual(115200, SerialConfig.ParseBaudRate("0"));
        Assert.AreEqual(115200, SerialConfig.ParseBaudRate("-9600"));
    }

    [TestMethod]
    public void Parse_MapsEveryFrameFormat()
    {
        SerialConfig config = SerialConfig.Parse(Request("COM3",
            ("dataBits", "7"), ("stopBits", "2"), ("parity", "even")));

        Assert.AreEqual(7, config.DataBits);
        Assert.AreEqual(StopBits.Two, config.StopBits);
        Assert.AreEqual(Parity.Even, config.Parity);
    }

    [TestMethod]
    public void Parse_MapsOnePointFiveStopBits()
    {
        // 只在 5 数据位下才有意义,但确实有设备用;字符串是 "1.5",不是 "1"。
        Assert.AreEqual(StopBits.OnePointFive,
            SerialConfig.Parse(Request("COM3", ("stopBits", "1.5"))).StopBits);
    }

    [TestMethod]
    public void Parse_MapsFlowControl()
    {
        Assert.AreEqual(Handshake.RequestToSend,
            SerialConfig.Parse(Request("COM3", ("flowControl", "rtscts"))).Handshake);
        Assert.AreEqual(Handshake.XOnXOff,
            SerialConfig.Parse(Request("COM3", ("flowControl", "xonxoff"))).Handshake);
        Assert.AreEqual(Handshake.RequestToSendXOnXOff,
            SerialConfig.Parse(Request("COM3", ("flowControl", "both"))).Handshake);
    }

    [TestMethod]
    public void Parse_FallsBackOnUnknownEnumValues()
    {
        // 老配置里可能留着已经不存在的取值(插件升级换过枚举)。回落而不是抛。
        SerialConfig config = SerialConfig.Parse(Request("COM3",
            ("parity", "whatever"), ("flowControl", "dsrdtr"), ("stopBits", "3"), ("dataBits", "9")));

        Assert.AreEqual(Parity.None, config.Parity);
        Assert.AreEqual(Handshake.None, config.Handshake);
        Assert.AreEqual(StopBits.One, config.StopBits);
        Assert.AreEqual(8, config.DataBits);
    }

    [TestMethod]
    public void ParseDelay_ClampsToOneSecond()
    {
        // 节流是逐字节生效的:粘贴 2KB 配置在 100ms/字节下就是 200 秒,期间写侧的锁一直握着。
        Assert.AreEqual(TimeSpan.Zero, SerialConfig.ParseDelay(0));
        Assert.AreEqual(TimeSpan.Zero, SerialConfig.ParseDelay(-5));
        Assert.AreEqual(TimeSpan.FromMilliseconds(5), SerialConfig.ParseDelay(5));
        Assert.AreEqual(TimeSpan.FromMilliseconds(1000), SerialConfig.ParseDelay(999999));
    }

    [TestMethod]
    public void Parse_TrimsThePortName()
    {
        // 端口名允许手输,粘进来带空格是常事;SerialPort 不会替我们 trim。
        Assert.AreEqual("/dev/ttyUSB0", SerialConfig.Parse(Request("  /dev/ttyUSB0 ")).PortName);
    }
}
