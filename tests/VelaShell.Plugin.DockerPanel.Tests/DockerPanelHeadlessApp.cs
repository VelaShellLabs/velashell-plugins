using Avalonia;
using Avalonia.Headless;
using VelaShell.Plugin.DockerPanel.Tests;

[assembly: AvaloniaTestApplication(typeof(DockerPanelHeadlessApp))]

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 确认闸门那几条测试共用的 headless 宿主。
/// <para>
/// <b>为什么需要它。</b><see cref="Ui.ConfirmGate.AskAsync" /> 经 <c>Ui.Post</c> 落状态,
/// 而 <c>Ui.Post</c> 只在**调用方已经在 UI 线程上**时才同步执行,否则排进
/// <c>Dispatcher.UIThread</c> 的队列。生产路径上闸门总是从 UI 线程打开的,这条成立;
/// 但测试线程不是 UI 线程 —— 那次 Post 会排进一个**没人泵**的队列,
/// 于是 <c>Request</c> 永远不落,断言读到的是闸门关着时的空值。
/// </para>
/// <para>
/// <b>为什么以前没露面。</b>没有 <c>TestTimeout</c> 时 MSTest 在当前线程上直接跑测试体,
/// 全程只有一条线程 —— 谁先碰 <c>Dispatcher.UIThread</c> 谁就是"UI 线程",
/// <c>CheckAccess()</c> 于是恒为真,Post 全部同步执行。本仓库的
/// <c>tests/velashell.runsettings</c> 设了 60 秒单测超时,而带超时的测试体被 MSTest 放到
/// **线程池线程**上跑 —— 每条测试落在哪条线程不再确定,这条隐式依赖就暴露了:
/// 并入本仓库后 5 次里有 4 次红,而且红的是哪几条全看调度。
/// </para>
/// <para>
/// <b>刻意不装任何主题。</b>这几条测试一个控件都不构造,要的只是一条真的在泵的 UI 线程。
/// (Redis 那边的 headless 宿主装 Fluent,是因为它真的要装载 AXAML 与模板。)
/// </para>
/// </summary>
public class DockerPanelHeadlessApp : Application
{
    /// <summary>headless 宿主的构建入口(由 Avalonia 的测试基建反射调用)。</summary>
    /// <returns>应用构建器。</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DockerPanelHeadlessApp>()
                  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
