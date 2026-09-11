using VelaShell.PluginSdk;
using VelaShell.PluginSdk.Protocols;
using VelaShell.PluginSdk.Testing;

namespace VelaShell.Plugin.S3.Tests;

/// <summary>
/// 插件激活:注册的协议描述本身就是产品的一部分 —— 它决定连接对话框长什么样、
/// 会话标签页画什么图标。这里按 SDK 的测试替身把它取出来验。
/// </summary>
[TestClass]
public sealed class S3PluginActivationTests
{
    private static TestPluginContext NewContext(string locale = "zh-Hans")
    {
        var context = new TestPluginContext { PluginId = "velashell.s3" };
        context.HostInfo.Locale = locale;
        context.RecordingProtocols.PluginId = "velashell.s3";
        return context;
    }

    private static async Task<ProtocolDescriptor> ActivateAsync(TestPluginContext context)
    {
        var plugin = new S3Plugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        return context.RecordingProtocols.Registered.Single();
    }

    [TestMethod]
    public async Task Activate_RegistersAFileProtocolUnderThePluginId()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        Assert.AreEqual("velashell.s3", descriptor.Id);
        Assert.AreEqual("S3", descriptor.DisplayName);
        // 文件协议,不是终端协议:标签里是双栏浏览器,宿主据此路由。
        Assert.IsNotNull(context.RecordingProtocols.GetFileSystem("velashell.s3"));
    }

    [TestMethod]
    public async Task Activate_GivesTheSessionTabTheCloudGlyph()
    {
        using TestPluginContext context = NewContext();

        ProtocolDescriptor descriptor = await ActivateAsync(context);

        PluginIcon? icon = descriptor.Icon;
        Assert.IsNotNull(icon, "不自报图标,标签上就是所有插件共用的那个通用插头。");
        // lucide 那套的规格:描边、视框 24。报成实心会把这个描边字形填成一团色块。
        Assert.IsFalse(icon.IsFilled);
        Assert.AreEqual(24d, icon.ViewBoxSize);
        // 刻意不是存储那一类字形:宿主给 SFTP / FTP 画的正是 hard-drive,而这三种标签并排
        // 躺在同一条标签条上。这一条写成断言,免得日后有人"顺手统一"成硬盘图标。
        Assert.AreEqual("M17.5 19H9a7 7 0 1 1 6.71-9h1.79a4.5 4.5 0 1 1 0 9Z", icon.PathData);
    }
}
