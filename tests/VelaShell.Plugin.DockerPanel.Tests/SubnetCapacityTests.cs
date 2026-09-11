using VelaShell.Plugin.DockerPanel.Ui.Pages;

namespace VelaShell.Plugin.DockerPanel.Tests;

/// <summary>
/// 子网容量。
/// <para>
/// 「已分配 3 / 65533」这一行的价值是**提前**看见子网快满 —— 到了才发现就太晚了,
/// 那时候新容器已经起不来。所以数字必须与 docker 实际能分出去的一致,不能是理论总数。
/// </para>
/// </summary>
[TestClass]
public class SubnetCapacityTests
{
    [TestMethod]
    public void SubtractsNetworkBroadcastAndGateway()
    {
        // /16 有 65536 个地址,减掉网络地址、广播地址与 docker 占的 .1 网关 = 65533,
        // 与设计稿那一行对得上。
        Assert.AreEqual(65533L, NetworksPageViewModel.SubnetCapacity("172.20.0.0/16"));
        Assert.AreEqual(253L, NetworksPageViewModel.SubnetCapacity("192.168.10.0/24"));
    }

    [TestMethod]
    public void TinySubnetsDoNotGoNegative()
    {
        // /31 与 /32 在点对点场景里合法,但给不出可用地址 —— 报 0,不报负数。
        Assert.AreEqual(0L, NetworksPageViewModel.SubnetCapacity("10.0.0.0/31"));
        Assert.AreEqual(0L, NetworksPageViewModel.SubnetCapacity("10.0.0.1/32"));
    }

    [TestMethod]
    public void SkipsIpv6BecauseTheNumberWouldBeMeaningless()
    {
        // 一个 /64 是 1.8×10^19 个地址。显示出来除了占地方没有任何信息。
        Assert.IsNull(NetworksPageViewModel.SubnetCapacity("fd00::/64"));
    }

    [TestMethod]
    public void UnparseableInputIsSilentlySkipped()
    {
        Assert.IsNull(NetworksPageViewModel.SubnetCapacity(null));
        Assert.IsNull(NetworksPageViewModel.SubnetCapacity(""));
        Assert.IsNull(NetworksPageViewModel.SubnetCapacity("(自动)"));
        Assert.IsNull(NetworksPageViewModel.SubnetCapacity("172.20.0.0/abc"));
        Assert.IsNull(NetworksPageViewModel.SubnetCapacity("172.20.0.0/99"));
    }
}

/// <summary>
/// <c>df</c> 输出的解析。
/// <para>
/// 「宿主盘 94 GB 中已用 38 GB」是决定要不要清理的那个数,而 Engine API 不给它 ——
/// 只能借一个容器跑 <c>df</c>。各家 <c>df</c> 的表头与列宽都不一样,所以按**位置**取,
/// 不按表头名。
/// </para>
/// </summary>
[TestClass]
public class DfParsingTests
{
    [TestMethod]
    public void ReadsTotalAndUsedFromTheDataRow()
    {
        const string output = """
            Filesystem     1-blocks        Used   Available Capacity Mounted on
            /dev/sda1    100931731456 41231731456 54700000000      43% /var/lib/docker
            """;

        (var total, var used) = SystemPageViewModel.ParseDf(output)!.Value;

        Assert.AreEqual(100931731456L, total);
        Assert.AreEqual(41231731456L, used);
    }

    [TestMethod]
    public void SkipsTheHeaderRow()
    {
        // 表头里的 "1-blocks" 不是数字,不能被当成总量。
        Assert.IsNull(SystemPageViewModel.ParseDf("Filesystem 1-blocks Used Available Capacity Mounted on"));
    }

    [TestMethod]
    public void ReturnsNullOnGarbage()
    {
        Assert.IsNull(SystemPageViewModel.ParseDf(""));
        Assert.IsNull(SystemPageViewModel.ParseDf("df: /nope: No such file or directory"));
    }
}
