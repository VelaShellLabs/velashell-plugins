using VelaShell.PluginSdk.Testing;
using VelaShell.PluginSdk.Ui;

namespace VelaShell.Plugin.Mongo.Tests;

/// <summary>
/// 宿主退出时的停用:宿主在 UI 线程上同步等着全部插件停用(拆容器那一下 <c>Wait</c>),
/// 停用本身跑在线程池上,而面板的 <c>Closed</c> 也就在线程池上触发。
/// 这时任何"排到 UI 线程再做"的事都排不上,任何直接碰界面对象的事都会抛跨线程异常。
/// </summary>
[TestClass]
public sealed class ShutdownTests
{
    /// <summary>关面板永远完不成(宿主退出时它排在被堵住的 UI 线程上)。</summary>
    private sealed class StuckPanel(IPluginPanel inner) : IPluginPanel
    {
        public string PanelId => inner.PanelId;

        public bool IsOpen => inner.IsOpen;

        public event Action? Closed
        {
            add => inner.Closed += value;
            remove => inner.Closed -= value;
        }

        public event Action<double>? PlacementRatioChanged
        {
            add => inner.PlacementRatioChanged += value;
            remove => inner.PlacementRatioChanged -= value;
        }

        public Task ActivateAsync() => inner.ActivateAsync();

        public Task CloseAsync() => new TaskCompletionSource().Task;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public void Deactivating_off_the_ui_thread_while_it_is_blocked_finishes_and_throws_nothing() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.mongo" };
        context.FakeUi.CreateContentEagerly = true;
        var plugin = new MongoPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        await Screens.PumpAsync(10);
        Assert.IsTrue(context.FakeUi.LastPanel.IsOpen);

        // 像宿主那样:UI 线程同步等,停用在线程池上跑(面板的 Closed 因此也在线程池上触发)。
        var deactivation = Task.Run(() => plugin.DeactivateAsync(CancellationToken.None));
        Assert.IsTrue(deactivation.Wait(TimeSpan.FromSeconds(2)), "deactivation must finish inside the host's 2 s budget");
        Assert.IsNull(deactivation.Exception, deactivation.Exception?.ToString());

        // UI 线程空出来以后,投递过去的界面收尾照常做完,也不抛。
        await Screens.PumpAsync(10);
        Assert.IsFalse(context.FakeUi.LastPanel.IsOpen);
    });

    [TestMethod]
    public void A_panel_that_never_closes_does_not_hold_deactivation_past_the_budget() => Screens.OnUi(async () =>
    {
        using var context = new TestPluginContext { PluginId = "velashell.mongo" };
        context.FakeUi.CreateContentEagerly = true;
        var plugin = new MongoPlugin();
        await plugin.ActivateAsync(context, CancellationToken.None);
        await Screens.PumpAsync(10);
        // 把插件手里的面板换成一个关不掉的(插件不留测试钩子,这里直接改字段)。
        typeof(MongoPlugin).GetField("_panel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(plugin, new StuckPanel(context.FakeUi.LastPanel));

        var deactivation = Task.Run(() => plugin.DeactivateAsync(CancellationToken.None));
        Assert.IsTrue(deactivation.Wait(TimeSpan.FromSeconds(2)), "a stuck CloseAsync is abandoned, not awaited forever");
        Assert.IsNull(deactivation.Exception, deactivation.Exception?.ToString());
        await Screens.PumpAsync(10);
    });
}
