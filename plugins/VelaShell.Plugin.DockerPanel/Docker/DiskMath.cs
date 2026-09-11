namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>一份 <c>system df</c> 里"能删掉多少"的拆分。</summary>
/// <param name="Images">未被任何容器引用的镜像。</param>
/// <param name="Volumes">引用计数为 0 的本地卷。</param>
/// <param name="BuildCache">未在使用的构建缓存。</param>
/// <param name="UnusedImages">上述镜像的个数。</param>
/// <param name="UnusedVolumes">上述卷的个数。</param>
public readonly record struct ReclaimBreakdown(
    long Images,
    long Volumes,
    long BuildCache,
    int UnusedImages,
    int UnusedVolumes)
{
    /// <summary>合计可回收字节。</summary>
    public long Total => Images + Volumes + BuildCache;

    /// <summary>一句话说清这些空间躺在哪儿。</summary>
    public string Describe() => Total <= 0
        ? "没有可回收的空间"
        : $"{UnusedImages} 个未使用镜像 · {UnusedVolumes} 个游离卷";
}

/// <summary>
/// <c>system df</c> 的口径换算。
/// <para>
/// 单独拎出来是因为总览页与系统页都要给出"可回收多少",而这两处一旦各算各的,
/// 用户就会在同一个面板的两张卡上看到两个数 —— 那比不显示更糟。
/// </para>
/// </summary>
public static class DiskMath
{
    /// <summary>按 <c>docker system prune -a --volumes</c> 的口径算可回收空间。</summary>
    public static ReclaimBreakdown Reclaimable(DiskUsage? usage)
    {
        if (usage is null)
        {
            return default;
        }
        // "未使用"跟着 Containers == 0 走,与 daemon 自己 prune 时的判断一致;
        // 悬空镜像只是其中一个子集,拿它当分母会把可回收量算少。
        ImageSummary[] unusedImages = [.. (usage.Images ?? []).Where(i => i.Containers <= 0)];
        VolumeSummary[] unusedVolumes = [.. (usage.Volumes ?? []).Where(v => v.UsageData is { RefCount: <= 0 })];
        return new(
            unusedImages.Sum(i => i.Size),
            unusedVolumes.Sum(v => v.UsageData is { Size: > 0 } u ? u.Size : 0),
            (usage.BuildCache ?? []).Where(c => !c.InUse).Sum(c => c.Size),
            unusedImages.Length,
            unusedVolumes.Length);
    }
}
