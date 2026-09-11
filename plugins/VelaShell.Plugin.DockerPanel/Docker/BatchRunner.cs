namespace VelaShell.Plugin.DockerPanel.Docker;

/// <summary>批量操作里单个目标的结果。</summary>
/// <param name="Target">目标的显示名(容器名 / 卷名)。</param>
/// <param name="Succeeded">是否成功。</param>
/// <param name="Failure">失败原因(daemon 的原话);成功时为 <see langword="null" />。</param>
public readonly record struct BatchOutcome(string Target, bool Succeeded, string? Failure);

/// <summary>一次批量操作的汇总。</summary>
/// <param name="Outcomes">逐个目标的结果,顺序与发起时一致。</param>
public sealed record BatchResult(IReadOnlyList<BatchOutcome> Outcomes)
{
    /// <summary>成功数。</summary>
    public int SucceededCount => Outcomes.Count(o => o.Succeeded);

    /// <summary>失败数。</summary>
    public int FailedCount => Outcomes.Count(o => !o.Succeeded);

    /// <summary>全部成功。</summary>
    public bool AllSucceeded => FailedCount == 0;

    /// <summary>失败的那些。</summary>
    public IEnumerable<BatchOutcome> Failures => Outcomes.Where(o => !o.Succeeded);
}

/// <summary>
/// 批量执行器。
/// <para>
/// 存在的唯一理由是**逐个目标判定**:选中 10 个容器点停止,其中 2 个因为被依赖而失败时,
/// 界面要能说"成功 8、失败 2 —— postgres-main 被 api-gateway 依赖",
/// 而不是把整批说成"操作失败"。所以这里绝不 short-circuit:一个目标的异常
/// 只记在它自己名下,后面的照跑。
/// </para>
/// </summary>
public static class BatchRunner
{
    /// <summary>
    /// 对每个目标跑一遍 <paramref name="action" />,逐个记录成败。
    /// </summary>
    /// <param name="targets">目标与显示名。</param>
    /// <param name="action">对单个目标的操作。</param>
    /// <param name="onProgress">每完成一个回调一次(已完成数, 总数, 当前目标名)。</param>
    /// <param name="cancellationToken">
    /// 取消令牌。触发后**不再启动**新目标,已经发出去的那条等它自己结束 ——
    /// 半路掐掉一条 <c>docker stop</c> 只会留下一个状态不明的容器。
    /// </param>
    public static async Task<BatchResult> RunAsync<T>(
        IReadOnlyList<(T Target, string Name)> targets,
        Func<T, CancellationToken, Task> action,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        List<BatchOutcome> outcomes = [with(targets.Count)];
        for (var i = 0; i < targets.Count; i++)
        {
            (var target, var name) = targets[i];
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new(name, false, "已取消,这个目标没有执行。"));
                continue;
            }
            onProgress?.Invoke(i, targets.Count, name);
            try
            {
                await action(target, cancellationToken).ConfigureAwait(false);
                outcomes.Add(new(name, true, null));
            }
            catch (DockerApiException ex)
            {
                outcomes.Add(new(name, false, ex.Message));
            }
            catch (DockerUnreachableException ex)
            {
                // 连接没了就没有必要继续戳后面的目标 —— 它们只会拿到同一条错误。
                outcomes.Add(new(name, false, ex.Message));
                for (var j = i + 1; j < targets.Count; j++)
                {
                    outcomes.Add(new(targets[j].Name, false, "连接已断开,这个目标没有执行。"));
                }
                break;
            }
            catch (OperationCanceledException)
            {
                outcomes.Add(new(name, false, "已取消。"));
            }
            catch (Exception ex)
            {
                outcomes.Add(new(name, false, ex.Message));
            }
        }
        onProgress?.Invoke(targets.Count, targets.Count, "");
        return new(outcomes);
    }
}
