using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;

namespace VelaShell.Plugin.Redis.Tests;

/// <summary>
/// 标签页图标那串路径数据:必须真的解析得出几何,而且撑满它自报的视框。
/// <para>
/// 这一层守的是一种**不会报错的失败**:宿主拿到插件给的路径是 <c>Geometry.Parse</c> 一把,
/// 解析不出来就当作没给、退回通用插头 —— 于是路径里打错一个字符,编译过、测试过、
/// 装载过,只是 Redis 标签上悄悄换成了插头。没人盯着标签条看就发现不了。
/// </para>
/// <para>
/// 与 <see cref="RedisPanelStylesTests" /> 同一条口径:不需要 127.0.0.1:6379,
/// 因此在任何机器上都**无条件跑得起来**。
/// </para>
/// <para>
/// ⚠️ 这里只验得了"解析得出、位置对",验不了"顶面那几处镂空还是不是洞" ——
/// headless 宿主(<c>UseHeadlessDrawing</c>)的 <c>FillContains</c> 给的结果与真实几何对不上
/// (整行整行地报 true,连视框外的点也报)。镂空那件事是另一条路核的:三段 path 的绕向相反,
/// 因此 evenodd 与 nonzero 两种填充规则画出来一模一样,不必在路径上声明 <c>F0</c> / <c>F1</c>。
/// </para>
/// </summary>
[TestClass]
[TestCategory("Plugins")]
public sealed class RedisIconTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _) =>
        // 几何要平台后端才建得起来(走的是渲染接口),所以这组也得进 headless 会话。
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RedisIconTests).Assembly);

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
            Rect bounds = Geometry.Parse(RedisIcon.PathData).Bounds;

            // 三段 path 都拼上了:横向正好撑满 1030,纵向 940 上下(原图上下各留一点白)。
            // 漏掉一段不会解析失败,只会让图形缺一层 —— 而缺了圆柱的 Redis 标画出来仍是个图标,
            // 只是不再是那个标,这种错肉眼之外没有别的地方会红。
            Assert.AreEqual(RedisIcon.ViewBoxSize, bounds.Width, 1d, $"实际 {bounds}");
            Assert.AreEqual(940d, bounds.Height, 2d, $"实际 {bounds}");
            // 落在视框内:宿主按 min(宽,高)/视框 缩放后**不裁剪**,越界的部分会画到相邻标签上。
            // 容 1 个单位(0.1%)的导出误差 —— 原图右边界就恰好探出 0.024。
            Assert.IsGreaterThanOrEqualTo(-1d, bounds.X, $"实际 {bounds}");
            Assert.IsGreaterThanOrEqualTo(0d, bounds.Y, $"实际 {bounds}");
            Assert.IsLessThanOrEqualTo(RedisIcon.ViewBoxSize + 1d, bounds.Right, $"实际 {bounds}");
            Assert.IsLessThanOrEqualTo(RedisIcon.ViewBoxSize, bounds.Bottom, $"实际 {bounds}");
        });
}
