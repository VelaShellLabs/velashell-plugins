using Avalonia.Threading;
using System.Collections.ObjectModel;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 把一条一条涌进来的行,攒成一小批再交给界面。
/// <para>
/// 直接 <c>Ui.Post</c> 每一行有个不明显的代价:每一次投递都是一个独立的调度任务,
/// 每一次 <c>Add</c> 都是一次 <c>CollectionChanged</c>,于是一次布局。
/// <c>compose logs --tail 500</c> 在七个服务的项目上开头就是三千多行 ——
/// 三千多次调度、三千多次排版,界面自然要僵一下,而这段时间里它什么也没多做。
/// </para>
/// <para>
/// 攒 120ms 再一起交,人眼看不出差别(一帧 16ms,日志本来也不是逐行读的),
/// 调度与布局却从"每行一次"变成"每批一次"。容器日志那边早就是这么做的,
/// 这里把它挪成两处共用。
/// </para>
/// </summary>
/// <typeparam name="T">行的类型。</typeparam>
/// <param name="target">最终落到的集合(只在 UI 线程上动)。</param>
/// <param name="max">保留多少行;超了从最旧的开始扔。</param>
public sealed class LineBuffer<T>(ObservableCollection<T> target, int max)
{
    private readonly Lock _gate = new();
    private readonly List<T> _pending = [];
    private DispatcherTimer? _timer;

    /// <summary>加一行。可以从任何线程调用。</summary>
    public void Add(T item)
    {
        lock (_gate)
        {
            _pending.Add(item);
        }
        Ui.Post(Start);
    }

    /// <summary>清空(界面上的和还没交出去的一起)。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
        Ui.Post(target.Clear);
    }

    /// <summary>把还压着的那一批立刻交出去 —— 一段命令跑完时用,免得最后几行等满 120ms。</summary>
    public void Flush() => Ui.Post(() => OnTick(null, EventArgs.Empty));

    private void Start()
    {
        _timer ??= new() { Interval = TimeSpan.FromMilliseconds(120) };
        if (_timer.IsEnabled)
        {
            return;
        }
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        List<T> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                // 空转两轮就把定时器停了 —— 一个面板上同时开着好几处日志时,
                // 常驻的定时器加起来也是一笔开销。
                if (_timer is { } idle)
                {
                    idle.Tick -= OnTick;
                    idle.Stop();
                }
                return;
            }
            batch = [.. _pending];
            _pending.Clear();
        }
        foreach (var item in batch)
        {
            target.Add(item);
        }
        // 按行截断:从最旧的开始扔,直到回到上限之内。
        while (target.Count > max)
        {
            target.RemoveAt(0);
        }
    }
}
