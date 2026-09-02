using Avalonia.Controls;
using Avalonia.Threading;
using Shapes = Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 面板版式表本身的装载与生效。
/// <para>
/// 这一组测试的存在理由很实际:面板视图必须先有一条**活的 Redis 连接**才建得起来,
/// 所以整套 <c>RedisPanelUiTests</c> 在没有 127.0.0.1:6379 的机器上全部跳过 ——
/// 于是"样式表写坏了"这件事在开发机上可能一路溜到真机才暴露。
/// 把版式抽成一个独立的 avares 资源(<c>Ui/RedisPanelStyles.axaml</c>)之后,
/// 它可以脱离连接单独装载,这一层就有了**无条件跑得起来**的守卫。
/// </para>
/// <para>
/// 与面板测试同一条口径:headless 宿主只装 Fluent,一个 <c>Vela*</c> 令牌都不给 ——
/// 宿主令牌缺席时样式表照样要能装载(未命中的动态资源让属性留在默认值,不该抛)。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RedisPanelStylesTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RedisPanelStylesTests).Assembly);

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static Styles LoadStyles() =>
        (Styles)AvaloniaXamlLoader.Load(
            new Uri("avares://VelaShell.Plugin.Redis/Ui/RedisPanelStyles.axaml"));

    /// <summary>版式表能装载 —— 选择器语法、模板、Setter 目标属性全部就位。</summary>
    [TestMethod]
    public void Styles_Load_WithoutHostThemeTokens() =>
        OnUi(() =>
        {
            Styles styles = LoadStyles();

            // 三百多行里任何一条选择器写坏都会在装载时抛,所以数量本身就是个有意义的断言:
            // 它顺带挡住"整段被误删只剩一条"这类合并事故。
            Assert.IsGreaterThan(30, styles.Count);
        });

    /// <summary>
    /// <c>Button.chip</c> 的自定义模板真的生效:图标(Tag → Path.Data)与文字(Content)
    /// 两样都要在可视树里出现。
    /// <para>
    /// 这一条守的是一个**沉默**的失败:模板写错时按钮不会报错,它只是变成一个空白方块 ——
    /// 而面板上四十来个动作按钮全走这个模板。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ChipButton_Template_RendersIconAndLabel() =>
        OnUi(() =>
        {
            var geometry = StreamGeometry.Parse("M0,0 L10,10");
            var button = new Button { Classes = { "chip" }, Content = "导出", Tag = geometry };
            var window = new Window { Width = 400, Height = 200, Content = button };
            window.Styles.Add(LoadStyles());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Shapes.Path? icon = button.GetVisualDescendants().OfType<Shapes.Path>().FirstOrDefault();
            Assert.IsNotNull(icon, "chip 模板里的图标 Path 没建出来。");
            Assert.AreSame(geometry, icon.Data, "Tag 上的图标没接到 Path.Data 上。");

            TextBlock? label = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            Assert.IsNotNull(label, "chip 模板里的文字没建出来 —— 按钮会是个空白方块。");
            Assert.AreEqual("导出", label.Text);

            window.Close();
        });

    /// <summary>
    /// 没给 <c>Tag</c> 的 chip 照样能用:图标那一格空着,文字还在。
    /// <para>模板里若把 Tag 当成必填,漏填一个就是一个看不见的按钮。</para>
    /// </summary>
    [TestMethod]
    public void ChipButton_WithoutIcon_StillShowsLabel() =>
        OnUi(() =>
        {
            var button = new Button { Classes = { "chip" }, Content = "取消" };
            var window = new Window { Width = 400, Height = 200, Content = button };
            window.Styles.Add(LoadStyles());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            TextBlock? label = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            Assert.IsNotNull(label);
            Assert.AreEqual("取消", label.Text);

            window.Close();
        });

    /// <summary>
    /// 分段控件的选中态换的是**底色**而不是别的:<c>seg</c> 只描边,<c>seg on</c> 上强调色底。
    /// <para>
    /// headless 宿主没有 <c>Vela*</c> 令牌,所以这里不比对具体颜色 ——
    /// 比的是"两种状态解析出来不是同一个画刷",那正是选中态存在的意义。
    /// </para>
    /// </summary>
    [TestMethod]
    public void SegButton_OnState_DiffersFromOff() =>
        OnUi(() =>
        {
            var off = new Button { Classes = { "seg" }, Content = "前缀" };
            var on = new Button { Classes = { "seg", "on" }, Content = "通配" };
            var window = new Window
            {
                Width = 400,
                Height = 200,
                Content = new StackPanel { Children = { off, on } }
            };
            window.Styles.Add(LoadStyles());
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.AreNotEqual(off.BorderBrush, on.BorderBrush, "选中态该把描边让给底色。");

            window.Close();
        });
}
