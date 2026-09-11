using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 最小的可观察对象。
/// <para>
/// 插件刻意不引 ReactiveUI / CommunityToolkit:插件包会被原样塞进 .vpx,
/// 而一个 Docker 面板需要的全部"MVVM"就是这两个类。少一个依赖,
/// 就少一次"宿主与插件加载了同一个库的两个版本"的排查。
/// </para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>赋值并在值真的变了时通知。</summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 手动触发一次通知(计算属性跟着源属性变时用)。
    /// <para>
    /// <b>通知一律在 UI 线程上发出去。</b>订阅者不是普通的观察者:XAML 绑定与视图侧那几个
    /// 监听器(抽屉布局、列宽)收到通知之后会直接去改控件,而在别的线程上碰控件是一个硬性异常
    /// (<c>The calling thread cannot access this object</c>)。
    /// </para>
    /// <para>
    /// 为什么放在这里而不是让每个调用方自己 <see cref="Ui.Post" />:后台线程改属性的地方太多了
    /// —— 事件流、统计采样、日志、以及**释放路径**(面板关闭时那串
    /// <c>ConfigureAwait(false)</c> 之后就已经不在 UI 线程上了)。漏掉任何一处,
    /// 用户看到的都是一个崩溃,而不是一次退化。这一处兜住,就没有"哪一处漏了"这回事。
    /// </para>
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        // 先取出来:发出去之前最后一个订阅者退订了的话,字段就成 null 了。
        if (PropertyChanged is not { } handler)
        {
            return;
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(this, new(propertyName));
            return;
        }
        try
        {
            Dispatcher.UIThread.Post(() => handler(this, new(propertyName)));
        }
        catch (Exception)
        {
            // 应用正在退出时 dispatcher 可能已经关了。那时没人再看这条通知了,
            // 而为一条通知把关闭流程炸掉是更糟的结果。
        }
    }

    /// <summary>触发一组通知。</summary>
    protected void OnPropertiesChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>
/// 一个命令。异步版本自带"跑着的时候不能再点"—— 双击一个"删除"按钮
/// 不该发出两条删除请求。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    /// <summary>同步命令。</summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = p =>
        {
            execute(p);
            return Task.CompletedTask;
        };
        _canExecute = canExecute;
    }

    /// <summary>异步命令。</summary>
    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 命令的实现自己负责把失败呈现出来;真漏到这里的话,
            // 至少不能让一个未观察的异常把宿主拖走。
            UnhandledCommandError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>命令实现漏出来的异常(面板挂一个全局处理,写进状态栏)。</summary>
    public static event Action<Exception>? UnhandledCommandError;

    /// <summary>重新求一次 <see cref="CanExecute" />。</summary>
    public void RaiseCanExecuteChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}

/// <summary>UI 线程封送的小工具。</summary>
public static class Ui
{
    /// <summary>在 UI 线程上跑一段同步代码(已经在 UI 线程就直接跑)。</summary>
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>在 UI 线程上跑一段代码并等它完成。</summary>
    public static Task InvokeAsync(Action action) =>
        Dispatcher.UIThread.CheckAccess()
            ? RunInline(action)
            : Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>带"整体替换但保留身份"的可观察集合。</summary>
public sealed class KeyedCollection<T>(Func<T, string> keySelector) : ObservableCollection<T>
{
    /// <summary>
    /// 用新快照就地合并:同 key 的项**保留原实例**(只更新内容),新增的插进去,
    /// 消失的删掉。
    /// <para>
    /// 不能简单地 Clear + AddRange:那会把选中态、滚动位置与展开状态全部清掉 ——
    /// 而这个面板每秒都可能因为一条事件而刷新。用户选中三行准备批量停止,
    /// 刷新一次就全没了,是这个面板能犯的最烦人的错误。
    /// </para>
    /// </summary>
    /// <param name="snapshot">新快照(顺序即目标顺序)。</param>
    /// <param name="update">把新数据合并进旧实例。</param>
    public void Merge(IReadOnlyList<T> snapshot, Action<T, T> update)
    {
        Dictionary<string, T> existing = [];
        foreach (var item in this)
        {
            existing[keySelector(item)] = item;
        }
        for (var i = 0; i < snapshot.Count; i++)
        {
            var incoming = snapshot[i];
            var key = keySelector(incoming);
            if (existing.TryGetValue(key, out var current))
            {
                update(current, incoming);
                var at = IndexOf(current);
                if (at != i && at >= 0)
                {
                    Move(at, i);
                }
                existing.Remove(key);
            }
            else
            {
                Insert(i, incoming);
            }
        }
        foreach (var stale in existing.Values)
        {
            Remove(stale);
        }
    }
}
