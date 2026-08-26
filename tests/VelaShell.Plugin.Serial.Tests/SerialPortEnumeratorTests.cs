namespace VelaShell.Plugin.Serial.Tests;

/// <summary>
/// 端口枚举里**能脱离硬件验证**的那几段:排序、名字整理、Windows 友好名拆分。
/// 三平台各自那段"去哪儿找设备"没法在 CI 上验(需要真设备),
/// 但它们最容易出错的部分恰好都在这三个纯函数里。
/// </summary>
[TestClass]
public sealed class SerialPortEnumeratorTests
{
    private static string[] SortNames(params string[] names) =>
        [.. SerialPortEnumerator.Sort(names.Select(name => new SerialPortInfo(name, string.Empty)))
            .Select(port => port.PortName)];

    [TestMethod]
    public void Sort_PutsCom2BeforeCom10()
    {
        // 微软的文档明写 GetPortNames 的返回顺序未定义,而字符串序下 COM10 排在 COM2 前面。
        // 插了十来个适配器的机器上,不自己排就是一个乱序下拉。
        CollectionAssert.AreEqual(
            new[] { "COM1", "COM2", "COM10", "COM11" },
            SortNames("COM11", "COM2", "COM10", "COM1"));
    }

    [TestMethod]
    public void Sort_HandlesUnixDeviceNames()
    {
        CollectionAssert.AreEqual(
            new[] { "/dev/ttyS0", "/dev/ttyUSB0", "/dev/ttyUSB2", "/dev/ttyUSB10" },
            SortNames("/dev/ttyUSB10", "/dev/ttyUSB2", "/dev/ttyS0", "/dev/ttyUSB0"));
    }

    [TestMethod]
    public void Sort_IgnoresLeadingZeroes()
    {
        CollectionAssert.AreEqual(new[] { "COM007", "COM8" }, SortNames("COM8", "COM007"));
    }

    [TestMethod]
    public void Sort_IsStableForNamesWithoutDigits()
    {
        CollectionAssert.AreEqual(
            new[] { "/dev/cu.Bluetooth-Incoming-Port", "/dev/cu.SLAB_USBtoUART" },
            SortNames("/dev/cu.SLAB_USBtoUART", "/dev/cu.Bluetooth-Incoming-Port"));
    }

    // ── Linux / macOS 的友好名 ──────────────────────────────────────────────

    [TestMethod]
    public void DescribeUnixName_UnwrapsAUdevByIdLink()
    {
        // udev 在 /dev/serial/by-id/ 建的链接名自带厂商/型号/序列号,是白捡的友好名 ——
        // 但要去掉 usb- 前缀与 -if00-port0 那截对用户毫无意义的接口后缀。
        Assert.AreEqual("FTDI FT232R USB UART A50285BI",
            SerialPortEnumerator.DescribeUnixName("usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0"));
    }

    [TestMethod]
    public void DescribeUnixName_HandlesALinkWithoutAnInterfaceSuffix()
    {
        Assert.AreEqual("Silicon Labs CP2102 UART Bridge",
            SerialPortEnumerator.DescribeUnixName("usb-Silicon_Labs_CP2102_UART_Bridge"));
    }

    [TestMethod]
    public void DescribeUnixName_KeepsAMacOsDeviceSuffixAsIs()
    {
        Assert.AreEqual("usbserial-A50285BI", SerialPortEnumerator.DescribeUnixName("usbserial-A50285BI"));
    }

    [TestMethod]
    public void DescribeUnixName_ReturnsEmptyForNothing()
    {
        Assert.AreEqual(string.Empty, SerialPortEnumerator.DescribeUnixName("   "));
    }

    // ── Windows 的友好名 ────────────────────────────────────────────────────

    [TestMethod]
    public void TrySplitFriendlyName_SeparatesTheDescriptionFromThePort()
    {
        Assert.IsTrue(WindowsSerialPortNames.TrySplitFriendlyName(
            "USB-SERIAL CH340 (COM3)", out string port, out string description));
        Assert.AreEqual("COM3", port);
        Assert.AreEqual("USB-SERIAL CH340", description);
    }

    [TestMethod]
    public void TrySplitFriendlyName_HandlesADescriptionContainingParentheses()
    {
        // 只认**最后**一对括号:友好名里出现别的括号是常事。
        Assert.IsTrue(WindowsSerialPortNames.TrySplitFriendlyName(
            "Prolific USB-to-Serial Comm Port (PL2303) (COM12)", out string port, out string description));
        Assert.AreEqual("COM12", port);
        Assert.AreEqual("Prolific USB-to-Serial Comm Port (PL2303)", description);
    }

    [TestMethod]
    public void TrySplitFriendlyName_RejectsAParallelPort()
    {
        // "端口(COM 和 LPT)"这个设备类里也有并口 —— 拆不出 (COMn) 正是过滤器本身。
        Assert.IsFalse(WindowsSerialPortNames.TrySplitFriendlyName("Printer Port (LPT1)", out _, out _));
    }

    [TestMethod]
    public void TrySplitFriendlyName_RejectsNamesWithoutAPortNumber()
    {
        Assert.IsFalse(WindowsSerialPortNames.TrySplitFriendlyName("Communications Port", out _, out _));
        Assert.IsFalse(WindowsSerialPortNames.TrySplitFriendlyName("Weird Device (COM)", out _, out _));
        Assert.IsFalse(WindowsSerialPortNames.TrySplitFriendlyName("Weird Device (COMx)", out _, out _));
    }

    [TestMethod]
    public void List_NeverThrows()
    {
        // 枚举跑在"用户打开连接对话框"这条画界面的路径上:
        // 一次列不出设备不该变成一个连表单都打不开的错误。
        IReadOnlyList<SerialPortInfo> ports = SerialPortEnumerator.List();

        Assert.IsNotNull(ports);
    }

    [TestMethod]
    public void Label_FallsBackToThePortNameWhenThereIsNoDescription()
    {
        Assert.AreEqual("COM3", new SerialPortInfo("COM3", string.Empty).Label);
        Assert.AreEqual("CH340 (COM3)", new SerialPortInfo("COM3", "CH340").Label);
    }
}
