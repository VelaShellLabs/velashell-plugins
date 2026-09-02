using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Serial.Tests;

/// <summary>
/// 插件激活:注册的协议描述本身就是产品的一部分 —— 它决定连接对话框长什么样。
/// 这里按 SDK 的测试替身把它取出来逐项验,因为这些声明一旦发布就落进用户的会话配置,
/// 改不回来。
/// </summary>
[TestClass]
public sealed class SerialPluginActivationTests
{
    private static TestPluginContext NewContext(string locale = "zh-Hans")
    {
        var context = new TestPluginContext { PluginId = "velashell.serial" };
        context.HostInfo.Locale = locale;
        context.RecordingProtocols.PluginId = "velashell.serial";
        return context;
    }

    private static async Task<ProtocolDescriptor> ActivateAsync(TestPluginContext context)
    {
        var plugin = new SerialPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        return context.RecordingProtocols.Registered.Single();
    }

    [TestMethod]
    public async Task Activate_RegistersATerminalProtocolUnderThePluginId()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        Assert.AreEqual("velashell.serial", descriptor.Id);
        // 终端协议,不是文件协议:串口没有文件系统。
        Assert.IsNotNull(context.RecordingProtocols.GetTerminal("velashell.serial"));
        Assert.IsNull(context.RecordingProtocols.GetFileSystem("velashell.serial"));
    }

    [TestMethod]
    public async Task Activate_HidesThePortAndCredentialColumns()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        // NoEndpoint:串口的目标不是 host:port,端口那一栏填什么都不会被用上。
        Assert.IsTrue(descriptor.Features.HasFlag(ProtocolFeatures.NoEndpoint));
        // NoCredentials:登录发生在带内(设备自己打印 login:),摆两个填了也发不出去的框只会误导。
        Assert.IsTrue(descriptor.Features.HasFlag(ProtocolFeatures.NoCredentials));
    }

    [TestMethod]
    public async Task Activate_MakesTheDeviceColumnARefreshableEditableCombo()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        // 动态:USB 转串口是热插拔的,注册时枚举一次的列表等用户插上适配器时早就过期了。
        Assert.AreEqual(ProtocolSettingKind.DynamicChoice, descriptor.HostKind);
        // 可手输:没插的适配器、容器里映射进来的 /dev/ttyS10 都得填得进去,
        // 而一条存着 COM7 的旧配置更不能因为"这次没枚举到"就被下拉改成别的口。
        Assert.IsTrue(descriptor.HostAllowsCustomValue);
        Assert.AreEqual("串口设备", descriptor.HostLabel);
    }

    [TestMethod]
    public async Task Activate_LetsTheUserTypeANonStandardBaudRate()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        ProtocolSettingField baud = descriptor.Fields.Single(field => field.Key == "baudRate");
        Assert.AreEqual("115200", baud.DefaultValue);
        Assert.IsTrue(baud.AllowsCustomValue, "250000(Marlin)与 76800 都不在标准表上,但驱动认");
        Assert.IsFalse(baud.IsAdvanced, "波特率是'连不连得上'的参数,不该收进高级选项");
    }

    [TestMethod]
    public async Task Activate_KeepsTheConnectOrNotFieldsOutOfTheAdvancedSection()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        string[] mainForm = [.. descriptor.Fields.Where(field => !field.IsAdvanced).Select(field => field.Key)];
        Assert.AreSequenceEqual(
            ["baudRate", "dataBits", "stopBits", "parity", "flowControl", "enterMode"], mainForm, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task Activate_HidesTheRtsToggleWhileTheDriverOwnsTheLine()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        ProtocolSettingField rts = descriptor.Fields.Single(field => field.Key == "rts");
        Assert.IsNotNull(rts.VisibleWhen);
        Assert.AreEqual("flowControl", rts.VisibleWhen.Key);
        // 流控制取 RTS/CTS 时这根线归驱动:留着一个设了也不生效的开关只会让用户以为自己设过了。
        Assert.IsFalse(rts.VisibleWhen.IsSatisfiedBy(_ => "rtscts"));
        Assert.IsTrue(rts.VisibleWhen.IsSatisfiedBy(_ => "none"));
    }

    [TestMethod]
    public async Task Activate_RegistersTheOutOfBandCommands()
    {
        using TestPluginContext context = NewContext();

        await ActivateAsync(context);

        string[] ids = [.. context.RecordingCommands.Registered.Select(command => command.Id)];
        Assert.AreSequenceEqual(
            [
                "velashell.serial.break",
                "velashell.serial.resetBoard",
                "velashell.serial.toggleDtr",
                "velashell.serial.toggleRts"
            ], ids, Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task Commands_DoNothingWhenNoSerialSessionIsOpen()
    {
        // 命令面板里点一下就崩掉插件是不可接受的;没有会话时它只该记一条日志。
        using TestPluginContext context = NewContext();
        await ActivateAsync(context);

        PluginCommandDescriptorSnapshot command = Snapshot(context, "velashell.serial.break");
        await command.ExecuteAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task GetChoices_ReturnsPortsForTheHostColumnOnly()
    {
        using TestPluginContext context = NewContext();
        await ActivateAsync(context);
        var source = (IProtocolChoiceSource)context.RecordingProtocols.GetTerminal("velashell.serial")!;

        IReadOnlyList<ProtocolSettingChoice> ports =
            await source.GetChoicesAsync(ProtocolDescriptor.HostFieldKey, CancellationToken.None);
        IReadOnlyList<ProtocolSettingChoice> other =
            await source.GetChoicesAsync("baudRate", CancellationToken.None);

        // 本机有没有串口不确定,所以只能断言"不抛、给得出一份表" —— 内容由枚举器的单测钉。
        Assert.IsNotNull(ports);
        Assert.AreEqual(0, other.Count, "不认识的字段键必须给空表:宿主会对每个动态字段都问一遍");
    }

    [TestMethod]
    public async Task Connect_WithoutADeviceName_SaysSoInPlainLanguage()
    {
        using TestPluginContext context = NewContext();
        await ActivateAsync(context);
        IProtocolTerminal terminal = context.RecordingProtocols.GetTerminal("velashell.serial")!;

        ProtocolConnectionException error = await Assert.ThrowsExactlyAsync<ProtocolConnectionException>(
            () => terminal.ConnectAsync(
                new() { Host = "  ", Port = 22 },
                new("xterm-256color", 80, 24),
                CancellationToken.None));

        // 不拦的话,用户看到的是 SerialPort 抛的那句英文 "PortName cannot be empty"。
        Assert.Contains("串口设备", error.Message);
    }

    [TestMethod]
    public async Task Deactivate_RemovesEverythingItRegistered()
    {
        using TestPluginContext context = NewContext();
        var plugin = new SerialPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);

        await plugin.DeactivateAsync(CancellationToken.None);

        Assert.AreEqual(0, context.RecordingProtocols.Registered.Count);
        Assert.AreEqual(0, context.RecordingCommands.Registered.Count);
    }

    [TestMethod]
    public async Task Activate_FallsBackToEnglishForOtherLocales()
    {
        using TestPluginContext context = NewContext("ja");

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        Assert.AreEqual("Serial", descriptor.DisplayName);
        Assert.AreEqual("Serial device", descriptor.HostLabel);
    }

    /// <summary>命令替身的形状随 SDK 走,这里只取执行体。</summary>
    private static PluginCommandDescriptorSnapshot Snapshot(TestPluginContext context, string id)
    {
        var command = context.RecordingCommands.Registered.Single(c => c.Id == id);
        return new(command.ExecuteAsync);
    }

    private sealed record PluginCommandDescriptorSnapshot(Func<CancellationToken, Task> ExecuteAsync);
}
