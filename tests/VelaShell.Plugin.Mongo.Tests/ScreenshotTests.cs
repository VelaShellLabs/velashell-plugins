using Avalonia.Media.Imaging;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 逐屏截图:每个设计稿画板一张,落到 <c>MONGO_SCREENSHOT_DIR</c> 里供人与设计稿对照。
/// 不设输出目录时照样跑一遍(验证"这一屏打得开、渲染不抛"),只是不落盘。
/// 需要本机有带 <c>shop</c> 库的 MongoDB(或用 <c>MONGO_SCREENSHOT_DB</c> 指一个);没有就 Inconclusive。
/// </summary>
[TestClass]
[TestCategory("Screenshots")]
public sealed class ScreenshotTests
{
    [TestMethod]
    public void Board01_Workspace_shell_renders() => Screens.OnUi(async () =>
    {
        await TestServer.RequireAsync();
        await using Workbench bench = await Screens.OpenWorkbenchAsync();
        WriteableBitmap? frame = Screens.Capture(bench.Window, "00-shell");
        Assert.IsNotNull(frame);
        Assert.IsTrue(bench.ViewModel.VisibleNodes.Count > 3, "the object tree should list the databases");
    });
}
