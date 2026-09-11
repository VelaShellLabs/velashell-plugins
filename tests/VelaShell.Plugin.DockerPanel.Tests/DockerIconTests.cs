using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 标签页图标那串路径数据:必须真的解析得出几何,而且撑满它自报的视框。
/// <para>
/// 这一层守的是一种**不会报错的失败**:宿主拿到插件给的路径是 <c>Geometry.Parse</c> 一把,
/// 解析不出来就当作没给、退回通用插头 —— 于是路径里打错一个字符,编译过、测试过、
/// 装载过,只是 Docker 标签上悄悄换成了插头。没人盯着标签条看就发现不了。
/// </para>
/// <para>
/// ⚠️ 这里验不了"鲸鱼肚子与那只眼睛还是不是洞":headless 宿主的 <c>FillContains</c> 给的
/// 结果与真实几何对不上(整行整行地报 true,连视框外的点也报)。镂空那件事是另一条路核的 ——
/// 原图那段 path 的内外绕向相反,evenodd 与 nonzero 两种填充规则画出来一模一样。
/// </para>
/// </summary>
[TestClass]
public sealed class DockerIconTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        // 几何要平台后端才建得起来(走的是渲染接口),所以这组也得进 headless 会话。
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DockerIconTests).Assembly);

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>路径解析得出几何,撑满自报的视框,且不越界。</summary>
    [TestMethod]
    public void PathData_ParsesAndFillsItsOwnViewBox() =>
        OnUi(() =>
        {
            // 解析不出来会抛(宿主那边则是吞掉、退回通用插头)—— 这一行本身就是一半的验收。
            Rect bounds = Geometry.Parse(DockerIcon.PathData).Bounds;

            // 撑满:横向几乎顶满 1024,纵向是鲸鱼加上那摞集装箱的高度。截断的路径不会解析失败,
            // 只会让图形缺一块 —— 而缺了集装箱的鲸鱼画出来仍是个图标,只是不再是 Docker 的标。
            Assert.IsGreaterThan(DockerIcon.ViewBoxSize * 0.9, bounds.Width, $"实际 {bounds}");
            Assert.IsGreaterThan(DockerIcon.ViewBoxSize * 0.7, bounds.Height, $"实际 {bounds}");
            // 落在视框内:宿主按 min(宽,高)/视框 缩放后**不裁剪**,越界的部分会画到相邻标签上。
            Assert.IsGreaterThanOrEqualTo(0d, bounds.X, $"实际 {bounds}");
            Assert.IsGreaterThanOrEqualTo(0d, bounds.Y, $"实际 {bounds}");
            Assert.IsLessThanOrEqualTo(DockerIcon.ViewBoxSize, bounds.Right, $"实际 {bounds}");
            Assert.IsLessThanOrEqualTo(DockerIcon.ViewBoxSize, bounds.Bottom, $"实际 {bounds}");
        });

    /// <summary>交给宿主的那份是**实心 + 自报视框**,两位错一位图标就画不对。</summary>
    [TestMethod]
    public void Panel_ReportsAFilledIconWithItsNativeViewBox()
    {
        // 实心图形被 2px 圆头画笔描边会变成一圈轮廓线;视框留在 lucide 的 24
        // 则等于把这张 1024 的图放大四十多倍,屏幕上什么也看不见。
        Assert.IsTrue(DockerIcon.Panel.IsFilled);
        Assert.AreEqual(1024d, DockerIcon.Panel.ViewBoxSize);
        Assert.AreEqual(DockerIcon.PathData, DockerIcon.Panel.PathData);
    }
}
