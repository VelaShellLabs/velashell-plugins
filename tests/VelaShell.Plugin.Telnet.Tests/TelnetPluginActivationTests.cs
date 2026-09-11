using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.Telnet.Tests;

/// <summary>
/// 插件激活:注册的协议描述本身就是产品的一部分 —— 它决定连接对话框长什么样、
/// 会话标签页画什么图标。这里按 SDK 的测试替身把它取出来验。
/// </summary>
[TestClass]
public sealed class TelnetPluginActivationTests
{
    private static TestPluginContext NewContext(string locale = "zh-Hans")
    {
        var context = new TestPluginContext { PluginId = "velashell.telnet" };
        context.HostInfo.Locale = locale;
        context.RecordingProtocols.PluginId = "velashell.telnet";
        return context;
    }

    private static async Task<ProtocolDescriptor> ActivateAsync(TestPluginContext context)
    {
        var plugin = new TelnetPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        return context.RecordingProtocols.Registered.Single();
    }

    [TestMethod]
    public async Task Activate_RegistersATerminalProtocolUnderThePluginId()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        Assert.AreEqual("velashell.telnet", descriptor.Id);
        Assert.AreEqual(23, descriptor.DefaultPort);
        Assert.IsNotNull(context.RecordingProtocols.GetTerminal("velashell.telnet"));
    }

    [TestMethod]
    public async Task Activate_GivesTheSessionTabTheEthernetPortGlyph()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        PluginIcon? icon = descriptor.Icon;
        Assert.IsNotNull(icon, "不自报图标,标签上就是所有插件共用的那个通用插头。");
        // lucide 那套的规格:描边、视框 24。报成实心会把这个描边字形填成一团色块。
        Assert.IsFalse(icon.IsFilled);
        Assert.AreEqual(24d, icon.ViewBoxSize);
        // 刻意不是终端字形:宿主的 SSH 标签画的就是 square-terminal,而标签上只有 12–16px,
        // 两个终端框摆在一起等于没分。这一条写成断言,免得日后有人"顺手统一"成终端图标。
        Assert.AreEqual(
            "M15 20l3-3h2a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h2l3 3Z M6 8v2 M10 8v2 M14 8v2 M18 8v2",
            icon.PathData);
    }
}
